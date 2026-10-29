using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;
using QPGL.Domain.Sales;

namespace QPGL.Desktop.Views;

public partial class SaleReturnControl : UserControl
{
    private readonly SaleReturnViewModel _vm = new();

    public SaleReturnControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void CustomerBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.DebounceCustomerSearch();
        CustPopup.IsOpen = true;
    }

    // ==== 三条件过滤框（编码/名称/车型）：实时+防抖；聚焦/点击全选；←/→ 在文本边界切换相邻框 ====
    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => _vm.DebounceFilter();

    private void FilterBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    private void FilterBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox box && !box.IsKeyboardFocusWithin)
        {
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var move = e.Key switch
        {
            Key.Left when box.CaretIndex == 0 => -1,
            Key.Right when box.CaretIndex == box.Text.Length => 1,
            _ => 0,
        };
        if (move == 0) return;
        // 三个查询框按 XAML 顺序相邻切换
        TextBox? target = (box == CpBox) ? (move > 0 ? Na1Box : null)
                        : (box == Na1Box) ? (move > 0 ? TyBox : CpBox)
                        : (move > 0 ? null : Na1Box);
        if (target is null) return;
        target.Focus();
        target.SelectAll();
        e.Handled = true;
    }

    private async void CustomerBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (CustList.Items.Count > 0)
                {
                    CustPopup.IsOpen = true;
                    // 焦点保持在输入框，手动下移选中项（避免焦点跳入列表后按键失效）
                    CustList.SelectedIndex = Math.Min(CustList.SelectedIndex + 1, CustList.Items.Count - 1);
                    CustList.ScrollIntoView(CustList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (CustList.Items.Count > 0 && CustList.SelectedIndex > 0)
                {
                    CustList.SelectedIndex--;
                    CustList.ScrollIntoView(CustList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (CustPopup.IsOpen && CustList.SelectedItem is Domain.Partners.Partner p)
                {
                    _vm.ApplyCustomer(p);
                    CustPopup.IsOpen = false;
                    // 回车确定客户后直接加载可退行
                    await _vm.LoadSourceAsync();
                }
                else
                {
                    await _vm.LoadSourceAsync();
                }
                e.Handled = true;
                break;
        }
    }

    private async void CustList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CustList.SelectedItem is Domain.Partners.Partner p)
        {
            _vm.ApplyCustomer(p);
            CustPopup.IsOpen = false;
            await _vm.LoadSourceAsync();
        }
    }

    /// <summary>上区双击可退行 → 退货编辑窗（添加）。</summary>
    private async void ReturnableGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReturnableGrid.SelectedItem is not ReturnableRow row) return;
        await OpenEditorAsync(row, null);
    }

    /// <summary>下区双击明细行 → 退货编辑窗（修改，销售开单页同款交互）。</summary>
    private async void LineGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LineGrid.SelectedItem is not ReturnLineRow line) return;
        var src = _vm.FindSource(line.RowId);
        if (src is null) return;
        await OpenEditorAsync(src, line);
    }

    private async System.Threading.Tasks.Task OpenEditorAsync(ReturnableRow src, ReturnLineRow? editing)
    {
        var owner = Window.GetWindow(this);
        IReadOnlyList<ReturnBatchOption> batches;
        try
        {
            batches = await _vm.GetBatchOptionsAsync(src.Cp, src.Nno);
        }
        catch
        {
            batches = [];
        }
        var dialog = new ReturnLineEditDialog(
            editing is null ? "添加" : "修改",
            $"{src.Cp}  {src.Nno}  {src.Na1}",
            editing?.Oprc ?? src.Oprc,
            editing?.Oprct ?? src.Oprct,
            src.Qty, src.Returned, src.Remain,
            editing?.DepotNo ?? src.DepotNo,
            editing?.Posi ?? src.Posi,
            batches, editing?.IsScrap ?? false)
        { Owner = owner };
        if (dialog.ShowDialog() == true)
        {
            _vm.ApplyDetail(src, dialog.ReturnQty, dialog.Oprc, dialog.Oprct, dialog.DepotNo, dialog.Posi, dialog.ScrapWarehouse);
        }
    }
}
