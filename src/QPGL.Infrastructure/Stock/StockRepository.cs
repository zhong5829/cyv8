using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Stock;

public interface IStockRepository
{
    /// <summary>库存查询（E3，分页 + 排序白名单）。</summary>
    Task<PagedResult<StockRow>> SearchAsync(StockQueryRequest request, string dept, CancellationToken ct = default);

    /// <summary>库存汇总（旧 _-.row35：SUM kcamount, iprj*kc, iprc*kc, oprc*kc）。</summary>
    Task<StockSummary> GetSummaryAsync(StockQueryRequest request, string dept, CancellationToken ct = default);

    /// <summary>仓库清单（旧莡.row75：tbsysset WHERE type='仓库'）。</summary>
    Task<IReadOnlyList<string>> GetWarehousesAsync(CancellationToken ct = default);

    /// <summary>tbsysset 通用字典（type：'发货方式'/'销售类型' 等，整单确认弹窗下拉）。</summary>
    Task<IReadOnlyList<string>> GetSyssetNamesAsync(string type, CancellationToken ct = default);

    /// <summary>单据类型/销售类型清单（tbsysset type='system' name='papertype' 各行 name）。</summary>
    Task<IReadOnlyList<string>> GetPaperTypesAsync(CancellationToken ct = default);
}

/// <summary>
/// 库存查询仓储（M4-2，只读 NOLOCK；2026-11-09 对齐 fr_seisto：tbistojj 警戒线 JOIN + 默认排序 nno；
/// 高级条件 AdvWhere 已按用户要求移除）。
/// 旧实现出处：fr_istoch row3 查询 tbisto 全列；fr_seisto row38 查询关联 tbistojj/tbprnoty/tbcomplex；
/// 仓库下拉 tbsysset type='仓库'；汇总=SUM kcamount, iprj*kcamount, iprc*kcamount, oprc*kcamount（fr_seisto MR.row41）。
/// 部门过滤：PASSTH 自动追加 tbxzb.variable（tbisto=?p_dpmusem like '%'+rtrim(dpm)+'%'），
/// 单门店等价 dpm=@dept；多部门时按会话权限部门集扩展（一期留 TODO）。
/// </summary>
public sealed class StockRepository(SqlConnectionFactory factory) : IStockRepository
{
    private const string Columns = """
        a.id, RTRIM(a.dpm) dpm, RTRIM(a.cp) cp, RTRIM(a.nno) nno, RTRIM(a.na1) na1, RTRIM(a.ty) ty,
        RTRIM(a.fa) fa, RTRIM(a.dc) dc, RTRIM(a.unit) unit, a.kcamount, a.iprj, a.iprc, a.oprc, a.oprb,
        RTRIM(a.posi) posi, a.kcbhdate, RTRIM(a.note) note, a.xno, a.tno, RTRIM(a.cxnnO) cxnno, RTRIM(a.depotno) depotno,
        ISNULL(s.amjj, 0) amjj, ISNULL(s.bmjj, 0) bmjj
        """;

    public async Task<PagedResult<StockRow>> SearchAsync(StockQueryRequest request, string dept, CancellationToken ct = default)
    {
        var (where, joins, p) = BuildWhere(request, dept);
        var hint = HasPinyinJoin(joins) ? "\nOPTION (HASH JOIN)" : "";
        var orderBy = StockSortFields.TryResolve(request.Sort, out var col)
            ? $"a.{col} {(request.IsDescending ? "DESC" : "ASC")}, a.id ASC"
            : "a.nno ASC, a.id ASC";

        await using var conn = await factory.OpenAsync(ct);
        var sql = $"""
            SELECT {Columns}
            FROM tbisto a WITH (NOLOCK){joins}
            WHERE {where}
            ORDER BY {orderBy}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY{hint};
            SELECT COUNT(1) FROM tbisto a WITH (NOLOCK){joins} WHERE {where}{hint};
            """;
        p.Add("skip", (request.Page - 1) * request.PageSize);
        p.Add("take", request.PageSize);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = (await multi.ReadAsync<StockRow>()).AsList();
        var total = await multi.ReadFirstAsync<int>();
        return new PagedResult<StockRow> { Items = rows, Total = total, Page = request.Page, PageSize = request.PageSize };
    }

    public async Task<StockSummary> GetSummaryAsync(StockQueryRequest request, string dept, CancellationToken ct = default)
    {
        var (where, joins, p) = BuildWhere(request, dept);
        var hint = HasPinyinJoin(joins) ? "\nOPTION (HASH JOIN)" : "";
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(decimal kc, decimal iprj, decimal iprc, decimal oprc)>(
            new CommandDefinition(
                $"""
                SELECT SUM(a.kcamount), SUM(a.iprj * a.kcamount), SUM(a.iprc * a.kcamount), SUM(a.oprc * a.kcamount)
                FROM tbisto a WITH (NOLOCK){joins} WHERE {where}{hint}
                """,
                p, cancellationToken: ct));
        return new StockSummary
        {
            TotalKcamount = row.kc,
            TotalIprjAmount = row.iprj,
            TotalIprcAmount = row.iprc,
            TotalOprcAmount = row.oprc,
        };
    }

    public async Task<IReadOnlyList<string>> GetWarehousesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = N'仓库' ORDER BY id",
            cancellationToken: ct);
        return (await conn.QueryAsync<string>(cmd)).AsList();
    }

    public async Task<IReadOnlyList<string>> GetSyssetNamesAsync(string type, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = @type ORDER BY id",
            new { type = new DbString { Value = type.Trim(), IsAnsi = false, Length = 20 } },
            cancellationToken: ct);
        return (await conn.QueryAsync<string>(cmd)).AsList();
    }

    /// <summary>单据类型/销售类型清单（tbsysset type='system' name='papertype' 各行 valuec：门店/仓库；旧 P__.row73 CBOPAPERTYPE）。</summary>
    public async Task<IReadOnlyList<string>> GetPaperTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            "SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE type = N'system' AND name = N'papertype' AND ISNULL(valuec, '') <> '' ORDER BY id",
            cancellationToken: ct);
        return (await conn.QueryAsync<string>(cmd)).AsList();
    }

    /// <summary>拼音派生表（tbname0/tbty0）在场判定——HASH JOIN 提示必须套用（PartRepository 实测：无提示嵌套循环 18s+）。</summary>
    private static bool HasPinyinJoin(string joins) =>
        joins.Contains("tbty0", StringComparison.Ordinal) || joins.Contains("tbname0", StringComparison.Ordinal);

    /// <summary>查询条件拼装（2026-10-30 车型拼音码：ty 条件扩展 tbty0 拼音匹配；2026-11-09 对齐 fr_seisto：
    /// 列名别名化 a. + LEFT JOIN tbistojj s（cp+nno+depotno=''，部门非空加 s.dpm）取警戒上下限 + 名称 tbname0 拼音码）。
    /// 返回 (where, joins, p)：joins 为 tbty0/tbname0 派生表 / tbistojj 的 JOIN 片段（无条件时也可能含 tbistojj），
    /// 调用方须把它拼进 FROM——含拼音派生表时必须配 OPTION (HASH JOIN)（实测无提示 IN/JOIN 优化器误选嵌套循环 30s+/超时）。
    /// 派生表列别名 pymatch/name（非裸列），避免与 tbisto 列在 SELECT/ORDER BY 中歧义（SQL 2515）。</summary>
    private static (string Where, string Joins, DynamicParameters P) BuildWhere(StockQueryRequest request, string dept)
    {
        var where = "a.dpm = @dpm";
        // fr_seisto row38 L160：tbisto a left join tbistojj s on cp+nno+depotno='' [+ and s.dpm=a.dpm 当 P_DPM 非空]
        var joins = " LEFT JOIN tbistojj s ON s.cp = a.cp AND s.nno = a.nno AND s.depotno = ''";
        if (dept.Trim().Length > 0)
            joins += " AND s.dpm = @dpm";
        var p = new DynamicParameters();
        p.Add("dpm", new DbString { Value = dept.Trim(), IsAnsi = true, Length = 10 });
        if (!string.IsNullOrWhiteSpace(request.Cp))
        {
            where += " AND a.cp LIKE @cp";
            p.Add("cp", $"{request.Cp.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Nno))
        {
            // 零件号框双模式（2026-10-30 需求，对齐开单页编码框）：首字符 / = 包含；否则前缀
            var kw = request.Nno.Trim();
            where += " AND a.nno LIKE @nno";
            p.Add("nno", kw.StartsWith("/") ? $"%{kw[1..].Trim()}%" : $"{kw}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Na1))
        {
            // 名称 = 中文包含 + 拼音码包含（tbname0 派生表半连接，照 PartRepository.SearchAsync 同款 SQL——
            // HASH JOIN 提示下派生表 JOIN 计划稳定，OR nb.name IS NOT NULL 与 IN 语义一致）
            joins += "\nLEFT JOIN (SELECT DISTINCT rtrim(name) AS name FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%') nb ON rtrim(a.na1) = nb.name";
            where += " AND (a.na1 LIKE '%' + @na1 + '%' OR nb.name IS NOT NULL)";
            p.Add("na1", request.Na1.Trim());
        }
        if (!string.IsNullOrWhiteSpace(request.Ty))
        {
            joins += "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS pymatch FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @ty + '%') tb2 ON a.ty = tb2.pymatch";
            where += " AND (a.ty LIKE @ty OR tb2.pymatch IS NOT NULL)";
            p.Add("ty", $"%{request.Ty.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Posi))
        {
            where += " AND a.posi LIKE @posi";
            p.Add("posi", $"{request.Posi.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Depotno))
        {
            where += " AND a.depotno = @depotno";
            p.Add("depotno", new DbString { Value = request.Depotno.Trim(), IsAnsi = true, Length = 10 });
        }
        if (request.OnlyInStock)
            where += " AND a.kcamount <> 0";
        return (where, joins, p);
    }
}
