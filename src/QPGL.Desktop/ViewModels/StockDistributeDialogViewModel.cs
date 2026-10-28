using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;

namespace QPGL.Desktop.ViewModels;

/// <summary>分货对话框勾选行。</summary>
public partial class StockDistributeLine : ObservableObject
{
    public string Gname { get; init; } = "";
    public string Cno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public decimal Oqty { get; init; }
    public decimal Fqty { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 库存分货对话框 VM（fr_kcfh row23，并入库存分货入口=调拨管理页按钮）。
/// 权限：OPGSELE=3 需 P_USEM1 '~'，=2 需 '-'（人进价）——沿用 SaleControl 调拨页已过 C3 Guard，
/// 模式级权限按备案由 AppSession 权限串承接（'~'/'-' 均在 P_USEM1，非 C3 范畴，此处不再拦截）。
/// </summary>
public partial class StockDistributeDialogViewModel : ObservableObject
{
    public ObservableCollection<StockDistributeLine> Lines { get; } = [];

    [ObservableProperty] private string _customerFilter = "";
    [ObservableProperty] private string _cnoFilter = "";
    [ObservableProperty] private int _mode = 1;
    [ObservableProperty] private int _roundMode = 1;
    [ObservableProperty] private string _oprcField = "iprc";
    [ObservableProperty] private decimal _oprcFactor = 1m;
    [ObservableProperty] private string _depotno = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    public bool Success { get; private set; }
    public StockDistributeResult? Result { get; private set; }

    [RelayCommand]
    public async System.Threading.Tasks.Task SearchAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        Message = "";
        try
        {
            var rows = await Db.StockDistribute.GetOrdersAsync(
                AppSession.DeptNo, CustomerFilter.Trim(), CnoFilter.Trim(), ct);
            Lines.Clear();
            foreach (var r in rows)
                Lines.Add(new StockDistributeLine
                {
                    Gname = r.Gname, Cno = r.Cno, Na1 = r.Na1, Oqty = r.Oqty, Fqty = r.Qty,
                    IsSelected = true
                });
            Message = rows.Count == 0 ? "没有未满足的订单（oqty-qty<=0 或无 gyname 匹配）" : $"共 {rows.Count} 行待分货";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>row23 执行分货：按勾选订单生成调拨单，结果由对话框调用方展示。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ExecuteAsync(CancellationToken ct)
    {
        var selected = Lines.Where(l => l.IsSelected)
            .Select(l => new StockDistributeOrderRef(l.Gname, l.Cno))
            .Distinct()
            .ToList();
        try
        {
            var command = new StockDistributeCommand
            {
                Mode = Mode,
                RoundMode = RoundMode,
                OprcField = OprcField,
                OprcFactor = OprcFactor,
                Depotno = string.IsNullOrWhiteSpace(Depotno) ? null : Depotno.Trim(),
                Orders = selected
            };
            command.Validate();
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            return;
        }
        IsBusy = true;
        Message = "";
        try
        {
            var pDpmname = await GetDeptNameAsync(ct);
            Result = await Db.StockDistribute.DistributeAsync(
                new StockDistributeCommand
                {
                    Mode = Mode, RoundMode = RoundMode, OprcField = OprcField, OprcFactor = OprcFactor,
                    Depotno = string.IsNullOrWhiteSpace(Depotno) ? null : Depotno.Trim(), Orders = selected
                },
                AppSession.DeptNo, AppSession.DeptNo, pDpmname, AppSession.Operator, ct);
            Success = Result.LineCount > 0;
            Message = Success
                ? $"分货完成，生成调拨单 {Result.FirstPaper} 至 {Result.LastPaper}，共 {Result.LineCount} 行"
                : "库存不足，没有生成调拨单！";
        }
        catch (AppException ex) { Message = ex.Message; }
        catch (Exception ex) { Message = "分货失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>部门名（旧 P_DPMNAME）：tbdpm dpmbh→dpm。</summary>
    private static async Task<string> GetDeptNameAsync(CancellationToken ct)
    {
        await using var conn = await Db.Factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT RTRIM(dpm) FROM tbdpm WITH (NOLOCK) WHERE dpmbh = @dpm",
            new { dpm = new DbString { Value = AppSession.DeptNo, IsAnsi = true, Length = 10 } },
            cancellationToken: ct)) ?? "";
    }
}
