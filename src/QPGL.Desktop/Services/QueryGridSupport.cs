using System.Collections;
using System.ComponentModel;
using System.Dynamic;
using System.Windows.Controls;

namespace QPGL.Desktop.Services;

/// <summary>
/// E 组查询列规格（fr_sort/fr_total/fr_mdefi/打印共用）。
/// Field=行属性名（QueryRow 字典键）、Caption=显示名（对齐旧 tbstru.EXPLAIN 语义）、
/// Type: C 字符 / N 数值 / D 日期 / T 日期时间。
/// </summary>
public sealed class QueryColumnSpec
{
    public QueryColumnSpec(string field, string caption, char type, double width = 90, string? format = null)
    {
        Field = field;
        Caption = caption;
        Type = type;
        Width = width;
        Format = format;
    }

    public string Field { get; set; }
    public string Caption { get; set; }
    public char Type { get; set; }
    public double Width { get; set; }
    public string? Format { get; set; }

    public QueryColumnSpec Clone() => new(Field, Caption, Type, Width, Format);
}

/// <summary>
/// 查询结果动态行（对齐旧本地游标：可删除行、可加自定义列，fr_mdefi ALTER 游标语义）。
/// WPF {Binding Prop} 走 TryGetMember；静态列绑定原属性名，自定义列走 {Binding [字段名]}。
/// </summary>
public sealed class QueryRow : DynamicObject
{
    private readonly Dictionary<string, object?> _values;

    public QueryRow(Dictionary<string, object?> values) => _values = values;

    public object? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public decimal GetDecimal(string name) => ToDecimal(_values.TryGetValue(name, out var v) ? v : null);

    public void Set(string name, object? value) => _values[name] = value;

    public override bool TryGetMember(GetMemberBinder binder, out object? result) =>
        _values.TryGetValue(binder.Name, out result);

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _values[binder.Name] = value;
        return true;
    }

    public override IEnumerable<string> GetDynamicMemberNames() => _values.Keys;

    public static decimal ToDecimal(object? v) => v switch
    {
        null or DBNull => 0m,
        decimal d => d,
        int i => i,
        long l => l,
        double db => (decimal)db,
        float f => (decimal)f,
        bool b => b ? 1m : 0m,
        _ => decimal.TryParse(v.ToString(), out var m) ? m : 0m,
    };
}

/// <summary>
/// 网格/行包装助手：强类型行集 → QueryRow 集（反射首行类型固化计算属性），
/// 列规格从类型属性推断（类型），显示名由调用方（页签静态 specs）提供。
/// </summary>
public static class QueryGridHelper
{
    /// <summary>把强类型行集包装为 QueryRow 集（属性名→值，含计算属性求值）。</summary>
    public static List<QueryRow> Wrap(IEnumerable rows)
    {
        var list = new List<QueryRow>();
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (row is not null)
            {
                foreach (var prop in row.GetType().GetProperties())
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    dict[prop.Name] = prop.GetValue(row);
                }
            }
            list.Add(new QueryRow(dict));
        }
        return list;
    }

    /// <summary>按规格给 DataGrid 追加一列（fr_mdefi 加列；网格 AutoGenerateColumns=False）。</summary>
    public static void AppendColumn(DataGrid grid, QueryColumnSpec spec)
    {
        var binding = new System.Windows.Data.Binding($"[{spec.Field}]");
        if (!string.IsNullOrEmpty(spec.Format))
            binding.StringFormat = spec.Format;
        var column = new DataGridTextColumn
        {
            Header = spec.Caption,
            Binding = binding,
            Width = new DataGridLength(spec.Width),
            IsReadOnly = true,
        };
        grid.Columns.Add(column);
    }

    /// <summary>fr_sort 排序：多键拼接（旧 INDE on 键串 + SET ORDER [DESCENDING] 整键同向）。</summary>
    public static IEnumerable<QueryRow> SortRows(
        IReadOnlyList<QueryRow> rows,
        IReadOnlyList<(QueryColumnSpec Spec, bool Descending)> keys)
    {
        if (keys.Count == 0) return rows;
        var allNumeric = keys.All(k => k.Spec.Type == 'N');
        IOrderedEnumerable<QueryRow>? ordered = null;
        foreach (var (spec, desc) in keys)
        {
            Func<QueryRow, object> keySelector = allNumeric
                ? r => QueryRow.ToDecimal(r.Get(spec.Field))
                : r => MixedKey(r.Get(spec.Field), spec.Type);
            if (ordered is null)
                ordered = desc ? rows.OrderByDescending(keySelector, KeyValueComparer.Instance)
                               : rows.OrderBy(keySelector, KeyValueComparer.Instance);
            else
                ordered = desc ? ordered.ThenByDescending(keySelector, KeyValueComparer.Instance)
                               : ordered.ThenBy(keySelector, KeyValueComparer.Instance);
        }
        return ordered;
    }

    /// <summary>混合键：C 原串（MACHINE 序≈Ordinal）、N 定宽右对齐、D/T yyyyMMddHHmmss。</summary>
    private static string MixedKey(object? v, char type) => type switch
    {
        'N' => QueryRow.ToDecimal(v).ToString("0.######").PadLeft(18),
        'D' or 'T' => v is DateTime d ? d.ToString("yyyyMMddHHmmss") : "",
        _ => v?.ToString() ?? "",
    };

    private sealed class KeyValueComparer : IComparer<object>
    {
        public static readonly KeyValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is decimal a && y is decimal b) return a.CompareTo(b);
            return string.CompareOrdinal(x?.ToString() ?? "", y?.ToString() ?? "");
        }
    }
}
