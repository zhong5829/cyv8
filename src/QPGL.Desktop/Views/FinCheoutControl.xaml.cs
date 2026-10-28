using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>期末结帐（I35，fr_fincheout）。</summary>
public partial class FinCheoutControl : UserControl
{
    private readonly FinCheoutViewModel _vm = new();

    public FinCheoutControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadAsync(System.Threading.CancellationToken.None);
}
