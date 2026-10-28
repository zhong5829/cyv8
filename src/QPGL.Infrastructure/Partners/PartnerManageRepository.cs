using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Partners;

public interface IPartnerManageRepository
{
    /// <summary>客户资料新增/修改（fr_gu row5 契约）。</summary>
    Task<SavePartnerResult> SaveAsync(SavePartnerCommand command, string dept, CancellationToken ct = default);

    /// <summary>客户资料删除（fr_gu p_.row111：DELETE WHERE id，无前置检查照抄）。</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// 客户资料管理仓储（M5，A5）。
/// 旧实现出处：fr_gu row5 ——
///   INSERT 列集 = gno,linkman,job,gname,tel,mobile,fax,zip,adr,note,note1,type1,type2,checkno,bank,name1,yhl,ywy,
///                 cwfzr,qq,web,email,sendcom,formula_name,ch,dp,cx,color,clcp,guno,dpm,pno
///   （一期取 UI 核心列；未列出的列走 DEFAULT('')，legacy 回放脚本用同列集保证 diff 一致）
///   校验：gno 唯一（新增或改号时）；name1 必填；name1≠gname 时须存在 gname=name1 的主客户；
///   dpm = PASSTH 自动补列（tbxzb：tbgu INSERT 补 dpm=p_dpm）。
/// </summary>
public sealed class PartnerManageRepository(SqlConnectionFactory factory) : IPartnerManageRepository
{
    private const string InsertColumns =
        "gno, gname, name1, linkman, job, tel, mobile, fax, zip, adr, note, type1, type2, checkno, bank, yhl, ywy, dpm";

    public async Task<SavePartnerResult> SaveAsync(SavePartnerCommand command, string dept, CancellationToken ct = default)
    {
        command.Validate();
        var gno = command.Gno.Trim();
        var gname = command.Gname.Trim();
        var name1 = command.Name1.Trim();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // ① gno 唯一（新增或改号时）
            var existingId = await conn.ExecuteScalarAsync<int?>(
                new CommandDefinition("SELECT id FROM tbgu WITH (NOLOCK) WHERE gno = @gno",
                    new { gno = new DbString { Value = gno, IsAnsi = true, Length = 10 } },
                    transaction: tx, cancellationToken: ct));
            if (existingId is not null && existingId != command.Id)
                throw new AppException("GNO_EXISTS", $"该客户号 {gno} 已经存在，请重新输入！", 409);

            // ③ name1 通用单位语义：name1≠gname 时必须存在 gname=name1 的主客户
            if (!string.Equals(name1, gname, StringComparison.Ordinal))
            {
                // 旧语义（fr_gu L46/L56）：查 gname=name1 且 gname<>当前录入 gname 的主客户
                // （修改时自身行仍是旧 gname，只要 name1=旧名即通过；新名不参与）
                var mainExists = await conn.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT COUNT(1) FROM tbgu WITH (NOLOCK) WHERE gname = @name1 AND gname <> @gname",
                        new
                        {
                            name1 = new DbString { Value = name1, IsAnsi = false, Length = 60 },
                            gname = new DbString { Value = gname, IsAnsi = false, Length = 60 },
                        },
                        transaction: tx, cancellationToken: ct));
                if (mainExists == 0)
                    throw new AppException("NAME1_NO_MAIN", "通用单位无对应客户名，请重新输入！", 422);
            }

            int id;
            var isCreate = command.Id is null;
            if (isCreate)
            {
                id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    $"""
                    INSERT INTO tbgu ({InsertColumns})
                    VALUES (@gno, @gname, @name1, @linkman, @job, @tel, @mobile, @fax, @zip, @adr, @note,
                            @type1, @type2, @checkno, @bank, @yhl, @ywy, @dpm);
                    SELECT CAST(SCOPE_IDENTITY() AS int);
                    """,
                    new
                    {
                        gno = Ansi(gno, 10), gname = Ansi(gname, 60), name1 = Ansi(name1, 60),
                        linkman = Ansi(command.Linkman, 8), job = Ansi(command.Job, 8),
                        tel = Ansi(command.Tel, 50), mobile = Ansi(command.Mobile, 15),
                        fax = Ansi(command.Fax, 16), zip = Ansi(command.Zip, 6), adr = Ansi(command.Adr, 40),
                        note = Ansi(command.Note, 200), type1 = Ansi("", 18),
                        type2 = Ansi(command.Type2, 10), checkno = Ansi(command.Checkno, 20),
                        bank = Ansi(command.Bank, 40), yhl = command.Yhl, ywy = Ansi(command.Ywy, 10),
                        dpm = Ansi(dept, 40),
                    },
                    transaction: tx, cancellationToken: ct));
            }
            else
            {
                id = command.Id!.Value;
                var rows = await conn.ExecuteAsync(new CommandDefinition(
                    $"""
                    UPDATE tbgu SET
                        gno = @gno, gname = @gname, name1 = @name1, linkman = @linkman, job = @job,
                        tel = @tel, mobile = @mobile, fax = @fax, zip = @zip, adr = @adr, note = @note,
                        type1 = @type1, type2 = @type2, checkno = @checkno, bank = @bank, yhl = @yhl, ywy = @ywy
                    WHERE id = @id
                    """,
                    new
                    {
                        id, gno = Ansi(gno, 10), gname = Ansi(gname, 60), name1 = Ansi(name1, 60),
                        linkman = Ansi(command.Linkman, 8), job = Ansi(command.Job, 8),
                        tel = Ansi(command.Tel, 50), mobile = Ansi(command.Mobile, 15),
                        fax = Ansi(command.Fax, 16), zip = Ansi(command.Zip, 6), adr = Ansi(command.Adr, 40),
                        note = Ansi(command.Note, 200), type1 = Ansi("", 18),
                        type2 = Ansi(command.Type2, 10), checkno = Ansi(command.Checkno, 20),
                        bank = Ansi(command.Bank, 40), yhl = command.Yhl, ywy = Ansi(command.Ywy, 10),
                    },
                    transaction: tx, cancellationToken: ct));
                if (rows == 0)
                    throw new AppException("PARTNER_NOT_FOUND", $"客户 {gno} 不存在", 404);
            }

            // ④ type2 新值自动补 tbsysset（type='客户类型'）
            if (!string.IsNullOrWhiteSpace(command.Type2))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    IF NOT EXISTS (SELECT 1 FROM tbsysset WITH (NOLOCK) WHERE name = @name AND type = N'客户类型')
                        INSERT INTO tbsysset (name, type, valuen, valuec) VALUES (@name, N'客户类型', 0, '')
                    """,
                    new { name = new DbString { Value = command.Type2.Trim(), IsAnsi = false, Length = 50 } },
                    transaction: tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return new SavePartnerResult { Id = id, IsCreate = isCreate };
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
                "DELETE tbgu WHERE id = @id", new { id },
                transaction: tx, cancellationToken: ct));
            if (rows == 0)
                throw new AppException("PARTNER_NOT_FOUND", $"客户记录 {id} 不存在", 404);
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
