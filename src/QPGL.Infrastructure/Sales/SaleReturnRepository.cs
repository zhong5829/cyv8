using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Sales;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;

namespace QPGL.Infrastructure.Sales;

public interface ISaleReturnRepository
{
    Task<CreateSaleReturnResult> CreateAsync(CreateSaleReturnCommand command, string per, CancellationToken ct = default);

    /// <summary>原单可退行加载（退货页；paper 校验 type IN ('历史','销售') 且有正数出库行）。</summary>
    Task<SaleSourceResult> GetSourceAsync(string paper, CancellationToken ct = default);

    /// <summary>按客户加载可退行（退货页；该客户全部销售单中未退完的正数出库行；from=出库日期下界，null=全量）。</summary>
    Task<CustomerSourceResult> GetSourceByCustomerAsync(string gno, string dpm, DateTime? from = null, CancellationToken ct = default);

    /// <summary>退款方式字典（tbsysset type='guno'，row145 COMBO1 行来源）。</summary>
    Task<IReadOnlyList<string>> GetRefundMethodsAsync(CancellationToken ct = default);

    /// <summary>确认退货窗通用字典（tbsysset type 字典：退货原因/fhfs 等）。</summary>
    Task<IReadOnlyList<string>> GetDictByTypeAsync(string type, CancellationToken ct = default);

    /// <summary>销售类型字典（tbsysset name='papertype' 的 valuec 去重）。</summary>
    Task<IReadOnlyList<string>> GetPaperTypesAsync(CancellationToken ct = default);

    /// <summary>原单头销售类型（确认退货窗「销售类型」默认值；空=门店）。</summary>
    Task<string> GetHeaderPapertypeAsync(string paper, string dpm, CancellationToken ct = default);

    /// <summary>零件名称→拼音码映射（tbname0 整表；名称框拼音过滤，内存过滤用）。</summary>
    Task<IReadOnlyDictionary<string, string>> GetPinyinMapAsync(CancellationToken ct = default);

    /// <summary>配件实际批次列表（退货编辑窗「库号/仓位」下拉选项）。</summary>
    Task<IReadOnlyList<ReturnBatchOption>> GetBatchesAsync(string cp, string nno, string dpm, CancellationToken ct = default);
}

/// <summary>
/// 销售退货仓储（写路径，触发器语义感知——契约出处 fr_sata row145，逐行复刻）。
/// 关键语义：
///   1) 取号：tbdjgs「退货单」（AH 前缀）——FormSet.GETNEWDH 的「退货」+"单"。
///   2) 退货行 type='销售'（与原单同类型）；INSERT 时 ckamount=0（避开 sale_in），
///      随后 UPDATE ckamount=-qty —— 库存回补由 sale_up 触发器完成
///      （kcamount = kcamount + qty，因 @amount = deleted(0) - inserted(-qty)）。
///      【禁止】自行 UPDATE tbisto。
///   3) 原行 ro（已退数）由应用层更新：ro = ro + qty
///      （sale_up 的 ro 联动因 deleted.ckamount=0 不触发，无双记）。
///   4) 急件（cp='急件'）需另插 tbistoed 入库流水——M2 不支持，显式拒绝。
///   5) 回读断言：tbisto.kcamount after == before + qty。
/// </summary>
public sealed class SaleReturnRepository(SqlConnectionFactory factory, IDocNoService docNo) : ISaleReturnRepository
{
    private const string SaleType = "销售";
    private const string HistoryType = "历史";

    /// <summary>可退行来源类型（fr_sata 加载条件：type IN ('历史','销售')——本部署"历史"占绝大多数）。</summary>
    private static readonly string[] SourceTypes = [SaleType, HistoryType];

    public async Task<CreateSaleReturnResult> CreateAsync(CreateSaleReturnCommand command, string per, CancellationToken ct = default)
    {
        SaleReturnValidator.Validate(command);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        string paper;
        try
        {
            // 1) 锁定并校验原单行（UPDLOCK 串行化并发退货；来源类型=历史/销售；
            //    2026-10-29 对齐旧 fr_sata row145：一张退货单可混装多张原单的行——不再按 paper 过滤，
            //    行级 ypaper 各写各的原单号；dpm 必须属于当前部门，防跨部门行混入）
            var sourceIds = command.Lines.Select(l => l.RowId).Distinct().ToList();
            var sourceRows = (await conn.QueryAsync<SourceRow>(new CommandDefinition(
                $"""
                SELECT {SourceColumns}
                FROM tbsada WITH (UPDLOCK)
                WHERE id IN @ids AND type IN @types AND dpm = @dpm
                """,
                new { ids = sourceIds, types = SourceTypes, dpm = Ansi(command.Dept.Trim(), 10) },
                transaction: tx, cancellationToken: ct))).AsList();

            if (sourceRows.Count != sourceIds.Count)
                throw new AppException("RETURN_SOURCE_INVALID", $"原单 {command.SourcePaper} 不存在或包含非销售/历史行", 422);

            foreach (var line in command.Lines)
            {
                var row = sourceRows.First(r => r.id == line.RowId);
                if (row.ckamount <= 0)
                    throw new AppException("RETURN_SOURCE_INVALID", $"原行 {line.RowId} 不是正数出库行", 422);
                if (row.cp.Trim() == "急件")
                    throw new AppException("RETURN_URGENT_UNSUPPORTED", "急件退货需走急件入库流程，暂不支持", 422);
                var returned = row.ro;                       // 已退数
                var remain = row.ckamount - returned;        // 未退量
                if (line.Qty > remain)
                    throw new AppException("RETURN_OVER_QTY",
                        $"配件 {row.cp.Trim()} 未退量 {remain}，退货 {line.Qty} 超出", 422);
            }

            // 2) 取退货单号（tbdjgs「退货单」，锁内）
            paper = await docNo.GetNewDocNoAsync("退货单", command.Dept, ct);

            var outdate = DateTime.Today;
            var reason = (command.Reason ?? "").Trim();
            var totalAmount = 0m;
            var beforeSnapshot = new Dictionary<int, decimal>();
            var lineCount = 0;
            var resolvedBatches = new List<(int kcid, decimal qty)>();

            foreach (var line in command.Lines)
            {
                var src = sourceRows.First(r => r.id == line.RowId);

                // 编辑窗可改：售价/实售价/库号/仓位（空=沿用原行值，旧 fr_sata 编辑窗语义）
                var depotno = (line.DepotNo is null ? src.depotno : line.DepotNo).Trim();
                var posi = (line.Posi is null ? src.posi : line.Posi).Trim();
                var oprc = line.Oprc ?? src.oprc;
                var oprct = line.Oprct ?? src.oprct;

                // 废品行（2026-10-29 需求）：勾选废品仓 → 不回补库存。
                // 实现=该行 kcid=0 不关联批次：sale_up 触发器按行上 kcid 回补（update tbisto ... where id=@kcid），
                // kcid=0 时回补空转 0 行受影响；退货单行/原单 ro 统计照常成立。落库自动备注[已作废]。
                var remark = (src.remark.Trim()
                              + (reason.Length > 0 ? $"[退货原因：{reason}]" : "")
                              + (line.Scrap ? "[已作废]" : ""));

                int kcid;
                if (line.Scrap)
                {
                    kcid = 0;
                    depotno = "废品仓";
                }
                else
                {
                    // 3) 批次重解析（旧 KCID('c_th') 链）：id 全匹配 → cp+nno+iprc+posi+depotno(+dpm) → 未命中新建批次
                    kcid = await ResolveKcidAsync(conn, tx, src, depotno, posi, ct);
                }

                // 快照以解析后批次为准（断言 sale_up 回补语义；废品行 kcid=0 无批次，不参与断言/快照）
                if (!line.Scrap)
                {
                    var kc = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                        "SELECT kcamount FROM tbisto WHERE id = @kcid", new { kcid },
                        transaction: tx, cancellationToken: ct));
                    if (!beforeSnapshot.TryAdd(kcid, kc))
                        beforeSnapshot[kcid] = Math.Min(beforeSnapshot[kcid], kc);
                    resolvedBatches.Add((kcid, line.Qty));
                }

                // 4) INSERT 退货行：ckamount=0（避开 sale_in）
                var newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    $"""
                    INSERT INTO tbsada
                        (paper, ypaper, type, cp, nno, na1, ty, dc, fa, unit, remark, note,
                         depotno, posi, iprc, iprj, oprc, oprct, ckamount, yhl, gno, gname, per,
                         outdate, pno, guno, xno, pnno, pna1, pty, pfa, kcid, dpm, t_mode, ro, dscp, trigger_mark)
                    VALUES
                        (@paper, @ypaper, @type, @cp, @nno, @na1, @ty, @dc, @fa, @unit, @remark, @note,
                         @depotno, @posi, @iprc, @iprj, @oprc, @oprct, 0, @yhl, @gno, @gname, @per,
                         @outdate, @pno, @guno, @xno, @pnno, @pna1, @pty, @pfa, @kcid, @dpm, @t_mode, @ro, @dscp, @triggerMark);
                    SELECT CAST(SCOPE_IDENTITY() AS int);
                    """,
                    new
                    {
                        paper = Ansi(paper, 10),
                        ypaper = Ansi(src.paper.Trim(), 10),
                        type = Ansi(SaleType),
                        cp = Ansi(src.cp.Trim(), 10),
                        nno = Ansi(src.nno.Trim(), 18),
                        na1 = Ansi(src.na1, 40),
                        ty = Ansi(src.ty, 14),
                        dc = Ansi(src.dc, 8),
                        fa = Ansi(src.fa, 8),
                        unit = Ansi(src.unit, 4),
                        remark = Ansi(remark, 250),
                        note = Ansi(src.note, 250),
                        depotno = Ansi(depotno, 10),           // 编辑窗「库号」
                        posi = Ansi(posi, 10),                 // 编辑窗「仓位」
                        iprc = src.iprc,
                        iprj = src.iprj,
                        oprc,                                  // 编辑窗「售价改为」
                        oprct,                                 // 编辑窗「实售价改为」
                        yhl = src.yhl,
                        gno = Ansi(src.gno, 10),
                        gname = Ansi(src.gname, 60),
                        per = Ansi(per, 10),                   // row145：默认当前操作员（P_THUSEOLDPER 为假）
                        outdate,
                        pno = Ansi((command.InvoiceNo ?? src.pno).Trim(), 12),   // 确认退货窗「发票号」（旧 Frmok TxtPno→PNO）；空=原行 pno
                        guno = Ansi(command.RefundMethod!.Trim(), 10),  // row145：GUNO=表单退款方式（COMBO1），非原行值
                        xno = src.xno,
                        pnno = Ansi(src.nno.Trim(), 18),       // row145：PNNO=NNO、PNA1=NA1、PTY=TY、PFA=FA（原行值）
                        pna1 = Ansi(src.na1, 40),
                        pty = Ansi(src.ty, 14),
                        pfa = Ansi(src.fa, 8),
                        kcid,                                  // 重解析后的批次
                        dpm = Ansi(command.Dept, 10),
                        t_mode = Ansi((command.ShipMode ?? src.t_mode).Trim(), 10),  // 确认退货窗「发货方式」（旧 Frmok Cbot_mode）；空=原行 t_mode
                        ro = src.id,                           // 指向原行 id
                        dscp = Ansi(src.dscp, 10),
                        triggerMark = src.trigger_mark,
                    },
                    transaction: tx, cancellationToken: ct));

                // 5) UPDATE ckamount = -qty（触发 sale_up：tbisto 回补 +qty）
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbsada SET ckamount = @ck WHERE id = @id",
                    new { ck = -line.Qty, id = newId },
                    transaction: tx, cancellationToken: ct));

                // 6) 原行 ro = ro + qty（应用层更新，与触发器不重复）
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbsada SET ro = ro + @qty WHERE id = @id",
                    new { qty = line.Qty, id = src.id },
                    transaction: tx, cancellationToken: ct));

                // 7) 原行 kcid 同步解析结果（旧 KCID() 末尾 update tbsada set kcid where id=原行id）；
                //    废品行 kcid=0 不回写（原行批次关联必须保留，否则原行后续退货的批次解析被破坏）
                if (!line.Scrap && kcid != src.kcid)
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbsada SET kcid = @kcid WHERE id = @id",
                        new { kcid, id = src.id },
                        transaction: tx, cancellationToken: ct));

                totalAmount += line.Qty * oprc;
                lineCount++;
            }

            // 6) 单头（row145：papertype=确认退货窗「销售类型」（CBOPAPERTYPE，默认取原单头、空则「门店」）；jptype='单据类型'）
            string papertype;
            if (!string.IsNullOrWhiteSpace(command.PaperType))
            {
                papertype = command.PaperType.Trim();
            }
            else
            {
                // 跨原单合并（2026-10-29）：默认销售类型取第一行明细自己的原单头
                var firstSourcePaper = sourceRows.First(r => r.id == command.Lines[0].RowId).paper.Trim();
                var headerPapertype = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT RTRIM(papertype) FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper AND dpm = @dpm",
                    new { paper = Ansi(firstSourcePaper, 10), dpm = Ansi(command.Dept, 10) },
                    transaction: tx, cancellationToken: ct));
                papertype = string.IsNullOrWhiteSpace(headerPapertype) ? "门店" : headerPapertype!.Trim();
            }
            await conn.ExecuteAsync(new CommandDefinition(
                """
                IF EXISTS (SELECT 1 FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper AND dpm = @dpm)
                    UPDATE tbsada_header SET papertype = @papertype, jptype = @jptype WHERE paper = @paper AND dpm = @dpm
                ELSE
                    INSERT INTO tbsada_header (paper, dpm, papertype, jptype)
                    VALUES (@paper, @dpm, @papertype, @jptype)
                """,
                new
                {
                    paper = Ansi(paper, 10),
                    dpm = Ansi(command.Dept, 10),
                    papertype = Ansi(papertype, 10),
                    jptype = Ansi("单据类型"),
                },
                transaction: tx, cancellationToken: ct));

            // 7) 回读断言：tbisto.kcamount after == before + qty（sale_up 回补语义，按解析后批次分组）
            foreach (var (kcid, before) in beforeSnapshot)
            {
                var after = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT kcamount FROM tbisto WHERE id = @kcid",
                    new { kcid },
                    transaction: tx, cancellationToken: ct));
                var returned = resolvedBatches
                    .Where(r => r.kcid == kcid)
                    .Sum(r => r.qty);
                if (after != before + returned)
                    throw new AppException("CONTRACT_VIOLATION",
                        $"退货库存断言失败：tbisto.id={kcid} 期望 {before + returned} 实际 {after}（触发器语义异常，已回滚）",
                        500);
            }

            await tx.CommitAsync(ct);
            return new CreateSaleReturnResult { Paper = paper, LineCount = lineCount, TotalAmount = totalAmount };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<SaleSourceResult> GetSourceAsync(string paper, CancellationToken ct = default)
    {
        var trimmed = paper.Trim();
        await using var conn = await factory.OpenAsync(ct);

        var head = await conn.QueryFirstOrDefaultAsync<SourceHead>(new CommandDefinition(
            """
            SELECT TOP 1 RTRIM(gname) AS gname, outdate
            FROM tbsada WITH (NOLOCK)
            WHERE paper = @paper AND type IN @types AND ckamount > 0
            """,
            new { paper = Ansi(trimmed, 10), types = SourceTypes },
            cancellationToken: ct));
        if (head?.gname is null)
            throw new AppException("RETURN_SOURCE_INVALID", $"原单 {trimmed} 不存在或无正数出库行", 422);

        var lines = (await conn.QueryAsync<SaleSourceLine>(new CommandDefinition(
            """
            SELECT id AS RowId,
                   RTRIM(cp) AS Cp, RTRIM(nno) AS Nno, RTRIM(na1) AS Na1, RTRIM(posi) AS Posi, RTRIM(depotno) AS DepotNo,
                   RTRIM(ty) AS Ty,
                   ckamount AS Qty, ro AS Returned, oprc AS Oprc, oprct AS Oprct
            FROM tbsada WITH (NOLOCK)
            WHERE paper = @paper AND type IN @types AND ckamount > 0
            ORDER BY id
            """,
            new { paper = Ansi(trimmed, 10), types = SourceTypes },
            cancellationToken: ct))).AsList();

        return new SaleSourceResult(trimmed, head.gname, head.outdate, lines);
    }

    /// <param name="from">出库日期下界（远程链路优化 2026-10-29：默认只拉近 3 个月，全量由调用方传 null）。</param>
    public async Task<CustomerSourceResult> GetSourceByCustomerAsync(
        string gno, string dpm, DateTime? from = null, CancellationToken ct = default)
    {
        var no = gno.Trim();
        await using var conn = await factory.OpenAsync(ct);

        // 远程链路优化（2026-10-29）：gname/可退行/批次仓位/拼音表原为 4 次独立往返（frp 往返 35ms/次），合并为 1 次 QueryMultiple
        var dateFilter = from is null ? "" : " AND outdate >= @from";
        var swExec = System.Diagnostics.Stopwatch.StartNew();
        var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            $"""
            SELECT TOP 1 RTRIM(gname) FROM tbgu WITH (NOLOCK) WHERE gno = @gno;
            SELECT RTRIM(paper) AS Paper,
                   id AS RowId,
                   RTRIM(cp) AS Cp, RTRIM(nno) AS Nno, RTRIM(na1) AS Na1, RTRIM(posi) AS Posi, RTRIM(depotno) AS DepotNo,
                   RTRIM(ty) AS Ty, RTRIM(fa) AS Fa, RTRIM(cxnno) AS Cxnno, RTRIM(guno) AS Guno,
                   RTRIM(gname) AS Gname, RTRIM(per) AS Per, RTRIM(note) AS Note,
                   RTRIM(pno) AS Pno, RTRIM(t_mode) AS TMode,
                   ckamount AS Qty, ro AS Returned, oprc AS Oprc, oprct AS Oprct,
                   iprc AS Iprc, iprj AS Iprj, yhl AS Yhl, outdate AS OutDate
            FROM tbsada WITH (NOLOCK)
            WHERE type IN @types AND ckamount > 0 AND ro < ckamount AND gno = @gno AND dpm = @dpm{dateFilter}
            ORDER BY outdate DESC, id DESC;   -- 拿货时间倒序（2026-10-29 需求：最近拿货的排前面，方便选择）
            SELECT DISTINCT RTRIM(cp) AS Cp, RTRIM(nno) AS Nno, RTRIM(depotno) AS DepotNo, RTRIM(posi) AS Posi
            FROM tbisto WITH (NOLOCK)
            WHERE dpm = @dpm
            ORDER BY Cp, Nno, DepotNo, Posi;
            SELECT RTRIM(code), RTRIM(name) FROM tbname0 WITH (NOLOCK);
            """,
            new { gno = Ansi(no, 10), types = SourceTypes, dpm = Ansi(dpm.Trim(), 10), from },
            cancellationToken: ct));
        QPGL.Infrastructure.DebugLog.Log($"[RetSource] QueryMultiple(执行+首包)={swExec.ElapsedMilliseconds}ms");

        var swRead = System.Diagnostics.Stopwatch.StartNew();
        var gname = await multi.ReadFirstOrDefaultAsync<string>();
        if (string.IsNullOrEmpty(gname))
            throw new AppException("RETURN_CUSTOMER_NOT_FOUND", $"客户 {no} 不存在", 422);
        var lines = (await multi.ReadAsync<SaleSourceLine>()).AsList();
        var tLines = swRead.ElapsedMilliseconds;
        var batches = (await multi.ReadAsync<BatchPosiRow>()).AsList();
        var tBatches = swRead.ElapsedMilliseconds - tLines;
        var pinyinRows = (await multi.ReadAsync<(string Code, string Name)>()).AsList();
        QPGL.Infrastructure.DebugLog.Log($"[RetSource] scope={(from?.ToString("yyyy-MM-dd") ?? "全部")} 执行+lines={tLines}ms({lines.Count}行) batches=+{tBatches}ms({batches.Count}) pinyin=+{swRead.ElapsedMilliseconds - tLines - tBatches}ms({pinyinRows.Count})");

        // 货位修正（旧 row145 L41-52，只读版）：原仓位在 tbisto 批次中不存在时修正为实际批次仓位（内存匹配）
        var swFix = System.Diagnostics.Stopwatch.StartNew();
        CorrectPosis(lines, batches);
        QPGL.Infrastructure.DebugLog.Log($"[RetSource] CorrectPosis={swFix.ElapsedMilliseconds}ms");

        var pyMap = new Dictionary<string, string>(pinyinRows.Count, StringComparer.Ordinal);
        foreach (var (code, name) in pinyinRows)
        {
            if (name.Length > 0 && !pyMap.ContainsKey(name)) pyMap[name] = code;
        }

        return new CustomerSourceResult(no, gname, lines, pyMap);
    }

    private sealed class BatchPosiRow
    {
        public string Cp { get; set; } = "";
        public string Nno { get; set; } = "";
        public string DepotNo { get; set; } = "";
        public string Posi { get; set; } = "";
    }

    /// <summary>
    /// 货位修正（旧 fr_sata row145 L41-52 逐行语义的只读版，纯内存）：
    /// 原仓位+库号在 tbisto 有批次 → 保持；原仓位已不存在但库号下有其它批次 → 修正为实际批次仓位（旧码取游标第一条，此处按 posi 序稳定）。
    /// 只修显示/编辑窗默认值，不写库（保存时 KCID 链仍以最终值为准）。
    /// 实现说明：批次行随主查询同批返回；勿改回 keys IN @keys 分块——大 IN 参数列表执行计划编译 1-2 秒曾致加载 30 秒。
    /// </summary>
    private static void CorrectPosis(List<SaleSourceLine> lines, IReadOnlyList<BatchPosiRow> batches)
    {
        if (lines.Count == 0 || batches.Count == 0) return;

        // (cp,nno,depotno) → 该配件库号下的批次仓位列表（posi 序稳定）
        var batchMap = new Dictionary<string, List<string>>(batches.Count);
        foreach (var b in batches)
        {
            var key = $"{b.Cp}|{b.Nno}|{b.DepotNo}";
            if (!batchMap.TryGetValue(key, out var posis))
                batchMap[key] = posis = [];
            posis.Add(b.Posi);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!batchMap.TryGetValue($"{l.Cp}|{l.Nno}|{l.DepotNo}", out var posis) || posis.Contains(l.Posi)) continue;
            lines[i] = new SaleSourceLine
            {
                RowId = l.RowId, Paper = l.Paper, Cp = l.Cp, Nno = l.Nno, Na1 = l.Na1,
                Posi = posis[0], DepotNo = l.DepotNo, Ty = l.Ty, Fa = l.Fa, Cxnno = l.Cxnno,
                Guno = l.Guno, Gname = l.Gname, Per = l.Per, Note = l.Note, Pno = l.Pno, TMode = l.TMode,
                Qty = l.Qty, Returned = l.Returned, Oprc = l.Oprc, Oprct = l.Oprct,
                Iprc = l.Iprc, Iprj = l.Iprj, Yhl = l.Yhl, OutDate = l.OutDate,
            };
        }
    }

    /// <summary>配件实际批次列表（退货编辑窗「库号/仓位」下拉选项；tbisto 按 cp+nno+部门）。</summary>
    public async Task<IReadOnlyList<ReturnBatchOption>> GetBatchesAsync(string cp, string nno, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<ReturnBatchOption>(new CommandDefinition(
            """
            SELECT RTRIM(depotno) AS DepotNo, RTRIM(posi) AS Posi
            FROM tbisto WITH (NOLOCK)
            WHERE cp = @cp AND nno = @nno AND dpm = @dpm
            GROUP BY depotno, posi
            ORDER BY depotno, posi
            """,
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18), dpm = Ansi(dpm, 10) },
            cancellationToken: ct))).AsList();
        return rows;
    }

    /// <summary>退款方式字典（row145 COMBO1：tbsysset type='guno'，按 id 序，默认取第一项）。</summary>
    public async Task<IReadOnlyList<string>> GetRefundMethodsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var names = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'guno' ORDER BY id",
            cancellationToken: ct))).AsList();
        return names;
    }

    /// <summary>确认退货窗通用字典（旧 Frmok：type='退货原因'/'fhfs' 等 tbsysset type 字典，按 id 序）。</summary>
    public async Task<IReadOnlyList<string>> GetDictByTypeAsync(string type, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var names = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = @type ORDER BY id",
            new { type = new DbString { Value = type, IsAnsi = false, Length = 20 } },
            cancellationToken: ct))).AsList();
        return names;
    }

    /// <summary>销售类型字典（旧 Frmok CBOPAPERTYPE 行来源：tbsysset name='papertype' 的 valuec 去重）。</summary>
    public async Task<IReadOnlyList<string>> GetPaperTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var names = (await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK)
            WHERE RTRIM(name) = N'papertype' AND ISNULL(RTRIM(valuec), '') <> ''
            GROUP BY RTRIM(valuec) ORDER BY RTRIM(valuec)
            """,
            cancellationToken: ct))).AsList();
        return names;
    }

    /// <summary>原单头销售类型（确认退货窗「销售类型」默认值；空=门店）。</summary>
    public async Task<string> GetHeaderPapertypeAsync(string paper, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var pt = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT RTRIM(papertype) FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper AND dpm = @dpm",
            new { paper = Ansi(paper.Trim(), 10), dpm = Ansi(dpm, 10) },
            cancellationToken: ct));
        return string.IsNullOrWhiteSpace(pt) ? "门店" : pt!.Trim();
    }

    /// <summary>
    /// 零件名称→拼音码映射（tbname0，名称框拼音过滤；销售页同源语义）。
    /// tbname0 全量仅数千行，直接整表拉回内存匹配——勿改回 name IN @names 分块（大 IN 列表执行计划编译开销大）。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetPinyinMapAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(string Code, string Name)>(new CommandDefinition(
            "SELECT RTRIM(code), RTRIM(name) FROM tbname0 WITH (NOLOCK)",
            cancellationToken: ct))).AsList();
        var map = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
        foreach (var (code, name) in rows)
        {
            if (name.Length > 0 && !map.ContainsKey(name)) map[name] = code;
        }
        return map;
    }

    /// <summary>
    /// 批次重解析（旧 pg_proc KCID L2817 链，部署 P_FGS=.T./P_XZFA=.F.）：
    /// ① id=原kcid 且 cp/nno/iprc/posi/depotno/dpm 全匹配 → ② 去掉 id 按 cp+nno+iprc+posi+depotno+dpm 匹配
    /// → ③ 未命中 INSERT 新 tbisto 批次（列集同旧码）。急件已在上方拒绝（旧 KCID 急件跳过新建不适用）。
    /// </summary>
    private static async Task<int> ResolveKcidAsync(
        SqlConnection conn, SqlTransaction tx, SourceRow src, string depotno, string posi, CancellationToken ct)
    {
        var kcid = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT TOP 1 id FROM tbisto WITH (NOLOCK)
            WHERE id = @kcid AND cp = @cp AND nno = @nno AND iprc = @iprc AND posi = @posi AND depotno = @depotno AND dpm = @dpm
            """,
            new { kcid = src.kcid, cp = Ansi(src.cp.Trim(), 10), nno = Ansi(src.nno.Trim(), 18), iprc = src.iprc, posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), dpm = Ansi(src.dpm.Trim(), 10) },
            transaction: tx, cancellationToken: ct));
        if (kcid.HasValue) return kcid.Value;

        kcid = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT TOP 1 id FROM tbisto WITH (NOLOCK)
            WHERE cp = @cp AND nno = @nno AND iprc = @iprc AND posi = @posi AND depotno = @depotno AND dpm = @dpm
            """,
            new { cp = Ansi(src.cp.Trim(), 10), nno = Ansi(src.nno.Trim(), 18), iprc = src.iprc, posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), dpm = Ansi(src.dpm.Trim(), 10) },
            transaction: tx, cancellationToken: ct));
        if (kcid.HasValue) return kcid.Value;

        // 未命中：新建批次（旧 KCID L2841 列集 cp,nno,na1,ty,fa,dc,unit,iprc,oprc,iprj,posi,depotno,dpm,cxnno）
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO tbisto (cp, nno, na1, ty, fa, dc, unit, iprc, oprc, iprj, posi, depotno, dpm, cxnno)
            VALUES (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @iprc, @oprc, @iprj, @posi, @depotno, @dpm, @cxnno);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new
            {
                cp = Ansi(src.cp.Trim(), 10),
                nno = Ansi(src.nno.Trim(), 18),
                na1 = Ansi(src.na1, 40),
                ty = Ansi(src.ty, 14),
                fa = Ansi(src.fa, 8),
                dc = Ansi(src.dc, 8),
                unit = Ansi(src.unit, 4),
                iprc = src.iprc,
                oprc = src.oprc,
                iprj = src.iprj,
                posi = Ansi(posi, 10),
                depotno = Ansi(depotno, 10),
                dpm = Ansi(src.dpm.Trim(), 10),
                cxnno = Ansi(src.cxnno, 10),
            },
            transaction: tx, cancellationToken: ct));
    }

    private sealed class SourceHead
    {
        public string gname { get; set; } = "";
        public DateTime outdate { get; set; }
    }

    private const string SourceColumns =
        "id, paper, cp, nno, na1, ty, dc, fa, unit, remark, note, depotno, posi, iprc, iprj, oprc, oprct, " +
        "ckamount, yhl, gno, gname, pno, guno, xno, t_mode, ro, dscp, trigger_mark, kcid, dpm, cxnno";

    private sealed class SourceRow
    {
        public int id { get; set; }
        public string paper { get; set; } = "";
        public string cp { get; set; } = "";
        public string nno { get; set; } = "";
        public string na1 { get; set; } = "";
        public string ty { get; set; } = "";
        public string dc { get; set; } = "";
        public string fa { get; set; } = "";
        public string unit { get; set; } = "";
        public string remark { get; set; } = "";
        public string note { get; set; } = "";
        public string depotno { get; set; } = "";
        public string posi { get; set; } = "";
        public decimal iprc { get; set; }
        public decimal iprj { get; set; }
        public decimal oprc { get; set; }
        public decimal oprct { get; set; }
        public decimal ckamount { get; set; }
        public decimal yhl { get; set; }
        public string gno { get; set; } = "";
        public string gname { get; set; } = "";
        public string pno { get; set; } = "";
        public string guno { get; set; } = "";
        public int xno { get; set; }
        public string t_mode { get; set; } = "";
        public decimal ro { get; set; }
        public string dscp { get; set; } = "";
        public bool trigger_mark { get; set; }
        public int kcid { get; set; }
        public string dpm { get; set; } = "";
        public string cxnno { get; set; } = "";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value, IsAnsi = true, Length = length };
}
