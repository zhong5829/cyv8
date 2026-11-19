using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Printing;

/// <summary>未打印单据（提醒弹窗与状态栏计数用，qp11 UnprintedBill 同构）。</summary>
public class UnprintedBill
{
    /// <summary>单号（tbsada.paper）。</summary>
    public string Sn { get; set; } = "";
    /// <summary>单据类型（销售 / 退货——AH 号段，与出库单查询 sk 口径一致）。</summary>
    public string BillType { get; set; } = "";
    /// <summary>出库日期（tbsada.outdate，行内同值取 MAX）。</summary>
    public DateTime Datetime { get; set; }
    /// <summary>客户名称（tbsada.gname，tbsada 直存名称无需另查客户表）。</summary>
    public string ClientName { get; set; } = "";
    /// <summary>操作员（tbsada.per）。</summary>
    public string BillOperator { get; set; } = "";
    /// <summary>单据金额（Σ oprct×(100-yhl)/100，与出库单查询 totalamount 同口径）。</summary>
    public decimal Total { get; set; }
}

public interface IPrintLogRepository
{
    /// <summary>打印状态变化事件（标记已打印/未打印成功后触发；UI 层订阅以即时刷新状态栏计数）。</summary>
    event Action? PrintStatusChanged;

    /// <summary>确保 print_log 存储表存在（进程内只建表一次；qp11 PrintLogService 同构）。</summary>
    Task EnsureTableAsync(CancellationToken ct = default);

    /// <summary>标记单据已打印（打印成功后调用；重复打印累加打印次数，按 sn 定位 upsert）。</summary>
    Task MarkPrintedAsync(string billType, string sn, string operatorName, CancellationToken ct = default);

    /// <summary>查询当天创建且未打印的销售单/退货单（全店范围，不限操作员）。</summary>
    Task<List<UnprintedBill>> GetUnprintedTodayAsync(CancellationToken ct = default);

    /// <summary>批量查询指定单号中已打印的单号集合（查询列表展示打印状态用）。</summary>
    Task<HashSet<string>> GetPrintedSnsAsync(IEnumerable<string>? sns, CancellationToken ct = default);

    /// <summary>取消打印标记（人工补正）：删除记录，使其重新计入未打印。</summary>
    Task UnmarkPrintedAsync(string sn, CancellationToken ct = default);
}

/// <summary>
/// 单据打印状态存储（开发文档 §8.3.1 未打印单据提醒，qp11 PrintLogService 同构复刻）。
/// 存储于 print_log 表：只记录「已打印」的单据，表中无记录即视为未打印，
/// 因此不需要对历史单据做全量回填。首次访问自动建表（进程内仅一次）。
/// 注：本表为打印状态辅助表，不触碰任何业务表结构（2026-11-19 用户确认复刻 qp11 方案）。
/// </summary>
public sealed class PrintLogRepository(SqlConnectionFactory factory) : IPrintLogRepository
{
    private const string TableName = "print_log";
    private const string IndexName = "UX_print_log_sn";

    /// <summary>打印状态变化事件（标记已打印/未打印成功后触发；UI 层订阅以即时刷新状态栏计数）。</summary>
    public event Action? PrintStatusChanged;

    /// <summary>建表任务缓存，进程内只执行一次（并发安全）。</summary>
    private static Task? _ensureTask;
    private static readonly object EnsureLock = new();

    public Task EnsureTableAsync(CancellationToken ct = default)
    {
        if (_ensureTask != null) return _ensureTask;
        lock (EnsureLock)
        {
            _ensureTask ??= EnsureTableCoreAsync(ct);
        }
        return _ensureTask;
    }

    private async Task EnsureTableCoreAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            // SQL Server 2012：无 CREATE INDEX IF NOT EXISTS，先查元数据再建
            var tableExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM sys.tables WHERE name = @TableName",
                new { TableName }, cancellationToken: ct));
            if (tableExists == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    $@"CREATE TABLE {TableName} (
                        id bigint IDENTITY(1,1) NOT NULL,
                        bill_type nvarchar(10) NOT NULL,
                        sn varchar(15) NOT NULL,
                        printed bit NOT NULL CONSTRAINT DF_{TableName}_printed DEFAULT (0),
                        print_count int NOT NULL CONSTRAINT DF_{TableName}_print_count DEFAULT (0),
                        first_print_time datetime NULL,
                        last_print_time datetime NULL,
                        last_operator varchar(20) NULL,
                        CONSTRAINT PK_{TableName} PRIMARY KEY (id)
                    )", cancellationToken: ct));
            }

            var indexExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM sys.indexes WHERE object_id = OBJECT_ID(@TableName) AND name = @IndexName",
                new { TableName, IndexName }, cancellationToken: ct));
            if (indexExists == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    $"CREATE UNIQUE INDEX {IndexName} ON {TableName}(sn)", cancellationToken: ct));
            }
        }
        catch (SqlException ex) when (ex.Number is 2714 or 1913)
        {
            // 多客户端并发建表/建索引窗口：检查时不存在、创建时另一客户端刚建完
            // （2714=对象已存在 / 1913=索引已存在）→ 幂等视为成功，不清缓存不重试
        }
        catch
        {
            // 其它失败时清除缓存，允许下次访问重试
            _ensureTask = null;
            throw;
        }
    }

    public async Task MarkPrintedAsync(string billType, string sn, string operatorName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(billType) || string.IsNullOrWhiteSpace(sn)) return;

        await EnsureTableAsync(ct);
        await using var conn = await factory.OpenAsync(ct);

        // 多客户端并发首打同一单：两台同时 SELECT WITH (UPDLOCK) 都读不到行 → 都 INSERT，
        // 后提交方撞 sn 唯一索引（2601/2627）→ 回滚后重试一轮（此时行已存在，必走 UPDATE 分支）
        for (var attempt = 0; ; attempt++)
        {
            await using var txn = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                // 事务内加行锁读取：已存在则更新，不存在则插入（qp11 同款写法）。
                // 按 sn 定位而非 (bill_type, sn)：tbsada.paper 全局唯一，且各打印入口传入的
                // bill_type 未必一致（销售开单页查询模式对退货单也传「销售」），按 sn 定位避免同单两条记录
                var id = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                    $"SELECT id FROM {TableName} WITH (UPDLOCK) WHERE sn = @Sn",
                    new { Sn = sn.Trim() }, txn, cancellationToken: ct));

                if (id.HasValue)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        $@"UPDATE {TableName} SET bill_type = @BillType, printed = 1, print_count = print_count + 1,
                           last_print_time = GETDATE(), last_operator = @Operator
                           WHERE id = @Id",
                        new { BillType = billType.Trim(), Operator = TrimTo(operatorName, 20), Id = id.Value },
                        txn, cancellationToken: ct));
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        $@"INSERT INTO {TableName} (bill_type, sn, printed, print_count, first_print_time, last_print_time, last_operator)
                           VALUES (@BillType, @Sn, 1, 1, GETDATE(), GETDATE(), @Operator)",
                        new { BillType = billType.Trim(), Sn = sn.Trim(), Operator = TrimTo(operatorName, 20) },
                        txn, cancellationToken: ct));
                }

                await txn.CommitAsync(ct);
                PrintStatusChanged?.Invoke();
                return;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627 && attempt == 0)
            {
                // 并发首打唯一键冲突：回滚后重试一轮（转为更新已有记录）
                await txn.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                await txn.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
    }

    public async Task<List<UnprintedBill>> GetUnprintedTodayAsync(CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        await using var conn = await factory.OpenAsync(ct);

        // tbsada 为行式存储（一行一配件行）：按 paper+type+dpm 聚合成单据；
        // 退货单 type 也是「销售」（AH 号段），与出库单查询 sk 口径一致按单号前缀区分；
        // 排除内耗（tbsada_header.papertype）与已确认行 trigger_mark <> 0；
        // 作废单为物理删除（SaleRepository.VoidAsync DELETE），自动不进清单。
        var rows = (await conn.QueryAsync<UnprintedBill>(new CommandDefinition(
            $"""
            SELECT c.paper AS Sn,
                   CASE WHEN c.paper LIKE 'AH%' THEN N'退货' ELSE N'销售' END AS BillType,
                   MAX(c.outdate) AS Datetime,
                   MAX(RTRIM(c.gname)) AS ClientName,
                   MAX(RTRIM(c.per)) AS BillOperator,
                   SUM(CAST(c.oprct * (100 - c.yhl) / 100 AS numeric(12,2))) AS Total
            FROM tbsada c WITH (NOLOCK)
            LEFT JOIN (SELECT paper, dpm, papertype FROM tbsada_header WITH (NOLOCK)) h ON h.paper = c.paper AND h.dpm = c.dpm
            LEFT JOIN {TableName} p WITH (NOLOCK) ON p.sn = c.paper AND p.printed = 1
            WHERE c.type = @type AND c.trigger_mark = 0
              AND c.outdate >= @Today AND c.outdate < @Tomorrow
              AND ISNULL(h.papertype, '') <> N'内耗'
              AND p.sn IS NULL
            GROUP BY c.paper, c.type, c.dpm
            ORDER BY MAX(c.outdate) DESC
            """,
            new
            {
                type = new DbString { Value = "销售", IsAnsi = false, Length = 10 },
                Today = DateTime.Today,
                Tomorrow = DateTime.Today.AddDays(1),
            }, cancellationToken: ct))).AsList();

        return rows;
    }

    public async Task<HashSet<string>> GetPrintedSnsAsync(IEnumerable<string>? sns, CancellationToken ct = default)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = sns?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList() ?? [];
        if (list.Count == 0) return result;

        await EnsureTableAsync(ct);
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            $"SELECT sn FROM {TableName} WHERE printed = 1 AND sn IN @Sns",
            new { Sns = list }, cancellationToken: ct));
        foreach (var s in rows)
            if (!string.IsNullOrEmpty(s)) result.Add(s);
        return result;
    }

    public async Task UnmarkPrintedAsync(string sn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sn)) return;

        await EnsureTableAsync(ct);
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {TableName} WHERE sn = @Sn",
            new { Sn = sn.Trim() }, cancellationToken: ct));
        PrintStatusChanged?.Invoke();
    }

    /// <summary>char(20) 列按字节截断（UI 录入按列宽限制惯例）。</summary>
    private static string TrimTo(string? value, int maxLength)
    {
        var v = (value ?? "").Trim();
        return v.Length <= maxLength ? v : v[..maxLength];
    }
}
