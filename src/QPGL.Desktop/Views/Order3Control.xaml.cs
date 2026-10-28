using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class Order3Control : UserControl
{
    private readonly Order3ViewModel _vm = new();

    public Order3Control()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);
}
