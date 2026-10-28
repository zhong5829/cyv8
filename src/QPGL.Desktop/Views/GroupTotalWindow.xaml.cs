using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Views;

/// <summary>分组汇总聚合列（各页签合计公式参数化：fr_total SUMSTR 语义）。</summary>
public sealed record TotalColumn(string Caption, char Type, Func<IEnumerable<QueryRow>, object> Compute);

/// <summary>
/// fr_total 复刻：可选分组字段（非数值列）→ 按组聚合 → 末行"合计"（首列 C 型）→
/// dpm 分组回填公司名称（tbdpm）→ 结果窗排序（合计行钉底）/打印（标题"数据汇总结果"）。
/// 图表（VFP Graph OLE）备案不实现。
/// </summary>
public partial class GroupTotalWindow : Window
{
    private readonly IReadOnlyList<QueryRow> _rows;
    private readonly IReadOnlyList<TotalColumn> _totals;
    private readonly IReadOnlyList<QueryColumnSpec> _columns;
    private readonly IReadOnlyDictionary<string, string> _dpmNames;
    private readonly string _company;

    public GroupTotalWindow(
        IReadOnlyList<QueryRow> rows,
        IReadOnlyList<QueryColumnSpec> columns,
        IReadOnlyList<TotalColumn> totals,
        IReadOnlyDictionary<string, string> dpmNames,
        string company)
    {
        InitializeComponent();
        _rows = rows;
        _columns = columns;
        _totals = totals;
        _dpmNames = dpmNames;
        _company = company;
        // 旧码 LST1 候选 = 非数值字段（TYPE(field)!='N'），显示名取 tbstru.EXPLAIN
        foreach (var c in columns.Where(c => c.Type != 'N'))
            Lst1.Items.Add(c);
        Lst1.DisplayMemberPath = nameof(QueryColumnSpec.Caption);
        Lst2.DisplayMemberPath = nameof(QueryColumnSpec.Caption);
        if (Lst1.Items.Count > 0) Lst1.SelectedIndex = 0;
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (Lst1.SelectedIndex < 0) return;
        Lst2.Items.Add(Lst1.SelectedItem);
        Lst1.Items.RemoveAt(Lst1.SelectedIndex);
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Lst2.SelectedIndex < 0) return;
        Lst1.Items.Add(Lst2.SelectedItem);
        Lst2.Items.RemoveAt(Lst2.SelectedIndex);
    }

    private void BtnAddAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Lst1.Items.Cast<QueryColumnSpec>().ToList())
        {
            Lst2.Items.Add(item);
            Lst1.Items.Remove(item);
        }
    }

    private void BtnRemoveAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Lst2.Items.Cast<QueryColumnSpec>().ToList())
        {
            Lst1.Items.Add(item);
            Lst2.Items.Remove(item);
        }
    }

    private void Lst1_DoubleClick(object sender, MouseButtonEventArgs e) => BtnAdd_Click(sender, e);
    private void Lst2_DoubleClick(object sender, MouseButtonEventArgs e) => BtnRemove_Click(sender, e);

    private void BtnExit_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var groupFields = Lst2.Items.Cast<QueryColumnSpec>().ToList();
        var result = ComputeResult(groupFields);
        var resultWindow = new GroupTotalResultWindow(
            result.rows, result.columns, _dpmNames,
            groupFields.Count > 0, groupFields, _company);
        resultWindow.Owner = Owner;
        Hide();
        resultWindow.ShowDialog();
        Close();
    }

    /// <summary>分组聚合 + 末行合计（旧 c_public_count 语义）。</summary>
    internal (List<QueryRow> rows, List<QueryColumnSpec> columns) ComputeResult(
        IReadOnlyList<QueryColumnSpec> groupFields)
    {
        var cols = new List<QueryColumnSpec>();
        foreach (var g in groupFields)
            cols.Add(new QueryColumnSpec(g.Field, g.Caption, g.Type == 'T' ? 'D' : g.Type));
        var dpmSpec = groupFields.FirstOrDefault(g => g.Field.Equals("Dpm", StringComparison.OrdinalIgnoreCase));
        if (dpmSpec is not null)
            cols.Add(new QueryColumnSpec("公司名称", "公司名称", 'C', 100));
        foreach (var t in _totals)
            cols.Add(new QueryColumnSpec(t.Caption, t.Caption, t.Type));

        var rows = new List<QueryRow>();
        if (groupFields.Count == 0)
        {
            // 无分组字段：仅一行全局合计（旧合计弹窗行为）
            var dict = new Dictionary<string, object?>();
            foreach (var t in _totals) dict[t.Caption] = t.Compute(_rows);
            rows.Add(new QueryRow(dict));
            return (rows, cols);
        }

        foreach (var grp in _rows.GroupBy(r => GroupKey(r, groupFields)))
        {
            var dict = new Dictionary<string, object?>();
            for (var i = 0; i < groupFields.Count; i++)
            {
                var g = groupFields[i];
                dict[g.Field] = g.Type == 'T' ? ToDate(grp.Key.Values[i]) : grp.Key.Values[i];
            }
            if (dpmSpec is not null)
            {
                var dpm = grp.Key.Values[IndexOfSpec(groupFields, dpmSpec)]?.ToString() ?? "";
                dict["公司名称"] = _dpmNames.TryGetValue(dpm, out var name) ? name : "";
            }
            foreach (var t in _totals) dict[t.Caption] = t.Compute(grp);
            rows.Add(new QueryRow(dict));
        }

        // 全局合计行（旧 select SUMSTR 无 group by 追加；首个 C 型分组字段置"合计"）
        var totalDict = new Dictionary<string, object?>();
        var first = groupFields[0];
        foreach (var g in groupFields) totalDict[g.Field] = "";
        if (dpmSpec is not null) totalDict["公司名称"] = "";
        if (first.Type == 'C') totalDict[first.Field] = "合计";
        foreach (var t in _totals) totalDict[t.Caption] = t.Compute(_rows);
        rows.Add(new QueryRow(totalDict));
        return (rows, cols);
    }

    private static object? ToDate(object? v) => v switch
    {
        DateTime d => d.Date,
        null => null,
        _ => v,
    };

    private static int IndexOfSpec(IReadOnlyList<QueryColumnSpec> list, QueryColumnSpec spec)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], spec)) return i;
        return -1;
    }

    private static RowGroupKey GroupKey(QueryRow row, IReadOnlyList<QueryColumnSpec> fields)
    {
        var vals = new List<object?>();
        var sb = new System.Text.StringBuilder();
        foreach (var f in fields)
        {
            var v = f.Type == 'T' ? ToDate(row.Get(f.Field)) : row.Get(f.Field);
            vals.Add(v);
            sb.Append(v?.ToString() ?? "").Append('\x1');
        }
        return new RowGroupKey(vals, sb.ToString());
    }

    private sealed record RowGroupKey(IReadOnlyList<object?> Values, string Key);
}

/// <summary>分组汇总结果窗（fr_total.Fr_count：行号列 + 合计行绿色 + 排序钉底 + 打印）。</summary>
public partial class GroupTotalResultWindow : Window
{
    private readonly List<QueryRow> _rows;
    private List<QueryColumnSpec> _columns;
    private readonly bool _showRowNumber;
    private readonly IReadOnlyList<QueryColumnSpec> _groupFields;
    private readonly string _company;

    public GroupTotalResultWindow(
        List<QueryRow> rows,
        List<QueryColumnSpec> columns,
        IReadOnlyDictionary<string, string> dpmNames,
        bool showRowNumber,
        IReadOnlyList<QueryColumnSpec> groupFields,
        string company)
    {
        InitializeComponent();
        _rows = rows;
        _columns = columns;
        _showRowNumber = showRowNumber;
        _groupFields = groupFields;
        _company = company;
        BuildColumns();
        FillRowNumbers();
        Grd.ItemsSource = new List<QueryRow>(_rows);
    }

    private void BuildColumns()
    {
        Grd.Columns.Clear();
        if (_showRowNumber)
        {
            Grd.Columns.Add(new DataGridTextColumn
            {
                Header = "行号",
                Binding = new System.Windows.Data.Binding("[行号]"),
                Width = new DataGridLength(40),
                IsReadOnly = true,
            });
        }
        foreach (var c in _columns)
        {
            var binding = new System.Windows.Data.Binding($"[{c.Field}]");
            if (c.Type == 'N') binding.StringFormat = "N2";
            else if (c.Type is 'D' or 'T') binding.StringFormat = "d";
            Grd.Columns.Add(new DataGridTextColumn
            {
                Header = c.Caption,
                Binding = binding,
                Width = new DataGridLength(c.Width),
                IsReadOnly = true,
            });
        }
    }

    /// <summary>行号列（旧 recn('c_public_count')）。</summary>
    private void FillRowNumbers()
    {
        if (!_showRowNumber) return;
        for (var i = 0; i < _rows.Count; i++) _rows[i].Set("行号", i + 1);
    }

    private void Grd_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        // 合计行（末行）浅绿 RGB(221,254,203)，对齐旧 DynamicBackColor
        if (e.Row.GetIndex() == _rows.Count - 1 && _groupFields.Count > 0)
            e.Row.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(221, 254, 203));
        else
            e.Row.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void BtnSort_Click(object sender, RoutedEventArgs e)
    {
        // 旧码：合计行 GO BOTTOM 暂存 → fr_sort → ZAP 重灌 → 合计行放回末尾
        if (_rows.Count == 0) return;
        var totalRow = _rows[^1];
        var body = _rows.Take(_rows.Count - 1).ToList();
        var dialog = new SortDialogWindow(_columns);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true || dialog.ResultKeys is null) return;
        var sorted = QueryGridHelper.SortRows(body, dialog.ResultKeys).ToList();
        sorted.Add(totalRow);
        _rows.Clear();
        _rows.AddRange(sorted);
        FillRowNumbers();
        Grd.ItemsSource = new List<QueryRow>(_rows);
        Grd.Items.Refresh();
    }

    private void BtnPrint_Click(object sender, RoutedEventArgs e)
    {
        // 旧码 ReportMain：CCAPTION="数据汇总结果"，列=当前结果列 + 行号
        var doc = Printing.ListPrintDocument.Build(new Printing.ListPrintRequest
        {
            Caption = "数据汇总结果",
            CprTitle = "数据汇总结果",
            Company = _company,
            Operator = AppSession.Operator,
            Columns = _columns,
            Rows = _rows,
            ShowRowNumber = _showRowNumber,
        });
        var preview = new PrintPreviewWindow(doc, "预览 - 数据汇总结果");
        preview.Owner = this;
        preview.ShowDialog();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
