using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class SupplierControl : UserControl
{
    /// <summary>距底部阈值：DataGrid 逻辑滚动时 ScrollViewer 的单位是「行」，留 5 行预取下一批。</summary>
    private const double LoadThreshold = 5;

    private readonly SupplierViewModel _vm = new();

    public SupplierControl()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += SupplierControl_Loaded;

        // 保存/修改后 VM 会定位选中行：这里把它滚进可视区，保证「保存后立刻能看到」
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SupplierViewModel.SelectedRow) && _vm.SelectedRow is not null)
                Grid.ScrollIntoView(_vm.SelectedRow);
        };
    }

    private async void SupplierControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.SearchAsync(System.Threading.CancellationToken.None);
        ScrollToTop();
        await FillViewportAsync();
    }

    /// <summary>关键字查询：从第 1 批重新加载，并回到顶部。</summary>
    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SearchAsync(System.Threading.CancellationToken.None);
        ScrollToTop();
        await FillViewportAsync();
    }

    /// <summary>接近底部时追加下一批；IsBusy 在 VM 内同步拦截，滚动事件再多也只会有一个请求在飞。</summary>
    private async void Grid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 只接管用户真实滚动：数据追加会让 DataGrid 重新锚定并产生 VerticalChange，必须排除（ExtentHeightChange != 0）
        if (e.ExtentHeightChange != 0 || e.VerticalChange == 0) return;
        // 附加事件挂在 DataGrid 上，sender 是 DataGrid，真正的滚动条在 OriginalSource
        if (e.OriginalSource is not ScrollViewer sv) return;
        if (sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset > LoadThreshold) return;

        var offsetBefore = sv.VerticalOffset;
        await _vm.LoadMoreAsync();
        // DataGrid 在末尾追加行后会把视图锚到新的底部（表现为"跳到底"）：等布局结束后恢复到加载前的位置
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        if (sv.VerticalOffset > offsetBefore && sv.VerticalOffset >= sv.ExtentHeight - sv.ViewportHeight - 1)
            sv.ScrollToVerticalOffset(offsetBefore);
    }

    /// <summary>首批不足一屏时继续补批（否则没有滚动条，用户无法触发加载）。</summary>
    private async System.Threading.Tasks.Task FillViewportAsync()
    {
        Grid.UpdateLayout();
        var sv = FindScrollViewer();
        while (sv is not null && _vm.HasMore && sv.ExtentHeight <= sv.ViewportHeight + 1)
        {
            var before = _vm.Rows.Count;
            await _vm.LoadMoreAsync();
            if (_vm.Rows.Count == before) break;
            sv.UpdateLayout();
        }
    }

    private void ScrollToTop() => FindScrollViewer()?.ScrollToTop();

    private ScrollViewer? FindScrollViewer() => FindChild<ScrollViewer>(Grid);

    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deep) return deep;
        }
        return null;
    }
}
