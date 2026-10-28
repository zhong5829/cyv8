using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Partners;

public interface IPartnerRepository
{
    Task<PagedResult<Partner>> SearchAsync(PagedRequest request, CancellationToken ct = default);

    /// <summary>客户限额/预警限额（tbgu.xede/yjxe；旧整单确认窗红字显示，列不存在时返回 0）。</summary>
    Task<(decimal Xede, decimal Yjxe)> GetLimitAsync(string gname, CancellationToken ct = default);

    /// <summary>客户经手人/发票号（tbgu.pno；旧确认窗 CBOPNO 按客户带出，缺省 '*'）。</summary>
    Task<string> GetCustomerPnoAsync(string gname, CancellationToken ct = default);
}

/// <summary>
/// 客户资料仓储（tbgu，结构零改动）。
/// 旧实现出处：fr_client* 表单族（客户资料维护/查询）。
/// 性能（§10.2）：显式列 + OFFSET/FETCH 强制分页 + 排序走白名单索引列 + 关键字前缀匹配（SARGable）
/// + READ UNCOMMITTED（旧系统全局 P_WITHNOLOCK 语义，仅列表查询；写路径严禁脏读）。
/// </summary>
public sealed class PartnerRepository(SqlConnectionFactory factory) : IPartnerRepository
{
    public async Task<PagedResult<Partner>> SearchAsync(PagedRequest request, CancellationToken ct = default)
    {
        var kw = (request.Keyword ?? "").Trim();
        var hasKeyword = kw.Length > 0;

        // 参数与 WHERE 一次性构造，保证数据页/计数两查询一致
        var args = new
        {
            kw = hasKeyword ? new DbString { Value = kw, IsAnsi = true, Length = 60 } : null,
            offset = (request.Page - 1) * request.PageSize,
            pageSize = request.PageSize,
        };
        var where = hasKeyword
            ? """
              WHERE gno    LIKE @kw + '%'
                 OR gname  LIKE @kw + '%'
                 OR code   LIKE @kw + '%'
                 OR code1  LIKE @kw + '%'
                 OR mobile LIKE @kw + '%'
                 OR tel    LIKE @kw + '%'
              """
            : "";

        var order = PartnerSortFields.Resolve(request.Sort, request.IsDescending);

        await using var conn = await factory.OpenAsync(ct);

        // 数据页：NOLOCK 仅用于列表查询（跟随旧系统语义）
        var pageSql =
            $"""
            SELECT id          AS Id,
                   gno         AS PartNo,
                   gname       AS PartName,
                   linkman     AS LinkMan,
                   tel         AS Tel,
                   mobile      AS Mobile,
                   adr         AS Address,
                   type1       AS Type1,
                   type2       AS Type2,
                   yhl         AS DiscountRate,
                   ywy         AS SalesMan,
                   note        AS Note
            FROM tbgu WITH (NOLOCK)
            {where}
            ORDER BY {order}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        // 总数：同条件计数（NOLOCK 近似容差可接受）
        var countSql = $"SELECT COUNT(1) FROM tbgu WITH (NOLOCK) {where}";

        var cmdPage = new CommandDefinition(pageSql, args, cancellationToken: ct);
        var cmdCount = new CommandDefinition(countSql, args, cancellationToken: ct);

        var items = (await conn.QueryAsync<Partner>(cmdPage)).AsList();
        var total = await conn.ExecuteScalarAsync<int>(cmdCount);

        return new PagedResult<Partner>
        {
            Items = items,
            Total = total,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    /// <summary>
    /// 客户限额/预警限额（整单确认窗红字行，旧 fr_sale FRMOK 侧 tbgu.xede/yjxe 直读）。
    /// 列在本部署可能未建（限额功能未启用）→ 动态探测，缺列返回 0（与旧系统空值显示 0 一致）。
    /// </summary>
    public async Task<(decimal Xede, decimal Yjxe)> GetLimitAsync(string gname, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var hasCols = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT CASE WHEN COL_LENGTH('tbgu', 'xede') IS NOT NULL AND COL_LENGTH('tbgu', 'yjxe') IS NOT NULL
                        THEN 1 ELSE 0 END
            """, cancellationToken: ct));
        if (!hasCols || string.IsNullOrWhiteSpace(gname)) return (0m, 0m);
        var row = await conn.QueryFirstOrDefaultAsync<(decimal Xede, decimal Yjxe)>(new CommandDefinition(
            "SELECT TOP 1 ISNULL(xede, 0), ISNULL(yjxe, 0) FROM tbgu WITH (NOLOCK) WHERE gname = @gname",
            new { gname = new DbString { Value = gname.Trim(), IsAnsi = false, Length = 60 } },
            cancellationToken: ct));
        return row == default ? (0m, 0m) : row;
    }

    /// <summary>客户经手人/发票号（tbgu.pno，旧 _b_.row270 L13-14 按客户带出语义）。</summary>
    public async Task<string> GetCustomerPnoAsync(string gname, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var pno = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 rtrim(pno) FROM tbgu WITH (NOLOCK) WHERE gname = @gname",
            new { gname = new DbString { Value = gname.Trim(), IsAnsi = false, Length = 60 } },
            cancellationToken: ct));
        return pno ?? "";
    }
}
