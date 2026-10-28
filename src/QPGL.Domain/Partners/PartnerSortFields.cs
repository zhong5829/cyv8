namespace QPGL.Domain.Partners;

/// <summary>
/// 客户列表排序白名单（§6.5：排序字段必须映射到现有索引键列）。
/// 映射值为 tbgu 真实列名。
/// </summary>
public static class PartnerSortFields
{
    /// <summary>前端字段名 → tbgu 真实列（均已建索引或为聚合分组键）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "id",
        ["partNo"] = "gno",
        ["partName"] = "gname",
        ["linkMan"] = "linkman",
        ["mobile"] = "mobile",
        ["type1"] = "type1",
        ["discountRate"] = "yhl",
    };

    public const string DefaultField = "id";

    /// <summary>
    /// 解析排序 SQL 片段；非法字段回退默认（不抛错，保证接口稳定）。
    /// 返回形如 "gname DESC" 的安全片段（白名单内拼接，无注入面）。
    /// </summary>
    public static string Resolve(string? sort, bool descending)
    {
        var column = DefaultField;
        if (!string.IsNullOrWhiteSpace(sort) && Map.TryGetValue(sort.Trim(), out var mapped))
            column = mapped;
        return $"{column} {(descending ? "DESC" : "ASC")}";
    }

    /// <summary>排序字段是否在白名单内。</summary>
    public static bool IsAllowed(string? sort) =>
        string.IsNullOrWhiteSpace(sort) || Map.ContainsKey(sort.Trim());
}
