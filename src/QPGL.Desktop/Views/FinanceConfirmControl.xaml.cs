using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;
using QPGL.Domain.Partners;

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

    // ── 筛选栏客户/供应商联想（与 InStockControl / OutDocQueryControl 同机制）：
    // 输入即搜（300ms 防抖，VM 侧计时器），↑↓ 移动、回车选中、Esc 关闭、失焦收起、点击选中 ──

    private void SuggestBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string tag) return;
        if (tag == "in") _vm.DebounceSupplierSearch();
        else _vm.DebounceCustomerSearch(tag);
        if (SiblingPopup(box) is { } popup) popup.IsOpen = box.Text.Trim().Length > 0;
    }

    private void SuggestBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var popup = SiblingPopup(box);
        var list = popup is null ? null : PopupList(popup);
        if (popup is null || list is null) return;

        switch (e.Key)
        {
            case Key.Down:
                if (list.Items.Count > 0)
                {
                    popup.IsOpen = true;
                    list.SelectedIndex = System.Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
                    list.ScrollIntoView(list.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (list.Items.Count > 0 && list.SelectedIndex > 0)
                {
                    list.SelectedIndex--;
                    list.ScrollIntoView(list.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                // 下拉展开时回车=选中高亮项；未展开时保持原行为（不触发查询）
                if (popup.IsOpen)
                {
                    if (list.SelectedItem is not null)
                    {
                        ApplySuggestion(box, list.SelectedItem);
                        box.Focus();
                        box.CaretIndex = box.Text.Length;
                    }
                    popup.IsOpen = false;
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (popup.IsOpen)
                {
                    popup.IsOpen = false;
                    e.Handled = true;
                }
                break;
        }
    }

    private void SuggestBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var popup = SiblingPopup(box);
        if (popup is null) return;
        // 点击下拉项会先触发 LostFocus：延后判断鼠标是否仍在下拉内，避免误收起导致选不中
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (PopupList(popup) is { IsMouseOver: true }) return;
            popup.IsOpen = false;
        }));
    }

    private void SuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || list.Tag is not string tag) return;
        var box = AnchorFor(tag);
        if (box is null || list.SelectedItem is null) return;
        ApplySuggestion(box, list.SelectedItem);
        if (SiblingPopup(box) is { } popup) popup.IsOpen = false;
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }

    /// <summary>选中联想项：客户/供应商都只回填名称（筛选 SQL 走 gname/gyname，编号不写入该框）。</summary>
    private void ApplySuggestion(TextBox box, object item)
    {
        var tag = (box.Tag as string) ?? "";
        if (tag == "in")
        {
            if (item is SupplierRow s) _vm.ApplySupplier(s);
        }
        else if (item is Partner p)
        {
            _vm.ApplyCustomer(p, tag);
        }
    }

    private TextBox? AnchorFor(string tag) => tag switch
    {
        "out" => OutGnameBox,
        "in" => InGynameBox,
        "tr" => TrGnameBox,
        "cs" => CsGnameBox,
        _ => null,
    };

    private static Popup? SiblingPopup(FrameworkElement anchor) =>
        anchor.Parent is Grid grid ? grid.Children.OfType<Popup>().FirstOrDefault() : null;

    private static ListBox? PopupList(Popup popup) =>
        (popup.Child as Border)?.Child as ListBox;
}
