using System.Windows;
using System.Windows.Controls;
using QPGL.Domain.Sales;
using QPGL.Domain.Receipt;

namespace QPGL.Desktop.Views;

/// <summary>
/// 整单确认弹窗（旧系统「整单确认」窗口复刻）：
/// 出库单号只读 + 客户限额/预警限额/客户余额 + 支付方式（tbsysset type='guno'）+
/// 优惠类型/优惠率（row298 CBOTYPE 三方式）+ 发票号/发货方式/销售类型/物流公司 + 优惠前后应收总金额。
/// 确认 → ConfirmSaleCommand（经 SaleViewModel.ConfirmDocAsync 调仓储）。
/// </summary>
public partial class ConfirmSaleDialog : Window
{
    /// <summary>确认后的命令（SaleViewModel 读取后补 Dept 调仓储）。</summary>
    public ConfirmSaleCommand Command { get; private set; } = new() { Dept = "", Paper = "" };

    /// <summary>确认后是否立即打印（勾选框，默认勾选；2026-10-30 需求：勾选时留在开单模式直接发送打印机）。</summary>
    public bool PrintAfterConfirm => ChkPrintAfter.IsChecked == true;

    private decimal _amountBefore;

    public ConfirmSaleDialog(string paper, string docType, string gno, string gname, decimal amountBefore, string remark = "",
        DateTime? outdate = null)
    {
        _paper = paper.Trim();
        _docType = docType;
        _amountBefore = amountBefore;
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        TxtPaper.Text = _paper;
        TxtGno.Text = gno;
        TxtGname.Text = gname;
        // 出库日期带单据实际日期（2026-10-30 修复：改单保留原单日期，弹窗不再固定显示当天）
        DpOutdate.SelectedDate = outdate ?? DateTime.Today;
        TxtAmountBefore.Text = amountBefore.ToString("N2");
        // 单据备注自动带入开单页已填备注（2026-10-30 需求），弹窗内可继续修改
        TxtRemark.Text = remark;
        RecalcAfter();
        TxtYhl.TextChanged += (_, _) => RecalcAfter();
        CboYhlType.SelectionChanged += (_, _) => RecalcAfter();
        _ = LoadAsync();
    }

    private readonly string _paper;
    private readonly string _docType;

    /// <summary>加载下拉（tbsysset 字典）与客户额度/余额。</summary>
    private async System.Threading.Tasks.Task LoadAsync()
    {
        try
        {
            var methods = await Services.Db.Receipts.GetPaymentMethodsAsync();
            CboGuno.ItemsSource = methods.Select(m => m.Name).ToList();
            // 默认「挂帐」（与入库页同语义：非 NS 部署默认挂帐，缺则取第一项）
            var names = methods.Select(m => m.Name).ToList();
            CboGuno.ItemsSource = names;
            CboGuno.SelectedItem = names.Contains("挂帐") ? "挂帐" : names.FirstOrDefault();

            // 发票号（旧 CBOPNO）：自由输入 + 客户档案 tbgu.pno 带入，缺省 '*'
            // 发货方式：tbsysset type='fhfs'（旧 l__.row294），默认「送货」；
            // 销售类型 = 单据类型 papertype（tbsysset type='system' name='papertype'，本库：门店/仓库；旧 P__.row73 CBOPAPERTYPE）
            CboFhfs.ItemsSource = await Services.Db.Stock.GetSyssetNamesAsync("fhfs");
            if (CboFhfs.Items.Contains("送货")) CboFhfs.SelectedItem = "送货";
            CboXslx.ItemsSource = await Services.Db.Stock.GetPaperTypesAsync();
            if (CboXslx.Items.Contains("仓库")) CboXslx.SelectedItem = "仓库";
            if (TxtGname.Text.Trim().Length > 0)
            {
                var pno = await Services.Db.Partners.GetCustomerPnoAsync(TxtGname.Text.Trim());
                if (!string.IsNullOrWhiteSpace(pno)) TxtFphs.Text = pno;
            }

            await LoadCustomerInfoAsync();
        }
        catch { /* 下拉/限额加载失败不阻断 */ }
    }

    /// <summary>客户名称变化时刷新：客户限额/预警限额（tbgu.xede/yjxe）+ 客户余额（tbysyf Σ(ys-yf)）。</summary>
    private async System.Threading.Tasks.Task LoadCustomerInfoAsync()
    {
        try
        {
            var gname = TxtGname.Text.Trim();
            if (gname.Length == 0) { TxtXede.Text = "0"; TxtYjxe.Text = "0"; TxtYue.Text = "0"; return; }
            var (xede, yjxe) = await Services.Db.Partners.GetLimitAsync(gname);
            TxtXede.Text = xede.ToString("N2");
            TxtYjxe.Text = yjxe.ToString("N2");
            TxtYue.Text = (await Services.Db.Xlsk.GetCustomerYueAsync(gname)).ToString("N2");
        }
        catch { /* 失败保持现值不阻断 */ }
    }

    private async void TxtGname_LostFocus(object sender, RoutedEventArgs e) => await LoadCustomerInfoAsync();

    /// <summary>优惠后应收总金额实时试算（row298 CBOTYPE 三方式）。</summary>
    private void RecalcAfter()
    {
        var yhl = ParseDecimal(TxtYhl.Text);
        var after = CboYhlType.SelectedIndex switch
        {
            1 => _amountBefore - yhl,                            // 折让：减固定额
            2 => _amountBefore * (100 + yhl) / 100,              // 上浮
            _ => _amountBefore * (100 - yhl) / 100,              // 优惠率
        };
        TxtAmountAfter.Text = after.ToString("N2");
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (CboGuno.SelectedItem is null)
        {
            MessageBox.Show("请选择支付方式", "整单确认", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Command = new ConfirmSaleCommand
        {
            Dept = "",                       // 由调用方补（AppSession.DeptNo）
            Paper = _paper,
            DocType = _docType,
            Gno = TxtGno.Text.Trim(),
            Gname = TxtGname.Text.Trim(),
            Guno = CboGuno.SelectedItem.ToString() ?? "",
            Pno = "",
            TMode = CboFhfs.Text.Trim(),
            Yhl = ParseDecimal(TxtYhl.Text),
            YhlMode = CboYhlType.SelectedIndex + 1,
            Outdate = DpOutdate.SelectedDate ?? DateTime.Today,
            Remark = TxtRemark.Text.Trim(),
            Fphs = TxtFphs.Text.Trim(),
            Wlgs = TxtWlgs.Text.Trim(),
            Papertype = CboXslx.Text.Trim(),   // 销售类型 → tbsada_header.papertype（门店/仓库），此前漏传致落库空白
            AmountBefore = _amountBefore,
            AmountAfter = ParseDecimal(TxtAmountAfter.Text),
        };
        DialogResult = true;
    }

    private static decimal ParseDecimal(string? text) =>
        decimal.TryParse((text ?? "").Trim(), out var v) ? v : 0m;
}
