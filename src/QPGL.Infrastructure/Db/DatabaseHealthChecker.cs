using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;

namespace QPGL.Infrastructure.Db;

public sealed record DbHealthResult(bool Success, long ElapsedMs, string? Error);

/// <summary>
/// 启动库自检：SELECT 1（§6.9）。失败时 API 立即终止并报错（D5：不返回兜底假数据）。
/// </summary>
public sealed class DatabaseHealthChecker(IConnectionStringProvider provider)
{
    public async Task<DbHealthResult> CheckAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(provider.ConnectionString);
            await conn.OpenAsync(ct);
            var one = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1", cancellationToken: ct));
            sw.Stop();
            return new DbHealthResult(one == 1, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DbHealthResult(false, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
