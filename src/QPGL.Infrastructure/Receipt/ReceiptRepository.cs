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

    /// <summary>收款确认（销售单等出库单；触发 tbgath/tbysyf 写入序列）。</summary>
    Task<ConfirmReceiptResult> ConfirmAsync(ConfirmReceiptCommand command, string per, CancellationToken ct = default);

    /// <summary>收款反确认（还原；挂账已收/分批发货拒绝）。</summary>
    Task CancelAsync(string paper, string type, string dept, string per, CancellationToken ct = default);
}

/// <summary>
/// 收款结算仓储（M3-3，写路径契约感知）。
/// 旧实现出处（fr_sk）：
///   row358 确认 —— UPDATE tbsada SET qrdate,mark='Y',guno,per1 WHERE type+paper（无 dpm 条件，照抄）
///   row369 L460 —— gdate = tbsysset「财务确认收款日期是否取确认日期」? qrdate : outdate
///   row369 L470 —— 先 DELETE tbgath WHERE type+cno（重确认幂等）
///   row369 L484 —— INSERT tbgath(Type,gdate,cno,payterm,amt,per,payno,dpm)
///   row369 L479 —— 挂账类 INLIST(payterm,'挂帐','托收','汇款') → 查 tbysyf TOP1(gname) 取 cb/tick/yjb，
///                  无客户行则先 INSERT(gname,dpm) 空行；再 INSERT tbysyf 完整行
///                  （ys=amt、pno=单pno+' '+per、xm=payterm+' '+t_mode+'  '+remark、skmoney=自动结转?ys:0）
///   row369 L535 —— 差额行（TAMT-TOPRC≠0 → PNO='KH'，ys=差额；旧为行级，此处推广为 Σoprct-Σoprc，M4 回放验证）
///   row34  还原 —— mark≠'Y' 拒绝；任一行 fhdate&gt;'1900-01-01' 拒绝；挂账类且 tbysyf.skmoney≠0 拒绝；
///                  UPDATE tbsada SET mark='',per1='',qrdate=1900,jianshou=0 WHERE type+paper+dpm；
///                  DELETE tbgath / tbysyf WHERE type+cno+dpm
/// 发票管理 tbfpgl（row369 L559-576，payno 非空 upsert）随 G6 发票管理二期补。
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

    public async Task<ConfirmReceiptResult> ConfirmAsync(ConfirmReceiptCommand command, string per, CancellationToken ct = default)
    {
        var paper = (command.Paper ?? "").Trim();
        var type = (command.Type ?? "销售").Trim();
        var payTerm = (command.PayTerm ?? "").Trim();
        if (paper.Length == 0) throw new AppException("DOC_NOT_FOUND", "单号不能为空", 422);
        if (payTerm.Length == 0) throw new AppException("PAYTERM_REQUIRED", "请选择付款方式", 422);
        if (command.Amount < 0) throw new AppException("AMOUNT_INVALID", "实收金额不能为负", 422);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 1) 单据汇总（UPDLOCK 防并发收款）
            var doc = await conn.QueryFirstOrDefaultAsync<(string gname, string pno, string t_mode, string remark,
                string mark, string guno, DateTime outdate, decimal sum_oprct, decimal sum_oprc, string spname)>(
                new CommandDefinition(
                    """
                    SELECT MAX(c.gname) gname, MAX(c.pno) pno, MAX(c.t_mode) t_mode, MAX(c.remark) remark,
                           MAX(c.mark) mark, MAX(c.guno) guno, MAX(c.outdate) outdate,
                           SUM(c.oprct) sum_oprct, SUM(c.oprc) sum_oprc,
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

            // 2) gdate：系统开关「财务确认收款日期是否取确认日期」
            var takeConfirmDate = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT ISNULL(valuel, 0) FROM tbsysset WITH (NOLOCK) WHERE type = 'system' AND name = N'财务确认收款日期是否取确认日期'",
                transaction: tx, cancellationToken: ct));
            var gdate = takeConfirmDate ? DateTime.Now : doc.outdate;

            // 3) 确认（旧 row358：WHERE type+paper，无 dpm）
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbsada SET qrdate = GETDATE(), mark = 'Y', guno = @guno, per1 = @per
                WHERE type = @type AND paper = @paper
                """,
                new
                {
                    guno = new DbString { Value = payTerm, IsAnsi = false, Length = 10 },
                    per = new DbString { Value = per, IsAnsi = true, Length = 10 },
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));

            // 4) 台账重置 + 写入（旧 L470/L484）
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbgath WHERE type = @type AND cno = @paper",
                new
                {
                    type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                    paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));
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
                    payterm = new DbString { Value = payTerm, IsAnsi = false, Length = 10 },
                    amt = command.Amount,
                    per = new DbString { Value = per, IsAnsi = true, Length = 10 },
                    payno = new DbString { Value = command.PayNo ?? "", IsAnsi = true, Length = 15 },
                    dpm = new DbString { Value = command.Dept, IsAnsi = true, Length = 10 },
                },
                transaction: tx, cancellationToken: ct));

            // 5) 挂账类 → tbysyf 应收行 + 差额行
            var onCredit = ReceiptGuards.IsOnCredit(payTerm);
            var diff = command.Amount - doc.sum_oprc;   // 旧 TAMT-TOPRC 语义
            if (onCredit)
            {
                // 5a. 客户既有 cb/tick/yjb（旧 L498-510：无客户行先插空行）
                var prior = await conn.QueryFirstOrDefaultAsync<(decimal cb, int tick, decimal yjb)>(new CommandDefinition(
                    "SELECT TOP (1) cb, tick, yjb FROM tbysyf WITH (NOLOCK) WHERE gname = @gname ORDER BY datime",
                    new { gname = new DbString { Value = doc.gname.Trim(), IsAnsi = false, Length = 60 } },
                    transaction: tx, cancellationToken: ct));
                if (prior.cb == 0 && prior.tick == 0 && prior.yjb == 0)
                {
                    var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname",
                        new { gname = new DbString { Value = doc.gname.Trim(), IsAnsi = false, Length = 60 } },
                        transaction: tx, cancellationToken: ct));
                    if (exists == 0)
                        await conn.ExecuteAsync(new CommandDefinition(
                            "INSERT INTO tbysyf (gname, dpm) VALUES (@gname, @dpm)",
                            new
                            {
                                gname = new DbString { Value = doc.gname.Trim(), IsAnsi = false, Length = 60 },
                                dpm = new DbString { Value = command.Dept, IsAnsi = true, Length = 10 },
                            },
                            transaction: tx, cancellationToken: ct));
                }

                // 5b. 应收行（旧 L534）
                await InsertYsyfAsync(conn, tx, ct, new YsyfRow
                {
                    Gname = doc.gname.Trim(),
                    Datime = gdate,
                    Cno = paper,
                    Ys = command.Amount,
                    Pno = $"{doc.pno.Trim()} {per}".Trim(),
                    Fkfs = payTerm,
                    Type = type,
                    Cb = prior.cb,
                    Tick = prior.tick,
                    Yjb = prior.yjb,
                    Xm = $"{payTerm} {doc.t_mode.Trim()}  {doc.remark.Trim()}".TrimEnd(),
                    Per = per,
                    Skmoney = command.AutoSettle || payTerm == "挂帐冲预收" ? command.Amount : 0,
                    Spname = doc.spname.Trim(),
                    Dpm = command.Dept,
                });

                // 5c. 差额行（旧 L535-538：PNO='KH'）
                if (diff != 0)
                    await InsertYsyfAsync(conn, tx, ct, new YsyfRow
                    {
                        Gname = doc.gname.Trim(),
                        Datime = gdate,
                        Cno = paper,
                        Ys = diff,
                        Pno = "KH",
                        Fkfs = payTerm,
                        Type = type,
                        Cb = prior.cb,
                        Tick = prior.tick,
                        Yjb = prior.yjb,
                        Xm = $"{payTerm} {doc.t_mode.Trim()}  {doc.remark.Trim()}".TrimEnd(),
                        Per = per,
                        Skmoney = 0,
                        Spname = doc.spname.Trim(),
                        Dpm = command.Dept,
                    });
            }

            // 6) 回读断言（§7.3）
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
                Amount = command.Amount,
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
