using System.Windows;
using QPGL.Domain.Parts;

namespace QPGL.Desktop.Views;

/// <summary>
/// 修改库存售价窗（2026-11-04 需求，复刻旧系统弹窗）：销售开单页配件列表右键入口。
/// 回显 tbisto 行 仓位/产地/售价/批发价 + tbprnoty 最低售价；确定后由 SaleViewModel 调
/// Db.Order4.ChangeStockPriceAsync 落库（tbisto 该编码全行 + tbprnoty.oprc_min + tbistoch 日志）。
/// </summary>
public partial class StockPriceEditWindow : Window
{
    public StockPriceEditWindow(PartItem part)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        TxtNno.Text = $"{part.Cp.Trim()} {part.Nno.Trim()}".Trim();
        TxtPosi.Text = part.Posi.Trim();
        TxtDc.Text = part.Dc.Trim();
        TxtOprc.Text = part.Oprc.ToString("0.##");
        TxtOprb.Text = part.Oprb.ToString("0.##");
        TxtOprcMin.Text = part.OprcMin.ToString("0.##");
        LblPer.Text = $"操作员: {Services.AppSession.Operator.Trim()}";
        LblDate.Text = $"日期: {DateTime.Today:yyyy-MM-dd}";
    }

    public string Posi => TxtPosi.Text.Trim();
    public string Dc => TxtDc.Text.Trim();
    public decimal Oprc => ParsePrice(TxtOprc.Text);
    public decimal Oprb => ParsePrice(TxtOprb.Text);
    public decimal OprcMin => ParsePrice(TxtOprcMin.Text);

    private static decimal ParsePrice(string text)
        => decimal.TryParse(text.Trim(), out var v) ? v : 0m;

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (box, name) in new[] { (TxtOprc, "售价"), (TxtOprb, "批发价"), (TxtOprcMin, "最低售价") })
        {
            if (!decimal.TryParse(box.Text.Trim(), out _))
            {
                MessageBox.Show($"{name}不是有效的数字。", "修改库存售价", MessageBoxButton.OK, MessageBoxImage.Warning);
                box.Focus();
                box.SelectAll();
                return;
            }
        }
        DialogResult = true;
    }
}
