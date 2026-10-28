using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Sales;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Sales;
using Xunit;
using Xunit.Abstractions;

namespace QPGL.Tests.Concurrency;

/// <summary>
/// §11.3 并发压测：隔离副本库（rscar_replay）。
///   C1 getnewdh 取号 200 并发无重号 —— 绕过进程内锁（每任务直连模拟独立客户端），
///       走「UPDLOCK 锁行 → getnewdh」完整路径（多客户端跨进程串行的核心保障）。
///   C2 单据提交 50 并发无死锁 —— SaleRepository.CreateAsync 混合负载：
///       高频同配件（热点批次 UPDLOCK 排队）+ 分散配件（不同锁目标），
///       断言全部成功、无死锁异常、库存冲减账实相符。
/// 每场景前快照重置（回放环境复用），互不污染；副本库连接串池上限 50（C2 需 50 并发连接）。
/// </summary>
[Collection("ReplaySerial")]
public class ConcurrencyTests
{
    private static readonly string Cs = Replay.ReplayConfig.ConnectionString;
    private const string Dept = Replay.ReplayConfig.Dept;
    private const string Operator = Replay.ReplayConfig.Operator;
    private const string SeedCp = "中驭";
    private const string SeedNnoHot = "Z-ZP";        // 库存 10 万热点批次
    private const string SeedNnoWarm = "Z-YF";       // 库存 9.7 万第二热点

    private static SqlConnectionFactory NewFactory() => new(new StaticCs(Cs));

    private sealed class StaticCs(string cs) : IConnectionStringProvider
    {
        public string ConnectionString { get; } = cs;
    }

    // ── C1：getnewdh 200 并发取号无重号（§11.3） ──

    [Fact]
    public async Task C1_GetNewDocNo_200Concurrent_NoDuplicates()
    {
        Replay.ReplayEnv.EnsureSnapshot();
        Replay.ReplayEnv.ResetToSnapshot();

        var baseLsh = 0;
        await using (var conn = new SqlConnection(Cs))
        {
            baseLsh = await conn.ExecuteScalarAsync<int>(
                "SELECT lsh FROM tbdjgs WHERE djname = N'销售单' AND dpm = @dpm",
                new { dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 } });
        }

        const int concurrency = 200;
        var results = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<string>();

        // 每任务独立 DocNoService 实例 + 独立连接（模拟 200 个独立客户端进程）
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            try
            {
                var svc = new DocNoService(NewFactory());
                results.Add(await svc.GetNewDocNoAsync("销售单", Dept));
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }).ToArray();
        await Task.WhenAll(tasks);

        Assert.Empty(errors.ToArray());

        var distinct = results.Distinct().ToList();
        Assert.True(distinct.Count == concurrency,
            $"取号重号：{concurrency} 并发只得到 {distinct.Count} 个不同单号");

        // 流水号连续性：lsh 从 baseLsh+1 递增（UPDLOCK 串行保证无跳号）
        var seqs = results.Select(p => int.Parse(p[^6..])).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(baseLsh + 1, concurrency), seqs);
    }

    // ── C2：单据提交 50 并发无死锁（§11.3/§13 锁顺序固定） ──

    [Fact]
    public async Task C2_CreateSale_50Concurrent_NoDeadlock()
    {
        Replay.ReplayEnv.EnsureSnapshot();
        Replay.ReplayEnv.ResetToSnapshot();

        // 种子库存快照（压测后账实核对基准）
        var seedHot = await GetStockAsync(SeedNnoHot);
        var seedWarm = await GetStockAsync(SeedNnoWarm);
        Assert.True(seedHot >= 60, $"热点批次库存不足压测用量（{seedHot}）");
        Assert.True(seedWarm >= 60, $"次热批次库存不足压测用量（{seedWarm}）");

        var sale = new SaleRepository(NewFactory(), new DocNoService(NewFactory()));
        const int concurrency = 50;
        var errors = new ConcurrentBag<string>();
        var ok = 0;

        // 混合负载：50 单 × 1 行，Qty=1；奇偶交替热点/次热配件（不同批次锁目标）
        var tasks = Enumerable.Range(0, concurrency)
            .Select<int, Func<Task>>(i => async () =>
            {
                try
                {
                    var cmd = new CreateSaleCommand
                    {
                        Dept = Dept,
                        Gname = "回放专用客户R",
                        Lines =
                        [
                            new SaleLineInput { Cp = SeedCp, Nno = i % 2 == 0 ? SeedNnoHot : SeedNnoWarm, Qty = 1 },
                        ],
                    };
                    await sale.CreateAsync(cmd, Operator);
                    Interlocked.Increment(ref ok);
                }
                catch (Exception ex)
                {
                    errors.Add($"{ex.GetType().Name}: {ex.Message}");
                }
            })
            .Select(f => f())
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.True(ok == concurrency, $"提交成功 {ok}/{concurrency}，失败：{string.Join("；", errors.Take(5))}");
        Assert.DoesNotContain(errors, e => e.Contains("deadlock", StringComparison.OrdinalIgnoreCase) ||
                                           e.Contains("死锁", StringComparison.Ordinal));

        // 库存账实相符：热点 -25、次热 -25（各 25 单 × 1）
        Assert.Equal(seedHot - concurrency / 2, await GetStockAsync(SeedNnoHot));
        Assert.Equal(seedWarm - concurrency / 2, await GetStockAsync(SeedNnoWarm));

        // 单号无重号（提交路径含取号，二次兜底断言）
        var papers = await QueryAsync<string>(
            "SELECT RTRIM(paper) FROM tbsada WITH (NOLOCK) WHERE type = N'销售' AND per = @per",
            new { per = new DbString { Value = Operator, IsAnsi = true, Length = 10 } });
        Assert.Equal(concurrency, papers.Distinct().Count());
    }

    private static async Task<decimal> GetStockAsync(string nno)
    {
        await using var conn = new SqlConnection(Cs);
        return await conn.ExecuteScalarAsync<decimal>(
            """
            SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WITH (NOLOCK)
            WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND (posi IS NULL OR posi NOT LIKE '报废%')
            """,
            new
            {
                dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 },
                cp = new DbString { Value = SeedCp, IsAnsi = false, Length = 10 },
                nno = new DbString { Value = nno, IsAnsi = true, Length = 18 },
            });
    }

    private static async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var conn = new SqlConnection(Cs);
        return (await conn.QueryAsync<T>(sql, param)).AsList();
    }
}
