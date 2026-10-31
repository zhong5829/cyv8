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
        string? note = null, bool fillLastIprj = true, string? brand = null, bool includeAttrOnly = false, CancellationToken ct = default);

    /// <summary>多编码查询（2026-10-31 需求）：按内部编号一次性查回多个配件（一行一个编号）。
    /// 默认精准匹配（RTRIM(nno)=编号），Contains=true 时包含匹配（nno LIKE %编号%）。
    /// 开单页多条件查询按钮使用；不限厂牌（用户确认口径）。</summary>
    Task<IReadOnlyList<PartItem>> SearchByNnosAsync(string dpm, IReadOnlyList<(string Code, bool Contains)> codes,
        CancellationToken ct = default);

    /// <summary>跨厂牌命中统计（2026-10-29 需求）：同 dpm+三条件、cp &lt;&gt; brand 的各厂牌行数。
    /// 仅主查询为空且带条件时调用（回退提示"是否加载门市编码"）。返回 (厂牌, 行数) 列表。
    /// note：备注包含匹配（2026-10-30 需求：开单页查询区新增备注框，与 SearchAsync 条件口径一致）。</summary>
    Task<IReadOnlyList<(string Cp, int Count)>> CountOtherBrandsAsync(string dpm, string brand,
        string? cp, string? na1, string? ty, CancellationToken ct = default, string? note = null);

    /// <summary>对已有行集批量回填最近进价（开单页「最近进价」列切换为可见时按需补取）。</summary>
    Task FillLastIprjAsync(IReadOnlyList<PartItem> items, string dpm, CancellationToken ct = default);

    /// <summary>配件历史售价（开单定价参考，qp11 SellEditDialog 右侧销售历史；keyword 按客户名称/拼音码筛选）。</summary>
    Task<IReadOnlyList<PartSaleHistory>> GetSaleHistoryAsync(string nno, string dpm, int top, string? keyword = null, CancellationToken ct = default);

    /// <summary>配件历史进价（进货历史：tbistoed type IN 入库/历史 正数行，按入库日期倒序；top&lt;=0 全部；keyword 按供应商名称/拼音码筛选）。</summary>
    Task<IReadOnlyList<PartPurchaseHistory>> GetPurchaseHistoryAsync(string nno, string dpm, int top, string? keyword = null, CancellationToken ct = default);

    /// <summary>按批次 id 批量点查当前库存（开单页轻量库存刷新：只刷库存列，不重查整表；2026-10-30 需求）。</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetStockByIdsAsync(IReadOnlyCollection<int> ids, string dpm, CancellationToken ct = default);

    /// <summary>入库单整单明细（tbistoed 按 cno；2026-10-31 需求：进货历史双击查看。本库 tbistoed 无 gu 列，不可复用收付款路径）。</summary>
    Task<IReadOnlyList<PartPurchaseDocLine>> GetPurchaseDocLinesAsync(string cno, CancellationToken ct = default);

    /// <summary>配件图片（tbImage.imgs base64 按内部编号；2026-10-31 需求：开单页选中配件显示图片。无图返回 null）。</summary>
    Task<byte[]?> GetPartImageAsync(string nno, CancellationToken ct = default);

    /// <summary>标签打印数据源（2026-10-31 需求，qp11 GetLabelItemsAsync 同构）：tbisto 三条件 LIKE 模糊查
    /// （nno/na1/ty，空条件忽略），na1/ty 支持拼音码（tbname0/tbty0 派生表，同 SearchAsync 机制），
    /// brand 非空时按厂牌 cp 精确过滤。按 仓位→内部编号 排序输出。
    /// tbisto 为批次表，同 nno 多行 GROUP BY 合并：库存 SUM(kcamount)、货位取首个非空值。</summary>
    Task<IReadOnlyList<LabelStockItem>> GetLabelItemsAsync(string dpm, string brand,
        string? nno = null, string? na1 = null, string? ty = null, CancellationToken ct = default);
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

    /// <summary>t. 前缀版列清单：拼音码 LEFT JOIN 派生表（nb.name/tb2.ty）在场时消除裸列名歧义（SQL 2515）</summary>
    private const string SelectColumnsT =
        "t.id, t.dpm, t.cp, t.nno, t.na1, t.ty, t.fa, t.dc, t.unit, t.kcamount, t.iprj, t.iprc, t.oprc, t.oprb, t.posi, t.depotno, t.note";

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
        string? note = null, bool fillLastIprj = true, string? brand = null, bool includeAttrOnly = false, CancellationToken ct = default)
    {
        var cpKw = (cp ?? "").Trim();
        var na1Kw = (na1 ?? "").Trim();
        var tyKw = (ty ?? "").Trim();
        var noteKw = (note ?? "").Trim();
        var brandKw = (brand ?? "").Trim();

        // 编码框双模式（2026-10-30 需求，实测 tbisto/tbprnoty nno 均为「前缀+空格+编码」格式如 PT /OPT /GH ）：
        //   首字符 / → 包含匹配 nno LIKE '%kw%'（可命中 PT 与 OPT 等含该片段的编码，原行为）；
        //   否则     → 前缀匹配 nno LIKE 'kw%'（只命中该前缀类，如 pt 只查 PT 开头不混 OPT）。
        // 前缀匹配用裸列（无 RTRIM）可走索引（SARGable，实测 7ms vs 包含 17ms）；/ 单独输入视为无编码条件。
        var cpContains = cpKw.StartsWith("/");
        var cpWord = cpContains ? cpKw[1..].Trim() : cpKw;
        var hasCp = cpWord.Length > 0;
        var hasAny = hasCp || na1Kw.Length > 0 || tyKw.Length > 0 || noteKw.Length > 0;

        var conditions = new List<string> { "dpm = @dpm" };
        // 厂牌过滤（2026-10-29 需求）：登录选择的厂牌（中驭/门市）初始只显示该厂牌编码；空 = 未选厂牌不过滤
        if (brandKw.Length > 0)
            conditions.Add("t.cp = @brand");
        if (hasCp)
        {
            // 编码框：前缀（缺省）/ 包含（首字符 /）
            conditions.Add(cpContains ? "nno LIKE '%' + @cp + '%'" : "nno LIKE @cp + '%'");
        }
        if (noteKw.Length > 0)
        {
            // 备注框：备注包含匹配（2026-10-30 需求：开单页查询区新增备注框）
            conditions.Add("t.note LIKE '%' + @note + '%'");
        }

        // 拼音码匹配统一用 LEFT JOIN 派生表（2026-10-30 bug 修复：名称 IN + 车型 IN 两组半连接经 OR 组合后
        // OPTION (HASH JOIN) 提示无法生成计划（SQL 8622，用户实测"同时查名称和车型报查询失败"）；
        // 改为显式 JOIN 派生表后 HASH JOIN 总能套用——实测双拼音条件 117ms（无提示嵌套循环 18.2s）。
        // 等价性：rtrim(t.na1)=nb.name 相等连接 + DISTINCT 去重不膨胀行数，OR nb.name IS NOT NULL 与 IN 语义一致）
        var na1Join = na1Kw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(name) AS name FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%') nb ON rtrim(t.na1) = nb.name"
            : "";
        var tyJoin = tyKw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS ty FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @ty + '%') tb2 ON rtrim(t.ty) = tb2.ty"
            : "";
        // 属性库补集分支（p 别名）侧的同款派生表（UNION 两分支为独立子查询，别名不冲突）
        var na1JoinP = na1Kw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(name) AS name FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%') nb ON rtrim(p.na1) = nb.name"
            : "";
        var tyJoinP = tyKw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS ty FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @ty + '%') tb2 ON rtrim(p.ty) = tb2.ty"
            : "";
        if (na1Kw.Length > 0)
        {
            // 名称框：名称/编码/车型包含 + tbname0 拼音码（code→name）带出（旧系统 DM() 取码机制，fr_sale row211 建码）
            // 拼音码包含匹配（2026-10-29 需求：中文可部分检索，拼音同样部分检索）
            conditions.Add("""
                (t.na1 LIKE '%' + @na1 + '%'
                 OR t.nno LIKE '%' + @na1 + '%'
                 OR t.ty LIKE '%' + @na1 + '%'
                 OR nb.name IS NOT NULL)
                """);
        }
        if (tyKw.Length > 0)
        {
            // 车型框：车型/名称/备注包含 + tbty0 车型拼音码（2026-10-30 需求：旧系统车型不支持拼音码检索；
            // 2026-10-30 补充：车型框同时查备注——用户习惯把车型写在备注里，中文关键词直接命中备注）
            conditions.Add("""
                (t.ty LIKE '%' + @ty + '%'
                 OR t.na1 LIKE '%' + @ty + '%'
                 OR t.note LIKE '%' + @ty + '%'
                 OR tb2.ty IS NOT NULL)
                """);
        }
        var where = "WHERE " + string.Join(" AND ", conditions);

        // 名称/车型条件带拼音码 JOIN 派生表，优化器可能误选嵌套循环导致逐行重扫（实测 10s+），强制 HASH JOIN 后 ~100ms
        var hint = (na1Kw.Length > 0 || tyKw.Length > 0) ? " OPTION (HASH JOIN)" : "";

        // 属性库补集条件（includeAttrOnly）：与 tbisto 侧同构（p. 别名），另带厂牌过滤 + 本部门无库存行排除；
        // tbprnoty 无 dpm 列（本地库实测），旧散件入库选件也仅 cp=?P_cp（fr_isto __.row2 L23）
        string attrBranch = "";
        if (includeAttrOnly)
        {
            var attrConditions = new List<string>();
            if (brandKw.Length > 0)
                attrConditions.Add("rtrim(p.cp) = @brand");
            if (hasCp)
                attrConditions.Add(cpContains ? "rtrim(p.nno) LIKE '%' + @cp + '%'" : "rtrim(p.nno) LIKE @cp + '%'");
            if (noteKw.Length > 0)
                attrConditions.Add("rtrim(p.note) LIKE '%' + @note + '%'");
            if (na1Kw.Length > 0)
            {
                attrConditions.Add("""
                    (rtrim(p.na1) LIKE '%' + @na1 + '%'
                     OR rtrim(p.nno) LIKE '%' + @na1 + '%'
                     OR rtrim(p.ty) LIKE '%' + @na1 + '%'
                     OR nb.name IS NOT NULL)
                    """);
            }
            if (tyKw.Length > 0)
                attrConditions.Add("""
                    (rtrim(p.ty) LIKE '%' + @ty + '%'
                     OR rtrim(p.na1) LIKE '%' + @ty + '%'
                     OR rtrim(p.note) LIKE '%' + @ty + '%'
                     OR tb2.ty IS NOT NULL)
                    """);
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
                FROM tbprnoty p WITH (NOLOCK){na1JoinP}{tyJoinP}
                WHERE {string.Join(" AND ", attrConditions)}
                """;
        }

        var args = new
        {
            dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
            brand = brandKw.Length > 0 ? new DbString { Value = brandKw, IsAnsi = false, Length = 20 } : null,
            cp = hasCp ? new DbString { Value = cpWord, IsAnsi = true, Length = 40 } : null,
            na1 = na1Kw.Length > 0 ? new DbString { Value = na1Kw, IsAnsi = false, Length = 40 } : null,
            ty = tyKw.Length > 0 ? new DbString { Value = tyKw, IsAnsi = false, Length = 40 } : null,
            note = noteKw.Length > 0 ? new DbString { Value = noteKw, IsAnsi = true, Length = 50 } : null,
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
        var hasCondition = hasCp || na1Kw.Length > 0 || tyKw.Length > 0 || noteKw.Length > 0;
        var orderX = order.Replace("t.", "x.");   // 骨架外层 t=派生表（仅 id/_total），排序须引用 x（回取的 tbisto 宽行）
        var orderU = order.Replace("t.", "u.");   // 属性库补集骨架外层 u=UNION 派生表（tbisto ∪ tbprnoty-only）
        var pageSql = attrBranch.Length > 0
            ? $"""
               SELECT u.*, COUNT(1) OVER() AS _total
               FROM (SELECT {SelectColumnsT}
                     FROM tbisto t WITH (NOLOCK){na1Join}{tyJoin}
                     {where}{attrBranch}
                    ) u
               ORDER BY {orderU}
               OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY{hint}
               """
            : hasCondition
            ? $"""
               SELECT x.id, x.dpm, x.cp, x.nno, x.na1, x.ty, x.fa, x.dc, x.unit, x.kcamount, x.iprj, x.iprc, x.oprc, x.oprb, x.posi, x.depotno, x.note, t._total
               FROM (SELECT t.id, COUNT(1) OVER() AS _total
                     FROM tbisto t WITH (NOLOCK){na1Join}{tyJoin}
                     {where}
                     ORDER BY {order}, t.id
                     OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY) t
               INNER JOIN tbisto x WITH (NOLOCK) ON x.id = t.id
               ORDER BY {orderX}{hint}
               """
            : $"""
               SELECT {SelectColumns}, COUNT(1) OVER() AS _total
               FROM tbisto t WITH (NOLOCK){na1Join}{tyJoin}
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
    public async Task<IReadOnlyList<PartItem>> SearchByNnosAsync(string dpm,
        IReadOnlyList<(string Code, bool Contains)> codes, CancellationToken ct = default)
    {
        var normalized = new List<(string Code, bool Contains)>();
        foreach (var c in codes)
        {
            var code = (c.Code ?? "").Trim();
            if (code.Length > 0) normalized.Add((code, c.Contains));
        }
        if (normalized.Count == 0) return [];

        // 逐行参数化（一行一个编号）：精准 = RTRIM(nno) 相等；包含 = nno LIKE %编号%
        var args = new DynamicParameters();
        args.Add("dpm", new DbString { Value = dpm.Trim(), IsAnsi = true, Length = 10 });
        var conditions = new List<string>();
        for (var i = 0; i < normalized.Count; i++)
        {
            var (code, contains) = normalized[i];
            var p = $"c{i}";
            args.Add(p, new DbString { Value = code, IsAnsi = true, Length = 40 });
            conditions.Add(contains ? $"t.nno LIKE '%' + @{p} + '%'" : $"RTRIM(t.nno) = @{p}");
        }

        var sql = $"""
            SELECT {SelectColumnsT}
            FROM tbisto t WITH (NOLOCK)
            WHERE t.dpm = @dpm AND ({string.Join(" OR ", conditions)})
            ORDER BY t.nno, t.id
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<PartSearchRow>(new CommandDefinition(sql, args, cancellationToken: ct))).AsList();
        return rows.Select(r => r.ToItem()).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(string Cp, int Count)>> CountOtherBrandsAsync(string dpm, string brand,
        string? cp, string? na1, string? ty, CancellationToken ct = default, string? note = null)
    {
        var cpKw = (cp ?? "").Trim();
        var na1Kw = (na1 ?? "").Trim();
        var tyKw = (ty ?? "").Trim();
        var noteKw = (note ?? "").Trim();
        var brandKw = (brand ?? "").Trim();
        if (brandKw.Length == 0) return [];

        // 编码框双模式与 SearchAsync 同构（2026-10-30 需求）：首字符 / = 包含；否则前缀
        var cpContains = cpKw.StartsWith("/");
        var cpWord = cpContains ? cpKw[1..].Trim() : cpKw;
        var hasCp = cpWord.Length > 0;

        // 条件与 SearchAsync 完全同构，仅厂牌条件换为 cp <> @brand；
        // 拼音码用 LEFT JOIN 派生表（同 SearchAsync 8622 修复：双 IN 半连接 + HASH JOIN 提示无法生成计划）
        var na1Join = na1Kw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(name) AS name FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%') nb ON rtrim(t.na1) = nb.name"
            : "";
        var tyJoin = tyKw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS ty FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @ty + '%') tb2 ON rtrim(t.ty) = tb2.ty"
            : "";
        var conditions = new List<string> { "t.dpm = @dpm", "t.cp <> @brand" };
        if (hasCp)
            conditions.Add(cpContains ? "t.nno LIKE '%' + @cp + '%'" : "t.nno LIKE @cp + '%'");
        if (noteKw.Length > 0)
            conditions.Add("t.note LIKE '%' + @note + '%'");
        if (tyKw.Length > 0)
            conditions.Add("""
                (t.ty LIKE '%' + @ty + '%'
                 OR t.na1 LIKE '%' + @ty + '%'
                 OR t.note LIKE '%' + @ty + '%'
                 OR tb2.ty IS NOT NULL)
                """);
        var hint = (na1Kw.Length > 0 || tyKw.Length > 0) ? " OPTION (HASH JOIN)" : "";

        // 属性库补集同计入跨厂牌命中（2026-10-29：属性定义新增配件无任何库存行，仅统计 tbisto 会漏报），
        // 条件与 SearchAsync 的 attrBranch 同构，厂牌条件换为 rtrim(p.cp) <> @brand
        var na1JoinP = na1Kw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(name) AS name FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%') nb ON rtrim(p.na1) = nb.name"
            : "";
        var tyJoinP = tyKw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS ty FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @ty + '%') tb2 ON rtrim(p.ty) = tb2.ty"
            : "";
        var attrConditions = new List<string> { "rtrim(p.cp) <> @brand" };
        if (hasCp)
            attrConditions.Add(cpContains ? "rtrim(p.nno) LIKE '%' + @cp + '%'" : "rtrim(p.nno) LIKE @cp + '%'");
        if (noteKw.Length > 0)
            attrConditions.Add("rtrim(p.note) LIKE '%' + @note + '%'");
        if (na1Kw.Length > 0)
        {
            attrConditions.Add("""
                (rtrim(p.na1) LIKE '%' + @na1 + '%'
                 OR rtrim(p.nno) LIKE '%' + @na1 + '%'
                 OR rtrim(p.ty) LIKE '%' + @na1 + '%'
                 OR nb.name IS NOT NULL)
                """);
        }
        if (tyKw.Length > 0)
            attrConditions.Add("""
                (rtrim(p.ty) LIKE '%' + @ty + '%'
                 OR rtrim(p.na1) LIKE '%' + @ty + '%'
                 OR rtrim(p.note) LIKE '%' + @ty + '%'
                 OR tb2.ty IS NOT NULL)
                """);
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
                FROM tbisto t WITH (NOLOCK){na1Join}{tyJoin}
                WHERE {string.Join(" AND ", conditions)}
                GROUP BY RTRIM(t.cp)
                UNION ALL
                SELECT rtrim(p.cp) AS cp, COUNT(1) AS n
                FROM tbprnoty p WITH (NOLOCK){na1JoinP}{tyJoinP}
                WHERE {string.Join(" AND ", attrConditions)}
                GROUP BY rtrim(p.cp)
            ) z
            GROUP BY z.cp{hint}
            """,
            new
            {
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                brand = new DbString { Value = brandKw, IsAnsi = false, Length = 20 },
                cp = hasCp ? new DbString { Value = cpWord, IsAnsi = true, Length = 40 } : null,
                na1 = new DbString { Value = na1Kw, IsAnsi = false, Length = 40 },
                ty = new DbString { Value = tyKw, IsAnsi = false, Length = 40 },
                note = noteKw.Length > 0 ? new DbString { Value = noteKw, IsAnsi = true, Length = 50 } : null,
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
                   RTRIM(paper) AS Paper, type AS Type, outdate AS OutDate, RTRIM(gname) AS Gname,
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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PartPurchaseDocLine>> GetPurchaseDocLinesAsync(string cno, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PartPurchaseDocLine>(new CommandDefinition(
            """
            SELECT RTRIM(cno) AS Cno, RTRIM(nno) AS Nno, RTRIM(na1) AS Na1, RTRIM(ty) AS Ty,
                   ISNULL(jkamount, 0) AS Qty, ISNULL(iprc, 0) AS Iprc, indate AS InDate,
                   RTRIM(ISNULL(gyname, '')) AS Gyname
            FROM tbistoed WITH (NOLOCK)
            WHERE RTRIM(cno) = @cno
            ORDER BY id
            """,
            new { cno = new DbString { Value = cno.Trim(), IsAnsi = true, Length = 20 } },
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetPartImageAsync(string nno, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // imgs 为 base64 文本（JPG/PNG），fn 存原始文件名；旧系统 bmp 目录文件方案已迁入库内，客户端零依赖。
        // tbImage.nno 存的是去掉空格的紧凑编号（如 PT48510-0N050），与 tbisto 带空格写法（PT 48510-0N050）不一致，
        // 双形式匹配（2026-10-31 修复：图片不显示）
        var key = nno.Trim();
        var compact = key.Replace(" ", "");
        var imgs = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 imgs FROM tbImage WITH (NOLOCK) WHERE RTRIM(nno) IN (@key, @compact) ORDER BY id",
            new
            {
                key = new DbString { Value = key, IsAnsi = true, Length = 18 },
                compact = new DbString { Value = compact, IsAnsi = true, Length = 18 },
            },
            cancellationToken: ct));
        if (string.IsNullOrWhiteSpace(imgs)) return null;
        try { return Convert.FromBase64String(imgs.Trim()); }
        catch { return null; }   // base64 损坏按无图处理
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, decimal>> GetStockByIdsAsync(IReadOnlyCollection<int> ids, string dpm, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<int, decimal>();
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int Id, decimal Kcamount)>(new CommandDefinition(
            """
            SELECT id, kcamount FROM tbisto WITH (NOLOCK)
            WHERE dpm = @dpm AND id IN @ids
            """,
            new { dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 }, ids },
            cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.Kcamount);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LabelStockItem>> GetLabelItemsAsync(string dpm, string brand,
        string? nno = null, string? na1 = null, string? ty = null, CancellationToken ct = default)
    {
        var nnoKw = (nno ?? "").Trim();
        var na1Kw = (na1 ?? "").Trim();
        var tyKw = (ty ?? "").Trim();
        var brandKw = (brand ?? "").Trim();

        var conditions = new List<string> { "t.dpm = @dpm" };
        if (brandKw.Length > 0) conditions.Add("t.cp = @brand");
        if (nnoKw.Length > 0) conditions.Add("t.nno LIKE '%' + @nno + '%'");

        // 拼音码匹配与开单页 SearchAsync 同构（2026-10-31 需求：标签打印名称/车型框支持拼音码）：
        // LEFT JOIN 派生表（tbname0 名称拼音码 / tbty0 车型拼音码，code→name/ty 等值连接），
        // 派生表已 DISTINCT 不膨胀行数，GROUP BY nno 去重语义不变
        var na1Join = na1Kw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(name) AS name FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%') nb ON rtrim(t.na1) = nb.name"
            : "";
        var tyJoin = tyKw.Length > 0
            ? "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS ty FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @ty + '%') tb2 ON rtrim(t.ty) = tb2.ty"
            : "";
        if (na1Kw.Length > 0)
            conditions.Add("(t.na1 LIKE '%' + @na1 + '%' OR nb.name IS NOT NULL)");
        if (tyKw.Length > 0)
            conditions.Add("(t.ty LIKE '%' + @ty + '%' OR tb2.ty IS NOT NULL)");

        // 名称/车型条件带拼音码 JOIN 派生表时强制 HASH JOIN（同 SearchAsync：嵌套循环会逐行重扫，实测 10s+）
        var hint = (na1Kw.Length > 0 || tyKw.Length > 0) ? "\nOPTION (HASH JOIN)" : "";

        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<LabelStockItem>(new CommandDefinition(
            $"""
            SELECT t.nno, MIN(t.cp) cp, MAX(t.na1) na1, MAX(t.ty) ty,
                   MIN(NULLIF(LTRIM(RTRIM(t.posi)), '')) posi, SUM(t.kcamount) kcamount
            FROM tbisto t WITH (NOLOCK){na1Join}{tyJoin}
            WHERE {string.Join(" AND ", conditions)}
            GROUP BY t.nno
            ORDER BY posi, nno{hint}
            """,
            new
            {
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                brand = new DbString { Value = brandKw, IsAnsi = true, Length = 20 },
                nno = nnoKw,
                na1 = na1Kw,
                ty = tyKw,
            },
            cancellationToken: ct));
        return rows.ToList();
    }
}
