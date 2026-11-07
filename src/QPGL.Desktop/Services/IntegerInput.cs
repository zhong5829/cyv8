using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace QPGL.Desktop.Services;

/// <summary>
/// 数量输入统一约束（2026-11-07 需求）：数量按旧系统 INT() 口径只接受整数。
/// 输入阶段：小数点/含小数粘贴按"整段作废"处理——恢复进入编辑前的原值并提示，不再只吞掉小数点；
/// 其它非数字字符（字母/全角数字等）仍按字符拦截；确认阶段 <see cref="IsInteger"/> 再校验一次，
/// 非整数由调用方弹提示，绝不静默截断已有小数数据。
/// 仅约束录入，不改 SQL、不改库结构、不改业务计算；金额/单价输入不使用本类。
/// </summary>
public static class IntegerInput
{
    /// <summary>进入编辑前的框内原值（获得焦点时记录）：小数点整段作废时恢复到这里。</summary>
    private static readonly DependencyProperty EditOriginalProperty = DependencyProperty.RegisterAttached(
        "EditOriginal", typeof(string), typeof(IntegerInput), new PropertyMetadata(""));

    /// <summary>挂到数量 TextBox：只允许 0-9（allowNegative=true 时允许开头一个负号）；小数点/含小数粘贴整段作废。</summary>
    public static void Restrict(TextBox box, bool allowNegative = false)
    {
        // 进入编辑（聚焦）时记录原值：弹窗即打开时的预填值，单元格即进入编辑时的显示值
        box.GotKeyboardFocus += (_, _) => box.SetValue(EditOriginalProperty, box.Text);
        box.PreviewTextInput += (_, e) =>
        {
            if (ContainsDecimal(e.Text))
            {
                e.Handled = true;   // 不插入该字符，整段恢复原值
                RejectDecimalInput(box);
                return;
            }
            if (!IsAllowed(box, e.Text, allowNegative)) e.Handled = true;
        };
        // 空格在数量输入里无意义，一并拦截，避免误输入
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) e.Handled = true;
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text)) return;
            var text = (string?)e.DataObject.GetData(DataFormats.Text) ?? "";
            if (ContainsDecimal(text))
            {
                e.Handled = true;   // 粘贴内容含小数：整段作废，恢复原值
                RejectDecimalInput(box);
                return;
            }
            if (!IsAllowed(box, text, allowNegative)) e.Handled = true;
        });
    }

    /// <summary>是否为小数点类输入（含全角句点/全角点）。</summary>
    private static bool ContainsDecimal(string input)
        => input.IndexOfAny(new[] { '.', '。', '．' }) >= 0;

    /// <summary>小数点/含小数粘贴：本次输入整段作废，恢复进入编辑前的原值并明确提示。</summary>
    private static void RejectDecimalInput(TextBox box)
    {
        var original = (string?)box.GetValue(EditOriginalProperty) ?? "";
        if (box.Text != original) box.Text = original;
        box.SelectAll();   // 恢复后全选，便于直接重新输入整数
        MessageBox.Show("数量必须为整数（不能带小数），本次输入已作废，请重新输入整数。",
            "数量录入", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>确认阶段校验：必须是整数字面量（无小数位）。</summary>
    public static bool IsInteger(string? text, out decimal value)
    {
        value = 0m;
        if (!decimal.TryParse((text ?? "").Trim(), out var v)) return false;
        if (v != decimal.Truncate(v)) return false;
        value = v;
        return true;
    }

    /// <summary>输入字符白名单：仅 0-9（allowNegative 时允许一个开头负号），小数点/全角符号/字母一律不允许。</summary>
    public static bool IsAllowedInput(string input, bool allowNegative = false)
    {
        var start = 0;
        if (allowNegative && input.Length > 0 && input[0] == '-') start = 1;
        if (start == 0 && input.Length > 0 && input[0] == '-') return false;
        for (var i = start; i < input.Length; i++)
            if (input[i] is < '0' or > '9') return false;
        return true;
    }

    private static bool IsAllowed(TextBox box, string input, bool allowNegative)
    {
        if (!IsAllowedInput(input, allowNegative)) return false;
        // 负号只允许出现在空框首位（库存调整等可负数量场景预留；件数录入默认不开启）
        return allowNegative || !box.Text.Contains('-');
    }

    // ---------- 表格数量单元格（2026-11-07 需求）：与弹窗同一套语义 ----------

    /// <summary>单元格编辑框标记：EditingElementStyle 里设为 True，复用 Restrict（禁小数点/非数字/粘贴）。</summary>
    public static readonly DependencyProperty IsQuantityProperty = DependencyProperty.RegisterAttached(
        "IsQuantity", typeof(bool), typeof(IntegerInput),
        new PropertyMetadata(false, (d, e) =>
        {
            if (d is TextBox box && (bool)e.NewValue) Restrict(box);
        }));

    public static void SetIsQuantity(DependencyObject d, bool value) => d.SetValue(IsQuantityProperty, value);

    public static bool GetIsQuantity(DependencyObject d) => (bool)d.GetValue(IsQuantityProperty);

    /// <summary>表格级开关：全局 DataGrid 样式设为 True，自动在 CellEditEnding 校验数量单元格（无需逐页写事件）。</summary>
    public static readonly DependencyProperty WatchQuantityCellsProperty = DependencyProperty.RegisterAttached(
        "WatchQuantityCells", typeof(bool), typeof(IntegerInput),
        new PropertyMetadata(false, (d, e) =>
        {
            if (d is not DataGrid grid) return;
            if ((bool)e.NewValue)
            {
                grid.PreparingCellForEdit += Grid_PreparingCellForEdit;
                grid.CellEditEnding += Grid_CellEditEnding;
            }
            else
            {
                grid.PreparingCellForEdit -= Grid_PreparingCellForEdit;
                grid.CellEditEnding -= Grid_CellEditEnding;
            }
        }));

    public static void SetWatchQuantityCells(DependencyObject d, bool value) => d.SetValue(WatchQuantityCellsProperty, value);

    public static bool GetWatchQuantityCells(DependencyObject d) => (bool)d.GetValue(WatchQuantityCellsProperty);

    private static void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => ValidateCell(e);

    /// <summary>
    /// 进入编辑：原值本身非整数时按原值回填（而不是按显示转换器的截断值），
    /// 避免用户"打开单元格未修改直接提交"把库内小数静默截断；此时提交会被整数校验拦下并提示改成整数。
    /// </summary>
    private static void Grid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is not TextBox box || !GetIsQuantity(box)) return;
        var raw = OriginalText(e.Row?.Item, e.Column);
        if (raw.Length > 0 && !IsInteger(raw, out _)) box.Text = raw;
        // 记录进入编辑时的显示值：遇到小数点整段作废时恢复到这里
        box.SetValue(EditOriginalProperty, box.Text);
    }

    /// <summary>
    /// 单元格提交校验：只对标记了 <see cref="IsQuantityProperty"/> 的编辑框生效。
    /// Esc 取消（EditAction=Cancel）直接放行，由 DataGrid 还原原值；
    /// 空值/非整数弹提示并取消本次提交，同时把编辑框恢复为原值（不静默截断、不反复弹）。
    /// </summary>
    public static bool ValidateCell(DataGridCellEditEndingEventArgs e, string? title = null)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Cancel) return true;
        if (e.EditingElement is not TextBox box || !GetIsQuantity(box)) return true;
        var field = (e.Column.Header as string)?.Trim() ?? "数量";
        var text = box.Text.Trim();
        string? message = null;
        if (text.Length == 0) message = $"{field}不能为空。";
        else if (!IsInteger(text, out _)) message = $"{field}必须是整数（不能带小数）。";
        if (message is null) return true;
        MessageBox.Show(message, title ?? "数量录入", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Cancel = true;
        var original = OriginalText(e.Row?.Item, e.Column);
        if (original.Length > 0)
        {
            box.Text = original;
            box.SelectAll();
        }
        return false;
    }

    /// <summary>取行源属性原值（用于校验失败时还原编辑框、非整数原值回填）。</summary>
    private static string OriginalText(object? item, DataGridColumn? column)
    {
        if (item is null || column is not DataGridBoundColumn col) return "";
        if (col.Binding is not Binding b || b.Path?.Path is not { Length: > 0 } path) return "";
        object? current = item;
        foreach (var part in path.Split('.'))
        {
            if (current is null) return "";
            var prop = current.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) return "";
            current = prop.GetValue(current);
        }
        return current switch
        {
            decimal d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            short s => s.ToString(),
            _ => current?.ToString() ?? "",
        };
    }
}
