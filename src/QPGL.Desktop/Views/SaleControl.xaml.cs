using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class SaleControl : UserControl
{
    private readonly SaleViewModel _vm;

    /// <param name="docType">'销售'（默认，菜单 C.1）/ '预售'（菜单 C.2，同销售路径取「预售单」号）/ '调拨'（菜单 C.3，指定批次出库，取号「调拨单」）/ '内耗'（菜单 C.4，同销售路径）/ '报价'（菜单 C.5，kcid=0 不占库存）。</param>
    public SaleControl(string docType = "销售")
    {
        InitializeComponent();
        _vm = new SaleViewModel { DocType = docType };
        DataContext = _vm;
        _vm.ConfirmSucceeded += Vm_ConfirmSucceeded;
        // row53/row298 按单据类型显隐：报价页显示「确认」（FRMOK）；销售页显示「转报价/转预售」；
        // 预售页显示「转销售」（row53 预售分支，需 CA）；调拨/内耗页皆隐
        switch (docType)
        {
            case "报价":
                BtnToQuote.Visibility = Visibility.Collapsed;
                break;
            case "销售":
                BtnConfirm.Visibility = Visibility.Collapsed;
                break;
            case "预售":
                BtnConfirm.Visibility = Visibility.Collapsed;
                BtnToQuote.Visibility = Visibility.Collapsed;
                break;
            default: // 调拨 / 内耗
                BtnConfirm.Visibility = Visibility.Collapsed;
                BtnToQuote.Visibility = Visibility.Collapsed;
                BtnToPresale.Visibility = Visibility.Collapsed;
                BtnToSale.Visibility = Visibility.Collapsed;
                // 内耗页不显示分货，仅调拨页保留（用户决策：库存分货并入调拨管理页）
                if (docType != "调拨") BtnDistribute.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await _vm.SearchPartsAsync(System.Threading.CancellationToken.None);
        // 无限滚动：配件列表滚到底自动加载下一页
        var sv = FindDescendantScrollViewer(PartGrid);
        if (sv is not null) sv.ScrollChanged += PartGrid_ScrollChanged;
    }

    private static System.Windows.Controls.ScrollViewer? FindDescendantScrollViewer(System.Windows.DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer sv) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var hit = FindDescendantScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (hit is not null) return hit;
        }
        return null;
    }

    // 滚动到距底部 2 行内即触发加载下一页
    private async void PartGrid_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer sv) return;
        if (sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 40 && e.VerticalChange != 0)
            await _vm.LoadMorePartsAsync(System.Threading.CancellationToken.None);
    }

    // 三条件实时搜索（500ms 防抖在 VM 内）
    private void CpBox_TextChanged(object sender, TextChangedEventArgs e) => _vm.DebounceSearch();
    private void Na1Box_TextChanged(object sender, TextChangedEventArgs e) => _vm.DebounceSearch();
    private void TyBox_TextChanged(object sender, TextChangedEventArgs e) => _vm.DebounceSearch();

    // 查询框获得键盘焦点即全选（Tab 切换），方便直接输入覆盖
    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    // 鼠标点击已聚焦查询框 → 全选（WPF 默认点击定位光标不触发全选）
    // 左右方向键在光标处于文本边界时切换到相邻查询框并全选，方便连续清除重输
    private void SearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox box && !box.IsKeyboardFocusWithin)
        {
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>查询框按键：回车立即搜索（不等防抖）；↓ 跳到配件列表选中第一行；←/→ 在文本边界时切换相邻查询框并全选。</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            // 远程优化：回车跳过防抖立即搜（VM 内 seq 竞态守卫，防抖到期不会重复查询）
            _vm.CancelPendingSearch();
            e.Handled = true;
            _ = _vm.SearchPartsAsync(System.Threading.CancellationToken.None);
            return;
        }
        if (e.Key == Key.Down)
        {
            JumpToPartGrid();
            e.Handled = true;
            return;
        }
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

    // 跳到配件列表选中第一行，后续 ↓ 由 DataGrid 正常导航
    private void JumpToPartGrid()
    {
        if (PartGrid.Items.Count == 0) return;
        PartGrid.SelectedIndex = 0;
        PartGrid.ScrollIntoView(PartGrid.SelectedItem);
        PartGrid.UpdateLayout();
        if (PartGrid.ItemContainerGenerator.ContainerFromIndex(0) is System.Windows.Controls.DataGridRow row)
            row.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
        else
            PartGrid.Focus();
    }

    // 双击配件搜索行 → 弹销售编辑对话框（qp11 SellEditDialog 风格）
    private async void PartGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PartGrid.SelectedItem is Domain.Parts.PartItem part)
            await _vm.AddLineViaDialogAsync(part);
    }

    // 双击明细行 → 再次弹窗修改数量/单价/备注
    private void LineGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LineGrid.SelectedItem is ViewModels.SaleLineRow row)
            _vm.EditExistingLine(row);
    }

    // 明细行 Delete 键删除选中行
    private void LineGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && LineGrid.SelectedItem is ViewModels.SaleLineRow row)
        {
            _vm.RemoveLineCommand.Execute(row);
            e.Handled = true;
        }
    }

    // 配件列表 F4 / Enter → 打开选中配件的销售编辑窗口（加入明细）；
    // 空格（2026-10-28 需求）：单击=打开销售编辑窗口（延时 330ms 与双击区分），双击=显示/隐藏「最近进价」列
    private DateTime _lastSpaceUtc = DateTime.MinValue;
    private System.Threading.Timer? _spaceTimer;

    private async void PartGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.F4 or Key.Enter && PartGrid.SelectedItem is Domain.Parts.PartItem part)
        {
            await _vm.AddLineViaDialogAsync(part);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Space) return;
        e.Handled = true;   // 空格默认翻页行为不生效
        var now = DateTime.UtcNow;
        if ((now - _lastSpaceUtc).TotalMilliseconds <= 300)
        {
            // 双击空格：切换最近进价列显示/隐藏，并取消待触发的单击开窗
            _lastSpaceUtc = DateTime.MinValue;
            _spaceTimer?.Dispose();
            _spaceTimer = null;
            LastIprcColumn.Visibility = LastIprcColumn.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            // 远程优化：列可见性同步给 VM（搜索按需回填进价），切为可见时对当前页补取一次
            _vm.IsLastIprjVisible = LastIprcColumn.Visibility == Visibility.Visible;
            if (_vm.IsLastIprjVisible) _ = _vm.FillLastIprjAsync();
        }
        else
        {
            // 单击空格：等 330ms 确认无双击后，对选中行打开销售编辑窗口
            _lastSpaceUtc = now;
            var selected = PartGrid.SelectedItem as Domain.Parts.PartItem;
            _spaceTimer?.Dispose();
            _spaceTimer = new System.Threading.Timer(_ =>
            {
                _lastSpaceUtc = DateTime.MinValue;
                if (selected is null) return;
                Dispatcher.Invoke(async () => await _vm.AddLineViaDialogAsync(selected));
            }, null, 330, System.Threading.Timeout.Infinite);
        }
    }

    // 客户联想（旧 cmm_class row326 FRDM 下拉机制）：输入即搜（300ms 防抖），支持拼音码/数字码/编号/名称
    private void GnameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.DebounceCustomerSearch();
        CustPopup.IsOpen = true;
    }

    private async void GnameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (CustList.Items.Count > 0)
                {
                    CustPopup.IsOpen = true;
                    // 焦点保持在输入框，手动下移选中项（避免焦点跳入列表后按键失效）
                    CustList.SelectedIndex = Math.Min(CustList.SelectedIndex + 1, CustList.Items.Count - 1);
                    CustList.ScrollIntoView(CustList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (CustList.Items.Count > 0 && CustList.SelectedIndex > 0)
                {
                    CustList.SelectedIndex--;
                    CustList.ScrollIntoView(CustList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (CustPopup.IsOpen && CustList.SelectedItem is Domain.Partners.Partner p)
                {
                    _vm.ApplyCustomer(p);
                    CustPopup.IsOpen = false;
                }
                else
                {
                    // 无联想选中时保持旧语义：Enter 直接按名称带出编号
                    await _vm.LookupCustomerAsync(System.Threading.CancellationToken.None);
                    CustPopup.IsOpen = false;
                }
                e.Handled = true;
                break;
            case Key.Escape:
                CustPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private async void CustList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CustList.SelectedItem is Domain.Partners.Partner p)
        {
            _vm.ApplyCustomer(p);
            CustPopup.IsOpen = false;
            GnameBox.Focus();
            GnameBox.CaretIndex = GnameBox.Text.Length;
        }
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// 开单/查询双模式切换（qp11 SellControl.ToggleMode 同构）：
    /// 进查询模式即刷新单据列表（日期重置为当天 + 立即查询），切回开单模式恢复面板。
    /// </summary>
    private bool _isQueryMode;

    public void ToggleMode()
    {
        _isQueryMode = !_isQueryMode;
        panelOrderMode.Visibility = _isQueryMode ? Visibility.Collapsed : Visibility.Visible;
        panelQueryMode.Visibility = _isQueryMode ? Visibility.Visible : Visibility.Collapsed;
        BtnSwitchMode.Content = _isQueryMode ? "切换开单模式 (F3)" : "切换查询模式 (F3)";
        if (_isQueryMode)
        {
            if (!_editHooked)
            {
                QueryControl.EditBillRequested += QueryControl_EditBillRequested;
                _editHooked = true;
            }
            QueryControl.RefreshToToday();
        }
    }

    private void BtnSwitchMode_Click(object sender, System.Windows.RoutedEventArgs e) => ToggleMode();

    /// <summary>F5：确认单据（仅开单模式响应，等价点「确认单据」按钮）。</summary>
    public void ConfirmBill()
    {
        if (_isQueryMode) return;
        if (_vm.ConfirmDocCommand.CanExecute(null))
            _vm.ConfirmDocCommand.Execute(null);
    }

    /// <summary>当前是否查询模式（快捷键路由判断用）。</summary>
    public bool IsQueryMode => _isQueryMode;

    /// <summary>F7：查询模式重打印选中单据（等价点「重打印」按钮，执行查询页 VM 的 PrintCommand）。</summary>
    public async System.Threading.Tasks.Task ReprintSelectedAsync()
    {
        if (QueryControl.DataContext is OutDocQueryViewModel vm && vm.PrintCommand.CanExecute(null))
            vm.PrintCommand.Execute(null);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>双击 Shift 检测：300ms 内连按两次 Shift → 光标定位客户名称框并全选。</summary>
    private DateTime _lastShiftUtc = DateTime.MinValue;

    public void OnShiftDoubleTap()
    {
        if (_isQueryMode) return;
        var now = DateTime.UtcNow;
        if ((now - _lastShiftUtc).TotalMilliseconds <= 300)
        {
            _lastShiftUtc = DateTime.MinValue;
            GnameBox.Focus();
            GnameBox.SelectAll();
        }
        else
        {
            _lastShiftUtc = now;
        }
    }

    /// <summary>整单确认成功：清屏后自动切换到查询模式（并刷新单据列表到当天）。</summary>
    private void Vm_ConfirmSucceeded(object? sender, EventArgs e)
    {
        if (!_isQueryMode) ToggleMode();
        else QueryControl.RefreshToToday();
    }

    /// <summary>查询模式「修改」：携带单据数据切回开单模式并回读填充（订阅一次，防重复挂接）。</summary>
    private bool _editHooked;

    private async void QueryControl_EditBillRequested(object? sender, (string Paper, string Type, string Dpm) args)
    {
        ToggleMode();   // 切回开单模式（面板可见）
        await _vm.LoadForEditAsync(args.Paper, args.Type, args.Dpm, System.Threading.CancellationToken.None);
    }

    /// <summary>F9 快捷键：清空开单输入与明细。</summary>
    public void ClearBill() => _vm.ClearBill();

    /// <summary>F3 快捷键入口（MainWindow KeyDown 路由到激活 Tab，仅开单类页面响应）。</summary>
    public void OnQuery()
    {
        // 仅「销售开单」页提供双模式；预售/调拨/内耗/报价页保持原单面板
        if (_vm.DocType == "销售") ToggleMode();
    }

    /// <summary>VIN 查询窗外部添加配件：转发 VM 的销售编辑对话框流程（SaleLineEditDialog 预填 + 确认后入明细，不重复弹编辑窗）。</summary>
    public async System.Threading.Tasks.Task AddExternalPartAsync(Domain.Parts.PartItem part)
        => await _vm.AddLineViaDialogAsync(part);
}

public partial class SaleControl : IDisposable
{
    public void Dispose()
    {
        _spaceTimer?.Dispose();
        _vm.Dispose();
    }
}
