using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Partners;

public interface ISupplierRepository
{
    /// <summary>供应商分页查询（tbgugys）。</summary>
    Task<PagedResult<SupplierRow>> SearchAsync(PagedRequest request, CancellationToken ct = default);

    /// <summary>供应商新增/修改（结构契约 + fr_gu 校验模式）。</summary>
    Task<SaveSupplierResult> SaveAsync(SaveSupplierCommand command, CancellationToken ct = default);

    /// <summary>供应商删除。</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// 供应商管理仓储（M5，A6）。供应商表=tbgugys（gyno/gyname/linkman/tel/fax/zip/cno/adr/code/code1/
/// zq 账期/email/note/note1/bank/checkno/sl 税率/lb 类别）；tbgu 全为 KH 客户（type1 空，生产实证）。
/// 校验模式照 fr_gu：gyno 唯一（新增或改号时）、gyname 必填。
/// </summary>
public sealed class SupplierRepository(SqlConnectionFactory factory) : ISupplierRepository
{
    private const string Columns =
        "id, RTRIM(gyno) gyno, RTRIM(gyname) gyname, RTRIM(linkman) linkman, RTRIM(tel) tel, RTRIM(fax) fax, " +
        "RTRIM(adr) adr, zq, RTRIM(email) email, RTRIM(note) note, RTRIM(bank) bank, sl, RTRIM(lb) lb";

    public async Task<PagedResult<SupplierRow>> SearchAsync(PagedRequest request, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        // 旧 cmm_class row326 Init + pg_proc L3276-3277：tbsysset「MAKER」(valuel) = P_GYSUSEKH，
        // 真则供应商联想源切 tbgu（gname/gno/code/code1），否则 tbgugys（gyname/gyno）——运行时读配置
        var useKh = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT ISNULL(CAST(valuel AS bit), 0) FROM tbsysset WITH (NOLOCK) WHERE RTRIM(name) = 'MAKER' AND type = 'system'",
            cancellationToken: ct));
        var table = useKh ? "tbgu" : "tbgugys";
        var nameCol = useKh ? "gname" : "gyname";
        var noCol = useKh ? "gno" : "gyno";

        var where = "1=1";
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            // 旧 cmm_class FRDM 机制：拼音码 code / 数字码 code1 / 名称 / 编号前缀匹配
            where += $" AND ({nameCol} LIKE N'%' + @kw + '%' OR {noCol} LIKE @kw + '%' OR code LIKE @kw + '%' OR code1 LIKE @kw + '%')";
            p.Add("kw", request.Keyword.Trim());
        }
        var orderBy = $"{noCol} ASC, id ASC";

        var sql = $"""
            SELECT id, RTRIM({noCol}) gyno, RTRIM({nameCol}) gyname, RTRIM(linkman) linkman, RTRIM(tel) tel,
                   RTRIM(fax) fax, RTRIM(adr) adr, 0 zq, RTRIM(email) email, RTRIM(note) note,
                   RTRIM(bank) bank, 0 sl, '' lb
            FROM {table} WITH (NOLOCK)
            WHERE {where}
            ORDER BY {orderBy}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            SELECT COUNT(1) FROM {table} WITH (NOLOCK) WHERE {where};
            """;
        p.Add("skip", (request.Page - 1) * request.PageSize);
        p.Add("take", request.PageSize);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = (await multi.ReadAsync<SupplierRow>()).AsList();
        var total = await multi.ReadFirstAsync<int>();
        return new PagedResult<SupplierRow> { Items = rows, Total = total, Page = request.Page, PageSize = request.PageSize };
    }

    public async Task<SaveSupplierResult> SaveAsync(SaveSupplierCommand command, CancellationToken ct = default)
    {
        command.Validate();
        var gyno = command.Gyno.Trim();
        var gyname = command.Gyname.Trim();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var existingId = await conn.ExecuteScalarAsync<int?>(
                new CommandDefinition("SELECT id FROM tbgugys WITH (NOLOCK) WHERE gyno = @gyno",
                    new { gyno = new DbString { Value = gyno, IsAnsi = true, Length = 10 } },
                    transaction: tx, cancellationToken: ct));
            if (existingId is not null && existingId != command.Id)
                throw new AppException("GYNO_EXISTS", $"该供应商编号 {gyno} 已经存在，请重新输入！", 409);

            int id;
            var isCreate = command.Id is null;
            if (isCreate)
            {
                id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    INSERT INTO tbgugys (gyno, gyname, linkman, tel, fax, zip, adr, zq, email, note, bank, checkno, sl, lb)
                    VALUES (@gyno, @gyname, @linkman, @tel, @fax, @zip, @adr, @zq, @email, @note, @bank, @checkno, @sl, @lb);
                    SELECT CAST(SCOPE_IDENTITY() AS int);
                    """,
                    new
                    {
                        gyno = Ansi(gyno, 10), gyname = Ansi(gyname, 40), linkman = Ansi(command.Linkman, 8),
                        tel = Ansi(command.Tel, 30), fax = Ansi(command.Fax, 14), zip = Ansi(command.Zip, 6),
                        adr = Ansi(command.Adr, 40), zq = command.Zq, email = Ansi(command.Email, 40),
                        note = Ansi(command.Note, 40), bank = Ansi(command.Bank, 40),
                        checkno = Ansi(command.Checkno, 20), sl = command.Sl, lb = Ansi(command.Lb, 4),
                    },
                    transaction: tx, cancellationToken: ct));
            }
            else
            {
                id = command.Id!.Value;
                var rows = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE tbgugys SET
                        gyno = @gyno, gyname = @gyname, linkman = @linkman, tel = @tel, fax = @fax, zip = @zip,
                        adr = @adr, zq = @zq, email = @email, note = @note, bank = @bank, checkno = @checkno, sl = @sl, lb = @lb
                    WHERE id = @id
                    """,
                    new
                    {
                        id, gyno = Ansi(gyno, 10), gyname = Ansi(gyname, 40), linkman = Ansi(command.Linkman, 8),
                        tel = Ansi(command.Tel, 30), fax = Ansi(command.Fax, 14), zip = Ansi(command.Zip, 6),
                        adr = Ansi(command.Adr, 40), zq = command.Zq, email = Ansi(command.Email, 40),
                        note = Ansi(command.Note, 40), bank = Ansi(command.Bank, 40),
                        checkno = Ansi(command.Checkno, 20), sl = command.Sl, lb = Ansi(command.Lb, 4),
                    },
                    transaction: tx, cancellationToken: ct));
                if (rows == 0)
                    throw new AppException("SUPPLIER_NOT_FOUND", $"供应商 {gyno} 不存在", 404);
            }

            await tx.CommitAsync(ct);
            return new SaveSupplierResult { Id = id, IsCreate = isCreate };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbgugys WHERE id = @id", new { id },
                transaction: tx, cancellationToken: ct));
            if (rows == 0)
                throw new AppException("SUPPLIER_NOT_FOUND", $"供应商记录 {id} 不存在", 404);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>char 列 GBK 字节语义：Length 按字节宽截断（1 汉字=2 字节，超宽不炸 SQL 截断错误）。</summary>
    private static DbString Ansi(string value, int length) =>
        new() { Value = AnsiBytes.Truncate(value ?? "", length), IsAnsi = true, Length = length };
}
