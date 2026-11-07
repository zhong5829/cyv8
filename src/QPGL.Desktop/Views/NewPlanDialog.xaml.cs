using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Views;

/// <summary>
/// 新建计划单对话框（2026-11-03 需求）：计划单号 + 订货日期 + 供应商（拼音码联想）。
/// 供应商联想与入库页同机制（Db.Suppliers.SearchMergedAsync：合并 tbgu + tbgugys，按 gyname/gyno/code 拼音码/code1 数字码
/// 前缀匹配，300ms 防抖；↑/↓ 选行、回车确认、Esc 关联想），确定后由 VM 校验单号并存为新单默认单头。
/// </summary>
public partial class NewPlanDialog : Window
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public string PlanCno => CnoBox.Text.Trim();
    public DateTime Orderdate => DatePick.SelectedDate ?? DateTime.Today;
    public string Gyname => GysBox.Text.Trim();

    public NewPlanDialog()
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        DatePick.SelectedDate = DateTime.Today;
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await SearchAsync();
        };
        Loaded += (_, _) => CnoBox.Focus();
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        var kw = GysBox.Text.Trim();
        if (kw.Length == 0)
        {
            GysList.ItemsSource = null;
            GysPopup.IsOpen = false;
            return;
        }
        try
        {
            var request = new PagedRequest { Page = 1, PageSize = 200, Sort = "gyno", Order = "asc", Keyword = kw };
            var result = await Db.Suppliers.SearchMergedAsync(request);
            GysList.ItemsSource = result.Items;
            GysPopup.IsOpen = result.Items.Count > 0;
            if (GysList.Items.Count > 0) GysList.SelectedIndex = 0;
        }
        catch
        {
            // 联想失败不阻断输入
        }
    }

    private void GysBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void GysBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (GysList.Items.Count > 0)
                {
                    GysPopup.IsOpen = true;
                    GysList.SelectedIndex = System.Math.Min(GysList.SelectedIndex + 1, GysList.Items.Count - 1);
                    GysList.ScrollIntoView(GysList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (GysList.Items.Count > 0 && GysList.SelectedIndex > 0)
                {
                    GysList.SelectedIndex--;
                    GysList.ScrollIntoView(GysList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (GysPopup.IsOpen && GysList.SelectedItem is SupplierRow s)
                {
                    GysBox.Text = s.Gyname.Trim();
                    GysBox.CaretIndex = GysBox.Text.Length;
                    GysPopup.IsOpen = false;
                    e.Handled = true;
                }
                // 联想未开：不拦截，回车触发「确定」默认按钮
                break;
            case Key.Escape when GysPopup.IsOpen:
                GysPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void GysList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GysList.SelectedItem is SupplierRow s)
        {
            GysBox.Text = s.Gyname.Trim();
            GysBox.CaretIndex = GysBox.Text.Length;
        }
        GysPopup.IsOpen = false;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (PlanCno.Length == 0)
        {
            MessageBox.Show("请输入计划单号。", "新建计划单", MessageBoxButton.OK, MessageBoxImage.Warning);
            CnoBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
