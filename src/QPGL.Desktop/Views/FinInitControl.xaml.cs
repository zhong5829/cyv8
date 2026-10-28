using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>帐务初始数据（N11，fr_fininit）：末级行编辑期初/本年余额，汇总/置零，试算平衡提示。</summary>
public partial class FinInitControl : UserControl
{
    private readonly FinInitViewModel _vm = new();

    public FinInitControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadAsync(System.Threading.CancellationToken.None);
}
