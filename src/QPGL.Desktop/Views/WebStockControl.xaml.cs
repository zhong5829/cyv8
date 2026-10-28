using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.Views;

public partial class WebStockControl : UserControl
{
    private readonly WebStockViewModel _vm = new();

    public WebStockControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);

    private async void Mark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: WebStockRow row })
            await _vm.ToggleMarkAsync(row);
    }
}
