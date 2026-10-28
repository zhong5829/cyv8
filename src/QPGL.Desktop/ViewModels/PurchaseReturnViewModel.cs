using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>退货车行（c_th 语义：源行快照 + 五个可编辑列）。</summary>
public partial class PurchaseCartRow : ObservableObject
{
    public int SourceId { get; init; }
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Unit { get; init; } = "";
    /// <summary>原进库数。</summary>
    public decimal Oamount { get; init; }
    public decimal Kshu { get; init; }
    public string Cno { get; init; } = "";
    public decimal Oprc { get; init; }
    public string Note { get; init; } = "";
    public decimal IprcOld { get; init; }

    [ObservableProperty] private decimal _amount;      // 退库数（可编辑）
    [ObservableProperty] private decimal _iprj;         // 外币价（可编辑；BA01 锁）
    [ObservableProperty] private decimal _iprc;         // 进价（可编辑；BA01 锁）
    [ObservableProperty] private string _posi = "";     // 仓位（可编辑）
    [ObservableProperty] private string _depotno = "";  // 库号（可编辑）
}

/// <summary>
/// 入库退货 VM（B.A，权限码 BA；fr_istk 契约）。
/// 拉单（tbistoed 可退流水）→ 退货车（可编辑数量/价格/仓位/库号）→ 确认（GETNEWDH 散件入库单 + 负数流水 + ISAPPE 冲减）。
/// BA01 细码：有此权限且非超级 → 进价/外币价只读（旧 __.row108 语义）。
/// </summary>
public partial class PurchaseReturnViewModel : ObservableObject
{
    public ObservableCollection<ReturnableLine> Lines { get; } = [];
    public ObservableCollection<PurchaseCartRow> Cart { get; } = [];
    public ObservableCollection<string> FkfsOptions { get; } = [];
    public ObservableCollection<string> ReasonOptions { get; } = [];
    public ObservableCollection<string> DepotOptions { get; } = [];

    [ObservableProperty] private string _cno = "";
    [ObservableProperty] private string _nnoPrefix = "";
    [ObservableProperty] private string _na1Prefix = "";
    [ObservableProperty] private string _gyname = "";
    [ObservableProperty] private DateTime? _inDateFrom;
    [ObservableProperty] private DateTime? _inDateTo;
    [ObservableProperty] private string? _selectedFkfs;
    [ObservableProperty] private string? _selectedReason;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private decimal _totalIprj;
    [ObservableProperty] private decimal _totalIprc;

    public ReturnableLine? SelectedLine { get; set; }
    public PurchaseCartRow? SelectedCart { get; set; }

    /// <summary>BA01 锁价：旧 __.row108——'@' 超级或无 BA01 → 可编辑；有 BA01 且非超级 → 只读。</summary>
    public bool PriceLocked => AppSession.Permission.HasMenu("BA01") && !AppSession.Permission.IsSuper;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            foreach (var f in await Db.PurchaseReturns.GetSyssetNamesAsync("guno", ct)) FkfsOptions.Add(f);
            foreach (var r in await Db.PurchaseReturns.GetSyssetNamesAsync("退货原因", ct)) ReasonOptions.Add(r);
            foreach (var d in await Db.PurchaseReturns.GetSyssetNamesAsync("库号", ct)) DepotOptions.Add(d);
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var rows = await Db.PurchaseReturns.SearchReturnableAsync(new PurchaseReturnQuery
            {
                Cno = Cno,
                NnoPrefix = NnoPrefix,
                Na1Prefix = Na1Prefix,
                Gyname = Gyname,
                InDateFrom = InDateFrom,
                InDateTo = InDateTo,
            }, AppSession.DeptNo, ct);
            Lines.Clear();
            foreach (var r in rows) Lines.Add(r);
            if (rows.Count == 0) Message = "未找到符合条件的入库流水（不含调拨入库：调拨退货须对方开调拨退货单）";
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

    /// <summary>添加到退货车（旧编辑校验前置：数量=可退数默认值，价格/仓位/库号取源行）。</summary>
    [RelayCommand]
    private void AddToCart()
    {
        if (SelectedLine is null) return;
        var s = SelectedLine;
        if (!string.Equals(s.Dpm.Trim(), AppSession.DeptNo.Trim(), StringComparison.Ordinal))
        {
            MessageBox.Show($"当前分店是 {AppSession.DeptNo.Trim()}, 入库历史是 {s.Dpm.Trim()}, 分店不一致不能退货.",
                "入库退货", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Cart.Any(c => c.SourceId == s.Id))
        {
            MessageBox.Show("该流水已在退货车中。", "入库退货", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Cart.Add(new PurchaseCartRow
        {
            SourceId = s.Id, Cp = s.Cp, Nno = s.Nno, Na1 = s.Na1, Ty = s.Ty, Unit = s.Unit,
            Oamount = s.Oamount, Kshu = s.Kshu, Cno = s.Cno, Oprc = s.Oprc, Note = s.Note, IprcOld = s.IprcOld,
            Amount = s.Amount, Iprj = s.Iprj, Iprc = s.Iprc, Posi = s.Posi, Depotno = s.Depotno,
        });
        RecalcTotals();
    }

    [RelayCommand]
    private void RemoveCart()
    {
        if (SelectedCart is null) return;
        Cart.Remove(SelectedCart);
        RecalcTotals();
    }

    [RelayCommand]
    private void ClearCart()
    {
        Cart.Clear();
        RecalcTotals();
    }

    private void RecalcTotals()  // 旧 heji：Σ IPRJ×AMOUNT、Σ IPRC×AMOUNT
    {
        TotalIprj = Cart.Sum(c => c.Iprj * c.Amount);
        TotalIprc = Cart.Sum(c => c.Iprc * c.Amount);
    }

    [RelayCommand]
    public async Task ConfirmAsync(CancellationToken ct)
    {
        if (Cart.Count == 0) return;
        if (string.IsNullOrWhiteSpace(SelectedFkfs))
        {
            MessageBox.Show("请选择退款方式!", "入库退货", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show("确认退货吗？", "入库退货", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            IsBusy = true;
            Message = "";
            var result = await Db.PurchaseReturns.ConfirmAsync(new ConfirmPurchaseReturnCommand
            {
                Fkfs = SelectedFkfs!,
                ReturnReason = SelectedReason ?? "",
                Lines = Cart.Select(c => new PurchaseReturnLineInput
                {
                    SourceId = c.SourceId, Amount = c.Amount, Iprj = c.Iprj,
                    Iprc = c.Iprc, Posi = c.Posi, Depotno = c.Depotno,
                }).ToList(),
            }, AppSession.DeptNo, AppSession.Operator, ct);

            MessageBox.Show($"退货完成，退库单号 {result.Cno}，共 {result.LineCount} 行。",
                "入库退货", MessageBoxButton.OK, MessageBoxImage.Information);
            Cart.Clear();
            RecalcTotals();
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "入库退货", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
