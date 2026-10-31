using System.Windows;
using System.Windows.Input;
using QPGL.Desktop.Services;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    /// <summary>VIN 查询窗单例（qp11 _vinQueryWindow 同构）：已打开时再次点菜单仅激活，避免多开</summary>
    private VinQueryWindow? _vinQueryWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.RequestClose += Close;
        Loaded += (_, _) => _vm.OpenDefault();
        // 启动静默检查更新（appsettings.json Update:AutoCheck=false 可关闭）
        Loaded += async (_, _) => await StartupUpdateCheckAsync();
        // 旧 pg_main.prg L115：F11 热键随时弹出厂牌切换（fr_cp）
        // F3（qp11 MainWindow_KeyDown）：激活 Tab 为销售开单页时切换开单/查询双模式，否则打开客户资料
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F11)
            {
                var brand = new BrandWindow(mandatory: false);
                brand.ShowDialog();
                // 旧 fr_cp T_.row5：只改标题不刷新已开页面（会话变量下次取数生效）
                Title = $"QP12 配件管理系统 帐套：{AppSession.DeptNo} 厂牌：[{AppSession.Brand}]";
            }
            else if (e.Key == System.Windows.Input.Key.F3)
            {
                e.Handled = true;
                if (_vm.ActiveTab?.Content is SaleControl sale)
                    sale.OnQuery();
                else
                    _vm.OpenPartnersCommand.Execute(null);
            }
            else if (e.Key == System.Windows.Input.Key.F9)
            {
                // F9：清空销售开单所有输入与明细
                if (_vm.ActiveTab?.Content is SaleControl sc)
                {
                    e.Handled = true;
                    sc.ClearBill();
                }
            }
            else if (e.Key == System.Windows.Input.Key.F5)
            {
                // F5：确认单据（销售开单页，等价点「确认单据」按钮）
                if (_vm.ActiveTab?.Content is SaleControl sc2)
                {
                    e.Handled = true;
                    sc2.ConfirmBill();
                }
            }
            else if (e.Key == System.Windows.Input.Key.F7)
            {
                // F7：查询模式重打印（选中单据直接发送打印机）
                if (_vm.ActiveTab?.Content is SaleControl sale7 && sale7.IsQueryMode)
                {
                    e.Handled = true;
                    _ = sale7.ReprintSelectedAsync();
                }
            }
        };
        // 双击 Shift：销售开单页光标定位到客户名称框并全选（旧 VFP ON KEY LABEL SHIFT 习惯）
        KeyUp += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.LeftShift || e.Key == System.Windows.Input.Key.RightShift)
            {
                if (_vm.ActiveTab?.Content is SaleControl sale)
                    sale.OnShiftDoubleTap();
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void OpenPrintSettings_Click(object sender, RoutedEventArgs e)
    {
        new PrintFooterSettingsWindow().ShowDialog();
    }

    /// <summary>系统管理(S) → 标签打印（qp11 复刻：三条件查询 + 模板/条码/预览编辑 + 批量打印，以标签页打开）。</summary>
    private void OpenLabelPrint_Click(object sender, RoutedEventArgs e)
    {
        _vm.OpenTab("labelPrint", "标签打印", () => new LabelPrintControl());
    }

    /// <summary>系统管理(S) → VIN 查询（qp11 复刻：VIN 解码 + 318car/品秀 适配配件查询）。</summary>
    private void OpenVinQuery_Click(object sender, RoutedEventArgs e)
    {
        if (Db.VinQuery is null || Db.VinLocalMatch is null)
        {
            MessageBox.Show("VIN 查询功能初始化失败，请检查 appsettings.json 的 VinQuery/Pinxiu 配置段。",
                "VIN 查询", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // qp11 OpenVinQueryWindow 语义：非模态单例；不设置Owner，使VIN窗口与主窗口平级，可自由切换焦点
        // （设Owner会使被拥有窗口永远压在主窗口上方，表现为"置顶覆盖主程序"）
        if (_vinQueryWindow != null && _vinQueryWindow.IsLoaded)
        {
            _vinQueryWindow.Activate();
            return;
        }
        _vinQueryWindow = new VinQueryWindow();
        _vinQueryWindow.Closed += (_, _) => _vinQueryWindow = null;
        _vinQueryWindow.Show();
    }

    /// <summary>VIN 查询窗添加配件入口：优先取激活 Tab 的销售开单页，其次任意已打开的销售开单 Tab
    /// （qp11 MainWindow.GetActiveSellControl 同构）。</summary>
    public SaleControl? GetActiveSaleControl()
    {
        if (_vm.ActiveTab?.Content is SaleControl active)
            return active;
        return _vm.Tabs.Select(t => t.Content).OfType<SaleControl>().FirstOrDefault();
    }

    /// <summary>启动静默检查更新：有新版本才提示，网络失败不打扰。</summary>
    private async Task StartupUpdateCheckAsync()
    {
        if (!UpdateService.Options.AutoCheck) return;
        try
        {
            var info = await UpdateService.CheckLatestAsync();
            if (info == null) return;
            var r = MessageBox.Show(this,
                $"发现新版本 {info.Tag}（当前 v{UpdateService.CurrentVersion.ToString(3)}），是否下载并升级？\n" +
                "升级将退出程序并自动运行安装包，数据库连接与打印配置会保留。",
                "在线更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
                new UpdateWindow(info) { Owner = this }.ShowDialog();
        }
        catch
        {
            // 静默失败不打扰用户
        }
    }

    /// <summary>帮助(H) → 检查更新（手动触发，失败给出原因）。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var info = await UpdateService.CheckLatestAsync();
            if (info == null)
            {
                MessageBox.Show(this,
                    UpdateService.LastError is null
                        ? $"当前已是最新版本（v{UpdateService.CurrentVersion.ToString(3)}）。"
                        : $"检查更新失败：{UpdateService.LastError}",
                    "检查更新", MessageBoxButton.OK,
                    UpdateService.LastError is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
                return;
            }
            var r = MessageBox.Show(this,
                $"发现新版本 {info.Tag}（当前 v{UpdateService.CurrentVersion.ToString(3)}），是否下载并升级？\n" +
                "升级将退出程序并自动运行安装包，数据库连接与打印配置会保留。",
                "在线更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
                new UpdateWindow(info) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"检查更新失败：{ex.Message}", "检查更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
