using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.ViewModels;

// =====================================================================================
// 账务第七批 VM（结转损益/期末结帐/帐务初始/帐套选项）
// 契约：开发文档/契约摘录/账务第七批契约.md
// =====================================================================================

/// <summary>结转损益（I34，fr_fincaforw：显示期间 + 执行结转 → 存储过程消息）。</summary>
public partial class FinCaforwViewModel : ObservableObject
{
    [ObservableProperty] private string _period = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var para = await Db.FinClosing.GetParaAsync(AppSession.DeptNo, ct);
        Period = para?.Period ?? "";
    }

    [RelayCommand]
    private async Task TransferAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            Message = await Db.FinClosing.TransferProfitAsync(AppSession.DeptNo, Period, AppSession.Operator, ct);
            await LoadAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>期末结帐（I35，fr_fincheout：结帐 / 反结帐（可删上月结转凭证））。</summary>
public partial class FinCheoutViewModel : ObservableObject
{
    [ObservableProperty] private string _period = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _delCaf;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var para = await Db.FinClosing.GetParaAsync(AppSession.DeptNo, ct);
        Period = para?.Period ?? "";
    }

    [RelayCommand]
    private async Task ClosePeriodAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            Message = await Db.FinClosing.PeriodCloseAsync(AppSession.DeptNo, Period, ct);
            await LoadAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UnCloseAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            Message = await Db.FinClosing.PeriodUnCloseAsync(AppSession.DeptNo, Period, DelCaf, ct);
            await LoadAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>帐务初始数据（N11，fr_fininit：detacode 重算 / 末级编辑 / Sum 汇总 / 置零 / 平衡提示）。</summary>
public partial class FinInitViewModel : ObservableObject
{
    public ObservableCollection<FinSubRow> Rows { get; } = [];

    [ObservableProperty] private FinSubRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _balanced;

    /// <summary>Load：detacode 重算（Init）→ 全量科目（合计行由 UI 哨兵显示）。</summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinClosing.RecalcDetacodeAsync(AppSession.DeptNo, ct);
            var rows = await Db.FinSub.GetSubsAsync(ct);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            CheckBalance(rows);
            Message = $"共 {rows.Count} 科目（灰底=非末级不可编辑）";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>末级余额编辑保存（借贷互斥 + ybdesum/ybcresum 派生，fr_fininit u_.row4）。</summary>
    [RelayCommand]
    private async Task SaveRowAsync(CancellationToken ct = default)
    {
        if (SelectedRow is not { } r) return;
        if (!r.Detacode) { Message = "非末级科目不可编辑!"; return; }
        if (r.Bdesum != 0 && r.Bcresum != 0) { Message = "借、贷方不能同时有金额!"; return; }
        try
        {
            await Db.FinClosing.UpdateInitBalanceAsync(new FinInitBalanceCommand
            {
                Subcode = r.Subcode, Bdesum = r.Bdesum, Bcresum = r.Bcresum,
                Ydesum = r.Ydesum, Ycresum = r.Ycresum, Ypalsum = r.Ypalsum,
            }, AppSession.DeptNo, ct);
            await LoadAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>Sum 汇总（myclick 'Sum'：全表清零 → 末级前缀链聚合）。</summary>
    [RelayCommand]
    private async Task SumAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await Db.FinClosing.InitSumAsync(AppSession.DeptNo, 3, ct);
            await LoadAsync(ct);
            Message = "汇总完成";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>置零（myclick 'Init'：确认后全表 12 列=0）。</summary>
    [RelayCommand]
    private async Task ZeroAsync(CancellationToken ct = default)
    {
        if (System.Windows.MessageBox.Show(
                "确认要将所有科目金额都置零吗?\r\n(置零后将不能恢复)", "询问",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;
        try
        {
            IsBusy = true;
            await Db.FinClosing.InitZeroAsync(AppSession.DeptNo, ct);
            await LoadAsync(ct);
            Message = "已置零";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>平衡提示（一级科目六列 SUM，fr_fininit row60）。</summary>
    private void CheckBalance(IReadOnlyList<FinSubRow> rows)
    {
        var l1 = rows.Where(r => r.Subcode.Length == 3).ToList();
        Balanced = l1.Sum(r => r.Ybdesum) == l1.Sum(r => r.Ybcresum)
                && l1.Sum(r => r.Ydesum) == l1.Sum(r => r.Ycresum)
                && l1.Sum(r => r.Bdesum) == l1.Sum(r => r.Bcresum);
        Message = Balanced ? "试算表已平衡！" : "试算表不平衡。";
    }
}

/// <summary>帐套选项（N12，fr_finpara：syscontrol 编辑保存，l_use=1 时只读）。</summary>
public partial class FinParaViewModel : ObservableObject
{
    [ObservableProperty] private FinPara? _para;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    public bool IsEditable => Para is not { LUse: true };

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Para = await Db.FinClosing.GetParaAsync(AppSession.DeptNo, ct);
        Message = Para is { LUse: true } ? "帐套已启用，选项不可修改（旧系统控件禁用）" : "";
        OnPropertyChanged(nameof(IsEditable));
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct = default)
    {
        if (Para is not { } p) return;
        try
        {
            IsBusy = true;
            await Db.FinClosing.SaveSysControlAsync(new FinParaSaveCommand
            {
                CId = p.CId, CName = p.CName, Addr = p.Addr, CType = p.CType, Attri = p.Attri,
                Artper = p.Artper, Taxid = p.Taxid, Bank = p.Bank, Bankid = p.Bankid,
                Tel = p.Tel, Fax = p.Fax, Gramount = p.Gramount,
                Leng1 = p.Leng1, Leng2 = p.Leng2, Leng3 = p.Leng3,
                Leng4 = p.Leng4, Leng5 = p.Leng5, Leng6 = p.Leng6,
                Uperiod = p.Uperiod, Udate = p.Udate, Period = p.Period,
            }, AppSession.DeptNo, ct);
            Message = "帐套选项已保存";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>启用帐套（N13，fr_finuse：平衡校验 → 清空九表 → l_use=1）。</summary>
public partial class FinUseViewModel : ObservableObject
{
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isUsed;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var para = await Db.FinClosing.GetParaAsync(AppSession.DeptNo, ct);
        IsUsed = para is { LUse: true };
        Message = IsUsed ? "帐套已启用" : "帐套未启用（启用前请先在帐务初始数据中录入平衡的期初余额）";
    }

    [RelayCommand]
    private async Task EnableAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            Message = await Db.FinClosing.EnableAsync(AppSession.DeptNo, 3, ct);
            if (Message.Length == 0) Message = "启用完成（初始科目余额已快照，凭证/账簿已清空重置）";
            await LoadAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
}
