using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Orders;
using QPGL.Infrastructure.Parts;

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
    public ObservableCollection<PartInRecordRow> InRecords { get; } = [];
    public ObservableCollection<PartSaleRecordRow> SaleRecords { get; } = [];
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

    // 选料查询条（2026-11-03 需求：编码/名称/车型三条件实时+防抖；名称/车型支持拼音码）
    [ObservableProperty] private string _qNno = "";
    [ObservableProperty] private string _qNa1 = "";
    [ObservableProperty] private string _qTy = "";
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
    partial void OnSelectedPickChanged(PartPickRow? value)
    {
        _ = RefreshLinkAsync();
        _ = LoadPickHistoriesAsync();
    }

    partial void OnSelectedFormulaChanged(OrderExpRow? value)
    {
        // 旧 ___.row589 L94-98：选定公式 → SETEXP 赋值 → 重查选料与明细（建议数列批量计算）
        _ = RefreshLinkAsync();
        _ = SearchPartsAsync(CancellationToken.None);
        _ = ReloadLinesWithFormulaAsync();
    }

    private async Task ReloadLinesWithFormulaAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentCno)) return;
        try { await LoadLinesAsync(CancellationToken.None); }
        catch { /* 重查失败不阻断 */ }
    }

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
        var rows = await Db.Order4.GetPlanListAsync(AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, ct);
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
        // 旧 ___.row183 L22：明细 ydqty=公式相关子查询（无公式=0）；日期区间=选料查询条（备案）
        var sugExp = SuggestExpOrNull();
        foreach (var r in await Db.Order4.GetPlanLinesAsync(CurrentCno, AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions,
            sugExp, QDate1, QDate2, ct)) Lines.Add(r);
    }

    /// <summary>选中公式的表达式（2026-11-03 起 P02=实时近三月月均，全部公式可执行）。</summary>
    private string? SuggestExpOrNull() => SelectedFormula?.Cexpress;

    // 新建计划单默认单头（2026-11-03 需求：新建时录入供应商/订货日期；tborder 行式存储，
    // 单头随首行订货写入，之后行继承）
    private DateTime? _newPlanDate;
    private string? _newPlanGyname;
    /// <summary>选料查询竞态序号（实时+防抖下旧请求晚到丢弃）。</summary>
    private int _pickSeq;
    /// <summary>选料无限滑动分页（2026-11-03 需求，PartPickWindow 同机制）。</summary>
    private const int PickPageSize = 100;
    private int _pickPage = 1;
    private bool _pickHasMore;
    /// <summary>历史区竞态序号（快速切换选中行时旧请求晚到丢弃）。</summary>
    private int _histSeq;

    [RelayCommand]
    public async Task NewPlanAsync(CancellationToken ct)
    {
        var d = new Views.NewPlanDialog();
        if (d.ShowDialog() != true) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.Order4.ValidateNewCnoAsync(d.PlanCno, ct);
            CurrentCno = d.PlanCno;
            _newPlanDate = d.Orderdate;
            _newPlanGyname = d.Gyname;
            // 单头行立即落库（nno=''、oqty=0）→ 计划单列表马上出现，关软件后可继续
            await Db.Order4.EnsureHeadRowAsync(CurrentCno, d.Orderdate, d.Gyname,
                AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, AppSession.Operator, ct);
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

    /// <summary>保存计划单（2026-11-03 需求）：确保单头行存在；已订明细行本就逐行落库，
    /// 保存后关闭软件重开 → 计划单列表双击打开即可继续订货。</summary>
    [RelayCommand]
    public async Task SavePlanAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentCno))
        {
            MessageBox.Show("请先打开或新建计划单。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            IsBusy = true;
            Message = "";
            var head = SelectedPlan ?? Plans.FirstOrDefault(pl => pl.Cno == CurrentCno);
            await Db.Order4.EnsureHeadRowAsync(CurrentCno,
                head?.Orderdate ?? _newPlanDate ?? DateTime.Today,
                head?.Gyname?.Trim() ?? _newPlanGyname ?? "",
                AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, AppSession.Operator, ct);
            await LoadPlansAsync(ct);
            Message = $"计划单 {CurrentCno} 已保存，关闭软件后可继续订货。";
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

    [RelayCommand]
    public async Task EditHeadAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentCno))
        {
            MessageBox.Show("请先打开一张计划单。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 2026-11-03 需求：付款方式下拉（默认挂帐）、直发客户拼音码联想
        var d = new Views.EditHeadDialog(CurrentCno, SelectedPlan?.Orderdate ?? DateTime.Today,
            SelectedPlan?.Bzlx?.Trim() ?? "", SelectedPlan?.Fkfs?.Trim() ?? "",
            SelectedPlan?.Gyname?.Trim() ?? "", SelectedPlan?.Gname?.Trim() ?? "");
        if (d.ShowDialog() != true) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.Order4.UpdateHeadAsync(CurrentCno, new PlanHeadInput
            {
                NewCno = d.NewCno, Orderdate = d.Orderdate, Bzlx = d.Bzlx, Fkfs = d.Fkfs,
                Gyname = d.Gyname, Gname = d.Gname,
            }, AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, ct);
            CurrentCno = d.NewCno;
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
            await Db.Order4.DeletePlanAsync(CurrentCno, AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, ct);
            CurrentCno = "";
            _newPlanDate = null;
            _newPlanGyname = null;
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
        // 旧 燂.row162 L8-13：订货总金额为 0 时二次确认
        var amount = SelectedPlan?.Iprj ?? Lines.Where(l => l.Cno == CurrentCno).Sum(l => l.Iprj * l.Oqty);
        if (amount == 0 && MessageBox.Show("订货总金额为0，您是否继续确认！", "提示",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (MessageBox.Show($"你确信要将该定单:{CurrentCno}并入在途吗？", "并入在途", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        try
        {
            IsBusy = true;
            Message = "";
            var n = await Db.Order4.MergeToTransitAsync(CurrentCno, AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, ct);
            MessageBox.Show($"已并入在途 {n} 行。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Information);
            CurrentCno = "";
            _newPlanDate = null;
            _newPlanGyname = null;
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

    /// <summary>打开选择配件窗口（2026-11-03 需求：选料区从页面移入独立窗口，PartPickWindow 同模式）。</summary>
    [RelayCommand]
    public void OpenPick()
    {
        var w = new Views.OrderPickWindow(this);
        w.ShowDialog();
    }

    [RelayCommand]
    public async Task SearchPartsAsync(CancellationToken ct)
    {
        // 竞态守卫：实时+防抖下连续触发，慢的旧请求返回后直接丢弃
        var seq = ++_pickSeq;
        try
        {
            IsBusy = true;
            Message = "";
            // 旧 __.row2 PR_FILT：SETEXP（选中公式）→ ydqty 列批量公式求值；无公式=0
            var rows = await Db.Order4.SearchPartsAsync(new PartPickQuery
            {
                Nno = QNno, Na1 = QNa1, Ty = QTy,
                Date1 = QDate1, Date2 = QDate2, Num1 = QNum1, Num2 = QNum2, Kc1 = QKc1, Kc2 = QKc2,
                Page = 1, PageSize = PickPageSize,
            }, AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, SuggestExpOrNull(), ct);
            if (seq != _pickSeq) return;
            ApplyOrderedNums(rows);
            PickRows.Clear();
            foreach (var r in rows) PickRows.Add(r);
            _pickPage = 1;
            _pickHasMore = rows.Count >= PickPageSize;
            if (rows.Count == 0)
            {
                Message = "未找到符合条件的配件";
                // 旧 __.row2 L81-86：按编码查无属性记录 → 询问添加新属性（fr_prnoty 新增窗）
                var nno = QNno.Trim();
                if (nno.Length > 0 && !nno.StartsWith("*") && !nno.StartsWith("/") &&
                    MessageBox.Show("无此属性记录，添加新属性吗？", "提示", MessageBoxButton.YesNo,
                        MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    var w = new Views.PartAttributeEditWindow(null, Cp.Trim());
                    w.Row.Nno = nno;
                    w.Owner = Application.Current.MainWindow;
                    if (w.ShowDialog() == true)
                        await Db.Prnoty.SaveAsync(new PrnotySaveCommand
                        {
                            IsAdd = true, Id = 0, Cp = w.Row.Cp, Nno = w.Row.Nno, OldNno = "",
                            Na1 = w.Row.Na1, Ty = w.Row.Ty, Fa = w.Row.Fa, Dc = w.Row.Dc, Unit = w.Row.Unit,
                            Iprj = w.Row.Iprj, Code_ts = w.Row.Code_ts, Eng = w.Row.Eng, Nnp = w.Row.Nnp,
                            Nnq = w.Row.Nnq, Uqty = w.Row.Uqty, Xno = w.Row.Xno, Tno = w.Row.Tno,
                            Amjj = w.Row.Amjj, Bmjj = w.Row.Bmjj,
                            Nvol = decimal.TryParse(w.Row.Nvol, out var nv) ? nv : 0,
                            Suttle = w.Row.Suttle, Note = w.Row.Note, Note1 = w.Row.Note1,
                            Oprc_cp = w.Row.Oprc_cp, Oprc_min = w.Row.Oprc_min,
                        }, Cp.Trim(), AppSession.DeptPermissions);
                }
            }
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

    /// <summary>无限滑动：选料列表滚到底自动加载下一页（2026-11-03 需求，PartPickWindow 同机制）。</summary>
    public async Task LoadMorePartsAsync(CancellationToken ct)
    {
        if (IsBusy || !_pickHasMore) return;
        var seq = _pickSeq;
        try
        {
            IsBusy = true;
            var rows = await Db.Order4.SearchPartsAsync(new PartPickQuery
            {
                Nno = QNno, Na1 = QNa1, Ty = QTy,
                Date1 = QDate1, Date2 = QDate2, Num1 = QNum1, Num2 = QNum2, Kc1 = QKc1, Kc2 = QKc2,
                Page = _pickPage + 1, PageSize = PickPageSize,
            }, AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, SuggestExpOrNull(), ct);
            if (seq != _pickSeq) return;   // 期间发生了新检索 → 丢弃
            _pickPage++;
            _pickHasMore = rows.Count >= PickPageSize;
            ApplyOrderedNums(rows);
            foreach (var r in rows) PickRows.Add(r);
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

    /// <summary>已订数（row2 L73-77 SET RELATION：当前单该编码的已订 oqty，索引序第一条）。</summary>
    private void ApplyOrderedNums(IEnumerable<PartPickRow> rows)
    {
        if (CurrentCno.Length == 0) return;
        var ordered = Lines.Where(l => l.Cno == CurrentCno).ToList();
        foreach (var r in rows)
            r.Num = ordered.FirstOrDefault(l => l.Nno == r.Nno)?.Oqty ?? 0;
    }

    /// <summary>历史区（2026-11-03 需求）：选中配件的进货历史（tbistoed）与销售历史（tbsada）。</summary>
    private async Task LoadPickHistoriesAsync()
    {
        var p = SelectedPick;
        InRecords.Clear();
        SaleRecords.Clear();
        if (p is null) return;
        var seq = ++_histSeq;
        var cp = Cp.Trim();
        try
        {
            var ins = await Db.Order4.GetInRecordsAsync(cp, p.Nno);
            var sales = await Db.Order4.GetSaleRecordsAsync(cp, p.Nno);
            if (seq != _histSeq || !ReferenceEquals(SelectedPick, p)) return;
            foreach (var r in ins) InRecords.Add(r);
            foreach (var r in sales) SaleRecords.Add(r);
        }
        catch (AppException ex)
        {
            if (seq == _histSeq) Message = ex.Message;
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
        var tIprj = d.AddText("进价", p.Iprj.ToString("0.##"));
        var tOprc = d.AddText("售价(可空)", "0");
        d.ChainEnterFocus(tOqty, tIprj, tOprc);   // 打开聚焦订数全选；回车→进价→售价全选；再回车=确定
        if (!d.ShowOkCancel()) return;
        // 旧 ___.row352 L9-13：新增行继承单头（日期/备注类型/供应商/付款方式/直发客户）；
        // 新单尚无行时用「新建计划单」录入的日期/供应商兜底
        var head = SelectedPlan ?? Plans.FirstOrDefault(pl => pl.Cno == CurrentCno);
        var firstLine = Lines.FirstOrDefault();
        var input = new OrderLineAddInput
        {
            Cno = CurrentCno, Nno = p.Nno, Na1 = p.Na1, Ty = p.Ty, Unit = p.Unit, Dc = p.Dc,
            Oqty = ParseDec(tOqty.Text), Iprj = ParseDec(tIprj.Text), Fa = p.Fa.Trim(),
            Oprc = ParseDec(tOprc.Text) is > 0 ? ParseDec(tOprc.Text) : null,
            Orderdate = head?.Orderdate ?? firstLine?.Orderdate ?? _newPlanDate ?? DateTime.Today,
            Bzlx = head?.Bzlx?.Trim() ?? firstLine?.Bzlx ?? "",
            Fkfs = head?.Fkfs?.Trim() ?? firstLine?.Fkfs ?? "",
            Gyname = head?.Gyname?.Trim() ?? firstLine?.Gyname ?? _newPlanGyname ?? "",
            Gname = head?.Gname?.Trim() ?? firstLine?.Gname ?? "",
        };
        await AddLineCoreAsync(input, addQty: false, ct);
    }

    private async Task AddLineCoreAsync(OrderLineAddInput input, bool addQty, CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            await Db.Order4.AddLineAsync(input, AppSession.DeptNo, Cp.Trim(), AppSession.DeptPermissions, AppSession.Operator, addQty, ct);
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
        var tIprj = d.AddText("进价", l.Iprj.ToString("0.##"));
        var tOprc = d.AddText("售价", l.Oprc.ToString("0.##"));
        if (!d.ShowOkCancel()) return;
        try
        {
            IsBusy = true;
            Message = "";
            var deleted = await Db.Order4.UpdateLineAsync(l.Id, ParseDec(tOqty.Text), ParseDec(tIprj.Text),
                ParseDec(tOprc.Text), l.Fa.Trim(), AppSession.DeptNo, Cp.Trim(), ct);
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

    /// <summary>
    /// 导出明细表格（2026-11-03 需求）：当前计划单明细导出 xlsx——
    /// 首行合并 A1:D1 居中大字体供应商名，第二行列头 编码/名称/车型/订货数量，逐行明细。
    /// </summary>
    [RelayCommand]
    public void ExportLines()
    {
        if (Lines.Count == 0)
        {
            MessageBox.Show("当前计划单没有明细可导出！", "订单生成", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var gy = (SelectedPlan?.Gyname ?? Lines.FirstOrDefault()?.Gyname ?? "").Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel文件|*.xlsx",
            FileName = $"{(gy.Length > 0 ? gy + "_" : "")}{CurrentCno.Trim()}.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("订货明细");
            // 首行：供应商名称（合并居中大字体）
            ws.Range(1, 1, 1, 4).Merge();
            ws.Cell(1, 1).Value = gy;
            ws.Range(1, 1, 1, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Range(1, 1, 1, 4).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Range(1, 1, 1, 4).Style.Font.Bold = true;
            ws.Range(1, 1, 1, 4).Style.Font.FontSize = 18;
            // 第二行：列头
            string[] heads = ["编码", "名称", "车型", "订货数量"];
            for (var i = 0; i < heads.Length; i++)
                ws.Cell(2, i + 1).Value = heads[i];
            ws.Range(2, 1, 2, 4).Style.Font.Bold = true;
            // 明细行
            var r = 3;
            foreach (var l in Lines)
            {
                ws.Cell(r, 1).Value = l.Nno.Trim();
                ws.Cell(r, 2).Value = l.Na1.Trim();
                ws.Cell(r, 3).Value = l.Ty.Trim();
                ws.Cell(r, 4).Value = l.Oqty;
                r++;
            }
            ws.Columns(1, 4).AdjustToContents();
            wb.SaveAs(dlg.FileName);
            MessageBox.Show($"已导出：{dlg.FileName}", "订单生成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        // 旧 g__.row510 L21-30：数量校验（TXT3.TAG=原数量）
        var newQty = ParseDec(tQty.Text);
        if (newQty > l.Oqty)
        {
            MessageBox.Show("新订货数量不能大于原订货数量。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (newQty <= 0)
        {
            MessageBox.Show("新订货数量不能是负数，也不能是零。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            IsBusy = true;
            Message = "";
            if (newCno == l.Cno.Trim())
            {
                // 旧 g__.row510 L31-35 U8：同单号改数量 → 原行直接更新
                if (newQty != l.Oqty)
                    await Db.Order4.ChangeQtyAsync(l.Id, newQty, AppSession.DeptNo, Cp.Trim(), ct);
            }
            else
            {
                // 旧 g__.row510 L37-40：cno or orno 防重（存在 → 询问是否继续，默认否）
                await Db.Order4.ValidateNewCnoAsync(newCno, ct);
                if (newQty < l.Oqty)
                    await Db.Order4.SplitLineAsync(l.Id, newCno, newQty, AppSession.DeptNo, Cp.Trim(), AppSession.Operator, ct);
                else
                    await Db.Order4.MoveLineAsync(l.Id, newCno, AppSession.DeptNo, Cp.Trim(), ct);
            }
            await LoadLinesAsync(ct);
            await LoadPlansAsync(ct);
        }
        catch (AppException ex) when (ex.Code == "CNO_EXISTS")
        {
            IsBusy = false;
            if (MessageBox.Show("该订单号已存在，是否继续？", "提示", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                try
                {
                    if (newQty < l.Oqty)
                        await Db.Order4.SplitLineAsync(l.Id, newCno, newQty, AppSession.DeptNo, Cp.Trim(), AppSession.Operator, ct);
                    else
                        await Db.Order4.MoveLineAsync(l.Id, newCno, AppSession.DeptNo, Cp.Trim(), ct);
                    await LoadLinesAsync(ct);
                    await LoadPlansAsync(ct);
                }
                catch (AppException ex2)
                {
                    Message = ex2.Message;
                    MessageBox.Show(ex2.Message, "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
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
        // 旧 查.row160 L3（明细入口）/ ㄧ_.row334 L3（选料入口）：B303 / B4 权限点（'@'超级恒可）
        var needCode = SelectedLine is not null ? "B303" : "B4";
        if (!AppSession.Permission.HasMenu(needCode))
        {
            MessageBox.Show("没有权限。", "订单生成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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
                // 公式执行日期区间=选料查询条（备案：旧为公式窗独立日期）；P02=实时近三月月均
                var q = await Db.Order4.GetSuggestQtyAsync(f.Cexpress, SelectedPick.Nno, AppSession.DeptNo, cp,
                    AppSession.DeptPermissions, QDate1, QDate2);
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
