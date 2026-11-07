using System.Windows.Controls;
using QPGL.Desktop.Services;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class StockAdjustControl : UserControl
{
    private readonly StockAdjustViewModel _vm = new();

    public StockAdjustControl()
    {
        InitializeComponent();
        IntegerInput.Restrict(TxtQty);   // 调仓数量只允许整数录入（2026-11-07 需求）
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);
}
