using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>报表中心（I.5：试算平衡/凭证汇总/损益/资产负债/现金流）。</summary>
public partial class FinReportControl : UserControl
{
    private readonly FinReportViewModel _vm = new();

    public FinReportControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadAsync(System.Threading.CancellationToken.None);
}
