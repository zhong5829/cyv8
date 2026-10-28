using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>库存调价（B4）+ 警戒线设置（B5）。</summary>
public partial class PriceAdjustControl : UserControl
{
    private readonly PriceAdjustViewModel _vm = new();

    public PriceAdjustControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadAsync(System.Threading.CancellationToken.None);
}
