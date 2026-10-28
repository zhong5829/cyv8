using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class ReportsControl : UserControl
{
    public ReportsControl()
    {
        InitializeComponent();
        var vm = new ReportsViewModel();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadedAsync(System.Threading.CancellationToken.None);
    }
}
