using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Stock;

public interface IStockAdjustRepository
{
    /// <summary>调仓单取号（fr_sadc a_.row150 DJLSH('调仓单',1)：djjx+年2位+6位流水，应用层拼号）。</summary>
    Task<string> NextPaperAsync(string dept, CancellationToken ct = default);

    /// <summary>查询未确认调仓单列表（tbsadc m='' GROUP BY paper）。</summary>
    Task<IReadOnlyList<string>> GetOpenPapersAsync(string dept, CancellationToken ct = default);

    /// <summary>查询调仓单明细（tbsadc WHERE paper）。</summary>
    Task<IReadOnlyList<StockAdjustLine>> GetLinesAsync(string paper, CancellationToken ct = default);

    /// <summary>源批次查询（tbisto 行，UI 选批次用）。</summary>
    Task<IReadOnlyList<StockBatchLine>> GetStockBatchesAsync(string dept, string? nnoContains, CancellationToken ct = default);

    /// <summary>新增调仓明细（fr_sadc a_.row150：INSERT tbsadc，源批次字段快照 + 目标仓位）。</summary>
    Task<string> AddLineAsync(string paper, AddStockAdjustLineCommand command, string dept, string per, CancellationToken ct = default);

    /// <summary>修改调仓明细（fr_sadc -_.row168：oprc,depotno1,posi1,ckamount,per）。</summary>
    Task<int> UpdateLineAsync(UpdateStockAdjustLineCommand command, string per, CancellationToken ct = default);

    /// <summary>删除明细行（fr_sadc __.row10）。</summary>
    Task<int> DeleteLineAsync(int id, CancellationToken ct = default);

    /// <summary>整单删除（fr_sadc __.row9：m<>'*' 已确认拒删）。</summary>
    Task<int> DeletePaperAsync(string paper, CancellationToken ct = default);

    /// <summary>确认调仓单（fr_sadc __.row11）：逐行库存校验 → ISAPPE 源减/目标加 → m='*'。</summary>
    Task<ConfirmStockAdjustResult> ConfirmAsync(string paper, string dept, CancellationToken ct = default);
}

/// <summary>源批次行（tbisto 投影，UI 选批次）。</summary>
public sealed class StockBatchLine
{
    public int Id { get; init; }
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Fa { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Depotno { get; init; } = "";
    public string Posi { get; init; } = "";
    public decimal Kcamount { get; init; }
    public decimal Iprc { get; init; }
    public decimal Iprj { get; init; }
    public decimal Oprc { get; init; }
}

/// <summary>
/// 调仓仓储（二期，库内仓位移动，纯应用层写路径——无触发器依赖）。旧实现出处（fr_sadc\ + pg_proc）：
///   - 取号 a_.row150 L10：DJLSH('调仓单',1)（pg_proc_client djlsh L945-997）：查 tbdjgs WHERE djname='调仓单'
///     （PASSTH 补部门权限）→ dyear=当前年 ? lsh+1 : lsh=1 → UPDATE tbdjgs SET dyear,lsh；
///     单号 = djjx + RIGHT(year,2) + RIGHT('00000'+lsh,6)。注意与 getnewdh 不同：无存储过程、无防重回取。
///   - 新增明细 a_.row150 L61：INSERT tbsadc(cp,nno,na1,ty,fa,unit,depotno,posi,depotno1,posi1,paper,
///     outdate,ckamount,xno,iprc,oprc,iprj,oprb,per,note,kcid,dc)（PASSTH 补 dpm），源字段快照自
///     tbisto 批次行（C_ISTO）；约束：数量≥0、目标≠源仓位（L48）、同单同 kcid 累计≤tbisto.kcamount（L52-58）
///   - 修改明细 -_.row168 L44：UPDATE tbsadc SET oprc,depotno1,posi1,ckamount,per WHERE id
///   - 删行 __.row10：DELETE tbsadc WHERE id=?；删单 __.row9：DELETE WHERE paper=? AND m<>'*'
///   - 确认 __.row11：逐行（m 为空）：
///       校验 SELECT isnull(sum(kcamount),0) FROM tbisto WHERE id=kcid AND cp+nno+depotno+posi+iprc
///       → kc &lt; ckam 则跳过（失败计数）
///       事务内 ISAPPE('tbsadc', id, -ckam) + ISAPPE('tbsadc', id, +ckam, 1)（P_WAY=1）：
///         tbsadc 分支 TYPE<>0 时目标 = posi1/depotno1（pg_proc L1566-1573）；
///         匹配 = tbisto WHERE cp+nno+iprc+posi+depotno+dpm ORDER BY kcamount DESC TOP 1（L1585）
///         命中 → UPDATE tbisto SET kcamount+=Δ, kcbhdate=今天, iprj, oprc/oprb=库内原值 WHERE id（L1612）
///         未命中 → INSERT tbisto(dpm,cp,nno,na1,unit,kcamount,ty,fa,cxnno,iprj,iprc,oprc,posi,
///           depotno,note,xno,dc,kcbhdate,oprb)，kcamount=Δ，cxnno=S_NNO(nno)（L1605；tbsysset type='cp'
///           行 cxnno 串号模板映射，生产未配置=''）
///       → UPDATE tbsadc SET m='*' WHERE id
/// 契约偏差注记：outdate 旧系统 GETDATE() 含时刻，消费方仅用日期；回放确定性两侧统一 DateTime.Today。
/// </summary>
public sealed class StockAdjustRepository(SqlConnectionFactory factory) : IStockAdjustRepository
{
    public async Task<string> NextPaperAsync(string dept, CancellationToken ct = default)
    {
        var deptTrim = dept.Trim();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 跨进程行锁（同 DocNoService §11.3 模式）：锁定 tbdjgs 目标行至事务结束
            var row = await conn.QueryFirstOrDefaultAsync<(int id, string djjx, int lsh, string dyear)>(new CommandDefinition(
                """
                SELECT id, RTRIM(djjx) AS djjx, lsh, RTRIM(dyear) AS dyear
                FROM tbdjgs WITH (UPDLOCK, ROWLOCK)
                WHERE djname = N'调仓单' AND dpm = @dpm
                """,
                new { dpm = Ansi(deptTrim, 10) },
                transaction: tx, cancellationToken: ct));
            if (row.id == 0)
                throw new AppException("DOC_NO_FAILED", $"取单号失败：单据类型「调仓单」部门「{deptTrim}」未在 tbdjgs 配置前缀", 422);

            var year = DateTime.Now.Year.ToString();
            var lsh = row.dyear == year ? row.lsh + 1 : 1;   // djlsh L960-966：跨年流水重置
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbdjgs SET dyear = @dyear, lsh = @lsh WHERE id = @id",
                new { dyear = Ansi(year, 4), lsh, id = row.id },
                transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return row.djjx + year[^2..] + lsh.ToString("D6");  // djlsh L973
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> GetOpenPapersAsync(string dept, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT RTRIM(paper) FROM tbsadc WITH (NOLOCK)
            WHERE dpm = @dpm AND ISNULL(RTRIM(m), '') = ''
            ORDER BY 1
            """,
            new { dpm = Ansi(dept.Trim(), 10) },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<StockAdjustLine>> GetLinesAsync(string paper, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<StockAdjustLine>(new CommandDefinition(
            """
            SELECT id, RTRIM(cp) AS Cp, RTRIM(nno) AS Nno, RTRIM(na1) AS Na1, RTRIM(ty) AS Ty, RTRIM(fa) AS Fa,
                   RTRIM(unit) AS Unit, RTRIM(depotno) AS Depotno, RTRIM(posi) AS Posi,
                   RTRIM(depotno1) AS Depotno1, RTRIM(posi1) AS Posi1, ckamount AS Ckamount,
                   iprc AS Iprc, iprj AS Iprj, oprc AS Oprc, RTRIM(paper) AS Paper, RTRIM(per) AS Per,
                   outdate AS Outdate, RTRIM(note) AS Note, kcid AS Kcid, RTRIM(m) AS M
            FROM tbsadc WITH (NOLOCK)
            WHERE paper = @paper
            ORDER BY id
            """,
            new { paper = Ansi(paper.Trim(), 10) },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<StockBatchLine>> GetStockBatchesAsync(string dept, string? nnoContains, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var where = "dpm = @dpm";
        var p = new Dictionary<string, object?> { ["dpm"] = Ansi(dept.Trim(), 10) };
        if (!string.IsNullOrWhiteSpace(nnoContains))
        {
            where += " AND RTRIM(nno) LIKE '%' + @nno + '%'";
            p["nno"] = nnoContains.Trim();
        }
        var rows = await conn.QueryAsync<StockBatchLine>(new CommandDefinition(
            $"""
            SELECT TOP (200) id, RTRIM(cp) AS Cp, RTRIM(nno) AS Nno, RTRIM(na1) AS Na1, RTRIM(ty) AS Ty,
                   RTRIM(fa) AS Fa, RTRIM(unit) AS Unit, RTRIM(depotno) AS Depotno, RTRIM(posi) AS Posi,
                   kcamount AS Kcamount, iprc AS Iprc, iprj AS Iprj, oprc AS Oprc
            FROM tbisto WITH (NOLOCK)
            WHERE {where}
            ORDER BY nno, depotno, posi, id
            """,
            p, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<string> AddLineAsync(string paper, AddStockAdjustLineCommand command, string dept, string per, CancellationToken ct = default)
    {
        command.Validate();
        var paperTrim = (paper ?? "").Trim();
        if (paperTrim.Length == 0)
            throw new AppException("PAPER_REQUIRED", "请先选择已生成未确认的调仓单或新建调仓单！", 422);
        var depotno1 = command.Depotno1.Trim();
        var posi1 = (command.Posi1 ?? "").Trim();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 源批次快照（a_.row150 L8 SCATTER MEMVAR FROM C_ISTO）
            var b = await conn.QueryFirstOrDefaultAsync<(int id, string cp, string nno, string na1, string ty, string fa,
                string unit, string depotno, string posi, decimal kc, int xno, decimal iprc, decimal iprj,
                decimal oprb, string dc)>(new CommandDefinition(
                """
                SELECT id, RTRIM(cp) AS cp, RTRIM(nno) AS nno, RTRIM(na1) AS na1, RTRIM(ty) AS ty, RTRIM(fa) AS fa,
                       RTRIM(unit) AS unit, RTRIM(depotno) AS depotno, RTRIM(posi) AS posi, kcamount AS kc,
                       xno, iprc, iprj, oprb, RTRIM(dc) AS dc
                FROM tbisto WITH (UPDLOCK)
                WHERE id = @kcid AND dpm = @dpm
                """,
                new { kcid = command.Kcid, dpm = Ansi(dept.Trim(), 10) },
                transaction: tx, cancellationToken: ct));
            if (b.id == 0)
                throw new AppException("BATCH_NOT_FOUND", $"库存批次 {command.Kcid} 不存在", 422);

            // 约束（a_.row150 L48）：不允许调往原库号原仓位
            if (b.depotno == depotno1 && b.posi == posi1)
                throw new AppException("SAME_POSITION", "不允许调往原库号原仓位！", 422);

            // 约束（a_.row150 L52-58）：同单同 kcid 累计不超源批次库存
            var existed = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(ckamount) FROM tbsadc WITH (NOLOCK) WHERE kcid = @kcid AND paper = @paper",
                new { kcid = command.Kcid, paper = Ansi(paperTrim, 10) },
                transaction: tx, cancellationToken: ct)) ?? 0m;
            if (existed + command.Ckamount > b.kc)
                throw new AppException("OVER_STOCK",
                    $"该零件已在该调仓单中存在！且继续调仓数量将超出系统中该仓位的原数量！（源库存 {b.kc}，本单已调 {existed}）",
                    422);

            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO tbsadc
                    (cp, dpm, nno, na1, ty, fa, unit, depotno, posi, depotno1, posi1, paper, outdate,
                     ckamount, xno, iprc, oprc, iprj, oprb, per, note, kcid, dc)
                VALUES
                    (@cp, @dpm, @nno, @na1, @ty, @fa, @unit, @depotno, @posi, @depotno1, @posi1, @paper, @outdate,
                     @ckamount, @xno, @iprc, @oprc, @iprj, @oprb, @per, @note, @kcid, @dc)
                """,
                new
                {
                    cp = Ansi(b.cp, 10), dpm = Ansi(dept.Trim(), 10), nno = Ansi(b.nno, 18), na1 = Ansi(b.na1, 40),
                    ty = Ansi(b.ty, 14), fa = Ansi(b.fa, 10), unit = Ansi(b.unit, 4),
                    depotno = Ansi(b.depotno, 10), posi = Ansi(b.posi, 10),
                    depotno1 = Ansi(depotno1, 10), posi1 = Ansi(posi1, 10),
                    paper = Ansi(paperTrim, 10), outdate = DateTime.Today,
                    ckamount = command.Ckamount, xno = b.xno, iprc = b.iprc,
                    oprc = command.Oprc, iprj = b.iprj, oprb = b.oprb,
                    per = Ansi(per, 10), note = Ansi((command.Note ?? "").Trim(), 100),
                    kcid = command.Kcid, dc = Ansi(b.dc, 8),
                },
                transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return paperTrim;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> UpdateLineAsync(UpdateStockAdjustLineCommand command, string per, CancellationToken ct = default)
    {
        if (command.Ckamount < 0)
            throw new AppException("INVALID_AMOUNT", "请输入有效调仓数量！", 422);
        await using var conn = await factory.OpenAsync(ct);
        // -_.row168 L44：UPDATE tbsadc SET oprc,depotno1,posi1,ckamount,per WHERE id
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tbsadc SET oprc = @oprc, depotno1 = @depotno1, posi1 = @posi1, ckamount = @ckamount, per = @per
            WHERE id = @id
            """,
            new
            {
                oprc = command.Oprc,
                depotno1 = Ansi(command.Depotno1.Trim(), 10),
                posi1 = Ansi((command.Posi1 ?? "").Trim(), 10),
                ckamount = command.Ckamount,
                per = Ansi(per, 10),
                id = command.Id,
            },
            cancellationToken: ct));
    }

    public async Task<int> DeleteLineAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbsadc WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<int> DeletePaperAsync(string paper, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // __.row9：已确认（m='*'）拒删
        var confirmed = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbsadc WITH (NOLOCK) WHERE paper = @paper AND RTRIM(m) = '*'",
            new { paper = Ansi(paper.Trim(), 10) }, cancellationToken: ct));
        if (confirmed > 0)
            throw new AppException("ADJUST_CONFIRMED", "该调仓单已确认，不能删除！", 422);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbsadc WHERE paper = @paper AND ISNULL(RTRIM(m), '') <> '*'",
            new { paper = Ansi(paper.Trim(), 10) }, cancellationToken: ct));
    }

    public async Task<ConfirmStockAdjustResult> ConfirmAsync(string paper, string dept, CancellationToken ct = default)
    {
        var paperTrim = paper.Trim();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // __.row11 L20-24：已确认拒重
            var already = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM tbsadc WITH (NOLOCK) WHERE paper = @paper AND m = '*'",
                new { paper = Ansi(paperTrim, 10) }, transaction: tx, cancellationToken: ct));
            if (already > 0)
                throw new AppException("ADJUST_CONFIRMED", $"此调仓单 【{paperTrim}】 已经确认了，请检查。", 409);

            // 待确认行（__.row11 L28：WHERE paper AND len(rtrim(m))=0）
            var rows = (await conn.QueryAsync<AdjustRow>(new CommandDefinition(
                """
                SELECT id, RTRIM(cp) AS cp, RTRIM(nno) AS nno, RTRIM(na1) AS na1, RTRIM(ty) AS ty, RTRIM(fa) AS fa,
                       RTRIM(unit) AS unit, RTRIM(depotno) AS depotno, RTRIM(posi) AS posi,
                       RTRIM(depotno1) AS depotno1, RTRIM(posi1) AS posi1, ckamount, iprc, iprj, oprc, oprct, oprb,
                       RTRIM(note) AS note, xno, RTRIM(dc) AS dc, kcid, RTRIM(dpm) AS dpm
                FROM tbsadc WITH (UPDLOCK)
                WHERE paper = @paper AND ISNULL(RTRIM(m), '') = ''
                ORDER BY id
                """,
                new { paper = Ansi(paperTrim, 10) },
                transaction: tx, cancellationToken: ct))).AsList();

            var confirmedCount = 0;
            var lost = 0;
            foreach (var r in rows)
            {
                // 校验（__.row11 L41-44）：源批次（kcid+cp+nno+depotno+posi+iprc）库存充足
                var kc = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    """
                    SELECT ISNULL(SUM(kcamount), 0) FROM tbisto
                    WHERE id = @kcid AND cp = @cp AND nno = @nno AND depotno = @depotno AND posi = @posi AND iprc = @iprc
                    """,
                    new
                    {
                        kcid = r.kcid, cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18),
                        depotno = Ansi(r.depotno, 10), posi = Ansi(r.posi, 10), iprc = r.iprc,
                    },
                    transaction: tx, cancellationToken: ct));
                if (kc < r.ckamount)
                {
                    lost++;
                    continue;
                }

                // ISAPPE('tbsadc', id, -ckam)：源仓位减
                await MoveBatchAsync(conn, tx, r, r.depotno, r.posi, -r.ckamount, ct);
                // ISAPPE('tbsadc', id, +ckam, 1)：目标仓位加（TYPE=1 → posi1/depotno1，pg_proc L1566-1573）
                await MoveBatchAsync(conn, tx, r, r.depotno1, r.posi1, r.ckamount, ct);

                // m='*'（__.row11 L50-51）
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbsadc SET m = '*' WHERE id = @id",
                    new { id = r.id }, transaction: tx, cancellationToken: ct));
                confirmedCount++;
            }

            await tx.CommitAsync(ct);
            return new ConfirmStockAdjustResult { ConfirmedLines = confirmedCount, LostLines = lost };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>tbsadc 待确认行投影。</summary>
    private sealed class AdjustRow
    {
        public int id { get; set; }
        public string cp { get; set; } = "";
        public string nno { get; set; } = "";
        public string na1 { get; set; } = "";
        public string ty { get; set; } = "";
        public string fa { get; set; } = "";
        public string unit { get; set; } = "";
        public string depotno { get; set; } = "";
        public string posi { get; set; } = "";
        public string depotno1 { get; set; } = "";
        public string posi1 { get; set; } = "";
        public decimal ckamount { get; set; }
        public decimal iprc { get; set; }
        public decimal iprj { get; set; }
        public decimal oprc { get; set; }
        public decimal oprct { get; set; }
        public decimal oprb { get; set; }
        public string note { get; set; } = "";
        public int xno { get; set; }
        public string dc { get; set; } = "";
        public int kcid { get; set; }
        public string dpm { get; set; } = "";
    }

    /// <summary>ISAPPE（IsappeEngine，pg_proc 主程序库版 L1546-1668）：批次匹配（P_WAY 运行时读 tbsysset），
    /// 命中批次 ORDER BY kcamount DESC；未命中 cxnno=S_NNO(nno)、oprc=OPRCT 优先。</summary>
    private static async Task MoveBatchAsync(SqlConnection conn, SqlTransaction tx, AdjustRow r,
        string depotno, string posi, decimal delta, CancellationToken ct)
    {
        var prm = await IsappeEngine.LoadParamsAsync(conn, tx, ct);
        // ISAPPE('tbsadc', id, Δ, 1)：M_TYPE 非空 + TBSADC → 匹配目标 = posi1/depotno1
        await IsappeEngine.ApplyAsync(
            conn, tx,
            new IsappeSource(r.dpm, r.cp, r.nno, r.na1, r.ty, r.fa, r.unit,
                r.iprj, r.iprc, r.oprc, r.oprct, r.oprb, posi, depotno, r.note, r.xno, r.dc),
            delta, prm, IsappeLibrary.MainProc, DateTime.Today, ct);
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value, IsAnsi = true, Length = length };
}
