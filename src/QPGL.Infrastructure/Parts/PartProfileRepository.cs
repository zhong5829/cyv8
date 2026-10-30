using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Parts;

public interface IPartProfileRepository
{
    /// <summary>配件资料分页查询（tbprnoty，逻辑键 cp+nno）。</summary>
    Task<PagedResult<PartProfileRow>> SearchAsync(PartProfileQueryRequest request, CancellationToken ct = default);

    /// <summary>配件资料修改（UPDATE tbprnoty WHERE cp+nno；一期不含新增——新增走散件入库自动生成）。</summary>
    Task UpdateAsync(UpdatePartProfileCommand command, CancellationToken ct = default);

    /// <summary>按内部编号（条码）批量取属性库行（散件入库表格导入 ___.row214 L49 语义）。</summary>
    Task<IReadOnlyList<PartProfileRow>> GetByNnosAsync(IEnumerable<string> nnos, CancellationToken ct = default);
}

/// <summary>
/// 配件资料（零件属性库）仓储（M5，A1）。tbprnoty 无 id 列，逻辑键 = cp+nno。
/// 入库新配件自动 INSERT 契约（fr_isto1 row31 L213-228）：
///   INSERT tbprnoty (cp,nno,na1,ty,fa,dc,unit,note,iprj,uqty,cxnno,oprc_cp,oprc_min,cpict) —— InStockRepository 内复刻。
/// </summary>
public sealed class PartProfileRepository(SqlConnectionFactory factory) : IPartProfileRepository
{
    private const string Columns =
        "RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa, RTRIM(dc) dc, " +
        "RTRIM(unit) unit, iprj, uqty, oprc_min, RTRIM(note) note";

    public async Task<PagedResult<PartProfileRow>> SearchAsync(PartProfileQueryRequest request, CancellationToken ct = default)
    {
        var where = "1=1";
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(request.Cp))
        {
            where += " AND cp LIKE @cp";
            p.Add("cp", $"{request.Cp.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Nno))
        {
            where += " AND nno LIKE @nno";
            p.Add("nno", $"{request.Nno.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Na1))
        {
            where += " AND na1 LIKE @na1";
            p.Add("na1", $"%{request.Na1.Trim()}%");
        }
        // 车型框：车型包含 + tbty0 车型拼音码（2026-10-30 需求，全页面对齐开单页）。
        // 必须 LEFT JOIN 派生表 + HASH JOIN 提示（实测）：无提示 IN 半连接优化器误选嵌套循环 30s+/超时，
        // JOIN 派生表后 40ms；列带 t. 前缀避免与派生表 tb2.ty 歧义（SQL 2515）
        var tyJoin = "";
        if (!string.IsNullOrWhiteSpace(request.Ty))
        {
            tyJoin = "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS ty FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @ty + '%') tb2 ON rtrim(t.ty) = tb2.ty";
            where += " AND (t.ty LIKE @ty OR tb2.ty IS NOT NULL)";
            p.Add("ty", $"%{request.Ty.Trim()}%");
        }
        var orderBy = PartProfileSortFields.TryResolve(request.Sort, out var col)
            ? $"{col} {(request.IsDescending ? "DESC" : "ASC")}, cp ASC, nno ASC"
            : "cp ASC, nno ASC";

        await using var conn = await factory.OpenAsync(ct);
        var sql = $"""
            SELECT t.cp AS cp, t.nno AS nno, t.na1 AS na1, t.ty AS ty, t.fa AS fa, t.dc AS dc, t.unit AS unit, t.iprj AS iprj, t.uqty AS uqty, t.oprc_min AS oprc_min, t.note AS note
            FROM tbprnoty t WITH (NOLOCK){tyJoin}
            WHERE {where}
            ORDER BY {orderBy}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            SELECT COUNT(1) FROM tbprnoty t WITH (NOLOCK){tyJoin} WHERE {where};
            """ + (tyJoin.Length > 0 ? "\nOPTION (HASH JOIN);" : "");
        p.Add("skip", (request.Page - 1) * request.PageSize);
        p.Add("take", request.PageSize);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = (await multi.ReadAsync<PartProfileRow>()).AsList();
        var total = await multi.ReadFirstAsync<int>();
        return new PagedResult<PartProfileRow> { Items = rows, Total = total, Page = request.Page, PageSize = request.PageSize };
    }

    public async Task UpdateAsync(UpdatePartProfileCommand command, CancellationToken ct = default)
    {
        command.Validate();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var setParts = new List<string>();
            var p = new DynamicParameters();
            p.Add("cp", new DbString { Value = command.Cp.Trim(), IsAnsi = true, Length = 10 });
            p.Add("nno", new DbString { Value = command.Nno.Trim(), IsAnsi = true, Length = 18 });
            void Set(string col, object? val, bool include)
            {
                if (!include) return;
                setParts.Add($"{col} = @{col}");
                p.Add(col, val);
            }
            Set("na1", new DbString { Value = (command.Na1 ?? "").Trim(), IsAnsi = false, Length = 40 }, command.Na1 is not null);
            Set("ty", new DbString { Value = (command.Ty ?? "").Trim(), IsAnsi = false, Length = 14 }, command.Ty is not null);
            Set("unit", new DbString { Value = (command.Unit ?? "").Trim(), IsAnsi = false, Length = 4 }, command.Unit is not null);
            Set("iprj", command.Iprj!.Value, command.Iprj is not null);
            Set("uqty", command.Uqty!.Value, command.Uqty is not null);
            Set("oprc_min", command.OprcMin!.Value, command.OprcMin is not null);
            Set("note", new DbString { Value = (command.Note ?? "").Trim(), IsAnsi = false, Length = 254 }, command.Note is not null);

            if (setParts.Count == 0)
                throw new AppException("NOTHING_TO_UPDATE", "没有需要修改的字段", 422);

            var rows = await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE tbprnoty SET {string.Join(", ", setParts)} WHERE cp = @cp AND nno = @nno",
                p, transaction: tx, cancellationToken: ct));
            if (rows == 0)
                throw new AppException("PART_PROFILE_NOT_FOUND", $"配件资料 {command.Cp}/{command.Nno} 不存在", 404);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<PartProfileRow>> GetByNnosAsync(IEnumerable<string> nnos, CancellationToken ct = default)
    {
        var list = nnos.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().Take(500).AsList();
        if (list.Count == 0) return [];
        await using var conn = await factory.OpenAsync(ct);
        // IN @nnos（内部编号 nno char(18) ANSI；cp 取该 nno 首个厂牌）
        var rows = await conn.QueryAsync<PartProfileRow>(new CommandDefinition(
            $"""
            SELECT {Columns}
            FROM tbprnoty WITH (NOLOCK)
            WHERE nno IN @nnos
            """,
            new { nnos = list.Select(n => new DbString { Value = n, IsAnsi = true, Length = 18 }) },
            cancellationToken: ct));
        // 同 nno 多厂牌时按 id 去重保留一行（tbprnoty 无 id，按 cp+nno 唯一 → 同 nno 取首条）
        return rows.GroupBy(r => r.Nno.Trim()).Select(g => g.First()).AsList();
    }
}
