namespace QPGL.Domain.Printing;

/// <summary>
/// 人民币金额大写（对齐旧系统 pg_proc.OOSIN6 输出语义：到分、无分补"整"、中间零处理）。
/// 示例：71.50 → 柒拾壹元伍角；100.00 → 壹佰元整；0 → 零元整；0.05 → 伍分。
/// </summary>
public static class MoneyUpper
{
    private static readonly string[] Digits = { "零", "壹", "贰", "叁", "肆", "伍", "陆", "柒", "捌", "玖" };
    private static readonly string[] IntUnits = { "", "拾", "佰", "仟" };
    private static readonly string[] BigUnits = { "", "万", "亿", "兆" };

    public static string ToUpper(decimal amount)
    {
        var negative = amount < 0;
        var value = Math.Round(Math.Abs(amount), 2);

        if (value == 0) return "零元整";

        long fen = (long)(value * 100);
        long intPart = fen / 100;
        int jiao = (int)(fen % 100 / 10);
        int fenPart = (int)(fen % 10);

        var text = (intPart > 0 ? IntToChinese(intPart) : "零") + "元";
        if (jiao == 0 && fenPart == 0)
        {
            text += "整";
        }
        else
        {
            if (jiao > 0) text += Digits[jiao] + "角";
            else if (fenPart > 0 && intPart > 0) text += "零";   // 有分无角补零（整元为零时不重复零）
            if (fenPart > 0) text += Digits[fenPart] + "分";
            else text += "整";                                   // 有角无分补整
        }

        return negative ? "负" + text : text;
    }

    private static string IntToChinese(long n)
    {
        if (n == 0) return "零";
        var sections = new List<int>();                       // 每 4 位一段
        while (n > 0)
        {
            sections.Add((int)(n % 10000));
            n /= 10000;
        }

        var result = "";
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            var section = sections[i];
            if (section == 0) continue;
            var sectionText = SectionToChinese(section);
            // 段内不足千位且非首段（前段非零）补零，如 100000100 → 壹亿零壹佰
            var needZeroPrefix = result.Length > 0 && section < 1000;
            result += (needZeroPrefix ? "零" : "") + sectionText + BigUnits[i];
        }
        return result;
    }

    private static string SectionToChinese(int section)
    {
        var text = "";
        var unit = 0;
        var zeroPending = false;
        while (section > 0)
        {
            var digit = section % 10;
            if (digit == 0)
            {
                zeroPending = text.Length > 0;
            }
            else
            {
                if (zeroPending) text = "零" + text;
                text = Digits[digit] + IntUnits[unit] + text;
                zeroPending = false;
            }
            section /= 10;
            unit++;
        }
        return text;
    }
}
