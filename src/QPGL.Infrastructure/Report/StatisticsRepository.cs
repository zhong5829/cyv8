using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Report;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Report;

public interface IStatisticsRepository
{
    /// <summary>日营业统计网格（D1，fr_rtj row3：销售/入库按日期聚合合并）。</summary>
    Task<List<DailyStatRow>> GetDailyStatAsync(string dpm, CancellationToken ct = default);

    /// <summary>日营业统计确认（D1 写路径，fr_rtj row5 原样序列）。</summary>
    Task<PostDailyStatResult> PostDailyStatAsync(DateTime date, string dpm, string per, CancellationToken ct = default);

    /// <summary>恢复未统计（D2 写路径，fr_hftj 0_.row5 原样序列）。</summary>
    Task UnpostDailyStatAsync(DateTime date, string dpm, string per, CancellationToken ct = default);
}

/// <summary>
/// 统计写路径仓储（D1/D2，二期）。旧实现出处：fr_rtj\__.row5 / fr_hftj\0_.row5。
/// 契约要点：
///   D1 统计（按日期，单事务）：
///     1) 销售组 = tbsada DISTINCT paper/gname/guno/pno/per1 WHERE outdate∈[d,d+1) AND type='销售'
///        AND dpm [+「不统计财务未确认」时 AND mark&lt;&gt;'']
///     2) 组内聚合（mark='' 行）：PT=ROUND(Σoprc*(100-yhl)/100*ckamount,2)、PTSJ=ROUND(Σoprct*…,2)
///     3) guno∈(汇款,托收,挂帐) 且 PT≠0 → INSERT tbysyf(type='销售', datime=d, cno=paper, ys=PT,
///        pno=pno+' '+per1, xm=fkfs, cb/yjb/tick 继承该客户末行) + PTSJ-PT≠0 再插 KH 差额行
///     4) UPDATE tbsada SET type='历史'（该日期未统计销售单）；
///        jzdate：汇款/托收/挂帐 未收款单=1900-01-01、其余=M_DATE
///     5) 入库组 = tbistoed DISTINCT seano/gyname/fkfs/pno/per/isord WHERE indate∈… AND type='入库'
///        [+「不统计急件」时 AND cp&lt;&gt;'急件'] → fkfs='挂帐' 且 gyname 非空 → INSERT tbysyf(type='入库', yf=PT)
///     6) UPDATE tbistoed SET type='历史'
///   D2 恢复（0_.row5）：
///     月结保护（tbyj.max(yjdate)+1 &gt; 日期 拒绝）→ tbsada/tbistoed type 回滚（已收款行除外：
///     tbysyf 中 datime=d 且 skmoney≠0）→ DELETE tbysyf skmoney=0 且单据已回滚的行。
/// 幂等性：D1 靠 type 翻转（'销售'→'历史'）；D2 靠收款行保护（skmoney≠0 不回滚）。
/// </summary>
public sealed class StatisticsRepository(SqlConnectionFactory factory) : IStatisticsRepository
{
    public async Task<List<DailyStatRow>> GetDailyStatAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var prm = await LoadParamsAsync(conn, null, ct);
        var tjJj = prm.IfrtjNoJj ? " AND cp <> N'急件'" : "";
        var tjWqr = prm.IfrtjNoQr ? " AND mark <> ''" : "";

        var sales = (await conn.QueryAsync<(DateTime outdate, int papers, decimal ckamount, decimal iprj,
            decimal iprc, decimal oprc, decimal oprct)>(new CommandDefinition(
            $"""
            SELECT CAST(outdate AS date) outdate, COUNT(DISTINCT paper) papers, SUM(ckamount) ckamount,
                   ROUND(SUM(iprj * ckamount), 2) iprj, ROUND(SUM(iprc * ckamount), 2) iprc,
                   SUM(oprc * ckamount * (100 - ISNULL(yhl, 0)) / 100.0) oprc,
                   SUM(oprct * ckamount * (100 - ISNULL(yhl, 0)) / 100.0) oprct
            FROM tbsada WITH (NOLOCK)
            WHERE type = N'销售' AND dpm = @dpm{tjWqr}
            GROUP BY CAST(outdate AS date)
            """,
            new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();

        var instock = (await conn.QueryAsync<(DateTime indate, int rccno, decimal rcamt, decimal rciprj, decimal rciprc)>(
            new CommandDefinition(
                $"""
                SELECT CAST(indate AS date) indate, COUNT(DISTINCT seano + isord) rccno, SUM(jkamount) rcamt,
                       ROUND(SUM(iprj * jkamount), 2) rciprj, ROUND(SUM(iprc * jkamount), 2) rciprc
                FROM tbistoed WITH (NOLOCK)
                WHERE type = N'入库' AND dpm = @dpm{tjJj}
                GROUP BY CAST(indate AS date)
                """,
                new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();

        // 剩余未统计单据日期集合（动态 MARK 展示）
        var openDates = (await conn.QueryAsync<DateTime>(new CommandDefinition(
            "SELECT DISTINCT CAST(outdate AS date) FROM tbsada WITH (NOLOCK) WHERE type = N'销售' AND dpm = @dpm " +
            "UNION SELECT DISTINCT CAST(indate AS date) FROM tbistoed WITH (NOLOCK) WHERE type = N'入库' AND dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).ToHashSet();

        var rows = new Dictionary<DateTime, DailyStatRow>();
        foreach (var s in sales)
        {
            var r = GetRow(rows, s.outdate);
            r.PaperCount = s.papers; r.Ckamount = s.ckamount; r.Iprj = s.iprj; r.Iprc = s.iprc;
            r.Oprc = s.oprc; r.Oprct = s.oprct;
            r.Ml = s.oprct - (prm.Wbtohs ? s.iprj : s.iprc);   // P_XSJEGS='1' 恒等（其他金额公式为已知偏差）
        }
        foreach (var i in instock)
        {
            var r = GetRow(rows, i.indate);
            r.Rccno = i.rccno; r.Rcamt = i.rcamt; r.Rciprj = i.rciprj; r.Rciprc = i.rciprc;
        }
        foreach (var r in rows.Values)
            r.Mark = openDates.Contains(r.Outdate) ? "未统计" : "已统计";
        return rows.Values.OrderBy(r => r.Outdate).ToList();
    }

    public async Task<PostDailyStatResult> PostDailyStatAsync(DateTime date, string dpm, string per, CancellationToken ct = default)
    {
        var result = new PostDailyStatResult();
        var d = date.Date;
        var d2 = d.AddDays(1);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var prm = await LoadParamsAsync(conn, tx, ct);
            var tjJj = prm.IfrtjNoJj ? " AND cp <> N'急件'" : "";
            var tjWqr = prm.IfrtjNoQr ? " AND mark <> ''" : "";

            // 1) 销售组（row5 L21；DISTINCT 游标加 ORDER BY paper 保证两侧行序一致）
            var groups = (await conn.QueryAsync<(string paper, string gname, string guno, string pno, string per1)>(
                new CommandDefinition(
                    $"""
                    SELECT DISTINCT RTRIM(paper), RTRIM(ISNULL(gname, '')), RTRIM(ISNULL(guno, '')),
                           RTRIM(ISNULL(pno, '')), RTRIM(ISNULL(per1, ''))
                    FROM tbsada WITH (UPDLOCK)
                    WHERE outdate >= @d AND outdate < @d2 AND type = N'销售' AND dpm = @dpm{tjWqr}
                    ORDER BY 1
                    """,
                    new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct))).AsList();

            foreach (var g in groups)
            {
                result.SalesGroups++;
                // 2) 组内聚合（row5 L29-33，mark=''）
                var agg = await conn.QueryFirstOrDefaultAsync<(decimal pt, decimal myhl, decimal ptsj)>(
                    new CommandDefinition(
                        """
                        SELECT ROUND(SUM(oprc * (100 - ISNULL(yhl, 0)) / 100.0 * ckamount), 2),
                               MIN(ISNULL(yhl, 0)),
                               ROUND(SUM(oprct * (100 - ISNULL(yhl, 0)) / 100.0 * ckamount), 2)
                        FROM tbsada WITH (NOLOCK)
                        WHERE type = N'销售' AND outdate >= @d AND outdate < @d2 AND guno = @guno
                          AND paper = @paper AND pno = @pno AND dpm = @dpm AND mark = ''
                        """,
                        new { d, d2, guno = Ansi(g.guno, 10), paper = Ansi(g.paper, 15), pno = Ansi(g.pno, 12), dpm = Ansi(dpm, 10) },
                        transaction: tx, cancellationToken: ct));
                if (agg.pt == 0) continue;

                var spname = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT TOP (1) ISNULL(spname, '') FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper",
                    new { paper = Ansi(g.paper, 15) }, transaction: tx, cancellationToken: ct)) ?? "";

                // 3) 应收行（row5 L38-69）
                if (g.guno is "汇款" or "托收" or "挂帐")
                {
                    var prev = await conn.QueryFirstOrDefaultAsync<(decimal cb, decimal tick, decimal yjb)>(
                        new CommandDefinition(
                            "SELECT TOP (1) ISNULL(cb, 0), ISNULL(tick, 0), ISNULL(yjb, 0) FROM tbysyf WITH (UPDLOCK) WHERE gname = @gname ORDER BY datime",
                            new { gname = Ansi(g.gname, 40) }, transaction: tx, cancellationToken: ct));
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbysyf (gname, datime, cno, ys, pno, fkfs, type, cb, yjb, tick, xm, spname, dpm)
                        VALUES (@gname, @datime, @cno, @ys, @pno, @fkfs, N'销售', @cb, @yjb, @tick, @xm, @spname, @dpm)
                        """,
                        new
                        {
                            gname = Ansi(g.gname, 40), datime = d, cno = Ansi(g.paper, 15), ys = agg.pt,
                            pno = Ansi((g.pno + " " + g.per1).Trim(), 12), fkfs = Ansi(g.guno, 10),
                            cb = prev.cb, yjb = prev.yjb, tick = prev.tick, xm = Ansi(g.guno, 10),
                            spname = Ansi(spname, 40), dpm = Ansi(dpm, 10),
                        },
                        transaction: tx, cancellationToken: ct));
                    result.YsyfSaleRows++;
                    if (agg.ptsj - agg.pt != 0)
                    {
                        await conn.ExecuteAsync(new CommandDefinition(
                            """
                            INSERT INTO tbysyf (gname, datime, cno, ys, pno, fkfs, type, cb, yjb, tick, xm, spname, dpm)
                            VALUES (@gname, @datime, @cno, @ys, N'KH', @fkfs, N'销售', @cb, @yjb, @tick, @xm, @spname, @dpm)
                            """,
                            new
                            {
                                gname = Ansi(g.gname, 40), datime = d, cno = Ansi(g.paper, 15), ys = agg.ptsj - agg.pt,
                                fkfs = Ansi(g.guno, 10), cb = prev.cb, yjb = prev.yjb, tick = prev.tick,
                                xm = Ansi(g.guno, 10), spname = Ansi(spname, 40), dpm = Ansi(dpm, 10),
                            },
                            transaction: tx, cancellationToken: ct));
                        result.YsyfSaleRows++;
                    }
                }
            }

            // 4) type 翻转 + jzdate（row5 L81-86）
            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE tbsada SET type = N'历史' WHERE outdate >= @d AND outdate < @d2 AND type = N'销售' AND dpm = @dpm{tjWqr}",
                new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbsada SET jzdate = '1900-01-01'
                WHERE outdate >= @d AND outdate < @d2 AND type = N'历史'
                  AND (guno = N'汇款' OR guno = N'托收' OR guno = N'挂帐') AND dpm = @dpm
                  AND paper NOT IN (SELECT cno FROM tbysyf WHERE datime >= @d AND datime < @d2
                                    AND type = N'销售' AND fkfs <> '' AND skmoney <> 0)
                """,
                new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbsada SET jzdate = @d
                WHERE outdate >= @d AND outdate < @d2 AND type = N'历史'
                  AND (guno <> N'汇款' AND guno <> N'托收' AND guno <> N'挂帐') AND dpm = @dpm
                """,
                new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));

            // 5) 入库组（row5 L90-143）
            var inGroups = (await conn.QueryAsync<(string seano, string gyname, string fkfs, string pno, string per, string isord)>(
                new CommandDefinition(
                    $"""
                    SELECT DISTINCT RTRIM(seano), RTRIM(ISNULL(gyname, '')), RTRIM(ISNULL(fkfs, '')),
                           RTRIM(ISNULL(pno, '')), RTRIM(ISNULL(per, '')), RTRIM(ISNULL(isord, ''))
                    FROM tbistoed WITH (UPDLOCK)
                    WHERE indate >= @d AND indate < @d2 AND type = N'入库' AND dpm = @dpm{tjJj}
                    ORDER BY 1
                    """,
                    new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct))).AsList();

            foreach (var g in inGroups)
            {
                result.InStockGroups++;
                var fkfs = string.IsNullOrWhiteSpace(g.fkfs) ? "入库" : g.fkfs;
                var agg = await conn.QueryFirstOrDefaultAsync<(decimal pt, decimal freight)>(
                    new CommandDefinition(
                        $"""
                        SELECT {(prm.Wbtohs
                            ? "ROUND(SUM(iprj * jkamount), 2)"
                            : "ROUND(SUM(CASE WHEN ISNULL(freight, 0) = 0 THEN iprc ELSE freight END * jkamount), 2)")},
                               SUM((iprc - CASE WHEN ISNULL(freight, 0) = 0 THEN iprc ELSE freight END) * jkamount)
                        FROM tbistoed WITH (NOLOCK)
                        WHERE type = N'入库' AND indate >= @d AND indate < @d2 AND isord = @isord
                          AND seano = @seano AND gyname = @gyname AND dpm = @dpm AND per = @per
                          AND pno = @pno{tjJj} AND mark = ''
                        """,
                        new { d, d2, isord = Ansi(g.isord, 10), seano = Ansi(g.seano, 15), gyname = Ansi(g.gyname, 60),
                              dpm = Ansi(dpm, 10), per = Ansi(g.per, 10), pno = Ansi(g.pno, 12) },
                        transaction: tx, cancellationToken: ct));
                if (agg.pt == 0) continue;

                if (fkfs == "挂帐" && !string.IsNullOrWhiteSpace(g.gyname))
                {
                    var prev = await conn.QueryFirstOrDefaultAsync<(decimal cb, decimal tick, decimal yjb)>(
                        new CommandDefinition(
                            "SELECT TOP (1) ISNULL(cb, 0), ISNULL(tick, 0), ISNULL(yjb, 0) FROM tbysyf WITH (UPDLOCK) WHERE gname = @gname ORDER BY datime",
                            new { gname = Ansi(g.gyname, 40) }, transaction: tx, cancellationToken: ct));
                    var xm = agg.freight == 0 ? fkfs : fkfs + "有运费：" + agg.freight.ToString("F2").PadLeft(10);
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbysyf (gname, datime, cno, yf, pno, fkfs, type, cb, yjb, tick, xm, isord, dpm)
                        VALUES (@gname, @datime, @cno, @yf, @pno, @fkfs, N'入库', @cb, @yjb, @tick, @xm, @isord, @dpm)
                        """,
                        new
                        {
                            gname = Ansi(g.gyname, 40), datime = d, cno = Ansi(g.seano, 15), yf = agg.pt,
                            pno = Ansi((g.pno + " " + g.per).Trim(), 12), fkfs = Ansi(fkfs, 10),
                            cb = prev.cb, yjb = prev.yjb, tick = prev.tick, xm = Ansi(xm, 10),
                            isord = Ansi(g.isord, 10), dpm = Ansi(dpm, 10),
                        },
                        transaction: tx, cancellationToken: ct));
                    result.YsyfInRows++;
                }
            }

            // 6) 入库 type 翻转（row5 L144-145）
            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE tbistoed SET type = N'历史' WHERE indate >= @d AND indate < @d2 AND type = N'入库' AND dpm = @dpm{tjJj}",
                new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UnpostDailyStatAsync(DateTime date, string dpm, string per, CancellationToken ct = default)
    {
        var d = date.Date;
        var d2 = d.AddDays(1);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 月结保护（0_.row5 L10-15）
            var yjEnabled = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT ISNULL(CAST(valuel AS bit), 0) FROM tbsysset WITH (NOLOCK) WHERE RTRIM(name) = N'启用财务月结' AND type = 'system'",
                transaction: tx, cancellationToken: ct));
            if (yjEnabled)
            {
                var yjDate = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                    "SELECT MAX(yjdate) FROM tbyj WITH (NOLOCK)", transaction: tx, cancellationToken: ct));
                if (yjDate.HasValue && yjDate.Value.Date.AddDays(1) > d)
                    throw new AppException("MONTHLY_CLOSED", "已做月结，不能恢复统计", 409);
            }

            // type 回滚（0_.row5 L21-24：已收款行保护）
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbsada SET type = N'销售'
                WHERE outdate >= @d AND outdate < @d2 AND type = N'历史' AND dpm = @dpm
                  AND (guno = N'挂帐冲预收' OR paper NOT IN
                       (SELECT cno FROM tbysyf WHERE datime = @d AND type = N'销售' AND fkfs <> '' AND skmoney <> 0))
                """,
                new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbistoed SET type = N'入库'
                WHERE indate >= @d AND indate < @d2 AND type = N'历史' AND dpm = @dpm
                  AND seano NOT IN
                      (SELECT cno FROM tbysyf WHERE datime = @d AND type = N'入库' AND fkfs <> '' AND skmoney <> 0)
                """,
                new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));

            // 清理未收款应收行（0_.row5 L27-30：销售侧 datime>=d 无上界为旧码原样）
            await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM tbysyf
                WHERE datime >= @d AND type = N'销售' AND skmoney = 0 AND dpm = @dpm
                  AND cno IN (SELECT DISTINCT paper FROM tbsada WHERE type = N'销售' AND mark = ''
                              AND outdate >= @d AND outdate < @d2 AND dpm = @dpm)
                """,
                new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM tbysyf
                WHERE datime >= @d AND datime < @d2 AND type = N'入库' AND skmoney = 0 AND dpm = @dpm
                  AND cno IN (SELECT DISTINCT seano FROM tbistoed WHERE type = N'入库' AND mark = ''
                              AND indate >= @d AND indate < @d2 AND dpm = @dpm)
                """,
                new { d, d2, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DailyStatRow GetRow(Dictionary<DateTime, DailyStatRow> rows, DateTime date)
    {
        if (!rows.TryGetValue(date, out var r))
        {
            r = new DailyStatRow { Outdate = date };
            rows[date] = r;
        }
        return r;
    }

    private sealed record StatParams(bool IfrtjNoJj, bool IfrtjNoQr, bool Wbtohs);

    private static async Task<StatParams> LoadParamsAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<(string name, bool? valuel)>(
            new CommandDefinition(
                "SELECT RTRIM(name), valuel FROM tbsysset WITH (NOLOCK) WHERE type = 'system' " +
                "AND name IN (N'日统计不统计急件入库', N'不统计财务未确认', N'外币为含税价')",
                transaction: tx, cancellationToken: ct))).AsList();
        return new StatParams(
            rows.Any(x => x.name == "日统计不统计急件入库" && (x.valuel ?? false)),
            rows.Any(x => x.name == "不统计财务未确认" && (x.valuel ?? false)),
            rows.Any(x => x.name == "外币为含税价" && (x.valuel ?? false)));
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
