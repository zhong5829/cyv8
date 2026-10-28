using System.Windows;
using System.Windows.Input;
using QPGL.Desktop.Services;
using QPGL.Infrastructure.Query;

namespace QPGL.Desktop.Views;

/// <summary>
/// fr_mdefi 模式②（FrmSTable/FrmSField/FrmAccount 合并）：系统表选表 → 选字段（tbstru EXPLAIN）→
/// 每字段聚合（原值/最大/最小/合计/平均）+ 新字段名 → 仓储聚合查询 → 按 CP+NNO（或 NNO）关联回填当前结果集。
/// 旧语义：任一字段选"原值"（M_CG=.F.）→ 无 GROUP BY 取全表行；新字段名 ≤10、不得为 CP/NNO、不得与现有字段重名。
/// </summary>
public partial class CustomFieldSysWindow : Window
{
    private readonly IReadOnlyList<QueryRow> _rows;
    private readonly Func<Task<IReadOnlyList<SysTableInfo>>> _getSysTables;
    private readonly Func<string, Task<IReadOnlyList<SysFieldInfo>>> _getSysFields;
    private readonly Func<string, IReadOnlyList<AggregateItem>, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> _aggregateSys;
    private readonly List<AggRow> _selected = [];
    private IReadOnlyList<SysFieldInfo> _tableFields = [];

    public CustomFieldOutcome? Outcome { get; private set; }

    public CustomFieldSysWindow(
        IReadOnlyList<QueryRow> rows,
        Func<Task<IReadOnlyList<SysTableInfo>>> getSysTables,
        Func<string, Task<IReadOnlyList<SysFieldInfo>>> getSysFields,
        Func<string, IReadOnlyList<AggregateItem>, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> aggregateSys)
    {
        InitializeComponent();
        _rows = rows;
        _getSysTables = getSysTables;
        _getSysFields = getSysFields;
        _aggregateSys = aggregateSys;
        Loaded += async (_, _) => await LoadTablesAsync();
    }

    private async Task LoadTablesAsync()
    {
        try
        {
            CboTable.ItemsSource = await _getSysTables();
            if (CboTable.Items.Count > 0) CboTable.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("系统表加载失败：" + ex.Message, "提示");
        }
    }

    private sealed class AggRow(SysFieldInfo field)
    {
        public string Field => field.FieldName;
        public string Explain => field.Explain;
        public char SourceType => field.FieldType;
        public IReadOnlyList<KeyValuePair<int, string>> Methods => BuildMethods(field.FieldType);
        public int Method { get; set; } = 1;
        public string NewName { get; set; } = "";
        public SysFieldInfo Source => field;
    }

    private static IReadOnlyList<KeyValuePair<int, string>> BuildMethods(char t) => t switch
    {
        'D' => [new(1, "原值"), new(2, "最大值"), new(3, "最小值")],
        'N' or 'F' or 'I' or 'B' or 'Y' => [new(1, "原值"), new(2, "最大值"), new(3, "最小值"), new(4, "合计"), new(5, "平均值")],
        _ => [new(1, "原值")],
    };

    private async void CboTable_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CboTable.SelectedItem is not SysTableInfo t) return;
        _selected.Clear();
        RefreshAggGrid();
        LstYField.Items.Clear();
        _tableFields = await _getSysFields(t.TableName);
        foreach (var f in _tableFields)
            LstYField.Items.Add(f);
    }

    private void RefreshAggGrid()
    {
        GrdAgg.ItemsSource = null;
        GrdAgg.ItemsSource = _selected;
        GrdAgg.Items.Refresh();
    }

    private void MoveSelected(SysFieldInfo f)
    {
        if (_selected.Any(s => s.Field.Equals(f.FieldName, StringComparison.OrdinalIgnoreCase))) return;
        var row = new AggRow(f) { Method = 1, NewName = "" };
        _selected.Add(row);
        RefreshAggGrid();
    }

    private void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        if (LstYField.SelectedItem is SysFieldInfo f) { MoveSelected(f); LstYField.Items.Remove(f); }
    }

    private void BtnUnselect_Click(object sender, RoutedEventArgs e)
    {
        if (GrdAgg.SelectedItem is AggRow row)
        {
            var f = _tableFields.FirstOrDefault(x => x.FieldName.Equals(row.Field, StringComparison.OrdinalIgnoreCase));
            _selected.Remove(row);
            if (f is not null) LstYField.Items.Add(f);
            RefreshAggGrid();
        }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in LstYField.Items.Cast<SysFieldInfo>().ToList()) { MoveSelected(f); LstYField.Items.Remove(f); }
    }

    private void BtnUnselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _selected.ToList())
        {
            var f = _tableFields.FirstOrDefault(x => x.FieldName.Equals(row.Field, StringComparison.OrdinalIgnoreCase));
            if (f is not null) LstYField.Items.Add(f);
        }
        _selected.Clear();
        RefreshAggGrid();
    }

    private void LstYField_DoubleClick(object sender, MouseButtonEventArgs e) => BtnSelect_Click(sender, e);

    private void BtnBack_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void BtnExit_Click(object sender, RoutedEventArgs e) { Outcome = null; DialogResult = false; }

    private async void BtnFinish_Click(object sender, RoutedEventArgs e)
    {
        // 旧码校验链：空名/重名/超长/保留名 cp,nno
        var items = new List<AggregateItem>();
        foreach (var row in _selected)
        {
            var newName = (row.NewName ?? "").Trim();
            if (newName.Length == 0)
            {
                MessageBox.Show("请输入聚合字段字段名称！", "提示");
                return;
            }
            if (newName.Length > 10)
            {
                MessageBox.Show("字段名称不能大于10", "提示");
                return;
            }
            if (newName.Equals("cp", StringComparison.OrdinalIgnoreCase) || newName.Equals("nno", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("聚合字段名不可取'CP','NNO'!", "提示");
                return;
            }
            if (_rows.SelectMany(r => r.GetDynamicMemberNames()).Any(f => f.Equals(newName, StringComparison.OrdinalIgnoreCase))
                || items.Any(i => i.NewName.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("该新字段" + newName.ToLowerInvariant() + "已存在！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            items.Add(new AggregateItem(row.Field, row.Explain, row.SourceType, row.Method, newName));
        }
        if (items.Count == 0)
        {
            MessageBox.Show("请选择要聚合的字段！", "提示");
            return;
        }
        // 结果游标关联键（旧 M_CPNNO：结果游标 cp/nno 字段计数；=0 报"原表没有关联汇总字段！"）
        var fieldNames = _rows[0].GetDynamicMemberNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasCp = fieldNames.Contains("Cp");
        var hasNno = fieldNames.Contains("Nno");
        var cpnno = (hasCp ? 1 : 0) + (hasNno ? 1 : 0);
        if (cpnno == 0)
        {
            MessageBox.Show("原表没有关联汇总字段！", "提示");
            return;
        }
        try
        {
            var aggRows = await _aggregateSys(((SysTableInfo)CboTable.SelectedItem).TableName, items);
            // 回填（旧 SET RELATION CP+NNO / LEFT(NNO,18)：无匹配行保持空）；字典预索引加速
            var index = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var a in aggRows)
            {
                var k = hasCp ? $"{a.GetValueOrDefault("cp")}|{a.GetValueOrDefault("nno")}" : $"{a.GetValueOrDefault("nno")}";
                index[k] = a;
            }
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
        catch (Exception ex)
        {
            MessageBox.Show("聚合查询失败：" + ex.Message, "提示");
        }
    }
}
