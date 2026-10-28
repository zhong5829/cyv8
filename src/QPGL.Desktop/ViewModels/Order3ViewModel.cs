using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.ViewModels;

/// <summary>行着色转换器：oqty &gt; qty（在途）→ 蓝字 #1565C0。</summary>
public sealed class OrderRowBrushConverter : IValueConverter
{
    private static readonly Brush InTransit = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is OrderRow r && r.Oqty > r.Qty ? InTransit : Brushes.Black;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 代码构建的简易表单弹窗（仅本组订单族页面使用；未新增独立文件）。
/// </summary>
public sealed class FormDialog : Window
{
    private readonly StackPanel _root = new() { Margin = new Thickness(14) };

    public FormDialog(string title)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Content = _root;
    }

    private void Add(string label, FrameworkElement input)
    {
        input.Margin = new Thickness(0, 2, 0, 2);
        input.VerticalAlignment = VerticalAlignment.Center;
        var tb = new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center };
        var dp = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tb, Dock.Left);
        dp.Children.Add(tb);
        dp.Children.Add(input);
        _root.Children.Add(dp);
    }

    public TextBox AddText(string label, string value = "")
    {
        var t = new TextBox { Text = value };
        Add(label, t);
        return t;
    }

    public ComboBox AddCombo(string label, IEnumerable<string> items, string? selected)
    {
        var c = new ComboBox { ItemsSource = items.ToList() };
        if (selected is not null && c.Items.Contains(selected)) c.SelectedItem = selected;
        Add(label, c);
        return c;
    }

    public DatePicker AddDate(string label, DateTime date)
    {
        var d = new DatePicker { SelectedDate = date };
        Add(label, d);
        return d;
    }

    public CheckBox AddCheck(string label, bool isChecked = false)
    {
        var c = new CheckBox { Content = label, IsChecked = isChecked };
        _root.Children.Add(c);
        return c;
    }

    public Button AddButton(string content)
    {
        var b = new Button { Content = content, Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        _root.Children.Add(b);
        return b;
    }

    public DataGrid AddGrid<T>(IEnumerable<T> rows, Action<DataGrid> buildColumns, double height = 220)
    {
        var g = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, Height = height,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column, FontSize = 12,
        };
        buildColumns(g);
        g.ItemsSource = rows.ToList();
        _root.Children.Add(g);
        return g;
    }

    /// <summary>追加确定/取消按钮并模态显示；返回是否确定。</summary>
    public bool ShowOkCancel()
    {
        var ok = new Button { Content = "确定", Width = 72, IsDefault = true, Style = Application.Current.TryFindResource("PrimaryButtonStyle") as Style };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        _root.Children.Add(row);
        Owner = Application.Current.MainWindow;
        return ShowDialog() == true;
    }
}

/// <summary>
/// 订货库管理 VM（菜单 F.2，权限码 F2；fr_order3 契约）。
/// 主网格 type='历史' 工作集；行级增/改/删 + 批改（改合同号/重排/删除/恢复/改订单号/改客户/改供应商/改预计到货）+ 合并散单。
/// </summary>
public partial class Order3ViewModel : ObservableObject
{
    public ObservableCollection<OrderRow> Rows { get; } = [];
    public ObservableCollection<string> FkfsOptions { get; } = [];
    public ObservableCollection<string> QuickOptions { get; } = ["", "已到", "在途"];
    public IReadOnlyList<string> BatchTypes { get; } =
    [
        "改合同号", "重排item", "删除合同", "恢复合同", "改订单号", "改客户", "改供应商", "改预计到货",
    ];

    [ObservableProperty] private string _cno = "";
    [ObservableProperty] private string _orno = "";
    [ObservableProperty] private string _nnoPrefix = "";
    [ObservableProperty] private string _na1Contains = "";
    [ObservableProperty] private string _gyname = "";
    [ObservableProperty] private string _gname = "";
    [ObservableProperty] private string _selectedQuick = "";
    [ObservableProperty] private string _cp = AppSession.Brand;   // 旧 tbxzb 对 tborder 自动过滤 cp=?p_cp
    [ObservableProperty] private OrderRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            foreach (var f in await Db.Order3.GetFkfsOptionsAsync(ct)) FkfsOptions.Add(f);
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var rows = await Db.Order3.SearchAsync(new OrderSearchQuery
            {
                Cno = Cno, Orno = Orno, NnoPrefix = NnoPrefix, Na1Contains = Na1Contains,
                Gyname = Gyname, Gname = Gname, Quick = SelectedQuick,
            }, AppSession.DeptNo, Cp.Trim(), ct);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            if (rows.Count == 0) Message = "未找到符合条件的订单行";
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

    // ---------- 行级编辑 ----------

    [RelayCommand]
    public async Task AddLineAsync(CancellationToken ct)
    {
        var d = new FormDialog("新增订单行");
        var tCno = d.AddText("合同号");
        var tOrno = d.AddText("订单号");
        var tNno = d.AddText("编号");
        var tNa1 = d.AddText("名称");
        var tOqty = d.AddText("订货数", "1");
        var tIprj = d.AddText("外币价", "0");
        var tOprc = d.AddText("售价", "0");
        var tTy = d.AddText("车型");
        var tFa = d.AddText("产地系列");
        var tUnit = d.AddText("单位");
        var tDc = d.AddText("产地");
        var tNote = d.AddText("备注");
        var tGname = d.AddText("客户");
        var cbFkfs = d.AddCombo("付款方式", FkfsOptions, FkfsOptions.Contains("挂帐") ? "挂帐" : FkfsOptions.FirstOrDefault());
        var headerInfo = d.AddText("表头", "");
        headerInfo.IsEnabled = false;
        var btnHeader = d.AddButton("带出表头");
        btnHeader.Click += async (_, _) =>
        {
            var h = await FetchHeaderAsync(tCno.Text);
            if (h is null) return;
            if (cbFkfs.Items.Contains(h.Fkfs)) cbFkfs.SelectedItem = h.Fkfs;
            headerInfo.Text = $"供应商={h.Gyname.Trim()} 日期={h.Orderdate:yyyy-MM-dd} 备注类型={h.Bzlx.Trim()}";
        };
        if (!d.ShowOkCancel()) return;

        try
        {
            IsBusy = true;
            Message = "";
            var header = await FetchHeaderAsync(tCno.Text);
            if (header is null)
            {
                MessageBox.Show("无此合同号", "订货库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var input = new OrderLineInput
            {
                Cno = tCno.Text.Trim(), Orno = tOrno.Text.Trim(), Nno = tNno.Text.Trim(), Na1 = tNa1.Text.Trim(),
                Oqty = ParseDec(tOqty.Text), Iprj = ParseDec(tIprj.Text), Oprc = ParseDec(tOprc.Text),
                Orderdate = header.Orderdate, Ty = tTy.Text.Trim(), Fa = tFa.Text.Trim(), Unit = tUnit.Text.Trim(),
                Dc = tDc.Text.Trim(), Note = tNote.Text.Trim(), Gyname = header.Gyname.Trim(),
                Gname = tGname.Text.Trim(), Fkfs = cbFkfs.SelectedItem?.ToString() ?? "", Bzlx = header.Bzlx.Trim(),
            };
            await Db.Order3.CreateAsync(input, AppSession.DeptNo, Cp.Trim(), AppSession.Operator, ct);
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task EditLineAsync(CancellationToken ct)
    {
        if (SelectedRow is null) return;
        var s = SelectedRow;
        var d = new FormDialog("修改订单行");
        var tCno = d.AddText("合同号", s.Cno);
        var tOrno = d.AddText("订单号", s.Orno);
        d.AddText("编号", s.Nno).IsEnabled = false;
        var tNa1 = d.AddText("名称", s.Na1);
        var tOqty = d.AddText("订货数", s.Oqty.ToString("0.##"));
        var tIprj = d.AddText("外币价", s.Iprj.ToString("0.##"));
        var tOprc = d.AddText("售价", s.Oprc.ToString("0.##"));
        var tTy = d.AddText("车型", s.Ty);
        var tFa = d.AddText("产地系列", s.Fa);
        var tUnit = d.AddText("单位", s.Unit);
        var tDc = d.AddText("产地", "");
        var tNote = d.AddText("备注", s.Note);
        var tGname = d.AddText("客户", s.Gname);
        var cbFkfs = d.AddCombo("付款方式", FkfsOptions, s.Fkfs);
        if (!d.ShowOkCancel()) return;

        try
        {
            IsBusy = true;
            Message = "";
            var input = new OrderLineInput
            {
                Cno = tCno.Text.Trim(), Orno = tOrno.Text.Trim(), Item = s.Item, Nno = s.Nno, Na1 = tNa1.Text.Trim(),
                Oqty = ParseDec(tOqty.Text), Iprj = ParseDec(tIprj.Text), Oprc = ParseDec(tOprc.Text),
                Orderdate = s.Orderdate, Ty = tTy.Text.Trim(), Fa = tFa.Text.Trim(), Unit = tUnit.Text.Trim(),
                Dc = tDc.Text.Trim(), Note = tNote.Text.Trim(), Gyname = s.Gyname,
                Gname = tGname.Text.Trim(), Fkfs = cbFkfs.SelectedItem?.ToString() ?? "", Bzlx = s.Bzlx,
            };
            await Db.Order3.UpdateAsync(s.Id, input, Cp.Trim(), ct);
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteLineAsync(CancellationToken ct)
    {
        if (SelectedRow is null) return;
        if (MessageBox.Show($"确认删除行 {SelectedRow.Cno.Trim()} 第 {SelectedRow.Item} 项（{SelectedRow.Nno.Trim()}）吗？",
                "订货库管理", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Db.Order3.DeleteAsync(SelectedRow.Id, ct);
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<ContractHeader?> FetchHeaderAsync(string cno)
        => await Db.Order3.GetContractHeaderAsync(cno.Trim(), AppSession.DeptNo, Cp.Trim());

    private static decimal ParseDec(string s) => decimal.TryParse(s?.Trim(), out var v) ? v : 0;

    // ---------- 批改 ----------

    [RelayCommand]
    public async Task BatchAsync(CancellationToken ct)
    {
        var d = new FormDialog("批改");
        var cbType = d.AddCombo("批改类型", BatchTypes, BatchTypes[0]);
        var tLoc = d.AddText("定位号(合同/订单)");
        var tNew = d.AddText("新值");
        var cByOrno = d.AddCheck("改订单号按订单号定位（否则按合同号）");
        var cRange = d.AddCheck("改合同号限定项号范围");
        var tFrom = d.AddText("项号从", "");
        var tTo = d.AddText("项号到", "");
        var dpEta = d.AddDate("预计到货", DateTime.Today);
        if (!d.ShowOkCancel()) return;

        try
        {
            IsBusy = true;
            Message = "";
            var type = cbType.SelectedItem?.ToString() ?? "";
            var loc = tLoc.Text.Trim();
            var dpm = AppSession.DeptNo;
            var cp = Cp.Trim();
            int n = type switch
            {
                "改合同号" => await Db.Order3.ChangeCnoAsync(loc, tNew.Text.Trim(),
                    cRange.IsChecked == true ? (int?)ParseDec(tFrom.Text) : null,
                    cRange.IsChecked == true ? (int?)ParseDec(tTo.Text) : null, dpm, cp, ct),
                "重排item" => await Db.Order3.ReorderItemsAsync(loc, dpm, cp, ct),
                "删除合同" => await ConfirmThenAsync(() => Db.Order3.DeleteContractAsync(loc, dpm, cp, ct)),
                "恢复合同" => await Db.Order3.RestoreContractAsync(loc, dpm, cp, ct),
                "改订单号" => await Db.Order3.ChangeOrnoAsync(loc, tNew.Text.Trim(), cByOrno.IsChecked == true, dpm, cp, ct),
                "改客户" => await Db.Order3.ChangeGnameAsync(loc, tNew.Text.Trim(), dpm, cp, ct),
                "改供应商" => await Db.Order3.ChangeGynameAsync(loc, tNew.Text.Trim(), dpm, cp, ct),
                "改预计到货" => await Db.Order3.ChangeEtaAsync(loc, dpEta.SelectedDate ?? DateTime.Today, dpm, cp, ct),
                _ => 0,
            };
            MessageBox.Show($"批改完成，更新 {n} 行。", "订货库管理", MessageBoxButton.OK, MessageBoxImage.Information);
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<int> ConfirmThenAsync(Func<Task<int>> action)
    {
        if (MessageBox.Show("确认执行删除吗？该操作不可恢复。", "订货库管理", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return 0;
        return await action();
    }

    // ---------- 合并散单 ----------

    [RelayCommand]
    public async Task MergeAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var unmerged = await Db.Order3.GetUnmergedAsync(AppSession.DeptNo, Cp.Trim(), ct);
            var d = new FormDialog("合并散单");
            var tCno = d.AddText("目标合同号");
            var tGy = d.AddText("供应商");
            var cbFkfs = d.AddCombo("付款方式", FkfsOptions, null);
            d.AddGrid<UnmergedOrderRow>(unmerged, g =>
            {
                g.Columns.Add(new DataGridTextColumn { Header = "订单号", Binding = new Binding(nameof(UnmergedOrderRow.Orno)), IsReadOnly = true, Width = 110 });
                g.Columns.Add(new DataGridTextColumn { Header = "金额", Binding = new Binding(nameof(UnmergedOrderRow.Iprj)) { Mode = BindingMode.OneWay }, IsReadOnly = true, Width = 70 });
                g.Columns.Add(new DataGridTextColumn { Header = "订货日期", Binding = new Binding(nameof(UnmergedOrderRow.Orderdate)) { Mode = BindingMode.OneWay, StringFormat = "yyyy-MM-dd" }, IsReadOnly = true, Width = 90 });
                g.Columns.Add(new DataGridTextColumn { Header = "付款方式", Binding = new Binding(nameof(UnmergedOrderRow.Fkfs)), IsReadOnly = true, Width = 80 });
                g.Columns.Add(new DataGridTextColumn { Header = "供应商", Binding = new Binding(nameof(UnmergedOrderRow.Gyname)), IsReadOnly = true, Width = 120 });
                g.Columns.Add(new DataGridCheckBoxColumn { Header = "合并", Binding = new Binding(nameof(UnmergedOrderRow.Mark)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 50 });
            });
            if (!d.ShowOkCancel()) return;
            var ornos = unmerged.Where(u => u.Mark).Select(u => u.Orno).ToList();
            if (ornos.Count == 0)
            {
                MessageBox.Show("请勾选要合并的散单。", "订货库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(tCno.Text))
            {
                MessageBox.Show("目标合同号不能为空。", "订货库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var n = await Db.Order3.MergeOrdersAsync(tCno.Text.Trim(), tGy.Text.Trim(),
                cbFkfs.SelectedItem?.ToString() ?? "", ornos, AppSession.DeptNo, Cp.Trim(), ct);
            MessageBox.Show($"已合并 {n} 行到合同 {tCno.Text.Trim()}。", "订货库管理", MessageBoxButton.OK, MessageBoxImage.Information);
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
