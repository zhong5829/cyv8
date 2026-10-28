using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>提货行明细包装（c_mx 语义：快照 + 勾选/箱号/发货数量可编辑）。</summary>
public partial class DeliveryLineItem : ObservableObject
{
    public int Id { get; init; }
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Fa { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Ckamount { get; init; }
    public decimal Oprc { get; init; }
    public string Posi { get; init; } = "";
    public string Depotno { get; init; } = "";
    public DateTime Fhdate { get; init; }
    /// <summary>已发/已备货标识（蓝字行；发货勾选只针对未发行）。</summary>
    public bool Flag { get; init; }

    [ObservableProperty] private bool _isSelected;   // 发货勾选
    [ObservableProperty] private string _box = "";   // 箱号（装箱页可编辑）
    [ObservableProperty] private decimal _fhAmount;  // 发货数量（装箱页可编辑）
}

/// <summary>
/// 提货管理 VM（菜单 B.B，权限码 BB；fr_th 契约）。
/// 三页签：待提货（mark=''）/ 已确认（mark='Y'）/ 装箱（mark='T'）。
/// 页1/2 共用一套工具命令，按当前页签取选中单据；装箱页独立命令。
/// </summary>
public partial class DeliveryViewModel : ObservableObject
{
    /// <summary>仓储访问点（Db.Delivery 单例）。</summary>
    private static readonly IDeliveryRepository Repo = Db.Delivery;

    public ObservableCollection<DeliveryOrderRow> Orders1 { get; } = [];
    public ObservableCollection<DeliveryOrderRow> Orders2 { get; } = [];
    public ObservableCollection<DeliveryLineItem> Lines1 { get; } = [];
    public ObservableCollection<DeliveryLineItem> Lines2 { get; } = [];
    public ObservableCollection<DeliveryPackingRow> PackingOrders { get; } = [];
    public ObservableCollection<DeliveryLineItem> PackingLines { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _activeTabIndex;

    public DeliveryOrderRow? SelectedOrder1 { get; set; }
    public DeliveryOrderRow? SelectedOrder2 { get; set; }
    public DeliveryPackingRow? SelectedPacking { get; set; }

    /// <summary>页1/2 共用命令的当前单据（按活动页签取）。</summary>
    private DeliveryOrderRow? CurrentOrder => ActiveTabIndex == 1 ? SelectedOrder2 : SelectedOrder1;

    [RelayCommand]
    public async Task LoadedAsync()
    {
        await LoadOrdersAsync(DeliveryTab.Pending);
        await LoadOrdersAsync(DeliveryTab.Confirmed);
        await LoadPackingAsync();
    }

    [RelayCommand]
    private Task RefreshTab1Async() => LoadOrdersAsync(DeliveryTab.Pending);

    [RelayCommand]
    private Task RefreshTab2Async() => LoadOrdersAsync(DeliveryTab.Confirmed);

    [RelayCommand]
    private Task RefreshPackingAsync() => LoadPackingAsync();

    /// <summary>页1/2 行明细（未发行默认勾选——旧发货勾选语义）。</summary>
    public async Task LoadLinesForTabAsync(DeliveryTab tab)
    {
        var order = tab == DeliveryTab.Pending ? SelectedOrder1 : SelectedOrder2;
        var target = tab == DeliveryTab.Pending ? Lines1 : Lines2;
        try
        {
            IsBusy = true;
            target.Clear();
            if (order is null) return;
            var rows = await Repo.GetLinesAsync(order.Paper);
            foreach (var r in rows)
                target.Add(new DeliveryLineItem
                {
                    Id = r.Id, Nno = r.Nno, Na1 = r.Na1, Ty = r.Ty, Fa = r.Fa, Unit = r.Unit,
                    Ckamount = r.Ckamount, Oprc = r.Oprc, Posi = r.Posi, Depotno = r.Depotno,
                    Fhdate = r.Fhdate, Flag = r.Flag, Box = r.Box, IsSelected = !r.Flag,
                });
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>装箱页行明细（发货数量默认=行数量）。</summary>
    public async Task LoadPackingLinesAsync()
    {
        try
        {
            IsBusy = true;
            PackingLines.Clear();
            if (SelectedPacking is null) return;
            var rows = await Repo.GetLinesAsync(SelectedPacking.Paper);
            foreach (var r in rows)
                PackingLines.Add(new DeliveryLineItem
                {
                    Id = r.Id, Nno = r.Nno, Na1 = r.Na1, Ty = r.Ty, Unit = r.Unit,
                    Ckamount = r.Ckamount, Oprc = r.Oprc, Posi = r.Posi, Depotno = r.Depotno,
                    Fhdate = r.Fhdate, Flag = r.Flag, Box = r.Box, FhAmount = r.Ckamount,
                });
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadOrdersAsync(DeliveryTab tab)
    {
        try
        {
            IsBusy = true;
            var rows = await Repo.GetOrdersAsync(tab, AppSession.DeptNo);
            var target = tab == DeliveryTab.Pending ? Orders1 : Orders2;
            target.Clear();
            foreach (var r in rows) target.Add(r);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadPackingAsync()
    {
        try
        {
            IsBusy = true;
            var rows = await Repo.GetPackingListAsync(AppSession.DeptNo);
            PackingOrders.Clear();
            foreach (var r in rows) PackingOrders.Add(r);
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    // ---------- 页1/2 工具命令 ----------

    /// <summary>备货（旧 bhmark='bh' 分支：非已确认单 → B 命令 StockOnly=true）。</summary>
    [RelayCommand]
    private async Task StockAsync()
    {
        var order = CurrentOrder;
        if (order is null) { Warn("请先选择单据。"); return; }
        if (order.Mark == "Y") { Warn("已确认单不能备货。"); return; }
        var input = ShowInputDialog("备货", new[]
        {
            ("检货人", ""), ("发货单号", order.FhPaper), ("代搬人", ""), ("站名", ""),
        });
        if (input is null) return;
        if (!RequireFhPaper(input[1])) return;
        if (!ConfirmOp($"确认备货单据 {order.Paper} 吗？")) return;
        await RunAsync(async () =>
        {
            var result = await Repo.ConfirmDeliveryBAsync(new DeliveryConfirmBCommand
            {
                Paper = order.Paper, FhPaper = input[1].Trim(), Jhr = input[0].Trim(),
                PerDbr = input[2].Trim(), Muzhan = input[3].Trim(),
                StockOnly = true, LineIds = CheckedLineIds(),
            }, AppSession.DeptNo);
            Info(result.Stocked ? "备货成功。" : "该单未走备货分支（零售/已确认单，已按发货流程处理）。");
        });
        await RefreshAfterOrderOpAsync();
    }

    /// <summary>发货确认 A（简：行级 fhdate + spname + 整单判定）。</summary>
    [RelayCommand]
    private async Task ShipAAsync()
    {
        var order = CurrentOrder;
        if (order is null) { Warn("请先选择单据。"); return; }
        if (order.Mark != "Y") { Warn("发货确认（简）仅适用于已确认单。"); return; }
        var ids = CheckedLineIds();
        if (ids.Count == 0) { Warn("请先在明细中勾选要发货的行（已发行不可重复发货）。"); return; }
        var input = ShowInputDialog("发货确认", new[]
        {
            ("发货员", order.Back1), ("备注", ""), ("发货公司", order.Spname),
        });
        if (input is null) return;
        if (!ConfirmOp($"确认发货单据 {order.Paper}（{ids.Count} 行）吗？")) return;
        await RunAsync(async () =>
        {
            var result = await Repo.ConfirmDeliveryAAsync(new DeliveryConfirmACommand
            {
                Paper = order.Paper, Back1 = input[0].Trim(), Remark = input[1].Trim(),
                Spname = input[2].Trim(), LineIds = ids,
            }, AppSession.DeptNo);
            Info(result.Completed ? "整单已发货完成。" : "发货确认完成（单据未发完）。");
        });
        await RefreshAfterOrderOpAsync();
    }

    /// <summary>发货确认 B（全：A + tbfh/tbwlfh + 整单判定）。</summary>
    [RelayCommand]
    private async Task ShipBAsync()
    {
        var order = CurrentOrder;
        if (order is null) { Warn("请先选择单据。"); return; }
        var ids = CheckedLineIds();
        if (ids.Count == 0) { Warn("请先在明细中勾选要发货的行。"); return; }
        var input = ShowInputDialog("发货确认（全）", new[]
        {
            ("发货员", order.Back1), ("备注", ""), ("发货公司", order.Spname),
            ("检货人", ""), ("发货单号", order.FhPaper), ("代搬人", ""), ("站名", ""),
        });
        if (input is null) return;
        if (!RequireFhPaper(input[4])) return;
        if (!ConfirmOp($"确认发货单据 {order.Paper}（{ids.Count} 行）吗？")) return;
        await RunAsync(async () =>
        {
            var result = await Repo.ConfirmDeliveryBAsync(new DeliveryConfirmBCommand
            {
                Paper = order.Paper, Back1 = input[0].Trim(), Remark = input[1].Trim(),
                Spname = input[2].Trim(), Jhr = input[3].Trim(), FhPaper = input[4].Trim(),
                PerDbr = input[5].Trim(), Muzhan = input[6].Trim(),
                StockOnly = false, LineIds = ids,
            }, AppSession.DeptNo);
            Info(result.Completed ? "整单已发货完成。" : "发货确认完成（单据未发完）。");
        });
        await RefreshAfterOrderOpAsync();
    }

    /// <summary>配货 / 还原配货（printype 1/0，切换不弹确认）。</summary>
    [RelayCommand]
    private async Task AllocateAsync() => await SetAllocationAsync(true);

    [RelayCommand]
    private async Task UnallocateAsync() => await SetAllocationAsync(false);

    private async Task SetAllocationAsync(bool allocated)
    {
        var order = CurrentOrder;
        if (order is null) { Warn("请先选择单据。"); return; }
        await RunAsync(async () =>
        {
            await Repo.SetAllocationAsync(order.Paper, allocated, AppSession.DeptNo);
            Info(allocated ? "配货完成。" : "已还原配货。");
        });
        await RefreshAfterOrderOpAsync();
    }

    [RelayCommand]
    private async Task RevertStockAsync()
    {
        var order = CurrentOrder;
        if (order is null) { Warn("请先选择单据。"); return; }
        if (!ConfirmOp($"确认还原单据 {order.Paper} 的备货吗？")) return;
        await RunAsync(async () =>
        {
            await Repo.RevertStockAsync(order.Paper, AppSession.DeptNo);
            Info("已还原备货。");
        });
        await RefreshAfterOrderOpAsync();
    }

    [RelayCommand]
    private async Task RevertDeliveryAsync()
    {
        var order = CurrentOrder;
        if (order is null) { Warn("请先选择单据。"); return; }
        if (!ConfirmOp($"确认还原单据 {order.Paper} 的发货吗？")) return;
        await RunAsync(async () =>
        {
            await Repo.RevertDeliveryAsync(order.Paper, AppSession.DeptNo);
            Info("已还原发货。");
        });
        await RefreshAfterOrderOpAsync();
    }

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        var input = ShowInputDialog("删除发货单", new[] { ("发货单号", "") });
        if (input is null) return;
        var fhPaper = input[0].Trim();
        if (fhPaper == "") { Warn("发货单号必填。"); return; }
        if (!ConfirmOp($"确认删除发货单 {fhPaper} 吗？")) return;
        await RunAsync(async () =>
        {
            var n = await Repo.DeleteDeliveryNoteAsync(fhPaper);
            Info($"已删除 {n} 行发货记录。");
        });
        await RefreshAfterOrderOpAsync();
    }

    // ---------- 页3 装箱命令 ----------

    [RelayCommand]
    private async Task PackingConfirmAsync()
    {
        var order = SelectedPacking;
        if (order is null) { Warn("请先选择单据。"); return; }
        if (PackingLines.Count == 0) { Warn("该单无明细行。"); return; }
        var input = ShowInputDialog("装箱确认", new[]
        {
            ("发货单号", order.FhPaper), ("发货公司", order.Fhgs), ("检货人", order.PerJhr),
            ("代搬人", order.PerDbr), ("物流报价", ""), ("发货简述", ""), ("发货员", ""), ("发货方式", ""),
        });
        if (input is null) return;
        if (!RequireFhPaper(input[0])) return;
        if (!ConfirmOp($"确认装箱单据 {order.Paper} 吗？")) return;
        await RunAsync(async () =>
        {
            await Repo.ConfirmPackingAsync(new DeliveryPackingCommand
            {
                FhPaper = input[0].Trim(), Papers = [order.Paper], Fhgs = input[1].Trim(),
                Jhr = input[2].Trim(), PerDbr = input[3].Trim(), Wlbj = input[4].Trim(),
                Fhjs = input[5].Trim(), Back1 = input[6].Trim(), TMode = input[7].Trim(),
                Lines = PackingLines.Select(l => new DeliveryPackingLine
                {
                    SadaId = l.Id, Box = l.Box, FhAmount = l.FhAmount,
                }).ToList(),
            }, AppSession.DeptNo);
            Info("装箱确认完成。");
        });
        await LoadPackingAsync();
        await LoadPackingLinesAsync();
    }

    [RelayCommand]
    private async Task RevertPackingAsync()
    {
        var order = SelectedPacking;
        if (order is null) { Warn("请先选择单据。"); return; }
        if (!ConfirmOp($"确认还原单据 {order.Paper} 的装箱吗？")) return;
        await RunAsync(async () =>
        {
            await Repo.RevertPackingAsync(order.Paper);
            Info("已还原装箱。");
        });
        await LoadPackingAsync();
        await LoadPackingLinesAsync();
    }

    // ---------- 私有 ----------

    /// <summary>发货/备货取勾选且未发的行 id。</summary>
    private List<int> CheckedLineIds()
    {
        var lines = ActiveTabIndex == 1 ? Lines2 : Lines1;
        return lines.Where(l => l.IsSelected && !l.Flag).Select(l => l.Id).ToList();
    }

    /// <summary>页1/2 操作后刷新两个页签列表 + 当前页签明细。</summary>
    private async Task RefreshAfterOrderOpAsync()
    {
        await LoadOrdersAsync(DeliveryTab.Pending);
        await LoadOrdersAsync(DeliveryTab.Confirmed);
        if (ActiveTabIndex == 0) await LoadLinesForTabAsync(DeliveryTab.Pending);
        else if (ActiveTabIndex == 1) await LoadLinesForTabAsync(DeliveryTab.Confirmed);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            IsBusy = true;
            Message = "";
            await action();
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "提货管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Warn(string text) =>
        MessageBox.Show(text, "提货管理", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void Info(string text) =>
        MessageBox.Show(text, "提货管理", MessageBoxButton.OK, MessageBoxImage.Information);

    private static bool ConfirmOp(string text) =>
        MessageBox.Show(text, "提货管理", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static bool RequireFhPaper(string fhPaper)
    {
        if (!string.IsNullOrWhiteSpace(fhPaper)) return true;
        Warn("发货单号必填！");
        return false;
    }

    /// <summary>简易多字段输入窗（确定返回与 fields 对齐的值数组；取消返回 null）。</summary>
    private static string[]? ShowInputDialog(string title, (string Label, string Default)[] fields)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var boxes = new List<TextBox>();
        foreach (var (label, def) in fields)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            var box = new TextBox { Text = def, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(box);
            boxes.Add(box);
        }
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "确定", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var confirmed = false;
        ok.Click += (_, _) => confirmed = true;
        var win = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current.MainWindow,
        };
        win.ShowDialog();
        if (!confirmed) return null;
        return boxes.Select(b => b.Text).ToArray();
    }
}
