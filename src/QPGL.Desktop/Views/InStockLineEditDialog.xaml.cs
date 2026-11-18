using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.Services;
using QPGL.Desktop.ViewModels;
using QPGL.Domain.Parts;

namespace QPGL.Desktop.Views;

/// <summary>入库明细行编辑结果（对话框 → VM 装配用）。</summary>
public sealed class InStockLineEditResult
{
    public decimal Jkamount { get; init; }
    public decimal Iprc { get; init; }
    public decimal Iprj { get; init; }
    public decimal Oprc { get; init; }
    public decimal Oprb { get; init; }
    /// <summary>最低售价（2026-11-15 需求；调用方落 tbprnoty.oprc_min，不随明细行）。</summary>
    public decimal OprcMin { get; init; }
    public string Depotno { get; init; } = "";
    public string Posi { get; init; } = "";
    public string Fa { get; init; } = "";
    public string Dc { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>
/// 入库明细行编辑对话框：双击配件选择行后弹出，
/// 设置数量/进价(含税)/进价(不含税)/售价/批发价/库号/仓位/分类码/产地/单位/车型/备注。
/// 确认后经 Owner（InStockControl）写入明细行。数量与进价(含税)必填（row31 校验语义）。
/// </summary>
public partial class InStockLineEditDialog : Window
{
    private readonly PartItem _part;

    /// <summary>确认后的结果（DialogResult=true 时读取）。</summary>
    public decimal Jkamount { get; private set; }
    public decimal Iprc { get; private set; }
    public decimal Iprj { get; private set; }
    public decimal Oprc { get; private set; }
    public decimal Oprb { get; private set; }
    /// <summary>最低售价（2026-11-15 需求；调用方比对后写 tbprnoty.oprc_min）。</summary>
    public decimal OprcMin { get; private set; }
    public string Depotno { get; private set; } = "";
    public string Posi { get; private set; } = "";
    public string Fa { get; private set; } = "";
    public string Dc { get; private set; } = "";
    public string Unit { get; private set; } = "";
    public string Ty { get; private set; } = "";
    public string Note { get; private set; } = "";

    private readonly IReadOnlyList<string> _warehouses;

    public InStockLineEditDialog(PartItem part, IReadOnlyList<string> warehouses) : this(part, warehouses, null) { }

    /// <summary>编辑已有明细行模式（existing 非空）：预填该行当前值，确认后回写该行。
    /// currentOprcMin=当前属性档最低售价（2026-11-15 需求：调用方查 tbprnoty 后传入，预填显示）。</summary>
    public InStockLineEditDialog(PartItem part, IReadOnlyList<string> warehouses, InStockLineRow? existing,
        decimal currentOprcMin = -1m)
    {
        InitializeComponent();
        IntegerInput.Restrict(TxtQty);   // 数量只允许整数录入（2026-11-07 需求：向零截断口径）
        RestrictDecimal(TxtOprcMin, 2);  // 最低售价两位小数（2026-11-15 需求）
        Owner = Application.Current.MainWindow;
        _part = part;
        _warehouses = warehouses;
        _existing = existing;

        // 信息区顺序（2026-11-18 需求）：内部编号、配件名称、车型（头部车型=生效值，可编辑车型见下方 TxtTy）
        TxtNno.Text = part.Nno;
        TxtNa1.Text = part.Na1;
        TxtTyHead.Text = existing?.Ty?.Trim().Length > 0 ? existing.Ty : part.Ty;
        TxtTy.Text = existing?.Ty?.Trim().Length > 0 ? existing.Ty : part.Ty;
        TxtUnit.Text = existing?.Unit?.Trim().Length > 0 ? existing.Unit : part.Unit;
        TxtFa.Text = existing?.Fa?.Trim().Length > 0 ? existing.Fa : part.Fa;
        TxtDc.Text = existing?.Dc?.Trim().Length > 0 ? existing.Dc : part.Dc;
        TxtPosi.Text = existing?.Posi?.Trim().Length > 0 ? existing.Posi : part.Posi;
        if (existing is not null)
        {
            // 编辑模式：预填该行当前值（数量为当前值 +1 备选基数仍从 1 开始语义由用户手改）
            TxtQty.Text = existing.Jkamount.ToString("0.##");
            TxtIprj.Text = existing.Iprj.ToString("N2");
            TxtIprc.Text = existing.Iprc.ToString("N2");
            TxtOprc.Text = existing.Oprc.ToString("N2");
            TxtOprb.Text = existing.Oprb.ToString("N2");
            TxtNote.Text = existing.Note?.Trim() ?? "";
        }
        else
        {
            TxtQty.Text = "1";
            TxtIprj.Text = part.Iprj.ToString("N2");
            TxtIprc.Text = part.Iprc.ToString("N2");
            TxtOprc.Text = part.Oprc.ToString("N2");
            TxtOprb.Text = part.Oprb.ToString("N2");
            TxtNote.Text = "";
        }
        // 2026-11-08 修复（生效后「总进价」全为 0）：进价(不含税)预填值恒为 0（属性库 tbprnoty 无该单价、
        // 存量批次 tbisto.iprc=0），而上面先赋 TxtIprj 时 Iprj_TextChanged 同步过来的金额又被
        // TxtIprc = part.Iprc 覆盖回 0；用户直接回车确认（Iprj_KeyDown）就会把 Iprc=0 落库。
        // 按 P2 口径「进价(含税)与进价(不含税)按同金额同步（不按税率换算）」在预填后补齐（仅当原值为空时，不覆盖真实值）。
        if (ParseDecimal(TxtIprc.Text) <= 0 && ParseDecimal(TxtIprj.Text) > 0)
            TxtIprc.Text = TxtIprj.Text;

        // 最低售价预填（2026-11-15 需求）：调用方查 tbprnoty.oprc_min 传入（<0=未知，显示 0）
        TxtOprcMin.Text = (currentOprcMin >= 0 ? currentOprcMin : 0m).ToString("N2");

        CboDepotno.ItemsSource = _warehouses;
        CboDepotno.SelectedItem = _warehouses.Contains(existing?.Depotno ?? part.Depotno)
            ? (existing?.Depotno ?? part.Depotno) : _warehouses.FirstOrDefault();
        // 编辑模式无库存上下文（PartItem 仅含行值），不显示"当前库存"以免误导
        TxtStockInfo.Text = existing is not null
            ? $"货位：{existing.Posi?.Trim()}    备注：{existing.Note?.Trim()}"
            : $"当前库存：{part.Kcamount:0.##}    货位：{part.Posi}    备注：{part.Note}";
        Title = existing is not null ? $"入库明细编辑 - {part.Nno}（修改已有行）" : "入库明细编辑";

        // 2026-11-02 需求：打开默认定位"进货数量"并全选
        Loaded += (_, _) => { TxtQty.Focus(); TxtQty.SelectAll(); };
    }

    private readonly InStockLineRow? _existing;

    /// <summary>进货数量框回车 → 跳转进价（含税）框并全选（2026-11-02 需求）。</summary>
    private void Qty_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        TxtIprj.Focus();
        TxtIprj.SelectAll();
        e.Handled = true;
    }

    /// <summary>
    /// 进价（含税）变化 → 进价（不含税）同步为同金额（P2，2026-11-08 用户确认：
    /// 含税价与不含税价按同金额同步，不按税率换算）。
    /// </summary>
    private void Iprj_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtIprc is null) return;   // InitializeComponent 初始化期触发
        TxtIprc.Text = TxtIprj.Text;
    }

    /// <summary>进价（含税）框回车 → 跳转最低售价框并全选（2026-11-15 需求：回车链路覆盖最低售价）。</summary>
    private void Iprj_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        TxtOprcMin.Focus();
        TxtOprcMin.SelectAll();
        e.Handled = true;
    }

    /// <summary>最低售价框回车 = 确认保存（2026-11-15 需求：数量→进价(含税)→最低售价→确认）。</summary>
    private void OprcMin_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        BtnOk_Click(sender, e);
    }

    /// <summary>最低售价输入约束（StockEditWindow 同款）：仅数字+一个小数点，两位小数；粘贴同校验；允许 "12." 中间态。</summary>
    private static void RestrictDecimal(TextBox box, int decimals)
    {
        box.PreviewTextInput += (_, e) =>
        {
            var newText = box.Text.Remove(box.SelectionStart, box.SelectionLength).Insert(box.SelectionStart, e.Text);
            e.Handled = !IsValidDecimal(newText, decimals);
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.Handled = true; return; }
            var paste = (string?)e.DataObject.GetData(DataFormats.Text) ?? "";
            var newText = box.Text.Remove(box.SelectionStart, box.SelectionLength).Insert(box.SelectionStart, paste);
            e.Handled = !IsValidDecimal(newText, decimals);
        });
    }

    private static bool IsValidDecimal(string text, int decimals)
    {
        if (text.Length == 0) return true;
        if (text.EndsWith(".")) text = text[..^1];   // 允许 "12." 中间态
        if (text.Length == 0) return false;
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        var dot = text.IndexOf('.');
        return dot < 0 || text.Length - dot - 1 <= decimals;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var qty = ParseDecimal(TxtQty.Text);
        if (qty <= 0)
        {
            MessageBox.Show("进货数量必须大于 0", "入库明细编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!IntegerInput.IsInteger(TxtQty.Text, out _))
        {
            MessageBox.Show("进货数量必须是整数（不能带小数）", "入库明细编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var iprj = ParseDecimal(TxtIprj.Text);
        if (iprj < 0)
        {
            MessageBox.Show("进价不能为负", "入库明细编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var oprcMin = ParseDecimal(TxtOprcMin.Text);
        if (oprcMin < 0)
        {
            MessageBox.Show("最低售价不能为负", "入库明细编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Jkamount = qty;
        Iprc = ParseDecimal(TxtIprc.Text);
        Iprj = iprj;
        Oprc = ParseDecimal(TxtOprc.Text);
        Oprb = ParseDecimal(TxtOprb.Text);
        OprcMin = oprcMin;
        Depotno = (CboDepotno.SelectedItem as string ?? "").Trim();
        Posi = TxtPosi.Text.Trim();
        Fa = TxtFa.Text.Trim();
        Dc = TxtDc.Text.Trim();
        Unit = TxtUnit.Text.Trim();
        Ty = TxtTy.Text.Trim();
        Note = TxtNote.Text.Trim();
        DialogResult = true;
    }

    private static decimal ParseDecimal(string? text) =>
        decimal.TryParse((text ?? "").Trim(), out var v) ? v : 0m;
}
