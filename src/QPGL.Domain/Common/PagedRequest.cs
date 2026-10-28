namespace QPGL.Domain.Common;

/// <summary>
/// 通用分页请求（§7.1）：page/pageSize/sort/order/keyword。
/// 单页上限 100 行（§10.2 强制分页），page 从 1 起。
/// </summary>
public class PagedRequest
{
    public const int MaxPageSize = 100;

    private int _page = 1;
    private int _pageSize = 50;

    public int Page { get => _page; set => _page = value < 1 ? 1 : value; }
    public int PageSize { get => _pageSize; set => _pageSize = value < 1 ? 50 : Math.Min(value, MaxPageSize); }

    /// <summary>排序字段名（前端传 camelCase 名，服务端经白名单映射到真实列）。</summary>
    public string? Sort { get; set; }

    /// <summary>asc / desc（默认 desc）。</summary>
    public string Order { get; set; } = "desc";

    /// <summary>关键字（前缀匹配语义，服务端决定落库列）。</summary>
    public string? Keyword { get; set; }

    /// <summary>是否降序。</summary>
    public bool IsDescending =>
        !string.Equals(Order, "asc", StringComparison.OrdinalIgnoreCase);
}
