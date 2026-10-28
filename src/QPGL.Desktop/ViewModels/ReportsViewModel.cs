using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Report;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 统计报表 VM（D3 日报表 / D4 月销售数统计 / D5 进销存分类统计，二期）。
/// 直调 IReportRepository（fr_dyrbb row3 / fr_ytj row35+_..row40 / fr_jxc row3 契约）。
/// </summary>
public partial class ReportsViewModel : ObservableObject
{
    public ObservableCollection<DailyReportSaleRow> DailySales { get; } = [];
    public ObservableCollection<DailyReportSaleRow> DailySettled { get; } = [];
    public ObservableCollection<DailyReportInRow> DailyInStock { get; } = [];
    public ObservableCollection<DailyReportSaleRow> DailyTransfers { get; } = [];
    public ObservableCollection<MonthlySaleRow> MonthlyRows { get; } = [];
    public ObservableCollection<JxcCategoryRow> JxcRows { get; } = [];
    public ObservableCollection<QPGL.Domain.Report.JxcDailyRow> JxcDailyRows { get; } = [];
    public ObservableCollection<QPGL.Domain.Report.CustomerAnalysisMonthRow> NewCustomerMonths { get; } = [];
    public ObservableCollection<QPGL.Domain.Report.CustomerAnalysisDetailRow> NewCustomerDetails { get; } = [];
    public ObservableCollection<QPGL.Domain.Report.CustomerAnalysisMonthRow> LostCustomerMonths { get; } = [];
    public ObservableCollection<QPGL.Domain.Report.CustomerAnalysisDetailRow> LostCustomerDetails { get; } = [];

    [ObservableProperty] private DateTime _dailyDate = DateTime.Today;
    [ObservableProperty] private DateTime _jxcD0 = DateTime.Today.AddDays(-1);
    [ObservableProperty] private DateTime _jxcD1 = DateTime.Today;
    [ObservableProperty] private DateTime _custD1 = new(DateTime.Today.Year - 1, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime _custD2 = DateTime.Today;
    [ObservableProperty] private int _lostThresholdDays = 365;
    [ObservableProperty] private string _monthlyCp = AppSession.Brand;   // 旧 非全部厂牌时 a.cp=?p_cp（库存第十批契约）
    [ObservableProperty] private string _monthlyKeyword = "";
    [ObservableProperty] private int _monthlyYear = DateTime.Today.Year;
    [ObservableProperty] private int _monthlyMonth = DateTime.Today.Month;
    [ObservableProperty] private bool _monthlyIncludeTransfer;
    [ObservableProperty] private bool _monthlyOnlyPositive;
    [ObservableProperty] private bool _monthlyForce;
    [ObservableProperty] private DateTime _jxcBdate = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            await LoadDailyAsync(ct);
            await LoadMonthlyAsync(ct);
            await LoadJxcAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    /// <summary>日报表（D3）：当日销售/结账销售/入库/调拨。</summary>
    [RelayCommand]
    private async Task LoadDailyAsync(CancellationToken ct)
    {
        Message = "";
        try
        {
            var r = await Db.Reports.GetDailyReportAsync(DailyDate, AppSession.DeptNo, ct);
            Fill(DailySales, r.Sales);
            Fill(DailySettled, r.Settled);
            Fill(DailyInStock, r.InStock);
            Fill(DailyTransfers, r.Transfers);
        }
        catch (Exception ex) { Message = "日报表加载失败：" + ex.Message; }
    }

    /// <summary>月销售数统计（D4）：tbrtsamo 矩阵查询。</summary>
    [RelayCommand]
    private async Task LoadMonthlyAsync(CancellationToken ct)
    {
        Message = "";
        try
        {
            var rows = await Db.Reports.GetMonthlySalesAsync(MonthlyCp.Trim(), MonthlyKeyword, ct);
            Fill(MonthlyRows, rows);
        }
        catch (Exception ex) { Message = "月销售统计加载失败：" + ex.Message; }
    }

    /// <summary>月末处理（D4 写路径）：tbsada/tbxlsa 按月汇总写 tbrtsamo。</summary>
    [RelayCommand]
    private async Task PostMonthlyAsync(CancellationToken ct)
    {
        if (MessageBox.Show($"确定处理 {MonthlyYear} 年 {MonthlyMonth} 月销售统计吗？\n（该月列将清零后按单据重算）",
                "询问", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Message = "";
        IsBusy = true;
        try
        {
            await Db.Reports.PostMonthlySalesAsync(MonthlyYear, MonthlyMonth, AppSession.Operator,
                MonthlyIncludeTransfer, MonthlyOnlyPositive, MonthlyForce, ct);
            Message = $"月末处理完毕：{MonthlyYear}-{MonthlyMonth:D2}";
            await LoadMonthlyAsync(ct);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "月末处理失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>进销存分类统计（D5）：按厂牌汇总 + 合计。</summary>
    [RelayCommand]
    private async Task LoadJxcAsync(CancellationToken ct)
    {
        Message = "";
        try
        {
            var rows = await Db.Reports.GetJxcCategoryAsync(JxcBdate, ct);
            Fill(JxcRows, rows);
        }
        catch (Exception ex) { Message = "进销存统计加载失败：" + ex.Message; }
    }

    /// <summary>进销存统计·日汇总（D6）：期初快照 + 六方向流水。</summary>
    [RelayCommand]
    private async Task LoadJxcDailyAsync(CancellationToken ct)
    {
        Message = "";
        try
        {
            var rows = await Db.Reports.GetJxcDailyAsync(JxcD0, JxcD1, MonthlyCp.Trim(), AppSession.DeptNo, null, 1, ct);
            Fill(JxcDailyRows, rows);
        }
        catch (Exception ex) { Message = "进销存统计加载失败：" + ex.Message; }
    }

    /// <summary>新增客户分析（D7）：首单月计数。</summary>
    [RelayCommand]
    private async Task LoadNewCustomersAsync(CancellationToken ct)
    {
        Message = "";
        try
        {
            var (months, details) = await Db.Reports.GetNewCustomersAsync(CustD1, CustD2, ct);
            Fill(NewCustomerMonths, months);
            Fill(NewCustomerDetails, details);
        }
        catch (Exception ex) { Message = "新增客户分析失败：" + ex.Message; }
    }

    /// <summary>流失客户分析（D8）：末单+阈值天判定。</summary>
    [RelayCommand]
    private async Task LoadLostCustomersAsync(CancellationToken ct)
    {
        Message = "";
        try
        {
            var (months, details) = await Db.Reports.GetLostCustomersAsync(CustD1, CustD2, LostThresholdDays, ct);
            Fill(LostCustomerMonths, months);
            Fill(LostCustomerDetails, details);
        }
        catch (Exception ex) { Message = "流失客户分析失败：" + ex.Message; }
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
