using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class OrsaControl : UserControl
{
    private readonly OrsaViewModel _vm = new();

    public OrsaControl()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>选中行变化 → 带出明细面板（row29 AfterRowColChange 语义）。</summary>
    private async void RowsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await _vm.LoadDetailAsync(_vm.SelectedRow);
}
