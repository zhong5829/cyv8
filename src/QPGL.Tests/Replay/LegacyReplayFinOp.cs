using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Finance;

namespace QPGL.Tests.Replay;

/// <summary>
/// 业务凭证（I.2，场景 20）+ G 组 finvouch 联动（场景 21）旧侧回放 —— fr_finoperation row9+myclick(longsky)
/// + fr_finopvouch Audit/Unaudit + fr_xlsk A_.row75 FINVOUCH 块 / c_.row74 联动删除 的 SQL 序列原样重放。
/// 独立于新仓储（FinOpVouchRepository/FinSubCodes）的平行实现，两侧 diff 一致方为契约对齐。
/// </summary>
public static partial class LegacyReplay
{
    // ==================== 种子 ====================

    /// <summary>科目种子（可指定 type/fclient——ADDSUB 按名称+类型查找的前置）。</summary>
    public static void FinSeedSubTyped(SqlConnection conn, string subcode, string subname, string type, string dpm)
        => conn.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM finsub WHERE rtrim(subcode) = @sub)
                INSERT INTO finsub (subcode, subname, type, fdc, fclient, detacode, dpm)
                VALUES (@sub, @name, @type, 'D', 1, 1, @dpm)
            """,
            new { sub = Ansi(subcode, 18), name = Ansi(subname, 40), type = Ansi(type, 1), dpm = Ansi(dpm, 10) });

    /// <summary>BBI 散件入库单种子（tbistoed；cno/type/mark/fkfs/freight 决定生成分支）。</summary>
    public static void FinSeedBbiDoc(SqlConnection conn, string dept, string per, string cno,
        string gyname, string fkfs, decimal qty, decimal iprc, decimal freight, string type = "入库", string mark = "已确认")
    {
        conn.Execute(
            """
            INSERT INTO tbistoed (cp, dpm, nno, na1, ty, unit, jkamount, kcamount, iprj, iprc, oprc, indate,
                                  cno, seano, isord, pno, gyname, per, type, mark, fkfs, freight)
            VALUES (N'回放', @dpm, N'REPLAYBBI', N'回放入库配件R', N'', N'', @qty, 0, @iprc, @iprc, @iprc, @indate,
                    @cno, N'', N'N', N'', @gyname, @per, @type, @mark, @fkfs, @freight)
            """,
            new
            {
                dpm = Ansi(dept, 10), qty, iprc, freight, indate = DateTime.Today,
                cno = Ansi(cno, 15), gyname = Ansi(gyname, 60), per = Ansi(per, 10),
                type = Ansi(type, 10), mark = Ansi(mark, 10), fkfs = Ansi(fkfs, 10),
            });
    }

    /// <summary>SRO/SMO 出库单种子（tbsada；paper/guno/yhl 决定生成分支）。</summary>
    public static void FinSeedSroDoc(SqlConnection conn, string dept, string per, string paper,
        string gname, string guno, decimal qty, decimal oprct, decimal iprc, decimal yhl,
        string type = "销售", string mark = "Y")
    {
        conn.Execute(
            """
            INSERT INTO tbsada (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc,
                                paper, gno, gname, per, outdate, dpm, depotno, kcid, mark, guno, yhl, remark, jzdate)
            VALUES (N'RE', N'REPLAYSRO', N'回放销售配件R', N'', N'回放车型', N'个', @type, N'', @qty, @oprct, @oprct, @iprc, @iprc,
                    @paper, N'', @gname, @per, @outdate, @dpm, N'', 0, @mark, @guno, @yhl, '', '1900-01-01')
            """,
            new
            {
                type = Ansi(type, 10), qty, oprct, iprc, yhl, paper = Ansi(paper, 15),
                gname = Ansi(gname, 60), per = Ansi(per, 10), outdate = DateTime.Today,
                dpm = Ansi(dept, 10), mark = Ansi(mark, 10), guno = Ansi(guno, 10),
            });
    }

    // ==================== 旧侧 ADDSUB / GSUBNAME（pg_proc 独立回放） ====================

    /// <summary>ADDSUB（pg_proc L2447 原样回放；tx 供联动事务内调用）。</summary>
    public static string FinOpAddSubLegacy(SqlConnection conn, string client, string cty, string dpm, SqlTransaction? tx = null)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        client = client.Trim();
        var sys = conn.QuerySingle<(string acc, string pay)>(
            "SELECT RTRIM(ISNULL(accsub,'')), RTRIM(ISNULL(paysub,'')) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, tx);
        var sub0 = (cty == "A" ? sys.acc : sys.pay).Trim();
        // 主科目兜底
        if (conn.ExecuteScalar<int>("SELECT COUNT(1) FROM finsub WHERE rtrim(subcode) = @s0",
                new { s0 = Ansi(sub0, 18) }, tx) == 0)
            conn.Execute(
                "INSERT INTO finsub (subcode, subname, type, fdc, detacode, dpm) VALUES (@s0, @n, @cty, @fdc, 0, @dpm)",
                new { s0 = Ansi(sub0, 18), n = Ansi(cty == "A" ? "应收帐款" : "应付帐款", 40), cty = Ansi(cty, 1), fdc = Ansi(cty == "A" ? "D" : "C", 1), dpm = Ansi(dpm, 10) }, tx);
        // subname+type 查找（SET ORDER subcode → 第一行）
        var found = conn.ExecuteScalar<string?>(
            "SELECT TOP (1) rtrim(subcode) FROM finsub WHERE rtrim(subname) = @c AND rtrim(ISNULL(type,'')) = @cty ORDER BY rtrim(subcode) COLLATE Latin1_General_BIN2",
            new { c = Ansi(client, 60), cty = Ansi(cty, 1) }, tx);
        if (!string.IsNullOrEmpty(found)) return found;
        // 子码窗口 LEN=累计位宽（GSUBLENG(len0,2)=SUBLENG2=6）
        var childLen = sub0.Length == 3 ? 6 : 15;
        var maxCode = conn.ExecuteScalar<string?>(
            "SELECT MAX(rtrim(subcode) COLLATE Latin1_General_BIN2) FROM finsub WHERE LEFT(rtrim(subcode), @l0) = @s0 AND LEN(rtrim(subcode)) = @cl",
            new { l0 = sub0.Length, s0 = Ansi(sub0, 18), cl = childLen }, tx);
        string scode0;
        if (maxCode is null) scode0 = "001";
        else if (maxCode.EndsWith("`99", StringComparison.Ordinal))
        {
            var pool = conn.ExecuteScalar<string?>(
                "SELECT TOP (1) rtrim(subcode) FROM finsub_auto WHERE rtrim(subcode) NOT IN (SELECT rtrim(subcode) FROM finsub WHERE dpm = @dpm) AND rtrim(ISNULL(type,'')) = @cty",
                new { dpm = Ansi(dpm, 10), cty = Ansi(cty, 1) }, tx);
            if (string.IsNullOrEmpty(pool)) throw new InvalidOperationException("无可使用科目代码！");
            return pool;
        }
        else if (maxCode[^3..] == "999") scode0 = "A00";
        else
        {
            var c4 = maxCode.Length >= 4 ? maxCode[3] : '\0';
            if (c4 is >= 'A' and <= '`')
            {
                var s2 = maxCode.Length >= 6 ? maxCode[4..6] : "";
                var sv = s2.Length == 2 && s2.All(char.IsDigit) ? int.Parse(s2) : 0;
                scode0 = sv + 1 > 99 ? (char)(c4 + 1) + "00" : c4 + (sv + 1).ToString("00");
            }
            else
            {
                var r3 = maxCode[^3..];
                scode0 = ((r3.All(char.IsDigit) ? int.Parse(r3) : 0) + 1).ToString("000");   // TRANSFORM(VAL+1,'@ZL 999')
            }
        }
        var retcode = sub0 + scode0;
        conn.Execute(
            "INSERT INTO finsub (subcode, subname, type, fdc, fclient, detacode, dpm) VALUES (@sc, @c, @cty, @fdc, 1, 1, @dpm)",
            new { sc = Ansi(retcode, 18), c = Ansi(client, 60), cty = Ansi(cty, 1), fdc = Ansi(cty == "A" ? "D" : "C", 1), dpm = Ansi(dpm, 10) }, tx);
        return retcode;
    }

    /// <summary>GSUBNAME（gsubname 原样回放：子码序扫描 + 级次推进）。</summary>
    public static string FinOpGSubNameLegacy(SqlConnection conn, string subid, SqlTransaction? tx = null)
    {
        subid = subid.Trim();
        if (subid.Length == 0) return "";
        var rows = conn.Query<(string sc, string sn)>(
            "SELECT rtrim(subcode), rtrim(subname) FROM finsub WITH (NOLOCK) WHERE LEFT(rtrim(subcode), 3) = @p3 ORDER BY rtrim(subcode) COLLATE Latin1_General_BIN2",
            new { p3 = Ansi(subid[..Math.Min(3, subid.Length)], 18) }, tx).AsList();
        int[] cum = [3, 6, 9, 11, 13, 15];
        static int NextCum(int n, int[] cum)
        {
            for (var i = 0; i < cum.Length; i++)
                if (cum[i] == n) return i + 1 < cum.Length ? cum[i + 1] : cum[^1];
            return cum[^1];
        }
        var n = 3;
        var ret = "";
        foreach (var r in rows)
        {
            if (n > subid.Length) continue;
            var left = r.sc.Length >= n ? r.sc[..n] : r.sc;
            if (left == subid[..n])
            {
                ret += (n == 3 ? "" : " - ") + r.sn;
                n = NextCum(n, cum);
            }
        }
        return ret;
    }

    private static bool LegacyFcash(SqlConnection conn, string subcode)
        => conn.ExecuteScalar<bool>(
            "SELECT TOP (1) ISNULL(fcash,0) FROM finsub WHERE rtrim(subcode) = @sc ORDER BY id",
            new { sc = Ansi(subcode.Trim(), 18) });

    private static string LegacySubcodeAuto(SqlConnection conn, string subname)
        => conn.ExecuteScalar<string?>(
            "SELECT TOP (1) rtrim(subcode) FROM finsub_auto WHERE rtrim(subname) = @n ORDER BY rtrim(subcode) COLLATE Latin1_General_BIN2",
            new { n = Ansi(subname, 40) }) ?? "";

    // ==================== 业务凭证生成（fr_finoperation row9 + myclick longsky） ====================

    private sealed record LegacyBbiRow(int Id, string Cno, DateTime Indate, string Gyname, string Fkfs,
        string Pno, string Per, string Depotno, decimal Jkamount, decimal Iprc, decimal Freight);

    /// <summary>BBI 生成回放（Fvtmp 查询 + 分组 + 科目分派 + 已入账检测；仅 ADDSUB 落库）。</summary>
    public static List<FinOpVouchGroup> FinOpGenerateBbiLegacy(SqlConnection conn, string dpm,
        DateTime? bedate, DateTime? endate, string bpaper, string epaper, int payOption,
        string cashSub, string balaSub)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var where = "rtrim(cno) <> '' AND (rtrim(type) = N'历史' OR rtrim(mark) = N'已确认')";
        var p = new DynamicParameters();
        if (bedate.HasValue) { where += " AND indate >= @bd"; p.Add("@bd", bedate.Value.Date); }
        if (endate.HasValue) { where += " AND indate <= @ed"; p.Add("@ed", endate.Value.Date); }
        if (!string.IsNullOrWhiteSpace(bpaper)) { where += " AND rtrim(cno) >= @bp"; p.Add("@bp", Ansi(bpaper, 15)); }
        if (!string.IsNullOrWhiteSpace(epaper)) { where += " AND rtrim(cno) <= @ep"; p.Add("@ep", Ansi(epaper, 15)); }
        where += payOption switch
        {
            1 => " AND rtrim(fkfs) = N'现金'",
            2 => " AND rtrim(fkfs) IN (N'支票', N'汇票', N'汇款')",
            3 => " AND rtrim(fkfs) IN (N'挂帐', N'代销', N'托收', N'其它')",
            _ => "",
        };
        var rows = conn.Query<LegacyBbiRow>(
            $"""
            SELECT id, rtrim(cno) cno, indate, rtrim(ISNULL(gyname,'')) gyname, rtrim(ISNULL(fkfs,'')) fkfs,
                   rtrim(ISNULL(pno,'')) pno, rtrim(ISNULL(per,'')) per, rtrim(ISNULL(depotno,'')) depotno,
                   ISNULL(jkamount,0) jkamount, ISNULL(iprc,0) iprc, ISNULL(freight,0) freight
            FROM tbistoed WITH (NOLOCK) WHERE {where}
            ORDER BY rtrim(cno) COLLATE Latin1_General_BIN2, id
            """, p).AsList();

        var baseVid = conn.ExecuteScalar<int>("SELECT vid FROM finvid WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
        var result = new List<FinOpVouchGroup>();
        var fcolor = false;
        var previewVid = baseVid;
        foreach (var g in rows.GroupBy(r => r.Cno).Where(g => g.Any(r => r.Gyname.Trim().Length > 0))
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            fcolor = !fcolor;
            var head = g.First();
            var sum0 = g.Sum(r => r.Jkamount * r.Iprc);
            var sumfre = g.Sum(r => (r.Iprc - (r.Freight == 0 ? r.Iprc : r.Freight)) * r.Jkamount);
            var fkfs0 = head.Fkfs.Trim();
            string subcode, type, vouword;
            if ("现金".Contains(fkfs0)) { subcode = cashSub.Trim(); type = "C"; vouword = "付"; }
            else if ("支票汇票汇款刷卡".Contains(fkfs0)) { subcode = balaSub.Trim(); type = "B"; vouword = "付"; }
            else { subcode = FinOpAddSubLegacy(conn, head.Gyname, "P", dpm); type = "P"; vouword = "转"; }
            var subname = FinOpGSubNameLegacy(conn, subcode);
            var fcash0 = LegacyFcash(conn, subcode);

            var existing = conn.Query<FinOpRow>(
                """
                SELECT id, vouchid, vcode, date, rtrim(period) period, recoid, rtrim(ISNULL(note,'')) note,
                       rtrim(subcode) subcode, rtrim(subname) subname, rtrim(ISNULL(type,'')) type, ISNULL(fcash,0) fcash,
                       ISNULL(debit,0) debit, ISNULL(credit,0) credit, rtrim(vouword) vouword,
                       rtrim(ISNULL(client,'')) client, rtrim(ISNULL(invtype,'')) invtype, rtrim(ISNULL(invcode,'')) invcode,
                       rtrim(ISNULL(opertype,'')) opertype, rtrim(ISNULL(opercode,'')) opercode,
                       ISNULL(audit,0) audit, ISNULL(post,0) post, rtrim(ISNULL(maker,'')) maker,
                       ISNULL(fcolor,0) fcolor, rtrim(ISNULL(depotno,'')) depotno
                FROM finvouch WITH (NOLOCK) WHERE rtrim(opercode) = @oc AND rtrim(ISNULL(opertype,'')) = 'BBI'
                ORDER BY id
                """, new { oc = Ansi(head.Cno.Trim(), 30) }).AsList();

            var group = new FinOpVouchGroup
            {
                Opertype = "BBI", Opercode = head.Cno.Trim(), Date = head.Indate.Date,
                Period = head.Indate.ToString("yyyyMM"), Client = head.Gyname.Trim(),
                Note = $"第-{head.Cno.Trim()}-号采购入库", Maker = head.Per.Trim(),
                Depotno = head.Depotno.Trim(), Fcolor = fcolor,
            };
            if (existing.Count > 0)
            {
                group.Audited = true;
                group.Vouchid = existing[0].Vouchid;
                group.Vcode = existing[0].Vcode;
                foreach (var r in existing) { r.Fcolor = fcolor; group.Lines.Add(r); }
            }
            else
            {
                previewVid++;
                group.Vouchid = previewVid;
                FinOpRow NewRow(int recoid, string sc, string sn, decimal debit, decimal credit, string ty, bool fc) => new()
                {
                    Vouchid = previewVid, Date = head.Indate.Date, Period = head.Indate.ToString("yyyyMM"),
                    Recoid = recoid, Note = group.Note, Subcode = sc, Subname = sn,
                    Debit = debit, Credit = credit, Type = ty, Vouword = vouword, Fcash = fc,
                    Client = group.Client, Invcode = head.Pno.Trim(), Opertype = "BBI",
                    Opercode = group.Opercode, Maker = group.Maker, Fcolor = fcolor, Depotno = group.Depotno,
                };
                group.Lines.Add(NewRow(1, "135001", "库存商品 - 配件入库", sum0, 0, "", false));
                group.Lines.Add(NewRow(2, subcode, subname, 0, sum0 - sumfre, type, fcash0));
                if (sumfre != 0)
                {
                    var yf = LegacySubcodeAuto(conn, "运费Y");
                    group.Lines.Add(NewRow(2, yf, yf.Length == 0 ? "" : FinOpGSubNameLegacy(conn, yf), 0, sumfre, "", false));
                }
            }
            result.Add(group);
        }
        return result;
    }

    private sealed record LegacySroRow(int Id, string Paper, DateTime Outdate, string Gname, string Guno,
        string Pno, string Per, string Depotno, decimal Ckamount, decimal Oprct, decimal Iprc, decimal Yhl);

    /// <summary>SRO/SMO 生成回放（longsky_sro；VOU_TYPE=3 含成本对；无折扣行）。</summary>
    public static List<FinOpVouchGroup> FinOpGenerateSroLegacy(SqlConnection conn, string dpm, string opty,
        DateTime? bedate, DateTime? endate, string bpaper, string epaper, int payOption,
        string cashSub, string balaSub, bool includeCost = true)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var where = opty == "SMO"
            ? "rtrim(type) = N'调拨' AND rtrim(mark) <> ''"
            : "(rtrim(type) = N'历史' OR (rtrim(type) = N'销售' AND rtrim(mark) <> ''))";
        var p = new DynamicParameters();
        if (bedate.HasValue) { where += " AND outdate >= @bd"; p.Add("@bd", bedate.Value.Date); }
        if (endate.HasValue) { where += " AND outdate <= @ed"; p.Add("@ed", endate.Value.Date); }
        if (!string.IsNullOrWhiteSpace(bpaper)) { where += " AND rtrim(paper) >= @bp"; p.Add("@bp", Ansi(bpaper, 15)); }
        if (!string.IsNullOrWhiteSpace(epaper)) { where += " AND rtrim(paper) <= @ep"; p.Add("@ep", Ansi(epaper, 15)); }
        where += payOption switch
        {
            1 => " AND rtrim(guno) = N'现金'",
            2 => " AND rtrim(guno) IN (N'支票', N'汇票', N'汇款')",
            3 => " AND rtrim(guno) IN (N'挂帐', N'代销', N'托收', N'其它')",
            _ => "",
        };
        var rows = conn.Query<LegacySroRow>(
            $"""
            SELECT id, rtrim(paper) paper, outdate, rtrim(ISNULL(gname,'')) gname, rtrim(ISNULL(guno,'')) guno,
                   rtrim(ISNULL(pno,'')) pno, rtrim(ISNULL(per,'')) per, rtrim(ISNULL(depotno,'')) depotno,
                   ISNULL(ckamount,0) ckamount, ISNULL(oprct,0) oprct, ISNULL(iprc,0) iprc, ISNULL(yhl,0) yhl
            FROM tbsada WITH (NOLOCK) WHERE {where}
            ORDER BY rtrim(paper) COLLATE Latin1_General_BIN2, id
            """, p).AsList();

        var baseVid = conn.ExecuteScalar<int>("SELECT vid FROM finvid WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) });
        var result = new List<FinOpVouchGroup>();
        var fcolor = false;
        var previewVid = baseVid;
        foreach (var g in rows.GroupBy(r => r.Paper).Where(g => g.Any(r => r.Gname.Trim().Length > 0))
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            fcolor = !fcolor;
            var head = g.First();
            var cost0 = g.Sum(r => r.Ckamount * r.Iprc);
            var sum0 = g.Sum(r => r.Ckamount * r.Oprct * (100 - r.Yhl) / 100);   // GETJE P_XSJEGS='1' 恒等
            var guno0 = head.Guno.Trim();
            string subcode, type, vouword;
            if ("现金".Contains(guno0)) { subcode = cashSub.Trim(); type = "C"; vouword = "收"; }
            else if ("支票汇票汇款刷卡".Contains(guno0)) { subcode = balaSub.Trim(); type = "B"; vouword = "收"; }
            else { subcode = FinOpAddSubLegacy(conn, head.Gname, "A", dpm); type = "A"; vouword = "转"; }
            var subname = FinOpGSubNameLegacy(conn, subcode);
            var fcash0 = LegacyFcash(conn, subcode);

            var existing = conn.Query<FinOpRow>(
                """
                SELECT id, vouchid, vcode, date, rtrim(period) period, recoid, rtrim(ISNULL(note,'')) note,
                       rtrim(subcode) subcode, rtrim(subname) subname, rtrim(ISNULL(type,'')) type, ISNULL(fcash,0) fcash,
                       ISNULL(debit,0) debit, ISNULL(credit,0) credit, rtrim(vouword) vouword,
                       rtrim(ISNULL(client,'')) client, rtrim(ISNULL(invtype,'')) invtype, rtrim(ISNULL(invcode,'')) invcode,
                       rtrim(ISNULL(opertype,'')) opertype, rtrim(ISNULL(opercode,'')) opercode,
                       ISNULL(audit,0) audit, ISNULL(post,0) post, rtrim(ISNULL(maker,'')) maker,
                       ISNULL(fcolor,0) fcolor, rtrim(ISNULL(depotno,'')) depotno
                FROM finvouch WITH (NOLOCK) WHERE rtrim(opercode) = @oc AND rtrim(ISNULL(opertype,'')) = @ot
                ORDER BY id
                """, new { oc = Ansi(head.Paper.Trim(), 30), ot = Ansi(opty, 8) }).AsList();

            var group = new FinOpVouchGroup
            {
                Opertype = opty, Opercode = head.Paper.Trim(), Date = head.Outdate.Date,
                Period = head.Outdate.ToString("yyyyMM"), Client = head.Gname.Trim(),
                Note = $"第-{head.Paper.Trim()}-号销售开单", Maker = head.Per.Trim(),
                Depotno = head.Depotno.Trim(), Fcolor = fcolor,
            };
            if (existing.Count > 0)
            {
                group.Audited = true;
                group.Vouchid = existing[0].Vouchid;
                group.Vcode = existing[0].Vcode;
                foreach (var r in existing) { r.Fcolor = fcolor; group.Lines.Add(r); }
            }
            else
            {
                previewVid++;
                group.Vouchid = previewVid;
                FinOpRow NewRow(int recoid, string sc, string sn, decimal debit, decimal credit, string ty, bool fc) => new()
                {
                    Vouchid = previewVid, Date = head.Outdate.Date, Period = head.Outdate.ToString("yyyyMM"),
                    Recoid = recoid, Note = group.Note, Subcode = sc, Subname = sn,
                    Debit = debit, Credit = credit, Type = ty, Vouword = vouword, Fcash = fc,
                    Client = group.Client, Invtype = head.Pno.Trim(), Opertype = opty,
                    Opercode = group.Opercode, Maker = group.Maker, Fcolor = fcolor, Depotno = group.Depotno,
                };
                group.Lines.Add(NewRow(1, subcode, subname, sum0, 0, type, fcash0));
                if (includeCost)
                {
                    group.Lines.Add(NewRow(2, "511002", "商品销售成本 - 配件销售成本", cost0, 0, "", false));
                    group.Lines.Add(NewRow(3, "135002", "库存商品 - 配件销售", 0, cost0, "", false));
                }
                group.Lines.Add(NewRow(includeCost ? 4 : 2, "501002", "商品销售收入 - 配件销售收入", 0, sum0, "", false));
            }
            result.Add(group);
        }
        return result;
    }

    // ==================== 业务凭证审核 / 反审核（fr_finopvouch row2） ====================

    /// <summary>审核（Audit）：finvid(dpm) 推进 → vcode=max+1 → INSERT finvouch(audit=1) → opaudit 回写。</summary>
    public static (int vouchid, int vcode) FinOpAuditLegacy(SqlConnection conn, FinOpVouchGroup group,
        string dpm, string per)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            conn.Execute("UPDATE finvid SET vid = vid + 1 WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, tx);
            var vouchid = conn.ExecuteScalar<int>("SELECT vid FROM finvid WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, tx);
            var vcode = conn.ExecuteScalar<int>(
                "SELECT ISNULL(MAX(vcode), 0) + 1 FROM finvouch WITH (NOLOCK) WHERE period = @p",
                new { p = Ansi(group.Period, 6) }, tx);
            foreach (var l in group.Lines)
            {
                conn.Execute(
                    """
                    INSERT INTO finvouch (vouchid, vcode, date, period, recoid, note, subcode, subname, type, fcash,
                                          debit, credit, vouword, client, invtype, invcode, opertype, opercode,
                                          audit, maker, auditer, fcolor, item, depotno, dpm)
                    VALUES (@vouchid, @vcode, @date, @period, @recoid, @note, @subcode, @subname, @type, @fcash,
                            @debit, @credit, @vouword, @client, @invtype, @invcode, @opertype, @opercode,
                            1, @maker, @per, @fcolor, '', @depotno, @dpm)
                    """,
                    new
                    {
                        vouchid, vcode, date = group.Date, period = Ansi(group.Period, 6), l.Recoid,
                        note = Ansi(l.Note, 60), subcode = Ansi(l.Subcode, 18), subname = Ansi(l.Subname, 40),
                        type = Ansi(l.Type, 1), l.Fcash, l.Debit, l.Credit, vouword = Ansi(l.Vouword, 4),
                        client = Ansi(l.Client, 40), invtype = Ansi(l.Invtype, 20), invcode = Ansi(l.Invcode, 20),
                        opertype = Ansi(l.Opertype, 8), opercode = Ansi(l.Opercode, 30),
                        maker = Ansi(group.Maker, 10), per = Ansi(per, 10), l.Fcolor,
                        depotno = Ansi(l.Depotno, 10), dpm = Ansi(dpm, 10),
                    }, tx);
            }
            // opaudit（fingenvoustyle=.F. 分支）
            var bizSql = group.Opertype == "BBI"
                ? "UPDATE tbistoed SET audit = 1 WHERE rtrim(cno) = @oc AND dpm = @dpm"
                : "UPDATE tbsada SET audit = 1 WHERE paper = @oc AND dpm = @dpm";
            conn.Execute(bizSql, new { oc = Ansi(group.Opercode, 30), dpm = Ansi(dpm, 10) }, tx);
            tx.Commit();
            return (vouchid, vcode);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>反审核（Unaudit）：DELETE finvouch WHERE vouchid + opaudit 回写 0。</summary>
    public static void FinOpUnauditLegacy(SqlConnection conn, int vouchid, string opty, string opercode, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            conn.Execute("DELETE FROM finvouch WHERE vouchid = @vid", new { vid = vouchid }, tx);
            var bizSql = opty == "BBI"
                ? "UPDATE tbistoed SET audit = 0 WHERE rtrim(cno) = @oc AND dpm = @dpm"
                : "UPDATE tbsada SET audit = 0 WHERE paper = @oc AND dpm = @dpm";
            conn.Execute(bizSql, new { oc = Ansi(opercode, 30), dpm = Ansi(dpm, 10) }, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ==================== G 组 finvouch 联动（fr_xlsk A_.row75 L190-237 / c_.row74） ====================

    /// <summary>收款/付款确认 finvouch 两行写入（FINVOUCH 块原样；finvid 全行推进无 WHERE）。</summary>
    public static void XlskFinVouchWriteLegacy(SqlConnection conn, string skno, string type, string gname,
        string fkfs, DateTime date, string pno, decimal bcje, string dpm, string per,
        (string subcode, string subname, string type, bool fcash) skqx)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            conn.Execute("UPDATE finvid SET vid = vid + 1", new { }, tx);   // SPT 无 WHERE，旧码原样
            var vouchid = conn.ExecuteScalar<int>(
                "SELECT TOP (1) vid FROM finvid WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, tx);
            var period = date.ToString("yyyyMM");
            var vcode = conn.ExecuteScalar<int>(
                "SELECT ISNULL(MAX(vcode), 0) + 1 FROM finvouch WITH (NOLOCK) WHERE period = @p",
                new { p = Ansi(period, 6) }, tx);
            var note = $"第-{skno}-号{type}";
            // 行 1：收付去向科目行
            conn.Execute(
                """
                INSERT INTO finvouch (vouchid, vcode, date, period, recoid, note, subcode, subname, type,
                                      paytype, paydate, payno, fcash, debit, credit, dcdir, vouword, client,
                                      invcode, opertype, opercode, maker, dpm)
                VALUES (@vouchid, @vcode, @date, @period, 1, @note, @subcode, @subname, @type,
                        @paytype, @paydate, @payno, @fcash, @debit, @credit, @dcdir, @vouword, @client,
                        @invcode, '', '', @maker, @dpm)
                """,
                new
                {
                    vouchid, vcode, date, period = Ansi(period, 6), note = Ansi(note, 60),
                    subcode = Ansi(skqx.subcode, 18), subname = Ansi(skqx.subname, 40), type = Ansi(skqx.type, 1),
                    paytype = Ansi(fkfs, 10), paydate = date, payno = Ansi(skno, 30), fcash = skqx.fcash,
                    debit = type == "收款" ? bcje : 0m, credit = type == "付款" ? bcje : 0m,
                    dcdir = Ansi(type == "收款" ? "借" : "贷", 2), vouword = Ansi(type, 8),
                    client = Ansi(gname, 60), invcode = Ansi(pno.Trim(), 20), maker = Ansi(per, 10), dpm = Ansi(dpm, 10),
                }, tx);
            // 行 2：往来科目行（ADDSUB）
            var cty = type == "收款" ? "A" : "P";
            var subcode = FinOpAddSubLegacy(conn, gname, cty, dpm, tx);
            var subname = FinOpGSubNameLegacy(conn, subcode, tx);
            conn.Execute(
                """
                INSERT INTO finvouch (vouchid, vcode, date, period, recoid, note, subcode, subname, type,
                                      paytype, paydate, payno, fcash, debit, credit, dcdir, vouword, client,
                                      opertype, opercode, maker, dpm)
                VALUES (@vouchid, @vcode, @date, @period, 2, @note, @subcode, @subname, @type,
                        @paytype, @paydate, @payno, 0, @debit, @credit, @dcdir, @vouword, @client,
                        '', '', @maker, @dpm)
                """,
                new
                {
                    vouchid, vcode, date, period = Ansi(period, 6), note = Ansi(note, 60),
                    subcode = Ansi(subcode, 18), subname = Ansi(subname, 40), type = Ansi(cty, 1),
                    paytype = Ansi(fkfs, 10), paydate = date, payno = Ansi(skno, 30),
                    debit = type == "付款" ? bcje : 0m, credit = type == "收款" ? bcje : 0m,
                    dcdir = Ansi(type == "收款" ? "贷" : "借", 2), vouword = Ansi(type, 8),
                    client = Ansi(gname, 60), maker = Ansi(per, 10), dpm = Ansi(dpm, 10),
                }, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>还原联动守卫（c_.row74 L36-44）：该单生成的凭证已审核 → 拒绝。</summary>
    public static bool XlskFinVouchRestoreBlockedLegacy(SqlConnection conn, string skno)
        => conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM finvouch WITH (NOLOCK) WHERE payno = @cno AND audit = 1",
            new { cno = Ansi(skno, 30) }) > 0;

    /// <summary>还原联动删除（c_.row74 L115-116）：DELETE finvouch WHERE payno=单号。</summary>
    public static void XlskFinVouchRestoreLegacy(SqlConnection conn, string skno)
        => conn.Execute("DELETE FROM finvouch WHERE payno = @cno", new { cno = Ansi(skno, 30) });
}
