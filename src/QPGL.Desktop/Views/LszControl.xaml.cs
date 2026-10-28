using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>收支流水帐页（G.5，fr_lsz）：期初+明细+小计+期末小计+总计。</summary>
public partial class LszControl : UserControl
{
    private readonly LszViewModel _vm = new();

    public LszControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync(System.Threading.CancellationToken.None);
    }
}
