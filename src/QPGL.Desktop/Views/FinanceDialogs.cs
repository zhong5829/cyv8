﻿using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using QPGL.Desktop.Services;
using QPGL.Domain.Partners;
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

/// <summary>
/// 添加/修改客户窗（复刻旧 fr_ysyf FMSGU 表单 UI，2026-10-29 需求）：
/// 行1 单位名称独占——支持客户档案拼音码联想（tbgu：code 拼音码/code1 数字码/名称/编号/电话，旧 cmm_class FRDM
/// 机制，同销售开单 GnameBox），↓/↑ 方向键选择、回车确认（回填名称 + 预填业务主管=tbg u.ywy）、Esc 关闭；
/// 行2 业务主管 / 财务负责人（下拉，名单 = tbkey.per WHERE dpmbh=部门，旧 __.row2 set_ywy/set_cwfzr）；
/// 行3 限额 / 警戒限额 / 赊数期；底部 K 确定 / C 取消（Alt 快捷键同旧热键）。
/// 添加语义：确认后 INSERT tbysyf（重名拦截）→ 客户出现在汇总列表（VM 刷新）；删除 = 从列表移出（余额为 0 才允许）。
/// 旧表单无合同号输入，Htno 传空。
/// </summary>
public sealed class YsyfCustomerWindow : Window
{
    private readonly TextBox _gname = new();
    private readonly Popup _suggestPopup = new()
    {
        Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, MinWidth = 320,
    };
    private readonly ListBox _suggestList = new()
    {
        MaxHeight = 260, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.White,
    };
    private readonly ObservableCollection<Partner> _suggestions = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly ComboBox _ywy = new() { IsEditable = true };
    private readonly ComboBox _cwfzr = new() { IsEditable = true };
    private readonly TextBox _cb = new() { Text = "0.00" };
    private readonly TextBox _tick = new() { Text = "0.00" };
    private readonly TextBox _yjb = new() { Text = "0" };

    public YsyfCustomerCommand? Result { get; private set; }

    public YsyfCustomerWindow(string title, YsyfSummaryRow? source)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (source is not null)
        {
            _gname.Text = source.Gname;
            _cb.Text = source.Cb.ToString("0.##");
            _tick.Text = source.Tick.ToString("0.##");
            _yjb.Text = source.Yjb.ToString("0.##");
            _ywy.Text = source.Ywy?.Trim() ?? "";
            _cwfzr.Text = source.Cwfzr?.Trim() ?? "";
        }

        // 客户档案拼音码联想（同 YsyfControl GnameBox / SaleControl cmm_class 机制）
        _suggestList.ItemsSource = _suggestions;
        _suggestList.ItemTemplate = CustomerSuggestTemplate();
        _suggestList.MouseLeftButtonUp += (_, _) => ApplySuggestion();
        _suggestPopup.Child = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = (System.Windows.Media.Brush)FindResource("PanelBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 2, 8, 8),
            Child = _suggestList,
        };
        _suggestPopup.PlacementTarget = _gname;
        _gname.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); _suggestPopup.IsOpen = true; };
        _gname.PreviewKeyDown += Gname_PreviewKeyDown;
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SearchSuggestionsAsync(); };
        _ = SearchSuggestionsAsync();

        _ = LoadStaffAsync();

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < 6; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = i % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });

        void AddLabel(string text, int row, int col)
        {
            var l = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 8, 6),
            };
            Grid.SetRow(l, row);
            Grid.SetColumn(l, col);
            grid.Children.Add(l);
        }

        void AddField(FrameworkElement f, int row, int col, int span = 1)
        {
            f.Margin = new Thickness(0, 3, 8, 3);
            Grid.SetRow(f, row);
            Grid.SetColumn(f, col);
            if (span > 1) Grid.SetColumnSpan(f, span);
            grid.Children.Add(f);
        }

        AddLabel("单位名称", 0, 0);
        AddField(_gname, 0, 1, 5);
        AddLabel("业务主管", 1, 0);
        AddField(_ywy, 1, 1);
        AddLabel("财务负责人", 1, 2);
        AddField(_cwfzr, 1, 3, 3);
        AddLabel("限 额", 2, 0);
        AddField(_cb, 2, 1);
        AddLabel("警戒限额", 2, 2);
        AddField(_tick, 2, 3);
        AddLabel("赊数期", 2, 4);
        AddField(_yjb, 2, 5);

        var ok = new Button { Content = "_K 确定", Width = 76, IsDefault = true, Padding = new Thickness(0, 3, 0, 3) };
        var cancel = new Button { Content = "_C 取消", Width = 76, IsCancel = true, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(0, 3, 0, 3) };
        ok.Click += (_, _) => Accept();

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 4),
            Children = { ok, cancel },
        });
        Content = panel;
    }

    /// <summary>联想行模板：编号 / 名称 / 联系人 / 手机（与销售开单客户下拉一致）。</summary>
    private static DataTemplate CustomerSuggestTemplate()
    {
        var template = new DataTemplate();
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(StackPanel.MarginProperty, new Thickness(2));
        AddText(panel, "PartNo", 70, "#666666");
        AddText(panel, "PartName", 170, null);
        AddText(panel, "LinkMan", 70, "#888888");
        AddText(panel, "Mobile", null, "#888888");
        template.VisualTree = panel;
        return template;

        static void AddText(FrameworkElementFactory parent, string prop, double? width, string? color)
        {
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding(prop));
            if (width is not null) tb.SetValue(WidthProperty, width.Value);
            if (color is not null) tb.SetValue(TextBlock.ForegroundProperty,
                new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)));
            parent.AppendChild(tb);
        }
    }

    /// <summary>客户档案联想搜索（tbgu 按 拼音码/数字码/名称/编号/电话 匹配，取 200 条）。</summary>
    private async System.Threading.Tasks.Task SearchSuggestionsAsync()
    {
        var kw = _gname.Text.Trim();
        if (kw.Length == 0) { _suggestions.Clear(); return; }
        try
        {
            var request = new QPGL.Domain.Common.PagedRequest { Page = 1, PageSize = 200, Sort = "partName", Order = "asc", Keyword = kw };
            var result = await Db.Partners.SearchAsync(request);
            _suggestions.Clear();
            foreach (var p in result.Items) _suggestions.Add(p);
        }
        catch { /* 联想失败不阻断输入 */ }
    }

    /// <summary>↓/↑ 在输入框内移动列表选中项（焦点不跳出）；回车：有联想选中 → 回填；无 → 落到默认确定按钮；Esc 关闭。</summary>
    private void Gname_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (_suggestList.Items.Count > 0)
                {
                    _suggestPopup.IsOpen = true;
                    _suggestList.SelectedIndex = Math.Min(_suggestList.SelectedIndex + 1, _suggestList.Items.Count - 1);
                    _suggestList.ScrollIntoView(_suggestList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (_suggestList.Items.Count > 0 && _suggestList.SelectedIndex > 0)
                {
                    _suggestList.SelectedIndex--;
                    _suggestList.ScrollIntoView(_suggestList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (_suggestPopup.IsOpen && _suggestList.SelectedItem is Partner p)
                {
                    ApplyPartner(p);
                    e.Handled = true;
                }
                // 无选中项时交还默认按钮（确定）语义
                break;
            case Key.Escape:
                _suggestPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>选中联想项：回填客户名称，预填业务主管（tbgu.ywy），关闭下拉，焦点回输入框。</summary>
    private void ApplyPartner(Partner p)
    {
        _gname.Text = p.PartName.Trim();
        _suggestPopup.IsOpen = false;
        _suggestions.Clear();
        _gname.Focus();
        _gname.CaretIndex = _gname.Text.Length;
        if (_ywy.Text.Trim().Length == 0 && p.SalesMan.Trim().Length > 0)
            _ywy.Text = p.SalesMan.Trim();
    }

    private void ApplySuggestion()
    {
        if (_suggestList.SelectedItem is Partner p) ApplyPartner(p);
    }

    /// <summary>业务主管/财务负责人名单（tbkey.per 按部门）；失败不阻断编辑（留空可手填，IsEditable）。</summary>
    private async System.Threading.Tasks.Task LoadStaffAsync()
    {
        try
        {
            var list = await Db.Ysyf.GetStaffListAsync(AppSession.DeptNo);
            _ywy.ItemsSource = list;
            _cwfzr.ItemsSource = list;
        }
        catch { /* 名单加载失败不阻断 */ }
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_gname.Text)) { MessageBox.Show("单位名称为空！", Title); return; }
        if (!decimal.TryParse(_cb.Text, out var cb)) cb = 0;
        if (!decimal.TryParse(_tick.Text, out var tick)) tick = 0;
        if (!decimal.TryParse(_yjb.Text, out var yjb)) yjb = 0;
        Result = new YsyfCustomerCommand(_gname.Text.Trim(), cb, tick, yjb, "", _ywy.Text.Trim(), _cwfzr.Text.Trim());
        DialogResult = true;
    }
}

/// <summary>
/// 添加/修改明细窗（复刻旧 fr_ysyf「添加修改项目」表单，2026-10-29 需求）：
/// 单列布局：日 期 / 类 型（下拉=tbsysset type='业务类型'，旧 ___.row129）/ 单据号 / 发票号 / 应 收 / 应 付 /
/// 项 目 / 收款方式（下拉=tbsysset type='guno'，旧 ___.row175）/ 财务备注（多行）；底部 K 确定 / C 取消。
/// 修改保留字类型行（销售/入库/收款/付款/维修）且非超权时禁用 类型/单据号/应收/应付/收款方式（旧 鹾_.row111 L10-17）。
/// </summary>
public sealed class YsyfDetailWindow : Window
{
    private static readonly string[] Reserved = ["销售", "入库", "收款", "付款", "维修"];
    private static readonly System.Windows.Media.Brush NoteBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xE1)); // 旧黄底
    private readonly DatePicker _datime = new() { Width = 160, SelectedDate = DateTime.Today };
    private readonly ComboBox _type = new() { Width = 160, IsEditable = true };
    private readonly TextBox _cno = new();
    private readonly TextBox _pno = new();
    private readonly TextBox _ys = new() { Text = "0.00", HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly TextBox _yf = new() { Text = "0.00", HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly TextBox _xm = new();
    private readonly ComboBox _fkfs = new() { IsEditable = true };
    private readonly TextBox _note = new()
    {
        AcceptsReturn = true, Height = 56,
        Background = NoteBrush, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly string? _editIdType;

    public YsyfDetailCommand? Result { get; private set; }

    public YsyfDetailWindow(string title, string gname, YsyfDetailRow? source)
    {
        Title = $"添加修改项目 — {gname}";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        bool lockReserved = false;
        if (source is not null)
        {
            _datime.SelectedDate = source.Datime == default ? DateTime.Today : source.Datime;
            _cno.Text = source.Cno;
            _xm.Text = source.Xm;
            _pno.Text = source.Pno;
            _ys.Text = source.Ys.ToString("0.00");
            _yf.Text = source.Yf.ToString("0.00");
            _type.Text = source.Type;
            _fkfs.Text = source.Fkfs;
            _note.Text = source.Note;
            _editIdType = source.Type;
            // 旧 鹾_.row111：修改保留字类型行（销售/维修/收款/付款/入库）且非 '@' 超权 → 锁定关键字段
            lockReserved = Reserved.Contains(source.Type.Trim()) && !AppSession.Permission.IsSuper;
        }
        else
        {
            _fkfs.Text = "";
        }

        _ = LoadOptionsAsync();

        var grid = new Grid { Margin = new Thickness(12) };
        for (var i = 0; i < 9; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labels = new[] { "日 期", "类 型", "单据号", "发票号", "应 收", "应 付", "项 目", "收款方式", "财务备注" };
        var fields = new FrameworkElement[] { _datime, _type, _cno, _pno, _ys, _yf, _xm, _fkfs, _note };
        for (var i = 0; i < labels.Length; i++)
        {
            var l = new TextBlock
            {
                Text = labels[i],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 10, 4),
            };
            Grid.SetRow(l, i);
            Grid.SetColumn(l, 0);
            grid.Children.Add(l);
            var f = fields[i];
            f.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(f, i);
            Grid.SetColumn(f, 1);
            grid.Children.Add(f);
        }

        // 旧 鹾_.row111：保留字行修改时锁定 类型/单据号/应收/应付/收款方式
        if (lockReserved)
        {
            _type.IsEnabled = false;
            _cno.IsEnabled = false;
            _ys.IsEnabled = false;
            _yf.IsEnabled = false;
            _fkfs.IsEnabled = false;
        }

        var ok = new Button { Content = "_K 确定", Width = 76, IsDefault = true, Padding = new Thickness(0, 3, 0, 3) };
        var cancel = new Button { Content = "_C 取消", Width = 76, IsCancel = true, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(0, 3, 0, 3) };
        ok.Click += (_, _) => Accept();

        Content = new StackPanel
        {
            Children =
            {
                grid,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 4),
                    Children = { ok, cancel },
                },
            },
        };
    }

    /// <summary>类型/收款方式下拉名单（tbsysset 业务类型/guno）；失败不阻断（可手输，IsEditable）。</summary>
    private async System.Threading.Tasks.Task LoadOptionsAsync()
    {
        try
        {
            _type.ItemsSource = await Db.Ysyf.GetTypeOptionsAsync();
            _fkfs.ItemsSource = await Db.Ysyf.GetFkfsOptionsAsync();
        }
        catch { /* 名单加载失败不阻断 */ }
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
