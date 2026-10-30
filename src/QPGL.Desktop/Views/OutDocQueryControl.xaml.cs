using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class OutDocQueryControl : UserControl
{
    private readonly OutDocQueryViewModel _vm = new();

    /// <summary>「修改」事件：携带选中单据（paper/type/dpm）通知宿主切换到开单模式并载入。</summary>
    public event EventHandler<(string Paper, string Type, string Dpm)>? EditBillRequested;

    public OutDocQueryControl()
    {
        InitializeComponent();
        DataContext = _vm;
        // 无限加载：ScrollChanged 从 DataGrid 内部 ScrollViewer 冒泡（路由事件），无需等可视化树生成
        DgBills.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(DgBills_ScrollChanged));
        Loaded += async (_, _) => await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }

    private async void DgBills_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // 路由事件挂在 DgBills 上：sender 是 DataGrid，真正的 ScrollViewer 在 OriginalSource
        if (e.OriginalSource is not System.Windows.Controls.ScrollViewer sv) return;
        // 到底（含本次因追加行导致 ExtentHeight 变化的回调）：触发加载，防重入由 VM 守卫
        if (sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 40)
            await _vm.LoadMoreAsync(System.Threading.CancellationToken.None);
    }

    // 客户联想（与开单模式 GnameBox 同机制）：输入即搜（300ms 防抖），支持拼音码/数字码/编号/名称
    private void GnameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.DebounceCustomerSearch();
        CustPopup.IsOpen = true;
    }

    private void GnameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (_vm.CustomerSuggestions.Count > 0)
                {
                    CustPopup.IsOpen = true;
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
                }
                else
                {
                    CustPopup.IsOpen = false;
                    // 直接查询（输入即筛选条件）
                    _ = _vm.SearchAsync(System.Threading.CancellationToken.None);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                CustPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void CustList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CustList.SelectedItem is Domain.Partners.Partner p)
        {
            _vm.ApplyCustomer(p);
            CustPopup.IsOpen = false;
            GnameBox.Focus();
            GnameBox.CaretIndex = GnameBox.Text.Length;
        }
    }

    /// <summary>从开单模式切入时调用：日期重置为当天（起止同日）、拉取操作员名单并立即查询刷新。</summary>
    public async void RefreshToToday()
    {
        _vm.DateFrom = DateTime.Today;
        _vm.DateTo = DateTime.Today;
        await _vm.EnsureOperatorsAsync();
        await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>修改：校验选中行并携带单据数据切回开单模式（2026-10-29：退单不支持修改）。</summary>
    private void EditBill_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedBill is null)
        {
            System.Windows.MessageBox.Show("请先选择单据行", "修改", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var bill = _vm.SelectedBill;
        // 退货单拦截（旧系统退单语义：行 ckamount<0，单据行 TotalQty=SUM(ckamount) 必为负；
        // 退单行 ypaper 指向原单且 ro 由退货联动，改单=删旧开新会破坏原单 ro 对账，故不允许）
        if (bill.TotalQty < 0)
        {
            System.Windows.MessageBox.Show("退货单不支持修改", "修改", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        EditBillRequested?.Invoke(this, (bill.Paper.Trim(), bill.Type.Trim(), bill.Dpm.Trim()));
    }

    // ==== 单据备注就地编辑（2026-10-30 需求）：双击进入编辑 → 回车确定保存 → Esc/失焦取消还原 ====

    private void RemarkBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        tb.IsReadOnly = false;
        tb.Background = System.Windows.Media.Brushes.White;
        tb.BorderThickness = new System.Windows.Thickness(1);
        tb.Focus();
        tb.SelectAll();
    }

    private void RemarkBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.IsReadOnly) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = CommitBillRemarkAsync(tb);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelBillRemarkEdit(tb);
        }
    }

    private void RemarkBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsReadOnly)
            CancelBillRemarkEdit(tb);   // 编辑态失焦（未回车）= 取消，还原显示原值
    }

    private void CancelBillRemarkEdit(TextBox tb)
    {
        // 还原显示并退出编辑态（未 UpdateSource，行数据未变）
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        tb.IsReadOnly = true;
        tb.Background = System.Windows.Media.Brushes.Transparent;
        tb.BorderThickness = new System.Windows.Thickness(0);
    }

    private async System.Threading.Tasks.Task CommitBillRemarkAsync(TextBox tb)
    {
        if (tb.DataContext is not Domain.Query.OutDocBillRow row) { CancelBillRemarkEdit(tb); return; }
        var newText = tb.Text.Trim();
        var oldText = row.Remark?.Trim() ?? "";
        if (newText == oldText) { CancelBillRemarkEdit(tb); return; }

        tb.IsEnabled = false;   // 保存期间禁输入防重复提交
        try
        {
            await _vm.UpdateBillRemarkAsync(row, newText);
            // 行对象已更新为规范值，回读显示
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("备注保存失败：" + ex.Message, "备注", MessageBoxButton.OK, MessageBoxImage.Warning);
            CancelBillRemarkEdit(tb);
        }
        finally
        {
            tb.IsEnabled = true;
            tb.IsReadOnly = true;
            tb.Background = System.Windows.Media.Brushes.Transparent;
            tb.BorderThickness = new System.Windows.Thickness(0);
        }
    }
}
