using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Api;
using QPGL.Api.Auth;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.ViewModels;

/// <summary>工作台（qp11 DesktopControl 风格）。KPI 数据二期接报表 API。</summary>
public partial class DesktopViewModel : ObservableObject
{
    public IRelayCommand OpenPartnersCommand { get; }
    public IRelayCommand NotImplementedCommand { get; }

    // ========== Web 端服务（进程内托管 QPGL.Api，仅系统管理员可见） ==========
    public bool IsWebPanelVisible => string.Equals(AppSession.Role, "系统管理员", StringComparison.Ordinal);

    [ObservableProperty] private bool _webRunning;
    [ObservableProperty] private string _webStatusText = "已停止";
    [ObservableProperty] private string _webUrlText = "";
    [ObservableProperty] private string _webUptimeText = "";
    [ObservableProperty] private string _webOnlineText = "0 人在线";
    [ObservableProperty] private string _webToggleText = "启动 Web 端";
    [ObservableProperty] private bool _webBusy;

    public IAsyncRelayCommand WebToggleCommand { get; }

    private DispatcherTimer? _webTimer;
    private DateTime? _webStartedAt;

    public DesktopViewModel(MainViewModel main)
    {
        OpenPartnersCommand = main.OpenPartnersCommand;
        NotImplementedCommand = main.NotImplementedCommand;
        WebToggleCommand = new AsyncRelayCommand(ToggleWebAsync, () => !WebBusy);
    }

    private async Task ToggleWebAsync()
    {
        if (WebRunning) await StopWebAsync();
        else await StartWebAsync();
    }

    private async Task StartWebAsync()
    {
        if (WebRunning || WebBusy) return;
        WebBusy = true;
        WebStatusText = "启动中...";
        try
        {
            // 宿主构建较重（CreateBuilder/DI/JWT），放后台线程避免 UI 卡顿；
            // Runner 内部全程 await（严禁从 UI 线程同步阻塞异步调用，否则死锁 Dispatcher）
            await Task.Run(() => WebApiRunner.StartAsync(new WebApiRunner.StartOptions
            {
                ContentRoot = AppContext.BaseDirectory,
                // 与桌面端同一所选库（多库选择窗确认后的连接串）
                ConnectionString = Db.ActiveConnectionString,
                // Web 端打印走桌面端静默打印（WPF 打印对象由桥调度到 UI 线程）
                PrintBridge = new QPGL.Desktop.Printing.DesktopPrintBridge(),
            }));
            _webStartedAt = DateTime.Now;
            WebRunning = true;
            WebStatusText = "运行中";
            WebUrlText = BuildLocalUrl(WebApiRunner.ListenUrl);
            WebToggleText = "停止 Web 端";
            StartWebTimer();
        }
        catch (Exception ex)
        {
            WebStatusText = "启动失败：" + ex.Message;
        }
        finally
        {
            WebBusy = false;
        }
    }

    private async Task StopWebAsync()
    {
        if (!WebRunning || WebBusy) return;
        WebBusy = true;
        WebStatusText = "停止中...";
        try
        {
            await Task.Run(() => WebApiRunner.StopAsync());
        }
        catch (Exception ex)
        {
            WebStatusText = "停止异常：" + ex.Message;
        }
        finally
        {
            MarkStopped();
            WebBusy = false;
        }
    }

    private void MarkStopped()
    {
        _webStartedAt = null;
        WebRunning = false;
        WebStatusText = "已停止";
        WebUrlText = "";
        WebUptimeText = "";
        WebOnlineText = "0 人在线";
        WebToggleText = "启动 Web 端";
        _webTimer?.Stop();
    }

    private void StartWebTimer()
    {
        _webTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _webTimer.Tick -= OnWebTimerTick;
        _webTimer.Tick += OnWebTimerTick;
        OnWebTimerTick(null, EventArgs.Empty);   // 立即刷一次
        _webTimer.Start();
    }

    private void OnWebTimerTick(object? sender, EventArgs e)
    {
        // 宿主意外退出（如端口被外部占用后崩溃）时自动复位状态
        if (WebRunning && !WebApiRunner.IsRunning)
        {
            WebStatusText = "已停止（服务异常退出）";
            _webStartedAt = null;
            WebRunning = false;
            WebUrlText = "";
            WebUptimeText = "";
            WebToggleText = "启动 Web 端";
            _webTimer?.Stop();
            return;
        }
        if (!WebRunning) return;

        WebOnlineText = $"{OnlineSessionTracker.Instance.CountOnline()} 人在线";
        if (_webStartedAt is { } started)
            WebUptimeText = $"运行时长 {DateTime.Now - started:hh\\:mm\\:ss}";
    }

    /// <summary>把监听地址转成局域网可访问的 URL（取本机首个 IPv4；取不到用 localhost）。</summary>
    private static string BuildLocalUrl(string listenUrl)
    {
        int port;
        try { port = new Uri(listenUrl.Replace("0.0.0.0", "localhost")).Port; }
        catch { port = 5100; }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);   // UDP connect 只做路由选择，不产生流量
            if (socket.LocalEndPoint is IPEndPoint ep)
                return $"http://{ep.Address}:{port}";
        }
        catch { /* 离线/无网卡时回退 */ }

        return $"http://localhost:{port}";
    }
}
