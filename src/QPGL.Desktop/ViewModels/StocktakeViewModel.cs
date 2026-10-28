using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>盘点明细行（可编辑实盘数/实盘仓位）。</summary>
public partial class StocktakeLineRow : ObservableObject
{
    public int Id { get; init; }
    public int Kcid { get; init; }
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Fa { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Depotno { get; init; } = "";
    public string Posi { get; init; } = "";
    public decimal Kcamount { get; init; }        // 账面数
    public string Note { get; init; } = "";
    public int Mark { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Nyk))]
    private decimal _npd;     // 实盘数（可编辑）

    [ObservableProperty] private string _pdposi = ""; // 实盘仓位（可编辑）

    public decimal Nyk => Npd - Kcamount;          // 盈亏 = 实盘 - 账面（row95 语义）
}

/// <summary>
/// 库存盘点 VM（B.8，二期）。直调 IStocktakeRepository（fr_istopd 契约）：
/// 生成盘点单（tbisto 快照拷贝）→ 录实盘 → 调仓位（tbistoch）→ 确认（nyk&lt;&gt;0 留历史）→
/// 生成散件入库草稿（GYNO='PDRK'，盘盈/盘亏走散件入库生效）。
/// </summary>
public partial class StocktakeViewModel : ObservableObject
{
    public ObservableCollection<StocktakePaper> OpenPapers { get; } = [];
    public ObservableCollection<StocktakeLineRow> Lines { get; } = [];

    [ObservableProperty] private StocktakePaper? _selectedPaper;
    [ObservableProperty] private string _nnoPrefix = "";
    [ObservableProperty] private string _na1Contains = "";
    [ObservableProperty] private string _depotno = "";
    [ObservableProperty] private string _currentPaper = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    public StocktakeLineRow? SelectedLine { get; set; }

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            await ReloadPapersAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    private async Task ReloadPapersAsync(CancellationToken ct)
    {
        OpenPapers.Clear();
        foreach (var p in await Db.Stocktake.GetOpenPapersAsync(AppSession.DeptNo, AppSession.Brand, ct))
            OpenPapers.Add(p);
    }

    /// <summary>生成盘点单（row114）：tbisto 快照拷贝，npd=账面、nyk=0、mark=0。</summary>
    [RelayCommand]
    private async Task GeneratePaperAsync(CancellationToken ct)
    {
        Message = "";
        IsBusy = true;
        try
        {
            var paper = await Db.Stocktake.GeneratePaperAsync(new GenerateStocktakeCommand
            {
                Cp = AppSession.Brand,          // 旧 fr_istopd row114：cp=?p_cp 按当前厂牌盘
                NnoPrefix = NnoPrefix.Trim(),
                Na1Contains = Na1Contains.Trim(),
                Depotno = Depotno.Trim(),
            }, AppSession.DeptNo, AppSession.Operator, ct);
            Message = $"盘点单 {paper} 已生成（{Lines.Count} 行）";
            CurrentPaper = paper;
            SelectedPaper = OpenPapers.FirstOrDefault(p => p.Pdpaper == paper);
            await ReloadPapersAsync(ct);
            SelectedPaper = OpenPapers.FirstOrDefault(p => p.Pdpaper == paper);
            await LoadLinesAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Message = "生成失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>切换单据加载明细。</summary>
    [RelayCommand]
    public async Task LoadLinesAsync(CancellationToken ct)
    {
        var paper = SelectedPaper?.Pdpaper ?? CurrentPaper;
        if (string.IsNullOrWhiteSpace(paper)) return;
        try
        {
            var lines = await Db.Stocktake.GetLinesAsync(paper, ct);
            Lines.Clear();
            foreach (var l in lines)
                Lines.Add(new StocktakeLineRow
                {
                    Id = l.Id, Kcid = l.Kcid, Cp = l.Cp, Nno = l.Nno, Na1 = l.Na1, Ty = l.Ty, Fa = l.Fa,
                    Unit = l.Unit, Depotno = l.Depotno, Posi = l.Posi, Kcamount = l.Kcamount,
                    Note = l.Note, Mark = l.Mark, Npd = l.Npd, Pdposi = l.Pdposi,
                });
            CurrentPaper = paper;
        }
        catch (Exception ex)
        {
            Message = "加载明细失败：" + ex.Message;
        }
    }

    /// <summary>保存实盘（row95）：pdposi/npd/nyk=npd-账面。</summary>
    [RelayCommand]
    private async Task SaveCountsAsync(CancellationToken ct)
    {
        if (Lines.Count == 0) { Message = "请先选择盘点单"; return; }
        Message = "";
        IsBusy = true;
        try
        {
            var saved = 0;
            foreach (var l in Lines)
            {
                saved += await Db.Stocktake.SaveCountLineAsync(new SaveStocktakeLineCommand
                {
                    Id = l.Id, Npd = l.Npd, Pdposi = l.Pdposi?.Trim() ?? "",
                }, ct);
            }
            Message = $"实盘已保存（{saved} 行）；盈亏已按 实盘-账面 重算";
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

    /// <summary>调整仓位（鐶.row47）：tbistopd.posi + tbisto.posi（kcid）+ tbistoch 历史。</summary>
    [RelayCommand]
    private async Task AdjustPosiAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentPaper)) { Message = "请先选择盘点单"; return; }
        Message = "";
        IsBusy = true;
        try
        {
            var n = await Db.Stocktake.AdjustPosiAsync(CurrentPaper, AppSession.Operator, ct);
            Message = n > 0 ? $"已更新 {n} 行库存仓位（tbistoch 已留修改记录）" : "没有仓位被修改";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "调整失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>确认盘点单（row49）：nyk&lt;&gt;0 → zt='1',mark='1'；nyk=0 → 删除。</summary>
    [RelayCommand]
    private async Task ConfirmAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentPaper)) { Message = "请先选择盘点单"; return; }
        if (MessageBox.Show($"是否确认盘点单：{CurrentPaper}？\n（盈亏为零的行将被删除，盈亏行转入历史）",
                "询问", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Message = "";
        IsBusy = true;
        try
        {
            var r = await Db.Stocktake.ConfirmAsync(CurrentPaper, ct);
            Message = $"确认完毕：盈亏行 {r.MarkedLines} 行已转历史，零差行 {r.DeletedLines} 行已删除。请到散件入库中生成/生效入库单";
            Lines.Clear();
            CurrentPaper = "";
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

    /// <summary>生成散件入库草稿（row92）：nyk&lt;&gt;0 行 → tbistosm（GYNO='PDRK'），已存在则更新。</summary>
    [RelayCommand]
    private async Task GenInStockDraftAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentPaper)) { Message = "请先选择盘点单"; return; }
        Message = "";
        IsBusy = true;
        try
        {
            var r = await Db.Stocktake.GenerateInStockDraftAsync(CurrentPaper, AppSession.Operator, ct);
            Message = r.InsertedLines > 0
                ? $"已生成散件入库单 {r.Cno}（{r.InsertedLines} 行），请到散件入库中进行入库生效"
                : $"已更新散件入库单 {r.Cno}（{r.UpdatedLines} 行），请到散件入库中查证";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "生成失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
