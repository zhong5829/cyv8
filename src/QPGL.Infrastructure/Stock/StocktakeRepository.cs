using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;

namespace QPGL.Infrastructure.Stock;

public interface IStocktakeRepository
{
    /// <summary>生成盘点单（fr_istopd row114）：tbdjgs 前缀保障 → getnewdh 取号 → tbisto 快照拷贝入 tbistopd。</summary>
    Task<string> GeneratePaperAsync(GenerateStocktakeCommand command, string dept, string per, CancellationToken ct = default);

    /// <summary>查询未确认盘点单列表（fr_istopd row2 setpaper：mark=0 GROUP BY pdpaper；cp 空=不限厂牌）。</summary>
    Task<IReadOnlyList<StocktakePaper>> GetOpenPapersAsync(string dept, string? cp = null, CancellationToken ct = default);

    /// <summary>查询盘点单明细（tbistopd WHERE pdpaper）。</summary>
    Task<IReadOnlyList<StocktakeLine>> GetLinesAsync(string pdpaper, CancellationToken ct = default);

    /// <summary>保存实盘行（fr_istopd row95：pdposi/npd/nyk，nyk=npd-kcamount）。</summary>
    Task<int> SaveCountLineAsync(SaveStocktakeLineCommand command, CancellationToken ct = default);

    /// <summary>整单实盘重算（fr_istopd Z_.row122 L64 语义，按 pdpaper 限定——旧系统为无 WHERE 全表，见方法注释）。</summary>
    Task<int> RecalcNykAsync(string pdpaper, CancellationToken ct = default);

    /// <summary>盘点调整仓位（fr_istopd 鐶.row47）：tbistopd.posi + tbisto.posi（kcid）+ tbistoch 修改历史。</summary>
    Task<int> AdjustPosiAsync(string pdpaper, string per, CancellationToken ct = default);

    /// <summary>确认盘点单（fr_istopd row49）：nyk<>0 → zt='1',mark='1'；nyk=0 → DELETE。</summary>
    Task<ConfirmStocktakeResult> ConfirmAsync(string pdpaper, CancellationToken ct = default);

    /// <summary>生成散件入库草稿（fr_istopd row92）：nyk<>0 行 → tbistosm（GYNO='PDRK'），已存在则更新。</summary>
    Task<GenerateInStockDraftResult> GenerateInStockDraftAsync(string pdpaper, string per, CancellationToken ct = default);
}

/// <summary>
/// 库存盘点仓储（二期，写路径契约感知）。旧实现出处（逆向源码\new_qpgl_表单方法\fr_istopd\）：
///   - 生成盘点单 row114：先保障 tbdjgs 有「盘点单」行（djjx=部门+'P'/空='PD'，lsh=0），
///     再 GETNEWDH(1,'盘点单','tbistopd','pdpaper') 取号，最后
///     INSERT INTO tbistopd(...) SELECT ... FROM tbisto（npd=ISNULL(kcamount,0)、nyk=0、mark=0、
///     kcid=tbisto.id、pdposi=posi、per/pd_date/mark=0），WHERE cp=?p_cp + 筛选 + dpm=?p_dpm
///   - 录实盘 row95 Click：PASSTH update tbistopd 'pdposi,npd,nyk' where id（nyk=客户端算 npd-kcamount）
///   - 调仓位 鐶.row47：对 pdposi<>posi 行 UPDATE tbistopd.posi + UPDATE tbisto.posi(id=kcid)
///     + INSERT tbistoch('CP,NNO,NA1,TY,UNIT,DEPOTNO,posi,xgDATE,PER,NOTE'，note='修改仓位：A -> B')
///   - 确认 row49：UPDATE tbistopd SET pd_date,zt='1',mark='1' WHERE nyk<>0 AND pdpaper=?
///     + DELETE tbistopd WHERE nyk=0 AND pdpaper=?
///   - 生成入库草稿 row92：tbistopd WHERE pdpaper（nyk<>0 行）→ INSERT tbistosm
///     （JKAMOUNT=NYK 可为负=盘亏、POSI=PDPOSI、GYNO='PDRK'、GYNAME='盘点入库'、FKFS='现金'、
///     CNO=pdpaper、INDATE=P_RQ、OPRB=tbisto max(oprb) by cp+nno），草稿已存在（tbistosm 有同 cno）则
///     UPDATE（where cno+pdpaper and nno=行nno）；生效走既有散件入库流程（回放场景 7 已覆盖）
/// 契约偏差注记（有意为之，已评审）：
///   1) pd_date：旧系统 GETDATE() 含时刻，tbistopd.pd_date 消费方仅取日期（DTOC(TTOD())）；
///      为回放确定性两侧统一写 DateTime.Today（00:00:00）。
///   2) RecalcNyk：旧系统 Z_.row122 L64 为无 WHERE 的全表 UPDATE（单机假设），多部门并发下会
///      误清他单 mark；新系统限定 pdpaper。回放库隔离且场景仅含回放单，diff 结果一致。
///   3) mark 类型为 int：0 未确认 / 1 已确认 / -1 重复行标记（Z_.row122）。
/// </summary>
public sealed class StocktakeRepository(SqlConnectionFactory factory, IDocNoService docNo) : IStocktakeRepository
{
    public async Task<string> GeneratePaperAsync(GenerateStocktakeCommand command, string dept, string per, CancellationToken ct = default)
    {
        var deptTrim = dept.Trim();
        var cpTrim = (command.Cp ?? "").Trim();   // 旧 fr_istopd row114：cp=?p_cp（会话厂牌，AppSession.Brand 注入）

        // 1) tbdjgs 前缀保障（row114 L16-24）：无「盘点单」行则插入（djjx = P_DPM+'P'/空 'PD'）。
        //    独立自动提交（旧系统 PASSTH insert 即时生效），避免与 DocNoService 取号事务互锁。
        await using (var conn = await factory.OpenAsync(ct))
        {
            var prefixExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM tbdjgs WITH (UPDLOCK, ROWLOCK) WHERE dpm = @dpm AND djname = N'盘点单'",
                new { dpm = Ansi(deptTrim, 10) }, cancellationToken: ct));
            if (prefixExists == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbdjgs (djname, djjx, lsh, dyear, dpm)
                    VALUES (N'盘点单', @djjx, 0, @dyear, @dpm)
                    """,
                    new
                    {
                        djjx = Ansi(deptTrim.Length > 0 ? deptTrim + "P" : "PD", 4),
                        dyear = Ansi(DateTime.Now.Year.ToString(), 4),
                        dpm = Ansi(deptTrim.ToUpperInvariant(), 10),
                    },
                    cancellationToken: ct));
            }
        }

        // 2) 取号（getnewdh '盘点单'；防重目标 = tbistopd.pdpaper，pg_proc.getnewdh L2042 语义）
        var paper = await docNo.GetNewDocNoAsync("盘点单", deptTrim,
            new DocNoService.DocNoCheckTarget("tbistopd", "pdpaper"), ct);

        await using var conn2 = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn2.BeginTransactionAsync(ct);
        try
        {
            // 3) tbisto 快照拷贝（row114 L33-34 原样，PASSTH 补 dpm 契约已在 SELECT 列中）
            var where = "dpm = @dpm";
            var p = new Dictionary<string, object?>
            {
                ["dpm"] = Ansi(deptTrim, 10),
                ["paper"] = Ansi(paper, 15),
                ["per"] = Ansi(per, 10),
                ["pdDate"] = DateTime.Today,
            };
            if (cpTrim.Length > 0)
            {
                where += " AND cp = @cp";
                p["cp"] = Ansi(cpTrim, 10);
            }
            if (!string.IsNullOrWhiteSpace(command.NnoPrefix))
            {
                where += " AND RTRIM(nno) LIKE @nno + '%'";
                p["nno"] = command.NnoPrefix.Trim();
            }
            if (!string.IsNullOrWhiteSpace(command.Na1Contains))
            {
                where += " AND RTRIM(na1) LIKE '%' + @na1 + '%'";
                p["na1"] = command.Na1Contains.Trim();
            }
            if (!string.IsNullOrWhiteSpace(command.Depotno))
            {
                where += " AND RTRIM(depotno) = @depotno";
                p["depotno"] = command.Depotno.Trim();
            }

            var inserted = await conn2.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO tbistopd
                    (cp, dpm, nno, cxnno, na1, ty, fa, unit, iprj, kcamount, iprc, posi, oprc,
                     npd, nyk, dc, depotno, kcid, pdpaper, per, pd_date, mark, pdposi)
                SELECT cp, dpm, nno, cxnnO, na1, ty, fa, unit, iprj, ISNULL(kcamount, 0), iprc, posi, oprc,
                       ISNULL(kcamount, 0), 0, dc, depotno, id, @paper, @per, @pdDate, 0, posi
                FROM tbisto
                WHERE {where}
                """,
                p, transaction: tx, cancellationToken: ct));
            if (inserted == 0)
                throw new AppException("STOCKTAKE_EMPTY", "筛选条件下无可盘库存行，未生成盘点单", 422);

            await tx.CommitAsync(ct);
            return paper;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<StocktakePaper>> GetOpenPapersAsync(string dept, string? cp = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cpTrim = (cp ?? "").Trim();
        var where = "dpm = @dpm AND mark = 0";
        var p = new Dictionary<string, object?> { ["dpm"] = Ansi(dept.Trim(), 10) };
        if (cpTrim.Length > 0)
        {
            where += " AND cp = @cp";
            p["cp"] = Ansi(cpTrim, 10);
        }
        var rows = await conn.QueryAsync<StocktakePaper>(new CommandDefinition(
            $"""
            SELECT RTRIM(pdpaper) AS Pdpaper, RTRIM(MAX(per)) AS Per, MAX(pd_date) AS PdDate,
                   COUNT(1) AS LineCount, MAX(mark) AS Mark
            FROM tbistopd WITH (NOLOCK)
            WHERE {where}
            GROUP BY pdpaper
            ORDER BY pdpaper
            """,
            p, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<StocktakeLine>> GetLinesAsync(string pdpaper, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<StocktakeLine>(new CommandDefinition(
            """
            SELECT id, RTRIM(cp) AS Cp, RTRIM(nno) AS Nno, RTRIM(na1) AS Na1, RTRIM(ty) AS Ty, RTRIM(fa) AS Fa,
                   RTRIM(unit) AS Unit, RTRIM(depotno) AS Depotno, RTRIM(posi) AS Posi, RTRIM(pdposi) AS Pdposi,
                   kcamount AS Kcamount, npd AS Npd, nyk AS Nyk, iprc AS Iprc, iprj AS Iprj,
                   RTRIM(note) AS Note, kcid AS Kcid, mark AS Mark, RTRIM(pdpaper) AS Pdpaper,
                   RTRIM(per) AS Per, pd_date AS PdDate
            FROM tbistopd WITH (NOLOCK)
            WHERE pdpaper = @pdpaper
            ORDER BY id
            """,
            new { pdpaper = Ansi(pdpaper.Trim(), 15) },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> SaveCountLineAsync(SaveStocktakeLineCommand command, CancellationToken ct = default)
    {
        if (command.Id <= 0)
            throw new AppException("ID_REQUIRED", "盘点行 id 无效", 422);

        await using var conn = await factory.OpenAsync(ct);
        // row95：m.NYK = m.NPD - KCAMOUNT（客户端算）→ UPDATE pdposi,npd,nyk；等效于同语句内引用列
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tbistopd SET pdposi = @pdposi, npd = @npd, nyk = @npd - kcamount WHERE id = @id
            """,
            new { pdposi = Ansi(command.Pdposi.Trim(), 10), npd = command.Npd, id = command.Id },
            cancellationToken: ct));
    }

    public async Task<int> RecalcNykAsync(string pdpaper, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // Z_.row122 L64 语义（限定 pdpaper，见类头偏差注记 2）
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbistopd SET nyk = npd - kcamount, mark = 0 WHERE pdpaper = @pdpaper",
            new { pdpaper = Ansi(pdpaper.Trim(), 15) },
            cancellationToken: ct));
    }

    public async Task<int> AdjustPosiAsync(string pdpaper, string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var rows = (await conn.QueryAsync<(int id, int kcid, string posi, string pdposi, string cp, string nno,
                string na1, string ty, string unit, string depotno)>(new CommandDefinition(
                """
                SELECT id, kcid, RTRIM(posi) AS posi, RTRIM(pdposi) AS pdposi, RTRIM(cp) AS cp, RTRIM(nno) AS nno,
                       RTRIM(na1) AS na1, RTRIM(ty) AS ty, RTRIM(unit) AS unit, RTRIM(depotno) AS depotno
                FROM tbistopd WITH (UPDLOCK)
                WHERE pdpaper = @pdpaper AND pdposi <> posi
                """,
                new { pdpaper = Ansi(pdpaper.Trim(), 15) },
                transaction: tx, cancellationToken: ct))).AsList();

            foreach (var r in rows)
            {
                // 鐶.row47 L29-34：tbistopd.posi + tbisto.posi(kcid) + tbistoch 历史
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbistopd SET posi = @pdposi WHERE id = @id",
                    new { pdposi = Ansi(r.pdposi, 10), id = r.id },
                    transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbisto SET posi = @pdposi WHERE id = @kcid",
                    new { pdposi = Ansi(r.pdposi, 10), kcid = r.kcid },
                    transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbistoch (cp, dpm, nno, na1, ty, unit, depotno, posi, xgdate, per, note)
                    VALUES (@cp, @dpm, @nno, @na1, @ty, @unit, @depotno, @posi, @xgdate, @per, @note)
                    """,
                    new
                    {
                        cp = Ansi(r.cp, 10),
                        dpm = Ansi("", 10),
                        nno = Ansi(r.nno, 18),
                        na1 = Ansi(r.na1, 40),
                        ty = Ansi(r.ty, 14),
                        unit = Ansi(r.unit, 4),
                        depotno = Ansi(r.depotno, 10),
                        posi = Ansi(r.pdposi, 10),
                        xgdate = DateTime.Today,
                        per = Ansi(per, 6),
                        note = Ansi($"修改仓位：{r.posi} -> {r.pdposi}", 100),
                    },
                    transaction: tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ConfirmStocktakeResult> ConfirmAsync(string pdpaper, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // row49 L25-26：pd_date=GETDATE()（见类头偏差注记 1，统一 Today）
            var marked = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbistopd SET pd_date = @pdDate, zt = '1', mark = '1' WHERE nyk <> 0 AND pdpaper = @pdpaper",
                new { pdDate = DateTime.Today, pdpaper = Ansi(pdpaper.Trim(), 15) },
                transaction: tx, cancellationToken: ct));
            var deleted = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tbistopd WHERE nyk = 0 AND pdpaper = @pdpaper",
                new { pdpaper = Ansi(pdpaper.Trim(), 15) },
                transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return new ConfirmStocktakeResult { MarkedLines = marked, DeletedLines = deleted };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<GenerateInStockDraftResult> GenerateInStockDraftAsync(string pdpaper, string per, CancellationToken ct = default)
    {
        var cno = pdpaper.Trim();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // row92 L25-38：草稿/历史存在性检测（已存在则走更新分支）
            var hasDraft = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM tbistosm WITH (UPDLOCK) WHERE cno = @cno",
                new { cno = Ansi(cno, 15) },
                transaction: tx, cancellationToken: ct)) > 0;

            var lines = (await conn.QueryAsync<(int id, string cp, string dpm, string nno, string na1, string ty, string fa,
                string dc, string unit, decimal nyk, decimal iprj, decimal iprc, decimal oprc, string pdposi,
                string depotno, string note)>(new CommandDefinition(
                """
                SELECT id, RTRIM(cp) AS cp, RTRIM(dpm) AS dpm, RTRIM(nno) AS nno, RTRIM(na1) AS na1, RTRIM(ty) AS ty,
                       RTRIM(fa) AS fa, RTRIM(dc) AS dc, RTRIM(unit) AS unit, nyk, iprj, iprc, oprc,
                       RTRIM(pdposi) AS pdposi, RTRIM(depotno) AS depotno, RTRIM(note) AS note
                FROM tbistopd WITH (NOLOCK)
                WHERE pdpaper = @pdpaper
                ORDER BY id
                """,
                new { pdpaper = Ansi(cno, 15) },
                transaction: tx, cancellationToken: ct))).AsList();

            var inserted = 0;
            var updated = 0;
            foreach (var l in lines)
            {
                if (l.nyk == 0)
                    continue;
                // row92 L56-57：OPRB = tbisto max(oprb) by cp+nno（NVL 0）
                var oprb = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                    "SELECT MAX(oprb) FROM tbisto WITH (NOLOCK) WHERE cp = @cp AND nno = @nno",
                    new { cp = Ansi(l.cp, 10), nno = Ansi(l.nno, 18) },
                    transaction: tx, cancellationToken: ct)) ?? 0m;

                if (hasDraft)
                {
                    // row92 L73：UPDATE 分支（cno=pdpaper AND nno=行nno）
                    updated += await conn.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE tbistosm SET cp = @cp, dpm = @dpm, na1 = @na1, ty = @ty, fa = @fa, dc = @dc,
                               unit = @unit, jkamount = @jkamount, iprj = @iprj, iprc = @iprc, oprc = @oprc,
                               posi = @posi, depotno = @depotno, indate = @indate, gyno = @gyno, gyname = @gyname,
                               per = @per, note = @note, fkfs = @fkfs, oprb = @oprb
                        WHERE cno = @cno AND nno = @nno
                        """,
                        new
                        {
                            cp = Ansi(l.cp, 10), dpm = Ansi(l.dpm, 10), na1 = Ansi(l.na1, 40), ty = Ansi(l.ty, 14),
                            fa = Ansi(l.fa, 8), dc = Ansi(l.dc, 8), unit = Ansi(l.unit, 4), jkamount = l.nyk,
                            iprj = l.iprj, iprc = l.iprc, oprc = l.oprc, posi = Ansi(l.pdposi, 10),
                            depotno = Ansi(l.depotno, 10), indate = DateTime.Today, cno = Ansi(cno, 15),
                            gyno = Ansi("PDRK", 10), gyname = Ansi("盘点入库", 60), per = Ansi(per, 6),
                            note = Ansi(l.note, 100), fkfs = Ansi("现金", 10), oprb, nno = Ansi(l.nno, 18),
                        },
                        transaction: tx, cancellationToken: ct));
                }
                else
                {
                    // row92 L59：INSERT 分支（列集 CP,NNO,NA1,TY,FA,DC,UNIT,JKAMOUNT,IPRJ,IPRC,OPRC,POSI,
                    // DEPOTNO,INDATE,CNO,GYNO,GYNAME,PER,NOTE,FKFS,OPRB + PASSTH 补 dpm）
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbistosm
                            (cp, dpm, nno, na1, ty, fa, dc, unit, jkamount, iprj, iprc, oprc, posi, depotno,
                             indate, cno, gyno, gyname, per, note, fkfs, oprb)
                        VALUES
                            (@cp, @dpm, @nno, @na1, @ty, @fa, @dc, @unit, @jkamount, @iprj, @iprc, @oprc, @posi, @depotno,
                             @indate, @cno, @gyno, @gyname, @per, @note, @fkfs, @oprb)
                        """,
                        new
                        {
                            cp = Ansi(l.cp, 10), dpm = Ansi(l.dpm, 10), nno = Ansi(l.nno, 18), na1 = Ansi(l.na1, 40),
                            ty = Ansi(l.ty, 14), fa = Ansi(l.fa, 8), dc = Ansi(l.dc, 8), unit = Ansi(l.unit, 4),
                            jkamount = l.nyk, iprj = l.iprj, iprc = l.iprc, oprc = l.oprc, posi = Ansi(l.pdposi, 10),
                            depotno = Ansi(l.depotno, 10), indate = DateTime.Today, cno = Ansi(cno, 15),
                            gyno = Ansi("PDRK", 10), gyname = Ansi("盘点入库", 60), per = Ansi(per, 6),
                            note = Ansi(l.note, 100), fkfs = Ansi("现金", 10), oprb,
                        },
                        transaction: tx, cancellationToken: ct));
                    inserted++;
                }
            }

            await tx.CommitAsync(ct);
            return new GenerateInStockDraftResult { Cno = cno, InsertedLines = inserted, UpdatedLines = updated };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value, IsAnsi = true, Length = length };
}
