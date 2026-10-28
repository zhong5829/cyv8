using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class StockAdjustControl : UserControl
{
    private readonly StockAdjustViewModel _vm = new();

    public StockAdjustControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);
}
