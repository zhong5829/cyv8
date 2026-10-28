using System.Windows;
using System.Windows.Controls;
using QPGL.Domain.Sales;

namespace QPGL.Desktop.Views;

/// <summary>
/// 退货编辑窗（复刻旧 fr_sata 编辑窗「添加」布局）：
/// 售价/实售价/出库数/已退数只读；售价改为/实售价改为/本次退货数量/库号/仓位可编辑。
/// 退货行落库值 = 售价改为/实售价改为，库号+仓位经仓储 KCID 链重解析回补批次。
/// </summary>
public partial class ReturnLineEditDialog : Window
{
    private readonly List<ReturnBatchOption> _batches;

    public decimal ReturnQty { get; private set; }
    public decimal Oprc { get; private set; }
    public decimal Oprct { get; private set; }
    public string DepotNo { get; private set; } = "";
    public string Posi { get; private set; } = "";

    public ReturnLineEditDialog(string caption, string partContext,
        decimal oprc, decimal oprct, decimal qty, decimal returned, decimal remain,
        string depotNo, string posi, IReadOnlyList<ReturnBatchOption> batches)
    {
        InitializeComponent();
        Title = caption;
        _batches = [.. batches];
        TxtPartContext.Text = partContext;

        TxtOprc.Text = oprc.ToString("N2");
        TxtOprct.Text = oprct.ToString("N2");
        TxtQty.Text = qty.ToString("0.##");
        TxtReturned.Text = returned.ToString("0.##");
        TxtReturnQty.Text = remain.ToString("0.##");   // 默认=未退量（旧图示例 出库2/已退0/本次2）
        TxtOprcNew.Text = oprc.ToString("N2");
        TxtOprctNew.Text = oprct.ToString("N2");

        // 库号/仓位：实际批次去重；默认当前值（列表为空时保留手输）
        foreach (var d in _batches.Select(b => b.DepotNo).Distinct()) CmbDepot.Items.Add(d);
        if (!string.IsNullOrEmpty(depotNo)) CmbDepot.SelectedItem = depotNo;
        else if (CmbDepot.Items.Count > 0) CmbDepot.SelectedIndex = 0;
        else CmbDepot.Text = depotNo ?? "";
        FillPosiItems(CmbDepot.Text, posi);

        Loaded += (_, _) => { TxtReturnQty.Focus(); TxtReturnQty.SelectAll(); };
    }

    private void FillPosiItems(string depot, string? prefer)
    {
        CmbPosi.Items.Clear();
        var posis = _batches.Where(b => b.DepotNo == depot).Select(b => b.Posi).Distinct().ToList();
        foreach (var p in posis) CmbPosi.Items.Add(p);
        if (!string.IsNullOrEmpty(prefer) && posis.Contains(prefer)) CmbPosi.SelectedItem = prefer;
        else if (posis.Count > 0) CmbPosi.SelectedIndex = 0;
        else CmbPosi.Text = prefer ?? "";
    }

    private void CmbDepot_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FillPosiItems(CmbDepot.Text, null);

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(TxtReturnQty.Text.Trim(), out var returnQty) || returnQty <= 0)
        {
            ShowError("本次退货数量必须大于 0！");
            return;
        }
        if (!decimal.TryParse(TxtQty.Text, out var qty) || !decimal.TryParse(TxtReturned.Text, out var returned))
        {
            ShowError("原行出库数/已退数异常，请重新加载！");
            return;
        }
        if (returnQty > qty - returned)
        {
            ShowError($"本次退货数量不能超过未退量 {qty - returned:0.##}！");
            return;
        }
        if (!decimal.TryParse(TxtOprcNew.Text.Trim(), out var oprc) || oprc < 0 ||
            !decimal.TryParse(TxtOprctNew.Text.Trim(), out var oprct) || oprct < 0)
        {
            ShowError("售价/实售价必须为不小于 0 的数字！");
            return;
        }
        ReturnQty = returnQty;
        Oprc = oprc;
        Oprct = oprct;
        DepotNo = CmbDepot.Text.Trim();
        Posi = CmbPosi.Text.Trim();
        DialogResult = true;
    }

    private void ShowError(string msg)
    {
        TxtError.Text = msg;
        TxtError.Visibility = Visibility.Visible;
    }
}
