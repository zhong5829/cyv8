using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Stock;

public interface IDeliveryRepository
{
    /// <summary>页1/2 单据列表（待提货 mark='' / 已确认 mark='Y'，合并聚合 SQL，契约 §1.2）。</summary>
    Task<IReadOnlyList<DeliveryOrderRow>> GetOrdersAsync(DeliveryTab tab, string dpm, CancellationToken ct = default);

    /// <summary>页3 装箱列表（mark='T'，契约 §1.3）。</summary>
    Task<IReadOnlyList<DeliveryPackingRow>> GetPackingListAsync(string dpm, CancellationToken ct = default);

    /// <summary>单据行明细（c_mx，契约 §1.4；含已发标识/箱号）。</summary>
    Task<IReadOnlyList<DeliveryLineRow>> GetLinesAsync(string paper, CancellationToken ct = default);

    /// <summary>配货 / 还原配货（tbsada_header.printype 1/0，契约 §2.1）。</summary>
    Task SetAllocationAsync(string paper, bool allocated, string dpm, CancellationToken ct = default);

    /// <summary>发货确认 A（简：行级 fhdate + header spname + 整单 mark='T' 判定；不写 tbfh/tbwlfh，契约 §2.4）。</summary>
    Task<DeliveryConfirmResult> ConfirmDeliveryAAsync(DeliveryConfirmACommand cmd, string dpm, CancellationToken ct = default);

    /// <summary>发货确认 B（全：A + tbfh upsert + tbwlfh insert/update + 备货分支 bhmark，契约 §2.5）。</summary>
    Task<DeliveryConfirmResult> ConfirmDeliveryBAsync(DeliveryConfirmBCommand cmd, string dpm, CancellationToken ct = default);

    /// <summary>还原发货（契约 §2.8）。</summary>
    Task RevertDeliveryAsync(string paper, string dpm, CancellationToken ct = default);

    /// <summary>还原备货（契约 §2.3）。</summary>
    Task RevertStockAsync(string paper, string dpm, CancellationToken ct = default);

    /// <summary>装箱确认（tbfh 行 upsert + header.zxmark='Z' + tbwlfh 更新 + tbsada.back1/t_mode，契约 §2.6）。</summary>
    Task ConfirmPackingAsync(DeliveryPackingCommand cmd, string dpm, CancellationToken ct = default);

    /// <summary>还原装箱（header.zxmark='' + tbfh.fhamount=0，契约 §2.7）。</summary>
    Task RevertPackingAsync(string paper, CancellationToken ct = default);

    /// <summary>删除发货单（tbfh by fh_paper；旧语义不联动 mark/zxmark/tbwlfh——已备案偏差，契约 §2.11）。</summary>
    Task<int> DeleteDeliveryNoteAsync(string fhPaper, CancellationToken ct = default);

    /// <summary>发货短信记录（tbmssendhis 写入；WRITEHIS 实际发送为平台函数未复刻——备案）。</summary>
    Task WriteSmsAsync(DeliverySmsInput sms, CancellationToken ct = default);
}

public enum DeliveryTab { Pending = 1, Confirmed = 2 }

/// <summary>单据列表行（旧 c_th 聚合列集）。</summary>
public sealed class DeliveryOrderRow
{
    public string Paper { get; set; } = "";
    public string Mark { get; set; } = "";
    /// <summary>发货状态（旧聚合式：已确认/已备货/未备货/已发货/未发完）。</summary>
    public string Status { get; set; } = "";
    public string Gname { get; set; } = "";
    public DateTime Outdate { get; set; }
    public string Per1 { get; set; } = "";
    public string Back1 { get; set; } = "";
    public string Guno { get; set; } = "";
    public decimal Ckamount { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprct { get; set; }
    public string Remark { get; set; } = "";
    public string Spname { get; set; } = "";
    public string FhPaper { get; set; } = "";
    public DateTime Fhdate { get; set; }
    public string Bhmark { get; set; } = "";
    public int Printype { get; set; }
}

/// <summary>装箱列表行（旧 c_zx）。</summary>
public sealed class DeliveryPackingRow
{
    public string Paper { get; set; } = "";
    public DateTime Outdate { get; set; }
    public string Guno { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Per { get; set; } = "";
    /// <summary>装箱状态展示（旧 case zxmark）。</summary>
    public string ZxStatus { get; set; } = "";
    public string Spname { get; set; } = "";
    public string FhPaper { get; set; } = "";
    public string Fhgs { get; set; } = "";
    public string PerJhr { get; set; } = "";
    public string PerDbr { get; set; } = "";
}

/// <summary>行明细（旧 c_mx）。</summary>
public sealed class DeliveryLineRow
{
    public int Id { get; set; }
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Ckamount { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprct { get; set; }
    public string Posi { get; set; } = "";
    public string Depotno { get; set; } = "";
    public DateTime Fhdate { get; set; }
    public string Box { get; set; } = "";
    /// <summary>已发/已备货标识（蓝字行）。</summary>
    public bool Flag { get; set; }
}

public class DeliveryConfirmACommand
{
    public required string Paper { get; set; }
    /// <summary>发货员（back1）。</summary>
    public string Back1 { get; set; } = "";
    public string Remark { get; set; } = "";
    /// <summary>发货公司（tbsada_header.spname）。</summary>
    public string Spname { get; set; } = "";
    /// <summary>本次发货的行 id 集合（c_mx 勾选且 fhdate 为空的行）。</summary>
    public required List<int> LineIds { get; set; } = [];
}

public sealed class DeliveryConfirmBCommand : DeliveryConfirmACommand
{
    /// <summary>检货人（tbfh.per_jhr）。</summary>
    public string Jhr { get; set; } = "";
    /// <summary>发货单号（旧系统人工录入+查重）。</summary>
    public required string FhPaper { get; set; }
    /// <summary>担保人/代搬人（tbfh.per_dbr）。</summary>
    public string PerDbr { get; set; } = "";
    /// <summary>站名（tbwlfh.muzhan）。</summary>
    public string Muzhan { get; set; } = "";
    /// <summary>备货分支：mark≠'Y' 且非零售 → 只写 tbfh/tbwlfh + bhmark='bh'，不改 mark/fhdate。</summary>
    public bool StockOnly { get; set; }
}

public sealed class DeliveryPackingCommand
{
    public required string FhPaper { get; set; }
    public required List<string> Papers { get; set; } = [];
    public string Fhgs { get; set; } = "";        // 发货公司
    public string Jhr { get; set; } = "";         // 检货人
    public string PerDbr { get; set; } = "";      // 代搬人
    public string Wlbj { get; set; } = "";        // 物流报价
    public string Fhjs { get; set; } = "";        // 发货简述
    public string Back1 { get; set; } = "";       // 发货员
    public string TMode { get; set; } = "";       // 发货方式
    public required List<DeliveryPackingLine> Lines { get; set; } = [];
}

public sealed class DeliveryPackingLine
{
    public required int SadaId { get; set; }
    public string Box { get; set; } = "";
    /// <summary>发货数量（fhamount，INT 截断语义）。</summary>
    public decimal FhAmount { get; set; }
}

public sealed class DeliverySmsInput
{
    public string Mobile { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Smsg { get; set; } = "";
}

public sealed class DeliveryConfirmResult
{
    public string Paper { get; set; } = "";
    /// <summary>true=整单已发货（mark='T'）；false=未发完。</summary>
    public bool Completed { get; set; }
    /// <summary>备货分支生效（bhmark='bh'）。</summary>
    public bool Stocked { get; set; }
}

/// <summary>
/// 提货管理仓储（菜单 B.B，权限码 BB；fr_th 契约：开发文档/契约摘录/提货管理fr_th契约.md）。
/// 旧系统逐语句自动提交、无事务；新系统按 §10.4 建议将每个操作包进单事务（回放对齐最终状态）。
/// 零库存表写入（tbisto/tbistoed 无任何 INSERT/UPDATE/DELETE——出库扣减在销售环节）。
/// fh_paper = 人工录入 + 应用层查重（旧系统无取号函数；页3 装箱路径不查重——原样保留）。
/// </summary>
public sealed class DeliveryRepository(SqlConnectionFactory factory) : IDeliveryRepository
{
    private const string SaleTypes = "N'销售', N'历史'";

    public async Task<IReadOnlyList<DeliveryOrderRow>> GetOrdersAsync(DeliveryTab tab, string dpm, CancellationToken ct = default)
    {
        // 旧页过滤（1_.row3 Init）：页1 mark='' and guno<>''；页2 mark='Y'；近 7 天
        var markFilter = tab == DeliveryTab.Pending ? "a.mark = '' AND a.guno <> ''" : "a.mark = 'Y'";
        var retailGuno = await GetRetailGunoAsync(dpm, ct);
        var sql = $"""
            WITH base AS (
                SELECT a.paper,
                       MAX(a.mark) AS mark,
                       MAX(CASE WHEN a.guno = @retail THEN 0 ELSE 1 END) AS aa,
                       MAX(a.gname) gname, MAX(a.outdate) outdate, MAX(ISNULL(a.back1, '')) back1,
                       MAX(a.guno) guno, SUM(a.ckamount) ckamount,
                       SUM(a.ckamount * a.oprc * (100 - a.yhl) / 100) oprc,
                       SUM(a.ckamount * a.oprct * (100 - a.yhl) / 100) oprct,
                       MAX(ISNULL(a.remark, '')) remark, MAX(a.fhdate) fhdate,
                       MAX(ISNULL(h.printype, 0)) printype, MAX(ISNULL(h.spname, '')) spname,
                       MAX(ISNULL(h.bhmark, '')) bhmark,
                       MAX(CASE WHEN a.fhdate > '1900-01-01' THEN 1 ELSE 0 END) hasFh,
                       MAX(CASE WHEN a.guno = @retail THEN 1 ELSE 0 END) isRetail
                FROM tbsada a WITH (NOLOCK)
                LEFT JOIN tbsada_header h WITH (NOLOCK) ON h.paper = a.paper AND h.dpm = a.dpm
                WHERE a.type IN ({SaleTypes}) AND {markFilter} AND a.dpm = @dpm
                  AND a.outdate >= @from
                GROUP BY a.paper
            )
            SELECT paper, mark, gname, outdate, back1, guno, ckamount, oprc, oprct, remark, fhdate,
                   printype, spname, bhmark,
                   CASE
                       WHEN mark = 'Y' THEN N'已确认'
                       WHEN mark = 'M' THEN guno
                       WHEN mark = '' THEN CASE WHEN bhmark = 'bh' THEN N'已备货' ELSE N'未备货' END
                       ELSE N'已发货'
                   END AS status
            FROM base
            ORDER BY aa, paper DESC
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<DeliveryOrderRow>(new CommandDefinition(sql,
            new
            {
                dpm = Ansi(dpm, 10), retail = Ansi(retailGuno, 10), from = DateTime.Today.AddDays(-7),
            }, cancellationToken: ct))).AsList();

        // 未发完修正（旧 §1.2 步骤 8）：mark='Y' 单仍有任一行 fhdate>1900 → 未发完
        foreach (var r in rows.Where(r => r.Mark == "Y"))
        {
            var any = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(1) FROM tbsada WITH (NOLOCK) WHERE type IN ({SaleTypes}) AND paper = @paper AND ISNULL(fhdate, '1900-01-01') > '1900-01-01'",
                new { paper = Ansi(r.Paper, 10) }, cancellationToken: ct));
            if (any > 0) r.Status = "未发完";
        }
        return rows;
    }

    public async Task<IReadOnlyList<DeliveryPackingRow>> GetPackingListAsync(string dpm, CancellationToken ct = default)
    {
        var sql = """
            SELECT a.paper, a.outdate, a.guno, a.gname, MAX(a.per) per,
                   CASE WHEN ISNULL(h.zxmark, '') = '' THEN N'未装箱' ELSE N'已装箱' END AS zxStatus,
                   MAX(ISNULL(h.spname, '')) spname,
                   MAX(ISNULL(f.fh_paper, '')) fhPaper, MAX(ISNULL(f.fhgs, '')) fhgs,
                   MAX(ISNULL(f.per_jhr, '')) perJhr, MAX(ISNULL(f.per_dbr, '')) perDbr
            FROM tbsada a WITH (NOLOCK)
            LEFT JOIN tbsada_header h WITH (NOLOCK) ON h.paper = a.paper AND h.dpm = a.dpm
            LEFT JOIN tbfh f WITH (NOLOCK) ON f.sada_paper = a.paper
            WHERE a.type IN (N'销售', N'历史') AND a.mark = 'T' AND a.dpm = @dpm AND a.outdate >= @from
            GROUP BY a.paper, a.outdate, a.guno, a.gname, ISNULL(h.zxmark, '')
            ORDER BY CASE WHEN ISNULL(h.zxmark, '') = '' THEN 0 ELSE 1 END, a.paper
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<DeliveryPackingRow>(new CommandDefinition(sql,
            new { dpm = Ansi(dpm, 10), from = DateTime.Today.AddDays(-7) }, cancellationToken: ct))).AsList();
        return rows;
    }

    public async Task<IReadOnlyList<DeliveryLineRow>> GetLinesAsync(string paper, CancellationToken ct = default)
    {
        // 旧 c_mx（碚.row100）：行明细 + box 子查询 + 已发标识（fhdate>1900）
        var sql = $"""
            SELECT a.id, RTRIM(a.cp) cp, RTRIM(a.nno) nno, RTRIM(a.na1) na1, RTRIM(a.ty) ty, RTRIM(a.fa) fa,
                   RTRIM(a.unit) unit, a.ckamount, a.oprc, a.oprct, RTRIM(a.posi) posi, RTRIM(a.depotno) depotno,
                   ISNULL(a.fhdate, '1900-01-01') fhdate,
                   ISNULL((SELECT TOP (1) box FROM tbfh WITH (NOLOCK) WHERE sada_id = a.id), '') box,
                   CASE WHEN ISNULL(a.fhdate, '1900-01-01') > '1900-01-01' THEN 1 ELSE 0 END flag
            FROM tbsada a WITH (NOLOCK)
            WHERE a.type IN ({SaleTypes}) AND a.paper = @paper AND a.paper <> ''
            ORDER BY a.id
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<DeliveryLineRow>(new CommandDefinition(sql,
            new { paper = Ansi(paper, 10) }, cancellationToken: ct))).AsList();
        return rows;
    }

    public async Task SetAllocationAsync(string paper, bool allocated, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await UpsertHeaderAsync(conn, tx, paper, dpm, h =>
            {
                h.Printype = allocated ? 1 : 0;
            }, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<DeliveryConfirmResult> ConfirmDeliveryAAsync(DeliveryConfirmACommand cmd, string dpm, CancellationToken ct = default)
    {
        var result = await ConfirmCoreAsync(cmd, dpm, writeFh: false, ct);
        return result;
    }

    public async Task<DeliveryConfirmResult> ConfirmDeliveryBAsync(DeliveryConfirmBCommand cmd, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        // 守卫（G__.row467）：mark='T' 存在 → 状态已改变
        var shipped = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(1) FROM tbsada WITH (UPDLOCK, HOLDLOCK) WHERE type IN ({SaleTypes}) AND paper = @paper AND mark = 'T'",
            new { paper = Ansi(cmd.Paper, 10) }, cancellationToken: ct));
        if (shipped > 0) throw new AppException("STATE_CHANGED", "该单状态已改变", 409);

        var retailGuno = await GetRetailGunoAsync(dpm, ct);
        var mark = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RTRIM(mark) FROM tbsada WITH (NOLOCK) WHERE paper = @paper",
            new { paper = Ansi(cmd.Paper, 10) }, cancellationToken: ct));
        var guno = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RTRIM(guno) FROM tbsada WITH (NOLOCK) WHERE paper = @paper",
            new { paper = Ansi(cmd.Paper, 10) }, cancellationToken: ct));
        var isRetail = guno == retailGuno;
        var stockOnly = cmd.StockOnly && mark != "Y" && !isRetail;

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var result = new DeliveryConfirmResult { Paper = cmd.Paper };

            if (!stockOnly)
            {
                // 行级发货（旧 G__.row467 步骤 1：勾选且未发货行；fhdate 含时刻但消费方仅用日期 → 归一 Today，§7 约定）
                var now = DateTime.Today;
                foreach (var lineId in cmd.LineIds)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbsada SET fhdate = @fhdate, back1 = @back1, remark = @remark WHERE id = @id AND (fhdate IS NULL OR fhdate = '1900-01-01')",
                        new { fhdate = now, back1 = Ansi(cmd.Back1, 10), remark = Ansi(cmd.Remark, 100), id = lineId },
                        transaction: tx, cancellationToken: ct));
                }
            }

            // tbfh upsert（旧步骤 3）
            await UpsertTbfhAsync(conn, tx, cmd.Paper, dpm, cmd.FhPaper, cmd.Jhr, cmd.PerDbr, cmd.Spname, ct);

            // tbwlfh（旧步骤 4：有行 UPDATE / 无行 INSERT...SELECT）
            await UpsertTbwlfhAsync(conn, tx, cmd.Paper, cmd.FhPaper, cmd.Spname, cmd.Muzhan, ct);

            if (stockOnly)
            {
                // 备货分支（旧步骤 7）
                await UpsertHeaderAsync(conn, tx, cmd.Paper, dpm, h => { h.Bhmark = "bh"; }, ct);
                result.Stocked = true;
            }
            else
            {
                // fhdate 同步（旧步骤 5；归一 Today）
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbfh SET fhdate = @fhdate WHERE sada_paper = @paper",
                    new { fhdate = DateTime.Today, paper = Ansi(cmd.Paper, 10) }, transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbwlfh SET fhdate = @fhdate WHERE paper = @paper",
                    new { fhdate = DateTime.Today, paper = Ansi(cmd.Paper, 10) }, transaction: tx, cancellationToken: ct));

                // 整单判定（旧步骤 6）
                var pending = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    $"SELECT COUNT(1) FROM tbsada WITH (NOLOCK) WHERE type IN ({SaleTypes}) AND paper = @paper AND ISNULL(fhdate, '1900-01-01') < '1900-01-02'",
                    new { paper = Ansi(cmd.Paper, 10) }, transaction: tx, cancellationToken: ct));
                if (pending == 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        $"UPDATE tbsada SET mark = 'T' WHERE type IN ({SaleTypes}) AND paper = @paper",
                        new { paper = Ansi(cmd.Paper, 10) }, transaction: tx, cancellationToken: ct));
                    result.Completed = true;
                }

                // header spname（旧步骤 2）
                await UpsertHeaderAsync(conn, tx, cmd.Paper, dpm, h => { h.Spname = cmd.Spname; }, ct);
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

    public async Task RevertDeliveryAsync(string paper, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 守卫（旧崁.row62）：已装箱 → 先还原装箱
            var zx = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT RTRIM(zxmark) FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper AND dpm = @dpm",
                new { paper = Ansi(paper, 10), dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
            if (zx == "Z") throw new AppException("PACKED", "该单已装箱，请先还原装箱！", 409);

            var retailGuno = await GetRetailGunoAsync(dpm, ct);
            var guno = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT RTRIM(guno) FROM tbsada WITH (NOLOCK) WHERE paper = @paper",
                new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
            var isRetail = guno == retailGuno;

            // 行级重置（旧步骤 2）
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsada SET fhdate = '1900-01-01', back1 = '', note = '' WHERE type IN (" + SaleTypes + ") AND paper = @paper",
                new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));

            var bhmark = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT RTRIM(bhmark) FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper AND dpm = @dpm",
                new { paper = Ansi(paper, 10), dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));

            if (string.IsNullOrEmpty(bhmark) || isRetail)
            {
                // 删除 tbfh/tbwlfh（旧步骤 3）
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE tbfh WHERE sada_paper = @paper", new { paper = Ansi(paper, 10) },
                    transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE tbwlfh WHERE paper = @paper", new { paper = Ansi(paper, 10) },
                    transaction: tx, cancellationToken: ct));
            }
            else
            {
                // fhdate 重置（旧步骤 3 else）
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbfh SET fhdate = '1900-01-01' WHERE sada_paper = @paper",
                    new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbwlfh SET fhdate = '1900-01-01' WHERE paper = @paper",
                    new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
            }

            // mark 回退：零售→''，非零售→'Y'（旧步骤 4）
            var newMark = isRetail ? "" : "Y";
            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE tbsada SET mark = @mark WHERE type IN ({SaleTypes}) AND paper = @paper",
                new { mark = new DbString { Value = newMark, IsAnsi = true, Length = 10 }, paper = Ansi(paper, 10) },
                transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RevertStockAsync(string paper, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 旧崁.row62 还原备货：mark='' 且 bhmark='bh'
            var mark = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT RTRIM(mark) FROM tbsada WITH (NOLOCK) WHERE paper = @paper",
                new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
            if (mark != "") throw new AppException("STATE_CHANGED", "该单状态已改变（仅未发货单可还原备货）", 409);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsada SET fhdate = '1900-01-01', back1 = '', note = '' WHERE type IN (" + SaleTypes + ") AND paper = @paper",
                new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
            await UpsertHeaderAsync(conn, tx, paper, dpm, h => { h.Bhmark = ""; }, ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbfh WHERE sada_paper = @paper", new { paper = Ansi(paper, 10) },
                transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbwlfh WHERE paper = @paper", new { paper = Ansi(paper, 10) },
                transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task ConfirmPackingAsync(DeliveryPackingCommand cmd, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 逐行 tbfh upsert（旧棏_.row437：sada_id 查重 → INSERT 17 列 / UPDATE 9 列；frmmain 路径不查重 fh_paper——原样）
            foreach (var l in cmd.Lines)
            {
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbfh WITH (UPDLOCK) WHERE sada_id = @sadaId",
                    new { sadaId = l.SadaId }, transaction: tx, cancellationToken: ct));
                if (exists == 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbfh
                            (fh_paper, sada_paper, sada_id, nno, na1, ty, fa, unit, box, per_jhr, fhamount, per_dbr, fhgs, fhcount, fhgl, gname, ckamount)
                        SELECT @fhPaper, paper, id, nno, na1, ty, fa, unit, @box, @jhr, @fhAmount, @perDbr, @fhgs, 0, 0, gname, ckamount
                        FROM tbsada WHERE id = @sadaId
                        """,
                        new
                        {
                            fhPaper = Ansi(cmd.FhPaper, 15), sadaId = l.SadaId, box = Ansi(l.Box, 20),
                            jhr = Ansi(cmd.Jhr, 10), fhAmount = Math.Floor(l.FhAmount), perDbr = Ansi(cmd.PerDbr, 10),
                            fhgs = Ansi(cmd.Fhgs, 20),
                        }, transaction: tx, cancellationToken: ct));
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbfh SET fh_paper = @fhPaper, box = @box, per_jhr = @jhr, fhamount = @fhAmount, per_dbr = @perDbr, fhgs = @fhgs WHERE sada_id = @sadaId",
                        new
                        {
                            fhPaper = Ansi(cmd.FhPaper, 15), box = Ansi(l.Box, 20), jhr = Ansi(cmd.Jhr, 10),
                            fhAmount = Math.Floor(l.FhAmount), perDbr = Ansi(cmd.PerDbr, 10), fhgs = Ansi(cmd.Fhgs, 20),
                            sadaId = l.SadaId,
                        }, transaction: tx, cancellationToken: ct));
                }
            }

            var paperList = string.Join(",", cmd.Papers.Select(p => $"'{p.Replace("'", "''")}'"));

            // 装箱标记（旧步骤 3：header.zxmark='Z'，不改 tbsada.mark）
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsada_header SET zxmark = 'Z' WHERE paper IN (" + paperList + ")",
                transaction: tx, cancellationToken: ct));

            // tbwlfh（旧步骤 3：fh_paper,spname,wlbj,fhjs）
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbwlfh SET fh_paper = @fhPaper, spname = @fhgs, wlbj = @wlbj, fhjs = @fhjs WHERE paper IN (" + paperList + ")",
                new { fhPaper = Ansi(cmd.FhPaper, 15), fhgs = Ansi(cmd.Fhgs, 60), wlbj = cmd.Wlbj, fhjs = Ansi(cmd.Fhjs, 100) },
                transaction: tx, cancellationToken: ct));

            // tbsada（旧步骤 3：back1,t_mode）
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsada SET back1 = @back1, t_mode = @tMode WHERE paper IN (" + paperList + ")",
                new { back1 = Ansi(cmd.Back1, 10), tMode = Ansi(cmd.TMode, 10) },
                transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RevertPackingAsync(string paper, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var zx = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT RTRIM(zxmark) FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper",
                new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
            if (string.IsNullOrEmpty(zx)) throw new AppException("NOT_PACKED", "此单未装箱！", 409);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsada_header SET zxmark = '' WHERE paper = @paper",
                new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbfh SET fhamount = 0 WHERE sada_paper = @paper",
                new { paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> DeleteDeliveryNoteAsync(string fhPaper, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var n = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbfh WHERE fh_paper = @fhPaper",
                new { fhPaper = Ansi(fhPaper, 15) }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return n;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task WriteSmsAsync(DeliverySmsInput sms, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tbmssendhis (mobile, sendty, gname, sender, smsg, operty, sdate)
            VALUES (@mobile, N'自动', @gname, @sender, @smsg, N'自由短信', @sdate)
            """,
            new
            {
                mobile = Ansi(sms.Mobile, 15), gname = Ansi(sms.Gname, 60), sender = Ansi(sms.Sender, 10),
                smsg = sms.Smsg, sdate = DateTime.Now,
            }, cancellationToken: ct));
    }

    // ---------- 私有 ----------

    /// <summary>发货 A 共享核心（旧瀍_.row232）：行级 fhdate → header spname → 整单判定 mark='T'。</summary>
    private async Task<DeliveryConfirmResult> ConfirmCoreAsync(DeliveryConfirmACommand cmd, string dpm, bool writeFh, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        // 守卫（瀍_.row232）：存在 mark<>'Y' 的行 → 状态已改变（旧语义：空串也拦截）
        var notY = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(1) FROM tbsada WITH (UPDLOCK, HOLDLOCK) WHERE type IN ({SaleTypes}) AND paper = @paper AND mark <> 'Y'",
            new { paper = Ansi(cmd.Paper, 10) }, cancellationToken: ct));
        if (notY > 0) throw new AppException("STATE_CHANGED", "该单状态已改变", 409);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var result = new DeliveryConfirmResult { Paper = cmd.Paper };
            var now = DateTime.Today;  // fhdate 归一 Today（消费方仅用日期，§7 约定）
            foreach (var lineId in cmd.LineIds)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbsada SET fhdate = @fhdate, back1 = @back1, remark = @remark WHERE id = @id AND (fhdate IS NULL OR fhdate = '1900-01-01')",
                    new { fhdate = now, back1 = Ansi(cmd.Back1, 10), remark = Ansi(cmd.Remark, 100), id = lineId },
                    transaction: tx, cancellationToken: ct));
            }

            await UpsertHeaderAsync(conn, tx, cmd.Paper, dpm, h => { h.Spname = cmd.Spname; }, ct);

            var pending = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(1) FROM tbsada WITH (NOLOCK) WHERE type IN ({SaleTypes}) AND paper = @paper AND ISNULL(fhdate, '1900-01-01') < '1900-01-02'",
                new { paper = Ansi(cmd.Paper, 10) }, transaction: tx, cancellationToken: ct));
            if (pending == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    $"UPDATE tbsada SET mark = 'T' WHERE type IN ({SaleTypes}) AND paper = @paper",
                    new { paper = Ansi(cmd.Paper, 10) }, transaction: tx, cancellationToken: ct));
                result.Completed = true;
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

    private async Task<string> GetRetailGunoAsync(string dpm, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP (1) RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE type = 'system' AND name = N'零售标识' AND dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, cancellationToken: ct)) ?? "";
    }

    /// <summary>tbsada_header spname/bhmark/printype/zxmark 逐列 upsert（旧 TEXTMERGE 参数化改写）。</summary>
    private static async Task UpsertHeaderAsync(SqlConnection conn, SqlTransaction tx, string paper, string dpm,
        Action<HeaderPatch> patch, CancellationToken ct)
    {
        var p = new HeaderPatch();
        patch(p);
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbsada_header WITH (UPDLOCK) WHERE paper = @paper AND dpm = @dpm",
            new { paper = Ansi(paper, 10), dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        if (exists == 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tbsada_header (paper, dpm, spname, bhmark, printype) VALUES (@paper, @dpm, @spname, ISNULL(@bhmark, ''), ISNULL(@printype, 0))",
                new { paper = Ansi(paper, 10), dpm = Ansi(dpm, 10), spname = Ansi(p.Spname, 60), bhmark = Ansi(p.Bhmark, 10), p.Printype },
                transaction: tx, cancellationToken: ct));
            return;
        }
        // 逐列更新（仅 patch 过的列）
        var sets = new List<string>();
        var args = new DynamicParameters(new { paper = Ansi(paper, 10), dpm = Ansi(dpm, 10) });
        if (p.Spname is { } v1) { sets.Add("spname = @spname"); args.Add("spname", Ansi(v1, 60)); }
        if (p.Bhmark is { } v2) { sets.Add("bhmark = @bhmark"); args.Add("bhmark", Ansi(v2, 10)); }
        if (p.Printype is { } v3) { sets.Add("printype = @printype"); args.Add("printype", v3); }
        if (sets.Count == 0) return;
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tbsada_header SET {string.Join(", ", sets)} WHERE paper = @paper AND dpm = @dpm",
            args, transaction: tx, cancellationToken: ct));
    }

    private sealed class HeaderPatch
    {
        public string? Spname { get; set; }
        public string? Bhmark { get; set; }
        public int? Printype { get; set; }
    }

    /// <summary>tbfh 按单 upsert（旧 G__.row467 步骤 3：有行 UPDATE 4 列 / 无行 INSERT...SELECT 18 列）。</summary>
    private static async Task UpsertTbfhAsync(SqlConnection conn, SqlTransaction tx, string paper, string dpm,
        string fhPaper, string jhr, string perDbr, string fhgs, CancellationToken ct)
    {
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbfh WITH (UPDLOCK) WHERE sada_id IN (SELECT id FROM tbsada WHERE paper = @paper AND dpm = @dpm)",
            new { paper = Ansi(paper, 10), dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        if (exists > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbfh SET per_jhr = @jhr, fh_paper = @fhPaper, fhgs = @fhgs, per_dbr = @perDbr " +
                "WHERE sada_id IN (SELECT id FROM tbsada WHERE paper = @paper AND dpm = @dpm)",
                new { jhr = Ansi(jhr, 10), fhPaper = Ansi(fhPaper, 15), fhgs = Ansi(fhgs, 20), perDbr = Ansi(perDbr, 10), paper = Ansi(paper, 10), dpm = Ansi(dpm, 10) },
                transaction: tx, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO tbfh
                    (fh_paper, sada_paper, sada_id, nno, na1, ty, fa, unit, fhdate, box, per_jhr, fhamount, per_dbr, fhgs, fhcount, fhgl, gname, ckamount)
                SELECT @fhPaper, paper, id, nno, na1, ty, fa, unit, ISNULL(fhdate, '1900-01-01'), '', @jhr, 0, @perDbr, @fhgs, 0, 0, gname, ckamount
                FROM tbsada WHERE paper = @paper AND dpm = @dpm
                """,
                new
                {
                    fhPaper = Ansi(fhPaper, 15), jhr = Ansi(jhr, 10), perDbr = Ansi(perDbr, 10), fhgs = Ansi(fhgs, 20),
                    paper = Ansi(paper, 10), dpm = Ansi(dpm, 10),
                }, transaction: tx, cancellationToken: ct));
        }
    }

    /// <summary>tbwlfh 按单 upsert（旧 G__.row467 步骤 4：INSERT 14 列 / UPDATE spname,muzhan,fh_paper）。</summary>
    private static async Task UpsertTbwlfhAsync(SqlConnection conn, SqlTransaction tx, string paper,
        string fhPaper, string spname, string muzhan, CancellationToken ct)
    {
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbwlfh WITH (UPDLOCK) WHERE fh_paper = @fhPaper AND paper = @paper",
            new { fhPaper = Ansi(fhPaper, 15), paper = Ansi(paper, 10) }, transaction: tx, cancellationToken: ct));
        if (exists > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbwlfh SET spname = @spname, muzhan = @muzhan, fh_paper = @fhPaper WHERE paper = @paper",
                new { spname = Ansi(spname, 60), muzhan = Ansi(muzhan, 20), fhPaper = Ansi(fhPaper, 15), paper = Ansi(paper, 10) },
                transaction: tx, cancellationToken: ct));
            return;
        }
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tbwlfh
                (spname, fh_paper, fhamount, tscount, fhdate, outdate, paper, salsecount, gname, per, dpm, cp, ckamount, muzhan)
            SELECT a.fhgs, @fhPaper, 0, 0, a.fhdate, b.outdate, a.sada_paper,
                   SUM(b.ckamount * b.oprc * (100 - b.yhl) / 100), a.gname, b.per, b.dpm, '', SUM(b.ckamount), @muzhan
            FROM tbfh a
            LEFT JOIN tbsada b ON a.sada_id = b.id
            WHERE a.sada_paper = @paper
            GROUP BY a.fhgs, a.fhdate, b.outdate, a.sada_paper, a.gname, b.per, b.dpm
            """,
            new { fhPaper = Ansi(fhPaper, 15), muzhan = Ansi(muzhan, 20), paper = Ansi(paper, 10) },
            transaction: tx, cancellationToken: ct));
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
