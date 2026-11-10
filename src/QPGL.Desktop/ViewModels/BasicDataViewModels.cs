using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Domain.Partners;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 供应商管理 VM（A6，M5）：查询+新增/修改/删除（tbgugys）。
/// 2026-11-08 需求：改无限滚动——每次只取一批（PageSize 条），滚动接近底部再追加下一批，不再有翻页按钮。
/// </summary>
public partial class SupplierViewModel : ObservableObject
{
    public ObservableCollection<SupplierRow> Rows { get; } = [];

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private SupplierRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _total;

    /// <summary>每批条数（原分页每页 50 条，保持一致）。</summary>
    private const int PageSize = 50;

    /// <summary>是否还有未加载的批次（按服务端总数判断）。</summary>
    public bool HasMore => Rows.Count < Total;

    /// <summary>列表底部轻提示：加载中 / 已加载进度 / 已全部加载。</summary>
    public string FooterText => IsBusy
        ? "加载中…"
        : Total == 0 ? "无数据"
        : HasMore ? $"已加载 {Rows.Count} / 共 {Total} 行，继续下滑加载更多"
        : $"共 {Total} 行，已全部加载";

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(FooterText));
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(FooterText));

    /// <summary>关键字查询：清空已加载数据并从第 1 批重新加载（等价原第一页；滚动位置由界面侧回到顶部）。</summary>
    public async Task SearchAsync(CancellationToken ct) => await ResetAsync(PageSize, null, ct);

    /// <summary>滚动接近底部时追加下一批：只追加、不重置已加载数据；IsBusy 防重复请求。</summary>
    public async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (IsBusy || !HasMore) return;
        IsBusy = true;
        Message = "";
        try
        {
            var result = await Db.Suppliers.SearchAsync(new PagedRequest
            {
                Page = Page + 1, PageSize = PageSize, Keyword = NullIfEmpty(Keyword),
            }, ct);
            if (result.Items.Count > 0)
            {
                Page = result.Page;
                foreach (var r in result.Items) Rows.Add(r);
            }
            Total = result.Total;
        }
        catch (Exception ex) { Message = ex.Message; }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(FooterText));
        }
    }

    /// <summary>
    /// 从第 1 批重新加载（刷新/换关键字/保存删除后）。
    /// keepRows：刷新前已加载的行数，循环补批到该数量，避免用户已滚动到的位置突然变空；
    /// focusId：加载完成后定位选中的行（新增/修改后保持可见）。
    /// </summary>
    private async Task ResetAsync(int keepRows, int? focusId, CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        Message = "";
        try
        {
            Rows.Clear();
            var first = await Db.Suppliers.SearchAsync(new PagedRequest
            {
                Page = 1, PageSize = PageSize, Keyword = NullIfEmpty(Keyword),
            }, ct);
            foreach (var r in first.Items) Rows.Add(r);
            Page = 1;
            Total = first.Total;

            var target = Math.Max(PageSize, keepRows);
            while (Rows.Count < target && Rows.Count < Total)
            {
                var next = await Db.Suppliers.SearchAsync(new PagedRequest
                {
                    Page = Page + 1, PageSize = PageSize, Keyword = NullIfEmpty(Keyword),
                }, ct);
                if (next.Items.Count == 0) break;
                Page++;
                foreach (var r in next.Items) Rows.Add(r);
                Total = next.Total;
            }

            if (focusId is int id && Rows.FirstOrDefault(r => r.Id == id) is { } hit) SelectedRow = hit;
        }
        catch (Exception ex) { Message = ex.Message; }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(FooterText));
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        // 统一供应商编辑窗（2026-11-07 需求）：新增/修改共用一窗（标题按模式切换），
        // 取代原先「编号/名称/账期」逐字段 PromptWindow 弹框；落库仍走 Db.Suppliers.SaveAsync
        Message = "";
        var wasNew = SelectedRow is null;
        var loadedRows = Rows.Count;
        var w = new Views.SupplierEditWindow(SelectedRow);
        w.ShowDialog();
        if (!w.Saved) return;

        if (wasNew)
        {
            // 新增：按新编号检索（等价原「按新编号检索 + 定位选中」），确保无条件列表里也能立刻看到
            if (!string.IsNullOrWhiteSpace(w.SavedGyno)) Keyword = w.SavedGyno.Trim();
            await ResetAsync(PageSize, w.SavedId, ct);
        }
        else
        {
            // 修改：保持已加载范围与排序，定位选中被修改行
            await ResetAsync(loadedRows, w.SavedId, ct);
        }
        if (!Rows.Any(r => r.Id == w.SavedId))
            Message = $"已保存供应商 {w.SavedGyno}，但当前列表未显示该行，请点查询确认";
    }

    [RelayCommand]
    private async Task DeleteAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择供应商"; return; }
        if (System.Windows.MessageBox.Show($"确认删除供应商 {SelectedRow.Gyname.Trim()}？", "删除确认",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        Message = "";
        try
        {
            await Db.Suppliers.DeleteAsync(SelectedRow.Id, ct);
            Message = "已删除";
            SelectedRow = null;
            // 删除后按原已加载条数减一刷新，列表与总数同步
            await ResetAsync(Math.Max(PageSize, Rows.Count - 1), null, ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "删除失败：" + ex.Message;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>配件资料 VM（A1，M5）：查询+修改（tbprnoty）。</summary>
public partial class PartProfileViewModel : ObservableObject
{
    public ObservableCollection<PartProfileRow> Rows { get; } = [];

    [ObservableProperty] private string _cpFilter = AppSession.Brand;   // 旧 tbxzb 对 tbprnoty 追加 cp=?p_cp
    [ObservableProperty] private string _nnoFilter = "";
    [ObservableProperty] private string _na1Filter = "";
    [ObservableProperty] private string _tyFilter = "";
    [ObservableProperty] private PartProfileRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _total;

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
            var result = await Db.PartProfiles.SearchAsync(new PartProfileQueryRequest
            {
                Page = Page, PageSize = PageSize,
                Cp = NullIfEmpty(CpFilter), Nno = NullIfEmpty(NnoFilter),
                Na1 = NullIfEmpty(Na1Filter), Ty = NullIfEmpty(TyFilter),
            }, ct);
            Rows.Clear();
            foreach (var r in result.Items) Rows.Add(r);
            Total = result.Total;
            OnPropertyChanged(nameof(HasPrevPage));
            OnPropertyChanged(nameof(HasNextPage));
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private async Task PrevPageAsync(CancellationToken ct) { if (HasPrevPage) { Page--; await SearchAsync(ct); } }
    [RelayCommand] private async Task NextPageAsync(CancellationToken ct) { if (HasNextPage) { Page++; await SearchAsync(ct); } }

    [RelayCommand]
    private async Task UpdateIprjAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择配件资料行"; return; }
        Message = "";
        try
        {
            var iprjText = Views.PromptWindow.Ask(
                $"修改 {SelectedRow.Cp.Trim()}/{SelectedRow.Nno.Trim()} 的进价金额", SelectedRow.Iprj.ToString());
            if (!decimal.TryParse(iprjText, out var iprj)) { Message = "进价金额格式错误"; return; }
            await Db.PartProfiles.UpdateAsync(new UpdatePartProfileCommand
            {
                Cp = SelectedRow.Cp, Nno = SelectedRow.Nno, Iprj = iprj,
            }, ct);
            Message = "已修改";
            await SearchAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "修改失败：" + ex.Message;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>单号前缀 VM（A9，M5）：查询+改前缀（tbdjgs）。</summary>
public partial class DocNoPrefixViewModel : ObservableObject
{
    public ObservableCollection<DocNoPrefixRow> Rows { get; } = [];

    [ObservableProperty] private DocNoPrefixRow? _selectedRow;
    [ObservableProperty] private string _message = "";

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        Message = "";
        try
        {
            Rows.Clear();
            foreach (var r in await Db.DocNoPrefix.GetListAsync(AppSession.DeptNo, ct)) Rows.Add(r);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private async Task UpdatePrefixAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择单据类型"; return; }
        Message = "";
        try
        {
            var djjx = Views.PromptWindow.Ask(
                $"修改「{SelectedRow.Djname.Trim()}」的当前流水号前缀（当前 {SelectedRow.Djjx.Trim()}，流水 {SelectedRow.Lsh}）",
                SelectedRow.Djjx.Trim());
            if (string.IsNullOrWhiteSpace(djjx)) return;
            await Db.DocNoPrefix.UpdateAsync(new UpdateDocNoPrefixCommand { Id = SelectedRow.Id, Djjx = djjx }, AppSession.DeptNo, ct);
            Message = "前缀已修改";
            await SearchAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "修改失败：" + ex.Message;
        }
    }
}
