using System.Text;
using System.Windows;
using System.Windows.Input;
using QPGL.Desktop.Services;
using QPGL.Infrastructure.Query;

namespace QPGL.Desktop.Views;

/// <summary>
/// fr_mdefi 模式③（FrmWdata/FrmSeleNno/FrmSField/FrmAccount 合并）：外部 CSV（旧 XLS/DBF 备案）
/// → 厂牌/编码映射列 → 字段聚合（应用层 GroupBy）→ 按 CP+NNO（或 NNO）回填当前结果集。
/// 旧语义同模式②：任一字段选"原值"→ 不分组取全部行。
/// </summary>
public partial class CustomFieldExtWindow : Window
{
    private readonly IReadOnlyList<QueryRow> _rows;
    private readonly string _file;
    private readonly Func<string, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> _loadCsv;
    private readonly List<ExtAggRow> _selected = [];
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _extRows = [];
    private IReadOnlyList<string> _extColumns = [];

    public CustomFieldOutcome? Outcome { get; private set; }

    public CustomFieldExtWindow(
        IReadOnlyList<QueryRow> rows,
        string file,
        Func<string, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> loadCsv)
    {
        InitializeComponent();
        _rows = rows;
        _file = file;
        _loadCsv = loadCsv;
        TxtFile.Text = file;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _extRows = await _loadCsv(_file);
            _extColumns = _extRows.Count == 0 ? [] : _extRows[0].Keys.ToList();
            CboCp.ItemsSource = _extColumns;
            CboNno.ItemsSource = _extColumns;
            if (_extColumns.Count > 0) CboNno.SelectedIndex = 0;
            foreach (var name in _extColumns)
                LstYField.Items.Add(name);
        }
        catch (Exception ex)
        {
            MessageBox.Show("外部数据加载失败：" + ex.Message, "提示");
        }
    }

    private sealed class ExtAggRow(string field)
    {
        public string Field { get; } = field;
        public string Explain => Field;
        public char SourceType => 'C';
        public IReadOnlyList<KeyValuePair<int, string>> Methods { get; } =
        [
            new(1, "原值"), new(2, "最大值"), new(3, "最小值"), new(4, "合计"), new(5, "平均值"),
        ];
        public int Method { get; set; } = 1;
        public string NewName { get; set; } = "";
    }

    private void RefreshAggGrid()
    {
        GrdAgg.ItemsSource = null;
        GrdAgg.ItemsSource = _selected;
        GrdAgg.Items.Refresh();
    }

    private void MoveSelected(string name)
    {
        if (_selected.Any(s => s.Field == name)) return;
        _selected.Add(new ExtAggRow(name));
        RefreshAggGrid();
    }

    private void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        if (LstYField.SelectedItem is string name) { MoveSelected(name); LstYField.Items.Remove(name); }
    }

    private void BtnUnselect_Click(object sender, RoutedEventArgs e)
    {
        if (GrdAgg.SelectedItem is ExtAggRow row)
        {
            _selected.Remove(row);
            LstYField.Items.Add(row.Field);
            RefreshAggGrid();
        }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var name in LstYField.Items.Cast<string>().ToList()) { MoveSelected(name); LstYField.Items.Remove(name); }
    }

    private void BtnUnselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _selected.ToList())
            LstYField.Items.Add(row.Field);
        _selected.Clear();
        RefreshAggGrid();
    }

    private void LstYField_DoubleClick(object sender, MouseButtonEventArgs e) => BtnSelect_Click(sender, e);

    private void BtnBack_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void BtnExit_Click(object sender, RoutedEventArgs e) { Outcome = null; DialogResult = false; }

    private void BtnFinish_Click(object sender, RoutedEventArgs e)
    {
        var cpCol = CboCp.SelectedItem as string;
        var nnoCol = CboNno.SelectedItem as string;
        if (string.IsNullOrEmpty(nnoCol))
        {
            MessageBox.Show("请选择编码所对应的字段！", "提示");
            return;
        }
        if (cpCol == nnoCol && !string.IsNullOrEmpty(cpCol))
        {
            MessageBox.Show("厂牌和编码代码不能为同一字段！", "提示");
            return;
        }
        var items = new List<AggregateItem>();
        foreach (var row in _selected)
        {
            var newName = (row.NewName ?? "").Trim();
            if (newName.Length == 0) { MessageBox.Show("请输入聚合字段字段名称！", "提示"); return; }
            if (newName.Length > 10) { MessageBox.Show("字段名称不能大于10", "提示"); return; }
            if (newName.Equals("cp", StringComparison.OrdinalIgnoreCase) || newName.Equals("nno", StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show("聚合字段名不可取'CP','NNO'!", "提示"); return; }
            if (_rows.SelectMany(r => r.GetDynamicMemberNames()).Any(f => f.Equals(newName, StringComparison.OrdinalIgnoreCase))
                || items.Any(i => i.NewName.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            { MessageBox.Show("该新字段" + newName.ToLowerInvariant() + "已存在！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            // 外部列类型运行时探测（首行可解析数值→N，日期→D，否则 C）
            var sample = _extRows.Count > 0 ? _extRows[0].GetValueOrDefault(row.Field) : null;
            var t = sample is decimal or int or long or double ? 'N' : sample is DateTime ? 'D' : 'C';
            items.Add(new AggregateItem(row.Field, row.Explain, t, row.Method, newName));
        }
        if (items.Count == 0)
        {
            MessageBox.Show("请选择要聚合的字段！", "提示");
            return;
        }
        var fieldNames = _rows[0].GetDynamicMemberNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasCp = fieldNames.Contains("Cp");
        var hasNno = fieldNames.Contains("Nno");
        var cpnno = (hasCp ? 1 : 0) + (hasNno ? 1 : 0);
        if (cpnno == 0)
        {
            MessageBox.Show("原表没有关联汇总字段！", "提示");
            return;
        }
        // 应用层聚合（旧 select ... group by cp,nno into curs c_tmp；原值→不分组）
        var anyRaw = items.Any(i => i.Method == 1);
        var grouped = anyRaw
            ? _extRows.Select(r => (Row: r, Key: ExtKey(r, cpCol, nnoCol))).GroupBy(x => x.Key).Select(g => g.First().Row)
            : _extRows.GroupBy(r => ExtKey(r, cpCol, nnoCol)).Select(g => AggregateRow(g.First(), g, items, cpCol, nnoCol));
        var index = grouped.ToDictionary(r => hasCp
                ? $"{r.GetValueOrDefault("cp")}|{r.GetValueOrDefault("nno")}"
                : $"{r.GetValueOrDefault("nno")}",
            r => r, StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            var key = hasCp ? $"{row.Get("Cp")}|{row.Get("Nno")}" : $"{row.Get("Nno")}";
            if (!index.TryGetValue(key, out var match)) continue;
            foreach (var it in items)
                row.Set(it.NewName, match.GetValueOrDefault(it.NewName));
        }
        var cols = items.Select(it => new QueryColumnSpec(it.NewName, it.NewName,
            it.Method == 1 ? it.SourceType : 'N', 90, it.Method == 1 ? null : "N3")).ToList();
        Outcome = new CustomFieldOutcome { NewColumns = cols, Apply = _ => { } };
        MessageBox.Show("新字段建立完成，可通过移动该列下方的箭头查看该新字段。", "提示",
            MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
    }

    private static string ExtKey(IReadOnlyDictionary<string, object?> row, string? cpCol, string nnoCol) =>
        cpCol is null ? $"{row.GetValueOrDefault(nnoCol)}" : $"{row.GetValueOrDefault(cpCol)}|{row.GetValueOrDefault(nnoCol)}";

    private static IReadOnlyDictionary<string, object?> AggregateRow(
        IReadOnlyDictionary<string, object?> seed, IEnumerable<IReadOnlyDictionary<string, object?>> group,
        List<AggregateItem> items, string? cpCol, string nnoCol)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (cpCol is not null) dict["cp"] = seed.GetValueOrDefault(cpCol);
        dict["nno"] = seed.GetValueOrDefault(nnoCol);
        foreach (var it in items)
        {
            dict[it.NewName] = it.Method switch
            {
                2 => group.Max(g => NumOf(g.GetValueOrDefault(it.SourceField))),
                3 => group.Min(g => NumOf(g.GetValueOrDefault(it.SourceField))),
                4 => group.Sum(g => NumOf(g.GetValueOrDefault(it.SourceField))),
                5 => group.Average(g => (double)NumOf(g.GetValueOrDefault(it.SourceField))),
                _ => seed.GetValueOrDefault(it.SourceField),
            };
        }
        return dict;
    }

    private static decimal NumOf(object? v) => QueryRow.ToDecimal(v);
}
