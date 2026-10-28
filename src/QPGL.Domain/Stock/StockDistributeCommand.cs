namespace QPGL.Domain.Stock;

using QPGL.Domain.Common;

/// <summary>
/// 库存分货命令（fr_kcfh row23）。
/// Mode：1=按需求顺序（MIN(库存, oqty-fqty)）；2=按比例（MIN(库存, fhamount)，分货量 0 的行剔除）。
/// RoundMode（OPG1）：1=保留小数（原样）；2=四舍五入到整数 round(x,0)；3=进1取整 ceiling(x)。
/// OprcField/OprcFactor：出库价表达式简化备案 —— 旧系统 OPGSELE 按钮 TAG + CBOYS/TXTXS 自由拼接
/// 表达式（存在注入面），新系统收敛为「基准列 × 系数」+ 上面三种取整，基准列白名单 iprc/oprc/oprb/iprj。
/// </summary>
public sealed class StockDistributeCommand
{
    /// <summary>分货方式：1=按需求；2=按比例。</summary>
    public int Mode { get; init; }

    /// <summary>价格取整方式（1/2/3，默认 1 保留小数）。</summary>
    public int RoundMode { get; init; } = 1;

    /// <summary>出库价基准列（默认 iprc）。</summary>
    public string OprcField { get; init; } = "iprc";

    /// <summary>出库价系数（默认 1）。</summary>
    public decimal OprcFactor { get; init; } = 1m;

    /// <summary>库号过滤（UI CBODEPOTNO；空=不指定）。</summary>
    public string? Depotno { get; init; }

    /// <summary>勾选的订单（gname + cno 对，UI C_OR CHOICE=.T.）。</summary>
    public required IReadOnlyList<StockDistributeOrderRef> Orders { get; init; }

    public void Validate()
    {
        if (Mode is not (1 or 2))
            throw new AppException("MODE_INVALID", "分货方式只能为 1（按需求）或 2（按比例）！", 422);
        if (RoundMode is not (1 or 2 or 3))
            throw new AppException("ROUND_INVALID", "价格取整方式无效！", 422);
        if (Orders.Count == 0)
            throw new AppException("NO_ORDER_SELECTED", "请先勾选要分货的订单！", 422);
    }
}

/// <summary>勾选的订单（gname=客户、cno=订单号）。</summary>
public sealed record StockDistributeOrderRef(string Gname, string Cno);

/// <summary>库存分货结果：无任何单生成时 Papers 为空（旧系统提示「库存不足，没有生成调拨单！」）。</summary>
public sealed class StockDistributeResult
{
    /// <summary>生成的调拨单号（按部门去重前的首尾，UI 提示用）。</summary>
    public string? FirstPaper { get; set; }
    public string? LastPaper { get; set; }
    /// <summary>生成的明细行数。</summary>
    public int LineCount { get; set; }
    /// <summary>涉及的单号（按生成顺序）。</summary>
    public List<string> Papers { get; } = [];
}
