using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Parts;

public interface IPartRepository
{
    /// <summary>配件库存三条件搜索（本部门）：编码/名称/车型，可空 AND 组合。</summary>
    Task<PagedResult<PartItem>> SearchAsync(string dpm, string? cp, string? na1, string? ty, PagedRequest request, CancellationToken ct = default);

    /// <summary>配件历史售价（开单定价参考，qp11 SellEditDialog 右侧销售历史；keyword 按客户名称/拼音码筛选）。</summary>
    Task<IReadOnlyList<PartSaleHistory>> GetSaleHistoryAsync(string nno, string dpm, int top, string? keyword = null, CancellationToken ct = default);

    /// <summary>配件历史进价（进货历史：tbistoed type IN 入库/历史 正数行，按入库日期倒序；top&lt;=0 全部；keyword 按供应商名称/拼音码筛选）。</summary>
    Task<IReadOnlyList<PartPurchaseHistory>> GetPurchaseHistoryAsync(string nno, string dpm, int top, string? keyword = null, CancellationToken ct = default);
}

/// <summary>
/// 配件仓储（tbisto = 主档+库存批次，结构零改动）。
/// 三条件对齐旧系统开单搜索（fr_sale row321/row384 均为 LIKE 包含）：
///   - cp（编码，含内部编号）前缀匹配（编码习惯从头输，SARGable §10.2-4）
///   - na1（名称）/ty（车型/图号）包含匹配——名称常为中间词（"后减震器"搜"减震"）；
///     tbisto 为批次表（万级行）包含匹配实测可控（雅阁 207 行 &lt;50ms），
///     若未来量级增长按 §10.5 升级内存索引（前缀树+倒排）。
/// 性能（§10.2）：显式列 + 分页 + dpm 必带 + NOLOCK（旧 P_WITHNOLOCK 语义）。
/// </summary>
public sealed class PartRepository(SqlConnectionFactory factory) : IPartRepository
{
    private const string SelectColumns =
        "id, dpm, cp, nno, na1, ty, fa, dc, unit, kcamount, iprj, iprc, oprc, oprb, posi, depotno, note";

    public async Task<PagedResult<PartItem>> SearchAsync(string dpm, string? cp, string? na1, string? ty, PagedRequest request, CancellationToken ct = default)
    {
        var cpKw = (cp ?? "").Trim();
        var na1Kw = (na1 ?? "").Trim();
        var tyKw = (ty ?? "").Trim();
        var hasAny = cpKw.Length > 0 || na1Kw.Length > 0 || tyKw.Length > 0;

        var conditions = new List<string> { "dpm = @dpm" };
        if (cpKw.Length > 0)
        {
            // 编码框：查内部编号（包含匹配）
            conditions.Add("nno LIKE '%' + @cp + '%'");
        }
        if (na1Kw.Length > 0)
        {
            // 名称框：名称/编码/车型包含 + tbname0 拼音码（code→name）带出（旧系统 DM() 取码机制，fr_sale row211 建码）
            // 拼音码走预聚合 JOIN（先按 code 前缀筛 tbname0），避免逐行 EXISTS 全表扫描
            conditions.Add($"""
                (t.na1 LIKE '%' + @na1 + '%'
                 OR t.nno LIKE '%' + @na1 + '%'
                 OR t.ty LIKE '%' + @na1 + '%'
                 OR t.na1 IN (SELECT RTRIM(name) FROM tbname0 WITH (NOLOCK) WHERE code LIKE @na1 + '%'))
                """);
        }
        if (tyKw.Length > 0)
        {
            conditions.Add("(ty LIKE '%' + @ty + '%' OR na1 LIKE '%' + @ty + '%')");
        }
        var where = "WHERE " + string.Join(" AND ", conditions);

        // 名称条件带拼音码 IN 子查询，优化器可能误选嵌套循环导致逐行重扫 tbname0（实测 10s+），强制 HASH JOIN 后 85ms
        var hint = na1Kw.Length > 0 ? " OPTION (HASH JOIN)" : "";

        var args = new
        {
            dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
            cp = hasAny ? new DbString { Value = cpKw, IsAnsi = true, Length = 40 } : null,
            na1 = na1Kw.Length > 0 ? new DbString { Value = na1Kw, IsAnsi = false, Length = 40 } : null,
            ty = tyKw.Length > 0 ? new DbString { Value = tyKw, IsAnsi = false, Length = 40 } : null,
            offset = (request.Page - 1) * request.PageSize,
            pageSize = request.PageSize,
        };

        // 排序白名单：编号/名称（§6.5）
        var order = request.Sort?.Trim().ToLowerInvariant() switch
        {
            "cp" => "t.cp",
            "na1" => "t.na1",
            _ => "t.cp",
        };
        order += request.IsDescending ? " DESC" : " ASC";

        await using var conn = await factory.OpenAsync(ct);

        var pageSql =
            $"""
            SELECT {SelectColumns}
            FROM tbisto t WITH (NOLOCK)
            {where}
            ORDER BY {order}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY{hint}
            """;
        var countSql = $"SELECT COUNT(1) FROM tbisto t WITH (NOLOCK) {where}{hint}";

        var items = (await conn.QueryAsync<PartItem>(new CommandDefinition(pageSql, args, cancellationToken: ct))).AsList();
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, args, cancellationToken: ct));

        // 最近一次进价批量回填（2026-10-28 需求：开单页隐藏列）：与本页零件号 IN 一次取
        //   每号最近一条入库行 iprj，口径与进货历史一致（type IN 入库/历史、jkamount>0、indate DESC,id DESC）
        if (items.Count > 0)
        {
            var nnos = items.Select(p => p.Nno.Trim()).Where(n => n.Length > 0).Distinct().ToList();
            var priceArgs = new
            {
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                nnos = nnos.Select(n => new DbString { Value = n, IsAnsi = true, Length = 18 }).ToList(),
            };
            var prices = await conn.QueryAsync<(string nno, decimal iprj)>(new CommandDefinition(
                """
                SELECT RTRIM(nno) AS nno, iprj
                FROM (
                    SELECT nno, iprj, ROW_NUMBER() OVER (PARTITION BY RTRIM(nno) ORDER BY indate DESC, id DESC) AS rn
                    FROM tbistoed WITH (NOLOCK)
                    WHERE dpm = @dpm AND nno IN @nnos AND type IN (N'入库', N'历史') AND jkamount > 0
                ) x
                WHERE rn = 1
                """, priceArgs, cancellationToken: ct));
            var map = prices.ToDictionary(r => r.nno.Trim(), r => r.iprj, StringComparer.OrdinalIgnoreCase);
            foreach (var p in items)
                p.LastIprj = map.GetValueOrDefault(p.Nno.Trim());
        }

        return new PagedResult<PartItem>
        {
            Items = items,
            Total = total,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    /// <summary>
    /// 历史售价：tbsada type='销售' 正数出库行按时间倒序（NOLOCK，显式列，点查 nno+dpm 走索引）。
    /// 旧出处：qp11 SellEditDialog 右侧销售历史；旧 VFP 等价查询见 fr_sale 配件历史。
    /// top &lt;= 0 时不限条数（返回全部记录）。
    /// </summary>
    public async Task<IReadOnlyList<PartSaleHistory>> GetSaleHistoryAsync(string nno, string dpm, int top, string? keyword = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var kw = (keyword ?? "").Trim();
        var topClause = top > 0 ? "TOP (@top) " : "";
        // keyword：客户名称包含 / 拼音码前缀（旧 cmm_class row326：tbgu 自带 code 列存拼音码，非 tbname0）
        var kwFilter = kw.Length > 0
            ? """
              AND (gname LIKE N'%' + @kw + '%'
                   OR gname IN (SELECT RTRIM(gname) FROM tbgu WITH (NOLOCK) WHERE code LIKE @kw + '%'))
              """
            : "";
        var sql = $"""
            SELECT {topClause}
                   RTRIM(paper) AS Paper, outdate AS OutDate, RTRIM(gname) AS Gname,
                   ckamount AS Qty, oprc AS Price
            FROM tbsada WITH (NOLOCK)
            WHERE nno = @nno AND dpm = @dpm AND type = N'销售' AND ckamount > 0{kwFilter}
            ORDER BY outdate DESC, id DESC
            """;
        var rows = await conn.QueryAsync<PartSaleHistory>(new CommandDefinition(
            sql,
            new
            {
                top,
                kw = new DbString { Value = kw, IsAnsi = false, Length = 40 },
                nno = new DbString { Value = nno.Trim(), IsAnsi = true, Length = 18 },
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
            },
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// 进货历史：tbistoed type IN ('入库','历史') 正数入库行按入库日期倒序（NOLOCK，点查 nno+dpm）。
    /// 与日统计口径一致（统计会把 '入库' 翻转为 '历史'），故两态都取。
    /// top &lt;= 0 时不限条数。
    /// </summary>
    public async Task<IReadOnlyList<PartPurchaseHistory>> GetPurchaseHistoryAsync(string nno, string dpm, int top, string? keyword = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var kw = (keyword ?? "").Trim();
        var topClause = top > 0 ? "TOP (@top) " : "";
        // keyword：供应商名称包含 / 拼音码前缀（旧 cmm_class row326 P_GYSUSEKH：生产库供应商走 tbgu（KH 编号），
        // tbgugys 仅少量；台账 gyno 两者皆有，拼音码同时关联 tbgu.code 与 tbgugys.code）
        var kwFilter = kw.Length > 0
            ? """
              AND (ISNULL(gyname, '') LIKE N'%' + @kw + '%'
                   OR (ISNULL(gyno, '') <> '' AND gyno IN (
                       SELECT RTRIM(gno) FROM tbgu WITH (NOLOCK) WHERE code LIKE @kw + '%'
                       UNION
                       SELECT RTRIM(gyno) FROM tbgugys WITH (NOLOCK) WHERE code LIKE @kw + '%')))
              """
            : "";
        var sql = $"""
            SELECT {topClause}
                   RTRIM(cno) AS Cno, indate AS InDate, RTRIM(ISNULL(gyname, '')) AS Gyname,
                   jkamount AS Qty, iprj AS Iprj, iprc AS Iprc
            FROM tbistoed WITH (NOLOCK)
            WHERE nno = @nno AND dpm = @dpm AND type IN (N'入库', N'历史') AND jkamount > 0{kwFilter}
            ORDER BY indate DESC, id DESC
            """;
        var rows = await conn.QueryAsync<PartPurchaseHistory>(new CommandDefinition(
            sql,
            new
            {
                top,
                kw = new DbString { Value = kw, IsAnsi = false, Length = 40 },
                nno = new DbString { Value = nno.Trim(), IsAnsi = true, Length = 18 },
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
            },
            cancellationToken: ct));
        return rows.AsList();
    }
}
