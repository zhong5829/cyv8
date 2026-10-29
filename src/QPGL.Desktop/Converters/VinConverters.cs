using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace QPGL.Desktop.Converters;

/// <summary>VIN 查询窗口专用转换器（qp11 迁移；仅保留 VIN 窗实际用到的转换器，
/// bool→Visibility 直接复用 App 级 BoolToVis）</summary>

/// <summary>bool反转 → Visibility（true=Collapsed, false=Visible）</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>bool反转（true→false, false→true）</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;
}

/// <summary>非空字符串 → Visible，null/空 → Collapsed</summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s) return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
        return value is null ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非null对象 → Visible，null → Collapsed</summary>
public class ObjectToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Brush：true=编号命中高亮色(#E53935)，false=普通色(#333333)</summary>
public class HighlightBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Highlight = new(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly SolidColorBrush Normal = new(Color.FromRgb(0x33, 0x33, 0x33));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Highlight : Normal;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
