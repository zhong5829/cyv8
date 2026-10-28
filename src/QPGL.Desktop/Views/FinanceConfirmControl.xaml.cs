using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>
/// 财务确认页（G.1，旧 fr_sk 复刻）。四个实现页签切换时显示对应列表（旧页框 PAF1 同刻）。
/// </summary>
public partial class FinanceConfirmControl : UserControl
{
    private readonly FinanceConfirmViewModel _vm = new();

    public FinanceConfirmControl()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FinanceConfirmViewModel.ActivePage)) UpdateListVisibility();
        };
        _vm.ActivePage = FinanceConfirmViewModel.PageOut;
        Loaded += async (_, _) =>
        {
            UpdateListVisibility();
            await _vm.EnsurePaperTypesAsync();
            await _vm.SearchAsync(System.Threading.CancellationToken.None);
        };
    }

    /// <summary>ActivePage 变化 → 仅显示当前页签列表（旧 setsource：其余网格隐藏）。</summary>
    public void UpdateListVisibility()
    {
        if (DataContext is not FinanceConfirmViewModel vm) return;
        var page = vm.ActivePage;
        // Grid 内四个 DataGrid 顺序：出库/入库/调拨/代销
        var grids = FindGrids();
        if (grids.Count < 4) return;
        grids[0].Visibility = page == FinanceConfirmViewModel.PageOut ? Visibility.Visible : Visibility.Collapsed;
        grids[1].Visibility = page == FinanceConfirmViewModel.PageIn ? Visibility.Visible : Visibility.Collapsed;
        grids[2].Visibility = page == FinanceConfirmViewModel.PageTransfer ? Visibility.Visible : Visibility.Collapsed;
        grids[3].Visibility = page == FinanceConfirmViewModel.PageConsign ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<System.Windows.Controls.DataGrid> FindGrids()
    {
        if (FindName("ListHost") is not Grid host) return [];
        return host.Children.OfType<System.Windows.Controls.DataGrid>().ToList();
    }
}
