namespace QPGL.Domain.Parts;

/// <summary>入库单整单明细行（tbistoed 按 cno；2026-10-31 需求：进货历史双击查看，不含沙河定制 gu 列）。</summary>
public sealed class PartPurchaseDocLine
{
    public string Cno { get; init; } = "";        // 进货单号
    public string Nno { get; init; } = "";        // 内部编号
    public string Na1 { get; init; } = "";        // 名称
    public string Ty { get; init; } = "";         // 车型
    public decimal Qty { get; init; }             // 入库数量（jkamount）
    public decimal Iprc { get; init; }            // 含税价
    public DateTime InDate { get; init; }         // 入库日期
    public string Gyname { get; init; } = "";     // 供应商
}
