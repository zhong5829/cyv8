﻿using System.Collections.ObjectModel;
using System.Windows;
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
    // 警戒线
    [ObservableProperty] private int _months = 3;
    [ObservableProperty] private decimal _factorA = 1.2m;
    [ObservableProperty] private decimal _factorB = 1.5m;
    [ObservableProperty] private bool _setAmjj = true;
    [ObservableProperty] private bool _setBmjj = true;
    [ObservableProperty] private bool _allBrand = false;   // 旧 fr_xlxsaj 默认当前厂牌（保留全部厂牌勾选语义）
    [ObservableProperty] private string _cp = AppSession.Brand;
    [ObservableProperty] private string? _jjFa;
    [ObservableProperty] private string? _jjDc;
    [ObservableProperty] private string? _jjTy;

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
            await LoadAsync(ct);
            Message = $"整批调价完成，更新 {n} 行";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>警戒线设置（B5：tbrtsamo 月均 × 系数 → tbistojj）。</summary>
    [RelayCommand]
    private async Task SetJjAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var n = await Db.PriceAdjust.SetJjLinesAsync(new JjLineCommand
            {
                Months = Months, FactorA = FactorA, FactorB = FactorB, SetAmjj = SetAmjj, SetBmjj = SetBmjj,
                AllBrand = AllBrand, Cp = Cp, Fa = JjFa, Dc = JjDc, Ty = JjTy,
            }, AppSession.DeptNo, ct);
            Message = $"警戒线设置成功（更新/插入 {n} 行，零值行已清理）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}
