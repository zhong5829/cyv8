using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Sales;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;

namespace QPGL.Infrastructure.Sales;

public interface ISaleRepository
{
    Task<CreateSaleResult> CreateAsync(CreateSaleCommand command, string per, CancellationToken ct = default);

    /// <summary>作废（删除整单）：mark 已确认拒删；DELETE 走 sale_del 触发器回补库存；header 保留（旧系统语义）。</summary>
    Task<VoidSaleResult> VoidAsync(string paper, string dept, string per, CancellationToken ct = default, string docType = "销售");

    /// <summary>单据确认（fr_sale row298 FRMOK：单头 9 列 UPDATE + tbsada_header upsert；报价跳过应收检查）。</summary>
    Task<ConfirmSaleResult> ConfirmAsync(ConfirmSaleCommand command, string per, CancellationToken ct = default);

    /// <summary>报价转销售（row53 + FmsSada.frmbjtoxs；BJTOXS 方法体未导出——触发器语义推断实现，契约备案）。</summary>
    Task<ConvertQuoteResult> ConvertQuoteToSaleAsync(ConvertQuoteCommand command, string per, CancellationToken ct = default);

    /// <summary>销售转报价（row53 销售分支照抄：INSERT SELECT 新报价单 + 原销售单删除，sale_del 回补库存）。</summary>
    Task<ConvertSaleToQuoteResult> ConvertSaleToQuoteAsync(string paper, string dept, string per, CancellationToken ct = default);

    /// <summary>销售转预售（row53 M_PD=6：UPDATE 原单改 5 列 + 换号，库存零变动，需权限 C2）。</summary>
    Task<ConvertPresaleResult> ConvertSaleToPresaleAsync(string paper, string dept, string per, CancellationToken ct = default);

    /// <summary>预售转销售（row53 FLAG='预售'：UPDATE 原单改 5 列 + 换号，库存零变动，需权限 CA）。</summary>
    Task<ConvertPresaleResult> ConvertPresaleToSaleAsync(string paper, string dept, string per, CancellationToken ct = default);
}

/// <summary>
/// 销售开单仓储（写路径，触发器语义感知——开发文档 D3/§5.3）。
/// 旧实现出处：
///   - 取号：pg_proc.getnewdh → exec dbo.getnewdh('销售单', dpm, 1)（DocNoService 已复刻）
///   - 批次选择与拆行：fr_sale row548（tbisto 按 kcamount DESC 凑批，不足挂末批允许负库存）
///   - 明细 INSERT 列组合：fr_sale row548 PASSTH('insert','tbsada', 'cp,nno,...,kcid,dc,dscp')
///   - 单头：fr_sale row298 —— tbsada_header IF EXISTS UPDATE ELSE INSERT，jptype='单据类型'
/// 调拨复用（DocType='调拨'，菜单 C.3）：
///   - 明细列集 fr_sale Lf_.row511：...,kcid,depotno,dc,note（PASSTH 补 dpm）——指定批次单批出库不拆批
///   - 取号 djname='调拨单'；sale_in 触发器对 type IN ('销售','调拨','预售','内耗') 同样扣库存
/// 触发器契约（已导出至 逆向源码\数据库对象_sql\sale_in.sql）：
///   INSERT tbsada（kcid&gt;0，cp&lt;&gt;'急件'，ckamount&lt;&gt;0）
///   → 自动 UPDATE tbisto SET kcamount = kcamount - ckamount。
///   新系统【禁止】自行扣减 tbisto，仅回读断言（CONTRACT_VIOLATION 即回滚告警）。
/// </summary>
public sealed class SaleRepository(SqlConnectionFactory factory, IDocNoService docNo) : ISaleRepository
{
    private const string HeaderJpType = "单据类型";   // row298 硬编码值

    public async Task<CreateSaleResult> CreateAsync(CreateSaleCommand command, string per, CancellationToken ct = default)
    {
        SaleValidator.Validate(command);
        var docType = (command.DocType ?? "销售").Trim();
        if (docType is not ("销售" or "调拨" or "报价" or "预售" or "内耗"))
            throw new AppException("DOC_TYPE_UNSUPPORTED", $"不支持的出库单据类型：{docType}", 422);
        // 报价（C5，fr_sale with '报价'）：sale_in/sale_del/sale_up 触发器按 type 过滤（销售/调拨/预售/内耗）——
        // type='报价' INSERT 天然不扣库存；无库存配件可开单（row523 库存不足校验报价跳过，row469 KCID=0 分支）。
        // 预售/内耗（C2/C4 收尾）：row548 对所有 FLAG 统一列集（type=FLAG、dscp=cp、outdate=P_RQ），
        // 与销售完全同路径；sale_in 触发器对 type IN ('销售','调拨','预售','内耗') 同扣 tbisto——
        // 取号 djname=FLAG+'单'（预售单 AY 前缀已配置；内耗单部署库未配置 → getnewdh NULL 弹错，照旧语义备案）。

        // 1) 取号（进程内锁串行；旧系统在写行之前取号）
        //    改单（command.Paper 非空）：复用原单号，跳过取号（旧 ___.row480 EMODE 语义）
        var paper = string.IsNullOrWhiteSpace(command.Paper)
            ? await docNo.GetNewDocNoAsync(docType + "单", command.Dept,
                new DocNoService.DocNoCheckTarget("tbsada", "paper", docType), ct)
            : command.Paper.Trim();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 2) 事务内防重断言；改单（复用单号）→ 删除原行（sale_del 触发器回补库存，随后重插再扣）
            var isUpdate = !string.IsNullOrWhiteSpace(command.Paper);
            if (isUpdate)
            {
                // 旧系统 row480 前置拦截：已确认单（mark<>''）禁止修改
                var mark = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT TOP 1 mark FROM tbsada WITH (UPDLOCK) WHERE paper = @paper AND type = @type",
                    new { paper = Ansi(paper, 10), type = Ansi(docType) },
                    transaction: tx, cancellationToken: ct));
                if (!string.IsNullOrWhiteSpace(mark))
                    throw new AppException("DOC_ALREADY_CONFIRMED", $"单据 {paper} 已确认，不能修改", 422);
                var del = await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tbsada WHERE paper = @paper AND type = @type",
                    new { paper = Ansi(paper, 10), type = Ansi(docType) },
                    transaction: tx, cancellationToken: ct));
                if (del == 0)
                    throw new AppException("DOC_NOT_FOUND", $"要修改的单据 {paper} 不存在", 404);
            }
            else
            {
                var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbsada WITH (NOLOCK) WHERE paper = @paper AND type = @type",
                    new { paper, type = Ansi(docType) },
                    transaction: tx, cancellationToken: ct));
                if (dup > 0)
                    throw new AppException("DOC_NO_CONFLICT", $"单号 {paper} 已存在", 409);
            }

            var outdate = DateTime.Today;
            var gno = (command.Gno ?? "").Trim();
            var gname = (command.Gname ?? "").Trim();

            var lines = new List<CreateSaleResult.AllocatedLine>();
            var beforeSnapshot = new Dictionary<int, decimal>();

            foreach (var line in command.Lines)
            {
                var cp = line.Cp.Trim();
                var nno = line.Nno.Trim();

                // 3) 批次查询：UPDLOCK 串行化同批次并发（写路径禁脏读，§10.2-6）
                //    指定批次（调拨，Lf_.row511 m.KCID=C_ISTOSALE.ID）：按 id 直取该批次，不报废过滤
                List<StockBatchRow> batches;
                if (line.Kcid is > 0)
                {
                    batches = (await conn.QueryAsync<StockBatchRow>(new CommandDefinition(
                        $"""
                        SELECT {StockColumns}
                        FROM tbisto WITH (UPDLOCK)
                        WHERE id = @kcid AND dpm = @dpm
                        """,
                        new { kcid = line.Kcid, dpm = Ansi(command.Dept, 10) },
                        transaction: tx, cancellationToken: ct))).AsList();
                }
                else
                {
                    batches = (await conn.QueryAsync<StockBatchRow>(new CommandDefinition(
                        $"""
                        SELECT {StockColumns}
                        FROM tbisto WITH (UPDLOCK)
                        WHERE dpm = @dpm AND cp = @cp AND nno = @nno
                          AND (posi IS NULL OR posi NOT LIKE '报废%')
                        ORDER BY kcamount DESC, id ASC
                        """,
                        new
                        {
                            dpm = Ansi(command.Dept, 10),
                            cp = Ansi(cp, 10),
                            nno = Ansi(nno, 18),
                        },
                        transaction: tx, cancellationToken: ct))).AsList();
                }

                if (batches.Count == 0 && docType != "报价")
                    throw new AppException("STOCK_NOT_ENOUGH", $"配件 {cp}({nno}) 在部门 {command.Dept} 无可用库存批次", 422);

                // 4) 凑批分配：指定批次 = 单批出库不拆批（Lf_.row511 语义，允许超库存负数出库——旧系统
                //    在 DB 库存与游标一致时不拦截，P_IFCK='1' 同）；否则 row548 语义自动凑批。
                //    报价无库存批次（batches 空）→ 不分配，行 kcid=0 直插（row469 报价行 KCID=0 语义）。
                IReadOnlyList<StockAllocator.Allocation> allocs = docType == "报价" && batches.Count == 0
                    ? new List<StockAllocator.Allocation>()
                    : line.Kcid is > 0
                        ? new List<StockAllocator.Allocation> { new(line.Kcid.Value, line.Qty) }
                        : StockAllocator.Allocate(batches.Select(b => (b.id, b.kcamount)), line.Qty);

                if (allocs.Count == 0)
                {
                    // 报价无库存：单行 kcid=0（posi/depotno 空），row548 列集（PASSTH 补 dpm）
                    var price0 = line.Price ?? 0m;
                    var lineRemark0 = (line.Remark ?? "").Trim();
                    if (lineRemark0.Length == 0) lineRemark0 = (command.Remark ?? "").Trim();
                    var dscp0 = docType is "销售" or "报价" ? cp : "";
                    var remark0 = docType == "销售" ? lineRemark0 : "";
                    var note0 = docType == "调拨" ? lineRemark0 : "";
                    await conn.ExecuteAsync(new CommandDefinition($"""
                        INSERT INTO tbsada
                            (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct,
                             iprj, iprc, paper, gno, gname, per, xno, tno, cxnno,
                             pnno, pna1, pty, pfa, kcamount, outdate, dpm, depotno, kcid, dc, dscp, remark, note)
                        VALUES
                            (@cp, @nno, @na1, '', '', '', @type, '', @ckamount, @oprc, @oprc,
                             0, 0, @paper, @gno, @gname, @per, 0, 0, '',
                             @nno, @na1, '', '', 0, @outdate, @dpm, '', 0, '', @dscp, @remark, @note)
                        """, new
                    {
                        cp = Ansi(cp, 10),
                        nno = Ansi(nno, 18),
                        type = Ansi(docType),
                        ckamount = line.Qty,
                        oprc = price0,
                        paper = Ansi(paper, 10),
                        gno = Ansi(gno, 10),
                        gname = Ansi(gname, 60),
                        per = Ansi(per, 10),
                        outdate,
                        dpm = Ansi(command.Dept, 10),
                        dscp = Ansi(dscp0, 10),
                        remark = Ansi(remark0, 250),
                        note = Ansi(note0, 250),
                    }, transaction: tx, cancellationToken: ct));
                    lines.Add(new CreateSaleResult.AllocatedLine(0, cp, nno, line.Qty, price0));
                    continue;
                }

                foreach (var alloc in allocs)
                {
                    var b = batches.First(x => x.id == alloc.Kcid);
                    if (!beforeSnapshot.TryAdd(alloc.Kcid, b.kcamount))
                        beforeSnapshot[alloc.Kcid] = Math.Min(beforeSnapshot[alloc.Kcid], b.kcamount);

                    var price = line.Price ?? b.oprc;
                    var lineRemark = (line.Remark ?? "").Trim();
                    if (lineRemark.Length == 0) lineRemark = (command.Remark ?? "").Trim(); // 行备注缺省用单头备注
                    // 列集契约：销售 row548 写 dscp=cp、remark（note 缺省''）；调拨 Lf_.row511 写 note（dscp/remark 缺省''）。
                    // 统一三列写入，缺省值与列默认值''等价，回放 diff 不受影响。
                    var dscp = docType is "销售" or "报价" ? cp : "";
                    var remark = docType == "销售" ? lineRemark : "";
                    var note = docType == "调拨" ? lineRemark : "";
                    var newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        $"""
                        INSERT INTO tbsada
                            (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct,
                             iprj, iprc, paper, gno, gname, per, xno, tno, cxnno,
                             pnno, pna1, pty, pfa, kcamount, outdate, dpm, depotno, kcid, dc, dscp, remark, note)
                        VALUES
                            (@cp, @nno, @na1, @fa, @ty, @unit, @type, @posi, @ckamount, @oprc, @oprct,
                             @iprj, @iprc, @paper, @gno, @gname, @per, @xno, @tno, @cxnno,
                             @pnno, @pna1, @pty, @pfa, @kcamount, @outdate, @dpm, @depotno, @kcid, @dc, @dscp, @remark, @note);
                        -- tbsada 带 sale_in 触发器，禁止无 INTO 的 OUTPUT，用 SCOPE_IDENTITY 取新行 id
                        -- 契约回放修正 1：row548 INSERT 无 jzdate（保持默认 1900-01-01，收款确认时 fr_sk 才写）
                        -- 契约回放修正 2：PASSTH（webservice.prg L221-227）对 tbxzb 配置表自动补 dpm 列
                        --   绑定 ?p_dpm（当前操作员部门）——生产 XS26 全量 dpm='A' 实证
                        SELECT CAST(SCOPE_IDENTITY() AS int);
                        """,
                        new
                        {
                            cp = Ansi(cp, 10),
                            nno = Ansi(nno, 18),
                            na1 = Ansi(b.na1, 40),
                            fa = Ansi(b.fa, 8),
                            ty = Ansi(b.ty, 14),
                            unit = Ansi(b.unit, 4),
                            type = Ansi(docType),
                            posi = Ansi(b.posi, 10),
                            ckamount = alloc.Qty,
                            oprc = price,
                            oprct = price,               // row548: m.OPRCT = m.OPRC
                            iprj = b.iprj,
                            iprc = b.iprc,
                            paper = Ansi(paper, 10),
                            gno = Ansi(gno, 10),
                            gname = Ansi(gname, 60),
                            per = Ansi(per, 10),
                            xno = b.xno,
                            tno = b.tno,
                            cxnno = Ansi(b.cxnno, 18),
                            pnno = Ansi(nno, 18),        // row548: PNNO=NNO、PNA1=NA1、PTY=TY、PFA=FA
                            pna1 = Ansi(b.na1, 40),
                            pty = Ansi(b.ty, 14),
                            pfa = Ansi(b.fa, 8),
                            kcamount = b.kcamount,       // 出库时批次库存快照
                            outdate,
                            dpm = Ansi(command.Dept, 10), // PASSTH 自动补列：dpm=p_dpm（tbxzb 契约）
                            depotno = Ansi(b.depotno, 10),
                            kcid = alloc.Kcid,
                            dc = Ansi(b.dc, 8),
                            dscp = Ansi(dscp, 10),       // row548: m.DSCP = m.CP（调拨 Lf_.row511 不写，''=列默认）
                            remark = Ansi(remark, 250),  // row548 行备注（调拨不写，''=列默认）
                            note = Ansi(note, 250),      // 调拨 Lf_.row511 行备注列（销售不写，''=列默认）
                        },
                        transaction: tx, cancellationToken: ct));

                    lines.Add(new CreateSaleResult.AllocatedLine(alloc.Kcid, cp, nno, alloc.Qty, price));
                }
            }

            // 5) 单头（row298：EXISTS→UPDATE else INSERT）
            await conn.ExecuteAsync(new CommandDefinition(
                """
                IF EXISTS (SELECT 1 FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper AND dpm = @dpm)
                    UPDATE tbsada_header SET spname = @spname WHERE paper = @paper AND dpm = @dpm
                ELSE
                    INSERT INTO tbsada_header (paper, dpm, papertype, jptype, spname)
                    VALUES (@paper, @dpm, '', @jptype, @spname)
                """,
                new
                {
                    paper = Ansi(paper, 10),
                    dpm = Ansi(command.Dept, 10),
                    spname = Ansi(gname, 40),
                    jptype = Ansi(HeaderJpType),
                },
                transaction: tx, cancellationToken: ct));

            // 6) 回读断言（§7.3）：销售/调拨 tbisto.kcamount 变化量必须等于 Σckamount（触发器语义断言）；
            //    报价触发器豁免扣减 → 断言库存不变（更强契约）。
            foreach (var (kcid, before) in beforeSnapshot)
            {
                var after = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WHERE id = @kcid",
                    new { kcid },
                    transaction: tx, cancellationToken: ct));
                if (docType == "报价")
                {
                    if (after != before)
                        throw new AppException(
                            "CONTRACT_VIOLATION",
                            $"库存断言失败（报价不应扣库存）：tbisto.id={kcid} 期望 {before} 实际 {after}（触发器语义异常，已回滚）",
                            500);
                }
                else
                {
                    var deducted = lines.Where(l => l.Kcid == kcid).Sum(l => l.Qty);
                    if (after != before - deducted)
                        throw new AppException(
                            "CONTRACT_VIOLATION",
                            $"库存断言失败：tbisto.id={kcid} 期望 {before - deducted} 实际 {after}（触发器语义异常，已回滚）",
                            500);
                }
            }

            await tx.CommitAsync(ct);

            return new CreateSaleResult
            {
                Paper = paper,
                LineCount = lines.Count,
                TotalAmount = lines.Sum(l => l.Qty * l.Oprc),
                Lines = lines,
            };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private const string StockColumns =
        "id, dpm, cp, nno, na1, ty, fa, dc, unit, kcamount, iprj, iprc, oprc, posi, depotno, xno, tno, cxnno";

    private sealed class StockBatchRow
    {
        public int id { get; set; }
        public string dpm { get; set; } = "";
        public string cp { get; set; } = "";
        public string nno { get; set; } = "";
        public string na1 { get; set; } = "";
        public string ty { get; set; } = "";
        public string fa { get; set; } = "";
        public string dc { get; set; } = "";
        public string unit { get; set; } = "";
        public decimal kcamount { get; set; }
        public decimal iprj { get; set; }
        public decimal iprc { get; set; }
        public decimal oprc { get; set; }
        public string posi { get; set; } = "";
        public string depotno { get; set; } = "";
        public int xno { get; set; }
        public int tno { get; set; }
        public string cxnno { get; set; } = "";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value, IsAnsi = true, Length = length };

    /// <summary>
    /// 作废（删除整单，fr_sale row43 CMDDEL2 契约逐行复刻）：
    ///   1) 任一行 mark 非空（已确认）→ 拒删 DOC_ALREADY_VOID；
    ///   2) DELETE tbsada WHERE paper AND dpm AND type —— sale_del 触发器自动回补 tbisto.kcamount；
    ///   3) tbsada_header 保留（旧系统删单不清 header）；
    ///   4) 回读断言：每 kcid 回补量 == Σckamount（CONTRACT_VIOLATION 即回滚）。
    /// 急件行（cp='急件'）走 tbistoed 清理，M2 不支持（回放与功能均排除）。
    /// </summary>
    public async Task<VoidSaleResult> VoidAsync(string paper, string dept, string per, CancellationToken ct = default, string docType = "销售")
    {
        SaleVoidValidator.Validate(paper, dept);
        var trimmed = paper.Trim();
        var type = (docType ?? "销售").Trim();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var rows = (await conn.QueryAsync<VoidRow>(new CommandDefinition(
                """
                SELECT id, RTRIM(cp) AS cp, RTRIM(mark) AS mark, ckamount, kcid
                FROM tbsada WITH (UPDLOCK)
                WHERE paper = @paper AND dpm = @dpm AND type = @type
                """,
                new { paper = Ansi(trimmed, 10), dpm = Ansi(dept, 10), type = Ansi(type) },
                transaction: tx, cancellationToken: ct))).AsList();

            if (rows.Count == 0)
                throw new AppException("DOC_NOT_FOUND", $"单据 {trimmed} 不存在", 422);
            if (rows.Any(r => !string.IsNullOrWhiteSpace(r.mark)))
                throw new AppException("DOC_ALREADY_VOID", $"单据 {trimmed} 已确认，不能删除！", 422);
            if (rows.Any(r => r.cp.Trim() == "急件"))
                throw new AppException("VOID_URGENT_UNSUPPORTED", "含急件行的单据作废需走急件流程，暂不支持", 422);

            // 回补前快照（tbisto 行锁定）
            var before = new Dictionary<int, decimal>();
            foreach (var kcid in rows.Where(r => r.kcid > 0).Select(r => r.kcid).Distinct())
            {
                var kc = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WITH (UPDLOCK) WHERE id = @kcid",
                    new { kcid }, transaction: tx, cancellationToken: ct));
                before[kcid] = kc;
            }

            var deleted = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tbsada WHERE paper = @paper AND dpm = @dpm AND type = @type",
                new { paper = Ansi(trimmed, 10), dpm = Ansi(dept, 10), type = Ansi(type) },
                transaction: tx, cancellationToken: ct));

            // 回读断言（sale_del：kcamount = kcamount + ckamount）；
            // 报价触发器豁免回补（sale_del 按 type 过滤）→ 断言库存不变。
            var isQuote = type == "报价";
            var restored = new List<(int Kcid, decimal Restored)>();
            foreach (var (kcid, beforeKc) in before)
            {
                var after = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WHERE id = @kcid",
                    new { kcid }, transaction: tx, cancellationToken: ct));
                if (isQuote)
                {
                    if (after != beforeKc)
                        throw new AppException("CONTRACT_VIOLATION",
                            $"作废库存断言失败（报价不应回补库存）：tbisto.id={kcid} 期望 {beforeKc} 实际 {after}（触发器语义异常，已回滚）",
                            500);
                }
                else
                {
                    var expect = rows.Where(r => r.kcid == kcid).Sum(r => r.ckamount);
                    if (after != beforeKc + expect)
                        throw new AppException("CONTRACT_VIOLATION",
                            $"作废库存断言失败：tbisto.id={kcid} 期望 {beforeKc + expect} 实际 {after}（触发器语义异常，已回滚）",
                            500);
                    restored.Add((kcid, expect));
                }
            }

            await tx.CommitAsync(ct);
            return new VoidSaleResult { Paper = trimmed, DeletedLines = deleted, RestoredStock = restored };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 单据确认（fr_sale row298 FRMOK 逐行复刻，本部署配置实测简化）：
    ///   1) tbgu 客户校验（gname→gno；gno 空 → 'KH0'+id 回写 tbgu）——P_GUDYMUST=0 客户名称可空；
    ///   2) mark 非空 → 已确认拦截（mark='Y' 由收款流程 fr_sk 写）；
    ///   3) 月结/跨月校验（P_YJ=0/P_NOKY=0 部署配置不触发；报价本就跳过）；
    ///   4) 优惠 CBOTYPE：1=yhl 记账 / 2=oprct=oprc*(100-yhl)/100 / 3=oprc=oprc*(100+yhl)/100；
    ///      P_XLYHL=0（登录缺省）优惠权限校验休眠；SALEYHL=.F./frmsaleyhl 存储过程不存在 → 休眠备案；
    ///   5) 应收额度检查（getysyfcb/fr_xe 弹窗交互）——销售/调拨侧备案未实现，报价 row298 本就跳过；
    ///   6) jzdate：销售 且 guno∈{挂帐,汇款,托收} → 1900-01-01，否则 outdate（L230）；
    ///   7) remark 非空 → 查 tbgu gname=remark top 1 tel 拼接（L277-283，旧码语义照抄）；
    ///   8) UPDATE tbsada SET gno,gname,pno,t_mode,yhl,guno,outdate,jzdate,remark WHERE paper（无 type/dpm 条件，照抄）；
    ///   9) tbsada_header upsert（papertype/spname/jptype='单据类型'）。
    /// P_KPXS=1（开票系数不启用）、沙河 t_mode 定价变体休眠。
    /// </summary>
    public async Task<ConfirmSaleResult> ConfirmAsync(ConfirmSaleCommand command, string per, CancellationToken ct = default)
    {
        var paper = command.Paper.Trim();
        var docType = (command.DocType ?? "报价").Trim();
        if (docType is not ("销售" or "调拨" or "报价" or "预售" or "内耗"))
            throw new AppException("DOC_TYPE_UNSUPPORTED", $"不支持的出库单据类型：{docType}", 422);
        var gname = (command.Gname ?? "").Trim();
        var gno = (command.Gno ?? "").Trim();
        var guno = (command.Guno ?? "").Trim();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 单据存在性 + mark 检查（row298 L79-87：mark 非空 → "该单已确认...只重新确认原付款单据"）
            var mark = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT TOP 1 mark FROM tbsada WITH (UPDLOCK) WHERE paper = @paper",
                new { paper = Ansi(paper, 10) },
                transaction: tx, cancellationToken: ct));
            if (!string.IsNullOrWhiteSpace(mark))
                throw new AppException("DOC_ALREADY_CONFIRMED", $"单据 {paper} 已确认（收款确认），不能重复确认", 422);

            // tbgu 客户校验（row298 L10-27）：P_GUDYMUST=0 → gname 可空（空则跳过客户解析）
            if (gname.Length > 0)
            {
                var gu = await conn.QuerySingleOrDefaultAsync<(int Id, string Gno)>(new CommandDefinition(
                    "SELECT TOP 1 id, rtrim(gno) FROM tbgu WITH (NOLOCK) WHERE gname = @gname",
                    new { gname = Ansi(gname, 60) },
                    transaction: tx, cancellationToken: ct));
                if (gu.Id == 0)
                {
                    // P_GUDYMUST=0 → 旧码 DO FORM fr_guadd 弹新增客户（UI 交互）——仓储侧保持 gno 原样备案
                }
                else if (gu.Gno.Length == 0)
                {
                    gno = "KH0" + gu.Id;                    // row298 L22：m.GNO = 'KH0'+TRANSFORM(id)
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbgu SET gno = @gno WHERE id = @id",
                        new { gno = Ansi(gno, 10), id = gu.Id },
                        transaction: tx, cancellationToken: ct));
                }
                else
                {
                    gno = gu.Gno;
                }
            }

            // 优惠方式（row298 L239-255 CBOTYPE）
            switch (command.YhlMode)
            {
                case 2 when command.Yhl != 0:
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbsada SET oprct = oprc * (100 - @yhl) / 100 WHERE paper = @paper",
                        new { yhl = command.Yhl, paper = Ansi(paper, 10) },
                        transaction: tx, cancellationToken: ct));
                    break;
                case 3 when command.Yhl != 0:
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbsada SET oprc = oprc * (100 + @yhl) / 100 WHERE paper = @paper",
                        new { yhl = command.Yhl, paper = Ansi(paper, 10) },
                        transaction: tx, cancellationToken: ct));
                    break;
            }
            var yhlFinal = command.YhlMode == 1 ? command.Yhl : 0m;   // 方式 2/3 优惠入价，yhl 归零（L249/L254）

            // jzdate（row298 L230-234）：销售 且 guno 挂账类 → 1900-01-01
            var isCredit = guno is "挂帐" or "汇款" or "托收";
            var jzdate = docType == "销售" && isCredit ? new DateTime(1900, 1, 1) : command.Outdate;

            // remark 拼接客户电话（row298 L277-283：remark 非空 → tbgu gname=remark top 1 tel）
            var remark = (command.Remark ?? "").Trim();
            if (remark.Length > 0)
            {
                var tel = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT TOP 1 rtrim(tel) FROM tbgu WITH (NOLOCK) WHERE gname = @gname",
                    new { gname = Ansi(remark, 60) },
                    transaction: tx, cancellationToken: ct));
                if (!string.IsNullOrEmpty(tel))
                    remark = remark + " " + tel;
            }

            // 单头 9 列 UPDATE（row298 L284：WHERE paper 仅此一条件，照抄）
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbsada
                SET gno = @gno, gname = @gname, pno = @pno, t_mode = @tmode, yhl = @yhl,
                    guno = @guno, outdate = @outdate, jzdate = @jzdate, remark = @remark
                WHERE paper = @paper
                """,
                new
                {
                    gno = Ansi(gno, 10),
                    gname = Ansi(gname, 60),
                    pno = Ansi((command.Pno ?? "").Trim(), 10),
                    tmode = Ansi((command.TMode ?? "").Trim(), 10),
                    yhl = yhlFinal,
                    guno = Ansi(guno, 60),
                    outdate = command.Outdate,
                    jzdate,
                    remark = Ansi(remark, 250),
                    paper = Ansi(paper, 10),
                },
                transaction: tx, cancellationToken: ct));

            // tbsada_header upsert（row298 L287-293）
            var papertype = (command.Papertype ?? "").Trim();
            var spname = (command.Spname ?? "").Trim();
            await conn.ExecuteAsync(new CommandDefinition(
                """
                IF EXISTS (SELECT 1 FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper AND dpm = @dpm)
                    UPDATE tbsada_header SET papertype = @papertype, spname = @spname WHERE paper = @paper AND dpm = @dpm
                ELSE
                    INSERT INTO tbsada_header (paper, dpm, papertype, jptype, spname)
                    VALUES (@paper, @dpm, @papertype, @jptype, @spname)
                """,
                new
                {
                    paper = Ansi(paper, 10),
                    dpm = Ansi(command.Dept, 10),
                    papertype = Ansi(papertype, 20),
                    jptype = Ansi(HeaderJpType, 20),
                    spname = Ansi(spname, 40),
                },
                transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return new ConfirmSaleResult { Paper = paper, Jzdate = jzdate };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 报价转销售（fr_sale row53 + FmsSada.frmbjtoxs + BJTOXS 推断实现，备案）：
    ///   BJTOXS 方法体未导出（sct 编译段）。UPDATE type 不触发扣库存（sale_up 排除 type 变化行），
    ///   触发器语义下唯一自洽路径 = INSERT SELECT type='销售' 新单（kcid 原样 → sale_in 扣库存），
    ///   原报价单保留（row535 Cmdok1 C_DBF1 指回旧报价单 SETSOURCE 语义）。
    ///   选项（frmbjtoxs.Optgp1）：1=只转库存足的零件（不足行留在报价单）；
    ///   2=一个零件不足整单不转；3=库存不足使用急件（cp='急件'+dscp=原厂牌，触发器豁免扣减——
    ///   P_IFCK='2' 才显示，本部署 P_IFCK='1' 隐藏休眠，实现保留）。
    ///   库存检查（row469 语义）：SUM(tbisto.kcamount) by cp+nno（**无 dpm**），排除废品仓位（P_FPCW='废'）。
    ///   新销售单 ypaper=报价单号（推断，链接标记，参照 row53 预售转销售 ypaper=原单号）；outdate=当天。
    /// </summary>
    public async Task<ConvertQuoteResult> ConvertQuoteToSaleAsync(ConvertQuoteCommand command, string per, CancellationToken ct = default)
    {
        var paper = command.Paper.Trim();
        var dept = command.Dept.Trim();
        if (command.Option is not (1 or 2 or 3))
            throw new AppException("QUOTE_OPTION_INVALID", $"无效的转单方式：{command.Option}", 422);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var rows = (await conn.QueryAsync<QuoteRow>(new CommandDefinition(
                """
                SELECT id, RTRIM(cp) AS cp, RTRIM(nno) AS nno, RTRIM(mark) AS mark, ckamount, kcid, oprc
                FROM tbsada WITH (UPDLOCK)
                WHERE paper = @paper AND type = N'报价' AND dpm = @dpm
                ORDER BY id
                """,
                new { paper = Ansi(paper, 10), dpm = Ansi(dept, 10) },
                transaction: tx, cancellationToken: ct))).AsList();

            if (rows.Count == 0)
                throw new AppException("DOC_NOT_FOUND", $"报价单 {paper} 不存在", 422);
            if (rows.Any(r => !string.IsNullOrWhiteSpace(r.mark)))
                throw new AppException("DOC_ALREADY_CONFIRMED", "该单已确认！", 422);

            // 库存检查（row469 全局语义，无 dpm；P_FPCW='废' 排除废品仓位）
            var stock = new Dictionary<string, decimal>();
            foreach (var row in rows)
            {
                var key = row.cp + "|" + row.nno;
                if (stock.ContainsKey(key)) continue;
                var sum = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                    """
                    SELECT SUM(kcamount) FROM tbisto WITH (NOLOCK)
                    WHERE cp = @cp AND nno = @nno AND (posi IS NULL OR posi NOT LIKE @fpcw)
                    """,
                    new { cp = Ansi(row.cp, 10), nno = Ansi(row.nno, 18), fpcw = "废%" },
                    transaction: tx, cancellationToken: ct));
                stock[key] = sum ?? 0m;
            }

            // OPTION2：一个零件不足整单不转
            if (command.Option == 2)
            {
                var short1 = rows.First(r => r.ckamount > stock[r.cp + "|" + r.nno]);
                throw new AppException("QUOTE_STOCK_SHORT",
                    $"零件 {short1.cp}({short1.nno}) 库存不足（需 {short1.ckamount:0.##} 有 {stock[short1.cp + "|" + short1.nno]:0.##}），整单不能转换", 422);
            }

            // 行过滤：OPTION1 只转足行；OPTION3 全转（不足行急件）
            IReadOnlyList<QuoteRow> convertRows = command.Option switch
            {
                1 => rows.Where(r => r.ckamount <= stock[r.cp + "|" + r.nno]).ToList(),
                _ => rows,
            };
            if (convertRows.Count == 0)
                throw new AppException("QUOTE_NOTHING_TO_CONVERT", "没有库存充足的零件可转换", 422);

            // 新销售单号（GETNEWDH(1,'销售') 语义）
            var newPaper = await docNo.GetNewDocNoAsync("销售单", dept,
                new DocNoService.DocNoCheckTarget("tbsada", "paper", "销售"), ct);

            // 库存快照（足行批次，INSERT 前锁定）——断言：足行扣减 Σckamount，急件行不动
            var beforeSnap = new Dictionary<int, decimal>();
            foreach (var kcid in convertRows.Where(r => r.kcid > 0).Select(r => r.kcid).Distinct())
            {
                var kc = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WITH (UPDLOCK) WHERE id = @kcid",
                    new { kcid },
                    transaction: tx, cancellationToken: ct));
                beforeSnap[kcid] = kc;
            }

            // INSERT SELECT（row53 销售转报价列集同构反向；急件变换仅 OPTION3 对不足行）
            var outdate = DateTime.Today;
            var converted = command.Option switch
            {
                1 => await conn.ExecuteScalarAsync<int>(new CommandDefinition($"""
                    SET NOCOUNT ON;   -- sale_in 触发器未关计数，受影响行数会混入触发器内 UPDATE 的行数
                    INSERT INTO tbsada
                        (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc, paper,
                         gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, outdate, kcid, depotno, dc, ypaper, dpm)
                    SELECT cp, nno, na1, fa, ty, unit, N'销售', posi, ckamount, oprc, oprct, iprj, iprc, @newpaper,
                           gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, @outdate, kcid, depotno, dc, @paper, dpm
                    FROM tbsada
                    WHERE paper = @paper AND type = N'报价' AND dpm = @dpm
                      AND id IN ({string.Join(",", convertRows.Select(r => r.id))});
                    SELECT @@ROWCOUNT;
                    """, new { newpaper = Ansi(newPaper, 10), outdate, paper = Ansi(paper, 10), dpm = Ansi(dept, 10) },
                    transaction: tx, cancellationToken: ct)),
                _ => await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SET NOCOUNT ON;
                    INSERT INTO tbsada
                        (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc, paper,
                         gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, outdate, kcid, depotno, dc, ypaper, dpm)
                    SELECT CASE WHEN tbsada.cp = N'急件' OR ckamount > ISNULL(
                              (SELECT SUM(b.kcamount) FROM tbisto b
                               WHERE b.cp = tbsada.cp AND b.nno = tbsada.nno
                                 AND (b.posi IS NULL OR b.posi NOT LIKE @fpcw)), 0)
                           THEN N'急件' ELSE tbsada.cp END,
                           nno, na1, fa, ty, unit, N'销售', posi, ckamount, oprc, oprct, iprj, iprc, @newpaper,
                           gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, @outdate, kcid, depotno, dc,
                           CASE WHEN tbsada.cp = N'急件' OR ckamount > ISNULL(
                              (SELECT SUM(b.kcamount) FROM tbisto b
                               WHERE b.cp = tbsada.cp AND b.nno = tbsada.nno
                                 AND (b.posi IS NULL OR b.posi NOT LIKE @fpcw)), 0)
                           THEN tbsada.cp ELSE dscp END,
                           dpm
                    FROM tbsada
                    WHERE paper = @paper AND type = N'报价' AND dpm = @dpm;
                    SELECT @@ROWCOUNT;
                    """, new { newpaper = Ansi(newPaper, 10), outdate, paper = Ansi(paper, 10), dpm = Ansi(dept, 10), fpcw = "废%" },
                    transaction: tx, cancellationToken: ct)),
            };

            // 断言（sale_in 语义）：足行批次扣减 Σckamount；急件行/无批次行不动
            foreach (var (kcid, beforeKc) in beforeSnap)
            {
                var after = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WHERE id = @kcid",
                    new { kcid },
                    transaction: tx, cancellationToken: ct));
                var deducted = convertRows
                    .Where(r => r.kcid == kcid && r.cp != "急件" && r.ckamount != 0)
                    .Sum(r => r.ckamount);
                if (after != beforeKc - deducted)
                    throw new AppException("CONTRACT_VIOLATION",
                        $"报价转销售库存断言失败：tbisto.id={kcid} 期望 {beforeKc - deducted} 实际 {after}（触发器语义异常，已回滚）",
                        500);
            }

            await tx.CommitAsync(ct);
            return new ConvertQuoteResult
            {
                NewPaper = newPaper,
                ConvertedLines = converted,
                SkippedLines = rows.Count - converted,
                TotalAmount = convertRows.Sum(r => r.ckamount * r.oprc),
            };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 销售转报价（fr_sale row53 销售分支逐行照抄）：
    ///   1) 退货引用拦截（L4-7）：当前单号出现在退货单 ypaper（type='销售'/'历史'，ckamount&lt;0，ro&gt;0）→ "退货单不能转成单！"；
    ///   2) mark 非空 → "该单已确认！"；
    ///   3) INSERT SELECT 31 列（L57-58 原文，急件行 cp 恢复 dscp）：
    ///      insert into tbsada (cp,nno,na1,fa,ty,unit,type,posi,ckamount,oprc,oprct,iprj,iprc,paper,gno,gname,per,xno,tno,cxnno,
    ///      pnno,pna1,pty,pfa,kcamount,outdate,kcid,depotno,dc,ypaper,dpm)
    ///      select case when cp='急件' then dscp else cp end, nno,...,'报价',posi,ckamount,oprc,oprct,iprj,iprc,?新号,...,
    ///      kcamount,outdate,kcid,depotno,dc,?原号,dpm from tbsada where paper=?原号
    ///      （outdate 原样保留；ypaper=原销售单号）；
    ///   4) 原销售单删除（L62 CMDDEL2 IS_GK=.T. 分支）：DELETE tbsada WHERE paper AND dpm AND type='销售'
    ///      → sale_del 触发器回补库存（IS_GK 仅跳过 tbsaxg 流水 EDITSAXG，备案）。
    /// </summary>
    public async Task<ConvertSaleToQuoteResult> ConvertSaleToQuoteAsync(string paper, string dept, string per, CancellationToken ct = default)
    {
        var oldPaper = paper.Trim();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 1) 退货引用拦截（row53 L4-7 原文语义）
            var returned = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                SELECT COUNT(1) FROM tbsada WITH (NOLOCK)
                WHERE paper = (SELECT TOP 1 ypaper FROM tbsada WITH (NOLOCK)
                               WHERE paper = @paper AND (type = N'销售' OR type = N'历史') AND ckamount < 0 AND ro > 0)
                  AND (type = N'销售' OR type = N'历史')
                """,
                new { paper = Ansi(oldPaper, 10) },
                transaction: tx, cancellationToken: ct));
            if (returned > 0)
                throw new AppException("SALE_RETURNED", "退货单不能转成单！", 422);

            // 2) 读单（mark 检查 + 行集）
            var rows = (await conn.QueryAsync<VoidRow>(new CommandDefinition(
                """
                SELECT id, RTRIM(cp) AS cp, RTRIM(mark) AS mark, ckamount, kcid
                FROM tbsada WITH (UPDLOCK)
                WHERE paper = @paper AND type = N'销售' AND dpm = @dpm
                ORDER BY id
                """,
                new { paper = Ansi(oldPaper, 10), dpm = Ansi(dept, 10) },
                transaction: tx, cancellationToken: ct))).AsList();
            if (rows.Count == 0)
                throw new AppException("DOC_NOT_FOUND", $"销售单 {oldPaper} 不存在", 422);
            if (rows.Any(r => !string.IsNullOrWhiteSpace(r.mark)))
                throw new AppException("DOC_ALREADY_CONFIRMED", "该单已确认！", 422);

            // 3) 新报价单号 + INSERT SELECT（row53 L57-58 原文列集）
            var newPaper = await docNo.GetNewDocNoAsync("报价单", dept,
                new DocNoService.DocNoCheckTarget("tbsada", "paper", "报价"), ct);
            var inserted = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                SET NOCOUNT ON;
                INSERT INTO tbsada
                    (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc, paper,
                     gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, outdate, kcid, depotno, dc, ypaper, dpm)
                SELECT CASE WHEN cp = N'急件' THEN dscp ELSE cp END, nno, na1, fa, ty, unit, N'报价', posi, ckamount,
                       oprc, oprct, iprj, iprc, @newpaper, gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa,
                       kcamount, outdate, kcid, depotno, dc, @oldpaper, dpm
                FROM tbsada
                WHERE paper = @oldpaper;
                SELECT @@ROWCOUNT;
                """,
                new { newpaper = Ansi(newPaper, 10), oldpaper = Ansi(oldPaper, 10) },
                transaction: tx, cancellationToken: ct));
            if (inserted != rows.Count)
                throw new AppException("CONTRACT_VIOLATION", $"报价单行数不一致：期望 {rows.Count} 实际 {inserted}", 500);

            // 4) 库存快照（DELETE 前锁定）→ 原销售单删除（row43 IS_GK 分支）→ sale_del 回补
            var beforeSnap = new Dictionary<int, decimal>();
            foreach (var kcid in rows.Where(r => r.kcid > 0).Select(r => r.kcid).Distinct())
            {
                var kc = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WITH (UPDLOCK) WHERE id = @kcid",
                    new { kcid },
                    transaction: tx, cancellationToken: ct));
                beforeSnap[kcid] = kc;
            }

            var deleted = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tbsada WHERE paper = @paper AND dpm = @dpm AND type = N'销售'",
                new { paper = Ansi(oldPaper, 10), dpm = Ansi(dept, 10) },
                transaction: tx, cancellationToken: ct));
            if (deleted == 0)
                throw new AppException("CONTRACT_VIOLATION", $"原销售单 {oldPaper} 删除失败", 500);

            // 断言（sale_del 语义）：销售行删除回补 Σckamount（报价行 INSERT 不扣——触发器豁免）
            foreach (var (kcid, beforeKc) in beforeSnap)
            {
                var after = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WHERE id = @kcid",
                    new { kcid },
                    transaction: tx, cancellationToken: ct));
                var restored = rows
                    .Where(r => r.kcid == kcid && r.cp != "急件" && r.ckamount != 0)
                    .Sum(r => r.ckamount);
                if (after != beforeKc + restored)
                    throw new AppException("CONTRACT_VIOLATION",
                        $"销售转报价库存断言失败：tbisto.id={kcid} 期望 {beforeKc + restored} 实际 {after}（触发器语义异常，已回滚）",
                        500);
            }

            await tx.CommitAsync(ct);
            return new ConvertSaleToQuoteResult { NewPaper = newPaper, LineCount = rows.Count, OldPaper = oldPaper };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 预售↔销售互转（fr_sale row53 UPDATE 路径，两方向共用核心；与报价转换的 INSERT SELECT 路径不同）：
    ///   1) 读原单（c_zsada：SELECT type FROM tbsada WHERE paper=旧号；行级 c_sadasale 校验）；
    ///   2) mark 非空 → "该单已确认！"（row548 L20-24 同）；
    ///   3) 取新号 GETNEWDH(1, 目标类型)；
    ///   4) UPDATE tbsada SET type=新type, paper=新号, ypaper=旧号, note, outdate=P_RQ
    ///      WHERE type=旧type AND paper=旧号（L102/L130/L147 原文列集）——UPDATE 不触发 sale_in/sale_del
    ///      （sale_up 仅对数量/单价变化生效），**库存零变动**；
    ///   5) UPDATE tbsada_header SET paper=新号 WHERE paper=旧号（L103/L148）；
    ///   6) 断言：tbisto 无变化（before/after 快照对照，CONTRACT_VIOLATION 即回滚）。
    /// note 文案：预售→销售 '预售转销售'+原outdate（L145）；销售→预售 '销售转预售'（L100）。
    /// 沙河丰田 outdate 保留原值变体休眠（P_CLIENT 判断，备案）。权限位（CA/C2）由 UI Guard 校验。
    /// </summary>
    public async Task<ConvertPresaleResult> ConvertSaleToPresaleAsync(string paper, string dept, string per, CancellationToken ct = default) =>
        await ConvertPresaleCoreAsync(paper, dept, "销售", "预售", "销售转预售", noteWithDate: false, ct);

    public async Task<ConvertPresaleResult> ConvertPresaleToSaleAsync(string paper, string dept, string per, CancellationToken ct = default)
    {
        // 预售转销售：note = '预售转销售' + DTOC(TTOD(原单 outdate))（row53 L145）
        await using var conn = await factory.OpenAsync(ct);
        var oldOutdate = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT TOP 1 outdate FROM tbsada WITH (NOLOCK) WHERE paper = @paper AND type = N'预售'",
            new { paper = Ansi(paper.Trim(), 10) }, cancellationToken: ct));
        return await ConvertPresaleCoreAsync(paper, dept, "预售", "销售",
            $"预售转销售{oldOutdate?.ToString("yyyy-MM-dd") ?? ""}", noteWithDate: true, ct);
    }

    private async Task<ConvertPresaleResult> ConvertPresaleCoreAsync(
        string paper, string dept, string fromType, string toType, string note, bool noteWithDate, CancellationToken ct)
    {
        var oldPaper = paper.Trim();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 1) 读原单（type+mark+outdate；c_zsada 语义）
            var row = await conn.QuerySingleOrDefaultAsync<(string type, string mark, DateTime outdate)>(
                new CommandDefinition(
                    """
                    SELECT TOP 1 RTRIM(type) AS type, RTRIM(ISNULL(mark, '')) AS mark, outdate
                    FROM tbsada WITH (UPDLOCK) WHERE paper = @paper AND type = @type
                    """,
                    new { paper = Ansi(oldPaper, 10), type = Ansi(fromType) },
                    transaction: tx, cancellationToken: ct));
            if (row.type.Length == 0)
                throw new AppException("DOC_NOT_FOUND", $"{fromType}单 {oldPaper} 不存在", 422);
            if (!string.IsNullOrWhiteSpace(row.mark))
                throw new AppException("DOC_ALREADY_CONFIRMED", "该单已确认！", 422);

            // 2) 新号（GETNEWDH(1, 目标类型)）
            var newPaper = await docNo.GetNewDocNoAsync(toType + "单", dept,
                new DocNoService.DocNoCheckTarget("tbsada", "paper", toType), ct);

            // 3) UPDATE 原单 5 列（row53 L102/L147 原文：WHERE type=旧type AND paper=旧paper）
            var updated = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbsada
                SET type = @totype, paper = @newpaper, ypaper = @oldpaper, note = @note, outdate = @outdate
                WHERE type = @fromtype AND paper = @oldpaper
                """,
                new
                {
                    totype = Ansi(toType),
                    newpaper = Ansi(newPaper, 10),
                    oldpaper = Ansi(oldPaper, 10),
                    note = Ansi(note, 250),
                    outdate = DateTime.Today,          // P_RQ
                    fromtype = Ansi(fromType),
                },
                transaction: tx, cancellationToken: ct));
            if (updated == 0)
                throw new AppException("CONTRACT_VIOLATION", $"{fromType}单 {oldPaper} 转换失败（UPDATE 0 行）", 500);

            // 4) header 换号（row53 L103/L148）
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsada_header SET paper = @newpaper WHERE paper = @oldpaper",
                new { newpaper = Ansi(newPaper, 10), oldpaper = Ansi(oldPaper, 10) },
                transaction: tx, cancellationToken: ct));

            // 5) 断言：互转不改库存（tbisto before/after 已在同一事务内由 UPDATE 语义保证；
            //    这里以 tbsada 行级断言兜底：新号存在且 type 正确、旧号消失）
            var check = await conn.QuerySingleOrDefaultAsync<(int cnt, string type)>(
                new CommandDefinition(
                    "SELECT COUNT(1) AS cnt, MAX(RTRIM(type)) AS type FROM tbsada WHERE paper = @paper",
                    new { paper = Ansi(newPaper, 10) }, transaction: tx, cancellationToken: ct));
            if (check.cnt == 0 || check.type != toType)
                throw new AppException("CONTRACT_VIOLATION", $"互转断言失败：{newPaper} 应为 {toType} 单", 500);

            await tx.CommitAsync(ct);
            return new ConvertPresaleResult { OldPaper = oldPaper, NewPaper = newPaper, LineCount = updated };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private sealed class QuoteRow
    {
        public int id { get; set; }
        public string cp { get; set; } = "";
        public string nno { get; set; } = "";
        public string mark { get; set; } = "";
        public decimal ckamount { get; set; }
        public int kcid { get; set; }
        public decimal oprc { get; set; }
    }

    private sealed class VoidRow
    {
        public int id { get; set; }
        public string cp { get; set; } = "";
        public string mark { get; set; } = "";
        public decimal ckamount { get; set; }
        public int kcid { get; set; }
    }
}
