using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.DocNo;

public interface IDocNoPrefixRepository
{
    /// <summary>单号前缀清单（tbdjgs，按部门）。</summary>
    Task<IReadOnlyList<DocNoPrefixRow>> GetListAsync(string dept, CancellationToken ct = default);

    /// <summary>修改前缀（fr_billprefx row5：SK/FK 前缀唯一校验后 UPDATE djjx）。</summary>
    Task UpdateAsync(UpdateDocNoPrefixCommand command, string dept, CancellationToken ct = default);
}

/// <summary>
/// 单号前缀设置仓储（M5，A9）。tbdjgs 结构：djname(单据类型名)/djjx(前缀)/lsh(流水)/dyear/dpm/dmonth。
/// getnewdh 机制：单号 = djjx + dyear 后 2 位 + lsh（6 位）。
/// 旧初始化 10 种类型（fr_billprefx row2 L11-20：销售 XS/报价 BJ/预售 YS/调拨 DB/调仓 DC/退货 TH/
/// 领料 LL/维修报价 XB/收款 SK/付款 FK），生产实际值域：退货单=AH、散件入库单=RK、进库单=AK、盘点单=AP 等。
/// </summary>
public sealed class DocNoPrefixRepository(SqlConnectionFactory factory) : IDocNoPrefixRepository
{
    public async Task<IReadOnlyList<DocNoPrefixRow>> GetListAsync(string dept, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            "SELECT id, RTRIM(djname) djname, RTRIM(djjx) djjx, lsh, RTRIM(dyear) dyear, RTRIM(dpm) dpm " +
            "FROM tbdjgs WITH (NOLOCK) WHERE dpm = @dpm ORDER BY id",
            new { dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 } },
            cancellationToken: ct);
        return (await conn.QueryAsync<DocNoPrefixRow>(cmd)).AsList();
    }

    public async Task UpdateAsync(UpdateDocNoPrefixCommand command, string dept, CancellationToken ct = default)
    {
        command.Validate();
        var djjx = command.Djjx.Trim();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 读取目标行（须同部门）
            var row = await conn.QueryFirstOrDefaultAsync<(string djname, string oldDjjx)>(
                new CommandDefinition(
                    "SELECT RTRIM(djname) djname, RTRIM(djjx) djjx FROM tbdjgs WITH (UPDLOCK) WHERE id = @id AND dpm = @dpm",
                    new { id = command.Id, dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 } },
                    transaction: tx, cancellationToken: ct));
            if (row.djname is null)
                throw new AppException("PREFIX_NOT_FOUND", $"单号前缀记录 {command.Id} 不存在", 404);

            // SK/FK 前缀唯一校验（旧 row5 契约：收款/付款前缀不得与其他类型冲突）
            if (djjx is "SK" or "FK")
            {
                var conflict = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbdjgs WITH (NOLOCK) WHERE dpm = @dpm AND RTRIM(djjx) = @djjx AND id <> @id",
                    new { dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 }, djjx = new DbString { Value = djjx, IsAnsi = true, Length = 4 }, id = command.Id },
                    transaction: tx, cancellationToken: ct));
                if (conflict > 0)
                    throw new AppException("DJJX_CONFLICT", $"前缀 {djjx} 已被其他单据类型使用（收款/付款前缀必须唯一）", 409);
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbdjgs SET djjx = @djjx WHERE id = @id",
                new { djjx = new DbString { Value = djjx, IsAnsi = true, Length = 4 }, id = command.Id },
                transaction: tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
