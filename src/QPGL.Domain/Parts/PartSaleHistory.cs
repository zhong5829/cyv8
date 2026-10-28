namespace QPGL.Domain.Parts;

/// <summary>配件历史售价行（开单定价参考；tbsada type='销售' 正数出库行）。</summary>
public sealed class PartSaleHistory
{
    public string Paper { get; init; } = "";      // 单号
    public DateTime OutDate { get; init; }        // 出库日期
    public string Gname { get; init; } = "";      // 客户
    public decimal Qty { get; init; }             // 数量
    public decimal Price { get; init; }           // 单价（oprc）
}
