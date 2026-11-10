using QPGL.Domain.Receipt;

namespace QPGL.Tests;

/// <summary>
/// 收款窗「多行收款方式 / 减收 / 差额 / 发票台账」纯逻辑单测。
/// 口径证据：fr_sk __.row380.prg L25-51、C__.row382.prg L9-25、_Q_.row369.prg L396-403/L535/L559。
/// </summary>
public sealed class ReceiptSettlementTests
{
    [Theory]
    [InlineData("挂帐", 1200, 1000, 1200)]
    [InlineData("托收", 1200, 1000, 1200)]
    [InlineData("汇款", 1200, 1000, 1200)]
    [InlineData("挂帐冲预收", 1200, 1000, 1200)] // SET EXACT OFF 下 INLIST 前缀命中「挂帐」
    [InlineData("现金", 1200, 1000, 1000)]
    [InlineData("代收", 1200, 1000, 1000)]
    public void Limit_ByPayTerm(string payTerm, decimal toprc, decimal tamt, decimal expected)
    {
        Assert.Equal(expected, ReceiptSettlement.Limit(payTerm, toprc, tamt));
    }

    [Fact]
    public void ApplyRowEdit_BelowLimit_ReturnsDifference()
    {
        // 限额 1000，已录 300，当前行 500 → 合计 800，减收 200
        var (row, jianshou) = ReceiptSettlement.ApplyRowEdit(1000m, 800m, 500m);
        Assert.Equal(500m, row);
        Assert.Equal(200m, jianshou);
    }

    [Fact]
    public void ApplyRowEdit_OverLimit_ClampsCurrentRow()
    {
        // 限额 1000，已录 300，当前行 800 → 合计 1100；当前行压到 700，减收 0
        var (row, jianshou) = ReceiptSettlement.ApplyRowEdit(1000m, 1100m, 800m);
        Assert.Equal(700m, row);
        Assert.Equal(0m, jianshou);
    }

    [Fact]
    public void ApplyRowEdit_ZeroTotal_JianshouEqualsLimit()
    {
        var (row, jianshou) = ReceiptSettlement.ApplyRowEdit(1000m, 0m, 0m);
        Assert.Equal(0m, row);
        Assert.Equal(1000m, jianshou);
    }

    [Fact]
    public void ApplyRowEdit_NegativeDirection_Clamps()
    {
        // 退货：限额 -1000，已录 -300，当前行 -800 → 合计 -1100；当前行压到 -700
        var (row, jianshou) = ReceiptSettlement.ApplyRowEdit(-1000m, -1100m, -800m);
        Assert.Equal(-700m, row);
        Assert.Equal(0m, jianshou);
    }

    [Fact]
    public void ApplyRowEdit_MultiLineSequence_ProducesJianshou()
    {
        // 多行分摊：现金 300 + 代收 500 + 挂帐 200，限额 1200 → 减收 200
        const decimal limit = 1200m;
        var sum = 0m;
        var jianshou = 0m;
        foreach (var amount in new[] { 300m, 500m, 200m })
        {
            sum += amount;
            var r = ReceiptSettlement.ApplyRowEdit(limit, sum, amount);
            Assert.Equal(amount, r.RowAmount);
            jianshou = r.Jianshou;
        }
        Assert.Equal(1000m, sum);
        Assert.Equal(200m, jianshou);
    }

    [Fact]
    public void MainPayTerm_UsesLargestAbsoluteLine()
    {
        var lines = new List<ReceiptPayLine>
        {
            new() { PayTerm = "现金", Amount = 300m },
            new() { PayTerm = "代收", Amount = -800m },
            new() { PayTerm = "挂帐", Amount = 200m },
        };
        Assert.Equal("代收", ReceiptSettlement.MainPayTerm(lines, "现金"));
    }

    [Fact]
    public void MainPayTerm_AllZero_FallsBackToSelected()
    {
        var lines = new List<ReceiptPayLine>
        {
            new() { PayTerm = "现金", Amount = 0m },
            new() { PayTerm = "挂帐", Amount = 0m },
        };
        Assert.Equal("挂帐", ReceiptSettlement.MainPayTerm(lines, "挂帐"));
    }

    [Fact]
    public void MainPayTerm_AllZeroWithoutFallback_Cash()
    {
        Assert.Equal("现金", ReceiptSettlement.MainPayTerm([], ""));
    }

    [Theory]
    [InlineData(1000, 900, true, true)]   // 实售 1000 / 开单 900 → KH 差额 +100
    [InlineData(900, 1000, true, true)]   // 差额 -100 也写
    [InlineData(1000, 1000, true, false)] // 无差额不写
    [InlineData(1000, 900, false, false)] // 无挂账行不写应收/KH
    public void ShouldWriteDiscountRow_ByTamtToprc(decimal tamt, decimal toprc, bool hasCredit, bool expected)
    {
        Assert.Equal(expected, ReceiptSettlement.ShouldWriteDiscountRow(tamt, toprc, hasCredit));
    }

    [Theory]
    [InlineData("12345", true)]
    [InlineData(" 12345 ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void ShouldWriteInvoiceLedger_OnlyWhenBodyNotEmpty(string? payNo, bool expected)
    {
        Assert.Equal(expected, ReceiptSettlement.ShouldWriteInvoiceLedger(payNo));
    }

    [Fact]
    public void RoundMoney_HalfCentAwayFromZero()
    {
        Assert.Equal(0.01m, ReceiptSettlement.RoundMoney(0.005m));
        Assert.Equal(-0.01m, ReceiptSettlement.RoundMoney(-0.005m));
        Assert.Equal(100.13m, ReceiptSettlement.RoundMoney(100.125m));
    }

    // ---- 挂帐支付方（旧 TXTGZGNAME / row369 L485-496·L561 / tbcskg「启用挂账支付方」） ----

    [Theory]
    [InlineData("挂帐", true)]
    [InlineData("挂帐冲预收", true)]   // SET EXACT OFF：'挂帐' 前缀命中
    [InlineData("汇款", false)]
    [InlineData("托收", false)]
    [InlineData("现金", false)]
    [InlineData(null, false)]
    public void UsesPayer_OnlyCreditTerms(string? payTerm, bool expected)
    {
        Assert.Equal(expected, ReceiptSettlement.UsesPayer(payTerm));
    }

    [Theory]
    [InlineData("挂帐", "甲公司", "乙公司", "乙公司")]      // 支付方非空 → 支付方
    [InlineData("挂帐", "甲公司", "", "甲公司")]            // 支付方为空 → 客户名
    [InlineData("挂帐", "甲公司", "  甲公司  ", "甲公司")]  // 同名（Trim）→ 客户名
    [InlineData("挂帐", "ABC Ltd", "abc ltd", "ABC Ltd")]  // 同名（忽略大小写）→ 客户名
    [InlineData("挂帐", "", "乙公司", "乙公司")]            // 无客户名（开关开）→ 支付方
    [InlineData("汇款", "甲公司", "乙公司", "甲公司")]      // 汇款一律客户名
    [InlineData("托收", "甲公司", "乙公司", "甲公司")]      // 托收一律客户名
    [InlineData("挂帐冲预收", "甲公司", "乙公司", "乙公司")] // EXACT OFF 同「挂帐」
    public void EffectiveGname_FollowsLegacyRules(string payTerm, string customer, string payer, string expected)
    {
        Assert.Equal(expected, ReceiptSettlement.EffectiveGname(customer, payer, payTerm));
    }

    [Fact]
    public void IsSamePayer_TrimsAndIgnoresCase()
    {
        Assert.True(ReceiptSettlement.IsSamePayer(" 甲公司 ", "甲公司"));
        Assert.True(ReceiptSettlement.IsSamePayer("ABC Ltd", "abc ltd"));
        Assert.False(ReceiptSettlement.IsSamePayer("甲公司", "乙公司"));
        Assert.False(ReceiptSettlement.IsSamePayer("甲公司", ""));
        Assert.False(ReceiptSettlement.IsSamePayer("", "乙公司"));
    }

    [Theory]
    [InlineData(false, "", false, "", false)]              // 不挂账 → 不校验
    [InlineData(true, "", false, "乙公司", true)]          // 开关关：支付方无效，必须客户名
    [InlineData(true, "", true, "", true)]                 // 开关开：两者都空 → 拦截
    [InlineData(true, "", true, "乙公司", false)]          // 开关开：支付方非空 → 允许
    [InlineData(true, "", true, "   ", true)]              // 开关开：支付方空白 → 拦截
    [InlineData(true, "甲公司", false, "", false)]         // 有客户名 → 允许
    [InlineData(true, "", false, "", true)]                // 开关关且都空 → 拦截
    public void RequiresCustomerName_SwitchAndPayerCombos(
        bool onCredit, string customer, bool payerEnabled, string payer, bool expected)
    {
        Assert.Equal(expected, ReceiptSettlement.RequiresCustomerName(onCredit, customer, payerEnabled, payer));
    }
}
