using System.Windows;
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
            MessageBox.Show($"启动配置错误：{ex.Message}", "长远汽配 V8",
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
                "长远汽配 V8 - 无法连接数据库",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
    }
}
