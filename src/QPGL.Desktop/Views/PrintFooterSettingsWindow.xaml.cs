using System.Windows;
using QPGL.Desktop.Services;
using QPGL.Domain.Printing;

namespace QPGL.Desktop.Views;

/// <summary>打印设置窗（票头只读 + 票尾 RPSET1/2 编辑写回 tbsysset，直连模式直调仓储）。</summary>
public partial class PrintFooterSettingsWindow : Window
{
    private PrintSettings? _settings;

    public string Message { get; set; } = "";
    public string CompanyInfo { get; set; } = "";

    public PrintFooterSettingsWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        try
        {
            _settings = await Db.PrintSettings.GetAsync();
            CompanyInfo = $"公司：{_settings.CompanyName}    电话：{_settings.Tel}\n地址：{_settings.Address}\n开户行：{_settings.Bank}    账号：{_settings.Account}";
            Part1Box.Text = _settings.FooterPart1;
            Part2Box.Text = _settings.FooterPart2;
        }
        catch (Exception ex)
        {
            Message = "加载打印设置失败：" + ex.Message;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Db.PrintSettings.SaveFooterAsync(Part1Box.Text, Part2Box.Text);
            Message = "票尾已保存（tbsysset RPSET1/RPSET2），旧系统打印同步生效";
        }
        catch (Exception ex)
        {
            Message = "保存失败：" + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
