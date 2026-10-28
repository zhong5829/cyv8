using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Stock;

public interface IDbrkRepository
{
    /// <summary>待确认调出单查询（fr_dbrk setsource：tbsada type='调拨' AND gname=?p_dpmname AND Mark='Y'）。</summary>
    Task<IReadOnlyList<string>> GetOpenPapersAsync(string dpmname, CancellationToken ct = default);

    /// <summary>调拨入库确认（fr_dbrk row7/__.row68）：仓位空拦截 → 负库存预演 → pty/pfa 回写 → exec dbrk → 挂帐写 tbysyf。</summary>
    Task<DbrkConfirmResult> ConfirmAsync(DbrkConfirmCommand command, string dpm, string dpmname, string per, CancellationToken ct = default);

    /// <summary>批量确认（__.row68）：按单逐个独立确认（每单独立事务），单笔失败记录后继续。</summary>
    Task<DbrkBatchResult> ConfirmBatchAsync(DbrkConfirmCommand command, string dpm, string dpmname, string per, CancellationToken ct = default);
}

/// <summary>
/// 调拨入库仓储（fr_dbrk 复刻，核心确认链路）。旧实现出处（T_.row7 单条 / __.row68 批量，逻辑一致仅差循环）：
/// <list type="bullet">
/// <item>入口（__.row68 L3-6）：GRD1.COLUMN5 可见性「先进行比较！」为导入比对 UI 交互，仓储不承接；
///   单条入口 row7 L3-7：无数据 →「没有数据，不能调拨确认！」。</item>
/// <item>仓位空拦截（row7 L8-15）：P_RKPOSINULL（tbsysset type='system' name='配件入库不许空仓位'，pg_main L3432）
///   为真且任一行 posi 为空 →「有 N 个编码的仓位为空，不允许入库！」。</item>
/// <item>负库存预演（row7 L26-63）：c2_sada = GROUP BY cp,nno,iprc,posi,depotno[,fa]（P_WAY=1 含 iprc），
///   SUM(ckamount)+现库存（tbisto 同键 dpm=?p_dpm；P_XZFA 含 fa），任一组 CKAMOUNT&gt;0 且 和&lt;0 → 中止
///   （旧系统仅弹出明细窗 RETURN，无错误文案；新系统抛 NEG_STOCK 422）。</item>
/// <item>pty/pfa 回写（row7 L64-70）：逐行 UPDATE tbsada SET pty=posi, pfa=depotno WHERE id（SP 游标取 s.pty/s.pfa 做入库仓位/库号）。</item>
/// <item>SP 调用（row7 L71）：exec dbrk ?mclient, ?mpaper, 2, ?mper, ?mdpm → c_rfh.CNO 新进库单号。
///   @iprccount=2 恒定；SP 原样在库（Data\create_tables.sql L9315），零改动直接调用。
///   注意 SP 内部按 mark='Y' 取 tbsada 游标自行入库/写 tbistoed，故本事务范围仅覆盖回写段；
///   旧系统 SPT 无显式事务（逐条自动提交），新系统整体单事务等价（SP 与回写同事务，异常全回滚更严格）。</item>
/// <item>挂帐写 tbysyf（__.row68 L71-105，row7 单条版不含此段——单条入口 GUNO 分支备案为旧系统差异，
///   新系统统一走同一段，行为为旧系统批量版超集备案）：guno∈{挂帐,托收,汇款} 时
///   pt=round(Σ oprc*(100-yhl)/100*ckamount, 2)（type='调拨' AND paper）；pt≠0 →
///   先查 tbysyf top 1 (gname=P_DPMNAME AND dpm)：无行 INSERT tbsada_header 式首行（gname,dpm，CB/TICK/YJB=0），
///   有行继承 CB/TICK/YJB；再 INSERT tbysyf 12 列（gname,datime=GETDATE,cno=MPAPER,ys=GETJE(pt),pno,fkfs=guno,
///   type='调拨',xm=fkfs,cb,yjb,tick,dpm）。GETJE 按 P_XSJEGS='1' 恒等复刻（一期 D1 同口径）。</item>
/// <item>SYSLOG（row7 L90）：op='确认入库：[ paper ] 生成进库单号为：[ cno ]'——日志表由 UI 层/审计承接，仓储返回结果。</item>
/// </list>
/// </summary>
public sealed class DbrkRepository(SqlConnectionFactory factory) : IDbrkRepository
{
    public async Task<IReadOnlyList<string>> GetOpenPapersAsync(string dpmname, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT RTRIM(paper) FROM tbsada WITH (NOLOCK)
            WHERE type = N'调拨' AND RTRIM(gname) = @dpmname AND RTRIM(ISNULL(mark, '')) = 'Y'
            ORDER BY 1
            """,
            new { dpmname = Ansi(dpmname.Trim(), 60) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<DbrkConfirmResult> ConfirmAsync(DbrkConfirmCommand command, string dpm, string dpmname, string per, CancellationToken ct = default)
    {
        command.Validate();
        var paper = command.Papers[0].Trim();
        var (newPaper, credit) = await ConfirmOneAsync(paper, command.Client, dpm, dpmname, per, ct);
        return new DbrkConfirmResult { Paper = paper, NewPaper = newPaper, CreditWritten = credit };
    }

    public async Task<DbrkBatchResult> ConfirmBatchAsync(DbrkConfirmCommand command, string dpm, string dpmname, string per, CancellationToken ct = default)
    {
        command.Validate();
        var result = new DbrkBatchResult();
        foreach (var raw in command.Papers)
        {
            var paper = raw.Trim();
            try
            {
                var (newPaper, credit) = await ConfirmOneAsync(paper, command.Client, dpm, dpmname, per, ct);
                result.Confirmed.Add(new DbrkConfirmResult { Paper = paper, NewPaper = newPaper, CreditWritten = credit });
            }
            catch (AppException ex)
            {
                result.Failed[paper] = ex.Message;   // __.row68：逐单独立，失败不中断
            }
        }
        return result;
    }

    private async Task<(string NewPaper, bool Credit)> ConfirmOneAsync(string paper, string client, string dpm, string dpmname, string per, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // row7 L26：单据行（c_sada WHERE paper）
            var lines = (await conn.QueryAsync<DbrkLine>(new CommandDefinition(
                """
                SELECT id, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, iprc, RTRIM(depotno) depotno,
                       RTRIM(posi) posi, RTRIM(fa) fa, ckamount, RTRIM(guno) guno
                FROM tbsada WITH (UPDLOCK)
                WHERE paper = @paper AND type = N'调拨'
                ORDER BY id
                """,
                new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct))).AsList();
            if (lines.Count == 0)
                throw new AppException("NO_DATA", $"调拨单 {paper} 不存在或无明细行！", 422);

            // row7 L8-15：仓位空拦截（P_RKPOSINULL）
            var rkposinull = await GetSwitchAsync(conn, tx, "配件入库不许空仓位", ct);
            if (rkposinull && lines.Any(l => string.IsNullOrWhiteSpace(l.posi)))
            {
                var n = lines.Count(l => string.IsNullOrWhiteSpace(l.posi));
                throw new AppException("POSI_EMPTY", $"有 {n} 个编码的仓位为空，不允许入库！", 422);
            }

            // row7 L26-63：负库存预演（P_WAY=1 含 iprc；P_XZFA 含 fa）
            var prm = await IsappeEngine.LoadParamsAsync(conn, tx, ct);
            var xzfa = prm.Xzfa ? " AND fa = @fa" : "";
            var wayIp = prm.PWay == 1 ? " AND iprc = @iprc" : "";
            var groups = lines.GroupBy(l => (l.cp, l.nno, prm.PWay == 1 ? l.iprc : 0m, l.posi, l.depotno, prm.Xzfa ? l.fa : ""));
            foreach (var g in groups)
            {
                var kc = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    $"""
                    SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WITH (UPDLOCK)
                    WHERE cp = @cp AND nno = @nno AND posi = @posi AND depotno = @depotno
                      AND dpm = @dpm{wayIp}{xzfa}
                    """,
                    new { cp = Ansi(g.Key.Item1, 10), nno = Ansi(g.Key.Item2, 18), posi = Ansi(g.Key.Item4, 10),
                          depotno = Ansi(g.Key.Item5, 10), dpm = Ansi(dpm, 10), iprc = g.Key.Item3, fa = Ansi(g.Key.Item6, 8) },
                    transaction: tx, cancellationToken: ct));
                var sum = g.Sum(x => x.ckamount);
                if (sum > 0 && kc + sum < 0)
                    throw new AppException("NEG_STOCK",
                        $"{g.Key.Item2} 库存不足：现库存 {kc}，本次调出 {sum}，预演后为负数，中止入库！", 422);
            }

            // row7 L64-70：pty/pfa 回写
            foreach (var l in lines)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbsada SET pty = @posi, pfa = @depotno WHERE id = @id",
                    new { posi = Ansi(l.posi, 10), depotno = Ansi(l.depotno, 10), id = l.id },
                    transaction: tx, cancellationToken: ct));
            }

            // row7 L71：exec dbrk（SP 原样，@iprccount=2 恒定）→ c_rfh.CNO
            var newPaper = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "exec dbrk @client, @paper, 2, @per, @dpm; SELECT RTRIM(CNO) FROM c_rfh",
                new { client = Ansi(client, 50), paper = Ansi(paper, 10), per = Ansi(per, 8), dpm = Ansi(dpm, 10) },
                commandType: System.Data.CommandType.Text, transaction: tx, cancellationToken: ct));
            if (string.IsNullOrWhiteSpace(newPaper))
                throw new AppException("DBRK_FAILED", $"dbrk 未返回进库单号（单据 {paper}）", 500);

            // __.row68 L71-105：挂帐写 tbysyf（取单上 guno —— 同单各行 guno 一致，任一命中即写）
            var guno = lines.FirstOrDefault(l => l.guno is "挂帐" or "托收" or "汇款")?.guno;
            var credit = false;
            if (guno != null)
            {
                var pt = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    """
                    SELECT ISNULL(ROUND(SUM(oprc * (100 - ISNULL(yhl, 0)) / 100 * ckamount), 2), 0)
                    FROM tbsada WHERE type = N'调拨' AND paper = @paper
                    """,
                    new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
                if (pt != 0)
                {
                    // 先查已有行决定 CB/TICK/YJB 继承（L78-92）
                    var prior = await conn.QueryFirstOrDefaultAsync<(decimal cb, decimal tick, decimal yjb)>(
                        new CommandDefinition(
                            "SELECT TOP 1 ISNULL(cb, 0), ISNULL(tick, 0), ISNULL(yjb, 0) FROM tbysyf WITH (UPDLOCK) " +
                            "WHERE gname = @gname AND dpm = @dpm ORDER BY datime DESC",
                            new { gname = Ansi(dpmname, 60), dpm = Ansi(dpm, 10) },
                            transaction: tx, cancellationToken: ct));
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbysyf
                            (gname, datime, cno, ys, pno, fkfs, type, cb, yjb, tick, xm, dpm)
                        VALUES
                            (@gname, GETDATE(), @cno, @ys, @pno, @fkfs, N'调拨', @cb, @yjb, @tick, @fkfs, @dpm)
                        """,
                        new
                        {
                            gname = Ansi(dpmname, 60), cno = Ansi(paper, 10), ys = pt,   // GETJE P_XSJEGS='1' 恒等
                            pno = Ansi((await GetMaxPnoAsync(conn, tx, paper, ct)) ?? "", 10),
                            fkfs = Ansi(guno, 10), cb = prior.cb, yjb = prior.yjb, tick = prior.tick,
                            dpm = Ansi(dpm, 10),
                        },
                        transaction: tx, cancellationToken: ct));
                    credit = true;
                }
            }

            await tx.CommitAsync(ct);
            return (newPaper, credit);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string?> GetMaxPnoAsync(SqlConnection conn, SqlTransaction tx, string paper, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT MAX(RTRIM(pno)) FROM tbsada WHERE type = N'调拨' AND paper = @paper",
            new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));

    private static async Task<bool> GetSwitchAsync(SqlConnection conn, SqlTransaction tx, string name, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT ISNULL(valuel, 0) FROM tbsysset WITH (NOLOCK) WHERE type = N'system' AND name = @name",
            new { name = Ansi(name, 30) }, transaction: tx, cancellationToken: ct));

    /// <summary>tbsada 调拨行投影。</summary>
    private sealed class DbrkLine
    {
        public int id { get; set; }
        public string cp { get; set; } = "";
        public string nno { get; set; } = "";
        public string na1 { get; set; } = "";
        public decimal iprc { get; set; }
        public string depotno { get; set; } = "";
        public string posi { get; set; } = "";
        public string fa { get; set; } = "";
        public decimal ckamount { get; set; }
        public string guno { get; set; } = "";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
