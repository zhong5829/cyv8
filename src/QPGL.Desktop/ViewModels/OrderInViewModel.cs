using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>订单入库草稿行（tbisorsm 行，可编辑数量/进价/售价/仓位/仓库）。</summary>
public partial class OrderInLineRow : ObservableObject
{
    public int Id { get; init; }
    public string Cp { get; init; } = "";
    public string Cno { get; init; } = "";
    public string Orno { get; init; } = "";
    public int Item { get; init; }
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Fkfs { get; init; } = "";
    public string Gyname { get; init; } = "";
    public string Gname { get; init; } = "";
    public string Box { get; init; } = "";
    public decimal Kcamount { get; init; }
    public decimal Zt { get; init; }

    [ObservableProperty] private decimal _jkamount;
    [ObservableProperty] private decimal _iprj;
    [ObservableProperty] private decimal _iprc;
    [ObservableProperty] private decimal _oprc;
    [ObservableProperty] private decimal _oprb;
    [ObservableProperty] private string _posi = "";
    [ObservableProperty] private string _depotno = "";
}

/// <summary>
/// 订单入库 VM（B2，二期）。直调 IOrderInRepository（fr_isor 契约）：
/// 拉单（tborder 缺口行）→ 整单导入 tbisorsm 草稿 → 行编辑/分货/整批调价/运费分摊 →
/// 确认入库（row220：tbistoed + 直发 tbsada / ISAPPE + tborder.qty 累计）。
/// </summary>
public partial class OrderInViewModel : ObservableObject
{
    public ObservableCollection<OrderInLineRow> Lines { get; } = [];
    public ObservableCollection<string> OpenCnos { get; } = [];

    [ObservableProperty] private string _cp = AppSession.Brand;   // 旧 cp=?p_cp（fr_order4 row31）
    [ObservableProperty] private string _seano = "";
    [ObservableProperty] private string _selectedCno = "";
    [ObservableProperty] private string _box = "";
    [ObservableProperty] private string _pno = "";
    [ObservableProperty] private string _depotno = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _updateProfileIprj;

    public OrderInLineRow? SelectedLine { get; set; }

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            await ReloadCnosAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    private async Task ReloadCnosAsync(CancellationToken ct)
    {
        OpenCnos.Clear();
        foreach (var (cno, _) in await Db.OrderIn.GetOpenCnosAsync(Cp.Trim(), AppSession.DeptNo, ct))
            OpenCnos.Add(cno);
    }

    /// <summary>整单导入（row156）：cno 缺口行 → tbisorsm。</summary>
    [RelayCommand]
    private async Task ImportAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Seano)) { Message = "请输入船单号"; return; }
        if (string.IsNullOrWhiteSpace(SelectedCno)) { Message = "请选择到货单号"; return; }
        Message = "";
        IsBusy = true;
        try
        {
            var (_, fpcw) = await GetSwitchesAsync(ct);
            var (bl1, bl2, bl3, wbtohs) = await Db.OrderIn.GetPriceRatiosAsync(ct);
            var n = await Db.OrderIn.ImportWholeOrderAsync(new ImportOrderInCommand
            {
                Seano = Seano,
                Cno = SelectedCno,
                Box = Box.Trim(),
                Pno = Pno.Trim(),
                Depotno = Depotno.Trim(),
            }, Cp.Trim(), AppSession.DeptNo, AppSession.Operator, GetPDepotno(), fpcw, bl1, bl2, bl3, wbtohs, ct);
            Message = $"已导入 {n} 行到船单 {Seano}";
            await LoadDraftAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "导入失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>加载草稿（row3）。</summary>
    [RelayCommand]
    public async Task LoadDraftAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Seano)) { Message = "请输入船单号"; return; }
        Message = "";
        try
        {
            var lines = await Db.OrderIn.GetDraftLinesAsync(Seano.Trim(), Cp.Trim(), AppSession.DeptNo, GetPDepotno(), ct);
            Lines.Clear();
            foreach (var l in lines)
                Lines.Add(new OrderInLineRow
                {
                    Id = l.Id, Cp = l.Cp, Cno = l.Cno, Orno = l.Orno, Item = l.Item, Nno = l.Nno,
                    Na1 = l.Na1, Ty = l.Ty, Fkfs = l.Fkfs, Gyname = l.Gyname, Gname = l.Gname, Box = l.Box,
                    Kcamount = l.Kcamount, Zt = l.Zt, Jkamount = l.Jkamount, Iprj = l.Iprj, Iprc = l.Iprc,
                    Oprc = l.Oprc, Oprb = l.Oprb, Posi = l.Posi, Depotno = l.Depotno,
                });
            Message = $"船单 {Seano}：{Lines.Count} 行";
        }
        catch (Exception ex)
        {
            Message = "加载失败：" + ex.Message;
        }
    }

    /// <summary>保存行修改（row211 EMODE）。</summary>
    [RelayCommand]
    private async Task SaveLineAsync(CancellationToken ct)
    {
        if (SelectedLine is null) { Message = "请先选中一行"; return; }
        Message = "";
        try
        {
            await Db.OrderIn.UpdateLineAsync(new UpdateOrderInLineCommand
            {
                Id = SelectedLine.Id, Nno = SelectedLine.Nno, Jkamount = SelectedLine.Jkamount,
                Iprj = SelectedLine.Iprj, Fa = "", Iprc = SelectedLine.Iprc, Oprc = SelectedLine.Oprc,
                Oprb = SelectedLine.Oprb, Posi = SelectedLine.Posi?.Trim() ?? "",
                Depotno = SelectedLine.Depotno?.Trim() ?? "", Suttle = 0,
            }, AppSession.DeptNo, ct);
            Message = "行已保存";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "保存失败：" + ex.Message;
        }
    }

    /// <summary>删行（row72）。</summary>
    [RelayCommand]
    private async Task DeleteLineAsync(CancellationToken ct)
    {
        if (SelectedLine is null) { Message = "请先选中一行"; return; }
        Message = "";
        try
        {
            await Db.OrderIn.DeleteLineAsync(SelectedLine.Id, AppSession.DeptNo, ct);
            await LoadDraftAsync(ct);
        }
        catch (Exception ex) { Message = "删除失败：" + ex.Message; }
    }

    /// <summary>弃单（row186）。</summary>
    [RelayCommand]
    private async Task DiscardAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Seano)) { Message = "请输入船单号"; return; }
        if (MessageBox.Show($"要将整张船单 {Seano} 删除吗?", "警告", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Message = "";
        try
        {
            var n = await Db.OrderIn.DiscardAsync(Seano.Trim(), Cp.Trim(), AppSession.DeptNo, ct);
            Message = $"已删除船单 {Seano}（{n} 行）";
            Lines.Clear();
        }
        catch (Exception ex) { Message = "删除失败：" + ex.Message; }
    }

    /// <summary>分货（row103）：原船单保留，拆分行写入 seano+"_1"。</summary>
    [RelayCommand]
    private async Task DistributeByDateAsync(CancellationToken ct) => await DistributeAsync(OrderInDistributeMode.ByOrderDate, ct);

    [RelayCommand]
    private async Task DistributeByRatioAsync(CancellationToken ct) => await DistributeAsync(OrderInDistributeMode.ByDemandRatio, ct);

    private async Task DistributeAsync(OrderInDistributeMode mode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Seano)) { Message = "请输入船单号"; return; }
        Message = "";
        IsBusy = true;
        try
        {
            var newSeano = await Db.OrderIn.DistributeAsync(Seano.Trim(), mode, Cp.Trim(), AppSession.DeptNo, AppSession.Operator, ct);
            Message = $"分货完成，生成新船单：{newSeano}";
            Seano = newSeano ?? Seano;
            await LoadDraftAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "分货失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>整批调价（row112）：按系数调整选中列（iprc/oprc/oprb/iprj）。</summary>
    [RelayCommand]
    private async Task BatchAdjustAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Seano)) { Message = "请输入船单号"; return; }
        if (BatchField is not ("iprc" or "oprc" or "oprb" or "iprj")) { Message = "请选择调价列"; return; }
        Message = "";
        try
        {
            var n = await Db.OrderIn.BatchAdjustAsync(new BatchAdjustOrderInCommand
            {
                Seano = Seano.Trim(),
                Ufield = BatchField,
                SourceColumn = BatchField,
                Factor = BatchFactor,
                RoundMode = 1,
                IprcCount = 2,
            }, Cp.Trim(), AppSession.DeptNo, ct);
            Message = $"已按系数 {BatchFactor} 调整 {n} 行 {BatchField}";
            await LoadDraftAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "调整失败：" + ex.Message;
        }
    }

    [ObservableProperty] private string _batchField = "iprc";
    [ObservableProperty] private decimal _batchFactor = 1m;
    public string[] BatchFields { get; } = ["iprc", "oprc", "oprb", "iprj"];

    /// <summary>运费分摊（row306）。</summary>
    [RelayCommand]
    private async Task AllocateFreightAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Seano)) { Message = "请输入船单号"; return; }
        if (FreightAmount <= 0) { Message = "请输入运费"; return; }
        Message = "";
        try
        {
            var n = await Db.OrderIn.AllocateFreightAsync(Seano.Trim(), FreightAmount, Cp.Trim(), AppSession.DeptNo, ct);
            Message = n > 0 ? $"运费 {FreightAmount} 已按进价金额比例分摊到 {n} 行" : "进价金额合计为 0，无法分摊";
            await LoadDraftAsync(ct);
        }
        catch (Exception ex) { Message = "分摊失败：" + ex.Message; }
    }

    [ObservableProperty] private decimal _freightAmount;

    /// <summary>确认入库（row220）：tbistoed + 直发/ISAPPE + tborder.qty 累计。</summary>
    [RelayCommand]
    private async Task ConfirmAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Seano)) { Message = "请输入船单号"; return; }
        if (MessageBox.Show($"确认将船单 {Seano} 更新库存吗？\n（有客户名称的行将直接生成销售单，不进本公司库存）",
                "询问", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Message = "";
        IsBusy = true;
        try
        {
            var (_, fpcw) = await GetSwitchesAsync(ct);
            var r = await Db.OrderIn.ConfirmAsync(new ConfirmOrderInCommand
            {
                Seano = Seano.Trim(),
                Mode = 3,
                Pno = Pno.Trim(),
                UpdateProfileIprj = UpdateProfileIprj,
            }, Cp.Trim(), AppSession.DeptNo, AppSession.Operator, GetPDepotno(), fpcw, ct);
            Message = $"库存更新完毕：入库 {r.StockInLines} 行，直发 {r.DirectSaleLines} 行"
                      + (r.Papers.Count > 0 ? $"，销售单 {string.Join("、", r.Papers)}" : "");
            Lines.Clear();
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "确认失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private static string GetPDepotno() => "Z";   // tbsysset「代订货库号标识」缺省 Z（会话读取待遗留项）

    private static async Task<(bool Posigl, string Fpcw)> GetSwitchesAsync(CancellationToken ct)
        => await Db.OrderIn.GetSwitchesAsync(ct);
}
