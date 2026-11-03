using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>订单下拉项（有缺口未入完订单：单号 + 供应商）。</summary>
public sealed class OrderOption
{
    public required string Cno { get; init; }
    public required string Gyname { get; init; }
    public string Label => Gyname.Length > 0 ? $"{Cno}  {Gyname}" : Cno;
}

/// <summary>订单到货行（row166 拉单列集 + 本次到货数/价格/仓位可编辑）。</summary>
public partial class OrderInArrivalRow : ObservableObject
{
    public string Cp { get; init; } = "";
    public string Cno { get; init; } = "";
    public string Orno { get; init; } = "";
    public int Item { get; init; }
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Dc { get; init; } = "";
    public string Fkfs { get; init; } = "";
    public string Gyname { get; init; } = "";
    public string Gname { get; init; } = "";
    /// <summary>订数。</summary>
    public decimal Oqty { get; init; }
    /// <summary>已入累计。</summary>
    public decimal Qty { get; init; }
    /// <summary>缺口 = 订数 - 已入。</summary>
    public decimal Zt { get; init; }

    [ObservableProperty] private bool _isPick = true;   // 勾选行
    [ObservableProperty] private decimal _arriveQty;    // 本次实际到货数（默认=缺口）
    [ObservableProperty] private decimal _iprj;
    [ObservableProperty] private decimal _iprc;
    [ObservableProperty] private decimal _oprc;
    [ObservableProperty] private decimal _oprb;
    [ObservableProperty] private string _posi = "";
}

/// <summary>
/// 订单入库 VM（B2，2026-11-03 改版）：选订单 → 自动加载明细（订数/已入/缺口，价率换算价格）→
/// 修改实际到货数 → 「更新库存」一步确认（无 tbisorsm 草稿；写路径 ConfirmByOrderAsync = row220 语义）。
/// </summary>
public partial class OrderInViewModel : ObservableObject
{
    public ObservableCollection<OrderOption> OpenOrders { get; } = [];
    public ObservableCollection<OrderInArrivalRow> Lines { get; } = [];

    [ObservableProperty] private string _cp = AppSession.Brand;
    [ObservableProperty] private OrderOption? _selectedOrder;
    [ObservableProperty] private bool _updateProfileIprj;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            await LoadOrdersAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    /// <summary>订单下拉（有缺口未入完订单，即订单生成生成的订单）。</summary>
    [RelayCommand]
    public async Task LoadOrdersAsync(CancellationToken ct)
    {
        var orders = await Db.OrderIn.GetOpenCnosAsync(Cp.Trim(), AppSession.DeptNo, ct);
        OpenOrders.Clear();
        foreach (var (cno, gy) in orders)
            OpenOrders.Add(new OrderOption { Cno = cno, Gyname = gy });
        Message = $"共 {OpenOrders.Count} 张未入完订单";
    }

    partial void OnSelectedOrderChanged(OrderOption? value) => _ = LoadLinesAsync(System.Threading.CancellationToken.None);

    /// <summary>选中订单 → 加载明细（row166 列集 + 价率换算），本次到货数默认=缺口，仓位默认=现有库存批次。</summary>
    [RelayCommand]
    public async Task LoadLinesAsync(CancellationToken ct)
    {
        if (SelectedOrder is null) { Message = "请先选择订单"; return; }
        Message = "";
        IsBusy = true;
        try
        {
            var (bl1, bl2, bl3, wbtohs) = await Db.OrderIn.GetPriceRatiosAsync(ct);
            var (_, _, fpcw) = await Db.OrderIn.GetSwitchesAsync(ct);
            var lines = await Db.OrderIn.GetOrderLinesAsync(SelectedOrder.Cno, Cp.Trim(), AppSession.DeptNo, bl1, bl2, bl3, wbtohs, ct);
            // 仓库传空：历史库存批次 depotno 全为空，ISAPPE 批次匹配含 depotno——默认 Z 会导致每次入库新建批次
            var posis = await Db.OrderIn.GetDefaultPosisAsync(Cp.Trim(), AppSession.DeptNo, "", fpcw,
                lines.Select(l => l.Nno), ct);
            Lines.Clear();
            foreach (var l in lines)
                Lines.Add(new OrderInArrivalRow
                {
                    Cp = l.Cp, Cno = l.Cno, Orno = l.Orno, Item = l.Item, Nno = l.Nno, Na1 = l.Na1,
                    Ty = l.Ty, Unit = l.Unit, Dc = l.Dc, Fkfs = l.Fkfs, Gyname = l.Gyname, Gname = l.Gname,
                    Oqty = l.Oqty, Qty = l.Qty, Zt = l.Zt,
                    ArriveQty = l.Zt, Iprj = l.Iprj, Iprc = l.Iprc, Oprc = l.Oprc, Oprb = l.Oprb,
                    Posi = posis.GetValueOrDefault(l.Nno, ""),
                });
            Message = $"订单 {SelectedOrder.Cno}：{Lines.Count} 行未入完";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "加载失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>更新库存（确认入库，row220 写路径语义）。</summary>
    [RelayCommand]
    private async Task ConfirmAsync(CancellationToken ct)
    {
        if (SelectedOrder is null) { Message = "请先选择订单"; return; }
        var lines = Lines
            .Where(l => l.IsPick && l.ArriveQty != 0)
            .Select(l => new OrderArrivalLineInput
            {
                Cp = l.Cp, Nno = l.Nno, Na1 = l.Na1, Ty = l.Ty, Fa = "", Unit = l.Unit, Dc = l.Dc,
                Orno = l.Orno, Item = l.Item, ArriveQty = l.ArriveQty,
                Iprj = l.Iprj, Iprc = l.Iprc, Oprc = l.Oprc, Oprb = l.Oprb,
                Posi = l.Posi?.Trim() ?? "", Fkfs = l.Fkfs, Gyname = l.Gyname,
            })
            .ToList();
        if (lines.Count == 0)
        {
            MessageBox.Show("现在入库选项没有入库数据可以更新库存！", "提示");
            return;
        }
        if (MessageBox.Show($"确认将订单 {SelectedOrder.Cno} 更新库存吗？\n（有客户名称的行将直接生成销售单，不进本公司库存）",
                "询问", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Message = "";
        IsBusy = true;
        try
        {
            var r = await Db.OrderIn.ConfirmByOrderAsync(new ConfirmByOrderCommand
            {
                Cno = SelectedOrder.Cno,
                Depotno = "",   // 仓库/提货单号/箱号输入框已移除（2026-11-03），按原默认空值写入
                Pno = "",
                Box = "",
                UpdateProfileIprj = UpdateProfileIprj,
                Lines = lines,
            }, AppSession.DeptNo, AppSession.Operator, GetPDepotno(), ct);
            var okMsg = $"库存更新完毕：入库 {r.StockInLines} 行，直发 {r.DirectSaleLines} 行"
                        + (r.Papers.Count > 0 ? $"，销售单 {string.Join("、", r.Papers)}" : "");
            await LoadOrdersAsync(ct);
            // 确认后订单即从待入库列表消失：显式复位选择并清空明细表格。
            // 注意 LoadOrdersAsync 清空下拉集合时 WPF 会同步把 SelectedOrder 复位为 null，
            // 导致随后的 LoadLinesAsync 因空选择提前返回、表格残留旧行——故此处直接清空。
            SelectedOrder = null;
            Lines.Clear();
            Message = okMsg;
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "确认失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private static string GetPDepotno() => "Z";   // tbsysset「代订货库号标识」缺省 Z（会话读取待遗留项）
}
