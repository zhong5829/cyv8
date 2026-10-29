using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace QPGL.Desktop.Converters;

/// <summary>
/// 负数金额红色字体（2026-10-29 需求）：绑定到单元格 TextBlock 自身的 Text（N2 格式化后的字符串），
/// 可解析为负数时返回红色，其余返回 UnsetValue 保持默认前景色（含"0.00"、空、非数字文本）。
/// 用法：NumCellRightNeg 样式（Theme.xaml）内 Foreground 绑定 RelativeSource Self / Text。
/// </summary>
public class NegativeBrushConverter : IValueConverter
{
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return DependencyProperty.UnsetValue;
        // N2 格式含千分位逗号；NumberStyles.Number 允许前后空白+千分位+小数
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n < 0)
            return RedBrush;
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
