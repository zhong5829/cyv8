using System.Windows;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Views;

/// <summary>
/// 在线更新进度窗：下载安装包 → 自动运行 Inno 安装程序 → 退出本程序。
/// 安装包升级保留用户 appsettings.json / print.ini（onlyifdoesntexist 保护）。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService.UpdateInfo _info;
    private readonly CancellationTokenSource _cts = new();

    public UpdateWindow(UpdateService.UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        InfoText.Text =
            $"新版本 {info.Tag}（当前 v{UpdateService.CurrentVersion.ToString(3)}）\n" +
            $"安装包：{info.AssetName}";
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        var progress = new Progress<double>(p =>
        {
            Progress.Value = p;
            StatusText.Text = $"下载中… {p:F1}%";
        });
        try
        {
            StatusText.Text = "准备下载…";
            var path = await UpdateService.DownloadAsync(_info, progress, _cts.Token);
            StatusText.Text = "下载完成，正在启动安装程序…";
            await Task.Delay(400);
            UpdateService.LaunchInstall(path);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"更新失败：{ex.Message}", "在线更新",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts.Cancel();
}
