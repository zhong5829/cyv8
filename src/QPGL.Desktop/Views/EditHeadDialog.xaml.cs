using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Domain.Receipt;

namespace QPGL.Desktop.Views;

/// <summary>
/// 修改单头对话框（2026-11-03 需求）：
/// 付款方式改下拉选择（tbsysset type='guno'，默认挂帐——与入库页同机制）；直发客户改下拉联想
/// （Db.Partners.SearchAsync：tbgu 按 gname/gno/code 拼音码/code1 数字码/tel/mobile 前缀匹配，300ms 防抖，
/// ↑/↓ 选行、回车确认、Esc 关联想——与销售开单页 GnameBox 同机制）。确定后由 VM 按合同号整体更新单头。
/// </summary>
public partial class EditHeadDialog : Window
{
    private readonly DispatcherTimer _custDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly string _fkfs;

    public string NewCno => CnoBox.Text.Trim();
    public DateTime Orderdate => DatePick.SelectedDate ?? DateTime.Today;
    public string Bzlx => BzlxBox.Text.Trim();
    public string Fkfs => FkfsCombo.SelectedItem is PaymentMethod pm ? pm.Name.Trim() : FkfsCombo.Text.Trim();
    public string Gyname => GyBox.Text.Trim();
    public string Gname => GnameBox.Text.Trim();

    public EditHeadDialog(string cno, DateTime orderdate, string bzlx, string fkfs, string gyname, string gname)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        DatePick.SelectedDate = orderdate;
        CnoBox.Text = cno;
        BzlxBox.Text = bzlx;
        GyBox.Text = gyname;
        GnameBox.Text = gname;
        _fkfs = fkfs;
        _custDebounce.Tick += async (_, _) =>
        {
            _custDebounce.Stop();
            await SearchCustomersAsync();
        };
        Loaded += async (_, _) =>
        {
            await LoadPaymentMethodsAsync();
            CnoBox.Focus();
        };
    }

    /// <summary>付款方式下拉（tbsysset type='guno'）：默认挂帐，无则回退列表第一项，都没有保留原值。</summary>
    private async System.Threading.Tasks.Task LoadPaymentMethodsAsync()
    {
        try
        {
            var methods = await Db.Receipts.GetPaymentMethodsAsync();
            FkfsCombo.ItemsSource = methods;
            var current = _fkfs.Length > 0 ? _fkfs : "挂帐";
            var hit = methods.FirstOrDefault(m => m.Name.Trim() == current)
                      ?? methods.FirstOrDefault(m => m.Name.Trim() == "挂帐")
                      ?? methods.FirstOrDefault();
            FkfsCombo.SelectedItem = hit;
        }
        catch
        {
            // 下拉加载失败不阻断，保留原值可手输
        }
    }

    private async System.Threading.Tasks.Task SearchCustomersAsync()
    {
        var kw = GnameBox.Text.Trim();
        if (kw.Length == 0)
        {
            GnameList.ItemsSource = null;
            GnamePopup.IsOpen = false;
            return;
        }
        try
        {
            var request = new PagedRequest { Page = 1, PageSize = 200, Sort = "partName", Order = "asc", Keyword = kw };
            var result = await Db.Partners.SearchAsync(request);
            GnameList.ItemsSource = result.Items;
            GnamePopup.IsOpen = result.Items.Count > 0;
            if (GnameList.Items.Count > 0) GnameList.SelectedIndex = 0;
        }
        catch
        {
            // 联想失败不阻断输入
        }
    }

    private void GnameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _custDebounce.Stop();
        _custDebounce.Start();
    }

    private void GnameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (GnameList.Items.Count > 0)
                {
                    GnamePopup.IsOpen = true;
                    GnameList.SelectedIndex = System.Math.Min(GnameList.SelectedIndex + 1, GnameList.Items.Count - 1);
                    GnameList.ScrollIntoView(GnameList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (GnameList.Items.Count > 0 && GnameList.SelectedIndex > 0)
                {
                    GnameList.SelectedIndex--;
                    GnameList.ScrollIntoView(GnameList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (GnamePopup.IsOpen && GnameList.SelectedItem is Partner p)
                {
                    GnameBox.Text = p.PartName.Trim();
                    GnameBox.CaretIndex = GnameBox.Text.Length;
                    GnamePopup.IsOpen = false;
                    e.Handled = true;
                }
                // 联想未开：不拦截，回车触发「确定」默认按钮
                break;
            case Key.Escape when GnamePopup.IsOpen:
                GnamePopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void GnameList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GnameList.SelectedItem is Partner p)
        {
            GnameBox.Text = p.PartName.Trim();
            GnameBox.CaretIndex = GnameBox.Text.Length;
        }
        GnamePopup.IsOpen = false;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (NewCno.Length == 0)
        {
            MessageBox.Show("请输入新合同号。", "修改单头", MessageBoxButton.OK, MessageBoxImage.Warning);
            CnoBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
