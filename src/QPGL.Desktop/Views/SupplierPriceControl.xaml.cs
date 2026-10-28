using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class SupplierPriceControl : UserControl
{
    private readonly SupplierPriceViewModel _vm = new();

    public SupplierPriceControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);
}
