namespace QPGL.Domain.Common;

/// <summary>分页响应（§7.1）：items/total/page/pageSize。</summary>
public sealed class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}
