using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>A1 零件属性定义（fr_prnoty 主路径；互换码/编码自动产生/打印备案）。</summary>
public partial class PrnotyControl : UserControl
{
    private readonly PrnotyViewModel _vm = new();

    public PrnotyControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e) => await _vm.SearchAsync();
}

/// <summary>A2 零件代码 / A7 税则号 / AA 分类码（三页签）。</summary>
public partial class BasicAttrControls : UserControl
{
    private readonly PrnameViewModel _nameVm = new();
    private readonly ComplexViewModel _complexVm = new();
    private readonly DcCoefViewModel _dcVm = new();

    public BasicAttrControls()
    {
        InitializeComponent();
        DataContext = this;
        NameTab.DataContext = _nameVm;
        ComplexTab.DataContext = _complexVm;
        DcTab.DataContext = _dcVm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _complexVm.LoadAsync();
        await _dcVm.LoadAsync();
    }
}

/// <summary>A3 属性传递 + A4 套件管理（两页签）。</summary>
public partial class AttrTransferKitControl : UserControl
{
    private readonly SxcdViewModel _sxcdVm = new();
    private readonly TjglViewModel _tjglVm = new();

    public AttrTransferKitControl()
    {
        InitializeComponent();
        DataContext = this;
        SxcdTab.DataContext = _sxcdVm;
        TjglTab.DataContext = _tjglVm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _sxcdVm.LoadAsync();
        await _tjglVm.LoadAsync();
    }

    private async void OnHeadSelected(object sender, SelectionChangedEventArgs e) => await _tjglVm.LoadLinesAsync();
}

/// <summary>A8 系统定义（公司信息/厂牌/字典/参数四页签）。</summary>
public partial class SystemDefineControl : UserControl
{
    private readonly SysDefineViewModel _vm = new();

    public SystemDefineControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.LoadAsync();
        await _vm.LoadParasAsync();
    }

    private async void OnDictTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboDictType.SelectedItem is ComboBoxItem it && it.Content is string t)
            await _vm.LoadDictAsync(t);
    }
}
