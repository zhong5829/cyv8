namespace QPGL.Domain.Parts;

/// <summary>
/// 标签打印行（qp11 PartRepository.GetLabelItemsAsync 复刻；2026-10-31 需求）。
/// tbisto 为批次表（同 nno 多批次行），本 DTO 按 nno 去重合并：库存 SUM、货位取首个非空值。
/// </summary>
public sealed class LabelStockItem
{
    public string Nno { get; init; } = "";      // 内部编号（标签编码/条码内容，用户确认口径）
    public string Cp { get; init; } = "";       // 配件编号
    public string Na1 { get; init; } = "";      // 名称
    public string Ty { get; init; } = "";       // 车型/图号
    public string Posi { get; init; } = "";     // 货位（仓位）
    public decimal Kcamount { get; init; }      // 合并库存
}
