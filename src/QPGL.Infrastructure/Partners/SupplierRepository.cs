using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Partners;

public interface ISupplierRepository
{
    /// <summary>供应商管理列表（固定 tbgugys，与新增/修改/删除同表，避免跨表 Id 误改误删）。</summary>
    Task<PagedResult<SupplierRow>> SearchAsync(PagedRequest request, CancellationToken ct = default);

    /// <summary>业务页供应商联想（合并 tbgu + tbgugys，tbgu 优先；存量客户型供应商与供应商档案都能命中）。</summary>
    Task<PagedResult<SupplierRow>> SearchMergedAsync(PagedRequest request, CancellationToken ct = default);

    /// <summary>按名称精确查找供应商（合并 tbgu + tbgugys，Trim 首尾空格、忽略大小写，不做中缀宽松匹配）。</summary>
    Task<SupplierRow?> FindExactAsync(string name, CancellationToken ct = default);

    /// <summary>供应商新增/修改（结构契约 + fr_gu 校验模式）。</summary>
    Task<SaveSupplierResult> SaveAsync(SaveSupplierCommand command, CancellationToken ct = default);

    /// <summary>供应商删除。</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// 供应商仓储（M5，A6）。
/// 供应商档案表=tbgugys（gyno/gyname/linkman/tel/fax/zip/cno/adr/code/code1/zq 账期/email/note/note1/bank/checkno/sl 税率/lb 类别）；
/// tbgu 为历史「供应商使用客户表」数据（gno/gname，无 zq/sl/lb 列）。
/// 2026-11-07 口径（用户确认 A+D）：管理页列表/新增/修改/删除固定 tbgugys；
/// 业务页联想与存在性校验合并查询 tbgu + tbgugys，保证存量客户型供应商与新建供应商都能命中。
/// </summary>
public sealed class SupplierRepository(SqlConnectionFactory factory) : ISupplierRepository
{
    public async Task<PagedResult<SupplierRow>> SearchAsync(PagedRequest request, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        // 管理页固定 tbgugys（与 SaveAsync/DeleteAsync 同表）：不再跟随 MAKER 切到 tbgu，
        // 否则列表行携带的是 tbgu.Id，编辑/删除会误作用到同 id 的 tbgugys 记录。
        var where = "1=1";
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            // 旧 cmm_class FRDM 机制：拼音码 code / 数字码 code1 / 名称 / 编号前缀匹配
            where += " AND (gyname LIKE N'%' + @kw + '%' OR gyno LIKE @kw + '%' OR code LIKE @kw + '%' OR code1 LIKE @kw + '%')";
            p.Add("kw", request.Keyword.Trim());
        }

        var sql = $"""
            SELECT 'tbgugys' AS source, id, RTRIM(gyno) gyno, RTRIM(gyname) gyname, RTRIM(linkman) linkman,
                   RTRIM(tel) tel, RTRIM(fax) fax, RTRIM(adr) adr, zq, RTRIM(email) email, RTRIM(note) note,
                   RTRIM(bank) bank, sl, RTRIM(lb) lb
            FROM tbgugys WITH (NOLOCK)
            WHERE {where}
            ORDER BY gyno ASC, id ASC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            SELECT COUNT(1) FROM tbgugys WITH (NOLOCK) WHERE {where};
            """;
        p.Add("skip", (request.Page - 1) * request.PageSize);
        p.Add("take", request.PageSize);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = (await multi.ReadAsync<SupplierRow>()).AsList();
        var total = await multi.ReadFirstAsync<int>();
        return new PagedResult<SupplierRow> { Items = rows, Total = total, Page = request.Page, PageSize = request.PageSize };
    }

    public async Task<PagedResult<SupplierRow>> SearchMergedAsync(PagedRequest request, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var p = new DynamicParameters();
        var guWhere = "1=1";
        var gysWhere = "1=1";
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            p.Add("kw", request.Keyword.Trim());
            guWhere += " AND (gname LIKE N'%' + @kw + '%' OR gno LIKE @kw + '%' OR code LIKE @kw + '%' OR code1 LIKE @kw + '%')";
            gysWhere += " AND (gyname LIKE N'%' + @kw + '%' OR gyno LIKE @kw + '%' OR code LIKE @kw + '%' OR code1 LIKE @kw + '%')";
        }

        // 默认取舍（待用户确认重名策略）：两表并集不去重，tbgu（存量客户型供应商）排在前面，表内按编号升序。
        var union = $"""
            SELECT 'tbgu' AS source, id, RTRIM(gno) gyno, RTRIM(gname) gyname, RTRIM(linkman) linkman,
                   RTRIM(tel) tel, RTRIM(fax) fax, RTRIM(adr) adr, 0 zq, RTRIM(email) email,
                   RTRIM(note) note, RTRIM(bank) bank, 0 sl, CAST('' AS varchar(4)) lb, 0 AS src_order
            FROM tbgu WITH (NOLOCK) WHERE {guWhere}
            UNION ALL
            SELECT 'tbgugys' AS source, id, RTRIM(gyno), RTRIM(gyname), RTRIM(linkman), RTRIM(tel),
                   RTRIM(fax), RTRIM(adr), zq, RTRIM(email), RTRIM(note), RTRIM(bank), sl, RTRIM(lb), 1 AS src_order
            FROM tbgugys WITH (NOLOCK) WHERE {gysWhere}
            """;

        var sql = $"""
            SELECT source, id, gyno, gyname, linkman, tel, fax, adr, zq, email, note, bank, sl, lb
            FROM ({union}) t
            ORDER BY src_order ASC, gyno ASC, id ASC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            SELECT COUNT(1) FROM ({union}) c;
            """;
        p.Add("skip", (request.Page - 1) * request.PageSize);
        p.Add("take", request.PageSize);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = (await multi.ReadAsync<SupplierRow>()).AsList();
        var total = await multi.ReadFirstAsync<int>();
        return new PagedResult<SupplierRow> { Items = rows, Total = total, Page = request.Page, PageSize = request.PageSize };
    }

    public async Task<SupplierRow?> FindExactAsync(string name, CancellationToken ct = default)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return null;

        await using var conn = await factory.OpenAsync(ct);

        // 名称整串相等（RTRIM 去尾空格；列排序规则 Chinese_PRC_CI_AS 大小写不敏感），
        // 不用 LIKE，避免被 200 条上限挤掉；tbgu 优先。
        const string sql = """
            SELECT TOP 1 source, id, gyno, gyname, linkman, tel, fax, adr, zq, email, note, bank, sl, lb
            FROM (
                SELECT 'tbgu' AS source, id, RTRIM(gno) gyno, RTRIM(gname) gyname, RTRIM(linkman) linkman,
                       RTRIM(tel) tel, RTRIM(fax) fax, RTRIM(adr) adr, 0 zq, RTRIM(email) email,
                       RTRIM(note) note, RTRIM(bank) bank, 0 sl, CAST('' AS varchar(4)) lb, 0 AS src_order
                FROM tbgu WITH (NOLOCK) WHERE RTRIM(gname) = @name
                UNION ALL
                SELECT 'tbgugys' AS source, id, RTRIM(gyno), RTRIM(gyname), RTRIM(linkman), RTRIM(tel),
                       RTRIM(fax), RTRIM(adr), zq, RTRIM(email), RTRIM(note), RTRIM(bank), sl, RTRIM(lb), 1 AS src_order
                FROM tbgugys WITH (NOLOCK) WHERE RTRIM(gyname) = @name
            ) t
            ORDER BY src_order ASC, gyno ASC, id ASC;
            """;
        return await conn.QueryFirstOrDefaultAsync<SupplierRow>(
            new CommandDefinition(sql, new { name = n }, cancellationToken: ct));
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
