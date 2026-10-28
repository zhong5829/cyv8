using System.Windows;
using System.Windows.Controls;
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

    /// <summary>明细行双击 = 业务明细跳转（fr_ysyf 网格双击习惯）。</summary>
    private async void Details_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.SelectedDetail is null) return;
        await _vm.ShowBizDetailCommand.ExecuteAsync(null);
    }
}
