using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Finance;

// =====================================================================================
// I.2 业务凭证（fr_finopvouch + fr_finoperation）—— 契约：开发文档/契约摘录/账务管理I组凭证闭环契约.md §6
//   生成（fr_finoperation myclick → longsky_bbi/longsky_sro，部署实测变体）：
//     Fvtmp 准备链（row9 Click）：tbistoed/tbsada 按日期+单号区间+付款方式选项拉取 → 按单据分组（fingenvoustyle=.F.）
//     BBI 借 135001=Σ(jkamount×iprc)；贷按 fkfs 分派（现金/空→cashsub C 付、支票汇票汇款刷卡→balasub B 付、
//     否则 ADDSUB(供应商,'P') P 转）=SUM0-SUMFRE；SUMFRE≠0 追加贷 运费行（finsub_auto 池'运费Y'→rscar 池空=空码，recoid 复用 2）
//     SRO 借按 guno 分派（同上、ADDSUB(客户,'A')、字'收'）=GETJE(Σ(ckamount×oprct×(100-yhl)/100))；
//     VOU_TYPE=3（finopvou.valuen=3 部署实测）加成本对 511002/135002；贷 501002=SUM0（recoid=4）
//     已入账检测：finvouch WHERE rtrim(opercode)=单号 AND opertype → 有行回显已审分录；否则游标预览不落库
//   审核（fr_finopvouch Audit）：finvid(dpm) 推进取新 vouchid → vcode=期间 max+1 → INSERT finvouch(audit=1)
//     → opaudit 回写业务单据 audit（BBI→tbistoed.cno / SRO/SMO→tbsada.paper，均带 dpm）
//   反审核（Unaudit）：已审/已过帐拦截 → DELETE finvouch WHERE vouchid + opaudit 回写 0
//   ADDSUB（pg_proc L2447，longsky 变体使用）：主科目(122/204)兜底建档 → subname+type 查找 →
//     子码窗口 LEN=累计位宽6（ORDER BY 子码需 BIN2 对齐 VFP MACHINE 排序，否则中文排序 '['<'Z' 会取错最大码）
//     尾码 '001'→'999'→'A00'→字母进位；'`99' 终止 → finsub_auto 池回退（rscar 池空报"无可使用科目代码！"，
//     实测 122 系列已耗尽至 '`99'、204 至 '204623'）
// =====================================================================================

/// <summary>生成查询条件（fr_finoperation 选择条件窗）。</summary>
public sealed class FinOpGenerateQuery
{
    public string Opty { get; set; } = "SRO";          // SRO 零售出库 / SMO 调拨出库 / BBI 散件入库
    public DateTime? BeginDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string BeginPaper { get; set; } = "";
    public string EndPaper { get; set; } = "";
    public int PayOption { get; set; } = 4;            // 1 现金 2 支票汇票汇款 3 挂帐代销托收其它 4 全部
    public string CashSub { get; set; } = "";
    public string BalaSub { get; set; } = "";
    public bool IncludeCost { get; set; } = true;      // VOU_TYPE=3（finopvou.valuen=3）
}

/// <summary>业务凭证行（预览行或已入账回显行，finvouch 结构）。</summary>
public sealed class FinOpRow
{
    public int Id { get; set; }
    public int Vouchid { get; set; }
    public int Vcode { get; set; }
    public DateTime Date { get; set; }
    public string Period { get; set; } = "";
    public int Recoid { get; set; }
    public string Note { get; set; } = "";
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Fcash { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Vouword { get; set; } = "";
    public string Client { get; set; } = "";
    public string Invtype { get; set; } = "";
    public string Invcode { get; set; } = "";
    public string Opertype { get; set; } = "";
    public string Opercode { get; set; } = "";
    public bool Audit { get; set; }
    public bool Post { get; set; }
    public string Maker { get; set; } = "";
    public bool Fcolor { get; set; }
    public string Depotno { get; set; } = "";
}

/// <summary>业务凭证组（按单据分组，一张凭证）。</summary>
public sealed class FinOpVouchGroup
{
    public string Opertype { get; set; } = "";
    public string Opercode { get; set; } = "";
    public DateTime Date { get; set; }
    public string Period { get; set; } = "";
    public string Client { get; set; } = "";
    public string Note { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Depotno { get; set; } = "";
    public bool Fcolor { get; set; }
    public bool Audited { get; set; }
    public int Vouchid { get; set; }
    public int Vcode { get; set; }
    public List<FinOpRow> Lines { get; set; } = [];
}

/// <summary>
/// 科目码共享助手：ADDSUB 尾码进位建档（pg_proc L2447）+ GSUBNAME 层级名（gsubname L2447 后）+ fcash 读取。
/// 供业务凭证生成（FinOpVouchRepository）与 G 组 finvouch 联动（XlskRepository）共用；进程内锁按 dpm+客户+类型。
/// </summary>
internal static class FinSubCodes
{
    private static readonly int[] Cum = [3, 6, 9, 11, 13, 15];      // SUBLENG1~6（leng 3,3,3,2,2,2 累计，pg_main 实证）
    private static readonly ConcurrentDictionary<(string, string, string), SemaphoreSlim> Locks = new();

    public static SemaphoreSlim GetLock(string dpm, string client, string cty) =>
        Locks.GetOrAdd((dpm, client, cty), _ => new SemaphoreSlim(1, 1));

    /// <summary>GSUBLENG(len,2)：len 所在级次的累计末位（STITYPE=2 NETLENG）；非边界落入 OTHERWISE=SUBLENG6。</summary>
    public static int NextCum(int n)
    {
        for (var i = 0; i < Cum.Length; i++)
            if (Cum[i] == n)
                return i + 1 < Cum.Length ? Cum[i + 1] : Cum[^1];
        return Cum[^1];
    }

    /// <summary>ADDSUB(client, cty)（pg_proc L2447 原样；tx 为空则自动提交，与旧系统一致）。</summary>
    public static async Task<string> AddSubClientAsync(SqlConnection conn, SqlTransaction? tx,
        string client, string cty, string dpm, CancellationToken ct = default)
    {
        client = client.Trim();
        var sys = await conn.QuerySingleAsync<(string acc, string pay)>(new CommandDefinition(
            "SELECT RTRIM(ISNULL(accsub,'')), RTRIM(ISNULL(paysub,'')) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
        var sub0 = (cty == "A" ? sys.acc : sys.pay).Trim();

        var gate = GetLock(dpm, client, cty);
        await gate.WaitAsync(ct);
        try
        {
            // 1) 主科目兜底建档（应收帐款/应付帐款）
            if (await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @sub0",
                    new { sub0 = Ansi(sub0, 18) }, transaction: tx, cancellationToken: ct)) == 0)
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO finsub (subcode, subname, type, fdc, detacode, dpm) VALUES (@sub0, @name, @cty, @fdc, 0, @dpm)",
                    new
                    {
                        sub0 = Ansi(sub0, 18),
                        name = Ansi(cty == "A" ? "应收帐款" : "应付帐款", 40),
                        cty = Ansi(cty, 1), fdc = Ansi(cty == "A" ? "D" : "C", 1), dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));

            // 2) 按名称+类型查找（SET ORDER TO subcode → LOCATE 取子码序第一行）
            var found = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                """
                SELECT TOP (1) rtrim(subcode) FROM finsub WITH (NOLOCK)
                WHERE rtrim(subname) = @client AND rtrim(ISNULL(type,'')) = @cty
                ORDER BY rtrim(subcode) COLLATE Latin1_General_BIN2
                """,
                new { client = Ansi(client, 60), cty = Ansi(cty, 1) }, transaction: tx, cancellationToken: ct));
            if (!string.IsNullOrEmpty(found)) return found;

            // 3) 尾码进位取新码（子码窗口 = 累计位宽；排序 BIN2 对齐 VFP MACHINE）
            var childLen = sub0.Length == Cum[0] ? Cum[1] : Cum[^1];   // GSUBLENG(len0,2)
            var maxCode = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                """
                SELECT MAX(rtrim(subcode) COLLATE Latin1_General_BIN2) FROM finsub WITH (NOLOCK)
                WHERE LEFT(rtrim(subcode), @l0) = @sub0 AND LEN(rtrim(subcode)) = @cl
                """,
                new { l0 = sub0.Length, sub0 = Ansi(sub0, 18), cl = childLen }, transaction: tx, cancellationToken: ct));
            string scode0;
            if (maxCode is null)
            {
                scode0 = "001";
            }
            else if (maxCode.EndsWith("`99", StringComparison.Ordinal))   // '`99' 终止 → finsub_auto 池回退
            {
                var pool = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    """
                    SELECT TOP (1) rtrim(subcode) FROM finsub_auto WITH (NOLOCK)
                    WHERE rtrim(subcode) NOT IN (SELECT rtrim(subcode) FROM finsub WHERE dpm = @dpm)
                      AND rtrim(ISNULL(type,'')) = @cty
                    """,
                    new { dpm = Ansi(dpm, 10), cty = Ansi(cty, 1) }, transaction: tx, cancellationToken: ct));
                if (string.IsNullOrEmpty(pool))
                    throw new AppException("FIN_SUB_POOL_EMPTY", "无可使用科目代码！", 409);
                return pool;
            }
            else if (maxCode[^3..] == "999")
            {
                scode0 = "A00";
            }
            else
            {
                var c4 = maxCode.Length >= 4 ? maxCode[3] : '\0';
                if (c4 is >= 'A' and <= '`')     // 字母进位区 'A'..'Z','[','\',']','^','_','`'
                {
                    var s2 = maxCode.Length >= 6 ? maxCode[4..6] : "";
                    var sv = s2.Length == 2 && s2.All(char.IsDigit) ? int.Parse(s2) : 0;   // VAL 非数字=0
                    if (sv + 1 > 99) scode0 = (char)(c4 + 1) + "00";
                    else scode0 = c4 + (sv + 1).ToString("00");
                }
                else
                {
                    var r3 = maxCode[^3..];
                    var nv = (r3.All(char.IsDigit) ? int.Parse(r3) : 0) + 1;
                    scode0 = nv.ToString("000");     // TRANSFORM(VAL+1, '@ZL 999')
                }
            }

            var retcode = sub0 + scode0;
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO finsub (subcode, subname, type, fdc, fclient, detacode, dpm) VALUES (@sc, @client, @cty, @fdc, 1, 1, @dpm)",
                new
                {
                    sc = Ansi(retcode, 18), client = Ansi(client, 60), cty = Ansi(cty, 1),
                    fdc = Ansi(cty == "A" ? "D" : "C", 1), dpm = Ansi(dpm, 10),
                }, transaction: tx, cancellationToken: ct));
            return retcode;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>GSUBNAME(subid)（gsubname 原样：子码序扫描 + 级次边界推进 + ' - ' 连接；重复行靠 N 推进天然去重）。</summary>
    public static async Task<string> GSubNameAsync(SqlConnection conn, SqlTransaction? tx,
        string subid, CancellationToken ct = default)
    {
        subid = subid.Trim();
        if (subid.Length == 0) return "";
        var p3 = subid[..Math.Min(3, subid.Length)];
        var rows = (await conn.QueryAsync<(string sc, string sn)>(new CommandDefinition(
            "SELECT rtrim(subcode), rtrim(subname) FROM finsub WITH (NOLOCK) WHERE LEFT(rtrim(subcode), 3) = @p3 ORDER BY rtrim(subcode) COLLATE Latin1_General_BIN2",
            new { p3 = Ansi(p3, 18) }, transaction: tx, cancellationToken: ct))).AsList();
        var n = 3;
        var ret = "";
        foreach (var r in rows)
        {
            if (n > subid.Length) continue;
            var left = r.sc.Length >= n ? r.sc[..n] : r.sc;    // VFP LEFT 短串返回原串
            if (left == subid[..n])
            {
                ret += (n == 3 ? "" : " - ") + r.sn;
                n = NextCum(n);
            }
        }
        return ret;
    }

    /// <summary>SEEK subcode 取首行 fcash（重复行取 rid 序首行）。</summary>
    public static async Task<bool> GetFcashAsync(SqlConnection conn, SqlTransaction? tx,
        string subcode, CancellationToken ct = default)
        => await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT TOP (1) ISNULL(fcash, 0) FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @sc ORDER BY id",
            new { sc = Ansi(subcode.Trim(), 18) }, transaction: tx, cancellationToken: ct));

    /// <summary>GETSUBCODE_AUTO(name)：finsub_auto 池按名取码（rscar 池空 → 空码，旧系统原样）。</summary>
    public static async Task<string> GetSubcodeAutoAsync(SqlConnection conn, string subname, CancellationToken ct = default)
        => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP (1) rtrim(subcode) FROM finsub_auto WITH (NOLOCK) WHERE rtrim(subname) = @n ORDER BY rtrim(subcode) COLLATE Latin1_General_BIN2",
            new { n = Ansi(subname, 40) }, cancellationToken: ct)) ?? "";

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}

/// <summary>业务凭证仓储（fr_finopvouch 页 + fr_finoperation 生成对话框契约）。</summary>
public interface IFinOpVouchRepository
{
    Task<int> GetFinOpVouOptionAsync(CancellationToken ct = default);
    Task<(decimal Luse, int FinInvou)> GetFinSwitchAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetSkqxNamesAsync(string dpm, CancellationToken ct = default);
    Task<(string Subcode, string Subname, string Type, bool Fcash)?> GetSkqxByNameAsync(string subname, string dpm, CancellationToken ct = default);
    Task<IReadOnlyList<FinOpVouchGroup>> GenerateAsync(FinOpGenerateQuery query, string dpm, CancellationToken ct = default);
    Task<(int VouchId, int Vcode)> AuditAsync(FinOpVouchGroup group, string dpm, string per, CancellationToken ct = default);
    Task UnauditAsync(int vouchid, string opty, string opercode, string dpm, CancellationToken ct = default);

    /// <summary>业务明细（fr_finopdet）：BBI→tbistoed(seano)；SRO/SMO→tbsada(paper, type<>'报价')。</summary>
    Task<IReadOnlyList<FinOpDetailRow>> GetOpDetailAsync(string opty, string opercode, CancellationToken ct = default);

    /// <summary>入库明细（fr_ysyf YWMX `__.row4` L126-129：tbistoed WHERE seano=单号 AND isord=匹配）。</summary>
    Task<IReadOnlyList<FinOpDetailRow>> GetOpDetailBySeanoAsync(string seano, string isord, CancellationToken ct = default);

    /// <summary>读凭证（fr_finrevouch）：按 vouchid 读 finvouch 分录 + 表头（无行回退 finvouch 预览语义不可用，业务凭证必已落库）。</summary>
    Task<FinOpVouchRead?> ReadVouchAsync(int vouchid, CancellationToken ct = default);

    /// <summary>单张过帐（fr_finopvouch myclick Post：本期+audit=1+post=0+月结 tp=1 → 复用整期过帐单凭证路径）。</summary>
    Task PostVouchAsync(int vouchid, string dpm, string per, CancellationToken ct = default);

    /// <summary>单张反过帐（unpost：本期+post=1+月结 → 对称回退）。</summary>
    Task UnpostVouchAsync(int vouchid, string dpm, string per, CancellationToken ct = default);

    /// <summary>凭证清单（fr_finrevouch Init：vouchid 分组清单，供上一张/下一张浏览）。</summary>
    Task<IReadOnlyList<(int Vouchid, int Vcode, DateTime Date, string Maker, string Auditer, string Poster)>> ListVouchesAsync(CancellationToken ct = default);

    /// <summary>finvprn 每页行数（fr_finvprint Load：tbsysset name='finvprn' valuen，0 视为缺省 5）。</summary>
    Task<int> GetFinVprnAsync(CancellationToken ct = default);
}

/// <summary>业务明细行（fr_finopdet Istotmp 12 列 + 合计入 VM）。</summary>
public sealed class FinOpDetailRow
{
    public string Gu { get; set; } = "";
    public DateTime Date { get; set; }
    public string Paper { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprct { get; set; }
    public decimal Yhl { get; set; }
    public string Pty { get; set; } = "";
    public string Per { get; set; } = "";
}

/// <summary>读凭证结果（fr_finrevouch 表头 + 分录）。</summary>
public sealed class FinOpVouchRead
{
    public int Vouchid { get; set; }
    public int Vcode { get; set; }
    public DateTime Date { get; set; }
    public string Vouword { get; set; } = "";
    public string Abill { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Auditer { get; set; } = "";
    public string Poster { get; set; } = "";
    public bool Audit { get; set; }
    public bool Post { get; set; }
    public string Period { get; set; } = "";
    public IReadOnlyList<FinVouchRow> Lines { get; set; } = [];
}

public sealed class FinOpVouchRepository(SqlConnectionFactory factory) : IFinOpVouchRepository
{
    private readonly FinVouchRepository _vouch = new(factory);

    /// <summary>tbsysset name='finopvou' valuen（1=手工凭证模式禁止生成；3=含成本对，部署实测）。</summary>
    public async Task<int> GetFinOpVouOptionAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT ISNULL(CAST(valuen AS int), 0) FROM tbsysset WITH (NOLOCK) WHERE name = 'finopvou' AND type = 'system'",
            cancellationToken: ct));
    }

    public async Task<(decimal Luse, int FinInvou)> GetFinSwitchAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var luse = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT ISNULL(l_use, 0) FROM syscontrol WITH (NOLOCK) WHERE dpm = 'A'", cancellationToken: ct));
        var fininvou = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT ISNULL(CAST(valuen AS int), 0) FROM tbsysset WITH (NOLOCK) WHERE name = 'fininvou' AND type = 'system'", cancellationToken: ct));
        return (luse, fininvou);
    }

    /// <summary>收付去向（fr_xlsk Init L293-303）：finsub detacode=1 AND type IN ('C','B')，子码序。
    /// 2026-10-29 修正：库为多套账合并（finsub 按 dpm 分部门），须按当前部门过滤（A 部门实测 8 行，旧系统一致）。</summary>
    public async Task<IReadOnlyList<string>> GetSkqxNamesAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT rtrim(subname) FROM finsub WITH (NOLOCK) WHERE dpm = @dpm AND detacode = 1 AND (type = 'C' OR type = 'B') ORDER BY rtrim(subcode) COLLATE Latin1_General_BIN2, id",
            new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<(string Subcode, string Subname, string Type, bool Fcash)?> GetSkqxByNameAsync(string subname, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = (await conn.QueryAsync<(string, string, string, bool)>(new CommandDefinition(
            """
            SELECT TOP (1) rtrim(subcode), rtrim(subname), rtrim(ISNULL(type,'')), ISNULL(fcash,0)
            FROM finsub WITH (NOLOCK)
            WHERE dpm = @dpm AND rtrim(subname) = @n AND detacode = 1 AND (type = 'C' OR type = 'B')
            ORDER BY id
            """, new { n = Ansi(subname.Trim(), 40), dpm = Ansi(dpm, 10) }, cancellationToken: ct))).FirstOrDefault();
        return row.Item1 is null ? null : (row.Item1, row.Item2, row.Item3, row.Item4);
    }

    /// <summary>业务凭证生成（fr_finoperation row9 查询 + myclick longsky 变体；游标预览不落库，ADDSUB 建科目除外）。</summary>
    public async Task<IReadOnlyList<FinOpVouchGroup>> GenerateAsync(FinOpGenerateQuery query, string dpm, CancellationToken ct = default)
    {
        var option = await GetFinOpVouOptionAsync(ct);
        if (option == 1)
            throw new AppException("FINOPVOU_MANUAL", "业务单据凭证不能在此生成,请修改凭证选项!", 409);
        if (string.IsNullOrWhiteSpace(query.CashSub))
            throw new AppException("CASHSUB_REQUIRED", "请输入现金结算科目", 422);
        if (string.IsNullOrWhiteSpace(query.BalaSub))
            throw new AppException("BALASUB_REQUIRED", "请输入银行结算科目", 422);
        if (query.BeginDate is null && string.IsNullOrWhiteSpace(query.BeginPaper))
            throw new AppException("DATE_REQUIRED", "请输入查询日期!", 422);

        await using var conn = await factory.OpenAsync(ct);
        return query.Opty switch
        {
            "BBI" => await GenerateBbiAsync(conn, query, dpm, ct),
            "SRO" or "SMO" => await GenerateSroAsync(conn, query, dpm, ct),
            _ => throw new AppException("FINOP_OPTY_NA", "该业务类型生成未开放（CBI/CRO/SEV/BMI 备案延后）", 422),
        };
    }

    // ---------- BBI 散件入库（longsky_bbi） ----------

    private sealed record BbiRow(int Id, string Cno, DateTime Indate, string Gyname, string Fkfs,
        string Pno, string Per, string Depotno, decimal Jkamount, decimal Iprc, decimal Freight);

    private async Task<IReadOnlyList<FinOpVouchGroup>> GenerateBbiAsync(
        SqlConnection conn, FinOpGenerateQuery q, string dpm, CancellationToken ct)
    {
        var where = "rtrim(cno) <> '' AND (rtrim(type) = N'历史' OR rtrim(mark) = N'已确认')";
        var p = new DynamicParameters();
        if (q.BeginDate.HasValue) { where += " AND indate >= @bd"; p.Add("@bd", q.BeginDate.Value.Date); }
        if (q.EndDate.HasValue) { where += " AND indate <= @ed"; p.Add("@ed", q.EndDate.Value.Date); }
        if (!string.IsNullOrWhiteSpace(q.BeginPaper)) { where += " AND rtrim(cno) >= @bp"; p.Add("@bp", Ansi(q.BeginPaper.Trim(), 15)); }
        if (!string.IsNullOrWhiteSpace(q.EndPaper)) { where += " AND rtrim(cno) <= @ep"; p.Add("@ep", Ansi(q.EndPaper.Trim(), 15)); }
        where += q.PayOption switch
        {
            1 => " AND rtrim(fkfs) = N'现金'",
            2 => " AND rtrim(fkfs) IN (N'支票', N'汇票', N'汇款')",
            3 => " AND rtrim(fkfs) IN (N'挂帐', N'代销', N'托收', N'其它')",
            _ => "",
        };

        var rows = (await conn.QueryAsync<BbiRow>(new CommandDefinition(
            $"""
            SELECT id, rtrim(cno) cno, indate, rtrim(ISNULL(gyname,'')) gyname, rtrim(ISNULL(fkfs,'')) fkfs,
                   rtrim(ISNULL(pno,'')) pno, rtrim(ISNULL(per,'')) per, rtrim(ISNULL(depotno,'')) depotno,
                   ISNULL(jkamount,0) jkamount, ISNULL(iprc,0) iprc, ISNULL(freight,0) freight
            FROM tbistoed WITH (NOLOCK) WHERE {where}
            ORDER BY rtrim(cno) COLLATE Latin1_General_BIN2, id
            """, p, cancellationToken: ct))).AsList();

        // SUBLENG1=3 短码判别（syscontrol leng1；本部署 dpm=A leng1=3）
        var leng1 = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT ISNULL(leng1, 0) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        string S(string shortCode, string longCode) => leng1 == 3 ? shortCode : longCode;

        // 分组（Fvstmp：GROUP BY cno，代表行=首行；仅 gyname 非空的组进入）
        var baseVid = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT vid FROM finvid WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        var groups = rows.GroupBy(r => r.Cno).Where(g => g.Any(r => r.Gyname.Trim().Length > 0))
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        var result = new List<FinOpVouchGroup>();
        var fcolor = false;
        var previewVid = baseVid;
        foreach (var g in groups)
        {
            fcolor = !fcolor;
            var head = g.First();
            var sum0 = g.Sum(r => r.Jkamount * r.Iprc);
            var sumfre = g.Sum(r => (r.Iprc - (r.Freight == 0 ? r.Iprc : r.Freight)) * r.Jkamount);
            var fkfs0 = head.Fkfs.Trim();
            string subcode, type, vouword;
            if ("现金".Contains(fkfs0))                       // VFP $：fkfs0 为 "现金" 子串
            {
                subcode = q.CashSub.Trim(); type = "C"; vouword = "付";
            }
            else if ("支票汇票汇款刷卡".Contains(fkfs0))
            {
                subcode = q.BalaSub.Trim(); type = "B"; vouword = "付";
            }
            else
            {
                subcode = await FinSubCodes.AddSubClientAsync(conn, null, head.Gyname, "P", dpm, ct);
                type = "P"; vouword = "转";
            }
            var subname = await FinSubCodes.GSubNameAsync(conn, null, subcode, ct);
            var fcash0 = await FinSubCodes.GetFcashAsync(conn, null, subcode, ct);

            // 已入账检测（fingenvoustyle=.F.：无 client 条件）
            var existing = (await conn.QueryAsync<FinOpRow>(new CommandDefinition(
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
                """, new { oc = Ansi(head.Cno.Trim(), 30) }, cancellationToken: ct))).AsList();

            var group = new FinOpVouchGroup
            {
                Opertype = "BBI",
                Opercode = head.Cno.Trim(),
                Date = head.Indate.Date,
                Period = head.Indate.ToString("yyyyMM"),
                Client = head.Gyname.Trim(),
                Note = $"第-{head.Cno.Trim()}-号采购入库",
                Maker = head.Per.Trim(),
                Depotno = head.Depotno.Trim(),
                Fcolor = fcolor,
            };
            if (existing.Count > 0)
            {
                group.Audited = true;
                group.Vouchid = existing[0].Vouchid;
                group.Vcode = existing[0].Vcode;
                foreach (var r in existing)
                {
                    r.Fcolor = fcolor;
                    group.Lines.Add(r);
                }
            }
            else
            {
                previewVid++;
                group.Vouchid = previewVid;     // 游标预览号，不落库
                group.Lines.Add(new FinOpRow
                {
                    Vouchid = previewVid, Date = head.Indate.Date, Period = head.Indate.ToString("yyyyMM"),
                    Recoid = 1, Note = group.Note, Subcode = S("135001", "1035001"),
                    Subname = "库存商品 - 配件入库", Debit = sum0, Credit = 0, Type = "",
                    Vouword = vouword, Fcash = false, Client = group.Client, Invcode = head.Pno.Trim(),
                    Opertype = "BBI", Opercode = group.Opercode, Maker = group.Maker,
                    Fcolor = fcolor, Depotno = group.Depotno,
                });
                group.Lines.Add(new FinOpRow
                {
                    Vouchid = previewVid, Date = head.Indate.Date, Period = head.Indate.ToString("yyyyMM"),
                    Recoid = 2, Note = group.Note, Subcode = subcode, Subname = subname,
                    Debit = 0, Credit = sum0 - sumfre, Type = type, Vouword = vouword,
                    Fcash = fcash0, Client = group.Client, Invcode = head.Pno.Trim(),
                    Opertype = "BBI", Opercode = group.Opercode, Maker = group.Maker,
                    Fcolor = fcolor, Depotno = group.Depotno,
                });
                if (sumfre != 0)
                {
                    var yfSub = await FinSubCodes.GetSubcodeAutoAsync(conn, "运费Y", ct);
                    group.Lines.Add(new FinOpRow
                    {
                        Vouchid = previewVid, Date = head.Indate.Date, Period = head.Indate.ToString("yyyyMM"),
                        Recoid = 2, Note = group.Note, Subcode = yfSub,
                        Subname = yfSub.Length == 0 ? "" : await FinSubCodes.GSubNameAsync(conn, null, yfSub, ct),
                        Debit = 0, Credit = sumfre, Type = "", Vouword = vouword, Fcash = false,
                        Client = group.Client, Invcode = head.Pno.Trim(), Opertype = "BBI",
                        Opercode = group.Opercode, Maker = group.Maker, Fcolor = fcolor, Depotno = group.Depotno,
                    });
                }
            }
            result.Add(group);
        }
        return result;
    }

    // ---------- SRO 零售出库 / SMO 调拨出库（longsky_sro，共用） ----------

    private sealed record SroRow(int Id, string Paper, DateTime Outdate, string Gname, string Guno,
        string Pno, string Per, string Depotno, decimal Ckamount, decimal Oprct, decimal Iprc, decimal Yhl);

    private async Task<IReadOnlyList<FinOpVouchGroup>> GenerateSroAsync(
        SqlConnection conn, FinOpGenerateQuery q, string dpm, CancellationToken ct)
    {
        var where = q.Opty == "SMO"
            ? "rtrim(type) = N'调拨' AND rtrim(mark) <> ''"
            : "(rtrim(type) = N'历史' OR (rtrim(type) = N'销售' AND rtrim(mark) <> ''))";
        var p = new DynamicParameters();
        if (q.BeginDate.HasValue) { where += " AND outdate >= @bd"; p.Add("@bd", q.BeginDate.Value.Date); }
        if (q.EndDate.HasValue) { where += " AND outdate <= @ed"; p.Add("@ed", q.EndDate.Value.Date); }
        if (!string.IsNullOrWhiteSpace(q.BeginPaper)) { where += " AND rtrim(paper) >= @bp"; p.Add("@bp", Ansi(q.BeginPaper.Trim(), 15)); }
        if (!string.IsNullOrWhiteSpace(q.EndPaper)) { where += " AND rtrim(paper) <= @ep"; p.Add("@ep", Ansi(q.EndPaper.Trim(), 15)); }
        where += q.PayOption switch
        {
            1 => " AND rtrim(guno) = N'现金'",
            2 => " AND rtrim(guno) IN (N'支票', N'汇票', N'汇款')",
            3 => " AND rtrim(guno) IN (N'挂帐', N'代销', N'托收', N'其它')",
            _ => "",
        };

        var rows = (await conn.QueryAsync<SroRow>(new CommandDefinition(
            $"""
            SELECT id, rtrim(paper) paper, outdate, rtrim(ISNULL(gname,'')) gname, rtrim(ISNULL(guno,'')) guno,
                   rtrim(ISNULL(pno,'')) pno, rtrim(ISNULL(per,'')) per, rtrim(ISNULL(depotno,'')) depotno,
                   ISNULL(ckamount,0) ckamount, ISNULL(oprct,0) oprct, ISNULL(iprc,0) iprc, ISNULL(yhl,0) yhl
            FROM tbsada WITH (NOLOCK) WHERE {where}
            ORDER BY rtrim(paper) COLLATE Latin1_General_BIN2, id
            """, p, cancellationToken: ct))).AsList();

        var leng1 = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT ISNULL(leng1, 0) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        string S(string shortCode, string longCode) => leng1 == 3 ? shortCode : longCode;

        var baseVid = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT vid FROM finvid WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        var groups = rows.GroupBy(r => r.Paper).Where(g => g.Any(r => r.Gname.Trim().Length > 0))
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        var result = new List<FinOpVouchGroup>();
        var fcolor = false;
        var previewVid = baseVid;
        foreach (var g in groups)
        {
            fcolor = !fcolor;
            var head = g.First();
            var cost0 = g.Sum(r => r.Ckamount * r.Iprc);
            var sum0 = g.Sum(r => r.Ckamount * r.Oprct * (100 - r.Yhl) / 100);   // GETJE P_XSJEGS='1' 恒等（一期备案）
            var guno0 = head.Guno.Trim();
            string subcode, type, vouword;
            if ("现金".Contains(guno0))
            {
                subcode = q.CashSub.Trim(); type = "C"; vouword = "收";
            }
            else if ("支票汇票汇款刷卡".Contains(guno0))
            {
                subcode = q.BalaSub.Trim(); type = "B"; vouword = "收";
            }
            else
            {
                subcode = await FinSubCodes.AddSubClientAsync(conn, null, head.Gname, "A", dpm, ct);
                type = "A"; vouword = "转";
            }
            var subname = await FinSubCodes.GSubNameAsync(conn, null, subcode, ct);
            var fcash0 = await FinSubCodes.GetFcashAsync(conn, null, subcode, ct);

            var existing = (await conn.QueryAsync<FinOpRow>(new CommandDefinition(
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
                """, new { oc = Ansi(head.Paper.Trim(), 30), ot = Ansi(q.Opty, 8) }, cancellationToken: ct))).AsList();

            var group = new FinOpVouchGroup
            {
                Opertype = q.Opty,
                Opercode = head.Paper.Trim(),
                Date = head.Outdate.Date,
                Period = head.Outdate.ToString("yyyyMM"),
                Client = head.Gname.Trim(),
                Note = $"第-{head.Paper.Trim()}-号销售开单",
                Maker = head.Per.Trim(),
                Depotno = head.Depotno.Trim(),
                Fcolor = fcolor,
            };
            if (existing.Count > 0)
            {
                group.Audited = true;
                group.Vouchid = existing[0].Vouchid;
                group.Vcode = existing[0].Vcode;
                foreach (var r in existing)
                {
                    r.Fcolor = fcolor;
                    group.Lines.Add(r);
                }
            }
            else
            {
                previewVid++;
                group.Vouchid = previewVid;
                FinOpRow NewRow(int recoid, string sc, string sn, decimal debit, decimal credit, string ty, bool fcash) => new()
                {
                    Vouchid = previewVid, Date = head.Outdate.Date, Period = head.Outdate.ToString("yyyyMM"),
                    Recoid = recoid, Note = group.Note, Subcode = sc, Subname = sn,
                    Debit = debit, Credit = credit, Type = ty, Vouword = vouword, Fcash = fcash,
                    Client = group.Client, Invtype = head.Pno.Trim(), Opertype = q.Opty,
                    Opercode = group.Opercode, Maker = group.Maker, Fcolor = fcolor, Depotno = group.Depotno,
                };
                group.Lines.Add(NewRow(1, subcode, subname, sum0, 0, type, fcash0));
                if (q.IncludeCost)
                {
                    group.Lines.Add(NewRow(2, S("511002", "5011002"), "商品销售成本 - 配件销售成本", cost0, 0, "", false));
                    group.Lines.Add(NewRow(3, S("135002", "1035002"), "库存商品 - 配件销售", 0, cost0, "", false));
                }
                group.Lines.Add(NewRow(q.IncludeCost ? 4 : 2, S("501002", "5001002"),
                    "商品销售收入 - 配件销售收入", 0, sum0, "", false));
            }
            result.Add(group);
        }
        return result;
    }

    /// <summary>
    /// 业务凭证审核（fr_finopvouch Audit）：finvid(dpm) 推进取新 vouchid → vcode=期间 max+1 →
    /// INSERT finvouch(audit=1, auditer) 逐分录 → opaudit 回写业务单据 audit=1（BBI→tbistoed.cno、SRO/SMO→tbsada.paper，带 dpm）。
    /// </summary>
    public async Task<(int VouchId, int Vcode)> AuditAsync(FinOpVouchGroup group, string dpm, string per, CancellationToken ct = default)
    {
        if (group.Audited)
            throw new AppException("FINOP_AUDITED", "凭证已审核!", 409);
        if (group.Period.CompareTo(DateTime.Today.ToString("yyyyMM")) < 0)
            throw new AppException("FINOP_PERIOD", "不能审核上一期的凭证!", 409);
        if (await _vouch.ChePostAsync(group.Date, 1, ct))
            throw new AppException("FINOP_MONTHCLOSE", "该月已做期末结帐,不能再审核凭证!", 409);
        if (await _vouch.ChePostAsync(group.Date, 2, ct))
            throw new AppException("FINOP_CAF", "该月已经结转损益,不能再审核凭证!", 409);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finvid SET vid = vid + 1 WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) },
                transaction: tx, cancellationToken: ct));
            var vouchid = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT vid FROM finvid WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) },
                transaction: tx, cancellationToken: ct));
            var vcode = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT ISNULL(MAX(vcode), 0) + 1 FROM finvouch WITH (NOLOCK) WHERE period = @p",
                new { p = Ansi(group.Period, 6) }, transaction: tx, cancellationToken: ct));

            foreach (var l in group.Lines)
            {
                await conn.ExecuteAsync(new CommandDefinition(
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
                    }, transaction: tx, cancellationToken: ct));
            }

            // opaudit（row2 opaudit，fingenvoustyle=.F. 分支）
            var bizSql = group.Opertype switch
            {
                "BBI" => "UPDATE tbistoed SET audit = 1 WHERE rtrim(cno) = @oc AND dpm = @dpm",
                _ => "UPDATE tbsada SET audit = 1 WHERE paper = @oc AND dpm = @dpm",
            };
            await conn.ExecuteAsync(new CommandDefinition(bizSql,
                new { oc = Ansi(group.Opercode, 30), dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return (vouchid, vcode);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>反审核（fr_finopvouch Unaudit）：未审/已过帐拦截 → DELETE finvouch WHERE vouchid + opaudit 回写 0。</summary>
    public async Task UnauditAsync(int vouchid, string opty, string opercode, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(bool audit, bool post)>(new CommandDefinition(
            "SELECT ISNULL(audit,0), ISNULL(post,0) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid",
            new { vid = vouchid }, cancellationToken: ct))).AsList();
        if (rows.Count == 0 || !rows[0].audit)
            throw new AppException("FINOP_NOT_AUDITED", "凭证未审核!", 409);
        if (rows.Any(r => r.post))
            throw new AppException("FINOP_POSTED", "凭证已过帐,不能反审核!", 409);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM finvouch WHERE vouchid = @vid", new { vid = vouchid },
                transaction: tx, cancellationToken: ct));
            var bizSql = opty switch
            {
                "BBI" => "UPDATE tbistoed SET audit = 0 WHERE rtrim(cno) = @oc AND dpm = @dpm",
                _ => "UPDATE tbsada SET audit = 0 WHERE paper = @oc AND dpm = @dpm",
            };
            await conn.ExecuteAsync(new CommandDefinition(bizSql,
                new { oc = Ansi(opercode, 30), dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 业务明细（fr_finopdet Init）。BBI：tbistoed WHERE rtrim(seano)=单号（GU=GYNAME、AMOUNT=JKAMOUNT、
    /// OPRC=IPRC、PTY=FKFS 空→'现金'）；SRO/SMO：tbsada WHERE rtrim(paper)=单号 AND type<>'报价'（AMOUNT=CKAMOUNT）。
    /// CBI/CRO（tbcarpr 汽车库跨库）与生成侧同批备案不实现。
    /// </summary>
    public async Task<IReadOnlyList<FinOpDetailRow>> GetOpDetailAsync(string opty, string opercode, CancellationToken ct = default)
    {
        var paper = (opercode ?? "").Trim();
        await using var conn = await factory.OpenAsync(ct);
        if (opty == "BBI")
        {
            // tbistoed 无 yhl/oprc：旧 fr_finopdet 对 BBI 令 YHL=0、OPRCT=IPRJ（进价金额）
            var rows = await conn.QueryAsync<(string gyname, DateTime indate, string cno, string nno, string na1,
                string ty, decimal jkamount, decimal iprc, decimal iprj, decimal yhl, string fkfs, string per)>(
                new CommandDefinition(
                    """
                    SELECT RTRIM(ISNULL(gyname,'')), indate, RTRIM(cno), RTRIM(nno), RTRIM(na1), RTRIM(ty),
                           ISNULL(jkamount,0), ISNULL(iprc,0), ISNULL(iprj,0), 0, RTRIM(ISNULL(fkfs,'')), RTRIM(ISNULL(per,''))
                    FROM tbistoed WITH (NOLOCK) WHERE rtrim(seano) = @paper ORDER BY id
                    """, new { paper = Ansi(paper, 15) }, cancellationToken: ct));
            return rows.Select(r => new FinOpDetailRow
            {
                Gu = r.gyname, Date = r.indate, Paper = r.cno, Nno = r.nno, Na1 = r.na1, Ty = r.ty,
                Amount = r.jkamount, Oprc = r.iprc, Oprct = r.iprj, Yhl = r.yhl,
                Pty = string.IsNullOrEmpty(r.fkfs) ? "现金" : r.fkfs, Per = r.per,
            }).ToList();
        }
        var rowsS = await conn.QueryAsync<(string gname, DateTime outdate, string paper, string nno, string na1,
            string ty, decimal ckamount, decimal iprc, decimal oprct, decimal yhl, string guno, string per)>(
            new CommandDefinition(
                """
                SELECT RTRIM(ISNULL(gname,'')), outdate, RTRIM(paper), RTRIM(nno), RTRIM(na1), RTRIM(ty),
                       ISNULL(ckamount,0), ISNULL(iprc,0), ISNULL(oprct,0), ISNULL(yhl,0), RTRIM(ISNULL(guno,'')), RTRIM(ISNULL(per,''))
                FROM tbsada WITH (NOLOCK) WHERE rtrim(paper) = @paper AND type <> N'报价' ORDER BY id
                """, new { paper = Ansi(paper, 15) }, cancellationToken: ct));
        return rowsS.Select(r => new FinOpDetailRow
        {
            Gu = r.gname, Date = r.outdate, Paper = r.paper, Nno = r.nno, Na1 = r.na1, Ty = r.ty,
            Amount = r.ckamount, Oprc = r.iprc, Oprct = r.oprct, Yhl = r.yhl, Pty = r.guno, Per = r.per,
        }).ToList();
    }

    /// <summary>
    /// 入库明细（fr_ysyf YWMX `__.row4` L126-129）：tbistoed WHERE seano=单号 AND isord=明细行 isord，
    /// 日期取首行 indate。复用 BBI 映射口径（GU=GYNAME、AMOUNT=JKAMOUNT、OPRC=IPRC、PTY=FKFS 空→'现金'）。
    /// </summary>
    public async Task<IReadOnlyList<FinOpDetailRow>> GetOpDetailBySeanoAsync(string seano, string isord, CancellationToken ct = default)
    {
        var paper = (seano ?? "").Trim();
        var ord = (isord ?? "").Trim();
        await using var conn = await factory.OpenAsync(ct);
        // tbistoed 无 yhl/oprc：旧 fr_finopdet 对 BBI 令 YHL=0、OPRCT=IPRJ（进价金额）
        var rows = await conn.QueryAsync<(string gyname, DateTime indate, string cno, string nno, string na1,
            string ty, decimal jkamount, decimal iprc, decimal iprj, decimal yhl, string fkfs, string per)>(
            new CommandDefinition(
                """
                SELECT RTRIM(ISNULL(gyname,'')), indate, RTRIM(cno), RTRIM(nno), RTRIM(na1), RTRIM(ty),
                       ISNULL(jkamount,0), ISNULL(iprc,0), ISNULL(iprj,0), 0, RTRIM(ISNULL(fkfs,'')), RTRIM(ISNULL(per,''))
                FROM tbistoed WITH (NOLOCK) WHERE rtrim(seano) = @paper AND rtrim(ISNULL(isord,'')) = @ord ORDER BY id
                """, new { paper = Ansi(paper, 15), ord = Ansi(ord, 6) }, cancellationToken: ct));
        return rows.Select(r => new FinOpDetailRow
        {
            Gu = r.gyname, Date = r.indate, Paper = r.cno, Nno = r.nno, Na1 = r.na1, Ty = r.ty,
            Amount = r.jkamount, Oprc = r.iprc, Oprct = r.iprj, Yhl = r.yhl,
            Pty = string.IsNullOrEmpty(r.fkfs) ? "现金" : r.fkfs, Per = r.per,
        }).ToList();
    }

    /// <summary>读凭证（fr_finrevouch）：按 vouchid 取 finvouch 分录 + 首行表头。</summary>
    public async Task<FinOpVouchRead?> ReadVouchAsync(int vouchid, CancellationToken ct = default)
    {
        var lines = await _vouch.QueryAsync("", vouchid, null, ct);
        if (lines.Count == 0) return null;
        var h = lines[0];
        return new FinOpVouchRead
        {
            Vouchid = h.Vouchid, Vcode = h.Vcode, Date = h.Date, Vouword = h.Vouword,
            Abill = "", Maker = h.Maker, Auditer = h.Auditer, Poster = h.Poster,
            Audit = h.Audit, Post = h.Post, Period = h.Period, Lines = lines,
        };
    }

    /// <summary>凭证清单（fr_finrevouch Init：vouchid<>0 GROUP BY vouchid ORDER BY vouchid）。</summary>
    public async Task<IReadOnlyList<(int Vouchid, int Vcode, DateTime Date, string Maker, string Auditer, string Poster)>> ListVouchesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int vouchid, int vcode, DateTime date, string maker, string auditer, string poster)>(
            new CommandDefinition(
                """
                SELECT vouchid, MAX(ISNULL(vcode,0)) vcode, MAX(date) date,
                       MAX(RTRIM(ISNULL(maker,''))) maker, MAX(RTRIM(ISNULL(auditer,''))) auditer,
                       MAX(RTRIM(ISNULL(poster,''))) poster
                FROM finvouch WITH (NOLOCK) WHERE vouchid <> 0
                GROUP BY vouchid ORDER BY vouchid
                """, cancellationToken: ct));
        return rows.Select(r => (r.vouchid, r.vcode, r.date, r.maker ?? "", r.auditer ?? "", r.poster ?? "")).ToList();
    }

    /// <summary>finvprn 每页行数（fr_finvprint Load L113-115：tbsysset name='finvprn' valuen）。</summary>
    public async Task<int> GetFinVprnAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var v = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT valuen FROM tbsysset WITH (NOLOCK) WHERE name = 'finvprn'", cancellationToken: ct));
        return v ?? 0;
    }

    /// <summary>
    /// 单张过帐（fr_finopvouch myclick Post L285-313）。校验：本期 + audit=1 + post=0 + CHEPOST(date,1) 月结；
    /// 写路径=旧 exec getfinpost_mx（部署库 SP 实测语义与整期客户端路径一致：finsub 逐级汇总 + finaleger
    /// recoid=1/2/3 upsert + 凭证行回写）→ 复用 FinVouchRepository.PostAsync 单凭证版本（byDate 路径限定该 vouchid）。
    /// </summary>
    public async Task PostVouchAsync(int vouchid, string dpm, string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var head = (await conn.QueryAsync<(string period, DateTime date, bool audit, bool post)>(new CommandDefinition(
            "SELECT RTRIM(period), MAX(date), MAX(ISNULL(audit,0)), MAX(ISNULL(post,0)) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid",
            new { vid = vouchid }, cancellationToken: ct))).FirstOrDefault();
        if (head.period is null)
            throw new AppException("FINOP_VOUCH_EMPTY", $"凭证 {vouchid} 不存在", 404);
        // 校验链（L286-303）
        if (head.period != DateTime.Today.ToString("yyyyMM"))
            throw new AppException("FINOP_NOT_CURRENT", "不是本期凭证，不能过帐！", 409);
        if (!head.audit)
            throw new AppException("FINOP_NOT_AUDITED", "凭证未审核！", 409);
        if (head.post)
            throw new AppException("FINOP_POSTED", "凭证已过帐！", 409);
        if (await _vouch.ChePostAsync(head.date, 1, ct))
            throw new AppException("FINOP_MONTHCLOSE", "该月已做期末结帐,不能过帐!", 409);
        await _vouch.PostOneAsync(vouchid, per, dpm, un: false, ct);
    }

    /// <summary>单张反过帐（unpost L315-340）：本期 + post=1 + 月结 → 对称回退。</summary>
    public async Task UnpostVouchAsync(int vouchid, string dpm, string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var head = (await conn.QueryAsync<(string period, DateTime date, bool post)>(new CommandDefinition(
            "SELECT RTRIM(period), MAX(date), MAX(ISNULL(post,0)) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid",
            new { vid = vouchid }, cancellationToken: ct))).FirstOrDefault();
        if (head.period is null)
            throw new AppException("FINOP_VOUCH_EMPTY", $"凭证 {vouchid} 不存在", 404);
        if (head.period != DateTime.Today.ToString("yyyyMM"))
            throw new AppException("FINOP_NOT_CURRENT", "不是本期凭证，不能反过帐！", 409);
        if (!head.post)
            throw new AppException("FINOP_NOT_POSTED", "凭证未过帐！", 409);
        if (await _vouch.ChePostAsync(head.date, 1, ct))
            throw new AppException("FINOP_MONTHCLOSE", "该月已做期末结帐,不能反过帐!", 409);
        await _vouch.PostOneAsync(vouchid, per, dpm, un: true, ct);
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}
