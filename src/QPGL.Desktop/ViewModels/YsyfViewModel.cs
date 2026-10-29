using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 应收应付 VM（G.4，fr_ysyf 契约：契约摘录/出纳管理G组契约.md §2）。
/// 页1 客户汇总（o__.row170 聚合），页2 选中客户明细（LJ 顺序累计）。
/// 写路径：客户增改删、明细增改删（收款/付款类型与已收款拦截）、多条删除（锚点前归并汇总行）。
/// 业务明细跳转（YWMX，__.row4 L90-131）：维修→fr_ywmx（维修模块排除无对应）；销售/调拨→出库明细 SRO（需 E2）、
/// 入库→入库明细 BBI（需 E1，按 seano+isord）、收/付款→fr_skmx 明细窗。
/// </summary>
public partial class YsyfViewModel : ObservableObject
{
    public ObservableCollection<YsyfSummaryRow> Summaries { get; } = [];
    public ObservableCollection<YsyfDetailRow> Details { get; } = [];
    public ObservableCollection<string> Options { get; } = ["全部", "大于零", "小于零", "等于零"];

    /// <summary>明细显示筛选（2026-10-29 需求）：结账状态或业务类型，切换后立即刷新明细。</summary>
    public ObservableCollection<string> DetailFilters { get; } = ["所有", "已结账", "未结账", "销售", "入库", "收款", "付款"];

    [ObservableProperty] private YsyfSummaryRow? _selectedSummary;
    [ObservableProperty] private YsyfDetailRow? _selectedDetail;
    [ObservableProperty] private string _gnameFilter = "";
    [ObservableProperty] private string _option = "全部";
    [ObservableProperty] private string _detailFilter = "所有";
    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private string _fkfs = "";
    [ObservableProperty] private decimal _totalYs;
    [ObservableProperty] private decimal _totalYf;
    [ObservableProperty] private decimal _totalLj;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>客户名称/拼音码联想防抖（300ms，同 SaleControl cmm_class FRDM 机制）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _custDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };

    public ObservableCollection<Partner> CustomerSuggestions { get; } = [];

    public YsyfViewModel()
    {
        _custDebounce.Tick += async (_, _) =>
        {
            _custDebounce.Stop();
            await SearchCustomerSuggestionsAsync(System.Threading.CancellationToken.None);
        };
    }

    /// <summary>输入变化时调用：重置客户联想防抖计时（View 的 TextChanged 转发）。</summary>
    public void DebounceCustomerSearch()
    {
        _custDebounce.Stop();
        _custDebounce.Start();
    }

    /// <summary>
    /// 客户联想搜索（旧 cmm_class row326 FRDM 下拉机制 WPF 化）：
    /// tbgu 按 gname/gno/code（拼音码）/code1（数字码）/tel/mobile 前缀匹配，取全量匹配（PageSize=200）。
    /// </summary>
    public async Task SearchCustomerSuggestionsAsync(CancellationToken ct = default)
    {
        var kw = GnameFilter?.Trim();
        if (string.IsNullOrEmpty(kw))
        {
            CustomerSuggestions.Clear();
            return;
        }
        try
        {
            var request = new PagedRequest { Page = 1, PageSize = 200, Sort = "partName", Order = "asc", Keyword = kw };
            var result = await Db.Partners.SearchAsync(request, ct);
            CustomerSuggestions.Clear();
            foreach (var p in result.Items) CustomerSuggestions.Add(p);
        }
        catch { /* 联想失败不阻断输入 */ }
    }

    /// <summary>选中联想项：回填客户名称并执行查询（等价旧 cmm_class 选中带出）。</summary>
    public async Task ApplyCustomerAsync(Partner p)
    {
        GnameFilter = p.PartName.Trim();
        CustomerSuggestions.Clear();
        Message = "";
        await SearchAsync(System.Threading.CancellationToken.None);
    }

    partial void OnSelectedSummaryChanged(YsyfSummaryRow? value)
    {
        if (value is not null) LoadDetailsCommand.Execute(null);
    }

    /// <summary>2026-10-29 需求：查询后切换「选项」→ 下方明细立即按选项刷新（不再作为查询前置条件）。</summary>
    partial void OnOptionChanged(string value)
    {
        if (SelectedSummary is not null) LoadDetailsCommand.Execute(null);
    }

    /// <summary>2026-10-29 需求：切换「明细显示」筛选（结账状态/业务类型）→ 明细立即刷新。</summary>
    partial void OnDetailFilterChanged(string value)
    {
        if (SelectedSummary is not null) LoadDetailsCommand.Execute(null);
    }

    /// <summary>最近添加/修改的客户名：刷新后自动选中定位（2026-10-29 需求，汇总区仅 2 行高需滚动可见）。</summary>
    public string? NewlyAddedGname { get; private set; }

    /// <summary>页1 查询（o__.row170）。</summary>
    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            Message = "";
            // 2026-10-29 需求：选项不再作为查询前置条件（汇总查全量），选项仅用于查询后切换时过滤下方明细
            var rows = await Db.Ysyf.GetSummariesAsync(GnameFilter, "", DateFrom, DateTo, "", AppSession.DeptNo, ct);
            Summaries.Clear();
            foreach (var r in rows) Summaries.Add(r);
            TotalYs = rows.Sum(r => r.SYs);
            TotalYf = rows.Sum(r => r.SYf);
            TotalLj = rows.Sum(r => r.Lj);
            Message = $"应收 {TotalYs:N2}    应付 {TotalYf:N2}    余额 {TotalYs - TotalYf:N2}";
            // 添加/修改客户后：自动选中新客户（View 端滚动到该行，明细联动加载）
            if (NewlyAddedGname is not null)
            {
                SelectedSummary = Summaries.FirstOrDefault(s => s.Gname == NewlyAddedGname);
                NewlyAddedGname = null;
            }
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>页2 明细（gname 过滤 + 选项过滤 + 明细显示筛选 + LJ 累计列应用层计算）。</summary>
    [RelayCommand]
    public async Task LoadDetailsAsync(CancellationToken ct = default)
    {
        if (SelectedSummary is null) return;
        try
        {
            var rows = await Db.Ysyf.GetDetailsAsync(SelectedSummary.Gname, ct);
            Details.Clear();
            foreach (var r in FilterDetails(FilterDetailsByOption(rows, Option), DetailFilter)) Details.Add(r);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>选项明细过滤（2026-10-29 需求：全部/大于零/小于零/等于零，按该行余额 ys-yf）。</summary>
    private static IReadOnlyList<YsyfDetailRow> FilterDetailsByOption(IReadOnlyList<YsyfDetailRow> rows, string option) =>
        option.Trim() switch
        {
            "大于零" => rows.Where(r => r.Ys - r.Yf > 0).ToList(),
            "小于零" => rows.Where(r => r.Ys - r.Yf < 0).ToList(),
            "等于零" => rows.Where(r => r.Ys - r.Yf == 0).ToList(),
            _ => rows,
        };

    /// <summary>明细显示过滤（2026-10-29 需求）：已结账=mark√；未结账=非√；销售/入库/收款/付款=按类型；所有=不过滤。</summary>
    private static IReadOnlyList<YsyfDetailRow> FilterDetails(IReadOnlyList<YsyfDetailRow> rows, string detailFilter) =>
        detailFilter.Trim() switch
        {
            "已结账" => rows.Where(r => r.Mark == "√").ToList(),
            "未结账" => rows.Where(r => r.Mark != "√").ToList(),
            "销售" or "入库" or "收款" or "付款" => rows.Where(r => r.Type.Trim() == detailFilter.Trim()).ToList(),
            _ => rows,
        };

    /// <summary>结账凭证（JZPZ，fr_ysyf_12y）：前十二月应收应付及余额弹窗（只读）。</summary>
    [RelayCommand]
    private async Task TwelveMonthAsync(CancellationToken ct = default)
    {
        if (SelectedSummary is null) { Message = "请先选择客户"; return; }
        try
        {
            var rows = await Db.Ysyf.GetTwelveMonthAsync(SelectedSummary.Gname, DateTime.Today, ct);
            var win = new Views.YsyfTwelveMonthWindow($"{SelectedSummary.Gname}  前十二月应收应付及余额", rows)
                { Owner = Application.Current.MainWindow };
            win.ShowDialog();
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>打开同步 cb/tick（Load 时执行一次）。</summary>
    [RelayCommand]
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await Db.Ysyf.SyncCbTickAsync(ct);
    }

    /// <summary>
    /// 业务明细跳转（YWMX，__.row4 L90-131）。按明细行 type 分派：维修→无对应（维修模块排除）；
    /// 销售/调拨→SRO 出库明细（权限 E2）；入库→BBI 入库明细（权限 E1，取 tbistoed 首行 indate + isord）；
    /// 收款/付款→fr_skmx 收付款明细窗；其他类型无跳转（旧 DO CASE 无 ELSE）。
    /// </summary>
    [RelayCommand]
    private async Task ShowBizDetailAsync(CancellationToken ct = default)
    {
        if (SelectedDetail is null) { Message = "请先选择明细行"; return; }
        var type = SelectedDetail.Type.Trim();
        var cno = SelectedDetail.Cno.Trim();
        try
        {
            switch (type)
            {
                case "维修":
                    Message = "维修明细随维修模块（按约定排除）"; return;
                case "销售":
                case "调拨":
                    if (!AppSession.Permission.HasMenu("E2")) { Message = "您没有查看出库明细的权限！"; return; }
                    var sro = await Db.FinOp.GetOpDetailAsync("SRO", cno, ct);
                    if (sro.Count == 0) { Message = "没有业务明细！"; return; }
                    new Views.FinOpDetailWindow("SRO", cno, SelectedDetail.Datime, sro)
                    { Owner = Application.Current.MainWindow }.ShowDialog();
                    return;
                case "入库":
                    if (!AppSession.Permission.HasMenu("E1")) { Message = "您没有查看入库明细的权限！"; return; }
                    var bbi = await Db.FinOp.GetOpDetailBySeanoAsync(cno, SelectedDetail.Isord, ct);
                    if (bbi.Count == 0) { Message = "没有业务明细！"; return; }
                    new Views.FinOpDetailWindow("BBI", cno, bbi[0].Date, bbi)
                    { Owner = Application.Current.MainWindow }.ShowDialog();
                    return;
                case "收款":
                case "付款":
                    var mx = await Db.Xlsk.GetSkMxAsync(type, cno, AppSession.DeptNo, ct);
                    if (mx is null) { Message = "没有业务明细！"; return; }
                    new Views.SkmxWindow(type, mx) { Owner = Application.Current.MainWindow }.ShowDialog();
                    return;
            }
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task AddCustomerAsync(CancellationToken ct = default)
    {
        var dlg = new Views.YsyfCustomerWindow("添加客户", null);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        try
        {
            await Db.Ysyf.AddCustomerAsync(dlg.Result, !string.IsNullOrWhiteSpace(dlg.Result.Htno),
                AppSession.Operator, AppSession.DeptNo, ct);
            Message = "客户已添加";
            // 旧 婅_.row140 L12/L31：APPEND BLANK + GATHER——新客户行立即追加到列表并选中（不做全量重查）
            var newRow = new YsyfSummaryRow
            {
                Gname = dlg.Result.Gname.Trim(),
                Cb = dlg.Result.Cb, Tick = dlg.Result.Tick, Yjb = dlg.Result.Yjb,
            };
            Summaries.Add(newRow);
            SelectedSummary = newRow;
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            // 重名（旧 婅_.row140 L8 仅提示）：增强——定位到列表中已存在的该客户（数万行不滚动确认不了）
            if (ex.Code == "GU_EXISTS")
            {
                GnameFilter = "";
                NewlyAddedGname = dlg.Result.Gname;
                await SearchAsync(ct);
            }
        }
    }

    [RelayCommand]
    private async Task EditCustomerAsync(CancellationToken ct = default)
    {
        if (SelectedSummary is null) return;
        var dlg = new Views.YsyfCustomerWindow("修改客户", SelectedSummary);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        try
        {
            await Db.Ysyf.UpdateCustomerAsync(SelectedSummary.Gname, dlg.Result,
                !string.IsNullOrWhiteSpace(dlg.Result.Htno), AppSession.Operator, ct);
            Message = "客户已修改";
            // 同上：改名后新名可能不在原前缀过滤范围内
            GnameFilter = "";
            NewlyAddedGname = dlg.Result.Gname;
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteCustomerAsync(CancellationToken ct = default)
    {
        if (SelectedSummary is null) return;
        if (MessageBox.Show($"确认删除 {SelectedSummary.Gname} 吗？", "应收应付",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Db.Ysyf.DeleteCustomerAsync(SelectedSummary.Gname, AppSession.Operator, ct);
            Message = $"删除:{SelectedSummary.Gname} ysyf";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task AddDetailAsync(CancellationToken ct = default)
    {
        if (SelectedSummary is null) { Message = "请先选择客户"; return; }
        var dlg = new Views.YsyfDetailWindow("添加明细", SelectedSummary.Gname, null);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        try
        {
            await Db.Ysyf.AddDetailAsync(dlg.Result, AppSession.Operator, AppSession.DeptNo, ct);
            Message = "明细已添加";
            await LoadDetailsAsync(ct);
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task EditDetailAsync(CancellationToken ct = default)
    {
        if (SelectedDetail is null || SelectedSummary is null) return;
        var dlg = new Views.YsyfDetailWindow("修改明细", SelectedSummary.Gname, SelectedDetail);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        try
        {
            await Db.Ysyf.UpdateDetailAsync(SelectedDetail.Id, dlg.Result, ct);
            Message = "明细已修改";
            await LoadDetailsAsync(ct);
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteDetailAsync(CancellationToken ct = default)
    {
        if (SelectedDetail is null || SelectedSummary is null) return;
        if (MessageBox.Show($"确认删除单据号[{SelectedDetail.Cno}]的资料吗？", "应收应付",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var delInvoice = false;
        if (!string.IsNullOrWhiteSpace(SelectedDetail.Pno))
            delInvoice = MessageBox.Show("是否删除发票管理中的开票记录？", "应收应付",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        try
        {
            await Db.Ysyf.DeleteDetailAsync(SelectedDetail.Id, SelectedSummary.Gname, delInvoice, AppSession.Operator, ct);
            Message = $"删除单据号[{SelectedDetail.Cno}]";
            await LoadDetailsAsync(ct);
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>多条删除（MDELE）：删除当前行之前的所有明细，当前行归并为汇总行。</summary>
    [RelayCommand]
    private async Task MultiDeleteAsync(CancellationToken ct = default)
    {
        if (SelectedDetail is null || SelectedSummary is null) return;
        if (SelectedDetail.Xm.StartsWith("做过多记录删除", StringComparison.Ordinal))
        {
            Message = "您已经做过多记录删除!";
            return;
        }
        if (MessageBox.Show("确认删除当前记录之前的所有应收应付明细吗？", "应收应付",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Db.Ysyf.MultiDeleteBeforeAsync(SelectedDetail.Id, SelectedSummary.Gname, DateTime.Today,
                AppSession.Operator, ct);
            Message = "已删除并归并汇总行";
            await LoadDetailsAsync(ct);
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }
}
