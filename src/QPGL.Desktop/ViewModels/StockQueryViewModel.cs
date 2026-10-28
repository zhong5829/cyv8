using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 库存查询 VM（E3，M4-2）+ 库存修改入口（B3，M4-3）。
/// 直调 IStockRepository / IStockEditRepository（复刻 fr_istoch 查询/修改/删除契约）。
/// </summary>
public partial class StockQueryViewModel : ObservableObject
{
    public ObservableCollection<StockRow> Rows { get; } = [];

    [ObservableProperty] private string _cpFilter = "";
    [ObservableProperty] private string _nnoFilter = "";
    [ObservableProperty] private string _na1Filter = "";
    [ObservableProperty] private string _tyFilter = "";
    [ObservableProperty] private string _posiFilter = "";
    [ObservableProperty] private string _depotnoFilter = "";
    [ObservableProperty] private bool _onlyInStock;

    [ObservableProperty] private StockRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _total;
    [ObservableProperty] private decimal _sumKcamount;
    [ObservableProperty] private decimal _sumIprjAmount;
    [ObservableProperty] private decimal _sumOprcAmount;

    private string? _sort;
    private bool _sortDesc;

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
            var request = BuildRequest();
            var result = await Db.Stock.SearchAsync(request, AppSession.DeptNo, ct);
            Rows.Clear();
            foreach (var r in result.Items) Rows.Add(r);
            Total = result.Total;
            var summary = await Db.Stock.GetSummaryAsync(request, AppSession.DeptNo, ct);
            SumKcamount = summary.TotalKcamount;
            SumIprjAmount = summary.TotalIprjAmount;
            SumOprcAmount = summary.TotalOprcAmount;
            OnPropertyChanged(nameof(HasPrevPage));
            OnPropertyChanged(nameof(HasNextPage));
            if (Rows.Count == 0) Message = "没有符合条件的记录";
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

    [RelayCommand]
    private async Task PrevPageAsync(CancellationToken ct)
    {
        if (!HasPrevPage) return;
        Page--;
        await SearchAsync(ct);
    }

    [RelayCommand]
    private async Task NextPageAsync(CancellationToken ct)
    {
        if (!HasNextPage) return;
        Page++;
        await SearchAsync(ct);
    }

    /// <summary>库存修改（打开编辑窗，fr_istoch FRM2 语义）。</summary>
    [RelayCommand]
    private void Edit()
    {
        if (SelectedRow is null) { Message = "请先选择库存行"; return; }
        var window = new StockEditWindow(SelectedRow)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        window.ShowDialog();
        if (window.Changed) _ = SearchAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>删除库存行（仅 kcamount=0；B304 禁删权限检查）。</summary>
    [RelayCommand]
    private async Task DeleteAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择库存行"; return; }
        if (AppSession.Permission.HasMenu(StockEditPermissions.ForbidDelete))
        {
            Message = "无删除库存权限（B304 禁删）";
            return;
        }
        var row = SelectedRow;
        if (System.Windows.MessageBox.Show(
                $"确认删除库存行：{row.Cp.Trim()} {row.Nno.Trim()}（数量 {row.Kcamount}）？",
                "删除确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.Yes) return;

        Message = "";
        IsBusy = true;
        try
        {
            await Db.StockEdit.DeleteAsync(row.Id, AppSession.DeptNo, ct);
            Message = "已删除";
            await SearchAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "删除失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>排序白名单列头切换（§7.1）。</summary>
    public void SortBy(string field)
    {
        if (!StockSortFields.TryResolve(field, out _)) return;
        if (string.Equals(_sort, field, StringComparison.OrdinalIgnoreCase))
            _sortDesc = !_sortDesc;
        else
            (_sort, _sortDesc) = (field, true);
        _ = SearchAsync(System.Threading.CancellationToken.None);
    }

    private StockQueryRequest BuildRequest() => new()
    {
        // 旧 fr_istoch：厂牌下拉默认 = P_CP（会话厂牌），用户可改；未填时按会话厂牌过滤
        Cp = NullIfEmpty(CpFilter) ?? (AppSession.Brand.Length > 0 ? AppSession.Brand : null),
        Nno = NullIfEmpty(NnoFilter),
        Na1 = NullIfEmpty(Na1Filter),
        Ty = NullIfEmpty(TyFilter),
        Posi = NullIfEmpty(PosiFilter),
        Depotno = NullIfEmpty(DepotnoFilter),
        OnlyInStock = OnlyInStock,
        Page = Page,
        PageSize = PageSize,
        Sort = _sort,
        Order = _sortDesc ? "desc" : "asc",
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
