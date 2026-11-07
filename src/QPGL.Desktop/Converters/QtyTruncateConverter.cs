using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QPGL.Desktop.Converters;

/// <summary>
/// 数量列统一显示口径（2026-11-07 需求）：按旧系统 VFP INT() 语义向零截断为整数
/// （2.6→2、-2.6→-2），仅影响显示，不参与业务计算；金额/比率/重量等非件数列不使用本转换器。
/// 全局单例实现，统一在 Theme.xaml 注册为 QtyInt，各页列只引用资源，不各写一份格式。
/// 可编辑数量列：ConvertBack 原样回填用户输入（不做截断），明细数值保持原值。
/// </summary>
public sealed class QtyTruncateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null or DBNull) return string.Empty;
        decimal d;
        switch (value)
        {
            case decimal m:
                d = m;
                break;
            case double db when !double.IsNaN(db) && !double.IsInfinity(db):
                d = (decimal)db;
                break;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                d = (decimal)f;
                break;
            case int i:
                d = i;
                break;
            case long l:
                d = l;
                break;
            case short s:
                d = s;
                break;
            case byte b:
                d = b;
                break;
            case string text when !string.IsNullOrWhiteSpace(text):
                if (!decimal.TryParse(text, NumberStyles.Number, culture ?? CultureInfo.InvariantCulture, out d))
                    return text;
                break;
            default:
                return value.ToString() ?? string.Empty;
        }
        // 向零截断（旧系统 INT() 语义），再按整数格式输出（不带小数、不带千分位）
        return Format(d);
    }

    /// <summary>数量显示文本（非 Binding 场景复用同一口径：打印票样、状态栏等）。</summary>
    public static string Format(decimal value) => decimal.Truncate(value).ToString("0", CultureInfo.InvariantCulture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal or double or float or int or long or short or byte) return value;
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return DependencyProperty.UnsetValue;
        if (decimal.TryParse(text, NumberStyles.Number, culture ?? CultureInfo.InvariantCulture, out var d))
            return d;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out d))
            return d;
        return DependencyProperty.UnsetValue;
    }
}
