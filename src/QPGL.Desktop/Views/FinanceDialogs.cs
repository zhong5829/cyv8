﻿using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.Views;

/// <summary>出纳管理 G 组对话框（代码构造，字段与旧表单一致）。</summary>
public static class FinanceDialogBuilder
{
    internal static Grid BuildForm(IEnumerable<(string Label, FrameworkElement Field)> items)
    {
        var grid = new Grid { Margin = new Thickness(12) };
        var list = items.ToList();
        for (var i = 0; i < list.Count; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = 0;
        foreach (var (label, field) in list)
        {
            grid.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4),
            });
            Grid.SetRow(grid.Children[^1], row);
            Grid.SetColumn(grid.Children[^1], 0);
            field.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetColumn(field, 1);
            Grid.SetRow(field, row);
            grid.Children.Add(field);
            row++;
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    internal static StackPanel Footer(Window win, Action accept)
    {
        var ok = new Button { Content = "确定", Width = 72, IsDefault = true, Padding = new Thickness(0, 3, 0, 3) };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0, 3, 0, 3) };
        ok.Click += (_, _) => accept();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Children = { ok, cancel },
        };
    }
}

public sealed class YsyfCustomerWindow : Window
{
    private readonly TextBox _gname = new() { Width = 240 };
    private readonly TextBox _cb = new() { Width = 120 };
    private readonly TextBox _tick = new() { Width = 120 };
    private readonly TextBox _yjb = new() { Width = 120 };
    private readonly TextBox _htno = new() { Width = 240 };
    private readonly TextBox _ywy = new() { Width = 120 };
    private readonly TextBox _cwfzr = new() { Width = 120 };
    private readonly bool _isEdit;

    public YsyfCustomerCommand? Result { get; private set; }

    public YsyfCustomerWindow(string title, YsyfSummaryRow? source)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _isEdit = source is not null;
        if (source is not null)
        {
            _gname.Text = source.Gname;
            _cb.Text = source.Cb.ToString("0.##");
            _tick.Text = source.Tick.ToString("0.##");
            _yjb.Text = source.Yjb.ToString("0.##");
            _ywy.Text = source.Ywy;
            _cwfzr.Text = source.Cwfzr;
        }
        var items = new List<(string, FrameworkElement)>
        {
            ("客户名称", _gname),
            ("余款(cb)", _cb),
            ("点数(tick)", _tick),
            ("押金(yjb)", _yjb),
            ("合同号(htno)", _htno),
            ("业务员", _ywy),
            ("财负责人", _cwfzr),
        };
        var grid = FinanceDialogBuilder.BuildForm(items);
        var dock = new DockPanel();
        DockPanel.SetDock(grid, Dock.Top);
        dock.Children.Add(grid);
        dock.Children.Add(FinanceDialogBuilder.Footer(this, Accept));
        Content = dock;
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_gname.Text)) { MessageBox.Show("客户名称为空！", Title); return; }
        if (!decimal.TryParse(_cb.Text, out var cb)) cb = 0;
        if (!decimal.TryParse(_tick.Text, out var tick)) tick = 0;
        if (!decimal.TryParse(_yjb.Text, out var yjb)) yjb = 0;
        Result = new YsyfCustomerCommand(_gname.Text.Trim(), cb, tick, yjb, _htno.Text.Trim(), _ywy.Text.Trim(), _cwfzr.Text.Trim());
        DialogResult = true;
    }
}

public sealed class YsyfDetailWindow : Window
{
    private static readonly string[] Reserved = ["销售", "入库", "收款", "付款", "维修"];
    private readonly DatePicker _datime = new() { Width = 140, SelectedDate = DateTime.Today };
    private readonly TextBox _cno = new() { Width = 240 };
    private readonly TextBox _xm = new() { Width = 240 };
    private readonly TextBox _pno = new() { Width = 240 };
    private readonly TextBox _ys = new() { Width = 120, Text = "0" };
    private readonly TextBox _yf = new() { Width = 120, Text = "0" };
    private readonly TextBox _type = new() { Width = 120 };
    private readonly TextBox _fkfs = new() { Width = 120 };
    private readonly TextBox _note = new() { Width = 240 };
    private readonly string? _editIdType;

    public YsyfDetailCommand? Result { get; private set; }

    public YsyfDetailWindow(string title, string gname, YsyfDetailRow? source)
    {
        Title = $"{title} — {gname}";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (source is not null)
        {
            _datime.SelectedDate = source.Datime == default ? DateTime.Today : source.Datime;
            _cno.Text = source.Cno;
            _xm.Text = source.Xm;
            _pno.Text = source.Pno;
            _ys.Text = source.Ys.ToString("0.##");
            _yf.Text = source.Yf.ToString("0.##");
            _type.Text = source.Type;
            _fkfs.Text = source.Fkfs;
            _note.Text = source.Note;
            _editIdType = source.Type;
        }
        var items = new List<(string, FrameworkElement)>
        {
            ("日期", _datime),
            ("单据号", _cno),
            ("项目", _xm),
            ("发票号", _pno),
            ("应收(ys)", _ys),
            ("应付(yf)", _yf),
            ("类型", _type),
            ("付款方式", _fkfs),
            ("备注", _note),
        };
        var grid = FinanceDialogBuilder.BuildForm(items);
        var dock = new DockPanel();
        DockPanel.SetDock(grid, Dock.Top);
        dock.Children.Add(grid);
        dock.Children.Add(FinanceDialogBuilder.Footer(this, Accept));
        Content = dock;
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_type.Text))
        {
            MessageBox.Show("类型不能为空！", Title);
            return;
        }
        var type = _type.Text.Trim();
        if (type != _editIdType && Reserved.Contains(type))
        {
            MessageBox.Show("销售、入库、收款、付款、维修 是系统保留的关键字！不能使用这些类型名称！", Title);
            return;
        }
        if (!decimal.TryParse(_ys.Text, out var ys)) ys = 0;
        if (!decimal.TryParse(_yf.Text, out var yf)) yf = 0;
        Result = new YsyfDetailCommand("", _datime.SelectedDate ?? DateTime.Today, _cno.Text.Trim(),
            _xm.Text, _pno.Text.Trim(), ys, yf, type, _fkfs.Text.Trim(), _note.Text);
        DialogResult = true;
    }
}

public sealed class LszEditWindow : Window
{
    private readonly DatePicker _lsDate = new() { Width = 140, SelectedDate = DateTime.Today };
    private readonly TextBox _skno = new() { Width = 200 };
    private readonly TextBox _ytu = new() { Width = 200 };
    private readonly TextBox _ys = new() { Width = 120, Text = "0" };
    private readonly TextBox _yf = new() { Width = 120, Text = "0" };
    private readonly TextBox _remark = new() { Width = 240 };
    private readonly TextBox _gname = new() { Width = 240 };
    private readonly TextBox _note = new() { Width = 240 };
    private readonly TextBox _guno = new() { Width = 120 };

    public LszManualCommand? Result { get; private set; }

    public LszEditWindow(string title, string bankName, LszRow? source)
    {
        Title = $"{title} — {bankName}";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (source is not null)
        {
            if (DateTime.TryParse(source.LsDate, out var d)) _lsDate.SelectedDate = d;
            _skno.Text = source.Skno;
            _ytu.Text = source.Ytu;
            _ys.Text = source.Ys.ToString("0.##");
            _yf.Text = source.Yf.ToString("0.##");
            _remark.Text = source.Remark;
            _gname.Text = source.Gname;
            _note.Text = source.Note;
            _guno.Text = source.Guno;
        }
        var items = new List<(string, FrameworkElement)>
        {
            ("日期", _lsDate),
            ("单号(skno)", _skno),
            ("业务(ytu)", _ytu),
            ("收入(ys)", _ys),
            ("支出(yf)", _yf),
            ("备注", _remark),
            ("客户", _gname),
            ("说明(note)", _note),
            ("去向(guno)", _guno),
        };
        var grid = FinanceDialogBuilder.BuildForm(items);
        var dock = new DockPanel();
        DockPanel.SetDock(grid, Dock.Top);
        dock.Children.Add(grid);
        dock.Children.Add(FinanceDialogBuilder.Footer(this, Accept));
        Content = dock;
    }

    private void Accept()
    {
        if (_lsDate.SelectedDate is null) { MessageBox.Show("请输入日期！", Title); return; }
        if (!decimal.TryParse(_ys.Text, out var ys)) ys = 0;
        if (!decimal.TryParse(_yf.Text, out var yf)) yf = 0;
        Result = new LszManualCommand(_lsDate.SelectedDate.Value, _skno.Text.Trim(), _ytu.Text.Trim(),
            ys, yf, _remark.Text, _gname.Text.Trim(), _note.Text, _guno.Text.Trim());
        DialogResult = true;
    }
}

public sealed class LszInitialWindow : Window
{
    private readonly TextBox _amount = new() { Width = 140, Text = "0" };

    public decimal Amount { get; private set; }

    public LszInitialWindow()
    {
        Title = "初始累计数";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "初始累计金额（>0 记收入，否则记支出）", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_amount);
        var ok = new Button { Content = "确定", Width = 72, IsDefault = true, Padding = new Thickness(0, 3, 0, 3) };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0, 3, 0, 3) };
        ok.Click += (_, _) =>
        {
            if (!decimal.TryParse(_amount.Text, out var amt))
            {
                MessageBox.Show("金额格式不正确", Title);
                return;
            }
            Amount = amt;
            DialogResult = true;
        };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { ok, cancel },
        });
        Content = panel;
    }
}

/// <summary>
/// 前十二月应收应付及余额窗（fr_ysyf_12y）：Grd1 只读 14 行（期初 + 12 月 + 累计），
/// 关闭按钮（row3 Click RELEASE / Esc）。
/// </summary>
public sealed class YsyfTwelveMonthWindow : Window
{
    public YsyfTwelveMonthWindow(string caption, IReadOnlyList<YsyfTwelveMonthRow> rows)
    {
        Title = caption;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid
        {
            ItemsSource = rows,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            FontSize = 12,
            Margin = new Thickness(12),
            MinHeight = 220,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "月份", Binding = new Binding("Cmonth"), Width = new DataGridLength(110) });
        grid.Columns.Add(new DataGridTextColumn { Header = "应收", Binding = new Binding("Ys") { StringFormat = "N2" }, Width = new DataGridLength(120), ElementStyle = (Style)Application.Current.Resources["NumCellRight"] });
        grid.Columns.Add(new DataGridTextColumn { Header = "应付", Binding = new Binding("Yf") { StringFormat = "N2" }, Width = new DataGridLength(120), ElementStyle = (Style)Application.Current.Resources["NumCellRight"] });
        grid.Columns.Add(new DataGridTextColumn { Header = "余额", Binding = new Binding("Ye") { StringFormat = "N2" }, Width = new DataGridLength(120), ElementStyle = (Style)Application.Current.Resources["NumCellRight"] });
        var close = new Button { Content = "关闭", Width = 72, IsCancel = true, Padding = new Thickness(0, 3, 0, 3) };
        var dock = new DockPanel();
        DockPanel.SetDock(grid, Dock.Top);
        dock.Children.Add(grid);
        dock.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Children = { close },
        });
        Content = dock;
    }
}
