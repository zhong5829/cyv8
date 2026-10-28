using System.Windows.Controls;
using QPGL.Desktop.ViewModels;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.Views;

public partial class DeliveryControl : UserControl
{
    private readonly DeliveryViewModel _vm = new();

    public DeliveryControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await _vm.LoadedAsync();

    private async void Order1_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await _vm.LoadLinesForTabAsync(DeliveryTab.Pending);

    private async void Order2_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await _vm.LoadLinesForTabAsync(DeliveryTab.Confirmed);

    private async void Packing_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await _vm.LoadPackingLinesAsync();
}
