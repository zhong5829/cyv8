using System.Windows;

namespace QPGL.Desktop.Views;

/// <summary>
/// 确认退货对话框（复刻旧 fr_sata Frmok）：单号（预览取号只读）/发票号/退款方式/退货原因/发货方式/销售类型。
/// 落库映射：发票号→pno、退款方式→guno、退货原因→remark 后缀、发货方式→t_mode、销售类型→tbsada_header.papertype。
/// </summary>
public partial class ReturnConfirmDialog : Window
{
    public string InvoiceNo { get; private set; } = "";
    public string RefundMethod { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string ShipMode { get; private set; } = "";
    public string PaperType { get; private set; } = "";

    /// <summary>确认后是否立即打印（勾选框，默认勾选；2026-10-31 需求：勾选时静默发送打印机，不弹预览窗）。</summary>
    public bool PrintAfterConfirm => ChkPrintAfter.IsChecked == true;

    /// <param name="paper">预览取号单号（旧 Frmok Init GETNEWDH(0)，正式号确认后再取）</param>
    /// <param name="invoiceDefault">发票号默认值 = 原行 pno（旧 TXTPNO.VALUE = C_TH.PNO）</param>
    /// <param name="refundDict">tbsysset type='guno'</param>
    /// <param name="reasonDict">tbsysset type='退货原因'（可输入）</param>
    /// <param name="shipDict">tbsysset type='fhfs'</param>
    /// <param name="shipDefault">发货方式默认 = 原行 t_mode（空则字典第一项）</param>
    /// <param name="paperTypeDict">tbsysset name='papertype' valuec 去重</param>
    /// <param name="paperTypeDefault">销售类型默认 = 原单头 papertype（空则「门店」，旧 CBOPAPERTYPE 默认逻辑）</param>
    public ReturnConfirmDialog(string paper, string invoiceDefault,
        IReadOnlyList<string> refundDict, IReadOnlyList<string> reasonDict,
        IReadOnlyList<string> shipDict, string shipDefault,
        IReadOnlyList<string> paperTypeDict, string paperTypeDefault)
    {
        InitializeComponent();
        TxtPaper.Text = paper;
        TxtInvoice.Text = invoiceDefault;

        foreach (var s in refundDict) CmbRefund.Items.Add(s);
        // 默认「挂帐」（2026-10-28 需求；字典无此项时回退第一项）
        if (refundDict.Contains("挂帐")) CmbRefund.SelectedItem = "挂帐";
        else if (refundDict.Count > 0) CmbRefund.SelectedIndex = 0;

        foreach (var s in reasonDict) CmbReason.Items.Add(s);

        foreach (var s in shipDict) CmbShip.Items.Add(s);
        // 默认「送货」（2026-10-28 需求）；字典无此项时回退：原行 t_mode → 字典第一项
        if (shipDict.Contains("送货"))
            CmbShip.SelectedItem = "送货";
        else if (!string.IsNullOrEmpty(shipDefault) && shipDict.Contains(shipDefault))
            CmbShip.SelectedItem = shipDefault;
        else if (!string.IsNullOrEmpty(shipDefault))
            CmbShip.Text = shipDefault;   // 旧 IIF(EMPTY(T_MODE), 默认值, T_MODE)
        else if (CmbShip.Items.Count > 0) CmbShip.SelectedIndex = 0;

        foreach (var s in paperTypeDict) CmbPaperType.Items.Add(s);
        if (paperTypeDict.Contains(paperTypeDefault)) CmbPaperType.SelectedItem = paperTypeDefault;
        else CmbPaperType.Text = paperTypeDefault;

        Loaded += (_, _) => TxtInvoice.Focus();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (CmbRefund.SelectedItem is null && string.IsNullOrWhiteSpace(CmbRefund.Text))
        {
            TxtError.Text = "退款方式不能为空！";
            TxtError.Visibility = Visibility.Visible;
            return;
        }
        InvoiceNo = TxtInvoice.Text.Trim();
        RefundMethod = (CmbRefund.SelectedItem as string ?? CmbRefund.Text).Trim();
        Reason = (CmbReason.SelectedItem as string ?? CmbReason.Text).Trim();
        ShipMode = (CmbShip.SelectedItem as string ?? CmbShip.Text).Trim();
        PaperType = (CmbPaperType.SelectedItem as string ?? CmbPaperType.Text).Trim();
        DialogResult = true;
    }
}
