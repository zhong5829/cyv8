using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;
using QPGL.Domain.Common;
using QPGL.Domain.Query;
using QPGL.Domain.Sales;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 报价单查询（C5，fr_sesa type='报价' 复刻：条件行级查询 + 明细 + 确认/转销售/打印）。
/// 复用 IDocQueryRepository（SearchOutDocsAsync/明细/汇总）；确认走 ISaleRepository.ConfirmAsync。
/// </summary>
public partial class QuoteQueryViewModel : ObservableObject
{
    public ObservableCollection<OutDocRow> Rows { get; } = [];

    [ObservableProperty] private DateTime _dateFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime _dateTo = DateTime.Today;
    [ObservableProperty] private string _paperFilter = "";
    [ObservableProperty] private string _gnameFilter = "";
    [ObservableProperty] private string _perFilter = "";
    [ObservableProperty] private OutDocRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _total;
    [ObservableProperty] private decimal _sumAmount;

    private string? _sort;
    private bool _sortDesc = true;

    private const int PageSize = 50;
    public bool HasPrevPage => Page > 1;
    public bool HasNextPage => Page * PageSize < Total;

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        Message = "";
        IsBusy = true;
        try
        {
            var request = new OutDocQueryRequest
            {
                Type = "报价",
                DateFrom = DateFrom.Date,
                DateTo = DateTo.Date,
                Paper = NullIfEmpty(PaperFilter),
                Gname = NullIfEmpty(GnameFilter),
                Per = NullIfEmpty(PerFilter),
                Page = Page,
                PageSize = PageSize,
                Sort = _sort,
                Order = _sortDesc ? "desc" : "asc",
            };
            var result = await Db.DocQuery.SearchOutDocsAsync(request, ct);
            Rows.Clear();
            foreach (var r in result.Items) Rows.Add(r);
            Total = result.Total;
            SumAmount = Rows.Sum(r => r.Yhloprct);
            OnPropertyChanged(nameof(HasPrevPage));
            OnPropertyChanged(nameof(HasNextPage));
            if (Rows.Count == 0) Message = "没有符合条件的记录";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "查询失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PrevPageAsync(CancellationToken ct) { if (HasPrevPage) { Page--; await SearchAsync(ct); } }

    [RelayCommand]
    private async Task NextPageAsync(CancellationToken ct) { if (HasNextPage) { Page++; await SearchAsync(ct); } }

    /// <summary>明细窗（DocLineWindow 复用，WHERE paper+type+dpm）。</summary>
    [RelayCommand]
    private void OpenLines()
    {
        if (SelectedRow is null) { Message = "请先选择单据行"; return; }
        new DocLineWindow(SelectedRow.Paper.Trim(), "报价", SelectedRow.Dpm.Trim())
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    /// <summary>确认（row298 FRMOK；确认弹窗与开单页共用）。</summary>
    [RelayCommand]
    private async Task ConfirmAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择单据行"; return; }
        var row = SelectedRow;
        var dialog = new Views.ConfirmSaleDialog(row.Paper.Trim(), "报价", row.Gno.Trim(), row.Gname.Trim(), row.Yhloprct, row.Remark?.Trim() ?? "");
        if (dialog.ShowDialog() != true) return;

        Message = "";
        IsBusy = true;
        try
        {
            var cmd = dialog.Command;
            cmd.Dept = row.Dpm.Trim();
            var result = await Db.Sales.ConfirmAsync(cmd, AppSession.Operator, ct);
            Message = $"已确认 {result.Paper}（结帐日期 {result.Jzdate:yyyy-MM-dd}）";
            await SearchAsync(ct);
        }
        catch (Exception ex) { Message = ex is AppException ae ? ae.Message : "确认失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>报价转销售（row53 + frmbjtoxs 三选项菜单）。</summary>
    [RelayCommand]
    private async Task ConvertToSaleAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择单据行"; return; }
        var paper = SelectedRow.Paper.Trim();
        var option = Views.ConvertOptionWindow.Ask(Owner());
        if (option is null) return;

        Message = "";
        IsBusy = true;
        try
        {
            var result = await Db.Sales.ConvertQuoteToSaleAsync(
                new ConvertQuoteCommand { Dept = SelectedRow.Dpm.Trim(), Paper = paper, Option = option.Value },
                AppSession.Operator, ct);
            var skipInfo = result.SkippedLines > 0 ? $"，{result.SkippedLines} 行不足留在报价单" : "";
            Message = $"已生成销售单 {result.NewPaper}（{result.ConvertedLines} 行，合计 {result.TotalAmount:N2}）{skipInfo}";
            await SearchAsync(ct);
        }
        catch (Exception ex) { Message = ex is AppException ae ? ae.Message : "转单失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>打印（row46 语义：guno='' 未确认拦截；票样按销售单渲染）。</summary>
    [RelayCommand]
    private async Task PrintAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择单据行"; return; }
        if (SelectedRow.Guno.Trim().Length == 0)
        {
            Message = "该报价单未确认（付款方式为空），请先确认再打印（row46 语义）";
            return;
        }
        Message = "";
        try
        {
            await Printing.PrintService.PreviewSaleAsync(SelectedRow.Paper.Trim(), "报价", ct);
        }
        catch (Exception ex) { Message = ex is AppException ae ? ae.Message : "打印失败：" + ex.Message; }
    }

    /// <summary>排序白名单列头切换。</summary>
    public void SortBy(string field)
    {
        if (!OutDocSortFields.TryResolve(field, out _)) return;
        if (string.Equals(_sort, field, StringComparison.OrdinalIgnoreCase)) _sortDesc = !_sortDesc;
        else (_sort, _sortDesc) = (field, true);
        _ = SearchAsync(System.Threading.CancellationToken.None);
    }

    private static System.Windows.Window? Owner() =>
        System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
