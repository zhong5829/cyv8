using System.Windows.Controls;

namespace QPGL.Desktop.Views;

public partial class DesktopControl : UserControl
{
    public DesktopControl(ViewModels.MainViewModel main)
    {
        InitializeComponent();
        DataContext = new ViewModels.DesktopViewModel(main);
    }
}
