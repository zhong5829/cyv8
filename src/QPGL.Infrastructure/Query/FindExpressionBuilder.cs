using System.Globalization;

namespace QPGL.Infrastructure.Query;

/// <summary>fr_find 字段值种类（旧 TYPE() 判定：C/N/D/M，L 位类型本批八表单未用）。</summary>
public enum FindKind
{
    Text,
    Numeric,
    Date,
    Memo,
}

/// <summary>fr_find 字段/快捷条件定义（旧 MFIELD 数组 + MTI 快捷二维数组行）。</summary>
/// <param name="Caption">显示名（旧 M_FC，tbstru.EXPLAIN 来源）。</param>
/// <param name="Expr">字段表达式（旧 M_FE；快捷条件为完整 SQL 片段，如 mark='已确认'）。</param>
/// <param name="Kind">值类型（旧 TYPE(M_FE) + tbstru 回落）。</param>
public sealed record FindFieldSpec(string Caption, string Expr, FindKind Kind);

/// <summary>一条已录入的查询条件行（旧 C_FIND 游标行）。</summary>
public sealed record FindRow(
    FindFieldSpec Field,
    string Op,
    string? TextValue,
    decimal? NumValue,
    DateTime Date1,
    DateTime Date2,
    bool IsAnd);

/// <summary>
/// fr_find 通用查询构造器的 WHERE 表达式生成（row12 InteractiveChange CCC1 规则 + row9 CMDGO 拼装）。
/// 契约：开发文档/契约摘录/E组万能查询族契约.md §2。
/// </summary>
public static class FindExpressionBuilder
{
    /// <summary>前缀匹配哨兵：旧码 CHR(65184)（CP936 0xFE 0xA0 → U+E864），between 'K1' and 'K1'+哨兵。</summary>
    public const string PrefixSentinel = "\uE864";

    /// <summary>操作符集（row13 LSTCZF 缺省串；tbFindField 定制记忆备案不实现）。</summary>
    public static string Operators(FindKind kind) => kind switch
    {
        FindKind.Text => " =, 包含,<>, >, <,>=,<=,不包含,==",
        FindKind.Date => " =,<>, >, <,>=,<=,今天,昨天,本周,上周,本月,上月,本年,上年,空日期",
        FindKind.Memo => " 包含,不包含, =,<>, >, <,>=,<=,不包含",
        _ => " =,<>, >, <,>=,<=",
    };

    /// <summary>快捷条件型字段（表达式含 = &lt; &gt; in 且为 MTI 段）——操作符/值输入禁用，CCC1=表达式原样。</summary>
    public static bool IsExprShortcut(FindFieldSpec f) =>
        f.Expr.Contains('=') || f.Expr.Contains('<') || f.Expr.Contains('>') || f.Expr.Contains(" in ", StringComparison.OrdinalIgnoreCase);

    /// <summary>日期快捷操作符 → (d1, d2)（_f.row14；P_RQ=今天）。</summary>
    public static (DateTime D1, DateTime D2) QuickRange(string op, DateTime today)
    {
        var mondayOffset = ((int)today.DayOfWeek + 6) % 7; // DOW(x,2)：周一=0 偏移
        return op switch
        {
            "今天" => (today, today),
            "昨天" => (today.AddDays(-1), today.AddDays(-1)),
            "本周" => (today.AddDays(-mondayOffset), today.AddDays(-mondayOffset + 6)),
            "上周" => (today.AddDays(-mondayOffset - 7), today.AddDays(-mondayOffset - 1)),
            "本月" => (new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
            "上月" => (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1).AddDays(-1)),
            "本年" => (new DateTime(today.Year, 1, 1), new DateTime(today.Year, 12, 31)),
            "上年" => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31)),
            "空日期" => (new DateTime(1900, 1, 1), new DateTime(1900, 1, 1)),
            _ => (today, today),
        };
    }

    /// <summary>旧 DTOC（SET DATE ANSI）→ yyyy.MM.dd。</summary>
    public static string Dtoc(DateTime d) => d.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 单条件生成（row12）。返回 CCC1；返回 null = 引号拦截（旧 MSG('查询表达式不能有引号！')）。
    /// </summary>
    public static string? BuildCondition(FindRow r)
    {
        var expr = r.Field.Expr;
        var op = (r.Op ?? "").Trim();

        // 快捷表达式字段：M_TYPE='2' 且表达式含比较符 → 原样
        if (IsExprShortcut(r.Field))
            return expr;

        switch (r.Field.Kind)
        {
            case FindKind.Text:
            case FindKind.Memo:
            {
                var k1 = (r.TextValue ?? "").Trim().ToLowerInvariant();
                if (k1.Contains('\'') || k1.Contains('"'))
                    return null;
                switch (op)
                {
                    case "包含":
                        return $"{expr} like '%{k1}%'";
                    case "不包含":
                        return $"{expr} not like '%{k1}%'";
                    case "==" when r.Field.Kind == FindKind.Text:
                        return $"{expr}='{k1}'";
                    case "=" when k1.Length == 0:
                        return "1=1"; // 旧码 REPLACE CCC1 WITH "1=1"
                    case "=" when expr.Trim().Equals("paper", StringComparison.OrdinalIgnoreCase) && IsDigits(k1):
                        return $"substring({expr},5,6)='{k1.PadLeft(6, '0')}'";
                    case "=":
                        return $"{expr} between '{k1}' and '{k1}{PrefixSentinel}'";
                    default:
                        return $"{expr}{op}'{k1}'";
                }
            }
            case FindKind.Date:
            {
                // 日期快捷（含 '='，DatePicker2 可见时取用户区间 d1/d2）
                if (op is "=" or "今天" or "昨天" or "本周" or "上周" or "本月" or "上月" or "本年" or "上年" or "空日期")
                {
                    var (d1, d2) = op == "=" ? (r.Date1, r.Date2) : QuickRange(op, r.Date1);
                    return $"{expr}>='{Dtoc(d1)}' and {expr}<'{Dtoc(d2.AddDays(1))}'";
                }
                // <= 特例：值取次日、op 转 <（日期无时刻语义）
                if (op == "<=")
                    return $"{expr}<'{Dtoc(r.Date1.AddDays(1))}'";
                return $"{expr}{op}'{Dtoc(r.Date1)}'";
            }
            case FindKind.Numeric:
                return $"{expr}{op}{(r.NumValue ?? 0m).ToString(CultureInfo.InvariantCulture)}";
            default:
                return $"{expr}{op}'{r.TextValue}'";
        }
    }

    /// <summary>条件行显示串（旧 M_MES，去引号；日期快捷行显示区间）。</summary>
    public static string BuildMes(FindRow r, string ccc1)
    {
        string mes;
        if (r.Field.Kind == FindKind.Date &&
            r.Op.Trim() is "=" or "今天" or "昨天" or "本周" or "上周" or "本月" or "上月" or "本年" or "上年" or "空日期")
        {
            mes = $"{r.Field.Caption}: {Dtoc(r.Date1)} 至 {Dtoc(r.Date2)}";
        }
        else if (IsExprShortcut(r.Field))
        {
            mes = $"{r.Field.Caption} {ccc1}";
        }
        else
        {
            var v = r.Field.Kind == FindKind.Numeric ? (r.NumValue ?? 0m).ToString(CultureInfo.InvariantCulture) : (r.TextValue ?? "").Trim();
            mes = $"{r.Field.Caption} {r.Op.Trim()} {v}";
        }
        return (mes + (r.IsAnd ? " 且" : " 或")).Replace("'", "").Replace("\"", "");
    }

    /// <summary>CMDGO 拼装（row9）：'(' + 各行（首行无连接词）+ ')'；空条件返回 1=1（旧码 '(  )' 直发 SQL 为语法错误，备案取等价「提取全部」语义）。</summary>
    public static string BuildWhere(IReadOnlyList<(bool IsAnd, string Ccc1)> rows)
    {
        if (rows.Count == 0)
            return "1=1";
        var ss = "";
        for (var i = 0; i < rows.Count; i++)
        {
            var joiner = i == 0 ? "" : (rows[i].IsAnd ? "and" : "or");
            ss = ss + " " + joiner + " " + rows[i].Ccc1.Trim();
        }
        return "(" + ss.Trim() + ")";
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsDigit);
}
