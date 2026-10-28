using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Report;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Report;

public interface IReportRepository
{
    /// <summary>日报表（D3，fr_dyrbb row3：当日销售/结账销售/入库/调拨四组）。</summary>
    Task<DailyReportResult> GetDailyReportAsync(DateTime date, string dpm, CancellationToken ct = default);

    /// <summary>月销售数统计（D4，fr_ytj row35：tbrtsamo 厂牌矩阵查询）。</summary>
    Task<List<MonthlySaleRow>> GetMonthlySalesAsync(string cp, string nnoContains, CancellationToken ct = default);

    /// <summary>月末处理（D4 写路径，fr_ytj _..row40：tbsada/tbxlsa 按月汇总写 tbrtsamo）。</summary>
    Task PostMonthlySalesAsync(int year, int month, string per, bool includeTransfer, bool onlyPositive, bool force, CancellationToken ct = default);

    /// <summary>进销存分类统计（D5，fr_jxc row3：按厂牌汇总 + 合计）。</summary>
    Task<List<JxcCategoryRow>> GetJxcCategoryAsync(DateTime bdate, CancellationToken ct = default);

    /// <summary>进销存统计·日汇总（D6，fr_jxctj dayhz：期初快照 + 六方向流水 + 差价/毛利派生）。</summary>
    Task<List<JxcDailyRow>> GetJxcDailyAsync(DateTime d0, DateTime d1, string cp, string dpm, string? depotno,
        int priceBase, CancellationToken ct = default);

    /// <summary>新增客户分析（D7，fr_tjfxb 盰.row26：每客户首单月计数 + 窗口内明细）。</summary>
    Task<(List<CustomerAnalysisMonthRow> Months, List<CustomerAnalysisDetailRow> Details)> GetNewCustomersAsync(
        DateTime d1, DateTime d2, CancellationToken ct = default);

    /// <summary>流失客户分析（D8，fr_tjfxb 竑.row27：交易间隔超阈值即流失，流失月=末单+阈值）。</summary>
    Task<(List<CustomerAnalysisMonthRow> Months, List<CustomerAnalysisDetailRow> Details)> GetLostCustomersAsync(
        DateTime d1, DateTime d2, int thresholdDays, CancellationToken ct = default);
}

public sealed class DailyReportResult
{
    public List<DailyReportSaleRow> Sales { get; set; } = [];
    public List<DailyReportSaleRow> Settled { get; set; } = [];
    public List<DailyReportInRow> InStock { get; set; } = [];
    public List<DailyReportSaleRow> Transfers { get; set; } = [];
}

/// <summary>
/// 统计报表仓储（D3/D4/D5，二期，全部只读查询 + D4 月末处理写路径）。
/// 旧实现出处：fr_dyrbb\__.row3 / fr_ytj\__.row35 + _..row40 / fr_jxc\__.row3。
/// GETJE 金额公式按 P_XSJEGS='1'（恒等）复刻，其他金额公式为已知偏差（与 D1 一致）。
/// </summary>
public sealed class ReportRepository(SqlConnectionFactory factory) : IReportRepository
{
    public async Task<DailyReportResult> GetDailyReportAsync(DateTime date, string dpm, CancellationToken ct = default)
    {
        var d = date.Date;
        var d2 = d.AddDays(1);
        await using var conn = await factory.OpenAsync(ct);
        var result = new DailyReportResult();

        const string saleCols = """
            RTRIM(paper) paper, RTRIM(ISNULL(gname, '')) gname, SUM(ckamount) ckamount,
            SUM(ckamount * iprc) iprc, SUM(ckamount * oprc * (100 - ISNULL(yhl, 0)) / 100.0) oprc,
            SUM(ckamount * oprct * (100 - ISNULL(yhl, 0)) / 100.0) oprct,
            RTRIM(ISNULL(guno, '')) guno, MAX(RTRIM(per)) per, outdate
            """;
        result.Sales = (await conn.QueryAsync<DailyReportSaleRow>(new CommandDefinition(
            $"""
            SELECT {saleCols} FROM tbsada WITH (NOLOCK)
            WHERE (type = N'销售' OR type = N'历史') AND outdate >= @d AND outdate < @d2 AND dpm = @dpm
            GROUP BY RTRIM(paper), ISNULL(gname, ''), ISNULL(guno, ''), outdate
            ORDER BY RTRIM(paper)
            """,
            new { d, d2, dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();

        // 页3：结账销售（jzdate 当日且 outdate≠jzdate）
        result.Settled = (await conn.QueryAsync<DailyReportSaleRow>(new CommandDefinition(
            $"""
            SELECT {saleCols} FROM tbsada WITH (NOLOCK)
            WHERE (type = N'销售' OR type = N'历史') AND jzdate >= @d AND jzdate < @d2
              AND dpm = @dpm AND outdate <> jzdate
            GROUP BY RTRIM(paper), ISNULL(gname, ''), ISNULL(guno, ''), outdate
            ORDER BY RTRIM(paper)
            """,
            new { d, d2, dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();

        // 页2：当日入库
        result.InStock = (await conn.QueryAsync<DailyReportInRow>(new CommandDefinition(
            """
            SELECT RTRIM(cno) cno, RTRIM(ISNULL(gyname, '')) gyname, COUNT(1) item, SUM(jkamount) jkamount,
                   SUM(jkamount * iprj) iprj, SUM(jkamount * iprc) iprc, MAX(RTRIM(depotno)) depotno,
                   RTRIM(fkfs) fkfs, RTRIM(per) per, indate, RTRIM(ISNULL(isord, '')) isord
            FROM tbistoed WITH (NOLOCK)
            WHERE (type = N'入库' OR type = N'历史') AND indate >= @d AND indate < @d2 AND dpm = @dpm
            GROUP BY RTRIM(cno), ISNULL(gyname, ''), RTRIM(fkfs), RTRIM(per), indate, ISNULL(isord, '')
            ORDER BY RTRIM(cno)
            """,
            new { d, d2, dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();

        // 页4：当日调拨
        result.Transfers = (await conn.QueryAsync<DailyReportSaleRow>(new CommandDefinition(
            $"""
            SELECT {saleCols} FROM tbsada WITH (NOLOCK)
            WHERE type = N'调拨' AND outdate >= @d AND outdate < @d2 AND dpm = @dpm
            GROUP BY RTRIM(paper), ISNULL(gname, ''), ISNULL(guno, ''), outdate
            ORDER BY RTRIM(paper)
            """,
            new { d, d2, dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();
        return result;
    }

    public async Task<List<MonthlySaleRow>> GetMonthlySalesAsync(string cp, string nnoContains, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var where = "cp = @cp";
        if (!string.IsNullOrWhiteSpace(nnoContains))
            where += " AND (RTRIM(nno) LIKE @kw + '%' OR RTRIM(na1) LIKE '%' + @kw + '%')";
        var rows = await conn.QueryAsync<MonthlySaleRow>(new CommandDefinition(
            $"SELECT id, RTRIM(dpm) dpm, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(unit) unit, " +
            "oamou1, oamou2, oamou3, oamou4, oamou5, oamou6, oamou7, oamou8, oamou9, oamou10, oamou11, oamou12 " +
            $"FROM tbrtsamo WITH (NOLOCK) WHERE {where} ORDER BY RTRIM(nno)",
            new { cp = Ansi(cp, 10), kw = Ansi(nnoContains?.Trim() ?? "", 18) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task PostMonthlySalesAsync(int year, int month, string per, bool includeTransfer, bool onlyPositive, bool force, CancellationToken ct = default)
    {
        if (month is < 1 or > 12) throw new AppException("MONTH_INVALID", "月份须在 1~12", 422);
        var d1 = new DateTime(year, month, 1);
        var d2 = d1.AddMonths(1);
        var col = $"oamou{month}";
        var tj = includeTransfer ? "type IN (N'销售', N'历史', N'调拨')" : "type IN (N'销售', N'历史')";
        if (onlyPositive) tj += " AND ckamount > 0";

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 覆盖确认（row40 L10-14：已有该月 >0 数据须确认）
            if (!force)
            {
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    $"SELECT TOP (1) 1 FROM tbrtsamo WITH (NOLOCK) WHERE {col} > 0",
                    transaction: tx, cancellationToken: ct));
                if (exists == 1)
                    throw new AppException("MONTH_EXISTS", "已有此月统计，如需覆盖请确认覆盖选项", 409);
            }

            // 清空该月（row40 L31 全表）
            await conn.ExecuteAsync(new CommandDefinition($"UPDATE tbrtsamo SET {col} = 0",
                transaction: tx, cancellationToken: ct));

            // 销售/历史（+可选调拨）汇总：既有行 UPDATE、缺行 INSERT（row40 L33-34）
            await conn.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE tbrtsamo SET {col} = tbrtsamo.{col} + b.ckamount
                FROM tbrtsamo
                INNER JOIN (SELECT dpm, cp, nno, SUM(ckamount) ckamount FROM tbsada WITH (NOLOCK)
                            WHERE {tj} AND outdate >= @d1 AND outdate < @d2 GROUP BY dpm, cp, nno) b
                    ON tbrtsamo.dpm = b.dpm AND tbrtsamo.cp = b.cp AND tbrtsamo.nno = b.nno
                """,
                new { d1, d2 }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO tbrtsamo (dpm, cp, nno, na1, ty, unit, {col})
                SELECT dpm, cp, nno, MAX(na1), MAX(ty), MAX(unit), SUM(ckamount)
                FROM tbsada WITH (NOLOCK)
                WHERE {tj} AND outdate >= @d1 AND outdate < @d2
                  AND RTRIM(dpm) + RTRIM(cp) + RTRIM(nno) NOT IN (SELECT RTRIM(dpm) + RTRIM(cp) + RTRIM(nno) FROM tbrtsamo WITH (NOLOCK))
                GROUP BY dpm, cp, nno
                """,
                new { d1, d2 }, transaction: tx, cancellationToken: ct));

            // 维修领料（tbxlsa，触发器共享语义照抄，row40 L39-40）
            await conn.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE tbrtsamo SET {col} = tbrtsamo.{col} + b.ckamount
                FROM tbrtsamo
                INNER JOIN (SELECT dpm, cp, nno, SUM(ckamount) ckamount FROM tbxlsa WITH (NOLOCK)
                            WHERE outdate >= @d1 AND outdate < @d2 GROUP BY dpm, cp, nno) b
                    ON tbrtsamo.dpm = b.dpm AND tbrtsamo.cp = b.cp AND tbrtsamo.nno = b.nno
                """,
                new { d1, d2 }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO tbrtsamo (dpm, cp, nno, na1, ty, unit, {col})
                SELECT dpm, cp, nno, MAX(na1), MAX(ty), MAX(unit), SUM(ckamount)
                FROM tbxlsa WITH (NOLOCK)
                WHERE outdate >= @d1 AND outdate < @d2
                  AND RTRIM(dpm) + RTRIM(cp) + RTRIM(nno) NOT IN (SELECT RTRIM(dpm) + RTRIM(cp) + RTRIM(nno) FROM tbrtsamo WITH (NOLOCK))
                GROUP BY dpm, cp, nno
                """,
                new { d1, d2 }, transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<List<JxcCategoryRow>> GetJxcCategoryAsync(DateTime bdate, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<JxcCategoryRow>(new CommandDefinition(
            """
            WITH cps AS (SELECT RTRIM(name) cp FROM tbsysset WITH (NOLOCK) WHERE type = 'cp'),
            xs AS (SELECT RTRIM(cp) cp, CAST(SUM(ckamount) AS int) xs_amou, SUM(ckamount * iprc) iprc,
                          SUM(ckamount * oprct * (100 - ISNULL(yhl, 0)) / 100.0) xs_jine
                   FROM tbsada WITH (NOLOCK)
                   WHERE outdate >= @bdate AND (type = N'销售' OR type = N'历史') GROUP BY RTRIM(cp)),
            th AS (SELECT RTRIM(cp) cp, CAST(SUM(-ckamount) AS int) th_amou,
                          SUM(-ckamount * oprct * (100 - ISNULL(yhl, 0)) / 100.0) th_jine
                   FROM tbsada WITH (NOLOCK)
                   WHERE outdate >= @bdate AND ckamount < 0 GROUP BY RTRIM(cp)),
            jk AS (SELECT RTRIM(cp) cp, CAST(SUM(jkamount) AS int) in_amou, SUM(jkamount * iprc) in_jine
                   FROM tbistoed WITH (NOLOCK)
                   WHERE indate >= @bdate GROUP BY RTRIM(cp)),
            kc AS (SELECT RTRIM(cp) cp, CAST(SUM(kcamount) AS int) kc_amou, SUM(kcamount * iprc) kc_jine
                   FROM tbisto WITH (NOLOCK) GROUP BY RTRIM(cp))
            SELECT c.cp, ISNULL(xs.xs_amou, 0) xs_amou, ISNULL(xs.iprc, 0) iprc, ISNULL(xs.xs_jine, 0) xs_jine,
                   ISNULL(th.th_amou, 0) th_amou, ISNULL(th.th_jine, 0) th_jine,
                   ISNULL(jk.in_amou, 0) in_amou, ISNULL(jk.in_jine, 0) in_jine,
                   ISNULL(kc.kc_amou, 0) kc_amou, ISNULL(kc.kc_jine, 0) kc_jine
            FROM cps c
            LEFT JOIN xs ON xs.cp = c.cp
            LEFT JOIN th ON th.cp = c.cp
            LEFT JOIN jk ON jk.cp = c.cp
            LEFT JOIN kc ON kc.cp = c.cp
            """,
            new { bdate = bdate.Date }, cancellationToken: ct))).AsList();

        foreach (var r in rows)
        {
            r.Ml = r.XsJine - r.Iprc;
            r.Mll = Math.Abs(r.Iprc) > 0.1m ? (r.Ml / r.Iprc * 100).ToString("F2") + "%" : "";
        }

        // 合计行（row3 L48-49）
        var total = new JxcCategoryRow
        {
            Cp = "合计",
            XsAmou = rows.Sum(r => r.XsAmou),
            XsJine = rows.Sum(r => r.XsJine),
            Iprc = rows.Sum(r => r.Iprc),
            ThAmou = rows.Sum(r => r.ThAmou),
            ThJine = rows.Sum(r => r.ThJine),
            InAmou = rows.Sum(r => r.InAmou),
            InJine = rows.Sum(r => r.InJine),
            KcAmou = rows.Sum(r => r.KcAmou),
            KcJine = rows.Sum(r => r.KcJine),
        };
        total.Ml = total.XsJine - total.Iprc;
        total.Mll = Math.Abs(total.Iprc) > 0.1m ? (total.Ml / total.Iprc * 100).ToString("F2") + "%" : "";
        rows.Add(total);
        return rows;
    }

    /// <summary>
    /// D6 日汇总（fr_jxctj __.row2 dayhz 原样复刻）：
    /// 期初=tbistosav 按 back_date 快照（BETWEEN d0 AND d2 含 d2，展示时剔除 back_date≥d2 行，
    /// 但其快照值先作为前一日期末参与）；期末=次日快照，末行 back_date=今天 时期末取 tbisto 实时合计
    /// （原 L58 仅过滤 dpm+仓库，无 cp 条件，照抄）；纠正金额=Σ价不乘数量（L40 原样）；
    /// 归库=tbsaxg 改单差集（L42 原样）；差价=期末-(期初+进库-销售-调拨-领料+纠正-归库)；
    /// 出库合计=销售+调拨+领料；出库毛利=出库售价-出库金额（沙河丰田 1.17 分歧不适用）。
    /// </summary>
    public async Task<List<JxcDailyRow>> GetJxcDailyAsync(DateTime d0, DateTime d1, string cp, string dpm, string? depotno,
        int priceBase, CancellationToken ct = default)
    {
        var d2 = d1.Date.AddDays(1);
        var priceCol = priceBase == 2 ? "iprj" : "iprc";
        var depFilter = string.IsNullOrWhiteSpace(depotno) ? "" : " AND depotno = @depotno";
        await using var conn = await factory.OpenAsync(ct);

        var sav = (await conn.QueryAsync<(DateTime back_date, decimal qckc, decimal qckcj)>(new CommandDefinition(
            $"""
            SELECT back_date, SUM(kcamount) qckc, SUM(kcamount * {priceCol}) qckcj
            FROM tbistosav WITH (NOLOCK)
            WHERE back_date BETWEEN @d0 AND @d2 AND cp = @cp AND dpm = @dpm{depFilter}
            GROUP BY back_date ORDER BY back_date
            """,
            ParamObj(dpm, cp, d0, d2, depotno), cancellationToken: ct))).AsList();

        var lsck = (await conn.QueryAsync<(DateTime dt, decimal xs, decimal xsj, decimal ckoprc)>(new CommandDefinition(
            $"""
            SELECT CAST(outdate AS date) dt, SUM(ckamount) xs, SUM(ckamount * {priceCol}) xsj, SUM(ckamount * oprct) ckoprc
            FROM tbsada WITH (NOLOCK)
            WHERE outdate >= @d0 AND outdate < @d2 AND type IN (N'销售', N'历史', N'预售') AND cp = @cp AND dpm = @dpm{depFilter}
            GROUP BY CAST(outdate AS date)
            """,
            ParamObj(dpm, cp, d0, d2, depotno), cancellationToken: ct))).AsList();

        var lsdb = (await conn.QueryAsync<(DateTime dt, decimal xs, decimal xsj, decimal ckoprc)>(new CommandDefinition(
            $"""
            SELECT CAST(outdate AS date) dt, SUM(ckamount) xs, SUM(ckamount * {priceCol}) xsj, SUM(ckamount * oprct) ckoprc
            FROM tbsada WITH (NOLOCK)
            WHERE outdate >= @d0 AND outdate < @d2 AND type = N'调拨' AND cp = @cp AND dpm = @dpm{depFilter}
            GROUP BY CAST(outdate AS date)
            """,
            ParamObj(dpm, cp, d0, d2, depotno), cancellationToken: ct))).AsList();

        var wxck = (await conn.QueryAsync<(DateTime dt, decimal xs, decimal xsj, decimal ckoprc)>(new CommandDefinition(
            $"""
            SELECT CAST(outdate AS date) dt, SUM(ckamount) xs, SUM(ckamount * {priceCol}) xsj, SUM(ckamount * oprct) ckoprc
            FROM tbxlsa WITH (NOLOCK)
            WHERE outdate >= @d0 AND outdate < @d2 AND type <> N'报价' AND cp = @cp AND dpm = @dpm{depFilter}
            GROUP BY CAST(outdate AS date)
            """,
            ParamObj(dpm, cp, d0, d2, depotno), cancellationToken: ct))).AsList();

        var jkls = (await conn.QueryAsync<(DateTime dt, decimal jk, decimal jkj)>(new CommandDefinition(
            $"""
            SELECT CAST(indate AS date) dt, SUM(jkamount) jk, SUM(jkamount * {priceCol}) jkj
            FROM tbistoed WITH (NOLOCK)
            WHERE indate >= @d0 AND indate < @d2 AND cp = @cp AND dpm = @dpm{depFilter}
            GROUP BY CAST(indate AS date)
            """,
            ParamObj(dpm, cp, d0, d2, depotno), cancellationToken: ct))).AsList();

        var kcxg = (await conn.QueryAsync<(DateTime dt, decimal xg, decimal xgj)>(new CommandDefinition(
            $"""
            SELECT CAST(xgdate AS date) dt, SUM(xgamount) xg, SUM({priceCol}) xgj
            FROM tbistoch WITH (NOLOCK)
            WHERE xgdate >= @d0 AND xgdate < @d2 AND cp = @cp AND dpm = @dpm{depFilter}
            GROUP BY CAST(xgdate AS date)
            """,
            ParamObj(dpm, cp, d0, d2, depotno), cancellationToken: ct))).AsList();

        var ckxg = (await conn.QueryAsync<(DateTime dt, decimal gk, decimal gkj)>(new CommandDefinition(
            $"""
            SELECT CAST(a.editdate AS date) dt,
                   SUM(ckamount * CASE WHEN outdate >= @d0 AND outdate < @d2 AND editdate > @d1 THEN -1 ELSE 1 END) gk,
                   SUM(ckamount * CASE WHEN outdate >= @d0 AND outdate < @d2 AND editdate > @d1 THEN -1 ELSE 1 END * b.{priceCol}) gkj
            FROM tbsaxg a WITH (NOLOCK)
            INNER JOIN (SELECT dpm, cp, nno, {priceCol} FROM tbisto WITH (NOLOCK)) b
                ON a.dpm = b.dpm AND a.cp = b.cp AND a.nno = b.nno
            WHERE ((editdate >= @d0 AND editdate < @d2 AND outdate < @d0) OR (outdate >= @d0 AND outdate < @d2 AND editdate > @d1))
              AND type <> N'报价' AND a.cp = @cp AND a.dpm = @dpm{depFilter.Replace("depotno", "a.depotno")}
            GROUP BY CAST(a.editdate AS date)
            """,
            ParamObj(dpm, cp, d0, d2, depotno, d1), cancellationToken: ct))).AsList();

        // 组装逐日行（row44-64：快照链 + 期末=次日快照，末行 back_date=今天 → tbisto 实时）
        var rows = new List<JxcDailyRow>();
        foreach (var s in sav)
            rows.Add(new JxcDailyRow { Date = s.back_date.ToString("yyyy-MM-dd"), Qckc = s.qckc, Qckcj = s.qckcj });
        for (var i = 1; i < sav.Count; i++)
        {
            rows[i - 1].Qmkc = sav[i].qckc;
            rows[i - 1].Qmkcj = sav[i].qckcj;
        }
        if (sav.Count > 0 && sav[^1].back_date.Date == DateTime.Today)
        {
            var live = await conn.QueryFirstOrDefaultAsync<(decimal qckc, decimal qckcj)>(new CommandDefinition(
                $"SELECT ISNULL(SUM(kcamount), 0), ISNULL(SUM(kcamount * {priceCol}), 0) FROM tbisto WITH (NOLOCK) " +
                "WHERE dpm = @dpm" + depFilter,
                ParamObj(dpm, cp, d0, d2, depotno), cancellationToken: ct));
            rows[^1].Qmkc = live.qckc;
            rows[^1].Qmkcj = live.qckcj;
        }
        rows = rows.Where(r => DateTime.Parse(r.Date) < d2).ToList();   // L79-81：剔除 back_date≥d2 行

        var xsDict = lsck.ToDictionary(x => x.dt);
        var dbDict = lsdb.ToDictionary(x => x.dt);
        var wxDict = wxck.ToDictionary(x => x.dt);
        var jkDict = jkls.ToDictionary(x => x.dt);
        var xgDict = kcxg.ToDictionary(x => x.dt);
        var gkDict = ckxg.ToDictionary(x => x.dt);
        foreach (var r in rows)
        {
            var dt = DateTime.Parse(r.Date);
            if (xsDict.TryGetValue(dt, out var v)) { r.Xs = v.xs; r.Xsj = v.xsj; r.Ckoprc += v.ckoprc; }
            if (dbDict.TryGetValue(dt, out v)) { r.Db = v.xs; r.Dbj = v.xsj; r.Ckoprc += v.ckoprc; }
            if (wxDict.TryGetValue(dt, out v)) { r.Wx = v.xs; r.Wxj = v.xsj; r.Ckoprc += v.ckoprc; }
            if (jkDict.TryGetValue(dt, out var j)) { r.Jk = j.jk; r.Jkj = j.jkj; }
            if (xgDict.TryGetValue(dt, out var g)) { r.Xg = g.xg; r.Xgj = g.xgj; }
            if (gkDict.TryGetValue(dt, out var k)) { r.Gk = k.gk; r.Gkj = k.gkj; }
            r.Cj = r.Qmkc - (r.Qckc + r.Jk - r.Xs - r.Db - r.Wx + r.Xg - r.Gk);
            r.Cjj = r.Qmkcj - (r.Qckcj + r.Jkj - r.Xsj - r.Dbj - r.Wxj + r.Xgj - r.Gkj);
            r.Ck = r.Xs + r.Db + r.Wx;
            r.Ckj = r.Xsj + r.Dbj + r.Wxj;
            r.Ckml = r.Ckoprc - r.Ckj;
        }

        // 总计行（L82-83）
        var total = new JxcDailyRow
        {
            Date = "总计:",
            Jk = rows.Sum(x => x.Jk), Jkj = rows.Sum(x => x.Jkj),
            Xs = rows.Sum(x => x.Xs), Xsj = rows.Sum(x => x.Xsj),
            Db = rows.Sum(x => x.Db), Dbj = rows.Sum(x => x.Dbj),
            Wx = rows.Sum(x => x.Wx), Wxj = rows.Sum(x => x.Wxj),
            Xg = rows.Sum(x => x.Xg), Xgj = rows.Sum(x => x.Xgj),
            Gk = rows.Sum(x => x.Gk), Gkj = rows.Sum(x => x.Gkj),
            Cj = rows.Sum(x => x.Cj), Cjj = rows.Sum(x => x.Cjj),
            Ck = rows.Sum(x => x.Ck), Ckj = rows.Sum(x => x.Ckj),
            Ckml = rows.Sum(x => x.Ckml), Ckoprc = rows.Sum(x => x.Ckoprc),
        };
        rows.Add(total);
        return rows;
    }

    /// <summary>
    /// D7 新增客户（盰.row26 原样）：tbsada 每客户 MIN(outdate)=首单日期 + 累计消费
    /// Σckamount*oprct*(100-yhl)/100；首单月计数，首单∈[d1,d2] 出明细（tbgu 联系人）。
    /// 跨部门转移分支（P_FGS×CHKDPM）不适用单部门部署，未实现。
    /// </summary>
    public async Task<(List<CustomerAnalysisMonthRow> Months, List<CustomerAnalysisDetailRow> Details)> GetNewCustomersAsync(
        DateTime d1, DateTime d2, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var firsts = (await conn.QueryAsync<(string gu, DateTime date, decimal oprc)>(new CommandDefinition(
            """
            SELECT RTRIM(gname), MIN(outdate), SUM(ckamount * oprct * (100 - ISNULL(yhl, 0)) / 100.0)
            FROM tbsada WITH (NOLOCK) WHERE RTRIM(gname) <> '' GROUP BY RTRIM(gname) ORDER BY 1
            """,
            cancellationToken: ct))).AsList()
            .Where(x => x.date <= d2).ToList();

        var guInfo = (await conn.QueryAsync<(string gname, string lxr, string lxrtel, string ch, string adr)>(
            new CommandDefinition(
                "SELECT RTRIM(gname), RTRIM(ISNULL(linkman, '')), RTRIM(ISNULL(tel, '')), '', RTRIM(ISNULL(adr, '')) " +
                "FROM tbgu WITH (NOLOCK) WHERE RTRIM(gname) <> '' ORDER BY RTRIM(gname)",
                cancellationToken: ct))).AsList()
            .GroupBy(x => x.gname).ToDictionary(g => g.Key, g => g.First());

        var months = new Dictionary<string, int>();
        var details = new List<CustomerAnalysisDetailRow>();
        foreach (var f in firsts)
        {
            var ym = $"{f.date.Year}年{f.date.Month}月";
            months[ym] = months.GetValueOrDefault(ym) + 1;
            if (f.date >= d1 && f.date <= d2)
            {
                guInfo.TryGetValue(f.gu, out var g);
                details.Add(new CustomerAnalysisDetailRow
                {
                    Months = ym, Gname = f.gu, Lxr = g.lxr, Lxrtel = g.lxrtel, Ch = g.ch,
                    Lxradr = g.adr, Jcdate = f.date, Oprc = f.oprc,
                });
            }
        }
        return (BuildMonthRows(months, d1, d2), details);
    }

    /// <summary>
    /// D8 流失客户（竑.row27 原样，单部门路径）：每客户(含 dpm)的 MAX(outdate) 交易序列按客户+日期排序，
    /// 相邻交易间隔&gt;阈值 → 间隔溢出日（末单+阈值）计一次流失（可复购再流失）；
    /// 客户切换时 上一客户末单+阈值&lt;今天 → 已流失。明细窗口 [d1,d2]。
    /// </summary>
    public async Task<(List<CustomerAnalysisMonthRow> Months, List<CustomerAnalysisDetailRow> Details)> GetLostCustomersAsync(
        DateTime d1, DateTime d2, int thresholdDays, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var seq = (await conn.QueryAsync<(string gu, DateTime date)>(new CommandDefinition(
            """
            SELECT DISTINCT RTRIM(gu), CAST(date AS date) FROM (
                SELECT gname gu, MAX(outdate) date FROM tbsada WITH (NOLOCK)
                WHERE RTRIM(gname) <> '' GROUP BY gname, dpm) s
            WHERE date <= @d2 ORDER BY gu, date
            """,
            new { d2 }, cancellationToken: ct))).AsList();

        var guInfo = (await conn.QueryAsync<(string gname, string lxr, string lxrtel, string ch, string adr)>(
            new CommandDefinition(
                "SELECT RTRIM(gname), RTRIM(ISNULL(linkman, '')), RTRIM(ISNULL(tel, '')), '', RTRIM(ISNULL(adr, '')) " +
                "FROM tbgu WITH (NOLOCK) WHERE RTRIM(gname) <> '' ORDER BY RTRIM(gname)",
                cancellationToken: ct))).AsList()
            .GroupBy(x => x.gname).ToDictionary(g => g.Key, g => g.First());

        var months = new Dictionary<string, int>();
        var details = new List<CustomerAnalysisDetailRow>();
        var today = DateTime.Today;
        string? prevGu = null;
        DateTime prevDate = default;
        void CountLost(string gu, DateTime lastDate)
        {
            var lostDate = lastDate.AddDays(thresholdDays);
            var ym = $"{lostDate.Year}年{lostDate.Month}月";
            months[ym] = months.GetValueOrDefault(ym) + 1;
            if (lostDate >= d1 && lostDate <= d2)
            {
                guInfo.TryGetValue(gu, out var g);
                details.Add(new CustomerAnalysisDetailRow
                {
                    Months = ym, Gname = gu, Lxr = g.lxr, Lxrtel = g.lxrtel, Ch = g.ch,
                    Lxradr = g.adr, Jcdate = lastDate,
                });
            }
        }
        foreach (var (gu, date) in seq)
        {
            if (prevGu == null) { prevGu = gu; prevDate = date; continue; }
            if (gu == prevGu)
            {
                if ((date - prevDate).TotalDays > thresholdDays)
                    CountLost(prevGu, prevDate);       // 同客户间隔超阈值：在末单+阈值日流失（后续复购再计）
            }
            else
            {
                if (prevDate.AddDays(thresholdDays) < today)
                    CountLost(prevGu, prevDate);       // 客户切换：上一客户末单+阈值 < 今天 → 已流失
                prevGu = gu;
            }
            prevDate = date;
        }
        // 末客户（旧码 SCAN 结束后无显式收尾——末客户流失由其后无交易的自然事实在下轮查询体现，不在此补计）
        return (BuildMonthRows(months, d1, d2), details);
    }

    /// <summary>月度序列行（窗口内逐月 + 总计行，旧行列「yyyy年M月」格式）。</summary>
    private static List<CustomerAnalysisMonthRow> BuildMonthRows(Dictionary<string, int> months, DateTime d1, DateTime d2)
    {
        var rows = new List<CustomerAnalysisMonthRow>();
        for (var m = new DateTime(d1.Year, d1.Month, 1); m <= d2; m = m.AddMonths(1))
        {
            var ym = $"{m.Year}年{m.Month}月";
            rows.Add(new CustomerAnalysisMonthRow { Ym = ym, Count = months.GetValueOrDefault(ym) });
        }
        rows.Add(new CustomerAnalysisMonthRow { Ym = "总计", Count = months.Sum(x => x.Value) });
        return rows;
    }

    private static object ParamObj(string dpm, string cp, DateTime d0, DateTime d2, string? depotno, DateTime d1 = default) =>
        new ParamBag(dpm, cp, d0, d2, depotno ?? "", d1);

    private sealed record ParamBag(string Dpm, string Cp, DateTime D0, DateTime D2, string Depotno, DateTime D1)
    {
        public DateTime d0 => D0;
        public DateTime d2 => D2;
        public DateTime d1 => D1;
        public string dpm => Dpm;
        public string cp => Cp;
        public string depotno => Depotno;
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
