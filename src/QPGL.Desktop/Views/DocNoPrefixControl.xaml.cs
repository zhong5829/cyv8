using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class DocNoPrefixControl : UserControl
{
    private readonly DocNoPrefixViewModel _vm = new();

    public DocNoPrefixControl()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }
}
