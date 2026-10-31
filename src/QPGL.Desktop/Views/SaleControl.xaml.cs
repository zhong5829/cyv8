using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.Services;
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

    /// <summary>多条件查询（2026-10-31 需求）：弹窗逐行输入内部编号（默认精准匹配，行首 / 包含匹配），
    /// 确定后一次查回多个配件替换结果列表。</summary>
    private async void MultiCodeSearch_Click(object sender, RoutedEventArgs e)
    {
        var codes = ShowMultiCodeDialog();
        if (codes is null) return;
        await _vm.SearchByCodesAsync(codes);
    }

    /// <summary>多编码输入窗（代码构建：多行文本框 + 确定/取消）。取消返回 null。</summary>
    private string? ShowMultiCodeDialog()
    {
        var box = new TextBox
        {
            Height = 200,
            Margin = new Thickness(0, 6, 0, 6),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var win = new Window
        {
            Title = "多条件查询",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var okBtn = new Button
        {
            Content = "确定", IsDefault = true,
            Padding = new Thickness(22, 4, 22, 4), Margin = new Thickness(0, 0, 10, 0),
        };
        var cancelBtn = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(22, 4, 22, 4) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style primary) okBtn.Style = primary;
        okBtn.Click += (_, _) =>
        {
            if (box.Text.Split('\n').All(l => string.IsNullOrWhiteSpace(l)))
            {
                MessageBox.Show("请输入至少一个内部编号（一行一个）。", "多条件查询",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            win.DialogResult = true;
        };

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock
        {
            Text = "请输入内部编号（一行一个）；默认精准匹配，编码前加 / 表示包含匹配：",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        body.Children.Add(buttons);
        win.Content = body;
        win.Loaded += (_, _) => box.Focus();

        return win.ShowDialog() == true ? box.Text : null;
    }

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
        // 三个查询框按 XAML 顺序相邻切换（编码→名称→车型）
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
        // Ctrl+C 复制内部编号（2026-10-30 需求）：单击内部编号单元格选中行后，Ctrl+C 直接复制编号
        if (e.Key == Key.C && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
            && PartGrid.SelectedItem is Domain.Parts.PartItem sel
            && string.Equals(PartGrid.CurrentColumn?.Header?.ToString(), "内部编号"))
        {
            e.Handled = true;
            var nno = sel.Nno.Trim();
            if (TrySetClipboardTextNative(nno, out _, out _))
                _vm.Message = $"已复制内部编号：{nno}";
            else
                _vm.Message = "复制失败：剪贴板被其它程序占用，请稍后重试";
            return;
        }
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
        else
        {
            // 切回开单模式（2026-10-30 需求）：配件列表库存可能已被其它单据扣减，轻量刷新库存列
            _ = _vm.RefreshStockAsync();
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

    /// <summary>整单确认成功：勾选"立即打印"（2026-10-30 需求）时留在开单模式（打印已在 VM 内发送）；
    /// 否则清屏后自动切换到查询模式（并刷新单据列表到当天）。</summary>
    private void Vm_ConfirmSucceeded(object? sender, EventArgs e)
    {
        if (_vm.SkipQuerySwitchAfterConfirm)
        {
            // 留在开单模式继续开下一单：确认已扣库存，轻量刷新配件列表库存列（不重查整表）
            _ = _vm.RefreshStockAsync();
            return;
        }
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

    /// <summary>右键按下时选中鼠标所在行（2026-10-30 需求）：右键菜单操作的是光标下的配件而非当前选中行。</summary>
    private void PartGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d &&
            ItemsControl.ContainerFromElement(PartGrid, d) is DataGridRow row)
            row.IsSelected = true;
    }

    /// <summary>
    /// Win32 原生剪贴板文本写入（2026-10-31 修复复制卡顿/误报）：
    /// WPF Clipboard.SetText 内部 OLE 封装被占用时单次阻塞约 1 秒且可能"已写入仍抛异常"；
    /// 原生 OpenClipboard 被占立即返回 false，由本方法快速重试（30ms × 20 次 ≈ 0.6s），成败判定准确。
    /// </summary>
    private static bool TrySetClipboardTextNative(string text, out string error, out int attempts)
    {
        error = "";
        for (attempts = 1; attempts <= 20; attempts++)
        {
            if (NativeClipboard.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!NativeClipboard.EmptyClipboard()) { error = "EmptyClipboard 失败"; return false; }
                    // GMEM_MOVEABLE|GMEM_ZEROINIT：分配含结尾 '\0' 的 UTF-16 缓冲，成功后所有权移交系统
                    var hMem = NativeClipboard.GlobalAlloc(0x0042, (nuint)((text.Length + 1) * 2));
                    if (hMem == IntPtr.Zero) { error = "GlobalAlloc 失败"; return false; }
                    var p = NativeClipboard.GlobalLock(hMem);
                    if (p == IntPtr.Zero) { error = "GlobalLock 失败"; return false; }
                    System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                    NativeClipboard.GlobalUnlock(hMem);
                    if (NativeClipboard.SetClipboardData(NativeClipboard.CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    {
                        error = "SetClipboardData 失败";
                        return false;   // 极罕见；hMem 不释放（正常路径所有权移交系统）
                    }
                    return true;
                }
                finally { NativeClipboard.CloseClipboard(); }
            }
            System.Threading.Thread.Sleep(30);   // OpenClipboard 被占立即失败，快速重试
        }
        error = "OpenClipboard 超时（剪贴板被其它程序持续占用）";
        return false;
    }

    private static class NativeClipboard
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool EmptyClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        internal static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        internal static extern IntPtr GlobalLock(IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        internal static extern bool GlobalUnlock(IntPtr hMem);

        internal const uint CF_UNICODETEXT = 13;
    }

    // 配件图片（2026-10-31 需求）：选中配件从 tbImage 取图显示缩略图，单击打开大图（可复制）
    private int _imageLoadSeq;                       // 竞态守卫：慢的旧请求不覆盖新选中
    private string _imageNno = "";                   // 当前缩略图对应的内部编号
    private byte[]? _imageBytes;                     // 当前图片字节（传大图窗）

    /// <summary>选中配件变化：异步取 tbImage 图片刷新缩略图（无图显示"无图片"占位）。</summary>
    private async void PartGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var seq = ++_imageLoadSeq;
        _imageBytes = null;
        _imageNno = "";
        PartImage.Source = null;
        PartImage.Visibility = Visibility.Collapsed;
        PartImagePlaceholder.Text = "加载中…";
        PartImagePlaceholder.Visibility = Visibility.Visible;
        PartImage.Cursor = Cursors.Arrow;

        if (PartGrid.SelectedItem is not Domain.Parts.PartItem p) { PartImagePlaceholder.Text = "无图片"; return; }
        var nno = p.Nno.Trim();
        var bytes = await Db.Parts.GetPartImageAsync(nno);
        if (seq != _imageLoadSeq) return;            // 期间用户已切换选中
        if (bytes is null || bytes.Length == 0)
        {
            PartImagePlaceholder.Text = "无图片";
            return;
        }
        _imageNno = nno;
        _imageBytes = bytes;
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        PartImage.Source = bmp;
        PartImage.Visibility = Visibility.Visible;
        PartImagePlaceholder.Visibility = Visibility.Collapsed;
        PartImage.Cursor = Cursors.Hand;
    }

    /// <summary>单击缩略图 → 打开大图查看窗（可复制图片、Esc 关闭）。</summary>
    private void PartImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (_imageBytes is null || _imageNno.Length == 0) return;
        new PartImageViewerWindow(_imageNno, _imageBytes).ShowDialog();
    }

    /// <summary>右键菜单「编辑配件属性」（2026-10-30 需求）：弹出属性编辑窗修改该配件。</summary>
    private async void EditPartAttribute_Click(object sender, RoutedEventArgs e)
    {
        if (PartGrid.SelectedItem is Domain.Parts.PartItem part)
            await _vm.EditPartAttributeAsync(part);
    }
}

public partial class SaleControl : IDisposable
{
    public void Dispose()
    {
        _spaceTimer?.Dispose();
        _vm.Dispose();
    }
}
