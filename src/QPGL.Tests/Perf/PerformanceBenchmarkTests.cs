using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Domain.Query;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.Partners;
using QPGL.Infrastructure.Parts;
using QPGL.Infrastructure.Query;
using Xunit;
using Xunit.Abstractions;

namespace QPGL.Tests.Perf;

/// <summary>
/// §10.1/§11.4 性能基准测试：隔离副本库（rscar_replay，本地 SQLEXPRESS）真实生产量级实测。
/// 量级摸底（2026-10-23）：tbsada 124 万行 / tbysyf 59 万 / tbgath 47 万 / tbprnoty 3 万配件 / tbgu 4 千客户。
/// 指标为硬性验收项（P95）：
///   精确点查（单号）          ≤ 20ms
///   常用列表分页（50 行/页）   ≤ 100ms
///   配件编号/名称前缀搜索      ≤ 100ms
///   模糊"包含"搜索（LIKE %x%） ≤ 300ms（打库兜底路径；§10.3 内存索引上线后不打库）
///   统计聚合（单据金额汇总）   ≤ 2s
/// 测量方法：每场景预热 2 次 + 采样 10 次取 P95（第 10 位/样本量 10 → 取最大值近似 P95）。
/// 运行前置：rscar_replay 已还原（同契约回放，见 ReplayConfig）。
/// </summary>
[Collection("ReplaySerial")]
public class PerformanceBenchmarkTests(ITestOutputHelper output)
{
    private static readonly string Cs = Replay.ReplayConfig.ConnectionString;
    private const int Warmup = 2;
    private const int Samples = 10;

    private static SqlConnectionFactory NewFactory() =>
        new(new StaticCs(Cs));

    private sealed class StaticCs(string cs) : IConnectionStringProvider
    {
        public string ConnectionString { get; } = cs;
    }

    /// <summary>预热 + 采样，返回毫秒样本升序。P95 = 样本量 10 时取第 10 位（最大值，保守）。</summary>
    private static async Task<List<double>> MeasureAsync(Func<Task> act)
    {
        for (var i = 0; i < Warmup; i++) await act();
        var sw = new Stopwatch();
        var samples = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            sw.Restart();
            await act();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return samples;
    }

    private static void AssertP95(List<double> samples, double limitMs, string label)
    {
        var p95 = samples[^1];   // 样本量 10：第 10 位 ≈ P95（保守取最大）
        Assert.True(p95 <= limitMs,
            $"性能基准未达标：{label} P95={p95:F1}ms > {limitMs}ms（样本：{string.Join(", ", samples.Select(s => $"{s:F0}"))}）");
    }

    private async Task Report(string label, List<double> samples, double limitMs) =>
        output.WriteLine($"[PERF] {label,-38} P50={samples[4],8:F1}ms  P95={samples[^1],8:F1}ms  目标≤{limitMs}ms  {(samples[^1] <= limitMs ? "PASS" : "FAIL")}");

    // ── 1. 精确点查：单号（tbsada 124 万行，索引 tbsada.paper）→ 目标 ≤ 20ms ──

    [Fact]
    public async Task B1_PointQuery_ByPaper_Under20ms()
    {
        // 种子：取最近一张销售单（存在性保证，且随快照重置稳定）
        string paper;
        await using (var conn = new SqlConnection(Cs))
        {
            paper = await conn.ExecuteScalarAsync<string>(
                "SELECT TOP (1) paper FROM tbsada WITH (NOLOCK) WHERE type = N'销售' AND trigger_mark = 0 ORDER BY outdate DESC, id DESC")
                ?? throw new InvalidOperationException("副本库无销售单种子");
        }
        var paperArg = paper.Trim();

        var samples = await MeasureAsync(async () =>
        {
            await using var conn = new SqlConnection(Cs);
            await conn.QueryAsync<OutDocLine>(
                """
                SELECT c.id, c.cp, c.nno, c.na1, c.ckamount, c.oprc, c.oprct,
                       CAST(c.oprct * (100 - c.yhl) / 100 AS numeric(12,2)) AS yhloprct,
                       c.posi, c.unit, c.depotno, c.remark
                FROM tbsada c WITH (NOLOCK)
                WHERE c.paper = @paper AND c.type = N'销售' AND c.dpm = 'A'
                ORDER BY c.id
                """,
                new { paper = new DbString { Value = paperArg, IsAnsi = true, Length = 10 } });
        });
        await Report("点查·单号明细(124万行)", samples, 20);
        AssertP95(samples, 20, "点查·单号明细");
    }

    // ── 2. 常用列表分页：出库单查询（默认当月，排序 outdate DESC）→ 目标 ≤ 100ms ──

    [Fact]
    public async Task B2_PagedDocQuery_Under100ms()
    {
        var repo = new DocQueryRepository(NewFactory());
        var request = new OutDocQueryRequest
        {
            Type = "销售",
            DateFrom = new DateTime(2026, 10, 1),
            DateTo = new DateTime(2026, 10, 23),
            Page = 1,
            PageSize = 50,
        };
        var samples = await MeasureAsync(() => repo.SearchOutDocsAsync(request));
        await Report("分页·出库单查询当月50行", samples, 100);
        AssertP95(samples, 100, "分页·出库单查询");
    }

    // ── 3. 配件搜索：编号前缀（tbprnoty 3 万行，索引 cp_nno）→ 目标 ≤ 100ms ──

    [Fact]
    public async Task B3_PartPrefixSearch_Under100ms()
    {
        var repo = new PartProfileRepository(NewFactory());
        var request = new PartProfileQueryRequest { Nno = "Z-", Page = 1, PageSize = 50 };
        var samples = await MeasureAsync(() => repo.SearchAsync(request));
        await Report("搜索·配件编号前缀(3万行)", samples, 100);
        AssertP95(samples, 100, "配件编号前缀搜索");
    }

    // ── 4. 模糊"包含"搜索：配件名称 LIKE %x%（§10.3 内存索引上线前的打库兜底路径）→ 目标 ≤ 300ms ──

    [Fact]
    public async Task B4_PartFuzzySearch_Under300ms()
    {
        var repo = new PartProfileRepository(NewFactory());
        var request = new PartProfileQueryRequest { Na1 = "减震", Page = 1, PageSize = 50 };
        var samples = await MeasureAsync(() => repo.SearchAsync(request));
        await Report("模糊·配件名称包含(3万行)", samples, 300);
        AssertP95(samples, 300, "配件名称模糊搜索");
    }

    // ── 5. 客户搜索：编号前缀（tbgu 4 千行）→ 归入"客户搜索 ≤ 100ms"档 ──

    [Fact]
    public async Task B5_PartnerSearch_Under100ms()
    {
        var repo = new PartnerRepository(NewFactory());
        var request = new PagedRequest { Keyword = "KH", Page = 1, PageSize = 50 };
        var samples = await MeasureAsync(() => repo.SearchAsync(request));
        await Report("搜索·客户编号前缀(4千行)", samples, 100);
        AssertP95(samples, 100, "客户编号前缀搜索");
    }

    // ── 6. 统计聚合：单据金额汇总（收款窗口依据，Σ oprct/oprc/折后）→ 目标 ≤ 2s ──

    [Fact]
    public async Task B6_SummaryAggregate_Under2s()
    {
        var repo = new DocQueryRepository(NewFactory());
        string paper;
        await using (var conn = new SqlConnection(Cs))
        {
            paper = (await conn.ExecuteScalarAsync<string>(
                "SELECT TOP (1) paper FROM tbsada WITH (NOLOCK) WHERE type = N'销售' AND trigger_mark = 0 AND dpm = 'A' ORDER BY outdate DESC, id DESC"))!.Trim();
        }
        var samples = await MeasureAsync(() => repo.GetOutDocSummaryAsync(paper, "销售", "A"));
        await Report("聚合·单据金额汇总", samples, 2000);
        AssertP95(samples, 2000, "单据金额汇总");
    }

    // ── 7. 月度进销存聚合（tbsada 124 万行全月度聚合，服务端 GROUP BY）→ 目标 ≤ 2s ──

    [Fact]
    public async Task B7_MonthlyAggregate_Under2s()
    {
        var samples = await MeasureAsync(async () =>
        {
            await using var conn = new SqlConnection(Cs);
            await conn.QueryAsync<MonthAggRow>(
                """
                SELECT dpm, YEAR(outdate) yr, MONTH(outdate) mon,
                       COUNT(DISTINCT paper) papers, SUM(ckamount) qty, SUM(oprct) amt
                FROM tbsada WITH (NOLOCK)
                WHERE type = N'销售' AND trigger_mark = 0 AND outdate >= '2026-01-01' AND outdate < '2027-01-01'
                GROUP BY dpm, YEAR(outdate), MONTH(outdate)
                """);
        });
        await Report("聚合·月度进销存(124万行)", samples, 2000);
        AssertP95(samples, 2000, "月度进销存聚合");
    }

    private sealed class MonthAggRow
    {
        public string dpm { get; set; } = "";
        public int yr { get; set; }
        public int mon { get; set; }
        public int papers { get; set; }
        public decimal qty { get; set; }
        public decimal amt { get; set; }
    }
}
