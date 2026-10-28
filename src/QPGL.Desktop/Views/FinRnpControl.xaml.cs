using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>出纳帐（I.7，fr_finrnp）。</summary>
public partial class FinRnpControl : UserControl
{
    private readonly FinRnpViewModel _vm = new();

    public FinRnpControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.SearchAsync(System.Threading.CancellationToken.None);
}
