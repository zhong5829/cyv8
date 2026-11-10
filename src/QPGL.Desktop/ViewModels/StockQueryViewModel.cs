﻿using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Infrastructure;
using QPGL.Desktop.Views;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 库存查询 VM（E3，M4-2）+ 库存修改入口（B3，M4-3）。
/// 直调 IStockRepository / IStockEditRepository（复刻 fr_istoch 查询/修改/删除契约）。
/// 2026-11-09 对齐 fr_seisto：警戒上下限两列 + 警戒红/黄行色、合计（MR.row41 四项汇总）、
/// 总批发价金额、默认排序 nno；条件区只留 零件号/名称/车型 三框（UI 防抖实时查询，高级条件已按用户要求移除）。
/// </summary>
public partial class StockQueryViewModel : ObservableObject
{
    public ObservableCollection<StockRow> Rows { get; } = [];

    // 2026-11-09 用户需求：条件区只保留 零件号/名称/车型 三框（销售退货页三条件查询区模式）；
    // 厂牌 cp 固定取会话厂牌，货位/仓库/仅看有库存走高级条件或默认值
    [ObservableProperty] private string _nnoFilter = "";
    [ObservableProperty] private string _na1Filter = "";
    [ObservableProperty] private string _tyFilter = "";

    [ObservableProperty] private StockRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _total;
    [ObservableProperty] private decimal _sumKcamount;
    /// <summary>总进价金额 = Σ(iprc×数量)（不含税净成本，2026-11-08 用户确认 P1=方案 b；原取 Σ(iprj×数量) 含税总额）。</summary>
    [ObservableProperty] private decimal _sumIprcAmount;
    /// <summary>总批发价金额 = Σ(iprj×数量)（fr_seisto MR.row41 汇总第 2 项）。</summary>
    [ObservableProperty] private decimal _sumIprjAmount;
    [ObservableProperty] private decimal _sumOprcAmount;

    private string? _sort;
    private bool _sortDesc;

    private const int PageSize = 50;

    /// <summary>是否还有更多批次（无限下滑加载模式，2026-11-09 取代分页按钮）。</summary>
    public bool HasMore => Rows.Count < Total;

    /// <summary>加载状态（底部状态条）。</summary>
    public string LoadStatus => $"已加载 {Rows.Count} / 共 {Total} 行";

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        Page = 1;
        await SearchCoreAsync(ct, append: false);
    }

    /// <summary>下滑到底加载下一批（StockQueryControl ScrollChanged 触发；汇总只随新查询重算，翻批不重算）。</summary>
    public async Task LoadMoreAsync(CancellationToken ct)
    {
        if (IsBusy || !HasMore) return;
        Page++;
        await SearchCoreAsync(ct, append: true);
    }

    private async Task SearchCoreAsync(CancellationToken ct, bool append)
    {
        Message = "";
        IsBusy = true;
        try
        {
            var request = BuildRequest();
            var result = await Db.Stock.SearchAsync(request, AppSession.DeptNo, ct);
            if (!append) Rows.Clear();
            foreach (var r in result.Items) Rows.Add(r);
            Total = result.Total;
            if (!append)
            {
                var summary = await Db.Stock.GetSummaryAsync(request, AppSession.DeptNo, ct);
                SumKcamount = summary.TotalKcamount;
                SumIprcAmount = summary.TotalIprcAmount;
                SumIprjAmount = summary.TotalIprjAmount;
                SumOprcAmount = summary.TotalOprcAmount;
            }
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(LoadStatus));
            if (!append && Rows.Count == 0) Message = "没有符合条件的记录";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "查询失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>合计（fr_seisto MR.row41：WAITWINDOW 汇总 数量/批发价额/进价额/售价额；数量 INT() 向零截断口径）。</summary>
    [RelayCommand]
    private async Task TotalAsync(CancellationToken ct)
    {
        try
        {
            var s = await Db.Stock.GetSummaryAsync(BuildRequest(), AppSession.DeptNo, ct);
            System.Windows.MessageBox.Show(
                $"数量：{Math.Truncate(s.TotalKcamount):0}\r\n" +
                $"批发价金额：{s.TotalIprjAmount:N2}\r\n" +
                $"进价金额：{s.TotalIprcAmount:N2}\r\n" +
                $"售价金额：{s.TotalOprcAmount:N2}",
                "合计", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "合计失败：" + ex.Message;
        }
    }

    /// <summary>库存修改（打开编辑窗，fr_istoch FRM2 语义）。</summary>
    [RelayCommand]
    private void Edit()
    {
        if (SelectedRow is null) { Message = "请先选择库存行"; return; }
        var window = new StockEditWindow(SelectedRow)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        window.ShowDialog();
        if (window.Changed) _ = SearchAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>排序白名单列头切换（§7.1）。</summary>
    public void SortBy(string field)
    {
        if (!StockSortFields.TryResolve(field, out _)) return;
        if (string.Equals(_sort, field, StringComparison.OrdinalIgnoreCase))
            _sortDesc = !_sortDesc;
        else
            (_sort, _sortDesc) = (field, true);
        _ = SearchAsync(System.Threading.CancellationToken.None);
    }

    private StockQueryRequest BuildRequest() => new()
    {
        // 厂牌固定 = 会话厂牌（AppSession.Brand；旧 fr_istoch/fr_seisto a.cp=?p_cp 语义），无输入框
        Cp = AppSession.Brand.Length > 0 ? AppSession.Brand : null,
        Nno = NullIfEmpty(NnoFilter),
        Na1 = NullIfEmpty(Na1Filter),
        Ty = NullIfEmpty(TyFilter),
        Posi = null,
        Depotno = null,
        OnlyInStock = false,
        Page = Page,
        PageSize = PageSize,
        Sort = _sort,
        Order = _sortDesc ? "desc" : "asc",
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
