using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class PurchaseReturnControl : UserControl
{
    private readonly PurchaseReturnViewModel _vm = new();
    private readonly DispatcherTimer _gyDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    /// <summary>选中联想项回填文本时抑制再次搜索（避免下拉重开）。</summary>
    private bool _suppressGySearch;

    public PurchaseReturnControl()
    {
        InitializeComponent();
        DataContext = _vm;
        _gyDebounce.Tick += async (_, _) =>
        {
            _gyDebounce.Stop();
            await SearchSuppliersAsync();
        };
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);

    /// <summary>F3 = 退货开单/单据查询双模式切换（2026-11-04 需求）。</summary>
    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F3) return;
        e.Handled = true;
        _ = _vm.ToggleModeAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>供应商联想搜索：合并 tbgu + tbgugys（tbgu 优先）按 拼音码 code/数字码 code1/名称/编号 匹配，取 200 条。</summary>
    private async System.Threading.Tasks.Task SearchSuppliersAsync()
    {
        if (_suppressGySearch) { _suppressGySearch = false; return; }
        var kw = GyBox.Text.Trim();
        try
        {
            var request = new PagedRequest { Page = 1, PageSize = 200, Sort = "partName", Order = "asc", Keyword = kw };
            var result = await Db.Suppliers.SearchMergedAsync(request);
            GyList.ItemsSource = result.Items;
            GyPopup.IsOpen = result.Items.Count > 0;
            if (GyList.Items.Count > 0) GyList.SelectedIndex = 0;
        }
        catch
        {
            // 联想失败不阻断输入
        }
    }

    private void GyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _gyDebounce.Stop();
        _gyDebounce.Start();
    }

    /// <summary>聚焦且未输入时列出供应商（下拉形式：点开即见列表）。</summary>
    private void GyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (GyBox.Text.Trim().Length == 0)
        {
            _gyDebounce.Stop();
            _gyDebounce.Start();
        }
    }

    private void GyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (GyList.Items.Count > 0)
                {
                    GyPopup.IsOpen = true;
                    GyList.SelectedIndex = System.Math.Min(GyList.SelectedIndex + 1, GyList.Items.Count - 1);
                    GyList.ScrollIntoView(GyList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (GyList.Items.Count > 0 && GyList.SelectedIndex > 0)
                {
                    GyList.SelectedIndex--;
                    GyList.ScrollIntoView(GyList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter when GyPopup.IsOpen && GyList.SelectedItem is SupplierRow s1:
                SelectSupplier(s1);
                e.Handled = true;
                break;
            case Key.Escape when GyPopup.IsOpen:
                GyPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void GyList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GyList.SelectedItem is SupplierRow s)
            SelectSupplier(s);
        GyPopup.IsOpen = false;
    }

    /// <summary>查询区输入框顺序（入库单号→配件编号→名称→车型）；供应商框已移至操作条（2026-11-04 需求）。</summary>
    private System.Windows.Controls.TextBox[] QueryBoxes => [CnoBox, NnoBox, Na1Box, TyBox];

    /// <summary>查询区 ←/→ 在输入框间切换并全选（2026-11-04 需求）；GyPopup 打开时其自身键位优先不受影响。</summary>
    private void QueryBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        var boxes = QueryBoxes;
        var idx = Array.IndexOf(boxes, Keyboard.FocusedElement as System.Windows.Controls.TextBox);
        if (idx < 0) return;   // 焦点不在查询区文本框（如日期控件）不拦截
        e.Handled = true;
        var next = e.Key == Key.Right
            ? boxes[Math.Min(idx + 1, boxes.Length - 1)]
            : boxes[Math.Max(idx - 1, 0)];
        next.Focus();
        next.SelectAll();
    }

    /// <summary>回填供应商名称（查询条件 Gyname 前缀随 PropertyChanged 同步更新）。</summary>
    private void SelectSupplier(Domain.Partners.SupplierRow s)
    {
        _suppressGySearch = true;
        GyBox.Text = s.Gyname.Trim();
        GyBox.CaretIndex = GyBox.Text.Length;
        GyPopup.IsOpen = false;
        // 2026-11-04 需求：选中供应商后立即按供应商加载流水
        _ = _vm.SearchNowAsync();
    }

    /// <summary>双击可退流水行 = 弹退货编辑窗设置数量/价格，确认后加入退货车（2026-11-04 需求）。</summary>
    private void LinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenLineEditDialog();

    /// <summary>配件区回车 = 弹退货编辑窗（与双击同语义）；单元格编辑态不拦截。</summary>
    private void LinesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        if (grid.ItemContainerGenerator.ContainerFromItem(grid.CurrentItem) is System.Windows.Controls.DataGridRow row && row.IsEditing) return;
        e.Handled = true;
        OpenLineEditDialog();
    }

    /// <summary>明细区（退货车）Del 键删除选中行：与"删除退货行"按钮同一命令；单元格编辑态不拦截。</summary>
    private void CartGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        // 单元格编辑中（正在删文本）不删行
        if (grid.ItemContainerGenerator.ContainerFromItem(grid.CurrentItem) is System.Windows.Controls.DataGridRow row && row.IsEditing) return;
        if (_vm.SelectedCart is null) return;
        _vm.RemoveCartCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>明细区右键：先选中右键所在行，保证菜单删除的是右键指向的行（2026-11-04 需求）。</summary>
    private void CartGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        DependencyObject d = e.OriginalSource as DependencyObject;
        while (d is not null and not System.Windows.Controls.DataGridRow)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is System.Windows.Controls.DataGridRow r)
            grid.SelectedItem = r.Item;
    }

    /// <summary>弹出退货行编辑窗（数量默认=可退数，进价取源行；BA01 锁价时进价只读），确认后加入退货车明细区（外币价取源行；勾选"不扣库存"按行透传）。</summary>
    private void OpenLineEditDialog()
    {
        if (_vm.SelectedLine is not Infrastructure.Stock.ReturnableLine line) return;
        var dialog = new PurchaseReturnLineDialog(line, _vm.PriceLocked) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        _vm.AddToCart(dialog.Amount, line.Iprj, dialog.Iprc, dialog.NoStock);
    }
}
