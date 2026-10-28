namespace QPGL.Domain.Sales;

/// <summary>
/// 单据确认命令（fr_sale row298 FRMOK 契约，销售/调拨/报价共用）。
/// 确认 = 单头 UPDATE（gno,gname,pno,t_mode,yhl,guno,outdate,jzdate,remark）+ tbsada_header upsert；
/// guno 非空 = 已确认（row46 打印拦截 guno=''）；mark='Y' 由收款流程（fr_sk）写，确认不涉及。
/// </summary>
public sealed class ConfirmSaleCommand
{
    public required string Dept { get; set; }
    public required string Paper { get; set; }
    /// <summary>'销售'/'调拨'/'报价'（报价跳过应收额度检查，row298 FLAG != '报价' 分支）。</summary>
    public string DocType { get; set; } = "报价";
    public string Gno { get; set; } = "";
    public string Gname { get; set; } = "";
    /// <summary>付款组（tbsysset type='guno' name 行；非空=已确认标志）。</summary>
    public string Guno { get; set; } = "";
    public string Pno { get; set; } = "";
    public string TMode { get; set; } = "";
    /// <summary>优惠率（row298 CBOTYPE 分支处理）。</summary>
    public decimal Yhl { get; set; }
    /// <summary>优惠方式（row298 CBOTYPE：1=yhl 记账 / 2=oprct=oprc*(100-yhl)/100 / 3=oprc=oprc*(100+yhl)/100）。</summary>
    public int YhlMode { get; set; } = 1;
    public DateTime Outdate { get; set; } = DateTime.Today;
    public string Remark { get; set; } = "";
    /// <summary>tbsada_header.papertype（确认弹窗 CBOPAPERTYPE）。</summary>
    public string Papertype { get; set; } = "";
    /// <summary>tbsada_header.spname（确认弹窗 CBOSPNAME）。</summary>
    public string Spname { get; set; } = "";
    /// <summary>发票号（整单确认弹窗 TxtFphs，缺省 '*'）。</summary>
    public string Fphs { get; set; } = "";
    /// <summary>物流公司名（整单确认弹窗 TxtWlgs）。</summary>
    public string Wlgs { get; set; } = "";
    /// <summary>优惠前应收总金额（整单确认窗显示用回执）。</summary>
    public decimal AmountBefore { get; set; }
    /// <summary>优惠后应收总金额（试算结果）。</summary>
    public decimal AmountAfter { get; set; }
}

/// <summary>确认结果。</summary>
public sealed class ConfirmSaleResult
{
    public required string Paper { get; init; }
    /// <summary>jzdate（row298：FLAG='销售' 且 guno∈挂账类 → 1900-01-01，否则 outdate）。</summary>
    public required DateTime Jzdate { get; init; }
}

/// <summary>
/// 报价转销售命令（fr_sale row53 + FmsSada.frmbjtoxs 契约）。
/// BJTOXS 方法体未导出（sct 编译段）——按触发器语义推断实现并备案：
/// INSERT SELECT type='销售' 新单（带 kcid → sale_in 触发器扣库存），原报价单保留。
/// </summary>
public sealed class ConvertQuoteCommand
{
    public string Dept { get; set; } = "";
    public required string Paper { get; set; }
    /// <summary>
    /// 转单方式（frmbjtoxs.Optgp1）：
    /// 1 = 只转库存足的零件（不足行留在报价单）；
    /// 2 = 一个零件不足整单不转；
    /// 3 = 库存不足使用急件（不足行 cp='急件'+dscp=原厂牌，触发器豁免扣库存；P_IFCK='2' 才显示，本部署隐藏休眠）。
    /// </summary>
    public int Option { get; set; } = 1;
}

/// <summary>报价转销售结果。</summary>
public sealed class ConvertQuoteResult
{
    public required string NewPaper { get; init; }
    public required int ConvertedLines { get; init; }
    public required int SkippedLines { get; init; }   // OPTION1 不足行数（留在报价单）
    public required decimal TotalAmount { get; init; }
}

/// <summary>销售转报价结果（row53 销售分支照抄：INSERT SELECT 新报价单 + 原销售单删除，sale_del 回补库存）。</summary>
public sealed class ConvertSaleToQuoteResult
{
    public required string NewPaper { get; init; }
    public required int LineCount { get; init; }
    public required string OldPaper { get; init; }
}

/// <summary>预售↔销售互转结果（row53 UPDATE 路径：改 type+换号+ypaper+note+outdate，库存零变动）。</summary>
public sealed class ConvertPresaleResult
{
    public required string OldPaper { get; init; }
    public required string NewPaper { get; init; }
    /// <summary>转换行数（UPDATE tbsada 行数）。</summary>
    public required int LineCount { get; init; }
}
