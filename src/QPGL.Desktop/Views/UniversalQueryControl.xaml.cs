using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QPGL.Desktop.Services;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>
/// E 组业务查询（第十一批：E1 进库单/E4 订货单/E5 库存修改查询/E6 商品出入/E7 月销售数/E8 营业额/E9 库存检测/EA 进出流水帐）。
/// 统一组件一页覆盖；E2 出库单查询（OutDocQueryControl）/E3 库存查询（StockQueryControl）一期已交。
/// 第十二批备案补齐：fr_sort 排序 / fr_total 分组汇总 / fr_mdefi 自定义字段（三模式）/ 本地删除 / 通用列表打印。
/// </summary>
public partial class UniversalQueryControl : UserControl
{
    private readonly UniversalQueryViewModel _vm = new();

    public UniversalQueryControl()
    {
        InitializeComponent();
        DataContext = _vm;
        Tabs.SelectionChanged += (_, _) => _vm.CurrentTab = Tabs.SelectedIndex;
        _vm.CustomFieldColumnAdded += spec =>
        {
            var grid = FindCurrentGrid();
            grid?.Dispatcher.Invoke(() => QueryGridHelper.AppendColumn(grid, spec));
        };
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e) { }

    /// <summary>当前页签的 DataGrid（命名约定 GrdIssm/GrdOrder/...）。</summary>
    private DataGrid? FindCurrentGrid()
    {
        if (Tabs.SelectedItem is not TabItem tab) return null;
        return FindChild<DataGrid>(tab);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            var result = FindChild<T>(child);
            if (result is not null) return result;
        }
        // 逻辑树兜底（DataGrid 在 ContentTemplate 外层时）
        if (parent is ContentControl { Content: ContentControl cc })
            return FindChild<T>(cc);
        if (parent is ContentControl { Content: DependencyObject d })
            return FindChild<T>(d);
        return null;
    }
}
