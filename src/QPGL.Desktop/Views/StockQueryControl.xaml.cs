using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class StockQueryControl : UserControl
{
    private readonly StockQueryViewModel _vm = new();

    public StockQueryControl()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }
}
