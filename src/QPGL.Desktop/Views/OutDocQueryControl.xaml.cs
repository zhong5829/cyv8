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

    /// <summary>修改：校验选中行并携带单据数据切回开单模式。</summary>
    private void EditBill_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedBill is null)
        {
            System.Windows.MessageBox.Show("请先选择单据行", "修改", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var bill = _vm.SelectedBill;
        EditBillRequested?.Invoke(this, (bill.Paper.Trim(), bill.Type.Trim(), bill.Dpm.Trim()));
    }
}
