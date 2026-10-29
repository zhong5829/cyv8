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
/// 设置数量/进价(不含税)/含税价/售价/批发价/库号/仓位/分类码/产地/单位/车型/备注。
/// 确认后经 Owner（InStockControl）写入明细行。数量与不含税进价必填（row31 校验语义）。
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
    public string Depotno { get; private set; } = "";
    public string Posi { get; private set; } = "";
    public string Fa { get; private set; } = "";
    public string Dc { get; private set; } = "";
    public string Unit { get; private set; } = "";
    public string Ty { get; private set; } = "";
    public string Note { get; private set; } = "";

    private readonly IReadOnlyList<string> _warehouses;

    public InStockLineEditDialog(PartItem part, IReadOnlyList<string> warehouses) : this(part, warehouses, null) { }

    /// <summary>编辑已有明细行模式（existing 非空）：预填该行当前值，确认后回写该行。</summary>
    public InStockLineEditDialog(PartItem part, IReadOnlyList<string> warehouses, InStockLineRow? existing)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _part = part;
        _warehouses = warehouses;
        _existing = existing;

        TxtCp.Text = part.Cp;
        TxtNno.Text = part.Nno;
        TxtNa1.Text = part.Na1;
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
        CboDepotno.ItemsSource = _warehouses;
        CboDepotno.SelectedItem = _warehouses.Contains(existing?.Depotno ?? part.Depotno)
            ? (existing?.Depotno ?? part.Depotno) : _warehouses.FirstOrDefault();
        // 编辑模式无库存上下文（PartItem 仅含行值），不显示"当前库存"以免误导
        TxtStockInfo.Text = existing is not null
            ? $"货位：{existing.Posi?.Trim()}    备注：{existing.Note?.Trim()}"
            : $"当前库存：{part.Kcamount:0.##}    货位：{part.Posi}    备注：{part.Note}";
        Title = existing is not null ? $"入库明细编辑 - {part.Nno}（修改已有行）" : "入库明细编辑";
    }

    private readonly InStockLineRow? _existing;

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var qty = ParseDecimal(TxtQty.Text);
        if (qty <= 0)
        {
            MessageBox.Show("进货数量必须大于 0", "入库明细编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var iprj = ParseDecimal(TxtIprj.Text);
        if (iprj < 0)
        {
            MessageBox.Show("进价不能为负", "入库明细编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Jkamount = qty;
        Iprc = ParseDecimal(TxtIprc.Text);
        Iprj = iprj;
        Oprc = ParseDecimal(TxtOprc.Text);
        Oprb = ParseDecimal(TxtOprb.Text);
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
