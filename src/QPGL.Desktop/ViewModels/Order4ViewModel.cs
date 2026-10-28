using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.ViewModels;

/// <summary>选料行着色转换器：已订数 Num &gt; 0 → 红字 #C0392B。</summary>
public sealed class PartPickBrushConverter : IValueConverter
{
    private static readonly Brush Ordered = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PartPickRow r && r.Num > 0 ? Ordered : Brushes.Black;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 订单生成 VM（菜单 F.1，权限码 F1；fr_order4 契约）。
/// 左=计划单列表 + 选料检索；右=当前计划单明细；行写路径 I1/I2/U5/D1/I5/U9/U10/U7/U1/D3。
/// 选料建议数：SearchPartsAsync 传 null（不批量算）；选中公式后对选中件用 GetSuggestQtyAsync 在状态栏显示单个建议订数。
/// </summary>
public partial class Order4ViewModel : ObservableObject
{
    public ObservableCollection<PlanHeadRow> Plans { get; } = [];
    public ObservableCollection<PartPickRow> PickRows { get; } = [];
    public ObservableCollection<PlanLineRow> Lines { get; } = [];
    public ObservableCollection<OrderExpRow> Formulas { get; } = [];

    [ObservableProperty] private string _cp = AppSession.Brand;   // 旧 M_WHERE 默认 p.cp=?p_cp（fr_order4 契约）
    [ObservableProperty] private string _currentCno = "";
    [ObservableProperty] private PlanHeadRow? _selectedPlan;
    [ObservableProperty] private PartPickRow? _selectedPick;
    [ObservableProperty] private PlanLineRow? _selectedLine;
    [ObservableProperty] private OrderExpRow? _selectedFormula;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    // 选料查询条
    [ObservableProperty] private string _qNno = "";
    [ObservableProperty] private string _qNa1 = "";
    [ObservableProperty] private string _qTy = "";
    [ObservableProperty] private string _qDc = "";
    [ObservableProperty] private DateTime _qDate1 = DateTime.Today.AddMonths(-1);
    [ObservableProperty] private DateTime _qDate2 = DateTime.Today;
    [ObservableProperty] private decimal _qNum1;
    [ObservableProperty] private decimal _qNum2 = 9999;
    [ObservableProperty] private decimal _qKc1;
    [ObservableProperty] private decimal _qKc2 = 9999;

    // 联动状态栏
    [ObservableProperty] private string _inInfo = "";
    [ObservableProperty] private string _transitInfo = "";
    [ObservableProperty] private string _supplyInfo = "";
    [ObservableProperty] private string _suggestInfo = "";

    partial void OnSelectedLineChanged(PlanLineRow? value) => _ = RefreshLinkAsync();
    partial void OnSelectedPickChanged(PartPickRow? value) => _ = RefreshLinkAsync();
    partial void OnSelectedFormulaChanged(OrderExpRow? value) => _ = RefreshLinkAsync();

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            foreach (var f in await Db.OrderExp.GetFormulasAsync(ct)) Formulas.Add(f);
            await LoadPlansAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
    }

    private async Task LoadPlansAsync(CancellationToken ct)
    {
        var rows = await Db.Order4.GetPlanListAsync(AppSession.DeptNo, Cp.Trim(), ct);
        Plans.Clear();
        foreach (var r in rows) Plans.Add(r);
    }

    [RelayCommand]
    public async Task OpenPlanAsync(CancellationToken ct)
    {
        if (SelectedPlan is null) return;
        CurrentCno = SelectedPlan.Cno;
        await LoadLinesAsync(ct);
    }

    private async Task LoadLinesAsync(CancellationToken ct)
    {
        Lines.Clear();
        SelectedLine = null;
        if (string.IsNullOrWhiteSpace(CurrentCno)) return;
        foreach (var r in await Db.Order4.GetPlanLinesAsync(CurrentCno, AppSession.DeptNo, Cp.Trim(), ct)) Lines.Add(r);
    }

    [RelayCommand]
    public async Task NewPlanAsync(CancellationToken ct)
    {
        var d = new FormDialog("新建计划单");
        var tCno = d.AddText("计划单号");
        if (!d.ShowOkCancel()) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.Order4.ValidateNewCnoAsync(tCno.Text.Trim(), ct);
            CurrentCno = tCno.Text.Trim();
            await LoadPlansAsync(ct);
            await LoadLinesAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task EditHeadAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentCno))
        {
            MessageBox.Show("请先打开一张计划单。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var d = new FormDialog("修改单头");
        var tCno = d.AddText("新合同号", CurrentCno);
        var dpDate = d.AddDate("订货日期", SelectedPlan?.Orderdate ?? DateTime.Today);
        var tBzlx = d.AddText("备注类型", SelectedPlan?.Bzlx?.Trim() ?? "");
        var tFkfs = d.AddText("付款方式", SelectedPlan?.Fkfs?.Trim() ?? "");
        var tGy = d.AddText("供应商", SelectedPlan?.Gyname?.Trim() ?? "");
        var tGname = d.AddText("直发客户", SelectedPlan?.Gname?.Trim() ?? "");
        if (!d.ShowOkCancel()) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.Order4.UpdateHeadAsync(CurrentCno, new PlanHeadInput
            {
                NewCno = tCno.Text.Trim(), Orderdate = dpDate.SelectedDate ?? DateTime.Today,
                Bzlx = tBzlx.Text.Trim(), Fkfs = tFkfs.Text.Trim(), Gyname = tGy.Text.Trim(), Gname = tGname.Text.Trim(),
            }, AppSession.DeptNo, Cp.Trim(), ct);
            CurrentCno = tCno.Text.Trim();
            await LoadPlansAsync(ct);
            await LoadLinesAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeletePlanAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentCno))
        {
            MessageBox.Show("请先打开一张计划单。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"确认删除整张计划单 {CurrentCno} 吗？", "订单生成", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.Order4.DeletePlanAsync(CurrentCno, AppSession.DeptNo, Cp.Trim(), ct);
            CurrentCno = "";
            Lines.Clear();
            await LoadPlansAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task MergeTransitAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentCno))
        {
            MessageBox.Show("请先打开一张计划单。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show("并入后进入在途并由订单入库消费，确认并入吗？", "订单生成", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        try
        {
            IsBusy = true;
            Message = "";
            var n = await Db.Order4.MergeToTransitAsync(CurrentCno, AppSession.DeptNo, Cp.Trim(), ct);
            MessageBox.Show($"已并入在途 {n} 行。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Information);
            CurrentCno = "";
            Lines.Clear();
            await LoadPlansAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- 选料 ----------

    [RelayCommand]
    public async Task SearchPartsAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var rows = await Db.Order4.SearchPartsAsync(new PartPickQuery
            {
                Nno = QNno, Na1 = QNa1, Ty = QTy, Dc = QDc,
                Date1 = QDate1, Date2 = QDate2, Num1 = QNum1, Num2 = QNum2, Kc1 = QKc1, Kc2 = QKc2,
            }, AppSession.DeptNo, Cp.Trim(), null, ct);
            PickRows.Clear();
            foreach (var r in rows) PickRows.Add(r);
            if (rows.Count == 0) Message = "未找到符合条件的配件";
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- 明细行操作 ----------

    [RelayCommand]
    public async Task OrderLineAsync(CancellationToken ct)
    {
        var p = SelectedPick;
        if (p is null) return;
        if (string.IsNullOrWhiteSpace(CurrentCno))
        {
            MessageBox.Show("请先打开或新建计划单。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var d = new FormDialog($"订货 - {p.Nno.Trim()} {p.Na1.Trim()}");
        var tOqty = d.AddText("订数", (p.Ydqty > 0 ? p.Ydqty : 1).ToString("0.##"));
        var tIprj = d.AddText("外币价", p.Iprj.ToString("0.##"));
        var tOprc = d.AddText("售价(可空)", "0");
        var tFa = d.AddText("系列", p.Fa.Trim());
        if (!d.ShowOkCancel()) return;
        var input = new OrderLineAddInput
        {
            Cno = CurrentCno, Nno = p.Nno, Na1 = p.Na1, Ty = p.Ty, Unit = p.Unit, Dc = p.Dc,
            Oqty = ParseDec(tOqty.Text), Iprj = ParseDec(tIprj.Text), Fa = tFa.Text.Trim(),
            Oprc = ParseDec(tOprc.Text) is > 0 ? ParseDec(tOprc.Text) : null,
        };
        await AddLineCoreAsync(input, addQty: false, ct);
    }

    private async Task AddLineCoreAsync(OrderLineAddInput input, bool addQty, CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            await Db.Order4.AddLineAsync(input, AppSession.DeptNo, Cp.Trim(), AppSession.Operator, addQty, ct);
            await LoadLinesAsync(ct);
            await LoadPlansAsync(ct);
        }
        catch (AppException ex) when (ex.Code == "LINE_EXISTS" && !addQty)
        {
            if (MessageBox.Show($"{ex.Message}", "订单生成", MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes)
            {
                await AddLineCoreAsync(input, addQty: true, ct);
                return;
            }
            Message = "已取消";
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task EditLineAsync(CancellationToken ct)
    {
        var l = SelectedLine;
        if (l is null) return;
        var d = new FormDialog($"修改行 - {l.Nno.Trim()} {l.Na1.Trim()}");
        var tOqty = d.AddText("数量", l.Oqty.ToString("0.##"));
        var tIprj = d.AddText("外币价", l.Iprj.ToString("0.##"));
        var tOprc = d.AddText("售价", l.Oprc.ToString("0.##"));
        var tFa = d.AddText("系列", l.Fa.Trim());
        if (!d.ShowOkCancel()) return;
        try
        {
            IsBusy = true;
            Message = "";
            var deleted = await Db.Order4.UpdateLineAsync(l.Id, ParseDec(tOqty.Text), ParseDec(tIprj.Text),
                ParseDec(tOprc.Text), tFa.Text.Trim(), AppSession.DeptNo, Cp.Trim(), ct);
            if (deleted) MessageBox.Show("数量为 0，该行已删除。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadLinesAsync(ct);
            await LoadPlansAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteLineAsync(CancellationToken ct)
    {
        if (SelectedLine is null) return;
        if (MessageBox.Show($"确认删除第 {SelectedLine.Item} 项（{SelectedLine.Nno.Trim()}）吗？",
                "订单生成", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Db.Order4.DeleteLineAsync(SelectedLine.Id, AppSession.DeptNo, Cp.Trim(), ct);
            await LoadLinesAsync(ct);
            await LoadPlansAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    public async Task SplitMoveAsync(CancellationToken ct)
    {
        var l = SelectedLine;
        if (l is null) return;
        var d = new FormDialog("改单号/拆数量");
        var tCno = d.AddText("新单号");
        var tQty = d.AddText("新数量", l.Oqty.ToString("0.##"));
        if (!d.ShowOkCancel()) return;
        var newCno = tCno.Text.Trim();
        if (newCno.Length == 0)
        {
            MessageBox.Show("新单号不能为空。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            IsBusy = true;
            Message = "";
            var newQty = ParseDec(tQty.Text);
            if (newQty < l.Oqty)
                await Db.Order4.SplitLineAsync(l.Id, newCno, newQty, AppSession.DeptNo, Cp.Trim(), AppSession.Operator, ct);
            else
                await Db.Order4.MoveLineAsync(l.Id, newCno, AppSession.DeptNo, Cp.Trim(), ct);
            await LoadLinesAsync(ct);
            await LoadPlansAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ChangePriceAsync(CancellationToken ct)
    {
        var nno = SelectedLine?.Nno ?? SelectedPick?.Nno;
        if (string.IsNullOrWhiteSpace(nno))
        {
            MessageBox.Show("请先选中明细行或选料行。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var d = new FormDialog($"售价修改 - {nno.Trim()}");
        var tOprc = d.AddText("售价", (SelectedLine?.Oprc ?? SelectedPick?.Oprc ?? 0).ToString("0.##"));
        var tOprb = d.AddText("批发价", (SelectedPick?.Oprb ?? 0).ToString("0.##"));
        if (!d.ShowOkCancel()) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.Order4.ChangeSalePriceAsync(Cp.Trim(), nno, ParseDec(tOprc.Text), ParseDec(tOprb.Text),
                AppSession.Operator, AppSession.DeptNo, ct);
            MessageBox.Show("售价/批发价已更新。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshLinkAsync();
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- 行联动状态栏 ----------

    private async Task RefreshLinkAsync()
    {
        var nno = SelectedLine?.Nno ?? SelectedPick?.Nno ?? "";
        InInfo = TransitInfo = SupplyInfo = SuggestInfo = "";
        if (string.IsNullOrWhiteSpace(nno)) return;
        try
        {
            var cp = Cp.Trim();
            var ins = await Db.Order4.GetInRecordsAsync(cp, nno);
            var last = ins.FirstOrDefault();
            InInfo = ins.Count == 0
                ? "进仓 0 笔"
                : $"进仓 {ins.Count} 笔，最近 {last!.Indate:yyyy-MM-dd} {last.Gyname.Trim()} 进价 {last.Iprc:N2}";
            var zt = await Db.Order4.GetTransitAsync(cp, nno);
            TransitInfo = zt.Count == 0 ? "无在途" : $"在途 {zt.Sum(r => r.Ztqty):N0} 件 / {zt.Count} 单";
            var sup = await Db.Order4.GetSupplyPricesAsync(cp, nno);
            SupplyInfo = sup.Count == 0
                ? "无供应商价"
                : $"供应商价 {sup.Count} 家，最低 {sup[0].Iprc:N2}（{sup[0].Gyname.Trim()}）";
            if (SelectedFormula is { } f && SelectedPick is not null)
            {
                var q = await Db.Order4.GetSuggestQtyAsync(f.Cexpress, SelectedPick.Nno, AppSession.DeptNo, cp, null, null);
                SuggestInfo = $"公式「{f.Expna.Trim()}」建议订数：{q:N0}";
            }
        }
        catch (AppException ex)
        {
            InInfo = ex.Message;
        }
    }

    private static decimal ParseDec(string s) => decimal.TryParse(s?.Trim(), out var v) ? v : 0;
}
