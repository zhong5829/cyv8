using System.Windows;
using Microsoft.Extensions.Configuration;
using QPGL.Desktop.Services;

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

        try
        {
            Db.Init();
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
