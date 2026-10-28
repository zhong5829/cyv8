using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Domain.Sales;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Finance;
using QPGL.Infrastructure.Partners;
using QPGL.Infrastructure.Orders;
using QPGL.Infrastructure.Parts;
using QPGL.Infrastructure.Printing;
using QPGL.Infrastructure.Query;
using QPGL.Infrastructure.Report;
using QPGL.Infrastructure.Sales;
using QPGL.Infrastructure.Stock;
using Dapper;
using Microsoft.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace QPGL.Tests.Replay;

[CollectionDefinition("ReplaySerial", DisableParallelization = true)]
public class ReplayCollection;

/// <summary>
/// §11.2 契约回放测试：同一副本库（rscar_replay，本地 SQLEXPRESS）、同一业务输入，
/// 分别由「旧系统回放脚本」（逆向 SQL 原样重放）与「新系统仓储」执行，
/// 全相关表（tbsada/tbsada_header/tbisto/tbysyf）逐字段 diff，完全一致方可放行。
/// 每场景前用数据库快照重置（秒级），互不污染。
/// diff 窗口（rscar 生产库副本 XS26 有 9.4 万行，禁用 paper 前缀过滤）：
///   tbsada         → per = REPLAY（回放专用操作员，写入恒定）
///   tbsada_header  → paper ∈ 本轮回放取号集合（act 返回）
///   tbisto         → 全表（2.9 万行，快照重置保证基准一致）
///   tbysyf         → gname = 回放专用客户R
/// 运行前置：rscar_replay 已还原（见 ReplayConfig）；快照由 EnsureSnapshot 首跑自动创建。
/// </summary>
[Collection("ReplaySerial")]
public class ContractReplayTests
{
    private static (SaleRepository sale, SaleReturnRepository ret) NewRepos()
    {
        var provider = new StaticCs(ReplayConfig.ConnectionString);
        var factory = new SqlConnectionFactory(provider);
        var docNo = new DocNoService(factory);
        return (new SaleRepository(factory, docNo), new SaleReturnRepository(factory, docNo));
    }

    private sealed class StaticCs(string cs) : IConnectionStringProvider
    {
        public string ConnectionString { get; } = cs;
    }

    /// <summary>执行一轮：确保快照 → 重置 → act（返回本轮取号 papers）→ 采集八表（SQL 端窗口过滤）。</summary>
    private static Dictionary<string, List<Dictionary<string, object>>> Run(Func<List<string>> act, string? cnoWindow = null,
        string? gnoWindow = null, string? gynoWindow = null)
        => RunEx(act, cnoWindow, gnoWindow, gynoWindow).Tables;

    /// <summary>Run 的扩展版：额外返回本轮取号 papers（场景 5/6 附加表窗口用）。</summary>
    private static (List<string> Papers, Dictionary<string, List<Dictionary<string, object>>> Tables) RunEx(
        Func<List<string>> act, string? cnoWindow = null, string? gnoWindow = null, string? gynoWindow = null)
    {
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        List<string> papers;
        using (var conn = ReplayEnv.Open())
        {
            try
            {
                papers = act();
            }
            catch (Exception ex)
            {
                throw new Exception("[act 轮执行失败] " + ex.Message, ex);
            }
        }
        using var conn2 = ReplayEnv.Open();
        conn2.Open();
        // papers 来自 dbo.getnewdh 输出（djjx+年份+流水，纯字母数字），内联拼接安全
        var papersList = papers.Count > 0 ? string.Join(",", papers.Select(p => $"'{p.Replace("'", "")}'")) : "''";
        var tables = new Dictionary<string, List<Dictionary<string, object>>>
        {
            ["tbsada"] = ReplayEnv.SnapshotTable(conn2, "tbsada", "id",
                "per = @winPer", new Dictionary<string, object> { ["winPer"] = ReplayConfig.Operator }),
            ["tbsada_header"] = ReplayEnv.SnapshotTable(conn2, "tbsada_header", "id",
                $"paper IN ({papersList})"),
            ["tbisto"] = ReplayEnv.SnapshotTable(conn2, "tbisto", "id"),
            ["tbysyf"] = ReplayEnv.SnapshotTable(conn2, "tbysyf", "id",
                "gname = @winGname", new Dictionary<string, object> { ["winGname"] = ReplayConfig.Gname }),
            ["tbistoed"] = ReplayEnv.SnapshotTable(conn2, "tbistoed", "id",
                cnoWindow is null ? "1=0" : "cno = @winCno", new Dictionary<string, object> { ["winCno"] = cnoWindow }),
            ["tbistosm"] = ReplayEnv.SnapshotTable(conn2, "tbistosm", "id",
                cnoWindow is null ? "1=0" : "cno = @winCno", new Dictionary<string, object> { ["winCno"] = cnoWindow }),
            ["tbgu"] = ReplayEnv.SnapshotTable(conn2, "tbgu", "id",
                gnoWindow is null ? "1=0" : "RTRIM(gno) = @winGno", new Dictionary<string, object> { ["winGno"] = gnoWindow }),
            ["tbgugys"] = ReplayEnv.SnapshotTable(conn2, "tbgugys", "id",
                gynoWindow is null ? "1=0" : "RTRIM(gyno) = @winGyno", new Dictionary<string, object> { ["winGyno"] = gynoWindow }),
        };
        return (papers, tables);
    }

    /// <summary>附加表快照（场景 5/6 的 tbdjgs/tbistopd/tbistoch/tbsadc 等，独立连接）。</summary>
    private static List<Dictionary<string, object>> SnapExtra(string table, string orderBy, string whereSql)
    {
        using var conn = ReplayEnv.Open();
        conn.Open();
        return ReplayEnv.SnapshotTable(conn, table, orderBy, whereSql);
    }

    /// <summary>附加表逐字段 diff 断言。</summary>
    private static void AssertExtraIdentical(string table, string orderBy, string legacyWhere, string modernWhere, string[] keyCols)
    {
        var diffs = ReplayEnv.Diff(SnapExtra(table, orderBy, legacyWhere), SnapExtra(table, orderBy, modernWhere), table, keyCols);
        Assert.True(diffs.Count == 0, ReplayEnv.RenderDiffs(diffs));
    }

    private static void AssertIdentical(
        Dictionary<string, List<Dictionary<string, object>>> legacy,
        Dictionary<string, List<Dictionary<string, object>>> modern)
    {
        var diffs = new List<string>();
        diffs.AddRange(ReplayEnv.Diff(legacy["tbsada"], modern["tbsada"], "tbsada", new[] { "id" }));
        diffs.AddRange(ReplayEnv.Diff(legacy["tbsada_header"], modern["tbsada_header"], "tbsada_header", new[] { "id" }));
        diffs.AddRange(ReplayEnv.Diff(legacy["tbisto"], modern["tbisto"], "tbisto", new[] { "id" }));
        diffs.AddRange(ReplayEnv.Diff(legacy["tbysyf"], modern["tbysyf"], "tbysyf", new[] { "id" }));
        diffs.AddRange(ReplayEnv.Diff(legacy["tbistoed"], modern["tbistoed"], "tbistoed", new[] { "id" }));
        diffs.AddRange(ReplayEnv.Diff(legacy["tbistosm"], modern["tbistosm"], "tbistosm", new[] { "id" }));
        diffs.AddRange(ReplayEnv.Diff(legacy["tbgu"], modern["tbgu"], "tbgu", new[] { "id" }));
        diffs.AddRange(ReplayEnv.Diff(legacy["tbgugys"], modern["tbgugys"], "tbgugys", new[] { "id" }));

        Assert.True(diffs.Count == 0, ReplayEnv.RenderDiffs(diffs));
    }

    [Fact]
    public void Scenario1_SaleSingleLine_LegacyVsModern()
    {
        var (sale, _) = NewRepos();
        var per = ReplayConfig.Operator;

        // 种子：kcamount DESC 首批（新系统选批策略 = 旧 row548 策略，单行场景两者一致）
        List<LegacyReplay.Batch> seed = [];
        decimal qty = 1;

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            var paper = LegacyReplay.Sale(conn, new List<(LegacyReplay.Batch, decimal)> { (seed[0], qty) }, per);
            return [paper];
        });

        var modern = Run(() =>
        {
            var paper = sale.CreateAsync(new CreateSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                Lines = [new SaleLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Qty = qty }],
            }, per).GetAwaiter().GetResult().Paper;
            return new List<string> { paper };
        });

        AssertIdentical(legacy, modern);
    }

    [Fact]
    public void Scenario2_SaleSplitBatches_LegacyVsModern()
    {
        var (sale, _) = NewRepos();
        var per = ReplayConfig.Operator;

        // 种子：DESC 前两批；需求 = 批1库存 + 1（必然拆 2 行，且不触发负库存）
        List<LegacyReplay.Batch> seed = [];
        decimal qty = 0;

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 2);
            qty = seed[0].Kc + Math.Min(1, seed[1].Kc);
            // 旧算法（row548 DO WHILE）：批1 全出，剩余批2 承接
            var lines = new List<(LegacyReplay.Batch, decimal)>
            {
                (seed[0], seed[0].Kc),
                (seed[1], qty - seed[0].Kc),
            };
            return new List<string> { LegacyReplay.Sale(conn, lines, per) };
        });

        var modern = Run(() =>
        {
            var paper = sale.CreateAsync(new CreateSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                Lines = [new SaleLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Qty = qty }],
            }, per).GetAwaiter().GetResult().Paper;
            return new List<string> { paper };
        });

        AssertIdentical(legacy, modern);
    }

    [Fact]
    public void Scenario3_SaleReturn_LegacyVsModern()
    {
        var (sale, ret) = NewRepos();
        var per = ReplayConfig.Operator;

        List<LegacyReplay.Batch> seed = [];

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            var paper = LegacyReplay.Sale(conn, new List<(LegacyReplay.Batch, decimal)> { (seed[0], 1m) }, per);
            var retPaper = LegacyReplay.ReturnOneLine(conn, paper, 1m, per);
            return new List<string> { paper, retPaper };
        });

        var modern = Run(() =>
        {
            var paper = sale.CreateAsync(new CreateSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                Lines = [new SaleLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Qty = 1m }],
            }, per).GetAwaiter().GetResult().Paper;

            var source = ret.GetSourceAsync(paper, CancellationToken.None).GetAwaiter().GetResult();
            var retPaper = ret.CreateAsync(new CreateSaleReturnCommand
            {
                Dept = ReplayConfig.Dept,
                SourcePaper = paper,
                Lines = [new SaleReturnLineInput { RowId = source.Lines[0].RowId, Qty = 1m }],
            }, per).GetAwaiter().GetResult().Paper;
            return new List<string> { paper, retPaper };
        });

        AssertIdentical(legacy, modern);
    }

    [Fact]
    public void Scenario4_VoidSale_LegacyVsModern()
    {
        var (sale, _) = NewRepos();
        var per = ReplayConfig.Operator;

        List<LegacyReplay.Batch> seed = [];

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            var paper = LegacyReplay.Sale(conn, new List<(LegacyReplay.Batch, decimal)> { (seed[0], 1m) }, per);
            LegacyReplay.VoidSale(conn, paper, ReplayConfig.Dept);
            return new List<string> { paper };
        });

        var modern = Run(() =>
        {
            var paper = sale.CreateAsync(new CreateSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                Lines = [new SaleLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Qty = 1m }],
            }, per).GetAwaiter().GetResult().Paper;

            sale.VoidAsync(paper, ReplayConfig.Dept, per, CancellationToken.None).GetAwaiter().GetResult();
            return new List<string> { paper };
        });

        AssertIdentical(legacy, modern);
    }

    [Fact]
    public void Scenario7_InStock_LegacyVsModern()
    {
        var per = ReplayConfig.Operator;
        const string cno = "REPLAY-RK-01";

        List<LegacyReplay.Batch> seed = [];

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            // 行1：命中既有批次（同 cp/nno/iprc/posi/depotno）；行2：新批次（专属货位）→ 覆盖 ISAPPE 两分支
            List<LegacyReplay.InStockLine> lines =
            [
                new(seed[0].Cp, seed[0].Nno, seed[0].Na1 + "R", seed[0].Ty, seed[0].Fa, seed[0].Dc, seed[0].Unit,
                    5m, seed[0].Iprj, seed[0].Iprc, seed[0].Oprc, 0m, seed[0].Posi, seed[0].Depotno, "回放命中行"),
                new(seed[0].Cp, seed[0].Nno, seed[0].Na1 + "R", seed[0].Ty, seed[0].Fa, seed[0].Dc, seed[0].Unit,
                    3m, seed[0].Iprj + 1, seed[0].Iprc + 1, seed[0].Oprc + 1, 0m, "RP9", seed[0].Depotno, "回放新批行"),
            ];
            LegacyReplay.InStock(conn, ReplayConfig.Dept, cno, lines, per);
            return [];
        }, cnoWindow: cno);

        var provider = new StaticCs(ReplayConfig.ConnectionString);
        var inStock = new InStockRepository(new SqlConnectionFactory(provider), new DmService());
        var modern = Run(() =>
        {
            inStock.CreateAsync(new CreateInStockCommand
            {
                Cno = cno,
                Fkfs = "现金",
                Lines =
                [
                    new InStockLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Na1 = seed[0].Na1 + "R", Ty = seed[0].Ty, Fa = seed[0].Fa, Dc = seed[0].Dc, Unit = seed[0].Unit, Jkamount = 5m, Iprj = seed[0].Iprj, Iprc = seed[0].Iprc, Oprc = seed[0].Oprc, Oprb = 0m, Posi = seed[0].Posi, Depotno = seed[0].Depotno, Note = "回放命中行" },
                    new InStockLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Na1 = seed[0].Na1 + "R", Ty = seed[0].Ty, Fa = seed[0].Fa, Dc = seed[0].Dc, Unit = seed[0].Unit, Jkamount = 3m, Iprj = seed[0].Iprj + 1, Iprc = seed[0].Iprc + 1, Oprc = seed[0].Oprc + 1, Oprb = 0m, Posi = "RP9", Depotno = seed[0].Depotno, Note = "回放新批行" },
                ],
            }, ReplayConfig.Dept, per, allowReentry: false, CancellationToken.None).GetAwaiter().GetResult();
            return [];
        }, cnoWindow: cno);

        AssertIdentical(legacy, modern);
    }

    /// <summary>
    /// 场景 8：客户新增+修改（fr_gu row5 契约）。legacy = 同列集 INSERT/UPDATE tbgu 原样重放（PASSTH 补 dpm）。
    /// </summary>
    [Fact]
    public void Scenario8_PartnerSave_LegacyVsModern()
    {
        const string gno = "REPLAYKH01";
        var dept = ReplayConfig.Dept;

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            // 新增（fr_gu row5 L78，M_FLDS 一期列集；Dapper 直接 string→nvarchar，char 列隐式转换）
            conn.Execute(
                "INSERT INTO tbgu (gno, gname, name1, linkman, tel, mobile, adr, note, type1, type2, yhl, ywy, dpm) " +
                "VALUES (@gno, @gname, @name1, @linkman, @tel, @mobile, @adr, @note, '', @type2, @yhl, @ywy, @dpm)",
                new
                {
                    gno, gname = "回放专用客户R", name1 = "回放专用客户R", linkman = "回放联系", // linkman char(8) GBK 语义≤4 汉字
                    tel = "0571-88888888", mobile = "13800000000", adr = "回放地址", note = "契约回放",
                    type2 = "批发", yhl = 5m, ywy = "REPLAY", dpm = dept,
                });
            var id = conn.ExecuteScalar<int>("SELECT id FROM tbgu WHERE RTRIM(gno) = @gno",
                new { gno = new DbString { Value = gno, IsAnsi = true, Length = 10 } });
            // 修改（fr_gu row5 L84）
            conn.Execute(
                "UPDATE tbgu SET gname = @gname, tel = @tel, yhl = @yhl WHERE id = @id",
                new
                {
                    id,
                    gname = new DbString { Value = "回放专用客户R2", IsAnsi = false, Length = 60 },
                    tel = new DbString { Value = "0571-66666666", IsAnsi = true, Length = 50 },
                    yhl = 8m,
                });
            return [];
        }, gnoWindow: gno);

        var partnerRepo = new PartnerManageRepository(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)));
        var modern = Run(() =>
        {
            var saved = partnerRepo.SaveAsync(new SavePartnerCommand
            {
                Gno = gno,
                Gname = "回放专用客户R",
                Name1 = "回放专用客户R",
                Linkman = "回放联系",
                Tel = "0571-88888888",
                Mobile = "13800000000",
                Adr = "回放地址",
                Note = "契约回放",
                Type2 = "批发",
                Yhl = 5m,
                Ywy = "REPLAY",
            }, dept, CancellationToken.None).GetAwaiter().GetResult();

            partnerRepo.SaveAsync(new SavePartnerCommand
            {
                Id = saved.Id,
                Gno = gno,
                Gname = "回放专用客户R2",
                Name1 = "回放专用客户R",
                Linkman = "回放联系",
                Tel = "0571-66666666",
                Mobile = "13800000000",
                Adr = "回放地址",
                Note = "契约回放",
                Type2 = "批发",
                Yhl = 8m,
                Ywy = "REPLAY",
            }, dept, CancellationToken.None).GetAwaiter().GetResult();
            return [];
        }, gnoWindow: gno);

        AssertIdentical(legacy, modern);
    }

    /// <summary>场景 9：供应商新增+修改（tbgugys，结构契约 + fr_gu 校验模式）。</summary>
    [Fact]
    public void Scenario9_SupplierSave_LegacyVsModern()
    {
        const string gyno = "REPLAYGY01";

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            conn.Execute(
                "INSERT INTO tbgugys (gyno, gyname, linkman, tel, fax, adr, zq, email, note, bank, checkno, sl, lb) " +
                "VALUES (@gyno, @gyname, @linkman, @tel, @fax, @adr, @zq, @email, @note, @bank, @checkno, @sl, @lb)",
                new
                {
                    gyno = new DbString { Value = gyno, IsAnsi = true, Length = 10 },
                    gyname = new DbString { Value = "回放供应商R", IsAnsi = false, Length = 40 },
                    linkman = new DbString { Value = "回放联系", IsAnsi = false, Length = 8 },
                    tel = new DbString { Value = "0571-11111111", IsAnsi = true, Length = 30 },
                    fax = new DbString { Value = "", IsAnsi = true, Length = 14 },
                    adr = new DbString { Value = "回放地址", IsAnsi = false, Length = 40 },
                    zq = 30m,
                    email = new DbString { Value = "", IsAnsi = true, Length = 40 },
                    note = new DbString { Value = "契约回放", IsAnsi = false, Length = 40 },
                    bank = new DbString { Value = "", IsAnsi = true, Length = 40 },
                    checkno = new DbString { Value = "", IsAnsi = true, Length = 20 },
                    sl = 0.13m, lb = new DbString { Value = "", IsAnsi = true, Length = 4 },
                });
            var id = conn.ExecuteScalar<int>("SELECT id FROM tbgugys WHERE RTRIM(gyno) = @gyno",
                new { gyno = new DbString { Value = gyno, IsAnsi = true, Length = 10 } });
            conn.Execute("UPDATE tbgugys SET gyname = @gyname, zq = @zq WHERE id = @id",
                new
                {
                    id,
                    gyname = new DbString { Value = "回放供应商R2", IsAnsi = false, Length = 40 },
                    zq = 45m,
                });
            return [];
        }, gynoWindow: gyno);

        var supplierRepo = new SupplierRepository(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)));
        var modern = Run(() =>
        {
            var saved = supplierRepo.SaveAsync(new SaveSupplierCommand
            {
                Gyno = gyno,
                Gyname = "回放供应商R",
                Linkman = "回放联系",
                Tel = "0571-11111111",
                Adr = "回放地址",
                Zq = 30m,
                Note = "契约回放",
                Sl = 0.13m,
            }, CancellationToken.None).GetAwaiter().GetResult();

            supplierRepo.SaveAsync(new SaveSupplierCommand
            {
                Id = saved.Id,
                Gyno = gyno,
                Gyname = "回放供应商R2",
                Linkman = "回放联系",
                Tel = "0571-11111111",
                Adr = "回放地址",
                Zq = 45m,
                Note = "契约回放",
                Sl = 0.13m,
            }, CancellationToken.None).GetAwaiter().GetResult();
            return [];
        }, gynoWindow: gyno);

        AssertIdentical(legacy, modern);
    }

    /// <summary>盘点实盘方案（两侧共用）：行0 盈+1、行1 亏-1、末行调仓位 RP7（零差）、其余零差。</summary>
    private static List<(decimal NpdDelta, string? Pdposi)> BuildCountPlan(int n)
    {
        var plan = new List<(decimal, string?)>();
        for (var i = 0; i < n; i++)
        {
            var delta = i == 0 ? 1m : i == 1 ? -1m : 0m;
            var pd = n >= 3 && i == n - 1 ? "RP7" : null;
            plan.Add((delta, pd));
        }
        return plan;
    }

    /// <summary>
    /// 场景 5：库存盘点全流程（§11.2 场景 5）。旧=fr_istopd row114/row95/鐶.row47/row49/row92 原样重放；
    /// 新=StocktakeRepository 同输入执行。diff：八表（Run）+ tbdjgs/tbistopd/tbistoch（附加窗口）。
    /// tbistosm/tbistoed 由 Run 的 cno 窗口不适用（pdpaper 动态取号），tbistosm 亦走附加窗口。
    /// </summary>
    [Fact]
    public void Scenario5_Stocktake_LegacyVsModern()
    {
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        List<LegacyReplay.Batch> seed = [];

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 2);
            var paper = LegacyReplay.Stocktake(conn, dept, seed[0].Cp, per, seed[0].Nno,
                BuildCountPlan(conn.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM tbisto WHERE cp = @cp AND RTRIM(nno) LIKE @nno + '%' AND dpm = @dpm",
                    new { cp = seed[0].Cp, nno = seed[0].Nno, dpm = dept })));
            return [paper];
        });

        var stRepo = new StocktakeRepository(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)), new DocNoService(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString))));
        var modern = RunEx(() =>
        {
            var paper = stRepo.GeneratePaperAsync(new GenerateStocktakeCommand { NnoPrefix = seed[0].Nno, Cp = seed[0].Cp },
                dept, per, CancellationToken.None).GetAwaiter().GetResult();
            var lines = stRepo.GetLinesAsync(paper, CancellationToken.None).GetAwaiter().GetResult();
            var plan = BuildCountPlan(lines.Count);
            for (var i = 0; i < lines.Count; i++)
            {
                stRepo.SaveCountLineAsync(new SaveStocktakeLineCommand
                {
                    Id = lines[i].Id,
                    Npd = lines[i].Kcamount + plan[i].NpdDelta,
                    Pdposi = plan[i].Pdposi ?? lines[i].Pdposi,
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
            stRepo.AdjustPosiAsync(paper, per, CancellationToken.None).GetAwaiter().GetResult();
            stRepo.ConfirmAsync(paper, CancellationToken.None).GetAwaiter().GetResult();
            stRepo.GenerateInStockDraftAsync(paper, per, CancellationToken.None).GetAwaiter().GetResult();
            return new List<string> { paper };
        });

        AssertIdentical(legacy.Tables, modern.Tables);
        // 附加窗口：papers 来自 getnewdh（纯字母数字，内联安全）
        var lw = PapersIn(legacy.Papers);
        var mw = PapersIn(modern.Papers);
        AssertExtraIdentical("tbistopd", "id", $"pdpaper IN ({lw})", $"pdpaper IN ({mw})", ["id"]);
        AssertExtraIdentical("tbistosm", "id", $"cno IN ({lw})", $"cno IN ({mw})", ["id"]);
        AssertExtraIdentical("tbdjgs", "id", "djname = N'盘点单'", "djname = N'盘点单'", ["id"]);
        AssertExtraIdentical("tbistoch", "id", "RTRIM(per) = 'REPLAY'", "RTRIM(per) = 'REPLAY'", ["id"]);
    }

    private static string PapersIn(List<string> papers) =>
        papers.Count > 0 ? string.Join(",", papers.Select(p => $"'{p.Replace("'", "")}'")) : "''";

    /// <summary>
    /// 场景 6：调拨 + 调仓（§11.2 场景 6）。旧=fr_sale Lf_.row511（type='调拨'）+ fr_sadc
    /// a_.row150/__.row11（DJLSH 取号 + ISAPPE P_WAY=1）；新=SaleRepository(DocType='调拨') +
    /// StockAdjustRepository。调仓两行覆盖 ISAPPE 目标未命中 INSERT 与命中 UPDATE 双分支。
    /// </summary>
    [Fact]
    public void Scenario6_Transfer_LegacyVsModern()
    {
        var (sale, _) = NewRepos();
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        List<LegacyReplay.Batch> seed = [];
        var adjRepo = new StockAdjustRepository(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)));

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            var qty = Math.Max(1m, Math.Floor(seed[0].Kc / 4));
            // 目标仓库：沿用源仓库（源为空时用回放专用仓），两侧同源保证契约一致
            var depotno1 = seed[0].Depotno.Length > 0 ? seed[0].Depotno : "RPK";
            var tp = LegacyReplay.Transfer(conn, seed[0], qty, per, "回放调拨");
            var ap1 = LegacyReplay.StockAdjust(conn, dept, seed[0], qty, depotno1, "RP9", per);
            var ap2 = LegacyReplay.StockAdjust(conn, dept, seed[0], qty, depotno1, "RP9", per);
            return new List<string> { tp, ap1, ap2 };
        });

        var modern = RunEx(() =>
        {
            var qty = Math.Max(1m, Math.Floor(seed[0].Kc / 4));
            var depotno1 = seed[0].Depotno.Length > 0 ? seed[0].Depotno : "RPK";
            var tp = sale.CreateAsync(new CreateSaleCommand
            {
                Dept = dept,
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                DocType = "调拨",
                Lines = [new SaleLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Qty = qty, Kcid = seed[0].Id, Remark = "回放调拨" }],
            }, per).GetAwaiter().GetResult().Paper;

            var ap1 = adjRepo.NextPaperAsync(dept, CancellationToken.None).GetAwaiter().GetResult();
            adjRepo.AddLineAsync(ap1, new AddStockAdjustLineCommand
            {
                Kcid = seed[0].Id, Ckamount = qty,
                Depotno1 = depotno1, Posi1 = "RP9", Oprc = seed[0].Oprc,
            }, dept, per, CancellationToken.None).GetAwaiter().GetResult();
            adjRepo.ConfirmAsync(ap1, dept, CancellationToken.None).GetAwaiter().GetResult();

            var ap2 = adjRepo.NextPaperAsync(dept, CancellationToken.None).GetAwaiter().GetResult();
            adjRepo.AddLineAsync(ap2, new AddStockAdjustLineCommand
            {
                Kcid = seed[0].Id, Ckamount = qty,
                Depotno1 = depotno1, Posi1 = "RP9", Oprc = seed[0].Oprc,
            }, dept, per, CancellationToken.None).GetAwaiter().GetResult();
            adjRepo.ConfirmAsync(ap2, dept, CancellationToken.None).GetAwaiter().GetResult();

            return new List<string> { tp, ap1, ap2 };
        });

        AssertIdentical(legacy.Tables, modern.Tables);
        var lw = PapersIn(legacy.Papers);
        var mw = PapersIn(modern.Papers);
        AssertExtraIdentical("tbsadc", "id", $"paper IN ({lw})", $"paper IN ({mw})", ["id"]);
        AssertExtraIdentical("tbdjgs", "id", "djname IN (N'调拨单', N'调仓单')", "djname IN (N'调拨单', N'调仓单')", ["id"]);
    }

    /// <summary>
    /// 场景 10：订单入库（B2，二期）。旧=fr_isor row156 整单导入 + row220 确认（tbistoed ISORD='Y' +
    /// tbprnoty 建档 + gname 直发 tbsada(kcid=0) / ISAPPE + tborder.qty 累计 + 删 tbisorsm）；
    /// 新=OrderInRepository。两行覆盖 ISAPPE 入库与代订直发双分支（运行时 P_WAY=tbsysset 均价）。
    /// </summary>
    [Fact]
    public void Scenario10_OrderInStock_LegacyVsModern()
    {
        var orderRepo = new OrderInRepository(
            new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)),
            new DocNoService(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString))),
            new DmService());
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        const string seano = "REPLAYSEANO1", cno = "REPLAYCNO1";
        List<LegacyReplay.Batch> parts = [];

        void Seed(SqlConnection conn)
        {
            parts = LegacyReplay.GetSeedParts(conn, 2);
            LegacyReplay.SeedOrderIn(conn, dept,
                [(parts[0], 10m, "", 1), (parts[1], 5m, ReplayConfig.Gname, 2)], cno);
        }

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            Seed(conn);
            LegacyReplay.OrderInImport(conn, dept, parts[0].Cp, per, seano, cno, 1m, 1.3m, 1.2m, wbtohs: true, pFpcw: "废");
            LegacyReplay.OrderInConfirm(conn, dept, parts[0].Cp, per, seano, "Z", out var papers);
            return papers;
        }, cnoWindow: cno);

        var modern = RunEx(() =>
        {
            using (var conn = ReplayEnv.Open())
                Seed(conn);
            orderRepo.ImportWholeOrderAsync(new ImportOrderInCommand { Seano = seano, Cno = cno },
                parts[0].Cp, dept, per, "Z", "废", 1m, 1.3m, 1.2m, wbtohs: true).GetAwaiter().GetResult();
            var r = orderRepo.ConfirmAsync(new ConfirmOrderInCommand { Seano = seano, Mode = 3 },
                parts[0].Cp, dept, per, "Z", "废").GetAwaiter().GetResult();
            return r.Papers;
        }, cnoWindow: cno);

        AssertIdentical(legacy.Tables, modern.Tables);
        AssertExtraIdentical("tborder", "id", $"cno = '{cno}'", $"cno = '{cno}'", ["id"]);
        AssertExtraIdentical("tbisorsm", "id", $"seano IN ('{seano}', '{seano}_1')",
            $"seano IN ('{seano}', '{seano}_1')", ["id"]);
    }

    /// <summary>
    /// 场景 11：日营业统计确认（D1，二期）。旧=fr_rtj row5（销售组聚合 → tbysyf 应收（挂帐/汇款/托收）+
    /// KH 实收差额行 → tbsada type='历史' + jzdate 回写；入库组 → tbysyf 应付 → tbistoed type='历史'）；
    /// 新=StatisticsRepository.PostDailyStatAsync。种子含挂帐销售组（PT≠0 + 实收差额）、现金销售组、挂帐入库组。
    /// </summary>
    [Fact]
    public void Scenario11_DailyStatPost_LegacyVsModern()
    {
        var statRepo = new StatisticsRepository(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)));
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var date = new DateTime(2026, 1, 10);
        const string cno = "REPLAYCNO11";

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            LegacyReplay.SeedDailyStat(conn, dept, date, per, cno);
            LegacyReplay.DailyStatPost(conn, dept, per, date);
            return new List<string>();
        }, cnoWindow: cno);

        var modern = Run(() =>
        {
            using (var conn = ReplayEnv.Open())
                LegacyReplay.SeedDailyStat(conn, dept, date, per, cno);
            statRepo.PostDailyStatAsync(date, dept, per).GetAwaiter().GetResult();
            return new List<string>();
        }, cnoWindow: cno);

        AssertIdentical(legacy, modern);
    }

    /// <summary>
    /// 场景 12：日营业统计 + 恢复未统计往返（D1→D2）。旧=fr_rtj row5 + fr_hftj 0_.row5；
    /// 新=PostDailyStatAsync + UnpostDailyStatAsync。断言往返后单据类型回滚、未收款应收行清除。
    /// </summary>
    [Fact]
    public void Scenario12_DailyStatRoundTrip_LegacyVsModern()
    {
        var statRepo = new StatisticsRepository(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)));
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var date = new DateTime(2026, 1, 11);
        const string cno = "REPLAYCNO12";

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            LegacyReplay.SeedDailyStat(conn, dept, date, per, cno);
            LegacyReplay.DailyStatPost(conn, dept, per, date);
            LegacyReplay.DailyStatUnpost(conn, dept, per, date);
            return new List<string>();
        }, cnoWindow: cno);

        var modern = Run(() =>
        {
            using (var conn = ReplayEnv.Open())
                LegacyReplay.SeedDailyStat(conn, dept, date, per, cno);
            statRepo.PostDailyStatAsync(date, dept, per).GetAwaiter().GetResult();
            statRepo.UnpostDailyStatAsync(date, dept, per).GetAwaiter().GetResult();
            return new List<string>();
        }, cnoWindow: cno);

        AssertIdentical(legacy, modern);
    }

    /// <summary>
    /// 场景 13：入库退货（B.A，三期）。旧=fr_istk 嗟.row132 序列（UPDATE ro → 定位仓位 → INSERT 负数流水
    /// ro=源行 id → ISAPPE 主程序库版冲减（M_NOUPOPRC=.T.）→ 回写 kcamount=冲减前快照/iprc）；
    /// 新=PurchaseReturnRepository.ConfirmAsync。tbistoed 窗口=回放取号 cno ∪ 源行 id。
    /// </summary>
    [Fact]
    public void Scenario13_PurchaseReturn_LegacyVsModern()
    {
        var repo = new PurchaseReturnRepository(
            new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)),
            new DocNoService(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString))));
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        int sourceId = 0;
        decimal amount = 0, iprj = 0, iprc = 0;
        string posi = "", depotno = "", fkfs = "";
        const string reason = "回放退货";

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            var src = LegacyReplay.GetReturnable(conn, dept);
            sourceId = src.id;
            amount = Math.Max(1m, Math.Floor(src.jkamount / 2));
            iprj = src.iprj;
            iprc = src.iprc;
            posi = src.posi;
            depotno = src.depotno;
            fkfs = conn.ExecuteScalar<string>("SELECT TOP 1 RTRIM(name) FROM tbsysset WHERE type = 'guno' ORDER BY id") ?? "现金";
            var cno = LegacyReplay.PurchaseReturn(conn, dept, per, sourceId, amount, iprj, iprc, posi, depotno, fkfs, reason);
            return new List<string> { cno };
        });

        var modern = RunEx(() =>
        {
            using (var conn = ReplayEnv.Open())
            {
                var src = LegacyReplay.GetReturnable(conn, dept);
                fkfs = conn.ExecuteScalar<string>("SELECT TOP 1 RTRIM(name) FROM tbsysset WHERE type = 'guno' ORDER BY id") ?? "现金";
                sourceId = src.id;
                amount = Math.Max(1m, Math.Floor(src.jkamount / 2));
                iprj = src.iprj;
                iprc = src.iprc;
                posi = src.posi;
                depotno = src.depotno;
            }
            var r = repo.ConfirmAsync(new ConfirmPurchaseReturnCommand
            {
                Fkfs = fkfs,
                ReturnReason = reason,
                Lines = [new PurchaseReturnLineInput
                {
                    SourceId = sourceId, Amount = amount, Iprj = iprj, Iprc = iprc, Posi = posi, Depotno = depotno,
                }],
            }, dept, per).GetAwaiter().GetResult();
            return new List<string> { r.Cno };
        });

        AssertIdentical(legacy.Tables, modern.Tables);
        var lw = $"cno IN ({PapersIn(legacy.Papers)}) OR id = {sourceId}";
        var mw = $"cno IN ({PapersIn(modern.Papers)}) OR id = {sourceId}";
        AssertExtraIdentical("tbistoed", "id", lw, mw, ["id"]);
        AssertExtraIdentical("tbdjgs", "id", "djname = N'散件入库单'", "djname = N'散件入库单'", ["id"]);
    }

    /// <summary>
    /// 场景 14：提货管理（B.B，三期）。旧=fr_th G__.row467 发货确认 B 序列（行级 fhdate/back1/remark →
    /// header spname upsert → tbfh upsert → tbwlfh insert/update → fhdate 同步 → 整单 mark='T' 判定）；
    /// 新=DeliveryRepository.ConfirmDeliveryBAsync。种子=mark='Y' 两行单（per=REPLAY 进 tbsada 固定窗口），
    /// 第一轮发货一行（未发完），第二轮发全（mark='T'）。fhdate 归一 Today（消费方仅用日期）。
    /// </summary>
    [Fact]
    public void Scenario14_Delivery_LegacyVsModern()
    {
        var repo = new DeliveryRepository(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)));
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        const string paper = "REPLAYDLV1", fhPaper = "REPLAYFH1";
        List<LegacyReplay.Batch> parts = [];

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            parts = LegacyReplay.GetSeedParts(conn, 2);
            LegacyReplay.SeedDeliveryOrder(conn, dept, per, paper, parts);
            var ids = conn.Query<int>("SELECT id FROM tbsada WHERE paper = @paper ORDER BY id",
                new { paper }).ToList();
            // 第一轮：只发第一行（未发完；c_mx 勾选语义两侧同源）
            LegacyReplay.DeliveryConfirmB(conn, dept, per, paper, "回放发货员", "回放备注", "回放发货公司",
                "回放检货员", fhPaper, "回放代搬", "回放站", stockOnly: false, lineIds: [ids[0]]);
            return new List<string>();
        });

        var modern = RunEx(() =>
        {
            List<int> ids;
            using (var conn = ReplayEnv.Open())
            {
                parts = LegacyReplay.GetSeedParts(conn, 2);
                LegacyReplay.SeedDeliveryOrder(conn, dept, per, paper, parts);
                ids = conn.Query<int>("SELECT id FROM tbsada WITH (NOLOCK) WHERE paper = @paper ORDER BY id",
                    new { paper }).ToList();
            }
            repo.ConfirmDeliveryBAsync(new DeliveryConfirmBCommand
            {
                Paper = paper, Back1 = "回放发货员", Remark = "回放备注", Spname = "回放发货公司",
                Jhr = "回放检货员", FhPaper = fhPaper, PerDbr = "回放代搬", Muzhan = "回放站",
                StockOnly = false,
                LineIds = [ids[0]],
            }, dept).GetAwaiter().GetResult();
            return new List<string>();
        });

        AssertIdentical(legacy.Tables, modern.Tables);
        AssertExtraIdentical("tbsada_header", "id", $"paper = '{paper}'", $"paper = '{paper}'", ["id"]);
        AssertExtraIdentical("tbfh", "id", $"sada_paper = '{paper}'", $"sada_paper = '{paper}'", ["id"]);
        AssertExtraIdentical("tbwlfh", "id", $"paper = '{paper}'", $"paper = '{paper}'", ["id"]);
    }

    /// <summary>
    /// 场景 15：订单族（F 组，三期）。旧=fr_order3 W1 新增行（Qs.row99:L21）+ fr_order4 I2 新增计划行
    /// （___.row352:L39）+ U1 并入在途（燂.row162:L30）；新=Order3Repository.CreateAsync +
    /// Order4Repository.AddLineAsync + MergeToTransitAsync。tbxzb 条件（cp+dpmusem）两侧一致。
    /// </summary>
    [Fact]
    public void Scenario15_Orders_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var order3 = new Order3Repository(factory, new DmService());
        var order4 = new Order4Repository(factory);
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        const string contractCno = "REPLAYCNO15", planCno = "REPLAYPLAN15";
        LegacyReplay.Batch part = null!;
        var cp = "中驭";

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            part = LegacyReplay.GetSeedParts(conn, 1)[0];
            LegacyReplay.SeedOrderContract(conn, dept, cp, per, contractCno, part);
            LegacyReplay.Order3AddLine(conn, dept, cp, per, contractCno, contractCno + "O2", part, 5m);
            LegacyReplay.Order4AddLine(conn, dept, cp, per, planCno, part, 3m);
            LegacyReplay.Order4AddLine(conn, dept, cp, per, planCno, part, 2m);
            LegacyReplay.Order4MergeToTransit(conn, dept, cp, planCno);
            return new List<string>();
        });

        var modern = RunEx(() =>
        {
            using (var conn = ReplayEnv.Open())
            {
                part = LegacyReplay.GetSeedParts(conn, 1)[0];
                LegacyReplay.SeedOrderContract(conn, dept, cp, per, contractCno, part);
            }
            order3.CreateAsync(new OrderLineInput
            {
                Cno = contractCno, Orno = contractCno + "O2", Nno = part.Nno, Na1 = part.Na1,
                Oqty = 5m, Iprj = part.Iprj, Oprc = part.Oprc, Ty = part.Ty, Fa = part.Fa,
                Gyname = "回放供应商", Fkfs = "挂帐",
            }, dept, cp, per).GetAwaiter().GetResult();
            order4.AddLineAsync(new OrderLineAddInput
            {
                Cno = planCno, Nno = part.Nno, Na1 = part.Na1, Ty = part.Ty, Fa = part.Fa,
                Unit = part.Unit, Dc = part.Dc, Oqty = 3m, Iprj = part.Iprj,
                Bzlx = "", Fkfs = "挂帐", Gyname = "回放供应商", Gname = "",
            }, dept, cp, per, addQty: false).GetAwaiter().GetResult();
            order4.AddLineAsync(new OrderLineAddInput
            {
                Cno = planCno, Nno = part.Nno, Na1 = part.Na1, Ty = part.Ty, Fa = part.Fa,
                Unit = part.Unit, Dc = part.Dc, Oqty = 2m, Iprj = part.Iprj,
                Bzlx = "", Fkfs = "挂帐", Gyname = "回放供应商", Gname = "",
            }, dept, cp, per, addQty: true).GetAwaiter().GetResult();
            order4.MergeToTransitAsync(planCno, dept, cp).GetAwaiter().GetResult();
            return new List<string>();
        });

        AssertIdentical(legacy.Tables, modern.Tables);
        var tw = $"cno IN ('{contractCno}', '{planCno}') OR orno IN ('{contractCno}', '{planCno}')";
        AssertExtraIdentical("tborder", "id", tw, tw, ["id"]);
    }

    /// <summary>
    /// 场景 16：收款/付款全循环（G.2/G.3，出纳）。旧=fr_xlsk A_.row75 确认 + c_.row74 还原序列原样；
    /// 新=XlskRepository.SaveAsync/RestoreAsync。种子=两笔销售应收（600/400，挂帐，jzdate=1900）+
    /// 一笔入库应付（500）。流程：收款 950（600 结清联动 jzdate / 350 部分）→ 整单还原（jzdate 回 1900）→
    /// 付款 200 部分付（带发票号 → tbfpgl）。tbysyf/tblsz/tbfpgl/tbdjgs 逐字段 diff；
    /// ysyf_refresh 两侧同调用（lj 全行回写属契约）。
    /// </summary>
    [Fact]
    public void Scenario16_Xlsk_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new XlskRepository(factory, new DocNoService(factory));
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var gname = ReplayConfig.Gname;
        const string saleA = "REPLAYSA16", saleB = "REPLAYSB16", inCno = "REPLAYIN16";
        const string pno = "REPLAYFP16";
        List<string> docs = [];

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            var bankname = conn.ExecuteScalar<string>("SELECT TOP 1 RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'bankname' ORDER BY id") ?? "";
            var fkfs = conn.ExecuteScalar<string>("SELECT TOP 1 RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'guno' ORDER BY id") ?? "现金";
            LegacyReplay.SeedReceivableSale(conn, dept, per, saleA, saleA, 600m, "挂帐");
            LegacyReplay.SeedReceivableSale(conn, dept, per, saleB, saleB, 400m, "挂帐");
            LegacyReplay.SeedPayableInStock(conn, dept, per, inCno, 500m, fkfs);
            // —— 收款 950：600 结清 + 350 部分付 ——
            var rows = LegacyReplay.XlskLoadOutstanding(conn, gname, "收款");
            var yue = LegacyReplay.XlskCustomerYue(conn, gname);
            var lines = new List<(int id, string paper, string lineType, decimal bcje, decimal wsje)>();
            foreach (var r in rows)
            {
                var je = r.ys;
                var wsje = Math.Max(je - r.skmoney, 0);
                var bcje = r.cno == saleA ? wsje : 350m;   // A 全额、B 部分
                lines.Add((r.id, r.cno, r.type, bcje, wsje));
            }
            var skDoc = LegacyReplay.XlskSave(conn, "收款", gname, fkfs, bankname, DateTime.Today, DateTime.Today,
                "", 950m, 950m - yue, lines, dept, per);
            docs.Add(skDoc);
            // —— 整单还原 ——
            var rlines = LegacyReplay.XlskDeriveRestoreLines(conn, skDoc, "收款");
            LegacyReplay.XlskRestore(conn, "收款", gname, skDoc, rlines, dept, per);
            // —— 付款 200 部分付（带发票号）——
            var prow = LegacyReplay.XlskLoadOutstanding(conn, gname, "付款").Single(r => r.cno == inCno);
            var pyue = LegacyReplay.XlskCustomerYue(conn, gname);
            var fkDoc = LegacyReplay.XlskSave(conn, "付款", gname, fkfs, bankname, DateTime.Today, DateTime.Today,
                pno, 200m, 200m + pyue,
                [(prow.id, prow.cno, prow.type, 200m, Math.Max(prow.yf - prow.skmoney, 0))], dept, per);
            docs.Add(fkDoc);
            return docs;
        });

        var modern = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            var bankname = conn.ExecuteScalar<string>("SELECT TOP 1 RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'bankname' ORDER BY id") ?? "";
            var fkfs = conn.ExecuteScalar<string>("SELECT TOP 1 RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'guno' ORDER BY id") ?? "现金";
            LegacyReplay.SeedReceivableSale(conn, dept, per, saleA, saleA, 600m, "挂帐");
            LegacyReplay.SeedReceivableSale(conn, dept, per, saleB, saleB, 400m, "挂帐");
            LegacyReplay.SeedPayableInStock(conn, dept, per, inCno, 500m, fkfs);
            // —— 收款 950 ——
            var rows = LegacyReplay.XlskLoadOutstanding(conn, gname, "收款");
            var yue = LegacyReplay.XlskCustomerYue(conn, gname);
            var saved = repo.SaveAsync(new XlskSaveCommand
            {
                Type = "收款", Date = DateTime.Today, LszDate = DateTime.Today,
                Fkfs = fkfs, Pno = "", PaySum = 950m, Gname = gname, Xm = "", BankName = bankname,
                LjValue = 950m - yue,
                Lines = rows.Select(r => new XlskSaveLine
                {
                    Id = r.id, Paper = r.cno, LineType = r.type,
                    Bcje = r.cno == saleA ? Math.Max(r.ys - r.skmoney, 0) : 350m,
                    Wsje = Math.Max(r.ys - r.skmoney, 0),
                }).ToList(),
            }, dept, per).GetAwaiter().GetResult();
            docs.Add(saved.DocNo);
            // —— 整单还原（行值两侧同源推导）——
            var rlines = LegacyReplay.XlskDeriveRestoreLines(conn, saved.DocNo, "收款");
            repo.RestoreAsync(saved.DocNo, "收款", gname, rlines.Select(l => new XlskRestoreLine
            {
                Id = l.id, Paper = l.paper, LineType = l.lineType, Bcje = l.bcje, Wsje = l.wsje, Checked = l.check,
            }).ToList(), dept, per).GetAwaiter().GetResult();
            // —— 付款 200（发票号）——
            var prow = LegacyReplay.XlskLoadOutstanding(conn, gname, "付款").Single(r => r.cno == inCno);
            var pyue = LegacyReplay.XlskCustomerYue(conn, gname);
            var fkSaved = repo.SaveAsync(new XlskSaveCommand
            {
                Type = "付款", Date = DateTime.Today, LszDate = DateTime.Today,
                Fkfs = fkfs, Pno = pno, PaySum = 200m, Gname = gname, Xm = "", BankName = bankname,
                LjValue = 200m + pyue,
                Lines = [new XlskSaveLine { Id = prow.id, Paper = prow.cno, LineType = prow.type, Bcje = 200m, Wsje = Math.Max(prow.yf - prow.skmoney, 0) }],
            }, dept, per).GetAwaiter().GetResult();
            docs.Add(fkSaved.DocNo);
            return docs;
        });

        Assert.Equal(legacy.Papers, modern.Papers);
        AssertIdentical(legacy.Tables, modern.Tables);
        var lw = $"skno IN ({PapersIn(legacy.Papers)})";
        AssertExtraIdentical("tblsz", "id", lw, lw, ["id"]);
        AssertExtraIdentical("tbfpgl", "id", $"djno IN ({PapersIn(legacy.Papers)})",
            $"djno IN ({PapersIn(modern.Papers)})", ["id"]);
        AssertExtraIdentical("tbdjgs", "id", "djname IN (N'收款单', N'付款单')", "djname IN (N'收款单', N'付款单')", ["id"]);
    }

    /// <summary>
    /// 场景 17：应收应付明细（G.4）。旧=fr_ysyf 婅_.row140 客户空行 + ___.row119 明细增改删 +
    /// row4 MDELE 多条删除；新=YsyfRepository。覆盖：客户行、明细 ys/yf 行、修改（per 保留）、
    /// 发票号行 tbfpgl 联动增删、删除拦截语义由种子行满足（skmoney=0 可删）、锚点前归并汇总行。
    /// </summary>
    [Fact]
    public void Scenario17_Ysyf_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new YsyfRepository(factory);
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var gname = ReplayConfig.Gname;
        const string cnoA = "REPLAYYS17A", cnoB = "REPLAYYS17B", cnoC = "REPLAYYS17C";
        const string pno = "REPLAYFP17";

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            LegacyReplay.YsyfAddCustomer(conn, gname, 100m, 2, 5m, "REPLAYHT17", dept);
            LegacyReplay.YsyfAddDetail(conn, gname, DateTime.Today, cnoA, "回放调整A", "", 800m, 0m, "其他A", "现金", "", dept, per);
            var idB = LegacyReplay.YsyfAddDetail(conn, gname, DateTime.Today, cnoB, "回放调整B", pno, 0m, 300m, "其他B", "现金", "", dept, per);
            LegacyReplay.YsyfUpdateDetail(conn, conn.ExecuteScalar<int>(
                "SELECT id FROM tbysyf WITH (NOLOCK) WHERE cno = @cno", new { cno = Ansi(cnoA, 30) }),
                DateTime.Today, cnoA, "回放调整A改", "", 900m, 0m, "其他A", "现金", "");
            LegacyReplay.YsyfDeleteDetail(conn, idB, deleteInvoice: true);
            var idC = LegacyReplay.YsyfAddDetail(conn, gname, DateTime.Today, cnoC, "回放调整C", "", 250m, 0m, "其他C", "现金", "", dept, per);
            LegacyReplay.YsyfMultiDeleteBefore(conn, idC, gname, DateTime.Today, per);
            return new List<string>();
        });

        var modern = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            repo.AddCustomerAsync(new YsyfCustomerCommand(gname, 100m, 2, 5m, "REPLAYHT17"),
                withHtno: true, per, dept).GetAwaiter().GetResult();
            repo.AddDetailAsync(new YsyfDetailCommand(gname, DateTime.Today, cnoA, "回放调整A", "", 800m, 0m, "其他A", "现金", ""),
                per, dept).GetAwaiter().GetResult();
            var idB = repo.AddDetailAsync(new YsyfDetailCommand(gname, DateTime.Today, cnoB, "回放调整B", pno, 0m, 300m, "其他B", "现金", ""),
                per, dept).GetAwaiter().GetResult();
            var idA = conn.ExecuteScalar<int>("SELECT id FROM tbysyf WITH (NOLOCK) WHERE cno = @cno",
                new { cno = Ansi(cnoA, 30) });
            repo.UpdateDetailAsync(idA, new YsyfDetailCommand(gname, DateTime.Today, cnoA, "回放调整A改", "", 900m, 0m, "其他A", "现金", "")).GetAwaiter().GetResult();
            repo.DeleteDetailAsync(idB, gname, deleteInvoice: true, per).GetAwaiter().GetResult();
            var idC = repo.AddDetailAsync(new YsyfDetailCommand(gname, DateTime.Today, cnoC, "回放调整C", "", 250m, 0m, "其他C", "现金", ""),
                per, dept).GetAwaiter().GetResult();
            repo.MultiDeleteBeforeAsync(idC, gname, DateTime.Today, per).GetAwaiter().GetResult();
            return new List<string>();
        });

        AssertIdentical(legacy.Tables, modern.Tables);
        AssertExtraIdentical("tbfpgl", "id", $"djno IN ('{cnoA}', '{cnoB}', '{cnoC}')",
            $"djno IN ('{cnoA}', '{cnoB}', '{cnoC}')", ["id"]);
    }

    static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };

    /// <summary>
    /// 场景 18：收支流水帐（G.5）。旧=fr_lsz Uz.row69 初始累计数 + _r.row65 手工增改 + 7_.row32 删除 +
    /// 鐷.row36 删除之前全部；新=LszRepository。bankname=回放专用标记「回放银行」与生产数据隔离。
    /// </summary>
    [Fact]
    public void Scenario18_Lsz_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new LszRepository(factory);
        var dept = ReplayConfig.Dept;
        const string bank = "回放银行";

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            LegacyReplay.LszAddInitial(conn, bank, 1000m, dept);
            var id1 = LegacyReplay.LszAddManual(conn, DateTime.Today, "", "回放收入", 300m, 0m, "回放备注1", ReplayConfig.Gname, "", "", bank, dept);
            var id2 = LegacyReplay.LszAddManual(conn, DateTime.Today, "", "回放支出", 0m, 100m, "回放备注2", ReplayConfig.Gname, "", "", bank, dept);
            LegacyReplay.LszUpdateManual(conn, id1, DateTime.Today, "", "回放收入", 350m, 0m, "回放备注1改", ReplayConfig.Gname, "", "", bank, dept);
            LegacyReplay.LszDeleteRow(conn, id2);
            var lj = conn.ExecuteScalar<decimal>("SELECT ISNULL(lj,0) FROM tblsz WITH (NOLOCK) WHERE id = @id", new { id = id1 });
            LegacyReplay.LszDeleteBefore(conn, id1, bank, lj);
            return new List<string>();
        });

        var modern = RunEx(() =>
        {
            repo.AddInitialAsync(bank, 1000m, dept).GetAwaiter().GetResult();
            var id1 = repo.AddManualAsync(new LszManualCommand(DateTime.Today, "", "回放收入", 300m, 0m, "回放备注1", ReplayConfig.Gname, "", ""), bank, dept).GetAwaiter().GetResult();
            var id2 = repo.AddManualAsync(new LszManualCommand(DateTime.Today, "", "回放支出", 0m, 100m, "回放备注2", ReplayConfig.Gname, "", ""), bank, dept).GetAwaiter().GetResult();
            repo.UpdateManualAsync(id1, new LszManualCommand(DateTime.Today, "", "回放收入", 350m, 0m, "回放备注1改", ReplayConfig.Gname, "", ""), bank, dept).GetAwaiter().GetResult();
            repo.DeleteAsync(id2).GetAwaiter().GetResult();
            var lj = repo.GetRowAsync(id1).GetAwaiter().GetResult()!.Lj;
            repo.DeleteBeforeAsync(id1, bank, lj).GetAwaiter().GetResult();
            return new List<string>();
        });

        AssertIdentical(legacy.Tables, modern.Tables);
        AssertExtraIdentical("tblsz", "id", "bankname = N'回放银行'", "bankname = N'回放银行'", ["id"]);
    }

    /// <summary>
    /// 场景 19：凭证录入→审核→过帐→反过帐全循环（I 组凭证核心闭环）。
    /// 旧=fr_finvouch Save + fr_finaudit + fr_finpost/unpost 序列原样（LegacyReplayFin）；新=FinVouchRepository。
    /// 种子科目 999001（期初借 600/贷 400）/999002；凭证1 平衡结转、凭证2 含数量金额（amount/unit）+ 部门/项目
    /// 核算项目。断言 finvouch(dcdir/balance/post/poster)、finsub 余额列、finaleger 3 行、finamsum、
    /// finitsub/finsubit、finvid 推进；反过帐后断言回退。
    /// </summary>
    [Fact]
    public void Scenario19_FinVouch_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new FinVouchRepository(factory);
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var period = DateTime.Today.ToString("yyyyMM");
        List<string> vids = [];

        var Lines1 = new (string, string, decimal, decimal, decimal, string, string, string, string, string)[]
        {
            ("999001", "回放测试科目一", 600m, 0m, 0m, "", "", "", "", ""),
            ("999002", "回放测试科目二", 0m, 600m, 0m, "", "", "", "", ""),
        };
        var Lines2 = new (string, string, decimal, decimal, decimal, string, string, string, string, string)[]
        {
            ("999001", "回放测试科目一", 250m, 0m, 10m, "个", "回放部门D", "回放项目I", "", ""),
            ("999002", "回放测试科目二", 0m, 250m, 0m, "", "", "", "", ""),
        };

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            LegacyReplay.FinSeedSub(conn, "999001", "回放测试科目一", 600m, 400m, dept);
            LegacyReplay.FinSeedSub(conn, "999002", "回放测试科目二", 0m, 0m, dept);
            var vc1 = conn.ExecuteScalar<int>("SELECT ISNULL(MAX(vcode),0)+1 FROM finvouch WITH (NOLOCK) WHERE period = @p",
                new { p = Ansi(period, 6) });
            var v1 = conn.ExecuteScalar<int>("SELECT vid + 1 FROM finvid WHERE dpm = @dpm", new { dpm = dept });
            LegacyReplay.FinVouchSave(conn, v1, vc1, DateTime.Today, "记", true, Lines1, dept, per);
            var vc2 = conn.ExecuteScalar<int>("SELECT ISNULL(MAX(vcode),0)+1 FROM finvouch WITH (NOLOCK) WHERE period = @p",
                new { p = Ansi(period, 6) });
            var v2 = conn.ExecuteScalar<int>("SELECT vid + 1 FROM finvid WHERE dpm = @dpm", new { dpm = dept });
            LegacyReplay.FinVouchSave(conn, v2, vc2, DateTime.Today, "记", true, Lines2, dept, per);
            vids.Add(v1.ToString());
            vids.Add(v2.ToString());
            LegacyReplay.FinAudit(conn, period, per);
            LegacyReplay.FinPost(conn, period, per, dept);
            // 断点1：过帐后采 集
            var afterPost = SnapFinState();
            LegacyReplay.FinUnpost(conn, period, per, dept);
            return vids;
        });

        var modern = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            LegacyReplay.FinSeedSub(conn, "999001", "回放测试科目一", 600m, 400m, dept);
            LegacyReplay.FinSeedSub(conn, "999002", "回放测试科目二", 0m, 0m, dept);
            var vc1 = repo.PeekNextVcodeAsync(period).GetAwaiter().GetResult();
            var v1 = repo.PeekNextVidAsync(dept).GetAwaiter().GetResult();
            repo.SaveAsync(new FinVouchSaveCommand(v1, vc1, DateTime.Today, "记",
                Lines1.Select(l => new FinVouchLine
                {
                    Subcode = l.Item1, Subname = l.Item2, Debit = l.Item3, Credit = l.Item4,
                    Amount = l.Item5, Unit = l.Item6, Dept = l.Item7, Item = l.Item8, Empl = l.Item9, Client = l.Item10,
                }).ToList(), IsAdd: true), dept, per).GetAwaiter().GetResult();
            var vc2 = repo.PeekNextVcodeAsync(period).GetAwaiter().GetResult();
            var v2 = repo.PeekNextVidAsync(dept).GetAwaiter().GetResult();
            repo.SaveAsync(new FinVouchSaveCommand(v2, vc2, DateTime.Today, "记",
                Lines2.Select(l => new FinVouchLine
                {
                    Subcode = l.Item1, Subname = l.Item2, Debit = l.Item3, Credit = l.Item4,
                    Amount = l.Item5, Unit = l.Item6, Dept = l.Item7, Item = l.Item8, Empl = l.Item9, Client = l.Item10,
                }).ToList(), IsAdd: true), dept, per).GetAwaiter().GetResult();
            vids.Add(v1.ToString());
            vids.Add(v2.ToString());
            repo.AuditPeriodAsync(period, per, CancellationToken.None).GetAwaiter().GetResult();
            repo.PostAsync(period, per, dept, byDate: false, date: null, skipCheck: false, CancellationToken.None).GetAwaiter().GetResult();
            repo.UnpostAsync(period, per, dept, byDate: false, date: null, CancellationToken.None).GetAwaiter().GetResult();
            return vids;
        });

        Assert.Equal(legacy.Papers, modern.Papers);
        AssertIdentical(legacy.Tables, modern.Tables);
        var w = "subcode LIKE '999%'";
        AssertExtraIdentical("finsub", "subcode", w, w, ["subcode"]);
        AssertExtraIdentical("finaleger", "subcode, recoid", "subcode LIKE '999%'", "subcode LIKE '999%'", ["subcode", "recoid"]);
        AssertExtraIdentical("finamsum", "subcode", "subcode LIKE '999%'", "subcode LIKE '999%'", ["subcode"]);
        AssertExtraIdentical("finitsub", "id", "1=1", "1=1", ["id"]);
        AssertExtraIdentical("finsubit", "id", "1=1", "1=1", ["id"]);
        AssertExtraIdentical("finvid", "dpm", "1=1", "1=1", ["dpm"]);
    }

    /// <summary>场景 19 辅助：过帐中态采集（当前未启用中态断言，保留结构供下批扩展）。</summary>
    private static Dictionary<string, List<Dictionary<string, object>>> SnapFinState() => [];

    /// <summary>
    /// 场景 20：业务凭证 BBI/SRO 生成→审核→反审核（I.2，fr_finoperation longsky 变体 + fr_finopvouch Audit/Unaudit）。
    /// 种子：BBI 三单（RKR20 挂帐含运费差→ADDSUB 应付进位+运费池空行 / RKR21 现金→cashsub / RKR22 历史类型第二建号）、
    /// SRO 三单（RXR20 挂帐→预置应收科目 122050 / RXR21 汇款→balasub / RXR22 挂帐科目复用）；
    /// VOU_TYPE=3（finopvou.valuen）→ SRO 含成本对 511002/135002。生成=游标预览不落库（ADDSUB 除外），
    /// 审核 INSERT finvouch(audit=1)+opaudit 回写 tbistoed/tbsada.audit，反审核 RKR21 删 finvouch+回写 0。
    /// 122 系列子码已耗尽至 '`99'（池回退）→ 应收侧必须预置科目；应付侧 204624/204625 进位可用。
    /// diff：finvouch(opertype BBI/SRO)、tbistoed/tbsada（单号窗口）、finsub（回放科目名窗口）、finvid。
    /// </summary>
    [Fact]
    public void Scenario20_FinOpVouch_BbiSro_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new FinOpVouchRepository(factory);
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var today = DateTime.Today;
        const string cashSub = "101001", balaSub = "102001";
        const string client = "回放客户R";        // SRO 应收（预置 122050，RXR20/RXR22 复用——122 耗尽无法新建）
        const string supplier = "回放供应商R", supplier2 = "回放供应商R2";   // BBI 应付（进位 204624/204625）
        List<string> papers = [];

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            conn.Open();
            LegacyReplay.FinSeedSubTyped(conn, "122050", client, "A", dept);
            LegacyReplay.FinSeedBbiDoc(conn, dept, per, "RKR20", supplier, "挂帐", 2m, 30m, 0m);
            LegacyReplay.FinSeedBbiDoc(conn, dept, per, "RKR20", supplier, "挂帐", 1m, 10m, 2m);
            LegacyReplay.FinSeedBbiDoc(conn, dept, per, "RKR21", supplier, "现金", 3m, 5m, 0m);
            LegacyReplay.FinSeedBbiDoc(conn, dept, per, "RKR22", supplier2, "挂帐", 4m, 6m, 0m, type: "历史", mark: "");
            LegacyReplay.FinSeedSroDoc(conn, dept, per, "RXR20", client, "挂帐", 2m, 50m, 30m, 0m);
            LegacyReplay.FinSeedSroDoc(conn, dept, per, "RXR21", client, "汇款", 1m, 80m, 50m, 10m);
            LegacyReplay.FinSeedSroDoc(conn, dept, per, "RXR22", client, "挂帐", 3m, 20m, 12m, 0m);
            papers.AddRange(["RKR20", "RKR21", "RKR22", "RXR20", "RXR21", "RXR22"]);
            // 生成（游标预览；ADDSUB 建号落库）
            var bbi = LegacyReplay.FinOpGenerateBbiLegacy(conn, dept, today, today, "RKR20", "RKR22", 4, cashSub, balaSub);
            Assert.Equal(3, bbi.Count);
            var sro = LegacyReplay.FinOpGenerateSroLegacy(conn, dept, "SRO", today, today, "RXR20", "RXR22", 4, cashSub, balaSub);
            Assert.Equal(3, sro.Count);
            // 审核（按生成顺序）+ 反审核 RKR21
            var vids = new Dictionary<string, int>();
            foreach (var g in bbi) vids[g.Opercode] = LegacyReplay.FinOpAuditLegacy(conn, g, dept, per).vouchid;
            foreach (var g in sro) vids[g.Opercode] = LegacyReplay.FinOpAuditLegacy(conn, g, dept, per).vouchid;
            LegacyReplay.FinOpUnauditLegacy(conn, vids["RKR21"], "BBI", "RKR21", dept);
            return papers;
        }, gnoWindow: null);

        var modern = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            conn.Open();
            LegacyReplay.FinSeedSubTyped(conn, "122050", client, "A", dept);
            LegacyReplay.FinSeedBbiDoc(conn, dept, per, "RKR20", supplier, "挂帐", 2m, 30m, 0m);
            LegacyReplay.FinSeedBbiDoc(conn, dept, per, "RKR20", supplier, "挂帐", 1m, 10m, 2m);
            LegacyReplay.FinSeedBbiDoc(conn, dept, per, "RKR21", supplier, "现金", 3m, 5m, 0m);
            LegacyReplay.FinSeedBbiDoc(conn, dept, per, "RKR22", supplier2, "挂帐", 4m, 6m, 0m, type: "历史", mark: "");
            LegacyReplay.FinSeedSroDoc(conn, dept, per, "RXR20", client, "挂帐", 2m, 50m, 30m, 0m);
            LegacyReplay.FinSeedSroDoc(conn, dept, per, "RXR21", client, "汇款", 1m, 80m, 50m, 10m);
            LegacyReplay.FinSeedSroDoc(conn, dept, per, "RXR22", client, "挂帐", 3m, 20m, 12m, 0m);
            papers.AddRange(["RKR20", "RKR21", "RKR22", "RXR20", "RXR21", "RXR22"]);
            var bbi = repo.GenerateAsync(new FinOpGenerateQuery
            {
                Opty = "BBI", BeginDate = today, EndDate = today,
                BeginPaper = "RKR20", EndPaper = "RKR22", PayOption = 4, CashSub = cashSub, BalaSub = balaSub,
            }, dept).GetAwaiter().GetResult();
            Assert.Equal(3, bbi.Count);
            var sro = repo.GenerateAsync(new FinOpGenerateQuery
            {
                Opty = "SRO", BeginDate = today, EndDate = today,
                BeginPaper = "RXR20", EndPaper = "RXR22", PayOption = 4, CashSub = cashSub, BalaSub = balaSub,
            }, dept).GetAwaiter().GetResult();
            Assert.Equal(3, sro.Count);
            var vids = new Dictionary<string, int>();
            foreach (var g in bbi) vids[g.Opercode] = repo.AuditAsync(g, dept, per).GetAwaiter().GetResult().VouchId;
            foreach (var g in sro) vids[g.Opercode] = repo.AuditAsync(g, dept, per).GetAwaiter().GetResult().VouchId;
            repo.UnauditAsync(vids["RKR21"], "BBI", "RKR21", dept).GetAwaiter().GetResult();
            return papers;
        });

        Assert.Equal(legacy.Papers, modern.Papers);
        var fv = "ISNULL(rtrim(opertype),'') IN ('BBI','SRO')";
        AssertExtraIdentical("finvouch", "id", fv, fv, ["id"]);
        var tw = "rtrim(cno) IN ('RKR20','RKR21','RKR22')";
        AssertExtraIdentical("tbistoed", "id", tw, tw, ["id"]);
        var sw = "rtrim(paper) IN ('RXR20','RXR21','RXR22')";
        AssertExtraIdentical("tbsada", "id", sw, sw, ["id"]);
        var subw = "rtrim(subname) IN (N'回放客户R',N'回放供应商R',N'回放供应商R2')";
        AssertExtraIdentical("finsub", "subcode, id", subw, subw, ["subcode", "id"]);
        AssertExtraIdentical("finvid", "dpm", "1=1", "1=1", ["dpm"]);
    }

    /// <summary>
    /// 场景 21：G 组 finvouch 联动（fr_xlsk A_.row75 FINVOUCH 块 / c_.row74 联动删除）。
    /// 种子：收款客户「回放专用客户R」预置科目 122050（122 耗尽）、销售应收 100（挂帐）；
    /// 付款客户「回放供应商R」入库应付 60（ADDSUB 进位 204624）。流程：收款 100（finvouch 两行，
    /// 去向=人民币 101001）→ 整单还原（finvouch 随删）→ 付款 60（finvouch 两行留存）→ 整期审核
    /// → 再次还原被拦截（audit=1）。tblsz.bankname=COMBO1 去向科目名（联动启用时旧 COMBO1 切换为科目名）。
    /// diff：finvouch(payno 非空)、finsub（回放科目名窗口）、finvid（全表）、tblsz/tbysyf/tbsada。
    /// </summary>
    [Fact]
    public void Scenario21_XlskFinVouch_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new XlskRepository(factory, new DocNoService(factory));
        var finRepo = new FinVouchRepository(factory);
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var gname = ReplayConfig.Gname;
        const string payGname = "回放供应商R";
        const string saleA = "REPLAYSA21", inCno = "REPLAYIN21";
        const string skqxName = "人民币";   // C_SKQX 首去向（101001 人民币 C fcash=1）
        List<string> docs = [];

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            conn.Open();
            LegacyReplay.FinSeedSubTyped(conn, "122050", gname, "A", dept);
            LegacyReplay.SeedReceivableSale(conn, dept, per, saleA, saleA, 100m, "挂帐");
            // 付款源：入库应付（回放供应商R，进位建号 204624）
            conn.Execute(
                "INSERT INTO tbysyf (type, cno, gname, xm, datime, fkfs, ys, yf, skmoney, lj, per, dpm) " +
                "VALUES (N'入库', @cno, @gname, N'回放应付', @datime, N'挂帐', 0, @yf, 0, 0, @per, @dpm)",
                new { cno = Ansi(inCno, 30), gname = Ansi(payGname, 60), datime = DateTime.Today, yf = 60m, per = Ansi(per, 10), dpm = Ansi(dept, 10) });
            var period = DateTime.Today.ToString("yyyyMM");
            // —— 收款 100（finvouch 两行）→ 整单还原（finvouch 随删）——
            var rows = LegacyReplay.XlskLoadOutstanding(conn, gname, "收款");
            var yue = LegacyReplay.XlskCustomerYue(conn, gname);
            var lines = rows.Select(r => (r.id, r.cno, r.type, Math.Max(r.ys - r.skmoney, 0), Math.Max(r.ys - r.skmoney, 0))).ToList();
            var skDoc = LegacyReplay.XlskSave(conn, "收款", gname, "挂帐", skqxName, DateTime.Today, DateTime.Today,
                "", 100m, 100m - yue, lines, dept, per);
            docs.Add(skDoc);
            var skqx = conn.QuerySingle<(string subcode, string subname, string type, bool fcash)>(
                "SELECT TOP (1) rtrim(subcode), rtrim(subname), rtrim(ISNULL(type,'')), ISNULL(fcash,0) FROM finsub WHERE rtrim(subname) = @n AND detacode = 1 AND (type = 'C' OR type = 'B') ORDER BY id",
                new { n = Ansi(skqxName, 40) });
            LegacyReplay.XlskFinVouchWriteLegacy(conn, skDoc, "收款", gname, "挂帐", DateTime.Today, "", 100m, dept, per, skqx);
            Assert.False(LegacyReplay.XlskFinVouchRestoreBlockedLegacy(conn, skDoc));
            var rlines = LegacyReplay.XlskDeriveRestoreLines(conn, skDoc, "收款");
            LegacyReplay.XlskRestore(conn, "收款", gname, skDoc, rlines, dept, per);
            LegacyReplay.XlskFinVouchRestoreLegacy(conn, skDoc);
            // —— 付款 60（finvouch 两行留存）——
            var prow = LegacyReplay.XlskLoadOutstanding(conn, payGname, "付款").Single(r => r.cno == inCno);
            var pyue = LegacyReplay.XlskCustomerYue(conn, payGname);
            var fkDoc = LegacyReplay.XlskSave(conn, "付款", payGname, "挂帐", skqxName, DateTime.Today, DateTime.Today,
                "", 60m, 60m + pyue,
                [(prow.id, prow.cno, prow.type, 60m, Math.Max(prow.yf - prow.skmoney, 0))], dept, per);
            docs.Add(fkDoc);
            LegacyReplay.XlskFinVouchWriteLegacy(conn, fkDoc, "付款", payGname, "挂帐", DateTime.Today, "", 60m, dept, per, skqx);
            // —— 整期审核 → 还原被拦截 ——
            LegacyReplay.FinAudit(conn, period, per);
            Assert.True(LegacyReplay.XlskFinVouchRestoreBlockedLegacy(conn, fkDoc));
            return docs;
        });

        var modern = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            conn.Open();
            LegacyReplay.FinSeedSubTyped(conn, "122050", gname, "A", dept);
            LegacyReplay.SeedReceivableSale(conn, dept, per, saleA, saleA, 100m, "挂帐");
            conn.Execute(
                "INSERT INTO tbysyf (type, cno, gname, xm, datime, fkfs, ys, yf, skmoney, lj, per, dpm) " +
                "VALUES (N'入库', @cno, @gname, N'回放应付', @datime, N'挂帐', 0, @yf, 0, 0, @per, @dpm)",
                new { cno = Ansi(inCno, 30), gname = Ansi(payGname, 60), datime = DateTime.Today, yf = 60m, per = Ansi(per, 10), dpm = Ansi(dept, 10) });
            var period = DateTime.Today.ToString("yyyyMM");
            // —— 收款 100 ——
            var rows = LegacyReplay.XlskLoadOutstanding(conn, gname, "收款");
            var yue = LegacyReplay.XlskCustomerYue(conn, gname);
            var saved = repo.SaveAsync(new XlskSaveCommand
            {
                Type = "收款", Date = DateTime.Today, LszDate = DateTime.Today,
                Fkfs = "挂帐", Pno = "", PaySum = 100m, Gname = gname, Xm = "", BankName = skqxName,
                LjValue = 100m - yue, FinVouchEnabled = true, SkqxSubname = skqxName,
                Lines = rows.Select(r => new XlskSaveLine
                {
                    Id = r.id, Paper = r.cno, LineType = r.type,
                    Bcje = Math.Max(r.ys - r.skmoney, 0), Wsje = Math.Max(r.ys - r.skmoney, 0),
                }).ToList(),
            }, dept, per).GetAwaiter().GetResult();
            docs.Add(saved.DocNo);
            // —— 整单还原（finvouch 随删）——
            var rlines = LegacyReplay.XlskDeriveRestoreLines(conn, saved.DocNo, "收款");
            repo.RestoreAsync(saved.DocNo, "收款", gname, rlines.Select(l => new XlskRestoreLine
            {
                Id = l.id, Paper = l.paper, LineType = l.lineType, Bcje = l.bcje, Wsje = l.wsje, Checked = l.check,
            }).ToList(), dept, per, finVouch: true).GetAwaiter().GetResult();
            // —— 付款 60 ——
            var prow = LegacyReplay.XlskLoadOutstanding(conn, payGname, "付款").Single(r => r.cno == inCno);
            var pyue = LegacyReplay.XlskCustomerYue(conn, payGname);
            var fkSaved = repo.SaveAsync(new XlskSaveCommand
            {
                Type = "付款", Date = DateTime.Today, LszDate = DateTime.Today,
                Fkfs = "挂帐", Pno = "", PaySum = 60m, Gname = payGname, Xm = "", BankName = skqxName,
                LjValue = 60m + pyue, FinVouchEnabled = true, SkqxSubname = skqxName,
                Lines = [new XlskSaveLine { Id = prow.id, Paper = prow.cno, LineType = prow.type, Bcje = 60m, Wsje = Math.Max(prow.yf - prow.skmoney, 0) }],
            }, dept, per).GetAwaiter().GetResult();
            docs.Add(fkSaved.DocNo);
            // —— 整期审核 → 还原被拦截 ——
            finRepo.AuditPeriodAsync(period, per).GetAwaiter().GetResult();
            Assert.Throws<AppException>(() => repo.RestoreAsync(fkSaved.DocNo, "付款", payGname,
                [], dept, per, finVouch: true).GetAwaiter().GetResult());
            return docs;
        });

        Assert.Equal(legacy.Papers, modern.Papers);
        var fv = "ISNULL(rtrim(payno),'') <> ''";
        AssertExtraIdentical("finvouch", "id", fv, fv, ["id"]);
        var subw = "rtrim(subname) IN (N'回放专用客户R',N'回放供应商R')";
        AssertExtraIdentical("finsub", "subcode, id", subw, subw, ["subcode", "id"]);
        AssertExtraIdentical("finvid", "dpm", "1=1", "1=1", ["dpm"]);
        var lw = $"skno IN ({PapersIn(legacy.Papers)})";
        AssertExtraIdentical("tblsz", "id", lw, lw, ["id"]);
        var yw = "rtrim(gname) IN (N'回放专用客户R',N'回放供应商R')";
        AssertExtraIdentical("tbysyf", "id", yw, yw, ["id"]);
        var saw = $"rtrim(paper) = '{saleA}'";
        AssertExtraIdentical("tbsada", "id", saw, saw, ["id"]);
    }

    /// <summary>
    /// 场景 22：账务周期全循环（第七批：I34 结转损益 + I35 期末结帐 + N11 帐务初始 + N12 帐套选项 + N13 启用帐套）。
    /// 种子：专用期间 209901；syscontrol data 行 l_use=0/uperiod=period='209901'；
    /// 科目树 999（一级）→ 999001/999002/999009（二级）→ 99900999（三级末级）。
    /// 流程：detacode 重算 → 置零 → 末级编辑（借贷互斥外的余额/ybsum 派生）→ Sum 逐级汇总（断言中间值）→
    /// 帐套选项保存 → 启用帐套（平衡校验/清空九表/finsoursub 快照列数不匹配静默失败/l_use=1/finvid=0）→
    /// 凭证 2 张（511002 借 1000 累计）→ 审核 → 过帐 → 结转损益（fincaforw：321 头行 + 511002 转出 + 尾差）→
    /// 再审核过帐 → 期末结帐（getfincheout：finhisbal 快照/finsub 期初结转/syscontrol.period 推进）→
    /// 反结帐（finuncheout del=1：对称回退 + CAF 删除）→ 再反结帐（finhisbal 已删 → 拦截）。
    /// diff：finsub/finhisbal/finaleger/finsoursub/finsubit/finitsub/finamsum/finrecout/finreclist/fincadistr 全表 +
    /// finvouch(period 窗口) + syscontrol(data 行) + finvid。
    /// </summary>
    [Fact]
    public void Scenario22_FinClosing_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new FinClosingRepository(factory);
        var finRepo = new FinVouchRepository(factory);
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var period = "209901";
        var date = new DateTime(2099, 1, 15);

        var Lines1 = new (string, string, decimal, decimal, decimal, string, string, string, string, string)[]
        {
            ("511002", "配件销售成本", 600m, 0m, 0m, "", "", "", "", ""),
            ("999001", "回放一级", 0m, 600m, 0m, "", "", "", "", ""),
        };
        var Lines2 = new (string, string, decimal, decimal, decimal, string, string, string, string, string)[]
        {
            ("511002", "配件销售成本", 400m, 0m, 0m, "", "", "", "", ""),
            ("999001", "回放一级", 0m, 400m, 0m, "", "", "", "", ""),
        };

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            conn.Open();
            var msgs = new List<string>();
            // —— 种子：启用前状态 + 科目树 ——
            conn.Execute("UPDATE syscontrol SET l_use = 0, uperiod = @p, period = @p WHERE dpm = @dpm",
                new { p = Ansi(period, 6), dpm = Ansi(dept, 10) });
            LegacyReplay.FinSeedSub(conn, "999", "回放一级", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999009", "回放二级", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999001", "回放测试科目一", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999002", "回放测试科目二", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999009999", "回放三级", 0m, 0m, dept);
            // —— N11 帐务初始：detacode 重算 → 置零 → 末级编辑 → Sum ——
            LegacyReplay.FinInitRecalcDetacode(conn, dept);
            var det = conn.Query<string>(
                "SELECT rtrim(subcode) + ':' + CAST(ISNULL(detacode,0) AS varchar(1)) FROM finsub WHERE rtrim(subcode) LIKE '999%' ORDER BY rtrim(subcode)").AsList();
            msgs.Add("det:" + string.Join(",", det));
            LegacyReplay.FinInitZero(conn, dept);
            LegacyReplay.FinInitUpdateBalance(conn, "999001", 500m, 0m, 0m, 0m, 0m, dept);
            LegacyReplay.FinInitUpdateBalance(conn, "999002", 0m, 500m, 0m, 0m, 0m, dept);
            LegacyReplay.FinInitUpdateBalance(conn, "999009999", 0m, 0m, 200m, 200m, 0m, dept);
            LegacyReplay.FinInitSum(conn, dept, 3);
            var l1 = conn.QueryFirst<(decimal ybd, decimal ybc, decimal bd, decimal bc, decimal yd, decimal yc, decimal ed, decimal ec)>(
                "SELECT ISNULL(ybdesum,0), ISNULL(ybcresum,0), ISNULL(bdesum,0), ISNULL(bcresum,0), ISNULL(ydesum,0), ISNULL(ycresum,0), ISNULL(edesum,0), ISNULL(ecresum,0) FROM finsub WHERE rtrim(subcode) = '999'");
            msgs.Add($"sum999:{l1.ybd}/{l1.ybc}/{l1.bd}/{l1.bc}/{l1.yd}/{l1.yc}/{l1.ed}/{l1.ec}");
            // —— N12 帐套选项保存 → N13 启用帐套 ——
            LegacyReplay.FinParaSave(conn, "data", "回放套账", period, 3, 3, 3, 2, 2, 2, dept);
            msgs.Add("use:" + LegacyReplay.FinUse(conn, dept, 3));
            // FinUse 契约 vid=0 后 vouchid=1 会撞基线 dpm='' 历史行（旧系统查重无 dpm 过滤→"顺序号重复"，
            // 用户手动调整 vid 续号）——两侧同种子对齐 vid 模拟调整后状态
            conn.Execute("UPDATE finvid SET vid = (SELECT ISNULL(MAX(vouchid),0) FROM finvouch) WHERE dpm = @dpm",
                new { dpm = Ansi(dept, 10) });
            // —— 凭证 2 张（5 开头成本累计 1000）→ 审核 → 过帐 ——
            var vc1 = conn.ExecuteScalar<int>("SELECT ISNULL(MAX(vcode),0)+1 FROM finvouch WITH (NOLOCK) WHERE period = @p",
                new { p = Ansi(period, 6) });
            var v1 = conn.ExecuteScalar<int>("SELECT vid + 1 FROM finvid WHERE dpm = @dpm", new { dpm = dept });
            LegacyReplay.FinVouchSave(conn, v1, vc1, date, "记", true, Lines1, dept, per);
            var vc2 = conn.ExecuteScalar<int>("SELECT ISNULL(MAX(vcode),0)+1 FROM finvouch WITH (NOLOCK) WHERE period = @p",
                new { p = Ansi(period, 6) });
            var v2 = conn.ExecuteScalar<int>("SELECT vid + 1 FROM finvid WHERE dpm = @dpm", new { dpm = dept });
            LegacyReplay.FinVouchSave(conn, v2, vc2, date, "记", true, Lines2, dept, per);
            LegacyReplay.FinAudit(conn, period, per);
            LegacyReplay.FinPost(conn, period, per, dept);
            // —— I34 结转损益 → 再审核过帐 ——
            msgs.Add("caforw:" + LegacyReplay.FinCaforw(conn, dept, period, per));
            var caf = conn.QueryFirst<(int vid, int vcode, decimal debit, decimal credit, int recoid)>(
                "SELECT vouchid, vcode, ISNULL(debit,0), ISNULL(credit,0), recoid FROM finvouch WHERE opertype = 'CAF' AND period = @p ORDER BY recoid",
                new { p = Ansi(period, 6) });
            var cafRows = conn.Query<string>(
                "SELECT rtrim(subcode) + ':' + CAST(ISNULL(debit,0) AS varchar) + '/' + CAST(ISNULL(credit,0) AS varchar) FROM finvouch WHERE opertype = 'CAF' AND period = @p ORDER BY recoid",
                new { p = Ansi(period, 6) }).AsList();
            msgs.Add($"caf:{caf.vid}/{caf.vcode}|" + string.Join("|", cafRows));
            LegacyReplay.FinAudit(conn, period, per);
            LegacyReplay.FinPost(conn, period, per, dept);
            // —— I35 期末结帐 → 反结帐 → 再反结帐 ——
            msgs.Add("cheout:" + LegacyReplay.FinCheout(conn, dept, period));
            msgs.Add("uncheout:" + LegacyReplay.FinUncheout(conn, dept, "209902", delCaf: true));
            msgs.Add("uncheout2:" + LegacyReplay.FinUncheout(conn, dept, "209902", delCaf: true));
            var p0 = conn.ExecuteScalar<string>("SELECT RTRIM(period) FROM syscontrol WHERE dpm = @dpm", new { dpm = Ansi(dept, 10) });
            msgs.Add("period:" + p0);
            return msgs;
        });

        var modern = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            conn.Open();
            var msgs = new List<string>();
            conn.Execute("UPDATE syscontrol SET l_use = 0, uperiod = @p, period = @p WHERE dpm = @dpm",
                new { p = Ansi(period, 6), dpm = Ansi(dept, 10) });
            LegacyReplay.FinSeedSub(conn, "999", "回放一级", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999009", "回放二级", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999001", "回放测试科目一", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999002", "回放测试科目二", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999009999", "回放三级", 0m, 0m, dept);
            // —— N11 帐务初始 ——
            repo.RecalcDetacodeAsync(dept).GetAwaiter().GetResult();
            var det = conn.Query<string>(
                "SELECT rtrim(subcode) + ':' + CAST(ISNULL(detacode,0) AS varchar(1)) FROM finsub WHERE rtrim(subcode) LIKE '999%' ORDER BY rtrim(subcode)").AsList();
            msgs.Add("det:" + string.Join(",", det));
            repo.InitZeroAsync(dept).GetAwaiter().GetResult();
            repo.UpdateInitBalanceAsync(new FinInitBalanceCommand { Subcode = "999001", Bdesum = 500m }, dept).GetAwaiter().GetResult();
            repo.UpdateInitBalanceAsync(new FinInitBalanceCommand { Subcode = "999002", Bcresum = 500m }, dept).GetAwaiter().GetResult();
            repo.UpdateInitBalanceAsync(new FinInitBalanceCommand { Subcode = "999009999", Ydesum = 200m, Ycresum = 200m }, dept).GetAwaiter().GetResult();
            repo.InitSumAsync(dept, 3).GetAwaiter().GetResult();
            var l1 = conn.QueryFirst<(decimal ybd, decimal ybc, decimal bd, decimal bc, decimal yd, decimal yc, decimal ed, decimal ec)>(
                "SELECT ISNULL(ybdesum,0), ISNULL(ybcresum,0), ISNULL(bdesum,0), ISNULL(bcresum,0), ISNULL(ydesum,0), ISNULL(ycresum,0), ISNULL(edesum,0), ISNULL(ecresum,0) FROM finsub WHERE rtrim(subcode) = '999'");
            msgs.Add($"sum999:{l1.ybd}/{l1.ybc}/{l1.bd}/{l1.bc}/{l1.yd}/{l1.yc}/{l1.ed}/{l1.ec}");
            // —— N12 帐套选项 → N13 启用帐套 ——
            repo.SaveSysControlAsync(new FinParaSaveCommand
            {
                CId = "data", CName = "回放套账", Uperiod = period, Period = period,
                Leng1 = 3, Leng2 = 3, Leng3 = 3, Leng4 = 2, Leng5 = 2, Leng6 = 2, Gramount = 6,
                Udate = new DateTime(2099, 1, 1),
            }, dept).GetAwaiter().GetResult();
            msgs.Add("use:" + repo.EnableAsync(dept, 3).GetAwaiter().GetResult());
            // 两侧同种子：vid 对齐历史最大 vouchid（模拟旧系统"顺序号重复"后用户手动调号）
            conn.Execute("UPDATE finvid SET vid = (SELECT ISNULL(MAX(vouchid),0) FROM finvouch) WHERE dpm = @dpm",
                new { dpm = Ansi(dept, 10) });
            // —— 凭证 2 张 → 审核 → 过帐 ——
            var vc1 = finRepo.PeekNextVcodeAsync(period).GetAwaiter().GetResult();
            var v1 = finRepo.PeekNextVidAsync(dept).GetAwaiter().GetResult();
            finRepo.SaveAsync(new FinVouchSaveCommand(v1, vc1, date, "记",
                Lines1.Select(l => new FinVouchLine
                {
                    Subcode = l.Item1, Subname = l.Item2, Debit = l.Item3, Credit = l.Item4,
                    Amount = l.Item5, Unit = l.Item6, Dept = l.Item7, Item = l.Item8, Empl = l.Item9, Client = l.Item10,
                }).ToList(), IsAdd: true), dept, per).GetAwaiter().GetResult();
            var vc2 = finRepo.PeekNextVcodeAsync(period).GetAwaiter().GetResult();
            var v2 = finRepo.PeekNextVidAsync(dept).GetAwaiter().GetResult();
            finRepo.SaveAsync(new FinVouchSaveCommand(v2, vc2, date, "记",
                Lines2.Select(l => new FinVouchLine
                {
                    Subcode = l.Item1, Subname = l.Item2, Debit = l.Item3, Credit = l.Item4,
                    Amount = l.Item5, Unit = l.Item6, Dept = l.Item7, Item = l.Item8, Empl = l.Item9, Client = l.Item10,
                }).ToList(), IsAdd: true), dept, per).GetAwaiter().GetResult();
            finRepo.AuditPeriodAsync(period, per).GetAwaiter().GetResult();
            finRepo.PostAsync(period, per, dept, byDate: false, date: null, skipCheck: false, CancellationToken.None).GetAwaiter().GetResult();
            // —— I34 结转损益 → 再审核过帐 ——
            msgs.Add("caforw:" + repo.TransferProfitAsync(dept, period, per).GetAwaiter().GetResult());
            var caf = conn.QueryFirst<(int vid, int vcode, decimal debit, decimal credit, int recoid)>(
                "SELECT vouchid, vcode, ISNULL(debit,0), ISNULL(credit,0), recoid FROM finvouch WHERE opertype = 'CAF' AND period = @p ORDER BY recoid",
                new { p = Ansi(period, 6) });
            var cafRows = conn.Query<string>(
                "SELECT rtrim(subcode) + ':' + CAST(ISNULL(debit,0) AS varchar) + '/' + CAST(ISNULL(credit,0) AS varchar) FROM finvouch WHERE opertype = 'CAF' AND period = @p ORDER BY recoid",
                new { p = Ansi(period, 6) }).AsList();
            msgs.Add($"caf:{caf.vid}/{caf.vcode}|" + string.Join("|", cafRows));
            finRepo.AuditPeriodAsync(period, per).GetAwaiter().GetResult();
            finRepo.PostAsync(period, per, dept, byDate: false, date: null, skipCheck: false, CancellationToken.None).GetAwaiter().GetResult();
            // —— I35 期末结帐 → 反结帐 → 再反结帐 ——
            msgs.Add("cheout:" + repo.PeriodCloseAsync(dept, period).GetAwaiter().GetResult());
            msgs.Add("uncheout:" + repo.PeriodUnCloseAsync(dept, "209902", delCaf: true).GetAwaiter().GetResult());
            msgs.Add("uncheout2:" + repo.PeriodUnCloseAsync(dept, "209902", delCaf: true).GetAwaiter().GetResult());
            var p0 = conn.ExecuteScalar<string>("SELECT RTRIM(period) FROM syscontrol WHERE dpm = @dpm", new { dpm = Ansi(dept, 10) });
            msgs.Add("period:" + p0);
            return msgs;
        });

        Assert.Equal(legacy.Papers, modern.Papers);
        AssertIdentical(legacy.Tables, modern.Tables);
        AssertExtraIdentical("finsub", "id", "1=1", "1=1", ["id"]);
        AssertExtraIdentical("finvouch", "id", $"period = '{period}'", $"period = '{period}'", ["id"]);
        AssertExtraIdentical("finhisbal", "subcode", "1=1", "1=1", ["subcode"]);
        AssertExtraIdentical("finaleger", "subcode, recoid", "1=1", "1=1", ["subcode", "recoid"]);
        AssertExtraIdentical("finsoursub", "subcode", "1=1", "1=1", ["subcode"]);
        AssertExtraIdentical("finsubit", "id", "1=1", "1=1", ["id"]);
        AssertExtraIdentical("finitsub", "id", "1=1", "1=1", ["id"]);
        AssertExtraIdentical("finamsum", "subcode", "1=1", "1=1", ["subcode"]);
        AssertExtraIdentical("finrecout", "id", "1=1", "1=1", ["id"]);
        AssertExtraIdentical("finreclist", "id", "1=1", "1=1", ["id"]);
        AssertExtraIdentical("fincadistr", "vouchid", "1=1", "1=1", ["vouchid"]);
        AssertExtraIdentical("syscontrol", "c_id", "dpm = 'A'", "dpm = 'A'", ["c_id"]);
        AssertExtraIdentical("finvid", "dpm", "1=1", "1=1", ["dpm"]);
    }

    /// <summary>
    /// 场景 23：账簿查询结果集 diff（第八批：I.4 明细账 / I.6 应收应付帐 / I.7 出纳帐 + 出纳手工行写路径）。
    /// 种子：209901 期间，科目树 999 系 + 基线科目 122050（A）/101001（C）/102001（B）；
    /// 凭证 3 张（成本/应收/现金银行）→ UPDATE type 快照 → 审核 → 过帐。
    /// 两侧各执行：明细账（999 前缀+期初+滚动+对方科目串+合计）、应收应付六集合（汇总/余额/明细）、
    /// 出纳（finrecout 汇总+滚动明细）→ 序列化逐行比对；
    /// 手工行（finreclist INSERT debit=50 + finrecout desum/cresum/ebalance 重算）→ 表 diff。
    /// </summary>
    [Fact]
    public void Scenario23_FinLedger_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new FinLedgerRepository(factory);
        var finRepo = new FinVouchRepository(factory);
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var period = "209901";
        var date = new DateTime(2099, 1, 15);

        var Lines = new (string, string, decimal, decimal)[]
        {
            ("511002", "配件销售成本", 600m, 0m),
            ("999001", "回放一级", 0m, 600m),
            ("122050", "回放客户R", 300m, 0m),
            ("101001", "人民币", 500m, 0m),
            ("102001", "银行存款", 0m, 500m),
        };

        // 查询消息经闭包收集；八表固定采集含 tbisto 全表，重内存查询后计划器易资源耗尽 →
        // 场景 23 绕开 RunEx（手动快照重置流程），只 diff 消息 + finreclist/finrecout 表
        var legacyMsgs = new List<string>();
        var modernMsgs = new List<string>();

        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            var msgs = legacyMsgs;
            conn.Execute("UPDATE syscontrol SET period = @p WHERE dpm = @dpm", new { p = Ansi(period, 6), dpm = Ansi(dept, 10) });
            LegacyReplay.FinSeedSub(conn, "999", "回放一级", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999001", "回放一级", 0m, 0m, dept);
            var vc = conn.ExecuteScalar<int>("SELECT ISNULL(MAX(vcode),0)+1 FROM finvouch WITH (NOLOCK) WHERE period = @p", new { p = Ansi(period, 6) });
            var vid = conn.ExecuteScalar<int>("SELECT vid + 1 FROM finvid WHERE dpm = @dpm", new { dpm = dept });
            LegacyReplay.FinVouchSave(conn, vid, vc, date, "记", true,
                Lines.Select(l => (l.Item1, l.Item2, l.Item3, l.Item4, 0m, "", "", "", "", "")).ToList(), dept, per);
            // type 快照（科目类别）
            conn.Execute(
                """
                UPDATE finvouch SET type = CASE rtrim(subcode)
                    WHEN '122050' THEN 'A' WHEN '101001' THEN 'C' WHEN '102001' THEN 'B' ELSE '' END
                WHERE period = @p
                """, new { p = Ansi(period, 6) });
            LegacyReplay.FinAudit(conn, period, per);
            LegacyReplay.FinPost(conn, period, per, dept);
            // —— 查询 A：明细账（999 前缀，仅过帐）——
            var det = LegacyReplay.FinAledetLegacy(conn, period, period, "999", false);
            msgs.AddRange(det.Select(d => "DET|" + d));
            // —— 查询 B：应收应付（当月窗口，全部对帐态）——
            var ys = LegacyReplay.FinYsyfLegacy(conn, new DateTime(2099, 1, 1), new DateTime(2099, 1, 31), 0, dept);
            msgs.AddRange(ys.Select(y => "YSYF|" + y));
            // —— 查询 C：出纳（当月窗口，仅过帐）——
            var rnp = LegacyReplay.FinRnpLegacy(conn, new DateTime(2099, 1, 1), new DateTime(2099, 1, 31), false, dept);
            msgs.AddRange(rnp.Select(r => "RNP|" + r));
            // —— 手工行（写路径）——
            msgs.Add("RECLIST:" + LegacyReplay.FinReclistAddLegacy(conn, "101001", 99999, date, "回放手工行", 50m, 0m, dept));
        }
        var legacyTables = new Dictionary<string, List<Dictionary<string, object>>>();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            legacyTables["finreclist"] = ReplayEnv.SnapshotTable(conn, "finreclist", "id");
            legacyTables["finrecout"] = ReplayEnv.SnapshotTable(conn, "finrecout", "id", "rtrim(accname) LIKE N'回放%'");
        }

        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            var msgs = modernMsgs;
            conn.Execute("UPDATE syscontrol SET period = @p WHERE dpm = @dpm", new { p = Ansi(period, 6), dpm = Ansi(dept, 10) });
            LegacyReplay.FinSeedSub(conn, "999", "回放一级", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999001", "回放一级", 0m, 0m, dept);
            var vc = finRepo.PeekNextVcodeAsync(period).GetAwaiter().GetResult();
            var vid = finRepo.PeekNextVidAsync(dept).GetAwaiter().GetResult();
            finRepo.SaveAsync(new FinVouchSaveCommand(vid, vc, date, "记",
                Lines.Select(l => new FinVouchLine
                { Subcode = l.Item1, Subname = l.Item2, Debit = l.Item3, Credit = l.Item4 }).ToList(),
                IsAdd: true), dept, per).GetAwaiter().GetResult();
            conn.Execute(
                """
                UPDATE finvouch SET type = CASE rtrim(subcode)
                    WHEN '122050' THEN 'A' WHEN '101001' THEN 'C' WHEN '102001' THEN 'B' ELSE '' END
                WHERE period = @p
                """, new { p = Ansi(period, 6) });
            finRepo.AuditPeriodAsync(period, per).GetAwaiter().GetResult();
            finRepo.PostAsync(period, per, dept, byDate: false, date: null, skipCheck: false, CancellationToken.None).GetAwaiter().GetResult();
            // —— 查询 A：明细账 ——
            var det = repo.GetAledetAsync(period, period, "999", false, ct: CancellationToken.None).GetAwaiter().GetResult();
            foreach (var d in det)
                msgs.Add("DET|" + (d.Recoid == 1
                    ? $"1|{d.Date:yyyy-MM-dd}|{d.Note}|{d.Debit:0.##}/{d.Credit:0.##}|{d.Dcdir}|{d.Balance:0.##}"
                    : d.Recoid == 99
                        ? $"99|{d.Date:yyyy-MM-dd}|{d.Note}|{d.Debit:0.##}/{d.Credit:0.##}|{d.Dcdir}|{d.Balance:0.##}"
                        : $"2|{d.Date:yyyy-MM-dd}|{d.Vouword}-{d.Vcode}|{d.Debit:0.##}/{d.Credit:0.##}|{d.Dcdir}|{d.Balance:0.##}|{d.Subcode}||{d.Resub}"));
            // —— 查询 B：应收应付 ——
            var ysyf = repo.GetYsyfAsync(new DateTime(2099, 1, 1), new DateTime(2099, 1, 31), 0, null, dept,
                CancellationToken.None).GetAwaiter().GetResult();
            foreach (var r in ysyf.YsHz)
                msgs.Add($"YSYF|YSHZ|{r.Subcode}|{r.Subname}|{r.Qcys:0.##}|{r.Debit:0.##}|{r.Credit:0.##}|{r.Ys:0.##}");
            foreach (var r in ysyf.YfHz)
                msgs.Add($"YSYF|YFHZ|{r.Subcode}|{r.Subname}|{r.Qcys:0.##}|{r.Debit:0.##}|{r.Credit:0.##}|{r.Ys:0.##}");
            foreach (var r in ysyf.YsyfHz)
                msgs.Add($"YSYF|YSHZ2|{r.Subname}|{r.Qcdir}|{r.Qcyue:0.##}|{r.Dcdir}|{r.Yue:0.##}");
            foreach (var r in ysyf.Ys)
                msgs.Add($"YSYF|YS|{r.Vouchid}|{r.Date:yyyy-MM-dd}|{r.Wordid}|{FinLedgerRepository.OpTypeName(r.Opertype)}|{r.Opercode}|" +
                         $"{r.Debit:0.##}/{r.Credit:0.##}|{r.Balance:0.##}|{(r.Fincheck == true ? 1 : 0)}");
            foreach (var r in ysyf.Yf)
                msgs.Add($"YSYF|YF|{r.Vouchid}|{r.Date:yyyy-MM-dd}|{r.Wordid}|{FinLedgerRepository.OpTypeName(r.Opertype)}|{r.Opercode}|" +
                         $"{r.Debit:0.##}/{r.Credit:0.##}|{r.Balance:0.##}|{(r.Fincheck == true ? 1 : 0)}");
            foreach (var r in ysyf.Ysyf)
                msgs.Add($"YSYF|YSYF|{r.Vouchid}|{r.Date:yyyy-MM-dd}|{r.Wordid}|{FinLedgerRepository.OpTypeName(r.Opertype)}|{r.Opercode}|" +
                         $"{r.Debit:0.##}/{r.Credit:0.##}|{r.DcdirRaw}|{r.Balance:0.##}");
            // —— 查询 C：出纳 ——
            var rnp = repo.GetRnpAsync(new DateTime(2099, 1, 1), new DateTime(2099, 1, 31), false, dept,
                CancellationToken.None).GetAwaiter().GetResult();
            foreach (var r in rnp.Recout)
                msgs.Add($"RNP|RECOUT|{r.Accname}|{r.Ctype}|{r.Accounts}|{r.Subname}|" +
                         $"{r.Bbalance:0.##}|{r.Desum:0.##}|{r.Cresum:0.##}|{r.Ebalance:0.##}");
            foreach (var v in rnp.Vouch)
                msgs.Add($"RNP|V|{v.Vouchid}|{v.Date:yyyy-MM-dd}|{v.Wordid}|{v.Opertype}|" +
                         $"{(v.Debit is null || v.Debit == 0 ? "" : v.Debit.Value.ToString("0.##"))}/" +
                         $"{(v.Credit is null || v.Credit == 0 ? "" : v.Credit.Value.ToString("0.##"))}|" +
                         $"{(v.Balance is null ? "" : v.Balance.Value.ToString("0.##"))}");
            // —— 手工行（写路径）——
            msgs.Add("RECLIST:" + repo.SaveFinreclistAsync(new FinReclistCommand
            {
                Subcode = "101001", Vouchid = 99999, Date = date, Note = "回放手工行", Debit = 50m,
            }, isAdd: true, dept).GetAwaiter().GetResult());
        }
        var modernTables = new Dictionary<string, List<Dictionary<string, object>>>();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            modernTables["finreclist"] = ReplayEnv.SnapshotTable(conn, "finreclist", "id");
            modernTables["finrecout"] = ReplayEnv.SnapshotTable(conn, "finrecout", "id", "rtrim(accname) LIKE N'回放%'");
        }

        // 查询结果集逐行比对（闭包消息）
        Assert.Equal(legacyMsgs, modernMsgs);
        // 手工行写路径表 diff（内存快照比对）
        Assert.True(ReplayEnv.Diff(legacyTables["finreclist"], modernTables["finreclist"], "finreclist", ["id"]).Count == 0);
        Assert.True(ReplayEnv.Diff(legacyTables["finrecout"], modernTables["finrecout"], "finrecout", ["id"]).Count == 0);
    }

    /// <summary>
    /// 场景 24：报表+现金流+工具+固定资产（第九批，手动快照流程）。
    /// 种子：209901 期间凭证+过账（同场景 23）+ finprol 表达式模板种子（SUM 展开行+科目行+C 引用行）
    /// + finvouch fcash=1（101001 行）。两侧各执行：试算平衡/凭证汇总（消息序列化）、损益 Sum+Save
    /// （表达式引擎独立移植互验 → finprol 表 diff）、现金流分配（差额校验+重写 → fincadistr/finvouch diff）、
    /// fintool 汇总（MPALSUM 旧 bug 对齐 → finsub diff）、固定资产 INSERT、帐簿初始化（清空断言）。
    /// </summary>
    [Fact]
    public void Scenario24_FinReport_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new FinReportRepository(factory);
        var finRepo = new FinVouchRepository(factory);
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        var period = "209901";
        var date = new DateTime(2099, 1, 15);
        var legacyMsgs = new List<string>();
        var modernMsgs = new List<string>();
        var now = DateTime.Now;

        int Seed(SqlConnection conn)
        {
            conn.Execute("UPDATE syscontrol SET period = @p WHERE dpm = @dpm", new { p = Ansi(period, 6), dpm = Ansi(dept, 10) });
            LegacyReplay.FinSeedSub(conn, "999", "回放一级", 0m, 0m, dept);
            LegacyReplay.FinSeedSub(conn, "999001", "回放一级", 0m, 0m, dept);
            var vc = conn.ExecuteScalar<int>("SELECT ISNULL(MAX(vcode),0)+1 FROM finvouch WITH (NOLOCK) WHERE period = @p", new { p = Ansi(period, 6) });
            var vid = conn.ExecuteScalar<int>("SELECT vid + 1 FROM finvid WHERE dpm = @dpm", new { dpm = dept });
            var lines = new (string, string, decimal, decimal)[]
            {
                ("511002", "配件销售成本", 600m, 0m),
                ("999001", "回放一级", 0m, 600m),
                ("101001", "人民币", 500m, 0m),
                ("102001", "银行存款", 0m, 500m),
            };
            LegacyReplay.FinVouchSave(conn, vid, vc, date, "记", true,
                lines.Select(l => (l.Item1, l.Item2, l.Item3, l.Item4, 0m, "", "", "", "", "")).ToList(), dept, per);
            conn.Execute(
                "UPDATE finvouch SET type = CASE rtrim(subcode) WHEN '101001' THEN 'C' WHEN '102001' THEN 'B' ELSE '' END, " +
                "fcash = CASE WHEN rtrim(subcode) = '101001' THEN 1 ELSE 0 END WHERE period = @p", new { p = Ansi(period, 6) });
            LegacyReplay.FinAudit(conn, period, per);
            LegacyReplay.FinPost(conn, period, per, dept);
            // finprol 模板种子：SUM 展开行 + 科目表达式行 + C 引用行
            conn.Execute("DELETE FROM finprol WHERE dpm = @dpm", new { dpm = Ansi(dept, 10) });
            conn.Execute(
                """
                INSERT INTO finprol (dpm, reid, itemna, itemty, csum, nsum, cexpress, nexpress, date) VALUES
                (@dpm, 1, N'主营业务收入', N'', 0, 0, N'SUM(C:501002:C:501002)', N'SUM(D:501002:D:501002)', '1900-01-01'),
                (@dpm, 2, N'成本合计（C 引用）', N'', 0, 0, N'C1', N'C1', '1900-01-01'),
                (@dpm, 3, N'科目直取（.SY 损益）', N'', 0, 0, N'511002.SY', N'511002.SY', '1900-01-01')
                """, new { dpm = Ansi(dept, 10) });
            return vid;
        }

        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            var msgs = legacyMsgs;
            var vid = Seed(conn);
            // 查询：试算平衡（当期）+ 凭证汇总（仅过帐）
            foreach (var t in LegacyReplay.FinTrialLegacy(conn, period, false, dept)) msgs.Add(t);
            foreach (var s in LegacyReplay.FinVouchSummaryLegacy(conn, date.AddDays(-1), date.AddDays(1), "(全部)", 2, dept)) msgs.Add(s);
            // 损益 Sum+Save（引擎；date 列两侧统一 now）
            LegacyReplay.FinProlSumSaveLegacy(conn, period, false, dept, now);
            // 现金流分配（差额平衡：101001 借 500 → 流入 500；差额两侧各自查库）
            msgs.Add("DIS:" + LegacyReplay.FinCashDisLegacy(conn, vid, period, date,
                [("01", "销售收到", 1, 500m, 0m)], dept));
            // fintool 汇总（MPALSUM bug）+ 固定资产 INSERT
            LegacyReplay.FinToolSumLegacy(conn, dept, 3);
            LegacyReplay.FinPerAddLegacy(conn, "perassty", "房屋建筑", 1, "", dept);
            LegacyReplay.FinPerAddLegacy(conn, "perchgty", "购入增加", 1, "102001", dept);
        }
        var legacyTables = SnapFinReportTables();

        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            var msgs = modernMsgs;
            var vid = Seed(conn);
            foreach (var r in repo.GetTrialBalanceAsync(period, null, 3, false, dept, CancellationToken.None).GetAwaiter().GetResult())
                msgs.Add($"TRIAL|{r.Subcode}|{r.Subname}|{r.Fdc}|{r.Ybdesum:0.##}/{r.Ybcresum:0.##}|{r.Bdesum:0.##}/{r.Bcresum:0.##}|" +
                         $"{r.Cdesum:0.##}/{r.Ccresum:0.##}|{r.Ydesum:0.##}/{r.Ycresum:0.##}|{r.Edesum:0.##}/{r.Ecresum:0.##}");
            var sum = repo.GetVouchSummaryAsync(date.AddDays(-1), date.AddDays(1), "(全部)", 2, 3, dept, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var r in sum.Rows) msgs.Add($"SUM|{r.Subcode}|{r.Subname}|{r.Debit:0.##}|{r.Credit:0.##}");
            msgs.Add($"SUMSTAT|{sum.VouchCount}|{sum.AbillSum:0.##}");
            // 损益 Sum+Save（仓储引擎求值+finprol 全量重写，与旧侧回放一体等价；date 同 now）
            repo.SumProlAsync(period, false, dept, now, CancellationToken.None).GetAwaiter().GetResult();
            // 现金流分配（差额两侧各自查库：101001 借 500 → 流入 500）
            var vouchDiff = repo.GetVouchDiffAsync(vid, CancellationToken.None).GetAwaiter().GetResult();
            msgs.Add("DIS:" + repo.SaveCashDisAsync(new FinCashDisCommand
            {
                Vouchid = vid, Period = period, Date = date,
                Lines = [("01", "销售收到", 1, 500m, 0m)],
            }, vouchDiff, dept).GetAwaiter().GetResult());
            // fintool 汇总 + 固定资产
            repo.ToolSumAsync(dept, 3, CancellationToken.None).GetAwaiter().GetResult();
            repo.AddPerasstyAsync(new PerasstyRow { Perasty = "房屋建筑", Deprty = 1 }, dept).GetAwaiter().GetResult();
            repo.AddPerchgtyAsync(new PerchgtyRow { Chgna = "购入增加", Chgty = 1, Subcode = "102001" }, dept).GetAwaiter().GetResult();
        }
        var modernTables = SnapFinReportTables();

        Assert.Equal(legacyMsgs, modernMsgs);
        foreach (var key in legacyTables.Keys)
            Assert.True(ReplayEnv.Diff(legacyTables[key], modernTables[key], key,
                key == "finvouch" ? ["id"] : key == "finsub" ? ["subcode"] : ["id"]).Count == 0,
                $"表 {key} 不一致");
    }

    private static Dictionary<string, List<Dictionary<string, object>>> SnapFinReportTables()
    {
        using var conn = ReplayEnv.Open();
        conn.Open();
        return new Dictionary<string, List<Dictionary<string, object>>>
        {
            ["finprol"] = ReplayEnv.SnapshotTable(conn, "finprol", "reid"),
            ["fincadistr"] = ReplayEnv.SnapshotTable(conn, "fincadistr", "id"),
            ["finvouch"] = ReplayEnv.SnapshotTable(conn, "finvouch", "id", "period = '209901'"),
            ["finsub"] = ReplayEnv.SnapshotTable(conn, "finsub", "subcode", "rtrim(subcode) LIKE '999%' OR rtrim(subcode) IN ('511002','101001','102001')"),
            ["perassty"] = ReplayEnv.SnapshotTable(conn, "perassty", "perasid"),
            ["perchgty"] = ReplayEnv.SnapshotTable(conn, "perchgty", "chgid"),
        };
    }

    /// <summary>
    /// 场景 25：B4 库存调价 + B5 警戒线设置（第十批，手动快照流程）。
    /// 种子：tbisto 回放批次 2 行（RKR25 厂牌）+ tbrtsamo 月销售数 2 行 + tbprnoty 1 行。
    /// B4：旧侧 UPDATE SQL 原样重放 vs 新 AdjustAsync（oprc=oprc*1.1 取整 int+0.5；供应商基准价 iprj）→ tbisto diff；
    /// B5：旧侧循环重放（UPDATE 有行/INSERT 双系数>0/清 0 行）vs 新 SetJjLinesAsync → tbistojj diff。
    /// </summary>
    [Fact]
    public void Scenario25_PriceAdjustJj_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new PriceAdjustRepository(factory);
        var dept = ReplayConfig.Dept;

        void Seed(SqlConnection conn)
        {
            // tbisto 回放批次（nno/na1/cp/kcamount/iprc/iprj/oprc/depotno/dpm）
            conn.Execute(
                """
                IF NOT EXISTS (SELECT 1 FROM tbisto WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm AND rtrim(depotno) = @depotno)
                    INSERT INTO tbisto (cp, nno, na1, ty, unit, kcamount, iprc, iprj, oprc, oprb, posi, depotno, dpm, cxnno)
                    VALUES (@cp, @nno, @na1, N'', N'个', @kc, @iprc, @iprj, @oprc, @oprb, N'A1', @depotno, @dpm, N'')
                """,
                new { nno = "RN25A", cp = "回放厂牌R", na1 = "回放配件R", kc = 100m, iprc = 10m, iprj = 12m, oprc = 15m, oprb = 14m, depotno = "01", dpm = Ansi(dept, 10) });
            conn.Execute(
                """
                IF NOT EXISTS (SELECT 1 FROM tbisto WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm AND rtrim(depotno) = @depotno)
                    INSERT INTO tbisto (cp, nno, na1, ty, unit, kcamount, iprc, iprj, oprc, oprb, posi, depotno, dpm, cxnno)
                    VALUES (@cp, @nno, @na1, N'', N'个', @kc, @iprc, @iprj, @oprc, @oprb, N'A1', @depotno, @dpm, N'')
                """,
                new { nno = "RN25B", cp = "回放厂牌R", na1 = "回放配件R2", kc = 50m, iprc = 20m, iprj = 25m, oprc = 30m, oprb = 28m, depotno = "01", dpm = Ansi(dept, 10) });
            // tbrtsamo 月销售数（最近 3 月 = oamou{M-3}~{M-1}；tbrtsamo 一行 12 列——首月 INSERT 后续月 UPDATE 同行）
            var m1 = DateTime.Today.Month + 12 - 3; if (m1 > 12) m1 -= 12;
            var m2 = DateTime.Today.Month + 12 - 2; if (m2 > 12) m2 -= 12;
            var m3 = DateTime.Today.Month + 12 - 1; if (m3 > 12) m3 -= 12;
            conn.Execute(
                "IF NOT EXISTS (SELECT 1 FROM tbrtsamo WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm) " +
                $"INSERT INTO tbrtsamo (dpm, cp, nno, na1, oamou{m1}) VALUES (@dpm, @cp, @nno, @na1, @oamou)",
                new { dpm = Ansi(dept, 10), cp = "回放厂牌R", nno = "RN25A", na1 = "回放配件R", oamou = 30m });
            conn.Execute(
                "IF NOT EXISTS (SELECT 1 FROM tbrtsamo WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm) " +
                $"INSERT INTO tbrtsamo (dpm, cp, nno, na1, oamou{m1}) VALUES (@dpm, @cp, @nno, @na1, @oamou)",
                new { dpm = Ansi(dept, 10), cp = "回放厂牌R", nno = "RN25B", na1 = "回放配件R2", oamou = 20m });
            foreach (var m in new[] { m2, m3 })
            {
                conn.Execute($"UPDATE tbrtsamo SET oamou{m} = @oamou WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm",
                    new { oamou = 30m, nno = "RN25A", cp = Ansi("回放厂牌R", 10), dpm = Ansi(dept, 10) });
                conn.Execute($"UPDATE tbrtsamo SET oamou{m} = @oamou WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm",
                    new { oamou = 20m, nno = "RN25B", cp = Ansi("回放厂牌R", 10), dpm = Ansi(dept, 10) });
            }
            conn.Execute(
                "IF NOT EXISTS (SELECT 1 FROM tbprnoty WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp) " +
                "INSERT INTO tbprnoty (nno, cp, na1, fa, dc, ty) VALUES (@nno, @cp, @na1, N'回放产地', N'A', N'回放类型')",
                new { nno = "RN25A", cp = "回放厂牌R", na1 = "回放配件R" });
        }

        var legacyTables = new Dictionary<string, List<Dictionary<string, object>>>();
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            Seed(conn);
            // B4 旧侧：oprc = CONVERT(int, oprc * 1.1 + 0.5)（库存基准价，全部厂牌窗口=本批种子 cp）
            conn.Execute(
                """
                UPDATE isto SET oprc = CONVERT(int, oprc * 1.1 + 0.5)
                FROM tbisto isto WHERE rtrim(isto.cp) = @cp AND rtrim(isto.depotno) = '01' AND isto.dpm = @dpm
                """, new { cp = Ansi("回放厂牌R", 10), dpm = Ansi(dept, 10) });
            // B4 旧侧：iprj = gys.iprj * 1（供应商基准价——tbgys 无回放行=不更新，两侧同空）
            // B5 旧侧：月均 SUM=90（3 月×30）→ /3*1.2=36、*1.5=45；RN25B SUM=60 → 24/30（ROUND(,0)）
            // RN25B 无 tbprnoty 行 → source_temp.ty 为空（INSERT ty=''）
            conn.Execute(
                "INSERT INTO tbistojj (dpm, cp, nno, na1, ty, amjj, bmjj) VALUES (@dpm, @cp, @nno, @na1, @ty, @amjj, @bmjj)",
                new { dpm = Ansi(dept, 10), cp = "回放厂牌R", nno = "RN25A", na1 = "回放配件R", ty = "回放类型", amjj = 36m, bmjj = 45m });
            conn.Execute(
                "INSERT INTO tbistojj (dpm, cp, nno, na1, ty, amjj, bmjj) VALUES (@dpm, @cp, @nno, @na1, @ty, @amjj, @bmjj)",
                new { dpm = Ansi(dept, 10), cp = "回放厂牌R", nno = "RN25B", na1 = "回放配件R2", ty = "", amjj = 24m, bmjj = 30m });
            conn.Execute("DELETE FROM tbistojj WHERE ISNULL(amjj,0) = 0 AND ISNULL(bmjj,0) = 0");
        }
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            legacyTables["tbisto"] = ReplayEnv.SnapshotTable(conn, "tbisto", "id", "rtrim(nno) LIKE 'RN25%'");
            legacyTables["tbistojj"] = ReplayEnv.SnapshotTable(conn, "tbistojj", "id");
        }

        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            Seed(conn);
            // B4 新侧（等价旧 UPDATE）
            repo.AdjustAsync(new PriceAdjustCommand
            {
                FieldKind = 1, BaseKind = 1, Oper = "*", Factor = 1.1m, RoundMode = 2, Depotno = "01",
            }, "回放厂牌R", dept).GetAwaiter().GetResult();
            // B5 新侧（等价旧 INSERT 双系数结果——factors 由旧 INSERT 反推：30/3*1.2=12、*1.5=15）
            repo.SetJjLinesAsync(new JjLineCommand
            {
                Months = 3, FactorA = 1.2m, FactorB = 1.5m, SetAmjj = true, SetBmjj = true, AllBrand = false, Cp = "回放厂牌R",
            }, dept).GetAwaiter().GetResult();
        }
        var modernTables = new Dictionary<string, List<Dictionary<string, object>>>();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            modernTables["tbisto"] = ReplayEnv.SnapshotTable(conn, "tbisto", "id", "rtrim(nno) LIKE 'RN25%'");
            modernTables["tbistojj"] = ReplayEnv.SnapshotTable(conn, "tbistojj", "id");
        }

        foreach (var key in legacyTables.Keys)
        {
            var diffs = ReplayEnv.Diff(legacyTables[key], modernTables[key], key, ["id"]);
            Assert.True(diffs.Count == 0, $"表 {key} 不一致：{string.Join("; ", diffs.Take(4))}");
        }
    }

    /// <summary>
    /// 场景 26：E 组万能查询族（第十一批，手动快照流程，查询结果集闭包 diff）。
    /// 种子：回放厂牌 R 窗口（tbisto/tbistoed/tbsada 四类型/tbistoch/tbsaxg/tbistosav/tborder/tbrtsamo）。
    /// E1 条件查询+大 CASE+合计 / E4 type='历史'+合计 / E5 日期快捷+益损 / E6 编码维度六路聚合（中间值断言：调拔三列恒 0、历史/预售毛利并入）/
    /// E7 nno 前缀+月均 / E8 当日窗+部门名翻译+合计 / E9 备份链 vs 实库（差异行）/ EA 单品五路+倒序滚动。
    /// </summary>
    [Fact]
    public void Scenario26_UniversalQuery_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new UniversalQueryRepository(factory);
        var dept = ReplayConfig.Dept;
        var cp = "回放厂牌R";
        var dpmusem = "A"; // tbrole.dpmusem（回放操作员部门权限串）
        var today = DateTime.Today;

        var legacyMsgs = new List<string>();
        var modernMsgs = new List<string>();
        var legacySrc = new List<string>(); // E6 中间值（旧码 c_tmpy1 逐类型聚合行，仅旧侧自证，不参与两侧 diff）

        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            LegacyReplay.SeedUniversalQuery(conn, dept, cp, today);
            var msgs = legacyMsgs;
            // —— E1 进库单（快捷「已确认」+ 包含「回放」，显示剩余）——
            msgs.AddRange(LegacyReplay.UniversalIssmLegacy(conn, dept, dpmusem, showRemain: true));
            // —— E4 订货单 ——
            msgs.AddRange(LegacyReplay.UniversalOrderLegacy(conn, dept, cp, dpmusem));
            // —— E5 库存修改（日期快捷「本月」）——
            msgs.AddRange(LegacyReplay.UniversalKcxgLegacy(conn, dept, dpmusem, today, today));
            // —— E6 商品出入（编码维度，中间值+最终行）——
            var (ioSrc, ioRows) = LegacyReplay.UniversalIoLegacy(conn, dept, cp, dpmusem, today, today, "nno between 'RN26A' and 'RN26Cz'");
            legacySrc.AddRange(ioSrc);
            msgs.AddRange(ioRows);
            // 中间值断言（旧码 bug 证据链）：调拨/历史/预售行各有毛利；调拔三列恒 0（IO|RN26B 尾部 |0|0|0）
            Assert.Contains(legacySrc, m => m.StartsWith("IO-SRC|调拨|RN26B|"));
            Assert.Contains(legacySrc, m => m.StartsWith("IO-SRC|历史|RN26B|"));
            Assert.Contains(legacySrc, m => m.StartsWith("IO-SRC|预售|RN26A|"));
            Assert.Contains(msgs, m => m.StartsWith("IO|RN26B|") && m.EndsWith("|0|0|0"));
            // —— E7 月销售数 ——
            msgs.AddRange(LegacyReplay.UniversalRtmoLegacy(conn, dept, dpmusem));
            // —— E8 营业额（当日窗 + 当前分公司）——
            msgs.AddRange(LegacyReplay.UniversalSesadaLegacy(conn, dept, dpmusem, today, today, "A"));
            // —— E9 库存检测 ——
            msgs.AddRange(LegacyReplay.UniversalCheckLegacy(conn, dept, cp, today));
            // —— EA 进出流水 ——
            msgs.AddRange(LegacyReplay.UniversalIsioLegacy(conn, dept, cp, dpmusem, "RN26A", today, today));
        }

        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            LegacyReplay.SeedUniversalQuery(conn, dept, cp, today);
            var msgs = modernMsgs;
            var d = today.Date;
            // —— E1：fr_find 构造 (mark='已确认' and na1 like '%回放%') → SearchIssmAsync（双份 dpmusem + 大 CASE）——
            var issm = repo.SearchIssmAsync("(mark='已确认' and na1 like '%回放%')", true, dept, dpmusem).GetAwaiter().GetResult();
            foreach (var r in issm)
                msgs.Add($"ISSM|{r.Id}|{r.Nno}|{r.Na1}|{r.Jkamount:0.##}|{r.Iprj:0.##}|{r.Iprc:0.##}|{r.Xkcamou:0.##}|{r.Xsamou:0.##}|{r.Freight:0.##}|{r.Indate:yyyy-MM-dd}|{r.Cno}");
            // E1 合计（CHECK1=1、P_WBTOHS=1 → iprj、P_IPRCCOUNT=4）
            msgs.Add($"ISSMTOT|{issm.Count}|{issm.Sum(r => (double)Math.Truncate(r.Jkamount)):0.##}|{issm.Sum(r => (double)r.Xsamou):0.##}|{issm.Sum(r => (double)r.Xkcamou):0.##}|" +
                     $"{issm.Sum(r => (double)Math.Round(r.Jkamount * r.Iprj, 2)):0.##}|{issm.Sum(r => (double)Math.Round(r.Iprc * r.Jkamount, 4)):0.##}|" +
                     $"{issm.Sum(r => (double)Math.Round(r.Iprj * r.Xsamou, 2)):0.##}|{issm.Sum(r => (double)Math.Round(r.Iprj * r.Xkcamou, 2)):0.##}|{issm.Sum(r => (double)r.Freight):0.##}");
            // —— E4 ——
            var ord = repo.SearchOrderAsync("(nno like '%RN26%')", dept, cp, dpmusem).GetAwaiter().GetResult();
            foreach (var r in ord)
                msgs.Add($"ORD|{r.Id}|{r.Cno}|{r.Item}|{r.Nno}|{r.Na1}|{r.Oqty:0.##}|{r.Qty:0.##}|{r.Zt:0.##}|{r.Iprj:0.##}|{r.Orderdate:yyyy-MM-dd}");
            msgs.Add($"ORDTOT|{ord.Count}|{ord.Sum(r => (double)Math.Truncate(r.Oqty)):0.##}|{ord.Sum(r => (double)Math.Round(r.Oqty * r.Iprj, 2)):0.##}|" +
                     $"{ord.Sum(r => (double)Math.Truncate(r.Oqty - r.Qty)):0.##}|{ord.Sum(r => (double)Math.Round((r.Oqty - r.Qty) * r.Iprj, 2)):0.##}");
            // —— E5：日期快捷「本月」→ fr_find 生成区间 ——
            var ms = new DateTime(today.Year, today.Month, 1);
            var me = ms.AddMonths(1).AddDays(-1);
            var kcxg = repo.SearchKcxgAsync(
                $"xgdate>='{FindExpressionBuilder.Dtoc(ms)}' and xgdate<'{FindExpressionBuilder.Dtoc(me.AddDays(1))}'", dept, dpmusem).GetAwaiter().GetResult();
            foreach (var r in kcxg)
                msgs.Add($"KCXG|{r.Id}|{r.Nno}|{r.Na1}|{r.Ty}|{r.Xgamount:0.##}|{r.Iprc:0.##}|{r.Xgdate:yyyy-MM-dd}|{r.Per}|{r.Posi}|{r.Depotno}");
            msgs.Add($"KCXGTOT|{kcxg.Sum(r => (double)r.Xgamount):0.##}|{kcxg.Sum(r => (double)r.Iprc):0.##}");
            // —— E6 ——
            var io = repo.SearchIoAsync("nno", today, today, "nno between 'RN26A' and 'RN26Cz'", dept, cp, dpmusem).GetAwaiter().GetResult();
            foreach (var r in io)
                msgs.Add($"IO|{r.How}|{r.销售数:0.##}|{r.销售成本:0.##}|{r.销售额:0.##}|{r.进库数:0.##}|{r.进库额:0.##}|{r.库存数:0.##}|{r.毛利:0.##}|" +
                         $"{r.毛利率}|{r.预售数:0.##}|{r.预售成本:0.##}|{r.预售额:0.##}|{r.调拔数:0.##}|{r.调拔成本:0.##}|{r.调拔额:0.##}");
            // —— E7 ——
            var rtmo = repo.SearchRtmoAsync("nno between 'RN26A' and 'RN26Cz'", dept, dpmusem, 12).GetAwaiter().GetResult();
            foreach (var r in rtmo)
                msgs.Add($"RTMO|{r.Id}|{r.Dpm}|{r.Cp}|{r.Nno}|{r.Na1}|{r.Ty}|{r.Unit}|{r.Oamou1:0.##}|{r.Oamou2:0.##}|{r.Oamou3:0.##}|{r.Oamou4:0.##}|{r.Oamou5:0.##}|{r.Oamou6:0.##}|" +
                         $"{r.Oamou7:0.##}|{r.Oamou8:0.##}|{r.Oamou9:0.##}|{r.Oamou10:0.##}|{r.Oamou11:0.##}|{r.Oamou12:0.##}|{r.月均销售数:0.####}");
            // —— E8 ——
            var sesada = repo.SearchSesadaAsync(today, today, "A", dept, dpmusem).GetAwaiter().GetResult();
            foreach (var r in sesada)
                msgs.Add($"SESADA|{r.日期}|{r.Dpm}|{r.单数}|{r.出库数量:0.##}|{r.日元:0.##}|{r.人进价:0.##}|{r.售额:0.##}|{r.实售额:0.##}|{r.ML:0.##}|{r.牟利率:0.##}");
            // —— E9 ——
            var chk = repo.CheckStockAsync(today, dept, cp, dpmusem).GetAwaiter().GetResult();
            Assert.True(chk.HasBackup);
            msgs.Add($"CHK-TOT|{chk.SavA:0.##}|{chk.IstoedA:0.##}|{chk.SaedA:0.##}|{chk.SadaA:0.##}|{chk.SadaAll - chk.SadaA:0.##}|{chk.SabeA:0.##}|{chk.SadbA:0.##}|{chk.XlsaA:0.##}|{chk.IstochA:0.##}|{chk.SaxgA:0.##}|{chk.NowA:0.##}|{chk.IstoA:0.##}|{chk.DiffA:0.##}");
            foreach (var r in chk.Rows)
                msgs.Add($"CHK|{r.Nno}|{r.Na1}|{r.Ty}|{r.Sav:0.##}|{r.Outs:0.##}|{r.In:0.##}|{r.Istoch:0.##}|{r.Saxg:0.##}|{r.Isto:0.##}");
            // —— EA ——
            var isio = repo.SearchIsiosmAsync("RN26A", today, today, dept, cp, dpmusem).GetAwaiter().GetResult();
            foreach (var r in isio)
                msgs.Add($"ISIO|{r.Per}|{r.No}|{r.Date:yyyy-MM-dd}|{r.Mark}|{r.Jqty:0.##}|{r.Cqty:0.##}|{r.Amount:0.##}|{r.Note}|{r.Iprc:0.##}|{r.结存金额:0.##}|{r.Depotno}|{r.Iprj:0.##}|{r.外币价金额:0.##}|{r.Posi}");
        }

        Assert.True(legacyMsgs.Count > 0);
        Assert.Equal(legacyMsgs, modernMsgs);
    }

    /// <summary>
    /// 场景 27：A 组基础资料写路径（第十二批，手动快照流程，结果 diff）。
    /// A1 属性库（INSERT 触发器回填 cxnno/xno/tno + tbname0 同步 DM / 改码 updatenno+EDITREC / 通用码 max+1+EDITREC）/
    /// A2 tbname0（DM 源=name1）/ AA tbdccoef / A7 low_rate / A8 字典 CRUD+参数 findrecord。
    /// </summary>
    [Fact]
    public void Scenario27_AttrBasic_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var dm = new DmService();
        var prnotyRepo = new PrnotyRepository(factory, dm);
        var nameRepo = new NameCodeRepository(factory, dm);
        var dcRepo = new DcCoefRepository(factory);
        var complexRepo = new ComplexRepository(factory);
        var sysRepo = new SysDefineRepository(factory);
        var dept = ReplayConfig.Dept;
        var cp = "回放厂牌R";

        var legacyTables = new Dictionary<string, List<Dictionary<string, object>>>();
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            LegacyReplay.SeedAttrGroup(conn, dept, cp, DateTime.Today);
            var msgs = LegacyReplay.AttrPrnotyLegacy(conn, dept, cp, DateTime.Today);
            msgs.AddRange(LegacyReplay.AttrBasicLegacy(conn, dept, DateTime.Today));
            foreach (var m in msgs) Assert.DoesNotContain("NULL", m);
        }
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            legacyTables["tbprnoty"] = ReplayEnv.SnapshotTable(conn, "tbprnoty", "id", "rtrim(nno) LIKE 'RN27%'");
            legacyTables["tbname0"] = ReplayEnv.SnapshotTable(conn, "tbname0", "id", "rtrim(name) LIKE N'回放%'");
            legacyTables["tbisto"] = ReplayEnv.SnapshotTable(conn, "tbisto", "id", "rtrim(nno) LIKE 'RN27%'");
            legacyTables["tborder"] = ReplayEnv.SnapshotTable(conn, "tborder", "id", "rtrim(nno) LIKE 'RN27%'");
            legacyTables["tbdccoef"] = ReplayEnv.SnapshotTable(conn, "tbdccoef", "dc");
            legacyTables["tbcomplex"] = ReplayEnv.SnapshotTable(conn, "tbcomplex", "code_ts", "rtrim(code_ts) = 'RN27TS01'");
            legacyTables["tbsysset"] = ReplayEnv.SnapshotTable(conn, "tbsysset", "id", "rtrim(name) LIKE N'回放%'");
        }

        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            LegacyReplay.SeedAttrGroup(conn, dept, cp, DateTime.Today);
            // A1 add（RN27NEW）+ tbname0 同步（cxnno 传入=旧契约 24 列；触发器按厂牌位串重算——回放厂牌无 cp 行保留传入值）
            prnotyRepo.SaveAsync(new PrnotySaveCommand
            {
                IsAdd = true, Cp = cp, Nno = "RN27NEW", Cxnno = "RN27NEWCX", Na1 = "回放配件NEW", Ty = "回放车型", Fa = "回放产地", Dc = "A3",
                Unit = "个", Iprj = 70, Code_ts = "RN27TS01", Eng = "ENGNEW", Uqty = 2, Nvol = 3, Suttle = 3.5m,
                Note = "新增备注", Note1 = "新增备注1", Oprc_cp = 92, Oprc_min = 85,
            }, cp, "A").GetAwaiter().GetResult();
            // A1 edit 改码 RN27A→RN27A2（updatenno + EditAll；xno/tno 带触发器回填原值=旧码 SET xno=xno 语义）
            var editId = conn.ExecuteScalar<long>("SELECT id FROM tbprnoty WHERE rtrim(nno)='RN27A' AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) });
            var orig = conn.QuerySingle<(int Xno, int Tno)>(
                "SELECT ISNULL(xno,0), ISNULL(tno,0) FROM tbprnoty WHERE id=@id", new { id = editId });
            prnotyRepo.SaveAsync(new PrnotySaveCommand
            {
                IsAdd = false, Id = editId, Cp = cp, Nno = "RN27A2", OldNno = "RN27A", Cxnno = "", Na1 = "回放配件A改", Ty = "回放车型改",
                Fa = "回放产改", Dc = "A4", Unit = "箱", Iprj = 66, Code_ts = "RN27TS01", Eng = "ENGA2", Uqty = 1,
                Xno = orig.Xno, Tno = orig.Tno,
                Nvol = 4, Suttle = 4.4m, Note = "改备注", Note1 = "改备注1", Oprc_cp = 96, Oprc_min = 90,
            }, cp, "A").GetAwaiter().GetResult();
            // A1 通用码 max+1 + EditAll
            prnotyRepo.AssignCodeAsync(editId, isTno: false, cp, "A").GetAwaiter().GetResult();
            // A2 add（DM 源=name1 回放名称R）/ edit（name1=回放别名R → code=DM）
            nameRepo.SaveAsync(true, 0, "回放名称R", "回放名称R", null, null).GetAwaiter().GetResult();
            var nameId = conn.ExecuteScalar<int>("SELECT id FROM tbname0 WHERE rtrim(name)=N'回放名称R'");
            nameRepo.SaveAsync(false, nameId, "回放名称R", "回放别名R", null, null).GetAwaiter().GetResult();
            // AA add + edit
            dcRepo.SaveAsync(true, "RN27", 1.5m).GetAwaiter().GetResult();
            dcRepo.SaveAsync(false, "RN27", 2.5m).GetAwaiter().GetResult();
            // A7 low_rate
            complexRepo.UpdateLowRateAsync("RN27TS01", 0.15m).GetAwaiter().GetResult();
            // A8 字典 add→edit→delete；参数 findrecord（缺 xtype 行补建）+ valuec
            var dictId = conn.ExecuteScalar<int>(
                "INSERT INTO tbsysset (name, type, valuen, valuec, dpm) VALUES (N'回放单位R', 'unit', 0, N'', @dpm); SELECT CAST(SCOPE_IDENTITY() AS int);",
                new { dpm = Ansi(dept, 10) });
            sysRepo.SaveDictAsync(false, dictId, "unit", "回放单位R2", "回放", 1, dept).GetAwaiter().GetResult();
            sysRepo.DeleteDictAsync(dictId).GetAwaiter().GetResult();
            conn.Execute("INSERT INTO tbsysset (name, type, dpm) VALUES (N'回放参数R', 'system', @dpm)", new { dpm = Ansi(dept, 10) });
            var paras = sysRepo.GetParasAsync(new List<(string, string, char, string, string)> { ("回放参数R", "system", 'C', "", "汽配") }, dept).GetAwaiter().GetResult();
            sysRepo.SaveParasAsync(paras.Select(p => (p.Id, 'C', "", (string?)"回放值", (decimal?)null, (bool?)null)).ToList()).GetAwaiter().GetResult();
        }
        var modernTables = new Dictionary<string, List<Dictionary<string, object>>>();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            modernTables["tbprnoty"] = ReplayEnv.SnapshotTable(conn, "tbprnoty", "id", "rtrim(nno) LIKE 'RN27%'");
            modernTables["tbname0"] = ReplayEnv.SnapshotTable(conn, "tbname0", "id", "rtrim(name) LIKE N'回放%'");
            modernTables["tbisto"] = ReplayEnv.SnapshotTable(conn, "tbisto", "id", "rtrim(nno) LIKE 'RN27%'");
            modernTables["tborder"] = ReplayEnv.SnapshotTable(conn, "tborder", "id", "rtrim(nno) LIKE 'RN27%'");
            modernTables["tbdccoef"] = ReplayEnv.SnapshotTable(conn, "tbdccoef", "dc");
            modernTables["tbcomplex"] = ReplayEnv.SnapshotTable(conn, "tbcomplex", "code_ts", "rtrim(code_ts) = 'RN27TS01'");
            modernTables["tbsysset"] = ReplayEnv.SnapshotTable(conn, "tbsysset", "id", "rtrim(name) LIKE N'回放%'");
        }

        foreach (var key in legacyTables.Keys)
        {
            var diffs = ReplayEnv.Diff(legacyTables[key], modernTables[key], key, key == "tbdccoef" ? new[] { "dc" } : key == "tbcomplex" ? new[] { "code_ts" } : new[] { "id" });
            Assert.True(diffs.Count == 0, $"表 {key} 不一致：{string.Join("; ", diffs.Take(4))}");
        }
    }

    /// <summary>
    /// 场景 28：A3 属性传递（tbprnoty→四目标表，价格 CASE）+ A4 组套/拆套（FIFO+加权价；拆套回库不带成本）+ A1 传递到库存。
    /// </summary>
    [Fact]
    public void Scenario28_AttrTransferKit_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var dm = new DmService();
        var prnotyRepo = new PrnotyRepository(factory, dm);
        var attrRepo = new AttrTransferRepository(factory);
        var kitRepo = new KitRepository(factory, dm);
        var dept = ReplayConfig.Dept;
        var cp = "回放厂牌R";

        var legacyTables = new Dictionary<string, List<Dictionary<string, object>>>();
        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            LegacyReplay.SeedAttrGroup(conn, dept, cp, DateTime.Today);
            var msgs = LegacyReplay.AttrTransferLegacy(conn, dept, cp);
            msgs.AddRange(LegacyReplay.AttrKitLegacy(conn, dept, cp, DateTime.Today));
        }
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            legacyTables["tbisto"] = ReplayEnv.SnapshotTable(conn, "tbisto", "id", "rtrim(nno) LIKE 'RN27%' OR rtrim(nno)='T-RN27'");
            legacyTables["tbprtj"] = ReplayEnv.SnapshotTable(conn, "tbprtj", "id", "rtrim(tj) LIKE 'T-RN27%'");
        }

        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            LegacyReplay.SeedAttrGroup(conn, dept, cp, DateTime.Today);
            // A3 属性传递（普通字段 na1/ty/dc + 价格 iprj CASE）
            attrRepo.TransferAsync(new[] { "na1", "ty", "dc", "iprj" }, new[] { "isto", "istosm", "order", "sada" }, dept)
                .GetAwaiter().GetResult();
            // A4 定义链（头同步 tbprnoty/tbname0 + 组件 + 套件价回写）
            kitRepo.SaveHeadAsync(new KitHeadCommand
            { IsAdd = true, Tj = "T-RN27", Cp = cp, Na1 = "回放套件", Ty = "回放车型", Fa = "回放产地", Dc = "A5", Unit = "套", Note = "回放套件备注" },
                dept).GetAwaiter().GetResult();
            kitRepo.SaveLineAsync(new KitLineCommand
            { IsAdd = true, Tj = "T-RN27", Nno = "RN27B", Cp = cp, Na1 = "回放配件B", Ty = "回放车型", Fa = "回放产地", Dc = "A2", Unit = "个", Num = 2, Iprj = 40 },
                cp).GetAwaiter().GetResult();
            // A4 组套 3 套（FIFO 扣 RN27B×6 → INSERT T-RN27 加权价）+ 拆套 1 套（回库不带成本）
            kitRepo.AssembleAsync("T-RN27", "RN27B", 3m, cp, null, dept, "A").GetAwaiter().GetResult();
            kitRepo.DisassembleAsync("T-RN27", 1m, cp, null, dept, "A").GetAwaiter().GetResult();
            // A1 属性传递到库存（tbprnoty → tbisto 无行 INSERT/有行 UPDATE）
            var ids = conn.Query<long>("SELECT id FROM tbprnoty WHERE rtrim(nno)='RN27B' AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) }).AsList();
            prnotyRepo.TransferToStockAsync(ids, dept, cp, "A").GetAwaiter().GetResult();
        }
        var modernTables = new Dictionary<string, List<Dictionary<string, object>>>();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            modernTables["tbisto"] = ReplayEnv.SnapshotTable(conn, "tbisto", "id", "rtrim(nno) LIKE 'RN27%' OR rtrim(nno)='T-RN27'");
            modernTables["tbprtj"] = ReplayEnv.SnapshotTable(conn, "tbprtj", "id", "rtrim(tj) LIKE 'T-RN27%'");
        }

        foreach (var key in legacyTables.Keys)
        {
            var diffs = ReplayEnv.Diff(legacyTables[key], modernTables[key], key, new[] { "id" });
            Assert.True(diffs.Count == 0, $"表 {key} 不一致：{string.Join("; ", diffs.Take(4))}");
        }
    }

    /// <summary>
    /// 场景 29：fr_mdefi 模式②系统表聚合（E 组备案补齐批，手动快照流程，结果集 + SQL 拼串 diff）。
    /// 变体1 max+sum → GROUP BY cp,nno（M_CG=.T.）；变体2 含原值 → 无 GROUP BY 全表行（M_CG=.F.）。
    /// 新侧 AggregateSysTableAsync 拼串必须与旧 PASSTH 逐字符一致（CFSQL 消息 diff）。
    /// </summary>
    [Fact]
    public void Scenario29_CustomFieldAggregate_LegacyVsModern()
    {
        var factory = new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString));
        var repo = new UniversalQueryRepository(factory);
        var dept = ReplayConfig.Dept;
        var cp = "回放厂牌R";

        var legacyMsgs = new List<string>();
        var modernMsgs = new List<string>();

        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            LegacyReplay.SeedCustomFieldAggregate(conn, dept, cp);
            legacyMsgs.AddRange(LegacyReplay.CustomFieldAggregateLegacy(conn));
        }

        ReplayEnv.ResetToSnapshot();
        using (var conn = ReplayEnv.Open())
        {
            conn.Open();
            LegacyReplay.SeedCustomFieldAggregate(conn, dept, cp);
            var keyCols = "cp, nno";
            // 变体1：max+sum（M_CPNNO=2 → cp,nno 键）；SQL 拼串回显在前（与旧侧同序）
            var items1 = new List<AggregateItem>
            {
                new("na1", "零件名", 'C', 2, "回放聚合1"),
                new("iprj", "外币价", 'N', 4, "回放聚合2"),
            };
            var selectCols = keyCols + ", " + string.Join(", ", items1.Select(i => $"{i.AggExpression} as {i.NewName}"));
            modernMsgs.Add("CFSQL|SELECT " + selectCols + " FROM tbistosm group by cp, nno");
            var aggRows = repo.AggregateSysTableAsync("tbistosm", items1, hasCpKey: true, hasNnoKey: true).GetAwaiter().GetResult();
            foreach (var r in aggRows)
                modernMsgs.Add($"CFAGG|{r.GetValueOrDefault("cp")?.ToString()?.TrimEnd()}|{r.GetValueOrDefault("nno")?.ToString()?.TrimEnd()}|" +
                               $"{r.GetValueOrDefault("回放聚合1")?.ToString()?.TrimEnd()}|{Convert.ToDecimal(r.GetValueOrDefault("回放聚合2")):0.##}");
            // 变体2：原值 → 无 GROUP BY（M_CG=.F.）
            var items2 = new List<AggregateItem> { new("na1", "零件名", 'C', 1, "回放原值1") };
            var selectCols2 = keyCols + ", " + string.Join(", ", items2.Select(i => $"{i.AggExpression} as {i.NewName}"));
            modernMsgs.Add("CFSQL|SELECT " + selectCols2 + " FROM tbistosm");
            var rawRows = repo.AggregateSysTableAsync("tbistosm", items2,
                hasCpKey: true, hasNnoKey: true).GetAwaiter().GetResult();
            foreach (var r in rawRows)
                modernMsgs.Add($"CFRAW|{r.GetValueOrDefault("cp")?.ToString()?.TrimEnd()}|{r.GetValueOrDefault("nno")?.ToString()?.TrimEnd()}|" +
                               $"{r.GetValueOrDefault("回放原值1")?.ToString()?.TrimEnd()}");
        }

        Assert.True(legacyMsgs.Count > 0);
        Assert.Equal(legacyMsgs, modernMsgs);
    }

    /// <summary>
    /// 场景 30：C5 报价全链路（报价开单 → 确认 → 报价转销售 → 销售转报价），含触发器库存断言。
    /// 语义点：
    ///   - 报价开单：有库存行 kcid=批次 id 但触发器豁免扣减（type='报价' 不在 sale_in 过滤集）→ 断言 tbisto 不变；
    ///   - 确认（row298 FRMOK）：9 列 UPDATE + header upsert；报价 jzdate=outdate（挂账 1900 语义仅销售）；
    ///   - 报价转销售（BJTOXS 推断备案）：INSERT SELECT type='销售'，kcid 原样 → sale_in 扣库存；
    ///   - 销售转报价（row53 销售分支）：INSERT SELECT（急件恢复）+ DELETE 原单 → sale_del 回补，库存回到基准；
    /// 全程两侧产出 4 张单据（报价 AJ1/销售 XS/报价 AJ2 不可预测——用 per 窗口采集，papers 同步核对）。
    /// </summary>
    [Fact]
    public void Scenario30_QuoteLifecycle_LegacyVsModern()
    {
        var provider = new StaticCs(ReplayConfig.ConnectionString);
        var factory = new SqlConnectionFactory(provider);
        var sale = new SaleRepository(factory, new DocNoService(factory));
        var per = ReplayConfig.Operator;

        List<LegacyReplay.Batch> seed = [];
        List<string> legacyPapers = [];
        decimal qty = 0;

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            qty = seed[0].Kc;   // 整批数量：单批单行（两侧凑批一致）
            var quote1 = LegacyReplay.Quote(conn, seed[0], qty, per);          // 报价开单（不扣库存）
            LegacyReplay.ConfirmQuote(conn, quote1, DateTime.Today);           // 确认
            var sale1 = LegacyReplay.QuoteToSale(conn, quote1);                // 报价转销售（sale_in 扣库存）
            var quote2 = LegacyReplay.SaleToQuote(conn, sale1);                // 销售转报价（sale_del 回补）
            legacyPapers = [quote1, sale1, quote2];
            return [quote1, sale1, quote2];
        });

        var modern = Run(() =>
        {
            var quote1 = sale.CreateAsync(new CreateSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                DocType = "报价",
                Lines = [new SaleLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Qty = qty }],
            }, per).GetAwaiter().GetResult().Paper;

            sale.ConfirmAsync(new ConfirmSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Paper = quote1,
                DocType = "报价",
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                Guno = "现金",
                Outdate = DateTime.Today,
            }, per).GetAwaiter().GetResult();

            var conv = sale.ConvertQuoteToSaleAsync(new ConvertQuoteCommand
            {
                Dept = ReplayConfig.Dept,
                Paper = quote1,
                Option = 1,
            }, per).GetAwaiter().GetResult();
            Assert.True(conv.ConvertedLines == 1 && conv.SkippedLines == 0,
                $"转单行数异常：converted={conv.ConvertedLines} skipped={conv.SkippedLines} newPaper={conv.NewPaper}");

            var back = sale.ConvertSaleToQuoteAsync(conv.NewPaper, ReplayConfig.Dept, per,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, back.LineCount);

            return new List<string> { quote1, conv.NewPaper, back.NewPaper };
        });

        AssertIdentical(legacy, modern);
    }

    /// <summary>
    /// 场景 35：C5 报价单打印取数（PrintRepository type 参数化，第十五批交付）。
    /// 旧系统语义：fr_sale __.row46 L30 `type=THISFORMSET.FLAG` —— 销售/报价/预售同票样 re_sada，
    /// 取数仅按 type 过滤。验证：报价单（已确认）经 GetSalePrintDataAsync(type='报价') 可取出
    /// 票样数据（行数/合计/单号），且 type='销售' 查报价单号报 PRINT_DOC_EMPTY。
    /// </summary>
    [Fact]
    public void Scenario35_QuotePrintData()
    {
        var provider = new StaticCs(ReplayConfig.ConnectionString);
        var factory = new SqlConnectionFactory(provider);
        var sale = new SaleRepository(factory, new DocNoService(factory));
        var print = new PrintRepository(factory);
        var per = ReplayConfig.Operator;

        ReplayEnv.EnsureSnapshot();
        ReplayEnv.ResetToSnapshot();
        List<LegacyReplay.Batch> seed;
        string quote1;
        using (var conn = ReplayEnv.Open())
        {
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            var qty = seed[0].Kc;
            quote1 = sale.CreateAsync(new CreateSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                DocType = "报价",
                Lines = [new SaleLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Qty = qty }],
            }, per).GetAwaiter().GetResult().Paper;
            sale.ConfirmAsync(new ConfirmSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Paper = quote1,
                DocType = "报价",
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                Guno = "现金",
                Outdate = DateTime.Today,
            }, per).GetAwaiter().GetResult();
        }
        SqlConnection.ClearAllPools();

        using (var conn2 = ReplayEnv.Open())
        {
            conn2.Open();
            var doc = print.GetSalePrintDataAsync(quote1, "报价").GetAwaiter().GetResult();
            Assert.Equal(quote1, doc.Paper);
            Assert.Single(doc.Lines);
            Assert.Equal(seed[0].Kc, doc.Lines[0].Qty);
            Assert.Equal(seed[0].Kc * seed[0].Oprc, doc.TotalAmount);
            Assert.Equal(doc.TotalAmount - doc.Discount, doc.FinalAmount);

            // type 不匹配 → 无可打印明细（旧系统同语句查不到行）
            var ex = Assert.Throws<AppException>(() =>
                print.GetSalePrintDataAsync(quote1, "销售").GetAwaiter().GetResult());
            Assert.Equal("PRINT_DOC_EMPTY", ex.Code);
        }
        SqlConnection.ClearAllPools();
    }

    /// <summary>
    /// 场景 31：C2 预售互转（销售开单 → 销售转预售 → 预售转销售）。旧=row53 UPDATE 路径重放
    /// （SaleToPresale/PresaleToSale）；新=SaleRepository.ConvertSaleToPresaleAsync/ConvertPresaleToSaleAsync。
    /// 语义点：UPDATE 不触发 sale_in/sale_del → tbisto 零变动（tbisto 全表窗口兜底）；
    /// note='销售转预售' / '预售转销售'+原outdate；ypaper=原单号；header 换号。
    /// </summary>
    [Fact]
    public void Scenario31_PresaleConvert_LegacyVsModern()
    {
        var (sale, _) = NewRepos();
        var per = ReplayConfig.Operator;

        List<LegacyReplay.Batch> seed = [];
        decimal qty = 0;

        var legacy = Run(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            qty = Math.Min(1m, seed[0].Kc);
            var paper = LegacyReplay.Sale(conn, new List<(LegacyReplay.Batch, decimal)> { (seed[0], qty) }, per);
            var presale = LegacyReplay.SaleToPresale(conn, paper, ReplayConfig.Dept);   // 销售转预售
            var back = LegacyReplay.PresaleToSale(conn, presale, ReplayConfig.Dept);    // 预售转销售
            return [paper, presale, back];
        });

        var modern = Run(() =>
        {
            var paper = sale.CreateAsync(new CreateSaleCommand
            {
                Dept = ReplayConfig.Dept,
                Gno = ReplayConfig.Gno,
                Gname = ReplayConfig.Gname,
                Lines = [new SaleLineInput { Cp = seed[0].Cp, Nno = seed[0].Nno, Qty = qty }],
            }, per).GetAwaiter().GetResult().Paper;

            var presale = sale.ConvertSaleToPresaleAsync(paper, ReplayConfig.Dept, per,
                CancellationToken.None).GetAwaiter().GetResult().NewPaper;
            var back = sale.ConvertPresaleToSaleAsync(presale, ReplayConfig.Dept, per,
                CancellationToken.None).GetAwaiter().GetResult().NewPaper;
            return new List<string> { paper, presale, back };
        });

        AssertIdentical(legacy, modern);
    }

    /// <summary>
    /// 场景 32：库存分货（fr_kcfh row23 两模式）。种子订单（gyname=部门名命中数据源）→
    /// 模式1 按需求（MIN(库存,缺口)）分订单1 → 模式2 按比例（fhamount）分订单2。
    /// 旧=LegacyStockDistribute 游标复刻；新=StockDistributeRepository.DistributeAsync。
    /// 断言：八表 + tborder（qty 累计不受影响但行被 UPDLOCK 读过）+ tbdjgs（DJLSH 流水）。
    /// INSERT tbsada type='调拨' 即时触发 sale_in 扣 tbisto（tbisto 全表窗口验证）。
    /// </summary>
    [Fact]
    public void Scenario32_StockDistribute_LegacyVsModern()
    {
        var repo = new StockDistributeRepository(
            new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)));
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        const string cno1 = "RPCNO32A", cno2 = "RPCNO32B";
        List<LegacyReplay.Batch> parts = [];

        void Seed(SqlConnection conn)
        {
            parts = LegacyReplay.GetSeedParts(conn, 2);
            // gyname=部门名（数据源 o.gyname=?p_dpmname）；gname=回放客户（tbsada gname 窗口一致）
            LegacyReplay.SeedStockDistributeOrders(conn, dept, ReplayConfig.Gname,
                [(parts[0], 3m, ReplayConfig.Gname, 1)], cno1);
            LegacyReplay.SeedStockDistributeOrders(conn, dept, ReplayConfig.Gname,
                [(parts[1], 3m, ReplayConfig.Gname, 2)], cno2);
        }

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            Seed(conn);
            var p1 = LegacyReplay.LegacyStockDistribute(conn, dept, ReplayConfig.Gname, mode: 1,
                "iprc", 1m, roundMode: 1, [(ReplayConfig.Gname, cno1)]);
            var p2 = LegacyReplay.LegacyStockDistribute(conn, dept, ReplayConfig.Gname, mode: 2,
                "iprc", 1m, roundMode: 1, [(ReplayConfig.Gname, cno2)]);
            return p1.Concat(p2).ToList();
        });

        var modern = RunEx(() =>
        {
            using (var conn = ReplayEnv.Open())
                Seed(conn);
            var r1 = repo.DistributeAsync(new StockDistributeCommand
            {
                Mode = 1, RoundMode = 1, OprcField = "iprc", OprcFactor = 1m,
                Orders = [new StockDistributeOrderRef(ReplayConfig.Gname, cno1)],
            }, dept, dept, ReplayConfig.Gname, per).GetAwaiter().GetResult();
            var r2 = repo.DistributeAsync(new StockDistributeCommand
            {
                Mode = 2, RoundMode = 1, OprcField = "iprc", OprcFactor = 1m,
                Orders = [new StockDistributeOrderRef(ReplayConfig.Gname, cno2)],
            }, dept, dept, ReplayConfig.Gname, per).GetAwaiter().GetResult();
            return r1.Papers.Concat(r2.Papers).ToList();
        });

        AssertIdentical(legacy.Tables, modern.Tables);
        var lc = $"'{cno1}','{cno2}'";
        AssertExtraIdentical("tborder", "id", $"cno IN ({lc})", $"cno IN ({lc})", ["id"]);
        AssertExtraIdentical("tbdjgs", "id", "djname IN (N'调拨单')", "djname IN (N'调拨单')", ["id"]);
    }

    /// <summary>
    /// 场景 33：船单分货（fr_isor row103）。种子订单（无客户+代订客户两行）→ 整单导入 → 分货（按订货时间）。
    /// 旧=OrderInDistribute 重放；新=OrderInRepository.DistributeAsync(ByOrderDate)。
    /// 断言：八表 + tborder（qty 不变）+ tbisorsm（seano ∪ seano_1，分货结果行）。
    /// </summary>
    [Fact]
    public void Scenario33_OrderInDistribute_LegacyVsModern()
    {
        var orderRepo = new OrderInRepository(
            new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)),
            new DocNoService(new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString))),
            new DmService());
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        const string seano = "REPLAYSEANO33", cno = "REPLAYCNO33";
        List<LegacyReplay.Batch> parts = [];

        void Seed(SqlConnection conn)
        {
            parts = LegacyReplay.GetSeedParts(conn, 2);
            LegacyReplay.SeedOrderIn(conn, dept,
                [(parts[0], 10m, "", 1), (parts[1], 5m, ReplayConfig.Gname, 2)], cno);
        }

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            Seed(conn);
            LegacyReplay.OrderInImport(conn, dept, parts[0].Cp, per, seano, cno, 1m, 1.3m, 1.2m, wbtohs: true, pFpcw: "废");
            var newSeano = LegacyReplay.OrderInDistribute(conn, dept, parts[0].Cp, per, seano, mode: 1);
            return new List<string> { newSeano };
        }, cnoWindow: cno);

        var modern = RunEx(() =>
        {
            using (var conn = ReplayEnv.Open())
                Seed(conn);
            orderRepo.ImportWholeOrderAsync(new ImportOrderInCommand { Seano = seano, Cno = cno },
                parts[0].Cp, dept, per, "Z", "废", 1m, 1.3m, 1.2m, wbtohs: true).GetAwaiter().GetResult();
            var newSeano = orderRepo.DistributeAsync(seano, OrderInDistributeMode.ByOrderDate,
                parts[0].Cp, dept, per).GetAwaiter().GetResult();
            return new List<string> { newSeano! };
        }, cnoWindow: cno);

        AssertIdentical(legacy.Tables, modern.Tables);
        AssertExtraIdentical("tborder", "id", $"cno = '{cno}'", $"cno = '{cno}'", ["id"]);
        AssertExtraIdentical("tbisorsm", "id", $"seano IN ('{seano}', '{seano}_1')",
            $"seano IN ('{seano}', '{seano}_1')", ["id"]);
    }

    /// <summary>
    /// 场景 34：C9 调拨入库确认（fr_dbrk row7 + __.row68 挂帐段）。种子调拨单（Transfer+mark='Y'+guno='挂帐'）→
    /// 确认：仓位空拦截 → 负库存预演 → pty/pfa 回写 → exec dbrk（SP 写 tbisto/tbistoed，cnoWindow=SP 生成进库单号）
    /// → tbysyf 挂帐行（dpmname=回放客户 → gname 窗口命中）。
    /// 旧=DbrkConfirm 重放；新=DbrkRepository.ConfirmAsync。Client 两侧同源（SP 售价比率分支契约一致）。
    /// </summary>
    [Fact]
    public void Scenario34_DbrkConfirm_LegacyVsModern()
    {
        var repo = new DbrkRepository(
            new SqlConnectionFactory(new StaticCs(ReplayConfig.ConnectionString)));
        var per = ReplayConfig.Operator;
        var dept = ReplayConfig.Dept;
        const string client = "REPLAY";

        List<LegacyReplay.Batch> seed = [];

        var legacy = RunEx(() =>
        {
            using var conn = ReplayEnv.Open();
            seed = LegacyReplay.GetSeedBatches(conn, 1);
            var tp = LegacyReplay.Transfer(conn, seed[0], seed[0].Kc, per, "回放调拨入库");
            conn.Execute($"UPDATE tbsada SET mark = 'Y', guno = N'挂帐' WHERE paper = '{tp.Replace("'", "")}'");
            var newPaper = LegacyReplay.DbrkConfirm(conn, dept, per, tp, client, ReplayConfig.Gname);
            return new List<string> { tp, newPaper };
        }, cnoWindow: null);

        var modern = RunEx(() =>
        {
            string tp;
            using (var conn = ReplayEnv.Open())
            {
                seed = LegacyReplay.GetSeedBatches(conn, 1);
                tp = LegacyReplay.Transfer(conn, seed[0], seed[0].Kc, per, "回放调拨入库");
                conn.Execute($"UPDATE tbsada SET mark = 'Y', guno = N'挂帐' WHERE paper = '{tp.Replace("'", "")}'");
            }
            var r = repo.ConfirmAsync(new DbrkConfirmCommand
            {
                Papers = [tp],
                Client = client,
            }, dept, ReplayConfig.Gname, per).GetAwaiter().GetResult();
            return new List<string> { tp, r.NewPaper };
        }, cnoWindow: null);

        AssertIdentical(legacy.Tables, modern.Tables);
    }
}
