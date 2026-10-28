using System.ComponentModel;
using System.Windows;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>库存分货对话框（fr_kcfh row23）：调拨管理页「分货」按钮打开，执行成功后自动关闭。</summary>
public partial class StockDistributeDialog : Window
{
    private readonly StockDistributeDialogViewModel _vm = new();

    public StockDistributeDialog()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.SearchAsync(System.Threading.CancellationToken.None);
        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    // 执行完成（IsBusy true→false）且成功时自动关闭
    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StockDistributeDialogViewModel.IsBusy) && !_vm.IsBusy && _vm.Success)
            Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
