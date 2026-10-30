using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // ==== 三条件过滤框（编码/名称/车型）：实时+防抖；聚焦/点击全选；←/→ 在文本边界切换相邻框 ====
    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => _vm.DebounceFilter();

    private void FilterBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    private void FilterBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox box && !box.IsKeyboardFocusWithin)
        {
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var move = e.Key switch
        {
            Key.Left when box.CaretIndex == 0 => -1,
            Key.Right when box.CaretIndex == box.Text.Length => 1,
            _ => 0,
        };
        if (move == 0) return;
        // 三个查询框按 XAML 顺序相邻切换
        TextBox? target = (box == CpBox) ? (move > 0 ? Na1Box : null)
                        : (box == Na1Box) ? (move > 0 ? TyBox : CpBox)
                        : (move > 0 ? null : Na1Box);
        if (target is null) return;
        target.Focus();
        target.SelectAll();
        e.Handled = true;
    }

    /// <summary>双击行 → 零件属性编辑窗（修改）。</summary>
    private async void RowsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowsGrid.SelectedItem is null) return;
        await _vm.EditRowAsync();
    }
}

/// <summary>A2 零件代码 / 车型拼音码 / A7 税则号 / AA 分类码（四页签）。</summary>
public partial class BasicAttrControls : UserControl
{
    private readonly PrnameViewModel _nameVm = new();
    private readonly TyCodeViewModel _tyVm = new();
    private readonly ComplexViewModel _complexVm = new();
    private readonly DcCoefViewModel _dcVm = new();

    public BasicAttrControls()
    {
        InitializeComponent();
        DataContext = this;
        NameTab.DataContext = _nameVm;
        TyTab.DataContext = _tyVm;
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
