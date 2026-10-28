using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace QPGL.Tests.Replay;

/// <summary>
/// 账务第九批旧侧回放（报表表达式引擎/现金流链/工具/固定资产）——契约：开发文档/契约摘录/账务第九批契约.md。
/// 表达式引擎独立移植（GOPERATOR/GSUBCODE/GEXPRESS/GETSUBN，pg_proc L2552-2700 语义）——与新仓储互为验证。
/// </summary>
public static partial class LegacyReplay
{
    // ───────────────── 表达式引擎（独立移植） ─────────────────

    private static string RptOperator(string cstr, int i)
    {
        var n = 0;
        for (var m = 0; m < cstr.Trim().Length; m++)
            if ("()+-*/:".Contains(cstr[m]))
            {
                n++;
                if (n == i) return cstr[m].ToString();
            }
        return "";
    }

    private static string RptSubcode(string cstr, int i)
    {
        if (string.IsNullOrEmpty(cstr)) return "";
        if (!cstr.Contains('+') && !cstr.Contains('-') && i == 1) return cstr;
        int spl = 0, epl = 0, n = 0;
        var leng = cstr.Trim().Length;
        for (var m = 0; m < leng; m++)
            if ("()+-*/:".Contains(cstr[m]))
            {
                n++;
                if (n == i - 1) spl = m + 1;
                if (n == i) { epl = m + 1; break; }
            }
        if (spl <= 0 && epl <= 0) return "";
        if (spl > 0 && epl == 0) epl = leng + 1;
        var s0 = spl;
        var e0 = epl - 1;
        if (s0 >= e0) return e0 - s0 + 1 == 0 ? "TTT" : "";
        return cstr[s0..e0];
    }

    private static string RptExpress(string cstr, string cy)
    {
        var pat0 = cstr.IndexOf('S');
        var pat1 = cstr.IndexOf('(');
        var pat2 = cstr.IndexOf(':');
        var pat3 = cstr.IndexOf(')');
        if (pat0 < 0 || pat1 < 0 || pat2 < 0 || pat3 < 0 || pat1 + 2 > pat2 || pat2 + 2 > pat3) return cstr;
        if (!int.TryParse(cstr.Substring(pat1 + 2, pat2 - pat1 - 2).Trim(), out var b)) return cstr;
        if (!int.TryParse(cstr.Substring(pat2 + 2, pat3 - pat2 - 2).Trim(), out var e)) return cstr;
        var parts = new List<string>();
        for (var j = b; j <= e; j++) parts.Add(cy + j);
        return cstr[..pat0] + string.Join("+", parts) + cstr[(pat3 + 1)..];
    }

    private sealed record RptSubRow(string Subcode, string Fdc, decimal Ybdesum, decimal Ybcresum,
        decimal Cdesum, decimal Ccresum, decimal Ydesum, decimal Ycresum,
        decimal Edesum, decimal Ecresum, decimal Mpalsum, decimal Ypalsum);

    private static List<RptSubRow> RptLoadFinoSub(SqlConnection conn, string period, bool useHisbal, string dpm)
    {
        const string cols = "rtrim(subcode), rtrim(ISNULL(fdc,'')), ISNULL(ybdesum,0), ISNULL(ybcresum,0), ISNULL(cdesum,0), ISNULL(ccresum,0), " +
                            "ISNULL(ydesum,0), ISNULL(ycresum,0), ISNULL(edesum,0), ISNULL(ecresum,0), ISNULL(mpalsum,0), ISNULL(ypalsum,0)";
        var raw = useHisbal
            ? conn.Query<(string subcode, string fdc, double ybd, double ybc, double cd, double cc, double yd, double yc,
                double ed, double ec, double mp, double yp)>(
                $"SELECT {cols} FROM finhisbal WITH (NOLOCK) WHERE period=@p AND dpm=@dpm ORDER BY rtrim(subcode)",
                new { p = Ansi(period, 6), dpm = Ansi(dpm, 10) }).AsList()
            : conn.Query<(string subcode, string fdc, double ybd, double ybc, double cd, double cc, double yd, double yc,
                double ed, double ec, double mp, double yp)>(
                $"SELECT {cols} FROM finsub WITH (NOLOCK) WHERE dpm=@dpm ORDER BY rtrim(subcode)",
                new { dpm = Ansi(dpm, 10) }).AsList();
        return raw.Select(x => new RptSubRow(x.subcode, x.fdc, (decimal)x.ybd, (decimal)x.ybc, (decimal)x.cd, (decimal)x.cc,
            (decimal)x.yd, (decimal)x.yc, (decimal)x.ed, (decimal)x.ec, (decimal)x.mp, (decimal)x.yp)).ToList();
    }

    private static decimal RptGetSubn(List<RptSubRow> finosub, string mSub)
    {
        var cSub = mSub;
        var baty = "E";
        if (mSub.Contains('.'))
        {
            var at0 = mSub.IndexOf('.');
            cSub = mSub[..at0];
            baty = mSub[(at0 + 1)..];
            if (baty.Contains('@')) baty = baty.Split('@')[0];
        }
        var row = finosub.FirstOrDefault(x => x.Subcode == cSub);
        if (row is null) return 0;
        var ndc = row.Fdc == "C" ? -1 : 1;
        return baty switch
        {
            "B" => ndc * (row.Ybdesum - row.Ybcresum),
            "DB" => row.Ybdesum, "CB" => row.Ybcresum, "AB" => Math.Abs(row.Ybdesum - row.Ybcresum),
            "E" => ndc * (row.Edesum - row.Ecresum),
            "DE" => row.Edesum, "CE" => row.Ecresum, "AE" => Math.Abs(row.Edesum - row.Ecresum),
            "DC" => row.Cdesum, "CC" => row.Ccresum, "DY" => row.Ydesum, "CY" => row.Ycresum,
            "SX" => row.Fdc == "C" ? row.Ccresum : row.Cdesum,
            "SY" => row.Mpalsum, "SL" => row.Ypalsum,
            _ => 0,
        };
    }

    private static decimal RptEval(string exp, int i, Func<int, decimal> self, List<RptSubRow> finosub)
    {
        decimal sum = 0;
        while (true)
        {
            var sub = RptSubcode(exp, i);
            var oper = RptOperator(exp, i - 1);
            if (string.IsNullOrEmpty(sub)) break;
            if (sub.Contains('C') && sub.Length > 1 && int.TryParse(sub[1..], out var reid))
                sum += (oper != "-" ? 1 : -1) * self(reid);
            else
                sum += (oper != "-" ? 1 : -1) * RptGetSubn(finosub, sub);
            i++;
        }
        return sum;
    }

    // ───────────────── 查询重放 ─────────────────

    /// <summary>试算平衡（fr_finbalanf y_.row15 语义）。</summary>
    public static List<string> FinTrialLegacy(SqlConnection conn, string period, bool useHisbal, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var table = useHisbal ? "finhisbal" : "finsub";
        var periodCond = useHisbal ? " AND period = @p" : "";
        var rows = conn.Query<(string subcode, string subname, string fdc, decimal ybd, decimal ybc, decimal bd, decimal bc,
            decimal cd, decimal cc, decimal yd, decimal yc, decimal ed, decimal ec)>(
            $"""
            SELECT rtrim(subcode), rtrim(subname), rtrim(ISNULL(fdc,'')), ISNULL(ybdesum,0), ISNULL(ybcresum,0),
                   ISNULL(bdesum,0), ISNULL(bcresum,0), ISNULL(cdesum,0), ISNULL(ccresum,0),
                   ISNULL(ydesum,0), ISNULL(ycresum,0), ISNULL(edesum,0), ISNULL(ecresum,0)
            FROM {table} WITH (NOLOCK) WHERE LEN(rtrim(subcode)) <= 3{periodCond} ORDER BY rtrim(subcode)
            """, new { p = Ansi(period, 6), dpm = Ansi(dpm, 10) }).AsList();
        return rows.Select(r =>
            $"TRIAL|{r.subcode}|{r.subname}|{r.fdc}|{r.ybd:0.##}/{r.ybc:0.##}|{r.bd:0.##}/{r.bc:0.##}|" +
            $"{r.cd:0.##}/{r.cc:0.##}|{r.yd:0.##}/{r.yc:0.##}|{r.ed:0.##}/{r.ec:0.##}").ToList();
    }

    /// <summary>凭证汇总（fr_fincollef j_.row12 语义：级次截断合并+张数/附单据）。</summary>
    public static List<string> FinVouchSummaryLegacy(SqlConnection conn, DateTime bDate, DateTime eDate, string vouword,
        int postMode, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var cond = "date >= @bDate AND date <= @eDate";
        if (vouword != "(全部)") cond += " AND rtrim(vouword) = @vouword";
        if (postMode == 2) cond += " AND post = 1";
        if (postMode == 3) cond += " AND ISNULL(post,0) = 0";
        var stat = conn.QueryFirst<(int cren, decimal abill)>(
            $"SELECT COUNT(*), ISNULL(SUM(abill),0) FROM finvouch WITH (NOLOCK) WHERE {cond}",
            new { bDate, eDate, vouword = Ansi(vouword, 4) });
        var raw = conn.Query<(string subcode, string subname, decimal debit, decimal credit)>(
            $"SELECT rtrim(subcode), rtrim(subname), ISNULL(SUM(debit),0), ISNULL(SUM(credit),0) FROM finvouch WITH (NOLOCK) WHERE {cond} GROUP BY rtrim(subcode), rtrim(subname) ORDER BY rtrim(subcode)",
            new { bDate, eDate, vouword = Ansi(vouword, 4) }).AsList();
        var merged = new Dictionary<string, (string subname, decimal d, decimal c)>();
        foreach (var r in raw)
        {
            var code = r.subcode.Length > 3 ? r.subcode[..3] : r.subcode;
            if (merged.TryGetValue(code, out var v))
                merged[code] = (v.subname, v.d + r.debit, v.c + r.credit);
            else
                merged[code] = (r.subname, r.debit, r.credit);
        }
        var result = merged.OrderBy(x => x.Key)
            .Select(x => $"SUM|{x.Key}|{x.Value.subname}|{x.Value.d:0.##}|{x.Value.c:0.##}").ToList();
        result.Add($"SUMSTAT|{stat.cren}|{stat.abill:0.##}");
        return result;
    }

    /// <summary>损益表求值+保存（myclick Sum + Save：finprol 全量重写）。</summary>
    public static void FinProlSumSaveLegacy(SqlConnection conn, string period, bool useHisbal, string dpm, DateTime now)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var rows = conn.Query<(int reid, string itemna, string itemty, decimal csum, decimal nsum,
            string cexpress, string nexpress)>(
            "SELECT reid, rtrim(ISNULL(itemna,'')), rtrim(ISNULL(itemty,'')), ISNULL(csum,0), ISNULL(nsum,0), " +
            "rtrim(ISNULL(cexpress,'')), rtrim(ISNULL(nexpress,'')) FROM finprol WITH (NOLOCK) WHERE dpm=@dpm ORDER BY reid",
            new { dpm = Ansi(dpm, 10) }).AsList();
        var finosub = RptLoadFinoSub(conn, period, useHisbal, dpm);
        // finprol 基线多 dpm 行 reid 可能重复（旧码 SET ORDER + LOCATE 取首行）→ GroupBy First
        var dict = rows.GroupBy(r => r.reid).ToDictionary(g => g.Key, g => (c: g.First().csum, n: g.First().nsum));
        var outRows = new List<(int reid, string itemna, string itemty, decimal csum, decimal nsum, string cexpress, string nexpress)>();
        foreach (var r in rows)
        {
            decimal csum = r.csum, nsum = r.nsum;
            if (!string.IsNullOrWhiteSpace(r.cexpress) || !string.IsNullOrWhiteSpace(r.nexpress))
            {
                var expc = r.cexpress;
                var expn = r.nexpress;
                if (expc.ToUpperInvariant().Contains("SUM")) expc = RptExpress(expc, "C");
                if (expn.ToUpperInvariant().Contains("SUM")) expn = RptExpress(expn, "D");
                csum = RptEval(expc, 1, reid => dict.TryGetValue(reid, out var v) ? v.c : 0, finosub);
                nsum = RptEval(expn, 1, reid => dict.TryGetValue(reid, out var v) ? v.n : 0, finosub);
            }
            outRows.Add((r.reid, r.itemna, r.itemty, csum, nsum, r.cexpress, r.nexpress));
        }
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM finprol WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, tx);
        foreach (var r in outRows)
            conn.Execute(
                "INSERT INTO finprol (dpm, reid, itemna, itemty, csum, nsum, cexpress, nexpress, date) VALUES (@dpm, @reid, @itemna, @itemty, @csum, @nsum, @cexpress, @nexpress, @date)",
                new
                {
                    dpm = Ansi(dpm, 10), r.reid, itemna = Ansi(r.itemna, 40), itemty = Ansi(r.itemty, 1),
                    csum = (double)r.csum, nsum = (double)r.nsum, cexpress = Ansi(r.cexpress, 60),
                    nexpress = Ansi(r.nexpress, 60), date = now,
                }, tx);
        tx.Commit();
    }

    /// <summary>现金流分配保存（fr_fincashdis QueryUnload：差额校验失败返回 false，成功 fincadistr 重写+cashdis=1+清 0 行）。</summary>
    public static string FinCashDisLegacy(SqlConnection conn, int vouchid, string period, DateTime date,
        IReadOnlyList<(string cashid, string cashna, int iodir, decimal desum, decimal cresum)> lines, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var vouchDiff = conn.ExecuteScalar<decimal>(
            "SELECT ISNULL(SUM(debit-credit),0) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid AND ISNULL(fcash,0) = 1",
            new { vid = vouchid });
        var disDiff = lines.Sum(l => l.desum - l.cresum);
        if (disDiff != vouchDiff)
            return $"DIFF|{vouchDiff:0.##}|{disDiff:0.##}";
        using var tx = conn.BeginTransaction();
        conn.Execute("UPDATE fincadistr SET desum=0, cresum=0 WHERE vouchid=@vid AND dpm=@dpm", new { vid = vouchid, dpm = Ansi(dpm, 10) }, tx);
        foreach (var l in lines)
        {
            if (l.desum == 0 && l.cresum == 0) continue;
            var exists = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM fincadistr WITH (NOLOCK) WHERE rtrim(cashid)=@cid AND vouchid=@vid AND dpm=@dpm",
                new { cid = Ansi(l.cashid, 15), vid = vouchid, dpm = Ansi(dpm, 10) }, tx);
            if (exists == 0)
                conn.Execute(
                    "INSERT INTO fincadistr (dpm, cashid, cashna, iodir, vouchid, period, date, desum, cresum) VALUES (@dpm, @cid, @cna, @iodir, @vid, @period, @date, @desum, @cresum)",
                    new { dpm = Ansi(dpm, 10), cid = Ansi(l.cashid, 15), cna = Ansi(l.cashna, 60), iodir = l.iodir, vid = vouchid, period = Ansi(period, 6), date, desum = (double)l.desum, cresum = (double)l.cresum }, tx);
            else
                conn.Execute(
                    "UPDATE fincadistr SET cashna=@cna, iodir=@iodir, period=@period, date=@date, desum=@desum, cresum=@cresum WHERE rtrim(cashid)=@cid AND vouchid=@vid AND dpm=@dpm",
                    new { cna = Ansi(l.cashna, 60), iodir = l.iodir, period = Ansi(period, 6), date, desum = (double)l.desum, cresum = (double)l.cresum, cid = Ansi(l.cashid, 15), vid = vouchid, dpm = Ansi(dpm, 10) }, tx);
        }
        conn.Execute("UPDATE finvouch SET cashdis = 1 WHERE vouchid=@vid AND dpm=@dpm", new { vid = vouchid, dpm = Ansi(dpm, 10) }, tx);
        conn.Execute("DELETE FROM fincadistr WHERE ISNULL(desum,0)=0 AND ISNULL(cresum,0)=0 AND dpm=@dpm", new { dpm = Ansi(dpm, 10) }, tx);
        tx.Commit();
        return "";
    }

    /// <summary>工具-汇总到父级（fr_fintool row5：MPALSUM 旧 bug 原样——mpalsum = ypalsum(父) + m.mpalsum）。</summary>
    public static void FinToolSumLegacy(SqlConnection conn, string dpm, int subleng1)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        conn.Execute(
            """
            UPDATE s SET detacode = CASE WHEN EXISTS (
                SELECT 1 FROM finsub c WHERE LEFT(LTRIM(RTRIM(c.subcode)), LEN(LTRIM(RTRIM(s.subcode)))) = LTRIM(RTRIM(s.subcode))
                  AND LEN(LTRIM(RTRIM(c.subcode))) > LEN(LTRIM(RTRIM(s.subcode)))) THEN 0 ELSE 1 END
            FROM finsub s WHERE s.dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) });
        var leaves = conn.Query<(string subcode, decimal ybd, decimal ybc, decimal bd, decimal bc,
            decimal cd, decimal cc, decimal yd, decimal yc, decimal ed, decimal ec, decimal mp, decimal yp)>(
            """
            SELECT rtrim(subcode), ISNULL(ybdesum,0), ISNULL(ybcresum,0), ISNULL(bdesum,0), ISNULL(bcresum,0),
                   ISNULL(cdesum,0), ISNULL(ccresum,0), ISNULL(ydesum,0), ISNULL(ycresum,0), ISNULL(edesum,0), ISNULL(ecresum,0),
                   ISNULL(mpalsum,0), ISNULL(ypalsum,0)
            FROM finsub WITH (NOLOCK) WHERE dpm = @dpm AND ISNULL(detacode,0) = 1 ORDER BY rtrim(subcode)
            """, new { dpm = Ansi(dpm, 10) }).AsList();
        conn.Execute(
            "UPDATE finsub SET ybdesum=0, ybcresum=0, bdesum=0, bcresum=0, cdesum=0, ccresum=0, ydesum=0, ycresum=0, edesum=0, ecresum=0, mpalsum=0, ypalsum=0 WHERE dpm=@dpm",
            new { dpm = Ansi(dpm, 10) });
        foreach (var lf in leaves)
        {
            var i = lf.subcode.Length;
            while (i >= subleng1 && i <= lf.subcode.Length)
            {
                var psub = lf.subcode[..i];
                var found = conn.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @psub", new { psub = Ansi(psub, 18) }) > 0;
                if (found)
                    conn.Execute(
                        """
                        UPDATE finsub SET edesum = CASE WHEN bdesum + @bd - (bcresum + @bc) + cdesum - ccresum >= 0
                                                         THEN bdesum + @bd - (bcresum + @bc) + cdesum - ccresum ELSE 0 END,
                                          ecresum = CASE WHEN bdesum + @bd - (bcresum + @bc) + cdesum - ccresum <= 0
                                                         THEN ABS(bdesum + @bd - (bcresum + @bc) + cdesum - ccresum) ELSE 0 END,
                                          bdesum = bdesum + @bd, bcresum = bcresum + @bc,
                                          ydesum = ydesum + @yd, ycresum = ycresum + @yc,
                                          ybdesum = ybdesum + @ybd, ybcresum = ybcresum + @ybc,
                                          mpalsum = ypalsum + @mp, ypalsum = ypalsum + @yp
                        WHERE rtrim(subcode) = @psub AND dpm = @dpm
                        """,
                        new
                        {
                            bd = (double)lf.bd, bc = (double)lf.bc, yd = (double)lf.yd, yc = (double)lf.yc,
                            ybd = (double)lf.ybd, ybc = (double)lf.ybc, mp = (double)lf.mp, yp = (double)lf.yp,
                            psub = Ansi(psub, 18), dpm = Ansi(dpm, 10),
                        });
                i -= FinSubWidth(i);
            }
        }
    }

    /// <summary>工具-帐簿初始化（fr_fintool row10：八表清空+凭证库清空或重置+finsoursub 恢复/回写）。</summary>
    public static void FinToolInitLegacy(SqlConnection conn, string dpm, bool clearVouch, bool restoreSource)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        foreach (var table in new[] { "finaleger", "finsubit", "finitsub", "finamsum", "finrecout", "finreclist", "fincadistr", "finhisbal" })
            conn.Execute($"DELETE FROM {table} WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
        if (clearVouch)
        {
            conn.Execute("DELETE FROM finvouch WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
            conn.Execute("UPDATE finvid SET vid = 0 WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
        }
        else
            conn.Execute("UPDATE finvouch SET balance=0, dcdir='', post=0, cashdis=0, poster='' WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
        var hasSource = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM (SELECT TOP (1) 1 AS x FROM finsoursub WITH (NOLOCK)) t") > 0;
        if (hasSource && restoreSource)
        {
            conn.Execute("DELETE FROM finsub WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
            try
            {
                conn.Execute("INSERT INTO finsub SELECT * FROM finsoursub WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
            }
            catch
            {
                // 旧系统行为：列数不匹配 INSERT 失败静默继续
            }
        }
        else
        {
            conn.Execute(
                """
                UPDATE a SET a.ybdesum = b.ybdesum, a.ybcresum = b.ybcresum, a.mpalsum = b.mpalsum, a.ypalsum = b.ypalsum,
                             a.bdesum = b.bdesum, a.bcresum = b.bcresum, a.cdesum = 0, a.ccresum = 0,
                             a.ydesum = b.ydesum, a.ycresum = b.ycresum, a.edesum = b.edesum, a.ecresum = b.ecresum
                FROM finsub a, finsoursub b WHERE a.dpm = @dpm AND rtrim(a.subcode) = rtrim(b.subcode)
                """, new { dpm = Ansi(dpm, 10) });
            conn.Execute(
                """
                UPDATE a SET ybdesum = 0, ybcresum = 0, mpalsum = 0, ypalsum = 0, bdesum = 0, bcresum = 0,
                             cdesum = 0, ccresum = 0, ydesum = 0, ycresum = 0, edesum = 0, ecresum = 0
                FROM finsub a WHERE a.dpm = @dpm AND NOT EXISTS (
                    SELECT 1 FROM finsoursub b WHERE b.dpm = @dpm AND rtrim(b.subcode) = rtrim(a.subcode))
                """, new { dpm = Ansi(dpm, 10) });
        }
    }

    /// <summary>固定资产类别/变动方式新增（perassty/perchgty INSERT；两表无 dpm 列）。</summary>
    public static void FinPerAddLegacy(SqlConnection conn, string table, string name, int ty, string subcode, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        if (table == "perassty")
            conn.Execute("INSERT INTO perassty (perasty, deprty, remrate) VALUES (@name, 1, 0)",
                new { name = Ansi(name, 20) });
        else
            conn.Execute("INSERT INTO perchgty (chgna, chgty, subcode, vouword, vnote) VALUES (@name, @ty, @sc, N'', N'')",
                new { name = Ansi(name, 20), ty, sc = Ansi(subcode, 20) });
    }
}
