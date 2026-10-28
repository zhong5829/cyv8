using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.ViewModels;

public sealed class ComboTypeItem
{
    public ComboTypeItem(int key, string name) { Key = key; Name = name; }
    public int Key { get; }
    public string Name { get; }
}

/// <summary>既有自动订单列表行（GetOrderNosAsync 的 ValueTuple 字段不可绑定，转显示行）。</summary>
public sealed class OrderNoRow
{
    public string Cno { get; set; } = "";
    public string Orderdate { get; set; } = "";
}

/// <summary>
/// 自动订货 VM（菜单 F.4，权限码 F4；fr_orderbud myclick 契约 §2.2）。
/// 生成：先以 AppendMode=true 探测——同号已存在时仓储抛 CNO_EXISTS → 弹覆盖确认 → AppendMode=false 重跑（仓储覆盖模式，先 DELETE 再 INSERT）。
/// </summary>
public partial class OrderBudViewModel : ObservableObject
{
    public ObservableCollection<OrderExpRow> Formulas { get; } = [];
    public ObservableCollection<OrderNoRow> OrderNos { get; } = [];
    public IReadOnlyList<ComboTypeItem> ComboTypes { get; } =
        [new(1, "根据属性"), new(2, "根据供应商价格"), new(3, "根据进库历史")];

    [ObservableProperty] private string _cno = "";
    [ObservableProperty] private DateTime _orderDate = DateTime.Today;
    [ObservableProperty] private OrderExpRow? _selectedFormula;
    [ObservableProperty] private ComboTypeItem _selectedComboType;
    [ObservableProperty] private string _gyname = "";
    [ObservableProperty] private DateTime _inDate1 = DateTime.Today.AddYears(-1);
    [ObservableProperty] private DateTime _inDate2 = DateTime.Today;
    [ObservableProperty] private DateTime _date1 = DateTime.Today.AddYears(-1);
    [ObservableProperty] private DateTime _date2 = DateTime.Today;
    [ObservableProperty] private string _bMon = "";
    [ObservableProperty] private string _eMon = "";
    [ObservableProperty] private string _ty = "";
    [ObservableProperty] private bool _tyExclude;
    [ObservableProperty] private string _dc = "";
    [ObservableProperty] private decimal _bAmt = -9999;
    [ObservableProperty] private decimal _eAmt = 9999;
    [ObservableProperty] private string _cp = AppSession.Brand;   // 旧 OrderBudRepository p.cp=@p_cp（fr_orderexp 契约）
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    public OrderBudViewModel() => _selectedComboType = ComboTypes[0];

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            foreach (var f in await Db.OrderExp.GetFormulasAsync(ct)) Formulas.Add(f);
            await LoadOrderNosAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
    }

    private async Task LoadOrderNosAsync(CancellationToken ct)
    {
        OrderNos.Clear();
        foreach (var (cno, orderdate) in await Db.OrderBud.GetOrderNosAsync(ct))
            OrderNos.Add(new OrderNoRow { Cno = cno, Orderdate = orderdate });
    }

    [RelayCommand]
    public async Task GenerateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Cno))
        {
            MessageBox.Show("订单号不能为空", "自动订货", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var cmd = BuildCommand();
        try
        {
            IsBusy = true;
            Message = "";
            try
            {
                await RunAsync(cmd, ct);
                return;
            }
            catch (AppException ex) when (ex.Code == "CNO_EXISTS")
            {
                if (MessageBox.Show("该订单已存在,是否覆盖?", "自动订货", MessageBoxButton.YesNo, MessageBoxImage.Question)
                    != MessageBoxResult.Yes)
                {
                    Message = "已取消";
                    return;
                }
            }
            cmd.AppendMode = false;   // 覆盖模式重跑（仓储先 DELETE tborder WHERE cno 再 INSERT）
            await RunAsync(cmd, ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "自动订货", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync(OrderBudCommand cmd, CancellationToken ct)
    {
        var (items, total) = await Db.OrderBud.GenerateAsync(cmd, AppSession.DeptNo, AppSession.Operator, ct);
        MessageBox.Show($"订单 {cmd.Cno} 生成完成：共 {items} 项，订货合计 {total:N2}。",
            "自动订货", MessageBoxButton.OK, MessageBoxImage.Information);
        await LoadOrderNosAsync(ct);
    }

    private OrderBudCommand BuildCommand() => new()
    {
        Cno = Cno.Trim(),
        AppendMode = true,
        OrderDate = OrderDate,
        CExpress = SelectedFormula?.Cexpress ?? "",
        ComboType = SelectedComboType?.Key ?? 1,
        Gyname = Gyname.Trim(),
        InDate1 = InDate1,
        InDate2 = InDate2,
        Date1 = Date1,
        Date2 = Date2,
        BMon = string.IsNullOrWhiteSpace(BMon) ? null : BMon.Trim(),
        EMon = string.IsNullOrWhiteSpace(EMon) ? null : EMon.Trim(),
        Ty = Ty.Trim(),
        TyExclude = TyExclude,
        Dc = Dc.Trim(),
        BAmt = BAmt,
        EAmt = EAmt,
        Cp = Cp.Trim(),
    };
}
