using System.Windows;
using System.Windows.Input;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.Views;

/// <summary>入库退货行录入结果（对话框 → VM 装配用）。</summary>
public sealed class PurchaseReturnLineResult
{
    public decimal Amount { get; init; }
    public decimal Iprc { get; init; }
}

/// <summary>
/// 入库退货行编辑对话框：双击/回车可退流水行后弹出（2026-11-04 需求），
/// 设置退货数量与进价，确认后加入退货车明细区（外币价取源行，不在弹窗内编辑）。
/// 默认退货数量 = 可退数，进价取源行。BA01 锁价（有 BA01 且非超级）时进价只读。
/// 退货数量必须在 (0, 可退数] 内，数量框离开时即校验（2026-11-04 需求）；确认接口另有 AMOUNT_EXCEED 兜底。
/// </summary>
public partial class PurchaseReturnLineDialog : Window
{
    private readonly decimal _ktx;   // 可退数上限（ReturnableLine.Amount）

    /// <summary>确认后的结果（DialogResult=true 时读取）。</summary>
    public decimal Amount { get; private set; }
    public decimal Iprc { get; private set; }
    /// <summary>不扣库存（勾选框，默认未勾选=正常扣库存；勾选=只写负数流水不冲减库存）。</summary>
    public bool NoStock { get; private set; }

    public PurchaseReturnLineDialog(ReturnableLine line, bool priceLocked)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _ktx = line.Amount;

        TxtNno.Text = line.Nno;
        TxtNa1.Text = line.Na1;
        TxtCp.Text = line.Cp;
        TxtQty.Text = line.Amount.ToString("0.##");
        TxtIprc.Text = line.Iprc.ToString("N2");
        TxtIprc.IsReadOnly = priceLocked;
        TxtStockInfo.Text =
            $"原进库数：{line.Oamount:0.##}    可退数：{line.Amount:0.##}    当前库存：{line.Kshu:0.##}    入库单号：{line.Cno}";

        // 打开默认定位"退货数量"并全选
        Loaded += (_, _) => { TxtQty.Focus(); TxtQty.SelectAll(); };
    }

    /// <summary>退货数量框：回车/↑↓ → 校验后跳转进价框并全选（2026-11-04 需求）。</summary>
    private void Qty_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Up:
            case Key.Down:
                e.Handled = true;
                if (!ValidateQty()) return;
                FocusInput(TxtIprc);
                break;
        }
    }

    /// <summary>进价框：回车 = 确认加入退货车；↑↓ 在数量/进价框间切换并全选（2026-11-04 需求）。</summary>
    private void Price_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                BtnOk_Click(sender, e);
                break;
            case Key.Up:
            case Key.Down:
                FocusInput(TxtQty);
                e.Handled = true;
                break;
        }
    }

    /// <summary>切换焦点并全选内容。</summary>
    private static void FocusInput(System.Windows.Controls.TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    /// <summary>退货数量校验：> 0 且 ≤ 可退数，不合法弹提示并回焦数量框全选。</summary>
    private bool ValidateQty()
    {
        var qty = ParseDecimal(TxtQty.Text);
        if (qty <= 0)
        {
            MessageBox.Show("退货数量必须大于 0", "入库退货行", MessageBoxButton.OK, MessageBoxImage.Warning);
            FocusInput(TxtQty);
            return false;
        }
        if (qty > _ktx)
        {
            MessageBox.Show($"退货数量不能大于可退数 {_ktx:0.##}", "入库退货行", MessageBoxButton.OK, MessageBoxImage.Warning);
            FocusInput(TxtQty);
            return false;
        }
        return true;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateQty()) return;

        Amount = ParseDecimal(TxtQty.Text);
        Iprc = ParseDecimal(TxtIprc.Text);
        NoStock = ChkNoStock.IsChecked == true;
        DialogResult = true;
    }

    private static decimal ParseDecimal(string? text) =>
        decimal.TryParse((text ?? "").Trim(), out var v) ? v : 0m;
}
