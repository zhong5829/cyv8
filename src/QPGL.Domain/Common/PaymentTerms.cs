namespace QPGL.Domain.Common;

/// <summary>
/// 付款方式统一字面值与「是否写应收应付」判定。
/// 取值来源：tbsysset type='guno'（当前部署字典：现金 / 汇款 / 代收 / 挂帐）。
/// 注意旧系统与数据用的是「挂帐」（帐），不是「挂账」。
/// 旧契约 fr_sk row369、妴_.row478 对 挂帐/托收/汇款 三类都写 tbysyf；
/// 二期业务口径改为仅挂账写，现金/汇款/代收/托收均不写。
/// </summary>
public static class PaymentTerms
{
    public const string Cash = "现金";
    public const string Remittance = "汇款";
    public const string AgentCollection = "代收";
    public const string CreditAccount = "挂帐";
    public const string Collection = "托收";
    public const string CreditOffset = "挂帐冲预收";

    /// <summary>
    /// 财务确认/调拨入库确认时是否写 tbysyf（应收/应付）。
    /// 业务口径（二期）：仅「挂帐」写；现金/汇款/代收/托收均不写。
    /// 旧系统 fr_sk row369、fr_dbrk __.row68 L71 为挂帐/托收/汇款三类都写，属已知差异。
    /// </summary>
    public static bool WritesReceivable(string? payTerm)
    {
        var t = (payTerm ?? "").Trim();
        return t == CreditAccount;
    }

    /// <summary>
    /// 旧契约的挂账类付款方式（挂帐/托收/汇款）。
    /// 仅用于反确认的历史数据保护（原 ReceiptGuards.IsOnCredit 语义），不用于新确认写入判定。
    /// </summary>
    public static bool IsLegacyReceivableTerm(string? payTerm)
    {
        var t = (payTerm ?? "").Trim();
        return t == CreditAccount || t == Collection || t == Remittance;
    }
}
