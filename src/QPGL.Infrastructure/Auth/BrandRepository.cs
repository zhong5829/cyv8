using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Auth;

public interface IBrandRepository
{
    /// <summary>厂牌清单（tbsysset type='cp'，旧 fr_cp Load 语义）。</summary>
    Task<IReadOnlyList<BrandRow>> GetBrandsAsync(string dpm, CancellationToken ct = default);
}

/// <summary>厂牌行（tbsysset type='cp'）。</summary>
public sealed class BrandRow
{
    /// <summary>厂牌名（tbsysset.name，旧 P_CP 来源）。</summary>
    public string name { get; set; } = "";
    /// <summary>货币单位（tbsysset.valuec，旧 P_WN）。</summary>
    public string valuec { get; set; } = "";
    /// <summary>查询码（tbsysset.cxnno，旧 P_SNNO）。</summary>
    public string cxnno { get; set; } = "";
}

/// <summary>
/// 厂牌仓储（旧 fr_cp 机制复刻）。
/// 旧实现出处：fr_cp __.row2 Load ——
///   超级用户('@'/'$')或 P_USEMCP 空 → PASSTH select tbsysset WHERE type='cp'（全量）；
///   否则 `type='cp' and ',{usemcp},' like '%,'+rtrim(name)+',%'`（白名单过滤）。
/// 确认按钮 T_.row5：P_CP=NAME / P_WN=VALUEC / P_SNNO=CXNNO。
/// </summary>
public sealed class BrandRepository(SqlConnectionFactory factory) : IBrandRepository
{
    public async Task<IReadOnlyList<BrandRow>> GetBrandsAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            """
            SELECT RTRIM(name) AS name, RTRIM(ISNULL(valuec, '')) AS valuec, RTRIM(ISNULL(cxnno, '')) AS cxnno
            FROM tbsysset WITH (NOLOCK)
            WHERE type = 'cp' AND dpm = @dpm
            ORDER BY id
            """,
            new { dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } },
            cancellationToken: ct);
        return (await conn.QueryAsync<BrandRow>(cmd)).AsList();
    }
}
