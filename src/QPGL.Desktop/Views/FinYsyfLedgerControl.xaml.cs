using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>应收应付帐（I.6，fr_finysyf）。</summary>
public partial class FinYsyfLedgerControl : UserControl
{
    private readonly FinYsyfLedgerViewModel _vm = new();

    public FinYsyfLedgerControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.SearchAsync(System.Threading.CancellationToken.None);
}
