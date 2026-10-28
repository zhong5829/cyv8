using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.ViewModels;

// =====================================================================================
// 账务第八批 VM（I.4 五账 / I.6 应收应付帐 / I.7 出纳帐查询）
// 契约：开发文档/契约摘录/账务第八批契约.md
// =====================================================================================

/// <summary>五账查询（总账/明细账/数量金额账/核算项目账/多栏账）。</summary>
public partial class FinLedgerViewModel : ObservableObject
{
    public ObservableCollection<FinAlegerRow> AlegerRows { get; } = [];
    public ObservableCollection<FinAledetRow> AledetRows { get; } = [];
    public ObservableCollection<FinAmsumRow> AmsumRows { get; } = [];
    public ObservableCollection<FinItsubRow> ItsubRows { get; } = [];
    public ObservableCollection<FinColumnLedgerRow> ColumnRows { get; } = [];
    public ObservableCollection<FinColumnAccount> ColumnAccounts { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _bPeriod = DateTime.Today.ToString("yyyy") + "01";
    [ObservableProperty] private string _ePeriod = DateTime.Today.ToString("yyyyMM");
    [ObservableProperty] private string _bSub = "";
    [ObservableProperty] private string _eSub = "";
    [ObservableProperty] private string _scode = "";
    [ObservableProperty] private bool _includeUnposted;
    [ObservableProperty] private bool _onlyDetail = true;
    [ObservableProperty] private string _itemTy = "C";
    [ObservableProperty] private string _itemNa = "";
    [ObservableProperty] private FinColumnAccount? _selectedColumnAccount;

    /// <summary>总账（finaleger，级次过滤：末级序号按当前级次位宽累计）。</summary>
    [RelayCommand]
    private async Task SearchAlegerAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.FinLedger.GetAlegerAsync(BPeriod, EPeriod, BSub, ESub, 3, ct);
            AlegerRows.Clear();
            foreach (var r in rows) AlegerRows.Add(r);
            Message = $"总账共 {rows.Count} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>明细账（科目前缀 + 期初 + 滚动余额 + 对方科目）。</summary>
    [RelayCommand]
    private async Task SearchAledetAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var code = string.IsNullOrWhiteSpace(Scode) ? "101" : Scode.Trim();
            var rows = await Db.FinLedger.GetAledetAsync(BPeriod, EPeriod, code, IncludeUnposted, ct: ct);
            AledetRows.Clear();
            foreach (var r in rows) AledetRows.Add(r);
            Message = $"明细账({code})共 {rows.Count} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>数量金额账（finamsum）。</summary>
    [RelayCommand]
    private async Task SearchAmsumAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.FinLedger.GetAmsumAsync(BPeriod, EPeriod, BSub, ESub, OnlyDetail, ct);
            AmsumRows.Clear();
            foreach (var r in rows) AmsumRows.Add(r);
            Message = $"数量金额账共 {rows.Count} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>核算项目账（finitsub：C 客户/D 部门/I 项目/E 员工）。</summary>
    [RelayCommand]
    private async Task SearchItsubAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.FinLedger.GetItsubAsync(ItemTy, ItemNa, BPeriod, EPeriod, BSub, ESub, ct);
            ItsubRows.Clear();
            foreach (var r in rows) ItsubRows.Add(r);
            Message = $"核算项目账共 {rows.Count} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>多栏账（fincolumn 定义 + finvouch 动态栏聚合）。</summary>
    [RelayCommand]
    private async Task SearchColumnAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var accounts = await Db.FinLedger.GetColumnAccountsAsync(ct);
            ColumnAccounts.Clear();
            foreach (var a in accounts) ColumnAccounts.Add(a);
            if (SelectedColumnAccount is not { } acc)
            {
                Message = accounts.Count == 0 ? "未定义多栏账（fincolumn）" : $"已载入 {accounts.Count} 个多栏账，选择后查询";
                return;
            }
            var rows = await Db.FinLedger.GetColumnLedgerAsync(acc.CCode, BPeriod, EPeriod, IncludeUnposted, ct);
            ColumnRows.Clear();
            foreach (var r in rows) ColumnRows.Add(r);
            Message = $"多栏账[{acc.CName}]共 {rows.Count} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>应收应付帐（I.6，fr_finysyf：应收/应付/余额三汇总 + 明细）。</summary>
public partial class FinYsyfLedgerViewModel : ObservableObject
{
    public ObservableCollection<FinYsyfHzRow> YsHz { get; } = [];
    public ObservableCollection<FinYsyfHzRow> YfHz { get; } = [];
    public ObservableCollection<FinYsyfBalanceRow> YsyfHz { get; } = [];
    public ObservableCollection<FinYsyfDetRow> Ys { get; } = [];
    public ObservableCollection<FinYsyfDetRow> Yf { get; } = [];
    public ObservableCollection<FinYsyfDetRow> Ysyf { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private DateTime _sDate = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime _eDate = DateTime.Today;
    [ObservableProperty] private int _isDz;    // 0 全部 / 1 已对帐 / 2 未对帐

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        if (SDate > EDate) { Message = "起始日期不能大于结束日期!"; return; }
        try
        {
            IsBusy = true;
            var r = await Db.FinLedger.GetYsyfAsync(SDate, EDate, IsDz, null, AppSession.DeptNo, ct);
            YsHz.Clear(); foreach (var x in r.YsHz) YsHz.Add(x);
            YfHz.Clear(); foreach (var x in r.YfHz) YfHz.Add(x);
            YsyfHz.Clear(); foreach (var x in r.YsyfHz) YsyfHz.Add(x);
            Ys.Clear(); foreach (var x in r.Ys) Ys.Add(x);
            Yf.Clear(); foreach (var x in r.Yf) Yf.Add(x);
            Ysyf.Clear(); foreach (var x in r.Ysyf) Ysyf.Add(x);
            Message = r.Uperiod + $"（应收 {r.YsHz.Count} / 应付 {r.YfHz.Count} / 余额 {r.YsyfHz.Count}）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>出纳帐（I.7，fr_finrnp：帐目汇总 + 滚动明细 + 手工行）。</summary>
public partial class FinRnpViewModel : ObservableObject
{
    public ObservableCollection<FinRnpRecoutRow> Recout { get; } = [];
    public ObservableCollection<FinRnpVouchRow> Vouch { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private DateTime _sDate = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime _eDate = DateTime.Today;
    [ObservableProperty] private bool _includeUnposted;
    [ObservableProperty] private FinRnpVouchRow? _selectedRow;

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var r = await Db.FinLedger.GetRnpAsync(SDate, EDate, IncludeUnposted, AppSession.DeptNo, ct);
            Recout.Clear(); foreach (var x in r.Recout) Recout.Add(x);
            Vouch.Clear(); foreach (var x in r.Vouch) Vouch.Add(x);
            Message = r.Uperiod + $"（帐目 {r.Recout.Count} / 明细 {r.Vouch.Count}）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>手工行（fr_finrnpaddmx：vouchid 必填/借贷互斥唯一 → finreclist + finrecout 重算）。</summary>
    [RelayCommand]
    private async Task AddManualAsync(CancellationToken ct = default)
    {
        var subcode = Prompt.Show("科目代码（须为现金银行帐目 finrecout 已定义科目）", "出纳手工行");
        if (string.IsNullOrWhiteSpace(subcode)) return;
        var vouchidStr = Prompt.Show("凭证编码（数字，finreclist 内唯一）", "出纳手工行");
        if (!int.TryParse(vouchidStr, out var vouchid)) return;
        var debitStr = Prompt.Show("借方金额（与贷方二选一，直接回车跳过）", "出纳手工行") ?? "";
        var creditStr = Prompt.Show("贷方金额", "出纳手工行") ?? "";
        if (!decimal.TryParse(debitStr, out var debit)) debit = 0;
        if (!decimal.TryParse(creditStr, out var credit)) credit = 0;
        try
        {
            IsBusy = true;
            Message = await Db.FinLedger.SaveFinreclistAsync(new FinReclistCommand
            {
                Subcode = subcode.Trim(), Vouchid = vouchid, Date = EDate,
                Note = "手工行", Debit = debit, Credit = credit,
            }, isAdd: true, AppSession.DeptNo, ct);
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}
