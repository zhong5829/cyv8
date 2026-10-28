using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.ViewModels;

/// <summary>简单单行输入对话框（替代 VFP INPUTBOX，避免 VB 依赖）。</summary>
public static class Prompt
{
    public static string? Show(string label, string title)
    {
        var box = new System.Windows.Controls.TextBox { Width = 320, Margin = new Thickness(0, 6, 0, 6) };
        var ok = new System.Windows.Controls.Button { Content = "确定", Width = 72, IsDefault = true, Padding = new Thickness(0, 3, 0, 3) };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 72, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0, 3, 0, 3) };
        var dlg = new Window
        {
            Title = title, Width = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = default(string);
        ok.Click += (_, _) => { result = box.Text; dlg.DialogResult = true; };
        dlg.Content = new System.Windows.Controls.StackPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                new System.Windows.Controls.TextBlock { Text = label },
                box,
                new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };
        dlg.ShowDialog();
        return result;
    }

    /// <summary>VM 内静态便捷入口。</summary>
    public static string? Invoke(string label, string title) => Show(label, title);
}

// =====================================================================================
// 账务 I 组 VM（凭证核心闭环；契约：开发文档/契约摘录/账务管理I组凭证闭环契约.md）
// =====================================================================================

/// <summary>会计科目（fr_finsub：树平铺 + 增改删 + 下级同步）。</summary>
public partial class FinSubViewModel : ObservableObject
{
    public ObservableCollection<FinSubRow> Rows { get; } = [];

    [ObservableProperty] private FinSubRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.FinSub.GetSubsAsync(ct);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Message = $"共 {rows.Count} 科目";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddAsync(CancellationToken ct = default)
    {
        var subcode = Prompt.Show("科目代码（逐级拼接，如 101001）", "新增科目");
        if (string.IsNullOrWhiteSpace(subcode)) return;
        var subname = Prompt.Show("科目名称", "新增科目");
        if (string.IsNullOrWhiteSpace(subname)) return;
        var type = Prompt.Show("科目类别（现金类/银行类/应收类/应付类，留空为普通）", "新增科目") ?? "";
        try
        {
            var row = new FinSubRow
            {
                Subcode = subcode.Trim(),
                Subname = subname.Trim(),
                Type = type.Trim() switch
                {
                    "现金类" => "C",
                    "银行类" => "B",
                    "应收类" => "A",
                    "应付类" => "P",
                    _ => "",
                },
                Fdc = "D",
                Detacode = true,
            };
            await Db.FinSub.AddSubAsync(row, new decimal?[10], AppSession.DeptNo, ct);
            // 新增末级科目 → 父科目 detacode=0（finsubadd L127-140；级次位宽 3,3,3,2,2,2）
            var parent = ParentCode(row.Subcode);
            if (parent.Length > 0)
                await Db.FinSub.UpdateSubAsync(new FinSubRow { Subcode = parent, Detacode = false }, AppSession.DeptNo, ct);
            Message = $"科目 {row.Subcode} 已添加";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    private static string ParentCode(string subcode)
    {
        int[] lens = [3, 3, 3, 2, 2, 2];
        var acc = 0;
        foreach (var l in lens)
        {
            if (acc + l == subcode.Length) return acc == 0 ? "" : subcode[..acc];
            if (acc + l > subcode.Length) return "";
            acc += l;
        }
        return "";
    }

    [RelayCommand]
    private async Task DeleteAsync(CancellationToken ct = default)
    {
        if (SelectedRow is null) return;
        if (MessageBox.Show($"是否删除该科目 [{SelectedRow.Subcode}]?", "会计科目",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Db.FinSub.DeleteSubAsync(SelectedRow.Subcode, AppSession.DeptNo, ct);
            Message = $"科目 {SelectedRow.Subcode} 已删除（余额已上移）";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }
}

/// <summary>凭证分录行 VM（Vouchtmp 等价）。</summary>
public partial class FinVouchLineVm : ObservableObject
{
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _subcode = "";
    [ObservableProperty] private string _subname = "";
    [ObservableProperty] private decimal _debit;
    [ObservableProperty] private decimal _credit;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private string _dept = "";
    [ObservableProperty] private string _item = "";
    [ObservableProperty] private string _empl = "";
    [ObservableProperty] private string _client = "";
}

/// <summary>
/// 凭证录入/查询/审核/过帐 VM（fr_finvouch + fr_finquery + fr_finaudit + fr_finpost）。
/// 校验口径：期间≥当前、现/银字必有 C/B 科目、科目空与金额互斥、月结/结转损益拦截、重码、借贷平衡确认。
/// </summary>
public partial class FinVouchViewModel : ObservableObject
{
    public ObservableCollection<FinVouchLineVm> Lines { get; } = [];
    public ObservableCollection<FinVouchRow> QueryRows { get; } = [];

    [ObservableProperty] private int _vouchId;
    [ObservableProperty] private int _vcode;
    [ObservableProperty] private DateTime _docDate = DateTime.Today;
    [ObservableProperty] private string _vouword = "记";
    [ObservableProperty] private decimal _totalDebit;
    [ObservableProperty] private decimal _totalCredit;
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    // 查询/审核/过帐
    [ObservableProperty] private string _queryPeriod = DateTime.Today.ToString("yyyyMM");
    [ObservableProperty] private int? _queryVouchId;
    [ObservableProperty] private string _querySubcode = "";
    [ObservableProperty] private bool _skipCheck;
    [ObservableProperty] private FinVouchRow? _selectedQueryRow;
    [ObservableProperty] private string _auditPeriod = DateTime.Today.ToString("yyyyMM");

    public FinVouchViewModel()
    {
        for (var i = 0; i < 6; i++) Lines.Add(new FinVouchLineVm());
    }

    partial void OnDocDateChanged(DateTime value) => RecalcPeriodGuard();
    private void RecalcPeriodGuard() { }

    private void RecalcTotals()
    {
        TotalDebit = Lines.Sum(l => l.Debit);
        TotalCredit = Lines.Sum(l => l.Credit);
    }

    /// <summary>新增（Add，row2 L23-68）：vouchid=finvid+1 预览、vcode=period max+1、日期越期取月末。</summary>
    [RelayCommand]
    public async Task NewDocAsync(CancellationToken ct = default)
    {
        VouchId = await Db.FinVouch.PeekNextVidAsync(AppSession.DeptNo, ct);
        Vcode = await Db.FinVouch.PeekNextVcodeAsync(DateTime.Today.ToString("yyyyMM"), ct);
        DocDate = DateTime.Today;
        Vouword = "记";
        Lines.Clear();
        for (var i = 0; i < 6; i++) Lines.Add(new FinVouchLineVm());
        RecalcTotals();
        IsEditMode = false;
        Message = $"新凭证 vouchid={VouchId} vcode={Vcode}";
    }

    /// <summary>载入已有凭证（Edit 前提）。</summary>
    [RelayCommand]
    public async Task LoadDocAsync(CancellationToken ct = default)
    {
        if (!QueryVouchId.HasValue) { Message = "请输入凭证号(vouchid)"; return; }
        var rows = await Db.FinVouch.QueryAsync("000000", QueryVouchId, null, ct);
        if (rows.Count == 0) { Message = "凭证不存在"; return; }
        VouchId = rows[0].Vouchid;
        Vcode = rows[0].Vcode;
        DocDate = rows[0].Date;
        Vouword = rows[0].Vouword;
        Lines.Clear();
        foreach (var r in rows)
        {
            Lines.Add(new FinVouchLineVm
            {
                Note = r.Note, Subcode = r.Subcode, Subname = r.Subname, Debit = r.Debit, Credit = r.Credit,
                Dept = "", Item = "", Empl = "", Client = r.Client,
            });
        }
        while (Lines.Count < 2) Lines.Add(new FinVouchLineVm());
        RecalcTotals();
        IsEditMode = true;
        Message = $"已载入凭证 {VouchId}（audit={rows[0].Audit} post={rows[0].Post}）";
    }

    /// <summary>保存（Save，row2 L117-297）。</summary>
    [RelayCommand]
    public async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            Message = "";
            // 校验（契约 §2.2）
            if (DocDate.ToString("yyyyMM").CompareTo(DateTime.Today.ToString("yyyyMM")) < 0)
            {
                Message = "不能录入上一期的凭证!";
                return;
            }
            var valid = Lines.Where(l => !string.IsNullOrWhiteSpace(l.Subcode) && (l.Debit != 0 || l.Credit != 0)).ToList();
            if (valid.Count == 0) { Message = "会计科目不能为空!"; return; }
            var dupLine = Lines.Any(l => !string.IsNullOrWhiteSpace(l.Subcode) && l.Debit == 0 && l.Credit == 0);
            if (dupLine) { Message = "借、贷方金额不能同时为空!"; return; }
            if (await Db.FinVouch.ChePostAsync(DocDate, 1, ct))
            {
                Message = "该月已做期末结帐,不能再录入凭证!";
                return;
            }
            var hasProfit = valid.Any(l => l.Subcode.Trim().StartsWith('5'));
            if (hasProfit && await Db.FinVouch.ChePostAsync(DocDate, 2, ct))
            {
                Message = "该月已经结转损益,不能再录入凭证!";
                return;
            }
            var de = valid.Sum(l => l.Debit);
            var cre = valid.Sum(l => l.Credit);
            if (de != cre &&
                MessageBox.Show("该凭证借贷不平,不修改则凭证作废,修改吗?", "记帐凭证",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.No)
                return;
            if (MessageBox.Show("保存当前凭证吗?", "记帐凭证", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var result = await Db.FinVouch.SaveAsync(new FinVouchSaveCommand(
                VouchId, Vcode, DocDate, Vouword,
                valid.Select(l => new FinVouchLine
                {
                    Note = l.Note, Subcode = l.Subcode.Trim(), Subname = l.Subname, Debit = l.Debit,
                    Credit = l.Credit, Amount = l.Amount, Unit = l.Unit, Dept = l.Dept, Item = l.Item,
                    Empl = l.Empl, Client = l.Client,
                }).ToList(), IsEditMode), AppSession.DeptNo, AppSession.Operator, ct);
            Message = $"凭证已保存 vouchid={result.VouchId} vcode={result.Vcode}（借 {de:N2} / 贷 {cre:N2}）";
            IsEditMode = true;
            RecalcTotals();
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>删除整张凭证（Vdelete，row2 L5-22）。</summary>
    [RelayCommand]
    public async Task DeleteDocAsync(CancellationToken ct = default)
    {
        if (VouchId <= 0) return;
        if (MessageBox.Show("确认要删除本凭证吗?", "记帐凭证", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Db.FinVouch.DeleteVouchAsync(VouchId, AppSession.DeptNo, AppSession.Operator, ct);
            Message = $"凭证 {VouchId} 已删除";
            await NewDocAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>查询（fr_finquery，period>=起始）。</summary>
    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.FinVouch.QueryAsync(QueryPeriod, QueryVouchId,
                string.IsNullOrWhiteSpace(QuerySubcode) ? null : QuerySubcode, ct);
            QueryRows.Clear();
            foreach (var r in rows) QueryRows.Add(r);
            Message = $"共 {rows.Count} 行分录（借 {rows.Sum(r => r.Debit):N2} / 贷 {rows.Sum(r => r.Credit):N2}）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>打印当前凭证（myclick Print → fr_finvprint 六选一版式窗）。</summary>
    [RelayCommand]
    public async Task PrintDocAsync(CancellationToken ct = default)
    {
        if (VouchId <= 0) { Message = "请先载入凭证"; return; }
        try
        {
            var head = await Db.FinOp.ReadVouchAsync(VouchId, ct);
            if (head is null || head.Lines.Count == 0) { Message = "凭证不存在或无分录"; return; }
            var vprn = await Db.FinOp.GetFinVprnAsync(ct);
            new Views.FinVouchPrintWindow(head, head.Lines, vprn).ShowDialog();
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>整期审核（fr_finaudit row3）。</summary>
    [RelayCommand]
    public async Task AuditAsync(CancellationToken ct = default)
    {
        if (MessageBox.Show($"确认审核 [{AuditPeriod}] 期间全部未审核凭证吗?", "凭证审核",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var n = await Db.FinVouch.AuditPeriodAsync(AuditPeriod, AppSession.Operator, ct);
            Message = $"共审核 {n} 行分录（期间 {AuditPeriod}）";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>过帐（fr_finpost post）。</summary>
    [RelayCommand]
    public async Task PostAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var n = await Db.FinVouch.PostAsync(AuditPeriod, AppSession.Operator, AppSession.DeptNo,
                byDate: false, date: null, skipCheck: SkipCheck, ct);
            Message = $"共过帐 {n} 张记帐凭证!";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>反过帐（fr_finpost unpost）。</summary>
    [RelayCommand]
    public async Task UnpostAsync(CancellationToken ct = default)
    {
        if (MessageBox.Show("确认要做反过帐处理吗?", "凭证过帐", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            IsBusy = true;
            var n = await Db.FinVouch.UnpostAsync(AuditPeriod, AppSession.Operator, AppSession.DeptNo,
                byDate: false, date: null, ct);
            Message = $"共反过帐 {n} 张记帐凭证!";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>
/// 业务凭证 VM（I.2，fr_finopvouch + fr_finoperation longsky 变体）。
/// 生成=游标预览（ADDSUB 建科目落库）→ 审核（finvid 取新号 + INSERT finvouch(audit=1) + 回写业务单据 audit）
/// → 反审核（删 finvouch + 回写 0）。CBI/CRO/SEV/BMI 备案延后。
/// </summary>
public partial class FinOpVouchViewModel : ObservableObject
{
    public ObservableCollection<FinOpRow> Rows { get; } = [];
    private List<FinOpVouchGroup> _groups = [];

    [ObservableProperty] private string _opty = "SRO";       // SRO / SMO / BBI
    [ObservableProperty] private DateTime _beginDate = DateTime.Today;
    [ObservableProperty] private DateTime _endDate = DateTime.Today;
    [ObservableProperty] private string _beginPaper = "";
    [ObservableProperty] private string _endPaper = "";
    [ObservableProperty] private int _payOption = 4;
    [ObservableProperty] private string _cashSub = "";
    [ObservableProperty] private string _balaSub = "";
    [ObservableProperty] private FinOpRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    public IReadOnlyList<(int Value, string Label)> PayOptions { get; } =
    [
        (1, "现金"), (2, "支票汇票汇款"), (3, "挂帐代销托收其它"), (4, "全部"),
    ];

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sys = await Db.FinSub.GetSysControlAsync(AppSession.DeptNo, ct);
        CashSub = sys?.CashSub ?? "";
        BalaSub = sys?.BalaSub ?? "";
    }

    private FinOpGenerateQuery BuildQuery() => new()
    {
        Opty = Opty,
        BeginDate = BeginDate,
        EndDate = EndDate,
        BeginPaper = BeginPaper,
        EndPaper = EndPaper,
        PayOption = PayOption,
        CashSub = CashSub,
        BalaSub = BalaSub,
        IncludeCost = true,     // VOU_TYPE=3（finopvou.valuen=3 部署实测）
    };

    private void FillRows(IReadOnlyList<FinOpVouchGroup> groups)
    {
        _groups = groups.ToList();
        Rows.Clear();
        foreach (var r in groups.SelectMany(g => g.Lines)) Rows.Add(r);
    }

    /// <summary>生成（row9 查询 + myclick longsky：游标预览，ADDSUB 建科目除外）。</summary>
    [RelayCommand]
    public async Task GenerateAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var groups = await Db.FinOp.GenerateAsync(BuildQuery(), AppSession.DeptNo, ct);
            FillRows(groups);
            Message = groups.Count == 0
                ? "没有符合条件的记录!"
                : $"共 {groups.Count} 张业务凭证（借 {groups.SelectMany(g => g.Lines).Sum(l => l.Debit):N2} / 贷 {groups.SelectMany(g => g.Lines).Sum(l => l.Credit):N2}）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    private FinOpVouchGroup? SelectedGroup =>
        SelectedRow is null ? null : _groups.FirstOrDefault(g => g.Opertype == SelectedRow.Opertype && g.Opercode == SelectedRow.Opercode);

    /// <summary>审核（row2 Audit：已审/上期/月结/结转损益拦截 → finvid 取新号 + INSERT finvouch(audit=1) + 回写业务单据）。</summary>
    [RelayCommand]
    public async Task AuditAsync(CancellationToken ct = default)
    {
        var group = SelectedGroup;
        if (group is null) { Message = "请选择凭证行"; return; }
        if (MessageBox.Show($"确认审核该业务凭证({group.Vouchid})吗?", "业务凭证",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var (vid, vcode) = await Db.FinOp.AuditAsync(group, AppSession.DeptNo, AppSession.Operator, ct);
            Message = $"凭证已审核 vouchid={vid} vcode={vcode}（{group.Opertype} {group.Opercode}）";
            await GenerateAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>全部审核（Allaudit：按生成序逐组审核，单组失败跳过继续）。</summary>
    [RelayCommand]
    public async Task AllAuditAsync(CancellationToken ct = default)
    {
        if (_groups.Count == 0) return;
        if (MessageBox.Show("确认审核当前所有业务凭证吗?", "业务凭证",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var ok = 0;
        var skip = new List<string>();
        foreach (var g in _groups.Where(g => !g.Audited))
        {
            try
            {
                await Db.FinOp.AuditAsync(g, AppSession.DeptNo, AppSession.Operator, ct);
                ok++;
            }
            catch (AppException ex) { skip.Add($"{g.Opertype} {g.Opercode}: {ex.Message}"); }
        }
        Message = $"全部审核完成 {ok} 张" + (skip.Count > 0 ? "；跳过 " + string.Join("；", skip) : "");
        await GenerateAsync(ct);
    }

    /// <summary>反审核（Unaudit：未审/已过帐拦截 → DELETE finvouch + 业务单据 audit 回 0）。</summary>
    [RelayCommand]
    public async Task UnauditAsync(CancellationToken ct = default)
    {
        var group = SelectedGroup;
        if (group is null) { Message = "请选择凭证行"; return; }
        try
        {
            await Db.FinOp.UnauditAsync(group.Vouchid, group.Opertype, group.Opercode, AppSession.DeptNo, ct);
            Message = $"凭证已反审核（{group.Opertype} {group.Opercode}）";
            await GenerateAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    // ── 第十七批补充：明细 / 读凭证 / 单张过帐 / 打印 ──

    /// <summary>业务明细（myclick Detail：SEV 拦截 → fr_finopdet 窗）。</summary>
    [RelayCommand]
    private async Task ShowDetailAsync(CancellationToken ct)
    {
        var group = SelectedGroup;
        if (group is null) { Message = "请选择凭证行"; return; }
        if (group.Opertype == "SEV") { Message = "零售单据无明细"; return; }
        try
        {
            var rows = await Db.FinOp.GetOpDetailAsync(group.Opertype, group.Opercode, ct);
            if (rows.Count == 0) { Message = "没有业务明细！"; return; }
            new Views.FinOpDetailWindow(group.Opertype, group.Opercode, group.Date, rows)
            { Owner = Application.Current.MainWindow }.ShowDialog();
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>读凭证（myclick Readvou F5：fr_finrevouch 按 vouchid 读取 + 上一张/下一张）。</summary>
    [RelayCommand]
    private async Task ReadVouchAsync(CancellationToken ct)
    {
        var group = SelectedGroup;
        if (group is null) { Message = "请选择凭证行"; return; }
        if (group.Vouchid == 0) { Message = "该凭证尚未审核（预览凭证无编号）"; return; }
        try
        {
            var vouches = await Db.FinOp.ListVouchesAsync(ct);
            new Views.FinVouchReadWindow(group.Vouchid, vouches)
            { Owner = Application.Current.MainWindow }.ShowDialog();
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>单张过帐（myclick Post：本期+audit+post 校验 → 复用整期过帐单凭证路径）。</summary>
    [RelayCommand]
    public async Task PostVouchAsync(CancellationToken ct = default)
    {
        var group = SelectedGroup;
        if (group is null || group.Vouchid == 0) { Message = "请选择已审核的凭证行"; return; }
        if (MessageBox.Show($"确认过帐凭证 {group.Vouchid} 吗?", "业务凭证",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            IsBusy = true;
            await Db.FinOp.PostVouchAsync(group.Vouchid, AppSession.DeptNo, AppSession.Operator, ct);
            Message = $"凭证 {group.Vouchid} 已过帐";
            await GenerateAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>单张反过帐（unpost）。</summary>
    [RelayCommand]
    public async Task UnpostVouchAsync(CancellationToken ct = default)
    {
        var group = SelectedGroup;
        if (group is null || group.Vouchid == 0) { Message = "请选择凭证行"; return; }
        if (MessageBox.Show($"确认反过帐凭证 {group.Vouchid} 吗?", "业务凭证",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            IsBusy = true;
            await Db.FinOp.UnpostVouchAsync(group.Vouchid, AppSession.DeptNo, AppSession.Operator, ct);
            Message = $"凭证 {group.Vouchid} 已反过帐";
            await GenerateAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>基础资料 VM（币别/摘要/核算项目/员工/现金银行五页签）。</summary>
public partial class FinBasicViewModel : ObservableObject
{
    public ObservableCollection<(string Mcode, string Mname, decimal Rate)> Currencies { get; } = [];
    public ObservableCollection<(int Id, string Smemo)> Sumaries { get; } = [];
    public ObservableCollection<(int Id, string CId, string CName, int CItem)> FinSets { get; } = [];
    public ObservableCollection<(string Ecode, string Ename)> Employees { get; } = [];
    public ObservableCollection<(int Id, string Accname, string Type, string Subcode, string Subname, string Accounts)> Accounts { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _input1 = "";
    [ObservableProperty] private string _input2 = "";

    [RelayCommand]
    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        try
        {
            Currencies.Clear();
            foreach (var r in await Db.FinBasic.GetCurrenciesAsync(ct)) Currencies.Add(r);
            Sumaries.Clear();
            foreach (var r in await Db.FinBasic.GetSumariesAsync(ct)) Sumaries.Add(r);
            FinSets.Clear();
            foreach (var r in await Db.FinBasic.GetFinSetsAsync(ct)) FinSets.Add(r);
            Employees.Clear();
            foreach (var r in await Db.FinBasic.GetEmployeesAsync(ct)) Employees.Add(r);
            Accounts.Clear();
            foreach (var r in await Db.FinBasic.GetAccountsAsync(ct)) Accounts.Add(r);
            Message = "基础资料已加载";
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task AddCurrencyAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Input1)) { Message = "币别代码不能为空"; return; }
        await Db.FinBasic.SaveCurrencyAsync(Input1.Trim(), Input2.Trim(), 1m, true, AppSession.DeptNo, ct);
        Message = "币别已添加";
        Input1 = Input2 = "";
        await LoadAllAsync(ct);
    }

    [RelayCommand]
    private async Task DeleteCurrencyAsync(CancellationToken ct = default)
    {
        if (Currencies.Count == 0) return;
        await Db.FinBasic.DeleteCurrencyAsync(Currencies[^1].Mcode, AppSession.DeptNo, ct);
        Message = "币别已删除";
        await LoadAllAsync(ct);
    }

    [RelayCommand]
    private async Task AddSumaryAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Input1)) { Message = "摘要内容不能为空"; return; }
        await Db.FinBasic.AddSumaryAsync(Input1.Trim(), AppSession.DeptNo, ct);
        Message = "摘要已添加";
        Input1 = "";
        await LoadAllAsync(ct);
    }

    [RelayCommand]
    private async Task DeleteSumaryAsync(CancellationToken ct = default)
    {
        if (Sumaries.Count == 0) return;
        await Db.FinBasic.DeleteSumaryAsync(Sumaries[^1].Id, AppSession.DeptNo, ct);
        Message = "摘要已删除";
        await LoadAllAsync(ct);
    }

    [RelayCommand]
    private async Task AddEmployeeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Input1)) { Message = "员工代码不能为空"; return; }
        await Db.FinBasic.SaveEmployeeAsync(Input1.Trim(), Input2.Trim(), true, AppSession.DeptNo, ct);
        Message = "员工已添加";
        Input1 = Input2 = "";
        await LoadAllAsync(ct);
    }

    [RelayCommand]
    private async Task DeleteEmployeeAsync(CancellationToken ct = default)
    {
        if (Employees.Count == 0) return;
        await Db.FinBasic.DeleteEmployeeAsync(Employees[^1].Ecode, AppSession.DeptNo, ct);
        Message = "员工已删除";
        await LoadAllAsync(ct);
    }
}
