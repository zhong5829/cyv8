using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class DailyStatControl : UserControl
{
    public DailyStatControl()
    {
        InitializeComponent();
        var vm = new DailyStatViewModel();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadedAsync(System.Threading.CancellationToken.None);
    }
}
