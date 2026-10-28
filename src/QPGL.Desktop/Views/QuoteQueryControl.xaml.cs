using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class QuoteQueryControl : UserControl
{
    private readonly QuoteQueryViewModel _vm = new();

    public QuoteQueryControl()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }
}
