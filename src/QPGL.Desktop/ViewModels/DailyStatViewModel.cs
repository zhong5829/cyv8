using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Report;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 日营业统计 VM（D1/D2，二期）。直调 IStatisticsRepository（fr_rtj row5 / fr_hftj 0_.row5 契约）：
/// 按日聚合网格 → 统计确认（写 tbysyf 应收应付 + tbsada/tbistoed type='历史' + jzdate）→ 恢复未统计。
/// </summary>
public partial class DailyStatViewModel : ObservableObject
{
    public ObservableCollection<DailyStatRow> Rows { get; } = [];

    [ObservableProperty] private DailyStatRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            await ReloadAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync(CancellationToken ct)
    {
        Message = "";
        try
        {
            var rows = await Db.Statistics.GetDailyStatAsync(AppSession.DeptNo, ct);
            Rows.Clear();
            foreach (var r in rows)
                Rows.Add(r);
        }
        catch (Exception ex)
        {
            Message = "加载失败：" + ex.Message;
        }
    }

    /// <summary>统计确认（D1）：选中日期写 tbysyf 并翻转型标志。</summary>
    [RelayCommand]
    private async Task PostAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选中统计日期"; return; }
        if (MessageBox.Show($"确定统计 {SelectedRow.Outdate:yyyy-MM-dd} 的日营业数据吗？\n（当日销售/入库单将标记为已统计并生成应收应付行）",
                "询问", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Message = "";
        IsBusy = true;
        try
        {
            var r = await Db.Statistics.PostDailyStatAsync(SelectedRow.Outdate, AppSession.DeptNo, AppSession.Operator, ct);
            Message = $"统计完毕：销售组 {r.SalesGroups}（应收 {r.YsyfSaleRows} 行），入库组 {r.InStockGroups}（应付 {r.YsyfInRows} 行）";
            await ReloadAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is QPGL.Domain.Common.AppException ae ? ae.Message : "统计失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>恢复未统计（D2）。</summary>
    [RelayCommand]
    private async Task UnpostAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选中日期"; return; }
        if (MessageBox.Show($"确定恢复 {SelectedRow.Outdate:yyyy-MM-dd} 为未统计吗？\n（已收款行受保护，未收款应收行将删除）",
                "询问", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Message = "";
        IsBusy = true;
        try
        {
            await Db.Statistics.UnpostDailyStatAsync(SelectedRow.Outdate, AppSession.DeptNo, AppSession.Operator, ct);
            Message = "已恢复未统计";
            await ReloadAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is QPGL.Domain.Common.AppException ae ? ae.Message : "恢复失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
