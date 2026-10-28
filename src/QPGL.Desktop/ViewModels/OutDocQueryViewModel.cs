using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Domain.Query;
using QPGL.Infrastructure.Query;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 出库单查询（E2，M3-2）。直连模式直调 IDocQueryRepository；
/// 作废/打印复用 ISaleRepository.VoidAsync / PrintService（与旧 fr_sesa 入口一致）。
/// 布局参照 qp11 SellControl 查询模式：上方单据列表（滚动无限加载）+ 下方选中单据明细。
/// </summary>
public partial class OutDocQueryViewModel : ObservableObject
{
    /// <summary>单据类型下拉（tbsada.type 值域；旧 CBOTY 取值后 4 字）。</summary>
    public static readonly string[] DocTypes = ["销售", "退货", "调拨", "历史", "预售"];

    public ObservableCollection<OutDocBillRow> Bills { get; } = [];
    public ObservableCollection<OutDocLine> Lines { get; } = [];

    /// <summary>操作员下拉名单（tbkey 实时查询，与登录页同源）。</summary>
    public System.Collections.Generic.IReadOnlyList<string> Operators { get; private set; } = [];

    /// <summary>客户名称/拼音码联想防抖（300ms，与开单模式 GnameBox 同机制）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _custDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };

    public ObservableCollection<Partner> CustomerSuggestions { get; } = [];

    [ObservableProperty]
    private string _docType = "销售";

    [ObservableProperty]
    private DateTime _dateFrom = DateTime.Today;

    [ObservableProperty]
    private DateTime _dateTo = DateTime.Today;

    [ObservableProperty]
    private string _paperFilter = "";

    [ObservableProperty]
    private string _gnameFilter = "";

    [ObservableProperty]
    private string _perFilter = "";

    [ObservableProperty]
    private OutDocBillRow? _selectedBill;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private decimal _sumAmount;

    /// <summary>选中单据的单头汇总（下方单头只读栏）。</summary>
    [ObservableProperty]
    private OutDocSummary? _selectedSummary;

    private string? _sort;
    private bool _sortDesc = true;

    private const int PageSize = 50;
    private int _page = 1;
    private bool _loadingMore;
    private bool _operatorsLoaded;

    /// <summary>单据列表滚动到底自动加载下一页（无限加载，替代分页按钮）。</summary>
    public bool HasMore => _page * PageSize < Total;

    public OutDocQueryViewModel()
    {
        _custDebounce.Tick += async (_, _) =>
        {
            _custDebounce.Stop();
            await SearchCustomerSuggestionsAsync();
        };
    }

    /// <summary>客户输入变化时调用：重置联想防抖（View 的 TextChanged 转发）。</summary>
    public void DebounceCustomerSearch()
    {
        _custDebounce.Stop();
        _custDebounce.Start();
    }

    /// <summary>客户联想搜索（tbgu 按 gname/gno/code/code1/tel/mobile 前缀匹配，与开单模式同源）。</summary>
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

    /// <summary>选中联想项：回填客户名称。</summary>
    public void ApplyCustomer(Partner p)
    {
        GnameFilter = p.PartName.Trim();
        CustomerSuggestions.Clear();
        Message = "";
    }

    /// <summary>操作员名单懒加载（首次打开查询模式时拉取；失败静默保留手输）。</summary>
    public async Task EnsureOperatorsAsync(CancellationToken ct = default)
    {
        if (_operatorsLoaded) return;
        _operatorsLoaded = true;
        try
        {
            Operators = await Db.Users.GetOperatorsAsync();
            OnPropertyChanged(nameof(Operators));
        }
        catch { /* 名单失败不阻断查询，仍可手输 */ }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        Message = "";
        IsBusy = true;
        try
        {
            _page = 1;
            var request = BuildRequest();
            var result = await Db.DocQuery.SearchOutDocBillsAsync(request, ct);
            Bills.Clear();
            foreach (var r in result.Items) Bills.Add(r);
            Total = result.Total;
            SumAmount = Bills.Sum(r => r.TotalAmount);
            OnPropertyChanged(nameof(HasMore));
            if (Bills.Count == 0)
            {
                Message = "没有符合条件的记录";
                Lines.Clear();
                SelectedSummary = null;
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

    /// <summary>滚动到底触发：追加下一页（竞态与防重入由 _loadingMore + HasMore 守卫）。</summary>
    public async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (_loadingMore || IsBusy || !HasMore) return;
        _loadingMore = true;
        try
        {
            _page++;
            var request = BuildRequest();
            var result = await Db.DocQuery.SearchOutDocBillsAsync(request, ct);
            foreach (var r in result.Items) Bills.Add(r);
            SumAmount = Bills.Sum(r => r.TotalAmount);
            OnPropertyChanged(nameof(HasMore));
        }
        catch (Exception ex)
        {
            _page--;   // 失败回退页码允许重试
            Message = ex is AppException ae ? ae.Message : "加载失败：" + ex.Message;
        }
        finally
        {
            _loadingMore = false;
        }
    }

    /// <summary>选中单据联动：加载单头汇总 + 明细（qp11 DgBills_SelectionChanged 同构）。</summary>
    partial void OnSelectedBillChanged(OutDocBillRow? value)
    {
        if (value is null) return;
        _ = LoadSelectionAsync(value);
    }

    private async Task LoadSelectionAsync(OutDocBillRow bill)
    {
        try
        {
            var paper = bill.Paper.Trim();
            var type = bill.Type.Trim();
            var dpm = bill.Dpm.Trim();
            var summaryTask = Db.DocQuery.GetOutDocSummaryAsync(paper, type, dpm);
            var linesTask = Db.DocQuery.GetOutDocLinesAsync(paper, type, dpm);
            await Task.WhenAll(summaryTask, linesTask);
            // 竞态守卫：快速切换行时丢弃过期结果
            if (!ReferenceEquals(SelectedBill, bill)) return;
            SelectedSummary = summaryTask.Result;
            Lines.Clear();
            foreach (var l in linesTask.Result) Lines.Add(l);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "明细加载失败：" + ex.Message;
        }
    }

    /// <summary>排序白名单列头切换（§7.1）。</summary>
    public void SortBy(string field)
    {
        if (!OutDocSortFields.TryResolve(field, out _)) return;
        if (string.Equals(_sort, field, StringComparison.OrdinalIgnoreCase))
            _sortDesc = !_sortDesc;
        else
            (_sort, _sortDesc) = (field, true);
        _ = SearchAsync(CancellationToken.None);
    }

    private OutDocQueryRequest BuildRequest() => new()
    {
        Type = DocType,
        DpmPermissions = AppSession.DeptPermissions,   // 旧 P_DPMUSEM 部门数据权限
        DateFrom = DateFrom.Date,
        DateTo = DateTo.Date,
        Paper = NullIfEmpty(PaperFilter),
        Gname = NullIfEmpty(GnameFilter),
        Per = NullIfEmpty(PerFilter),
        Page = _page,
        PageSize = PageSize,
        Sort = _sort,
        Order = _sortDesc ? "desc" : "asc",
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>作废（删除整单，旧 fr_sesa 删除入口：仅 type='销售'；走 sale_del 触发器回补）。</summary>
    [RelayCommand]
    private async Task VoidAsync(CancellationToken ct)
    {
        if (SelectedBill is null) { Message = "请先选择单据行"; return; }
        if (!string.Equals(SelectedBill.Type.Trim(), "销售", StringComparison.Ordinal))
        {
            Message = "旧系统语义：仅销售单支持查询界面作废删除";
            return;
        }
        var paper = SelectedBill.Paper.Trim();
        if (System.Windows.MessageBox.Show($"确认作废并删除销售单 {paper}？库存将经触发器回补，不可恢复。",
                "作废确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        Message = "";
        IsBusy = true;
        try
        {
            var result = await Db.Sales.VoidAsync(paper, SelectedBill.Dpm.Trim(), AppSession.Operator, ct);
            Message = $"已作废 {paper}：删除 {result.DeletedLines} 行，回补库存 {result.RestoredStock} 件";
            await SearchAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "作废失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>重打印（旧 fr_sesa 查询后打印入口）。</summary>
    [RelayCommand]
    private async Task PrintAsync(CancellationToken ct)
    {
        if (SelectedBill is null) { Message = "请先选择单据行"; return; }
        if (!string.Equals(SelectedBill.Type.Trim(), "销售", StringComparison.Ordinal))
        {
            Message = "一期票样仅支持销售单重打印";
            return;
        }
        Message = "";
        try
        {
            await QPGL.Desktop.Printing.PrintService.PreviewSaleAsync(SelectedBill.Paper.Trim(), "销售", ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "打印失败：" + ex.Message;
        }
    }
}
