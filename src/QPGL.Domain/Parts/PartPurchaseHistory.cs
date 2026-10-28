namespace QPGL.Domain.Parts;

/// <summary>配件历史进货价行（入库价参考；tbistoed type IN ('入库','历史') 正数入库行）。</summary>
public sealed class PartPurchaseHistory
{
    public string Cno { get; init; } = "";        // 进货单号
    public DateTime InDate { get; init; }         // 入库日期
    public string Gyname { get; init; } = "";     // 供应商
    public decimal Qty { get; init; }             // 数量
    public decimal Iprj { get; init; }            // 进价（不含税）
    public decimal Iprc { get; init; }            // 进价（含税）
}
