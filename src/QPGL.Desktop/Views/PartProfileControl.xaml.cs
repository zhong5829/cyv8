using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class PartProfileControl : UserControl
{
    private readonly PartProfileViewModel _vm = new();

    public PartProfileControl()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }
}
