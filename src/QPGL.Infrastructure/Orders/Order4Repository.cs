using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Orders;

public interface IOrder4Repository
{
    /// <summary>计划单列表（c_dhht：type='' 按 cno 聚合，契约 §3.4）。</summary>
    Task<IReadOnlyList<PlanHeadRow>> GetPlanListAsync(string dpm, string cp, CancellationToken ct = default);

    /// <summary>新增计划单号防重（cno or orno 双向，契约 §4）。</summary>
    Task ValidateNewCnoAsync(string cno, CancellationToken ct = default);

    /// <summary>打开计划单明细（c_dhorder，契约 §3.5）。</summary>
    Task<IReadOnlyList<PlanLineRow>> GetPlanLinesAsync(string cno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>属性检索（PR_FILT 普通分支 ISOLD=false，契约 §3.2/§3.3）。</summary>
    Task<IReadOnlyList<PartPickRow>> SearchPartsAsync(PartPickQuery query, string dpm, string cp, string? suggestExp, CancellationToken ct = default);

    /// <summary>参考公式建议订数（契约 §3.8：对单件执行公式返回 oqty；0=不足订）。</summary>
    Task<decimal> GetSuggestQtyAsync(string cExpress, string nno, string dpm, string cp, DateTime? bMon = null, DateTime? eMon = null, CancellationToken ct = default);

    /// <summary>新增订货行（I1/I2；同编码已存在时相加=U4 由调用方决定）。</summary>
    Task<int> AddLineAsync(OrderLineAddInput input, string dpm, string cp, string per, bool addQty, CancellationToken ct = default);

    /// <summary>修改行（U5；oqty=0 → D4 删行，返回 deleted=true）。</summary>
    Task<bool> UpdateLineAsync(int id, decimal oqty, decimal iprj, decimal oprc, string fa, string dpm, string cp, CancellationToken ct = default);

    /// <summary>删除行（D1）。</summary>
    Task DeleteLineAsync(int id, string dpm, string cp, CancellationToken ct = default);

    /// <summary>修改单头（U2/U3：cno,orderdate,bzlx,fkfs,gyname[,gname]；先防重）。</summary>
    Task UpdateHeadAsync(string oldCno, PlanHeadInput head, string dpm, string cp, CancellationToken ct = default);

    /// <summary>删除整张计划单（D3：type='' + tbxzb 条件）。</summary>
    Task<int> DeletePlanAsync(string cno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>并入在途（U1：整单 type='历史'、orno=原cno、cno=''、item 重排、orderdate=今天）。</summary>
    Task<int> MergeToTransitAsync(string cno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>同单号改数量（U8）。</summary>
    Task ChangeQtyAsync(int id, decimal oqty, string dpm, string cp, CancellationToken ct = default);

    /// <summary>拆数量：新单号新增行（I5）+ 旧行余量（U9）。</summary>
    Task SplitLineAsync(int id, string newCno, decimal newQty, string dpm, string cp, string per, CancellationToken ct = default);

    /// <summary>整行迁移到新单号（U10）。</summary>
    Task MoveLineAsync(int id, string newCno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>售价/批发价修改（U7：该编码库存全行 + I6：tbistoch 日志）。</summary>
    Task ChangeSalePriceAsync(string cp, string nno, decimal oprc, decimal oprb, string per, string dpm, CancellationToken ct = default);

    /// <summary>属性新增（I3）/修改（U6）/删除（D2）。</summary>
    Task<int> AddProfileAsync(PartProfileInput input, string dpm, CancellationToken ct = default);
    Task UpdateProfileAsync(string oldNno, PartProfileInput input, string dpm, string cp, CancellationToken ct = default);
    Task DeleteProfileAsync(string cp, string nno, string dpm, CancellationToken ct = default);

    /// <summary>行联动明细：进仓记录（c_jk）/在途（c_zt）/供应商价（c_gys，BZ 系数折算）。</summary>
    Task<IReadOnlyList<PartInRecordRow>> GetInRecordsAsync(string cp, string nno, CancellationToken ct = default);
    Task<IReadOnlyList<PartTransitRow>> GetTransitAsync(string cp, string nno, CancellationToken ct = default);
    Task<IReadOnlyList<PartSupplyRow>> GetSupplyPricesAsync(string cp, string nno, CancellationToken ct = default);
}

public sealed class PlanHeadRow
{
    public string Cno { get; set; } = "";
    public decimal Iprj { get; set; }
    public DateTime Orderdate { get; set; }
    public string Bzlx { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
}

public sealed class PlanLineRow
{
    public int Id { get; set; }
    public int Item { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Dc { get; set; } = "";
    public decimal Kcamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Ztqty { get; set; }
    public decimal Oqty { get; set; }
    public decimal Ydqty { get; set; }
    public decimal Oprc { get; set; }
    public string Cno { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Bzlx { get; set; } = "";
    public string Oenno { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Per { get; set; } = "";
}

public sealed class PartPickRow
{
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Kcamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Ztqty { get; set; }
    public decimal Ydqty { get; set; }
    public string Nnp { get; set; } = "";
    public string Nnq { get; set; } = "";
    public string Note { get; set; } = "";
    public string Tno { get; set; } = "";
    public string Xno { get; set; } = "";
    public string Dc { get; set; } = "";
    public decimal Uqty { get; set; }
    public decimal Amjj { get; set; }
    public decimal Bmjj { get; set; }
    public string Cp { get; set; } = "";
    public decimal Oprc { get; set; }
    public decimal Oprb { get; set; }
    /// <summary>已订数（c_dhorder 同码合计）。</summary>
    public decimal Num { get; set; }
    public decimal Oamou1 { get; set; }
    public decimal Oamou2 { get; set; }
    public decimal Oamou3 { get; set; }
    public decimal Oamou4 { get; set; }
    public decimal Oamou5 { get; set; }
    public decimal Oamou6 { get; set; }
    public decimal Oamou7 { get; set; }
    public decimal Oamou8 { get; set; }
    public decimal Oamou9 { get; set; }
    public decimal Oamou10 { get; set; }
    public decimal Oamou11 { get; set; }
    public decimal Oamou12 { get; set; }
    public decimal Ckamount { get; set; }
}

public sealed class PartPickQuery
{
    /// <summary>编码：'' 全部；'*'前缀→按 cxnno；'/'前缀→包含；否则前缀（长度≥6 或含编码链）。</summary>
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Dc { get; set; } = "";
    public DateTime Date1 { get; set; } = DateTime.Today.AddMonths(-1);
    public DateTime Date2 { get; set; } = DateTime.Today;
    public decimal Num1 { get; set; }
    public decimal Num2 { get; set; } = 9999;
    public decimal Kc1 { get; set; }
    public decimal Kc2 { get; set; } = 9999;
}

public sealed class OrderLineAddInput
{
    public string Cno { get; set; } = "";
    public required string Nno { get; set; }
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Dc { get; set; } = "";
    public decimal Oqty { get; set; }
    public decimal Iprj { get; set; }
    /// <summary>专订售价（P_是否专订时写入）。</summary>
    public decimal? Oprc { get; set; }
    public DateTime Orderdate { get; set; } = DateTime.Today;
    public string Bzlx { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
}

public sealed class PlanHeadInput
{
    public string NewCno { get; set; } = "";
    public DateTime Orderdate { get; set; } = DateTime.Today;
    public string Bzlx { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
}

public sealed class PartProfileInput
{
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Iprj { get; set; }
    public string CodeTs { get; set; } = "";
    public string Eng { get; set; } = "";
    public string Nnp { get; set; } = "";
    public string Nnq { get; set; } = "";
    public decimal Uqty { get; set; }
    public string Xno { get; set; } = "";
    public string Tno { get; set; } = "";
    public decimal Amjj { get; set; }
    public decimal Bmjj { get; set; }
    public string Note { get; set; } = "";
}

public sealed class PartInRecordRow
{
    public DateTime Indate { get; set; }
    public string Gyname { get; set; } = "";
    public decimal Jkamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
}

public sealed class PartTransitRow
{
    public DateTime Orderdate { get; set; }
    public string Cno { get; set; } = "";
    public decimal Ztqty { get; set; }
    public decimal Iprj { get; set; }
}

public sealed class PartSupplyRow
{
    public string Gyname { get; set; } = "";
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Zq { get; set; }
}

/// <summary>
/// 订单生成仓储（菜单 F.1，权限码 F1；fr_order4 契约：开发文档/契约摘录/订单生成fr_order4契约.md）。
/// 无取号（cno 全人工）；写路径 I1/I2/I3/I5/I6/U1-U10/D1-D4；type=''=计划态、'历史'=在途态。
/// tbxzb 过滤：tborder → cp=@xcp + dpmusem like；tbprnoty/tbisto → cp=?p_cp（+tbisto dpmusem like）。
/// 旧系统逐语句自动提交；多行 U1/U9-U12 新系统包单事务（备案行为差异）。
/// ISOLD（订货旧编码统计）分支未实现——当前库该配置为 0（备案：若开启需补子件聚合查询）。
/// </summary>
public sealed class Order4Repository(SqlConnectionFactory factory) : IOrder4Repository
{
    // tbxzb 条件（与 Order3Repository 同源：tborder cp+dpmusem）
    private const string TborderXzb = " AND cp = @xcp AND @p_dpmusem LIKE '%' + RTRIM(dpm) + '%'";

    private static void AddXzb(DynamicParameters args, string cp)
    {
        args.Add("xcp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        args.Add("p_dpmusem", new DbString { Value = cp, IsAnsi = true, Length = 50 });
    }

    /// <summary>P_FPCW 运行时读 tbsysset（pg_proc L3285，缺省「废」）。</summary>
    private async Task<string> GetFpcwAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE name = N'废品仓位标识' AND type = 'system'",
            transaction: tx, cancellationToken: ct)) is { } v && v.Length > 0 ? v : "废";

    public async Task<IReadOnlyList<PlanHeadRow>> GetPlanListAsync(string dpm, string cp, CancellationToken ct = default)
    {
        // §3.4（___.row176:L12）：type='    ' 等价 type=''；orderdate desc
        var sql = $"""
            SELECT RTRIM(cno) cno, SUM(iprj * oqty) iprj, MAX(orderdate) orderdate,
                   MAX(RTRIM(bzlx)) bzlx, MAX(RTRIM(fkfs)) fkfs, MAX(RTRIM(gyname)) gyname, MAX(RTRIM(gname)) gname
            FROM tborder WITH (NOLOCK)
            WHERE type = ''{TborderXzb}
            GROUP BY cno
            ORDER BY MAX(orderdate) DESC
            """;
        var args = new DynamicParameters();
        AddXzb(args, cp);
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PlanHeadRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task ValidateNewCnoAsync(string cno, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tborder WITH (NOLOCK) WHERE cno = @cno OR orno = @cno",
            new { cno = Ansi(cno, 15) }, cancellationToken: ct));
        if (exists > 0) throw new AppException("CNO_EXISTS", "订货单已存在该合同号", 409);
    }

    public async Task<IReadOnlyList<PlanLineRow>> GetPlanLinesAsync(string cno, string dpm, string cp, CancellationToken ct = default)
    {
        // §3.5（___.row183）：主表 tborder o，type='' + READXZB(别名 o) → tbxzb 条件前缀 o.
        var fpcw = await GetFpcwCoreAsync(ct);
        var sql = $"""
            SELECT o.id, o.item, RTRIM(o.nno) nno, ISNULL(i.na1, p.na1) na1, ISNULL(i.ty, p.ty) ty,
                   ISNULL(i.fa, p.fa) fa, ISNULL(i.unit, p.unit) unit, ISNULL(i.dc, p.dc) dc,
                   ISNULL(k.kcamount, 0) + ISNULL(mx.xam, 0) kcamount,
                   o.iprj, CASE WHEN o.oprc > 0 THEN o.oprc ELSE ISNULL(i.iprc, 0) END iprc,
                   ISNULL(z.ztqty, 0) ztqty, o.oqty,
                   ISNULL(p.uqty - ISNULL(k.kcamount, 0) - ISNULL(z.oqty, 0), 0) ydqty,
                   o.oprc, RTRIM(o.cno) cno, RTRIM(o.fkfs) fkfs, RTRIM(o.gyname) gyname, RTRIM(o.bzlx) bzlx,
                   ISNULL(g.oenno, '') oenno, RTRIM(o.gname) gname, RTRIM(o.per) per
            FROM tborder o WITH (NOLOCK)
            LEFT JOIN tbprnoty p WITH (NOLOCK) ON o.cp = p.cp AND o.nno = p.nno
            LEFT JOIN tbisto i WITH (NOLOCK) ON o.cp = i.cp AND o.nno = i.nno AND i.id = (SELECT MAX(id) FROM tbisto WHERE cp = o.cp AND nno = o.nno)
            LEFT JOIN (SELECT cp, nno, SUM(kcamount) kcamount FROM tbisto WITH (NOLOCK) WHERE posi <> @fpcw AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) k ON o.cp = k.cp AND o.nno = k.nno
            LEFT JOIN (SELECT cp, nno, SUM(oqty - qty) ztqty, SUM(oqty) oqty FROM tborder WITH (NOLOCK) WHERE type = '历史' AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) z ON o.cp = z.cp AND o.nno = z.nno
            LEFT JOIN (SELECT x.cp, x.nno, SUM(v.kcamount) xam FROM tbprnoty x WITH (NOLOCK) LEFT JOIN tbisto v WITH (NOLOCK) ON x.cp = v.cp WHERE v.posi <> @fpcw AND v.nno IN (CASE WHEN x.nno = x.nnp THEN '' ELSE x.nnp END, CASE WHEN x.nno = x.nnq THEN '' ELSE CASE WHEN x.nnp = x.nnq THEN '' ELSE x.nnq END END) GROUP BY x.cp, x.nno) mx ON o.cp = mx.cp AND o.nno = mx.nno
            LEFT JOIN (SELECT cp, nno, MAX(oenno) oenno FROM tbgys WHERE gyname = @gyname GROUP BY cp, nno) g ON o.cp = g.cp AND o.nno = g.nno
            WHERE o.cno = @cno AND o.type = '' AND o.cp = @xcp AND @p_dpmusem LIKE '%' + RTRIM(o.dpm) + '%'
            ORDER BY o.id
            """;
        var gyname = await GetCurrentGynameAsync(ct);
        var args = new DynamicParameters(new
        {
            cno = Ansi(cno, 15), fpcw = Ansi(fpcw, 10), gyname = Ansi(gyname, 40),
        });
        AddXzb(args, cp);
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PlanLineRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PartPickRow>> SearchPartsAsync(PartPickQuery query, string dpm, string cp, string? suggestExp, CancellationToken ct = default)
    {
        // §3.2 PR_FILT 普通分支（ISOLD=false）；§3.3 条件构造
        var fpcw = await GetFpcwCoreAsync(ct);
        var where = "p.cp = @p_cp";
        var args = new DynamicParameters(new
        {
            p_cp = Ansi(cp, 10), p_dpm = Ansi(dpm, 10), p_dpmusem = Ansi(cp, 50),
            fpcw = Ansi(fpcw, 10), m_date1 = query.Date1.Date, m_date2 = query.Date2.Date,
        });
        var nno = query.Nno?.Trim() ?? "";
        if (nno.Length > 0)
        {
            if (nno.StartsWith("*"))
            {
                var m = nno[1..];
                where += " AND p.cxnno BETWEEN @nno AND @nno + 'ZZZZ'";
                args.Add("nno", Ansi(m, 18));
            }
            else if (nno.StartsWith("/"))
            {
                where += " AND p.nno LIKE @nnoLike";
                args.Add("nnoLike", new DbString { Value = "%" + nno[1..] + "%", IsAnsi = true, Length = 20 });
            }
            else
            {
                where += " AND (p.nno BETWEEN @nno AND @nno + 'ZZZZ'";
                args.Add("nno", Ansi(nno, 18));
                if (nno.Length >= 6 && !nno.Contains('+'))
                    where += " OR p.nnp = @nno OR p.nnq = @nno";
                where += ")";
            }
        }
        var na1 = query.Na1?.Trim() ?? "";
        if (na1.Length > 0)
        {
            if (na1.StartsWith("/"))
            {
                where += " AND p.na1 LIKE @na1Like";
                args.Add("na1Like", new DbString { Value = "%" + na1[1..] + "%", IsAnsi = true, Length = 42 });
            }
            else
            {
                where += " AND p.na1 BETWEEN @na1 AND @na1 + 'ZZZZ'";
                args.Add("na1", Ansi(na1, 40));
            }
        }
        if (!string.IsNullOrWhiteSpace(query.Ty))
        {
            where += " AND p.ty + p.note LIKE @tyLike";
            args.Add("tyLike", new DbString { Value = "%" + query.Ty.Trim() + "%", IsAnsi = true, Length = 40 });
        }
        if (!string.IsNullOrWhiteSpace(query.Dc))
        {
            where += " AND p.dc BETWEEN @dc AND @dc + 'ZZZZ'";
            args.Add("dc", Ansi(query.Dc.Trim(), 8));
        }
        if (query.Num1 != 0 || query.Num2 != 9999)
            where += " AND ckamount >= @num1 AND ckamount <= @num2";
        if (query.Kc1 != 0 || query.Kc2 != 9999)
            where += " AND k.kcamount >= @kc1 AND k.kcamount <= @kc2";

        // ydqty = ISNULL((SETEXP + p.nno), 0)；suggestExp 为空 → 0
        var ydCol = string.IsNullOrWhiteSpace(suggestExp) ? "0 AS ydqty" : $"ISNULL(({suggestExp}p.nno), 0) AS ydqty";

        var sql = $"""
            SELECT RTRIM(p.nno) nno, RTRIM(p.na1) na1, RTRIM(p.ty) ty, RTRIM(p.fa) fa, RTRIM(p.unit) unit,
                   ISNULL(k.kcamount, 0) + ISNULL(mx.xam, 0) kcamount, p.iprj,
                   ISNULL(z.ztqty, 0) ztqty, {ydCol},
                   RTRIM(p.nnp) nnp, RTRIM(p.nnq) nnq, RTRIM(p.note) note, RTRIM(p.tno) tno, RTRIM(p.xno) xno,
                   RTRIM(p.dc) dc, p.uqty, ISNULL(jj.amjj, 0) amjj, ISNULL(jj.bmjj, 0) bmjj, RTRIM(p.cp) cp,
                   CASE WHEN k.kcweight > 0 THEN k.kcweight ELSE ISNULL(i.iprc, 0) END iprc,
                   ISNULL(i.oprc, 0) oprc, ISNULL(i.oprb, 0) oprb, 0 AS num,
                   ISNULL(r.oamou1, 0) oamou1, ISNULL(r.oamou2, 0) oamou2, ISNULL(r.oamou3, 0) oamou3,
                   ISNULL(r.oamou4, 0) oamou4, ISNULL(r.oamou5, 0) oamou5, ISNULL(r.oamou6, 0) oamou6,
                   ISNULL(r.oamou7, 0) oamou7, ISNULL(r.oamou8, 0) oamou8, ISNULL(r.oamou9, 0) oamou9,
                   ISNULL(r.oamou10, 0) oamou10, ISNULL(r.oamou11, 0) oamou11, ISNULL(r.oamou12, 0) oamou12,
                   ISNULL(ck.ckamount, 0) ckamount
            FROM tbprnoty p WITH (NOLOCK)
            LEFT JOIN (SELECT dpm, cp, nno, MAX(amjj) amjj, MAX(bmjj) bmjj FROM tbistojj WITH (NOLOCK) WHERE dpm = @p_dpm GROUP BY dpm, cp, nno) jj ON p.cp = jj.cp AND p.nno = jj.nno
            LEFT JOIN (SELECT cp, nno, SUM(oamou1) oamou1, SUM(oamou2) oamou2, SUM(oamou3) oamou3, SUM(oamou4) oamou4, SUM(oamou5) oamou5, SUM(oamou6) oamou6, SUM(oamou7) oamou7, SUM(oamou8) oamou8, SUM(oamou9) oamou9, SUM(oamou10) oamou10, SUM(oamou11) oamou11, SUM(oamou12) oamou12 FROM tbrtsamo WITH (NOLOCK) WHERE dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) r ON p.cp = r.cp AND p.nno = r.nno
            LEFT JOIN (SELECT cp, nno, SUM(kcamount) kcamount, MAX(id) id, SUM(kcamount * iprc) kcweight FROM tbisto WITH (NOLOCK) WHERE posi <> @fpcw AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) k ON p.cp = k.cp AND p.nno = k.nno
            LEFT JOIN tbisto i WITH (NOLOCK) ON p.cp = i.cp AND p.nno = i.nno AND k.id = i.id
            LEFT JOIN tbcomplex c ON p.code_ts = c.code_ts
            LEFT JOIN (SELECT x.cp, x.nno, SUM(v.kcamount) xam FROM tbprnoty x WITH (NOLOCK) LEFT JOIN tbisto v WITH (NOLOCK) ON x.cp = v.cp WHERE v.posi <> @fpcw AND v.nno IN (CASE WHEN x.nno = x.nnp THEN '' ELSE x.nnp END, CASE WHEN x.nno = x.nnq THEN '' ELSE CASE WHEN x.nnp = x.nnq THEN '' ELSE x.nnq END END) GROUP BY x.cp, x.nno) mx ON p.cp = mx.cp AND p.nno = mx.nno
            LEFT JOIN (SELECT cp, nno, SUM(oqty - qty) ztqty FROM tborder WITH (NOLOCK) WHERE type = '历史' AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) z ON p.cp = z.cp AND p.nno = z.nno
            LEFT JOIN (SELECT p1.cp, p1.nno, SUM(ckamount) ckamount FROM tbprnoty p1 WITH (NOLOCK) LEFT JOIN (SELECT cp, nno, ckamount FROM tbsada WITH (NOLOCK) WHERE outdate >= @m_date1 AND outdate <= @m_date2 AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND (type = '历史' OR type = '销售') UNION ALL SELECT cp, nno, ckamount FROM tbxlsa WITH (NOLOCK) WHERE dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND outdate >= @m_date1 AND outdate <= @m_date2) aa ON p1.cp = aa.cp WHERE aa.nno IN (p1.nno, p1.nnp, p1.nnq) GROUP BY p1.cp, p1.nno) ck ON p.nno = ck.nno AND p.cp = ck.cp
            LEFT JOIN tbcomplex tbc ON p.code_ts = tbc.code_ts
            WHERE {where}
            ORDER BY p.nno
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<PartPickRow>(new CommandDefinition(sql, args, cancellationToken: ct))).AsList();
        return rows;
    }

    public async Task<decimal> GetSuggestQtyAsync(string cExpress, string nno, string dpm, string cp, DateTime? bMon, DateTime? eMon, CancellationToken ct = default)
    {
        // §3.8（___.row589）：公式变换 → 内层 w 子查询 → 外层单件求值
        var exp = cExpress.Trim()
            .Replace("P01", "kcamount").Replace("P03", "ztqty").Replace("P04", "amjj")
            .Replace("P05", "bmjj").Replace("P06", "Coeffict").Replace("P09", "ckamount")
            .Replace("P10", "ysamount").Replace("P11", "dbztamount");
        var hasP12 = exp.Contains("P12");
        if (hasP12) exp = exp.Replace("P12", "xqcs");
        var paras = (await GetParamsCoreAsync(ct)).Where(p => p.paraid is "P07" or "P08");
        foreach (var (paraid, nvalue) in paras)
            exp = exp.Replace(paraid, nvalue.ToString("0.##"));
        if (exp.Contains("P02"))
        {
            if (bMon is null || eMon is null) return 0;  // P02 需月区间，未提供则不计
            var rb = bMon.Value;
            var reRaw = eMon.Value;
            var cur = DateTime.Today;
            var re = reRaw > cur ? cur : reRaw;
            if (re < rb) re = rb;
            var months = (re.Year - rb.Year) * 12 + (re.Month - rb.Month) + 1;
            months = Math.Min(months, 12);
            var parts = new List<string>();
            for (var m = 1; m <= months; m++)
            {
                var ln = m + rb.Month - 1;
                parts.Add($"oamou{(ln > 12 ? ln - 12 : ln)}");
            }
            exp = exp.Replace("P02", $"({string.Join("+", parts)})/{months}");
        }
        var p12Select = hasP12 ? " ,ISNULL(xqcs,0) xqcs " : "";
        var p12Join = hasP12
            ? " left join (select cp,nno,count(nno) xqcs from (select cp,nno from tbsada with(nolock) where outdate>=@m_date1 and outdate<=@m_date2 and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and type='需求') xx group by xx.cp,xx.nno) aaxq on p.cp=aaxq.cp and p.nno=aaxq.nno"
            : "";
        var mDstr = "outdate>=@m_date1 and outdate<=@m_date2";
        var sql = $"""
            SELECT ({exp}) FROM (
              select p.nno, isnull(k.kcamount,0) kcamount, isnull(p.uqty-k.kcamount-z.oqty,0) ydqty,
                     ISNULL(s.amjj,0) amjj, ISNULL(s.bmjj,0) bmjj, ISNULL(y.coeffict,1) coeffict,
                     ISNULL(r.oamou1,0) oamou1, ISNULL(r.oamou2,0) oamou2, ISNULL(r.oamou3,0) oamou3,
                     ISNULL(r.oamou4,0) oamou4, ISNULL(r.oamou5,0) oamou5, ISNULL(r.oamou6,0) oamou6,
                     ISNULL(r.oamou7,0) oamou7, ISNULL(r.oamou8,0) oamou8, ISNULL(r.oamou9,0) oamou9,
                     ISNULL(r.oamou10,0) oamou10, ISNULL(r.oamou11,0) oamou11, ISNULL(r.oamou12,0) oamou12,
                     ISNULL(ck.ckamount,0) ckamount, ISNULL(ys.ysamount,0) ysamount, ISNULL(dbzt.dbztamount,0) dbztamount{p12Select}
              from tbprnoty p
              left join tbistojj s on s.cp=@p_cp and s.nno=p.nno and s.dpm=@p_dpm
              left join (select cp,nno,sum(oamou1) oamou1,sum(oamou2) oamou2,sum(oamou3) oamou3,sum(oamou4) oamou4,sum(oamou5) oamou5,sum(oamou6) oamou6,sum(oamou7) oamou7,sum(oamou8) oamou8,sum(oamou9) oamou9,sum(oamou10) oamou10,sum(oamou11) oamou11,sum(oamou12) oamou12 from tbrtsamo where dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) group by cp,nno) r on p.cp=r.cp and p.nno=r.nno
              left join tbdccoef y on p.dc=y.dc
              left join (select cp,nno,sum(kcamount) kcamount,max(id) id from tbisto with(nolock) group by cp,nno) k on p.cp=k.cp and p.nno=k.nno
              left join tbisto i with(nolock) on p.cp=i.cp and p.nno=i.nno and k.id=i.id
              left join (SELECT cp, nno, SUM(oqty - qty) ztqty, sum(oqty) oqty FROM tborder with(nolock) where type='历史' and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) group by cp,nno) z on p.cp=z.cp and p.nno=z.nno
              left join tbcomplex c on p.code_ts = c.code_ts
              left join (select cp,nno,sum(ckamount) dbztamount from tbsada with(nolock) where type='调拨' and mark<>'R' and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) group by cp,nno) dbzt on p.cp=dbzt.cp and p.nno=dbzt.nno
              left join (select p1.cp,p1.nno,sum(ckamount) ckamount from tbprnoty p1 with(nolock) left join (select cp,nno,ckamount from tbsada with(nolock) where {mDstr} and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and (type='历史' or type='销售') union all select cp,nno,ckamount from tbxlsa with(nolock) where dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and {mDstr}) as aa on p1.cp=aa.cp where aa.nno in (p1.nno,p1.nnp,p1.nnq) group by p1.cp,p1.nno) ck on p.nno=ck.nno and p.cp=ck.cp
              left join (select cp,nno,sum(ckamount) ysamount from (select cp,nno,ckamount from tbsada with(nolock) where {mDstr} and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and type='预售') aa group by cp,nno) ys on p.cp=ys.cp and p.nno=ys.nno{p12Join}
            ) w WHERE ({exp}) >= 1 AND w.nno = @nno
            """;
        var args = new DynamicParameters(new
        {
            p_cp = Ansi(cp, 10), p_dpm = Ansi(dpm, 10), p_dpmusem = Ansi(cp, 50),
            nno = Ansi(nno, 18), m_date1 = DateTime.Today.AddYears(-1), m_date2 = DateTime.Today,
        });
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(sql, args, cancellationToken: ct)) ?? 0;
    }

    public async Task<int> AddLineAsync(OrderLineAddInput input, string dpm, string cp, string per, bool addQty, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Cno)) throw new AppException("CNO_REQUIRED", "合同号不能为空", 422);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 同编码已存在 → 相加（U4，___.row352:L32）
            var existArgs = new DynamicParameters(new { cno = Ansi(input.Cno, 15), nno = Ansi(input.Nno, 18) });
            AddXzb(existArgs, cp);
            var existing = await conn.QueryFirstOrDefaultAsync<(int id, decimal oqty)>(new CommandDefinition(
                $"SELECT id, oqty FROM tborder WITH (UPDLOCK) WHERE cno = @cno AND type = '' AND nno = @nno{TborderXzb}",
                existArgs, transaction: tx, cancellationToken: ct));
            int newId;
            if (existing.id != 0 && addQty)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tborder SET oqty = @oqty WHERE id = @id",
                    new { oqty = existing.oqty + input.Oqty, id = existing.id }, transaction: tx, cancellationToken: ct));
                newId = existing.id;
            }
            else if (existing.id != 0)
            {
                throw new AppException("LINE_EXISTS", "该编码已在当前单中，是否相加？", 409);
            }
            else
            {
                // I1（专订含 oprc）/I2（非专订）——列集原样 + dpm 自动补
                var hasOprc = input.Oprc is > 0;
                var cols = "cp,nno,na1,ty,fa,unit,dc,oqty,iprj,cno,orderdate,bzlx,fkfs,gyname,gname,per" + (hasOprc ? ",oprc" : "") + ",dpm";
                var values = "@cp,@nno,@na1,@ty,@fa,@unit,@dc,@oqty,@iprj,@cno,@orderdate,@bzlx,@fkfs,@gyname,@gname,@per" + (hasOprc ? ",@oprc" : "") + ",@dpm";
                newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    $"INSERT INTO tborder ({cols}) OUTPUT INSERTED.id VALUES ({values})",
                    new
                    {
                        cp = Ansi(cp, 10), nno = Ansi(input.Nno, 18), na1 = Ansi(input.Na1, 40),
                        ty = Ansi(input.Ty, 14), fa = Ansi(input.Fa, 8), unit = Ansi(input.Unit, 4),
                        dc = Ansi(input.Dc, 8), oqty = input.Oqty, iprj = input.Iprj, cno = Ansi(input.Cno, 15),
                        orderdate = input.Orderdate.Date, bzlx = Ansi(input.Bzlx, 10), fkfs = Ansi(input.Fkfs, 10),
                        gyname = Ansi(input.Gyname, 60), gname = Ansi(input.Gname, 40), per = Ansi(per, 10),
                        oprc = input.Oprc ?? 0, dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return newId;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> UpdateLineAsync(int id, decimal oqty, decimal iprj, decimal oprc, string fa, string dpm, string cp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // U5（___.row352:L55）：oqty,iprj,oprc,fa
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tborder SET oqty = @oqty, iprj = @iprj, oprc = @oprc, fa = @fa WHERE id = @id",
                new { oqty, iprj, oprc, fa = Ansi(fa, 8), id }, transaction: tx, cancellationToken: ct));
            // D4：oqty=0 → 删行
            if (oqty == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE tborder WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                return true;
            }
            await tx.CommitAsync(ct);
            return false;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteLineAsync(int id, string dpm, string cp, CancellationToken ct = default)
    {
        // D1（k_.row35:L8）：delete by id（id= 前缀无 tbxzb）
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tborder WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task UpdateHeadAsync(string oldCno, PlanHeadInput head, string dpm, string cp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 防重（___.row180:L32）：cno=?m_cno or orno=?m_cno
            if (!string.Equals(head.NewCno.Trim(), oldCno.Trim(), StringComparison.Ordinal))
            {
                var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tborder WITH (NOLOCK) WHERE cno = @cno OR orno = @cno",
                    new { cno = Ansi(head.NewCno, 15) }, transaction: tx, cancellationToken: ct));
                if (dup > 0) throw new AppException("CNO_EXISTS", "订货单已存在该合同号", 409);
            }
            // U2/U3（___.row180:L52/L55）：gname 非空才更新该列
            var setGname = !string.IsNullOrWhiteSpace(head.Gname);
            var sql = setGname
                ? "UPDATE tborder SET cno = @newCno, orderdate = @orderdate, bzlx = @bzlx, fkfs = @fkfs, gyname = @gyname, gname = @gname WHERE cno = @oldCno" + TborderXzb
                : "UPDATE tborder SET cno = @newCno, orderdate = @orderdate, bzlx = @bzlx, fkfs = @fkfs, gyname = @gyname WHERE cno = @oldCno" + TborderXzb;
            var args = new DynamicParameters(new
            {
                newCno = Ansi(head.NewCno, 15), orderdate = head.Orderdate.Date, bzlx = Ansi(head.Bzlx, 10),
                fkfs = Ansi(head.Fkfs, 10), gyname = Ansi(head.Gyname, 60), gname = Ansi(head.Gname, 40),
                oldCno = Ansi(oldCno, 15),
            });
            AddXzb(args, cp);
            await conn.ExecuteAsync(new CommandDefinition(sql, args, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> DeletePlanAsync(string cno, string dpm, string cp, CancellationToken ct = default)
    {
        // D3（o__.row181:L10）：type=''（旧 '    ' 尾随空格等价）+ tbxzb
        await using var conn = await factory.OpenAsync(ct);
        var args = new DynamicParameters(new { cno = Ansi(cno, 15) });
        AddXzb(args, cp);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"DELETE tborder WHERE cno = @cno AND type = ''{TborderXzb}", args, cancellationToken: ct));
    }

    public async Task<int> MergeToTransitAsync(string cno, string dpm, string cp, CancellationToken ct = default)
    {
        // U1（燂.row162:L30）：SCAN 逐行 type='历史'、orno=原cno、item=1..n、orderdate=P_RQ、cno=''（沙河丰田保留——不激活）
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var args = new DynamicParameters(new { cno = Ansi(cno, 15) });
            AddXzb(args, cp);
            var ids = (await conn.QueryAsync<int>(new CommandDefinition(
                $"SELECT id FROM tborder WITH (UPDLOCK) WHERE cno = @cno AND type = ''{TborderXzb} ORDER BY id",
                args, transaction: tx, cancellationToken: ct))).AsList();
            var n = 0;
            foreach (var id in ids)
            {
                n++;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tborder SET type = '历史', orno = @orno, cno = '', item = @item, orderdate = @orderdate WHERE id = @id",
                    new { orno = Ansi(cno, 15), item = n, orderdate = DateTime.Today, id },
                    transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return n;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task ChangeQtyAsync(int id, decimal oqty, string dpm, string cp, CancellationToken ct = default)
    {
        // U8（g__.row510:L33）
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tborder SET oqty = @oqty WHERE id = @id", new { oqty, id }, cancellationToken: ct));
    }

    public async Task SplitLineAsync(int id, string newCno, decimal newQty, string dpm, string cp, string per, CancellationToken ct = default)
    {
        // I5 + U9（g__.row510:L46/L48/L55）：按新单号新增行 + 旧行余量
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var src = await conn.QueryFirstOrDefaultAsync<PlanLineRow>(new CommandDefinition(
                "SELECT * FROM tborder WITH (UPDLOCK) WHERE id = @id", new { id },
                transaction: tx, cancellationToken: ct));
            if (src is null) throw new AppException("LINE_NOT_FOUND", "订单行不存在", 404);

            var cpRow = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT RTRIM(cp) FROM tborder WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
            var maxItem = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT MAX(item) FROM tborder WITH (NOLOCK) WHERE cno = @cno",
                new { cno = Ansi(newCno, 15) }, transaction: tx, cancellationToken: ct));

            // U9：旧行余量
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tborder SET oqty = @remain WHERE id = @id",
                new { remain = Math.Max(src.Oqty - newQty, 0), id }, transaction: tx, cancellationToken: ct));

            if (newQty < src.Oqty)
            {
                // I5：新单号新增一行（oqty=新数量，item=SUM(item)+1）
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tborder (cp, cno, item, nno, na1, ty, fa, unit, dc, oqty, iprj, orderdate, bzlx, fkfs, gyname, gname, per, dpm)
                    SELECT cp, @newCno, @item, nno, na1, ty, fa, unit, dc, @newQty, iprj, orderdate, bzlx, fkfs, gyname, gname, per, dpm
                    FROM tborder WHERE id = @id
                    """,
                    new { newCno = Ansi(newCno, 15), item = (maxItem ?? 0) + 1, newQty, id },
                    transaction: tx, cancellationToken: ct));
            }
            else
            {
                // U10：整行迁移到新单号
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tborder SET cno = @newCno, item = @item WHERE id = @id",
                    new { newCno = Ansi(newCno, 15), item = (maxItem ?? 0) + 1, id },
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

    public async Task MoveLineAsync(int id, string newCno, string dpm, string cp, CancellationToken ct = default)
    {
        // U10（g__.row510:L55）
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var maxItem = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT MAX(item) FROM tborder WITH (NOLOCK) WHERE cno = @cno",
                new { cno = Ansi(newCno, 15) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tborder SET cno = @newCno, item = @item WHERE id = @id",
                new { newCno = Ansi(newCno, 15), item = (maxItem ?? 0) + 1, id }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task ChangeSalePriceAsync(string cp, string nno, decimal oprc, decimal oprb, string per, string dpm, CancellationToken ct = default)
    {
        // U7（L__.row497:L24）+ I6（L36）：更新 tbisto 售价/批发价 + tbistoch 日志
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await conn.QueryFirstOrDefaultAsync<(decimal oprc, decimal oprb)>(new CommandDefinition(
                "SELECT TOP (1) oprc, oprb FROM tbisto WITH (UPDLOCK) WHERE cp = @cp AND nno = @nno ORDER BY id DESC",
                new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbisto SET oprc = @oprc, oprb = @oprb WHERE cp = @cp AND nno = @nno",
                new { oprc, oprb, cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, transaction: tx, cancellationToken: ct));

            var note = (oprc != old.oprc ? $"  售价:{old.oprc}-->{oprc}" : "") +
                       (oprb != old.oprb ? $"  批发价:{old.oprb}-->{oprb}" : "");
            if (note.Length > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO tbistoch (CP, NNO, NA1, XGDATE, per, note, dpm) VALUES (@cp, @nno, @na1, @xgdate, @per, @note, @dpm)",
                    new
                    {
                        cp = Ansi(cp, 10), nno = Ansi(nno, 18),
                        na1 = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                            "SELECT TOP (1) RTRIM(na1) FROM tbisto WHERE cp = @cp AND nno = @nno",
                            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, transaction: tx, cancellationToken: ct)) ?? "",
                        xgdate = DateTime.Now, per = Ansi(per, 6), note = Ansi(note, 100), dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> AddProfileAsync(PartProfileInput input, string dpm, CancellationToken ct = default)
    {
        // I3（鵎_.row422:L25）
        if (string.IsNullOrWhiteSpace(input.Nno)) throw new AppException("NNO_REQUIRED", "编码不能为空", 422);
        await using var conn = await factory.OpenAsync(ct);
        var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbprnoty WITH (NOLOCK) WHERE nno = @nno",
            new { nno = Ansi(input.Nno, 18) }, cancellationToken: ct));
        if (dup > 0) throw new AppException("NNO_DUP", "该编码已存在", 409);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO tbprnoty
                (cp, nno, na1, ty, fa, dc, unit, iprj, code_ts, eng, nnp, nnq, uqty, xno, tno, amjj, bmjj, note)
            OUTPUT INSERTED.id
            VALUES
                (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @iprj, @codeTs, @eng, @nnp, @nnq, @uqty, @xno, @tno, @amjj, @bmjj, @note)
            """,
            new
            {
                cp = Ansi(input.Cp, 10), nno = Ansi(input.Nno, 18), na1 = Ansi(input.Na1, 40),
                ty = Ansi(input.Ty, 14), fa = Ansi(input.Fa, 8), dc = Ansi(input.Dc, 8), unit = Ansi(input.Unit, 4),
                iprj = input.Iprj, codeTs = Ansi(input.CodeTs, 20), eng = Ansi(input.Eng, 20),
                nnp = Ansi(input.Nnp, 18), nnq = Ansi(input.Nnq, 18), uqty = input.Uqty,
                xno = Ansi(input.Xno, 10), tno = Ansi(input.Tno, 10), amjj = input.Amjj, bmjj = input.Bmjj,
                note = Ansi(input.Note, 254),
            }, cancellationToken: ct));
    }

    public async Task UpdateProfileAsync(string oldNno, PartProfileInput input, string dpm, string cp, CancellationToken ct = default)
    {
        // U6（鵎_.row422:L57）：17 列 + 改码
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tbprnoty SET nno = @nno, na1 = @na1, ty = @ty, fa = @fa, dc = @dc, unit = @unit, iprj = @iprj,
                   code_ts = @codeTs, eng = @eng, nnp = @nnp, nnq = @nnq, uqty = @uqty, xno = @xno, tno = @tno,
                   amjj = @amjj, bmjj = @bmjj, note = @note
            WHERE cp = @cp AND nno = @oldNno
            """,
            new
            {
                nno = Ansi(input.Nno, 18), na1 = Ansi(input.Na1, 40), ty = Ansi(input.Ty, 14), fa = Ansi(input.Fa, 8),
                dc = Ansi(input.Dc, 8), unit = Ansi(input.Unit, 4), iprj = input.Iprj, codeTs = Ansi(input.CodeTs, 20),
                eng = Ansi(input.Eng, 20), nnp = Ansi(input.Nnp, 18), nnq = Ansi(input.Nnq, 18), uqty = input.Uqty,
                xno = Ansi(input.Xno, 10), tno = Ansi(input.Tno, 10), amjj = input.Amjj, bmjj = input.Bmjj,
                note = Ansi(input.Note, 254), cp = Ansi(cp, 10), oldNno = Ansi(oldNno, 18),
            }, cancellationToken: ct));
        // U14 dbo.updatenno / U15 EDITALL 未复刻（备案：属性改码后的历史单据编码同步）
    }

    public async Task DeleteProfileAsync(string cp, string nno, string dpm, CancellationToken ct = default)
    {
        // D2（卒_.row333:L11）
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tbprnoty WHERE cp = @cp AND nno = @nno",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PartInRecordRow>> GetInRecordsAsync(string cp, string nno, CancellationToken ct = default)
    {
        // §3.6（Y_.row7:L20）：c_jk
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PartInRecordRow>(new CommandDefinition(
            "SELECT indate, RTRIM(gyname) gyname, jkamount, iprj, iprc FROM tbistoed WITH (NOLOCK) " +
            "WHERE cp = @cp AND (nno = @nno OR nno IN (SELECT ISNULL(NULLIF(nnp, ''), nno) FROM tbprnoty WHERE cp = @cp AND nno = @nno)) ORDER BY indate DESC",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PartTransitRow>> GetTransitAsync(string cp, string nno, CancellationToken ct = default)
    {
        // §3.6（Y_.row7:L21）：c_zt
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PartTransitRow>(new CommandDefinition(
            "SELECT MAX(orderdate) orderdate, RTRIM(cno) cno, SUM(oqty - qty) ztqty, MAX(iprj) iprj " +
            "FROM tborder WITH (NOLOCK) WHERE cp = @cp AND nno = @nno AND type = '历史' AND oqty > qty " +
            "GROUP BY cno ORDER BY MAX(orderdate) DESC",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PartSupplyRow>> GetSupplyPricesAsync(string cp, string nno, CancellationToken ct = default)
    {
        // §3.6（Y_.row7:L22）：c_gys（BZ 系数折算）
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PartSupplyRow>(new CommandDefinition(
            """
            SELECT RTRIM(a.gyname) gyname, a.iprj,
                   CAST(a.iprj * (CASE WHEN ISNULL(b.valuen, 0) = 0 THEN 1 ELSE b.valuen END) AS numeric(18, 2)) iprc, a.zq
            FROM tbgys a WITH (NOLOCK)
            LEFT JOIN (SELECT name, valuen FROM tbsysset WHERE type = 'BZ') b ON a.bzlx = b.name
            WHERE a.cp = @cp AND a.nno = @nno
            ORDER BY iprc
            """,
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
        return rows.AsList();
    }

    // ---------- 私有 ----------

    private async Task<string> GetFpcwCoreAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE name = N'废品仓位标识' AND type = 'system'",
            cancellationToken: ct)) is { } v && v.Length > 0 ? v : "废";
    }

    private async Task<IEnumerable<(string paraid, decimal nvalue)>> GetParamsCoreAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string paraid, decimal nvalue)>(new CommandDefinition(
            "SELECT RTRIM(paraid) paraid, nvalue FROM tborexppara WITH (NOLOCK) WHERE paraid IN ('P07', 'P08')",
            cancellationToken: ct));
        return rows;
    }

    private async Task<string> GetCurrentGynameAsync(CancellationToken ct) => "";

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
