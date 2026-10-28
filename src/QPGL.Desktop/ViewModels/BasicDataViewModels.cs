using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Domain.Partners;

namespace QPGL.Desktop.ViewModels;

/// <summary>供应商管理 VM（A6，M5）：查询+新增/修改/删除（tbgugys）。</summary>
public partial class SupplierViewModel : ObservableObject
{
    public ObservableCollection<SupplierRow> Rows { get; } = [];

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private SupplierRow? _selectedRow;
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
            var result = await Db.Suppliers.SearchAsync(new PagedRequest { Page = Page, PageSize = PageSize, Keyword = NullIfEmpty(Keyword) }, ct);
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
    private async Task SaveAsync(CancellationToken ct)
    {
        // 简易行内编辑：新增用对话框输入（gyno/gyname 必填），修改选中行
        Message = "";
        IsBusy = true;
        try
        {
            var s = SelectedRow;
            var gyno = Views.PromptWindow.Ask("供应商编号", s?.Gyno ?? "") ?? "";
            var gyname = Views.PromptWindow.Ask("供应商名称", s?.Gyname ?? "") ?? "";
            var zqText = Views.PromptWindow.Ask("账期（天）", s?.Zq.ToString() ?? "0");
            var command = new SaveSupplierCommand
            {
                Id = s?.Id,
                Gyno = gyno,
                Gyname = gyname,
                Zq = decimal.TryParse(zqText, out var zq) ? zq : 0,
                Tel = s?.Tel ?? "",
                Adr = s?.Adr ?? "",
                Note = s?.Note ?? "",
            };
            var result = await Db.Suppliers.SaveAsync(command, ct);
            Message = result.IsCreate ? "供应商已添加" : "供应商已修改";
            await SearchAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "保存失败：" + ex.Message;
        }
        finally { IsBusy = false; }
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
            await SearchAsync(ct);
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
