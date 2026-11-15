using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Stock;
using Xunit;

namespace QPGL.Tests.Replay;

/// <summary>
/// 入库退货防负库存（2026-11-15 需求，契约 §12）：
/// ① 库存拆两仓位（60+40）退 80 → 拆分冲减，各批次 kcamount ≥ 0；
/// ② 合计不足 → CheckStockAsync 返回不足行、直连确认兜底 STOCK_CONCURRENT；NoStock 确认写负数流水且 tbisto 不动；
/// ③ 行级拆分：够扣行实扣、不足行转不扣库存。
/// </summary>
[Collection("ReplaySerial")]
public class NegativeStockGuardTests
{
    private sealed class StaticCs(string cs) : IConnectionStringProvider
    {
        public string ConnectionString { get; } = cs;
    }

    private static PurchaseReturnRepository CreateRepo() => new(
        new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)),
        new DocNoService(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString))));

    private const string Cp = "回放厂牌R";
    private const string Nno = "RN27A";
    private const string Depot = "01";
    private const string Dept = ReplayConfig.Dept;

    /// <summary>种子：两批次库存（A1 仓 kc1 / A2 仓 kc2）+ 可退入库流水（cno RN27RK1，jkamount=jk，ro=0，仓位 A1）。</summary>
    private static void Seed(SqlConnection conn, decimal kc1, decimal kc2, decimal jk)
    {
        conn.Execute(
            "DELETE FROM tbisto WHERE rtrim(cp) = @cp AND rtrim(nno) = @nno AND dpm = @dpm AND depotno = @depot",
            new { cp = Cp, nno = Nno, dpm = Dept, depot = Depot });
        conn.Execute(
            "DELETE FROM tbistoed WHERE rtrim(cp) = @cp AND rtrim(nno) = @nno AND dpm = @dpm",
            new { cp = Cp, nno = Nno, dpm = Dept });
        foreach (var (posi, kc) in new[] { ("A1", kc1), ("A2", kc2) })
            conn.Execute(
                """
                INSERT INTO tbisto (cp, nno, na1, ty, unit, kcamount, iprc, iprj, oprc, oprb, posi, depotno, dpm, cxnno, kcbhdate)
                VALUES (@cp, @nno, N'回放配件R27', N'回放类型', N'个', @kc, 50, 60, 80, 75, @posi, @depot, @dpm, N'', @today)
                """,
                new { cp = Cp, nno = Nno, kc, posi, depot = Depot, dpm = Dept, today = DateTime.Today });
        conn.Execute(
            """
            INSERT INTO tbistoed (cp, nno, na1, ty, unit, jkamount, iprj, iprc, oprc, posi, depotno, dpm, indate, cno, seano, isord,
                                  per, fkfs, pno, gyname, gyno, ywr, note, mark, type, box, dc, fa, qrdate, freight)
            VALUES (@cp, @nno, N'回放配件R27', N'回放类型', N'个', @jk, 60, 50, 80, N'A1', @depot, @dpm, @today,
                    N'RN27RK1', N'', N'N', N'REPLAY', N'挂帐', N'PN27', N'回放供应商R', N'GYN27', N'YWR27',
                    N'回放入库备注', N'已确认', N'', N'', N'A', N'回放产地', @today, 0)
            """,
            new { cp = Cp, nno = Nno, jk, depot = Depot, dpm = Dept, today = DateTime.Today });
    }

    private static int SourceId(SqlConnection conn) => conn.ExecuteScalar<int>(
        "SELECT id FROM tbistoed WHERE rtrim(cp) = @cp AND rtrim(nno) = @nno AND dpm = @dpm AND rtrim(cno) = 'RN27RK1'",
        new { cp = Cp, nno = Nno, dpm = Dept });

    private static List<(string posi, decimal kc)> Batches(SqlConnection conn) => conn
        .Query<(string posi, decimal kc)>("SELECT RTRIM(posi) posi, kcamount kc FROM tbisto " +
            "WHERE rtrim(cp) = @cp AND rtrim(nno) = @nno AND dpm = @dpm AND depotno = @depot ORDER BY posi",
            new { cp = Cp, nno = Nno, dpm = Dept, depot = Depot })
        .AsList();

    private static ConfirmPurchaseReturnCommand Cmd(int sourceId, decimal amount, bool noStock = false) => new()
    {
        Fkfs = "现金",
        Lines = [new PurchaseReturnLineInput { SourceId = sourceId, Amount = amount, Posi = "A1", Depotno = Depot, NoStock = noStock }],
    };

    /// <summary>① 拆分冲减：两仓位 60+40 退 80 → A1 扣 60、A2 扣 20，各批次 ≥ 0；源行 ro 累计。</summary>
    [Fact]
    public void Confirm_SplitAcrossPosis_NoNegativeBatch()
    {
        var repo = CreateRepo();
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        int sourceId;
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            Seed(conn, 60, 40, 80);
            sourceId = SourceId(conn);
        }
        var result = repo.ConfirmAsync(Cmd(sourceId, 80), Dept, ReplayConfig.Operator).GetAwaiter().GetResult();

        Assert.Equal(1, result.LineCount);
        using var check = ReplayEnv.Open();
        check.Open();
        var batches = Batches(check);
        Assert.All(batches, b => Assert.True(b.kc >= 0, $"批次 {b.posi} 库存 {b.kc} 为负"));
        Assert.Equal(0, batches.First(b => b.posi == "A1").kc);
        Assert.Equal(20, batches.First(b => b.posi == "A2").kc);
        Assert.Equal(80, check.ExecuteScalar<decimal>("SELECT ro FROM tbistoed WHERE id = @id", new { id = sourceId }));
        Assert.Equal(-80, check.ExecuteScalar<decimal>(
            "SELECT jkamount FROM tbistoed WHERE ro = @id AND jkamount < 0", new { id = sourceId }));
    }

    /// <summary>② 合计不足：CheckStockAsync 返回不足行；直连确认由拆分冲减兜底 STOCK_CONCURRENT；NoStock 确认库存不动、负数流水带"未扣"。</summary>
    [Fact]
    public void CheckStock_Shortage_NoStockConfirm_KeepsStockUntouched()
    {
        var repo = CreateRepo();
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        int sourceId;
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            Seed(conn, 10, 0, 80);
            sourceId = SourceId(conn);
        }

        var shortages = repo.CheckStockAsync(Cmd(sourceId, 80), Dept).GetAwaiter().GetResult();
        var s = Assert.Single(shortages);
        Assert.Equal(0, s.LineIndex);
        Assert.Equal(80, s.Amount);
        Assert.Equal(10, s.Available);

        var ex = Assert.Throws<AppException>(() =>
            repo.ConfirmAsync(Cmd(sourceId, 80), Dept, ReplayConfig.Operator).GetAwaiter().GetResult());
        Assert.Equal("STOCK_CONCURRENT", ex.Code);

        // VM 弹窗"是"路径：不足组行转 NoStock 再确认
        var result = repo.ConfirmAsync(Cmd(sourceId, 80, noStock: true), Dept, ReplayConfig.Operator).GetAwaiter().GetResult();
        Assert.Equal(1, result.LineCount);

        using var check = ReplayEnv.Open();
        check.Open();
        Assert.All(Batches(check), b => Assert.Equal(b.posi == "A1" ? 10m : 0m, b.kc));
        Assert.Equal(80, check.ExecuteScalar<decimal>("SELECT ro FROM tbistoed WHERE id = @id", new { id = sourceId }));
        Assert.Equal(-80, check.ExecuteScalar<decimal>(
            "SELECT jkamount FROM tbistoed WHERE ro = @id AND jkamount < 0", new { id = sourceId }));
        var ty = check.ExecuteScalar<string>(
            "SELECT ty FROM tbistoed WHERE ro = @id AND jkamount < 0", new { id = sourceId }) ?? "";
        Assert.StartsWith("未扣", ty.Trim());
    }

    /// <summary>③ 行级拆分（2026-11-15 需求）：同组两行按购物车顺序模拟——行1 退 2 可扣（池 4→2）、
    /// 行2 退 3 不足转不扣库存；确认后库存 = 2，行1 实扣、行2 标"未扣"。</summary>
    [Fact]
    public void Confirm_LineLevelSplit_DeductsSufficientLinesOnly()
    {
        var repo = CreateRepo();
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        int sourceId;
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            Seed(conn, 4, 0, 10);   // 库存批次 4 个，源入库流水 jkamount=10（可退充足）
            sourceId = SourceId(conn);
        }

        // 预检：行1（退2）够扣，行2（退3）不足（池剩 2）
        var cmd = new ConfirmPurchaseReturnCommand
        {
            Fkfs = "现金",
            Lines =
            [
                new PurchaseReturnLineInput { SourceId = sourceId, Amount = 2, Posi = "A1", Depotno = Depot },
                new PurchaseReturnLineInput { SourceId = sourceId, Amount = 3, Posi = "A1", Depotno = Depot },
            ],
        };
        var shortages = repo.CheckStockAsync(cmd, Dept).GetAwaiter().GetResult();
        var shortage = Assert.Single(shortages);
        Assert.Equal(1, shortage.LineIndex);
        Assert.Equal(3, shortage.Amount);
        Assert.Equal(2, shortage.Available);

        // VM"是"路径：不足行转 NoStock 后确认 → 行1 实扣、行2 不扣
        cmd.Lines[1].NoStock = true;
        var result = repo.ConfirmAsync(cmd, Dept, ReplayConfig.Operator).GetAwaiter().GetResult();
        Assert.Equal(2, result.LineCount);

        using var check = ReplayEnv.Open();
        check.Open();
        Assert.Equal(2, check.ExecuteScalar<decimal>(
            "SELECT kcamount FROM tbisto WHERE rtrim(cp) = @cp AND rtrim(nno) = @nno AND dpm = @dpm AND depotno = @depot",
            new { cp = Cp, nno = Nno, dpm = Dept, depot = Depot }));
        Assert.Equal(5, check.ExecuteScalar<decimal>(
            "SELECT ro FROM tbistoed WHERE id = @id", new { id = sourceId }));   // 两行 ro 均累计（2+3）
        var flags = check.Query<string>(
            "SELECT rtrim(ty) FROM tbistoed WHERE ro = @id AND jkamount < 0 ORDER BY jkamount", new { id = sourceId }).AsList();
        Assert.Equal(2, flags.Count);
        Assert.StartsWith("未扣", flags[0]);          // 退 3 行（jk=-3）：不扣库存
        Assert.False(flags[1].StartsWith("未扣"));   // 退 2 行（jk=-2）：实扣
    }
}
