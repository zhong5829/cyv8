using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class DbrkControl : UserControl
{
    private readonly DbrkViewModel _vm = new();

    public DbrkControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await _vm.SearchAsync(System.Threading.CancellationToken.None);
}
