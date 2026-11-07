using System.Globalization;
using System.Windows.Data;

namespace QPGL.Desktop.Converters;

/// <summary>
/// 数量不足 1 判定（2026-11-07 需求）：库存 &lt; 1（含 0、0.1～0.9、负库存）返回 true，
/// 与数量列 QtyInt 截断显示口径一致（显示为 0 即命中）。仅用于样式触发器，不参与查询/数据。
/// </summary>
public sealed class LessThanOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        decimal n = value switch
        {
            decimal d => d,
            double d => (decimal)d,
            float f => (decimal)f,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            _ => 0m
        };
        return n < 1m;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
