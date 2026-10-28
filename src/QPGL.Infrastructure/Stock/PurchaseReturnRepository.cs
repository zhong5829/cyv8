using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;

namespace QPGL.Infrastructure.Stock;

public interface IPurchaseReturnRepository
{
    /// <summary>拉单：可退入库流水（契约 §2）。</summary>
    Task<IReadOnlyList<ReturnableLine>> SearchReturnableAsync(PurchaseReturnQuery query, string dpm, CancellationToken ct = default);

    /// <summary>批量添加前的宽松库存试算（契约 §4：按 cp+nno 汇总，无 dpm/depotno 条件——旧系统原样）。</summary>
    Task<decimal> GetLooseStockAsync(string cp, string nno, CancellationToken ct = default);

    /// <summary>tbsysset 下拉选项（type='guno' 退款方式 / type='退货原因' / type='库号'）。</summary>
    Task<IReadOnlyList<string>> GetSyssetNamesAsync(string type, CancellationToken ct = default);

    /// <summary>确认退货（契约 §6 核心写路径）。</summary>
    Task<ConfirmPurchaseReturnResult> ConfirmAsync(ConfirmPurchaseReturnCommand command, string dpm, string per, CancellationToken ct = default);
}

/// <summary>拉单条件（旧简易查询窗 __.row144 / fr_find 通用条件的常用子集）。</summary>
public sealed class PurchaseReturnQuery
{
    public string Cno { get; set; } = "";
    public string NnoPrefix { get; set; } = "";
    public string Na1Prefix { get; set; } = "";
    public string Gyname { get; set; } = "";
    public DateTime? InDateFrom { get; set; }
    public DateTime? InDateTo { get; set; }
}

/// <summary>可退入库流水行（ised cursor 列集，契约 §2）。</summary>
public sealed class ReturnableLine
{
    public int Id { get; set; }
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>可退数 = jkamount - ro。</summary>
    public decimal Amount { get; set; }
    /// <summary>当前库存（kshu 子查询：cp+nno+dpm+depotno，P_WAY=1 加 iprc）。</summary>
    public decimal Kshu { get; set; }
    /// <summary>原进库数（jkamount）。</summary>
    public decimal Oamount { get; set; }
    public decimal Iprj { get; set; }
    /// <summary>IPRC=CASE WHEN freight=0 THEN iprc ELSE Freight END。</summary>
    public decimal Iprc { get; set; }
    public decimal Oprc { get; set; }
    public string Posi { get; set; } = "";
    public DateTime Indate { get; set; }
    public string Cno { get; set; } = "";
    public string Box { get; set; } = "";
    public string Pno { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Depotno { get; set; } = "";
    public decimal Ro { get; set; }
    public string Fkfs { get; set; } = "";
    public string Note { get; set; } = "";
    public string Cxnno { get; set; } = "";
    public string Ywr { get; set; } = "";
    public decimal IprcOld { get; set; }
    public string Dpm { get; set; } = "";
}

/// <summary>
/// 入库退货仓储（菜单 B.A，权限码 BA）。
/// 旧实现出处：fr_istk（嗟.row132 确认 / _9.row49 拉单 / 洘.row118 编辑校验）。
/// 写路径无触发器依赖（tbistoed 纯台账 + ISAPPE 主程序库版冲减 tbisto）。
/// 事务边界：旧系统逐语句自动提交；新系统按既有约定逐行事务（行序列原子，行间独立——回放对齐最终状态）。
/// </summary>
public sealed class PurchaseReturnRepository(SqlConnectionFactory factory, IDocNoService docNo) : IPurchaseReturnRepository
{
    public async Task<IReadOnlyList<ReturnableLine>> SearchReturnableAsync(PurchaseReturnQuery query, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var prm = await IsappeEngine.LoadParamsAsync(conn, null, ct);

        // 旧拉单条件（_9.row49:L480）：(jkamount-ro>0) and isord<>'D'；PASSTH 部门权限追加 dpm
        var where = "a.jkamount - a.ro > 0 AND a.isord <> @isordD AND a.dpm = @dpm";
        var args = new DynamicParameters(new
        {
            isordD = new DbString { Value = "D", IsAnsi = true, Length = 10 },
            dpm = new DbString { Value = dpm.Trim(), IsAnsi = true, Length = 10 },
        });
        if (!string.IsNullOrWhiteSpace(query.Cno))
        {
            where += " AND a.cno = @cno";
            args.Add("cno", new DbString { Value = query.Cno.Trim(), IsAnsi = true, Length = 15 });
        }
        if (!string.IsNullOrWhiteSpace(query.NnoPrefix))
        {
            where += " AND a.nno LIKE @nno";
            args.Add("nno", new DbString { Value = query.NnoPrefix.Trim() + "%", IsAnsi = true, Length = 19 });
        }
        if (!string.IsNullOrWhiteSpace(query.Na1Prefix))
        {
            where += " AND a.na1 LIKE @na1";
            args.Add("na1", new DbString { Value = query.Na1Prefix.Trim() + "%", IsAnsi = true, Length = 41 });
        }
        if (!string.IsNullOrWhiteSpace(query.Gyname))
        {
            where += " AND a.gyname = @gyname";
            args.Add("gyname", new DbString { Value = query.Gyname.Trim(), IsAnsi = true, Length = 60 });
        }
        if (query.InDateFrom is { } from)
        {
            where += " AND a.indate >= @from";
            args.Add("from", from.Date);
        }
        if (query.InDateTo is { } to)
        {
            where += " AND a.indate < @to";
            args.Add("to", to.Date.AddDays(1));
        }

        // kshu 子查询（契约 §2）：cp+nno+dpm+depotno 汇总，P_WAY=1 加 iprc；进价展示 CASE freight
        var kshuWhere = prm.PWay == 1 ? " AND b.iprc = a.iprc" : "";
        var sql = $"""
            SELECT a.id, RTRIM(a.cp) cp, RTRIM(a.nno) nno, RTRIM(a.na1) na1, RTRIM(a.ty) ty, RTRIM(a.fa) fa,
                   RTRIM(a.dc) dc, RTRIM(a.unit) unit, a.jkamount - a.ro AS amount,
                   ISNULL((SELECT SUM(b.kcamount) FROM tbisto b WITH (NOLOCK)
                           WHERE b.nno = a.nno AND b.cp = a.cp AND b.dpm = a.dpm AND b.depotno = a.depotno{kshuWhere}), 0) kshu,
                   a.jkamount AS oamount, a.iprj,
                   CASE WHEN a.freight = 0 THEN a.iprc ELSE a.Freight END AS iprc,
                   a.oprc, RTRIM(a.posi) posi, a.indate, RTRIM(a.cno) cno, RTRIM(a.box) box,
                   RTRIM(a.pno) pno, RTRIM(a.gyname) gyname, RTRIM(a.depotno) depotno, a.ro,
                   RTRIM(a.fkfs) fkfs, RTRIM(a.note) note, RTRIM(a.cxnno) cxnno, RTRIM(a.ywr) ywr,
                   a.iprc AS iprcOld, RTRIM(a.dpm) dpm
            FROM tbistoed a WITH (NOLOCK)
            WHERE {where}
            ORDER BY a.indate DESC, a.id DESC
            """;
        var rows = await conn.QueryAsync<ReturnableLine>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>批量添加宽松校验（SS.row55:L694 原样：cp+nno，无 dpm/depotno/iprc）。</summary>
    public async Task<decimal> GetLooseStockAsync(string cp, string nno, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WITH (NOLOCK) WHERE cp = @cp AND nno = @nno",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
    }

    /// <summary>tbsysset 选项（钩.row131 退款方式 / 壟.row135 退货原因 / 頄.row111 库号）。</summary>
    public async Task<IReadOnlyList<string>> GetSyssetNamesAsync(string type, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = @type ORDER BY id",
            new { type = new DbString { Value = type, IsAnsi = true, Length = 16 } },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ConfirmPurchaseReturnResult> ConfirmAsync(ConfirmPurchaseReturnCommand command, string dpm, string per, CancellationToken ct = default)
    {
        command.Validate();
        var deptTrim = dpm.Trim();
        var today = DateTime.Today;
        var reasonSuffix = string.IsNullOrWhiteSpace(command.ReturnReason) ? "" : $"[退货原因：{command.ReturnReason.Trim()}]";

        await using var conn = await factory.OpenAsync(ct);

        // 取号（旧 L811：GETNEWDH(1,'散件入库单','tbistoed','cno')，独立提交——与散件入库同前缀）
        var cno = await docNo.GetNewDocNoAsync("散件入库单", deptTrim, new DocNoService.DocNoCheckTarget("tbistoed", "cno"), ct);

        var result = new ConfirmPurchaseReturnResult { Cno = cno };
        var prm = await IsappeEngine.LoadParamsAsync(conn, null, ct);

        // 确认窗逐件校验（旧 L785-804：按 cp+nno+depotno[P_WAY=1 加 iprc] 汇总退库数 vs max(kcamount)——注意旧系统是 max）
        // 先重读全部源行（校验 + 写入共用）
        var sourceRows = new Dictionary<int, SrcRow>();
        await using (var probe = (SqlTransaction)await conn.BeginTransactionAsync(ct))
        {
            foreach (var line in command.Lines)
            {
                var src = await ReadSourceAsync(conn, probe, line.SourceId, ct)
                    ?? throw new AppException("SRC_NOT_FOUND", $"入库流水 id={line.SourceId} 不存在（已回滚）", 404);
                if (!string.Equals(src.Dpm.Trim(), deptTrim, StringComparison.Ordinal))
                    throw new AppException("DPM_MISMATCH", $"当前分店是 {deptTrim}，入库历史是 {src.Dpm.Trim()}，分店不一致不能退货", 409);
                if (line.Amount > src.Jkamount - src.Ro)
                    throw new AppException("AMOUNT_EXCEED", $"{src.Nno.Trim()} 退库数超过进库数（可退 {src.Jkamount - src.Ro}）", 409);
                sourceRows[line.SourceId] = src;
            }
            await probe.RollbackAsync(CancellationToken.None);
        }

        // 逐件库存校验（旧 max(kcamount) 口径）
        var groups = command.Lines
            .Select(l => (Line: l, Src: sourceRows[l.SourceId]))
            .GroupBy(x => new { x.Src.Cp, x.Src.Nno, Depotno = x.Line.Depotno.Trim(), Iprc = prm.PWay == 1 ? x.Src.IprcOld : (decimal?)null });
        foreach (var g in groups)
        {
            var total = g.Sum(x => x.Line.Amount);
            var maxKc = prm.PWay == 1
                ? await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT ISNULL(MAX(kcamount), 0) FROM tbisto WITH (NOLOCK) WHERE cp = @cp AND nno = @nno AND depotno = @depotno AND iprc = @iprc AND dpm = @dpm",
                    new { cp = Ansi(g.Key.Cp, 10), nno = Ansi(g.Key.Nno, 18), depotno = Ansi(g.Key.Depotno, 10), iprc = g.Key.Iprc!.Value, dpm = Ansi(deptTrim, 10) }, cancellationToken: ct))
                : await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT ISNULL(MAX(kcamount), 0) FROM tbisto WITH (NOLOCK) WHERE cp = @cp AND nno = @nno AND depotno = @depotno AND dpm = @dpm",
                    new { cp = Ansi(g.Key.Cp, 10), nno = Ansi(g.Key.Nno, 18), depotno = Ansi(g.Key.Depotno, 10), dpm = Ansi(deptTrim, 10) }, cancellationToken: ct));
            if (maxKc < total)
                throw new AppException("STOCK_EXCEED", $"{g.Key.Cp.Trim()}{g.Key.Nno.Trim()} 退库数 {total} 超过库存数 {maxKc}", 409);
        }

        // 逐行写序列（行级事务；旧 L819-849 原样）
        var item = 0;
        foreach (var line in command.Lines)
        {
            item++;
            var src = sourceRows[line.SourceId];
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                // 1) 原入库行 ro 累计（旧 L833；并发下重读可退数防御）
                var currentRo = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT ro FROM tbistoed WITH (UPDLOCK) WHERE id = @id",
                    new { id = line.SourceId }, transaction: tx, cancellationToken: ct));
                if (line.Amount > src.Jkamount - currentRo)
                    throw new AppException("AMOUNT_EXCEED", $"{src.Nno.Trim()} 退库数超过剩余可退数（已被并发退货占用）", 409);
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbistoed SET ro = ro + @amount WHERE id = @id",
                    new { amount = line.Amount, id = line.SourceId }, transaction: tx, cancellationToken: ct));

                // 2) 仓位定位（旧 L834-840：优先行仓位且库存足额；降级同库号任一足额批次；再否则保留行仓位）
                var posi = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT TOP (1) RTRIM(posi) FROM tbisto WITH (NOLOCK) WHERE cp = @cp AND nno = @nno AND posi = @posi AND depotno = @depotno AND kcamount >= @amount AND dpm = @dpm",
                    new { cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18), posi = Ansi(line.Posi, 10), depotno = Ansi(line.Depotno, 10), amount = line.Amount, dpm = Ansi(deptTrim, 10) },
                    transaction: tx, cancellationToken: ct));
                if (string.IsNullOrEmpty(posi))
                    posi = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT TOP (1) RTRIM(posi) FROM tbisto WITH (NOLOCK) WHERE cp = @cp AND nno = @nno AND depotno = @depotno AND kcamount >= @amount AND dpm = @dpm",
                        new { cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18), depotno = Ansi(line.Depotno, 10), amount = line.Amount, dpm = Ansi(deptTrim, 10) },
                        transaction: tx, cancellationToken: ct));
                var finalPosi = string.IsNullOrEmpty(posi) ? line.Posi : posi;

                // 3) 插入负数退货流水（旧 L843 列集；ro 列=原入库行 id）
                var insertIprc = prm.PWay == 1 ? src.IprcOld : line.Iprc;
                var note = src.Note + reasonSuffix;
                var newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    INSERT INTO tbistoed
                        (CP, NNO, NA1, TY, FA, DC, UNIT, JKAMOUNT, KCAMOUNT, IPRJ, IPRC, OPRC, POSI, INDATE, ITEM,
                         CNO, BOX, SEANO, PNO, GYNO, GYNAME, PER, NOTE, CXNNO, FKFS, DEPOTNO, TYPE, ro, ywr, dpm)
                    OUTPUT INSERTED.id
                    VALUES
                        (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @jkamount, 0, @iprj, @iprc, @oprc, @posi, @indate, @item,
                         @cno, @box, @seano, @pno, @gyno, @gyname, @per, @note, @cxnno, @fkfs, @depotno, @type, @ro, @ywr, @dpm)
                    """,
                    new
                    {
                        cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18), na1 = Ansi(src.Na1, 40),
                        ty = Ansi(src.Ty, 14), fa = Ansi(src.Fa, 8), dc = Ansi(src.Dc, 8), unit = Ansi(src.Unit, 4),
                        jkamount = -line.Amount, iprj = line.Iprj, iprc = insertIprc, oprc = src.Oprc,
                        posi = Ansi(finalPosi, 10), indate = today, item = src.Item,
                        cno = Ansi(cno, 15), box = Ansi(src.Box, 10), seano = Ansi(cno, 15),
                        pno = Ansi(src.Pno, 12), gyno = Ansi(src.Gyno, 10), gyname = Ansi(src.Gyname, 60),
                        per = Ansi(per, 10), note = Ansi(note, 100), cxnno = Ansi(src.Cxnno, 18),
                        fkfs = Ansi(command.Fkfs, 10), depotno = Ansi(line.Depotno, 10),
                        type = Ansi("入库", 10), ro = (decimal)line.SourceId, ywr = Ansi(src.Ywr, 10),
                        dpm = Ansi(deptTrim, 10),
                    }, transaction: tx, cancellationToken: ct));

                // 4) ISAPPE 冲减（主程序库版语义；M_NOUPOPRC=.T. 无可观察差异——oprc/oprb 本就取库内原值）
                var (_, kcBefore, _, inserted) = await IsappeEngine.ApplyAsync(
                    conn, tx,
                    new IsappeSource(deptTrim, src.Cp, src.Nno, src.Na1, src.Ty, src.Fa, src.Unit,
                        line.Iprj, insertIprc, src.Oprc, 0, 0, finalPosi, line.Depotno, note, 0, src.Dc),
                    -line.Amount, prm, IsappeLibrary.MainProc, today, ct);
                if (inserted || kcBefore is null)
                    throw new AppException("CONTRACT_VIOLATION", $"退货冲减未命中批次（cp={src.Cp} nno={src.Nno}，已回滚）", 500);

                // 5) 回写 kcamount=冲减前批次库存（实证语义）、iprc=用户编辑值（旧 L845-846）
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbistoed SET kcamount = @kc, iprc = @iprc WHERE id = @id",
                    new { kc = kcBefore.Value, iprc = line.Iprc, id = newId },
                    transaction: tx, cancellationToken: ct));

                await tx.CommitAsync(ct);
                result.Lines.Add(new PurchaseReturnLineResult
                {
                    SourceId = line.SourceId, NewRowId = newId, Amount = line.Amount, KcBefore = kcBefore.Value,
                });
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        result.LineCount = result.Lines.Count;
        return result;
    }

    private static async Task<SrcRow?> ReadSourceAsync(SqlConnection conn, SqlTransaction tx, int id, CancellationToken ct) =>
        await conn.QueryFirstOrDefaultAsync<SrcRow>(new CommandDefinition(
            """
            SELECT id, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa, RTRIM(dc) dc,
                   RTRIM(unit) unit, jkamount, ro, iprc, iprj, oprc, RTRIM(posi) posi, item, RTRIM(box) box,
                   RTRIM(pno) pno, RTRIM(gyno) gyno, RTRIM(gyname) gyname, RTRIM(note) note,
                   RTRIM(cxnno) cxnno, RTRIM(ywr) ywr, RTRIM(dpm) dpm
            FROM tbistoed WITH (NOLOCK)
            WHERE id = @id
            """, new { id }, transaction: tx, cancellationToken: ct));

    private sealed class SrcRow
    {
        public int Id { get; set; }
        public string Cp { get; set; } = "";
        public string Nno { get; set; } = "";
        public string Na1 { get; set; } = "";
        public string Ty { get; set; } = "";
        public string Fa { get; set; } = "";
        public string Dc { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal Jkamount { get; set; }
        public decimal Ro { get; set; }
        public decimal Iprc { get; set; }
        public decimal IprcOld { get; set; }
        public decimal Iprj { get; set; }
        public decimal Oprc { get; set; }
        public string Posi { get; set; } = "";
        public int Item { get; set; }
        public string Box { get; set; } = "";
        public string Pno { get; set; } = "";
        public string Gyno { get; set; } = "";
        public string Gyname { get; set; } = "";
        public string Note { get; set; } = "";
        public string Cxnno { get; set; } = "";
        public string Ywr { get; set; } = "";
        public string Dpm { get; set; } = "";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
