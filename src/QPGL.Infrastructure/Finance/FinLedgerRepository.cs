using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Finance;

// =====================================================================================
// 账务第八批（I.4 五账 / I.6 应收应付帐 / I.7 出纳帐）—— 契约：开发文档/契约摘录/账务第八批契约.md
// 只读查询为主；写路径仅：多栏账定义维护（fincolumn）+ 出纳手工行（finreclist + finrecout 重算）。
// 期初链：当月=finsub 余额列；历史期=finsub JOIN finhisbal（空回退 finsoursub）。
// opertype 中文名映射（fr_finysyf/fr_finrnp 原样）：BBI 采购入库 / SRO 商品销售 / CBI 汽车入库 /
//   CRO 汽车销售 / else 维修车辆（出纳版 else=NOTE）。
// =====================================================================================

/// <summary>总账行（finaleger 3 行/科目/期间）。</summary>
public sealed class FinAlegerRow
{
    public string Subcode { get; set; } = "";
    public string Name { get; set; } = "";          // GSUBNAME 层级名
    public string Period { get; set; } = "";
    public string Recoid { get; set; } = "";
    public string Csummary { get; set; } = "";
    public decimal Desum { get; set; }
    public decimal Cresum { get; set; }
    public string Dcdir { get; set; } = "";
    public decimal Balance { get; set; }
    public bool Lcolor { get; set; }                // 隔组交替色
}

/// <summary>明细账行（FinaleDetmp 游标语义）。</summary>
public sealed class FinAledetRow
{
    public DateTime? Date { get; set; }
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public string Vouword { get; set; } = "";
    public int Vcode { get; set; }
    public string Note { get; set; } = "";
    public string Opertype { get; set; } = "";
    public string Opercode { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Dcdir { get; set; } = "";
    public decimal Balance { get; set; }
    public int Recoid { get; set; }                 // 1 期初 / 2 分录 / 99 合计
    public string Resub { get; set; } = "";         // 对方科目串
}

/// <summary>数量金额账行（finamsum）。</summary>
public sealed class FinAmsumRow
{
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public string Period { get; set; } = "";
    public bool Isdetail { get; set; }
    public string Bdcdir { get; set; } = "";
    public decimal Bamount { get; set; }
    public decimal Bbalance { get; set; }
    public decimal Cdeamou { get; set; }
    public decimal Cdesum { get; set; }
    public decimal Ccreamou { get; set; }
    public decimal Ccresum { get; set; }
    public decimal Ydeamou { get; set; }
    public decimal Ydesum { get; set; }
    public decimal Ycreamou { get; set; }
    public decimal Ycresum { get; set; }
    public string Edcdir { get; set; } = "";
    public decimal Eamount { get; set; }
    public decimal Ebalance { get; set; }
}

/// <summary>核算项目账行（finitsub）。</summary>
public sealed class FinItsubRow
{
    public string Itemty { get; set; } = "";
    public string Itemna { get; set; } = "";
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public string Period { get; set; } = "";
    public string Vouword { get; set; } = "";
    public string Bdcdir { get; set; } = "";
    public decimal Bbalance { get; set; }
    public decimal Cdesum { get; set; }
    public decimal Ccresum { get; set; }
    public string Edcdir { get; set; } = "";
    public decimal Ebalance { get; set; }
}

/// <summary>多栏账定义（fincolumn distinct c_no/c_code/c_name）。</summary>
public sealed class FinColumnAccount
{
    public string CNo { get; set; } = "";
    public string CCode { get; set; } = "";
    public string CName { get; set; } = "";
}

/// <summary>多栏账定义明细（fincolumn 栏）。</summary>
public sealed class FinColumnDef
{
    public string CNo { get; set; } = "";
    public int Reid { get; set; }
    public string CCode { get; set; } = "";
    public string CName { get; set; } = "";
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public string Fdc { get; set; } = "";           // BT 借方栏 / DT 贷方栏
}

/// <summary>多栏账行（动态栏按 fincolumn fdc,subcode 序）。</summary>
public sealed class FinColumnLedgerRow
{
    public DateTime? Date { get; set; }
    public string Vword { get; set; } = "";
    public string Note { get; set; } = "";
    public List<decimal> Cells { get; set; } = [];  // 栏列（正数=借方栏 debit-credit / 贷方栏 credit-debit）
    public decimal DBa { get; set; }
    public decimal CBa { get; set; }
    public string Dcdir { get; set; } = "";
    public decimal Balance { get; set; }
    public int Vouchid { get; set; }
    public string Opertype { get; set; } = "";
    public int Reid { get; set; }                   // 1 期初 / 2 凭证 / 3 本期合计 / 4 本年累计
    public string Period { get; set; } = "";
}

/// <summary>应收应付汇总行（c_yshz/c_yfhz）。</summary>
public sealed class FinYsyfHzRow
{
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public decimal Qcys { get; set; }               // 期初应收（应付=QCYF）
    public decimal Debit { get; set; }              // 本期应收（应付侧=Σcredit，契约换位）
    public decimal Credit { get; set; }
    public decimal Ys { get; set; }                 // 期末余额（应付=YF）
    public decimal Quota { get; set; }
}

/// <summary>往来余额汇总行（c_ysyfhz，GROUP BY subname）。</summary>
public sealed class FinYsyfBalanceRow
{
    public string Subname { get; set; } = "";
    public string Qcdir { get; set; } = "";
    public decimal Qcyue { get; set; }
    public string Dcdir { get; set; } = "";
    public decimal Yue { get; set; }
}

/// <summary>应收/应付/余额明细行（c_ys/c_yf/c_ysyf）。</summary>
public sealed class FinYsyfDetRow
{
    public DateTime? Date { get; set; }
    public string Wordid { get; set; } = "";
    public string Opertype { get; set; } = "";
    public string Opercode { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
    public bool? Fincheck { get; set; }
    public DateTime? Chedate { get; set; }
    public string Note { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Auditer { get; set; } = "";
    public string Poster { get; set; } = "";
    public string Subcode { get; set; } = "";
    public string Client { get; set; } = "";
    public int Vouchid { get; set; }
    public int Vcode { get; set; }
    public string DcdirRaw { get; set; } = "";      // c_ysyf 原样 dcdir
}

/// <summary>应收应付帐查询结果（六集合）。</summary>
public sealed class FinYsyfLedger
{
    public string Uperiod { get; set; } = "";
    public IReadOnlyList<FinYsyfHzRow> YsHz { get; set; } = [];
    public IReadOnlyList<FinYsyfHzRow> YfHz { get; set; } = [];
    public IReadOnlyList<FinYsyfBalanceRow> YsyfHz { get; set; } = [];
    public IReadOnlyList<FinYsyfDetRow> Ys { get; set; } = [];
    public IReadOnlyList<FinYsyfDetRow> Yf { get; set; } = [];
    public IReadOnlyList<FinYsyfDetRow> Ysyf { get; set; } = [];
}

/// <summary>出纳帐汇总行（c_recout）。</summary>
public sealed class FinRnpRecoutRow
{
    public string Accname { get; set; } = "";
    public string Ctype { get; set; } = "";
    public string Accounts { get; set; } = "";
    public string Subname { get; set; } = "";
    public decimal Bbalance { get; set; }
    public decimal Desum { get; set; }
    public decimal Cresum { get; set; }
    public decimal Ebalance { get; set; }
    public string Subcode { get; set; } = "";
}

/// <summary>出纳帐明细行（c_vouch）。</summary>
public sealed class FinRnpVouchRow
{
    public DateTime? Date { get; set; }
    public string Wordid { get; set; } = "";
    public string Opertype { get; set; } = "";
    public string Opercode { get; set; } = "";
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public decimal? Balance { get; set; }
    public bool? Fincheck { get; set; }
    public DateTime? Chedate { get; set; }
    public string Note { get; set; } = "";
    public string Subcode { get; set; } = "";
    public string Client { get; set; } = "";
    public int Vouchid { get; set; }
    public int Vcode { get; set; }
    public string YwType { get; set; } = "";
}

/// <summary>出纳帐查询结果。</summary>
public sealed class FinRnpLedger
{
    public string Uperiod { get; set; } = "";
    public IReadOnlyList<FinRnpRecoutRow> Recout { get; set; } = [];
    public IReadOnlyList<FinRnpVouchRow> Vouch { get; set; } = [];
}

/// <summary>出纳手工行命令（fr_finrnpaddmx）。</summary>
public sealed class FinReclistCommand
{
    public string Subcode { get; set; } = "";
    public int Vouchid { get; set; }
    public DateTime Date { get; set; }
    public string Note { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public int? Id { get; set; }                    // Edit 时必填
}

public interface IFinLedgerRepository
{
    Task<IReadOnlyList<FinAlegerRow>> GetAlegerAsync(string bPeriod, string ePeriod, string bSub, string eSub, int subleng0, CancellationToken ct = default);
    Task<IReadOnlyList<FinAledetRow>> GetAledetAsync(string bPeriod, string ePeriod, string scode, bool includeUnposted,
        string? dept = null, string? item = null, string? empl = null, string? client = null, CancellationToken ct = default);
    Task<IReadOnlyList<FinAmsumRow>> GetAmsumAsync(string bPeriod, string ePeriod, string bSub, string eSub, bool onlyDetail, CancellationToken ct = default);
    Task<IReadOnlyList<FinItsubRow>> GetItsubAsync(string itemTy, string itemNa, string bPeriod, string ePeriod, string bSub, string eSub, CancellationToken ct = default);
    Task<IReadOnlyList<FinColumnAccount>> GetColumnAccountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FinColumnDef>> GetColumnDefsAsync(string cNo, CancellationToken ct = default);
    Task<IReadOnlyList<FinColumnLedgerRow>> GetColumnLedgerAsync(string cCode, string bPeriod, string ePeriod, bool includeUnposted, CancellationToken ct = default);
    Task<string> AddColumnDefAsync(string cCode, string cName, string subcode, string subname, string fdc, string dpm, CancellationToken ct = default);
    Task<string> UpdateColumnDefAsync(string cNo, int reid, string subcode, string subname, string fdc, string dpm, CancellationToken ct = default);
    Task<FinYsyfLedger> GetYsyfAsync(DateTime sDate, DateTime eDate, int isDz, string? depotno, string dpm, CancellationToken ct = default);
    Task<FinRnpLedger> GetRnpAsync(DateTime sDate, DateTime eDate, bool includeUnposted, string dpm, CancellationToken ct = default);
    Task<string> SaveFinreclistAsync(FinReclistCommand cmd, bool isAdd, string dpm, CancellationToken ct = default);
}

public sealed class FinLedgerRepository(SqlConnectionFactory factory) : IFinLedgerRepository
{
    // ─────────────────────────────── 总账 ───────────────────────────────

    /// <summary>总账（fr_finalegerf W_.row3）：finaleger 查询 + GSUBNAME 层级名 + 隔组交替色。</summary>
    public async Task<IReadOnlyList<FinAlegerRow>> GetAlegerAsync(string bPeriod, string ePeriod, string bSub, string eSub,
        int subleng0, CancellationToken ct = default)
    {
        var cond = string.IsNullOrWhiteSpace(bSub) || string.IsNullOrWhiteSpace(eSub)
            ? "rtrim(subcode) <> ''"
            : "rtrim(subcode) >= @bSub AND rtrim(subcode) <= @eSub";
        await using var conn = await factory.OpenAsync(ct);
        // distinct (subcode, period) 组合 + 全部行一次拉回（VFP 循环等价）
        var combos = (await conn.QueryAsync<(string subcode, string period)>(new CommandDefinition(
            $"""
            SELECT DISTINCT rtrim(subcode), rtrim(period) FROM finaleger WITH (NOLOCK)
            WHERE {cond} AND period >= @bPeriod AND period <= @ePeriod
              AND LEN(rtrim(subcode)) <= @subleng0
            ORDER BY rtrim(subcode), rtrim(period)
            """,
            new { bPeriod = Ansi(bPeriod, 6), ePeriod = Ansi(ePeriod, 6), bSub = Ansi(bSub ?? "", 18), eSub = Ansi(eSub ?? "", 18), subleng0 },
            cancellationToken: ct))).AsList();
        var rows = (await conn.QueryAsync<(string subcode, string period, string recoid, string csummary,
            decimal desum, decimal cresum, string dcdir, decimal balance)>(new CommandDefinition(
            $"""
            SELECT rtrim(subcode), rtrim(period), rtrim(recoid), rtrim(ISNULL(csummary,'')), ISNULL(desum,0), ISNULL(cresum,0),
                   rtrim(ISNULL(dcdir,'')), ISNULL(balance,0)
            FROM finaleger WITH (NOLOCK)
            WHERE {cond} AND period >= @bPeriod AND period <= @ePeriod
              AND LEN(rtrim(subcode)) <= @subleng0
            """,
            new { bPeriod = Ansi(bPeriod, 6), ePeriod = Ansi(ePeriod, 6), bSub = Ansi(bSub ?? "", 18), eSub = Ansi(eSub ?? "", 18), subleng0 },
            cancellationToken: ct))).AsList();
        var names = await GSubNamesAsync(conn, rows.Select(r => r.subcode).Distinct(), ct);
        var result = new List<FinAlegerRow>();
        var color = false;
        foreach (var (sub, period) in combos)
        {
            color = !color;
            foreach (var r in rows.Where(r => r.subcode == sub && r.period == period)
                         .OrderBy(r => r.recoid))
                result.Add(new FinAlegerRow
                {
                    Subcode = r.subcode, Name = names.GetValueOrDefault(r.subcode, ""), Period = r.period,
                    Recoid = r.recoid, Csummary = r.csummary, Desum = r.desum, Cresum = r.cresum,
                    Dcdir = r.dcdir, Balance = r.balance, Lcolor = color,
                });
        }
        return result;
    }

    // ─────────────────────────────── 明细账 ───────────────────────────────

    /// <summary>明细账（fr_findetail U_.row15）：finvouch 科目前缀 + 期初 + 滚动余额 + 对方科目串 + 合计。</summary>
    public async Task<IReadOnlyList<FinAledetRow>> GetAledetAsync(string bPeriod, string ePeriod, string scode, bool includeUnposted,
        string? dept = null, string? item = null, string? empl = null, string? client = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cond = "period >= @bPeriod AND period <= @ePeriod AND Left(rtrim(subcode), LEN(@scode)) = @scode";
        if (!includeUnposted) cond += " AND post = 1";
        if (!string.IsNullOrWhiteSpace(dept)) cond += " AND rtrim(dept) = @dept";
        if (!string.IsNullOrWhiteSpace(item)) cond += " AND rtrim(item) = @item";
        if (!string.IsNullOrWhiteSpace(empl)) cond += " AND rtrim(empl) = @empl";
        if (!string.IsNullOrWhiteSpace(client)) cond += " AND rtrim(client) = @client";
        var p = new
        {
            bPeriod = Ansi(bPeriod, 6), ePeriod = Ansi(ePeriod, 6), scode = Ansi(scode, 18),
            dept = Ansi(dept ?? "", 20), item = Ansi(item ?? "", 20), empl = Ansi(empl ?? "", 10), client = Ansi(client ?? "", 40),
        };
        // 窗口分录 + 涉及凭证的全部行（对方科目查找；两步取号避免 IN 子查询计划爆炸）
        var lines = (await conn.QueryAsync<(int vouchid, string subcode, string subname, decimal debit, decimal credit,
            string dcdir, decimal balance, DateTime? date, string vouword, int vcode, string opertype, string opercode,
            string note, string dept0)>(new CommandDefinition(
            $"""
            SELECT vouchid, rtrim(subcode), rtrim(subname), ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dcdir,'')),
                   ISNULL(balance,0), date, rtrim(vouword), ISNULL(vcode,0), ISNULL(rtrim(opertype),''), rtrim(ISNULL(opercode,'')),
                   rtrim(ISNULL(note,'')), rtrim(ISNULL(dept,''))
            FROM finvouch WITH (NOLOCK) WHERE {cond} ORDER BY vouchid, recoid
            """, p, cancellationToken: ct))).AsList();
        var allRows = lines.Count == 0 ? [] : (await conn.QueryAsync<(int vouchid, string subcode, string subname, decimal debit, decimal credit, string dept0)>(
            new CommandDefinition(
                "SELECT vouchid, rtrim(subcode), rtrim(subname), ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dept,'')) " +
                "FROM finvouch WITH (NOLOCK) WHERE vouchid IN @vids",
                new { vids = lines.Select(l => l.vouchid).Distinct().ToList() }, cancellationToken: ct))).AsList();
        var byVouchid = allRows.GroupBy(r => r.vouchid).ToDictionary(g => g.Key, g => g.AsList());

        var result = new List<FinAledetRow>();
        // 期初行（finaleger recoid='1'）
        var open = await conn.QueryFirstOrDefaultAsync<(string dcdir, decimal balance)>(new CommandDefinition(
            "SELECT TOP (1) rtrim(ISNULL(dcdir,'')), ISNULL(balance,0) FROM finaleger WITH (NOLOCK) " +
            "WHERE period = @bPeriod AND rtrim(subcode) = @scode AND recoid = '1'",
            new { bPeriod = Ansi(bPeriod, 6), scode = Ansi(scode, 18) }, cancellationToken: ct));
        var sBal = 0m;
        if (open.dcdir != null)
        {
            sBal = (open.dcdir == "贷" ? -1 : 1) * open.balance;
            result.Add(new FinAledetRow
            {
                Date = new DateTime(int.Parse(bPeriod[..4]), int.Parse(bPeriod[4..]), 1),
                Subcode = scode, Note = "期初余额", Recoid = 1, Dcdir = open.dcdir, Balance = open.balance,
            });
        }
        else
        {
            result.Add(new FinAledetRow
            {
                Date = new DateTime(int.Parse(bPeriod[..4]), int.Parse(bPeriod[4..]), 1),
                Subcode = scode, Note = "期初余额", Recoid = 1, Dcdir = "平",
            });
        }
        // 逐分录滚动
        foreach (var l in lines)
        {
            sBal += l.debit - l.credit;
            // 对方科目串（同 vouchid 借贷反向行）
            var resub = "";
            if (byVouchid.TryGetValue(l.vouchid, out var peers))
                foreach (var q in peers)
                {
                    var isCounter = l.debit == 0 ? q.credit == 0 : q.debit == 0;
                    if (!isCounter) continue;
                    var seg = q.subcode + ":" + q.subname + (dept0Empty(l.dept0) && string.IsNullOrEmpty(q.dept0) ? "" : "|" + (dept0Empty(l.dept0) ? q.dept0 : l.dept0));
                    resub = resub.Length == 0 ? seg : resub + ";" + seg;
                }
            result.Add(new FinAledetRow
            {
                Date = l.date, Subcode = l.subcode, Subname = l.subname, Vouword = l.vouword, Vcode = l.vcode,
                Note = l.note, Opertype = l.opertype, Opercode = l.opercode, Debit = l.debit, Credit = l.credit,
                Dcdir = sBal > 0 ? "借" : sBal < 0 ? "贷" : "平", Balance = Math.Abs(sBal), Recoid = 2, Resub = resub,
            });
        }
        // 本期合计（游标全行 SUM——fr_findetail 语义，与总账明细的 finaleger 读数不同）
        result.Add(new FinAledetRow
        {
            Date = MonthEnd(ePeriod), Subcode = scode, Note = "本期合计",
            Debit = lines.Sum(x => x.debit), Credit = lines.Sum(x => x.credit),
            Dcdir = sBal > 0 ? "借" : sBal < 0 ? "贷" : "平", Balance = Math.Abs(sBal), Recoid = 99,
        });
        return result;
    }

    private static bool dept0Empty(string d) => string.IsNullOrEmpty(d);

    // ─────────────────────────────── 数量金额账 ───────────────────────────────

    /// <summary>数量金额账（fr_finasfind row6）：finamsum 查询。</summary>
    public async Task<IReadOnlyList<FinAmsumRow>> GetAmsumAsync(string bPeriod, string ePeriod, string bSub, string eSub,
        bool onlyDetail, CancellationToken ct = default)
    {
        var cond = "period >= @bPeriod AND period <= @ePeriod";
        if (!string.IsNullOrWhiteSpace(bSub)) cond += " AND rtrim(subcode) >= @bSub";
        if (!string.IsNullOrWhiteSpace(eSub)) cond += " AND rtrim(subcode) <= @eSub";
        if (onlyDetail) cond += " AND ISNULL(isdetail,0) = 1";
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinAmsumRow>(new CommandDefinition(
            $"""
            SELECT rtrim(subcode) subcode, rtrim(subname) subname, rtrim(period) period, ISNULL(isdetail,0) isdetail,
                   rtrim(ISNULL(bdcdir,'平')) bdcdir, ISNULL(bamount,0) bamount, ISNULL(bbalance,0) bbalance,
                   ISNULL(cdeamou,0) cdeamou, ISNULL(cdesum,0) cdesum, ISNULL(ccreamou,0) ccreamou, ISNULL(ccresum,0) ccresum,
                   ISNULL(ydeamou,0) ydeamou, ISNULL(ydesum,0) ydesum, ISNULL(ycreamou,0) ycreamou, ISNULL(ycresum,0) ycresum,
                   rtrim(ISNULL(edcdir,'平')) edcdir, ISNULL(eamount,0) eamount, ISNULL(ebalance,0) ebalance
            FROM finamsum WITH (NOLOCK) WHERE {cond} ORDER BY rtrim(subcode), rtrim(period)
            """,
            new { bPeriod = Ansi(bPeriod, 6), ePeriod = Ansi(ePeriod, 6), bSub = Ansi(bSub ?? "", 18), eSub = Ansi(eSub ?? "", 18) },
            cancellationToken: ct));
        return rows.AsList();
    }

    // ─────────────────────────────── 核算项目账 ───────────────────────────────

    /// <summary>核算项目账（fr_finitemsubq G_.row3）：finitsub 查询。</summary>
    public async Task<IReadOnlyList<FinItsubRow>> GetItsubAsync(string itemTy, string itemNa, string bPeriod, string ePeriod,
        string bSub, string eSub, CancellationToken ct = default)
    {
        var cond = "period <> '' AND period >= @bPeriod AND period <= @ePeriod AND itemty = @itemTy";
        if (!string.IsNullOrWhiteSpace(itemNa)) cond += " AND rtrim(ItemNa) = @itemNa";
        if (!string.IsNullOrWhiteSpace(bSub)) cond += " AND rtrim(subcode) >= @bSub";
        if (!string.IsNullOrWhiteSpace(eSub)) cond += " AND rtrim(subcode) <= @eSub";
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinItsubRow>(new CommandDefinition(
            $"""
            SELECT rtrim(itemty) itemty, rtrim(itemna) itemna, rtrim(subcode) subcode, rtrim(subname) subname,
                   rtrim(period) period, rtrim(ISNULL(vouword,'')) vouword, rtrim(ISNULL(bdcdir,'平')) bdcdir,
                   ISNULL(bbalance,0) bbalance, ISNULL(cdesum,0) cdesum, ISNULL(ccresum,0) ccresum,
                   rtrim(ISNULL(edcdir,'平')) edcdir, ISNULL(ebalance,0) ebalance
            FROM finitsub WITH (NOLOCK) WHERE {cond} ORDER BY rtrim(subcode)
            """,
            new
            {
                itemTy = Ansi(itemTy, 1), itemNa = Ansi(itemNa ?? "", 40),
                bPeriod = Ansi(bPeriod, 6), ePeriod = Ansi(ePeriod, 6),
                bSub = Ansi(bSub ?? "", 18), eSub = Ansi(eSub ?? "", 18),
            }, cancellationToken: ct));
        return rows.AsList();
    }

    // ─────────────────────────────── 多栏账 ───────────────────────────────

    /// <summary>多栏账账号列表（fr_fincolumnadd Unload：distinct c_no,c_code,c_name）。</summary>
    public async Task<IReadOnlyList<FinColumnAccount>> GetColumnAccountsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinColumnAccount>(new CommandDefinition(
            "SELECT DISTINCT rtrim(c_no) c_no, rtrim(c_code) c_code, rtrim(c_name) c_name FROM fincolumn WITH (NOLOCK) ORDER BY rtrim(c_code)",
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>多栏账定义明细（fr_fincolumnadd Init：c_no 全栏 ORDER BY fdc,subcode）。</summary>
    public async Task<IReadOnlyList<FinColumnDef>> GetColumnDefsAsync(string cNo, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinColumnDef>(new CommandDefinition(
            """
            SELECT rtrim(c_no) c_no, reid, rtrim(c_code) c_code, rtrim(c_name) c_name,
                   rtrim(subcode) subcode, rtrim(subname) subname, rtrim(fdc) fdc
            FROM fincolumn WITH (NOLOCK) WHERE rtrim(c_no) = @cNo ORDER BY fdc, subcode
            """, new { cNo = Ansi(cNo, 10) }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>多栏账生成（fr_fincolumnf g_.row8）：动态栏 + 期初 + 逐凭证滚动 + 本期合计 + 本年累计。</summary>
    public async Task<IReadOnlyList<FinColumnLedgerRow>> GetColumnLedgerAsync(string cCode, string bPeriod, string ePeriod,
        bool includeUnposted, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var defs = (await conn.QueryAsync<(string fdc, string subcode, string subname)>(new CommandDefinition(
            "SELECT rtrim(fdc), rtrim(subcode), rtrim(subname) FROM fincolumn WITH (NOLOCK) WHERE rtrim(c_code) = @cCode AND rtrim(subname) <> '' ORDER BY fdc, subcode",
            new { cCode = Ansi(cCode, 18) }, cancellationToken: ct))).AsList();
        if (defs.Count == 0) return [];
        var cond = "period BETWEEN @bPeriod AND @ePeriod";
        if (!includeUnposted) cond += " AND post = 1";
        var p = new { bPeriod = Ansi(bPeriod, 6), ePeriod = Ansi(ePeriod, 6), cCode = Ansi(cCode, 18) };
        var vouchers = (await conn.QueryAsync<(int vouchid, DateTime? date, string period, string vouword, int vcode,
            string opertype, string note, string subcode, decimal debit, decimal credit)>(new CommandDefinition(
            $"""
            SELECT a.vouchid, a.date, rtrim(a.period), rtrim(a.vouword), ISNULL(a.vcode,0), ISNULL(rtrim(a.opertype),''),
                   rtrim(ISNULL(a.note,'')), rtrim(a.subcode), ISNULL(a.debit,0), ISNULL(a.credit,0)
            FROM finvouch a WITH (NOLOCK)
            WHERE {cond} AND EXISTS (SELECT 1 FROM fincolumn f WITH (NOLOCK) WHERE rtrim(f.c_code) = @cCode AND f.subcode = a.subcode)
            ORDER BY a.vouchid, a.recoid
            """, p, cancellationToken: ct))).AsList();
        // 期初（finsub(c_code).edesum-ecresum）
        var open = await conn.QueryFirstOrDefaultAsync<(decimal ed, decimal ec)>(new CommandDefinition(
            "SELECT TOP (1) ISNULL(edesum,0), ISNULL(ecresum,0) FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @cCode",
            p, cancellationToken: ct));
        var be0 = open.ed - open.ec;
        var result = new List<FinColumnLedgerRow>
        {
            new()
            {
                Date = new DateTime(int.Parse(bPeriod[..4]), int.Parse(bPeriod[4..]), 1), Note = "期初余额",
                Dcdir = be0 > 0 ? "借" : be0 < 0 ? "贷" : "平", Balance = Math.Abs(be0), Reid = 1,
                Cells = Enumerable.Repeat(0m, defs.Count).ToList(),
            },
        };
        var cellIdx = defs.Select((d, i) => (d.subcode, i)).GroupBy(x => x.subcode).ToDictionary(g => g.Key, g => g.First().i);
        foreach (var g in vouchers.GroupBy(v => v.vouchid))
        {
            var head = g.First();
            var row = new FinColumnLedgerRow
            {
                Date = head.date, Period = head.period, Vword = head.vouword + "-" + head.vcode,
                Vouchid = head.vouchid, Opertype = head.opertype, Reid = 2,
                Note = head.opertype == "CAF" ? "结转本期损益" : head.note,
                Cells = Enumerable.Repeat(0m, defs.Count).ToList(),
            };
            foreach (var v in g)
            {
                if (!cellIdx.TryGetValue(v.subcode, out var idx)) continue;
                var def = defs[idx];
                row.Cells[idx] += def.fdc == "BT" ? v.debit - v.credit : v.credit - v.debit;
                row.DBa += v.debit;
                row.CBa += v.credit;
            }
            be0 += g.Sum(v => v.debit) - g.Sum(v => v.credit);
            row.Dcdir = be0 > 0 ? "借" : be0 < 0 ? "贷" : "平";
            row.Balance = Math.Abs(be0);
            result.Add(row);
        }
        // 本期合计（REID=3）：各栏 SUM(栏列 WHERE opertype<>'CAF'，旧码 SUM(d_clom&ci) 直接累计含负值)；
        // 合计行 D_BA/C_BA=全表 SUM（含期初行 0）
        var sumRow = new FinColumnLedgerRow
        {
            Date = MonthEnd(ePeriod), Note = "本期合计", Reid = 3,
            Cells = Enumerable.Range(0, defs.Count)
                .Select(i => result.Where(r => r.Reid == 2 && r.Opertype != "CAF").Sum(r => r.Cells[i]))
                .ToList(),
        };
        sumRow.DBa = result.Sum(r => r.DBa);
        sumRow.CBa = result.Sum(r => r.CBa);
        var be3 = sumRow.DBa - sumRow.CBa;
        sumRow.Dcdir = be3 > 0 ? "借" : be3 < 0 ? "贷" : "平";
        sumRow.Balance = Math.Abs(be3);
        result.Add(sumRow);
        // 本年累计（REID=4）：年内 period<=ePeriod 重聚合
        var yearCond = "LEFT(rtrim(period),4) = @year AND period <= @ePeriod";
        if (!includeUnposted) yearCond += " AND post = 1";
        var yearRows = (await conn.QueryAsync<(string subcode, decimal debit, decimal credit, string opertype)>(
            new CommandDefinition(
                $"""
                SELECT rtrim(a.subcode), ISNULL(b.debit,0), ISNULL(b.credit,0), ISNULL(rtrim(b.opertype),'')
                FROM fincolumn a WITH (NOLOCK) JOIN finvouch b WITH (NOLOCK) ON a.subcode = b.subcode
                WHERE rtrim(a.c_code) = @cCode AND {yearCond}
                """, new { cCode = Ansi(cCode, 18), year = bPeriod[..4], ePeriod = Ansi(ePeriod, 6) }, cancellationToken: ct))).AsList();
        var yearRow = new FinColumnLedgerRow
        {
            Note = "本年累计", Reid = 4, Cells = new List<decimal>(),
        };
        for (var i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            var sum = yearRows.Where(r => r.subcode == def.subcode && r.opertype != "CAF")
                .Sum(r => def.fdc == "BT" ? r.debit - r.credit : r.credit - r.debit);
            yearRow.Cells.Add(sum);
        }
        yearRow.DBa = yearRows.Sum(r => r.debit);
        yearRow.CBa = yearRows.Sum(r => r.credit);
        var be4 = yearRow.DBa - yearRow.CBa;
        yearRow.Dcdir = be4 > 0 ? "借" : be4 < 0 ? "贷" : "平";
        yearRow.Balance = Math.Abs(be4);
        result.Add(yearRow);
        return result;
    }

    /// <summary>多栏定义新增（fr_fincolumnedit Click Add：栏 subcode 同账内重复拦截、reid=MAX+1）。</summary>
    public async Task<string> AddColumnDefAsync(string cCode, string cName, string subcode, string subname, string fdc,
        string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 取账内 c_no（按 c_code 定位；同 c_code 多行取首行 c_no——旧码 Edit 态传入，Add 态由 UI 生成新号）
        var cNo = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT TOP (1) rtrim(c_no) FROM fincolumn WITH (NOLOCK) WHERE rtrim(c_code) = @cCode",
            new { cCode = Ansi(cCode, 18) }, cancellationToken: ct));
        if (cNo == null) return "多栏账不存在!";
        var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM fincolumn WITH (NOLOCK) WHERE rtrim(c_no) = @cNo AND rtrim(subcode) = @subcode",
            new { cNo = Ansi(cNo, 10), subcode = Ansi(subcode, 18) }, cancellationToken: ct));
        if (dup > 0) return $"明细项栏目{subcode}已经存在!";
        var reid = (await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(reid) FROM fincolumn WITH (NOLOCK) WHERE rtrim(c_no) = @cNo",
            new { cNo = Ansi(cNo, 10) }, cancellationToken: ct)) ?? 0) + 1;
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO fincolumn (dpm, c_no, reid, c_code, c_name, subcode, subname, fdc)
            VALUES (@dpm, @cNo, @reid, @cCode, @cName, @subcode, @subname, @fdc)
            """,
            new
            {
                dpm = Ansi(dpm, 10), cNo = Ansi(cNo, 10), reid, cCode = Ansi(cCode, 18),
                cName = Ansi(cName, 40), subcode = Ansi(subcode, 18), subname = Ansi(subname, 40), fdc = Ansi(fdc, 2),
            }, cancellationToken: ct));
        return "";
    }

    /// <summary>多栏定义修改（fr_fincolumnedit Click Edit：UPDATE WHERE c_no AND reid）。</summary>
    public async Task<string> UpdateColumnDefAsync(string cNo, int reid, string subcode, string subname, string fdc,
        string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM fincolumn WITH (NOLOCK) WHERE rtrim(c_no) = @cNo AND rtrim(subcode) = @subcode AND reid <> @reid",
            new { cNo = Ansi(cNo, 10), reid, subcode = Ansi(subcode, 18) }, cancellationToken: ct));
        if (dup > 0) return "明细项栏目已经存在!";
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE fincolumn SET subcode = @subcode, subname = @subname, fdc = @fdc WHERE rtrim(c_no) = @cNo AND reid = @reid AND dpm = @dpm",
            new
            {
                cNo = Ansi(cNo, 10), reid, subcode = Ansi(subcode, 18), subname = Ansi(subname, 40),
                fdc = Ansi(fdc, 2), dpm = Ansi(dpm, 10),
            }, cancellationToken: ct));
        return "";
    }

    // ─────────────────────────────── 应收应付帐 ───────────────────────────────

    /// <summary>应收应付帐（fr_finysyf FIND/Init；六集合）。</summary>
    public async Task<FinYsyfLedger> GetYsyfAsync(DateTime sDate, DateTime eDate, int isDz, string? depotno, string dpm,
        CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var period = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT RTRIM(period) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct)) ?? "";
        var mPeriod = MaxPeriod(sDate.ToString("yyyyMM"), period);
        // 科目集（当月=finsub 余额；历史=finhisbal→finsoursub 期初链）
        var depotCond = string.IsNullOrWhiteSpace(depotno) ? "" :
            " AND rtrim(subcode) IN (SELECT rtrim(subcode) FROM finvouch WITH (NOLOCK) WHERE (type='A' OR type='P') AND LEFT(rtrim(depotno),4) = @depotno)";
        List<(string subcode, string subname, string type, decimal bdesum, decimal bcresum, decimal edesum, decimal ecresum)> subs;
        if (mPeriod == period)
        {
            subs = (await conn.QueryAsync<(string, string, string, decimal, decimal, decimal, decimal)>(new CommandDefinition(
                $"""
                SELECT rtrim(a.subcode), rtrim(a.subname), rtrim(ISNULL(a.type,'')), ISNULL(a.bdesum,0), ISNULL(a.bcresum,0), ISNULL(a.edesum,0), ISNULL(a.ecresum,0)
                FROM finsub a WITH (NOLOCK) WHERE a.dpm = @dpm AND (a.type = 'A' OR a.type = 'P') AND ISNULL(a.detacode,0) = 1{depotCond}
                ORDER BY rtrim(a.subname)
                """, new { dpm = Ansi(dpm, 10), depotno = Ansi(depotno ?? "", 10) }, cancellationToken: ct))).AsList();
        }
        else
        {
            subs = (await conn.QueryAsync<(string, string, string, decimal, decimal, decimal, decimal)>(new CommandDefinition(
                $"""
                SELECT rtrim(a.subcode), rtrim(a.subname), rtrim(ISNULL(a.type,'')), ISNULL(b.bdesum,0), ISNULL(b.bcresum,0), ISNULL(a.edesum,0), ISNULL(a.ecresum,0)
                FROM finsub a WITH (NOLOCK) LEFT JOIN finhisbal b WITH (NOLOCK) ON rtrim(a.subcode) = rtrim(b.subcode)
                WHERE a.dpm = @dpm AND b.dpm = @dpm AND (a.type = 'A' OR a.type = 'P') AND ISNULL(a.detacode,0) = 1{depotCond}
                ORDER BY rtrim(a.subname)
                """, new { dpm = Ansi(dpm, 10), depotno = Ansi(depotno ?? "", 10) }, cancellationToken: ct))).AsList();
            if (subs.Count == 0)
                subs = (await conn.QueryAsync<(string, string, string, decimal, decimal, decimal, decimal)>(new CommandDefinition(
                    $"""
                    SELECT rtrim(a.subcode), rtrim(a.subname), rtrim(ISNULL(a.type,'')), ISNULL(b.bdesum,0), ISNULL(b.bcresum,0), ISNULL(a.edesum,0), ISNULL(a.ecresum,0)
                    FROM finsub a WITH (NOLOCK) LEFT JOIN finsoursub b WITH (NOLOCK) ON rtrim(a.subcode) = rtrim(b.subcode)
                    WHERE a.dpm = @dpm AND b.dpm = @dpm AND (a.type = 'A' OR a.type = 'P') AND ISNULL(a.detacode,0) = 1{depotCond}
                    ORDER BY rtrim(a.subname)
                    """, new { dpm = Ansi(dpm, 10), depotno = Ansi(depotno ?? "", 10) }, cancellationToken: ct))).AsList();
        }
        // S_DATE 非 1 日：期初 += 月 1 日~S_DATE 前发生额
        var bDate = sDate;
        if (sDate.Day != 1)
        {
            bDate = new DateTime(sDate.Year, sDate.Month, 1);
            var adj = (await conn.QueryAsync<(string subcode, decimal debit, decimal credit)>(new CommandDefinition(
                "SELECT rtrim(subcode), ISNULL(SUM(debit),0), ISNULL(SUM(credit),0) FROM finvouch WITH (NOLOCK) " +
                "WHERE date >= @bDate AND date <= @sDate AND post = 1 AND (type='A' OR type='P') GROUP BY rtrim(subcode)",
                new { bDate, sDate }, cancellationToken: ct))).AsList();
            subs = subs.Select(s =>
            {
                var a = adj.FirstOrDefault(x => x.subcode == s.subcode);
                return (s.subcode, s.subname, s.type, s.bdesum + a.debit, s.bcresum + a.credit, s.edesum, s.ecresum);
            }).ToList();
        }
        var eDate1 = eDate.AddDays(1);
        var dzCond = isDz switch { 1 => " AND ISNULL(fincheck,0) = 1", 2 => " AND ISNULL(fincheck,0) = 0", _ => "" };
        var allDetails = (await conn.QueryAsync<(int vouchid, int vcode, DateTime? date, string vouword, string opertype,
            string opercode, decimal debit, decimal credit, string dcdir, decimal balance, bool? fincheck, DateTime? chedate,
            string note, string maker, string auditer, string poster, string subcode, string client)>(new CommandDefinition(
            $"""
            SELECT vouchid, ISNULL(vcode,0), date, rtrim(vouword), ISNULL(rtrim(opertype),''), rtrim(ISNULL(opercode,'')),
                   ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dcdir,'')), ISNULL(balance,0), ISNULL(fincheck,0), chedate,
                   rtrim(ISNULL(note,'')), rtrim(ISNULL(maker,'')), rtrim(ISNULL(auditer,'')), rtrim(ISNULL(poster,'')),
                   rtrim(subcode), rtrim(ISNULL(client,''))
            FROM finvouch WITH (NOLOCK)
            WHERE (type='A' OR type='P'){dzCond}
            ORDER BY vouchid, recoid
            """, cancellationToken: ct))).AsList();
        // 窗口发生额（汇总用：date BETWEEN sDate AND eDate+1 post=1 type A/P）
        var details = allDetails.Where(x => x.date >= sDate && x.date < eDate1).ToList();
        var result = new FinYsyfLedger { Uperiod = $"会计期间：{mPeriod[..4]}年{mPeriod[4..]}月" };
        // 应收汇总（type='A'）
        result.YsHz = subs.Where(s => s.type == "A").Select(s =>
        {
            var d = details.Where(x => x.subcode == s.subcode).ToList();
            return new FinYsyfHzRow
            {
                Subcode = s.subcode, Subname = s.subname, Qcys = s.bdesum - s.bcresum,
                Debit = d.Sum(x => x.debit), Credit = d.Sum(x => x.credit), Ys = s.edesum - s.ecresum,
            };
        }).ToList();
        // 应付汇总（type='P'：DEBIT=Σcredit、CREDIT=Σdebit 契约换位）
        result.YfHz = subs.Where(s => s.type == "P").Select(s =>
        {
            var d = details.Where(x => x.subcode == s.subcode).ToList();
            return new FinYsyfHzRow
            {
                Subcode = s.subcode, Subname = s.subname, Qcys = s.bcresum - s.bdesum,
                Debit = d.Sum(x => x.credit), Credit = d.Sum(x => x.debit), Ys = s.ecresum - s.edesum,
            };
        }).ToList();
        // 余额汇总（GROUP BY subname）
        result.YsyfHz = subs.GroupBy(s => s.subname).Select(g =>
        {
            var qc = g.Sum(s => s.bdesum - s.bcresum);
            var yue = qc + details.Where(x => g.Any(s2 => s2.subcode == x.subcode)).Sum(x => x.debit - x.credit);
            return new FinYsyfBalanceRow
            {
                Subname = g.Key,
                Qcyue = Math.Abs(qc),
                Qcdir = qc > 0 ? "借" : qc < 0 ? "贷" : "平",
                Yue = Math.Abs(yue),
                Dcdir = yue > 0 ? "借" : yue < 0 ? "贷" : "平",
            };
        }).ToList();
        // 明细三集合（旧码 c_ys/c_yf/c_ysyf 为全部历史 finvouch type 过滤，非窗口）
        result.Ys = allDetails.Where(x => TypeOfSub(allDetails, subs, x.subcode) == "A").Select(x => new FinYsyfDetRow
        {
            Date = x.date, Wordid = x.vouword + "-" + x.vcode, Opertype = OpTypeName(x.opertype), Opercode = x.opercode,
            Debit = x.debit, Credit = x.credit, Balance = (x.dcdir == "贷" ? -1 : 1) * x.balance,
            Fincheck = x.fincheck, Chedate = x.chedate, Note = x.note, Maker = x.maker, Auditer = x.auditer,
            Poster = x.poster, Subcode = x.subcode, Client = x.client, Vouchid = x.vouchid, Vcode = x.vcode,
        }).ToList();
        result.Yf = allDetails.Where(x => TypeOfSub(allDetails, subs, x.subcode) == "P").Select(x => new FinYsyfDetRow
        {
            Date = x.date, Wordid = x.vouword + "-" + x.vcode, Opertype = OpTypeName(x.opertype), Opercode = x.opercode,
            Debit = x.credit, Credit = x.debit, Balance = (x.dcdir == "借" ? -1 : 1) * x.balance,
            Fincheck = x.fincheck, Chedate = x.chedate, Note = x.note, Maker = x.maker, Auditer = x.auditer,
            Poster = x.poster, Subcode = x.subcode, Client = x.client, Vouchid = x.vouchid, Vcode = x.vcode,
        }).ToList();
        result.Ysyf = allDetails.Select(x => new FinYsyfDetRow
        {
            Date = x.date, Wordid = x.vouword + "-" + x.vcode, Opertype = OpTypeName(x.opertype), Opercode = x.opercode,
            Debit = x.debit, Credit = x.credit, Balance = x.balance, DcdirRaw = x.dcdir,
            Fincheck = x.fincheck, Chedate = x.chedate, Note = x.note, Maker = x.maker, Auditer = x.auditer,
            Poster = x.poster, Subcode = x.subcode, Client = x.client, Vouchid = x.vouchid, Vcode = x.vcode,
        }).ToList();
        return result;
    }

    /// <summary>科目类别（内存字典，subs 未覆盖时按明细行自身无法判定 → 查 finsub type 缓存）。</summary>
    private string TypeOfSub(List<(int vouchid, int vcode, DateTime? date, string vouword, string opertype,
        string opercode, decimal debit, decimal credit, string dcdir, decimal balance, bool? fincheck, DateTime? chedate,
        string note, string maker, string auditer, string poster, string subcode, string client)> details,
        List<(string subcode, string subname, string type, decimal bdesum, decimal bcresum, decimal edesum, decimal ecresum)> subs,
        string subcode)
        => subs.FirstOrDefault(s => s.subcode == subcode).type ?? "";

    public static string OpTypeName(string opertype) => opertype switch
    {
        "BBI" => "采购入库",
        "SRO" => "商品销售",
        "CBI" => "汽车入库",
        "CRO" => "汽车销售",
        _ => "维修车辆",
    };

    // ─────────────────────────────── 出纳帐 ───────────────────────────────

    /// <summary>出纳帐（fr_finrnp Init/Locate；c_recout + c_vouch + resum 滚动）。</summary>
    public async Task<FinRnpLedger> GetRnpAsync(DateTime sDate, DateTime eDate, bool includeUnposted, string dpm,
        CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var postCond = includeUnposted ? "" : " AND post = 1";
        var ctlPeriod = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT RTRIM(period) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct)) ?? "";
        var mPeriod = MaxPeriod(sDate.ToString("yyyyMM"), ctlPeriod);
        // 科目期初（当月=finsub；历史=finhisbal→finsoursub）
        Dictionary<string, (decimal bd, decimal bc)> openMap;
        if (mPeriod == ctlPeriod)
        {
            openMap = (await conn.QueryAsync<(string subcode, decimal bd, decimal bc)>(new CommandDefinition(
                "SELECT rtrim(subcode), ISNULL(bdesum,0), ISNULL(bcresum,0) FROM finsub WITH (NOLOCK) " +
                "WHERE dpm = @dpm AND (type='B' OR type='C') AND ISNULL(detacode,0) = 1",
                new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList()
                .ToDictionary(x => x.subcode, x => (x.bd, x.bc));
        }
        else
        {
            openMap = (await conn.QueryAsync<(string subcode, decimal bd, decimal bc)>(new CommandDefinition(
                "SELECT rtrim(a.subcode), ISNULL(b.bdesum,0), ISNULL(b.bcresum,0) FROM finsub a WITH (NOLOCK) " +
                "LEFT JOIN finhisbal b WITH (NOLOCK) ON rtrim(a.subcode) = rtrim(b.subcode) " +
                "WHERE a.dpm = @dpm AND b.dpm = @dpm AND (a.type='B' OR a.type='C') AND ISNULL(a.detacode,0) = 1",
                new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList()
                .ToDictionary(x => x.subcode, x => (x.bd, x.bc));
            if (openMap.Count == 0)
                openMap = (await conn.QueryAsync<(string subcode, decimal bd, decimal bc)>(new CommandDefinition(
                    "SELECT rtrim(a.subcode), ISNULL(b.bdesum,0), ISNULL(b.bcresum,0) FROM finsub a WITH (NOLOCK) " +
                    "LEFT JOIN finsoursub b WITH (NOLOCK) ON rtrim(a.subcode) = rtrim(b.subcode) " +
                    "WHERE a.dpm = @dpm AND b.dpm = @dpm AND (a.type='B' OR a.type='C') AND ISNULL(a.detacode,0) = 1",
                    new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList()
                    .ToDictionary(x => x.subcode, x => (x.bd, x.bc));
        }
        // S_DATE 非 1 日：期初调整
        if (sDate.Day != 1)
        {
            var adj = (await conn.QueryAsync<(string subcode, decimal debit, decimal credit)>(new CommandDefinition(
                "SELECT rtrim(subcode), ISNULL(SUM(debit),0), ISNULL(SUM(credit),0) FROM finvouch WITH (NOLOCK) " +
                "WHERE date >= @bDate AND date <= @sDate" + postCond + " AND (type='B' OR type='C') GROUP BY rtrim(subcode)",
                new { bDate = new DateTime(sDate.Year, sDate.Month, 1), sDate }, cancellationToken: ct))).AsList();
            foreach (var a in adj)
                if (openMap.TryGetValue(a.subcode, out var v))
                    openMap[a.subcode] = (v.bd + a.debit, v.bc + a.credit);
        }
        // 明细（date 窗口）
        var eDate1 = eDate.AddDays(1);
        var vouchers = (await conn.QueryAsync<(int vouchid, int vcode, DateTime? date, string vouword, string opertype,
            string opercode, decimal debit, decimal credit, string dcdir, decimal balance, bool? fincheck, DateTime? chedate,
            string note, string subcode, string client)>(new CommandDefinition(
            $"""
            SELECT vouchid, ISNULL(vcode,0), date, rtrim(vouword), ISNULL(rtrim(opertype),''), rtrim(ISNULL(opercode,'')),
                   ISNULL(debit,0), ISNULL(credit,0), rtrim(ISNULL(dcdir,'')), ISNULL(balance,0), ISNULL(fincheck,0), chedate,
                   rtrim(ISNULL(note,'')), rtrim(subcode), rtrim(ISNULL(client,''))
            FROM finvouch WITH (NOLOCK)
            WHERE date >= @sDate AND date < @eDate1{postCond} AND (type='B' OR type='C')
            ORDER BY vouchid, recoid
            """, new { sDate, eDate1 }, cancellationToken: ct))).AsList();
        // 汇总（finrecout LEFT JOIN 期初+Σ）
        var recouts = (await conn.QueryAsync<(int id, string accname, string subcode, string subname, string type, string accounts)>(
            new CommandDefinition(
                "SELECT id, rtrim(accname), rtrim(subcode), rtrim(subname), rtrim(ISNULL(type,'')), rtrim(ISNULL(accounts,'')) " +
                "FROM finrecout WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();
        var recoutRows = recouts.Select(a =>
        {
            openMap.TryGetValue(a.subcode, out var o);
            var d = vouchers.Where(v => v.subcode == a.subcode).ToList();
            return new FinRnpRecoutRow
            {
                Accname = a.accname,
                Ctype = a.type == "C" ? "现金类    " : "银行存款类",
                Accounts = a.accounts, Subname = a.subname,
                Bbalance = o.bd - o.bc, Desum = d.Sum(x => x.debit), Cresum = d.Sum(x => x.credit),
                Ebalance = o.bd - o.bc + d.Sum(x => x.debit - x.credit), Subcode = a.subcode,
            };
        }).ToList();
        // 明细（wordid/opertype 中文名/BALANCE 待滚动；未滚动行输出空）
        var vouchRows = vouchers.Select(v => new FinRnpVouchRow
        {
            Date = v.date, Wordid = v.vouword + "-" + v.vcode,
            Opertype = v.opertype switch { "BBI" => "采购入库", "SRO" => "商品销售", "SEV" => "维修车辆", _ => v.note },
            Opercode = v.opercode, Debit = v.debit == 0 ? null : v.debit, Credit = v.credit == 0 ? null : v.credit,
            Balance = null,   // resum 前置空（旧码未 REPLACE 行初值）
            Fincheck = v.fincheck, Chedate = v.chedate, Note = v.note, Subcode = v.subcode, Client = v.client,
            Vouchid = v.vouchid, Vcode = v.vcode, YwType = v.opertype,
        }).ToList();
        // resum：逐科目（finrecout 顺序）余额滚动
        foreach (var rec in recoutRows)
        {
            var bal = rec.Bbalance;
            foreach (var v in vouchRows.Where(v => v.Subcode == rec.Subcode).OrderBy(v => v.Date))
            {
                bal += (v.Debit ?? 0) - (v.Credit ?? 0);
                v.Balance = bal;
            }
        }
        return new FinRnpLedger
        {
            Uperiod = $"会计期间：{mPeriod[..4]}年{mPeriod[4..]}月", Recout = recoutRows, Vouch = vouchRows,
        };
    }

    /// <summary>出纳手工行（fr_finrnpaddmx：Add INSERT / Edit UPDATE finreclist + finrecout 重算）。</summary>
    public async Task<string> SaveFinreclistAsync(FinReclistCommand cmd, bool isAdd, string dpm, CancellationToken ct = default)
    {
        if (cmd.Vouchid == 0) return "请输入凭证编码！";
        if (cmd.Debit == 0 && cmd.Credit == 0) return "借方和贷方金额必须输入其中一项！";
        if (cmd.Debit != 0 && cmd.Credit != 0) return "借方和贷方金额只能输入其中一项！";
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            if (isAdd)
            {
                var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM finreclist WITH (NOLOCK) WHERE vouchid = @vouchid AND dpm = @dpm",
                    new { cmd.Vouchid, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
                if (dup > 0) { await tx.RollbackAsync(CancellationToken.None); return "该凭证编码已经存在，请重新输入！"; }
                // finreclist.id 为 IDENTITY（显式插入报错——旧码 VFP SPT 游标 TABLEUPDATE 不含 id，IDENTITY 自动生成）
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO finreclist (dpm, subcode, vouchid, date, note, debit, credit)
                    VALUES (@dpm, @subcode, @vouchid, @date, @note, @debit, @credit)
                    """,
                    new
                    {
                        dpm = Ansi(dpm, 10), subcode = Ansi(cmd.Subcode, 18), cmd.Vouchid, cmd.Date,
                        note = Ansi(cmd.Note, 30), debit = (double)cmd.Debit, credit = (double)cmd.Credit,
                    }, transaction: tx, cancellationToken: ct));
            }
            else
            {
                if (cmd.Id is not int id) { await tx.RollbackAsync(CancellationToken.None); return "修改失败！"; }
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE finreclist SET subcode = @subcode, vouchid = @vouchid, date = @date, note = @note,
                                          debit = @debit, credit = @credit
                    WHERE id = @id AND dpm = @dpm
                    """,
                    new
                    {
                        subcode = Ansi(cmd.Subcode, 18), cmd.Vouchid, cmd.Date, note = Ansi(cmd.Note, 30),
                        debit = (double)cmd.Debit, credit = (double)cmd.Credit, id, dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
            }
            // finrecout 重算（SUM finreclist 该科目 → desum/cresum + ebalance=bbalance+ΣD-ΣC）
            var sums = await conn.QueryFirstOrDefaultAsync<(decimal desum, decimal cresum)>(new CommandDefinition(
                "SELECT ISNULL(SUM(debit),0), ISNULL(SUM(credit),0) FROM finreclist WITH (NOLOCK) WHERE rtrim(subcode) = @subcode AND dpm = @dpm",
                new { subcode = Ansi(cmd.Subcode, 18), dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE finrecout SET desum = @desum, cresum = @cresum,
                                     ebalance = ISNULL(bbalance,0) + @desum - @cresum
                WHERE rtrim(subcode) = @subcode AND dpm = @dpm
                """,
                new { desum = (double)sums.desum, cresum = (double)sums.cresum, subcode = Ansi(cmd.Subcode, 18), dpm = Ansi(dpm, 10) },
                transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return "";
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ─────────────────────────────── 公共 ───────────────────────────────

    private async Task<Dictionary<string, string>> GSubNamesAsync(SqlConnection conn, IEnumerable<string> subcodes,
        CancellationToken ct)
    {
        var codes = subcodes.Distinct().ToList();
        var all = (await conn.QueryAsync<(string subcode, string subname)>(new CommandDefinition(
            "SELECT rtrim(subcode), rtrim(subname) FROM finsub WITH (NOLOCK)", cancellationToken: ct))).AsList();
        // GSUBNAME 层级名：父名 - 子名（取与 FinOpVouchRepository.GSubName 相同语义）
        var map = new Dictionary<string, string>();
        foreach (var code in codes)
        {
            var name = all.FirstOrDefault(x => x.subcode == code).subname ?? "";
            // 逐级上溯（位宽 3,3,3,2,2,2）
            var parts = new List<string>();
            var j = code.Length;
            while (j >= 3)
            {
                var prefix = code[..j];
                var pn = all.FirstOrDefault(x => x.subcode == prefix).subname;
                if (!string.IsNullOrEmpty(pn)) parts.Insert(0, pn);
                j -= SubWidth(j);
            }
            map[code] = parts.Count > 1 ? string.Join(" - ", parts) : name;
        }
        return map;
    }

    private static int SubWidth(int len)
    {
        int[] lens = [3, 3, 3, 2, 2, 2];
        var acc = 0;
        foreach (var l in lens)
        {
            if (len <= acc + l) return l;
            acc += l;
        }
        return lens[^1];
    }

    private static DateTime MonthEnd(string period)
    {
        var y = int.Parse(period[..4]);
        var m = int.Parse(period[4..]);
        return m == 12 ? new DateTime(y, 12, 31) : new DateTime(y, m + 1, 1).AddDays(-1);
    }

    private static string MaxPeriod(string a, string b) => string.CompareOrdinal(a, b) > 0 ? a : b;

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}
