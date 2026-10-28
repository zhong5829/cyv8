namespace QPGL.Domain.Sales;

/// <summary>原销售单可退行（退货页加载；旧出处 fr_sata，列集对照旧 GrdSada 网格）。</summary>
public sealed class SaleSourceLine
{
    public int RowId { get; init; }
    public string Paper { get; init; } = ""; // 原销售单号（按客户加载时区分来源单；出库单号同值）
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Posi { get; init; } = "";
    public string DepotNo { get; init; } = ""; // 库号（退货编辑窗「库号」下拉默认值）
    public string Ty { get; init; } = "";      // 车型
    public string Fa { get; init; } = "";      // 产地
    public string Cxnno { get; init; } = "";   // 分类码（厂牌分类）
    public string Guno { get; init; } = "";    // 付款方式（原单行）
    public string Gname { get; init; } = "";   // 客户名称
    public string Per { get; init; } = "";     // 操作员
    public string Note { get; init; } = "";    // 备注
    public string Pno { get; init; } = "";     // 发票号（确认退货窗「发票号」默认值）
    public string TMode { get; init; } = "";   // 发货方式（确认退货窗「发货方式」默认值）
    public decimal Qty { get; init; }        // 原出库数
    public decimal Returned { get; init; }   // 已退数 ro
    public decimal Remain => Qty - Returned; // 未退量（旧网格「可退数」tkamount）
    public decimal Oprc { get; init; }       // 售价
    public decimal Oprct { get; init; }      // 实售价
    public decimal Iprc { get; init; }       // 进价（含税）
    public decimal Iprj { get; init; }       // 进价（不含税）
    public decimal Yhl { get; init; }        // 优惠率
    public DateTime OutDate { get; init; }   // 出库日期
}

/// <summary>配件可退批次选项（退货编辑窗「库号/仓位」下拉；tbisto 实际批次）。</summary>
public sealed record ReturnBatchOption(string DepotNo, string Posi);

public sealed record SaleSourceResult(string Paper, string Gname, DateTime OutDate, IReadOnlyList<SaleSourceLine> Lines);

/// <summary>按客户加载的可退行集合（跨多张原销售单，退货页）。</summary>
public sealed record CustomerSourceResult(string Gno, string Gname, IReadOnlyList<SaleSourceLine> Lines);
