using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>账务基础资料页（I.9，fr_finmoney/fr_finsumary/fr_finset/fr_finEmplf/fr_finaccount）。</summary>
public partial class FinBasicControl : UserControl
{
    private readonly FinBasicViewModel _vm = new();

    public FinBasicControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadAllAsync(System.Threading.CancellationToken.None);
}
