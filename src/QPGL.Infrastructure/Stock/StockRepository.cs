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
/// 库存查询仓储（M4-2，只读 NOLOCK）。
/// 旧实现出处：fr_istoch row3 查询 tbisto 全列；仓库下拉 tbsysset type='仓库'。
/// 部门过滤：PASSTH 自动追加 tbxzb.variable（tbisto=?p_dpmusem like '%'+rtrim(dpm)+'%'），
/// 单门店等价 dpm=@dept；多部门时按会话权限部门集扩展（一期留 TODO）。
/// </summary>
public sealed class StockRepository(SqlConnectionFactory factory) : IStockRepository
{
    private const string Columns = """
        id, RTRIM(dpm) dpm, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty,
        RTRIM(fa) fa, RTRIM(dc) dc, RTRIM(unit) unit, kcamount, iprj, iprc, oprc, oprb,
        RTRIM(posi) posi, kcbhdate, RTRIM(note) note, xno, tno, RTRIM(cxnnO) cxnno, RTRIM(depotno) depotno
        """;

    public async Task<PagedResult<StockRow>> SearchAsync(StockQueryRequest request, string dept, CancellationToken ct = default)
    {
        var (where, tyJoin, p) = BuildWhere(request, dept);
        var hint = tyJoin.Length > 0 ? "\nOPTION (HASH JOIN)" : "";
        var orderBy = StockSortFields.TryResolve(request.Sort, out var col)
            ? $"{col} {(request.IsDescending ? "DESC" : "ASC")}, id ASC"
            : "depotno ASC, nno ASC, id ASC";

        await using var conn = await factory.OpenAsync(ct);
        var sql = $"""
            SELECT {Columns}
            FROM tbisto WITH (NOLOCK){tyJoin}
            WHERE {where}
            ORDER BY {orderBy}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY{hint};
            SELECT COUNT(1) FROM tbisto WITH (NOLOCK){tyJoin} WHERE {where}{hint};
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
        var (where, tyJoin, p) = BuildWhere(request, dept);
        var hint = tyJoin.Length > 0 ? "\nOPTION (HASH JOIN)" : "";
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(decimal kc, decimal iprj, decimal iprc, decimal oprc)>(
            new CommandDefinition(
                $"""
                SELECT SUM(kcamount), SUM(iprj * kcamount), SUM(iprc * kcamount), SUM(oprc * kcamount)
                FROM tbisto WITH (NOLOCK){tyJoin} WHERE {where}{hint}
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

    /// <summary>查询条件拼装（2026-10-30 车型拼音码：ty 条件扩展 tbty0 拼音匹配）。
    /// 返回 (where, tyJoin, p)：tyJoin 为车型拼音 LEFT JOIN 派生表（仅 ty 条件在场时非空），
    /// 调用方须把它拼进 FROM——必须配 OPTION (HASH JOIN)（实测无提示 IN/JOIN 优化器误选嵌套循环 30s+/超时）。
    /// 派生表列别名 pymatch（非 ty），避免与 tbisto.ty 在 SELECT/ORDER BY 中歧义（SQL 2515）。</summary>
    private static (string Where, string TyJoin, DynamicParameters P) BuildWhere(StockQueryRequest request, string dept)
    {
        var where = "dpm = @dpm";
        var tyJoin = "";
        var p = new DynamicParameters();
        p.Add("dpm", new DbString { Value = dept.Trim(), IsAnsi = true, Length = 10 });
        if (!string.IsNullOrWhiteSpace(request.Cp))
        {
            where += " AND cp LIKE @cp";
            p.Add("cp", $"{request.Cp.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Nno))
        {
            // 零件号框双模式（2026-10-30 需求，对齐开单页编码框）：首字符 / = 包含；否则前缀
            var kw = request.Nno.Trim();
            where += " AND nno LIKE @nno";
            p.Add("nno", kw.StartsWith("/") ? $"%{kw[1..].Trim()}%" : $"{kw}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Na1))
        {
            where += " AND na1 LIKE @na1";
            p.Add("na1", $"%{request.Na1.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Ty))
        {
            tyJoin = "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS pymatch FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @ty + '%') tb2 ON ty = tb2.pymatch";
            where += " AND (ty LIKE @ty OR tb2.pymatch IS NOT NULL)";
            p.Add("ty", $"%{request.Ty.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Posi))
        {
            where += " AND posi LIKE @posi";
            p.Add("posi", $"{request.Posi.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Depotno))
        {
            where += " AND depotno = @depotno";
            p.Add("depotno", new DbString { Value = request.Depotno.Trim(), IsAnsi = true, Length = 10 });
        }
        if (request.OnlyInStock)
            where += " AND kcamount <> 0";
        return (where, tyJoin, p);
    }
}
