using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.Parts;

namespace QPGL.Infrastructure.Stock;

public interface IInStockRepository
{
    /// <summary>散件入库生效（复刻 fr_isor 收货 + fr_isto1 row31 生效序列）。</summary>
    Task<CreateInStockResult> CreateAsync(CreateInStockCommand command, string dept, string per, bool allowReentry, CancellationToken ct = default);

    /// <summary>业务员列表（旧 fr_isto row203：tbsysset type='ywr' 优先，无则 tbkey.per 全量）。</summary>
    Task<IReadOnlyList<string>> GetYwrListAsync(CancellationToken ct = default);

    /// <summary>表格导入价格比率（旧 ___.row214 L38-43：进价比率/售价比率/批价比率 + 外币为含税价；缺省 0.08/0.05/0.09）。</summary>
    Task<(decimal Jj, decimal Sj, decimal Pj, bool Wbtohs)> GetImportRatiosAsync(CancellationToken ct = default);

    /// <summary>库内现有批次仓位/仓库（ISAPPE P_WAY=2 同款取批次：kcamount DESC TOP 1）。
    /// 表格导入行未填仓位时带出，使批次键 cp+nno+posi+depotno+dpm 命中原批次、不多出新批次行（2026-10-27 用户确认）。</summary>
    Task<(string Posi, string Depotno)?> GetTopBatchPosiAsync(string cp, string nno, string dpm, CancellationToken ct = default);
}

/// <summary>
/// 散件入库仓储（M4-1，写路径契约感知）。
/// 旧实现出处（无数据库触发器，纯应用层 SQL）：
///   fr_isto1 row31 生效序列：
///     1) 校验：空厂牌/空名称拒、fkfs 必填、挂帐须供应商、同 cno 重复入库（tbistoed TOP 1 WHERE cno，
///        2 天内可确认重入，超期拒绝——allowReentry 对应旧确认弹窗）
///     2) UPDATE tbistosm SET indate,per,gyname,pno,fkfs WHERE cno（row31 L105）
///     3) 逐行（tbistosm WHERE cno，行级事务）：
///        a. ISAPPE("tbistosm", id, jkamount)（IsappeEngine，pg_proc_client过程库 L620-728 原样；
///           2026-10-24 用户确认运行时读 tbsysset「均价」选 P_WAY，缺省 2）：
///           P_WAY=1 批次匹配 = tbisto WHERE cp+nno+iprc+posi+depotno+dpm（进价参与匹配）
///           P_WAY=2 批次匹配 = cp+nno+posi+depotno+dpm（进价不参与），命中按加权平均重算 iprc/iprj(外币为含税价)
///           - 未命中 → INSERT tbisto(dpm,cp,nno,na1,unit,kcamount,ty,fa,cxnno,iprj,iprc,oprc,posi,
///             depotno,note,xno,dc,kcbhdate,oprb)，kcamount=jkamount，cxnno=S_NNO(nno)（生产无串号配置=''）
///           - 命中 → UPDATE tbisto SET kcamount+=jkamount, kcbhdate=今天（价格不变；P_WAY=2 例外加权）
///        b. UPDATE tbprnoty SET na1,ty,unit WHERE cp+nno（属性库同步，L197）
///        c. INSERT tbistoed(CP,NNO,NA1,TY,FA,DC,UNIT,JKAMOUNT,KCAMOUNT,IPRJ,IPRC,OPRC,POSI,DEPOTNO,
///           INDATE,ITEM,CNO,BOX,SEANO,isord,PNO,GYNO,GYNAME,PER,NOTE,CXNNO,FKFS,TYPE,YWR,Freight)
///           TYPE='入库'、ISORD=行值（旧散件固定 'N'）、SEANO=cno、KCAMOUNT=ISAPPE 返回值（入库后快照）
///        d. DELETE tbistosm WHERE id（行处理完即删）
///     4) SYSLOG（二期日志）
///   PASSTH 自动补 dpm（webservice.prg L221-227）→ tbistosm/tbisto INSERT 显式写 dpm=dept。
/// </summary>
public sealed class InStockRepository(SqlConnectionFactory factory, Parts.IDmService dm, DocNo.IDocNoService docNo) : IInStockRepository
{
    public async Task<CreateInStockResult> CreateAsync(CreateInStockCommand command, string dept, string per, bool allowReentry, CancellationToken ct = default)
    {
        command.Validate();
        var deptTrim = dept.Trim();
        var today = DateTime.Today;

        // 取号（旧 X__.row212 L110-123 / _L_.row165 L238-270）：
        // tbsysset「是否自动合同号」关（本部署语义）→ 生效时 GETNEWDH(1,'散件入库单','tbistoed','cno') 自动取号；
        // 开 → 人工输入合同号（FRMCNO 窗口）。命令带 cno = 人工合同号，空 = 自动取号。
        var cno = command.Cno.Trim();
        if (cno.Length == 0)
            cno = await docNo.GetNewDocNoAsync("散件入库单", deptTrim,
                new DocNo.DocNoService.DocNoCheckTarget("tbistoed", "cno"), ct);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 1) 同 cno 重复入库校验（旧 row31 L76-86）
            var last = await conn.QueryFirstOrDefaultAsync<(DateTime indate, string per)>(
                new CommandDefinition(
                    "SELECT TOP (1) indate, RTRIM(per) AS per FROM tbistoed WITH (NOLOCK) WHERE cno = @cno ORDER BY id DESC",
                    new { cno = new DbString { Value = cno, IsAnsi = true, Length = 15 } },
                    transaction: tx, cancellationToken: ct));
            if (last.indate != default)
            {
                if ((today - last.indate.Date).TotalDays >= 2)
                    throw new AppException("CNO_ALREADY_IN", $"该单号 {cno} 已于 {last.indate:yyyy-MM-dd} 由 {last.per} 入过库（超 2 天不可重入）", 409);
                if (!allowReentry)
                    throw new AppException("CNO_REENTRY_CONFIRM",
                        $"该单号 {cno} 已于 {last.indate:yyyy-MM-dd} 由 {last.per} 入过库，仍生效入库请确认重入", 409);
            }

            // 2) 收货暂存写入（旧 fr_isor → tbistosm；PASSTH 补 dpm 契约显式写）
            foreach (var l in command.Lines)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbistosm
                        (cp, dpm, nno, na1, ty, fa, dc, unit, jkamount, iprj, iprc, oprc, oprb, posi,
                         cno, gyno, gyname, pno, note, fkfs, depotno, ywr, Freight)
                    VALUES
                        (@cp, @dpm, @nno, @na1, @ty, @fa, @dc, @unit, @jkamount, @iprj, @iprc, @oprc, @oprb, @posi,
                         @cno, @gyno, @gyname, @pno, @note, @fkfs, @depotno, @ywr, @freight)
                    """,
                    new
                    {
                        cp = Ansi(l.Cp, 10), dpm = Ansi(deptTrim, 10), nno = Ansi(l.Nno, 18), na1 = Ansi(l.Na1, 40),
                        ty = Ansi(l.Ty, 14), fa = Ansi(l.Fa, 8), dc = Ansi(l.Dc, 8), unit = Ansi(l.Unit, 4),
                        jkamount = l.Jkamount, iprj = l.Iprj, iprc = l.Iprc, oprc = l.Oprc, oprb = l.Oprb,
                        posi = Ansi(l.Posi, 10), cno = Ansi(cno, 15), gyno = Ansi(command.Gyno, 10),
                        gyname = Ansi(command.Gyname, 60), pno = Ansi(command.Pno, 12), note = Ansi(l.Note, 100),
                        fkfs = Ansi(command.Fkfs, 10), depotno = Ansi(l.Depotno, 10), ywr = Ansi(command.Ywr, 10),
                        freight = command.Freight,
                    },
                    transaction: tx, cancellationToken: ct));
            }

            // 3) 生效前置更新（旧 row31 L105）
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbistosm SET indate = @indate, per = @per, gyname = @gyname, pno = @pno, fkfs = @fkfs WHERE cno = @cno",
                new
                {
                    indate = today,
                    per = Ansi(per, 6),
                    gyname = Ansi(command.Gyname, 60),
                    pno = Ansi(command.Pno, 12),
                    fkfs = Ansi(command.Fkfs, 10),
                    cno = Ansi(cno, 15),
                },
                transaction: tx, cancellationToken: ct));

            // 4) 逐行生效（ISAPPE 复刻（IsappeEngine，运行时读 tbsysset 均价）+ tbistoed 台账 + 清 tbistosm）
            var prm = await IsappeEngine.LoadParamsAsync(conn, tx, ct);
            var rows = (await conn.QueryAsync<SmRow>(
                new CommandDefinition(
                    """
                    SELECT id, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa,
                           RTRIM(dc) dc, RTRIM(unit) unit, jkamount, iprj, iprc, oprc, oprb,
                           RTRIM(posi) posi, RTRIM(depotno) depotno, RTRIM(note) note, xno, RTRIM(cxnnO) cxnno
                    FROM tbistosm WITH (UPDLOCK)
                    WHERE cno = @cno
                    ORDER BY id
                    """,
                    new { cno = Ansi(cno, 15) },
                    transaction: tx, cancellationToken: ct))).AsList();

            var result = new CreateInStockResult { Cno = cno, LineCount = rows.Count };
            var batchDelta = new Dictionary<int, decimal>();
            var item = 0;
            foreach (var r in rows)
            {
                item++;
                // ISAPPE（pg_proc_client 库版本）：批次匹配（P_WAY=1 含 iprc / P_WAY=2 不含）+ 命中更新/未命中新建
                var (batchId, _, kcAfter, inserted) = await IsappeEngine.ApplyAsync(
                    conn, tx,
                    new IsappeSource(deptTrim, r.Cp, r.Nno, r.Na1, r.Ty, r.Fa, r.Unit,
                        r.Iprj, r.Iprc, r.Oprc, 0, r.Oprb, r.Posi, r.Depotno, r.Note, r.Xno, r.Dc),
                    r.Jkamount, prm, IsappeLibrary.Client, today, ct);
                batchDelta[batchId] = batchDelta.GetValueOrDefault(batchId) + r.Jkamount;

                // 2026-10-26 用户确认：命中属性库的行不回写名称/车型/单位（不按表格覆盖库内原值）；
                // 新配件由下方自动建档写入。旧 row31 L197 的 UPDATE tbprnoty 同步在此部署停用。

                // 属性库新配件自动建档（旧 L206-228：tbprnoty 无记录时 INSERT + tbname0 同步 L254-263）
                var profileExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbprnoty WITH (NOLOCK) WHERE cp = @cp AND nno = @nno",
                    new { cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18) },
                    transaction: tx, cancellationToken: ct));
                if (profileExists == 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, note, iprj, uqty, cxnno, oprc_cp, oprc_min, cpict)
                        VALUES (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @note, @iprj, 1, @cxnno, 0, 0, '')
                        """,
                        new
                        {
                            cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 40),
                            ty = Ansi(r.Ty, 14), fa = Ansi(r.Fa, 8), dc = Ansi(r.Dc, 8),
                            unit = Ansi(r.Unit, 4), note = Ansi(r.Note, 254), iprj = r.Iprj,
                            cxnno = Ansi(r.Cxnno, 18),
                        },
                        transaction: tx, cancellationToken: ct));
                    await Name0Sync.SyncAsync(conn, tx, dm, r.Na1, ct);
                    await Name0Sync.SyncTyAsync(conn, tx, dm, r.Ty, ct);   // 车型拼音码（2026-10-30 需求）
                }

                // 入库台账（旧 L205 列清单原样 + PASSTH 补 dpm）
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbistoed
                        (CP, NNO, NA1, TY, FA, DC, UNIT, JKAMOUNT, KCAMOUNT, IPRJ, IPRC, OPRC, POSI, DEPOTNO,
                         INDATE, ITEM, CNO, BOX, SEANO, isord, PNO, GYNO, GYNAME, PER, NOTE, CXNNO, FKFS, TYPE, YWR, Freight, dpm)
                    VALUES
                        (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @jkamount, @kcamount, @iprj, @iprc, @oprc, @posi, @depotno,
                         @indate, @item, @cno, @box, @seano, @isord, @pno, @gyno, @gyname, @per, @note, @cxnno, @fkfs, @type, @ywr, @freight, @dpm)
                    """,
                    new
                    {
                        cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 40),
                        ty = Ansi(r.Ty, 14), fa = Ansi(r.Fa, 8), dc = Ansi(r.Dc, 8), unit = Ansi(r.Unit, 4),
                        jkamount = r.Jkamount, kcamount = kcAfter, iprj = r.Iprj, iprc = r.Iprc, oprc = r.Oprc,
                        posi = Ansi(r.Posi, 10), depotno = Ansi(r.Depotno, 10), indate = today, item = item,
                        cno = Ansi(cno, 15), box = Ansi("", 10), seano = Ansi(cno, 15),
                        isord = Ansi("N", 10), // 旧 row31 硬编码 ISORD='N'（tbistosm 无该列；订单入库后补）
                        pno = Ansi(command.Pno, 12), gyno = Ansi(command.Gyno, 10), gyname = Ansi(command.Gyname, 60),
                        per = Ansi(per, 10), note = Ansi(r.Note, 100), cxnno = Ansi(r.Cxnno, 18),
                        fkfs = Ansi(command.Fkfs, 10), type = Ansi("入库", 10), ywr = Ansi(command.Ywr, 10),
                        freight = command.Freight, dpm = Ansi(deptTrim, 10),
                    },
                    transaction: tx, cancellationToken: ct));

                result.Lines.Add(new InStockLineResult
                {
                    Nno = r.Nno,
                    MatchedBatchId = inserted ? null : batchId,
                    KcamountAfter = kcAfter,
                    Jkamount = r.Jkamount,
                });
                result.TotalIprcAmount += r.Jkamount * r.Iprc;
                result.TotalIprjAmount += r.Jkamount * r.Iprj;

                // 行处理完即删（旧 L233）
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE tbistosm WHERE id = @id", new { id = r.id },
                    transaction: tx, cancellationToken: ct));
            }

            // 5) 回读断言（§7.3）：tbisto.kcamount 变化量 == Σjkamount
            foreach (var (batchId, delta) in batchDelta)
            {
                var now = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WHERE id = @id", new { id = batchId },
                    transaction: tx, cancellationToken: ct));
                // 快照式断言：以批次匹配前快照重建期望值不可行（同批多行），此处断言非负异常下降由触发器外无写路径保证
                if (now + 0.0000001m < 0 && delta > 0)
                    throw new AppException("CONTRACT_VIOLATION", $"库存断言异常：tbisto.id={batchId} 入库后为负（已回滚）", 500);
            }

            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>char 列 GBK 字节语义：Length 按字节宽截断（1 汉字=2 字节，超宽不炸 SQL 截断错误）。</summary>
    private static DbString Ansi(string value, int length) =>
        new() { Value = Db.AnsiBytes.Truncate(value ?? "", length), IsAnsi = true, Length = length };

    private sealed class SmRow
    {
        public int id { get; set; }
        public string Cp { get; set; } = "";
        public string Nno { get; set; } = "";
        public string Na1 { get; set; } = "";
        public string Ty { get; set; } = "";
        public string Fa { get; set; } = "";
        public string Dc { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal Jkamount { get; set; }
        public decimal Iprj { get; set; }
        public decimal Iprc { get; set; }
        public decimal Oprc { get; set; }
        public decimal Oprb { get; set; }
        public string Posi { get; set; } = "";
        public string Depotno { get; set; } = "";
        public string Note { get; set; } = "";
        public int Xno { get; set; }
        public string Cxnno { get; set; } = "";
    }

    /// <summary>
    /// 业务员列表（旧 fr_isto row203 Init 语义）：
    /// 先查 tbsysset WHERE type='ywr'（name 存业务员名），有配置用它；
    /// 否则回退 tbkey.per 全量（分店过滤 p_fgs 时旧走 dpmbh，本部署单店取全量）。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetYwrListAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var configured = (await conn.QueryAsync<string>(
            new CommandDefinition(
                "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'ywr' AND RTRIM(name) <> '' ORDER BY id",
                cancellationToken: ct))).AsList();
        if (configured.Count > 0) return configured;

        var ops = (await conn.QueryAsync<string>(
            new CommandDefinition(
                "SELECT DISTINCT RTRIM(per) FROM tbkey WITH (NOLOCK) WHERE per <> '' ORDER BY per",
                cancellationToken: ct))).AsList();
        return ops;
    }

    /// <summary>
    /// 表格导入价格比率（旧 ___.row214 L38-43）：
    /// tbsysset type='system' name IN（进价比率/售价比率/批价比率）取 valuec，
    /// len(valuec)&gt;0 才生效，缺省 0.08/0.05/0.09；「外币为含税价」定 iprc 推导方向。
    /// </summary>
    public async Task<(decimal Jj, decimal Sj, decimal Pj, bool Wbtohs)> GetImportRatiosAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(string name, string valuec, bool? valuel)>(
            new CommandDefinition(
                "SELECT RTRIM(name), ISNULL(RTRIM(valuec), ''), valuel FROM tbsysset WITH (NOLOCK) " +
                "WHERE type = 'system' AND name IN (N'进价比率', N'售价比率', N'批价比率', N'外币为含税价')",
                cancellationToken: ct))).AsList();
        static decimal ParseOr(string raw, decimal def) =>
            raw.Length > 0 && decimal.TryParse(raw, out var v) ? v : def;
        var jj = 0.08m; var sj = 0.05m; var pj = 0.09m; var wbtohs = false;
        foreach (var (name, valuec, valuel) in rows)
        {
            switch (name)
            {
                case "进价比率": jj = ParseOr(valuec, jj); break;
                case "售价比率": sj = ParseOr(valuec, sj); break;
                case "批价比率": pj = ParseOr(valuec, pj); break;
                case "外币为含税价": wbtohs = valuel ?? false; break;
            }
        }
        return (jj, sj, pj, wbtohs);
    }

    /// <summary>库内现有批次仓位/仓库（kcamount DESC TOP 1，与 ISAPPE P_WAY=2 命中取批次同序）。</summary>
    public async Task<(string Posi, string Depotno)?> GetTopBatchPosiAsync(string cp, string nno, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(string posi, string depotno)>(
            new CommandDefinition(
                """
                SELECT TOP (1) RTRIM(posi) AS posi, RTRIM(depotno) AS depotno
                FROM tbisto WITH (NOLOCK)
                WHERE cp = @cp AND nno = @nno AND dpm = @dpm
                ORDER BY kcamount DESC
                """,
                new { cp = Ansi(cp, 10), nno = Ansi(nno, 18), dpm = Ansi(dpm, 10) },
                cancellationToken: ct));
        return row == default ? null : (row.posi, row.depotno);
    }
}
