namespace QPGL.Domain.Sales;

/// <summary>作废（删除整单）结果：受影响行数与回补的库存行。</summary>
public sealed class VoidSaleResult
{
    public required string Paper { get; init; }
    public required int DeletedLines { get; init; }
    public required IReadOnlyList<(int Kcid, decimal Restored)> RestoredStock { get; init; }
}

/// <summary>作废领域校验（fr_sale row43 契约）。</summary>
public static class SaleVoidValidator
{
    public static void Validate(string paper, string dept)
    {
        if (string.IsNullOrWhiteSpace(paper))
            throw new Common.AppException("VOID_PAPER_REQUIRED", "缺少单号", 422);
        if (string.IsNullOrWhiteSpace(dept))
            throw new Common.AppException("SALE_DEPT_REQUIRED", "缺少部门", 422);
    }
}
