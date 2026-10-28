using QPGL.Domain.Common;

namespace QPGL.Domain.Stock;

/// <summary>分货方式（fr_isor 癌.row103 弹出菜单）。</summary>
public enum OrderInDistributeMode
{
    /// <summary>按订货时间分货（orderdate 升序优先，gname 非空优先）。</summary>
    ByOrderDate = 1,
    /// <summary>按需求比例分货（缺口 oqty-qty 加权，gname 非空优先保量）。</summary>
    ByDemandRatio = 2,
}

/// <summary>
/// 整单导入命令（fr_isor -__.row156「整单导入」）：按到货单号 cno 从 tborder 拉未入完订单行
/// （oqty&gt;qty）逐行 INSERT tbisorsm 草稿，jkamount=缺口(oqty-qty)。
/// </summary>
public sealed class ImportOrderInCommand
{
    /// <summary>船单号（seano，本次到货批次号，用户输入）。</summary>
    public required string Seano { get; set; }
    /// <summary>到货单号/合同号（tborder.cno）。</summary>
    public required string Cno { get; set; }
    /// <summary>箱号（box，可空）。</summary>
    public string Box { get; set; } = "";
    /// <summary>提货单号（pno，可空）。</summary>
    public string Pno { get; set; } = "";
    /// <summary>默认仓库（depotno，可空；gname 非空行强制改写为代订货库号）。</summary>
    public string Depotno { get; set; } = "";
}

/// <summary>手工加行命令（fr_isor 祔_.row211 非 EMODE 分支）。</summary>
public sealed class AddOrderInLineCommand
{
    public required string Seano { get; set; }
    public required string Cno { get; set; }
    public required string Orno { get; set; }
    public int Item { get; set; }
    public required string Nno { get; set; }
    public required string Na1 { get; set; }
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Jkamount { get; set; }
    public decimal Fpamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprb { get; set; }
    public string Posi { get; set; } = "";
    public string Depotno { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Box { get; set; } = "";
    public string Pno { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>草稿行编辑命令（fr_isor 祔_.row211 EMODE 分支：UPDATE tbisorsm WHERE id）。</summary>
public sealed class UpdateOrderInLineCommand
{
    public required int Id { get; set; }
    public required string Nno { get; set; }
    public decimal Jkamount { get; set; }
    public decimal Iprj { get; set; }
    public string Fa { get; set; } = "";
    public decimal Iprc { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprb { get; set; }
    public string Posi { get; set; } = "";
    public string Depotno { get; set; } = "";
    public decimal Suttle { get; set; }
}

/// <summary>整批调价命令（fr_isor 饰.row112 通用路径）。</summary>
public sealed class BatchAdjustOrderInCommand
{
    public required string Seano { get; set; }
    /// <summary>被改列：iprc / oprc / oprb。</summary>
    public required string Ufield { get; set; }
    /// <summary>乘数来源列：与 Ufield 同列（自身×系数）或 iprj（外币价×系数）。</summary>
    public required string SourceColumn { get; set; }
    /// <summary>系数（旧 m_xs）。</summary>
    public decimal Factor { get; set; }
    /// <summary>取整方式（旧 OPGXS）：1=ROUND（iprc 按库精度/其余 2 位） 2=INT(+0.5) 3=INT(+0.99)。</summary>
    public int RoundMode { get; set; } = 1;
    /// <summary>小数位（旧 P_IPRCCOUNT，iprc 专用）。</summary>
    public int IprcCount { get; set; } = 2;
}

/// <summary>
/// 确认入库命令（fr_isor 矑_.row220 Click「更新库存」完整序列）。
/// 每行行级事务：INSERT tbistoed（ISORD='Y'）→ tbprnoty 缺档自动建档 →
/// （gname 非空：GETNEWDH('销售单')+INSERT tbsada 直发，kcid=0 不动库存 / 否则 ISAPPE 入库）→
/// UPDATE tborder.qty=MIN(qty+行量,oqty) → 删 tbisorsm 行。
/// </summary>
public sealed class ConfirmOrderInCommand
{
    public required string Seano { get; set; }
    /// <summary>提货单号（pno，旧 FRMMAIN.TXTPNO，写入 tbistoed.pno）。</summary>
    public string Pno { get; set; } = "";
    /// <summary>入库范围（旧 FR_JK OPT1）：1=按箱号 3=全部。MVP 固定按船单全部（=3）。</summary>
    public int Mode { get; set; } = 3;
    /// <summary>按箱号模式的箱号（Mode=1 时必填）。</summary>
    public string Box { get; set; } = "";
    /// <summary>仓位标识后缀（旧 TXT1，非空时 posi=posi+suffix，仅允许 !@#$%^&* ）。</summary>
    public string PosiSuffix { get; set; } = "";
    /// <summary>更新档案进价（旧 CHKIPRJ：UPDATE tbprnoty.iprj）。</summary>
    public bool UpdateProfileIprj { get; set; }
}

/// <summary>确认入库结果。</summary>
public sealed class ConfirmOrderInResult
{
    public int ProcessedLines { get; set; }
    /// <summary>直发生成的销售单号（按 gname 分组取号，同客户共用一单号）。</summary>
    public List<string> Papers { get; set; } = [];
    public int DirectSaleLines { get; set; }
    public int StockInLines { get; set; }
}

/// <summary>待入库订单行（拉单查询，row166 列集）。</summary>
public sealed class OrderInOrderLine
{
    public required string Gname { get; set; }
    public int Item { get; set; }
    public required string Nno { get; set; }
    public required string Na1 { get; set; }
    public decimal Oqty { get; set; }
    public decimal Qty { get; set; }
    /// <summary>缺口 = oqty - qty。</summary>
    public decimal Zt { get; set; }
    public decimal Iprj { get; set; }
    public decimal Oprc { get; set; }
    public required string Orno { get; set; }
    public decimal Iprc { get; set; }
    public decimal Oprb { get; set; }
    public required string Ty { get; set; }
    public required string Fa { get; set; }
    public required string Cp { get; set; }
    public required string Unit { get; set; }
    public required string Dc { get; set; }
    public required string Note { get; set; }
    public required string Fkfs { get; set; }
    public required string Gyname { get; set; }
    public required string Cno { get; set; }
}

/// <summary>草稿行（tbisorsm + row3 连接展示列）。</summary>
public sealed class OrderInDraftLine
{
    public int Id { get; set; }
    public required string Pno { get; set; }
    public required string Box { get; set; }
    public required string Cno { get; set; }
    public int Item { get; set; }
    public required string Nno { get; set; }
    public required string Na1 { get; set; }
    public decimal Jkamount { get; set; }
    public decimal Fpamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprb { get; set; }
    public required string Ty { get; set; }
    public required string Fa { get; set; }
    public required string Posi { get; set; }
    public required string Depotno { get; set; }
    public required string Seano { get; set; }
    public required string Orno { get; set; }
    public required string Unit { get; set; }
    public required string Fkfs { get; set; }
    public required string Gyname { get; set; }
    public required string Dc { get; set; }
    public required string Cp { get; set; }
    public DateTime Indate { get; set; }
    public decimal Nvol { get; set; }
    public decimal Suttle { get; set; }
    /// <summary>订单客户（tborder type='历史' 行 gname；非空行确认时走直发）。</summary>
    public required string Gname { get; set; }
    /// <summary>该配件当前库存合计（tbisto，按 cp+nno 含别名）。</summary>
    public decimal Kcamount { get; set; }
    /// <summary>订单未入缺口（oqty-qty）。</summary>
    public decimal Zt { get; set; }
    public decimal Freight { get; set; }
}
