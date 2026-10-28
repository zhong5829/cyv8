using System.Text;

namespace QPGL.Infrastructure.Db;

/// <summary>
/// char 列 GBK 字节语义助手（旧系统 VFP/SQL char 列按字节存储，1 汉字=2 字节）。
/// UI 未限宽时仓储层兜底：超宽按 GBK 字节截断（保尾完整字符），避免 SQL「将截断字符串」错误。
/// 例：tbgu.linkman char(8) 只容 4 汉字。
/// </summary>
public static class AnsiBytes
{
    private static readonly Encoding Gbk = Encoding.GetEncoding("GBK");

    /// <summary>按 GBK 字节宽度截断（不超过 <paramref name="maxBytes"/>；不产生半字符）。</summary>
    public static string Truncate(string value, int maxBytes)
    {
        value ??= "";
        if (Gbk.GetByteCount(value) <= maxBytes) return value;
        var bytes = Gbk.GetBytes(value);
        // 从 maxBytes 回退到完整字符边界：GBK 双字节字符首尾字节均 >=0x80，
        // 统计 [cut) 前缀中"未配对的高位字节"——奇数说明截断点落在双字节字符中间，回退 1
        var cut = maxBytes;
        var unpaired = 0;
        for (var i = 0; i < cut; i++)
            unpaired = bytes[i] >= 0x80 ? unpaired + 1 : 0;
        if (unpaired % 2 == 1) cut--;
        return Gbk.GetString(bytes, 0, cut);
    }
}
