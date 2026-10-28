using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.Views;

public partial class OrderExpControl : UserControl
{
    private readonly OrderExpViewModel _vm = new();

    public OrderExpControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        => await _vm.LoadedAsync(System.Threading.CancellationToken.None);

    private async void ParamGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.EditingElement is not TextBox tb) return;
        if (e.Row.Item is not OrderExpParaRow p) return;
        if (!decimal.TryParse(tb.Text.Trim(), out var value))
        {
            MessageBox.Show("参数值必须为数字。", "订货公式", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await _vm.SaveParamAsync(p.Paraid, value);
    }

    private async void FormulaGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.EditingElement is not TextBox tb) return;
        if (e.Row.Item is not OrderExpRow f) return;
        await _vm.SaveFormulaNameAsync(f.Expid, tb.Text.Trim(), f.Cexpress);
    }
}
