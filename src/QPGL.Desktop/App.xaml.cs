using System.Windows;
using Microsoft.Extensions.Configuration;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;

namespace QPGL.Desktop;

/// <summary>
/// 直连模式启动（开发文档 D5）：初始化本机连接配置 → 库自检，
/// 失败立即弹窗报错退出，任何界面不返回兜底假数据。
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string connectionString;
        try
        {
            // 多库配置：ConnectionStrings 下配置了多个键时，启动先弹窗选择本次使用的数据库，
            // 确定后才进入登录流程；仅一条（Default）时保持原有行为。
            var config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();
            var connections = config.GetSection("ConnectionStrings").GetChildren()
                .Where(c => !string.IsNullOrEmpty(c.Value))
                .Select(c => (Name: c.Key!, ConnectionString: c.Value!))
                .ToList();

            if (connections.Count > 1)
            {
                // WPF 坑：OnStartup 期间该弹窗是应用唯一窗口，点击确定关闭时会触发
                // ShutdownMode.OnLastWindowClose 的"最后一个窗口已关"判定 → 应用直接退出。
                // 弹窗期间临时改为显式关闭模式，结束后恢复，登录窗/主窗关闭语义不变。
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    var picker = new DatabaseSelectWindow(connections);
                    if (picker.ShowDialog() != true)
                    {
                        Shutdown(0);
                        return;
                    }
                    connectionString = picker.Selected.ConnectionString;
                }
                finally
                {
                    ShutdownMode = ShutdownMode.OnLastWindowClose;
                }
            }
            else
            {
                connectionString = connections.FirstOrDefault().ConnectionString
                    ?? throw new InvalidOperationException("缺少连接串配置 ConnectionStrings:Default（appsettings.json）");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动配置错误：{ex.Message}", "QP12",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            Db.Init(connectionString);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动配置错误：{ex.Message}", "QP12",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var check = await Db.Health.CheckAsync();
        if (!check.Success)
        {
            MessageBox.Show(
                $"数据库连接失败（{check.ElapsedMs}ms）：{check.Error}\n\n" +
                "请检查 appsettings.json 连接配置与数据库状态。",
                "QP12 - 无法连接数据库",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // VIN 查询组装（qp11 迁移）：appsettings.json 的 VinQuery/Pinxiu 配置段；
        // 失败静默不阻断主流程（菜单入口处有兜底提示）。
        try
        {
            var vinConfig = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();
            Db.InitVin(vinConfig);

            // 启动时主动续期已登录数据源 Token（fire-and-forget，静默失败）
            if (Db.VinQuery.IsLoggedIn)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await Db.VinQuery.StartupRefreshAsync(); }
                    catch { /* 静默 */ }
                });
            }
        }
        catch { /* VIN 初始化失败不阻断主流程 */ }
    }
}
