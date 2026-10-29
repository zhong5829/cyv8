using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Parts;

public interface IPartRepository
{
    /// <summary>配件库存三条件搜索（本部门）：编码/名称/车型，可空 AND 组合。
    /// brand：厂牌过滤（tbisto.cp = 厂牌，AppSession.Brand；空 = 不过滤）。
    /// fillLastIprj：是否回填最近进价（远程链路优化 2026-10-29：该列默认隐藏，隐藏时省一次 tbistoed 查询+往返；列可见后调 <see cref="FillLastIprjAsync"/> 补取）。
    /// includeAttrOnly：补集属性库行（2026-10-29 bug 修复：旧散件入库选件窗直接查 tbprnoty（fr_isto __.row2 L33），
    /// 属性定义新增配件无需「传递到库存」即可搜到；新系统仅查 tbisto 导致新配件搜不到。true 时 UNION tbprnoty
    /// 中本部门无库存行的配件（kcamount=0、iprj/oprc 取属性库价，id=0 无库存行语义）。</summary>
    Task<PagedResult<PartItem>> SearchAsync(string dpm, string? cp, string? na1, string? ty, PagedRequest request,
        bool fillLastIprj = true, string? brand = null, bool includeAttrOnly = false, CancellationToken ct = default);

    /// <summary>跨厂牌命中统计（2026-10-29 需求）：同 dpm+三条件、cp &lt;&gt; brand 的各厂牌行数。
    /// 仅主查询为空且带条件时调用（回退提示"是否加载门市编码"）。返回 (厂牌, 行数) 列表。</summary>
    Task<IReadOnlyList<(string Cp, int Count)>> CountOtherBrandsAsync(string dpm, string brand,
        string? cp, string? na1, string? ty, CancellationToken ct = default);

    /// <summary>对已有行集批量回填最近进价（开单页「最近进价」列切换为可见时按需补取）。</summary>
    Task FillLastIprjAsync(IReadOnlyList<PartItem> items, string dpm, CancellationToken ct = default);

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

    /// <summary>搜索行 DTO（列名与 tbisto 对齐由 Dapper 大小写不敏感映射；_total=窗口函数 COUNT(1) OVER() 总行数）。</summary>
    private sealed class PartSearchRow
    {
        public int id { get; set; }
        public string dpm { get; set; } = "";
        public string cp { get; set; } = "";
        public string nno { get; set; } = "";
        public string na1 { get; set; } = "";
        public string ty { get; set; } = "";
        public string fa { get; set; } = "";
        public string dc { get; set; } = "";
        public string unit { get; set; } = "";
        public decimal kcamount { get; set; }
        public decimal iprj { get; set; }
        public decimal iprc { get; set; }
        public decimal oprc { get; set; }
        public decimal oprb { get; set; }
        public string posi { get; set; } = "";
        public string depotno { get; set; } = "";
        public string note { get; set; } = "";
        public int _total { get; set; }

        public PartItem ToItem() => new()
        {
            Id = id, Dpm = dpm, Cp = cp, Nno = nno, Na1 = na1, Ty = ty, Fa = fa, Dc = dc, Unit = unit,
            Kcamount = kcamount, Iprj = iprj, Iprc = iprc, Oprc = oprc, Oprb = oprb,
            Posi = posi, Depotno = depotno, Note = note,
        };
    }

    public async Task<PagedResult<PartItem>> SearchAsync(string dpm, string? cp, string? na1, string? ty, PagedRequest request,
        bool fillLastIprj = true, string? brand = null, bool includeAttrOnly = false, CancellationToken ct = default)
    {
        var cpKw = (cp ?? "").Trim();
        var na1Kw = (na1 ?? "").Trim();
        var tyKw = (ty ?? "").Trim();
        var brandKw = (brand ?? "").Trim();
        var hasAny = cpKw.Length > 0 || na1Kw.Length > 0 || tyKw.Length > 0;

        var conditions = new List<string> { "dpm = @dpm" };
        // 厂牌过滤（2026-10-29 需求）：登录选择的厂牌（中驭/门市）初始只显示该厂牌编码；空 = 未选厂牌不过滤
        if (brandKw.Length > 0)
            conditions.Add("t.cp = @brand");
        if (cpKw.Length > 0)
        {
            // 编码框：查内部编号（包含匹配）
            conditions.Add("nno LIKE '%' + @cp + '%'");
        }
        if (na1Kw.Length > 0)
        {
            // 名称框：名称/编码/车型包含 + tbname0 拼音码（code→name）带出（旧系统 DM() 取码机制，fr_sale row211 建码）
            // 拼音码包含匹配（2026-10-29 需求：中文可部分检索，拼音同样部分检索）；tbname0 数千行全扫代价可忽略
            conditions.Add($"""
                (t.na1 LIKE '%' + @na1 + '%'
                 OR t.nno LIKE '%' + @na1 + '%'
                 OR t.ty LIKE '%' + @na1 + '%'
                 OR t.na1 IN (SELECT RTRIM(name) FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%'))
                """);
        }
        if (tyKw.Length > 0)
        {
            conditions.Add("(ty LIKE '%' + @ty + '%' OR na1 LIKE '%' + @ty + '%')");
        }
        var where = "WHERE " + string.Join(" AND ", conditions);

        // 名称条件带拼音码 IN 子查询，优化器可能误选嵌套循环导致逐行重扫 tbname0（实测 10s+），强制 HASH JOIN 后 85ms
        var hint = na1Kw.Length > 0 ? " OPTION (HASH JOIN)" : "";

        // 属性库补集条件（includeAttrOnly）：与 tbisto 侧同构（p. 别名），另带厂牌过滤 + 本部门无库存行排除；
        // tbprnoty 无 dpm 列（本地库实测），旧散件入库选件也仅 cp=?P_cp（fr_isto __.row2 L23）
        string attrBranch = "";
        if (includeAttrOnly)
        {
            var attrConditions = new List<string>();
            if (brandKw.Length > 0)
                attrConditions.Add("rtrim(p.cp) = @brand");
            if (cpKw.Length > 0)
                attrConditions.Add("rtrim(p.nno) LIKE '%' + @cp + '%'");
            if (na1Kw.Length > 0)
            {
                attrConditions.Add($"""
                    (rtrim(p.na1) LIKE '%' + @na1 + '%'
                     OR rtrim(p.nno) LIKE '%' + @na1 + '%'
                     OR rtrim(p.ty) LIKE '%' + @na1 + '%'
                     OR rtrim(p.na1) IN (SELECT RTRIM(name) FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%'))
                    """);
            }
            if (tyKw.Length > 0)
                attrConditions.Add("(rtrim(p.ty) LIKE '%' + @ty + '%' OR rtrim(p.na1) LIKE '%' + @ty + '%')");
            attrConditions.Add("""
                NOT EXISTS (SELECT 1 FROM tbisto x WITH (NOLOCK)
                            WHERE x.dpm = @dpm AND rtrim(x.cp) = rtrim(p.cp) AND rtrim(x.nno) = rtrim(p.nno))
                """);
            attrBranch = $"""

                UNION ALL
                SELECT 0 AS id, '' AS dpm, rtrim(p.cp) AS cp, rtrim(p.nno) AS nno, rtrim(p.na1) AS na1,
                       rtrim(p.ty) AS ty, rtrim(p.fa) AS fa, rtrim(p.dc) AS dc, rtrim(p.unit) AS unit,
                       0 AS kcamount, ISNULL(p.iprj,0) AS iprj, 0 AS iprc, ISNULL(p.oprc_cp,0) AS oprc, 0 AS oprb,
                       '' AS posi, '' AS depotno, rtrim(p.note) AS note
                FROM tbprnoty p WITH (NOLOCK)
                WHERE {string.Join(" AND ", attrConditions)}
                """;
        }

        var args = new
        {
            dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
            brand = brandKw.Length > 0 ? new DbString { Value = brandKw, IsAnsi = false, Length = 20 } : null,
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

        // 扫描优化（2026-10-29 日志定位：LIKE '%..%' 全扫 tbisto 2.5 万行是搜索 1 秒级主因，与返回行数无关；
        // 备用库弱服务器单次扫描 ~550ms）——两个 SQL 骨架按是否带条件选择（远程 A/B 实测）：
        //   有条件 → qp11 子查询代理骨架（F:\qp11 PartRepository.GetLabelItemsAsync 同构）：内层窄列(id+_total)筛+排序+分页，
        //            外层按 id 回取宽列——谓词只碰窄行，实测 777→460ms / 1015→440ms；
        //   无条件 → 窗口函数直查（骨架 JOIN 反增开销，实测 442ms vs 195ms）。
        // _total=COUNT(1) OVER() 在过滤后的同一行集上计数，tbisto 只扫一遍，总数与分页+COUNT 完全一致
        var hasCondition = cpKw.Length > 0 || na1Kw.Length > 0 || tyKw.Length > 0;
        var orderX = order.Replace("t.", "x.");   // 骨架外层 t=派生表（仅 id/_total），排序须引用 x（回取的 tbisto 宽行）
        var orderU = order.Replace("t.", "u.");   // 属性库补集骨架外层 u=UNION 派生表（tbisto ∪ tbprnoty-only）
        var pageSql = attrBranch.Length > 0
            ? $"""
               SELECT u.*, COUNT(1) OVER() AS _total
               FROM (SELECT {SelectColumns}
                     FROM tbisto t WITH (NOLOCK)
                     {where}{attrBranch}
                    ) u
               ORDER BY {orderU}
               OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY{hint}
               """
            : hasCondition
            ? $"""
               SELECT x.id, x.dpm, x.cp, x.nno, x.na1, x.ty, x.fa, x.dc, x.unit, x.kcamount, x.iprj, x.iprc, x.oprc, x.oprb, x.posi, x.depotno, x.note, t._total
               FROM (SELECT t.id, COUNT(1) OVER() AS _total
                     FROM tbisto t WITH (NOLOCK)
                     {where}
                     ORDER BY {order}, t.id
                     OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY) t
               INNER JOIN tbisto x WITH (NOLOCK) ON x.id = t.id
               ORDER BY {orderX}{hint}
               """
            : $"""
               SELECT {SelectColumns}, COUNT(1) OVER() AS _total
               FROM tbisto t WITH (NOLOCK)
               {where}
               ORDER BY {order}
               OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY{hint}
               """;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = (await conn.QueryAsync<PartSearchRow>(new CommandDefinition(pageSql, args, cancellationToken: ct))).AsList();
        var items = rows.Select(r => r.ToItem()).ToList();
        var total = rows.Count > 0 ? rows[0]._total : 0;
        QPGL.Infrastructure.DebugLog.Log($"[PartSearch] {(attrBranch.Length > 0 ? "补集" : hasCondition ? "骨架" : "直查")}={sw.ElapsedMilliseconds}ms rows={items.Count} total={total} dpm={dpm} cp='{cpKw}' na1='{na1Kw}' ty='{tyKw}'");
        // 最近一次进价批量回填（2026-10-28 需求：开单页隐藏列）：与本页零件号 IN 一次取
        //   每号最近一条入库行 iprj，口径与进货历史一致（type IN 入库/历史、jkamount>0、indate DESC,id DESC）
        //   远程优化：fillLastIprj=false 时跳过（列默认隐藏，隐藏时白跑一查+一往返）
        if (fillLastIprj && items.Count > 0)
        {
            var swFill = System.Diagnostics.Stopwatch.StartNew();
            await FillLastIprjCoreAsync(conn, items, dpm, ct);
            QPGL.Infrastructure.DebugLog.Log($"[PartSearch] fillLastIprj={swFill.ElapsedMilliseconds}ms");
        }

        return new PagedResult<PartItem>
        {
            Items = items,
            Total = total,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(string Cp, int Count)>> CountOtherBrandsAsync(string dpm, string brand,
        string? cp, string? na1, string? ty, CancellationToken ct = default)
    {
        var cpKw = (cp ?? "").Trim();
        var na1Kw = (na1 ?? "").Trim();
        var tyKw = (ty ?? "").Trim();
        var brandKw = (brand ?? "").Trim();
        if (brandKw.Length == 0) return [];

        // 条件与 SearchAsync 完全同构，仅厂牌条件换为 cp <> @brand
        var conditions = new List<string> { "dpm = @dpm", "t.cp <> @brand" };
        if (cpKw.Length > 0)
            conditions.Add("nno LIKE '%' + @cp + '%'");
        if (na1Kw.Length > 0)
            conditions.Add($"""
                (t.na1 LIKE '%' + @na1 + '%'
                 OR t.nno LIKE '%' + @na1 + '%'
                 OR t.ty LIKE '%' + @na1 + '%'
                 OR t.na1 IN (SELECT RTRIM(name) FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%'))
                """);
        if (tyKw.Length > 0)
            conditions.Add("(ty LIKE '%' + @ty + '%' OR na1 LIKE '%' + @ty + '%')");
        var hint = na1Kw.Length > 0 ? " OPTION (HASH JOIN)" : "";

        // 属性库补集同计入跨厂牌命中（2026-10-29：属性定义新增配件无任何库存行，仅统计 tbisto 会漏报），
        // 条件与 SearchAsync 的 attrBranch 同构，厂牌条件换为 rtrim(p.cp) <> @brand
        var attrConditions = new List<string> { "rtrim(p.cp) <> @brand" };
        if (cpKw.Length > 0)
            attrConditions.Add("rtrim(p.nno) LIKE '%' + @cp + '%'");
        if (na1Kw.Length > 0)
        {
            attrConditions.Add($"""
                (rtrim(p.na1) LIKE '%' + @na1 + '%'
                 OR rtrim(p.nno) LIKE '%' + @na1 + '%'
                 OR rtrim(p.ty) LIKE '%' + @na1 + '%'
                 OR rtrim(p.na1) IN (SELECT RTRIM(name) FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%'))
                """);
        }
        if (tyKw.Length > 0)
            attrConditions.Add("(rtrim(p.ty) LIKE '%' + @ty + '%' OR rtrim(p.na1) LIKE '%' + @ty + '%')");
        attrConditions.Add("""
            NOT EXISTS (SELECT 1 FROM tbisto x WITH (NOLOCK)
                        WHERE x.dpm = @dpm AND rtrim(x.cp) = rtrim(p.cp) AND rtrim(x.nno) = rtrim(p.nno))
            """);

        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string cp, int n)>(new CommandDefinition(
            $"""
            SELECT z.cp, SUM(z.n) AS n
            FROM (
                SELECT RTRIM(t.cp) AS cp, COUNT(1) AS n
                FROM tbisto t WITH (NOLOCK)
                WHERE {string.Join(" AND ", conditions)}
                GROUP BY RTRIM(t.cp)
                UNION ALL
                SELECT rtrim(p.cp) AS cp, COUNT(1) AS n
                FROM tbprnoty p WITH (NOLOCK)
                WHERE {string.Join(" AND ", attrConditions)}
                GROUP BY rtrim(p.cp)
            ) z
            GROUP BY z.cp{hint}
            """,
            new
            {
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                brand = new DbString { Value = brandKw, IsAnsi = false, Length = 20 },
                cp = new DbString { Value = cpKw, IsAnsi = true, Length = 40 },
                na1 = new DbString { Value = na1Kw, IsAnsi = false, Length = 40 },
                ty = new DbString { Value = tyKw, IsAnsi = false, Length = 40 },
            }, cancellationToken: ct));
        return rows.Select(r => (r.cp, r.n)).ToList();
    }

    /// <inheritdoc/>
    public async Task FillLastIprjAsync(IReadOnlyList<PartItem> items, string dpm, CancellationToken ct = default)
    {
        if (items.Count == 0) return;
        await using var conn = await factory.OpenAsync(ct);
        await FillLastIprjCoreAsync(conn, items, dpm, ct);
    }

    /// <summary>最近进价回填核心（nno IN 本页零件号，每号取最近一条入库行 iprj）。</summary>
    private static async Task FillLastIprjCoreAsync(System.Data.Common.DbConnection conn, IReadOnlyList<PartItem> items, string dpm, CancellationToken ct)
    {
        var nnos = items.Select(p => p.Nno.Trim()).Where(n => n.Length > 0).Distinct().ToList();
        if (nnos.Count == 0) return;
        {
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
    }

    /// <summary>
    /// 历史售价：tbsada type IN ('销售','历史') 全部出库行按时间倒序（NOLOCK，显式列，点查 nno+dpm 走索引）。
    /// type 两态都取：日营业统计会把 '销售' 翻转为 '历史'（旧 VFP fr_sale 同款口径），只查 '销售' 会漏掉全部已日结历史。
    /// 不限符号（2026-10-29 需求：退货负数行也显示——数量为负即退货记录）。
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
                   RTRIM(ISNULL(depotno, '')) AS Depotno,
                   ckamount AS Qty, oprc AS Price
            FROM tbsada WITH (NOLOCK)
            WHERE nno = @nno AND dpm = @dpm AND type IN (N'销售', N'历史'){kwFilter}
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
    /// 进货历史：tbistoed type IN ('入库','历史') 按入库日期倒序（NOLOCK，点查 nno+dpm）。
    /// 与日统计口径一致（统计会把 '入库' 翻转为 '历史'），故两态都取。
    /// 不过滤 jkamount 正负：进货退货是同类型的负数行（旧 fr_order4 进货历史网格无数量过滤，退货一并显示）。
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
            WHERE nno = @nno AND dpm = @dpm AND type IN (N'入库', N'历史'){kwFilter}
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
