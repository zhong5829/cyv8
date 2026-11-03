using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class OrderInControl : UserControl
{
    public OrderInControl()
    {
        InitializeComponent();
        var vm = new OrderInViewModel();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadedAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>双击单元格进入编辑（本次到货数等可编辑列）。</summary>
    private void ArrivalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ArrivalGrid.CurrentCell.Column is not null)
            ArrivalGrid.BeginEdit();
    }

    /// <summary>进入编辑时全选文本（PartPickWindow 同款体验）。</summary>
    private void ArrivalGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }
}
