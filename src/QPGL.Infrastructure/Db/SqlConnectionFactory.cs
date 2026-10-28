using Microsoft.Data.SqlClient;

namespace QPGL.Infrastructure.Db;

/// <summary>
/// Dapper 连接工厂：连接池常驻（§10.2-8 Max Pool Size=200 由连接串控制），
/// 禁止每次新建物理连接。
/// </summary>
public sealed class SqlConnectionFactory(IConnectionStringProvider provider)
{
    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(provider.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
