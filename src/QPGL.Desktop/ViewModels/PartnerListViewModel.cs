using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 客户资料查询（tbgu，§6.7：实时查询分页，禁一次性 TOP N）。直连模式直调仓储。
/// </summary>
public partial class PartnerListViewModel : ObservableObject
{
    public ObservableCollection<Partner> Items { get; } = [];

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "";

    /// <summary>排序字段（白名单映射真实列）。</summary>
    [ObservableProperty] private string? _sort;
    [ObservableProperty] private bool _sortDesc = true;

    [RelayCommand]
    public async System.Threading.Tasks.Task SearchAsync(CancellationToken ct)
    {
        Page = 1;
        await LoadAsync(ct);
    }

    [RelayCommand] private System.Threading.Tasks.Task FirstPageAsync(CancellationToken ct) => GoPageAsync(1, ct);
    [RelayCommand] private System.Threading.Tasks.Task PrevPageAsync(CancellationToken ct) => GoPageAsync(Page - 1, ct);
    [RelayCommand] private System.Threading.Tasks.Task NextPageAsync(CancellationToken ct) => GoPageAsync(Page + 1, ct);
    [RelayCommand] private System.Threading.Tasks.Task LastPageAsync(CancellationToken ct) => GoPageAsync(TotalPages, ct);

    [RelayCommand]
    public async System.Threading.Tasks.Task ChangePageSizeAsync(string size, CancellationToken ct)
    {
        if (int.TryParse(size, out var n) && n > 0)
        {
            PageSize = n;
            Page = 1;
            await LoadAsync(ct);
        }
    }

    /// <summary>列头点击排序（Sorting 事件转发）。</summary>
    public async System.Threading.Tasks.Task SortByAsync(string sortField, CancellationToken ct = default)
    {
        if (Sort == sortField)
            SortDesc = !SortDesc;
        else
        {
            Sort = sortField;
            SortDesc = false;
        }
        await LoadAsync(ct);
    }

    private async System.Threading.Tasks.Task GoPageAsync(int page, CancellationToken ct)
    {
        if (page < 1 || page > TotalPages) return;
        Page = page;
        await LoadAsync(ct);
    }

    public async System.Threading.Tasks.Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoading) return;
        IsLoading = true;
        Message = "";
        try
        {
            var request = new PagedRequest
            {
                Page = Page,
                PageSize = PageSize,
                Sort = Sort,
                Order = SortDesc ? "desc" : "asc",
                Keyword = Keyword.Trim(),
            };

            var result = await Db.Partners.SearchAsync(request, ct);
            Items.Clear();
            foreach (var item in result.Items) Items.Add(item);
            Total = result.Total;
            TotalPages = Math.Max(1, (int)Math.Ceiling(result.Total / (double)result.PageSize));
            Page = result.Page;
        }
        catch (Exception ex)
        {
            Message = "查询失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
