using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>结转损益（I34，fr_fincaforw）。</summary>
public partial class FinCaforwControl : UserControl
{
    private readonly FinCaforwViewModel _vm = new();

    public FinCaforwControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadAsync(System.Threading.CancellationToken.None);
}
