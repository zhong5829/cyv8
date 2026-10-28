using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 每日动态盘点 VM（B.C；旧菜单权限码复用 BB）。重建契约（转储缺查询 SQL，只读）：
/// 日期窗默认昨天~今天，展示当前批次进仓数/出仓数/未确认数/急件未冲减数/批次留痕，打印 re_dtpd 列序。
/// </summary>
public partial class DailyStocktakeViewModel : ObservableObject
{
    public ObservableCollection<DailyStocktakeRow> Rows { get; } = [];

    [ObservableProperty] private DateTime _dateFrom = DateTime.Today.AddDays(-1);
    [ObservableProperty] private DateTime _dateTo = DateTime.Today;
    [ObservableProperty] private bool _allCp = false;   // 厂牌：默认当前（旧 fr_dtpd 契约"默认当前 P_CP"）
    [ObservableProperty] private string _cp = AppSession.Brand;
    [ObservableProperty] private string _depotno = "";
    [ObservableProperty] private string _posi = "";
    [ObservableProperty] private string _nnoPrefix = "";
    [ObservableProperty] private string _na1Prefix = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var rows = await Db.DailyStocktake.QueryAsync(new DailyStocktakeQuery
            {
                DateFrom = DateFrom,
                DateTo = DateTo,
                Cp = AllCp ? null : Cp,
                Depotno = Depotno,
                Posi = Posi,
                NnoPrefix = NnoPrefix,
                Na1Prefix = Na1Prefix,
            }, AppSession.DeptNo, ct);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Message = $"共 {rows.Count} 行";
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

    /// <summary>打印（旧 PG_PRINT('re_dtpd') 列序：仓位/编码/库存数/名称/入库数/出库数/车型）。</summary>
    [RelayCommand]
    private void Print(object? parameter)
    {
        if (parameter is not System.Windows.Controls.DataGrid grid || Rows.Count == 0) return;
        try
        {
            var dialog = new System.Windows.Controls.PrintDialog();
            if (dialog.ShowDialog() != true) return;
            dialog.PrintVisual(grid, $"每日动态盘点表 {DateTo:yyyy-MM-dd}");
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打印失败：{ex.Message}", "每日动态盘点", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
