using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Orders;

// =====================================================================================
// F.3 订货公式（fr_orderexp 契约：开发文档/契约摘录/F组小表单契约.md §1）
// 写路径：UPDATE tborexppara.nvalue / UPDATE tborderexp.expna / INSERT+UPDATE+DELETE tborderexp。
// 公式 id 取号：INSERT 后 SCOPE_IDENTITY（旧 SELECT TOP 1 expid ORDER BY expid DESC，IDENTITY 语义等价）。
// =====================================================================================
public interface IOrderExpRepository
{
    Task<IReadOnlyList<OrderExpRow>> GetFormulasAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OrderExpParaRow>> GetParamsAsync(CancellationToken ct = default);
    Task UpdateParamValueAsync(string paraId, decimal nValue, CancellationToken ct = default);
    Task RenameFormulaAsync(int expId, string expNa, CancellationToken ct = default);
    Task<int> AddFormulaAsync(string cExpress, string expNa, CancellationToken ct = default);
    Task UpdateFormulaAsync(int expId, string cExpress, string expNa, CancellationToken ct = default);
    Task DeleteFormulaAsync(int expId, CancellationToken ct = default);
}

public sealed class OrderExpRow
{
    public int Expid { get; set; }
    public string Expna { get; set; } = "";
    public string Cexpress { get; set; } = "";
    public bool Basexp { get; set; }
}

public sealed class OrderExpParaRow
{
    public string Paraid { get; set; } = "";
    public string Parana { get; set; } = "";
    public decimal Nvalue { get; set; }
    public bool Baspara { get; set; }
}

public sealed class OrderExpRepository(SqlConnectionFactory factory) : IOrderExpRepository
{
    public async Task<IReadOnlyList<OrderExpRow>> GetFormulasAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OrderExpRow>(new CommandDefinition(
            "SELECT expid, RTRIM(expna) expna, RTRIM(cexpress) cexpress, basexp FROM tborderexp WITH (NOLOCK) ORDER BY expid",
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<OrderExpParaRow>> GetParamsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OrderExpParaRow>(new CommandDefinition(
            "SELECT RTRIM(paraid) paraid, RTRIM(parana) parana, nvalue, baspara FROM tborexppara WITH (NOLOCK) ORDER BY paraid",
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>参数值修改（fr_orderexp/__.row18:L10）。</summary>
    public async Task UpdateParamValueAsync(string paraId, decimal nValue, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tborexppara SET nvalue = @nvalue WHERE paraid = @paraid",
            new { nvalue = nValue, paraid = new DbString { Value = paraId, IsAnsi = true, Length = 3 } },
            cancellationToken: ct));
    }

    /// <summary>公式改名（fr_orderexp/P_.row12:L8）。</summary>
    public async Task RenameFormulaAsync(int expId, string expNa, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tborderexp SET expna = @expna WHERE expid = @expid",
            new { expna = Ansi(expNa, 20), expid = expId }, cancellationToken: ct));
    }

    /// <summary>新增公式（fr_orderexp/_5.row31:L12）。</summary>
    public async Task<int> AddFormulaAsync(string cExpress, string expNa, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "INSERT INTO tborderexp (cexpress, expna) OUTPUT INSERTED.expid VALUES (@cexpress, @expna)",
            new { cexpress = Ansi(cExpress, 200), expna = Ansi(expNa, 20) }, cancellationToken: ct));
    }

    /// <summary>修改公式（fr_orderexp/_5.row31:L23）。</summary>
    public async Task UpdateFormulaAsync(int expId, string cExpress, string expNa, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tborderexp SET cexpress = @cexpress, expna = @expna WHERE expid = @expid",
            new { cexpress = Ansi(cExpress, 200), expna = Ansi(expNa, 20), expid = expId }, cancellationToken: ct));
    }

    /// <summary>删除公式（fr_orderexp/__.row21:L21；basexp 系统公式由调用方/仓储校验禁止删除）。</summary>
    public async Task DeleteFormulaAsync(int expId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var basexp = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT basexp FROM tborderexp WITH (NOLOCK) WHERE expid = @expid", new { expid = expId }, cancellationToken: ct));
        if (basexp) throw new AppException("BASEXP_LOCKED", "系统公式不允许删除", 409);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tborderexp WHERE expid = @expid", new { expid = expId }, cancellationToken: ct));
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}

// =====================================================================================
// F.5 供应商价格（fr_gysjg 契约 §3；实际表 = tbgys）
// 写路径：DELETE by id / DELETE by gyname / INSERT（返回自增 id）/ UPDATE（updadate=GETDATE()）。
// =====================================================================================
public interface ISupplierPriceRepository
{
    Task<IReadOnlyList<SupplierPriceRow>> SearchAsync(string? nnoPrefix, string? gyname, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task DeleteByGynameAsync(string gyname, CancellationToken ct = default);
    Task<int> InsertAsync(SupplierPriceInput input, CancellationToken ct = default);
    Task UpdateAsync(int id, SupplierPriceInput input, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetBzOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(string Gyno, string Gyname)>> GetSupplierOptionsAsync(CancellationToken ct = default);
    Task<string?> GetPartNameAsync(string nno, CancellationToken ct = default);
}

public sealed class SupplierPriceRow
{
    public int Id { get; set; }
    public string Nno { get; set; } = "";
    public string Oenno { get; set; } = "";
    /// <summary>名称（ISNULL(b.na1, a.na1)，tbprnoty 聚合派生）。</summary>
    public string Na1 { get; set; } = "";
    public decimal Zq { get; set; }
    public decimal Iprj { get; set; }
    public string Gyno { get; set; } = "";
    public string Gyname { get; set; } = "";
    public DateTime Updadate { get; set; }
    public string Ty { get; set; } = "";
    public string Bzlx { get; set; } = "";
    public string Cp { get; set; } = "";
}

public sealed class SupplierPriceInput
{
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Oenno { get; set; } = "";
    public decimal Zq { get; set; }
    public decimal Iprj { get; set; }
    public string Gyno { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Bzlx { get; set; } = "";
}

public sealed class SupplierPriceRepository(SqlConnectionFactory factory) : ISupplierPriceRepository
{
    public async Task<IReadOnlyList<SupplierPriceRow>> SearchAsync(string? nnoPrefix, string? gyname, CancellationToken ct = default)
    {
        // 旧查询（fr_gysjg/__.row4:L15）：tbgys a, (tbprnoty group by nno) b，a.nno*=b.nno 旧式外连接 → LEFT JOIN
        var where = "1=1";
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(nnoPrefix))
        {
            where += " AND a.nno LIKE @nno";
            args.Add("nno", new DbString { Value = nnoPrefix.Trim() + "%", IsAnsi = true, Length = 19 });
        }
        if (!string.IsNullOrWhiteSpace(gyname))
        {
            where += " AND a.gyname = @gyname";
            args.Add("gyname", new DbString { Value = gyname.Trim(), IsAnsi = true, Length = 40 });
        }
        var sql = $"""
            SELECT a.id, RTRIM(a.nno) nno, RTRIM(a.oenno) oenno, ISNULL(b.na1, a.na1) na1,
                   a.zq, a.iprj, RTRIM(a.gyno) gyno, RTRIM(a.gyname) gyname,
                   ISNULL(a.updadate, '1900-01-01') updadate, ISNULL(b.ty, '') ty, RTRIM(a.bzlx) bzlx, RTRIM(a.cp) cp
            FROM tbgys a WITH (NOLOCK)
            LEFT JOIN (SELECT nno, MAX(na1) na1, MAX(ty) ty FROM tbprnoty WITH (NOLOCK) GROUP BY nno) b
                   ON a.nno = b.nno
            WHERE {where}
            ORDER BY a.nno
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<SupplierPriceRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tbgys WHERE id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>按供应商全删（fr_gysjg/__.row30:L11；跨厂牌不区分 cp——旧系统原样）。</summary>
    public async Task DeleteByGynameAsync(string gyname, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tbgys WHERE gyname = @gyname",
            new { gyname = new DbString { Value = gyname.Trim(), IsAnsi = true, Length = 40 } }, cancellationToken: ct));
    }

    /// <summary>新增报价（fr_gysjg/_5.row46:L12；cp=P_CP 由调用方显式传值——厂牌会话未接入）。</summary>
    public async Task<int> InsertAsync(SupplierPriceInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Gyname)) throw new AppException("GYNAME_REQUIRED", "供应商名称不能为空", 422);
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO tbgys (cp, nno, oenno, zq, iprj, gyno, gyname, updadate, bzlx)
            OUTPUT INSERTED.id
            VALUES (@cp, @nno, @oenno, @zq, @iprj, @gyno, @gyname, GETDATE(), @bzlx)
            """,
            new
            {
                cp = Ansi(input.Cp, 10), nno = Ansi(input.Nno, 18), oenno = Ansi(input.Oenno, 18),
                zq = input.Zq, iprj = input.Iprj, gyno = Ansi(input.Gyno, 10), gyname = Ansi(input.Gyname, 40),
                bzlx = Ansi(input.Bzlx, 10),
            }, cancellationToken: ct));
    }

    /// <summary>修改报价（fr_gysjg/_5.row46:L14）。</summary>
    public async Task UpdateAsync(int id, SupplierPriceInput input, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tbgys SET nno = @nno, oenno = @oenno, zq = @zq, iprj = @iprj,
                   gyno = @gyno, gyname = @gyname, updadate = GETDATE(), bzlx = @bzlx
            WHERE id = @id
            """,
            new
            {
                id, nno = Ansi(input.Nno, 18), oenno = Ansi(input.Oenno, 18), zq = input.Zq, iprj = input.Iprj,
                gyno = Ansi(input.Gyno, 10), gyname = Ansi(input.Gyname, 40), bzlx = Ansi(input.Bzlx, 10),
            }, cancellationToken: ct));
    }

    /// <summary>币种/备注字典（fr_gysjg/__.row31:L4）。</summary>
    public async Task<IReadOnlyList<string>> GetBzOptionsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'BZ' ORDER BY id", cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>供应商代码下拉（fr_gysjg/__.row31:L3，tbgugys）。</summary>
    public async Task<IReadOnlyList<(string Gyno, string Gyname)>> GetSupplierOptionsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string gyno, string gyname)>(new CommandDefinition(
            "SELECT RTRIM(gyno) gyno, RTRIM(gyname) gyname FROM tbgugys WITH (NOLOCK) ORDER BY gyno", cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>输入件号带出名称（fr_gysjg/__.row33:L4）。</summary>
    public async Task<string?> GetPartNameAsync(string nno, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP (1) RTRIM(na1) FROM tbprnoty WITH (NOLOCK) WHERE nno = @nno",
            new { nno = new DbString { Value = nno.Trim(), IsAnsi = true, Length = 18 } }, cancellationToken: ct));
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}

// =====================================================================================
// F.6 库存上网（fr_kc 契约 §6；表 = tbkc）
// 写路径：DELETE / 从 tbisto 聚合生成（存在 UPDATE 价量 / 不存在 INSERT）/ 全表 mark 置位 / 行编辑回写 4 列。
// 已知偏差备案：行级 mark 切换旧系统疑未落库（fr_kc/__.row4 只有本地游标）——新系统持久化；
// 生成时存在性按 tbkc.nno 全表查（旧用本地游标 SEEK，游标为空时会重复 INSERT——按旧系统意图取 DB 查重）。
// =====================================================================================
public interface IWebStockRepository
{
    Task<IReadOnlyList<WebStockRow>> SearchAsync(string? nnoPrefix, string? na1Contains, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    /// <summary>从 tbisto 聚合生成/刷新（fr_kc/__.row11：nno 汇总 kcamount + max(id) 明细行；nnp=nno、dc='正厂'、mark=量>0）。</summary>
    Task<(int Inserted, int Updated)> GenerateAsync(CancellationToken ct = default);
    /// <summary>全表 mark 置位（fr_kc/__.row12/row13：无 WHERE 整表更新——旧系统原样）。</summary>
    Task SetMarkAllAsync(bool mark, CancellationToken ct = default);
    Task UpdateRowAsync(int id, string nno, string ty, string dc, string nnp, CancellationToken ct = default);
    Task ToggleMarkAsync(int id, bool mark, CancellationToken ct = default);
}

public sealed class WebStockRow
{
    public int Id { get; set; }
    public bool Mark { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Dc { get; set; } = "";
    public decimal Oprc { get; set; }
    public decimal Kcamount { get; set; }
    public string Nnp { get; set; } = "";
}

public sealed class WebStockRepository(SqlConnectionFactory factory) : IWebStockRepository
{
    public async Task<IReadOnlyList<WebStockRow>> SearchAsync(string? nnoPrefix, string? na1Contains, CancellationToken ct = default)
    {
        var where = "1=1";
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(nnoPrefix))
        {
            where += " AND nno LIKE @nno";
            args.Add("nno", new DbString { Value = nnoPrefix.Trim() + "%", IsAnsi = true, Length = 19 });
        }
        if (!string.IsNullOrWhiteSpace(na1Contains))
        {
            where += " AND na1 LIKE @na1";
            args.Add("na1", new DbString { Value = "%" + na1Contains.Trim() + "%", IsAnsi = true, Length = 42 });
        }
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<WebStockRow>(new CommandDefinition(
            $"SELECT id, mark, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(dc) dc, oprc, kcamount, RTRIM(nnp) nnp " +
            $"FROM tbkc WITH (NOLOCK) WHERE {where} ORDER BY nno",
            args, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tbkc WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<(int Inserted, int Updated)> GenerateAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 旧聚合（fr_kc/__.row11:L3）：select nno, max(id) id, sum(kcamount) kcamount from tbisto group by nno（无 cp/dpm 过滤——原样）
            var agg = (await conn.QueryAsync<(string nno, int id, decimal kc)>(new CommandDefinition(
                "SELECT RTRIM(nno) nno, MAX(id) id, SUM(kcamount) kc FROM tbisto WITH (NOLOCK) GROUP BY RTRIM(nno)",
                transaction: tx, cancellationToken: ct))).AsList();
            int inserted = 0, updated = 0;
            foreach (var (nno, id, kc) in agg)
            {
                var detail = await conn.QueryFirstOrDefaultAsync<(string na1, string ty, decimal oprc)>(new CommandDefinition(
                    "SELECT RTRIM(na1) na1, RTRIM(ty) ty, oprc FROM tbisto WITH (NOLOCK) WHERE id = @id",
                    new { id }, transaction: tx, cancellationToken: ct));
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbkc WITH (UPDLOCK) WHERE nno = @nno",
                    new { nno = Ansi(nno, 18) }, transaction: tx, cancellationToken: ct));
                if (exists > 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbkc SET oprc = @oprc, kcamount = @kc WHERE nno = @nno",
                        new { oprc = detail.oprc, kc, nno = Ansi(nno, 18) }, transaction: tx, cancellationToken: ct));
                    updated++;
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO tbkc (mark, nno, na1, ty, dc, nnp, kcamount, oprc) VALUES (@mark, @nno, @na1, @ty, N'正厂', @nno, @kc, @oprc)",
                        new { mark = kc > 0, nno = Ansi(nno, 18), na1 = Ansi(detail.na1, 40), ty = Ansi(detail.ty, 14), kc, oprc = detail.oprc },
                        transaction: tx, cancellationToken: ct));
                    inserted++;
                }
            }
            await tx.CommitAsync(ct);
            return (inserted, updated);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task SetMarkAllAsync(bool mark, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbkc SET mark = @mark", new { mark }, cancellationToken: ct));
    }

    /// <summary>行编辑保存（fr_kc/_7.row43:L4：仅回写 nno,ty,dc,nnp 四列）。</summary>
    public async Task UpdateRowAsync(int id, string nno, string ty, string dc, string nnp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbkc SET nno = @nno, ty = @ty, dc = @dc, nnp = @nnp WHERE id = @id",
            new { id, nno = Ansi(nno, 18), ty = Ansi(ty, 14), dc = Ansi(dc, 8), nnp = Ansi(nnp, 16) },
            cancellationToken: ct));
    }

    public async Task ToggleMarkAsync(int id, bool mark, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbkc SET mark = @mark WHERE id = @id", new { id, mark }, cancellationToken: ct));
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
