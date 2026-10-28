using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Stock;

public interface IStockDistributeRepository
{
    /// <summary>库存分货查询（fr_kcfh setsource：tborder type='历史' AND gyname=?p_dpmname，oqty-qty>0）。</summary>
    Task<IReadOnlyList<StockDistributeOrderRow>> GetOrdersAsync(string dept, string? gnameFilter, string? cnoFilter, CancellationToken ct = default);

    /// <summary>库存分货（fr_kcfh row23）：按勾选订单生成调拨单（type='调拨'，guno='其它'，ypaper=cno）。</summary>
    Task<StockDistributeResult> DistributeAsync(StockDistributeCommand command, string dpm, string pDpm, string pDpmname, string per, CancellationToken ct = default);
}

/// <summary>订单行投影（UI PAGE1 网格数据源）。</summary>
public sealed class StockDistributeOrderRow
{
    public int Id { get; init; }
    public string Gname { get; init; } = "";
    public string Cno { get; init; } = "";
    public string Orno { get; init; } = "";
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Fa { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Oqty { get; init; }
    public decimal Qty { get; init; }
    public DateTime? Orderdate { get; init; }
    public string Depotno { get; init; } = "";
}

/// <summary>
/// 库存分货仓储（fr_kcfh row23 复刻）。旧实现出处（_G.row23.prg_fr_kcfh.txt）：
/// <list type="bullet">
/// <item>权限（L3-10）：OPGSELE=3 无 P_USEM1 '~' 拒绝；=2 无 '-'（人进价）拒绝 —— UI Guard 承接。</item>
/// <item>数据源（L35-39）：tborder o WHERE type='历史' AND gyname=?p_dpmname AND gname=?m.gname AND cno=?m.cno AND oqty-qty&gt;0
///   （勾选订单逐条 UNION，PASSTH 按部门权限；fqty=Σtbsada(gname+ypaper=cno+nno)，kcamount=Σtbisto(dpm=?p_dpm+cp+nno,posi&lt;&gt;废仓)）。</item>
/// <item>L49：DELETE FOR OQTY&lt;=FQTY（已满足订单剔除）。</item>
/// <item>预计算（L51-58）：M_FHAMOUNT=Σ(CP+NNO 组内 oqty-fqty)；FHAMOUNT=MIN(INT(kcamount/M_FHAMOUNT*(oqty-fqty)), oqty-fqty)。</item>
/// <item>模式2（L59-61）：DELETE FOR FHAMOUNT=0（分货量 0 的行剔除；模式1 保留，改为取 MIN(库存,缺口)）。</item>
/// <item>排序（L62）：INDEX ON CP+NNO+TTOC(ORDERDATE)+CNO。</item>
/// <item>取号（L91-101）：T_PAPER 游标 LOCATE FOR DPM —— 每部门一张单复用单号，首次 DJLSH('调拨单',1)。</item>
/// <item>批次（L83-89）：tbisto WHERE dpm=?p_dpm AND cp+nno AND kcamount&gt;0 AND posi&lt;&gt;废仓 [AND depotno 过滤]
///   ORDER BY depotno_or（tbsysset type='库号' name=depotno 的 id，缺失取 MAX(id)），附带
///   lastiprc=tbistoed 同批次最近 indate 的 iprc、oprc=表达式(M_OPRCSTR)、xno/tno/cxnno/note/dc/unit/iprj/iprc/depotno/posi/kcid(id)。</item>
/// <item>逐行（L90-137）：CKAMOUNT=MIN(C_IS.KCAMOUNT, 模式1? oqty-fqty : FHAMOUNT)；INSERT tbsada 31 列
///   （dpm,gname,type,cp,nno,na1,ty,fa,dc,unit,xno,tno,cxnno,note,pnno,pna1,pty,pfa,depotno,posi,iprj,iprc,oprc,oprct,ckamount,paper,ypaper,outdate,guno,per,kcid；
///   m.DPM=P_DPM、m.GNAME=订单 gname、m.YPAPER=订单 cno、m.TYPE='调拨'、m.PAPER=部门单号、m.OUTDATE=P_RQ、m.PER=P_PER、
///   m.KCID=tbisto.id、m.GUNO='其它'、m.PNNO/PNA1/PTY/PFA=订单 nno/na1/ty/fa）；每部门行数不限，单号用完为止（T_PAPER LOCATE 复用）。</item>
/// <item>游标扣减（L133-135）：T_OR.FQTY+=CKAMOUNT / FHAMOUNT-=CKAMOUNT；C_IS.KCAMOUNT-=CKAMOUNT，数量用尽 SKIP 下一批次。</item>
/// <item>结果（L153-164）：无任何行 → 「库存不足，没有生成调拨单！」；有行 → 提示首尾单号。</item>
/// <item>事务（L33-148）：整体 SQLROLLBACK/SQLCOMMIT。</item>
/// <item>契约偏差备案：① 库存分货不扣 tbisto（调拨单走 fr_dbrk 确认时才扣，与旧系统一致——tbsada INSERT 由 sale_in
///   触发器 type='调拨' 分支扣 tbisto，此处 INSERT tbsada 即时触发，新系统两侧同由触发器处理，禁止应用层再扣）。
///   ② M_OPRCSTR 表达式收敛为「基准列×系数 + 取整」白名单（OPGSELE TAG 语义=基准列，TXTXS=系数，CBOYS 简化合并）。</item>
/// </list>
/// </summary>
public sealed class StockDistributeRepository(SqlConnectionFactory factory) : IStockDistributeRepository
{
    private static readonly string[] OprcFields = ["iprc", "oprc", "oprb", "iprj"];

    public async Task<IReadOnlyList<StockDistributeOrderRow>> GetOrdersAsync(string dept, string? gnameFilter, string? cnoFilter, CancellationToken ct = default)
    {
        var where = "o.type = N'历史' AND o.gyname = @dpmname AND o.oqty - o.qty > 0";
        var p = new Dictionary<string, object?> { ["dpmname"] = Ansi(dept.Trim(), 60) };
        if (!string.IsNullOrWhiteSpace(gnameFilter)) { where += " AND o.gname = @gname"; p["gname"] = Ansi(gnameFilter.Trim(), 40); }
        if (!string.IsNullOrWhiteSpace(cnoFilter)) { where += " AND o.cno = @cno"; p["cno"] = Ansi(cnoFilter.Trim(), 15); }
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<StockDistributeOrderRow>(new CommandDefinition(
            $"""
            SELECT o.id, ISNULL(RTRIM(o.gname), '') Gname, RTRIM(o.cno) Cno, RTRIM(o.orno) Orno,
                   RTRIM(o.cp) Cp, RTRIM(o.nno) Nno, RTRIM(o.na1) Na1, RTRIM(o.ty) Ty, RTRIM(o.fa) Fa,
                   RTRIM(o.unit) Unit, o.oqty Oqty, o.qty Qty, o.orderdate Orderdate,
                   ISNULL(RTRIM(o.depotno), '') Depotno
            FROM tborder o WITH (NOLOCK)
            WHERE {where}
            ORDER BY o.cno, o.orderdate, o.id
            """, p, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<StockDistributeResult> DistributeAsync(StockDistributeCommand command, string dpm, string pDpm, string pDpmname, string per, CancellationToken ct = default)
    {
        command.Validate();
        var oprcField = command.OprcField.Trim().ToLowerInvariant();
        if (!OprcFields.Contains(oprcField))
            throw new AppException("OPRC_FIELD_INVALID", "出库价基准列仅支持 iprc/oprc/oprb/iprj", 422);
        // L18-26：M_OPRCSTR = 基准列 × 系数，按取整方式包裹
        var baseExpr = $"i.{oprcField} * @oprcFactor";
        var oprcExpr = command.RoundMode switch
        {
            2 => $"CAST(ROUND({baseExpr}, 0, 1) AS numeric(18,4))",   // round(x,0)
            3 => $"CAST(CEILING({baseExpr}) AS numeric(18,4))",       // ceiling(x)
            _ => $"CAST({baseExpr} AS numeric(18,4))",                // 保留小数
        };

        var fpcw = await GetFpcwAsync(ct);
        var today = DateTime.Today;

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 取 tbsysset type='库号' 挑选条件（L69-74，生产未配置则无附加条件）
            // 契约：tbxzb.variable 为 VFP 表达式（如 dpm=?p_dpm），?p_xxx 参数化（语义同 UniversalQueryRepository.AppendXzb）
            var xzbRaw = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT RTRIM(variable) FROM tbxzb WITH (NOLOCK) WHERE tablename = N'tbsysset'", transaction: tx, cancellationToken: ct)) ?? "";
            var xzb = System.Text.RegularExpressions.Regex.Replace(xzbRaw, @"\?p_(\w+)", "@xzb_$1");
            var xzbParams = new Dictionary<string, object?>();
            if (xzb.Contains("@xzb_dpm")) xzbParams["xzb_dpm"] = Ansi(pDpm, 10);

            // L35-47：勾选订单聚合（逐条 gname+cno，fqty/kcamount 子查询）
            var orders = new List<OrderRow>();
            foreach (var o in command.Orders)
            {
                var rows = (await conn.QueryAsync<OrderRow>(new CommandDefinition(
                    $"""
                    SELECT o.id, o.oqty, o.qty, ISNULL(RTRIM(o.gname), '') gname, RTRIM(o.cno) cno,
                           RTRIM(o.cp) cp, RTRIM(o.nno) nno, RTRIM(o.na1) na1, RTRIM(o.ty) ty, RTRIM(o.fa) fa,
                           o.orderdate, RTRIM(o.unit) unit,
                           fqty = ISNULL((SELECT SUM(s.ckamount) FROM tbsada s WITH (NOLOCK)
                                          WHERE s.gname = o.gname AND s.ypaper = o.cno AND s.nno = o.nno), 0),
                           kcamount = ISNULL((SELECT SUM(t.kcamount) FROM tbisto t WITH (NOLOCK)
                                              WHERE t.dpm = @pDpm AND t.cp = o.cp AND t.nno = o.nno
                                                AND t.posi <> @fpcw), 0)
                    FROM tborder o WITH (UPDLOCK)
                    WHERE o.type = N'历史' AND o.gyname = @dpmname
                      AND o.gname = @gname AND o.cno = @cno AND o.oqty - o.qty > 0
                    """,
                    new { pDpm = Ansi(pDpm, 10), fpcw = Ansi(fpcw, 10), dpmname = Ansi(pDpmname, 60),
                          gname = Ansi(o.Gname, 40), cno = Ansi(o.Cno, 15), },
                    transaction: tx, cancellationToken: ct))).AsList();
                orders.AddRange(rows);
            }
            if (orders.Count == 0)
                throw new AppException("NO_ORDER_DATA", "没有可分货的订单数据！", 422);

            // L49：已满足订单剔除
            orders = orders.Where(o => o.oqty > o.fqty).ToList();
            if (orders.Count == 0)
                return new StockDistributeResult();

            // L51-58：组内预计算 fhamount
            var groups = orders.GroupBy(o => (o.cp, o.nno)).ToDictionary(g => g.Key, g => g.Sum(x => x.oqty - x.fqty));
            foreach (var o in orders)
            {
                o.xqamou = o.oqty - o.fqty;
                var total = groups[(o.cp, o.nno)];
                o.fhamount = Math.Min(Math.Floor(o.kcamount / total * (o.oqty - o.fqty)), o.oqty - o.fqty);
            }
            // L59-61：模式2 剔除分货量为 0 的行
            if (command.Mode == 2)
                orders = orders.Where(o => o.fhamount > 0).ToList();

            // L62：排序 CP+NNO+ORDERDATE+CNO
            orders = orders.OrderBy(o => o.cp).ThenBy(o => o.nno).ThenBy(o => o.orderdate ?? DateTime.MaxValue).ThenBy(o => o.cno).ToList();

            var result = new StockDistributeResult();
            var deptPapers = new Dictionary<string, string>();   // T_PAPER 游标：每部门单号复用

            foreach (var o in orders)
            {
                // L83-89：源批次（depotno_or 排序 = tbsysset type='库号' id，缺失 MAX(id)）
                var depotFilter = string.IsNullOrWhiteSpace(command.Depotno) ? "" : " AND i.depotno = @depotno";
                var batches = (await conn.QueryAsync<BatchRow>(new CommandDefinition(
                    $"""
                    SELECT i.id, RTRIM(i.depotno) depotno, RTRIM(i.posi) posi, i.kcamount,
                           i.iprj, i.iprc, RTRIM(i.dc) dc, i.tno, i.xno, RTRIM(i.cxnno) cxnno, RTRIM(i.note) note, RTRIM(i.unit) unit,
                           oprc = {oprcExpr},
                           lastiprc = ISNULL((SELECT TOP (1) e.iprc FROM tbistoed e WITH (NOLOCK)
                                              WHERE e.dpm = i.dpm AND e.cp = i.cp AND e.nno = i.nno
                                                AND e.indate = ISNULL((SELECT MAX(e2.indate) FROM tbistoed e2 WITH (NOLOCK)
                                                                       WHERE e2.dpm = i.dpm AND e2.cp = i.cp AND e2.nno = i.nno), '')), 0),
                           depotno_or = ISNULL((SELECT TOP (1) s.id FROM tbsysset s WITH (NOLOCK)
                                                WHERE s.type = N'库号' AND s.name = i.depotno AND ({xzb})),
                                               (SELECT MAX(s2.id) FROM tbsysset s2 WITH (NOLOCK)))
                    FROM tbisto i WITH (UPDLOCK)
                    WHERE i.dpm = @pDpm AND i.cp = @cp AND i.nno = @nno AND i.kcamount > 0 AND i.posi <> @fpcw{depotFilter}
                    ORDER BY depotno_or
                    """,
                    ToMerged(new { pDpm = Ansi(pDpm, 10), cp = Ansi(o.cp, 10), nno = Ansi(o.nno, 18), fpcw = Ansi(fpcw, 10),
                           depotno = Ansi((command.Depotno ?? "").Trim(), 10), oprcFactor = command.OprcFactor }, xzbParams),
                    transaction: tx, cancellationToken: ct))).AsList();

                var bi = 0;
                while (bi < batches.Count && o.oqty > o.fqty)
                {
                    var b = batches[bi];
                    if (b.kcamount <= 0) { bi++; continue; }

                    // L91-101：每部门一张单复用单号
                    if (!deptPapers.TryGetValue(pDpm, out var paper))
                    {
                        paper = await NextPaperAsync(conn, tx, pDpm, ct);
                        deptPapers[pDpm] = paper;
                        result.Papers.Add(paper);
                    }

                    // L103：CKAMOUNT = MIN(批次库存, 模式1?订单缺口:预计算量)
                    var want = command.Mode == 1 ? o.oqty - o.fqty : o.fhamount;
                    var ckamount = Math.Min(b.kcamount, want);
                    if (ckamount <= 0) break;

                    // L127：INSERT tbsada 31 列（sale_in 触发器 type='调拨' 即时扣 tbisto）
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbsada
                            (dpm, gname, type, cp, nno, na1, ty, fa, dc, unit, xno, tno, cxnno, note,
                             pnno, pna1, pty, pfa, depotno, posi, iprj, iprc, oprc, oprct, ckamount,
                             paper, ypaper, outdate, guno, per, kcid)
                        VALUES
                            (@dpm, @gname, N'调拨', @cp, @nno, @na1, @ty, @fa, @dc, @unit, @xno, @tno, @cxnno, @note,
                             @pnno, @pna1, @pty, @pfa, @depotno, @posi, @iprj, @iprc, @oprc, @oprc, @ckamount,
                             @paper, @ypaper, @outdate, N'其它', @per, @kcid)
                        """,
                        new
                        {
                            dpm = Ansi(pDpm, 10), gname = Ansi(o.gname, 40), cp = Ansi(o.cp, 10), nno = Ansi(o.nno, 18),
                            na1 = Ansi(o.na1, 40), ty = Ansi(o.ty, 14), fa = Ansi(o.fa, 10), dc = Ansi(b.dc, 8),
                            unit = Ansi(b.unit, 4), b.xno, b.tno, cxnno = Ansi(b.cxnno, 18), note = Ansi(b.note, 100),
                            pnno = Ansi(o.nno, 18), pna1 = Ansi(o.na1, 40), pty = Ansi(o.ty, 14), pfa = Ansi(o.fa, 10),
                            depotno = Ansi(b.depotno, 10), posi = Ansi(b.posi, 10),
                            b.iprj, b.iprc, b.oprc, ckamount, paper = Ansi(paper, 15),
                            ypaper = Ansi(o.cno, 15), outdate = today, per = Ansi(per, 6), kcid = b.id,
                        },
                        transaction: tx, cancellationToken: ct));

                    // L133-135：游标扣减
                    o.fqty += ckamount;
                    o.fhamount -= ckamount;
                    b.kcamount -= ckamount;
                    result.LastPaper = paper;
                    result.LineCount++;
                }
            }

            if (result.LineCount > 0)
            {
                result.FirstPaper = result.Papers[0];
            }
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>废品仓位标识（tbsysset type='system' name='废品仓位标识'，缺省 '废'）。</summary>

    /// <summary>匿名参数对象转字典并合并附加参数（tbxzb 变量注入用）。</summary>
    private static Dictionary<string, object?> ToMerged(object anon, Dictionary<string, object?> extra)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in anon.GetType().GetProperties())
            d[p.Name] = p.GetValue(anon);
        foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    private async Task<string> GetFpcwAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var v = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT valuec FROM tbsysset WITH (NOLOCK) WHERE type = N'system' AND name = N'废品仓位标识'",
            cancellationToken: ct));
        return string.IsNullOrWhiteSpace(v) ? "废" : v.Trim();
    }

    /// <summary>DJLSH('调拨单',1) 应用层拼号（djjx+年2位+6位流水，模式同 StockAdjustRepository.NextPaperAsync）。</summary>
    private static async Task<string> NextPaperAsync(SqlConnection conn, SqlTransaction tx, string dept, CancellationToken ct)
    {
        var row = await conn.QueryFirstOrDefaultAsync<(int id, string djjx, int lsh, string dyear)>(new CommandDefinition(
            """
            SELECT id, RTRIM(djjx) AS djjx, lsh, RTRIM(dyear) AS dyear
            FROM tbdjgs WITH (UPDLOCK, ROWLOCK)
            WHERE djname = N'调拨单' AND dpm = @dpm
            """,
            new { dpm = Ansi(dept.Trim(), 10) }, transaction: tx, cancellationToken: ct));
        if (row.id == 0)
            throw new AppException("DOC_NO_FAILED", $"取单号失败：单据类型「调拨单」部门「{dept.Trim()}」未在 tbdjgs 配置前缀", 422);
        var year = DateTime.Now.Year.ToString();
        var lsh = row.dyear == year ? row.lsh + 1 : 1;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbdjgs SET dyear = @dyear, lsh = @lsh WHERE id = @id",
            new { dyear = Ansi(year, 4), lsh, id = row.id }, transaction: tx, cancellationToken: ct));
        return row.djjx + year[^2..] + lsh.ToString("D6");
    }

    /// <summary>订单行（含分货中间量）。</summary>
    private sealed class OrderRow
    {
        public int id { get; set; }
        public decimal oqty { get; set; }
        public decimal qty { get; set; }
        public string gname { get; set; } = "";
        public string cno { get; set; } = "";
        public string cp { get; set; } = "";
        public string nno { get; set; } = "";
        public string na1 { get; set; } = "";
        public string ty { get; set; } = "";
        public string fa { get; set; } = "";
        public string unit { get; set; } = "";
        public DateTime? orderdate { get; set; }
        public decimal fqty { get; set; }
        public decimal kcamount { get; set; }
        public decimal xqamou { get; set; }
        public decimal fhamount { get; set; }
    }

    /// <summary>tbisto 批次行。</summary>
    private sealed class BatchRow
    {
        public int id { get; set; }
        public string depotno { get; set; } = "";
        public string posi { get; set; } = "";
        public decimal kcamount { get; set; }
        public decimal iprj { get; set; }
        public decimal iprc { get; set; }
        public decimal oprc { get; set; }
        public string dc { get; set; } = "";
        public int tno { get; set; }
        public int xno { get; set; }
        public string cxnno { get; set; } = "";
        public string note { get; set; } = "";
        public string unit { get; set; } = "";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
