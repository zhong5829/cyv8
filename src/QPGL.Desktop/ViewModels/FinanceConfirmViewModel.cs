using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.FinanceConfirm;
using QPGL.Infrastructure.FinanceConfirm;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 财务确认（G.1，旧 fr_sk 复刻）。10 页签中按部署约定实现 4 页：
/// 出库确认（复用 ReceiptRepository row358/row369 写路径 + ReceiptWindow）、
/// 入库确认（row410）、调拨确认（妴_.row478）、代销付款确认（___.row454）；
/// 维修 3 页依附录A排除、服务卡/精品/增值卡模块未建 → 置灰并标注。
/// 工具栏：查询/确认/还原/刷新/分类汇总/打印/说明（旧 Dh.row32/__.row33/蓽.row34/踰_.row190/Z_.row54/__.row72/遊_.row160）。
/// </summary>
public partial class FinanceConfirmViewModel : ObservableObject
{
    /// <summary>
    /// 页签序号（0 基，直接绑定 TabControl.SelectedIndex；旧 PAF1.ACTIVEPAGE 为 1 基，
    /// 对应关系：出库=旧页1、入库=旧页2、调拨=旧页3、代销付款=旧页7）。
    /// </summary>
    public const int PageOut = 0, PageIn = 1, PageTransfer = 2, PageConsign = 6;

    /// <summary>
    /// 工具栏右侧下拉 = 旧 CBOPAPERTYPE（P__.row73：tbsysset papertype 值域 + 「全部」，
    /// 按 tbsada_header.papertype 过滤；截图值域 门店/仓库/全部）。
    /// 旧 CBO1（销售/预售）仅展力/致友忠达显示，本部署隐藏 → 页1 type 固定「销售」。
    /// </summary>
    public ObservableCollection<string> PaperTypes { get; } = ["全部"];

    [ObservableProperty]
    private string _paperType = "全部";

    /// <summary>页1 出库类型（旧 CBO1 通用分支固定「销售」；不再暴露 UI）。</summary>
    public string OutType => "销售";

    public ObservableCollection<FinConfirmOutRow> OutBills { get; } = [];
    public ObservableCollection<FinConfirmInRow> InBills { get; } = [];
    public ObservableCollection<FinConfirmTransferRow> TransferBills { get; } = [];
    public ObservableCollection<FinConfirmConsignRow> ConsignRows { get; } = [];

    /// <summary>单号明细（旧 GRDMX：选中行联动，三页共用列集 cp/nno/na1/ty/fa/unit/数量/售价/实售价/进价/外币价/仓库）。</summary>
    public ObservableCollection<FinConfirmLine> Lines { get; } = [];

    [ObservableProperty]
    private int _activePage = PageOut;

    /// <summary>页1 状态下拉索引（0=未确认/1=已确认，旧 Dh.row32 CASE 1 fr_find：未确认 mark='' / 已确认 mark&lt;&gt;''）；
    /// 旧 setsource CASE 1 默认查未确认。请求 Status 在 BuildOutRequest 映射。</summary>
    [ObservableProperty]
    private int _outStatus;

    /// <summary>旧 setsource CASE 1 无日期默认（仅 mark 过滤）；留空 = 不过滤。</summary>
    [ObservableProperty]
    private DateTime? _outDateFrom;

    [ObservableProperty]
    private DateTime? _outDateTo;

    [ObservableProperty]
    private string _outPaper = "";

    [ObservableProperty]
    private string _outGname = "";

    [ObservableProperty]
    private DateTime _inDateFrom = DateTime.Today.AddDays(-7);

    [ObservableProperty]
    private DateTime _inDateTo = DateTime.Today;

    [ObservableProperty]
    private string _inCno = "";

    [ObservableProperty]
    private string _inGyname = "";

    /// <summary>页2 状态（旧 Dh.row32 CASE 2 fr_find：未确认 mark='' / 已确认 mark='已确认'）。</summary>
    [ObservableProperty]
    private int _inStatus;

    [ObservableProperty]
    private DateTime _trDateFrom = DateTime.Today.AddDays(-7);

    [ObservableProperty]
    private DateTime _trDateTo = DateTime.Today;

    [ObservableProperty]
    private string _trPaper = "";

    [ObservableProperty]
    private string _trGname = "";

    /// <summary>页3 状态（旧 Dh.row32 CASE 3 fr_find：未确认 mark='' / 已确认 mark='Y'）。</summary>
    [ObservableProperty]
    private int _trStatus;

    [ObservableProperty]
    private DateTime _csDateFrom = DateTime.Today;

    [ObservableProperty]
    private DateTime _csDateTo = DateTime.Today;

    [ObservableProperty]
    private string _csPaper = "";

    [ObservableProperty]
    private string _csGname = "";

    /// <summary>页7 状态（旧 Dh.row32 CASE 7 fr_find：未确认 skper='' / 已确认 skper&lt;&gt;''）。</summary>
    [ObservableProperty]
    private int _csStatus;

    [ObservableProperty]
    private FinConfirmOutRow? _selectedOut;

    [ObservableProperty]
    private FinConfirmInRow? _selectedIn;

    [ObservableProperty]
    private FinConfirmTransferRow? _selectedTransfer;

    [ObservableProperty]
    private FinConfirmConsignRow? _selectedConsign;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isBusy;

    public bool CanConfirm => AppSession.Permission.IsSuper || AppSession.Permission.HasMenu("G11");
    public bool CanSeeCost => AppSession.Permission.CanSeeCost;

    partial void OnActivePageChanged(int value)
    {
        // 旧页框 Activate：首次进入自动 SETSOURCE（TAG 空判断；此处简化为切换即重查）
        _ = SearchAsync();
    }

    partial void OnPaperTypeChanged(string value)
    {
        // 旧 P__.row73 InteractiveChange：设置 M_WHERE（papertype 过滤）并触发刷新
        if (ActivePage == PageOut) _ = SearchAsync();
    }

    /// <summary>加载单据类型字典（旧 P__.row73 Init：tbsysset papertype + 「全部」）。</summary>
    public async Task EnsurePaperTypesAsync(CancellationToken ct = default)
    {
        if (PaperTypes.Count > 1) return;
        try
        {
            foreach (var t in await Db.FinConfirm.GetPaperTypesAsync(ct))
                if (!PaperTypes.Contains(t)) PaperTypes.Add(t);
        }
        catch { /* 字典失败不阻断，「全部」仍可用 */ }
    }

    /// <summary>查询（旧 Dh.row32 → setsource；刷新按钮 踰_.row190 同路径）。</summary>
    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        Message = "";
        IsBusy = true;
        try
        {
            switch (ActivePage)
            {
                case PageOut:
                {
                    var rows = await Db.FinConfirm.SearchOutAsync(BuildOutRequest(), ct);
                    OutBills.Clear();
                    foreach (var r in rows) OutBills.Add(r);
                    if (rows.Count == 0) Message = "没有符合条件的记录";
                    Lines.Clear();
                    break;
                }
                case PageIn:
                {
                    var rows = await Db.FinConfirm.SearchInAsync(BuildInRequest(), ct);
                    InBills.Clear();
                    foreach (var r in rows) InBills.Add(r);
                    if (rows.Count == 0) Message = "没有符合条件的记录";
                    Lines.Clear();
                    break;
                }
                case PageTransfer:
                {
                    var rows = await Db.FinConfirm.SearchTransferAsync(BuildTransferRequest(), ct);
                    TransferBills.Clear();
                    foreach (var r in rows) TransferBills.Add(r);
                    if (rows.Count == 0) Message = "没有符合条件的记录";
                    Lines.Clear();
                    break;
                }
                case PageConsign:
                {
                    var rows = await Db.FinConfirm.SearchConsignAsync(BuildConsignRequest(), ct);
                    ConsignRows.Clear();
                    foreach (var r in rows) ConsignRows.Add(r);
                    if (rows.Count == 0) Message = "没有符合条件的记录";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "查询失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private FinConfirmOutRequest BuildOutRequest() => new()
    {
        Type = OutType,
        // 下拉索引映射请求状态：未确认(0)→1（mark=''）、已确认(1)→2（mark<>''）
        Status = OutStatus == 1 ? 2 : 1,
        // 旧 P__.row73：非「全部」→ paper in (select paper from tbsada_header where papertype=?)
        PaperType = PaperType.Trim() == "全部" ? null : PaperType,
        DateFrom = OutDateFrom,
        DateTo = OutDateTo,
        Paper = NullIfEmpty(OutPaper),
        Gname = NullIfEmpty(OutGname),
        // 旧 fr_sk 四页均无部门数据权限过滤（P_DPMUSEM），照抄不加
    };

    private FinConfirmInRequest BuildInRequest() => new()
    {
        // 旧 setsource CASE 2 默认 mark<>'已确认'（未确认）；fr_find 可选已确认（mark='已确认'）→ Status 2
        Status = InStatus == 1 ? 2 : 1,
        DateFrom = InDateFrom,
        DateTo = InDateTo,
        Cno = NullIfEmpty(InCno),
        Gyname = NullIfEmpty(InGyname),
    };

    private FinConfirmTransferRequest BuildTransferRequest() => new()
    {
        // 旧 setsource CASE 3 默认 mark=''；fr_find 可选已确认（mark='Y'）→ Status 2
        Status = TrStatus == 1 ? 2 : 1,
        DateFrom = TrDateFrom,
        DateTo = TrDateTo,
        Paper = NullIfEmpty(TrPaper),
        Gname = NullIfEmpty(TrGname),
    };

    private FinConfirmConsignRequest BuildConsignRequest() => new()
    {
        // 旧 fr_find 状态选项默认：skper=''（未确认）；已确认 = skper<>'' → Status 2
        Status = CsStatus == 1 ? 2 : 1,
        DateFrom = CsDateFrom,
        DateTo = CsDateTo,
        Paper = NullIfEmpty(CsPaper),
        Gname = NullIfEmpty(CsGname),
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── 单据行 → 明细联动（旧 薀_.row232 / ___.row302 / ⒀.row35 AfterRowColChange） ──

    partial void OnSelectedOutChanged(FinConfirmOutRow? value)
    {
        if (value is null) return;
        _ = LoadLinesAsync(GetOutLinesAsync(value));
    }

    partial void OnSelectedInChanged(FinConfirmInRow? value)
    {
        if (value is null) return;
        _ = LoadLinesAsync(Db.FinConfirm.GetInLinesAsync(value.Cno.Trim(), value.Gyname.Trim(), value.Indate, value.Isord.Trim()));
    }

    partial void OnSelectedTransferChanged(FinConfirmTransferRow? value)
    {
        if (value is null) return;
        _ = LoadLinesAsync(Db.FinConfirm.GetTransferLinesAsync(value.Paper.Trim()));
    }

    /// <summary>页1 明细（旧 row232：type=?m_type and paper=?m_paper；售价=oprc 实售=oprct）。</summary>
    private async Task<IReadOnlyList<FinConfirmLine>> GetOutLinesAsync(FinConfirmOutRow bill)
    {
        // 出库明细走 IDocQueryRepository 行级查询（同 WHERE type+paper 语义）
        var lines = await Db.DocQuery.GetOutDocLinesAsync(bill.Paper.Trim(), OutType.Trim(), bill.Dpm.Trim());
        return lines.Select(l => new FinConfirmLine
        {
            Cp = l.Cp, Nno = l.Nno, Na1 = l.Na1, Ty = l.Ty, Unit = l.Unit,
            Amount = l.Ckamount, Oprc = l.Oprc, Oprct = l.Oprct, Depotno = l.Depotno,
        }).ToList();
    }

    private async Task LoadLinesAsync(Task<IReadOnlyList<FinConfirmLine>> task)
    {
        try
        {
            var lines = await task;
            Lines.Clear();
            foreach (var l in lines) Lines.Add(l);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "明细加载失败：" + ex.Message;
        }
    }

    // ── 确认（旧 __.row33 逐页分发） ──

    [RelayCommand]
    private async Task ConfirmAsync(CancellationToken ct)
    {
        if (!CanConfirm) { Message = "权限不够，不能确认！"; return; }
        switch (ActivePage)
        {
            case PageOut: await ConfirmOutAsync(ct); break;
            case PageIn: await ConfirmInAsync(ct); break;
            case PageTransfer: await ConfirmTransferAsync(ct); break;
            case PageConsign: await ConfirmConsignAsync(ct); break;
            default: Message = "该页签未启用"; break;
        }
    }

    /// <summary>页1 出库确认（旧 row33 CASE 1 → FRMGATH/FRMSK 收款窗 = ReceiptWindow）。
    /// 确认成功后就地更新本地行（旧 row421 REPLACE C_CK，不重查）。</summary>
    private async Task ConfirmOutAsync(CancellationToken ct)
    {
        if (SelectedOut is null) { Message = "请先选择单据行"; return; }
        if (SelectedOut.Mark.Trim().Length > 0) { Message = "该单已做收款！"; return; }
        if (SelectedOut.Sk == "开单中") { Message = "销售单正在开单，请稍后确认！"; return; }
        await Task.CompletedTask;
        var row = SelectedOut;
        var win = new Views.ReceiptWindow(row.Paper.Trim(), OutType.Trim(), row.Dpm.Trim())
        { Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive) };
        win.ShowDialog();
        if (win.ViewModel.AlreadyConfirmed)
        {
            // 旧 row421：REPLACE MARK 'Y', SK "已确认", PER1, QRDATE（GUNO 窗内选择，待刷新对齐）
            row.Mark = "Y";
            row.Sk = "已确认";
            row.Per1 = AppSession.Operator;
            row.Qrdate = DateTime.Now;
            var idx = OutBills.IndexOf(row);
            if (idx >= 0) OutBills[idx] = row;   // 触发行刷新
        }
    }

    /// <summary>页2 入库确认（旧 row33 CASE 2 → FRM_PNO 弹窗）。成功后就地更新本地行（旧 row30-34 REPLACE C_RK，不重查）。</summary>
    private async Task ConfirmInAsync(CancellationToken ct)
    {
        if (SelectedIn is null) { Message = "没有数据！"; return; }
        if (SelectedIn.Mark.Trim() == "已确认") { Message = "该单已确认！"; return; }
        var locked = await Db.FinConfirm.HasInYsyfAsync(SelectedIn.Cno.Trim(), SelectedIn.Isord.Trim(), ct);
        var dlg = new Views.FinanceConfirmDialogs.ConfirmInDialog(SelectedIn, locked) { Owner = Owner() };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        IsBusy = true;
        try
        {
            await Db.FinConfirm.ConfirmInAsync(dlg.Result, AppSession.Operator, ct);
            // 旧 row30-34：REPLACE MARK '已确认', PNO, FKFS, GYNAME, QRR, QRDATE（本地游标，不重查）
            var row = SelectedIn;
            row.Mark = "已确认";
            row.Pno = dlg.Result.Pno;
            row.Fkfs = dlg.Result.Fkfs;
            row.Gyname = dlg.Result.Gyname;
            row.Qrr = AppSession.Operator;
            row.Qrdate = DateTime.Now;
            var idx = InBills.IndexOf(row);
            if (idx >= 0) InBills[idx] = row;
            Message = $"进库单 {row.Cno.Trim()} 确认成功";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "确认失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>页3 调拨确认（旧 row33 CASE 3 → FRM调拨确认弹窗）。成功后就地更新本地行（旧 row33 REPLACE C_DB，不重查）。</summary>
    private async Task ConfirmTransferAsync(CancellationToken ct)
    {
        if (SelectedTransfer is null) { Message = "没有数据！"; return; }
        if (SelectedTransfer.Sk == "开单中") { Message = "调拨单正在开单，请稍后确认！"; return; }
        // 旧 row33 CASE 3 L107：实时查库 mark in ('Y','R') 才算已确认——char 列空格 mark（''=筛选可通过）
        //   在旧 VFP EMPTY() 语义下是未确认，照抄只判 Y/R，不做 Length>0 判断
        if (SelectedTransfer.Mark.Trim() is "Y" or "R") { Message = "该单已确认！"; return; }
        var dlg = new Views.FinanceConfirmDialogs.ConfirmTransferDialog(SelectedTransfer) { Owner = Owner() };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        IsBusy = true;
        try
        {
            await Db.FinConfirm.ConfirmTransferAsync(dlg.Result, AppSession.Operator, ct);
            // 旧 row33：REPLACE MARK 'Y', SK '已确认' IN C_DB（GUNO 窗内选择）
            var row = SelectedTransfer;
            row.Mark = "Y";
            row.Sk = "已确认";
            row.Guno = dlg.Result.Fkfs;
            row.Qrdate = DateTime.Now;
            var idx = TransferBills.IndexOf(row);
            if (idx >= 0) TransferBills[idx] = row;
            Message = $"调拨单 {row.Paper.Trim()} 确认成功";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "确认失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>页7 代销付款确认（旧 row33 CASE 7 → FRMPJ 弹窗）。成功后就地更新勾选行（旧 row40 REPLACE C_DXPJ，不重查）。</summary>
    private async Task ConfirmConsignAsync(CancellationToken ct)
    {
        var picked = ConsignRows.Where(r => r.Checked && r.Mark == "未确认").ToList();
        if (ConsignRows.Count == 0) { Message = "没有数据！"; return; }
        if (picked.Count == 0) { Message = "请选择要确认的选项！"; return; }
        var dlg = new Views.FinanceConfirmDialogs.ConfirmConsignDialog(picked) { Owner = Owner() };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        IsBusy = true;
        try
        {
            await Db.FinConfirm.ConfirmConsignAsync(dlg.Result, AppSession.Operator, ct);
            // 旧 row40：REPLACE SKPER, SKDATE, MARK '已确认', 选项 0（本地游标，不重查）
            foreach (var cmd in dlg.Result)
            {
                var row = picked.FirstOrDefault(r => r.Paper == cmd.Paper && r.Nno == cmd.Nno && r.Cp == cmd.Cp && r.Dpm == cmd.Dpm);
                if (row is null) continue;
                row.Mark = "已确认";
                row.Skper = cmd.Skper;
                row.Skdate = cmd.Skdate;
                row.Checked = false;
                var idx = ConsignRows.IndexOf(row);
                if (idx >= 0) ConsignRows[idx] = row;
            }
            Message = $"代销付款确认完成（{dlg.Result.Count} 行）";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "确认失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    // ── 还原（旧 蓽.row34 逐页） ──

    [RelayCommand]
    private async Task RestoreAsync(CancellationToken ct)
    {
        if (!CanConfirm) { Message = "权限不够，不能还原！"; return; }
        switch (ActivePage)
        {
            case PageOut: await RestoreOutAsync(ct); break;
            case PageIn: await RestoreInAsync(ct); break;
            case PageTransfer: await RestoreTransferAsync(ct); break;
            case PageConsign: await RestoreConsignAsync(ct); break;
            default: Message = "该页签未启用"; break;
        }
    }

    /// <summary>页1 出库还原（旧 row34 CASE 1，核心写路径在 ReceiptRepository.CancelAsync）。
    /// 成功后就地更新本地行（旧 row55 REPLACE，不重查）。</summary>
    private async Task RestoreOutAsync(CancellationToken ct)
    {
        if (SelectedOut is null || SelectedOut.Mark.Trim().Length == 0)
        { Message = "没有记录，或该单未收款！"; return; }
        if (SelectedOut.Mark.Trim() == "T") { Message = "已提货！"; return; }
        if (await Db.FinConfirm.IsMonthlyLockedAsync(SelectedOut.Qrdate, ct))
        { Message = "该单已做月结，不允许还原！"; return; }
        if (System.Windows.MessageBox.Show("确认还原吗？", "还原",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            await Db.Receipts.CancelAsync(SelectedOut.Paper.Trim(), OutType.Trim(), SelectedOut.Dpm.Trim(), AppSession.Operator, ct);
            // 旧 row55：REPLACE MARK '', SK '', PER1 '', QRDATE {}, JIANSHOU 0
            var row = SelectedOut;
            row.Mark = "";
            row.Sk = "";
            row.Per1 = "";
            row.Qrdate = new DateTime(1900, 1, 1);
            row.Jianshou = 0;
            row.Ssje = 0;
            var idx = OutBills.IndexOf(row);
            if (idx >= 0) OutBills[idx] = row;
            Message = $"出库确认 单据 {row.Paper.Trim()} 已还原";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "还原失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>页2 入库还原（旧 row34 CASE 2）。成功后就地更新本地行（旧 row179 REPLACE，不重查）。</summary>
    private async Task RestoreInAsync(CancellationToken ct)
    {
        if (SelectedIn is null || SelectedIn.Mark.Trim().Length == 0)
        { Message = "没有记录，或该单未确认！"; return; }
        if (await Db.FinConfirm.IsMonthlyLockedAsync(SelectedIn.Qrdate, ct))
        { Message = "该单已做月结，不允许还原！"; return; }
        if (System.Windows.MessageBox.Show("确认还原吗？", "还原",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            await Db.FinConfirm.CancelInAsync(SelectedIn.Cno.Trim(), SelectedIn.Isord.Trim(), AppSession.Operator, ct);
            // 旧 row179：REPLACE MARK '', QRR '', QRDATE {^1900-01-01}
            var row = SelectedIn;
            row.Mark = "";
            row.Qrr = "";
            row.Qrdate = new DateTime(1900, 1, 1);
            var idx = InBills.IndexOf(row);
            if (idx >= 0) InBills[idx] = row;
            Message = $"入库确认 单据 {row.Cno.Trim()} 已还原";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "还原失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>页3 调拨还原（旧 row34 CASE 3：mark='R' 拒绝在仓储）。成功后就地更新本地行（旧 row218 REPLACE，不重查）。</summary>
    private async Task RestoreTransferAsync(CancellationToken ct)
    {
        if (SelectedTransfer is null) { Message = "没有记录！"; return; }
        if (await Db.FinConfirm.IsMonthlyLockedAsync(SelectedTransfer.Qrdate, ct))
        { Message = "该单已做月结，不允许还原！"; return; }
        if (System.Windows.MessageBox.Show("确认还原吗？", "还原",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            await Db.FinConfirm.CancelTransferAsync(SelectedTransfer.Paper.Trim(), SelectedTransfer.Gname.Trim(),
                SelectedTransfer.Dpm.Trim(), AppSession.Operator, ct);
            // 旧 row218：REPLACE MARK '', SK ''
            var row = SelectedTransfer;
            row.Mark = "";
            row.Sk = "";
            var idx = TransferBills.IndexOf(row);
            if (idx >= 0) TransferBills[idx] = row;
            Message = $"调拨确认 单据 {row.Paper.Trim()} 已还原";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "还原失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>页7 代销付款还原（旧 row34 CASE 7：逐勾选行）。成功后就地更新勾选行（旧 row335 REPLACE，不重查）。</summary>
    private async Task RestoreConsignAsync(CancellationToken ct)
    {
        var pickedRows = ConsignRows.Where(r => r.Checked && r.Mark == "已确认").ToList();
        var picked = pickedRows.Select(r => new FinConfirmConsignCommand
        {
            Paper = r.Paper, Nno = r.Nno, Cp = r.Cp, Dpm = r.Dpm,
            Skper = "", Skdate = new DateTime(1900, 1, 1),
        }).ToList();
        if (ConsignRows.Count == 0) { Message = "没有数据！"; return; }
        if (picked.Count == 0)
        {
            Message = ConsignRows.Any(r => r.Checked) ? "您所选择的选项还没有确认！" : "请选择要还原的选项！";
            return;
        }
        if (System.Windows.MessageBox.Show("是否真的要还原所选择的选项？", "提示",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            await Db.FinConfirm.CancelConsignAsync(picked, AppSession.Operator, ct);
            // 旧 row335：REPLACE SKPER '', SKDATE {}, MARK '未确认', 选项 0
            foreach (var row in pickedRows)
            {
                row.Mark = "未确认";
                row.Skper = "";
                row.Skdate = new DateTime(1900, 1, 1);
                row.Checked = false;
                var idx = ConsignRows.IndexOf(row);
                if (idx >= 0) ConsignRows[idx] = row;
            }
            Message = $"代销付款还原完成（{picked.Count} 行）";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "还原失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    // ── 分类汇总（旧 Z_.row54：当前列表合计 → fr_total 窗） ──

    [RelayCommand]
    private async Task TotalAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<FinConfirmTotalRow> totals = ActivePage switch
            {
                PageOut => await Db.FinConfirm.TotalOutAsync(BuildOutRequest(), ct),
                PageIn => await Db.FinConfirm.TotalInAsync(BuildInRequest(), ct),
                PageTransfer => await Db.FinConfirm.TotalTransferAsync(BuildTransferRequest(), ct),
                PageConsign => await Db.FinConfirm.TotalConsignAsync(BuildConsignRequest(), ct),
                _ => [],
            };
            if (totals.Count == 0) return;
            Views.FinanceConfirmDialogs.ShowTotal(totals);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "合计失败：" + ex.Message;
        }
        await Task.CompletedTask;
    }

    // ── 打印（旧 __.row72：页1 打印销售单 re_sada → PrintService 票样） ──

    [RelayCommand]
    private async Task PrintAsync(CancellationToken ct)
    {
        // 旧 __.row72 打印菜单：BAR2 销售单（页1）→ re_sada 票样；BAR1 放行条对调拨（页3）走 re_sada 调拨单
        // 新系统票样统一 SaleTicketDocument（PrintService 支持 docType 取数类型）
        switch (ActivePage)
        {
            case PageOut when SelectedOut is not null:
                await QPGL.Desktop.Printing.PrintService.PreviewSaleAsync(SelectedOut.Paper.Trim(), OutType.Trim(), ct);
                break;
            case PageTransfer when SelectedTransfer is not null:
                await QPGL.Desktop.Printing.PrintService.PreviewSaleAsync(SelectedTransfer.Paper.Trim(), "调拨", ct);
                break;
            default:
                Message = "本选项卡没有可供打印！";
                break;
        }
    }

    // ── 说明（旧 遊_.row160 → fr_note） ──

    [RelayCommand]
    private void ShowHelp()
    {
        // 遊_.row160 LCHELP 原文（本次部署排除维修页，保留 1/3/4/8 条并标注）
        const string text = """
            财务确认（新系统实现：出库/入库/调拨/代销付款四页签；维修相关页按约定排除）

            1、出库确认：外卖销售单的收款确认，来源是[出库管理/销售管理]，确认后进入收款记录，挂帐的进入应收应付。

            2、出库确认-[单号明细]：红色表示该零件的实售价小于人进价。

            3、入库确认：入库单的确认，用于财务标记该单已核对，不影响其它流程。挂帐的在[日营业统计]时进入应收应付。

            4、调拨确认：调拨单的确认。挂帐的在[日营业统计]时进入应收应付。

            8、代销付款确认：配件/精品代销，以出库记录与供应商对帐，即卖多少结算多少，查询的是财务确认后的出库记录，出库类型包括'销售'和'历史'，记录付款日期和付款经手人，在出库单查询中可查到。
            """;
        System.Windows.MessageBox.Show(text, "财务确认说明",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private static System.Windows.Window? Owner() =>
        System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
}
