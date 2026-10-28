using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Printing;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Printing;

public interface IPrintSettingsRepository
{
    Task<PrintSettings> GetAsync(CancellationToken ct = default);
    Task SaveFooterAsync(string part1, string part2, CancellationToken ct = default);
}

/// <summary>
/// 打印设置（票头/票尾）——沿用旧系统位置 tbsysset（§8.3.2-2，不建新表）：
/// 公司名称/电话/地址/开户银行/账号 = type='system' 段（只读展示，由旧系统统一维护）；
/// 票尾声明存 RPSET1/RPSET2 行，与旧 VFP 客户端共享（pg_dosprint 实证拼接 valuec）。
/// 列容量实证：valuec=char(40)（20 个汉字），valuec1/valuec2=char(254)（长段）。
/// 双写策略：完整票尾写 valuec1（新系统读取优先，最长 120 汉字），
/// 截 20 汉字写 valuec（旧 VFP 客户端打印显示前 40 字，兼容不截断报错）。
/// </summary>
public sealed class PrintSettingsRepository(SqlConnectionFactory factory) : IPrintSettingsRepository
{
    public async Task<PrintSettings> GetAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<SettingRow>(new CommandDefinition(
            """
            SELECT RTRIM(name) AS name, ISNULL(valuec, '') AS valuec, ISNULL(valuec1, '') AS valuec1
            FROM tbsysset WITH (NOLOCK)
            WHERE RTRIM(type) = 'system'
               OR RTRIM(name) IN ('RPSET1', 'RPSET2')
            """,
            cancellationToken: ct));
        // name 可重复（如 papertype 门店/仓库两行），取每名首行（pg_dosprint LOCATE 语义）
        var map = rows.GroupBy(x => x.name.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return new PrintSettings
        {
            CompanyName = Get(map, "公司名称").valuec,
            Tel = Get(map, "电话").valuec,
            Address = Get(map, "地址").valuec,
            Bank = Get(map, "开户银行").valuec,
            Account = Get(map, "账号").valuec,
            FooterPart1 = LongOrShort(Get(map, "RPSET1")),
            FooterPart2 = LongOrShort(Get(map, "RPSET2")),
        };
    }

    public async Task SaveFooterAsync(string part1, string part2, CancellationToken ct = default)
    {
        part1 ??= "";
        part2 ??= "";
        if (part1.Length > 120 || part2.Length > 120)
            throw new Domain.Common.AppException("FOOTER_TOO_LONG", "票尾每段最长 120 字", 422);

        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tbsysset
            SET valuec = @valueShort, valuec1 = @valueLong
            WHERE RTRIM(name) = @name AND RTRIM(type) = 'system'
            """,
            new[]
            {
                new { name = "RPSET1", valueShort = TruncateBytes(part1, 20), valueLong = part1 },
                new { name = "RPSET2", valueShort = TruncateBytes(part2, 20), valueLong = part2 },
            },
            cancellationToken: ct));
    }

    private static SettingRow Get(Dictionary<string, SettingRow> map, string key) =>
        map.TryGetValue(key, out var row) ? row : new SettingRow();

    /// <summary>长段优先（valuec1 有内容用之），回退短段 valuec。</summary>
    private static string LongOrShort(SettingRow row) =>
        row.valuec1.Trim().Length > 0 ? row.valuec1.Trim() : row.valuec.Trim();

    /// <summary>
    /// valuec=char(40) 截断保护：最保守按 20 字符截（全中文=40 字节正好，混 ASCII 更省），
    /// 避免 SQL「将截断字符串」错误；完整内容在 valuec1（char(254)）。
    /// </summary>
    private static string TruncateBytes(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];

    private sealed class SettingRow
    {
        public string name { get; set; } = "";
        public string valuec { get; set; } = "";
        public string valuec1 { get; set; } = "";
    }
}
