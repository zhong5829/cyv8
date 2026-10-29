using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>应收应付页（G.4，fr_ysyf）：上=客户汇总，下=选中客户明细。</summary>
public partial class YsyfControl : UserControl
{
    private readonly YsyfViewModel _vm = new();

    public YsyfControl()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            // 添加/修改客户后：滚动到选中的新客户行（汇总区仅 2 行高，选中项在视口外时用户感知不到）
            if (e.PropertyName == nameof(YsyfViewModel.SelectedSummary) && _vm.SelectedSummary is not null)
                SummariesGrid.ScrollIntoView(_vm.SelectedSummary);
        };
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.SyncAsync(System.Threading.CancellationToken.None);
        await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>查询条回车 = 执行查询（2026-10-29：查询按钮已移除）。
    /// 用冒泡 KeyDown：客户联想框内有选中项时回车已在 PreviewKeyDown 被处理（确认选中不查询），其余回车冒泡到此触发查询。</summary>
    private void QueryBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    // 客户联想（同 SaleControl cmm_class row326 FRDM 下拉机制）：输入即搜（300ms 防抖），支持拼音码/数字码/编号/名称
    private void GnameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.DebounceCustomerSearch();
        CustPopup.IsOpen = GnameBox.Text.Length > 0;   // 清空时不开空弹窗
    }

    private void GnameBox_PreviewKeyDown(object sender, KeyEventArgs e)
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
                    _ = _vm.ApplyCustomerAsync(p);
                    CustPopup.IsOpen = false;
                    e.Handled = true;   // 确认选中，不触发查询（KeyDown 冒泡被阻断）
                }
                else
                {
                    // 无联想选中：关弹窗并放行，KeyDown 冒泡到查询条触发查询
                    CustPopup.IsOpen = false;
                }
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
            _ = _vm.ApplyCustomerAsync(p);
            CustPopup.IsOpen = false;
            GnameBox.Focus();
            GnameBox.CaretIndex = GnameBox.Text.Length;
        }
    }

    /// <summary>明细行双击 = 业务明细跳转（fr_ysyf 网格双击习惯）。</summary>
    private async void Details_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.SelectedDetail is null) return;
        await _vm.ShowBizDetailCommand.ExecuteAsync(null);
    }
}
