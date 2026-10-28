using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>凭证录入/查询/审核/过帐页（I.1 + I.3，fr_finvouch/fr_finquery/fr_finaudit/fr_finpost）。</summary>
public partial class FinVouchControl : UserControl
{
    private readonly FinVouchViewModel _vm = new();

    public FinVouchControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.NewDocAsync(System.Threading.CancellationToken.None);
}
