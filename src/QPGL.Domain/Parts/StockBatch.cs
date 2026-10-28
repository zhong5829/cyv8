namespace QPGL.Domain.Parts;

/// <summary>库存批次行（tbisto，同一配件可多批次/多货位）。</summary>
public sealed class StockBatch
{
    public int Id { get; init; }                // tbisto.id（= tbsada.kcid）
    public string Dpm { get; init; } = "";
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Fa { get; init; } = "";
    public string Dc { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Kcamount { get; init; }      // 该批次当前库存
    public decimal Iprj { get; init; }          // 进价（不含税）
    public decimal Iprc { get; init; }          // 进价（含税）
    public decimal Oprc { get; init; }          // 零售价
    public string Posi { get; init; } = "";     // 货位
    public string Depotno { get; init; } = "";  // 仓库号
    public int Xno { get; init; }
    public int Tno { get; init; }
    public string Cxnno { get; init; } = "";
}
