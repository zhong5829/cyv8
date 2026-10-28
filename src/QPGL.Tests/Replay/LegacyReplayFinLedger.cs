using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace QPGL.Tests.Replay;

/// <summary>
/// 账务第八批（五账/应收应付/出纳）旧侧查询重放 —— 契约：开发文档/契约摘录/账务第八批契约.md。
/// VFP 游标逻辑的 SQL/C# 等价重演；结果序列化后与新仓储逐行比对。
/// </summary>
public static partial class LegacyReplay
{
    /// <summary>明细账（fr_findetail U_.row15 语义）：期初 + 滚动余额 + 对方科目串 + 合计。</summary>
    public static List<string> FinAledetLegacy(SqlConnection conn, string bPeriod, string ePeriod, string scode, bool includeUnposted)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var postCond = includeUnposted ? "" : " AND post = 1";
        var lines = conn.Query<(int vouchid, string subcode, string subname, decimal debit, decimal credit,
            string dcdir, decimal balance, DateTime? date, string vouword, int vcode, string opertype, string opercode,
            string note, string dept0)>(
            $"""
            SELECT vouchid, rtrim(subcode), rtrim(subname), ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dcdir,'')),
                   ISNULL(balance,0), date, rtrim(vouword), ISNULL(vcode,0), ISNULL(rtrim(opertype),''), rtrim(ISNULL(opercode,'')),
                   rtrim(ISNULL(note,'')), rtrim(ISNULL(dept,''))
            FROM finvouch WITH (NOLOCK)
            WHERE period >= @bp AND period <= @ep AND Left(rtrim(subcode), LEN(@sc)) = @sc{postCond}
            ORDER BY vouchid, recoid
            """, new { bp = Ansi(bPeriod, 6), ep = Ansi(ePeriod, 6), sc = Ansi(scode, 18) }).AsList();
        var allRows = conn.Query<(int vouchid, string subcode, string subname, decimal debit, decimal credit, string dept0)>(
            "SELECT vouchid, rtrim(subcode), rtrim(subname), ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dept,'')) " +
            "FROM finvouch WITH (NOLOCK) WHERE vouchid IN @vids",
            new { vids = lines.Select(l => l.vouchid).Distinct().ToList() }).AsList();
        var byVouchid = allRows.GroupBy(r => r.vouchid).ToDictionary(g => g.Key, g => g.AsList());
        var result = new List<string>();
        // 期初行
        var open = conn.QueryFirstOrDefault<(string dcdir, decimal balance)>(
            "SELECT TOP (1) rtrim(ISNULL(dcdir,'')), ISNULL(balance,0) FROM finaleger WITH (NOLOCK) " +
            "WHERE period = @bp AND rtrim(subcode) = @sc AND recoid = '1'",
            new { bp = Ansi(bPeriod, 6), sc = Ansi(scode, 18) });
        var sBal = 0m;
        if (open.dcdir != null)
        {
            sBal = (open.dcdir == "贷" ? -1 : 1) * open.balance;
            result.Add($"1|{bPeriod[..4]}-{bPeriod[4..]}-01|期初余额|0/0|{open.dcdir}|{open.balance:0.##}");
        }
        else
        {
            result.Add($"1|{bPeriod[..4]}-{bPeriod[4..]}-01|期初余额|0/0|平|0");
        }
        // 逐分录滚动
        foreach (var l in lines)
        {
            sBal += l.debit - l.credit;
            var resub = "";
            if (byVouchid.TryGetValue(l.vouchid, out var peers))
                foreach (var q in peers)
                {
                    var isCounter = l.debit == 0 ? q.credit == 0 : q.debit == 0;
                    if (!isCounter) continue;
                    var deptPart = string.IsNullOrEmpty(l.dept0) && string.IsNullOrEmpty(q.dept0)
                        ? "" : "|" + (string.IsNullOrEmpty(l.dept0) ? q.dept0 : l.dept0);
                    var seg = q.subcode + ":" + q.subname + deptPart;
                    resub = resub.Length == 0 ? seg : resub + ";" + seg;
                }
            result.Add($"2|{l.date:yyyy-MM-dd}|{l.vouword}-{l.vcode}|{l.debit:0.##}/{l.credit:0.##}|" +
                       $"{(sBal > 0 ? "借" : sBal < 0 ? "贷" : "平")}|{Math.Abs(sBal):0.##}|{l.subcode}||{resub}");
        }
        // 本期合计（SUM 重算）
        var de = lines.Sum(x => x.debit);
        var cr = lines.Sum(x => x.credit);
        var eEnd = ePeriod[..4] + "-" + ePeriod[4..] + "-01";
        var eDate = DateTime.Parse(eEnd).AddMonths(1).AddDays(-1);
        result.Add($"99|{eDate:yyyy-MM-dd}|本期合计|{de:0.##}/{cr:0.##}|" +
                   $"{(sBal > 0 ? "借" : sBal < 0 ? "贷" : "平")}|{Math.Abs(sBal):0.##}");
        return result;
    }

    private static string resubX(this string _, string resub) => "";

    /// <summary>应收应付帐（fr_finysyf FIND 语义）：六集合序列化。</summary>
    public static List<string> FinYsyfLegacy(SqlConnection conn, DateTime sDate, DateTime eDate, int isDz, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var result = new List<string>();
        var ctlPeriod = conn.ExecuteScalar<string>(
            "SELECT RTRIM(period) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }) ?? "";
        var sPeriod = sDate.ToString("yyyyMM");
        var mPeriod = string.CompareOrdinal(sPeriod, ctlPeriod) > 0 ? sPeriod : ctlPeriod;
        // 科目集
        List<(string subcode, string subname, string type, decimal bd, decimal bc, decimal ed, decimal ec)> subs;
        if (mPeriod == ctlPeriod)
            subs = conn.Query<(string, string, string, decimal, decimal, decimal, decimal)>(
                "SELECT rtrim(a.subcode), rtrim(a.subname), rtrim(ISNULL(a.type,'')), ISNULL(a.bdesum,0), ISNULL(a.bcresum,0), ISNULL(a.edesum,0), ISNULL(a.ecresum,0) " +
                "FROM finsub a WITH (NOLOCK) WHERE a.dpm = @dpm AND (a.type='A' OR a.type='P') AND ISNULL(a.detacode,0)=1 ORDER BY rtrim(a.subname)",
                new { dpm = Ansi(dpm, 10) }).AsList();
        else
        {
            subs = conn.Query<(string, string, string, decimal, decimal, decimal, decimal)>(
                "SELECT rtrim(a.subcode), rtrim(a.subname), rtrim(ISNULL(a.type,'')), ISNULL(b.bdesum,0), ISNULL(b.bcresum,0), ISNULL(a.edesum,0), ISNULL(a.ecresum,0) " +
                "FROM finsub a WITH (NOLOCK) LEFT JOIN finhisbal b WITH (NOLOCK) ON rtrim(a.subcode)=rtrim(b.subcode) " +
                "WHERE a.dpm=@dpm AND b.dpm=@dpm AND (a.type='A' OR a.type='P') AND ISNULL(a.detacode,0)=1 ORDER BY rtrim(a.subname)",
                new { dpm = Ansi(dpm, 10) }).AsList();
            if (subs.Count == 0)
                subs = conn.Query<(string, string, string, decimal, decimal, decimal, decimal)>(
                    "SELECT rtrim(a.subcode), rtrim(a.subname), rtrim(ISNULL(a.type,'')), ISNULL(b.bdesum,0), ISNULL(b.bcresum,0), ISNULL(a.edesum,0), ISNULL(a.ecresum,0) " +
                    "FROM finsub a WITH (NOLOCK) LEFT JOIN finsoursub b WITH (NOLOCK) ON rtrim(a.subcode)=rtrim(b.subcode) " +
                    "WHERE a.dpm=@dpm AND b.dpm=@dpm AND (a.type='A' OR a.type='P') AND ISNULL(a.detacode,0)=1 ORDER BY rtrim(a.subname)",
                    new { dpm = Ansi(dpm, 10) }).AsList();
        }
        // S_DATE 非 1 日：期初调整
        if (sDate.Day != 1)
        {
            var bDate = new DateTime(sDate.Year, sDate.Month, 1);
            var adj = conn.Query<(string subcode, decimal debit, decimal credit)>(
                "SELECT rtrim(subcode), ISNULL(SUM(debit),0), ISNULL(SUM(credit),0) FROM finvouch WITH (NOLOCK) " +
                "WHERE date >= @bDate AND date <= @sDate AND post=1 AND (type='A' OR type='P') GROUP BY rtrim(subcode)",
                new { bDate, sDate }).AsList();
            subs = subs.Select(s =>
            {
                var a = adj.FirstOrDefault(x => x.subcode == s.subcode);
                return (s.subcode, s.subname, s.type, s.bd + a.debit, s.bc + a.credit, s.ed, s.ec);
            }).ToList();
        }
        // 明细（窗口 post=1 type A/P）
        var eDate1 = eDate.AddDays(1);
        var details = conn.Query<(int vouchid, DateTime? date, string vouword, int vcode, string opertype, string opercode,
            decimal debit, decimal credit, string dcdir, decimal balance, bool? fincheck, string subcode, string client)>(
            """
            SELECT vouchid, date, rtrim(vouword), ISNULL(vcode,0), ISNULL(rtrim(opertype),''), rtrim(ISNULL(opercode,'')),
                   ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dcdir,'')), ISNULL(balance,0), ISNULL(fincheck,0),
                   rtrim(subcode), rtrim(ISNULL(client,''))
            FROM finvouch WITH (NOLOCK)
            WHERE date >= @sDate AND date < @eDate1 AND post=1 AND (type='A' OR type='P')
            ORDER BY vouchid, recoid
            """, new { sDate, eDate1 }).AsList();
        var dzCond = isDz switch { 1 => " AND ISNULL(fincheck,0)=1", 2 => " AND ISNULL(fincheck,0)=0", _ => "" };
        var allDetails = conn.Query<(int vouchid, DateTime? date, string vouword, int vcode, string opertype, string opercode,
            decimal debit, decimal credit, string dcdir, decimal balance, bool? fincheck, string subcode, string client)>(
            "SELECT vouchid, date, rtrim(vouword), ISNULL(vcode,0), ISNULL(rtrim(opertype),''), rtrim(ISNULL(opercode,'')), " +
            "ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dcdir,'')), ISNULL(balance,0), ISNULL(fincheck,0), " +
            "rtrim(subcode), rtrim(ISNULL(client,'')) FROM finvouch WITH (NOLOCK) WHERE (type='A' OR type='P')" + dzCond +
            " ORDER BY vouchid, recoid").AsList();
        // 汇总
        foreach (var s in subs.Where(s => s.type == "A"))
        {
            var d = details.Where(x => x.subcode == s.subcode).ToList();
            result.Add($"YSHZ|{s.subcode}|{s.subname}|{s.bd - s.bc:0.##}|{d.Sum(x => x.debit):0.##}|{d.Sum(x => x.credit):0.##}|{s.ed - s.ec:0.##}");
        }
        foreach (var s in subs.Where(s => s.type == "P"))
        {
            var d = details.Where(x => x.subcode == s.subcode).ToList();
            result.Add($"YFHZ|{s.subcode}|{s.subname}|{s.bc - s.bd:0.##}|{d.Sum(x => x.credit):0.##}|{d.Sum(x => x.debit):0.##}|{s.ec - s.ed:0.##}");
        }
        foreach (var g in subs.GroupBy(s => s.subname))
        {
            var qc = g.Sum(s => s.bd - s.bc);
            var yue = qc + details.Where(x => g.Any(s2 => s2.subcode == x.subcode)).Sum(x => x.debit - x.credit);
            result.Add($"YSHZ2|{g.Key}|{(qc > 0 ? "借" : qc < 0 ? "贷" : "平")}|{Math.Abs(qc):0.##}|" +
                       $"{(yue > 0 ? "借" : yue < 0 ? "贷" : "平")}|{Math.Abs(yue):0.##}");
        }
        // 明细
        // 明细（opertype 中文名映射：BBI 采购入库/SRO 商品销售/CBI 汽车入库/CRO 汽车销售/else 维修车辆——空串同落维修车辆）
        static string OpName(string op) => op switch { "BBI" => "采购入库", "SRO" => "商品销售", "CBI" => "汽车入库", "CRO" => "汽车销售", _ => "维修车辆" };
        foreach (var x in allDetails.Where(x => subs.Any(s => s.subcode == x.subcode && s.type == "A")))
            result.Add($"YS|{x.vouchid}|{x.date:yyyy-MM-dd}|{x.vouword}-{x.vcode}|{OpName(x.opertype)}|{x.opercode}|" +
                       $"{x.debit:0.##}/{x.credit:0.##}|{((x.dcdir == "贷" ? -1 : 1) * x.balance):0.##}|{(x.fincheck == true ? 1 : 0)}");
        foreach (var x in allDetails.Where(x => subs.Any(s => s.subcode == x.subcode && s.type == "P")))
            result.Add($"YF|{x.vouchid}|{x.date:yyyy-MM-dd}|{x.vouword}-{x.vcode}|{OpName(x.opertype)}|{x.opercode}|" +
                       $"{x.credit:0.##}/{x.debit:0.##}|{((x.dcdir == "借" ? -1 : 1) * x.balance):0.##}|{(x.fincheck == true ? 1 : 0)}");
        foreach (var x in allDetails)
            result.Add($"YSYF|{x.vouchid}|{x.date:yyyy-MM-dd}|{x.vouword}-{x.vcode}|{OpName(x.opertype)}|{x.opercode}|" +
                       $"{x.debit:0.##}/{x.credit:0.##}|{x.dcdir}|{x.balance:0.##}");
        return result;
    }

    /// <summary>出纳帐（fr_finrnp Locate 语义）：汇总 + 滚动明细序列化。</summary>
    public static List<string> FinRnpLegacy(SqlConnection conn, DateTime sDate, DateTime eDate, bool includeUnposted, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var result = new List<string>();
        var postCond = includeUnposted ? "" : " AND post=1";
        var ctlPeriod = conn.ExecuteScalar<string>(
            "SELECT RTRIM(period) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }) ?? "";
        var sPeriod = sDate.ToString("yyyyMM");
        var mPeriod = string.CompareOrdinal(sPeriod, ctlPeriod) > 0 ? sPeriod : ctlPeriod;
        // 期初
        Dictionary<string, (decimal bd, decimal bc)> openMap;
        if (mPeriod == ctlPeriod)
            openMap = conn.Query<(string subcode, decimal bd, decimal bc)>(
                "SELECT rtrim(subcode), ISNULL(bdesum,0), ISNULL(bcresum,0) FROM finsub WITH (NOLOCK) " +
                "WHERE dpm=@dpm AND (type='B' OR type='C') AND ISNULL(detacode,0)=1", new { dpm = Ansi(dpm, 10) }).AsList()
                .ToDictionary(x => x.subcode, x => (x.bd, x.bc));
        else
        {
            openMap = conn.Query<(string subcode, decimal bd, decimal bc)>(
                "SELECT rtrim(a.subcode), ISNULL(b.bdesum,0), ISNULL(b.bcresum,0) FROM finsub a WITH (NOLOCK) " +
                "LEFT JOIN finhisbal b WITH (NOLOCK) ON rtrim(a.subcode)=rtrim(b.subcode) " +
                "WHERE a.dpm=@dpm AND b.dpm=@dpm AND (a.type='B' OR a.type='C') AND ISNULL(a.detacode,0)=1",
                new { dpm = Ansi(dpm, 10) }).AsList().ToDictionary(x => x.subcode, x => (x.bd, x.bc));
            if (openMap.Count == 0)
                openMap = conn.Query<(string subcode, decimal bd, decimal bc)>(
                    "SELECT rtrim(a.subcode), ISNULL(b.bdesum,0), ISNULL(b.bcresum,0) FROM finsub a WITH (NOLOCK) " +
                    "LEFT JOIN finsoursub b WITH (NOLOCK) ON rtrim(a.subcode)=rtrim(b.subcode) " +
                    "WHERE a.dpm=@dpm AND b.dpm=@dpm AND (a.type='B' OR a.type='C') AND ISNULL(a.detacode,0)=1",
                    new { dpm = Ansi(dpm, 10) }).AsList().ToDictionary(x => x.subcode, x => (x.bd, x.bc));
        }
        if (sDate.Day != 1)
        {
            var adj = conn.Query<(string subcode, decimal debit, decimal credit)>(
                "SELECT rtrim(subcode), ISNULL(SUM(debit),0), ISNULL(SUM(credit),0) FROM finvouch WITH (NOLOCK) " +
                "WHERE date >= @bDate AND date <= @sDate" + postCond + " AND (type='B' OR type='C') GROUP BY rtrim(subcode)",
                new { bDate = new DateTime(sDate.Year, sDate.Month, 1), sDate }).AsList();
            foreach (var a in adj)
                if (openMap.TryGetValue(a.subcode, out var v))
                    openMap[a.subcode] = (v.bd + a.debit, v.bc + a.credit);
        }
        // 明细
        var eDate1 = eDate.AddDays(1);
        var vouchers = conn.Query<(int vouchid, DateTime? date, string vouword, int vcode, string opertype, string opercode,
            decimal debit, decimal credit, string dcdir, decimal balance, string subcode, string note)>(
            "SELECT vouchid, date, rtrim(vouword), ISNULL(vcode,0), ISNULL(rtrim(opertype),''), rtrim(ISNULL(opercode,'')), " +
            "ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dcdir,'')), ISNULL(balance,0), rtrim(subcode), rtrim(ISNULL(note,'')) " +
            "FROM finvouch WITH (NOLOCK) WHERE date >= @sDate AND date < @eDate1" + postCond + " AND (type='B' OR type='C') " +
            "ORDER BY vouchid, recoid", new { sDate, eDate1 = eDate1, }).AsList();
        // 汇总（finrecout 顺序）
        var recouts = conn.Query<(string accname, string subcode, string subname, string type, string accounts)>(
            "SELECT rtrim(accname), rtrim(subcode), rtrim(subname), rtrim(ISNULL(type,'')), rtrim(ISNULL(accounts,'')) " +
            "FROM finrecout WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }).AsList();
        foreach (var a in recouts)
        {
            openMap.TryGetValue(a.subcode, out var o);
            var d = vouchers.Where(v => v.subcode == a.subcode).ToList();
            result.Add($"RECOUT|{a.accname}|{(a.type == "C" ? "现金类    " : "银行存款类")}|{a.accounts}|{a.subname}|" +
                       $"{o.bd - o.bc:0.##}|{d.Sum(x => x.debit):0.##}|{d.Sum(x => x.credit):0.##}|{o.bd - o.bc + d.Sum(x => x.debit - x.credit):0.##}");
        }
        // 明细（c_vouch 全部行；resum 仅滚动 finrecout 覆盖科目的余额，其余 BALANCE 保持原值/空）
        var balMap = new Dictionary<(int, string), decimal?>();
        foreach (var v in vouchers)
            balMap[(v.vouchid, v.subcode)] = null;   // 未滚动行 balance 输出空（对应旧码未 REPLACE 的初值）
        foreach (var a in recouts)
        {
            openMap.TryGetValue(a.subcode, out var o);
            var bal = o.bd - o.bc;
            foreach (var v in vouchers.Where(v => v.subcode == a.subcode).OrderBy(v => v.date))
            {
                bal += v.debit - v.credit;
                balMap[(v.vouchid, v.subcode)] = bal;
            }
        }
        foreach (var v in vouchers)
        {
            var opname = v.opertype switch { "BBI" => "采购入库", "SRO" => "商品销售", "SEV" => "维修车辆", _ => v.note };
            var bal = balMap.TryGetValue((v.vouchid, v.subcode), out var b) && b.HasValue ? b.Value.ToString("0.##") : "";
            result.Add($"V|{v.vouchid}|{v.date:yyyy-MM-dd}|{v.vouword}-{v.vcode}|{opname}|" +
                       $"{(v.debit == 0 ? "" : v.debit.ToString("0.##"))}/{(v.credit == 0 ? "" : v.credit.ToString("0.##"))}|{bal}");
        }
        return result;
    }

    /// <summary>出纳手工行（fr_finrnpaddmx 旧写路径）：INSERT finreclist + finrecout 重算。
    /// finreclist.id 为 IDENTITY（显式插入报错——旧码 VFP SPT 游标 TABLEUPDATE 不含 id，IDENTITY 自动生成）。
    /// 返回 ""（对齐新仓储 SaveFinreclistAsync 成功返回）。</summary>
    public static string FinReclistAddLegacy(SqlConnection conn, string subcode, int vouchid, DateTime date,
        string note, decimal debit, decimal credit, string dpm)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        conn.Execute(
            "INSERT INTO finreclist (dpm, subcode, vouchid, date, note, debit, credit) VALUES (@dpm, @sc, @vid, @date, @note, @debit, @credit)",
            new { dpm = Ansi(dpm, 10), sc = Ansi(subcode, 18), vid = vouchid, date, note = Ansi(note, 30), debit = (double)debit, credit = (double)credit });
        // finrecout 重算（SUM finreclist WHERE subcode → desum/cresum/ebalance=bbalance+ΣD-ΣC）
        var sums = conn.QueryFirst<(decimal desum, decimal cresum)>(
            "SELECT ISNULL(SUM(debit),0), ISNULL(SUM(credit),0) FROM finreclist WITH (NOLOCK) WHERE rtrim(subcode)=@sc AND dpm=@dpm",
            new { sc = Ansi(subcode, 18), dpm = Ansi(dpm, 10) });
        conn.Execute(
            "UPDATE finrecout SET desum=@desum, cresum=@cresum, ebalance=ISNULL(bbalance,0)+@desum-@cresum WHERE rtrim(subcode)=@sc AND dpm=@dpm",
            new { desum = (double)sums.desum, cresum = (double)sums.cresum, sc = Ansi(subcode, 18), dpm = Ansi(dpm, 10) });
        return "";
    }
}
