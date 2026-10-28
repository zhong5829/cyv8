using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Infrastructure.Query;

namespace QPGL.Desktop.Views;

/// <summary>
/// fr_find 通用查询构造器复刻（契约 §2）：
/// 字段列表 → 操作符（按类型）→ 值（文本/数值/日期区间）→ 且/或 → 条件行 → 生成 WHERE。
/// tbFindField 排序记忆 / tbFindLog 条件日志 / LSTVALUE 候选下拉 / TXTGNAME 客户联想备案不实现（明文输入等价）。
/// </summary>
public partial class FindDialogWindow : Window
{
    private readonly IReadOnlyList<FindFieldSpec> _fields;
    private readonly IReadOnlyList<FindFieldSpec> _shortcuts;
    private readonly List<(bool IsAnd, string Ccc1)> _rows = new();
    private string? _result;

    private FindDialogWindow(IReadOnlyList<FindFieldSpec> fields, IReadOnlyList<FindFieldSpec>? shortcuts)
    {
        InitializeComponent();
        _fields = fields;
        _shortcuts = shortcuts ?? Array.Empty<FindFieldSpec>();
        LstField.ItemsSource = _fields.Select(f => f.Caption).ToList();
        LstShortcut.ItemsSource = _shortcuts.Select(f => f.Caption).ToList();
        LstField.SelectedIndex = 0;
    }

    /// <summary>打开对话框；返回 where 串（取消/无条件返回 null）。空条件返回 "1=1"（备案：旧码 '(  )' 直发为语法错误）。</summary>
    public static string? Show(string title, IReadOnlyList<FindFieldSpec> fields, IReadOnlyList<FindFieldSpec>? shortcuts = null)
    {
        var win = new FindDialogWindow(fields, shortcuts) { Title = $"查询条件 - {title}" };
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner != null) win.Owner = owner;
        win._rows.Clear();
        return win.ShowDialog() == true ? win._result : null;
    }

    private FindFieldSpec? CurrentField =>
        LstField.SelectedIndex >= 0 && LstField.SelectedIndex < _fields.Count ? _fields[LstField.SelectedIndex] : null;

    private void OnFieldChanged(object sender, SelectionChangedEventArgs e)
    {
        var f = CurrentField;
        if (f is null) return;
        var ops = FindExpressionBuilder.Operators(f.Kind)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(o => o.Trim()).ToList();
        CboOp.ItemsSource = ops;
        CboOp.SelectedIndex = 0;
        PnlText.Visibility = f.Kind == FindKind.Text || f.Kind == FindKind.Memo ? Visibility.Visible : Visibility.Collapsed;
        PnlNum.Visibility = f.Kind == FindKind.Numeric ? Visibility.Visible : Visibility.Collapsed;
        PnlDate.Visibility = f.Kind == FindKind.Date ? Visibility.Visible : Visibility.Collapsed;
        if (f.Kind == FindKind.Date) { Dp1.SelectedDate = DateTime.Today; Dp2.SelectedDate = DateTime.Today; }
    }

    private void OnShortcutAdd(object sender, MouseButtonEventArgs e)
    {
        if (LstShortcut.SelectedIndex < 0) return;
        var f = _shortcuts[LstShortcut.SelectedIndex];
        var isAnd = OptAnd.IsChecked == true;
        var ccc1 = FindExpressionBuilder.BuildCondition(new FindRow(f, "", f.Caption, null, default, default, isAnd));
        if (ccc1 is null) return;
        _rows.Add((isAnd, ccc1));
        RefreshRows();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var f = CurrentField;
        if (f is null) return;
        var op = (CboOp.SelectedItem as string) ?? "";
        var isAnd = OptAnd.IsChecked == true;
        string? ccc1;
        switch (f.Kind)
        {
            case FindKind.Numeric:
                if (!decimal.TryParse(TxtNum.Text.Trim(), out var num)) { MessageBox.Show("请输入数值！"); return; }
                ccc1 = FindExpressionBuilder.BuildCondition(new FindRow(f, op, null, num, default, default, isAnd));
                break;
            case FindKind.Date:
                if (Dp1.SelectedDate is null) { MessageBox.Show("请选择日期！"); return; }
                ccc1 = FindExpressionBuilder.BuildCondition(new FindRow(f, op, null, null,
                    Dp1.SelectedDate.Value, Dp2.SelectedDate ?? Dp1.SelectedDate.Value, isAnd));
                break;
            default:
                ccc1 = FindExpressionBuilder.BuildCondition(new FindRow(f, op, TxtValue.Text, null, default, default, isAnd));
                if (ccc1 is null) { MessageBox.Show("查询表达式不能有引号！"); return; }
                break;
        }
        if (ccc1 is null) { MessageBox.Show("查询表达式不能有引号！"); return; }
        _rows.Add((isAnd, ccc1));
        RefreshRows();
    }

    private void OnRowDelete(object sender, MouseButtonEventArgs e)
    {
        if (LstRows.SelectedIndex < 0 || LstRows.SelectedIndex >= _rows.Count) return;
        _rows.RemoveAt(LstRows.SelectedIndex);
        RefreshRows();
    }

    private void RefreshRows()
    {
        var mes = new List<string>();
        for (var i = 0; i < _rows.Count; i++)
        {
            var joiner = i == 0 ? "" : (_rows[i].IsAnd ? "且" : "或");
            mes.Add((joiner.Length > 0 ? joiner + " " : "") + _rows[i].Ccc1);
        }
        LstRows.ItemsSource = mes;
    }

    private void OnBuild(object sender, RoutedEventArgs e)
    {
        _result = FindExpressionBuilder.BuildWhere(_rows);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _result = null;
        DialogResult = false;
        Close();
    }
}
