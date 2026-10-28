using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Finance;

// =====================================================================================
// 账务第七批（结转损益/期末结帐/帐务初始/帐套选项）—— 契约：开发文档/契约摘录/账务第七批契约.md
//   I34 结转损益 fr_fincaforw  → exec fincaforw（存储过程自含校验/取号/生成/尾差修正）
//   I35 期末结帐 fr_fincheout  → exec getfincheout / finuncheout（菜单绑定旧版；_new 变体休眠备案）
//   N11 帐务初始 fr_fininit    → detacode 重算 / 末级余额编辑（ybsum 派生）/ Sum 逐级汇总 / 置零
//   N12 帐套选项 fr_finpara    → update syscontrol 18 列（l_use=1 时 UI 只读）
//   N13 启用帐套 fr_finuse     → 平衡校验 + 清空九表 + finsoursub 快照（列数不匹配 INSERT 恒失败——
//                                旧系统 SQLEXEC 返回值未检查静默吞掉，照抄该行为）+ l_use=1
// finsub 级次位宽 3,3,3,2,2,2（syscontrol dpm=A 实测）；finsub.subcode char(18)。
// =====================================================================================

/// <summary>帐套选项完整行（syscontrol，fr_finpara Init SCATTER 全字段）。</summary>
public sealed class FinPara
{
    public string CId { get; set; } = "";
    public bool LUse { get; set; }
    public string CName { get; set; } = "";
    public string Addr { get; set; } = "";
    public string CType { get; set; } = "";
    public int Attri { get; set; }
    public string Artper { get; set; } = "";
    public string Taxid { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Bankid { get; set; } = "";
    public string Tel { get; set; } = "";
    public string Fax { get; set; } = "";
    public int Gramount { get; set; }
    public int Leng1 { get; set; }
    public int Leng2 { get; set; }
    public int Leng3 { get; set; }
    public int Leng4 { get; set; }
    public int Leng5 { get; set; }
    public int Leng6 { get; set; }
    public string Uperiod { get; set; } = "";
    public DateTime Udate { get; set; }
    public string Period { get; set; } = "";
}

/// <summary>帐套选项保存命令（fr_finpara row3，18 列）。</summary>
public sealed class FinParaSaveCommand
{
    public string CId { get; set; } = "";
    public string CName { get; set; } = "";
    public string Addr { get; set; } = "";
    public string CType { get; set; } = "";
    public int Attri { get; set; }
    public string Artper { get; set; } = "";
    public string Taxid { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Bankid { get; set; } = "";
    public string Tel { get; set; } = "";
    public string Fax { get; set; } = "";
    public int Gramount { get; set; }
    public int Leng1 { get; set; }
    public int Leng2 { get; set; }
    public int Leng3 { get; set; }
    public int Leng4 { get; set; }
    public int Leng5 { get; set; }
    public int Leng6 { get; set; }
    public string Uperiod { get; set; } = "";
    public DateTime Udate { get; set; }
    public string Period { get; set; } = "";
}

/// <summary>帐务初始末级科目余额编辑命令（fr_fininit u_.row4 Refresh 派生）。</summary>
public sealed class FinInitBalanceCommand
{
    public string Subcode { get; set; } = "";
    public decimal Bdesum { get; set; }
    public decimal Bcresum { get; set; }
    public decimal Ydesum { get; set; }
    public decimal Ycresum { get; set; }
    public decimal Ypalsum { get; set; }
}

public interface IFinClosingRepository
{
    /// <summary>帐套选项读取（fr_finpara Init：syscontrol dpm 行全字段）。</summary>
    Task<FinPara?> GetParaAsync(string dpm, CancellationToken ct = default);

    /// <summary>结转损益（I34，fr_fincaforw row4）：exec fincaforw(dpm, period, per) → rvalue 消息。</summary>
    Task<string> TransferProfitAsync(string dpm, string period, string per, CancellationToken ct = default);

    /// <summary>期末结帐（I35，fr_fincheout row18）：exec getfincheout(period, dpm) → rvalue 消息。</summary>
    Task<string> PeriodCloseAsync(string dpm, string period, CancellationToken ct = default);

    /// <summary>反结帐（I35，fr_fincheout row20）：01 月/上月月结数据校验 + exec finuncheout(period, dpm, del)。</summary>
    Task<string> PeriodUnCloseAsync(string dpm, string period, bool delCaf, CancellationToken ct = default);

    /// <summary>帐套选项保存（N12，fr_finpara row3）：校验级次 + update syscontrol 18 列。</summary>
    Task SaveSysControlAsync(FinParaSaveCommand cmd, string dpm, CancellationToken ct = default);

    /// <summary>帐务初始 detacode 重算（N11，fr_fininit Init）：存在更长前缀科目 → 非末级。</summary>
    Task<int> RecalcDetacodeAsync(string dpm, CancellationToken ct = default);

    /// <summary>帐务初始置零（N11，fr_fininit myclick 'Init'）：确认后全表 12 列=0（dpm 范围）。</summary>
    Task<int> InitZeroAsync(string dpm, CancellationToken ct = default);

    /// <summary>帐务初始 Sum 汇总（N11，fr_fininit myclick 'Sum'）：全表清零 → 末级科目前缀链聚合 7 列 + edesum/ecresum 派生。</summary>
    Task<int> InitSumAsync(string dpm, int subleng1, CancellationToken ct = default);

    /// <summary>帐务初始末级余额编辑（N11，fr_fininit 网格编辑 + Refresh 派生 ybdesum/ybcresum）。</summary>
    Task UpdateInitBalanceAsync(FinInitBalanceCommand cmd, string dpm, CancellationToken ct = default);

    /// <summary>启用帐套（N13，fr_finuse row3）：平衡校验 → 清空九表 → finsoursub 快照（INSERT 列数不匹配照抄静默失败）→ l_use=1。</summary>
    Task<string> EnableAsync(string dpm, int subleng1, CancellationToken ct = default);
}

public sealed class FinClosingRepository(SqlConnectionFactory factory) : IFinClosingRepository
{
    public async Task<FinPara?> GetParaAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<FinPara>(new CommandDefinition(
            """
            SELECT RTRIM(c_id) c_id, ISNULL(l_use,0) l_use, RTRIM(ISNULL(c_name,'')) c_name, RTRIM(ISNULL(addr,'')) addr,
                   RTRIM(ISNULL(c_type,'')) c_type, ISNULL(attri,0) attri, RTRIM(ISNULL(artper,'')) artper,
                   RTRIM(ISNULL(taxid,'')) taxid, RTRIM(ISNULL(bank,'')) bank, RTRIM(ISNULL(bankid,'')) bankid,
                   RTRIM(ISNULL(tel,'')) tel, RTRIM(ISNULL(fax,'')) fax, ISNULL(gramount,0) gramount,
                   ISNULL(leng1,0) leng1, ISNULL(leng2,0) leng2, ISNULL(leng3,0) leng3,
                   ISNULL(leng4,0) leng4, ISNULL(leng5,0) leng5, ISNULL(leng6,0) leng6,
                   RTRIM(ISNULL(uperiod,'')) uperiod, ISNULL(udate,'1900-01-01') udate, RTRIM(ISNULL(period,'')) period
            FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).FirstOrDefault();
    }

    /// <summary>结转损益（I34）：exec fincaforw(dpm, period, per) → rvalue 消息。</summary>
    public async Task<string> TransferProfitAsync(string dpm, string period, string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "EXEC fincaforw @dpm, @period, @per",
            new { dpm = Ansi(dpm, 10), period = Ansi(period, 6), per = Ansi(per, 10) }, cancellationToken: ct))) ?? "";
    }

    public async Task<string> PeriodCloseAsync(string dpm, string period, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "EXEC getfincheout @period, @dpm",
            new { period = Ansi(period, 6), dpm = Ansi(dpm, 10) }, cancellationToken: ct))) ?? "";
    }

    public async Task<string> PeriodUnCloseAsync(string dpm, string period, bool delCaf, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 表单校验（fr_fincheout row20）：01 月不能反结帐到上一年度
        if (period.EndsWith("01"))
            return "不能反结帐到上一年度!";
        // CPERIOD = MAX(UPERIOD, 上月)
        var uperiod = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT RTRIM(uperiod) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct)) ?? "";
        var prevMonth = PrevPeriod(period);
        var cperiod = string.CompareOrdinal(uperiod, prevMonth) > 0 ? uperiod : prevMonth;
        // finhisbal 上月无数据 → 拦截
        var hasBal = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM finhisbal WITH (NOLOCK) WHERE period = @cperiod AND dpm = @dpm",
            new { cperiod = Ansi(cperiod, 6), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        if (hasBal == 0)
            return "没有上月月结数据!";
        return (await conn.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "EXEC finuncheout @period, @dpm, @del",
            new { period = Ansi(period, 6), dpm = Ansi(dpm, 10), del = delCaf }, cancellationToken: ct))) ?? "";
    }

    /// <summary>帐套选项保存（row3）：级次校验 → 18 列更新（PASSTH 补 dpm）。</summary>
    public async Task SaveSysControlAsync(FinParaSaveCommand cmd, string dpm, CancellationToken ct = default)
    {
        if (cmd.Leng1 == 0 || cmd.Leng2 == 0 || cmd.Leng3 == 0)
            throw new AppException("LENG_INVALID", "科目结构定义格式错误,代码长度不能为0!", 400);
        if (cmd.Leng1 + cmd.Leng2 + cmd.Leng3 + cmd.Leng4 + cmd.Leng5 + cmd.Leng6 > 18)
            throw new AppException("LENG_OVERFLOW", "科目代码总长度超过了18!", 400);
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE syscontrol SET c_name = @cName, addr = @addr, c_type = @cType, attri = @attri, artper = @artper,
                                  taxid = @taxid, bank = @bank, bankid = @bankid, tel = @tel, fax = @fax,
                                  gramount = @gramount, leng1 = @leng1, leng2 = @leng2, leng3 = @leng3,
                                  leng4 = @leng4, leng5 = @leng5, leng6 = @leng6,
                                  uperiod = @uperiod, udate = @udate, period = @period
            WHERE rtrim(c_id) = @cId AND dpm = @dpm
            """,
            new
            {
                cName = Ansi(cmd.CName, 40), addr = Ansi(cmd.Addr, 40), cType = Ansi(cmd.CType, 10),
                cmd.Attri, artper = Ansi(cmd.Artper, 10), taxid = Ansi(cmd.Taxid, 20),
                bank = Ansi(cmd.Bank, 40), bankid = Ansi(cmd.Bankid, 20), tel = Ansi(cmd.Tel, 20),
                fax = Ansi(cmd.Fax, 20), cmd.Gramount,
                cmd.Leng1, cmd.Leng2, cmd.Leng3, cmd.Leng4, cmd.Leng5, cmd.Leng6,
                uperiod = Ansi(cmd.Uperiod, 6), cmd.Udate, period = Ansi(cmd.Period, 6),
                cId = Ansi(cmd.CId, 10), dpm = Ansi(dpm, 10),
            }, cancellationToken: ct));
    }

    /// <summary>detacode 重算（Init）：存在更长前缀科目（全库游标 LOCATE 语义，无 dpm 限定）→ 非末级。</summary>
    public async Task<int> RecalcDetacodeAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE s SET detacode = CASE WHEN EXISTS (
                SELECT 1 FROM finsub c
                WHERE LEFT(LTRIM(RTRIM(c.subcode)), LEN(LTRIM(RTRIM(s.subcode)))) = LTRIM(RTRIM(s.subcode))
                  AND LEN(LTRIM(RTRIM(c.subcode))) > LEN(LTRIM(RTRIM(s.subcode)))
            ) THEN 0 ELSE 1 END
            FROM finsub s
            WHERE s.dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    /// <summary>置零（myclick 'Init'）：全表 12 列=0（update 无 WHERE，PASSTH 补 dpm）。</summary>
    public async Task<int> InitZeroAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE finsub SET ybdesum = 0, ybcresum = 0, bdesum = 0, bcresum = 0, cdesum = 0, ccresum = 0,
                              ydesum = 0, ycresum = 0, edesum = 0, ecresum = 0, mpalsum = 0, ypalsum = 0
            WHERE dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    /// <summary>
    /// Sum 汇总（myclick 'Sum'）：全表 12 列清零 → 末级科目（detacode=1）沿自身→祖先前缀链
    /// （级次累计位宽 3/6/9/11/13/15）聚合 7 列（bdesum/bcresum/ydesum/ycresum/ybdesum/ybcresum/ypalsum，
    /// cdesum/ccresum/mpalsum 不参与）→ edesum/ecresum 按 bdesum-bcresum+cdesum-ccresum 符号拆分。
    /// 集合式实现（VFP 逐级循环的等价聚合：清零后父科目最终值=Σ以它为前缀的末级科目值）。
    /// 三段必须单批执行：Dapper 参数化文本命令走 sp_executesql，临时表跨命令不可见（实测教训）。
    /// </summary>
    public async Task<int> InitSumAsync(string dpm, int subleng1, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            SELECT CAST(LEFT(LTRIM(RTRIM(l.subcode)), p.k) AS varchar(18)) AS psub,
                   SUM(l.bdesum) bdesum, SUM(l.bcresum) bcresum, SUM(l.ydesum) ydesum, SUM(l.ycresum) ycresum,
                   SUM(l.ybdesum) ybdesum, SUM(l.ybcresum) ybcresum, SUM(l.ypalsum) ypalsum
            INTO #agg
            FROM finsub l
            CROSS APPLY (VALUES (3),(6),(9),(11),(13),(15)) p(k)
            WHERE l.dpm = @dpm AND ISNULL(l.detacode, 0) = 1 AND p.k <= LEN(LTRIM(RTRIM(l.subcode))) AND p.k >= @subleng1
              AND EXISTS (SELECT 1 FROM finsub f WHERE rtrim(f.subcode) = LEFT(LTRIM(RTRIM(l.subcode)), p.k))
            GROUP BY CAST(LEFT(LTRIM(RTRIM(l.subcode)), p.k) AS varchar(18));

            UPDATE finsub SET ybdesum = 0, ybcresum = 0, bdesum = 0, bcresum = 0, cdesum = 0, ccresum = 0,
                              ydesum = 0, ycresum = 0, edesum = 0, ecresum = 0, mpalsum = 0, ypalsum = 0
            WHERE dpm = @dpm;

            UPDATE s SET s.bdesum = a.bdesum, s.bcresum = a.bcresum, s.ydesum = a.ydesum, s.ycresum = a.ycresum,
                         s.ybdesum = a.ybdesum, s.ybcresum = a.ybcresum, s.ypalsum = a.ypalsum,
                         s.edesum = CASE WHEN a.bdesum - a.bcresum + s.cdesum - s.ccresum >= 0
                                          THEN a.bdesum - a.bcresum + s.cdesum - s.ccresum ELSE 0 END,
                         s.ecresum = CASE WHEN a.bdesum - a.bcresum + s.cdesum - s.ccresum <= 0
                                          THEN ABS(a.bdesum - a.bcresum + s.cdesum - s.ccresum) ELSE 0 END
            FROM finsub s
            JOIN #agg a ON rtrim(s.subcode) = a.psub
            WHERE s.dpm = @dpm;

            DROP TABLE #agg;
            """, new { dpm = Ansi(dpm, 10), subleng1 }, cancellationToken: ct));
    }

    /// <summary>末级余额编辑（u_.row4 Refresh）：ybdesum/ybcresum = (BDESUM-BCRESUM)-(YDESUM-YCRESUM) 符号拆分，7 列更新。</summary>
    public async Task UpdateInitBalanceAsync(FinInitBalanceCommand cmd, string dpm, CancellationToken ct = default)
    {
        var baln0 = (cmd.Bdesum - cmd.Bcresum) - (cmd.Ydesum - cmd.Ycresum);
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE finsub SET ybdesum = @yb, ybcresum = @ybc, bdesum = @bd, bcresum = @bc,
                              ydesum = @yd, ycresum = @yc, ypalsum = @yp
            WHERE rtrim(subcode) = @subcode AND dpm = @dpm
            """,
            new
            {
                yb = (double)(baln0 >= 0 ? baln0 : 0), ybc = (double)(baln0 < 0 ? -baln0 : 0),
                bd = (double)cmd.Bdesum, bc = (double)cmd.Bcresum,
                yd = (double)cmd.Ydesum, yc = (double)cmd.Ycresum, yp = (double)cmd.Ypalsum,
                subcode = Ansi(cmd.Subcode, 18), dpm = Ansi(dpm, 10),
            }, cancellationToken: ct));
    }

    /// <summary>启用帐套（fr_finuse row3）：平衡校验 → 清空九表（自动提交逐条）→ finsoursub 快照（照抄静默失败）→ l_use=1。</summary>
    public async Task<string> EnableAsync(string dpm, int subleng1, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 1. 一级科目六列平衡校验（SUM ... FOR LEN(ALLTRIM(subcode))=SUBLENG1）
        var bal = (await conn.QueryFirstOrDefaultAsync<(decimal yb, decimal ybc, decimal yd, decimal yc, decimal bd, decimal bc)>(
            new CommandDefinition(
                """
                SELECT ISNULL(SUM(ybdesum),0), ISNULL(SUM(ybcresum),0), ISNULL(SUM(ydesum),0),
                       ISNULL(SUM(ycresum),0), ISNULL(SUM(bdesum),0), ISNULL(SUM(bcresum),0)
                FROM finsub WITH (NOLOCK) WHERE dpm = @dpm AND LEN(LTRIM(RTRIM(subcode))) = @subleng1
                """, new { dpm = Ansi(dpm, 10), subleng1 }, cancellationToken: ct)));
        if (bal.yb != bal.ybc || bal.yd != bal.yc || bal.bd != bal.bc)
            return "帐务初始数据不平衡，不能启用帐套,\r\n请再调整帐务系统初始数据";
        // 2. 清空九表（SQLEXEC 逐条、自动提交；旧系统无事务）
        foreach (var table in new[] { "finaleger", "finsubit", "finitsub", "finamsum", "finrecout", "finreclist", "fincadistr", "finhisbal", "finvouch" })
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        // 3. finvid 归零
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE finvid SET vid = 0 WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        // 4. finsoursub 快照：finsub(35 列) vs finsoursub(31 列) 列数不匹配 —— 旧系统 SQLEXEC 返回值未检查
        //    静默吞掉，INSERT 从未成功。照抄：失败不中断、不提示（回放两侧 finsoursub 恒空）。
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO finsoursub SELECT * FROM finsub WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        }
        catch
        {
            // 旧系统行为：INSERT 失败静默继续
        }
        // 5. l_use=1（c_id 取本部门 syscontrol 行）
        var cid = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT RTRIM(c_id) FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct)) ?? "";
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE syscontrol SET l_use = 1 WHERE c_id = @cid AND dpm = @dpm",
            new { cid = Ansi(cid, 10), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        return "";
    }

    private static string PrevPeriod(string period)
    {
        var y = int.Parse(period[..4]);
        var m = int.Parse(period[4..]);
        return m == 1 ? $"{y - 1}12" : $"{y}{m - 1:D2}";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}
