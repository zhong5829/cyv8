using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>帐套选项（N12，fr_finpara）。</summary>
public partial class FinParaControl : UserControl
{
    private readonly FinParaViewModel _vm = new();

    public FinParaControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadAsync(System.Threading.CancellationToken.None);
}
