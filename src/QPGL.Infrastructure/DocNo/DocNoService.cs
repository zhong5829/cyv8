using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.DocNo;

public sealed class DocNoService(SqlConnectionFactory factory) : IDocNoService
{
    /// <summary>「dpm|djname」→ 信号量。进程内串行取号（D4）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    /// <summary>
    /// 取号防重目标（旧 pg_proc.getnewdh L2042/L2044 的 Y_TABLE/Y_FIELD 参数化）：
    /// 取号后查目标业务表是否已存在该单号，存在则按目标表最大流水修正 tbdjgs.lsh 后重取。
    /// </summary>
    public sealed record DocNoCheckTarget(string Table, string Field, string? TypeFilter = null);

    /// <summary>销售单防重目标（一期默认：tbsada.paper + type='销售'）。</summary>
    public static readonly DocNoCheckTarget SaleTarget = new("tbsada", "paper", "销售");

    public Task<string> GetNewDocNoAsync(string djName, string dpm, CancellationToken ct = default) =>
        GetNewDocNoAsync(djName, dpm, SaleTarget, ct);

    public async Task<string> GetNewDocNoAsync(string djName, string dpm, DocNoCheckTarget target, CancellationToken ct = default)
    {
        var key = $"{dpm.Trim()}|{djName.Trim()}";
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await IssueAsync(djName, dpm, target, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 预览取号（旧 getnewdh isadd=0：只显示下一号，不推进 tbdjgs.lsh、不防重）。
    /// 用于确认对话框先显示单号，正式号在确认后 GetNewDocNoAsync 取得（并发下可能与预览号不同，旧系统同语义）。
    /// </summary>
    public async Task<string> PreviewDocNoAsync(string djName, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await ExecGetNewDhAsync(conn, djName, dpm, isAdd: 0, ct);
    }

    private async Task<string> IssueAsync(string djName, string dpm, DocNoCheckTarget target, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 跨进程行锁（§11.3/§13）：getnewdh 内部 SELECT lsh → UPDATE 分离且无事务包裹，
            // 多客户端并发取号会重号。这里先对 tbdjgs 目标行取 UPDLOCK（持有到事务结束），
            // 迫使其他会话的 getnewdh 在 SELECT lsh 处排队，实现跨进程串行取号。
            // 锁提示属会话行为，非数据库对象变更（结构零改动约束不受影响）。
            var locked = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                SELECT COUNT(1) FROM tbdjgs WITH (UPDLOCK, ROWLOCK)
                WHERE djname = @dj AND dpm = @dpm
                """,
                new
                {
                    dj = new DbString { Value = djName, IsAnsi = false, Length = 16 },
                    dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
            if (locked == 0)
                throw new AppException("DOC_NO_FAILED", $"取单号失败：单据类型「{djName}」部门「{dpm}」未在 tbdjgs 配置前缀", 422);

            var docNo = await ExecGetNewDhAsync(conn, djName, dpm, isAdd: 1, ct, tx);

            // 防重：旧系统 pg_proc.getnewdh L2042 逻辑——目标业务表已存在该单号时
            // 取目标表最大单号的流水段修正 tbdjgs.lsh 后重取一次（表/字段/类型过滤随调用方）
            var typeSql = target.TypeFilter is null ? "" : $" AND type = N'{target.TypeFilter}'";
            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(1) FROM {target.Table} WITH (NOLOCK) WHERE {target.Field} = @dh{typeSql}",
                new { dh = new DbString { Value = docNo, IsAnsi = true, Length = 15 } },
                transaction: tx, cancellationToken: ct));
            if (exists > 0)
            {
                await FixLshAsync(conn, djName, dpm, target, ct, tx);
                docNo = await ExecGetNewDhAsync(conn, djName, dpm, isAdd: 1, ct, tx);

                var stillExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    $"SELECT COUNT(1) FROM {target.Table} WITH (NOLOCK) WHERE {target.Field} = @dh{typeSql}",
                    new { dh = new DbString { Value = docNo, IsAnsi = true, Length = 15 } },
                    transaction: tx, cancellationToken: ct));
                if (stillExists > 0)
                    throw new AppException("DOC_NO_CONFLICT", $"单号 {docNo} 仍冲突，请检查单据前缀设置（tbdjgs）", 409);
            }

            await tx.CommitAsync(ct);
            return docNo;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string> ExecGetNewDhAsync(SqlConnection conn, string djName, string dpm, int isAdd,
        CancellationToken ct, SqlTransaction? tx = null)
    {
        var cmd = new CommandDefinition(
            "dbo.getnewdh",
            new
            {
                ctype = new DbString { Value = djName, IsAnsi = false, Length = 10 }, // djname 为中文 nchar 语义（char 列，参数按默认 Unicode 可匹配）
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 1 },
                isadd = isAdd,
            },
            commandType: System.Data.CommandType.StoredProcedure,
            transaction: tx,
            cancellationToken: ct);
        var value = await conn.ExecuteScalarAsync<string>(cmd);
        if (string.IsNullOrWhiteSpace(value))
            throw new AppException("DOC_NO_FAILED", $"取单号失败：单据类型「{djName}」部门「{dpm}」未在 tbdjgs 配置前缀", 422);
        return value.Trim();
    }

    private static async Task FixLshAsync(SqlConnection conn, string djName, string dpm, DocNoCheckTarget target,
        CancellationToken ct, SqlTransaction? tx = null)
    {
        // 与旧系统一致（pg_proc.getnewdh L2044）：按目标业务表当前最大流水重置 tbdjgs.lsh，再重取
        var typeSql = target.TypeFilter is null ? "" : $" AND type = N'{target.TypeFilter}'";
        var sql = $"""
            DECLARE @lsh int;
            SELECT @lsh = ISNULL(MAX(CAST(RIGHT(RTRIM({target.Field}), 6) AS int)), 0)
            FROM {target.Table} WITH (NOLOCK)
            WHERE dpm = @dpm{typeSql}
              AND LEFT(RTRIM({target.Field}), LEN(RTRIM({target.Field})) - 6) = (SELECT RTRIM(djjx) + SUBSTRING(CAST(YEAR(GETDATE()) AS char(4)), 3, 2) FROM tbdjgs WHERE djname = @dj AND dpm = @dpm);
            UPDATE tbdjgs SET lsh = @lsh WHERE djname = @dj AND dpm = @dpm;
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new
            {
                dj = new DbString { Value = djName, IsAnsi = false, Length = 16 },
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
            },
            transaction: tx,
            cancellationToken: ct));
    }
}
