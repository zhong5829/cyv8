using System.Globalization;
using System.Windows.Data;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Views;

/// <summary>
/// 价格列掩码转换器（旧 cmm_class ___.row331 L873-880 语义复刻）：
/// 未授权时清空显示 → 单元格空白、列仍占位（旧"清 ControlSource"语义）。
/// </summary>
public sealed class PriceMaskConverter : IValueConverter
{
    /// <summary>掩码类别：Cost=成本 IPRC('−')；Profit=毛利('*')；Iprj=外币价('~')。</summary>
    public string Kind { get; set; } = "Cost";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = AppSession.Permission;
        var visible = Kind switch
        {
            "Profit" => p.CanSeeProfit,
            "Iprj" => p.CanSeeIprj,
            _ => p.CanSeeCost,
        };
        return visible ? value ?? "" : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
