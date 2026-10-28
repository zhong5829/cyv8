using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.FinanceConfirm;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.FinanceConfirm;

/// <summary>
/// 财务确认仓储实现（SQL 照抄旧 fr_sk 逆向 prg，出处逐条标注）。
/// </summary>
public sealed class FinConfirmRepository(SqlConnectionFactory factory) : IFinConfirmRepository
{
    // ════════════════════ 页1 出库确认（setsource CASE 1） ════════════════════

    /// <summary>旧 setsource CASE 1 列语义：按单聚合 round(sum(ckamount*oprc*(100-yhl)/100),2)，sk 派生，ssje=挂帐类取 OPRC-JIANSHOU。</summary>
    private const string OutGroupSql = """
        SELECT c.paper, MAX(c.dpm) AS dpm, MAX(c.mark) AS mark,
               MAX(c.gname) AS gname, MAX(c.outdate) AS outdate,
               MAX(c.per1) AS per1, MAX(c.per) AS per, MAX(c.guno) AS guno, MAX(c.pno) AS pno,
               MAX(c.yhl) AS yhl,
               CAST(ROUND(SUM(c.ckamount * c.oprc * (100 - c.yhl) / 100), 2) AS numeric(12,2)) AS oprc,
               CAST(ROUND(SUM(c.ckamount * c.oprct * (100 - c.yhl) / 100), 2) AS numeric(12,2)) AS oprct,
               SUM(c.jianshou) AS jianshou,
               MAX(c.qrdate) AS qrdate, MAX(c.remark) AS remark, MAX(c.ypaper) AS ypaper,
               CASE WHEN MAX(c.mark) = 'Y' THEN N'已确认'
                    WHEN MAX(c.mark) = 'T' THEN N'已提货'
                    WHEN MAX(c.mark) = '' AND MAX(c.guno) = '' THEN N'开单中'
                    ELSE '' END AS sk
        FROM tbsada c WITH (NOLOCK)
        WHERE {where} AND c.trigger_mark = 0
        GROUP BY c.paper
        """;

    public async Task<IReadOnlyList<FinConfirmOutRow>> SearchOutAsync(FinConfirmOutRequest request, CancellationToken ct = default)
    {
        var (where, p) = BuildOutWhere(request);
        // 旧排序：mark desc,outdate desc（SQL 侧）+ orname,Mark,outdate,Paper（本地归并）→ 聚合侧等价 ORDER BY
        var sql = OutGroupSql.Replace("{where}", where) +
                  " ORDER BY CASE WHEN MAX(c.mark) = '' THEN 0 ELSE 1 END, MAX(c.outdate) DESC, c.paper";
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<FinConfirmOutRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
        // 旧 row3 L83：REPLACE SSJE WITH IIF(INLIST(guno,'挂帐','托收','汇款'), OPRC-JIANSHOU, OPRCT-JIANSHOU)
        foreach (var r in rows)
        {
            var onCredit = r.Guno.Trim() is "挂帐" or "托收" or "汇款";
            r.Ssje = (onCredit ? r.Oprc : r.Oprct) - r.Jianshou;
        }
        return rows;
    }

    public async Task<IReadOnlyList<FinConfirmTotalRow>> TotalOutAsync(FinConfirmOutRequest request, CancellationToken ct = default)
    {
        // 旧 Z_.row54 CASE 1：对按单聚合后的游标求和 —— count(*) 单据数量, sum(oprc) 开单金额,
        //   sum(oprct) 实售金额, sum(jianshou) 减收金额, sum(oprct-jianshou) 实收金额
        var (where, p) = BuildOutWhere(request);
        var sql = """
            SELECT COUNT(*) AS cnt,
                   SUM(x.oprc) AS oprc, SUM(x.oprct) AS oprct, SUM(x.jianshou) AS jianshou,
                   SUM(x.oprct - x.jianshou) AS ssje
            FROM (SELECT c.paper,
                         CAST(ROUND(SUM(c.ckamount * c.oprc * (100 - c.yhl) / 100), 2) AS numeric(12,2)) AS oprc,
                         CAST(ROUND(SUM(c.ckamount * c.oprct * (100 - c.yhl) / 100), 2) AS numeric(12,2)) AS oprct,
                         MAX(c.jianshou) AS jianshou
                  FROM tbsada c WITH (NOLOCK)
                  WHERE {where} AND c.trigger_mark = 0
                  GROUP BY c.paper) x
            """.Replace("{where}", where);
        await using var conn = await factory.OpenAsync(ct);
        var t = await conn.QueryFirstAsync<(int cnt, decimal oprc, decimal oprct, decimal jianshou, decimal ssje)>(
            new CommandDefinition(sql, p, cancellationToken: ct));
        return
        [
            new() { Name = "单据数量", Count = t.cnt },
            new() { Name = "开单金额", Value = t.oprc },
            new() { Name = "实售金额", Value = t.oprct },
            new() { Name = "减收金额", Value = t.jianshou },
            new() { Name = "实收金额", Value = t.ssje },
        ];
    }

    private static (string where, DynamicParameters p) BuildOutWhere(FinConfirmOutRequest request)
    {
        var type = (request.Type ?? "销售").Trim();
        if (type.Length == 0) type = "销售";
        // 旧 setsource CASE 1 默认：type=?m_type and (mark='')；fr_find 状态选项：已确认 mark<>'' / 未确认 mark=''
        var where = "c.type = @type";
        var p = new DynamicParameters();
        p.Add("type", new DbString { Value = type, IsAnsi = false, Length = 10 });
        if (request.Status == 1) where += " AND c.mark = ''";
        if (request.Status == 2) where += " AND c.mark <> ''";
        // 旧 CBOPAPERTYPE：paper in (select paper from tbsada_header where papertype=?)
        if (!string.IsNullOrWhiteSpace(request.PaperType))
        {
            where += " AND c.paper IN (SELECT paper FROM tbsada_header WITH (NOLOCK) WHERE papertype = @papertype)";
            p.Add("papertype", new DbString { Value = request.PaperType.Trim(), IsAnsi = false, Length = 20 });
        }
        if (request.DateFrom.HasValue) { where += " AND c.outdate >= @from"; p.Add("from", request.DateFrom.Value.Date); }
        if (request.DateTo.HasValue) { where += " AND c.outdate < @toEnd"; p.Add("toEnd", request.DateTo.Value.Date.AddDays(1)); }
        if (!string.IsNullOrWhiteSpace(request.Paper))
        {
            where += " AND c.paper LIKE @paper";
            p.Add("paper", $"%{request.Paper.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Gname))
        {
            where += " AND c.gname LIKE @gname";
            p.Add("gname", $"%{request.Gname.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Per1))
        {
            where += " AND c.per1 = @per1";
            p.Add("per1", new DbString { Value = request.Per1.Trim(), IsAnsi = true, Length = 10 });
        }
        // 旧 fr_sk 页1 无部门数据权限过滤（P_DPMUSEM），不加
        return (where, p);
    }

    // ════════════════════ 页2 入库确认（setsource CASE 2 + row410 + row34 CASE 2） ════════════════════

    public async Task<IReadOnlyList<FinConfirmInRow>> SearchInAsync(FinConfirmInRequest request, CancellationToken ct = default)
    {
        var (where, p) = BuildInWhere(request);
        // 旧 setsource CASE 2：seano cno,gyno,gyname,indate,fkfs,pno,per,mark,isord,MAX(ywr) ywr,SUM(jkamount) jkamount,qrr,MAX(qrdate) qrdate
        //                      +cast(sum(jkamount*iprc) as dec(10,2)) iprc +cast(sum(jkamount*iprj) as dec(10,2)) iprj
        //                      GROUP BY seano,gyno,gyname,indate,fkfs,pno,per,mark,isord,qrr ORDER BY indate
        var sql = """
            SELECT c.seano AS cno, c.gyno, c.gyname, c.indate, c.fkfs, c.pno, c.per, RTRIM(c.mark) AS mark, c.isord,
                   MAX(c.ywr) AS ywr, SUM(c.jkamount) AS jkamount, c.qrr, MAX(c.qrdate) AS qrdate,
                   CAST(SUM(c.jkamount * c.iprc) AS numeric(10,2)) AS iprc,
                   CAST(SUM(c.jkamount * c.iprj) AS numeric(10,2)) AS iprj
            FROM tbistoed c WITH (NOLOCK)
            WHERE {where}
            GROUP BY c.seano, c.gyno, c.gyname, c.indate, c.fkfs, c.pno, c.per, c.mark, c.isord, c.qrr
            ORDER BY c.indate
            """.Replace("{where}", where);
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<FinConfirmInRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<FinConfirmTotalRow>> TotalInAsync(FinConfirmInRequest request, CancellationToken ct = default)
    {
        // 旧 Z_.row54 CASE 2：count(*) 单据数量, sum(iprc) 进价金额, sum(iprj) 外币价金额
        var (where, p) = BuildInWhere(request);
        var sql = """
            SELECT COUNT(DISTINCT c.seano) AS cnt, SUM(c.jkamount * c.iprc) AS iprc, SUM(c.jkamount * c.iprj) AS iprj
            FROM tbistoed c WITH (NOLOCK)
            WHERE {where}
            """.Replace("{where}", where);
        await using var conn = await factory.OpenAsync(ct);
        var t = await conn.QueryFirstAsync<(int cnt, decimal iprc, decimal iprj)>(new CommandDefinition(sql, p, cancellationToken: ct));
        return
        [
            new() { Name = "单据数量", Count = t.cnt },
            new() { Name = "进价金额", Value = t.iprc },
            new() { Name = "外币价金额", Value = t.iprj },
        ];
    }

    private static (string where, DynamicParameters p) BuildInWhere(FinConfirmInRequest request)
    {
        // 旧默认：cp<>'急件' and indate > getdate()-7 and mark<>'已确认'
        var where = "c.cp <> N'急件'";
        var p = new DynamicParameters();
        if (request.Status == 1) where += " AND c.mark <> '已确认'";
        if (request.Status == 2) where += " AND c.mark = '已确认'";
        if (request.DateFrom.HasValue) { where += " AND c.indate >= @from"; p.Add("from", request.DateFrom.Value.Date); }
        if (request.DateTo.HasValue) { where += " AND c.indate < @toEnd"; p.Add("toEnd", request.DateTo.Value.Date.AddDays(1)); }
        if (!string.IsNullOrWhiteSpace(request.Cno))
        {
            where += " AND c.seano LIKE @cno";
            p.Add("cno", $"%{request.Cno.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Gyname))
        {
            where += " AND c.gyname LIKE @gyname";
            p.Add("gyname", $"%{request.Gyname.Trim()}%");
        }
        // 旧 fr_sk 页2 无部门数据权限过滤，不加
        return (where, p);
    }

    public async Task<IReadOnlyList<FinConfirmLine>> GetInLinesAsync(string seano, string gyname, DateTime indate, string isord, CancellationToken ct = default)
    {
        // 旧 ___.row302 AfterRowColChange：cp,nno,na1,ty,fa,unit,jkamount,iprc,iprj,depotno
        //   WHERE seano=?m.cno and gyname=?m.gyname and indate=?m.indate and isord=?m.isord
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            """
            SELECT cp, nno, na1, ty, fa, unit, jkamount AS amount, iprc, iprj, depotno
            FROM tbistoed WITH (NOLOCK)
            WHERE seano = @seano AND gyname = @gyname AND indate = @indate AND isord = @isord
            ORDER BY id
            """,
            new
            {
                seano = new DbString { Value = seano.Trim(), IsAnsi = true, Length = 12 },
                gyname = new DbString { Value = gyname.Trim(), IsAnsi = false, Length = 60 },
                indate,
                isord = new DbString { Value = isord.Trim(), IsAnsi = true, Length = 10 },
            },
            cancellationToken: ct);
        return (await conn.QueryAsync<FinConfirmLine>(cmd)).AsList();
    }

    public async Task<bool> HasInYsyfAsync(string seano, string isord, CancellationToken ct = default)
    {
        // 旧 row33 CASE 2 L66：select top 1 id from tbysyf where cno=?m_cno and isord=?m_isord
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT TOP (1) 1 FROM tbysyf WITH (NOLOCK) WHERE cno = @seano AND isord = @isord",
            new
            {
                seano = new DbString { Value = seano.Trim(), IsAnsi = true, Length = 15 },
                isord = new DbString { Value = isord.Trim(), IsAnsi = true, Length = 10 },
            },
            cancellationToken: ct)) == 1;
    }

    public async Task ConfirmInAsync(FinConfirmInCommand command, string per, CancellationToken ct = default)
    {
        var seano = (command.Seano ?? "").Trim();
        var isord = (command.Isord ?? "").Trim();
        if (seano.Length == 0) throw new AppException("DOC_NOT_FOUND", "进库单号不能为空", 422);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 旧 row410 L25：UPDATE tbistoed SET mark,pno,fkfs,gyname,qrr,qrdate WHERE seano=?lcCno and isord=?m_isord
            //   mark='已确认', qrr=P_PER, qrdate=P_RQ
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbistoed SET mark = '已确认', pno = @pno, fkfs = @fkfs, gyname = @gyname,
                       qrr = @per, qrdate = GETDATE()
                WHERE seano = @seano AND isord = @isord
                """,
                new
                {
                    pno = new DbString { Value = (command.Pno ?? "").Trim(), IsAnsi = false, Length = 12 },
                    fkfs = new DbString { Value = (command.Fkfs ?? "").Trim(), IsAnsi = false, Length = 10 },
                    gyname = new DbString { Value = (command.Gyname ?? "").Trim(), IsAnsi = false, Length = 60 },
                    per = new DbString { Value = per, IsAnsi = true, Length = 10 },
                    seano = new DbString { Value = seano, IsAnsi = true, Length = 12 },
                    isord = new DbString { Value = isord, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));

            // 旧 row410 L27：UPDATE tbysyf SET pno=? WHERE cno=?lccno and isord=?m_isord and type='入库'
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbysyf SET pno = @pno WHERE cno = @seano AND isord = @isord AND type = N'入库'",
                new
                {
                    pno = new DbString { Value = $"{(command.Pno ?? "").Trim()} {per}".Trim(), IsAnsi = false, Length = 50 },
                    seano = new DbString { Value = seano, IsAnsi = true, Length = 15 },
                    isord = new DbString { Value = isord, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));

            // 旧 row410 L44-84：挂帐且有金额 → 汇总运费后写 tbysyf（type='入库'，yf=PT）
            //   M_B1 = sum(jkamount) oamou00, round(sum(CASE WHEN freight=0 THEN iprc ELSE Freight END*jkamount),2) pt,
            //          SUM((iprc-CASE WHEN freight=0 THEN iprc ELSE Freight END)*jkamount) Freight（P_WBTOHS=.F. 分支）
            //   聚合仅在挂帐分支执行（旧码 M_GZ!=0 AND FKFS='挂帐' 才取数）
            var fkfs = (command.Fkfs ?? "").Trim();
            var gyname = (command.Gyname ?? "").Trim();
            if (fkfs == "挂帐" && gyname.Length > 0)
            {
                var agg = await conn.QueryFirstOrDefaultAsync<(decimal pt, decimal freight)>(
                    new CommandDefinition(
                        """
                        SELECT ROUND(SUM(CASE WHEN ISNULL(Freight, 0) = 0 THEN iprc ELSE Freight END * jkamount), 2),
                               SUM((iprc - CASE WHEN ISNULL(Freight, 0) = 0 THEN iprc ELSE Freight END) * jkamount)
                        FROM tbistoed WITH (NOLOCK)
                        WHERE seano = @seano AND isord = @isord
                        """,
                        new
                        {
                            seano = new DbString { Value = seano, IsAnsi = true, Length = 12 },
                            isord = new DbString { Value = isord, IsAnsi = true, Length = 10 },
                        },
                        transaction: tx, cancellationToken: ct));
                if (agg.pt != 0)
                {
                // L54：select top 1 * from tbysyf where gname=?m_gname order by datime（取 cb/tick/yjb；无则插空行）
                var prior = await conn.QueryFirstOrDefaultAsync<(decimal cb, int tick, decimal yjb)>(new CommandDefinition(
                    "SELECT TOP (1) cb, tick, yjb FROM tbysyf WITH (NOLOCK) WHERE gname = @gname ORDER BY datime",
                    new { gname = new DbString { Value = gyname, IsAnsi = false, Length = 60 } },
                    transaction: tx, cancellationToken: ct));
                var priorExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname",
                    new { gname = new DbString { Value = gyname, IsAnsi = false, Length = 60 } },
                    transaction: tx, cancellationToken: ct));
                if (priorExists == 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO tbysyf (gname) VALUES (@gname)",
                        new { gname = new DbString { Value = gyname, IsAnsi = false, Length = 60 } },
                        transaction: tx, cancellationToken: ct));
                }
                // L75-82：已有 skmoney<>0 收款记录 → 仅提示（阻断）；否则 delete+insert
                var financed = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE cno = @cno AND type = N'入库' AND gname = @gname AND skmoney <> 0",
                    new
                    {
                        cno = new DbString { Value = seano, IsAnsi = true, Length = 15 },
                        gname = new DbString { Value = gyname, IsAnsi = false, Length = 60 },
                    },
                    transaction: tx, cancellationToken: ct));
                if (financed > 0)
                    throw new AppException("RECEIPT_FINANCED", "该入库单已做过收款，因为您的操作流程问题，可能会影响应收应付的准确性！", 409);
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE tbysyf WHERE cno = @cno AND type = N'入库' AND gname = @gname",
                    new
                    {
                        cno = new DbString { Value = seano, IsAnsi = true, Length = 15 },
                        gname = new DbString { Value = gyname, IsAnsi = false, Length = 60 },
                    },
                    transaction: tx, cancellationToken: ct));
                // L81：insert tbysyf(gname,datime,cno,yf,pno,fkfs,type,cb,yjb,tick,xm,isord)
                //   datime=C_RK.INDATE、yf=M_GZ、pno=M_PNO(码单+操作员)、xm=fkfs+[有运费：金额]
                var indate = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
                    "SELECT MAX(indate) FROM tbistoed WITH (NOLOCK) WHERE seano = @seano AND isord = @isord",
                    new
                    {
                        seano = new DbString { Value = seano, IsAnsi = true, Length = 12 },
                        isord = new DbString { Value = isord, IsAnsi = true, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
                var xm = fkfs + (agg.freight == 0 ? "" : $"有运费：{agg.freight:0.##}");
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbysyf (gname, datime, cno, yf, pno, fkfs, type, cb, yjb, tick, xm, isord)
                    VALUES (@gname, @datime, @cno, @yf, @pno, @fkfs, N'入库', @cb, @yjb, @tick, @xm, @isord)
                    """,
                    new
                    {
                        gname = new DbString { Value = gyname, IsAnsi = false, Length = 60 },
                        datime = indate,
                        cno = new DbString { Value = seano, IsAnsi = true, Length = 15 },
                        yf = agg.pt,
                        pno = new DbString { Value = $"{(command.Pno ?? "").Trim()} {per}".Trim(), IsAnsi = false, Length = 50 },
                        fkfs = new DbString { Value = fkfs.Length == 0 ? "入库" : fkfs, IsAnsi = false, Length = 10 },
                        cb = prior.cb,
                        yjb = prior.yjb,
                        tick = prior.tick,
                        xm = new DbString { Value = xm, IsAnsi = false, Length = 250 },
                        isord = new DbString { Value = isord, IsAnsi = true, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task CancelInAsync(string seano, string isord, string per, CancellationToken ct = default)
    {
        seano = (seano ?? "").Trim();
        isord = (isord ?? "").Trim();
        // 旧 row34 CASE 2 L175：UPDATE tbistoed SET mark,type,qrr,qrdate WHERE seano=?lcCno and isord=?m_isord
        //   mark='', type='入库', qrr='', qrdate={^1900-01-01}
        // L177：DELETE tbysyf WHERE type='入库' and cno=?lcCno
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbistoed SET mark = '', type = '入库', qrr = '', qrdate = '1900-01-01'
                WHERE seano = @seano AND isord = @isord
                """,
                new
                {
                    seano = new DbString { Value = seano, IsAnsi = true, Length = 12 },
                    isord = new DbString { Value = isord, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbysyf WHERE type = N'入库' AND cno = @seano",
                new { seano = new DbString { Value = seano, IsAnsi = true, Length = 15 } },
                transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ════════════════════ 页3 调拨确认（setsource CASE 3 + 妴_.row478 + row34 CASE 3） ════════════════════

    public async Task<IReadOnlyList<FinConfirmTransferRow>> SearchTransferAsync(FinConfirmTransferRequest request, CancellationToken ct = default)
    {
        var (where, p) = BuildTransferWhere(request);
        // 旧 setsource CASE 3：paper,mark,gname,outdate,per,guno,pno,ckamount,qrdate,id
        //   + 本地 GROUP BY PAPER：SUM(CKAMOUNT*OPRC) OPRC、SUM(CKAMOUNT*OPRCT) OPRCT、sk 派生，ORDER BY PAPER DESC
        var sql = """
            SELECT c.paper, MAX(c.mark) AS mark, MAX(c.gname) AS gname, MAX(c.outdate) AS outdate,
                   MAX(c.per) AS per, MAX(c.guno) AS guno, MAX(c.pno) AS pno,
                   SUM(c.ckamount) AS ckamount, MAX(c.qrdate) AS qrdate, MAX(c.dpm) AS dpm,
                   SUM(c.ckamount * c.oprc) AS oprc, SUM(c.ckamount * c.oprct) AS oprct,
                   CASE WHEN MAX(c.mark) = 'Y' THEN N'已确认'
                        WHEN MAX(c.mark) = 'R' THEN N'已入库'
                        WHEN MAX(c.mark) = '' AND MAX(c.guno) = '' THEN N'开单中'
                        ELSE '' END AS sk
            FROM tbsada c WITH (NOLOCK)
            WHERE {where} AND c.trigger_mark = 0
            GROUP BY c.paper
            ORDER BY c.paper DESC
            """.Replace("{where}", where);
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<FinConfirmTransferRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<FinConfirmTotalRow>> TotalTransferAsync(FinConfirmTransferRequest request, CancellationToken ct = default)
    {
        // 旧 Z_.row54 CASE 3：count(*) 单据数量, sum(oprc) 开单金额, sum(oprct) 实售金额
        var (where, p) = BuildTransferWhere(request);
        var sql = """
            SELECT COUNT(DISTINCT c.paper) AS cnt, SUM(c.ckamount * c.oprc) AS oprc, SUM(c.ckamount * c.oprct) AS oprct
            FROM tbsada c WITH (NOLOCK)
            WHERE {where} AND c.trigger_mark = 0
            """.Replace("{where}", where);
        await using var conn = await factory.OpenAsync(ct);
        var t = await conn.QueryFirstAsync<(int cnt, decimal oprc, decimal oprct)>(new CommandDefinition(sql, p, cancellationToken: ct));
        return
        [
            new() { Name = "单据数量", Count = t.cnt },
            new() { Name = "开单金额", Value = t.oprc },
            new() { Name = "实售金额", Value = t.oprct },
        ];
    }

    private static (string where, DynamicParameters p) BuildTransferWhere(FinConfirmTransferRequest request)
    {
        // 旧默认：type='调拨' and mark='' and outdate > getdate()-7
        var where = "c.type = N'调拨'";
        var p = new DynamicParameters();
        if (request.Status == 1) where += " AND c.mark = ''";
        // 旧 Dh.row32 CASE 3 fr_find：已确认 = mark='Y'（'R' 已入库不在任一选项，照抄）
        if (request.Status == 2) where += " AND c.mark = 'Y'";
        if (request.DateFrom.HasValue) { where += " AND c.outdate >= @from"; p.Add("from", request.DateFrom.Value.Date); }
        if (request.DateTo.HasValue) { where += " AND c.outdate < @toEnd"; p.Add("toEnd", request.DateTo.Value.Date.AddDays(1)); }
        if (!string.IsNullOrWhiteSpace(request.Paper))
        {
            where += " AND c.paper LIKE @paper";
            p.Add("paper", $"%{request.Paper.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Gname))
        {
            where += " AND c.gname LIKE @gname";
            p.Add("gname", $"%{request.Gname.Trim()}%");
        }
        // 旧 fr_sk 页3 无部门数据权限过滤，不加
        return (where, p);
    }

    public async Task<IReadOnlyList<FinConfirmLine>> GetTransferLinesAsync(string paper, CancellationToken ct = default)
    {
        // 旧 ⒀.row35：cp,nno,na1,ty,fa,unit,ckamount,oprc,oprct WHERE type='调拨' and paper=?m_paper
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            """
            SELECT cp, nno, na1, ty, fa, unit, ckamount AS amount, oprc, oprct
            FROM tbsada WITH (NOLOCK)
            WHERE type = N'调拨' AND paper = @paper
            ORDER BY id
            """,
            new { paper = new DbString { Value = paper.Trim(), IsAnsi = true, Length = 10 } },
            cancellationToken: ct);
        return (await conn.QueryAsync<FinConfirmLine>(cmd)).AsList();
    }

    public async Task ConfirmTransferAsync(FinConfirmTransferCommand command, string per, CancellationToken ct = default)
    {
        var paper = (command.Paper ?? "").Trim();
        var fkfs = (command.Fkfs ?? "").Trim();
        if (paper.Length == 0) throw new AppException("DOC_NOT_FOUND", "调拨单号不能为空", 422);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 旧妴_.row478 L4-29：付款方式 IN ('挂帐','托收','汇款') → 写 tbysyf（转出方 dpm，ys=OPRCT）
            if (fkfs is "挂帐" or "托收" or "汇款")
            {
                var gname = (command.Gname ?? "").Trim();
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname AND cno = @cno",
                    new
                    {
                        gname = new DbString { Value = gname, IsAnsi = false, Length = 60 },
                        cno = new DbString { Value = paper, IsAnsi = true, Length = 15 },
                    },
                    transaction: tx, cancellationToken: ct));
                if (exists > 0)
                    throw new AppException("YSYF_EXISTS", "该单在应收应付中已存在，请刷新数据！", 409);
                // L13：select tbdpm dpmbh as dpmb where dpm=?m.gname（客户名=转入部门名 → 转 dpmbh）
                var dpmB = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT TOP (1) dpmbh FROM tbdpm WITH (NOLOCK) WHERE dpm = @gname",
                    new { gname = new DbString { Value = gname, IsAnsi = false, Length = 20 } },
                    transaction: tx, cancellationToken: ct));
                var ys = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT ISNULL(SUM(c.ckamount * c.oprct), 0) FROM tbsada c WITH (NOLOCK) WHERE c.type = N'调拨' AND c.paper = @paper",
                    new { paper = new DbString { Value = paper, IsAnsi = true, Length = 10 } },
                    transaction: tx, cancellationToken: ct));
                // L27：insert tbysyf(datime,cno,pno,fkfs,type,xm,per,dpm,gname,ys,yf)
                //   datime=GETDT()、cno=C_DB.PAPER、pno=C_DB.PNO、xm=fkfs、dpm=P_DPM、ys=GZ、yf=0
                var head = await conn.QueryFirstOrDefaultAsync<(string pno, decimal oprct)>(new CommandDefinition(
                    "SELECT TOP (1) pno, oprct FROM tbsada WITH (NOLOCK) WHERE type = N'调拨' AND paper = @paper",
                    new { paper = new DbString { Value = paper, IsAnsi = true, Length = 10 } },
                    transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbysyf (datime, cno, pno, fkfs, type, xm, per, dpm, gname, ys, yf)
                    VALUES (@datime, @cno, @pno, @fkfs, N'调拨', @xm, @per, @dpm, @gname, @ys, 0)
                    """,
                    new
                    {
                        datime = DateTime.Now,
                        cno = new DbString { Value = paper, IsAnsi = true, Length = 15 },
                        pno = new DbString { Value = head.pno.Trim(), IsAnsi = false, Length = 50 },
                        fkfs = new DbString { Value = fkfs, IsAnsi = false, Length = 10 },
                        xm = new DbString { Value = fkfs, IsAnsi = false, Length = 250 },
                        per = new DbString { Value = per, IsAnsi = true, Length = 10 },
                        dpm = new DbString { Value = (command.Dpm ?? "").Trim(), IsAnsi = true, Length = 10 },
                        gname = new DbString { Value = gname, IsAnsi = false, Length = 60 },
                        ys = ys,
                    },
                    transaction: tx, cancellationToken: ct));
            }
            // L35：UPDATE tbsada SET mark='Y',guno=<付款方式>,qrdate=P_RQ WHERE Paper=?lcPaper
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsada SET mark = 'Y', guno = @guno, qrdate = GETDATE() WHERE paper = @paper AND type = N'调拨'",
                new
                {
                    guno = new DbString { Value = fkfs, IsAnsi = false, Length = 10 },
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task CancelTransferAsync(string paper, string gname, string dpm, string per, CancellationToken ct = default)
    {
        paper = (paper ?? "").Trim();
        // 旧 row34 CASE 3：mark='R' 已入库拒绝；UPDATE tbsada SET mark='' WHERE Paper；双方分公司 tbysyf 各删一条
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var mark = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT TOP (1) mark FROM tbsada WITH (UPDLOCK) WHERE type = N'调拨' AND paper = @paper",
                new { paper = new DbString { Value = paper, IsAnsi = true, Length = 10 } },
                transaction: tx, cancellationToken: ct));
            if (string.IsNullOrEmpty(mark?.Trim()))
                throw new AppException("DOC_NOT_CONFIRMED", "没有记录，或该单未确认！", 409);
            if (mark!.Trim() == "R")
                throw new AppException("DOC_RECEIVED", "该调拨单已经入库，不能还原！", 409);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsada SET mark = '' WHERE paper = @paper AND type = N'调拨'",
                new { paper = new DbString { Value = paper, IsAnsi = true, Length = 10 } },
                transaction: tx, cancellationToken: ct));
            // L223-227：tbdpm dpmbh→dpmB；DELETE tbysyf WHERE dpm=?m.dpmA and cno / dpm=?m.dpmB and cno and type='调拨'
            var dpmB = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT TOP (1) dpmbh FROM tbdpm WITH (NOLOCK) WHERE dpm = @gname",
                new { gname = new DbString { Value = (gname ?? "").Trim(), IsAnsi = false, Length = 20 } },
                transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbysyf WHERE dpm = @dpm AND cno = @cno AND type = N'调拨'",
                new
                {
                    dpm = new DbString { Value = (dpm ?? "").Trim(), IsAnsi = true, Length = 10 },
                    cno = new DbString { Value = paper, IsAnsi = true, Length = 15 },
                },
                transaction: tx, cancellationToken: ct));
            if (!string.IsNullOrWhiteSpace(dpmB) && dpmB != (dpm ?? "").Trim())
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE tbysyf WHERE dpm = @dpm AND cno = @cno AND type = N'调拨'",
                    new
                    {
                        dpm = new DbString { Value = dpmB.Trim(), IsAnsi = true, Length = 10 },
                        cno = new DbString { Value = paper, IsAnsi = true, Length = 15 },
                    },
                    transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ════════════════════ 页7 代销付款确认（setsource CASE 7 + ___.row454 + row34 CASE 7） ════════════════════

    public async Task<IReadOnlyList<FinConfirmConsignRow>> SearchConsignAsync(FinConfirmConsignRequest request, CancellationToken ct = default)
    {
        var (where, p) = BuildConsignWhere(request);
        // 旧 setsource CASE 7：(type='销售'or type='历史') and mark<>'' 必带；
        //   列：0 as 选项, case when skper='' or skper is null then '未确认' else '已确认' end as mark,
        //      paper,outdate,per,skper,depotno,ckamount,nno,na1,iprc,iprj,人进价合计,外币价合计,gname,skdate,cp,dpm
        var sql = """
            SELECT 0 AS checked,
                   CASE WHEN ISNULL(c.skper, '') = '' THEN N'未确认' ELSE N'已确认' END AS mark,
                   c.paper, c.outdate, c.per, c.skper, c.depotno, c.ckamount, c.nno, c.na1,
                   c.iprc, c.iprj, c.gname, c.skdate, c.cp, c.dpm,
                   CAST(c.iprc * c.ckamount AS numeric(15,2)) AS oprc,
                   CAST(c.iprj * c.ckamount AS numeric(15,2)) AS oprct
            FROM tbsada c WITH (NOLOCK)
            WHERE {where} AND c.trigger_mark = 0
            ORDER BY mark, c.outdate DESC, c.paper
            """.Replace("{where}", where);
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<FinConfirmConsignRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<FinConfirmTotalRow>> TotalConsignAsync(FinConfirmConsignRequest request, CancellationToken ct = default)
    {
        // 旧 Z_.row54 CASE 7：sum(iprc*ckamount) 人进价合计, sum(iprj*ckamount) 外币价合计, sum(ckamount)
        var (where, p) = BuildConsignWhere(request);
        var sql = """
            SELECT SUM(c.iprc * c.ckamount) AS iprc, SUM(c.iprj * c.ckamount) AS iprj, SUM(c.ckamount) AS qty
            FROM tbsada c WITH (NOLOCK)
            WHERE {where} AND c.trigger_mark = 0
            """.Replace("{where}", where);
        await using var conn = await factory.OpenAsync(ct);
        var t = await conn.QueryFirstAsync<(decimal iprc, decimal iprj, decimal qty)>(new CommandDefinition(sql, p, cancellationToken: ct));
        return
        [
            new() { Name = "人进价合计", Value = t.iprc },
            new() { Name = "外币价合计", Value = t.iprj },
            new() { Name = "数量合计", Value = t.qty },
        ];
    }

    private static (string where, DynamicParameters p) BuildConsignWhere(FinConfirmConsignRequest request)
    {
        var where = "c.type IN (N'销售', N'历史') AND c.mark <> ''";
        var p = new DynamicParameters();
        if (request.Status == 1) where += " AND ISNULL(c.skper, '') = ''";
        if (request.Status == 2) where += " AND ISNULL(c.skper, '') <> ''";
        if (request.DateFrom.HasValue) { where += " AND c.outdate >= @from"; p.Add("from", request.DateFrom.Value.Date); }
        if (request.DateTo.HasValue) { where += " AND c.outdate < @toEnd"; p.Add("toEnd", request.DateTo.Value.Date.AddDays(1)); }
        if (!string.IsNullOrWhiteSpace(request.Paper))
        {
            where += " AND c.paper LIKE @paper";
            p.Add("paper", $"%{request.Paper.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Gname))
        {
            where += " AND c.gname LIKE @gname";
            p.Add("gname", $"%{request.Gname.Trim()}%");
        }
        // 旧 fr_sk 页7 无部门数据权限过滤，不加
        return (where, p);
    }

    public async Task ConfirmConsignAsync(IReadOnlyList<FinConfirmConsignCommand> commands, string per, CancellationToken ct = default)
    {
        // 旧 ___.row454：预检（任一行已确认 → 刷新提示）→ 逐行 update tbsada SET skper,skdate WHERE paper+nno+cp+dpm
        if (commands.Count == 0) throw new AppException("NO_SELECTION", "请选择要确认的选项！", 422);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var c in commands)
            {
                var confirmed = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    SELECT COUNT(1) FROM tbsada WITH (UPDLOCK)
                    WHERE paper = @paper AND nno = @nno AND cp = @cp AND dpm = @dpm
                      AND (type = '销售' OR type = '历史') AND skper <> ''
                    """,
                    new
                    {
                        paper = new DbString { Value = c.Paper.Trim(), IsAnsi = true, Length = 10 },
                        nno = new DbString { Value = c.Nno.Trim(), IsAnsi = true, Length = 18 },
                        cp = new DbString { Value = c.Cp.Trim(), IsAnsi = false, Length = 10 },
                        dpm = new DbString { Value = c.Dpm.Trim(), IsAnsi = true, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
                if (confirmed > 0)
                    throw new AppException("DOC_ALREADY_CONFIRMED", "该出库单已代销付款确认，请刷新数据！", 409);
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE tbsada SET skper = @skper, skdate = @skdate
                    WHERE paper = @paper AND nno = @nno AND cp = @cp AND dpm = @dpm
                    """,
                    new
                    {
                        skper = new DbString { Value = c.Skper.Trim(), IsAnsi = true, Length = 10 },
                        skdate = c.Skdate,
                        paper = new DbString { Value = c.Paper.Trim(), IsAnsi = true, Length = 10 },
                        nno = new DbString { Value = c.Nno.Trim(), IsAnsi = true, Length = 18 },
                        cp = new DbString { Value = c.Cp.Trim(), IsAnsi = false, Length = 10 },
                        dpm = new DbString { Value = c.Dpm.Trim(), IsAnsi = true, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task CancelConsignAsync(IReadOnlyList<FinConfirmConsignCommand> commands, string per, CancellationToken ct = default)
    {
        // 旧 row34 CASE 7：逐勾选行 update tbsada SET skper='', skdate='1900-01-01' WHERE paper+nno+cp+dpm
        if (commands.Count == 0) throw new AppException("NO_SELECTION", "请选择要还原的选项！", 422);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var c in commands)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE tbsada SET skper = '', skdate = '1900-01-01'
                    WHERE paper = @paper AND nno = @nno AND cp = @cp AND dpm = @dpm
                    """,
                    new
                    {
                        paper = new DbString { Value = c.Paper.Trim(), IsAnsi = true, Length = 10 },
                        nno = new DbString { Value = c.Nno.Trim(), IsAnsi = true, Length = 18 },
                        cp = new DbString { Value = c.Cp.Trim(), IsAnsi = false, Length = 10 },
                        dpm = new DbString { Value = c.Dpm.Trim(), IsAnsi = true, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ════════════════════ 公共 ════════════════════

    /// <summary>月结保护（旧 row34 P_YJ 分支：tbyj.max(yjdate)+1 > 单据确认日期 → 拒绝）。</summary>
    public async Task<bool> IsMonthlyLockedAsync(DateTime confirmDate, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var enabled = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT ISNULL(CAST(valuel AS bit), 0) FROM tbsysset WITH (NOLOCK) WHERE RTRIM(name) = N'启用财务月结' AND type = 'system'",
            cancellationToken: ct));
        if (enabled == 0) return false;
        var yj = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(yjdate) FROM tbyj WITH (NOLOCK)", cancellationToken: ct));
        return yj.HasValue && yj.Value.AddDays(1) > confirmDate;
    }

    /// <summary>单据类型字典（旧 P__.row73 CBOPAPERTYPE Init：tbsysset name='papertype' group by valuec）。</summary>
    public async Task<IReadOnlyList<string>> GetPaperTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE RTRIM(name) = N'papertype' GROUP BY RTRIM(valuec) ORDER BY RTRIM(valuec)",
            cancellationToken: ct));
        return rows.AsList();
    }
}
