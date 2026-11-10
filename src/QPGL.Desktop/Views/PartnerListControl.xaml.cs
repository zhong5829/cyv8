using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class PartnerListControl : UserControl
{
    /// <summary>距底部阈值：DataGrid 逻辑滚动时 ScrollViewer 的单位是「行」，留 5 行预取下一批。</summary>
    private const double LoadThreshold = 5;

    private readonly PartnerListViewModel _vm = new();

    public PartnerListControl()
    {
        InitializeComponent();
        DataContext = _vm;

        KeywordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _ = SearchAndScrollTopAsync();
        };
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.SearchAsync();
        ScrollToTop();
        await FillViewportAsync();
    }

    /// <summary>关键字查询：从第 1 批重新加载，并回到顶部。</summary>
    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAndScrollTopAsync();

    private async System.Threading.Tasks.Task SearchAndScrollTopAsync()
    {
        await _vm.SearchAsync();
        ScrollToTop();
        await FillViewportAsync();
    }

    /// <summary>接近底部时追加下一批；IsLoading 在 VM 内同步拦截，滚动事件再多也只会有一个请求在飞。</summary>
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

    private async void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        await _vm.SortByAsync(e.Column.SortMemberPath);
        ScrollToTop();
        await FillViewportAsync();
    }

    /// <summary>首批不足一屏时继续补批（否则没有滚动条，用户无法触发加载）。</summary>
    private async System.Threading.Tasks.Task FillViewportAsync()
    {
        Grid.UpdateLayout();
        var sv = FindScrollViewer();
        while (sv is not null && _vm.HasMore && sv.ExtentHeight <= sv.ViewportHeight + 1)
        {
            var before = _vm.Items.Count;
            await _vm.LoadMoreAsync();
            if (_vm.Items.Count == before) break;
            sv.UpdateLayout();
        }
    }

    // ---- M5 客户资料增删改（fr_gu 契约） ----

    private async void OnAddPartner(object sender, RoutedEventArgs e)
    {
        var loadedRows = _vm.Items.Count;
        var w = new PartnerEditWindow(null, "", "", "", "", "", "", "", "", 0, "");
        w.ShowDialog();
        if (!w.Saved) return;
        await _vm.AfterSavedAsync(w.ViewModel.Gno, true, loadedRows);
        ScrollSelectionIntoView();
    }

    private async void OnEditPartner(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not Domain.Partners.Partner p)
        { MessageBox.Show("请先选择客户行"); return; }
        var loadedRows = _vm.Items.Count;
        var w = new PartnerEditWindow(p.Id, p.PartNo, p.PartName, p.PartName, p.LinkMan, p.Tel, p.Mobile, p.Address, p.Note, p.DiscountRate, p.SalesMan);
        w.ShowDialog();
        if (!w.Saved) return;
        await _vm.AfterSavedAsync(p.PartNo, false, loadedRows);
        ScrollSelectionIntoView();
    }

    private async void OnDeletePartner(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not Domain.Partners.Partner p)
        { MessageBox.Show("请先选择客户行"); return; }
        if (MessageBox.Show($"确认删除客户 {p.PartNo} {p.PartName}？", "删除确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var loadedRows = _vm.Items.Count;
            await QPGL.Desktop.Services.Db.PartnerManage.DeleteAsync(p.Id);
            MessageBox.Show("已删除");
            await _vm.AfterDeletedAsync(loadedRows);
            await FillViewportAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ScrollSelectionIntoView()
    {
        if (_vm.SelectedItem is not null) Grid.ScrollIntoView(_vm.SelectedItem);
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
