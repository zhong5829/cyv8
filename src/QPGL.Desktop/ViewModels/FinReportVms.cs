using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.ViewModels;

// =====================================================================================
// 账务第九批 VM（I.5 报表+现金流 / I.8 辅助工具 / N 组固定资产）
// 契约：开发文档/契约摘录/账务第九批契约.md
// =====================================================================================

/// <summary>报表中心（试算平衡/凭证汇总/损益/资产负债/现金流五页签）。</summary>
public partial class FinReportViewModel : ObservableObject
{
    public ObservableCollection<FinTrialRow> TrialRows { get; } = [];
    public ObservableCollection<FinVouchSummaryRow> SummaryRows { get; } = [];
    public ObservableCollection<FinProlRow> ProlRows { get; } = [];
    public ObservableCollection<FinBsRow> BsRows { get; } = [];
    public ObservableCollection<FinCashRow> CashRows { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _period = DateTime.Today.ToString("yyyyMM");
    [ObservableProperty] private bool _useHisbal;      // fr_finreportf：询问"当前期间"=否 → finhisbal
    [ObservableProperty] private bool _exactLevel;     // finbalanf CHECK1（仅该级次）
    [ObservableProperty] private string _sub0 = "";
    [ObservableProperty] private bool _onlyPosted = true;   // 凭证汇总 post 三态（1=全部）
    [ObservableProperty] private bool _includeUnposted;     // post=0
    [ObservableProperty] private DateTime _bDate = new(DateTime.Today.Year, 1, 1);
    [ObservableProperty] private DateTime _eDate = DateTime.Today;
    [ObservableProperty] private string _vouword = "(全部)";

    /// <summary>试算平衡（I51：finsub 当期 / finhisbal 历史期）。</summary>
    [RelayCommand]
    private async Task SearchTrialAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.FinReport.GetTrialBalanceAsync(Period, Sub0, 3, ExactLevel, AppSession.DeptNo, ct);
            TrialRows.Clear();
            foreach (var r in rows) TrialRows.Add(r);
            Message = $"试算平衡（{Period}{(UseHisbal ? " 历史期" : "")}）共 {rows.Count} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>凭证汇总表（I52：级次截断合并 + 凭证数/附单据数）。</summary>
    [RelayCommand]
    private async Task SearchSummaryAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var postMode = IncludeUnposted ? 3 : OnlyPosted ? 2 : 1;
            var (rows, vouchCount, abill) = await Db.FinReport.GetVouchSummaryAsync(BDate, EDate, Vouword, postMode, 3, AppSession.DeptNo, ct);
            SummaryRows.Clear();
            foreach (var r in rows) SummaryRows.Add(r);
            Message = $"凭证汇总：{BDate:yyyy-MM-dd}至{EDate:yyyy-MM-dd}（{Vouword}）共 {vouchCount} 张凭证 附单据 {abill:0} 张，{rows.Count} 科目";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>损益表求值（I53 Sum：cexpress/nexpress 表达式引擎）。</summary>
    [RelayCommand]
    private async Task SumProlAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinReport.SumProlAsync(Period, UseHisbal, AppSession.DeptNo, ct: ct);
            var rows = await Db.FinReport.GetProlAsync(AppSession.DeptNo, ct);
            ProlRows.Clear();
            foreach (var r in rows) ProlRows.Add(r);
            Message = $"损益表已求值（{Period}）——修改后需保存落库";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>损益表保存（finprol 全量重写）。</summary>
    [RelayCommand]
    private async Task SaveProlAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinReport.SaveProlAsync(ProlRows.ToList(), AppSession.DeptNo, ct);
            Message = "损益表已保存（finprol 全量重写）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>资产负债表求值（I54 Sum：四列独立求值）。</summary>
    [RelayCommand]
    private async Task SumBsAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinReport.SumBsAsync(Period, UseHisbal, AppSession.DeptNo, ct: ct);
            var rows = await Db.FinReport.GetBsAsync(AppSession.DeptNo, ct);
            BsRows.Clear();
            foreach (var r in rows) BsRows.Add(r);
            Message = $"资产负债表已求值（{Period}）——修改后需保存落库";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>资产负债表保存（finbs 全量重写）。</summary>
    [RelayCommand]
    private async Task SaveBsAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinReport.SaveBsAsync(BsRows.ToList(), AppSession.DeptNo, ct);
            Message = "资产负债表已保存（finbs 全量重写）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>现金流量表求值（I57 Sum：fincadistr/fincashba 取数）。</summary>
    [RelayCommand]
    private async Task SumCashAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinReport.SumCashAsync(Period, UseHisbal, AppSession.DeptNo, ct: ct);
            var rows = await Db.FinReport.GetCashAsync(AppSession.DeptNo, ct);
            CashRows.Clear();
            foreach (var r in rows) CashRows.Add(r);
            Message = $"现金流量表已求值（{Period}）——修改后需保存落库";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>现金流量表保存（fincash 全量重写）。</summary>
    [RelayCommand]
    private async Task SaveCashAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinReport.SaveCashAsync(CashRows.ToList(), AppSession.DeptNo, ct);
            Message = "现金流量表已保存（fincash 全量重写）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>载入三模板（页面进入时）。</summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            ProlRows.Clear(); foreach (var r in await Db.FinReport.GetProlAsync(AppSession.DeptNo, ct)) ProlRows.Add(r);
            BsRows.Clear(); foreach (var r in await Db.FinReport.GetBsAsync(AppSession.DeptNo, ct)) BsRows.Add(r);
            CashRows.Clear(); foreach (var r in await Db.FinReport.GetCashAsync(AppSession.DeptNo, ct)) CashRows.Add(r);
            Message = $"模板载入：损益 {ProlRows.Count} / 资产负债 {BsRows.Count} / 现金流 {CashRows.Count}";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>现金流初始/准备（I55/I56）。</summary>
public partial class FinCashPrepViewModel : ObservableObject
{
    public ObservableCollection<FinCashbaRow> CashbaRows { get; } = [];
    public ObservableCollection<FinCashbaRow> DisRows { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _month = DateTime.Today.Month;
    [ObservableProperty] private int _vouchid;
    [ObservableProperty] private decimal _vouchDiff;

    /// <summary>现金流数据初始（I55：载入当月 fincashba）。</summary>
    [RelayCommand]
    private async Task LoadCashbaAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.FinReport.GetCashbaAsync(Month, AppSession.DeptNo, ct);
            CashbaRows.Clear();
            foreach (var r in rows) CashbaRows.Add(r);
            Message = $"现金流初始（{Month} 月）共 {rows.Count} 行——编辑后点保存";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>现金流初始保存（QueryUnload 逐行 UPDATE）。</summary>
    [RelayCommand]
    private async Task SaveCashbaAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinReport.UpdateCashbaAsync(CashbaRows.ToList(), AppSession.DeptNo, ct);
            Message = "现金流初始已保存（fincashba UPDATE）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>凭证现金流分配（I56：vouchid + 各项目流入/流出，差额校验）。</summary>
    [RelayCommand]
    private async Task SaveDisAsync(CancellationToken ct = default)
    {
        if (Vouchid == 0) { Message = "请输入凭证编码!"; return; }
        try
        {
            IsBusy = true;
            // 凭证差额=Σ(fcash=.T. 行 debit-credit)
            var diff = await GetVouchDiffAsync(Vouchid, ct);
            var cmd = new FinCashDisCommand
            {
                Vouchid = Vouchid, Period = DateTime.Today.ToString("yyyyMM"), Date = DateTime.Today,
                Lines = DisRows.Select(r => (r.Cashid, r.Cashna, (int)r.Iodir, r.Desum, r.Cresum)).ToList(),
            };
            var msg = await Db.FinReport.SaveCashDisAsync(cmd, diff, AppSession.DeptNo, ct);
            if (msg.Length > 0)
            {
                // 差额不平 → 询问退出（=6 → 重置）
                if (MessageBox.Show(msg + "\r\n是否重置该凭证分配?", "询问", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    await Db.FinReport.ResetCashDisAsync(Vouchid, AppSession.DeptNo, ct);
                    Message = "该凭证现金流分配已重置（cashdis=0 + fincadistr 删除）";
                }
                else Message = msg;
            }
            else Message = "凭证现金流分配已保存（fincadistr + cashdis=1）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoadDisAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.FinReport.GetCashbaAsync(Month, AppSession.DeptNo, ct);
            DisRows.Clear();
            foreach (var r in rows) DisRows.Add(new FinCashbaRow
            { Period = r.Period, Cashid = r.Cashid, Cashna = r.Cashna, Iodir = r.Iodir });
            VouchDiff = await GetVouchDiffAsync(Vouchid, ct);
            Message = $"分配窗载入（{Month} 月）{DisRows.Count} 行，凭证差额 {VouchDiff:0.##}";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task<decimal> GetVouchDiffAsync(int vouchid, CancellationToken ct)
        => await Db.FinReport.GetVouchDiffAsync(vouchid, ct);
}

/// <summary>辅助工具 + 固定资产（I8/N7/N8）。</summary>
public partial class FinToolViewModel : ObservableObject
{
    public ObservableCollection<PerasstyRow> AsstyRows { get; } = [];
    public ObservableCollection<PerchgtyRow> ChgtyRows { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private PerasstyRow? _selectedAssty;
    [ObservableProperty] private PerchgtyRow? _selectedChgty;

    /// <summary>汇总到父级（fintool row5：MPALSUM 旧 bug 照抄）。</summary>
    [RelayCommand]
    private async Task ToolSumAsync(CancellationToken ct = default)
    {
        if (MessageBox.Show("本功能将会按明细科目发生的金额向父级科目汇总金额,是否执行?", "询问",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            IsBusy = true;
            await Db.FinReport.ToolSumAsync(AppSession.DeptNo, 3, ct);
            Message = "汇总完成（finsub 12 列前缀链聚合，MPALSUM 旧码行为照抄）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>帐簿初始化（fintool row10：八表清空+凭证库清空/重置+finsoursub 恢复）。</summary>
    [RelayCommand]
    private async Task ToolInitAsync(CancellationToken ct = default)
    {
        if (MessageBox.Show("本功能将会初始化帐簿数据且需要独占运行!\r\n请确认要初始化吗？", "提示信息",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var clearVouch = MessageBox.Show("是否清空凭证库?", "询问", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        var restore = MessageBox.Show("是否恢复原始余额库?", "提示", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        try
        {
            IsBusy = true;
            await Db.FinReport.ToolInitAsync(AppSession.DeptNo, clearVouch, restore, ct);
            Message = $"帐簿初始化完成（八表清空，凭证库{(clearVouch ? "已清空+finvid 归零" : "已重置 post=0")}，{(restore ? "尝试恢复 finsoursub" : "余额回写 finsoursub")}）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            AsstyRows.Clear(); foreach (var r in await Db.FinReport.GetPerasstyAsync(ct)) AsstyRows.Add(r);
            ChgtyRows.Clear(); foreach (var r in await Db.FinReport.GetPerchgtyAsync(ct)) ChgtyRows.Add(r);
            Message = $"固定资产：类别 {AsstyRows.Count} / 变动方式 {ChgtyRows.Count}";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>固定资产类别新增（N7 Add）。</summary>
    [RelayCommand]
    private async Task AddAsstyAsync(CancellationToken ct = default)
    {
        var name = Prompt.Show("类别名称", "固定资产类别");
        if (string.IsNullOrWhiteSpace(name)) return;
        var rateStr = Prompt.Show("残值率（数字）", "固定资产类别") ?? "0";
        _ = decimal.TryParse(rateStr, out var rate);
        try
        {
            IsBusy = true;
            await Db.FinReport.AddPerasstyAsync(new PerasstyRow { Perasty = name.Trim(), Deprty = 1, Remrate = rate }, AppSession.DeptNo, ct);
            await LoadAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>固定资产类别修改（N7 Edit）。</summary>
    [RelayCommand]
    private async Task UpdateAsstyAsync(CancellationToken ct = default)
    {
        if (SelectedAssty is not { } r) { Message = "请选择类别行"; return; }
        try
        {
            IsBusy = true;
            await Db.FinReport.UpdatePerasstyAsync(r, AppSession.DeptNo, ct);
            await LoadAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>变动方式新增（N8 Add；Edit 旧码 INSERT+WHERE 恒报错照抄不实现）。</summary>
    [RelayCommand]
    private async Task AddChgtyAsync(CancellationToken ct = default)
    {
        var name = Prompt.Show("变动方式名称", "固定资产变动方式");
        if (string.IsNullOrWhiteSpace(name)) return;
        var tyStr = Prompt.Show("类型（1=增加方式 2=减少方式）", "固定资产变动方式") ?? "1";
        _ = int.TryParse(tyStr, out var ty);
        var subcode = Prompt.Show("对方科目代码", "固定资产变动方式") ?? "";
        try
        {
            IsBusy = true;
            await Db.FinReport.AddPerchgtyAsync(new PerchgtyRow { Chgna = name.Trim(), Chgty = ty, Subcode = subcode.Trim() }, AppSession.DeptNo, ct);
            await LoadAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}
