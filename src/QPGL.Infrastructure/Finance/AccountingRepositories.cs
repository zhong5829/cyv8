using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Finance;

// =====================================================================================
// 账务管理 I 组（凭证核心闭环）—— 契约：开发文档/契约摘录/账务管理I组凭证闭环契约.md
//   I.9 基础资料（finsub/finmoney/finsumary/finset/finempl/finrecout）
//   I.1 凭证录入 fr_finvouch   I.3 凭证查询 fr_finquery / 审核 fr_finaudit / 过帐 fr_finpost
//   I.2 业务凭证 fr_finopvouch（生成 BBI/SRO + 审核/反审核；CBI/CRO/SEV 备案延后）
// syscontrol 套账参数（dpm 对应行）：leng1~6=3,3,3,2,2,2 科目级次、cashsub/balasub/accsub/paysub。
// PASSTH INSERT 自动补 dpm → 新仓储全部显式写 dpm（教训见出纳 G 组批）。
// =====================================================================================

/// <summary>套账参数（syscontrol，dpm 对应行）。</summary>
public sealed class FinSysControl
{
    public string CId { get; set; } = "";
    public bool LUse { get; set; }
    public int Leng1 { get; set; }
    public int Leng2 { get; set; }
    public int Leng3 { get; set; }
    public int Leng4 { get; set; }
    public int Leng5 { get; set; }
    public int Leng6 { get; set; }
    public string CashSub { get; set; } = "";
    public string BalaSub { get; set; } = "";
    public string AccSub { get; set; } = "";
    public string PaySub { get; set; } = "";
}

public sealed class FinSubRow
{
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public string Type { get; set; } = "";
    public string Fdc { get; set; } = "";
    public bool Fcash { get; set; }
    public bool Detacode { get; set; }
    public string Unit { get; set; } = "";
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
    public decimal Mpalsum { get; set; }
    public decimal Ypalsum { get; set; }
}

/// <summary>会计科目仓储（fr_finsub / fr_finsubadd 契约）。</summary>
public interface IFinSubRepository
{
    Task<FinSysControl?> GetSysControlAsync(string dpm, CancellationToken ct = default);
    Task<IReadOnlyList<FinSubRow>> GetSubsAsync(CancellationToken ct = default);
    Task AddSubAsync(FinSubRow row, decimal?[] balances, string dpm, CancellationToken ct = default);
    Task UpdateSubAsync(FinSubRow row, string dpm, CancellationToken ct = default);
    Task DeleteSubAsync(string subcode, string dpm, CancellationToken ct = default);
    Task SyncChildrenAsync(string subcode, FinSubRow row, CancellationToken ct = default);
    Task<string> AddSubForClientAsync(string client, string cty, string types, string dpm, CancellationToken ct = default);
}

public sealed class FinSubRepository(SqlConnectionFactory factory) : IFinSubRepository
{
    public async Task<FinSysControl?> GetSysControlAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<FinSysControl>(new CommandDefinition(
            """
            SELECT RTRIM(c_id) c_id, ISNULL(l_use, 0) l_use, ISNULL(leng1, 0) leng1, ISNULL(leng2, 0) leng2,
                   ISNULL(leng3, 0) leng3, ISNULL(leng4, 0) leng4, ISNULL(leng5, 0) leng5, ISNULL(leng6, 0) leng6,
                   RTRIM(ISNULL(cashsub,'')) cashsub, RTRIM(ISNULL(balasub,'')) balasub,
                   RTRIM(ISNULL(accsub,'')) accsub, RTRIM(ISNULL(paysub,'')) paysub
            FROM syscontrol WITH (NOLOCK) WHERE dpm = @dpm
            """, new { dpm = Ansi(dpm, 10) }, cancellationToken: ct))).FirstOrDefault();
    }

    public async Task<IReadOnlyList<FinSubRow>> GetSubsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinSubRow>(new CommandDefinition(
            """
            SELECT RTRIM(subcode) subcode, RTRIM(subname) subname, RTRIM(ISNULL(type,'')) type, RTRIM(ISNULL(fdc,'')) fdc,
                   ISNULL(fcash,0) fcash, ISNULL(detacode,0) detacode,
                   ISNULL(ybdesum,0) ybdesum, ISNULL(ybcresum,0) ybcresum,
                   ISNULL(bdesum,0) bdesum, ISNULL(bcresum,0) bcresum, ISNULL(cdesum,0) cdesum, ISNULL(ccresum,0) ccresum,
                   ISNULL(ydesum,0) ydesum, ISNULL(ycresum,0) ycresum, ISNULL(edesum,0) edesum, ISNULL(ecresum,0) ecresum,
                   ISNULL(mpalsum,0) mpalsum, ISNULL(ypalsum,0) ypalsum
            FROM finsub WITH (NOLOCK) ORDER BY subcode
            """, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>新增科目（finsubadd row3 L84-92；PASSTH 补 dpm；pyid/wbid 拼音码待 DM 复刻置空）。</summary>
    public async Task AddSubAsync(FinSubRow row, decimal?[] balances, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO finsub (subcode, subname, type, fdc, fcash, detacode, unit,
                                bdesum, bcresum, cdesum, ccresum, ydesum, ycresum, edesum, ecresum, mpalsum, ypalsum, dpm)
            VALUES (@subcode, @subname, @type, @fdc, @fcash, @detacode, @unit,
                    @b1, @b2, @b3, @b4, @b5, @b6, @b7, @b8, @b9, @b10, @dpm)
            """,
            new
            {
                row.Subcode, row.Subname, row.Type, row.Fdc, row.Fcash, row.Detacode, Unit = Ansi("", 10),
                b1 = balances[0], b2 = balances[1], b3 = balances[2], b4 = balances[3], b5 = balances[4],
                b6 = balances[5], b7 = balances[6], b8 = balances[7], b9 = balances[8], b10 = balances[9],
                dpm = Ansi(dpm, 10),
            }, cancellationToken: ct));
    }

    /// <summary>修改科目（finsubadd row3 L88-91）。</summary>
    public async Task UpdateSubAsync(FinSubRow row, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE finsub SET subname = @subname, type = @type, fdc = @fdc, fcash = @fcash WHERE rtrim(subcode) = @subcode",
            new { row.Subcode, row.Subname, row.Type, row.Fdc, row.Fcash }, cancellationToken: ct));
    }

    /// <summary>删除科目（finsub row2 L172-198）：有下级/凭证引用时余额上移由调用方先算好。</summary>
    public async Task DeleteSubAsync(string subcode, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 余额 12 列搬到上级科目（父码 = 去掉本级位宽段）
            var row = (await conn.QueryAsync<FinSubRow>(new CommandDefinition(
                "SELECT RTRIM(subcode) subcode, ISNULL(bdesum,0) bdesum, ISNULL(bcresum,0) bcresum, ISNULL(cdesum,0) cdesum, ISNULL(ccresum,0) ccresum, ISNULL(ydesum,0) ydesum, ISNULL(ycresum,0) ycresum, ISNULL(edesum,0) edesum, ISNULL(ecresum,0) ecresum, ISNULL(mpalsum,0) mpalsum, ISNULL(ypalsum,0) ypalsum FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @subcode",
                new { subcode = Ansi(subcode, 18) }, transaction: tx, cancellationToken: ct))).FirstOrDefault();
            if (row is null) throw new AppException("FINSUB_GONE", "科目不存在", 404);
            var parent = ParentCode(subcode);
            if (parent.Length > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE finsub SET bdesum = ISNULL(bdesum,0) + @bd, bcresum = ISNULL(bcresum,0) + @bc,
                                      cdesum = ISNULL(cdesum,0) + @cd, ccresum = ISNULL(ccresum,0) + @cc,
                                      ydesum = ISNULL(ydesum,0) + @yd, ycresum = ISNULL(ycresum,0) + @yc,
                                      edesum = @ed, ecresum = @ec, mpalsum = ISNULL(mpalsum,0) + @mp, ypalsum = ISNULL(ypalsum,0) + @yp,
                                      detacode = 1
                    WHERE rtrim(subcode) = @parent
                    """,
                    new
                    {
                        bd = row.Bdesum, bc = row.Bcresum, cd = row.Cdesum, cc = row.Ccresum,
                        yd = row.Ydesum, yc = row.Ycresum, ed = row.Edesum, ec = row.Ecresum,
                        mp = row.Mpalsum, yp = row.Ypalsum, parent = Ansi(parent, 18),
                    }, transaction: tx, cancellationToken: ct));
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM finsub WHERE rtrim(subcode) = @subcode",
                new { subcode = Ansi(subcode, 18) }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>编辑同步下级（finsubadd L101-118）：确认后逐下级 UPDATE 定义列。</summary>
    public async Task SyncChildrenAsync(string subcode, FinSubRow row, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE finsub SET fcash = @fcash, fdc = @fdc, type = @type
            WHERE rtrim(subcode) LIKE @prefix + '%' AND LEN(rtrim(subcode)) > LEN(@subcode)
            """,
            new { row.Fcash, row.Fdc, row.Type, subcode = Ansi(subcode, 18), prefix = Ansi(subcode, 18) },
            cancellationToken: ct));
    }

    /// <summary>按客户/供应商取或建明细科目（addsub_xzts_wxgl L4205；finsub_auto 池）。</summary>
    public async Task<string> AddSubForClientAsync(string client, string cty, string types, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var existing = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP (1) RTRIM(subcode) FROM finsub_auto WITH (NOLOCK) WHERE subname = @client AND type = @types",
            new { client = Ansi(client, 60), types = Ansi(types, 60) }, cancellationToken: ct));
        if (!string.IsNullOrEmpty(existing)) return existing;
        var subcode = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP (1) RTRIM(subcode) FROM finsub_auto WITH (NOLOCK) WHERE ISNULL(rtrim(subname),'') = '' AND type = @types AND rtrim(subcode) NOT IN (SELECT rtrim(subcode) FROM finsub)",
            new { types = Ansi(types, 60) }, cancellationToken: ct));
        if (string.IsNullOrEmpty(subcode))
            throw new AppException("FIN_SUB_POOL_EMPTY", "无可使用科目代码（finsub_auto 池空）", 409);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO finsub (subcode, subname, type, fdc, fclient, detacode, dpm)
            VALUES (@subcode, @client, @cty, @fdc, 1, 1, @dpm);
            UPDATE finsub_auto SET subname = @client WHERE rtrim(subcode) = @subcode AND type = @types;
            """,
            new
            {
                subcode = Ansi(subcode, 18), client = Ansi(client, 40), cty = Ansi(cty, 1),
                fdc = cty == "A" ? "D" : "C", dpm = Ansi(dpm, 10), types = Ansi(types, 60),
            }, cancellationToken: ct));
        return subcode;
    }

    /// <summary>父科目码 = 去掉本级位宽段（syscontrol leng1~6 = 3,3,3,2,2,2；dpm=A 实测固化）。</summary>
    private static string ParentCode(string subcode)
    {
        int[] lens = [3, 3, 3, 2, 2, 2];
        var acc = 0;
        foreach (var l in lens)
        {
            if (acc + l == subcode.Length) return acc == 0 ? "" : subcode[..acc];
            if (acc + l > subcode.Length) return "";
            acc += l;
        }
        return "";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}

/// <summary>基础资料小表（币别/摘要/核算项目/员工/现金银行）仓储（fr_finmoney/fr_finsumary/fr_finset/fr_finEmplf/fr_finaccount 契约）。</summary>
public interface IFinBasicRepository
{
    // 币别（finmoney）
    Task<IReadOnlyList<(string Mcode, string Mname, decimal Rate)>> GetCurrenciesAsync(CancellationToken ct = default);
    Task SaveCurrencyAsync(string mcode, string mname, decimal rate, bool isAdd, string dpm, CancellationToken ct = default);
    Task DeleteCurrencyAsync(string mcode, string dpm, CancellationToken ct = default);
    // 凭证摘要（finsumary）
    Task<IReadOnlyList<(int Id, string Smemo)>> GetSumariesAsync(CancellationToken ct = default);
    Task AddSumaryAsync(string smemo, string dpm, CancellationToken ct = default);
    Task DeleteSumaryAsync(int id, string dpm, CancellationToken ct = default);
    // 核算项目（finset）
    Task<IReadOnlyList<(int Id, string CId, string CName, int CItem)>> GetFinSetsAsync(CancellationToken ct = default);
    Task SaveFinSetAsync(int id, string cId, string cName, int cItem, bool isAdd, string dpm, CancellationToken ct = default);
    Task DeleteFinSetAsync(int id, string dpm, CancellationToken ct = default);
    // 员工（finempl）
    Task<IReadOnlyList<(string Ecode, string Ename)>> GetEmployeesAsync(CancellationToken ct = default);
    Task SaveEmployeeAsync(string ecode, string ename, bool isAdd, string dpm, CancellationToken ct = default);
    Task DeleteEmployeeAsync(string ecode, string dpm, CancellationToken ct = default);
    // 现金银行（finrecout）
    Task<IReadOnlyList<(int Id, string Accname, string Type, string Subcode, string Subname, string Accounts)>> GetAccountsAsync(CancellationToken ct = default);
    Task SaveAccountAsync(int id, string accname, string type, string subcode, string subname, string accounts, bool isAdd, string dpm, CancellationToken ct = default);
}

public sealed class FinBasicRepository(SqlConnectionFactory factory) : IFinBasicRepository
{
    public async Task<IReadOnlyList<(string, string, decimal)>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string, string, decimal)>(new CommandDefinition(
            "SELECT RTRIM(mcode), RTRIM(mname), ISNULL(rate,0) FROM finmoney WITH (NOLOCK) ORDER BY mcode", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SaveCurrencyAsync(string mcode, string mname, decimal rate, bool isAdd, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (isAdd)
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO finmoney (mcode, mname, rate, dpm) VALUES (@mcode, @mname, @rate, @dpm)",
                new { mcode = Ansi(mcode, 10), mname = Ansi(mname, 20), rate, dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        else
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finmoney SET mname = @mname, rate = @rate WHERE rtrim(mcode) = @mcode",
                new { mname = Ansi(mname, 20), rate, mcode = Ansi(mcode, 10) }, cancellationToken: ct));
    }

    public async Task DeleteCurrencyAsync(string mcode, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM finmoney WHERE rtrim(mcode) = @mcode AND dpm = @dpm",
            new { mcode = Ansi(mcode, 10), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<(int, string)>> GetSumariesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int, string)>(new CommandDefinition(
            "SELECT id, ISNULL(RTRIM(sumaryna),'') FROM finsumary WITH (NOLOCK) ORDER BY id", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task AddSumaryAsync(string sumaryna, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO finsumary (dpm, sumaryty, sumaryna, pyid, wbid) VALUES (@dpm, '', @sumaryna, '', '')",
            new { sumaryna = Ansi(sumaryna, 30), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    public async Task DeleteSumaryAsync(int id, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM finsumary WHERE id = @id AND dpm = @dpm", new { id, dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<(int, string, string, int)>> GetFinSetsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int, string, string, int)>(new CommandDefinition(
            "SELECT id, RTRIM(c_id), RTRIM(c_name), ISNULL(c_item,0) FROM finset WITH (NOLOCK) ORDER BY c_id, c_item", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SaveFinSetAsync(int id, string cId, string cName, int cItem, bool isAdd, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (isAdd)
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO finset (c_id, c_name, c_item, dpm) VALUES (@cId, @cName, @cItem, @dpm)",
                new { cId = Ansi(cId, 10), cName = Ansi(cName, 30), cItem, dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        else
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finset SET c_id = @cId, c_name = @cName, c_item = @cItem WHERE id = @id",
                new { cId = Ansi(cId, 10), cName = Ansi(cName, 30), cItem, id }, cancellationToken: ct));
    }

    public async Task DeleteFinSetAsync(int id, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 旧码随后的 c_item 重排为无效操作（只读游标 REPLACE），照抄：仅删除
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM finset WHERE id = @id AND dpm = @dpm", new { id, dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<(string, string)>> GetEmployeesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // Load 清理（fr_finEmplf row2）：DELETE finempl WHERE ecode='' or ename=''
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM finempl WHERE ISNULL(rtrim(ecode),'') = '' OR ISNULL(rtrim(ename),'') = ''", cancellationToken: ct));
        var rows = await conn.QueryAsync<(string, string)>(new CommandDefinition(
            "SELECT RTRIM(ecode), RTRIM(ename) FROM finempl WITH (NOLOCK) ORDER BY ecode", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SaveEmployeeAsync(string ecode, string ename, bool isAdd, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (isAdd)
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO finempl (ecode, ename, dpm) VALUES (@ecode, @ename, @dpm)",
                new { ecode = Ansi(ecode, 8), ename = Ansi(ename, 20), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        else
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finempl SET ename = @ename WHERE rtrim(ecode) = @ecode",
                new { ename = Ansi(ename, 20), ecode = Ansi(ecode, 8) }, cancellationToken: ct));
    }

    public async Task DeleteEmployeeAsync(string ecode, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM finempl WHERE rtrim(ecode) = @ecode AND dpm = @dpm",
            new { ecode = Ansi(ecode, 8), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<(int, string, string, string, string, string)>> GetAccountsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int, string, string, string, string, string)>(new CommandDefinition(
            """
            SELECT id, RTRIM(accname), RTRIM(ISNULL(type,'')), RTRIM(ISNULL(subcode,'')), RTRIM(ISNULL(subname,'')), RTRIM(ISNULL(accounts,''))
            FROM finrecout WITH (NOLOCK) ORDER BY id
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SaveAccountAsync(int id, string accname, string type, string subcode, string subname, string accounts, bool isAdd, string dpm, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accname)) throw new AppException("ACCNAME_REQUIRED", "帐目名称不能为空", 422);
        await using var conn = await factory.OpenAsync(ct);
        if (isAdd)
        {
            var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finrecout WITH (NOLOCK) WHERE rtrim(accname) = @accname",
                new { accname = Ansi(accname, 40) }, cancellationToken: ct));
            if (dup > 0) throw new AppException("ACCNAME_DUP", "帐目名称重复,请修改!", 409);
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO finrecout (accname, type, date, subcode, subname, accounts, dpm) VALUES (@accname, @type, @date, @subcode, @subname, @accounts, @dpm)",
                new
                {
                    accname = Ansi(accname, 40), type = Ansi(type, 1), date = DateTime.Today,
                    subcode = Ansi(subcode, 18), subname = Ansi(subname, 40), accounts = Ansi(accounts, 20),
                    dpm = Ansi(dpm, 10),
                }, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finrecout SET accname = @accname, type = @type, subcode = @subcode, subname = @subname, accounts = @accounts WHERE id = @id",
                new
                {
                    accname = Ansi(accname, 40), type = Ansi(type, 1), subcode = Ansi(subcode, 18),
                    subname = Ansi(subname, 40), accounts = Ansi(accounts, 20), id,
                }, cancellationToken: ct));
        }
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}

/// <summary>记帐凭证行（finvouch 分录）。</summary>
public sealed class FinVouchLine
{
    public string Note { get; set; } = "";
    public string Subcode { get; set; } = "";
    public string Subname { get; set; } = "";
    public string Type { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Amount { get; set; }
    public string Unit { get; set; } = "";
    public string Dept { get; set; } = "";
    public string Item { get; set; } = "";
    public string Empl { get; set; } = "";
    public string Client { get; set; } = "";
    public int Abill { get; set; }
    public string Depotno { get; set; } = "";
}

public sealed record FinVouchSaveCommand(int VouchId, int Vcode, DateTime Date, string Vouword,
    IReadOnlyList<FinVouchLine> Lines, bool IsAdd);

public sealed class FinVouchRow
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
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Vouword { get; set; } = "";
    public string Client { get; set; } = "";
    public string Opertype { get; set; } = "";
    public string Opercode { get; set; } = "";
    public bool Audit { get; set; }
    public bool Post { get; set; }
    public string Maker { get; set; } = "";
    public string Auditer { get; set; } = "";
    public string Poster { get; set; } = "";
    public string Dcdir { get; set; } = "";
    public decimal Balance { get; set; }
}

/// <summary>
/// 记帐凭证仓储（fr_finvouch 录入 / fr_finquery 查询 / fr_finaudit 审核 / fr_finpost 过帐契约）。
/// 过帐：逐凭证事务；分录科目逐级汇总（gsubleng 位宽 3,3,3,2,2,2）→ finsub 余额列 → finaleger 3 行
/// （期初余额/本期合计/本年累计，recoid 为 char(1)）→ finamsum 数量金额账 → finitsub/finsubit 核算项目
/// → 凭证行回写 dcdir/balance/post/poster。反过帐对称回退（finhisbal 月结拦截）。
/// </summary>
public interface IFinVouchRepository
{
    Task<int> PeekNextVidAsync(string dpm, CancellationToken ct = default);
    Task<int> PeekNextVcodeAsync(string period, CancellationToken ct = default);
    Task<bool> ChePostAsync(DateTime date, int tp, CancellationToken ct = default);
    Task<FinVouchSaveResult> SaveAsync(FinVouchSaveCommand cmd, string dpm, string per, CancellationToken ct = default);
    Task DeleteVouchAsync(int vouchid, string dpm, string per, CancellationToken ct = default);
    Task<IReadOnlyList<FinVouchRow>> QueryAsync(string periodFrom, int? vouchid, string? subcode, CancellationToken ct = default);
    Task<int> AuditPeriodAsync(string period, string per, CancellationToken ct = default);
    Task<int> PostAsync(string period, string per, string dpm, bool byDate, DateTime? date, bool skipCheck, CancellationToken ct = default);
    Task<int> UnpostAsync(string period, string per, string dpm, bool byDate, DateTime? date, CancellationToken ct = default);
}

public sealed class FinVouchSaveResult
{
    public int VouchId { get; set; }
    public int Vcode { get; set; }
}

public sealed class FinVouchRepository(SqlConnectionFactory factory) : IFinVouchRepository
{
    private static readonly int[] SubLens = [3, 3, 3, 2, 2, 2];   // syscontrol leng1~6（dpm=A 实测）

    public async Task<int> PeekNextVidAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT vid + 1 FROM finvid WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    public async Task<int> PeekNextVcodeAsync(string period, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT ISNULL(MAX(vcode), 0) + 1 FROM finvouch WITH (NOLOCK) WHERE period = @period",
            new { period = Ansi(period, 6) }, cancellationToken: ct));
    }

    /// <summary>CHEPOST（pg_proc L3533）：tp=1 finhisbal 月结；tp=2 finvouch CAF 结转损益。</summary>
    public async Task<bool> ChePostAsync(DateTime date, int tp, CancellationToken ct = default)
    {
        var period = date.ToString("yyyyMM");
        await using var conn = await factory.OpenAsync(ct);
        if (tp == 1)
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finhisbal WITH (NOLOCK) WHERE period = @period",
                new { period = Ansi(period, 6) }, cancellationToken: ct)) > 0;
        if (tp == 2)
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finvouch WITH (NOLOCK) WHERE opertype = 'CAF' AND period = @period",
                new { period = Ansi(period, 6) }, cancellationToken: ct)) > 0;
        return false;
    }

    /// <summary>凭证保存（fr_finvouch Save row2 L252-286；校验在 VM，仓储兜底重码/期间）。</summary>
    public async Task<FinVouchSaveResult> SaveAsync(FinVouchSaveCommand cmd, string dpm, string per, CancellationToken ct = default)
    {
        var period = cmd.Date.ToString("yyyyMM");
        await using var conn = await factory.OpenAsync(ct);
        // Add 查重（row2 L190-201）
        if (cmd.IsAdd)
        {
            var dupVid = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid",
                new { vid = cmd.VouchId }, cancellationToken: ct));
            if (dupVid > 0) throw new AppException("VOUCHID_DUP", "顺序号重复,不能保存!", 409);
            var dupVcode = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finvouch WITH (NOLOCK) WHERE period = @period AND vcode = @vcode",
                new { period = Ansi(period, 6), vcode = cmd.Vcode }, cancellationToken: ct));
            if (dupVcode > 0) throw new AppException("VCODE_DUP", "凭证编号重复,不能保存!", 409);
        }

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            if (cmd.IsAdd)
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE finvid SET vid = vid + 1 WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) },
                    transaction: tx, cancellationToken: ct));
            else
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM finvouch WHERE vouchid = @vid", new { vid = cmd.VouchId },
                    transaction: tx, cancellationToken: ct));

            var recoid = 0;
            foreach (var l in cmd.Lines)
            {
                if (l.Debit == 0 && l.Credit == 0) continue;
                if (string.IsNullOrWhiteSpace(l.Subcode)) continue;
                recoid++;
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO finvouch (vouchid, vcode, date, period, recoid, note, subcode, subname, type, fcash,
                                          debit, credit, vouword, abill, amount, unit, dept, item, empl, client,
                                          audit, post, maker, depotno, dpm)
                    VALUES (@vouchid, @vcode, @date, @period, @recoid, @note, @subcode, @subname, @type, 0,
                            @debit, @credit, @vouword, @abill, @amount, @unit, @dept, @item, @empl, @client,
                            0, 0, @maker, @depotno, @dpm)
                    """,
                    new
                    {
                        vouchid = cmd.VouchId, vcode = cmd.Vcode, date = cmd.Date, period = Ansi(period, 6),
                        recoid, note = Ansi(l.Note, 60), subcode = Ansi(l.Subcode, 18), subname = Ansi(l.Subname, 40),
                        type = Ansi(l.Type, 1), debit = l.Debit, credit = l.Credit, vouword = Ansi(cmd.Vouword, 4),
                        abill = l.Abill, amount = (double)l.Amount, unit = Ansi(l.Unit, 10),
                        dept = Ansi(l.Dept, 20), item = Ansi(l.Item, 20), empl = Ansi(l.Empl, 10),
                        client = Ansi(l.Client, 40), maker = Ansi(per, 10), depotno = Ansi(l.Depotno, 10),
                        dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return new FinVouchSaveResult { VouchId = cmd.VouchId, Vcode = cmd.Vcode };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>凭证删除（row2 L5-22）：已审核/系统生成（CAF 除外）拒绝。</summary>
    public async Task DeleteVouchAsync(int vouchid, string dpm, string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var row = (await conn.QueryAsync<(bool audit, string opertype)>(new CommandDefinition(
                "SELECT TOP (1) ISNULL(audit,0), ISNULL(rtrim(opertype),'') FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid",
                new { vid = vouchid }, transaction: tx, cancellationToken: ct))).FirstOrDefault();
            if (row.audit) throw new AppException("VOUCH_AUDITED", "凭证已审核,不能删除!", 409);
            if (!string.IsNullOrEmpty(row.opertype) && row.opertype != "CAF")
                throw new AppException("VOUCH_SYS_GEN", "系统生成的凭证不能删除!", 409);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM finvouch WHERE vouchid = @vid", new { vid = vouchid },
                transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<FinVouchRow>> QueryAsync(string periodFrom, int? vouchid, string? subcode, CancellationToken ct = default)
    {
        var where = "period >= @pf";
        var p = new DynamicParameters();
        p.Add("@pf", Ansi(periodFrom, 6));
        if (vouchid.HasValue) { where += " AND vouchid = @vid"; p.Add("@vid", vouchid.Value); }
        if (!string.IsNullOrWhiteSpace(subcode))
        {
            where += " AND rtrim(subcode) LIKE @sub + '%'";
            p.Add("@sub", Ansi(subcode.Trim(), 18));
        }
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FinVouchRow>(new CommandDefinition(
            $"""
            SELECT id, vouchid, vcode, date, RTRIM(period) period, recoid, ISNULL(RTRIM(note),'') note,
                   RTRIM(subcode) subcode, RTRIM(subname) subname, RTRIM(ISNULL(type,'')) type,
                   ISNULL(debit,0) debit, ISNULL(credit,0) credit, RTRIM(vouword) vouword,
                   RTRIM(ISNULL(client,'')) client, RTRIM(ISNULL(opertype,'')) opertype, RTRIM(ISNULL(opercode,'')) opercode,
                   ISNULL(audit,0) audit, ISNULL(post,0) post, RTRIM(ISNULL(maker,'')) maker,
                   RTRIM(ISNULL(auditer,'')) auditer, RTRIM(ISNULL(poster,'')) poster,
                   RTRIM(ISNULL(dcdir,'')) dcdir, ISNULL(balance,0) balance
            FROM finvouch WITH (NOLOCK) WHERE {where} ORDER BY period, vouchid, recoid
            """, p, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>整期审核（fr_finaudit row3）：UPDATE finvouch SET audit=1,auditer WHERE period AND audit=0。</summary>
    public async Task<int> AuditPeriodAsync(string period, string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE finvouch SET audit = 1, auditer = @per WHERE period = @period AND ISNULL(audit,0) = 0",
            new { per = Ansi(per, 10), period = Ansi(period, 6) }, cancellationToken: ct));
    }

    /// <summary>过帐（fr_finpost post，row2 L2-207）。返回过帐凭证张数。</summary>
    public async Task<int> PostAsync(string period, string per, string dpm, bool byDate, DateTime? date, bool skipCheck, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var sql = byDate && date.HasValue
            ? "SELECT DISTINCT vouchid FROM finvouch WITH (NOLOCK) WHERE date = @d AND ISNULL(audit,0) = 1 AND ISNULL(post,0) = 0 ORDER BY vouchid"
            : "SELECT DISTINCT vouchid FROM finvouch WITH (NOLOCK) WHERE period = @p AND ISNULL(audit,0) = 1 AND ISNULL(post,0) = 0 ORDER BY vouchid";
        var p = new DynamicParameters();
        p.Add("@d", date ?? DateTime.Today);
        p.Add("@p", Ansi(period, 6));
        var vids = (await conn.QueryAsync<int>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
        if (vids.Count == 0) throw new AppException("FIN_POST_EMPTY", "当前会计期间没有需要过帐的凭证!", 404);
        var count = 0;
        foreach (var vid in vids)
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                var lines = (await conn.QueryAsync<(int id, string subcode, decimal debit, decimal credit,
                    decimal amount, string unit, string opertype, string dept, string item, string empl, string client,
                    string vouword, string subname)>(new CommandDefinition(
                    "SELECT id, RTRIM(subcode), ISNULL(debit,0), ISNULL(credit,0), ISNULL(amount,0), RTRIM(ISNULL(unit,'')), ISNULL(rtrim(opertype),''), RTRIM(ISNULL(dept,'')), RTRIM(ISNULL(item,'')), RTRIM(ISNULL(empl,'')), RTRIM(ISNULL(client,'')), RTRIM(vouword), RTRIM(subname) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid ORDER BY recoid",
                    new { vid }, transaction: tx, cancellationToken: ct))).AsList();
                foreach (var l in lines)
                {
                    // 逐级汇总（J 从 LEN 递减到一级位宽 3）
                    var code = l.subcode.Trim();
                    var j = code.Length;
                    while (j >= 3 && j <= code.Length)
                    {
                        var fcode = code[..j];
                        await ApplySubTotalsAsync(conn, tx, fcode, l, per, period, dpm, ct: ct);
                        j -= SubWidth(j);
                    }
                    await ApplyFinItsubAsync(conn, tx, l, per, period, un: false, ct);
                    await ApplyFinSubitAsync(conn, tx, l, per, period, un: false, ct);
                    // 凭证行回写（L170-181）
                    var finRow = await TryReadSubAsync(conn, tx, code);
                    var fin = finRow ?? new FinSubRow();
                    var deCre = fin.Edesum - fin.Ecresum;
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE finvouch SET dcdir = @dcdir, balance = @balance, post = 1, poster = @per WHERE id = @id",
                        new
                        {
                            dcdir = Ansi(deCre > 0 ? "借" : deCre < 0 ? "贷" : "平", 2),
                            balance = (double)Math.Abs(deCre), per = Ansi(per, 10), l.id,
                        }, transaction: tx, cancellationToken: ct));
                }
                await tx.CommitAsync(ct);
                count++;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        return count;
    }

    /// <summary>反过帐（unpost，row2 L208-365）：finhisbal 月结拦截；对称回退。</summary>
    public async Task<int> UnpostAsync(string period, string per, string dpm, bool byDate, DateTime? date, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finhisbal WITH (NOLOCK) WHERE period = @period AND dpm = @dpm",
                new { period = Ansi(period, 6), dpm = Ansi(dpm, 10) }, cancellationToken: ct)) > 0)
            throw new AppException("FIN_MONTH_CLOSED", "该月已结帐,不能做反过帐!", 409);
        var sql = byDate && date.HasValue
            ? "SELECT DISTINCT vouchid FROM finvouch WITH (NOLOCK) WHERE date = @d AND ISNULL(audit,0) = 1 AND ISNULL(post,0) = 1 ORDER BY vouchid"
            : "SELECT DISTINCT vouchid FROM finvouch WITH (NOLOCK) WHERE period = @p AND ISNULL(audit,0) = 1 AND ISNULL(post,0) = 1 ORDER BY vouchid";
        var p = new DynamicParameters();
        p.Add("@d", date ?? DateTime.Today);
        p.Add("@p", Ansi(period, 6));
        var vids = (await conn.QueryAsync<int>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
        if (vids.Count == 0) throw new AppException("FIN_UNPOST_EMPTY", "当前会计期间没有需要反过帐的凭证!", 404);
        var count = 0;
        foreach (var vid in vids)
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                var lines = (await conn.QueryAsync<(int id, string subcode, decimal debit, decimal credit,
                    decimal amount, string unit, string opertype, string dept, string item, string empl, string client,
                    string vouword, string subname)>(new CommandDefinition(
                    "SELECT id, RTRIM(subcode), ISNULL(debit,0), ISNULL(credit,0), ISNULL(amount,0), RTRIM(ISNULL(unit,'')), ISNULL(rtrim(opertype),'VOU'), RTRIM(ISNULL(dept,'')), RTRIM(ISNULL(item,'')), RTRIM(ISNULL(empl,'')), RTRIM(ISNULL(client,'')), RTRIM(vouword), RTRIM(subname) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid ORDER BY recoid",
                    new { vid }, transaction: tx, cancellationToken: ct))).AsList();
                foreach (var l in lines)
                {
                    var code = l.subcode.Trim();
                    var j = code.Length;
                    while (j >= 3 && j <= code.Length)
                    {
                        var fcode = code[..j];
                        await ApplySubTotalsAsync(conn, tx, fcode, l, per, period, dpm, un: true, ct: ct);
                        j -= SubWidth(j);
                    }
                    await ApplyFinItsubAsync(conn, tx, l, per, period, un: true, ct);
                    await ApplyFinSubitAsync(conn, tx, l, per, period, un: true, ct);
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE finvouch SET dcdir = '', balance = 0, post = 0, poster = '' WHERE id = @id",
                        new { l.id }, transaction: tx, cancellationToken: ct));
                }
                await tx.CommitAsync(ct);
                count++;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        return count;
    }

    /// <summary>科目逐级汇总（post L52-149 / unpost L255-311）：finsub 余额列 + finaleger recoid=2/3 回写 + finamsum 数量金额账。</summary>
    private static async Task ApplySubTotalsAsync(SqlConnection conn, SqlTransaction tx, string fcode,
        (int id, string subcode, decimal debit, decimal credit, decimal amount, string unit, string opertype,
            string dept, string item, string empl, string client, string vouword, string subname) l,
        string per, string period, string dpm, bool un = false, CancellationToken ct = default)
    {
        var sub = await TryReadSubAsync(conn, tx, fcode);
        if (sub is null) return;   // 上级科目不存在 → 旧系统 SEEK 无行跳过
        var sign = un ? -1 : 1;
        var cdesum = sub.Cdesum + sign * l.debit;
        var ccresum = sub.Ccresum + sign * l.credit;
        var ydesum = sub.Ydesum + sign * l.debit;
        var ycresum = sub.Ycresum + sign * l.credit;
        var ede0 = sub.Bdesum - sub.Bcresum + cdesum - ccresum;
        var edesum = ede0 >= 0 ? ede0 : 0;
        var ecresum = ede0 < 0 ? -ede0 : 0;
        var mpal = sub.Mpalsum;
        var ypal = sub.Ypalsum;
        if (l.opertype != "CAF" && fcode.StartsWith('5'))
        {
            var d = sub.Fdc == "C" ? l.credit - l.debit : l.debit - l.credit;
            mpal += sign * d;
            ypal += sign * d;
        }
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE finsub SET cdesum = @cd, ccresum = @cc, ydesum = @yd, ycresum = @yc,
                              edesum = @ed, ecresum = @ec, mpalsum = @mp, ypalsum = @yp
            WHERE rtrim(subcode) = @fcode
            """,
            new { cd = cdesum, cc = ccresum, yd = ydesum, yc = ycresum, ed = edesum, ec = ecresum, mp = mpal, yp = ypal, fcode = Ansi(fcode, 18) },
            transaction: tx, cancellationToken: ct));

        var nbal = sub.Bdesum - sub.Bcresum;
        var finCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM finaleger WITH (NOLOCK) WHERE rtrim(subcode) = @fcode AND period = @period",
            new { fcode = Ansi(fcode, 18), period = Ansi(period, 6) }, transaction: tx, cancellationToken: ct));
        if (finCount == 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO finaleger (subcode, subname, period, recoid, csummary, desum, cresum, dcdir, balance)
                VALUES (@fcode, @fname, @period, '1', '期初余额', 0, 0, @nbalDir, @nbalAbs),
                       (@fcode, @fname, @period, '2', '本期合计', @d2, @c2, @dir2, @bal2),
                       (@fcode, @fname, @period, '3', '本年累计', @d3, @c3, @dir2, @bal2)
                """,
                new
                {
                    fcode = Ansi(fcode, 18), fname = Ansi(sub.Subname, 40), period = Ansi(period, 6),
                    d2 = (double)cdesum, c2 = (double)ccresum,
                    dir2 = Ansi(ede0 > 0 ? "借" : ede0 < 0 ? "贷" : "平", 2), bal2 = (double)Math.Abs(ede0),
                    d3 = (double)ydesum, c3 = (double)ycresum,
                    nbalDir = Ansi(nbal > 0 ? "借" : nbal < 0 ? "贷" : "平", 2), nbalAbs = (double)Math.Abs(nbal),
                }, transaction: tx, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE finaleger SET desum = @d2, cresum = @c2, dcdir = @dir2, balance = @bal2
                WHERE rtrim(subcode) = @fcode AND period = @period AND recoid = '2'
                """,
                new
                {
                    d2 = (double)cdesum, c2 = (double)ccresum,
                    dir2 = Ansi(ede0 > 0 ? "借" : ede0 < 0 ? "贷" : "平", 2), bal2 = (double)Math.Abs(ede0),
                    fcode = Ansi(fcode, 18), period = Ansi(period, 6),
                }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finaleger SET desum = @d3, cresum = @c3 WHERE rtrim(subcode) = @fcode AND period = @period AND recoid = '3'",
                new { d3 = (double)ydesum, c3 = (double)ycresum, fcode = Ansi(fcode, 18), period = Ansi(period, 6) },
                transaction: tx, cancellationToken: ct));
        }

        // 数量金额账（L105-143）：unit 非空或 amount≠0
        if (sub.Unit.Length > 0 || l.amount != 0)
        {
            var hasCur = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finamsum WITH (NOLOCK) WHERE rtrim(subcode) = @fcode AND period = @period",
                new { fcode = Ansi(fcode, 18), period = Ansi(period, 6) }, transaction: tx, cancellationToken: ct));
            if (hasCur == 0 && !un)
            {
                var prev = await conn.QuerySingleOrDefaultAsync<(decimal bAmount, decimal bBalance, string bDcdir)>(new CommandDefinition(
                    "SELECT TOP (1) ISNULL(bamount,0), ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')) FROM finamsum WITH (NOLOCK) WHERE rtrim(subcode) = @fcode AND period = @pperiod",
                    new { fcode = Ansi(fcode, 18), pperiod = Ansi(PrevPeriod(period), 6) },
                    transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO finamsum (subcode, subname, period, isdetail, bdcdir, bamount, bbalance, unit, dpm) VALUES (@fcode, @fname, @period, @isd, @bdc, @bam, @bbal, @unit, @dpm)",
                    new
                    {
                        fcode = Ansi(fcode, 18), fname = Ansi(sub.Subname, 40), period = Ansi(period, 6),
                        isd = sub.Detacode, bdc = Ansi(prev.bDcdir ?? "平", 2), bam = (double)prev.bAmount,
                        bbal = (double)prev.bBalance, unit = Ansi(l.unit, 10), dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
            }
            var cur = await conn.QuerySingleOrDefaultAsync<(decimal cdeamou, decimal cdesum, decimal ccreamou, decimal ccresum,
                decimal ydeamou, decimal ydesum, decimal bamount, decimal bbalance, string bdcdir)>(new CommandDefinition(
                "SELECT ISNULL(cdeamou,0), ISNULL(cdesum,0), ISNULL(ccreamou,0), ISNULL(ccresum,0), ISNULL(ydeamou,0), ISNULL(ydesum,0), ISNULL(bamount,0), ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')) FROM finamsum WITH (NOLOCK) WHERE rtrim(subcode) = @fcode AND period = @period",
                new { fcode = Ansi(fcode, 18), period = Ansi(period, 6) }, transaction: tx, cancellationToken: ct));
            if (cur.bdcdir.Length > 0 || cur.cdeamou != 0 || cur.cdesum != 0 || cur.ccreamou != 0 || cur.ccresum != 0)
            {
                var cdeamou = cur.cdeamou + sign * (l.debit != 0 ? l.amount : 0);
                var cdesum2 = cur.cdesum + sign * (l.debit != 0 ? l.debit : 0);
                var ccreamou = cur.ccreamou + sign * (l.debit != 0 ? 0 : l.amount);
                var ccresum2 = cur.ccresum + sign * (l.debit != 0 ? 0 : l.credit);
                var ydeamou = cur.ydeamou + sign * (l.debit != 0 ? l.amount : 0);
                var ydesum2 = cur.ydesum + sign * (l.debit != 0 ? l.debit : 0);
                // 旧码 BAM0 = IIF(BDCDIR != ',贷', 1, -1) * BAMOUNT + CDEAMOU - CCREAMOU（'!=',贷'' 为 VFP 原样怪表达式，等效 BDCDIR<>'贷'）
                var bam0 = (cur.bdcdir != "贷" ? 1 : -1) * cur.bamount + cdeamou - ccreamou;
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE finamsum SET cdeamou = @cde, cdesum = @cds, ccreamou = @ccr, ccresum = @ccs,
                                        ydeamou = @yde, ydesum = @yds, edcdir = @edc, eamount = @eam, ebalance = @ebal
                    WHERE rtrim(subcode) = @fcode AND period = @period
                    """,
                    new
                    {
                        cde = (double)cdeamou, cds = (double)cdesum2, ccr = (double)ccreamou, ccs = (double)ccresum2,
                        yde = (double)ydeamou, yds = (double)ydesum2,
                        edc = Ansi(bam0 > 0 ? "借" : bam0 < 0 ? "贷" : "平", 2),
                        eam = (double)Math.Abs(bam0), ebal = (double)Math.Abs(cur.bbalance + cdesum2 - ccresum2),
                        fcode = Ansi(fcode, 18), period = Ansi(period, 6),
                    }, transaction: tx, cancellationToken: ct));
            }
        }
    }

    /// <summary>核算项目维度账（chekitem L366-400）：finitsub（itemna+itemty+subcode+period）。</summary>
    private static async Task ApplyFinItsubAsync(SqlConnection conn, SqlTransaction tx,
        (int id, string subcode, decimal debit, decimal credit, decimal amount, string unit, string opertype,
            string dept, string item, string empl, string client, string vouword, string subname) l,
        string per, string period, bool un, CancellationToken ct = default)
    {
        var code = l.subcode.Trim();
        foreach (var (ty, name, enabled) in new[]
                 {
                     ("D", l.dept, l.dept.Length > 0), ("I", l.item, l.item.Length > 0),
                     ("E", l.empl, l.empl.Length > 0), ("C", l.client, l.client.Length > 0 && (l.opertype == "A" || l.opertype == "P")),
                 })
        {
            if (!enabled) continue;
            var sign = un ? -1 : 1;
            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finitsub WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                new { name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), period = Ansi(period, 6) },
                transaction: tx, cancellationToken: ct));
            if (exists == 0 && !un)
            {
                var prev = await conn.QuerySingleOrDefaultAsync<(decimal bbal, string bdc)>(new CommandDefinition(
                    "SELECT TOP (1) ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')) FROM finitsub WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @pperiod",
                    new { name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), pperiod = Ansi(PrevPeriod(period), 6) },
                    transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO finitsub (itemty, itemna, subcode, subname, bdcdir, period, bbalance, vouword, cdesum, ccresum, edcdir, ebalance)
                    VALUES (@ty, @name, @code, @subname, @bdc, @period, @bbal, @vouword, @cd, @cc, @edc, @ebal)
                    """,
                    new
                    {
                        ty = Ansi(ty, 1), name = Ansi(name, 40), code = Ansi(code, 18), subname = Ansi(l.subname, 40),
                        bdc = Ansi(prev.bdc ?? "平", 2), period = Ansi(period, 6), bbal = (double)prev.bbal,
                        vouword = Ansi(l.vouword, 4), cd = (double)l.debit, cc = (double)l.credit,
                        edc = Ansi("", 2), ebal = (double)0m,
                    }, transaction: tx, cancellationToken: ct));
            }
            var row = await conn.QuerySingleOrDefaultAsync<(decimal bbal, string bdc, decimal cd, decimal cc)>(new CommandDefinition(
                "SELECT ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')), ISNULL(cdesum,0), ISNULL(ccresum,0) FROM finitsub WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                new { name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), period = Ansi(period, 6) },
                transaction: tx, cancellationToken: ct));
            if (row.bbal == 0 && row.bdc.Length == 0 && row.cd == 0 && row.cc == 0 && exists == 0) continue;
            var cd2 = row.cd + sign * l.debit;
            var cc2 = row.cc + sign * l.credit;
            if (un && cd2 == 0 && cc2 == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM finitsub WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                    new { name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18), period = Ansi(period, 6) },
                    transaction: tx, cancellationToken: ct));
                continue;
            }
            var eb = (row.bdc == "贷" ? -1 * row.bbal : row.bbal) + cd2 - cc2;
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finitsub SET cdesum = @cd, ccresum = @cc, edcdir = @edc, ebalance = @eb WHERE rtrim(itemna) = @name AND itemty = @ty AND rtrim(subcode) = @code AND period = @period",
                new
                {
                    cd = (double)cd2, cc = (double)cc2, edc = Ansi(eb > 0 ? "借" : eb < 0 ? "贷" : "平", 2),
                    eb = (double)Math.Abs(eb), name = Ansi(name, 40), ty = Ansi(ty, 1), code = Ansi(code, 18),
                    period = Ansi(period, 6),
                }, transaction: tx, cancellationToken: ct));
        }
    }

    /// <summary>核算项目汇总账（itemgen L401-435）：finsubit（itemna+itemty+period）。</summary>
    private static async Task ApplyFinSubitAsync(SqlConnection conn, SqlTransaction tx,
        (int id, string subcode, decimal debit, decimal credit, decimal amount, string unit, string opertype,
            string dept, string item, string empl, string client, string vouword, string subname) l,
        string per, string period, bool un, CancellationToken ct = default)
    {
        foreach (var (ty, name, enabled) in new[]
                 {
                     ("D", l.dept, l.dept.Length > 0), ("I", l.item, l.item.Length > 0),
                     ("E", l.empl, l.empl.Length > 0), ("C", l.client, l.client.Length > 0 && (l.opertype == "A" || l.opertype == "P")),
                 })
        {
            if (!enabled) continue;
            var sign = un ? -1 : 1;
            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finsubit WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                new { name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6) },
                transaction: tx, cancellationToken: ct));
            if (exists == 0 && !un)
            {
                var prev = await conn.QuerySingleOrDefaultAsync<(decimal bbal, string bdc)>(new CommandDefinition(
                    "SELECT TOP (1) ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')) FROM finsubit WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @pperiod",
                    new { name = Ansi(name, 40), ty = Ansi(ty, 1), pperiod = Ansi(PrevPeriod(period), 6) },
                    transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO finsubit (itemty, itemna, period, vouword, bdcdir, bbalance, cdesum, ccresum, edcdir, ebalance) " +
                    "VALUES (@ty, @name, @period, @vouword, @bdc, @bbal, @cd, @cc, '', 0)",
                    new
                    {
                        ty = Ansi(ty, 1), name = Ansi(name, 40), period = Ansi(period, 6),
                        vouword = Ansi(l.vouword, 4), bdc = Ansi(prev.bdc ?? "平", 2), bbal = (double)prev.bbal,
                        cd = (double)l.debit, cc = (double)l.credit,
                    }, transaction: tx, cancellationToken: ct));
            }
            var row = await conn.QuerySingleOrDefaultAsync<(decimal bbal, string bdc, decimal cd, decimal cc)>(new CommandDefinition(
                "SELECT ISNULL(bbalance,0), RTRIM(ISNULL(bdcdir,'平')), ISNULL(cdesum,0), ISNULL(ccresum,0) FROM finsubit WITH (NOLOCK) WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                new { name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6) },
                transaction: tx, cancellationToken: ct));
            if (row.bbal == 0 && row.bdc.Length == 0 && row.cd == 0 && row.cc == 0 && exists == 0) continue;
            var cd2 = row.cd + sign * l.debit;
            var cc2 = row.cc + sign * l.credit;
            if (un && cd2 == 0 && cc2 == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM finsubit WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                    new { name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6) },
                    transaction: tx, cancellationToken: ct));
                continue;
            }
            var eb = (row.bdc == "贷" ? -1 * row.bbal : row.bbal) + cd2 - cc2;
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE finsubit SET cdesum = @cd, ccresum = @cc, edcdir = @edc, ebalance = @eb WHERE rtrim(itemna) = @name AND itemty = @ty AND period = @period",
                new
                {
                    cd = (double)cd2, cc = (double)cc2, edc = Ansi(eb > 0 ? "借" : eb < 0 ? "贷" : "平", 2),
                    eb = (double)Math.Abs(eb), name = Ansi(name, 40), ty = Ansi(ty, 1), period = Ansi(period, 6),
                }, transaction: tx, cancellationToken: ct));
        }
    }

    /// <summary>读科目（旧系统 SEEK 语义：finsub 存在 159 组 rtrim(subcode) 重复（rscar 实测），取 TOP 1 容错；无行返回 null 跳过）。</summary>
    private static async Task<FinSubRow?> TryReadSubAsync(SqlConnection conn, SqlTransaction tx, string subcode)
    {
        return await conn.QueryFirstOrDefaultAsync<FinSubRow>(new CommandDefinition(
            """
            SELECT TOP (1) RTRIM(subcode) subcode, RTRIM(subname) subname, RTRIM(ISNULL(type,'')) type, RTRIM(ISNULL(fdc,'')) fdc,
                   ISNULL(fcash,0) fcash, ISNULL(detacode,0) detacode, RTRIM(ISNULL(unit,'')) unit,
                   ISNULL(ybdesum,0) ybdesum, ISNULL(ybcresum,0) ybcresum,
                   ISNULL(bdesum,0) bdesum, ISNULL(bcresum,0) bcresum,
                   ISNULL(cdesum,0) cdesum, ISNULL(ccresum,0) ccresum, ISNULL(ydesum,0) ydesum, ISNULL(ycresum,0) ycresum,
                   ISNULL(edesum,0) edesum, ISNULL(ecresum,0) ecresum, ISNULL(mpalsum,0) mpalsum, ISNULL(ypalsum,0) ypalsum
            FROM finsub WITH (NOLOCK) WHERE rtrim(subcode) = @subcode
            """, new { subcode = Ansi(subcode, 18) }, transaction: tx));
    }

    private static int SubWidth(int len)
    {
        int[] lens = [3, 3, 3, 2, 2, 2];
        var acc = 0;
        foreach (var l in lens)
        {
            if (len <= acc) return lens[Math.Max(0, Array.IndexOf(lens, l) - 1)];
            if (len <= acc + l) return l;
            acc += l;
        }
        return lens[^1];
    }

    private static string PrevPeriod(string period)
    {
        var y = int.Parse(period[..4]);
        var m = int.Parse(period[4..]);
        return m == 1 ? $"{y - 1}12" : $"{y}{m - 1:D2}";
    }

    /// <summary>
    /// 单凭证过帐/反过帐（fr_finopvouch myclick Post/unpost L285-340，旧 exec getfinpost_mx）。
    /// 部署库 SP 实测语义与整期客户端路径一致：finsub 逐级汇总+finaleger+finamsum+finitsub/finsubit+凭证行回写。
    /// 校验（本期/audit/post/月结）由调用方（FinOpVouchRepository）承担。返回过帐分录数。
    /// </summary>
    internal async Task<int> PostOneAsync(int vouchid, string per, string dpm, bool un, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var period = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP (1) RTRIM(period) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid", new { vid = vouchid },
            cancellationToken: ct));
        if (period is null)
            throw new AppException("FINOP_VOUCH_EMPTY", $"凭证 {vouchid} 不存在", 404);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var lines = (await conn.QueryAsync<(int id, string subcode, decimal debit, decimal credit,
                decimal amount, string unit, string opertype, string dept, string item, string empl, string client,
                string vouword, string subname)>(new CommandDefinition(
                "SELECT id, RTRIM(subcode), ISNULL(debit,0), ISNULL(credit,0), ISNULL(amount,0), RTRIM(ISNULL(unit,'')), ISNULL(rtrim(opertype),''), RTRIM(ISNULL(dept,'')), RTRIM(ISNULL(item,'')), RTRIM(ISNULL(empl,'')), RTRIM(ISNULL(client,'')), RTRIM(vouword), RTRIM(subname) FROM finvouch WITH (NOLOCK) WHERE vouchid = @vid ORDER BY recoid",
                new { vid = vouchid }, transaction: tx, cancellationToken: ct))).AsList();
            foreach (var l in lines)
            {
                var code = l.subcode.Trim();
                var j = code.Length;
                while (j >= 3 && j <= code.Length)
                {
                    var fcode = code[..j];
                    await ApplySubTotalsAsync(conn, tx, fcode, l, per, period, dpm, un: un, ct: ct);
                    j -= SubWidth(j);
                }
                await ApplyFinItsubAsync(conn, tx, l, per, period, un: un, ct);
                await ApplyFinSubitAsync(conn, tx, l, per, period, un: un, ct);
                if (!un)
                {
                    var finRow = await TryReadSubAsync(conn, tx, code);
                    var fin = finRow ?? new FinSubRow();
                    var deCre = fin.Edesum - fin.Ecresum;
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE finvouch SET dcdir = @dcdir, balance = @balance, post = 1, poster = @per WHERE id = @id",
                        new
                        {
                            dcdir = Ansi(deCre > 0 ? "借" : deCre < 0 ? "贷" : "平", 2),
                            balance = (double)Math.Abs(deCre), per = Ansi(per, 10), l.id,
                        }, transaction: tx, cancellationToken: ct));
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE finvouch SET dcdir = '', balance = 0, post = 0, poster = '' WHERE id = @id",
                        new { l.id }, transaction: tx, cancellationToken: ct));
                }
            }
            await tx.CommitAsync(ct);
            return lines.Count;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}
