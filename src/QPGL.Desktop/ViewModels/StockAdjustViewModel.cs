using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>调仓明细行（tbsadc，可编辑数量/目标仓位）。</summary>
public partial class StockAdjustLineRow : ObservableObject
{
    public int Id { get; init; }
    public int Kcid { get; init; }
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Depotno { get; init; } = "";     // 源仓库
    public string Posi { get; init; } = "";        // 源仓位
    public decimal Iprc { get; init; }
    public string M { get; init; } = "";           // '*' = 已确认

    [ObservableProperty] private decimal _ckamount;      // 调仓数量（可编辑）
    [ObservableProperty] private string _depotno1 = "";  // 目标仓库（可编辑）
    [ObservableProperty] private string _posi1 = "";     // 目标仓位（可编辑）
}

/// <summary>
/// 调仓管理 VM（C.7，二期）。直调 IStockAdjustRepository（fr_sadc 契约）：
/// DJLSH 取号 → 选源批次 + 目标仓位生成明细（tbsadc）→ 确认（ISAPPE 源减/目标加，m='*'）。
/// </summary>
public partial class StockAdjustViewModel : ObservableObject
{
    public ObservableCollection<StockBatchLine> Batches { get; } = [];
    public ObservableCollection<StockAdjustLineRow> Lines { get; } = [];
    public ObservableCollection<string> OpenPapers { get; } = [];

    [ObservableProperty] private string _nnoContains = "";
    [ObservableProperty] private string _paper = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private decimal _qty = 1;
    [ObservableProperty] private string _depotno1 = "";
    [ObservableProperty] private string _posi1 = "";

    public StockBatchLine? SelectedBatch { get; set; }
    public StockAdjustLineRow? SelectedLine { get; set; }

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            await ReloadPapersAsync(ct);
            await SearchBatchesAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    private async Task ReloadPapersAsync(CancellationToken ct)
    {
        OpenPapers.Clear();
        foreach (var p in await Db.StockAdjust.GetOpenPapersAsync(AppSession.DeptNo, ct))
            OpenPapers.Add(p);
    }

    /// <summary>源批次查询（tbisto，零件号包含）。</summary>
    [RelayCommand]
    private async Task SearchBatchesAsync(CancellationToken ct)
    {
        try
        {
            var rows = await Db.StockAdjust.GetStockBatchesAsync(AppSession.DeptNo, NnoContains.Trim(), ct);
            Batches.Clear();
            foreach (var b in rows) Batches.Add(b);
        }
        catch (Exception ex)
        {
            Message = "批次查询失败：" + ex.Message;
        }
    }

    /// <summary>新建调仓单（DJLSH('调仓单',1)）。</summary>
    [RelayCommand]
    private async Task NewPaperAsync(CancellationToken ct)
    {
        Message = "";
        IsBusy = true;
        try
        {
            Paper = await Db.StockAdjust.NextPaperAsync(AppSession.DeptNo, ct);
            Lines.Clear();
            Message = $"调仓单 {Paper} 已建号，请选择源批次后添加调仓明细";
            await ReloadPapersAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "取号失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>切换加载调仓单明细。</summary>
    [RelayCommand]
    private async Task LoadPaperAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Paper)) return;
        try
        {
            await LoadLinesAsync(Paper, ct);
        }
        catch (Exception ex)
        {
            Message = "加载失败：" + ex.Message;
        }
    }

    private async Task LoadLinesAsync(string paper, CancellationToken ct)
    {
        var rows = await Db.StockAdjust.GetLinesAsync(paper, ct);
        Lines.Clear();
        foreach (var l in rows)
            Lines.Add(new StockAdjustLineRow
            {
                Id = l.Id, Kcid = l.Kcid, Nno = l.Nno, Na1 = l.Na1,
                Depotno = l.Depotno, Posi = l.Posi, Iprc = l.Iprc, M = l.M,
                Ckamount = l.Ckamount, Depotno1 = l.Depotno1, Posi1 = l.Posi1,
            });
        Paper = paper;
    }

    /// <summary>添加调仓明细（a_.row150：源批次快照 + 目标仓位；约束：目标≠源、累计不超库存）。</summary>
    [RelayCommand]
    private async Task AddLineAsync(CancellationToken ct)
    {
        Message = "";
        if (SelectedBatch is null) { Message = "请先在上表选择源批次"; return; }
        if (string.IsNullOrWhiteSpace(Paper)) { Message = "请先新建调仓单（或输入未确认单号）"; return; }
        IsBusy = true;
        try
        {
            await Db.StockAdjust.AddLineAsync(Paper, new AddStockAdjustLineCommand
            {
                Kcid = SelectedBatch.Id,
                Ckamount = Qty,
                Depotno1 = Depotno1.Trim(),
                Posi1 = Posi1.Trim(),
                Oprc = SelectedBatch.Oprc,
            }, AppSession.DeptNo, AppSession.Operator, ct);
            await LoadLinesAsync(Paper, ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "添加失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>修改明细行（-_.row168：oprc/depotno1/posi1/ckamount/per）。</summary>
    [RelayCommand]
    private async Task SaveLineAsync(CancellationToken ct)
    {
        Message = "";
        if (SelectedLine is null) { Message = "请先在明细中选择要修改的行"; return; }
        IsBusy = true;
        try
        {
            await Db.StockAdjust.UpdateLineAsync(new UpdateStockAdjustLineCommand
            {
                Id = SelectedLine.Id,
                Ckamount = SelectedLine.Ckamount,
                Depotno1 = SelectedLine.Depotno1?.Trim() ?? "",
                Posi1 = SelectedLine.Posi1?.Trim() ?? "",
                Oprc = SelectedBatch?.Oprc ?? 0m,
            }, AppSession.Operator, ct);
            Message = "明细已保存";
            await LoadLinesAsync(Paper, ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "保存失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>删除明细行（__.row10）。</summary>
    [RelayCommand]
    private async Task DeleteLineAsync(CancellationToken ct)
    {
        Message = "";
        if (SelectedLine is null) { Message = "请先在明细中选择要删除的行"; return; }
        IsBusy = true;
        try
        {
            await Db.StockAdjust.DeleteLineAsync(SelectedLine.Id, ct);
            await LoadLinesAsync(Paper, ct);
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

    /// <summary>整单删除（__.row9：m='*' 已确认拒删）。</summary>
    [RelayCommand]
    private async Task DeletePaperAsync(CancellationToken ct)
    {
        Message = "";
        if (string.IsNullOrWhiteSpace(Paper)) { Message = "请先选择调仓单"; return; }
        if (MessageBox.Show($"确认整单删除调仓单 [{Paper}] 吗？", "询问", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        IsBusy = true;
        try
        {
            var n = await Db.StockAdjust.DeletePaperAsync(Paper, ct);
            Message = $"已删除调仓单 {Paper}（{n} 行）";
            Paper = "";
            Lines.Clear();
            await ReloadPapersAsync(ct);
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

    /// <summary>确认调仓（__.row11）：校验源库存 → tbisto 源减/目标加 → m='*'。</summary>
    [RelayCommand]
    private async Task ConfirmAsync(CancellationToken ct)
    {
        Message = "";
        if (string.IsNullOrWhiteSpace(Paper)) { Message = "请先选择调仓单"; return; }
        if (MessageBox.Show("是否确认调仓？", "询问", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        IsBusy = true;
        try
        {
            var r = await Db.StockAdjust.ConfirmAsync(Paper, AppSession.DeptNo, ct);
            Message = r.LostLines > 0
                ? $"调仓完毕：成功 {r.ConfirmedLines} 行，失败 {r.LostLines} 行（原因可能是你修改过原来的仓位/库号/人进价，或原库存量不足）"
                : "调仓完毕";
            await LoadLinesAsync(Paper, ct);
            await ReloadPapersAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "确认失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
