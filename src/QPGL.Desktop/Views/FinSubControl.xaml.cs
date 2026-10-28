using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>会计科目页（I.9-1，fr_finsub）。</summary>
public partial class FinSubControl : UserControl
{
    private readonly FinSubViewModel _vm = new();

    public FinSubControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.SearchAsync(System.Threading.CancellationToken.None);
}
