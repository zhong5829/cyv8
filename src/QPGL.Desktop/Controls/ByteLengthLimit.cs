using System.Windows;
using System.Windows.Controls;

namespace QPGL.Desktop.Controls;

/// <summary>
/// TextBox 字节长度限制（附加属性）：按数据库 varchar/char 列的字节宽度限制输入，
/// 超限从尾部逐字裁剪。GB 系列编码下汉字/全角 = 2 字节、ASCII = 1 字节——
/// WPF 自带 MaxLength 按字符数计，无法表达"7 个汉字与 14 个字母同为 14 字节"的语义；
/// 无限制时超长值直达 SQL Server 报"将截断字符串或二进制数据"（2026-11-02 销售开单车型截断 bug）。
/// 用法：xmlns:controls="clr-namespace:QPGL.Desktop.Controls" ... controls:ByteLengthLimit.MaxBytes="14"
/// </summary>
public static class ByteLengthLimit
{
    public static readonly DependencyProperty MaxBytesProperty = DependencyProperty.RegisterAttached(
        "MaxBytes", typeof(int), typeof(ByteLengthLimit), new PropertyMetadata(0, OnMaxBytesChanged));

    public static void SetMaxBytes(DependencyObject obj, int value) => obj.SetValue(MaxBytesProperty, value);
    public static int GetMaxBytes(DependencyObject obj) => (int)obj.GetValue(MaxBytesProperty);

    private static void OnMaxBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        if ((int)e.NewValue > 0) tb.TextChanged += TrimToByteLimit;
        else tb.TextChanged -= TrimToByteLimit;
    }

    private static void TrimToByteLimit(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var max = GetMaxBytes(tb);
        var text = tb.Text;
        if (max <= 0 || ByteLength(text) <= max) return;

        var end = text.Length;
        while (end > 0 && ByteLength(text[..end]) > max) end--;
        var caret = tb.CaretIndex;
        tb.Text = text[..end];   // 重设 Text 会再触发本方法，此时已不超限，无递归
        tb.CaretIndex = Math.Min(caret, tb.Text.Length);
    }

    /// <summary>CP936/GB 字节宽：汉字及全角字符 2 字节，ASCII 1 字节。</summary>
    public static int ByteLength(string text)
    {
        var n = 0;
        foreach (var c in text) n += c > 0xFF ? 2 : 1;
        return n;
    }
}
