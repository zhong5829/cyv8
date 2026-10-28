using QPGL.Domain.Printing;

namespace QPGL.Tests;

/// <summary>人民币大写（对齐 pg_proc.OOSIN6 语义）。</summary>
public class MoneyUpperTests
{
    [Theory]
    [InlineData(0, "零元整")]
    [InlineData(71.50, "柒拾壹元伍角整")]
    [InlineData(100.00, "壹佰元整")]
    [InlineData(0.05, "零元伍分")]
    [InlineData(1.05, "壹元零伍分")]
    [InlineData(10.00, "壹拾元整")]
    [InlineData(1234.56, "壹仟贰佰叁拾肆元伍角陆分")]
    [InlineData(100000.00, "壹拾万元整")]
    [InlineData(1000010.50, "壹佰万零壹拾元伍角整")]
    [InlineData(-8.30, "负捌元叁角整")]
    public void ToUpper_StandardCases(decimal input, string expected)
        => Assert.Equal(expected, MoneyUpper.ToUpper(input));
}
