using QPGL.Domain.Common;

namespace QPGL.Domain.FinanceConfirm;

/// <summary>
/// 财务确认（G.1，旧 fr_sk 复刻）行模型。
/// 旧实现出处：fr_sk __.row3.prg setsource（10 页签）——本次按部署约定实现
/// 出库(页1)/入库(页2)/调拨(页3)/代销付款(页7) 四页签；维修 3 页与服务卡/精品/增值卡 6 页
/// 依开发文档附录A（维修排除）与模块未建备案置灰。
/// </summary>

/// <summary>页1 出库确认行（tbsada 按单聚合，旧 c_ck 语义：round(sum(ckamount*oprc*(100-yhl)/100),2)）。</summary>
public sealed class FinConfirmOutRow
{
    public string Paper { get; set; } = "";
    public string Dpm { get; set; } = "";
    /// <summary>状态（旧 sk 派生列）：已确认/已提货/开单中/空白。</summary>
    public string Sk { get; set; } = "";
    public string Gname { get; set; } = "";
    public DateTime Outdate { get; set; }
    /// <summary>收款人（确认时写入 per1）。</summary>
    public string Per1 { get; set; } = "";
    public string Per { get; set; } = "";
    /// <summary>付款方式 guno。</summary>
    public string Guno { get; set; } = "";
    /// <summary>发票号 pno（前缀+号码）。</summary>
    public string Pno { get; set; } = "";
    /// <summary>优惠率% yhl。</summary>
    public decimal Yhl { get; set; }
    /// <summary>售价 round(sum(ckamount*oprc*(100-yhl)/100),2)（旧 c_tmp 按单汇总）。</summary>
    public decimal Oprc { get; set; }
    /// <summary>实售价 round(sum(ckamount*oprct*(100-yhl)/100),2)。</summary>
    public decimal Oprct { get; set; }
    /// <summary>减收 jianshou。</summary>
    public decimal Jianshou { get; set; }
    /// <summary>实收金额（旧 ssje：挂帐/托收/汇款取 OPRC-JIANSHOU，否则 OPRCT-JIANSHOU）。</summary>
    public decimal Ssje { get; set; }
    /// <summary>确认日期 max(qrdate)。</summary>
    public DateTime Qrdate { get; set; }
    public string Remark { get; set; } = "";
    /// <summary>原单号 ypaper。</summary>
    public string Ypaper { get; set; } = "";
    /// <summary>mark 原值（'Y'/'T'/''）。</summary>
    public string Mark { get; set; } = "";
}

/// <summary>页2 入库确认行（tbistoed 按 seano+isord 聚合，旧 c_rk 语义）。</summary>
public sealed class FinConfirmInRow
{
    /// <summary>进库单号（seano，旧列名 cno）。</summary>
    public string Cno { get; set; } = "";
    public string Gyno { get; set; } = "";
    public string Gyname { get; set; } = "";
    public DateTime Indate { get; set; }
    /// <summary>付款方式 fkfs。</summary>
    public string Fkfs { get; set; } = "";
    /// <summary>码单 pno。</summary>
    public string Pno { get; set; } = "";
    public string Per { get; set; } = "";
    /// <summary>'已确认'/''（tbistoed.mark char(6)）。</summary>
    public string Mark { get; set; } = "";
    /// <summary>订单入库标志 isord。</summary>
    public string Isord { get; set; } = "";
    public string Ywr { get; set; } = "";
    /// <summary>Σjkamount 进货数量。</summary>
    public decimal Jkamount { get; set; }
    /// <summary>确认人 qrr。</summary>
    public string Qrr { get; set; } = "";
    public DateTime Qrdate { get; set; }
    /// <summary>cast(sum(jkamount*iprc) as dec(10,2))。</summary>
    public decimal Iprc { get; set; }
    /// <summary>cast(sum(jkamount*iprj) as dec(10,2))。</summary>
    public decimal Iprj { get; set; }
}

/// <summary>页3 调拨确认行（tbsada type='调拨' 按单聚合，旧 c_db 语义）。</summary>
public sealed class FinConfirmTransferRow
{
    public string Paper { get; set; } = "";
    /// <summary>'Y'/'R'/''（'R'=已入库不可还原）。</summary>
    public string Mark { get; set; } = "";
    /// <summary>状态（旧 sk：已确认/已入库/开单中）。</summary>
    public string Sk { get; set; } = "";
    public string Gname { get; set; } = "";
    public DateTime Outdate { get; set; }
    public string Per { get; set; } = "";
    public string Guno { get; set; } = "";
    public string Pno { get; set; } = "";
    /// <summary>成本 sum(ckamount*oprc)。</summary>
    public decimal Oprc { get; set; }
    /// <summary>实成本 sum(ckamount*oprct)。</summary>
    public decimal Oprct { get; set; }
    public decimal Ckamount { get; set; }
    public DateTime Qrdate { get; set; }
    public string Dpm { get; set; } = "";
}

/// <summary>页7 代销付款确认行（tbsada type in('销售','历史') and mark<>''，旧 c_dxpj 语义）。</summary>
public sealed class FinConfirmConsignRow
{
    /// <summary>勾选（旧「选项」列，0/1）。</summary>
    public bool Checked { get; set; }
    /// <summary>'已确认'（skper<>''）/ '未确认'。</summary>
    public string Mark { get; set; } = "";
    public string Paper { get; set; } = "";
    public DateTime Outdate { get; set; }
    public string Per { get; set; } = "";
    /// <summary>付款经手人 skper。</summary>
    public string Skper { get; set; } = "";
    public string Depotno { get; set; } = "";
    public decimal Ckamount { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Iprc { get; set; }
    public decimal Iprj { get; set; }
    public string Gname { get; set; } = "";
    /// <summary>付款日期 skdate。</summary>
    public DateTime Skdate { get; set; }
    public string Cp { get; set; } = "";
    public string Dpm { get; set; } = "";
}

/// <summary>入库确认命令（旧 __.row410 FRM_PNO 确定）。</summary>
public sealed class FinConfirmInCommand
{
    /// <summary>进库单号 seano。</summary>
    public string Seano { get; set; } = "";
    public string Isord { get; set; } = "";
    /// <summary>码单 pno。</summary>
    public string Pno { get; set; } = "";
    /// <summary>付款方式 fkfs。</summary>
    public string Fkfs { get; set; } = "";
    /// <summary>供应商 gyname（挂帐时必填）。</summary>
    public string Gyname { get; set; } = "";
}

/// <summary>调拨确认命令（旧 妴_.row478 FRM调拨确认 确定）。</summary>
public sealed class FinConfirmTransferCommand
{
    public string Paper { get; set; } = "";
    /// <summary>付款方式（挂帐/托收/汇款 → 写 tbysyf）。</summary>
    public string Fkfs { get; set; } = "";
    /// <summary>转出方部门（c_db.dpm，写 tbysyf.dpm）。</summary>
    public string Dpm { get; set; } = "";
    /// <summary>客户（tbdpm dpmbh→dpm 转入方）。</summary>
    public string Gname { get; set; } = "";
}

/// <summary>代销付款确认命令（旧 ___.row454 FRMPJ 确定，逐行 skper/skdate）。</summary>
public sealed class FinConfirmConsignCommand
{
    public string Paper { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Cp { get; set; } = "";
    public string Dpm { get; set; } = "";
    /// <summary>付款经手人。</summary>
    public string Skper { get; set; } = "";
    /// <summary>付款日期。</summary>
    public DateTime Skdate { get; set; }
}

/// <summary>入库确认明细行（旧 c_mx：cp,nno,na1,ty,fa,unit,jkamount,iprc,iprj,depotno）。</summary>
public sealed class FinConfirmLine
{
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprct { get; set; }
    public decimal Iprc { get; set; }
    public decimal Iprj { get; set; }
    public string Depotno { get; set; } = "";
    /// <summary>红字规则（旧 鍻.row6 GRDMX：oprct &lt; iprc → 实售价低于人进价）。</summary>
    public bool IsRedLine => Oprct < Iprc && Iprc > 0;
}

/// <summary>分类汇总结果（旧 Z_.row54 TOTAL_FIELD + fr_total 窗）。</summary>
public sealed class FinConfirmTotalRow
{
    public string Name { get; set; } = "";
    public decimal Value { get; set; }
    /// <summary>计数型值（单据数量）。</summary>
    public long Count { get; set; }
}
