using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.Parts;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Report;
using QPGL.Infrastructure.Stock;
using QPGL.Tests.Replay;
using Xunit;
using Xunit.Abstractions;

namespace QPGL.Tests.Uat;

/// <summary>
/// 二期 UAT 清单（开发文档/二期UAT验收清单.md）自动化核对：
/// B1/B2（ISAPPE 回归）、A10/A11（订单入库双分支）——在真实副本库 rscar_replay 上
/// 走新系统真实仓储代码路径，按清单「预期」逐条断言（快照重置保证可重复执行）。
/// 与契约回放（旧 vs 新 diff）互补：此处直接断言清单明示的契约事实。
/// </summary>
[Collection("ReplaySerial")]
[Trait("Category", "Uat")]
public class UatChecklistTests
{
    private readonly ITestOutputHelper _out;
    private static SqlConnectionFactory Factory => new(new StaticCs(ReplayConfig.ConnectionString));
    private const string Dept = ReplayConfig.Dept;
    private const string Per = ReplayConfig.Operator;
    private const string Cp = "中驭";

    public UatChecklistTests(ITestOutputHelper output) => _out = output;

    private sealed class StaticCs(string cs) : IConnectionStringProvider
    {
        public string ConnectionString { get; } = cs;
    }

    /// <summary>B1/B2 种子：匹配键（cp+nno+posi+depotno+dpm）唯一的正库存批次，保证断言确定。</summary>
    private static LegacyReplay.Batch GetUniqueBatch(SqlConnection conn)
        => conn.QueryFirst<LegacyReplay.Batch>(
            """
            SELECT TOP (1) id, RTRIM(cp) Cp, RTRIM(nno) Nno, RTRIM(na1) Na1, RTRIM(ty) Ty, RTRIM(fa) Fa,
                   RTRIM(dc) Dc, RTRIM(unit) Unit, kcamount Kc, iprj Iprj, iprc Iprc, oprc Oprc,
                   RTRIM(posi) Posi, RTRIM(depotno) Depotno, xno Xno, tno Tno, RTRIM(cxnnO) Cxnno, oprb Oprb
            FROM tbisto WITH (NOLOCK)
            WHERE dpm = @dpm AND kcamount > 0 AND NOT EXISTS (
                  SELECT 1 FROM tbisto t2 WITH (NOLOCK)
                  WHERE t2.cp = tbisto.cp AND t2.nno = tbisto.nno AND t2.posi = tbisto.posi
                    AND t2.depotno = tbisto.depotno AND t2.dpm = tbisto.dpm AND t2.id <> tbisto.id)
            ORDER BY kcamount DESC
            """,
            new { dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 } });

    /// <summary>B1：散件入库-命中批次（均价=2：匹配不含进价 + 加权均价 + tbistoed.kcamount=入库后快照）。</summary>
    [Fact]
    public void B1_InStock_HitBatch_WeightedAverage()
    {
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        var inStock = new InStockRepository(Factory, new DmService());

        decimal qty = 5m, newIprc, newIprj;
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            var seed = GetUniqueBatch(conn);
            newIprc = seed.Iprc + 10m;   // 与旧批次不同进价 → P_WAY=1 将新建批次，P_WAY=2 应命中
            newIprj = seed.Iprj + 20m;
            _out.WriteLine($"B1 种子批次 id={seed.Id} nno={seed.Nno} kc={seed.Kc} iprc={seed.Iprc} iprj={seed.Iprj} posi={seed.Posi} depotno={seed.Depotno}");

            var result = inStock.CreateAsync(new CreateInStockCommand
            {
                Cno = "UATB1CNO1",
                Fkfs = "现金",
                Lines =
                [
                    new InStockLineInput
                    {
                        Cp = seed.Cp, Nno = seed.Nno, Na1 = seed.Na1, Ty = seed.Ty, Fa = seed.Fa,
                        Dc = seed.Dc, Unit = seed.Unit, Jkamount = qty,
                        Iprc = newIprc, Iprj = newIprj, Oprc = seed.Oprc, Oprb = seed.Oprb,
                        Posi = seed.Posi, Depotno = seed.Depotno,
                    },
                ],
            }, Dept, Per, allowReentry: true).GetAwaiter().GetResult();

            // 命中既有批次（均价=2 匹配不含进价）
            var line = Assert.Single(result.Lines);
            Assert.True(line.MatchedBatchId == seed.Id,
                $"[B1] 应命中既有批次 id={seed.Id}，实际 {(line.MatchedBatchId?.ToString() ?? "新建")}（P_WAY 运行时=2 应忽略进价匹配）");
            Assert.True(line.KcamountAfter == seed.Kc + qty, $"[B1] 入库后数量 {line.KcamountAfter} ≠ 期望 {seed.Kc + qty}");

            // 加权均价重算（外币为含税价=1 → iprj 同步加权）
            var expectIprc = Math.Round(
                (Math.Max(seed.Iprc * seed.Kc, 0) + newIprc * qty) / (Math.Max(seed.Kc, 0) + qty), 4);
            var expectIprj = Math.Round((seed.Iprj * seed.Kc + newIprj * qty) / (seed.Kc + qty), 2);
            var batch = conn.QueryFirst<(decimal kc, decimal iprc, decimal iprj, DateTime kcbhdate)>(
                "SELECT kcamount, iprc, iprj, kcbhdate FROM tbisto WHERE id = @id",
                new { id = seed.Id });
            _out.WriteLine($"B1 库内结果 kc={batch.kc} iprc={batch.iprc}（期望 {expectIprc}） iprj={batch.iprj}（期望 {expectIprj}）");
            Assert.True(batch.kc == seed.Kc + qty, "[B1] tbisto.kcamount 未正确追加");
            Assert.True(batch.iprc == expectIprc, $"[B1] 加权进价 {batch.iprc} ≠ {expectIprc}");
            Assert.True(batch.iprj == expectIprj, $"[B1] 加权外币价 {batch.iprj} ≠ {expectIprj}");
            Assert.True(batch.kcbhdate == DateTime.Today, "[B1] kcbhdate 未更新为今天");

            // 台账：散件入库 kcamount=入库后快照、isord='N'
            var ed = conn.QueryFirst<(decimal jk, decimal kc, string isord, string type, string per)>(
                "SELECT jkamount, kcamount, RTRIM(isord), RTRIM(type), RTRIM(per) FROM tbistoed WHERE cno = 'UATB1CNO1'");
            Assert.True(ed.jk == qty && ed.kc == seed.Kc + qty && ed.isord == "N" && ed.type == "入库" && ed.per == Per,
                $"[B1] tbistoed 台账不符：jk={ed.jk} kc={ed.kc} isord={ed.isord} type={ed.type} per={ed.per}");
        }
    }

    /// <summary>B2：散件入库-新批次（未命中 INSERT，cxnno=S_NNO 生产未配置=''）。</summary>
    [Fact]
    public void B2_InStock_MissBatch_NewRow()
    {
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        var inStock = new InStockRepository(Factory, new DmService());

        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            var seed = GetUniqueBatch(conn);
            const string newPosi = "UATP2";
            _out.WriteLine($"B2 种子 nno={seed.Nno}，新仓位 {newPosi}（避开既有批次匹配键）");

            inStock.CreateAsync(new CreateInStockCommand
            {
                Cno = "UATB2CNO1",
                Fkfs = "现金",
                Lines =
                [
                    new InStockLineInput
                    {
                        Cp = seed.Cp, Nno = seed.Nno, Na1 = seed.Na1, Ty = seed.Ty, Fa = seed.Fa,
                        Dc = seed.Dc, Unit = seed.Unit, Jkamount = 3m,
                        Iprc = seed.Iprc, Iprj = seed.Iprj, Oprc = seed.Oprc, Oprb = seed.Oprb,
                        Posi = newPosi, Depotno = seed.Depotno,
                    },
                ],
            }, Dept, Per, allowReentry: true).GetAwaiter().GetResult();

            var row = conn.QueryFirstOrDefault<(int cnt, decimal kc, string cxnno, string posi)>(
                """
                SELECT COUNT(1), (SELECT kcamount FROM tbisto WHERE cp = @cp AND nno = @nno AND posi = @posi
                                  AND depotno = @depotno AND dpm = @dpm),
                       (SELECT RTRIM(cxnnO) FROM tbisto WHERE cp = @cp AND nno = @nno AND posi = @posi
                        AND depotno = @depotno AND dpm = @dpm), @posi
                FROM tbisto WHERE cp = @cp AND nno = @nno AND posi = @posi AND depotno = @depotno AND dpm = @dpm
                """,
                new
                {
                    cp = new DbString { Value = seed.Cp, IsAnsi = true, Length = 10 },
                    nno = new DbString { Value = seed.Nno, IsAnsi = true, Length = 18 },
                    posi = new DbString { Value = newPosi, IsAnsi = true, Length = 10 },
                    depotno = new DbString { Value = seed.Depotno, IsAnsi = true, Length = 10 },
                    dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 },
                });
            _out.WriteLine($"B2 新批次行数={row.cnt} kc={row.kc} cxnno='{row.cxnno}'");
            Assert.True(row.cnt == 1, "[B2] 新批次未创建");
            Assert.True(row.kc == 3m, "[B2] 新批次数量≠入库量 3");
            Assert.True(row.cxnno == "", $"[B2] cxnno 应为 S_NNO 映射（生产未配置=''），实际 '{row.cxnno}'");

            // 原批次不受影响
            var oldKc = conn.ExecuteScalar<decimal>("SELECT kcamount FROM tbisto WHERE id = @id", new { id = seed.Id });
            Assert.True(oldKc == seed.Kc, "[B2] 原批次数量被误改");
        }
    }

    /// <summary>A10：订单入库-自营行（gname 空）：ISAPPE 入库 + tbistoed.kcamount=入库前快照 + tborder.qty 累计。</summary>
    [Fact]
    public void A10_OrderIn_StockIn_Branch()
    {
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        var orderRepo = new OrderInRepository(Factory, new DocNoService(Factory), new DmService());
        const string seano = "UATA10SEANO", cno = "UATA10CNO1";
        decimal oqty = 10m;

        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            var part = GetUniqueBatch(conn);
            LegacyReplay.SeedOrderIn(conn, Dept, [(part, oqty, "", 1)], cno);

            // 导入（价率 1/1.3/1.2，外币含税价=1）
            var imported = orderRepo.ImportWholeOrderAsync(new ImportOrderInCommand { Seano = seano, Cno = cno },
                part.Cp, Dept, Per, "Z", "废", 1m, 1.3m, 1.2m, wbtohs: true).GetAwaiter().GetResult();
            Assert.True(imported == 1, $"[A10] 导入行数 {imported} ≠ 1");

            // 入库前快照（P_DEPOTNO 语义：gname 空 → depotno=草稿行 depotno=''）
            var presum = conn.ExecuteScalar<decimal>(
                "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WHERE cp = @cp AND nno = @nno AND depotno = @depotno AND dpm = @dpm",
                new
                {
                    cp = new DbString { Value = part.Cp, IsAnsi = true, Length = 10 },
                    nno = new DbString { Value = part.Nno, IsAnsi = true, Length = 18 },
                    depotno = new DbString { Value = "", IsAnsi = true, Length = 10 },
                    dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 },
                });

            var r = orderRepo.ConfirmAsync(new ConfirmOrderInCommand { Seano = seano, Mode = 3, Pno = "UATPNO1" },
                part.Cp, Dept, Per, "Z", "废").GetAwaiter().GetResult();
            Assert.True(r.StockInLines == 1 && r.DirectSaleLines == 0 && r.Papers.Count == 0,
                $"[A10] 自营行误入直发分支：入库 {r.StockInLines} 直发 {r.DirectSaleLines} 销售单 {r.Papers.Count}");

            // 台账：ISORD='Y'、KCAMOUNT=入库前快照、type='入库'
            var ed = conn.QueryFirst<(decimal jk, decimal kc, string isord, string type, string seanoCol, decimal freight)>(
                "SELECT jkamount, kcamount, RTRIM(isord), RTRIM(type), RTRIM(seano), ISNULL(Freight, 0) FROM tbistoed WHERE cno = @cno",
                new { cno = new DbString { Value = cno, IsAnsi = true, Length = 15 } });
            _out.WriteLine($"A10 tbistoed jk={ed.jk} kc={ed.kc}（入库前快照期望 {presum}） isord={ed.isord}");
            Assert.True(ed.jk == oqty && ed.kc == presum && ed.isord == "Y" && ed.type == "入库" && ed.seanoCol == seano,
                $"[A10] tbistoed 不符：jk={ed.jk} kc={ed.kc}（期望快照 {presum}） isord={ed.isord} type={ed.type}");

            // 库存 +Σ行量（ISAPPE），无销售单产生
            var stockAfter = conn.ExecuteScalar<decimal>(
                "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WHERE cp = @cp AND nno = @nno AND dpm = @dpm",
                new
                {
                    cp = new DbString { Value = part.Cp, IsAnsi = true, Length = 10 },
                    nno = new DbString { Value = part.Nno, IsAnsi = true, Length = 18 },
                    dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 },
                });
            Assert.True(stockAfter == part.Kc + oqty, $"[A10] 库存 {stockAfter} ≠ 期望 {part.Kc + oqty}（ISAPPE 入库）");
            var sales = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM tbsada WHERE per = @per AND type = N'销售'",
                new { per = new DbString { Value = Per, IsAnsi = true, Length = 10 } });
            Assert.True(sales == 0, "[A10] 自营行不应产生销售单");

            // tborder.qty = MIN(qty+行量, oqty) = 10；草稿清空
            var orderQty = conn.ExecuteScalar<decimal>("SELECT qty FROM tborder WHERE cno = @cno",
                new { cno = new DbString { Value = cno, IsAnsi = true, Length = 15 } });
            var draftLeft = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM tbisorsm WHERE seano = @seano",
                new { seano = new DbString { Value = seano, IsAnsi = true, Length = 15 } });
            _out.WriteLine($"A10 tborder.qty={orderQty} 草稿残留={draftLeft}");
            Assert.True(orderQty == oqty, $"[A10] tborder.qty={orderQty} ≠ {oqty}");
            Assert.True(draftLeft == 0, "[A10] tbisorsm 草稿未删除");
        }
    }

    /// <summary>A11：订单入库-代订直发行（gname 非空）：tbsada kcid=0 + 库存不动 + 无单头。</summary>
    [Fact]
    public void A11_OrderIn_DirectSale_Branch()
    {
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        var orderRepo = new OrderInRepository(Factory, new DocNoService(Factory), new DmService());
        const string seano = "UATA11SEANO", cno = "UATA11CNO1";
        decimal oqty = 5m;

        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            var part = GetUniqueBatch(conn);
            LegacyReplay.SeedOrderIn(conn, Dept, [(part, oqty, ReplayConfig.Gname, 1)], cno);

            orderRepo.ImportWholeOrderAsync(new ImportOrderInCommand { Seano = seano, Cno = cno },
                part.Cp, Dept, Per, "Z", "废", 1m, 1.3m, 1.2m, wbtohs: true).GetAwaiter().GetResult();

            // 库存基准（直发不应动任何批次）
            var stockBefore = conn.ExecuteScalar<decimal>(
                "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WHERE cp = @cp AND nno = @nno AND dpm = @dpm",
                new
                {
                    cp = new DbString { Value = part.Cp, IsAnsi = true, Length = 10 },
                    nno = new DbString { Value = part.Nno, IsAnsi = true, Length = 18 },
                    dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 },
                });
            // 直发行 depotno=Z → kcamount 快照按 Z 仓统计（row220 L107）
            var presumZ = conn.ExecuteScalar<decimal>(
                "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WHERE cp = @cp AND nno = @nno AND depotno = 'Z' AND dpm = @dpm",
                new
                {
                    cp = new DbString { Value = part.Cp, IsAnsi = true, Length = 10 },
                    nno = new DbString { Value = part.Nno, IsAnsi = true, Length = 18 },
                    dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 },
                });

            var r = orderRepo.ConfirmAsync(new ConfirmOrderInCommand { Seano = seano, Mode = 3 },
                part.Cp, Dept, Per, "Z", "废").GetAwaiter().GetResult();
            Assert.True(r.DirectSaleLines == 1 && r.StockInLines == 0 && r.Papers.Count == 1,
                $"[A11] 直发分支未走通：直发 {r.DirectSaleLines} 入库 {r.StockInLines} 销售单 {r.Papers.Count}");
            var paper = r.Papers[0];

            // tbsada：type='销售'、kcid=0（sale_in 不扣库存）、ckamount=入库数、kcamount=入库前 Z 仓快照
            var sada = conn.QueryFirst<(string type, int kcid, decimal ck, decimal kc, string gname, string paper)>(
                """
                SELECT RTRIM(type), ISNULL(kcid, 0), ckamount, ISNULL(kcamount, 0), RTRIM(gname), RTRIM(paper)
                FROM tbsada WHERE paper = @paper AND per = @per
                """,
                new { paper = new DbString { Value = paper, IsAnsi = true, Length = 15 }, per = new DbString { Value = Per, IsAnsi = true, Length = 10 } });
            _out.WriteLine($"A11 tbsada type={sada.type} kcid={sada.kcid} ck={sada.ck} kc={sada.kc} paper={sada.paper}");
            Assert.True(sada.type == "销售" && sada.kcid == 0 && sada.ck == oqty,
                $"[A11] tbsada 不符：type={sada.type} kcid={sada.kcid} ck={sada.ck}");
            Assert.True(sada.kc == presumZ, $"[A11] tbsada.kcamount={sada.kc} ≠ 入库前 Z 仓快照 {presumZ}");
            Assert.True(sada.gname == ReplayConfig.Gname && sada.paper == paper, "[A11] 客户/单号不符");

            // 库存不动（核心：sale_in 仅 kcid>0 扣减）
            var stockAfter = conn.ExecuteScalar<decimal>(
                "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WHERE cp = @cp AND nno = @nno AND dpm = @dpm",
                new
                {
                    cp = new DbString { Value = part.Cp, IsAnsi = true, Length = 10 },
                    nno = new DbString { Value = part.Nno, IsAnsi = true, Length = 18 },
                    dpm = new DbString { Value = Dept, IsAnsi = true, Length = 10 },
                });
            _out.WriteLine($"A11 库存 before={stockBefore} after={stockAfter}");
            Assert.True(stockAfter == stockBefore, $"[A11] 代订直发不应动库存：{stockBefore} → {stockAfter}");

            // 无 tbsada_header；tbistoed depotno=Z、isord='Y'；tborder.qty 累计
            var headerCnt = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM tbsada_header WHERE paper = @paper",
                new { paper = new DbString { Value = paper, IsAnsi = true, Length = 15 } });
            var ed = conn.QueryFirst<(string depotno, string isord)>(
                "SELECT RTRIM(depotno), RTRIM(isord) FROM tbistoed WHERE cno = @cno",
                new { cno = new DbString { Value = cno, IsAnsi = true, Length = 15 } });
            var orderQty = conn.ExecuteScalar<decimal>("SELECT qty FROM tborder WHERE cno = @cno",
                new { cno = new DbString { Value = cno, IsAnsi = true, Length = 15 } });
            _out.WriteLine($"A11 header={headerCnt} tbistoed depotno={ed.depotno} isord={ed.isord} tborder.qty={orderQty}");
            Assert.True(headerCnt == 0, "[A11] 直发不应产生 tbsada_header（旧契约无单头）");
            Assert.True(ed.depotno == "Z" && ed.isord == "Y", $"[A11] tbistoed depotno={ed.depotno} isord={ed.isord} ≠ Z/Y");
            Assert.True(orderQty == oqty, $"[A11] tborder.qty={orderQty} ≠ {oqty}");
        }
    }
}
