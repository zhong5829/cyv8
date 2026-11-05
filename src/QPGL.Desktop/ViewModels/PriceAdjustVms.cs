﻿﻿﻿using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>库存调价（B4）+ 警戒线设置（B5）—— 契约：开发文档/契约摘录/库存第十批契约.md。</summary>
public partial class PriceAdjustViewModel : ObservableObject
{
    public ObservableCollection<IstoTjRow> IstoRows { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private IstoTjRow? _selectedRow;
    [ObservableProperty] private int _fieldKind = 1;      // 1=oprc 2=oprb 3=iprc 4=iprj
    [ObservableProperty] private int _baseKind = 1;       // 1=oprc 2=iprj 3=iprc 4=oprb
    [ObservableProperty] private string _oper = "*";
    [ObservableProperty] private decimal _factor = 1.1m;
    [ObservableProperty] private int _roundMode = 2;      // 1=不取整 2=int+0.5 3=int+0.99
    [ObservableProperty] private bool _bySupplier;
    [ObservableProperty] private string _gyname = "";
    [ObservableProperty] private string _depotno = "01";
    [ObservableProperty] private string? _dc;
    [ObservableProperty] private string? _dcCon;
    [ObservableProperty] private string? _ty;
    // ==== 警戒线（B5，2026-11-04 改手动编辑：QP11 库存预警同构，tbistojj 双档 amjj/bmjj）====
    [ObservableProperty] private string _cp = AppSession.Brand;   // 当前厂牌（警戒线清单数据源过滤）
    public ObservableCollection<JjLineRow> JjRows { get; } = [];
    /// <summary>警戒线视图（内存过滤：编码 / 名称(拼音码) / 车型）。</summary>
    public ICollectionView JjView { get; }
    [ObservableProperty] private string _jjCpFilter = "";
    [ObservableProperty] private string _jjNa1Filter = "";
    [ObservableProperty] private string _jjTyFilter = "";
    [ObservableProperty] private JjLineRow? _selectedJjRow;
    private Dictionary<string, string> _jjPyMap = new(StringComparer.Ordinal);   // 名称→拼音码（tbname0）
    private Dictionary<string, string> _jjTyMap = new(StringComparer.Ordinal);   // 车型→拼音码（tbty0）
    /// <summary>查询条件防抖（2026-11-04 修复：实时输入 + 300ms 防抖刷新过滤视图）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _jjFilterDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public PriceAdjustViewModel()
    {
        JjView = CollectionViewSource.GetDefaultView(JjRows);
        JjView.Filter = JjFilter;   // 2026-11-04 修复：此前未挂过滤委托，条件框全不生效
        _jjFilterDebounce.Tick += (_, _) =>
        {
            _jjFilterDebounce.Stop();
            JjView.Refresh();
        };
    }

    /// <summary>加载警戒线清单（当前厂牌 + 当前分店；过滤条件变化触发防抖重刷视图）。</summary>
    [RelayCommand]
    public async Task LoadJjAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            Message = "";
            try { _jjPyMap = new Dictionary<string, string>(await Db.SaleReturns.GetPinyinMapAsync(ct), StringComparer.Ordinal); }
            catch { /* 拼音映射失败不阻断——名称框退化为纯中文包含 */ }
            try { _jjTyMap = new Dictionary<string, string>(await Db.SaleReturns.GetTyPinyinMapAsync(ct), StringComparer.Ordinal); }
            catch { /* 车型拼音映射失败不阻断——车型框退化为纯中文包含 */ }
            var rows = await Db.PriceAdjust.GetJjLinesAsync(Cp, AppSession.DeptNo, ct);
            JjRows.Clear();
            foreach (var r in rows) JjRows.Add(r);
            JjView.Refresh();
            Message = $"警戒线清单 {JjRows.Count} 行（预警 {JjRows.Count(r => r.IsYellow)}，告急 {JjRows.Count(r => r.IsRed)}）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    private bool JjFilter(object item)
    {
        if (item is not JjLineRow r) return false;
        var cp = JjCpFilter.Trim();
        var na1 = JjNa1Filter.Trim();
        var ty = JjTyFilter.Trim();
        if (cp.Length > 0 && !r.Nno.Contains(cp, StringComparison.OrdinalIgnoreCase)) return false;
        if (ty.Length > 0)
        {
            // 车型框口径（选料窗同源）：车型/名称包含 + tbty0 车型拼音码
            if (!(r.Ty.Contains(ty, StringComparison.OrdinalIgnoreCase)
                || r.Na1.Contains(ty, StringComparison.OrdinalIgnoreCase)
                || (_jjTyMap.TryGetValue(r.Ty, out var tyCode)
                    && tyCode.Contains(ty, StringComparison.OrdinalIgnoreCase))))
                return false;
        }
        if (na1.Length == 0) return true;
        // 名称框口径（选料窗同源）：名称/编码/车型包含 + tbname0 拼音码
        return r.Na1.Contains(na1, StringComparison.OrdinalIgnoreCase)
            || r.Nno.Contains(na1, StringComparison.OrdinalIgnoreCase)
            || r.Ty.Contains(na1, StringComparison.OrdinalIgnoreCase)
            || (_jjPyMap.TryGetValue(r.Na1, out var py)
                && py.Contains(na1, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnJjCpFilterChanged(string value) => RestartJjDebounce();
    partial void OnJjNa1FilterChanged(string value) => RestartJjDebounce();
    partial void OnJjTyFilterChanged(string value) => RestartJjDebounce();

    private void RestartJjDebounce()
    {
        _jjFilterDebounce.Stop();
        _jjFilterDebounce.Start();
    }

    /// <summary>单元格编辑提交（由视图 CellEditEnding 调用）：单档落库（null=另一档不动），失败恢复原值。</summary>
    public async Task CommitJjEditAsync(JjLineRow row, bool isA, decimal value, decimal oldValue)
    {
        try
        {
            IsBusy = true;
            Message = "";
            await Db.PriceAdjust.UpdateJjLineAsync(AppSession.DeptNo, row.Cp, row.Nno,
                isA ? value : null, isA ? null : value);
            if (isA) row.Amjj = value; else row.Bmjj = value;
            Message = $"{row.Nno} 警戒值已保存。";
        }
        catch (AppException ex)
        {
            if (isA) row.Amjj = oldValue; else row.Bmjj = oldValue;
            Message = ex.Message;
            MessageBox.Show(ex.Message, "警戒线设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsBusy = false; }
    }

    /// <summary>加载库存调价列表（B4）。</summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var rows = await Db.PriceAdjust.GetIstoAsync(Depotno, Dc, ct);
            IstoRows.Clear();
            foreach (var r in rows) IstoRows.Add(r);
            Message = $"库存 {rows.Count} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>整批调价（B4：调进价需确认）。</summary>
    [RelayCommand]
    private async Task AdjustAsync(CancellationToken ct = default)
    {
        if (FieldKind == 3 && MessageBox.Show("调进价会给财务统计造成进销存成本不对，是否真的要调价", "提醒",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            IsBusy = true;
            var n = await Db.PriceAdjust.AdjustAsync(new PriceAdjustCommand
            {
                FieldKind = FieldKind, BaseKind = BaseKind, Oper = Oper, Factor = Factor,
                RoundMode = RoundMode, BySupplier = BySupplier, Gyname = Gyname,
                Depotno = string.IsNullOrWhiteSpace(Depotno) ? null : Depotno,
                Dc = Dc, DcCon = DcCon, Ty = Ty,
            }, SelectedRow?.Cp ?? "", AppSession.DeptNo, ct);
            Services.PartCacheService.Invalidate();   // tbisto 价格整批变更 → 配件选择缓存失效
            await LoadAsync(ct);
            Message = $"整批调价完成，更新 {n} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}
