using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QPGL.Desktop.ViewModels;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.Views;

/// <summary>收款/付款管理页（G.2/G.3，fr_xlsk）：Tag=收款/付款（供列头文案），VM 按类型复用。</summary>
public partial class XlskControl : UserControl
{
    private readonly XlskViewModel _vm;

    public XlskControl(string type)
    {
        InitializeComponent();
        Tag = type;
        _vm = new XlskViewModel(type);
        DataContext = _vm;
        // DataGridColumn 不在可视树，Header 的 {Binding} 取不到 DataContext（显示空白）→ 此处按类型赋值
        ColBcColumn.Header = _vm.ColBc;
        ColMarkColumn.Header = _vm.ColMark;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>点击收款/付款金额输入框 → 全选，便于直接覆盖输入。</summary>
    private void PaySumBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        tb.Focus();
        tb.SelectAll();
        e.Handled = true;   // 阻止默认的鼠标定位，保留全选
    }

    // ==== 客户联想下拉（与 SaleControl GnameBox 同机制）====
    private void GnameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.DebounceCustomerSearch();
        // 仅用户在本框内输入时才展开联想；双击活跃客户/载入单据等程序赋值不弹下拉
        CustPopup.IsOpen = GnameBox.IsKeyboardFocusWithin;
    }

    private void GnameBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Down:
                if (CustList.Items.Count > 0)
                {
                    CustPopup.IsOpen = true;
                    // 焦点保持在输入框，手动下移选中项（避免焦点跳入列表后按键失效）
                    CustList.SelectedIndex = Math.Min(CustList.SelectedIndex + 1, CustList.Items.Count - 1);
                    CustList.ScrollIntoView(CustList.SelectedItem);
                }
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Up:
                if (CustList.Items.Count > 0 && CustList.SelectedIndex > 0)
                {
                    CustList.SelectedIndex--;
                    CustList.ScrollIntoView(CustList.SelectedItem);
                }
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter:
                if (CustPopup.IsOpen && CustList.SelectedItem is Domain.Partners.Partner p)
                {
                    _vm.ApplyCustomer(p);
                    CustPopup.IsOpen = false;
                }
                else
                {
                    // 无联想选中时保持旧语义：Enter 直接拉单（同 Go）
                    _ = _vm.LoadCustomerCommand.ExecuteAsync(null);
                }
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Escape:
                CustPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    // ==== 勾选列：Shift+鼠标左键拖动连续勾选（沿用起点状态；仅从勾选列按下生效） ====
    private bool _draggingCheck;
    private bool _dragCheckValue;

    private void LinesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift) return;
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is null || cell.Column != LinesGrid.Columns[0]) return;
        if (cell.DataContext is not XlskLineVm line) return;
        _draggingCheck = true;
        _dragCheckValue = !line.IsChecked;   // 起点已勾 → 拖动取消；起点未勾 → 拖动全勾
        SetLineChecked(line, _dragCheckValue);
        LinesGrid.CaptureMouse();
        e.Handled = true;
    }

    private void LinesGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingCheck) return;
        if (e.LeftButton != MouseButtonState.Pressed || Keyboard.Modifiers != ModifierKeys.Shift)
        {
            EndCheckDrag();
            return;
        }
        var row = RowAt(e.GetPosition(LinesGrid));
        if (row?.Item is XlskLineVm line && line.IsChecked != _dragCheckValue)
            SetLineChecked(line, _dragCheckValue);
        e.Handled = true;
    }

    /// <summary>勾选联动（旧码 columnclick）：勾选即本次金额取未结额，取消即清零。</summary>
    private static void SetLineChecked(XlskLineVm line, bool value)
    {
        line.IsChecked = value;
        line.Bcje = value ? line.Wsje : 0;
    }

    private void LineCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: XlskLineVm line })
            SetLineChecked(line, line.IsChecked);
    }

    // ==== 业务明细：双击明细行打开（勾选列/本次金额列除外，避免与勾选、编辑冲突） ====
    private async void LinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is not null && (cell.Column == LinesGrid.Columns[0] || cell.Column == ColBcColumn)) return;
        var row = RowAt(e.GetPosition(LinesGrid));
        if (row?.Item is XlskLineVm line)
            await _vm.ShowBizDetailForLineAsync(line);
    }

    private void LinesGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingCheck) return;
        EndCheckDrag();
        e.Handled = true;
    }

    private void LinesGrid_LostMouseCapture(object sender, MouseEventArgs e) => _draggingCheck = false;

    private void EndCheckDrag()
    {
        _draggingCheck = false;
        if (LinesGrid.IsMouseCaptured) LinesGrid.ReleaseMouseCapture();
    }

    /// <summary>命中测试定位鼠标下的明细行。</summary>
    private DataGridRow? RowAt(Point p)
    {
        DataGridRow? row = null;
        VisualTreeHelper.HitTest(LinesGrid, null, r =>
        {
            var found = FindAncestor<DataGridRow>(r.VisualHit);
            if (found is null) return HitTestResultBehavior.Continue;
            row = found;
            return HitTestResultBehavior.Stop;
        }, new PointHitTestParameters(p));
        return row;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ==== 活跃客户面板 ====
    private void PasteSort_Click(object sender, RoutedEventArgs e)
    {
        var win = new ActiveCustomerSortWindow();
        if (win.ShowDialog() != true) return;
        _vm.ApplyCustomOrder(win.NamesText);
    }

    private void ResetSort_Click(object sender, RoutedEventArgs e) => _vm.ResetActiveOrder();

    private async void ActiveCustList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ActiveCustList.SelectedItem is ActiveCustomerRow row)
        {
            await _vm.PullActiveCustomerAsync(row);
            CustPopup.IsOpen = false;   // 程序回填客户名不弹联想下拉
        }
    }

    private void CustList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CustList.SelectedItem is Domain.Partners.Partner p)
        {
            _vm.ApplyCustomer(p);
            CustPopup.IsOpen = false;
            GnameBox.Focus();
            GnameBox.CaretIndex = GnameBox.Text.Length;
        }
    }
}
