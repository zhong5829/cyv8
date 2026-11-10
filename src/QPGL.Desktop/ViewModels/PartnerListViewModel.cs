using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 客户资料查询（tbgu，§6.7：实时查询分页，禁一次性 TOP N）。直连模式直调仓储。
/// 2026-11-08 需求：改无限滚动——每次只取一批（PageSize 条），滚动接近底部再追加下一批，不再有翻页按钮。
/// </summary>
public partial class PartnerListViewModel : ObservableObject
{
    public ObservableCollection<Partner> Items { get; } = [];

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _total;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private Partner? _selectedItem;

    /// <summary>排序字段（白名单映射真实列）。</summary>
    [ObservableProperty] private string? _sort;
    [ObservableProperty] private bool _sortDesc = true;

    /// <summary>每批条数（原分页每页 50 条，保持一致）。</summary>
    private const int PageSize = 50;

    /// <summary>是否还有未加载的批次（按服务端总数判断）。</summary>
    public bool HasMore => Items.Count < Total;

    /// <summary>列表底部轻提示：加载中 / 已加载进度 / 已全部加载。</summary>
    public string FooterText => IsLoading
        ? "加载中…"
        : Total == 0 ? "无数据"
        : HasMore ? $"已加载 {Items.Count} / 共 {Total} 行，继续下滑加载更多"
        : $"共 {Total} 行，已全部加载";

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(FooterText));
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(FooterText));

    /// <summary>关键字查询：清空已加载数据并从第 1 批重新加载（等价原第一页；滚动位置由界面侧回到顶部）。</summary>
    public async System.Threading.Tasks.Task SearchAsync(CancellationToken ct = default)
        => await ResetAsync(PageSize, null, ct);

    /// <summary>滚动接近底部时追加下一批：只追加、不重置已加载数据；IsLoading 防重复请求。</summary>
    public async System.Threading.Tasks.Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (IsLoading || !HasMore) return;
        IsLoading = true;
        Message = "";
        try
        {
            var result = await Db.Partners.SearchAsync(BuildRequest(Page + 1), ct);
            if (result.Items.Count > 0)
            {
                Page = result.Page;
                foreach (var item in result.Items) Items.Add(item);
            }
            Total = result.Total;
        }
        catch (Exception ex)
        {
            Message = "查询失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
            NotifyFooter();
        }
    }

    /// <summary>列头点击排序（Sorting 事件转发）：排序变化后从第 1 批重载（与关键字查询同语义）。</summary>
    public async System.Threading.Tasks.Task SortByAsync(string sortField, CancellationToken ct = default)
    {
        if (Sort == sortField)
            SortDesc = !SortDesc;
        else
        {
            Sort = sortField;
            SortDesc = false;
        }
        await ResetAsync(PageSize, null, ct);
    }

    /// <summary>
    /// 保存后刷新：新增定位新客户（列表里没有则按编号检索），修改保持已加载范围并定位。
    /// </summary>
    public async System.Threading.Tasks.Task AfterSavedAsync(string partNo, bool isNew, int loadedRows, CancellationToken ct = default)
    {
        await ResetAsync(isNew ? PageSize : Math.Max(PageSize, loadedRows), partNo, ct);

        // 新增但当前关键字过滤掉了它 → 按新编号重新检索（沿用供应商管理页的处理）
        if (isNew && !string.IsNullOrWhiteSpace(partNo) && !Items.Any(p => SameNo(p.PartNo, partNo)))
        {
            Keyword = partNo.Trim();
            await ResetAsync(PageSize, partNo, ct);
        }
    }

    /// <summary>删除后刷新：按原已加载条数减一重载，列表与总数同步。</summary>
    public async System.Threading.Tasks.Task AfterDeletedAsync(int loadedRows, CancellationToken ct = default)
        => await ResetAsync(Math.Max(PageSize, loadedRows - 1), null, ct);

    /// <summary>
    /// 从第 1 批重新加载（刷新/换关键字/排序/保存删除后）。
    /// keepRows：刷新前已加载的行数，循环补批到该数量，避免用户已滚动到的位置突然变空；
    /// focusPartNo：加载完成后定位选中的行（新增/修改后保持可见）。
    /// </summary>
    private async System.Threading.Tasks.Task ResetAsync(int keepRows, string? focusPartNo, CancellationToken ct)
    {
        if (IsLoading) return;
        IsLoading = true;
        Message = "";
        try
        {
            Items.Clear();
            var first = await Db.Partners.SearchAsync(BuildRequest(1), ct);
            foreach (var item in first.Items) Items.Add(item);
            Page = 1;
            Total = first.Total;

            var target = Math.Max(PageSize, keepRows);
            while (Items.Count < target && Items.Count < Total)
            {
                var next = await Db.Partners.SearchAsync(BuildRequest(Page + 1), ct);
                if (next.Items.Count == 0) break;
                Page++;
                foreach (var item in next.Items) Items.Add(item);
                Total = next.Total;
            }

            if (!string.IsNullOrWhiteSpace(focusPartNo))
            {
                var hit = Items.FirstOrDefault(p => SameNo(p.PartNo, focusPartNo));
                if (hit is not null) SelectedItem = hit;
            }
        }
        catch (Exception ex)
        {
            Message = "查询失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
            NotifyFooter();
        }
    }

    private PagedRequest BuildRequest(int page) => new()
    {
        Page = page,
        PageSize = PageSize,
        Sort = Sort,
        Order = SortDesc ? "desc" : "asc",
        Keyword = Keyword.Trim(),
    };

    private void NotifyFooter()
    {
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(FooterText));
    }

    private static bool SameNo(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
