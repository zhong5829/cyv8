using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class DailyStocktakeControl : UserControl
{
    private readonly DailyStocktakeViewModel _vm = new();

    public DailyStocktakeControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.SearchAsync(System.Threading.CancellationToken.None);
}
