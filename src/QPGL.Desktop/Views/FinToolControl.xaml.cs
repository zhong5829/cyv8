using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>辅助工具 + 固定资产（I.8 工具 / N.7-N.8）。</summary>
public partial class FinToolControl : UserControl
{
    private readonly FinToolViewModel _vm = new();

    public FinToolControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadAsync(System.Threading.CancellationToken.None);
}
