using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class Order4Control : UserControl
{
    private readonly Order4ViewModel _vm = new();

    public Order4Control()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);

    private async void PlanList_DoubleClick(object sender, MouseButtonEventArgs e)
        => await _vm.OpenPlanAsync(System.Threading.CancellationToken.None);
}
