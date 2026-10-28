using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>收款/付款管理页（G.2/G.3，fr_xlsk）：Tag=收款/付款（供列头文案），VM 按类型复用。</summary>
public partial class XlskControl : UserControl
{
    private readonly XlskViewModel _vm;

    public XlskControl(string type)
    {
        InitializeComponent();
        Tag = type;
        _vm = new XlskViewModel(type);
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync(System.Threading.CancellationToken.None);
    }
}
