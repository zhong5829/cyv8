using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 收支流水帐 VM（G.5，fr_lsz 契约：契约摘录/出纳管理G组契约.md §3）。
/// 查询=期初金额+本期明细+本期小计+期末小计+总计（哨兵行应用层聚合，LJ 顺序累计）；
/// 手工行增改删（usem1 I/A/J）、删除之前全部、初始累计数。
/// 已知偏差：SK/FK 收付款流水删除拦截照旧。提取销售/入库已接入（第十七批，肱.row133 单事务备案）。
/// </summary>
public partial class LszViewModel : ObservableObject
{
    public ObservableCollection<LszRow> Rows { get; } = [];
    public ObservableCollection<string> BankNames { get; } = [];

    [ObservableProperty] private string? _bankName;
    [ObservableProperty] private DateTime _dateFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime _dateTo = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
    [ObservableProperty] private LszRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canAdd;
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private bool _canDelete;

    public LszViewModel()
    {
        // Init（row3 L5-10）：usem1 I=新增 A=修改 J=删除（'@'/'$' 全放开）
        var super = AppSession.Permission.IsSuper || AppSession.Permission.IsDataAdmin;
        CanAdd = super || AppSession.Permission.HasFlag('I');
        CanEdit = super || AppSession.Permission.HasFlag('A');
        CanDelete = super || AppSession.Permission.HasFlag('J');
    }

    /// <summary>去向下拉（_2.row28 Init：tbsysset bankname ORDER BY name DESC）。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var banks = await Db.Lsz.GetBankNamesAsync(ct);
        BankNames.Clear();
        foreach (var b in banks) BankNames.Add(b);
    }

    /// <summary>查询（setsource）。</summary>
    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(BankName)) { Message = "请先选择收款去向！"; return; }
        try
        {
            IsBusy = true;
            Message = "";
            var rows = await Db.Lsz.GetSheetAsync(BankName, DateFrom, DateTo, ct);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Message = $"共 {rows.Count(r => !r.IsSentinel)} 笔流水";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(BankName)) { Message = "请先选择收款去向！"; return; }
        var dlg = new Views.LszEditWindow("新增流水", BankName, null);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        try
        {
            await Db.Lsz.AddManualAsync(dlg.Result, BankName, AppSession.DeptNo, ct);
            Message = "流水已新增";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task EditAsync(CancellationToken ct = default)
    {
        if (SelectedRow is null || SelectedRow.IsSentinel) { Message = "请选择流水明细行"; return; }
        var dlg = new Views.LszEditWindow("修改流水", BankName!, SelectedRow);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        try
        {
            await Db.Lsz.UpdateManualAsync(SelectedRow.Id, dlg.Result, BankName!, AppSession.DeptNo, ct);
            Message = "流水已修改";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteAsync(CancellationToken ct = default)
    {
        if (SelectedRow is null || SelectedRow.IsSentinel) { Message = "没有记录，请先选择收款去向！"; return; }
        try
        {
            if (!await Db.Lsz.CanDeleteAsync(SelectedRow.Id, ct))
            {
                Message = string.IsNullOrEmpty(SelectedRow.Paper)
                    ? "不能删除此流水帐记录"
                    : "不能删除此流水帐记录";
                return;
            }
            if (MessageBox.Show("确实要删除记录吗？", "收支流水帐",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await Db.Lsz.DeleteAsync(SelectedRow.Id, ct);
            Message = "流水已删除";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>删除当前行之前的所有可删行（鐷.row36）。</summary>
    [RelayCommand]
    private async Task DeleteBeforeAsync(CancellationToken ct = default)
    {
        if (SelectedRow is null) { Message = "没有记录，请先选择收款去向！"; return; }
        if (MessageBox.Show("确实要删除记录之前的所有记录吗？", "收支流水帐",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Db.Lsz.DeleteBeforeAsync(SelectedRow.Id, BankName!, SelectedRow.Lj, ct);
            Message = "已删除之前的流水";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>初始累计数（Uz.row69）。</summary>
    [RelayCommand]
    private async Task AddInitialAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(BankName)) { Message = "请先选择收款去向！"; return; }
        var dlg = new Views.LszInitialWindow();
        if (dlg.ShowDialog() != true) return;
        try
        {
            await Db.Lsz.AddInitialAsync(BankName, dlg.Amount, AppSession.DeptNo, ct);
            Message = "初始累计数已保存";
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>提取销售/入库（fr_lsz 内嵌 CMDTIQU 面板，谛.row139/肱.row133）：候选勾选 → 批量写 tblsz。</summary>
    [RelayCommand]
    private async Task ExtractAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(BankName)) { Message = "请先选择收款去向！"; return; }
        var win = new Views.LszTiquWindow(BankName) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.InsertedCount == 0) return;
        Message = $"已提取 {win.InsertedCount} 条流水";
        await SearchAsync(ct);
    }
}
