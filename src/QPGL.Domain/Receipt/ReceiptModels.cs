namespace QPGL.Domain.Receipt;

/// <summary>
/// 收款确认命令（G2 收款，M3-3；一期仅销售单收款确认/反确认）。
/// 旧实现出处：fr_sk row358（确认 UPDATE tbsada qrdate,mark='Y',guno,per1）、
/// row364（TAMT=oprct 实收参考 / TOPRC=oprc 应收）、row369（tbgath/tbysyf 写入序列）。
/// </summary>
public sealed class ConfirmReceiptCommand
{
    public required string Paper { get; set; }
    /// <summary>单据类型（tbsada.type，一期='销售'）。</summary>
    public string Type { get; set; } = "销售";
    public required string Dept { get; set; }
    /// <summary>付款方式（tbsysset type='guno' 的 name，如 现金/挂帐/托收/汇款…）。</summary>
    public required string PayTerm { get; set; }
    /// <summary>实收金额（默认=折后应收合计）。</summary>
    public decimal Amount { get; set; }
    /// <summary>票据号（tbgath.payno，可空）。</summary>
    public string? PayNo { get; set; }
    /// <summary>备注（拼入 tbysyf.xm）。</summary>
    public string? Remark { get; set; }
    /// <summary>运输方式（t_mode，拼入 tbysyf.xm）。</summary>
    public string? TMode { get; set; }
    /// <summary>自动结转（M_AUTOJZ：skmoney=ys 的条件之一；旧 tbsysset 配置）。</summary>
    public bool AutoSettle { get; set; }
}

/// <summary>收款确认结果。</summary>
public sealed class ConfirmReceiptResult
{
    public required string Paper { get; set; }
    public decimal Amount { get; set; }
    public DateTime Gdate { get; set; }
    /// <summary>是否生成挂账应收（tbysyf 行）。</summary>
    public bool OnCredit { get; set; }
    /// <summary>差额行金额（TAMT-TOPRC≠0 时生成，旧 PNO='KH' 行）。</summary>
    public decimal DiffAmount { get; set; }
}

/// <summary>收款方式（tbsysset WHERE type='guno'：name=方式名，valuen=附加数值）。</summary>
public sealed class PaymentMethod
{
    public string Name { get; set; } = "";
    public decimal Valuen { get; set; }
}

/// <summary>收款守卫（纯逻辑，供仓储与单测复用）。</summary>
public static class ReceiptGuards
{
    /// <summary>
    /// 挂账类付款方式（旧 fr_sk row369 L479/485 与 row34 L42：INLIST(payterm,'挂帐','托收','汇款')）。
    /// 该类方式确认收款时写 tbysyf 应收行；反确认时若财务已收（skmoney≠0）拒绝还原。
    /// </summary>
    public static bool IsOnCredit(string payTerm)
    {
        var t = (payTerm ?? "").Trim();
        return t is "挂帐" or "托收" or "汇款";
    }
}
