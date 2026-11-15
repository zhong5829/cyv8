using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;

namespace QPGL.Infrastructure.Stock;

/// <summary>ISAPPE 库存冲减运行参数（旧 pg_main 登录装载，tbsysset type='system'）。</summary>
public sealed record IsappeParams(int PWay, bool Wbtohs, bool Xzfa, bool Fgs, bool Kcadd);

/// <summary>ISAPPE 源行快照（草稿表 tbistosm/tbisorsm/tbsadc 行字段，SCATTER MEMVAR 语义）。</summary>
public sealed record IsappeSource(
    string Dpm, string Cp, string Nno, string Na1, string Ty, string Fa, string Unit,
    decimal Iprj, decimal Iprc, decimal Oprc, decimal Oprct, decimal Oprb,
    string Posi, string Depotno, string Note, int Xno, string Dc);

/// <summary>ISAPPE 调用方库别差异（pg_proc_client 与 pg_proc_主程序过程库两版 isappe 的行为差异）。</summary>
public enum IsappeLibrary
{
    /// <summary>pg_proc_client过程库.prg L620-728（散件入库 fr_isto1 / 订单入库 fr_isor）：
    /// 命中批次无 ORDER BY；P_WAY=1/2 命中时 iprj 取库内原值（非加权时为 no-op）。</summary>
    Client,
    /// <summary>pg_proc_主程序过程库.prg L1546-1668（调仓 fr_sadc）：
    /// 命中批次 ORDER BY kcamount DESC 取最大批次；P_WAY=1/2 命中时 iprj 写源行值（非加权时覆盖库内）。</summary>
    MainProc,
}

/// <summary>
/// ISAPPE 库存冲减引擎（公共复刻，2026-10-24 用户确认「运行时读配置」契约）。
/// 旧实现出处：pg_proc_主程序过程库.prg L1546-1668 / pg_proc_client过程库.prg L620-728；
/// 参数装载 pg_main.prg + pg_proc L3247/L3273/L3409/L3423：tbsysset(type='system')
/// 「均价」valuen → P_WAY（缺省 2）、「外币为含税价」→ P_WBTOHS、「库存是否分产地」→ P_XZFA（true 时匹配含 fa）、
/// 「是否分部门」valuen → P_FGS ×「自动增加库存」→ P_KCADD（仅新建行后跨部门补零行分支）。
/// </summary>
public static class IsappeEngine
{
    /// <summary>读运行参数（须在调用方同一事务连接上执行；缺省值与旧装载代码一致）。</summary>
    public static async Task<IsappeParams> LoadParamsAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct = default)
    {
        var rows = (await conn.QueryAsync<(string name, decimal? valuen, bool? valuel)>(
            new CommandDefinition(
                "SELECT RTRIM(name), valuen, valuel FROM tbsysset WITH (NOLOCK) WHERE type = 'system' " +
                "AND name IN (N'均价', N'外币为含税价', N'库存是否分产地', N'自动增加库存', N'是否分部门')",
                transaction: tx, cancellationToken: ct))).AsList();

        decimal? pway = null, fgs = null;
        var wbtohs = false; var xzfa = false; var kcadd = false;
        foreach (var (name, valuen, valuel) in rows)
        {
            switch (name)
            {
                case "均价": pway = valuen; break;
                case "外币为含税价": wbtohs = valuel ?? false; break;
                case "库存是否分产地": xzfa = valuel ?? false; break;
                case "自动增加库存": kcadd = valuel ?? false; break;
                case "是否分部门": fgs = valuen; break;
            }
        }
        // 旧 L3247：均价越界/缺省 → 2；P_FGS 由登录逻辑判定（pg_main L387-409），此处读 tbsysset 是否分部门 valuen
        var way = pway is null or < 1 or > 2 ? 2 : (int)pway.Value;
        return new IsappeParams(way, wbtohs, xzfa, (fgs ?? 0) > 0, kcadd);
    }

    /// <summary>
    /// ISAPPE 核心冲减：批次匹配 + 命中 UPDATE / 未命中 INSERT tbisto（同连接同事务，§7.3）。
    /// 返回（批次 id，冲减前批次库存，冲减后数量，是否新建行）。
    /// KcBefore=旧主程序库 RETURN NRT（pg_proc L1601/L1635，命中时=C_ISAPPE.KCAMOUNT 即冲减前值）；
    /// 入库退货 fr_istk 用它回写 tbistoed.kcamount（退货前快照）；未命中（新建行）旧库返回空值，此处为 null。
    /// </summary>
    /// <param name="amount">冲减量（正入负出，旧 m.AMOUI）。</param>
    /// <param name="today">业务日期（旧 P_RQ；回放两侧必须同源传入）。</param>
    /// <param name="splitBatches">拆分冲减（2026-11-15 负库存防护需求，仅入库退货启用）：按匹配键取全部批次
    /// （kcamount DESC，UPDLOCK 锁至提交）从大到小逐批扣到扣够，Σ批次不足抛 STOCK_CONCURRENT 回滚；
    /// 拆分模式不插入负数新批次。false（缺省）保持旧单批语义，散件入库/订单入库/调仓零影响。</param>
    public static async Task<(int BatchId, decimal? KcBefore, decimal KcAfter, bool Inserted)> ApplyAsync(
        SqlConnection conn, SqlTransaction tx, IsappeSource src, decimal amount, IsappeParams prm,
        IsappeLibrary library, DateTime today, CancellationToken ct = default, bool splitBatches = false)
    {
        if (splitBatches && amount < 0)
            return await ApplySplitAsync(conn, tx, src, amount, prm, library, today, ct);

        var orderBy = library == IsappeLibrary.MainProc ? " ORDER BY kcamount DESC" : "";
        var (filter, filterArgs) = BuildMatchFilter(src, prm);
        var matchSql = $"SELECT TOP (1) id, kcamount, iprj, iprc, oprc, oprb FROM tbisto WITH (UPDLOCK) " +
                       $"WHERE {filter}{orderBy}";
        var matchArgs = filterArgs;
        var batch = await conn.QueryFirstOrDefaultAsync<(int id, decimal kc, decimal iprj, decimal iprc, decimal oprc, decimal oprb)>(
            new CommandDefinition(matchSql, matchArgs, transaction: tx, cancellationToken: ct));

        // 命中：oprc/oprb 一律取库内原值（两版库一致，等效 no-op）；iprj 依库别/加权而定
        var oprc = batch.id != 0 ? batch.oprc : (src.Oprct != 0 ? src.Oprct : src.Oprc);
        var iprj = batch.id != 0 && library == IsappeLibrary.Client ? batch.iprj : src.Iprj;
        decimal iprc = src.Iprc;
        if (batch.id != 0)
        {
            var kcBefore = batch.kc;
            var kcAfter = batch.kc + amount;
            if (prm.PWay == 2 && kcAfter != 0)
            {
                // 主程序 L1644-1646：ROUND(..., LCXSW=XSW(1,4) 进价小数位，本部署=4)；
                // client L716-718：ROUND(..., 2) 硬编码——散件入库 fr_isto1 / 订单入库 fr_isor 走 client 库
                //（2026-11-02 修正：此前恒 4 位与旧 client 库不符；XSW 配置变更属备案，MainProc 暂取部署值 4）
                var iprcDigits = library == IsappeLibrary.Client ? 2 : 4;
                iprc = Math.Round((Math.Max(batch.iprc * batch.kc, 0) + src.Iprc * amount) /
                                  (Math.Max(batch.kc, 0) + amount), iprcDigits);
                if (prm.Wbtohs)
                {
                    iprj = library == IsappeLibrary.MainProc
                        ? Math.Round((Math.Max(batch.iprj * batch.kc, 0) + src.Iprj * amount) /
                                     (Math.Max(batch.kc, 0) + amount), 2)
                        : Math.Round((batch.iprj * batch.kc + src.Iprj * amount) / (batch.kc + amount), 2);
                }
            }
            else if (prm.PWay == 2)
            {
                // 主程序 L1652：冲减后总量=0 时 m.IPRC 不重算（保持源行值）且 iprc 列始终在 UPDATE 列表中
                //（IIF(kc=0,',iprc',',iprc') 两侧相同——旧码原样）；client 库维持库内原值（已由回放验证）
                iprc = library == IsappeLibrary.MainProc ? src.Iprc : batch.iprc;
            }
            else
            {
                // P_WAY=1 两版库 UPDATE 均不含 iprc（主程序 L1612），此处写库内原值等效 no-op
                iprc = batch.iprc;
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbisto SET kcamount = kcamount + @amount, kcbhdate = @today, iprj = @iprj, " +
                "oprc = @oprc, oprb = @oprb, iprc = @iprc WHERE id = @id",
                new { amount, today, iprj, iprc, oprc, oprb = batch.oprb, id = batch.id },
                transaction: tx, cancellationToken: ct));
            return (batch.id, kcBefore, kcAfter, false);
        }

        // 未命中：INSERT 新批次行（cxnno=S_NNO(nno)，kcamount=AMOUI，价格取源行）
        var cxnno = await SnnoAsync(conn, tx, src.Cp, src.Nno, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tbisto
                (dpm, cp, nno, na1, unit, kcamount, ty, fa, cxnno, iprj, iprc, oprc, posi, depotno, note, xno, dc, kcbhdate, oprb)
            VALUES
                (@dpm, @cp, @nno, @na1, @unit, @kcamount, @ty, @fa, @cxnno, @iprj, @iprc, @oprc, @posi, @depotno, @note, @xno, @dc, @today, @oprb)
            """,
            new
            {
                dpm = Ansi(src.Dpm, 10), cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18), na1 = Ansi(src.Na1, 40),
                unit = Ansi(src.Unit, 4), kcamount = amount, ty = Ansi(src.Ty, 14), fa = Ansi(src.Fa, 8),
                cxnno = Ansi(cxnno, 18), iprj = src.Iprj, iprc = src.Iprc, oprc,
                posi = Ansi(src.Posi, 10), depotno = Ansi(src.Depotno, 10), note = Ansi(src.Note, 100),
                xno = src.Xno, dc = Ansi(src.Dc, 8), today, oprb = src.Oprb,
            },
            transaction: tx, cancellationToken: ct));
        var newId = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT CAST(SCOPE_IDENTITY() AS int)", transaction: tx, cancellationToken: ct));

        // 主程序 L1656-1667：M_INSERT × P_FGS × P_KCADD → 跨部门补零库存行（本部署 KCADD=0 不激活，忠实保留）
        if (prm.Fgs && prm.Kcadd)
        {
            var others = (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT RTRIM(dpm) FROM tbsysset WITH (NOLOCK) WHERE dpm <> @dpm AND RTRIM(name) = @cp AND type = 'cp'",
                new { dpm = Ansi(src.Dpm, 10), cp = Ansi(src.Cp, 10) },
                transaction: tx, cancellationToken: ct))).AsList();
            foreach (var otherDpm in others)
            {
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbisto WHERE nno = @nno AND dpm = @dpm AND cp = @cp",
                    new { nno = Ansi(src.Nno, 18), dpm = Ansi(otherDpm, 10), cp = Ansi(src.Cp, 10) },
                    transaction: tx, cancellationToken: ct));
                if (exists == 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbisto
                            (dpm, cp, nno, na1, unit, ty, fa, cxnno, iprj, iprc, oprc, note, xno, dc, kcbhdate, oprb)
                        VALUES
                            (@dpm, @cp, @nno, @na1, @unit, @ty, @fa, @cxnno, @iprj, @iprc, @oprc, @note, @xno, @dc, @today, @oprb)
                        """,
                        new
                        {
                            dpm = Ansi(otherDpm, 10), cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18),
                            na1 = Ansi(src.Na1, 40), unit = Ansi(src.Unit, 4), ty = Ansi(src.Ty, 14),
                            fa = Ansi(src.Fa, 8), cxnno = Ansi(cxnno, 18), iprj = src.Iprj, iprc = src.Iprc,
                            oprc, note = Ansi(src.Note, 100), xno = src.Xno, dc = Ansi(src.Dc, 8),
                            today, oprb = src.Oprb,
                        },
                        transaction: tx, cancellationToken: ct));
                }
            }
        }
        return (newId, null, amount, true);
    }

    /// <summary>ISAPPE 批次匹配过滤（P_WAY=1 加 iprc；XZFA 加 fa）——单批与拆分两种模式共用，保证键一致。</summary>
    private static (string Filter, object Args) BuildMatchFilter(IsappeSource src, IsappeParams prm)
    {
        var faFilter = prm.Xzfa ? " AND fa = @fa" : "";
        return prm.PWay switch
        {
            1 => ($"cp = @cp AND nno = @nno AND iprc = @iprc AND posi = @posi AND depotno = @depotno AND dpm = @dpm{faFilter}",
                  (object)new { cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18), iprc = src.Iprc, fa = Ansi(src.Fa, 8),
                                posi = Ansi(src.Posi, 10), depotno = Ansi(src.Depotno, 10), dpm = Ansi(src.Dpm, 10) }),
            _ => ($"cp = @cp AND nno = @nno AND posi = @posi AND depotno = @depotno AND dpm = @dpm{faFilter}",
                  (object)new { cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18), fa = Ansi(src.Fa, 8),
                                posi = Ansi(src.Posi, 10), depotno = Ansi(src.Depotno, 10), dpm = Ansi(src.Dpm, 10) }),
        };
    }

    /// <summary>拆分冲减匹配过滤（2026-11-15 负库存防护）：放宽 posi——库存拆多仓位而任一仓位不足一行退货数时，
    /// 单批键会误判"无批次"（旧系统此处扣成负库存）；拆分模式跨仓位聚合扣减，其余键（cp+nno+depotno+dpm
    /// [+fa XZFA][+iprc P_WAY=1]）与单批模式一致。</summary>
    private static (string Filter, object Args) BuildSplitMatchFilter(IsappeSource src, IsappeParams prm)
    {
        var faFilter = prm.Xzfa ? " AND fa = @fa" : "";
        return prm.PWay switch
        {
            1 => ($"cp = @cp AND nno = @nno AND iprc = @iprc AND depotno = @depotno AND dpm = @dpm{faFilter}",
                  (object)new { cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18), iprc = src.Iprc, fa = Ansi(src.Fa, 8),
                                depotno = Ansi(src.Depotno, 10), dpm = Ansi(src.Dpm, 10) }),
            _ => ($"cp = @cp AND nno = @nno AND depotno = @depotno AND dpm = @dpm{faFilter}",
                  (object)new { cp = Ansi(src.Cp, 10), nno = Ansi(src.Nno, 18), fa = Ansi(src.Fa, 8),
                                depotno = Ansi(src.Depotno, 10), dpm = Ansi(src.Dpm, 10) }),
        };
    }

    /// <summary>
    /// 拆分冲减（2026-11-15 负库存防护，仅入库退货）：按匹配键取全部批次（kcamount DESC，UPDLOCK 锁至提交），
    /// 从大到小逐批扣减到扣够；每批沿用单批命中分支的加权价公式（P_WAY=2 逐批重算 iprc/iprj）。
    /// Σ正库存批次不足 → STOCK_CONCURRENT（预检后被并发单据扣走的兜底）回滚；扣够即返回，
    /// 不走「未命中 INSERT 负数新批次」路径。返回：首个（最大）批次 id 与冲减前库存（tbistoed.kcamount 快照语义不变）。
    /// </summary>
    private static async Task<(int BatchId, decimal? KcBefore, decimal KcAfter, bool Inserted)> ApplySplitAsync(
        SqlConnection conn, SqlTransaction tx, IsappeSource src, decimal amount, IsappeParams prm,
        IsappeLibrary library, DateTime today, CancellationToken ct)
    {
        var (filter, filterArgs) = BuildSplitMatchFilter(src, prm);
        var sql = $"SELECT id, kcamount, iprj, iprc, oprc, oprb FROM tbisto WITH (UPDLOCK) WHERE {filter} ORDER BY kcamount DESC";
        var batches = (await conn.QueryAsync<(int id, decimal kc, decimal iprj, decimal iprc, decimal oprc, decimal oprb)>(
            new CommandDefinition(sql, filterArgs, transaction: tx, cancellationToken: ct))).AsList();

        var remaining = -amount;   // 待扣总量（正数）
        int? firstId = null;
        decimal firstKcBefore = 0, firstKcAfter = 0;
        foreach (var b in batches)
        {
            if (remaining <= 0) break;
            if (b.kc <= 0) continue;   // 已是零/负的异常批次不参与扣减（防扩大负数）
            var take = Math.Min(b.kc, remaining);
            var delta = -take;

            // 每批价格重算：与单批命中分支同公式（P_WAY=2 加权；冲减后=0 保持源行值；P_WAY=1 写库内原值）
            var iprj = library == IsappeLibrary.Client ? b.iprj : src.Iprj;
            var kcAfter = b.kc + delta;
            decimal iprc;
            if (prm.PWay == 2 && kcAfter != 0)
            {
                var iprcDigits = library == IsappeLibrary.Client ? 2 : 4;
                iprc = Math.Round((Math.Max(b.iprc * b.kc, 0) + src.Iprc * delta) /
                                  (Math.Max(b.kc, 0) + delta), iprcDigits);
                if (prm.Wbtohs)
                {
                    iprj = library == IsappeLibrary.MainProc
                        ? Math.Round((Math.Max(b.iprj * b.kc, 0) + src.Iprj * delta) /
                                     (Math.Max(b.kc, 0) + delta), 2)
                        : Math.Round((b.iprj * b.kc + src.Iprj * delta) / (b.kc + delta), 2);
                }
            }
            else if (prm.PWay == 2)
            {
                iprc = library == IsappeLibrary.MainProc ? src.Iprc : b.iprc;
            }
            else
            {
                iprc = b.iprc;
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbisto SET kcamount = kcamount + @delta, kcbhdate = @today, iprj = @iprj, " +
                "oprc = @oprc, oprb = @oprb, iprc = @iprc WHERE id = @id",
                new { delta, today, iprj, iprc, oprc = b.oprc, oprb = b.oprb, id = b.id },
                transaction: tx, cancellationToken: ct));

            if (firstId is null)
            {
                firstId = b.id;
                firstKcBefore = b.kc;
                firstKcAfter = kcAfter;
            }
            remaining -= take;
        }

        if (remaining > 0)
            throw new AppException("STOCK_CONCURRENT",
                $"{src.Cp.Trim()}{src.Nno.Trim()} 批次库存不足（差 {remaining:0.##}，可能已被并发单据扣减）", 409);
        return (firstId!.Value, firstKcBefore, firstKcAfter, false);
    }

    /// <summary>S_NNO 串号映射（pg_proc L980-990：tbsysset type='cp' 行 cxnno 模板，生产未配置=''）。</summary>
    public static async Task<string> SnnoAsync(SqlConnection conn, SqlTransaction? tx, string cp, string nno, CancellationToken ct = default)
    {
        var template = (await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP (1) RTRIM(cxnno) FROM tbsysset WITH (NOLOCK) WHERE RTRIM(name) = @cp AND type = 'cp'",
            new { cp = Ansi(cp.Trim(), 10) }, transaction: tx, cancellationToken: ct)) ?? "").Trim();
        var result = "";
        foreach (var ch in template)
        {
            var at = char.IsDigit(ch) ? ch - '0' : ch - 'A' + 10;   // VFP SUBSTR 1-based，越界取''
            result += at >= 1 && at <= nno.Length ? nno[at - 1].ToString() : "";
        }
        return result;
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
