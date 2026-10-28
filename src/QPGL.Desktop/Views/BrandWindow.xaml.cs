using System.Windows;
using QPGL.Desktop.Services;
using QPGL.Infrastructure.Auth;

namespace QPGL.Desktop.Views;

/// <summary>
/// 厂牌选择窗（旧 fr_cp 复刻）：登录成功后必选一次；主窗 F11 可随时重开切换。
/// 确认后 SetBrand（P_CP/P_WN/P_SNNO 会话级变量，已开页面不刷新，下次取数生效）。
/// </summary>
public partial class BrandWindow : Window
{
    /// <summary>true = 登录后强制选择（取消=退出系统）；false = F11 主动切换（取消=保留现厂牌）。</summary>
    private readonly bool _mandatory;

    public BrandWindow(bool mandatory)
    {
        InitializeComponent();
        _mandatory = mandatory;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        var brands = await Db.Brands.GetBrandsAsync(AppSession.DeptNo);
        // 白名单过滤（旧 fr_cp Load：'@'/'$' 或 usemcp 空 = 全部；否则按 usemcp 白名单；"急件"恒可选）
        var usem = AppSession.Permissions;
        var isSuper = usem.Contains("@") || usem.Contains("$");
        if (!isSuper && AppSession.BrandWhitelist.Length > 0)
        {
            var wl = AppSession.BrandWhitelist;
            brands = brands.Where(b => wl.Contains(b.name)).ToList();
        }
        BrandList.ItemsSource = brands.Select(b => new BrandItem(
            b.name, b.valuec, b.cxnno, $"{b.name}  {(string.IsNullOrEmpty(b.valuec) ? "" : $"({b.valuec})")}")).ToList();
        if (BrandList.Items.Count > 0)
        {
            // WHO 记忆定位（旧 fr_cp __.row2：读 WHO.NCP 定位列表行，无记录默认第 1 行）
            BrandList.SelectedIndex = Math.Min(BrandPreference.GetIndex(AppSession.Operator), BrandList.Items.Count) - 1;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (BrandList.SelectedItem is not BrandItem item)
        {
            MessageBox.Show("请选择厂牌。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AppSession.SetBrand(item.Name, item.Currency, item.Cxnno);
        // WHO 记忆（旧 fr_cp T_.row5：勾选记住→存所选行号；未勾选→重置 1）
        BrandPreference.SaveIndex(AppSession.Operator,
            RememberCheck.IsChecked == true ? BrandList.SelectedIndex + 1 : 1);
        DialogResult = true;
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        if (_mandatory)
            Application.Current.Shutdown();   // 旧系统登录后不选厂牌即退出
        else
            DialogResult = false;
    }

    private sealed record BrandItem(string Name, string Currency, string Cxnno, string DisplayName);
}
