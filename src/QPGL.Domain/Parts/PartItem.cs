namespace QPGL.Domain.Parts;

/// <summary>
/// 配件库存行（tbisto，主档与库存批次合一——旧系统开单直接取该行价格与 kcid）。
/// 旧实现出处：fr_sale row548 —— SPT("select * from tbisto where cp=? and nno=? and dpm=? ...")
/// </summary>
public sealed class PartItem
{
    public int Id { get; init; }                // tbisto.id（= tbsada.kcid）
    public string Dpm { get; init; } = "";
    public string Cp { get; init; } = "";       // 配件编号
    public string Nno { get; init; } = "";      // 内部编号
    public string Na1 { get; init; } = "";      // 名称
    public string Ty { get; init; } = "";       // 图号
    public string Fa { get; init; } = "";       // 方案/厂家
    public string Dc { get; init; } = "";       // 产地
    public string Unit { get; init; } = "";     // 单位
    public decimal Kcamount { get; init; }      // 该批次当前库存
    public decimal Iprj { get; init; }          // 进价（不含税）
    public decimal Iprc { get; init; }          // 进价（含税）
    public decimal Oprc { get; init; }          // 零售价
    public decimal Oprb { get; init; }          // 批发价
    public string Posi { get; init; } = "";     // 货位
    public string Depotno { get; init; } = "";  // 仓库号
    public string Note { get; init; } = "";     // 备注
    public decimal LastIprj { get; set; }       // 最近一次进价（tbistoed 最近一条入库行 iprj，搜索后批量回填；2026-10-28 需求：默认隐藏列，双击空格切换显示）
}
