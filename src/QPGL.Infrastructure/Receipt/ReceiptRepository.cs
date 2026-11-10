using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Receipt;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Receipt;

public interface IReceiptRepository
{
    /// <summary>付款方式清单（tbsysset WHERE type='guno'）。</summary>
    Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(CancellationToken ct = default);

    /// <summary>
    /// 系统开关「启用挂账支付方」（tbcskg name='启用挂账支付方'，旧 CSKG 口径：valuel=1 为开）。
    /// 关闭时收款窗挂帐支付方置灰；确认时忽略支付方、挂账必须有客户名。
    /// </summary>
    Task<bool> GetPayerEnabledAsync(CancellationToken ct = default);

    /// <summary>收款确认（销售单等出库单；触发 tbgath/tbysyf 写入序列）。</summary>
    Task<ConfirmReceiptResult> ConfirmAsync(ConfirmReceiptCommand command, string per, CancellationToken ct = default);

    /// <summary>收款反确认（还原；挂账已收/分批发货拒绝）。</summary>
    Task CancelAsync(string paper, string type, string dept, string per, CancellationToken ct = default);
}

/// <summary>
/// 收款结算仓储（M3-3，写路径契约感知）。
/// 旧实现出处（fr_sk）：
///   row358 确认 —— UPDATE tbsada SET qrdate,mark='Y',guno,per1 WHERE type+paper（无 dpm 条件，照抄）
///   row369 L396-403 —— guno = ABS(PAYAMT) 最大的方式；全 0 回退 CBOFKFS；为空取「现金」
///   row369 L414/L421 —— jianshou = TEXT2（网格金额失焦自动算，见 __.row380 / __.row3 L76）
///   row369 L447/L449/L452 —— pno = CBO_PNO(1 位前缀 */Y/N)+TXTPAYNO；UPDATE tbsada 追加 pno/t_mode
///   row369 L470 —— 先 DELETE tbgath WHERE type+cno（重确认幂等）
///   row369 L471-484 —— 逐非 0 行 INSERT tbgath(Type,gdate,cno,payterm,amt,per,payno,dpm)；
///                  信用类 amt=PAYAMT，其余 GETJE(PAYAMT)（本部署 GETJE='1' 恒等，见下）
///   row369 L485-534 —— 信用类逐行写 tbysyf：ys=amt、pno=确认后pno+' '+per、xm=payterm+' '+t_mode+'  '+备注、
///                  skmoney=AUTOJZ 或挂帐冲预收?ys:0；无客户行先 INSERT(gname,dpm) 空行
///   row369 L535-539 —— 每个信用行内，若 TAMT-TOPRC≠0 再插 PNO='KH'、ys=差额（skmoney 未重置，沿用该信用行）
///   row369 L559-576 —— 发票号号码本体非空：tbfpgl upsert（pno=号码本体，不含前缀；fpje=ΣPAYAMT；
///                  gname=客户/挂帐支付方；djno+ctype='销售'+gname 命中则更新，否则插入）
///   row369 L485-496/L561 —— gname：仅「挂帐」且支付方非空时取支付方（旧 TXTGZGNAME），其余取客户名；
///                  支付方与客户名相同则提示「客户挂帐到自己的名下时，无需输入挂帐支付方」并按客户名处理；
///                  开关 tbcskg.name='启用挂账支付方'（旧 CSKG）关闭时忽略支付方。支付方不落库、重开窗为空。
///   row34  还原 —— mark≠'Y' 拒绝；任一行 fhdate&gt;'1900-01-01' 拒绝；挂账类且 tbysyf.skmoney≠0 拒绝；
///                  UPDATE tbsada SET mark='',per1='',qrdate=1900,jianshou=0 WHERE type+paper+dpm；
///                  DELETE tbgath / tbysyf WHERE type+cno+dpm
/// 取整：GETJE 读 tbsysset type='xs' name='销售金额计算公式'（pg_proc L3254-3255；'1' 不取整，默认），
///       本部署/本工程沿用 '1' 恒等口径（DbrkRepository/FinOpVouch 同）；金额列按 2 位小数。
/// </summary>
public sealed class ReceiptRepository(SqlConnectionFactory factory) : IReceiptRepository
{
    public async Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            "SELECT RTRIM(name) AS name, valuen FROM tbsysset WITH (NOLOCK) WHERE type = 'guno' ORDER BY id",
            cancellationToken: ct);
        return (await conn.QueryAsync<PaymentMethod>(cmd)).AsList();
    }

    public async Task<bool> GetPayerEnabledAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await ReadPayerEnabledAsync(conn, null, ct);
    }

    /// <summary>
    /// 旧 CSKG('启用挂账支付方')：tbcskg.name='启用挂账支付方' 且 valuel=1 才为开（无行/valuel≠1 → 关）。
    /// </summary>
    private static Task<bool> ReadPayerEnabledAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM tbcskg WITH (NOLOCK)
                WHERE RTRIM(name) = N'启用挂账支付方' AND valuel = 1
            ) THEN 1 ELSE 0 END
            """,
            transaction: tx, cancellationToken: ct));

    public async Task<ConfirmReceiptResult> ConfirmAsync(ConfirmReceiptCommand command, string per, CancellationToken ct = default)
    {
        var paper = (command.Paper ?? "").Trim();
        var type = (command.Type ?? "销售").Trim();
        var payTerm = (command.PayTerm ?? "").Trim();
        if (paper.Length == 0) throw new AppException("DOC_NOT_FOUND", "单号不能为空", 422);
        if (payTerm.Length == 0) throw new AppException("PAYTERM_REQUIRED", "请选择付款方式", 422);

        // 付款明细（旧 C_GUNO 逐行 PAYAMT）：金额列 2 位小数；GETJE='1' 恒等，不再二次取整。
        var lines = (command.Lines ?? [])
            .Select(l => new ReceiptPayLine
            {
                PayTerm = (l.PayTerm ?? "").Trim(),
                Amount = ReceiptSettlement.RoundMoney(l.Amount),
            })
            .ToList();
        var sumPay = lines.Sum(l => l.Amount);
        var mainPayTerm = ReceiptSettlement.MainPayTerm(lines, payTerm);   // 旧 L396-403
        var jianshou = ReceiptSettlement.RoundMoney(command.Jianshou);      // 旧 L414

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 1) 单据汇总（UPDLOCK 防并发收款）
            //    TAMT/TOPRC = 旧 c_ck 聚合口径：ROUND(Σ ckamount*金额*(100-yhl)/100, 2)（fr_sk __.row3 L76）
            var doc = await conn.QueryFirstOrDefaultAsync<(string gname, string pno, string t_mode, string remark,
                string mark, DateTime outdate, decimal tamt, decimal toprc, string spname)>(
                new CommandDefinition(
                    """
                    SELECT MAX(c.gname) gname, MAX(c.pno) pno, MAX(c.t_mode) t_mode, MAX(c.remark) remark,
                           MAX(c.mark) mark, MAX(c.outdate) outdate,
                           CAST(SUM(c.oprct * c.ckamount * (100 - c.yhl) / 100.0) AS numeric(12,2)) tamt,
                           CAST(SUM(c.oprc * c.ckamount * (100 - c.yhl) / 100.0) AS numeric(12,2)) toprc,
                           ISNULL(MAX(h.spname), '') spname
                    FROM tbsada c WITH (UPDLOCK)
                    LEFT JOIN (SELECT paper, spname FROM tbsada_header WITH (NOLOCK)) h ON h.paper = c.paper
                    WHERE c.paper = @paper AND c.type = @type
                    """,
                    new
                    {
                        paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                        type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
            if (doc.gname is null)
                throw new AppException("DOC_NOT_FOUND", $"单据 {paper} 不存在", 404);
            // 旧 row33 CASE 1 前置：仅 mark 非空 → "该单已做收款！"（开单中 mark=''且guno='' 由 UI 拦）。
            // guno 非空 mark='' 是开单时默认的付款方式，旧系统允许确认覆盖（row358/row369 均直接 UPDATE guno），
            // 不能作为"已确认"拦截（否则部署库开单默认 guno 的单永远无法确认）。
            if (doc.mark.Trim().Length != 0)
                throw new AppException("DOC_ALREADY_CONFIRMED", $"单据 {paper} 已做收款", 409);

            // 1b) 发票号/发货方式（旧 row369 L447/L452、L409/L421/L449/L524）
            //     pno = CBO_PNO(1 位前缀) + TXTPAYNO(号码本体)，整单写回 tbsada.pno；
            //     t_mode 取收款窗下拉值（字典 tbsysset type='fhfs'），同时用于 tbysyf.xm。
            //     未传 pno 时保留单据原值，未传 t_mode 时保留单据原值（避免非 UI 调用清空）。
            var newPno = (command.Pno ?? "").Trim().Length > 0 ? command.Pno!.Trim() : doc.pno.Trim();
            var tMode = (command.TMode ?? "").Trim().Length > 0 ? command.TMode!.Trim() : doc.t_mode.Trim();
            // 备注只进应收行（tbysyf.xm）；null 视为未传沿用单据原备注，空串视为用户清空。旧 TEXTREMARK 初值=C_CK.REMARK。
            var remark = command.Remark is null ? doc.remark.Trim() : command.Remark.Trim();

            // 1c) 挂账支付方（旧 TXTGZGNAME；CSKG('启用挂账支付方')；row369 L485-496/L561）
            //     开关关闭：忽略支付方，挂账必须有客户名（沿用旧提示口径）；
            //     开关开启：客户名或支付方至少一个非空；支付方仅「挂帐」生效，与客户名相同按客户名处理。
            var payerEnabled = await ReadPayerEnabledAsync(conn, tx, ct);
            var customerName = doc.gname.Trim();
            var payer = payerEnabled ? (command.Payer ?? "").Trim() : "";
            var writeLines = lines.Where(l => l.Amount != 0m).ToList();
            var creditLines = writeLines
                .Where(l => PaymentTerms.WritesReceivable(l.PayTerm))
                .ToList();
            var onCredit = creditLines.Count > 0;
            var diff = doc.tamt - doc.toprc;   // 旧 TAMT-TOPRC
            if (ReceiptSettlement.RequiresCustomerName(onCredit, customerName, payerEnabled, payer))
                throw new AppException("CUSTOMER_REQUIRED",
                    payerEnabled
                        ? "客户名称或挂帐支付方为空，必须其中一个不为空才能挂帐！"
                        : "客户名称为空，不能挂帐！", 422);
            // 挂账行生效名：挂帐且支付方非空 → 支付方，否则客户名（旧 L485-496）；gname 列 char(60)
            var ysyfGname = AnsiBytes.Truncate(
                ReceiptSettlement.EffectiveGname(customerName, payer, creditLines.FirstOrDefault()?.PayTerm),
                60);
            // 发票台账 gname 按主付款方式取生效名（旧 L561），tbfpgl.gname 仅 50 字符
            var fpglGname = AnsiBytes.Truncate(
                ReceiptSettlement.EffectiveGname(customerName, payer, mainPayTerm),
                50);


            // 2) gdate：系统开关「财务确认收款日期是否取确认日期」
            var takeConfirmDate = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT ISNULL(valuel, 0) FROM tbsysset WITH (NOLOCK) WHERE type = 'system' AND name = N'财务确认收款日期是否取确认日期'",
                transaction: tx, cancellationToken: ct));
            var gdate = takeConfirmDate ? DateTime.Now : doc.outdate;

            // 3) 确认（旧 row358：WHERE type+paper，无 dpm；旧 row369 L421/L449 追加 pno/t_mode/jianshou）
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbsada SET qrdate = GETDATE(), mark = 'Y', guno = @guno, per1 = @per,
                                  pno = @pno, t_mode = @t_mode, jianshou = @jianshou
                WHERE type = @type AND paper = @paper
                """,
                new
                {
                    guno = new DbString { Value = mainPayTerm, IsAnsi = false, Length = 10 },
                    per = new DbString { Value = per, IsAnsi = true, Length = 10 },
                    pno = new DbString { Value = newPno, IsAnsi = false, Length = 50 },
                    t_mode = new DbString { Value = tMode, IsAnsi = false, Length = 10 },
                    jianshou,
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));

            // 4) 台账重置 + 逐非 0 行写入（旧 L470-484）
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbgath WHERE type = @type AND cno = @paper",
                new
                {
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
            foreach (var line in lines)
            {
                if (line.Amount == 0m) continue;   // 旧 L474-477
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbgath (type, gdate, cno, payterm, amt, per, payno, dpm)
                    VALUES (@type, @gdate, @cno, @payterm, @amt, @per, @payno, @dpm)
                    """,
                    new
                    {
                        type = new DbString { Value = type, IsAnsi = false, Length = 4 },
                        gdate,
                        cno = new DbString { Value = paper, IsAnsi = true, Length = 30 },
                        payterm = new DbString { Value = line.PayTerm, IsAnsi = false, Length = 10 },
                        amt = line.Amount,
                        per = new DbString { Value = per, IsAnsi = true, Length = 10 },
                        payno = new DbString { Value = command.PayNo ?? "", IsAnsi = true, Length = 15 },
                        dpm = new DbString { Value = command.Dept, IsAnsi = true, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
            }

            // 5) 应收应付：新口径仅「挂帐」行写 tbysyf；旧 L485 信用类分支
            if (onCredit)
            {
                // 5a. 客户既有 cb/tick/yjb（旧 L498-510：无客户行先插空行）
                var prior = await conn.QueryFirstOrDefaultAsync<(decimal cb, int tick, decimal yjb)>(new CommandDefinition(
                    "SELECT TOP (1) cb, tick, yjb FROM tbysyf WITH (NOLOCK) WHERE gname = @gname ORDER BY datime",
                    new { gname = new DbString { Value = ysyfGname, IsAnsi = false, Length = 60 } },
                    transaction: tx, cancellationToken: ct));
                if (prior.cb == 0 && prior.tick == 0 && prior.yjb == 0)
                {
                    var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname",
                        new { gname = new DbString { Value = ysyfGname, IsAnsi = false, Length = 60 } },
                        transaction: tx, cancellationToken: ct));
                    if (exists == 0)
                        await conn.ExecuteAsync(new CommandDefinition(
                            "INSERT INTO tbysyf (gname, dpm) VALUES (@gname, @dpm)",
                            new
                            {
                                gname = new DbString { Value = ysyfGname, IsAnsi = false, Length = 60 },
                                dpm = new DbString { Value = command.Dept, IsAnsi = true, Length = 10 },
                            },
                            transaction: tx, cancellationToken: ct));
                }

                // 5b. 逐信用行写应收（旧 L522-534）
                foreach (var line in creditLines)
                {
                    var xm = $"{line.PayTerm} {tMode}  {remark}".TrimEnd();
                    var skmoney = command.AutoSettle || line.PayTerm == "挂帐冲预收" ? line.Amount : 0m;
                    await InsertYsyfAsync(conn, tx, ct, new YsyfRow
                    {
                        Gname = ysyfGname,
                        Datime = gdate,
                        Cno = paper,
                        Ys = line.Amount,
                        Pno = $"{newPno} {per}".Trim(),
                        Fkfs = line.PayTerm,
                        Type = type,
                        Cb = prior.cb,
                        Tick = prior.tick,
                        Yjb = prior.yjb,
                        Xm = xm,
                        Per = per,
                        Skmoney = skmoney,
                        Spname = doc.spname.Trim(),
                        Dpm = command.Dept,
                    });

                    // 5c. 差额行（旧 L535-539）：每个信用行内判断；skmoney 未重置 → 沿用信用行
                    if (ReceiptSettlement.ShouldWriteDiscountRow(doc.tamt, doc.toprc, true))
                    {
                        await InsertYsyfAsync(conn, tx, ct, new YsyfRow
                        {
                            Gname = ysyfGname,
                            Datime = gdate,
                            Cno = paper,
                            Ys = diff,
                            Pno = "KH",
                            Fkfs = line.PayTerm,
                            Type = type,
                            Cb = prior.cb,
                            Tick = prior.tick,
                            Yjb = prior.yjb,
                            Xm = xm,
                            Per = per,
                            Skmoney = skmoney,
                            Spname = doc.spname.Trim(),
                            Dpm = command.Dept,
                        });
                    }
                }
            }

            // 6) 发票台账（旧 L559-576）：号码本体非空 → tbfpgl upsert；pno 只存本体，不含前缀
            var payNoBody = (command.PayNo ?? "").Trim();
            if (ReceiptSettlement.ShouldWriteInvoiceLedger(payNoBody))
            {
                var ctype = "销售";   // 旧 L567 固定
                var fpId = await conn.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
                    "SELECT TOP (1) id FROM tbfpgl WITH (NOLOCK) WHERE djno = @djno AND ctype = @ctype AND gname = @gname",
                    new
                    {
                        djno = new DbString { Value = paper, IsAnsi = true, Length = 30 },
                        ctype = new DbString { Value = ctype, IsAnsi = false, Length = 10 },
                        gname = new DbString { Value = fpglGname, IsAnsi = false, Length = 50 },
                    },
                    transaction: tx, cancellationToken: ct));
                if (fpId.HasValue)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE tbfpgl SET pno = @pno, gname = @gname, fpdate = @fpdate, fpje = @fpje,
                                         jsr = @jsr, ctype = @ctype, fptype = '', note = '', fkfs = @fkfs
                        WHERE id = @id
                        """,
                        new
                        {
                            pno = new DbString { Value = payNoBody, IsAnsi = true, Length = 50 },
                            gname = new DbString { Value = fpglGname, IsAnsi = false, Length = 50 },
                            fpdate = DateTime.Now,
                            fpje = sumPay,
                            jsr = new DbString { Value = per, IsAnsi = true, Length = 10 },
                            ctype = new DbString { Value = ctype, IsAnsi = false, Length = 10 },
                            fkfs = new DbString { Value = mainPayTerm, IsAnsi = false, Length = 10 },
                            id = fpId.Value,
                        },
                        transaction: tx, cancellationToken: ct));
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbfpgl (pno, gname, fpdate, fpje, jsr, djno, ctype, fptype, note, fkfs, dpm)
                        VALUES (@pno, @gname, @fpdate, @fpje, @jsr, @djno, @ctype, '', '', @fkfs, @dpm)
                        """,
                        new
                        {
                            pno = new DbString { Value = payNoBody, IsAnsi = true, Length = 50 },
                            gname = new DbString { Value = fpglGname, IsAnsi = false, Length = 50 },
                            fpdate = DateTime.Now,
                            fpje = sumPay,
                            jsr = new DbString { Value = per, IsAnsi = true, Length = 10 },
                            djno = new DbString { Value = paper, IsAnsi = true, Length = 30 },
                            ctype = new DbString { Value = ctype, IsAnsi = false, Length = 10 },
                            fkfs = new DbString { Value = mainPayTerm, IsAnsi = false, Length = 10 },
                            dpm = new DbString { Value = command.Dept, IsAnsi = true, Length = 10 },
                        },
                        transaction: tx, cancellationToken: ct));
                }
            }

            // 7) 回读断言（§7.3）
            var after = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT TOP (1) mark FROM tbsada WITH (NOLOCK) WHERE paper = @paper AND type = @type",
                new
                {
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
            if (after?.Trim() != "Y")
                throw new AppException("CONTRACT_VIOLATION", $"收款断言失败：{paper} mark 期望 Y 实际 {after}（已回滚）", 500);

            await tx.CommitAsync(ct);
            return new ConfirmReceiptResult
            {
                Paper = paper,
                Amount = sumPay,
                Gdate = gdate,
                OnCredit = onCredit,
                DiffAmount = onCredit ? diff : 0,
            };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task CancelAsync(string paper, string type, string dept, string per, CancellationToken ct = default)
    {
        paper = (paper ?? "").Trim();
        type = (type ?? "").Trim();
        if (paper.Length == 0) throw new AppException("DOC_NOT_FOUND", "单号不能为空", 422);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var doc = await conn.QueryFirstOrDefaultAsync<(string mark, string guno, DateTime max_fhdate)>(
                new CommandDefinition(
                    """
                    SELECT MAX(mark) mark, MAX(guno) guno, MAX(ISNULL(fhdate, '1900-01-01')) max_fhdate
                    FROM tbsada WITH (UPDLOCK)
                    WHERE paper = @paper AND type = @type AND dpm = @dpm
                    """,
                    new
                    {
                        paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                        type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                        dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
            if (doc.mark is null)
                throw new AppException("DOC_NOT_FOUND", $"单据 {paper} 不存在", 404);
            if (doc.mark.Trim() != "Y")
                throw new AppException("DOC_NOT_CONFIRMED", "该单已不是已收款状态，请刷新数据", 409);
            if (doc.max_fhdate > new DateTime(1900, 1, 1))
                throw new AppException("DOC_SHIPPED", "该单已分批发货，不能还原收款", 409);
            if (ReceiptGuards.IsOnCredit(doc.guno))
            {
                var financed = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    SELECT COUNT(1) FROM tbysyf WITH (NOLOCK)
                    WHERE cno = @paper AND skmoney <> 0 AND dpm = @dpm AND fkfs <> N'挂帐冲预收'
                    """,
                    new
                    {
                        paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                        dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 },
                    },
                    transaction: tx, cancellationToken: ct));
                if (financed > 0)
                    throw new AppException("RECEIPT_FINANCED", "此单已经做了财务收款，不能还原", 409);
            }

            // 还原（旧 row34 L60/L62/L63）
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbsada SET mark = '', per1 = '', qrdate = '1900-01-01', jianshou = 0
                WHERE type = @type AND paper = @paper AND dpm = @dpm
                """,
                new
                {
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                    dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbgath WHERE type = @type AND cno = @paper AND dpm = @dpm",
                new
                {
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                    dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbysyf WHERE type = @type AND cno = @paper AND dpm = @dpm",
                new
                {
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                    dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));

            // 回读断言
            var after = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT TOP (1) mark FROM tbsada WITH (NOLOCK) WHERE paper = @paper AND type = @type AND dpm = @dpm",
                new
                {
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
            if (after?.Trim().Length != 0)
                throw new AppException("CONTRACT_VIOLATION", $"反确认断言失败：{paper} mark 期望空 实际 {after}（已回滚）", 500);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task InsertYsyfAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct, YsyfRow r)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tbysyf (gname, datime, cno, ys, pno, fkfs, type, cb, tick, xm, per, skmoney, spname, dpm, yjb)
            VALUES (@gname, @datime, @cno, @ys, @pno, @fkfs, @type, @cb, @tick, @xm, @per, @skmoney, @spname, @dpm, @yjb)
            """,
            new
            {
                gname = new DbString { Value = r.Gname, IsAnsi = false, Length = 60 },
                datime = r.Datime,
                cno = new DbString { Value = r.Cno, IsAnsi = true, Length = 30 },
                ys = r.Ys,
                pno = new DbString { Value = r.Pno, IsAnsi = false, Length = 50 },
                fkfs = new DbString { Value = r.Fkfs, IsAnsi = false, Length = 10 },
                type = new DbString { Value = r.Type, IsAnsi = false, Length = 8 },
                cb = r.Cb,
                tick = r.Tick,
                xm = new DbString { Value = r.Xm, IsAnsi = false, Length = 250 },
                per = new DbString { Value = r.Per, IsAnsi = true, Length = 10 },
                skmoney = r.Skmoney,
                spname = new DbString { Value = r.Spname, IsAnsi = false, Length = 60 },
                dpm = new DbString { Value = r.Dpm, IsAnsi = true, Length = 10 },
                yjb = r.Yjb,
            },
            transaction: tx, cancellationToken: ct));
    }

    private sealed class YsyfRow
    {
        public string Gname { get; init; } = "";
        public DateTime Datime { get; init; }
        public string Cno { get; init; } = "";
        public decimal Ys { get; init; }
        public string Pno { get; init; } = "";
        public string Fkfs { get; init; } = "";
        public string Type { get; init; } = "";
        public decimal Cb { get; init; }
        public int Tick { get; init; }
        public decimal Yjb { get; init; }
        public string Xm { get; init; } = "";
        public string Per { get; init; } = "";
        public decimal Skmoney { get; init; }
        public string Spname { get; init; } = "";
        public string Dpm { get; init; } = "";
    }
}
