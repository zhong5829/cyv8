using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class StocktakeControl : UserControl
{
    private readonly StocktakeViewModel _vm = new();

    public StocktakeControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);

    private async void Paper_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await _vm.LoadLinesAsync(System.Threading.CancellationToken.None);
}
