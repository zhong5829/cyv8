namespace QPGL.Domain.Parts;

/// <summary>配件历史售价行（开单定价参考；tbsada type IN ('销售','历史') 正数出库行）。</summary>
public sealed class PartSaleHistory
{
    public string Paper { get; init; } = "";      // 单号
    /// <summary>单据类型（销售/历史；整单明细查询按此过滤，2026-10-31 修复明细空白）。</summary>
    public string Type { get; init; } = "";
    public DateTime OutDate { get; init; }        // 出库日期
    public string Gname { get; init; } = "";      // 客户
    public string Depotno { get; init; } = "";    // 库号
    public decimal Qty { get; init; }             // 数量
    public decimal Price { get; init; }           // 单价（oprc）
}
