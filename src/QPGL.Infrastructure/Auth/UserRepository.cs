using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Auth;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Auth;

public interface IUserRepository
{
    /// <summary>按操作员取账号（含密码与权限串，供登录验证）。</summary>
    Task<UserRow?> GetByOperatorAsync(string per, CancellationToken ct = default);

    /// <summary>操作员名单（登录窗下拉自动获取；tbkey 行数极少，可一次返回）。</summary>
    Task<IReadOnlyList<string>> GetOperatorsAsync(CancellationToken ct = default);
}

/// <summary>tbkey 行（含密码字段，仅登录验证用，禁止对外返回）。</summary>
public sealed class UserRow
{
    public int id { get; set; }
    public string dpmbh { get; set; } = "";
    public string role { get; set; } = "";
    public string per { get; set; } = "";
    public string password { get; set; } = "";
    public string usem { get; set; } = "";
    public string dpmusem { get; set; } = "";
    public string dpmisto { get; set; } = "";
}

/// <summary>
/// 用户仓储：身份源 = 现有 tbkey 表（结构零改动）。
/// 旧实现出处：fr_pwd Click —— PASSTH("select","tbkey","password","per=?p_per")；
/// 密码比较语义 LOWER(ALLTRIM(col)) == LOWER(ALLTRIM(input))（不区分大小写）。
/// </summary>
public sealed class UserRepository(SqlConnectionFactory factory) : IUserRepository
{
    private const string SelectColumns =
        "id, dpmbh, role, per, password, usem, dpmusem, dpmisto"; // 显式列，禁 SELECT *

    public async Task<UserRow?> GetByOperatorAsync(string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            $"SELECT {SelectColumns} FROM tbkey WHERE per = @per",
            new { per = new DbString { Value = per, IsAnsi = true, Length = 12 } },
            cancellationToken: ct);
        return await conn.QueryFirstOrDefaultAsync<UserRow>(cmd);
    }

    public async Task<IReadOnlyList<string>> GetOperatorsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            "SELECT DISTINCT per FROM tbkey WITH (NOLOCK) WHERE per <> '' ORDER BY per",
            cancellationToken: ct);
        // per 为 char(12)，去除尾部空格（旧系统 ALLTRIM 语义）
        return (await conn.QueryAsync<string>(cmd)).AsList()
            .Select(p => p.Trim()).ToList();
    }
}
