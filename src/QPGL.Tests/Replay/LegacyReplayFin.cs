using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace QPGL.Tests.Replay;

/// <summary>
/// 账务 I 组（凭证核心闭环）旧侧回放 —— 契约：开发文档/契约摘录/账务管理I组凭证闭环契约.md。
/// 科目级次位宽 3,3,3,2,2,2（syscontrol dpm=A 实测）；finaleger.recoid 为 char(1)。
/// </summary>
public static partial class LegacyReplay
{
    private static readonly int[] FinSubLens = [3, 3, 3, 2, 2, 2];

    /// <summary>种子科目（finsub 现有列；bdesum/bcresum 期初链验证用）。</summary>
    public static void FinSeedSub(SqlConnection conn, string subcode, string subname, decimal bd, decimal bc, string dpm)
        => conn.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM finsub WHERE rtrim(subcode) = @sub)
                INSERT INTO finsub (subcode, subname, fdc, type, detacode, bdesum, bcresum, cdesum, ccresum, ydesum, ycresum, edesum, ecresum, mpalsum, ypalsum, dpm)
                VALUES (@sub, @name, 'D', '', 1, @bd, @bc, 0, 0, 0, 0, 0, 0, 0, 0, @dpm)
            """,
            new { sub = Ansi(subcode, 18), name = Ansi(subname, 40), bd, bc, dpm = Ansi(dpm, 10) });

    /// <summary>凭证保存（fr_finvouch Save row2 L252-286 原样；Add 推进 finvid）。</summary>
    public static void FinVouchSave(SqlConnection conn, int vouchid, int vcode, DateTime date, string vouword,
        bool isAdd, IReadOnlyList<(string subcode, string subname, decimal debit, decimal credit, decimal amount,
            string unit, string dept, string item, string empl, string client)> lines, string dpm, string per)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var period = date.ToString("yyyyMM");
        using var tx = conn.BeginTransaction();
        try
        {
            if (isAdd)
                conn.Execute("UPDATE finvid SET vid = vid + 1 WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, tx);
            else
                conn.Execute("DELETE FROM finvouch WHERE vouchid = @vouchid", new { vouchid }, tx);
            var recoid = 0;
            foreach (var l in lines)
            {
                if (l.debit == 0 && l.credit == 0) continue;
                if (string.IsNullOrWhiteSpace(l.subcode)) continue;
                recoid++;
                conn.Execute(
                    """
                    INSERT INTO finvouch (vouchid, vcode, date, period, recoid, note, subcode, subname, type, fcash,
                                          debit, credit, vouword, abill, amount, unit, dept, item, empl, client,
                                          audit, post, maker, depotno, dpm)
                    VALUES (@vouchid, @vcode, @date, @period, @recoid, N'', @subcode, @subname, N'', 0,
                            @debit, @credit, @vouword, 0, @amount, @unit, @dept, @item, @empl, @client,
                            0, 0, @maker, N'', @dpm)
                    """,
                    new
                    {
                        vouchid, vcode, date, period = Ansi(period, 6), recoid,
                        subcode = Ansi(l.subcode, 18), subname = Ansi(l.subname, 40),
                        debit = l.debit, credit = l.credit, vouword = Ansi(vouword, 4),
                        amount = (double)l.amount, unit = Ansi(l.unit, 10),
                        dept = Ansi(l.dept, 20), item = Ansi(l.item, 20), empl = Ansi(l.empl, 10),
                        client = Ansi(l.client, 40), maker = Ansi(per, 10), dpm = Ansi(dpm, 10),
                    }, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>整期审核（fr_finaudit row3）。</summary>
    public static void FinAudit(SqlConnection conn, string period, string per)
        => conn.Execute("UPDATE finvouch SET audit = 1, auditer = @per WHERE period = @period AND ISNULL(audit,0) = 0",
            new { per = Ansi(per, 10), period = Ansi(period, 6) });

    private static int FinSubWidth(int len)
    {
        var acc = 0;
        foreach (var l in FinSubLens)
        {
            if (len <= acc) return FinSubLens[Math.Max(0, Array.IndexOf(FinSubLens, l) - 1)];
            if (len <= acc + l) return l;
            acc += l;
        }
        return FinSubLens[^1];
    }

    private static string FinPrevPeriod(string period)
    {
        var y = int.Parse(period[..4]);
        var m = int.Parse(period[4..]);
        return m == 1 ? $"{y - 1}12" : $"{y}{m - 1:D2}";
    }

    private static void FinApplySub(SqlConnection conn, SqlTransaction tx, string fcode,
        (int id, string subcode, decimal debit, decimal credit, decimal amount, string unit, string opertype,
            string dept, string item, string empl, string client, string vouword, string subname) l,
        string period, string dpm, bool un)
    {
        var sign = un ? -1 : 1;
        var sub = conn.QueryFirstOrDefault<(decimal bd, decimal bc, decimal cd, decimal cc, decimal yd, decimal yc,
            decimal mp, decimal yp, string fdc, string subname, int detacode)>(
            "SELECT ISNULL(bdesum,0), ISNULL(bcresum,0), ISNULL(cdesum,0), ISNULL(ccresum,0), ISNULL(ydesum,0), ISNULL(ycresum,0), ISNULL(mpalsum,0), ISNULL(ypalsum,0), RTRIM(ISNULL(fdc,'')), RTRIM(subname), ISNULL(detacode,0) FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @fcode",
            new { fcode = Ansi(fcode, 18) }, tx);
        if (sub.subname == null) return;   // 上级科目不存在 → 旧系统 SEEK 无行跳过
        var cdesum = sub.cd + sign * l.debit;
        var ccresum = sub.cc + sign * l.credit;
        var ydesum = sub.yd + sign * l.debit;
        var ycresum = sub.yc + sign * l.credit;
        var ede0 = sub.bd - sub.bc + cdesum - ccresum;
        var edesum = ede0 >= 0 ? ede0 : 0;
        var ecresum = ede0 < 0 ? -ede0 : 0;
        var mpal = sub.mp;
        var ypal = sub.yp;
        if (l.opertype != "CAF" && fcode.StartsWith('5'))
        {
            var d = sub.fdc == "C" ? l.credit - l.debit : l.debit - l.credit;
            mpal += sign * d;
            ypal += sign * d;
        }
        conn.Execute(
            "UPDATE finsub SET cdesum = @cd, ccresum = @cc, ydesum = @yd, ycresum = @yc, edesum = @ed, ecresum = @ec, mpalsum = @mp, ypalsum = @yp WHERE rtrim(subcode) = @fcode",
            new { cd = cdesum, cc = ccresum, yd = ydesum, yc = ycresum, ed = edesum, ec = ecresum, mp = mpal, yp = ypal, fcode = Ansi(fcode, 18) }, tx);

        var nbal = sub.bd - sub.bc;
        var finCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM finaleger WITH (NOLOCK) WHERE rtrim(subcode) = @fcode AND period = @period",
            new { fcode = Ansi(fcode, 18), period = Ansi(period, 6) }, tx);
        if (finCount == 0)
        {
            conn.Execute(
                """
                INSERT INTO finaleger (subcode, subname, period, recoid, csummary, desum, cresum, dcdir, balance)
                VALUES (@fcode, @fname, @period, '1', '期初余额', 0, 0, @nbalDir, @nbalAbs),
                       (@fcode, @fname, @period, '2', '本期合计', @d2, @c2, @dir2, @bal2),
                       (@fcode, @fname, @period, '3', '本年累计', @d3, @c3, @dir2, @bal2)
                """,
                new
                {
                    fcode = Ansi(fcode, 18), fname = Ansi(sub.subname, 40), period = Ansi(period, 6),
                    d2 = (double)cdesum, c2 = (double)ccresum,
                    dir2 = Ansi(ede0 > 0 ? "借" : ede0 < 0 ? "贷" : "平", 2), bal2 = (double)Math.Abs(ede0),
                    d3 = (double)ydesum, c3 = (double)ycresum,
                    nbalDir = Ansi(nbal > 0 ? "借" : nbal < 0 ? "贷" : "平", 2), nbalAbs = (double)Math.Abs(nbal),
                }, tx);
        }
        else
        {
            conn.Execute(
                "UPDATE finaleger SET desum = @d2, cresum = @c2, dcdir = @dir2, balance = @bal2 WHERE rtrim(subcode) = @fcode AND period = @period AND recoid = '2'",
                new
                {
                    d2 = (double)cdesum, c2 = (double)ccresum,
                    dir2 = Ansi(ede0 > 0 ? "借" : ede0 < 0 ? "贷" : "平", 2), bal2 = (double)Math.Abs(ede0),
                    fcode = Ansi(fcode, 18), period = Ansi(period, 6),
                }, tx);
            conn.Execute(
                "UPDATE finaleger SET desum = @d3, cresum = @c3 WHERE rtrim(subcode) = @fcode AND period = @period AND recoid = '3'",
                new { d3 = (double)ydesum, c3 = (double)ycresum, fcode = Ansi(fcode, 18), period = Ansi(period, 6) }, tx);
        }

        // finamsum 数量金额账（unit 非空或 amount≠0；post L105-143）
        if (l.unit.Length > 0 || l.amount != 0)
        {
            var hasCur = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM finamsum WITH (NOLOCK) WHERE rtrim(subcode) = @fcode AND period = @period",
                new { fcode = Ansi(fcode, 18), period = Ansi(period, 6) }, tx);
            if (hasCur == 0 && !un)
            {
                var prev = conn.QuerySingleOrDefault<(decimal bam, decimal bbal, string bdc)>(
                    "SELECT TOP (1) ISNULL(bamount,0), ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')) FROM finamsum WITH (NOLOCK) WHERE rtrim(subcode) = @fcode AND period = @pp",
                    new { fcode = Ansi(fcode, 18), pp = Ansi(FinPrevPeriod(period), 6) }, tx);
                conn.Execute(
                    "INSERT INTO finamsum (subcode, subname, period, isdetail, bdcdir, bamount, bbalance, unit, dpm) VALUES (@fcode, @fname, @period, @isd, @bdc, @bam, @bbal, @unit, @dpm)",
                    new
                    {
                        fcode = Ansi(fcode, 18), fname = Ansi(sub.subname, 40), period = Ansi(period, 6),
                        isd = sub.detacode != 0, bdc = Ansi(prev.bdc ?? "平", 2), bam = (double)prev.bam,
                        bbal = (double)prev.bbal, unit = Ansi(l.unit, 10), dpm = Ansi(dpm, 10),
                    }, tx);
            }
            var cur = conn.QuerySingleOrDefault<(decimal cde, decimal cds, decimal ccr, decimal ccs, decimal yde, decimal yds,
                decimal bam, decimal bbal, string bdc)>(
                "SELECT ISNULL(cdeamou,0), ISNULL(cdesum,0), ISNULL(ccreamou,0), ISNULL(ccresum,0), ISNULL(ydeamou,0), ISNULL(ydesum,0), ISNULL(bamount,0), ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')) FROM finamsum WITH (NOLOCK) WHERE rtrim(subcode) = @fcode AND period = @period",
                new { fcode = Ansi(fcode, 18), period = Ansi(period, 6) }, tx);
            if (cur.bdc.Length > 0 || cur.cde != 0 || cur.cds != 0 || cur.ccr != 0 || cur.ccs != 0)
            {
                var cde = cur.cde + sign * (l.debit != 0 ? l.amount : 0);
                var cds = cur.cds + sign * (l.debit != 0 ? l.debit : 0);
                var ccr = cur.ccr + sign * (l.debit != 0 ? 0 : l.amount);
                var ccs = cur.ccs + sign * (l.debit != 0 ? 0 : l.credit);
                var yde = cur.yde + sign * (l.debit != 0 ? l.amount : 0);
                var yds = cur.yds + sign * (l.debit != 0 ? l.debit : 0);
                var bam0 = (cur.bdc != "贷" ? 1 : -1) * cur.bam + cde - ccr;
                conn.Execute(
                    "UPDATE finamsum SET cdeamou = @cde, cdesum = @cds, ccreamou = @ccr, ccresum = @ccs, ydeamou = @yde, ydesum = @yds, edcdir = @edc, eamount = @eam, ebalance = @ebal WHERE rtrim(subcode) = @fcode AND period = @period",
                    new
                    {
                        cde = (double)cde, cds = (double)cds, ccr = (double)ccr, ccs = (double)ccs,
                        yde = (double)yde, yds = (double)yds,
                        edc = Ansi(bam0 > 0 ? "借" : bam0 < 0 ? "贷" : "平", 2),
                        eam = (double)Math.Abs(bam0), ebal = (double)Math.Abs(cur.bbal + cds - ccs),
                        fcode = Ansi(fcode, 18), period = Ansi(period, 6),
                    }, tx);
            }
        }
    }

    private static void FinApplyItsub(SqlConnection conn, SqlTransaction tx,
        (int id, string subcode, decimal debit, decimal credit, decimal amount, string unit, string opertype,
            string dept, string item, string empl, string client, string vouword, string subname) l,
        string period, bool un)
    {
        var code = l.subcode.Trim();
        foreach (var (ty, name, enabled) in new[]
                 {
                     ("D", l.dept, l.dept.Length > 0), ("I", l.item, l.item.Length > 0),
                     ("E", l.empl, l.empl.Length > 0), ("C", l.client, l.client.Length > 0 && (l.opertype == "A" || l.opertype == "P")),
                 })
        {
            if (!enabled) continue;
            var exists = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM finitsub WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                new { name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), period = Ansi(period, 6) }, tx);
            if (exists == 0 && !un)
            {
                var prev = conn.QuerySingleOrDefault<(decimal bbal, string bdc)>(
                    "SELECT TOP (1) ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')) FROM finitsub WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @pp",
                    new { name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), pp = Ansi(FinPrevPeriod(period), 6) }, tx);
                conn.Execute(
                    "INSERT INTO finitsub (itemty, itemna, subcode, subname, bdcdir, period, bbalance, vouword, cdesum, ccresum) VALUES (@ty, @name, @code, @subname, @bdc, @period, @bbal, @vouword, @cd, @cc)",
                    new
                    {
                        ty = Ansi(ty, 1), name = Ansi(name, 40), code = Ansi(code, 18), subname = Ansi(l.subname, 40),
                        bdc = Ansi(prev.bdc ?? "平", 2), period = Ansi(period, 6), bbal = (double)prev.bbal,
                        vouword = Ansi(l.vouword, 4), cd = (double)l.debit, cc = (double)l.credit,
                    }, tx);
            }
            else
            {
                conn.Execute(
                    "UPDATE finitsub SET cdesum = ISNULL(cdesum,0) + @cd, ccresum = ISNULL(ccresum,0) + @cc WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                    new { cd = (un ? -1 : 1) * l.debit, cc = (un ? -1 : 1) * l.credit, name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), period = Ansi(period, 6) }, tx);
            }
            var row = conn.QuerySingleOrDefault<(decimal bbal, string bdc, decimal cd, decimal cc)>(
                "SELECT ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')), ISNULL(cdesum,0), ISNULL(ccresum,0) FROM finitsub WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                new { name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), period = Ansi(period, 6) }, tx);
            if (row.bbal == 0 && row.bdc.Length == 0 && row.cd == 0 && row.cc == 0)
            {
                if (un) conn.Execute(
                    "DELETE FROM finitsub WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                    new { name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), period = Ansi(period, 6) }, tx);
                continue;
            }
            var eb = (row.bdc == "贷" ? -1 * row.bbal : row.bbal) + row.cd - row.cc;
            conn.Execute(
                "UPDATE finitsub SET edcdir = @edc, ebalance = @eb WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                new
                {
                    edc = Ansi(eb > 0 ? "借" : eb < 0 ? "贷" : "平", 2), eb = (double)Math.Abs(eb),
                    name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), period = Ansi(period, 6),
                }, tx);
        }
    }

    private static void FinApplySubit(SqlConnection conn, SqlTransaction tx,
        (int id, string subcode, decimal debit, decimal credit, decimal amount, string unit, string opertype,
            string dept, string item, string empl, string client, string vouword, string subname) l,
        string period, bool un)
    {
        foreach (var (ty, name, enabled) in new[]
                 {
                     ("D", l.dept, l.dept.Length > 0), ("I", l.item, l.item.Length > 0),
                     ("E", l.empl, l.empl.Length > 0), ("C", l.client, l.client.Length > 0 && (l.opertype == "A" || l.opertype == "P")),
                 })
        {
            if (!enabled) continue;
            var exists = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM finsubit WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                new { name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6) }, tx);
            if (exists == 0 && !un)
            {
                var prev = conn.QuerySingleOrDefault<(decimal bbal, string bdc)>(
                    "SELECT TOP (1) ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')) FROM finsubit WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @pp",
                    new { name = Ansi(name, 40), ty = Ansi(ty, 1), pp = Ansi(FinPrevPeriod(period), 6) }, tx);
                conn.Execute(
                    "INSERT INTO finsubit (itemty, itemna, period, vouword, bdcdir, bbalance, cdesum, ccresum) VALUES (@ty, @name, @period, @vouword, @bdc, @bbal, @cd, @cc)",
                    new
                    {
                        ty = Ansi(ty, 1), name = Ansi(name, 40), period = Ansi(period, 6),
                        vouword = Ansi(l.vouword, 4), bdc = Ansi(prev.bdc ?? "平", 2), bbal = (double)prev.bbal,
                        cd = (double)l.debit, cc = (double)l.credit,
                    }, tx);
            }
            else
            {
                conn.Execute(
                    "UPDATE finsubit SET cdesum = ISNULL(cdesum,0) + @cd, ccresum = ISNULL(ccresum,0) + @cc WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                    new { cd = (un ? -1 : 1) * l.debit, cc = (un ? -1 : 1) * l.credit, name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6) }, tx);
            }
            var row = conn.QuerySingleOrDefault<(decimal bbal, string bdc, decimal cd, decimal cc)>(
                "SELECT ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')), ISNULL(cdesum,0), ISNULL(ccresum,0) FROM finsubit WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                new { name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6) }, tx);
            if (row.bbal == 0 && row.bdc.Length == 0 && row.cd == 0 && row.cc == 0)
            {
                if (un) conn.Execute(
                    "DELETE FROM finsubit WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                    new { name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6) }, tx);
                continue;
            }
            var eb = (row.bdc == "贷" ? -1 * row.bbal : row.bbal) + row.cd - row.cc;
            conn.Execute(
                "UPDATE finsubit SET edcdir = @edc, ebalance = @eb WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                new
                {
                    edc = Ansi(eb > 0 ? "借" : eb < 0 ? "贷" : "平", 2), eb = (double)Math.Abs(eb),
                    name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6),
                }, tx);
        }
    }

    /// <summary>过帐（fr_finpost post row2）。</summary>
    public static void FinPost(SqlConnection conn, string period, string per, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var vids = conn.Query<int>(
            "SELECT DISTINCT vouchid FROM finvouch WITH (NOLOCK) WHERE period = @p AND ISNULL(audit,0) = 1 AND ISNULL(post,0) = 0 ORDER BY vouchid",
            new { p = Ansi(period, 6) }).AsList();
        foreach (var vid in vids)
        {
            using var tx = conn.BeginTransaction();
            try
            {
                var lines = conn.Query<(int id, string subcode, decimal debit, decimal credit, decimal amount,
                    string unit, string opertype, string dept, string item, string empl, string client,
                    string vouword, string subname)>(
                    "SELECT id, RTRIM(subcode), ISNULL(debit,0), ISNULL(credit,0), ISNULL(amount,0), RTRIM(ISNULL(unit,'')), ISNULL(rtrim(opertype),''), RTRIM(ISNULL(dept,'')), RTRIM(ISNULL(item,'')), RTRIM(ISNULL(empl,'')), RTRIM(ISNULL(client,'')), RTRIM(vouword), RTRIM(subname) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid ORDER BY recoid",
                    new { vid }, tx).AsList();
                foreach (var l in lines)
                {
                    var code = l.subcode.Trim();
                    var j = code.Length;
                    while (j >= 3 && j <= code.Length)
                    {
                        FinApplySub(conn, tx, code[..j], l, period, dpm, un: false);
                        j -= FinSubWidth(j);
                    }
                    FinApplyItsub(conn, tx, l, period, un: false);
                    FinApplySubit(conn, tx, l, period, un: false);
                    var fin = conn.QueryFirstOrDefault<(decimal ed, decimal ec)>(
                        "SELECT ISNULL(edesum,0), ISNULL(ecresum,0) FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @sub",
                        new { sub = Ansi(code, 18) }, tx);
                    var deCre = fin.ed - fin.ec;
                    conn.Execute(
                        "UPDATE finvouch SET dcdir = @dcdir, balance = @balance, post = 1, poster = @per WHERE id = @id",
                        new
                        {
                            dcdir = Ansi(deCre > 0 ? "借" : deCre < 0 ? "贷" : "平", 2),
                            balance = (double)Math.Abs(deCre), per = Ansi(per, 10), l.id,
                        }, tx);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>反过帐（fr_finpost unpost row2）。</summary>
    public static void FinUnpost(SqlConnection conn, string period, string per, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var vids = conn.Query<int>(
            "SELECT DISTINCT vouchid FROM finvouch WITH (NOLOCK) WHERE period = @p AND ISNULL(audit,0) = 1 AND ISNULL(post,0) = 1 ORDER BY vouchid",
            new { p = Ansi(period, 6) }).AsList();
        foreach (var vid in vids)
        {
            using var tx = conn.BeginTransaction();
            try
            {
                var lines = conn.Query<(int id, string subcode, decimal debit, decimal credit, decimal amount,
                    string unit, string opertype, string dept, string item, string empl, string client,
                    string vouword, string subname)>(
                    "SELECT id, RTRIM(subcode), ISNULL(debit,0), ISNULL(credit,0), ISNULL(amount,0), RTRIM(ISNULL(unit,'')), ISNULL(rtrim(opertype),'VOU'), RTRIM(ISNULL(dept,'')), RTRIM(ISNULL(item,'')), RTRIM(ISNULL(empl,'')), RTRIM(ISNULL(client,'')), RTRIM(vouword), RTRIM(subname) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid ORDER BY recoid",
                    new { vid }, tx).AsList();
                foreach (var l in lines)
                {
                    var code = l.subcode.Trim();
                    var j = code.Length;
                    while (j >= 3 && j <= code.Length)
                    {
                        FinApplySub(conn, tx, code[..j], l, period, dpm, un: true);
                        j -= FinSubWidth(j);
                    }
                    FinApplyItsub(conn, tx, l, period, un: true);
                    FinApplySubit(conn, tx, l, period, un: true);
                    conn.Execute(
                        "UPDATE finvouch SET dcdir = '', balance = 0, post = 0, poster = '' WHERE id = @id",
                        new { l.id }, tx);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    // =================================================================================
    // 账务第七批（结转损益/期末结帐/帐务初始/帐套选项）旧侧回放
    // 契约：开发文档/契约摘录/账务第七批契约.md
    // =================================================================================

    /// <summary>结转损益（I34，fr_fincaforw row4）：exec fincaforw 原样调用。</summary>
    public static string FinCaforw(SqlConnection conn, string dpm, string period, string per)
        => conn.QueryFirstOrDefault<string>("EXEC fincaforw @dpm, @period, @per",
            new { dpm = Ansi(dpm, 10), period = Ansi(period, 6), per = Ansi(per, 10) }) ?? "";

    /// <summary>期末结帐（I35，fr_fincheout row18）：exec getfincheout 原样调用。</summary>
    public static string FinCheout(SqlConnection conn, string dpm, string period)
        => conn.QueryFirstOrDefault<string>("EXEC getfincheout @period, @dpm",
            new { period = Ansi(period, 6), dpm = Ansi(dpm, 10) }) ?? "";

    /// <summary>反结帐（I35，fr_fincheout row20）：表单校验 + exec finuncheout(period, dpm, del)。</summary>
    public static string FinUncheout(SqlConnection conn, string dpm, string period, bool delCaf)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        if (period.EndsWith("01")) return "不能反结帐到上一年度!";
        var uperiod = conn.ExecuteScalar<string>(
            "SELECT RTRIM(uperiod) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }) ?? "";
        var prev = FinPrevPeriod(period);
        var cperiod = string.CompareOrdinal(uperiod, prev) > 0 ? uperiod : prev;
        var hasBal = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM finhisbal WITH (NOLOCK) WHERE period = @cperiod AND dpm = @dpm",
            new { cperiod = Ansi(cperiod, 6), dpm = Ansi(dpm, 10) });
        if (hasBal == 0) return "没有上月月结数据!";
        return conn.QueryFirstOrDefault<string>("EXEC finuncheout @period, @dpm, @del",
            new { period = Ansi(period, 6), dpm = Ansi(dpm, 10), del = delCaf }) ?? "";
    }

    /// <summary>帐套选项保存（N12，fr_finpara row3）：syscontrol 18 列更新。</summary>
    public static void FinParaSave(SqlConnection conn, string cId, string cName, string uperiod,
        int leng1, int leng2, int leng3, int leng4, int leng5, int leng6, string dpm)
    {
        var udate = DateTime.Parse($"{uperiod[..4]}-{uperiod[4..]}-01");
        conn.Execute(
            """
            UPDATE syscontrol SET c_name = @cName, addr = '', c_type = '', attri = 0, artper = '',
                                  taxid = '', bank = '', bankid = '', tel = '', fax = '', gramount = 6,
                                  leng1 = @l1, leng2 = @l2, leng3 = @l3, leng4 = @l4, leng5 = @l5, leng6 = @l6,
                                  uperiod = @uperiod, udate = @udate, period = @uperiod
            WHERE rtrim(c_id) = @cId AND dpm = @dpm
            """,
            new
            {
                cName = Ansi(cName, 40), l1 = leng1, l2 = leng2, l3 = leng3, l4 = leng4, l5 = leng5, l6 = leng6,
                uperiod = Ansi(uperiod, 6), udate, cId = Ansi(cId, 10), dpm = Ansi(dpm, 10),
            });
    }

    /// <summary>帐务初始 detacode 重算（N11，fr_fininit Init：全库游标 LOCATE 语义逐行判定）。</summary>
    public static int FinInitRecalcDetacode(SqlConnection conn, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var rows = conn.Query<(string subcode, int len)>(
            "SELECT RTRIM(subcode), LEN(RTRIM(subcode)) FROM finsub WITH (NOLOCK) WHERE dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }).AsList();
        var n = 0;
        foreach (var (sub, len) in rows)
        {
            // LOCATE FOR LEFT(ALLTRIM(SUBCODE),LENG)=SUB0 AND LEN(ALLTRIM(SUBCODE))>LENG（全库游标）
            var hasChild = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM finsub WITH (NOLOCK) WHERE LEFT(RTRIM(subcode), @len) = @sub AND LEN(RTRIM(subcode)) > @len",
                new { len, sub = Ansi(sub, 18) }) > 0;
            n += conn.Execute(
                "UPDATE finsub SET detacode = @d WHERE rtrim(subcode) = @sub AND dpm = @dpm",
                new { d = !hasChild, sub = Ansi(sub, 18), dpm = Ansi(dpm, 10) });
        }
        return n;
    }

    /// <summary>帐务初始置零（N11，myclick 'Init'）：全表 12 列=0（dpm 范围）。</summary>
    public static int FinInitZero(SqlConnection conn, string dpm)
        => conn.Execute(
            """
            UPDATE finsub SET ybdesum = 0, ybcresum = 0, bdesum = 0, bcresum = 0, cdesum = 0, ccresum = 0,
                              ydesum = 0, ycresum = 0, edesum = 0, ecresum = 0, mpalsum = 0, ypalsum = 0
            WHERE dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) });

    /// <summary>
    /// 帐务初始 Sum 汇总（N11，myclick 'Sum'）：VFP 逐级循环语义重放——
    /// 末级科目快照（清零前）→ 全表 12 列清零 → 逐科目沿前缀链（I 从 LEN 递减到 SUBLENG1，步长 gsubleng）
    /// 累计 7 列 + edesum/ecresum 符号拆分（每级独立 update）。
    /// </summary>
    public static int FinInitSum(SqlConnection conn, string dpm, int subleng1)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        // 1. subtmp：末级科目快照（清零前；含 7 列累计源值）
        var leaves = conn.Query<(string subcode, decimal bd, decimal bc, decimal yd, decimal yc, decimal ybd, decimal ybc, decimal yp)>(
            """
            SELECT RTRIM(subcode), ISNULL(bdesum,0), ISNULL(bcresum,0), ISNULL(ydesum,0), ISNULL(ycresum,0),
                   ISNULL(ybdesum,0), ISNULL(ybcresum,0), ISNULL(ypalsum,0)
            FROM finsub WITH (NOLOCK) WHERE dpm = @dpm AND ISNULL(detacode,0) = 1 ORDER BY RTRIM(subcode)
            """, new { dpm = Ansi(dpm, 10) }).AsList();
        // 2. 全表 12 列清零（PASSTH update 无 WHERE → dpm）
        var rows = conn.Execute(
            """
            UPDATE finsub SET ybdesum = 0, ybcresum = 0, bdesum = 0, bcresum = 0, cdesum = 0, ccresum = 0,
                              ydesum = 0, ycresum = 0, edesum = 0, ecresum = 0, mpalsum = 0, ypalsum = 0
            WHERE dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) });
        // 3. 逐末级科目沿前缀链累计（LOCATE FOUND() 才 REPLACE；EDECRE 用累计后的父行值）
        foreach (var lf in leaves)
        {
            var i = lf.subcode.Length;
            while (i >= subleng1 && i <= lf.subcode.Length)
            {
                var psub = lf.subcode[..i];
                var found = conn.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @psub",
                    new { psub = Ansi(psub, 18) }) > 0;
                if (found)
                {
                    conn.Execute(
                        """
                        UPDATE finsub SET edesum = CASE WHEN bdesum + @bd - (bcresum + @bc) + cdesum - ccresum >= 0
                                                         THEN bdesum + @bd - (bcresum + @bc) + cdesum - ccresum ELSE 0 END,
                                          ecresum = CASE WHEN bdesum + @bd - (bcresum + @bc) + cdesum - ccresum <= 0
                                                         THEN ABS(bdesum + @bd - (bcresum + @bc) + cdesum - ccresum) ELSE 0 END,
                                          bdesum = bdesum + @bd, bcresum = bcresum + @bc,
                                          ydesum = ydesum + @yd, ycresum = ycresum + @yc,
                                          ybdesum = ybdesum + @ybd, ybcresum = ybcresum + @ybc,
                                          ypalsum = ypalsum + @yp
                        WHERE rtrim(subcode) = @psub AND dpm = @dpm
                        """,
                        new
                        {
                            bd = (double)lf.bd, bc = (double)lf.bc, yd = (double)lf.yd, yc = (double)lf.yc,
                            ybd = (double)lf.ybd, ybc = (double)lf.ybc, yp = (double)lf.yp,
                            psub = Ansi(psub, 18), dpm = Ansi(dpm, 10),
                        });
                }
                i -= FinSubWidth(i);
            }
        }
        return rows;
    }

    /// <summary>帐务初始末级余额编辑（N11，u_.row4 Refresh：ybdesum/ybcresum 派生 + 7 列更新）。</summary>
    public static void FinInitUpdateBalance(SqlConnection conn, string subcode, decimal bd, decimal bc,
        decimal yd, decimal yc, decimal yp, string dpm)
    {
        var baln0 = (bd - bc) - (yd - yc);
        conn.Execute(
            """
            UPDATE finsub SET ybdesum = @yb, ybcresum = @ybc, bdesum = @bd, bcresum = @bc,
                              ydesum = @yd, ycresum = @yc, ypalsum = @yp
            WHERE rtrim(subcode) = @subcode AND dpm = @dpm
            """,
            new
            {
                yb = (double)(baln0 >= 0 ? baln0 : 0), ybc = (double)(baln0 < 0 ? -baln0 : 0),
                bd = (double)bd, bc = (double)bc, yd = (double)yd, yc = (double)yc, yp = (double)yp,
                subcode = Ansi(subcode, 18), dpm = Ansi(dpm, 10),
            });
    }

    /// <summary>启用帐套（N13，fr_finuse row3）：平衡校验 → 清空九表 → finsoursub 快照（列数不匹配静默失败）→ l_use=1。</summary>
    public static string FinUse(SqlConnection conn, string dpm, int subleng1)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var bal = conn.QueryFirst<(decimal yb, decimal ybc, decimal yd, decimal yc, decimal bd, decimal bc)>(
            """
            SELECT ISNULL(SUM(ybdesum),0), ISNULL(SUM(ybcresum),0), ISNULL(SUM(ydesum),0),
                   ISNULL(SUM(ycresum),0), ISNULL(SUM(bdesum),0), ISNULL(SUM(bcresum),0)
            FROM finsub WITH (NOLOCK) WHERE dpm = @dpm AND LEN(RTRIM(subcode)) = @subleng1
            """, new { dpm = Ansi(dpm, 10), subleng1 });
        if (bal.yb != bal.ybc || bal.yd != bal.yc || bal.bd != bal.bc)
            return "帐务初始数据不平衡，不能启用帐套,\r\n请再调整帐务系统初始数据";
        foreach (var table in new[] { "finaleger", "finsubit", "finitsub", "finamsum", "finrecout", "finreclist", "fincadistr", "finhisbal", "finvouch" })
            conn.Execute($"DELETE FROM {table} WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
        conn.Execute("UPDATE finvid SET vid = 0 WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
        // finsoursub 快照 INSERT：finsub(35 列)/finsoursub(31 列) 列数不匹配——旧 SQLEXEC 返回值未检查静默吞掉
        try
        {
            conn.Execute("INSERT INTO finsoursub SELECT * FROM finsub WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
        }
        catch
        {
            // 旧系统行为：INSERT 失败静默继续
        }
        var cid = conn.ExecuteScalar<string>(
            "SELECT RTRIM(c_id) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }) ?? "";
        conn.Execute("UPDATE syscontrol SET l_use = 1 WHERE c_id = @cid AND dpm = @dpm",
            new { cid = Ansi(cid, 10), dpm = Ansi(dpm, 10) });
        return "";
    }
}
