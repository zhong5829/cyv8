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
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.SyncAsync(System.Threading.CancellationToken.None);
        await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }

    // 客户联想（同 SaleControl cmm_class row326 FRDM 下拉机制）：输入即搜（300ms 防抖），支持拼音码/数字码/编号/名称
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
