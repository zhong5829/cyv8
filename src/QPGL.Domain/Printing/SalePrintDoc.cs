namespace QPGL.Domain.Printing;

/// <summary>
/// 销售单票样数据（pg_dosprint pg_sa1 复刻：票头/明细/合计/大写/票尾）。
/// API 端统一计算合计、优惠（旧系统 INT 取整语义）与大写（§8.3.2-3）。
/// </summary>
public sealed class SalePrintDoc
{
    // ---- 票头：公司信息（tbsysset type='system'）----
    public string CompanyName { get; init; } = "";
    public string Tel { get; init; } = "";
    public string Fax { get; init; } = "";            // 传真（公司信息行）
    public string Address { get; init; } = "";
    public string Bank { get; init; } = "";           // 开户银行（pno='Y' 票尾行）
    public string Account { get; init; } = "";        // 账号（tbsysset name='帐号'）
    public string TaxNo { get; init; } = "";          // 公司税号
    public string DocMaker { get; init; } = "";       // 制单人（系统信息区）

    // ---- 单据信息 ----
    public string Paper { get; init; } = "";          // 单号
    public string PaperSuffix { get; init; } = "";    // 单号后6位（旧票样显示 RIGHT('000000'+LTRIM(PAPER),6)）
    public DateTime OutDate { get; init; }
    public DateTime PrintTime { get; init; }
    public string CustomerName { get; init; } = "";   // gname（收货单位）
    public string CustomerNo { get; init; } = "";     // tbgu.checkno（旧票样"账号"位）
    public string CustomerTaxNo { get; init; } = "";  // tbgu.type1（税号）
    public string CustomerTel { get; init; } = "";
    public string CustomerAddress { get; init; } = "";
    public string CustomerBank { get; init; } = "";
    public string PayMode { get; init; } = "";        // guno（付款方式）
    public string Operator { get; init; } = "";       // per（营业员）
    public bool IsRetail { get; init; }               // pno='N' 散客：不打印客户资料行
    /// <summary>pno='Y'（开发票，旧 pg_sa1 L113）：打印公司开户银行+账号行（ADD1）。</summary>
    public bool PrintCompanyBank { get; init; }

    // ---- 明细 ----
    public required IReadOnlyList<SalePrintLine> Lines { get; init; }

    // ---- 合计（服务端计算）----
    public decimal TotalQty { get; init; }
    public decimal TotalAmount { get; init; }         // Σ qty*oprc
    public decimal Discount { get; init; }            // yhl 优惠额（INT 取整，旧 OTHERWISE 分支）
    public decimal FinalAmount { get; init; }         // 折后合计
    public string AmountUpper { get; init; } = "";    // 人民币大写

    // ---- 票尾（tbsysset RPSET1+RPSET2）----
    public string Footer { get; init; } = "";
    public string Remark { get; init; } = "";         // 单据备注（tbsada.remark，票尾"备注："内容）
}

public sealed class SalePrintLine
{
    public int Seq { get; init; }
    public string Cp { get; init; } = "";        // 配件编号（票样明细用 PNNO 语义，取 nno）
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Qty { get; init; }            // ckamount（正出库）
    public decimal Price { get; init; }          // oprc
    public decimal Amount { get; init; }         // qty*oprc
    public string Posi { get; init; } = "";
}

/// <summary>打印设置（票头公司信息只读展示 + 票尾可编辑）。</summary>
public sealed class PrintSettings
{
    public string CompanyName { get; init; } = "";
    public string Tel { get; init; } = "";
    public string Address { get; init; } = "";
    public string Bank { get; init; } = "";
    public string Account { get; init; } = "";
    public string FooterPart1 { get; set; } = "";   // RPSET1（可编辑）
    public string FooterPart2 { get; set; } = "";   // RPSET2（可编辑）
}
