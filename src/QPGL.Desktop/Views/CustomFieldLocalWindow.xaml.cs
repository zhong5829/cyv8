using System.Windows;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Views;

/// <summary>
/// fr_mdefi 模式①（FrmBd）：对当前结果集逐行求值 VFP 表达式加新列（旧 ALTER 游标 ADD COLUMN + REPL ALL 语义，纯客户端）。
/// 表达式可用显示名（旧 getexpression：EXPLAIN→FIELD_NAME 替换）。
/// </summary>
public partial class CustomFieldLocalWindow : Window
{
    private readonly IReadOnlyList<QueryRow> _rows;
    private readonly IReadOnlyList<QueryColumnSpec> _columns;

    public CustomFieldOutcome? Outcome { get; private set; }

    public CustomFieldLocalWindow(IReadOnlyList<QueryRow> rows, IReadOnlyList<QueryColumnSpec> columns)
    {
        InitializeComponent();
        _rows = rows;
        _columns = columns;
        var list = new List<FieldDisplay>();
        foreach (var c in columns)
            list.Add(new FieldDisplay($"{c.Caption} ({c.Field})", c.Caption, c.Field));
        GrdField.ItemsSource = list;
    }

    private sealed record FieldDisplay(string Display, string Caption, string Field);

    private void BtnBack_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void BtnExit_Click(object sender, RoutedEventArgs e) { Outcome = null; DialogResult = false; }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var fn = TxtNewField.Text.Trim().ToUpperInvariant(); // 旧 FN = UPPER(ALLTRIM())
        var exprRaw = TxtExpression.Text.Trim();
        if (fn.Contains(' '))
        {
            MessageBox.Show("新字段名称不能含有空格", "提示");
            return;
        }
        if (fn.Length == 0 || exprRaw.Length == 0)
        {
            MessageBox.Show("计算表达式输入不正确！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var existing = _columns.Select(c => c.Field).Concat(_rows.SelectMany(r => r.GetDynamicMemberNames())).Distinct(StringComparer.OrdinalIgnoreCase);
        if (existing.Any(f => f.Equals(fn, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("该新字段已存在！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // getexpression：显示名 → 字段名（长名优先避免子串误替换）
        var expr = exprRaw;
        foreach (var c in _columns.OrderByDescending(c => c.Caption.Length))
        {
            if (c.Caption.Length > 0 && expr.Contains(c.Caption, StringComparison.Ordinal))
                expr = expr.Replace(c.Caption, c.Field);
        }
        // 求值类型探测（旧 FT=TYPE(FB)：N→N(10,3)、C→C(len)、U→报错）
        char type;
        try
        {
            var first = VfpExpression.Evaluate(expr, ToLookup(_rows[0]));
            type = first is decimal or int or long or double ? 'N'
                : first is DateTime ? 'D'
                : 'C';
        }
        catch (Exception)
        {
            MessageBox.Show("计算表达式输入不正确！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 逐行计算（旧 REPL ALL &fn WITH EVAL(FB)；任一行失败即中止——与旧 EVAL 抛错一致）
        try
        {
            foreach (var row in _rows)
            {
                var v = VfpExpression.Evaluate(expr, ToLookup(row));
                row.Set(fn, v);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("计算表达式输入不正确！" + Environment.NewLine + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var format = type == 'N' ? "N3" : null;
        Outcome = new CustomFieldOutcome
        {
            NewColumns = [new QueryColumnSpec(fn, fn, type, 90, format)],
            Apply = _ => { },
        };
        MessageBox.Show("新字段建立完成，可通过移动该列下方的箭头查看该新字段。", "提示",
            MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
    }

    /// <summary>QueryRow → 字段查找表（VfpExpression 入参；大小写不敏感兜底）。</summary>
    private static IReadOnlyDictionary<string, object?> ToLookup(QueryRow row)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in row.GetDynamicMemberNames())
            dict[name] = row.Get(name);
        return dict;
    }
}
