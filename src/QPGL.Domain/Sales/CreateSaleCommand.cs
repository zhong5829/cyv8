using QPGL.Domain.Common;

namespace QPGL.Domain.Sales;

/// <summary>销售开单明细行输入（客户端提交的最小集）。</summary>
public sealed class SaleLineInput
{
    public string Cp { get; set; } = "";       // 配件编号
    public string Nno { get; set; } = "";      // 内部编号
    public decimal Qty { get; set; }           // 出库数量（正数，旧系统 ckamount 语义）
    public decimal? Price { get; set; }        // 售价（空=取库存批次零售价 oprc）
    public string? Remark { get; set; }        // 行备注（tbsada.remark，如车牌/车型备注）
    public int? Kcid { get; set; }             // 指定批次（调拨：用户在库存列表选批次，不自动凑批；空=销售自动凑批）
}

/// <summary>销售开单命令（§7.3）。Dept 由服务端从 JWT 强制注入，客户端传入值被忽略。</summary>
public sealed class CreateSaleCommand
{
    public string Dept { get; set; } = "";
    public string? Gno { get; set; }           // 客户编号
    public string? Gname { get; set; }         // 客户名称
    public required IReadOnlyList<SaleLineInput> Lines { get; set; }
    public string? Remark { get; set; }
    /// <summary>单据类型：'销售'（默认，fr_sale row548 路径）或 '调拨'（fr_sale Lf_.row511 路径，菜单 C.3）。</summary>
    public string DocType { get; set; } = "销售";
    /// <summary>改单目标单号（复用原单号：DELETE 旧行 + INSERT 新行，旧 ___.row480 EMODE 语义）。空=新开单取新号。</summary>
    public string? Paper { get; set; }
}

/// <summary>开单结果。</summary>
public sealed class CreateSaleResult
{
    public required string Paper { get; init; }    // 单号
    public required int LineCount { get; init; }   // 明细行数（含批次拆分）
    public required decimal TotalAmount { get; init; } // Σ qty*oprc
    public required IReadOnlyList<AllocatedLine> Lines { get; init; }

    public sealed record AllocatedLine(int Kcid, string Cp, string Nno, decimal Qty, decimal Oprc);
}
