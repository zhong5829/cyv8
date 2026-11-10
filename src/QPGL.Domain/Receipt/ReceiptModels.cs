namespace QPGL.Domain.Receipt;

/// <summary>
/// 收款确认命令（G2 收款，M3-3；一期仅销售单收款确认/反确认）。
/// 旧实现出处：fr_sk row358（确认 UPDATE tbsada qrdate,mark='Y',guno,per1/jianshou）、
/// row364（TAMT=折后实售 / TOPRC=折后开单）、row369（逐行 tbgath/tbysyf/KH/tbfpgl 写入序列）。
/// </summary>
public sealed class ConfirmReceiptCommand
{
    public required string Paper { get; set; }
    /// <summary>单据类型（tbsada.type，一期='销售'）。</summary>
    public string Type { get; set; } = "销售";
    public required string Dept { get; set; }
    /// <summary>
    /// 主付款方式（旧 CBOFKFS 下拉值，tbsysset type='guno' 的 name）。
    /// 确认时按付款明细中 ABS 最大行决定 tbsada.guno；全 0 时回退本值（row369 L396-403）。
    /// </summary>
    public required string PayTerm { get; set; }
    /// <summary>付款明细（旧 C_GUNO 网格 NAME/PAYAMT；确认时按行写 tbgath）。</summary>
    public IReadOnlyList<ReceiptPayLine> Lines { get; set; } = [];
    /// <summary>减收（旧 TEXT2.VALUE，网格金额失焦时自动计算；row380 / row369 L414）。</summary>
    public decimal Jianshou { get; set; }
    /// <summary>
    /// 挂帐支付方（旧 TXTGZGNAME，手工输入/从 tbgu.gname 查找）。
    /// 仅「挂帐」且非空时替代客户名写 tbysyf.gname / tbfpgl.gname（旧 row369 L485-496/L561）；
    /// 受系统开关「启用挂账支付方」（tbcskg）控制，关闭时忽略。
    /// </summary>
    public string? Payer { get; set; }
    /// <summary>票据号（tbgath.payno / tbfpgl.pno，可空；旧 TXTPAYNO，不含前缀）。</summary>
    public string? PayNo { get; set; }
    /// <summary>
    /// 发票号（写入 tbsada.pno；旧 row369 L447：CBO_PNO.VALUE + TXTPAYNO.VALUE，即 1 位前缀(*/Y/N)+号码）。
    /// 仅前缀时为空号情况写 "*"。
    /// </summary>
    public string? Pno { get; set; }
    /// <summary>备注（拼入 tbysyf.xm）。</summary>
    public string? Remark { get; set; }
    /// <summary>发货方式（写入 tbsada.t_mode；并拼入 tbysyf.xm；字典 tbsysset type='fhfs'）。</summary>
    public string? TMode { get; set; }
    /// <summary>自动结转（M_AUTOJZ：skmoney=ys 的条件之一；旧 tbsysset 配置）。</summary>
    public bool AutoSettle { get; set; }
}

/// <summary>付款明细行（旧 C_GUNO.NAME / PAYAMT）。</summary>
public sealed class ReceiptPayLine
{
    public string PayTerm { get; set; } = "";
    public decimal Amount { get; set; }
}

/// <summary>收款确认结果。</summary>
public sealed class ConfirmReceiptResult
{
    public required string Paper { get; set; }
    /// <summary>收款合计（旧 Σ PAYAMT）。</summary>
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
    /// 旧契约的挂账类付款方式（旧 fr_sk row369 L479/485 与 row34 L42：INLIST(payterm,'挂帐','托收','汇款')）。
    /// 仅用于反确认时保护历史数据（若财务已收 skmoney≠0 则拒绝还原）；
    /// 新的确认写入判定统一走 <see cref="QPGL.Domain.Common.PaymentTerms.WritesReceivable"/>。
    /// </summary>
    public static bool IsOnCredit(string payTerm) =>
        QPGL.Domain.Common.PaymentTerms.IsLegacyReceivableTerm(payTerm);
}

/// <summary>
/// 收款分配/减收纯逻辑（旧 fr_sk 口径，避免 UI 与仓储各写一套）。
/// 证据：
///   __.row380.prg L25-51  网格金额 LostFocus：按限额压行、算减收；
///   C__.row382.prg L9-25  切换主付款方式：清空各行、选中行填限额、减收=0；
///   _Q_.row369.prg L396-403 主付款方式 = ABS 最大的行，全 0 回退 CBOFKFS；
///   _Q_.row369.prg L535-539 KH 差额行。
/// </summary>
public static class ReceiptSettlement
{
    private const string Gz = "挂帐";
    private const string Ts = "托收";
    private const string Hk = "汇款";
    private const string Gzcs = "挂帐冲预收";

    /// <summary>
    /// 限额判定（旧 row380 L29/L46、C__.row382 L15）：
    /// 主付款方式为挂帐/托收/汇款时取折后开单金额 TOPRC，否则取折后实售金额 TAMT。
    /// 旧程序 SET EXACT OFF（pg_main.prg L23），INLIST 对「挂帐冲预收」也按前缀命中「挂帐」，一并按开单金额。
    /// </summary>
    public static bool UsesOrderAmountLimit(string? payTerm)
    {
        var t = (payTerm ?? "").Trim();
        return t == Gz || t == Ts || t == Hk || t == Gzcs;
    }

    public static decimal Limit(string? payTerm, decimal totalOprc, decimal totalOprct) =>
        UsesOrderAmountLimit(payTerm) ? totalOprc : totalOprct;

    /// <summary>
    /// 网格编辑提交（旧 row380 L25-44）：sumIncludingRow = 全部行合计数。
    /// 超额（同号方向）时把当前行压到 限额-(合计-当前行)，减收归 0；否则减收=限额-合计。
    /// 合计为 0 时公式同样得到 减收=限额（旧 L45-50）。
    /// </summary>
    public static (decimal RowAmount, decimal Jianshou) ApplyRowEdit(
        decimal limit, decimal sumIncludingRow, decimal rowAmount)
    {
        if ((limit > 0 && sumIncludingRow > limit) || (limit < 0 && sumIncludingRow < limit))
        {
            return (limit - (sumIncludingRow - rowAmount), 0m);
        }
        return (rowAmount, limit - sumIncludingRow);
    }

    /// <summary>
    /// 主付款方式（旧 row369 L396-403）：ABS 最大行；无行或最大值为 0 时回退 CBOFKFS；
    /// 仍为空则「现金」。
    /// </summary>
    public static string MainPayTerm(IReadOnlyList<ReceiptPayLine> lines, string? fallback)
    {
        decimal maxAbs = 0m;
        string maxTerm = "";
        foreach (var line in lines)
        {
            var abs = Math.Abs(line.Amount);
            if (abs > maxAbs)
            {
                maxAbs = abs;
                maxTerm = (line.PayTerm ?? "").Trim();
            }
        }
        if (maxAbs == 0m)
        {
            var fb = (fallback ?? "").Trim();
            return fb.Length > 0 ? fb : "现金";
        }
        return maxTerm.Length > 0 ? maxTerm : "现金";
    }

    /// <summary>KH 差额行条件（旧 row369 L535）：TAMT-TOPRC≠0，且只在写应收的信用行分支内生成。</summary>
    public static bool ShouldWriteDiscountRow(decimal tamt, decimal toprc, bool hasReceivableLine) =>
        hasReceivableLine && tamt - toprc != 0m;

    /// <summary>发票台账 tbfpgl 触发条件（旧 row369 L559）：发票号号码本体非空。</summary>
    public static bool ShouldWriteInvoiceLedger(string? payNoBody) =>
        !string.IsNullOrWhiteSpace(payNoBody);

    /// <summary>
    /// 是否使用挂帐支付方（旧 row369 L485/L561：仅 PAYTERM='挂帐'；SET EXACT OFF 下含「挂帐冲预收」）。
    /// </summary>
    public static bool UsesPayer(string? payTerm)
    {
        var t = (payTerm ?? "").Trim();
        return t == Gz || t == Gzcs;
    }

    /// <summary>支付方与客户名同一（Trim 后忽略大小写；旧 row369 L487 提示「客户挂帐到自己的名下时，无需输入挂帐支付方」）。</summary>
    public static bool IsSamePayer(string? customerName, string? payer)
    {
        var c = (customerName ?? "").Trim();
        var p = (payer ?? "").Trim();
        return p.Length > 0 && string.Equals(c, p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 生效应收单位名（旧 row369 L485-496 / L561）：
    /// 挂帐且支付方非空且与客户名不同 → 支付方；否则客户名（托收/汇款等一律客户名）。
    /// </summary>
    public static string EffectiveGname(string? customerName, string? payer, string? payTerm)
    {
        var c = (customerName ?? "").Trim();
        var p = (payer ?? "").Trim();
        if (UsesPayer(payTerm) && p.Length > 0 && !string.Equals(c, p, StringComparison.OrdinalIgnoreCase))
            return p;
        return c;
    }

    /// <summary>
    /// 挂账前置校验（旧 __.row380 L8-13 / row369 L187-198）：
    /// 开关「启用挂账支付方」关闭时挂账必须有客户名；开启时客户名或支付方至少一个非空。
    /// </summary>
    public static bool RequiresCustomerName(bool onCredit, string? customerName, bool payerEnabled, string? payer)
    {
        if (!onCredit) return false;
        if ((customerName ?? "").Trim().Length > 0) return false;
        var usablePayer = payerEnabled && (payer ?? "").Trim().Length > 0;
        return !usablePayer;
    }

    /// <summary>金额列 2 位小数（旧金额列/网格 PAYAMT 的显示与落库口径；不足 1 分按 2 位小数舍入）。</summary>
    public static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
