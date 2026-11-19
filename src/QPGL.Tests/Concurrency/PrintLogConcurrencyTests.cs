using System.Collections.Concurrent;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.Printing;
using Xunit;

namespace QPGL.Tests.Concurrency;

/// <summary>
/// print_log 打印状态并发加固验证（2026-11-19 未打印提醒功能）：
///   C3 建表竞态 —— DROP 表后重置进程内建表缓存，20 任务屏障同时放行模拟 20 台电脑
///       首次并发建表/建索引：竞态触发 2714/1913（对象/索引已存在）须被幂等 catch 吞掉，
///       全部任务无异常、表与唯一索引最终存在。
///   C4 同单并发标记 —— 20 任务并发 MarkPrintedAsync 同一单号（模拟两台以上同时打印同一张单）：
///       upsert 唯一键冲突（2601/2627）须重试转更新，断言全部成功、恰 1 行、print_count=20。
///   C5 不同单并发标记 —— 50 任务各标一单（常规多客户端负载）：全部成功、行数精确。
///   C6 标记/取消混发 —— 同单 10 打印 + 10 取消并发（多客户端对同一单人工补正与打印交错）：
///       无异常，终态为「无行」或「1 行且 printed=1」，不出现脏中间态。
/// 测试单号用 CSPL 前缀（print_log 独立表，不触碰业务表；每用例开头清理）。
/// </summary>
[Collection("ReplaySerial")]
public class PrintLogConcurrencyTests
{
    private static readonly string Cs = Replay.ReplayConfig.ConnectionString;
    private const string Operator = Replay.ReplayConfig.Operator;

    private static SqlConnectionFactory NewFactory() => new(new StaticCs(Cs));

    private sealed class StaticCs(string cs) : IConnectionStringProvider
    {
        public string ConnectionString { get; } = cs;
    }

    /// <summary>重置进程内「已建表」缓存（模拟新客户端进程首次访问）。</summary>
    private static void ResetEnsureTask()
    {
        typeof(PrintLogRepository)
            .GetField("_ensureTask", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, null);
    }

    private static async Task CleanTestSnsAsync()
    {
        await using var conn = new SqlConnection(Cs);
        // 表可能尚未建（C3 删表后并发窗口期）——先确保存在再清理
        await conn.ExecuteAsync("IF OBJECT_ID('print_log', 'U') IS NOT NULL DELETE FROM print_log WHERE sn LIKE 'CSPL%'");
    }

    private static async Task<int> CountRowsAsync(string sn)
    {
        await using var conn = new SqlConnection(Cs);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM print_log WHERE sn = @Sn",
            new { Sn = sn });
    }

    // ── C3：多客户端并发首次建表（2714/1913 幂等吞掉） ──

    [Fact]
    public async Task C3_EnsureTable_20ClientsConcurrent_AllSucceed()
    {
        // 模拟全新部署：删表 + 重置进程内缓存，使所有并发任务都进入「检查不存在 → 创建」窗口
        await using (var conn = new SqlConnection(Cs))
        {
            await conn.ExecuteAsync("IF OBJECT_ID('print_log', 'U') IS NOT NULL DROP TABLE print_log");
        }
        ResetEnsureTask();

        var repo = new PrintLogRepository(NewFactory());
        const int concurrency = 20;
        var errors = new ConcurrentBag<string>();

        // 屏障：20 个任务全部就位后同时放行，最大化「同时通过表存在检查」的竞态概率。
        // 必须异步等待（TaskCompletionSource）：同步 gate.Wait() 会在 ToArray() 枚举时
        // 于未放行的门上阻塞枚举线程，造成测试自锁死锁
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            try
            {
                await gate.Task;
                await repo.EnsureTableAsync();
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }
        }).ToArray();
        await Task.Delay(300);   // 等全部任务阻塞在 gate
        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Empty(errors.ToArray());

        // 表与唯一索引最终存在
        await using (var conn = new SqlConnection(Cs))
        {
            var table = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM sys.tables WHERE name = 'print_log'");
            var index = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM sys.indexes WHERE object_id = OBJECT_ID('print_log') AND name = 'UX_print_log_sn'");
            Assert.Equal(1, table);
            Assert.Equal(1, index);
        }
    }

    // ── C4：20 客户端并发标记同一单（2601/2627 重试转更新） ──

    [Fact]
    public async Task C4_MarkPrinted_20ConcurrentSameSn_AllSucceed()
    {
        await CleanTestSnsAsync();

        const string sn = "CSPL-C4";
        var repo = new PrintLogRepository(NewFactory());
        await repo.EnsureTableAsync();

        const int concurrency = 20;
        var errors = new ConcurrentBag<string>();
        var ok = 0;

        var tasks = Enumerable.Range(0, concurrency).Select<int, Func<Task>>(_ => async () =>
        {
            try
            {
                await repo.MarkPrintedAsync("销售", sn, Operator);
                Interlocked.Increment(ref ok);
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }
        }).Select(f => f()).ToArray();
        await Task.WhenAll(tasks);

        Assert.True(ok == concurrency, $"标记成功 {ok}/{concurrency}，失败：{string.Join("；", errors.Take(5))}");

        // 恰 1 行（唯一索引保证无重复行）且计数累加到 20（冲突重试不丢标记）
        Assert.Equal(1, await CountRowsAsync(sn));
        await using var conn = new SqlConnection(Cs);
        var row = await conn.QuerySingleAsync<(int PrintCount, bool Printed, string LastOperator)>(
            "SELECT print_count, printed, ISNULL(last_operator, '') FROM print_log WHERE sn = @Sn",
            new { Sn = sn });
        Assert.Equal(concurrency, row.PrintCount);
        Assert.True(row.Printed);
        Assert.Equal(Operator, row.LastOperator);
    }

    // ── C5：50 客户端各标一单（常规多客户端负载） ──

    [Fact]
    public async Task C5_MarkPrinted_50ConcurrentDistinctSns_NoLoss()
    {
        await CleanTestSnsAsync();

        var repo = new PrintLogRepository(NewFactory());
        await repo.EnsureTableAsync();

        const int concurrency = 50;
        var errors = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, concurrency).Select<int, Func<Task>>(i => async () =>
        {
            try
            {
                await repo.MarkPrintedAsync("销售", $"CSPLC5{i:D3}", Operator);
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }
        }).Select(f => f()).ToArray();
        await Task.WhenAll(tasks);

        Assert.Empty(errors.ToArray());
        Assert.Equal(concurrency, await CountRowsLikeAsync("CSPLC5%"));
    }

    // ── C6：同一单「标记已打印 / 取消标记」混发（人工补正与打印交错） ──

    [Fact]
    public async Task C6_MixMarkAndUnmark_20Concurrent_ConsistentFinalState()
    {
        await CleanTestSnsAsync();

        const string sn = "CSPL-C6";
        var repo = new PrintLogRepository(NewFactory());
        await repo.EnsureTableAsync();

        var errors = new ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, 20).Select<int, Func<Task>>(i => async () =>
        {
            try
            {
                if (i % 2 == 0)
                    await repo.MarkPrintedAsync("销售", sn, Operator);
                else
                    await repo.UnmarkPrintedAsync(sn);
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }
        }).Select(f => f()).ToArray();
        await Task.WhenAll(tasks);

        Assert.Empty(errors.ToArray());

        // 终态一致：要么无行（最后一步是取消），要么 1 行且 printed=1（最后一步是打印）；无脏中间态
        var rows = await CountRowsAsync(sn);
        if (rows > 0)
        {
            Assert.Equal(1, rows);
            await using var conn = new SqlConnection(Cs);
            var printed = await conn.ExecuteScalarAsync<bool>(
                "SELECT printed FROM print_log WHERE sn = @Sn", new { Sn = sn });
            Assert.True(printed);
        }
    }

    private static async Task<int> CountRowsLikeAsync(string pattern)
    {
        await using var conn = new SqlConnection(Cs);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM print_log WHERE sn LIKE @Pattern",
            new { Pattern = pattern });
    }
}
