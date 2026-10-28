using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Finance;

// =====================================================================================
// 账务第九批（I.5 报表+现金流链 / I.8 辅助工具 / N 组固定资产）—— 契约：开发文档/契约摘录/账务第九批契约.md
// 报表三模板（finprol/finbs/fincash）= 载入 + 表达式引擎求值 + Save 全量重写（DELETE WHERE dpm + INSERT）。
// 表达式引擎（pg_proc L2552-2700 移植）：GOPERATOR/GSUBCODE/GEXPRESS/GETSUBN（FINOSUB=finsub 当期 / finhisbal 历史期）。
// 现金流链：fincashba 初始 UPDATE → fincadistr 分配（差额校验+finvouch.cashdis）→ 现金流量表取数。
// 工具：fintool 汇总（MPALSUM 旧 bug 照抄）+ 帐簿初始化（八表清空+finsoursub 恢复恒失败静默）；固定资产 perassty/perchgty。
// =====================================================================================

/// <summary>试算平衡行（finsub/finhisbal 同构列）。</summary>
public sealed class FinTrialRow
{
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public string Fdc { get; set; } = "";
    public decimal Ybdesum { get; set; }
    public decimal Ybcresum { get; set; }
    public decimal Bdesum { get; set; }
    public decimal Bcresum { get; set; }
    public decimal Cdesum { get; set; }
    public decimal Ccresum { get; set; }
    public decimal Ydesum { get; set; }
    public decimal Ycresum { get; set; }
    public decimal Edesum { get; set; }
    public decimal Ecresum { get; set; }
}

/// <summary>凭证汇总行（fincolle）。</summary>
public sealed class FinVouchSummaryRow
{
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

/// <summary>损益表行。</summary>
public sealed class FinProlRow
{
    public int Reid { get; set; }
    public string Itemna { get; set; } = "";
    public string Itemty { get; set; } = "";
    public decimal Csum { get; set; }
    public decimal Nsum { get; set; }
    public string Cexpress { get; set; } = "";
    public string Nexpress { get; set; } = "";
    public DateTime? Date { get; set; }
}

/// <summary>资产负债表行。</summary>
public sealed class FinBsRow
{
    public int Reno { get; set; }
    public string Asset { get; set; } = "";
    public int Areid { get; set; }
    public decimal Absum { get; set; }
    public decimal Aesum { get; set; }
    public string Aexpresb { get; set; } = "";
    public string Aexprese { get; set; } = "";
    public string Queer { get; set; } = "";
    public int Qreid { get; set; }
    public decimal Qbsum { get; set; }
    public decimal Qesum { get; set; }
    public string Qexpresb { get; set; } = "";
    public string Qexprese { get; set; } = "";
    public DateTime? Date { get; set; }
}

/// <summary>现金流量表行。</summary>
public sealed class FinCashRow
{
    public int Id { get; set; }
    public string Cashid { get; set; } = "";
    public string Cashna { get; set; } = "";
    public int Reid { get; set; }
    public decimal Balance { get; set; }
    public string Express { get; set; } = "";
    public DateTime? Date { get; set; }
}

/// <summary>现金流初始行（fincashba）。</summary>
public sealed class FinCashbaRow
{
    public string Period { get; set; } = "";
    public string Cashid { get; set; } = "";
    public string Cashna { get; set; } = "";
    public decimal Iodir { get; set; }
    public decimal Desum { get; set; }
    public decimal Cresum { get; set; }
}

/// <summary>凭证现金流分配命令（fr_fincashdis QueryUnload）。</summary>
public sealed class FinCashDisCommand
{
    public int Vouchid { get; set; }
    public string Period { get; set; } = "";       // 凭证月 2 位
    public DateTime Date { get; set; }
    public List<(string Cashid, string Cashna, int Iodir, decimal Desum, decimal Cresum)> Lines { get; set; } = [];
}

/// <summary>固定资产类别行。</summary>
public sealed class PerasstyRow
{
    public int Perasid { get; set; }
    public string Perasty { get; set; } = "";
    public int Deprty { get; set; }
    public decimal Remrate { get; set; }
}

/// <summary>固定资产变动方式行。</summary>
public sealed class PerchgtyRow
{
    public int Chgid { get; set; }
    public string Chgna { get; set; } = "";
    public int Chgty { get; set; }
    public string Subcode { get; set; } = "";
    public string Vouword { get; set; } = "";
    public string Vnote { get; set; } = "";
}

public interface IFinReportRepository
{
    Task<IReadOnlyList<FinTrialRow>> GetTrialBalanceAsync(string period, string? sub0, int subleng0, bool exactLevel, string dpm, CancellationToken ct = default);
    Task<(IReadOnlyList<FinVouchSummaryRow> Rows, int VouchCount, decimal AbillSum)> GetVouchSummaryAsync(DateTime bDate, DateTime eDate, string vouword, int postMode, int subleng0, string dpm, CancellationToken ct = default);
    Task<IReadOnlyList<FinProlRow>> GetProlAsync(string dpm, CancellationToken ct = default);
    Task<IReadOnlyList<FinBsRow>> GetBsAsync(string dpm, CancellationToken ct = default);
    Task<IReadOnlyList<FinCashRow>> GetCashAsync(string dpm, CancellationToken ct = default);
    Task SumProlAsync(string period, bool useHisbal, string dpm, DateTime? now = null, CancellationToken ct = default);
    Task SumBsAsync(string period, bool useHisbal, string dpm, DateTime? now = null, CancellationToken ct = default);
    Task SumCashAsync(string period, bool useHisbal, string dpm, DateTime? now = null, CancellationToken ct = default);
    Task SaveProlAsync(IReadOnlyList<FinProlRow> rows, string dpm, CancellationToken ct = default);
    Task SaveBsAsync(IReadOnlyList<FinBsRow> rows, string dpm, CancellationToken ct = default);
    Task SaveCashAsync(IReadOnlyList<FinCashRow> rows, string dpm, CancellationToken ct = default);
    Task<IReadOnlyList<FinCashbaRow>> GetCashbaAsync(int month, string dpm, CancellationToken ct = default);
    Task UpdateCashbaAsync(IReadOnlyList<FinCashbaRow> rows, string dpm, CancellationToken ct = default);
    Task<string> SaveCashDisAsync(FinCashDisCommand cmd, decimal vouchDiff, string dpm, CancellationToken ct = default);
    Task ResetCashDisAsync(int vouchid, string dpm, CancellationToken ct = default);
    Task ToolSumAsync(string dpm, int subleng1, CancellationToken ct = default);
    Task ToolInitAsync(string dpm, bool clearVouch, bool restoreSource, CancellationToken ct = default);
    Task<IReadOnlyList<PerasstyRow>> GetPerasstyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PerchgtyRow>> GetPerchgtyAsync(CancellationToken ct = default);
    Task AddPerasstyAsync(PerasstyRow row, string dpm, CancellationToken ct = default);
    Task UpdatePerasstyAsync(PerasstyRow row, string dpm, CancellationToken ct = default);
    Task AddPerchgtyAsync(PerchgtyRow row, string dpm, CancellationToken ct = default);
    Task<decimal> GetVouchDiffAsync(int vouchid, CancellationToken ct = default);
}

public sealed class FinReportRepository(SqlConnectionFactory factory) : IFinReportRepository
{
    // ───────────────────── 表达式引擎（pg_proc L2552-2700 移植） ─────────────────────

    /// <summary>GOPERATOR：第 i 个运算符（'()+-*/:'；i=0 → ''）。</summary>
    private static string GOperator(string cstr, int i)
    {
        var ops = "()+-*/:".ToCharArray();
        var n = 0;
        for (var m = 0; m < cstr.Trim().Length; m++)
        {
            if (ops.Contains(cstr[m]))
            {
                n++;
                if (n == i) return cstr[m].ToString();
            }
        }
        return "";
    }

    /// <summary>GSUBCODE：第 i 个操作数（空段→'TTT'；无 +/- 且 i=1 → 整串）。</summary>
    private static string GSubcode(string cstr, int i)
    {
        if (string.IsNullOrEmpty(cstr)) return "";
        if (!cstr.Contains('+') && !cstr.Contains('-') && i == 1) return cstr;
        var ops = "()+-*/:".ToCharArray();
        var spl = 0;
        var epl = 0;
        var n = 0;
        var leng = cstr.Trim().Length;
        for (var m = 0; m < leng; m++)
        {
            if (!ops.Contains(cstr[m])) continue;
            n++;
            if (n == i - 1) spl = m + 1;
            if (n == i) { epl = m + 1; break; }
        }
        if (spl <= 0 && epl <= 0) return "";
        if (spl > 0 && epl == 0) epl = leng + 1;
        var s0 = spl;   // VFP SPL+1
        var e0 = epl - 1;
        if (s0 >= e0) return e0 - s0 + 1 == 0 ? "TTT" : "";
        return cstr[s0..e0];
    }

    /// <summary>GEXPRESS：SUM 表达式展开 S(cy:b:cy:e) → cy{b}+...+cy{e}。</summary>
    private static string GExpress(string cstr, string cy)
    {
        var pat0 = cstr.IndexOf('S');
        var pat1 = cstr.IndexOf('(');
        var pat2 = cstr.IndexOf(':');
        var pat3 = cstr.IndexOf(')');
        if (pat0 < 0 || pat1 < 0 || pat2 < 0 || pat3 < 0 || pat1 + 2 > pat2 || pat2 + 2 > pat3) return cstr;
        if (!int.TryParse(cstr.Substring(pat1 + 2, pat2 - pat1 - 2).Trim(), out var bexp)) return cstr;
        if (!int.TryParse(cstr.Substring(pat2 + 2, pat3 - pat2 - 2).Trim(), out var eexp)) return cstr;
        var parts = new List<string>();
        for (var j = bexp; j <= eexp; j++) parts.Add(cy + j);
        return cstr[..pat0] + string.Join("+", parts) + cstr[(pat3 + 1)..];
    }

    /// <summary>FINOSUB 行（finsub/finhisbal 同构余额列）。</summary>
    private sealed record SubBalRow(string Subcode, string Fdc, decimal Ybdesum, decimal Ybcresum,
        decimal Cdesum, decimal Ccresum, decimal Ydesum, decimal Ycresum,
        decimal Edesum, decimal Ecresum, decimal Mpalsum, decimal Ypalsum);

    private async Task<List<SubBalRow>> LoadFinoSubAsync(SqlConnection conn, string period, bool useHisbal, string dpm, CancellationToken ct)
    {
        const string cols = "rtrim(subcode), rtrim(ISNULL(fdc,'')), ISNULL(ybdesum,0), ISNULL(ybcresum,0), ISNULL(cdesum,0), ISNULL(ccresum,0), " +
                            "ISNULL(ydesum,0), ISNULL(ycresum,0), ISNULL(edesum,0), ISNULL(ecresum,0), ISNULL(mpalsum,0), ISNULL(ypalsum,0)";
        var raw = useHisbal
            ? (await conn.QueryAsync<(string subcode, string fdc, double ybd, double ybc, double cd, double cc,
                double yd, double yc, double ed, double ec, double mp, double yp)>(new CommandDefinition(
                $"SELECT {cols} FROM finhisbal WITH (NOLOCK) WHERE period = @period AND dpm = @dpm ORDER BY rtrim(subcode)",
                new { period = Ansi(period, 6), dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList()
            : (await conn.QueryAsync<(string subcode, string fdc, double ybd, double ybc, double cd, double cc,
                double yd, double yc, double ed, double ec, double mp, double yp)>(new CommandDefinition(
                $"SELECT {cols} FROM finsub WITH (NOLOCK) WHERE dpm = @dpm ORDER BY rtrim(subcode)",
                new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();
        return raw.Select(x => new SubBalRow(x.subcode, x.fdc, (decimal)x.ybd, (decimal)x.ybc, (decimal)x.cd, (decimal)x.cc,
            (decimal)x.yd, (decimal)x.yc, (decimal)x.ed, (decimal)x.ec, (decimal)x.mp, (decimal)x.yp)).ToList();
    }

    /// <summary>GETSUBN：科目取值（16 种 BATY；NDC=fdc='C'?-1:1）。</summary>
    private static decimal GetSubn(List<SubBalRow> finosub, string mSub)
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
        if (baty.Length == 0) baty = "E";
        var row = finosub.FirstOrDefault(x => x.Subcode == cSub);
        if (row is null) return 0;
        var ndc = row.Fdc == "C" ? -1 : 1;
        return baty switch
        {
            "B" => ndc * (row.Ybdesum - row.Ybcresum),
            "DB" => row.Ybdesum,
            "CB" => row.Ybcresum,
            "AB" => Math.Abs(row.Ybdesum - row.Ybcresum),
            "E" => ndc * (row.Edesum - row.Ecresum),
            "DE" => row.Edesum,
            "CE" => row.Ecresum,
            "AE" => Math.Abs(row.Edesum - row.Ecresum),
            "DC" => row.Cdesum,
            "CC" => row.Ccresum,
            "DY" => row.Ydesum,
            "CY" => row.Ycresum,
            "SX" => row.Fdc == "C" ? row.Ccresum : row.Cdesum,
            "SY" => row.Mpalsum,
            "SL" => row.Ypalsum,
            _ => 0,
        };
    }

    /// <summary>表达式求值（报表 Sum 通用：模板行 'C{n}'/'G{n}' 引用 + 科目 GETSUBN）。</summary>
    private static decimal EvalExpress(string exp, int i, Func<int, decimal> selfRowValue, List<SubBalRow> finosub)
    {
        decimal sum = 0;
        while (true)
        {
            var sub = GSubcode(exp, i);
            var oper = GOperator(exp, i - 1);
            if (string.IsNullOrEmpty(sub)) break;
            if (sub.Contains('C') && sub.Length > 1 && int.TryParse(sub[1..], out var reid))
            {
                var v = selfRowValue(reid);
                sum += (oper != "-" ? 1 : -1) * v;
            }
            else
            {
                sum += (oper != "-" ? 1 : -1) * GetSubn(finosub, sub);
            }
            i++;
        }
        return sum;
    }

    // ───────────────────── 试算平衡（fr_finbalanf） ─────────────────────

    public async Task<IReadOnlyList<FinTrialRow>> GetTrialBalanceAsync(string period, string? sub0, int subleng0,
        bool exactLevel, string dpm, CancellationToken ct = default)
    {
        var useHisbal = period != await CurrentPeriodAsync(dpm, ct);
        var cond = exactLevel
            ? "LEN(rtrim(subcode)) = @subleng0"
            : "LEN(rtrim(subcode)) <= @subleng0";
        if (!string.IsNullOrWhiteSpace(sub0)) cond += exactLevel ? " AND rtrim(subcode) = @sub0" : " AND LEFT(rtrim(subcode), @subleng1) = LEFT(@sub0, @subleng1)";
        await using var conn = await factory.OpenAsync(ct);
        var table = useHisbal ? "finhisbal" : "finsub";
        var periodCond = useHisbal ? " AND period = @period" : "";
        var rows = await conn.QueryAsync<FinTrialRow>(new CommandDefinition(
            $"""
            SELECT rtrim(subcode) subcode, rtrim(subname) subname, rtrim(ISNULL(fdc,'')) fdc,
                   ISNULL(ybdesum,0) ybdesum, ISNULL(ybcresum,0) ybcresum,
                   ISNULL(bdesum,0) bdesum, ISNULL(bcresum,0) bcresum,
                   ISNULL(cdesum,0) cdesum, ISNULL(ccresum,0) ccresum,
                   ISNULL(ydesum,0) ydesum, ISNULL(ycresum,0) ycresum,
                   ISNULL(edesum,0) edesum, ISNULL(ecresum,0) ecresum
            FROM {table} WITH (NOLOCK)
            WHERE {cond}{periodCond}
            ORDER BY rtrim(subcode)
            """,
            new
            {
                subleng0, subleng1 = 3, sub0 = Ansi(sub0 ?? "", 18), period = Ansi(period, 6), dpm = Ansi(dpm, 10),
            }, cancellationToken: ct));
        return rows.AsList();
    }

    // ───────────────────── 凭证汇总表（fr_fincollef） ─────────────────────

    public async Task<(IReadOnlyList<FinVouchSummaryRow> Rows, int VouchCount, decimal AbillSum)> GetVouchSummaryAsync(
        DateTime bDate, DateTime eDate, string vouword, int postMode, int subleng0, string dpm, CancellationToken ct = default)
    {
        var cond = "date >= @bDate AND date <= @eDate";
        if (vouword != "(全部)" && !string.IsNullOrWhiteSpace(vouword)) cond += " AND rtrim(vouword) = @vouword";
        if (postMode == 2) cond += " AND post = 1";
        if (postMode == 3) cond += " AND ISNULL(post,0) = 0";
        await using var conn = await factory.OpenAsync(ct);
        var p = new { bDate, eDate, vouword = Ansi(vouword, 4) };
        var stat = await conn.QueryFirstAsync<(int cren, decimal abill)>(new CommandDefinition(
            $"SELECT COUNT(*), ISNULL(SUM(abill),0) FROM finvouch WITH (NOLOCK) WHERE {cond}", p, cancellationToken: ct));
        var raw = (await conn.QueryAsync<(string subcode, string subname, decimal debit, decimal credit)>(new CommandDefinition(
            $"SELECT rtrim(subcode), rtrim(subname), ISNULL(SUM(debit),0), ISNULL(SUM(credit),0) FROM finvouch WITH (NOLOCK) WHERE {cond} GROUP BY rtrim(subcode), rtrim(subname) ORDER BY rtrim(subcode)",
            p, cancellationToken: ct))).AsList();
        // 级次截断合并（m.SUBCODE=LEFT(SUBCODE,SUBLENG0)，GSUBNAME，SEEK 合并累计）
        var merged = new Dictionary<string, FinVouchSummaryRow>();
        foreach (var r in raw)
        {
            var code = r.subcode.Length > subleng0 ? r.subcode[..subleng0] : r.subcode;
            if (merged.TryGetValue(code, out var row))
            {
                row.Debit += r.debit;
                row.Credit += r.credit;
            }
            else
            {
                merged[code] = new FinVouchSummaryRow { Subcode = code, Subname = r.subname, Debit = r.debit, Credit = r.credit };
            }
        }
        return (merged.Values.OrderBy(x => x.Subcode).ToList(), stat.cren, stat.abill);
    }

    // ───────────────────── 报表三模板（finprol/finbs/fincash） ─────────────────────

    public async Task<IReadOnlyList<FinProlRow>> GetProlAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinProlRow>(new CommandDefinition(
            """
            SELECT reid, rtrim(ISNULL(itemna,'')) itemna, rtrim(ISNULL(itemty,'')) itemty, ISNULL(csum,0) csum, ISNULL(nsum,0) nsum,
                   rtrim(ISNULL(cexpress,'')) cexpress, rtrim(ISNULL(nexpress,'')) nexpress, date
            FROM finprol WITH (NOLOCK) WHERE dpm = @dpm ORDER BY reid
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FinBsRow>> GetBsAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinBsRow>(new CommandDefinition(
            """
            SELECT reno, rtrim(ISNULL(asset,'')) asset, ISNULL(areid,0) areid, ISNULL(absum,0) absum, ISNULL(aesum,0) aesum,
                   rtrim(ISNULL(aexpresb,'')) aexpresb, rtrim(ISNULL(aexprese,'')) aexprese, rtrim(ISNULL(queer,'')) queer,
                   ISNULL(qreid,0) qreid, ISNULL(qbsum,0) qbsum, ISNULL(qesum,0) qesum,
                   rtrim(ISNULL(qexpresb,'')) qexpresb, rtrim(ISNULL(qexprese,'')) qexprese, date
            FROM finbs WITH (NOLOCK) WHERE dpm = @dpm ORDER BY reno
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FinCashRow>> GetCashAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinCashRow>(new CommandDefinition(
            """
            SELECT id, rtrim(ISNULL(cashid,'')) cashid, rtrim(ISNULL(cashna,'')) cashna, ISNULL(reid,0) reid,
                   ISNULL(balance,0) balance, rtrim(ISNULL(express,'')) express, date
            FROM fincash WITH (NOLOCK) WHERE dpm = @dpm ORDER BY reid
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>损益表求值+落库（myclick Sum+Save 一体：cexpress/nexpress 双列独立，GEXPRESS 后同循环 → finprol 全量重写）。</summary>
    public async Task SumProlAsync(string period, bool useHisbal, string dpm, DateTime? now = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await GetProlAsync(dpm, ct)).ToList();
        var finosub = await LoadFinoSubAsync(conn, period, useHisbal, dpm, ct);
        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(r.Cexpress) || !string.IsNullOrWhiteSpace(r.Nexpress))
            {
                var expc = r.Cexpress;
                var expn = r.Nexpress;
                if (expc.ToUpperInvariant().Contains("SUM")) expc = GExpress(expc, "C");
                if (expn.ToUpperInvariant().Contains("SUM")) expn = GExpress(expn, "D");
                var selfC = new Func<int, decimal>(reid => rows.FirstOrDefault(x => x.Reid == reid)?.Csum ?? 0);
                var selfN = new Func<int, decimal>(reid => rows.FirstOrDefault(x => x.Reid == reid)?.Nsum ?? 0);
                r.Csum = EvalExpress(expc, 1, selfC, finosub);
                r.Nsum = EvalExpress(expn, 1, selfN, finosub);
            }
            r.Date = now ?? DateTime.Now;
        }
        await SaveProlAsync(rows, dpm, ct);
    }

    /// <summary>资产负债表求值（bsclick Sum：四列独立求值，'C' 引用 AREID/'G' 引用 QREID）。</summary>
    public async Task SumBsAsync(string period, bool useHisbal, string dpm, DateTime? now = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await GetBsAsync(dpm, ct)).ToList();
        var finosub = await LoadFinoSubAsync(conn, period, useHisbal, dpm, ct);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Aexpresb) && string.IsNullOrWhiteSpace(r.Aexprese)
                && string.IsNullOrWhiteSpace(r.Qexpresb) && string.IsNullOrWhiteSpace(r.Qexprese)) continue;
            var expb = r.Aexpresb;
            var expe = r.Aexprese;
            var qexpb = r.Qexpresb;
            var qexpe = r.Qexprese;
            if (expb.ToUpperInvariant().Contains("SUM")) expb = GExpress(expb, "C");
            if (expe.ToUpperInvariant().Contains("SUM")) expe = GExpress(expe, "D");
            if (qexpb.ToUpperInvariant().Contains("SUM")) qexpb = GExpress(qexpb, "G");
            if (qexpe.ToUpperInvariant().Contains("SUM")) qexpe = GExpress(qexpe, "H");
            var selfA = new Func<int, decimal>(reid => rows.FirstOrDefault(x => x.Areid == reid)?.Absum ?? 0);
            var selfQ = new Func<int, decimal>(reid => rows.FirstOrDefault(x => x.Qreid == reid)?.Qbsum ?? 0);
            r.Absum = EvalExpress(expb, 1, selfA, finosub);
            r.Aesum = EvalExpress(expe, 1, reid => rows.FirstOrDefault(x => x.Areid == reid)?.Aesum ?? 0, finosub);
            r.Qbsum = EvalExpress(qexpb, 1, selfQ, finosub);
            r.Qesum = EvalExpress(qexpe, 1, reid => rows.FirstOrDefault(x => x.Qreid == reid)?.Qesum ?? 0, finosub);
            r.Date = now ?? DateTime.Now;
        }
        await SaveBsAsync(rows, dpm, ct);
    }

    /// <summary>现金流量表求值（caclick Sum：非模板操作数取 fincadistr + fincashba）。</summary>
    public async Task SumCashAsync(string period, bool useHisbal, string dpm, DateTime? now = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await GetCashAsync(dpm, ct)).ToList();
        var finosub = await LoadFinoSubAsync(conn, period, useHisbal, dpm, ct);
        var cperiodM = period;
        var distrib = (await conn.QueryAsync<(string cashid, int iodir, decimal desum, decimal cresum)>(new CommandDefinition(
            "SELECT rtrim(cashid), ISNULL(iodir,0), ISNULL(desum,0), ISNULL(cresum,0) FROM fincadistr WITH (NOLOCK) WHERE period = @period AND dpm = @dpm",
            new { period = Ansi(period, 6), dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();
        // fincashba：CPERIODM 长>4 按月（TRANSFORM(PERIOD,'@ZL 99')=RIGHT(CPERIODM,2)）；否则全月 SUM
        var byMonth = cperiodM.Length > 4;
        var monthKey = byMonth ? cperiodM[^2..].TrimStart('0') : "";
        var cashba = (await conn.QueryAsync<(string cashid, string period, decimal iodir, decimal desum, decimal cresum)>(new CommandDefinition(
            "SELECT rtrim(cashid), rtrim(period), ISNULL(iodir,0), ISNULL(desum,0), ISNULL(cresum,0) FROM fincashba WITH (NOLOCK) WHERE dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Express)) continue;
            var exp = r.Express;
            if (exp.ToUpperInvariant().Contains("SUM")) exp = GExpress(exp, "C");
            decimal sum = 0;
            var i = 1;
            while (true)
            {
                var cashid = GSubcode(exp, i);
                var oper = GOperator(exp, i - 1);
                if (string.IsNullOrEmpty(cashid)) break;
                if (cashid.Contains('C') && cashid.Length > 1 && int.TryParse(cashid[1..], out var reid))
                {
                    var v = rows.FirstOrDefault(x => x.Reid == reid)?.Balance ?? 0;
                    sum += (oper != "-" ? 1 : -1) * v;
                }
                else
                {
                    foreach (var d in distrib.Where(x => x.cashid == cashid))
                        sum += d.iodir == 1 ? d.desum - d.cresum : d.cresum - d.desum;
                    // fincashba：按月（period=月数字）过滤；否则全表 SUM（SEEK FOUND 判定）
                    IEnumerable<(string cashid, string period, decimal iodir, decimal desum, decimal cresum)> src =
                        byMonth ? cashba.Where(x => x.period.TrimStart('0') == monthKey) : cashba;
                    var agg = src.Where(x => x.cashid == cashid).Sum(x => x.desum - x.cresum);
                    var any = src.Any(x => x.cashid == cashid);
                    if (any)
                        sum += cashba.First(x => x.cashid == cashid).iodir == 1 ? agg : -agg;
                }
                i++;
            }
            r.Balance = sum;
            r.Date = now ?? DateTime.Now;
        }
        await SaveCashAsync(rows, dpm, ct);
    }

    // ───────────────────── 报表 Save（DELETE WHERE dpm + 全量 INSERT 重写） ─────────────────────

    public async Task SaveProlAsync(IReadOnlyList<FinProlRow> rows, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM finprol WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        foreach (var r in rows)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO finprol (dpm, reid, itemna, itemty, csum, nsum, cexpress, nexpress, date)
                VALUES (@dpm, @reid, @itemna, @itemty, @csum, @nsum, @cexpress, @nexpress, @date)
                """,
                new
                {
                    dpm = Ansi(dpm, 10), r.Reid, itemna = Ansi(r.Itemna, 40), itemty = Ansi(r.Itemty, 1),
                    csum = (double)r.Csum, nsum = (double)r.Nsum,
                    cexpress = Ansi(r.Cexpress, 60), nexpress = Ansi(r.Nexpress, 60), r.Date,
                }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task SaveBsAsync(IReadOnlyList<FinBsRow> rows, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM finbs WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        foreach (var r in rows)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO finbs (dpm, reno, asset, areid, absum, aesum, aexpresb, aexprese, queer, qreid, qbsum, qesum, qexpresb, qexprese, date)
                VALUES (@dpm, @reno, @asset, @areid, @absum, @aesum, @aexpresb, @aexprese, @queer, @qreid, @qbsum, @qesum, @qexpresb, @qexprese, @date)
                """,
                new
                {
                    dpm = Ansi(dpm, 10), r.Reno, asset = Ansi(r.Asset, 50), r.Areid,
                    absum = (double)r.Absum, aesum = (double)r.Aesum,
                    aexpresb = Ansi(r.Aexpresb, 60), aexprese = Ansi(r.Aexprese, 60), queer = Ansi(r.Queer, 50),
                    r.Qreid, qbsum = (double)r.Qbsum, qesum = (double)r.Qesum,
                    qexpresb = Ansi(r.Qexpresb, 60), qexprese = Ansi(r.Qexprese, 60), r.Date,
                }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task SaveCashAsync(IReadOnlyList<FinCashRow> rows, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM fincash WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        foreach (var r in rows)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO fincash (dpm, cashid, cashna, reid, balance, express, date)
                VALUES (@dpm, @cashid, @cashna, @reid, @balance, @express, @date)
                """,
                new
                {
                    dpm = Ansi(dpm, 10), cashid = Ansi(r.Cashid, 15), cashna = Ansi(r.Cashna, 60),
                    r.Reid, balance = (double)r.Balance, express = Ansi(r.Express, 40), r.Date,
                }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // ───────────────────── 现金流初始/分配 ─────────────────────

    public async Task<IReadOnlyList<FinCashbaRow>> GetCashbaAsync(int month, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinCashbaRow>(new CommandDefinition(
            """
            SELECT rtrim(period) period, rtrim(cashid) cashid, rtrim(cashna) cashna, ISNULL(iodir,0) iodir, ISNULL(desum,0) desum, ISNULL(cresum,0) cresum
            FROM fincashba WITH (NOLOCK) WHERE dpm = @dpm AND rtrim(period) = @month ORDER BY rtrim(cashid)
            """, new { dpm = Ansi(dpm, 10), month = Ansi(month.ToString(), 2) }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>现金流初始退出保存（fr_fincashba QueryUnload：逐行 UPDATE WHERE period+cashid）。</summary>
    public async Task UpdateCashbaAsync(IReadOnlyList<FinCashbaRow> rows, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        foreach (var r in rows)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE fincashba SET desum = @desum, cresum = @cresum WHERE rtrim(period) = @period AND rtrim(cashid) = @cashid AND dpm = @dpm",
                new
                {
                    desum = (double)r.Desum, cresum = (double)r.Cresum,
                    period = Ansi(r.Period, 2), cashid = Ansi(r.Cashid, 15), dpm = Ansi(dpm, 10),
                }, cancellationToken: ct));
    }

    /// <summary>凭证现金流分配（fr_fincashdis QueryUnload：差额校验 → fincadistr 重写 → finvouch.cashdis=1 → 清 0 行）。
    /// 返回消息（差额不平由调用方走 ResetCashDisAsync）。</summary>
    public async Task<string> SaveCashDisAsync(FinCashDisCommand cmd, decimal vouchDiff, string dpm, CancellationToken ct = default)
    {
        var disDiff = cmd.Lines.Sum(l => l.Desum - l.Cresum);
        if (disDiff != vouchDiff)
            return $"凭证中现金流量类科目借方、贷方差额为:{vouchDiff:0.##}\r\n而现金流量金额分配中流入、流出差额为:{disDiff:0.##}\r\n两者不相等,是否退出?";
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE fincadistr SET desum = 0, cresum = 0 WHERE vouchid = @vouchid AND dpm = @dpm",
            new { cmd.Vouchid, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        foreach (var l in cmd.Lines)
        {
            if (l.Desum == 0 && l.Cresum == 0) continue;
            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM fincadistr WITH (NOLOCK) WHERE rtrim(cashid) = @cashid AND vouchid = @vouchid AND dpm = @dpm",
                new { cashid = Ansi(l.Cashid, 15), cmd.Vouchid, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
            if (exists == 0)
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO fincadistr (dpm, cashid, cashna, iodir, vouchid, period, date, desum, cresum)
                    VALUES (@dpm, @cashid, @cashna, @iodir, @vouchid, @period, @date, @desum, @cresum)
                    """,
                    new
                    {
                        dpm = Ansi(dpm, 10), cashid = Ansi(l.Cashid, 15), cashna = Ansi(l.Cashna, 60), l.Iodir,
                        cmd.Vouchid, period = Ansi(cmd.Period, 6), cmd.Date,
                        desum = (double)l.Desum, cresum = (double)l.Cresum,
                    }, transaction: tx, cancellationToken: ct));
            else
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE fincadistr SET cashna = @cashna, iodir = @iodir, period = @period, date = @date,
                                          desum = @desum, cresum = @cresum
                    WHERE rtrim(cashid) = @cashid AND vouchid = @vouchid AND dpm = @dpm
                    """,
                    new
                    {
                        cashna = Ansi(l.Cashna, 60), l.Iodir, period = Ansi(cmd.Period, 6), cmd.Date,
                        desum = (double)l.Desum, cresum = (double)l.Cresum,
                        cashid = Ansi(l.Cashid, 15), cmd.Vouchid, dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE finvouch SET cashdis = 1 WHERE vouchid = @vouchid AND dpm = @dpm",
            new { cmd.Vouchid, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM fincadistr WHERE ISNULL(desum,0) = 0 AND ISNULL(cresum,0) = 0 AND dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return "";
    }

    /// <summary>凭证现金流差额（Σ fcash=.T. 行 debit-credit，fr_fincashdis Init 语义）。</summary>
    public async Task<decimal> GetVouchDiffAsync(int vouchid, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT ISNULL(SUM(debit-credit),0) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vouchid AND ISNULL(fcash,0) = 1",
            new { vouchid }, cancellationToken: ct));
    }

    /// <summary>分配差额不平退出（=6：cashdis=0 重置 + DELETE fincadistr WHERE vouchid）。</summary>
    public async Task ResetCashDisAsync(int vouchid, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE finvouch SET cashdis = 0 WHERE vouchid = @vouchid AND dpm = @dpm",
            new { vouchid, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM fincadistr WHERE vouchid = @vouchid AND dpm = @dpm",
            new { vouchid, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // ───────────────────── 辅助工具（fr_fintool） ─────────────────────

    /// <summary>汇总到父级（row5）：detacode 重算 → 12 列清零 → 末级前缀链汇总。
    /// **旧码 bug 照抄：父级 MPALSUM 累加的是自身 YPALSUM（REPLACE MPALSUM WITH YPALSUM + m.MPALSUM）**。</summary>
    public async Task ToolSumAsync(string dpm, int subleng1, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // detacode 重算（全库 LOCATE 语义）
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE s SET detacode = CASE WHEN EXISTS (
                SELECT 1 FROM finsub c
                WHERE LEFT(LTRIM(RTRIM(c.subcode)), LEN(LTRIM(RTRIM(s.subcode)))) = LTRIM(RTRIM(s.subcode))
                  AND LEN(LTRIM(RTRIM(c.subcode))) > LEN(LTRIM(RTRIM(s.subcode)))
            ) THEN 0 ELSE 1 END
            FROM finsub s WHERE s.dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        var leaves = (await conn.QueryAsync<(string subcode, decimal ybd, decimal ybc, decimal bd, decimal bc,
            decimal cd, decimal cc, decimal yd, decimal yc, decimal ed, decimal ec, decimal mp, decimal yp)>(new CommandDefinition(
            """
            SELECT rtrim(subcode), ISNULL(ybdesum,0), ISNULL(ybcresum,0), ISNULL(bdesum,0), ISNULL(bcresum,0),
                   ISNULL(cdesum,0), ISNULL(ccresum,0), ISNULL(ydesum,0), ISNULL(ycresum,0), ISNULL(edesum,0), ISNULL(ecresum,0),
                   ISNULL(mpalsum,0), ISNULL(ypalsum,0)
            FROM finsub WITH (NOLOCK) WHERE dpm = @dpm AND ISNULL(detacode,0) = 1 ORDER BY rtrim(subcode)
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).AsList();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE finsub SET ybdesum = 0, ybcresum = 0, bdesum = 0, bcresum = 0, cdesum = 0, ccresum = 0,
                              ydesum = 0, ycresum = 0, edesum = 0, ecresum = 0, mpalsum = 0, ypalsum = 0
            WHERE dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        foreach (var lf in leaves)
        {
            var i = lf.subcode.Length;
            while (i >= subleng1 && i <= lf.subcode.Length)
            {
                var psub = lf.subcode[..i];
                var found = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @psub",
                    new { psub = Ansi(psub, 18) }, cancellationToken: ct)) > 0;
                if (found)
                {
                    // 旧码：mpalsum = ypalsum(父行当前) + m.mpalsum（bug 原样）；ypalsum = ypalsum + m.ypalsum
                    await conn.ExecuteAsync(new CommandDefinition(
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
                        }, cancellationToken: ct));
                }
                i -= SubWidth(i);
            }
        }
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

    /// <summary>帐簿初始化（row10）：八表清空 → 凭证库清空或重置 → finsoursub 恢复（INSERT 列数不匹配静默失败照抄）或余额回写。</summary>
    public async Task ToolInitAsync(string dpm, bool clearVouch, bool restoreSource, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        foreach (var table in new[] { "finaleger", "finsubit", "finitsub", "finamsum", "finrecout", "finreclist", "fincadistr", "finhisbal" })
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        if (clearVouch)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM finvouch WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finvid SET vid = 0 WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finvouch SET balance = 0, dcdir = '', post = 0, cashdis = 0, poster = '' WHERE dpm = @dpm",
                new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        }
        var hasSource = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM (SELECT TOP (1) 1 AS x FROM finsoursub WITH (NOLOCK)) t", cancellationToken: ct)) > 0;
        if (hasSource && restoreSource)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM finsub WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
            // 列数不匹配（finsoursub 31 列 vs finsub 35 列）——旧系统静默吞掉照抄
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO finsub SELECT * FROM finsoursub WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
            }
            catch
            {
                // 旧系统行为：INSERT 失败静默继续
            }
        }
        else
        {
            // 余额回写（b 侧无 dpm 过滤——旧码原样）+ 非 finsoursub 科目清零
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE a SET a.ybdesum = b.ybdesum, a.ybcresum = b.ybcresum, a.mpalsum = b.mpalsum, a.ypalsum = b.ypalsum,
                             a.bdesum = b.bdesum, a.bcresum = b.bcresum, a.cdesum = 0, a.ccresum = 0,
                             a.ydesum = b.ydesum, a.ycresum = b.ycresum, a.edesum = b.edesum, a.ecresum = b.ecresum
                FROM finsub a, finsoursub b
                WHERE a.dpm = @dpm AND rtrim(a.subcode) = rtrim(b.subcode)
                """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE a SET ybdesum = 0, ybcresum = 0, mpalsum = 0, ypalsum = 0, bdesum = 0, bcresum = 0,
                             cdesum = 0, ccresum = 0, ydesum = 0, ycresum = 0, edesum = 0, ecresum = 0
                FROM finsub a
                WHERE a.dpm = @dpm AND NOT EXISTS (
                    SELECT 1 FROM finsoursub b WHERE b.dpm = @dpm AND rtrim(b.subcode) = rtrim(a.subcode))
                """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        }
    }

    // ───────────────────── 固定资产（perassty/perchgty） ─────────────────────

    public async Task<IReadOnlyList<PerasstyRow>> GetPerasstyAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PerasstyRow>(new CommandDefinition(
            "SELECT perasid, rtrim(ISNULL(perasty,'')) perasty, ISNULL(deprty,0) deprty, ISNULL(remrate,0) remrate FROM perassty WITH (NOLOCK) ORDER BY perasid",
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PerchgtyRow>> GetPerchgtyAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PerchgtyRow>(new CommandDefinition(
            """
            SELECT chgid, rtrim(ISNULL(chgna,'')) chgna, ISNULL(chgty,0) chgty, rtrim(ISNULL(subcode,'')) subcode,
                   rtrim(ISNULL(vouword,'')) vouword, rtrim(ISNULL(vnote,'')) vnote
            FROM perchgty WITH (NOLOCK) ORDER BY chgid
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task AddPerasstyAsync(PerasstyRow row, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO perassty (perasty, deprty, remrate) VALUES (@perasty, @deprty, @remrate)",
            new { perasty = Ansi(row.Perasty, 20), row.Deprty, remrate = (double)row.Remrate },
            cancellationToken: ct));
    }

    public async Task UpdatePerasstyAsync(PerasstyRow row, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE perassty SET perasty = @perasty, deprty = @deprty, remrate = @remrate WHERE perasid = @perasid",
            new { perasty = Ansi(row.Perasty, 20), row.Deprty, remrate = (double)row.Remrate, row.Perasid },
            cancellationToken: ct));
    }

    /// <summary>变动方式新增（fr_perchgty Add：INSERT perchgty；perchgty 无 dpm 列）。</summary>
    public async Task AddPerchgtyAsync(PerchgtyRow row, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO perchgty (chgna, chgty, subcode, vouword, vnote)
            VALUES (@chgna, @chgty, @subcode, @vouword, @vnote)
            """,
            new
            {
                chgna = Ansi(row.Chgna, 20), row.Chgty,
                subcode = Ansi(row.Subcode, 20), vouword = Ansi(row.Vouword, 6), vnote = Ansi(row.Vnote, 40),
            }, cancellationToken: ct));
    }

    private async Task<string> CurrentPeriodAsync(string dpm, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT RTRIM(period) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct)) ?? "";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}
