using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Query;

public interface IUniversalQueryRepository
{
    /// <summary>E1 进库单查询（fr_seissm row58；P_CA 分支：调用方 dpmusem + tbxzb 双份过滤）。</summary>
    Task<IReadOnlyList<IssmRow>> SearchIssmAsync(string where, bool showRemain, string dpm, string dpmusem, CancellationToken ct = default);

    /// <summary>E1 计算（row86：按 nno 前向流水摊余 XKCAMOU/XSAMOU，应用层重算）。</summary>
    Task<IReadOnlyList<IssmRow>> RecalcIssmRemainAsync(IReadOnlyList<IssmRow> rows, string dpm, CancellationToken ct = default);

    /// <summary>E4 订货单查询（fr_seorder row23：强制 type='历史'）。</summary>
    Task<IReadOnlyList<OrderRow>> SearchOrderAsync(string where, string dpm, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>E5 库存修改查询（fr_sekcxg row24）。</summary>
    Task<IReadOnlyList<KcxgRow>> SearchKcxgAsync(string where, string dpm, string dpmusem, CancellationToken ct = default);

    /// <summary>E6 商品出入查询（fr_seio sp_io：类型集来自 tbsysset type='xs'，六路聚合 + 应用层拼合）。</summary>
    Task<IReadOnlyList<IoRow>> SearchIoAsync(string how, DateTime d1, DateTime d2, string dimTag, string dpm, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>E7 月销售数查询（fr_sertmo row54：tbrtsamo + Σoamou/月数，ORDER BY dpm,nno）。</summary>
    Task<IReadOnlyList<RtmoRow>> SearchRtmoAsync(string where, string dpm, string dpmusem, int months, CancellationToken ct = default);

    /// <summary>E8 营业额查询（fr_sesada row33：P_FGS 分支，GROUP BY outdate+部门名）。</summary>
    Task<IReadOnlyList<SesadaRow>> SearchSesadaAsync(DateTime d1, DateTime d2, string? cldpm, string dpm, string dpmusem, CancellationToken ct = default);

    /// <summary>E9 库存检测（fr_secheck row27：备份链 vs 实库，全局汇总 + 逐品差异行）。</summary>
    Task<StockCheckResult> CheckStockAsync(DateTime date, string dpm, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>EA 进出流水帐（fr_seis row51：单品 + 日期窗，结存/进库/修改/销售/领料五路 + 倒序滚动）。</summary>
    Task<IReadOnlyList<IsiosmRow>> SearchIsiosmAsync(string nno, DateTime d1, DateTime d2, string dpm, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>fr_mdefi 模式②系统表清单（FrmSTable.Init：tbstru TABLE_CNAM 非空且该表同时含 CP/NNO 字段）。</summary>
    Task<IReadOnlyList<SysTableInfo>> GetSysTablesAsync(CancellationToken ct = default);

    /// <summary>fr_mdefi 模式②字段清单（tbstru 按表；FLAG=2 分支：SELECT * FROM tbstru WHERE TABLE_NAME=..）。</summary>
    Task<IReadOnlyList<SysFieldInfo>> GetSysFieldsAsync(string table, CancellationToken ct = default);

    /// <summary>
    /// fr_mdefi 模式②聚合（FrmAccount.CmdFinish：PASSTH select 无 WHERE 无 tbxzb，照抄旧拼串）。
    /// hasKey=true → "SELECT cp, nno, ..."；任一 Method=1（原值，M_CG=.F.）→ 无 GROUP BY 全表行。
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AggregateSysTableAsync(
        string table, IReadOnlyList<AggregateItem> items, bool hasCpKey, bool hasNnoKey, CancellationToken ct = default);

    /// <summary>P_COMPANY（pg_proc 主程序：tbsysset type='system' name='公司名称' valuec）。</summary>
    Task<string> GetCompanyNameAsync(CancellationToken ct = default);

    /// <summary>fr_total 公司名称列回填源（tbdpm dpmbh→dpm）。</summary>
    Task<IReadOnlyDictionary<string, string>> GetDpmNamesAsync(CancellationToken ct = default);
}

/// <summary>fr_mdefi 模式②系统表（tbstru DISTINCT TABLE_NAME/TABLE_CNAM）。</summary>
public sealed record SysTableInfo(string TableName, string Cname);

/// <summary>fr_mdefi 模式②系统表字段（tbstru 行）。</summary>
public sealed record SysFieldInfo(string TableName, string FieldName, string Explain, char FieldType);

/// <summary>fr_mdefi 聚合配置（C_FIELD：Method 1 原值 / 2 max / 3 min / 4 sum / 5 avg）。</summary>
public sealed record AggregateItem(string SourceField, string Explain, char SourceType, int Method, string NewName)
{
    /// <summary>聚合表达式（旧 m_fieldname 拼串语义）。</summary>
    public string AggExpression => Method switch
    {
        2 => $"max({SourceField})",
        3 => $"min({SourceField})",
        4 => $"sum({SourceField})",
        5 => $"avg({SourceField})",
        _ => SourceField,
    };
}

// ---------- 行模型 ----------

/// <summary>E1 进库单行（M_B2 列集，code_ts/low_rate 占位列省略）。</summary>
public sealed class IssmRow
{
    public long Id { get; set; }
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Jkamount { get; set; }
    public decimal Iprj { get; set; }
    public string Cxnno { get; set; } = "";
    public decimal Iprc { get; set; }
    public decimal Oprc { get; set; }
    public string Posi { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Cno { get; set; } = "";
    public string Box { get; set; } = "";
    public string Seano { get; set; } = "";
    public DateTime Indate { get; set; }
    public string Per { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Pno { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Dpm { get; set; } = "";
    public string Depotno { get; set; } = "";
    public string Note { get; set; } = "";
    public string Ywr { get; set; } = "";
    public string Gyno { get; set; } = "";
    public string Isord { get; set; } = "";
    public DateTime? Qrdate { get; set; }
    public decimal Xkcamou { get; set; }
    public decimal Xsamou { get; set; }
    public decimal Freight { get; set; }
}

/// <summary>E4 订货单行。</summary>
public sealed class OrderRow
{
    public long Id { get; set; }
    public string Cno { get; set; } = "";
    public int Item { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Oqty { get; set; }
    public decimal Qty { get; set; }
    public decimal Zt { get; set; }
    public decimal Iprj { get; set; }
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public DateTime Orderdate { get; set; }
    public string Fkfs { get; set; } = "";
}

/// <summary>E5 库存修改行。</summary>
public sealed class KcxgRow
{
    public long Id { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public decimal Xgamount { get; set; }
    public decimal Iprc { get; set; }
    public DateTime Xgdate { get; set; }
    public string Per { get; set; } = "";
    public string Posi { get; set; } = "";
    public string Depotno { get; set; } = "";
}

/// <summary>E6 商品出入汇总行（列名含旧码"调拔"（拔），调拨三列旧码恒 0——列名 拨/拔 错位 bug 照抄）。</summary>
public sealed class IoRow
{
    public string How { get; set; } = "";
    public decimal 销售数 { get; set; }
    public decimal 销售成本 { get; set; }
    public decimal 销售额 { get; set; }
    public decimal 进库数 { get; set; }
    public decimal 进库额 { get; set; }
    public decimal 库存数 { get; set; }
    public decimal 毛利 { get; set; }
    public string 毛利率 { get; set; } = "";
    public decimal 预售数 { get; set; }
    public decimal 预售成本 { get; set; }
    public decimal 预售额 { get; set; }
    public decimal 调拔数 { get; set; }
    public decimal 调拔成本 { get; set; }
    public decimal 调拔额 { get; set; }
    public string Cp { get; set; } = "";
}

/// <summary>E7 月销售数行。</summary>
public sealed class RtmoRow
{
    public long Id { get; set; }
    public string Dpm { get; set; } = "";
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Unit { get; set; } = "";
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
    public decimal 月均销售数 { get; set; }
}

/// <summary>E8 营业额行（日期列旧 DTOC 字符串）。</summary>
public sealed class SesadaRow
{
    public string 日期 { get; set; } = "";
    public string Dpm { get; set; } = "";
    public int 单数 { get; set; }
    public decimal 出库数量 { get; set; }
    public decimal 日元 { get; set; }
    public decimal 人进价 { get; set; }
    public decimal 售额 { get; set; }
    public decimal 实售额 { get; set; }
    public decimal ML { get; set; }
    public decimal 牟利率 { get; set; }
    public bool IsTotal { get; set; }
}

/// <summary>E9 库存检测差异行（c_check 游标列；列8=逻辑库存、列10=误差负值，旧网格计算列）。</summary>
public sealed class StockCheckRow
{
    public string Nno { get; set; } = "";
    public string Cp { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public decimal Sav { get; set; }
    public decimal Outs { get; set; }
    public decimal In { get; set; }
    public decimal Istoch { get; set; }
    public decimal Saxg { get; set; }
    public decimal Isto { get; set; }
    /// <summary>列8 = sav+in+istoch+saxg-outs（逻辑库存）。</summary>
    public decimal 逻辑库存 => Sav + In + Istoch + Saxg - Outs;
    /// <summary>列10 = -(sav+in+istoch+saxg-outs-isto)（误差负值）。</summary>
    public decimal 误差 => -(Sav + In + Istoch + Saxg - Outs - Isto);
}

/// <summary>E9 检测结果：全局汇总（TXT1 文本要素）+ 差异行。</summary>
public sealed class StockCheckResult
{
    public bool HasBackup { get; set; }
    public decimal SavA { get; set; }
    public decimal IstoA { get; set; }
    public decimal SaedA { get; set; }
    public decimal SadaAll { get; set; }
    public decimal SadaA { get; set; }
    public decimal SabeA { get; set; }
    public decimal SadbA { get; set; }
    public decimal XlsaA { get; set; }
    public decimal IstochA { get; set; }
    public decimal SaxgA { get; set; }
    public decimal IstoedA { get; set; }
    /// <summary>逻辑库存 = SAV+ISTOED-SAED-SADA_A-SABE-SADB-XLSA+ISTOCH-SAXG。</summary>
    public decimal NowA { get; set; }
    /// <summary>误差 = 实际 - 逻辑。</summary>
    public decimal DiffA { get; set; }
    public IReadOnlyList<StockCheckRow> Rows { get; set; } = Array.Empty<StockCheckRow>();
}

/// <summary>EA 进出流水行（c_isiosm 游标列）。</summary>
public sealed class IsiosmRow
{
    public string Per { get; set; } = "";
    public string No { get; set; } = "";
    public DateTime? Date { get; set; }
    public string Mark { get; set; } = "";
    public decimal Jqty { get; set; }
    public decimal Cqty { get; set; }
    public decimal? Amount { get; set; }
    public string Note { get; set; } = "";
    public string Posi { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public decimal? Iprc { get; set; }
    public string Depotno { get; set; } = "";
    /// <summary>结存金额（倒序滚动累计进价金额）。</summary>
    public decimal? 结存金额 { get; set; }
    public decimal? Iprj { get; set; }
    /// <summary>外币价金额（列头 = 当前厂牌 tbsysset valuec + "结存金额"）。</summary>
    public decimal? 外币价金额 { get; set; }
}

/// <summary>
/// E 组万能查询族仓储（第十一批，只读）。
/// 契约：开发文档/契约摘录/E组万能查询族契约.md。
/// WHERE 由调用方（FindExpressionBuilder / 页面筛选）构造，仓储统一追加 tbxzb 自动过滤（PASSTH/BUILD_CA 语义）。
/// </summary>
public sealed class UniversalQueryRepository(SqlConnectionFactory factory) : IUniversalQueryRepository
{
    // ---------- tbxzb 追加器（pg_proc_client passth L311-398 / cmm_class build_ca） ----------

    private sealed record XzbRule(string Fieldname, string Variable);

    private async Task<List<XzbRule>> GetXzbRulesAsync(SqlConnection conn, string table, CancellationToken ct)
    {
        var rules = await conn.QueryAsync<(string Fieldname, string Variable)>(
            new CommandDefinition(
                "SELECT rtrim(fieldname), rtrim(variable) FROM tbxzb WITH (NOLOCK) WHERE tablename = @t OR @t LIKE rtrim(tablename) + ' %'",
                new { t = new DbString { Value = table, IsAnsi = true, Length = 250 } },
                cancellationToken: ct));
        return rules.Select(r => new XzbRule(r.Fieldname, r.Variable)).ToList();
    }

    /// <summary>把用户条件包一层括号并追加 tbxzb 规则；变量 ?p_xxx 参数化为 @p_xxx 并按需补参数。</summary>
    private async Task<(string WhereSql, DynamicParameters Params)> BuildWhereAsync(
        SqlConnection conn, string table, string userWhere, string dpm, string dpmusem, string? cp, CancellationToken ct)
    {
        var p = new DynamicParameters();
        var rules = await GetXzbRulesAsync(conn, table, ct);
        var low = (userWhere ?? "").ToLowerInvariant();
        var ti = new StringBuilder();
        foreach (var rule in rules)
        {
            if (rule.Fieldname == "dpm" && string.IsNullOrEmpty(dpm)) continue;
            if (low.Contains("id=") || low.Contains(rule.Fieldname.ToLowerInvariant() + "=")) continue;
            var expr = rule.Variable.Replace("?p_dpmusem", "@p_dpmusem").Replace("?p_dpm", "@p_dpm").Replace("?p_cp", "@p_cp");
            ti.Append(" and ").Append(expr);
        }
        if (ti.Length > 0)
        {
            if (ti.ToString().Contains("@p_dpmusem") && dpmusem != null)
                p.Add("p_dpmusem", new DbString { Value = dpmusem ?? "", IsAnsi = true, Length = 60 });
            if (ti.ToString().Contains("@p_dpm") && !ti.ToString().Contains("@p_dpmusem") && dpm != null)
                p.Add("p_dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
            if (ti.ToString().Contains("@p_cp") && !string.IsNullOrEmpty(cp))
                p.Add("p_cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        }
        var where = string.IsNullOrWhiteSpace(userWhere)
            ? (ti.Length > 0 ? "1=1" + ti : "")
            : "(" + userWhere + ")" + ti;
        return (where, p);
    }

    // ---------- E1 进库单查询（fr_seissm row58） ----------

    private const string IssmBaseColumns = """
        id, rtrim(cp) cp, rtrim(nno) nno, rtrim(na1) na1, jkamount, iprj, rtrim(cxnno) cxnno, iprc, oprc,
        rtrim(posi) posi, rtrim(ty) ty, rtrim(fa) fa, rtrim(dc) dc, rtrim(gyname) gyname, rtrim(cno) cno,
        rtrim(box) box, rtrim(seano) seano, indate, rtrim(per) per, rtrim(fkfs) fkfs, rtrim(pno) pno,
        rtrim(unit) unit, rtrim(dpm) dpm, rtrim(depotno) depotno, rtrim(note) note, rtrim(ywr) ywr,
        rtrim(gyno) gyno, rtrim(isord) isord, qrdate
        """;

    /// <summary>xkcamou/xsamou 大 CASE（CHECK1=1；?p_dpm → @dpm，P_WITHNOLOCK=.T.）。</summary>
    private const string IssmRemainCase = """
        ,CASE when indate<='1900-01-01' or isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
            >ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
          then 0 else
          CASE when jkamount>ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
            -isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
          then ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
            -isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
          else jkamount end end as xkcamou
        ,CASE when indate<='1900-01-01' or isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
            >ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
          then jkamount-0 else
          CASE when jkamount>ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
            -isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
          then jkamount-(ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
            -isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0))
          else jkamount-jkamount end end as xsamou
        ,CAST((iprc-CASE WHEN freight=0 THEN iprc ELSE Freight END)*jkamount AS NUMERIC(18,2)) AS freight
        """;

    public async Task<IReadOnlyList<IssmRow>> SearchIssmAsync(string where, bool showRemain, string dpm, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var (whereSql, p) = await BuildWhereAsync(conn, "tbistoed", where, dpm, dpmusem, null, ct);
        // P_CA 分支调用方已拼一条 dpmusem（双份过滤，契约 §1.1）
        var fullWhere = whereSql.Length == 0 ? "1=1" : whereSql;
        if (!string.IsNullOrEmpty(dpm))
            fullWhere += " and @p_dpmusem like '%'+rtrim(dpm)+'%'";
        if (!p.ParameterNames.Contains("p_dpmusem"))
            p.Add("p_dpmusem", new DbString { Value = dpmusem ?? "", IsAnsi = true, Length = 60 });
        p.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });

        var cols = showRemain ? IssmBaseColumns + "\n" + IssmRemainCase : IssmBaseColumns + "\n,0 as xkcamou, 0 as xsamou, CAST((iprc-CASE WHEN freight=0 THEN iprc ELSE Freight END)*jkamount AS NUMERIC(18,2)) AS freight";
        var sql = $"SELECT {cols} FROM tbistoed WITH (NOLOCK) WHERE {fullWhere} ORDER BY indate DESC, ID DESC";
        var rows = (await conn.QueryAsync<IssmRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
        return rows;
    }

    /// <summary>row86 计算：应用层重算（对每个 nno 一次聚合，避免逐行子查询）。</summary>
    public async Task<IReadOnlyList<IssmRow>> RecalcIssmRemainAsync(IReadOnlyList<IssmRow> rows, string dpm, CancellationToken ct = default)
    {
        if (rows.Count == 0) return rows;
        await using var conn = await factory.OpenAsync(ct);
        var nnos = rows.Select(r => r.Nno).Distinct().ToList();
        // 前向流水：Σ jkamount（indate 大于本行 或 同日 id 大于本行）
        var ls = (await conn.QueryAsync<(string Nno, long Id, DateTime Indate, decimal Sum)>(
            new CommandDefinition("""
                SELECT rtrim(a.nno), b.id, a.indate, SUM(a.jkamount)
                FROM tbistoed a WITH (NOLOCK)
                JOIN (SELECT id, nno, indate FROM tbistoed WITH (NOLOCK) WHERE rtrim(nno) IN @nnos AND dpm = @dpm) b
                  ON a.nno = b.nno AND a.dpm = @dpm AND (a.indate > b.indate OR (a.indate = b.indate AND a.id > b.id))
                GROUP BY rtrim(a.nno), b.id, a.indate
                """, new { nnos, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } }, cancellationToken: ct))).AsList();
        var kc = (await conn.QueryAsync<(string Nno, decimal Sum)>(
            new CommandDefinition(
                "SELECT rtrim(nno), SUM(kcamount) FROM tbisto WITH (NOLOCK) WHERE rtrim(nno) IN @nnos AND dpm = @dpm GROUP BY rtrim(nno)",
                new { nnos, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } }, cancellationToken: ct))).AsList();
        var lsMap = ls.ToDictionary(x => (x.Nno, x.Id), x => x.Sum);
        var kcMap = kc.ToDictionary(x => x.Nno, x => x.Sum);
        foreach (var row in rows)
        {
            var lsAmou = lsMap.TryGetValue((row.Nno, row.Id), out var v) ? v : 0m;
            var kcAmou = kcMap.TryGetValue(row.Nno, out var k) ? k : 0m;
            decimal lc;
            if (lsAmou > kcAmou) lc = 0m;
            else if (row.Jkamount > kcAmou - lsAmou) lc = kcAmou - lsAmou;
            else lc = row.Jkamount;
            row.Xkcamou = lc;
            row.Xsamou = row.Jkamount - lc;
        }
        return rows;
    }

    // ---------- E4 订货单查询（fr_seorder row23） ----------

    public async Task<IReadOnlyList<OrderRow>> SearchOrderAsync(string where, string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var userWhere = $"({where}) and type='历史'";
        var (whereSql, p) = await BuildWhereAsync(conn, "tborder", userWhere, dpm, dpmusem, cp, ct);
        var sql = $"""
            SELECT id, rtrim(cno) cno, item, rtrim(nno) nno, rtrim(na1) na1, oqty, qty, oqty - qty AS zt, iprj,
                   rtrim(ty) ty, rtrim(fa) fa, orderdate, rtrim(fkfs) fkfs
            FROM tborder WITH (NOLOCK)
            WHERE {whereSql}
            ORDER BY id
            """;
        return (await conn.QueryAsync<OrderRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
    }

    // ---------- E5 库存修改查询（fr_sekcxg row24） ----------

    public async Task<IReadOnlyList<KcxgRow>> SearchKcxgAsync(string where, string dpm, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var (whereSql, p) = await BuildWhereAsync(conn, "tbistoch", where, dpm, dpmusem, null, ct);
        var sql = $"""
            SELECT id, rtrim(nno) nno, rtrim(na1) na1, rtrim(ty) ty, xgamount, iprc, xgdate,
                   rtrim(per) per, rtrim(posi) posi, rtrim(depotno) depotno
            FROM tbistoch WITH (NOLOCK)
            WHERE {(whereSql.Length == 0 ? "1=1" : whereSql)}
            ORDER BY id
            """;
        return (await conn.QueryAsync<KcxgRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
    }

    // ---------- E6 商品出入查询（fr_seio row2.sp_io） ----------

    private sealed record IoAggRow(string How, decimal N, decimal Cost, decimal Amt, decimal Ml, decimal Mlr);

    /// <summary>sp_io 游标列累计器（旧 APPEND FROM 同名匹配语义）。</summary>
    private sealed class IoAcc
    {
        public decimal 销售数, 销售成本, 销售额, 进库数, 进库额, 库存数, 毛利, 预售数, 预售成本, 预售额;
        public decimal 毛利率数值;
    }

    public async Task<IReadOnlyList<IoRow>> SearchIoAsync(string how, DateTime d1, DateTime d2, string dimTag, string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        var ft = $"outdate between '{d1:yyyy-MM-dd}' and '{d2:yyyy-MM-dd}'" + (string.IsNullOrWhiteSpace(dimTag) ? "" : $" and {dimTag}");
        // 销售行毛利公式（旧码 ISNULL + 连接 ANSI_WARNINGS OFF 除零兜底 → 新侧 NULLIF 等价，契约 §1.2）
        var salesSql = $"""
            SELECT rtrim({how}) AS How, SUM(ckamount) AS N, SUM(ckamount * iprc) AS Cost, SUM(ckamount * oprct * (100 - yhl) / 100) AS Amt,
                   ISNULL(SUM(ckamount * oprct * (100 - yhl) / 100) - SUM(ckamount * iprc), 0) AS Ml,
                   ISNULL((SUM(ckamount * oprct * (100 - yhl) / 100) - SUM(ckamount * iprc)) / NULLIF(SUM(ckamount * iprc), 0) * 100, 0) AS Mlr
            FROM tbsada WITH (NOLOCK)
            WHERE type = @lx AND cp = @cp AND {ft}
            GROUP BY rtrim({how}), cp, type
            """;

        await using var conn = await factory.OpenAsync(ct);
        // 进库路：userWhere 含 'cp=' → cp 规则跳过，仅追加 dpmusem（旧 PASSTH 语义）
        var (inWhere, inP) = await BuildWhereAsync(conn, "tbistoed",
            $"cp=@cp and indate between '{d1:yyyy-MM-dd}' and '{d2:yyyy-MM-dd}'" + (string.IsNullOrWhiteSpace(dimTag) ? "" : $" and {dimTag.Replace("outdate", "indate")}"),
            dpm, dpmusem, cp, ct);
        // 库存路：tbisto 有 cp+dpmusem 两条规则，userWhere 不含 'cp=' → 两条均追加
        var (stoWhere, stoP) = await BuildWhereAsync(conn, "tbisto",
            $"kcbhdate between '{d1:yyyy-MM-dd}' and '{d2:yyyy-MM-dd}'" + (string.IsNullOrWhiteSpace(dimTag) ? "" : $" and {dimTag.Replace("outdate", "kcbhdate")}"),
            dpm, dpmusem, cp, ct);
        inP.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        stoP.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        var inFull = $"""
            SELECT rtrim({how}) AS How, SUM(jkamount) AS N, SUM(jkamount * iprc) AS Cost, SUM(jkamount * oprc) AS Amt, CAST(0 AS decimal(18,4)) AS Ml, CAST(0 AS decimal(18,4)) AS Mlr
            FROM tbistoed WITH (NOLOCK) WHERE {inWhere} GROUP BY rtrim({how}), cp
            """;
        var stoFull = $"""
            SELECT rtrim({how}) AS How, SUM(kcamount) AS N, CAST(0 AS decimal(18,4)) AS Cost, CAST(0 AS decimal(18,4)) AS Amt, CAST(0 AS decimal(18,4)) AS Ml, CAST(0 AS decimal(18,4)) AS Mlr
            FROM tbisto WITH (NOLOCK) WHERE {stoWhere} GROUP BY rtrim({how}), cp
            """;

        // 类型集（动态，tbsysset type='xs' name<>'报价'；tbsysset tbxzb 规则 dpm=?p_dpm）
        var (sysWhere, sysP) = await BuildWhereAsync(conn, "tbsysset", "type='xs' and name<>'报价'", dpm, dpmusem, null, ct);
        var types = (await conn.QueryAsync<string>(new CommandDefinition(
            $"SELECT rtrim(name) FROM tbsysset WITH (NOLOCK) WHERE {sysWhere}", sysP, cancellationToken: ct))).AsList();

        var map = new Dictionary<string, IoAcc>();
        IoAcc Acc(string key)
        {
            if (!map.TryGetValue(key, out var v)) { v = new IoAcc(); map[key] = v; }
            return v;
        }

        foreach (var lx in types)
        {
            var rows = (await conn.QueryAsync<IoAggRow>(new CommandDefinition(salesSql,
                new DynamicParameters(new { lx = new DbString { Value = lx, IsAnsi = false, Length = 14 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } }),
                cancellationToken: ct))).AsList();
            foreach (var r in rows)
            {
                var acc = Acc(r.How);
                switch (lx)
                {
                    // 列名对齐旧游标 APPEND FROM 同名匹配（契约 §6）
                    case "销售":
                        acc.销售数 += r.N; acc.销售成本 += r.Cost; acc.销售额 += r.Amt;
                        acc.毛利 += r.Ml; acc.毛利率数值 += r.Mlr;
                        break;
                    case "预售":
                        acc.预售数 += r.N; acc.预售成本 += r.Cost; acc.预售额 += r.Amt;
                        acc.毛利 += r.Ml; acc.毛利率数值 += r.Mlr;
                        break;
                    default:
                        // 调拨/历史：数量/成本/额列名错位（拨/拔）丢弃；毛利/毛利率列匹配并入
                        acc.毛利 += r.Ml; acc.毛利率数值 += r.Mlr;
                        break;
                }
            }
        }
        // 进库：进库数/进库额 对齐，"进库成本"旧游标无此列丢弃
        foreach (var r in await conn.QueryAsync<IoAggRow>(new CommandDefinition(inFull, inP, cancellationToken: ct)))
        {
            var acc = Acc(r.How);
            acc.进库数 += r.N; acc.进库额 += r.Amt;
        }
        // 库存：仅库存数
        foreach (var r in await conn.QueryAsync<IoAggRow>(new CommandDefinition(stoFull, stoP, cancellationToken: ct)))
            Acc(r.How).库存数 += r.N;

        // 汇总：GROUP BY {how} → SUM 各列 + 毛利率 = str(Σ毛利/Σ销售成本*100,7,2)+'%'（销售成本=0 → ''）
        var rows2 = map.Select(kv =>
        {
            var a = kv.Value;
            return new IoRow
            {
                How = kv.Key,
                销售数 = a.销售数, 销售成本 = a.销售成本, 销售额 = a.销售额,
                进库数 = a.进库数, 进库额 = a.进库额, 库存数 = a.库存数,
                毛利 = a.毛利,
                毛利率 = a.销售成本 == 0 ? "" : (a.毛利 / a.销售成本 * 100).ToString("0.00") + "%",
                预售数 = a.预售数, 预售成本 = a.预售成本, 预售额 = a.预售额,
                Cp = "",
            };
        }).ToList();
        // 合计行（旧码 INSERT '[合计]' 后 REPLACE ALL 毛利率 '' FOR 销售成本=0 一并生效）
        var total = new IoRow
        {
            How = "[合计]",
            销售数 = rows2.Sum(r => r.销售数), 销售成本 = rows2.Sum(r => r.销售成本), 销售额 = rows2.Sum(r => r.销售额),
            进库数 = rows2.Sum(r => r.进库数), 进库额 = rows2.Sum(r => r.进库额), 库存数 = rows2.Sum(r => r.库存数),
            毛利 = rows2.Sum(r => r.毛利),
            毛利率 = rows2.Sum(r => r.销售成本) == 0 ? "" : (rows2.Sum(r => r.毛利) / rows2.Sum(r => r.销售成本) * 100).ToString("0.00") + "%",
            预售数 = rows2.Sum(r => r.预售数), 预售成本 = rows2.Sum(r => r.预售成本), 预售额 = rows2.Sum(r => r.预售额),
            Cp = "",
        };
        rows2.Add(total);
        // 最终输出：order by cp（合计行 cp='' 排最前）
        return rows2.OrderBy(r => r.Cp).ToList();
    }

    // ---------- E7 月销售数（fr_sertmo row54） ----------

    public async Task<IReadOnlyList<RtmoRow>> SearchRtmoAsync(string where, string dpm, string dpmusem, int months, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var (whereSql, p) = await BuildWhereAsync(conn, "tbrtsamo", where, dpm, dpmusem, null, ct);
        var sql = $"""
            SELECT id, rtrim(dpm) dpm, rtrim(cp) cp, rtrim(nno) nno, rtrim(na1) na1, rtrim(ty) ty, rtrim(unit) unit,
                   oamou1, oamou2, oamou3, oamou4, oamou5, oamou6, oamou7, oamou8, oamou9, oamou10, oamou11, oamou12,
                   (oamou1 + oamou2 + oamou3 + oamou4 + oamou5 + oamou6 + oamou7 + oamou8 + oamou9 + oamou10 + oamou11 + oamou12) / @months AS 月均销售数
            FROM tbrtsamo WITH (NOLOCK)
            WHERE {(whereSql.Length == 0 ? "1=1" : whereSql)}
            ORDER BY dpm, nno
            """;
        p.Add("months", months);
        return (await conn.QueryAsync<RtmoRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
    }

    // ---------- E8 营业额（fr_sesada row33） ----------

    public async Task<IReadOnlyList<SesadaRow>> SearchSesadaAsync(DateTime d1, DateTime d2, string? cldpm, string dpm, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // P_FGS=.T. 分支（契约 §8）：dpm 列 = 部门名（dpmbh → dpm 翻译），GROUP BY outdate+部门名
        var dpmExpr = cldpm is "*"
            ? "(select dpm from tbdpm where dpmbh = tbsada.dpm)"
            : "(select top 1 dpm from tbdpm where dpmbh = @cldpm)";
        var userWhere = "(type='销售'or type='历史') and outdate>=@d1 and outdate<@d2" + (cldpm is "*" or null ? "" : " and dpm=@cldpm");
        var (whereSql, p) = await BuildWhereAsync(conn, "tbsada", userWhere, dpm, dpmusem, null, ct);
        p.Add("d1", d1.Date);
        p.Add("d2", d2.Date.AddDays(1));
        if (cldpm is not "*" and not null)
            p.Add("cldpm", new DbString { Value = cldpm, IsAnsi = true, Length = 10 });
        var sql = $"""
            SELECT CONVERT(varchar(10), outdate, 102) + '' AS 日期, dpmname AS Dpm, COUNT(DISTINCT paper) AS 单数,
                   SUM(ckamount) AS 出库数量,
                   ROUND(SUM(ckamount * iprj), 2) AS 日元, ROUND(SUM(ckamount * iprc), 2) AS 人进价,
                   ROUND(SUM(ckamount * oprc * (1 - yhl / 100)), 2) AS 售额,
                   ROUND(SUM(ckamount * oprct * (1 - yhl / 100)), 2) AS 实售额,
                   ROUND(SUM(ckamount * oprct * (1 - yhl / 100)), 2) - ROUND(SUM(ckamount * iprj), 2) AS ML
            FROM (SELECT outdate, RTRIM({dpmExpr}) AS dpmname, paper, ckamount, iprj, iprc, oprct, yhl, oprc FROM tbsada WITH (NOLOCK) WHERE {whereSql}) x
            GROUP BY outdate, dpmname
            ORDER BY outdate, dpmname
            """;
        var rows = (await conn.QueryAsync<SesadaRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
        foreach (var r in rows)
        {
            r.日期 = r.日期.Replace('.', '.'); // CONVERT 102 = yyyy.mm.dd（SET DATE ANSI 同形）
            r.牟利率 = r.实售额 != 0 ? Math.Round(r.ML / r.实售额 * 100, 2) : 0;
        }
        // 合计行（旧码 INSERT '[合计]'：SUM 各列 + 牟利率=N7/N6*100，销售成本=0 时毛利率清 ''）
        var total = new SesadaRow
        {
            日期 = "[合计]",
            单数 = rows.Sum(r => r.单数),
            出库数量 = rows.Sum(r => r.出库数量),
            日元 = rows.Sum(r => r.日元),
            人进价 = rows.Sum(r => r.人进价),
            售额 = rows.Sum(r => r.售额),
            实售额 = rows.Sum(r => r.实售额),
            ML = rows.Sum(r => r.ML),
            IsTotal = true,
        };
        total.牟利率 = total.实售额 != 0 ? total.ML / total.实售额 * 100 : 0;
        rows.Add(total);
        return rows;
    }

    // ---------- E9 库存检测（fr_secheck row27） ----------

    public async Task<StockCheckResult> CheckStockAsync(DateTime date, string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var result = new StockCheckResult();
        var pSav = new DynamicParameters();
        var (savWhere, p) = await BuildWhereAsync(conn, "tbistosav", "back_date=@mdate and cp=@cp and dpm=@dpm", dpm, dpmusem, null, ct);
        p.Add("mdate", date.Date);
        p.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        p.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
        result.SavA = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            $"SELECT SUM(kcamount) FROM tbistosav WITH (NOLOCK) WHERE {savWhere}", p, cancellationToken: ct)) ?? 0m;
        result.HasBackup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM tbistosav WITH (NOLOCK) WHERE {savWhere}) THEN 1 ELSE 0 END", p, cancellationToken: ct)) > 0;
        if (!result.HasBackup)
            return result;

        result.IstoA = await SumAsync(conn, "SELECT SUM(kcamount) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and dpm=@dpm", cp, dpm, ct);
        result.SaedA = await SumAsync(conn, "SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='历史' and outdate>=@mdate and cp=@cp and dpm=@dpm", cp, dpm, ct, date);
        result.SadaAll = await SumAsync(conn, "SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='销售' and outdate>=@mdate and cp=@cp and dpm=@dpm", cp, dpm, ct, date);
        result.SadaA = await SumAsync(conn, """
            SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK)
            WHERE type='销售' and outdate>=@mdate and cp=@cp and dpm=@dpm
              and (left(note,5)!='预售转销售' or (left(note,5)='预售转销售' and convert(datetime,SUBSTRING(note,6,10))>=@mdate))
            """, cp, dpm, ct, date);
        result.SabeA = await SumAsync(conn, "SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='预售' and outdate>=@mdate and cp=@cp and dpm=@dpm", cp, dpm, ct, date);
        result.SadbA = await SumAsync(conn, "SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='调拨' and outdate>=@mdate and cp=@cp and dpm=@dpm", cp, dpm, ct, date);
        result.XlsaA = await SumAsync(conn, "SELECT SUM(ckamount) FROM tbxlsa WITH (NOLOCK) WHERE type='领料' and outdate>=@mdate and cp=@cp and dpm=@dpm", cp, dpm, ct, date);
        result.IstochA = await SumAsync(conn, "SELECT SUM(xgamount) FROM tbistoch WITH (NOLOCK) WHERE xgdate>=@mdate and cp=@cp and dpm=@dpm", cp, dpm, ct, date);
        result.SaxgA = await SumAsync(conn, "SELECT SUM(ckamount) FROM tbsaxg WITH (NOLOCK) WHERE editdate>=@mdate and outdate<@mdate and cp=@cp and dpm=@dpm", cp, dpm, ct, date);
        result.IstoedA = await SumAsync(conn, "SELECT SUM(jkamount) FROM tbistoed WITH (NOLOCK) WHERE indate>=@mdate and cp=@cp and dpm=@dpm", cp, dpm, ct, date);
        result.NowA = result.SavA + result.IstoedA - result.SaedA - result.SadaA - result.SabeA - result.SadbA - result.XlsaA + result.IstochA - result.SaxgA;
        result.DiffA = result.IstoA - result.NowA;

        // 逐品差异（旧码 SUM REST WHILE 语义 = GROUP BY nno；每 tbisto 批次行输出一条，序=tbisto id 序）
        var istoRows = (await conn.QueryAsync<(long Id, string Nno, string Na1, string Ty, decimal Kc)>(
            new CommandDefinition("""
                SELECT id, rtrim(nno), rtrim(na1), rtrim(ty), kcamount FROM tbisto WITH (NOLOCK)
                WHERE cp=@cp and dpm=@dpm ORDER BY id
                """, new { cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } },
                cancellationToken: ct))).AsList();
        var savByNno = (await conn.QueryAsync<(string Nno, decimal S)>(
            new CommandDefinition($"SELECT rtrim(nno), SUM(kcamount) FROM tbistosav WITH (NOLOCK) WHERE {savWhere} GROUP BY rtrim(nno)", p, cancellationToken: ct)))
            .ToDictionary(x => x.Nno, x => x.S);
        var saedByNno = await GroupNno(conn, "SELECT rtrim(nno), SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='历史' and outdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)", cp, dpm, date, ct);
        var sadaByNno = await GroupNno(conn, """
            SELECT rtrim(nno), SUM(ckamount) FROM tbsada WITH (NOLOCK)
            WHERE type='销售' and outdate>=@mdate and cp=@cp and dpm=@dpm
              and (left(note,5)!='预售转销售' or (left(note,5)='预售转销售' and convert(datetime,SUBSTRING(note,6,10))>=@mdate))
            GROUP BY rtrim(nno)
            """, cp, dpm, date, ct);
        var sabeByNno = await GroupNno(conn, "SELECT rtrim(nno), SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='预售' and outdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)", cp, dpm, date, ct);
        var sadbByNno = await GroupNno(conn, "SELECT rtrim(nno), SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='调拨' and outdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)", cp, dpm, date, ct);
        var xlsaByNno = await GroupNno(conn, "SELECT rtrim(nno), SUM(ckamount) FROM tbxlsa WITH (NOLOCK) WHERE type='领料' and outdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)", cp, dpm, date, ct);
        var istochByNno = await GroupNno(conn, "SELECT rtrim(nno), SUM(xgamount) FROM tbistoch WITH (NOLOCK) WHERE xgdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)", cp, dpm, date, ct);
        var saxgByNno = await GroupNno(conn, "SELECT rtrim(nno), SUM(ckamount) FROM tbsaxg WITH (NOLOCK) WHERE editdate>=@mdate and outdate<@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)", cp, dpm, date, ct);
        var istoedByNno = await GroupNno(conn, "SELECT rtrim(nno), SUM(jkamount) FROM tbistoed WITH (NOLOCK) WHERE indate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)", cp, dpm, date, ct);
        var istoByNno = istoRows.GroupBy(r => r.Nno).ToDictionary(g => g.Key, g => g.Sum(x => x.Kc));

        var list = new List<StockCheckRow>();
        foreach (var r in istoRows)
        {
            var savM = savByNno.GetValueOrDefault(r.Nno);
            var istodM = istoedByNno.GetValueOrDefault(r.Nno);
            var saedM = saedByNno.GetValueOrDefault(r.Nno);
            var sadaM = sadaByNno.GetValueOrDefault(r.Nno);
            var sabeM = sabeByNno.GetValueOrDefault(r.Nno);
            var sadbM = sadbByNno.GetValueOrDefault(r.Nno);
            var xlsaM = xlsaByNno.GetValueOrDefault(r.Nno);
            var istochM = istochByNno.GetValueOrDefault(r.Nno);
            var saxgM = saxgByNno.GetValueOrDefault(r.Nno);
            var istoM = istoByNno.GetValueOrDefault(r.Nno);
            var nowM = savM + istodM - saedM - sadaM - sabeM - sadbM - xlsaM + istochM - saxgM;
            if (nowM != istoM)
            {
                list.Add(new StockCheckRow
                {
                    Nno = r.Nno, Cp = cp, Na1 = r.Na1, Ty = r.Ty,
                    Sav = savM,
                    Outs = sadaM + saedM + sabeM + sadbM + xlsaM,
                    In = istodM, Istoch = istochM, Saxg = saxgM, Isto = istoM,
                });
            }
        }
        result.Rows = list;
        return result;
    }

    private static async Task<decimal> SumAsync(SqlConnection conn, string sql, string cp, string dpm, CancellationToken ct, DateTime? date = null)
    {
        var p = new DynamicParameters();
        p.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        p.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
        if (date.HasValue) p.Add("mdate", date.Value.Date);
        return await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(sql, p, cancellationToken: ct)) ?? 0m;
    }

    private static async Task<Dictionary<string, decimal>> GroupNno(SqlConnection conn, string sql, string cp, string dpm, DateTime date, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<(string Nno, decimal S)>(new CommandDefinition(sql,
            new { cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 }, mdate = date.Date },
            cancellationToken: ct));
        return rows.ToDictionary(x => x.Nno, x => x.S);
    }

    // ---------- EA 进出流水帐（fr_seis row51） ----------

    public async Task<IReadOnlyList<IsiosmRow>> SearchIsiosmAsync(string nno, DateTime d1, DateTime d2, string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var d2End = d2.Date.AddDays(1);
        // 品校验 + 期初结存（D2+1 > 今天 = 当前库存；否则区间内最后备份日）
        var (istoWhere, istoP) = await BuildWhereAsync(conn, "tbisto", "cp=@cp and nno=@nno And Dpm = @dpm", dpm, dpmusem, cp, ct);
        istoP.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        istoP.Add("nno", new DbString { Value = nno, IsAnsi = true, Length = 18 });
        istoP.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
        var isto = (await conn.QueryAsync<(string Na1, string Ty)>(new CommandDefinition(
            $"SELECT rtrim(na1), rtrim(ty) FROM tbisto WITH (NOLOCK) WHERE {istoWhere}", istoP, cancellationToken: ct))).AsList();
        if (isto.Count == 0)
            return Array.Empty<IsiosmRow>();

        decimal amount, iprcAmt, iprjAmt;
        if (d2End > DateTime.Today)
        {
            amount = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(kcamount) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and nno=@nno And Dpm = @dpm", istoP, cancellationToken: ct)) ?? 0m;
            iprcAmt = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(iprc * kcamount) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and nno=@nno And Dpm = @dpm", istoP, cancellationToken: ct)) ?? 0m;
            iprjAmt = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(iprj * kcamount) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and nno=@nno And Dpm = @dpm", istoP, cancellationToken: ct)) ?? 0m;
        }
        else
        {
            var (savWhere, savP) = await BuildWhereAsync(conn, "tbistosav",
                "cp=@cp and nno=@nno And Dpm = @dpm and back_date=(select top 1 back_date from tbistosav where back_date>=@d1 and back_date<=@d2 order by back_date desc)", dpm, dpmusem, cp, ct);
            savP.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
            savP.Add("nno", new DbString { Value = nno, IsAnsi = true, Length = 18 });
            savP.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
            savP.Add("d1", d1.Date);
            savP.Add("d2", d2End);
            amount = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(kcamount) FROM tbistosav WITH (NOLOCK) WHERE " + savWhere, savP, cancellationToken: ct)) ?? 0m;
            iprcAmt = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(iprc * kcamount) FROM tbistosav WITH (NOLOCK) WHERE " + savWhere, savP, cancellationToken: ct)) ?? 0m;
            iprjAmt = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(iprj * kcamount) FROM tbistosav WITH (NOLOCK) WHERE " + savWhere, savP, cancellationToken: ct)) ?? 0m;
        }

        var rows = new List<IsiosmRow>
        {
            new() { Date = d1.Date, Mark = "结存", Amount = amount },
        };
        // 进库（tbistoed）
        var (inWhere, inP) = await BuildWhereAsync(conn, "tbistoed", "cp=@cp and nno=@nno and indate>=@d1 and indate<@d2 And Dpm = @dpm", dpm, dpmusem, cp, ct);
        inP.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        inP.Add("nno", new DbString { Value = nno, IsAnsi = true, Length = 18 });
        inP.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
        inP.Add("d1", d1.Date);
        inP.Add("d2", d2End);
        foreach (var r in await conn.QueryAsync<(decimal Jk, DateTime D, string Per, string Cno, string Gyn, string Posi, decimal Iprc, decimal Iprj, string Dep)>(
            new CommandDefinition($"""
                SELECT jkamount, indate, rtrim(per), rtrim(cno), rtrim(gyname), rtrim(posi), iprc, iprj, rtrim(depotno), id
                FROM tbistoed WITH (NOLOCK) WHERE {inWhere} ORDER BY id
                """, inP, cancellationToken: ct)))
            rows.Add(new IsiosmRow
            {
                Jqty = r.Jk, Date = r.D, Per = r.Per, No = r.Cno, Mark = "进库", Note = r.Gyn,
                Posi = r.Posi, Iprc = r.Iprc, Iprj = r.Iprj, Depotno = r.Dep,
            });
        // 修改（tbistoch，IPRJ=0）
        var (chgWhere, chgP) = await BuildWhereAsync(conn, "tbistoch", "cp=@cp and nno=@nno and xgdate>=@d1 and xgdate<@d2 And Dpm = @dpm", dpm, dpmusem, cp, ct);
        chgP.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        chgP.Add("nno", new DbString { Value = nno, IsAnsi = true, Length = 18 });
        chgP.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
        chgP.Add("d1", d1.Date);
        chgP.Add("d2", d2End);
        foreach (var r in await conn.QueryAsync<(decimal Xg, DateTime D, string Per, string Posi, decimal Iprc)>(
            new CommandDefinition($"""
                SELECT xgamount, xgdate, rtrim(per), rtrim(posi), iprc, id FROM tbistoch WITH (NOLOCK) WHERE {chgWhere} ORDER BY id
                """, chgP, cancellationToken: ct)))
            rows.Add(new IsiosmRow
            {
                Jqty = r.Xg, Date = r.D, Per = r.Per, No = r.Posi, Mark = "修改",
                Posi = r.Posi, Iprc = r.Iprc, Iprj = 0,
            });
        // 销售/历史/预售/调拨（tbsada）
        var (sadaWhere, sadaP) = await BuildWhereAsync(conn, "tbsada",
            "cp=@cp and nno=@nno and outdate>=@d1 and outdate<@d2 and type in ('销售','历史','预售','调拨') And Dpm = @dpm", dpm, dpmusem, cp, ct);
        sadaP.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        sadaP.Add("nno", new DbString { Value = nno, IsAnsi = true, Length = 18 });
        sadaP.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
        sadaP.Add("d1", d1.Date);
        sadaP.Add("d2", d2End);
        foreach (var r in await conn.QueryAsync<(decimal Ck, DateTime D, string Per, string Paper, string Type, string Gn, string Posi, decimal Iprc, decimal Iprj, string Dep)>(
            new CommandDefinition($"""
                SELECT ckamount, outdate, rtrim(per), rtrim(paper), rtrim(type), rtrim(gname), rtrim(posi), iprc, iprj, rtrim(depotno), id
                FROM tbsada WITH (NOLOCK) WHERE {sadaWhere} ORDER BY id
                """, sadaP, cancellationToken: ct)))
        {
            var mark = r.Ck < 0 ? "退" : r.Type.Length > 4 ? r.Type[..4] : r.Type;
            rows.Add(new IsiosmRow
            {
                Cqty = r.Ck, Date = r.D, Per = r.Per, No = r.Paper, Mark = mark, Note = r.Gn,
                Posi = r.Posi, Iprc = r.Iprc, Iprj = r.Iprj, Depotno = r.Dep,
            });
        }
        // 领料（tbxlsa，type<>'维修报价'）
        var (xlWhere, xlP) = await BuildWhereAsync(conn, "tbxlsa",
            "cp=@cp and nno=@nno and outdate>=@d1 and outdate<@d2 and type<>'维修报价' And Dpm = @dpm", dpm, dpmusem, cp, ct);
        xlP.Add("cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        xlP.Add("nno", new DbString { Value = nno, IsAnsi = true, Length = 18 });
        xlP.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
        xlP.Add("d1", d1.Date);
        xlP.Add("d2", d2End);
        foreach (var r in await conn.QueryAsync<(decimal Ck, DateTime D, string Per, string Paper, string Type, string Gn, string Bz, string Gh, string Posi, decimal Iprc, decimal Iprj, string Dep)>(
            new CommandDefinition($"""
                SELECT ckamount, outdate, rtrim(per), rtrim(paper), rtrim(type), rtrim(gname), rtrim(bz), rtrim(gh), rtrim(posi), iprc, iprj, rtrim(depotno), id
                FROM tbxlsa WITH (NOLOCK) WHERE {xlWhere} ORDER BY id
                """, xlP, cancellationToken: ct)))
        {
            var mark = r.Ck < 0 ? "退" : r.Type.Length > 4 ? r.Type[..4] : r.Type;
            rows.Add(new IsiosmRow
            {
                Cqty = r.Ck, Date = r.D, Per = r.Per, No = r.Paper, Mark = mark,
                Note = $"{r.Gn},{r.Bz},{r.Gh}", Posi = r.Posi, Iprc = r.Iprc, Iprj = r.Iprj, Depotno = r.Dep,
            });
        }

        // 倒序滚动（row51 L79-89）：AMOUNT=O_N、结存金额=M_IPRC、外币价金额=M_IPRJ；随后 O_N-=JQTY-CQTY、M_IPRJ/M_IPRC 同步
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            row.Amount = amount;
            row.结存金额 = iprcAmt;
            row.外币价金额 = iprjAmt;
            amount = amount - row.Jqty + row.Cqty;
            iprjAmt = iprjAmt - row.Jqty * (row.Iprj ?? 0) + row.Cqty * (row.Iprj ?? 0);
            iprcAmt = iprcAmt - row.Jqty * (row.Iprc ?? 0) + row.Cqty * (row.Iprc ?? 0);
        }
        // 期初结存行 JQTY 置 0（旧码 GO TOP 后 REPLACE JQTY WITH 0——滚动时首行已用 Amount 初值，不重复扣减）
        rows[0].Jqty = 0;
        return rows;
    }

    // ---------- fr_mdefi 模式② / fr_total 打印辅助（第十二批 E 组备案补齐） ----------

    public async Task<IReadOnlyList<SysTableInfo>> GetSysTablesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // FrmSTable.Init：DIST TABLE_NAME,TABLE_CNAM（CNAM 非空）且该表 FIELD_NAME 集合同时含 CP 与 NNO
        var tables = (await conn.QueryAsync<(string TableName, string Cname)>(new CommandDefinition("""
            SELECT DISTINCT rtrim(table_name), rtrim(table_cnam) FROM tbstru WITH (NOLOCK)
            WHERE rtrim(table_cnam) <> ''
              AND rtrim(table_name) IN (
                  SELECT rtrim(a.table_name) FROM tbstru a WITH (NOLOCK)
                  INNER JOIN tbstru b WITH (NOLOCK) ON rtrim(a.table_name) = rtrim(b.table_name)
                  WHERE rtrim(a.field_name) = 'CP' AND rtrim(b.field_name) = 'NNO')
            """, cancellationToken: ct))).AsList();
        return tables.Select(t => new SysTableInfo(t.TableName, t.Cname)).ToList();
    }

    public async Task<IReadOnlyList<SysFieldInfo>> GetSysFieldsAsync(string table, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(string TableName, string FieldName, string Explain, string FieldType)>(
            new CommandDefinition(
                "SELECT rtrim(table_name), rtrim(field_name), rtrim(explain), rtrim(field_type) FROM tbstru WITH (NOLOCK) WHERE table_name = @t",
                new { t = new DbString { Value = table, IsAnsi = true, Length = 100 } },
                cancellationToken: ct))).AsList();
        return rows.Select(r => new SysFieldInfo(r.TableName, r.FieldName, r.Explain,
            string.IsNullOrEmpty(r.FieldType) ? 'C' : char.ToUpperInvariant(r.FieldType[0]))).ToList();
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AggregateSysTableAsync(
        string table, IReadOnlyList<AggregateItem> items, bool hasCpKey, bool hasNnoKey, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 旧 m_fieldname/m_group 拼串（FrmAccount.CmdFinish）：cp/nno 在前、聚合列随后；
        // 任一"原值"（M_CG=.F.）→ 无 GROUP BY；否则 GROUP BY cp,nno（M_CPNNO=1 时仅 nno）。
        // 无 WHERE、无 tbxzb（PASSTH select 直查物理表），照抄。
        var keyCols = hasCpKey ? "cp, nno" : "nno";
        var selectCols = keyCols + ", " + string.Join(", ", items.Select(i => $"{i.AggExpression} as {i.NewName}"));
        var groupBy = hasCpKey ? "group by cp, nno" : "group by nno";
        var sql = items.Any(i => i.Method == 1)
            ? $"SELECT {selectCols} FROM {table}"
            : $"SELECT {selectCols} FROM {table} {groupBy}";
        var rows = (await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct))).AsList();
        var result = new List<IReadOnlyDictionary<string, object?>>();
        foreach (IDictionary<string, object> row in rows)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in row) dict[kv.Key] = kv.Value;
            result.Add(dict);
        }
        return result;
    }

    public async Task<string> GetCompanyNameAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT valuec FROM tbsysset WITH (NOLOCK) WHERE type='system' and name=@n",
            new { n = new DbString { Value = "公司名称", IsAnsi = false, Length = 60 } },
            cancellationToken: ct)) ?? "";
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDpmNamesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string Dpmbh, string Dpm)>(new CommandDefinition(
            "SELECT rtrim(dpmbh), rtrim(dpm) FROM tbdpm WITH (NOLOCK)", cancellationToken: ct));
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows) dict.TryAdd(r.Dpmbh, r.Dpm);
        return dict;
    }
}
