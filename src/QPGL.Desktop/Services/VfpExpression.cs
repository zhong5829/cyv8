using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QPGL.Desktop.Services;

/// <summary>
/// VFP 表达式求值子集（fr_mdefi 模式①本地字段：旧码 TYPE(FB)='U' 报"计算表达式输入不正确"）。
/// 支持：字段引用（含中文显示名，调用方先做 EXPLAIN→FIELD_NAME 替换）、四则/比较、
/// 常用函数 IIF/ROUND/STR/VAL/SUBSTR/LEFT/RIGHT/ALLTRIM/TRIM/UPPER/LOWER/LEN/MIN/MAX/ABS/INT/MOD/TRANSFORM/DTOS/TTOD/CTOD。
/// 值域：decimal / string / DateTime / bool。
/// </summary>
public static partial class VfpExpression
{
    public sealed class EvalException(string message) : Exception(message);

    // ---------- 词法 ----------
    private enum Tok { Num, Str, Name, Op, End }

    private sealed class Lexer(string src)
    {
        private int _pos;
        public Tok Kind = Tok.End;
        public string Text = "";
        private static readonly HashSet<char> Ops = new("+-*/^()=<>,.$");

        public void Next()
        {
            while (_pos < src.Length && char.IsWhiteSpace(src[_pos])) _pos++;
            if (_pos >= src.Length) { Kind = Tok.End; Text = ""; return; }
            var c = src[_pos];
            if (char.IsDigit(c) || (c == '.' && _pos + 1 < src.Length && char.IsDigit(src[_pos + 1])))
            {
                var start = _pos;
                while (_pos < src.Length && (char.IsDigit(src[_pos]) || src[_pos] == '.')) _pos++;
                Kind = Tok.Num; Text = src[start.._pos]; return;
            }
            if (c == '\'' || c == '"')
            {
                var quote = c;
                _pos++;
                var sb = new StringBuilder();
                while (_pos < src.Length && src[_pos] != quote)
                {
                    sb.Append(src[_pos]);
                    _pos++;
                }
                if (_pos >= src.Length) throw new EvalException("计算表达式输入不正确");
                _pos++;
                Kind = Tok.Str; Text = sb.ToString(); return;
            }
            if (char.IsLetter(c) || c == '_' || c > 127)
            {
                var start = _pos;
                while (_pos < src.Length && (char.IsLetterOrDigit(src[_pos]) || src[_pos] == '_' || src[_pos] > 127)) _pos++;
                Kind = Tok.Name; Text = src[start.._pos]; return;
            }
            if (Ops.Contains(c))
            {
                _pos++;
                // 双字符运算符 <= >= <> !=
                if ((c == '<' || c == '>' || c == '!') && _pos < src.Length && src[_pos] == '=')
                {
                    Text = c + "=";
                    _pos++;
                }
                else if (c == '<' && _pos < src.Length && src[_pos] == '>')
                {
                    Text = "<>";
                    _pos++;
                }
                else
                {
                    Text = c.ToString();
                }
                Kind = Tok.Op; return;
            }
            throw new EvalException("计算表达式输入不正确");
        }
    }

    // ---------- 求值 ----------
    public static object Evaluate(string expression, IReadOnlyDictionary<string, object?> fields)
    {
        var lexer = new Lexer(expression ?? "");
        lexer.Next();
        var value = ParseExpr(lexer, fields);
        if (lexer.Kind != Tok.End) throw new EvalException("计算表达式输入不正确");
        return value;
    }

    private static object ParseExpr(Lexer lx, IReadOnlyDictionary<string, object?> f)
    {
        var left = ParseAdd(lx, f);
        while (lx.Kind == Tok.Op && lx.Text is "=" or "==" or "<" or ">" or "<=" or ">=" or "<>" or "!=" or "$")
        {
            var op = lx.Text;
            lx.Next();
            var right = ParseAdd(lx, f);
            left = Compare(op, left, right);
        }
        return left;
    }

    private static object ParseAdd(Lexer lx, IReadOnlyDictionary<string, object?> f)
    {
        var left = ParseMul(lx, f);
        while (lx.Kind == Tok.Op && lx.Text is "+" or "-")
        {
            var op = lx.Text;
            lx.Next();
            var right = ParseMul(lx, f);
            if (op == "+")
            {
                if (left is string || right is string) left = AsString(left) + AsString(right);
                else left = AsNum(left) + AsNum(right);
            }
            else left = AsNum(left) - AsNum(right);
        }
        return left;
    }

    private static object ParseMul(Lexer lx, IReadOnlyDictionary<string, object?> f)
    {
        var left = ParseUnary(lx, f);
        while (lx.Kind == Tok.Op && lx.Text is "*" or "/" or "%" or "^")
        {
            var op = lx.Text;
            lx.Next();
            var right = ParseUnary(lx, f);
            left = op switch
            {
                "*" => AsNum(left) * AsNum(right),
                "/" => AsNum(right) == 0 ? 0m : AsNum(left) / AsNum(right),
                "%" => AsNum(right) == 0 ? 0m : AsNum(left) % AsNum(right),
                "^" => (decimal)Math.Pow((double)AsNum(left), (double)AsNum(right)),
                _ => left,
            };
        }
        return left;
    }

    private static object ParseUnary(Lexer lx, IReadOnlyDictionary<string, object?> f)
    {
        if (lx.Kind == Tok.Op && lx.Text == "-")
        {
            lx.Next();
            return -AsNum(ParseUnary(lx, f));
        }
        if (lx.Kind == Tok.Op && lx.Text == "+") lx.Next();
        return ParsePrimary(lx, f);
    }

    private static object ParsePrimary(Lexer lx, IReadOnlyDictionary<string, object?> f)
    {
        switch (lx.Kind)
        {
            case Tok.Num:
                var n = decimal.Parse(lx.Text, CultureInfo.InvariantCulture);
                lx.Next();
                return n;
            case Tok.Str:
                var s = lx.Text;
                lx.Next();
                return s;
            case Tok.Op when lx.Text == "(":
                lx.Next();
                var v = ParseExpr(lx, f);
                if (lx.Kind != Tok.Op || lx.Text != ")") throw new EvalException("计算表达式输入不正确");
                lx.Next();
                return v;
            case Tok.Op when lx.Text == ".": // .T. / .F.
                lx.Next();
                if (lx.Kind == Tok.Name && (lx.Text.Equals("T", StringComparison.OrdinalIgnoreCase) || lx.Text.Equals("F", StringComparison.OrdinalIgnoreCase)))
                {
                    var isTrue = lx.Text.Equals("T", StringComparison.OrdinalIgnoreCase);
                    lx.Next();
                    if (lx.Kind == Tok.Op && lx.Text == ".") { lx.Next(); return isTrue; }
                }
                throw new EvalException("计算表达式输入不正确");
            case Tok.Name:
                var name = lx.Text;
                var lower = name.ToLowerInvariant();
                lx.Next();
                if (lx.Kind == Tok.Op && lx.Text == "(")
                    return CallFunction(lower, lx, f);
                if (lower is ".t" or ".f") throw new EvalException("计算表达式输入不正确");
                if (f.TryGetValue(name, out var val) || f.TryGetValue(lower, out val)) return val ?? "";
                // .T./.F. 词法切分后 name 可能是 "T"/"F"（前面被 '.' 分开），此处字段查找失败报错
                throw new EvalException("计算表达式输入不正确");
            default:
                throw new EvalException("计算表达式输入不正确");
        }
    }

    private static object CallFunction(string name, Lexer lx, IReadOnlyDictionary<string, object?> f)
    {
        lx.Next(); // consume '('
        var args = new List<object>();
        if (lx.Kind != Tok.Op || lx.Text != ")")
        {
            args.Add(ParseExpr(lx, f));
            while (lx.Kind == Tok.Op && lx.Text == ",")
            {
                lx.Next();
                args.Add(ParseExpr(lx, f));
            }
        }
        if (lx.Kind != Tok.Op || lx.Text != ")") throw new EvalException("计算表达式输入不正确");
        lx.Next();
        return name switch
        {
            "iif" when args.Count == 3 => IsTrue(args[0]) ? args[1] : args[2],
            "round" when args.Count is 1 or 2 => RoundNum(args),
            "str" => StrFunc(args),
            "val" => ValFunc(args),
            "substr" when args.Count is 2 or 3 => SubstrFunc(args),
            "left" when args.Count == 2 => AsString(args[0])[..Math.Min(Math.Max((int)AsNum(args[1]), 0), AsString(args[0]).Length)],
            "right" when args.Count == 2 => RightFunc(args),
            "alltrim" or "trim" or "ltrim" => TrimFunc(name, args),
            "upper" when args.Count == 1 => AsString(args[0]).ToUpperInvariant(),
            "lower" when args.Count == 1 => AsString(args[0]).ToLowerInvariant(),
            "len" when args.Count == 1 => (decimal)AsString(args[0]).Length,
            "min" when args.Count >= 1 => MinMax(args, true),
            "max" when args.Count >= 1 => MinMax(args, false),
            "abs" when args.Count == 1 => Math.Abs(AsNum(args[0])),
            "int" when args.Count == 1 => Math.Truncate(AsNum(args[0])),
            "mod" when args.Count == 2 => AsNum(args[1]) == 0 ? 0m : AsNum(args[0]) % AsNum(args[1]),
            "transform" when args.Count >= 1 => AsString(args[0]),
            "dtos" when args.Count == 1 => AsDate(args[0]).ToString("yyyyMMdd"),
            "ttod" when args.Count == 1 => AsDate(args[0]).Date,
            "ctod" when args.Count == 1 && DateTime.TryParse(AsString(args[0]), out var d) => d,
            "date" when args.Count == 0 => DateTime.Today,
            "datetime" when args.Count == 0 => DateTime.Now,
            "recno" when args.Count == 0 => 0m, // 旧表达式偶见 RECNO()，此处游标无序号语义置 0
            _ => throw new EvalException("计算表达式输入不正确"),
        };
    }

    // ---------- 函数实现 ----------
    private static bool IsTrue(object v) => v switch
    {
        bool b => b,
        decimal d => d != 0,
        string s => s.Equals(".T.", StringComparison.OrdinalIgnoreCase) || (s == "1"),
        _ => false,
    };

    private static decimal RoundNum(List<object> a) =>
        a.Count == 1 ? Math.Round(AsNum(a[0]), 0, MidpointRounding.AwayFromZero)
                     : Math.Round(AsNum(a[0]), (int)AsNum(a[1]), MidpointRounding.AwayFromZero);

    /// <summary>VFP STR(x[,len[,dec]])：定宽右对齐空格；无长度按 10 位。</summary>
    private static string StrFunc(List<object> a)
    {
        var x = AsNum(a[0]);
        if (a.Count == 1) return x.ToString("0").PadLeft(10);
        var len = (int)AsNum(a[1]);
        var dec = a.Count > 2 ? (int)AsNum(a[2]) : 0;
        var s = dec > 0 ? Math.Round(x, dec, MidpointRounding.AwayFromZero).ToString("F" + dec, CultureInfo.InvariantCulture)
                        : Math.Round(x, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        if (s.Length > len) s = s[..len];
        return s.PadLeft(len);
    }

    private static decimal ValFunc(List<object> a)
    {
        var s = AsString(a[0]).TrimStart();
        var m = ValRegex().Match(s);
        return m.Success && decimal.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    [GeneratedRegex(@"^[+-]?\d*\.?\d+")]
    private static partial Regex ValRegex();

    private static string SubstrFunc(List<object> a)
    {
        var s = AsString(a[0]);
        var start = (int)AsNum(a[1]);
        if (start <= 0) start = 1;
        if (start > s.Length) return "";
        var rest = s[(start - 1)..];
        if (a.Count == 2) return rest;
        var len = (int)AsNum(a[2]);
        if (len < 0) return "";
        return rest[..Math.Min(len, rest.Length)];
    }

    private static string RightFunc(List<object> a)
    {
        var s = AsString(a[0]);
        var n = (int)AsNum(a[1]);
        if (n <= 0) return "";
        return n >= s.Length ? s : s[^n..];
    }

    private static string TrimFunc(string name, List<object> a)
    {
        var s = AsString(a[0]);
        return name switch
        {
            "alltrim" => s.Trim(),
            "trim" => s.TrimEnd(),
            _ => s.TrimStart(),
        };
    }

    private static object MinMax(List<object> a, bool isMin)
    {
        object best = a[0];
        foreach (var x in a.Skip(1))
        {
            if (best is string || x is string)
            {
                var c = string.CompareOrdinal(AsString(x), AsString(best));
                if (isMin ? c < 0 : c > 0) best = x;
            }
            else
            {
                var c = AsNum(x).CompareTo(AsNum(best));
                if (isMin ? c < 0 : c > 0) best = x;
            }
        }
        return best;
    }

    private static int Compare(string op, object l, object r)
    {
        int c;
        if (l is string ls && r is string rs) c = string.CompareOrdinal(ls, rs);
        else if (l is DateTime ld && r is DateTime rd) c = ld.CompareTo(rd);
        else c = AsNum(l).CompareTo(AsNum(r));
        return op switch
        {
            "=" or "==" => c == 0,
            "!=" or "<>" => c != 0,
            "<=" => c <= 0,
            ">=" => c >= 0,
            "<" => c < 0,
            ">" => c > 0,
            "$" => AsString(r).Contains(AsString(l)),
            _ => throw new EvalException("计算表达式输入不正确"),
        } ? 1 : 0;
    }

    private static decimal AsNum(object v) => v switch
    {
        decimal d => d,
        bool b => b ? 1m : 0m,
        DateTime d => d.Ticks,
        string s => decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0m,
        _ => 0m,
    };

    public static string AsString(object v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? ".T." : ".F.",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
        decimal d => d == Math.Truncate(d) ? d.ToString("0", CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static DateTime AsDate(object v) => v switch
    {
        DateTime d => d,
        string s when DateTime.TryParse(s, out var d) => d,
        _ => new DateTime(1900, 1, 1),
    };
}
