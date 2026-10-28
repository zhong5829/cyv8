using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Stock;

public interface IDailyStocktakeRepository
{
    /// <summary>每日动态盘点查询（重建契约，只读）。</summary>
    Task<IReadOnlyList<DailyStocktakeRow>> QueryAsync(DailyStocktakeQuery query, string dpm, CancellationToken ct = default);
}

/// <summary>查询条件（旧表单：日期窗 TXT1/TXT2 默认 昨天~今天、厂牌 当前/全部、仓位/库号、fr_find 通用条件）。</summary>
public sealed class DailyStocktakeQuery
{
    public DateTime DateFrom { get; set; } = DateTime.Today.AddDays(-1);
    public DateTime DateTo { get; set; } = DateTime.Today;
    /// <summary>null=全部厂牌；非空=指定厂牌（旧「当前厂牌」；新系统厂牌会话未接入，由 UI 显式传值）。</summary>
    public string? Cp { get; set; }
    public string Depotno { get; set; } = "";
    public string Posi { get; set; } = "";
    public string NnoPrefix { get; set; } = "";
    public string Na1Prefix { get; set; } = "";
}

/// <summary>动态盘点行（旧 c_isto 网格 15 列，重建契约）。</summary>
public sealed class DailyStocktakeRow
{
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>库存数（当前批次账面）。</summary>
    public decimal Kcamount { get; set; }
    public string Posi { get; set; } = "";
    /// <summary>窗口内入库数（tbistoed type IN 入库/历史 正数）。</summary>
    public decimal JinChang { get; set; }
    /// <summary>窗口内出库数（tbsada 销售/历史/调拨/预售/内耗，按 kcid 关联本批次）。</summary>
    public decimal ChuCang { get; set; }
    public string Fa { get; set; } = "";
    /// <summary>批次留痕（tbistoch 拼接）。</summary>
    public string KcInfo { get; set; } = "";
    public string Depotno { get; set; } = "";
    public string Cp { get; set; } = "";
    /// <summary>散件入库草稿未生效数（tbistosm）。</summary>
    public decimal Unconfirmed { get; set; }
    /// <summary>急件入库未冲减数（tbistoed cp='急件'，按件汇总——重建口径最弱项，UAT 校准）。</summary>
    public decimal UrgentUnoffset { get; set; }
}

/// <summary>
/// 每日动态盘点仓储（菜单 B.C；旧菜单权限码复用 BB——旧系统复制粘贴痕迹，新系统按 BB 守卫一致）。
/// ⚠️ 重建契约：容器转储缺查询填充 SQL（开发文档/契约摘录/每日动态盘点fr_dtpd契约_重建.md）。
/// 只读无写路径。UAT 以旧系统同窗口实跑校准。
/// </summary>
public sealed class DailyStocktakeRepository(SqlConnectionFactory factory) : IDailyStocktakeRepository
{
    public async Task<IReadOnlyList<DailyStocktakeRow>> QueryAsync(DailyStocktakeQuery query, string dpm, CancellationToken ct = default)
    {
        var from = query.DateFrom.Date;
        var to = query.DateTo.Date.AddDays(1);

        var where = "b.dpm = @dpm";
        var args = new DynamicParameters(new
        {
            dpm = new DbString { Value = dpm.Trim(), IsAnsi = true, Length = 10 },
            from,
            to,
            urgentCp = new DbString { Value = "急件", IsAnsi = true, Length = 10 },
        });
        if (!string.IsNullOrWhiteSpace(query.Cp))
        {
            where += " AND b.cp = @cp";
            args.Add("cp", new DbString { Value = query.Cp.Trim(), IsAnsi = true, Length = 10 });
        }
        if (!string.IsNullOrWhiteSpace(query.Depotno))
        {
            where += " AND b.depotno = @depotno";
            args.Add("depotno", new DbString { Value = query.Depotno.Trim(), IsAnsi = true, Length = 10 });
        }
        if (!string.IsNullOrWhiteSpace(query.Posi))
        {
            where += " AND b.posi = @posi";
            args.Add("posi", new DbString { Value = query.Posi.Trim(), IsAnsi = true, Length = 10 });
        }
        if (!string.IsNullOrWhiteSpace(query.NnoPrefix))
        {
            where += " AND b.nno LIKE @nno";
            args.Add("nno", new DbString { Value = query.NnoPrefix.Trim() + "%", IsAnsi = true, Length = 19 });
        }
        if (!string.IsNullOrWhiteSpace(query.Na1Prefix))
        {
            where += " AND b.na1 LIKE @na1";
            args.Add("na1", new DbString { Value = query.Na1Prefix.Trim() + "%", IsAnsi = true, Length = 41 });
        }

        var sql = $"""
            SELECT RTRIM(b.nno) nno, RTRIM(b.na1) na1, RTRIM(b.dc) dc, RTRIM(b.ty) ty, RTRIM(b.unit) unit,
                   b.kcamount, RTRIM(b.posi) posi, b.fa, RTRIM(b.depotno) depotno, RTRIM(b.cp) cp,
                   ISNULL(i.jin, 0) AS jinChang, ISNULL(o.chu, 0) AS chuCang, ISNULL(u.draft, 0) AS unconfirmed,
                   ISNULL(ug.urgent, 0) AS urgentUnoffset,
                   ISNULL(STUFF((SELECT '；' + CONVERT(char(10), x.xgdate, 120) + ' ' + RTRIM(x.note)
                                 FROM tbistoch x WITH (NOLOCK)
                                 WHERE x.cp = b.cp AND x.nno = b.nno AND x.posi = b.posi
                                   AND x.depotno = b.depotno AND x.dpm = b.dpm
                                 ORDER BY x.id DESC
                                 FOR XML PATH('')), 1, 1, ''), '') AS kcInfo
            FROM tbisto b WITH (NOLOCK)
            LEFT JOIN (SELECT cp, nno, posi, depotno, dpm, SUM(jkamount) jin
                       FROM tbistoed WITH (NOLOCK)
                       WHERE type IN (N'入库', N'历史') AND jkamount > 0 AND indate >= @from AND indate < @to
                       GROUP BY cp, nno, posi, depotno, dpm) i
                   ON i.cp = b.cp AND i.nno = b.nno AND i.posi = b.posi AND i.depotno = b.depotno AND i.dpm = b.dpm
            LEFT JOIN (SELECT kcid, SUM(ckamount) chu
                       FROM tbsada WITH (NOLOCK)
                       WHERE type IN (N'销售', N'历史', N'调拨', N'预售', N'内耗')
                         AND outdate >= @from AND outdate < @to
                       GROUP BY kcid) o
                   ON o.kcid = b.id
            LEFT JOIN (SELECT cp, nno, posi, depotno, dpm, SUM(jkamount) draft
                       FROM tbistosm WITH (NOLOCK)
                       GROUP BY cp, nno, posi, depotno, dpm) u
                   ON u.cp = b.cp AND u.nno = b.nno AND u.posi = b.posi AND u.depotno = b.depotno AND u.dpm = b.dpm
            LEFT JOIN (SELECT nno, dpm, SUM(jkamount) urgent
                       FROM tbistoed WITH (NOLOCK)
                       WHERE cp = @urgentCp AND type = N'入库' AND jkamount > 0 AND indate >= @from AND indate < @to
                       GROUP BY nno, dpm) ug
                   ON ug.nno = b.nno AND ug.dpm = b.dpm
            WHERE {where}
            ORDER BY b.nno, b.posi
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<DailyStocktakeRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }
}
