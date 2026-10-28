using System.Data;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;

namespace QPGL.Tests.Replay;

/// <summary>
/// 回放环境：数据库快照重置 + 表快照采集 + 逐字段 diff（§11.2 契约回放验收核心）。
/// 重置 = RESTORE DATABASE FROM DATABASE_SNAPSHOT（秒级回滚全库，保证每场景独立起点）。
/// </summary>
public static class ReplayEnv
{
    private const string DbName = "rscar_replay";
    private const string SnapshotName = "rscar_replay_snap";
    // 本地 SQLEXPRESS（Developer 版），快照文件与副本库 mdf 同目录（F:\SQLData）
    private const string SnapshotFile = @"F:\SQLData\rscar_replay_snap.ss";

    public static SqlConnection Open() =>
        new(ReplayConfig.ConnectionString);

    /// <summary>初始化：确认库存在 + 建数据库快照（若不存在）。须在副本还原后调用一次。</summary>
    public static void EnsureSnapshot()
    {
        using var conn = new SqlConnection(
            new SqlConnectionStringBuilder(ReplayConfig.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        conn.Open();

        var exists = conn.ExecuteScalar<int>(
            $"SELECT COUNT(1) FROM sys.databases WHERE name = N'{SnapshotName}'");
        if (exists > 0) return;

        var (dataFile, _) = GetLogicalFiles(conn);
        conn.Execute(
            $"""
            CREATE DATABASE [{SnapshotName}]
            ON ( NAME = [{dataFile}], FILENAME = '{SnapshotFile}' )
            AS SNAPSHOT OF [{DbName}]
            """);
    }

    /// <summary>场景前重置：从快照恢复副本库（秒级）。须独占连接。</summary>
    public static void ResetToSnapshot()
    {
        using var conn = new SqlConnection(
            new SqlConnectionStringBuilder(ReplayConfig.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        // 关闭副本库现有连接
        SqlMapper.Execute(conn,
            $"ALTER DATABASE [{DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        try
        {
            SqlMapper.Execute(conn,
                $"RESTORE DATABASE [{DbName}] FROM DATABASE_SNAPSHOT = '{SnapshotName}'");
        }
        finally
        {
            SqlMapper.Execute(conn,
                $"ALTER DATABASE [{DbName}] SET MULTI_USER");
            // SINGLE_USER/ROLLBACK IMMEDIATE 已杀掉池中连接，清池避免后续取到 broken 连接
            SqlConnection.ClearAllPools();
        }
    }

    private static (string data, string log) GetLogicalFiles(SqlConnection conn)
    {
        var rows = conn.Query<(string name, string type_desc)>(
            $"SELECT name, type_desc FROM sys.master_files WHERE database_id = DB_ID(N'{DbName}')").ToList();
        return (rows.First(r => r.type_desc == "ROWS").name, rows.First(r => r.type_desc == "LOG").name);
    }

    /// <summary>采集表快照（全列，按指定排序列稳定排序；可选 SQL 端 WHERE 窗口过滤——生产量级数据禁全表拉取）。</summary>
    public static List<Dictionary<string, object>> SnapshotTable(
        SqlConnection conn, string table, string orderBy, string? whereSql = null, object? param = null)
    {
        var rows = new List<Dictionary<string, object>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = whereSql is null
            ? $"SELECT * FROM [dbo].[{table}] WITH (NOLOCK) ORDER BY {orderBy}"
            : $"SELECT * FROM [dbo].[{table}] WITH (NOLOCK) WHERE {whereSql} ORDER BY {orderBy}";
        if (param is IDictionary<string, object?> winParams)
            foreach (var (name, value) in winParams)
                cmd.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                dict[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            rows.Add(dict);
        }
        return rows;
    }

    /// <summary>逐字段 diff（§11.2 要求），返回差异描述（空 = 完全一致）。</summary>
    public static List<string> Diff(
        List<Dictionary<string, object>> legacy,
        List<Dictionary<string, object>> modern,
        string table,
        string[] keyCols)
    {
        var diffs = new List<string>();

        if (legacy.Count != modern.Count)
            diffs.Add($"{table}: 行数不一致 旧={legacy.Count} 新={modern.Count}");

        // 统一列集（忽略顺序差异）
        var cols = legacy.Count > 0 ? legacy[0].Keys.ToArray() : Array.Empty<string>();
        for (var i = 0; i < Math.Min(legacy.Count, modern.Count); i++)
        {
            var l = legacy[i];
            var m = modern[i];
            foreach (var col in cols)
            {
                var lv = Normalize(l.GetValueOrDefault(col));
                var mv = Normalize(m.GetValueOrDefault(col));
                if (!Equals(lv, mv))
                {
                    var key = string.Join("/", keyCols.Select(k => l.GetValueOrDefault(k)));
                    diffs.Add($"{table}[{key}].{col}: 旧='{lv}' 新='{mv}'");
                    if (diffs.Count > 50) return diffs;   // 防爆
                }
            }
        }
        return diffs;
    }

    private static object Normalize(object v)
    {
        if (v is DBNull) return "<null>";
        if (v is decimal d) return d.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        if (v is double db) return db.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        if (v is float f) return f.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        if (v is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
        if (v is bool b) return b ? "1" : "0";
        if (v is string s) return s.TrimEnd();    // char 列尾随空格等价
        if (v is byte[] bytes) return Convert.ToHexString(bytes);
        return v;
    }

    public static string RenderDiffs(List<string> diffs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"契约回放 diff 共 {diffs.Count} 处不一致：");
        foreach (var d in diffs.Take(30)) sb.AppendLine("  " + d);
        return sb.ToString();
    }
}
