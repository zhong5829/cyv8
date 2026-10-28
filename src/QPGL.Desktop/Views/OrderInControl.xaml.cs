using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class OrderInControl : UserControl
{
    public OrderInControl()
    {
        InitializeComponent();
        var vm = new OrderInViewModel();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadedAsync(System.Threading.CancellationToken.None);
    }
}
