using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Stock;

public interface IStockEditRepository
{
    /// <summary>库存修改（tbisto 直改 + tbistoch 修改流水 + tbprnoty 属性联动）。</summary>
    Task<UpdateStockResult> UpdateAsync(UpdateStockCommand command, string dept, CancellationToken ct = default);

    /// <summary>库存行删除（仅 kcamount=0；fr_istoch row31）。</summary>
    Task DeleteAsync(int id, string dept, CancellationToken ct = default);
}

/// <summary>
/// 库存修改仓储（M4-3，写路径契约感知）。
/// 旧实现出处：fr_istoch 禰.row71 / row31（无触发器，纯应用层 SQL）：
///   1) 并发守卫：重查 tbisto.kcamount 与查询时快照（OLDVAL）比对，不一致拒绝
///   2) UPDATE tbisto（数量变化含 KCAMOUNT、否则不含）WHERE id
///   3) na1/ty/fa/dc/unit 变更 → UPDATE tbprnoty SET na1,ty,fa,dc,unit WHERE cp+nno
///   4) 数量/iprc/iprj 任一变化 → INSERT tbistoch 修改流水：
///      XGAMOUNT=新kc-旧kc；IPRC=新kc*新iprc-旧kc*旧iprc（金额差额）；IPRJ 同；XGDATE=当前；per；dpm
///   5) 删除：kcamount=0 才可 DELETE（B304 禁删权限在应用层检查）
/// </summary>
public sealed class StockEditRepository(SqlConnectionFactory factory) : IStockEditRepository
{
    public async Task<UpdateStockResult> UpdateAsync(UpdateStockCommand command, string dept, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 1) 读行 + 并发守卫
            var row = await conn.QueryFirstOrDefaultAsync<(decimal kc, decimal iprc, decimal iprj, string cp, string nno,
                string na1, string ty, string fa, string dc, string unit, string posi, string depotno)>(
                new CommandDefinition(
                    """
                    SELECT kcamount, iprc, iprj, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty,
                           RTRIM(fa) fa, RTRIM(dc) dc, RTRIM(unit) unit, RTRIM(posi) posi, RTRIM(depotno) depotno
                    FROM tbisto WITH (UPDLOCK) WHERE id = @id AND dpm = @dpm
                    """,
                    new { id = command.Id, dpm = Ansi(dept, 10) },
                    transaction: tx, cancellationToken: ct));
            if (row.cp is null)
                throw new AppException("STOCK_NOT_FOUND", $"库存行 {command.Id} 不存在", 404);
            if (row.kc != command.OrigKcamount)
                throw new AppException("STOCK_CHANGED",
                    $"库存有变动：目前库存为 {row.kc}，您查询时为 {command.OrigKcamount}，请重新查询后再修改", 409);

            // 2) UPDATE tbisto（含/不含 KCAMOUNT 两分支）
            var newKc = command.Kcamount ?? row.kc;
            var newIprc = command.Iprc ?? row.iprc;
            var newIprj = command.Iprj ?? row.iprj;
            var kcChanged = command.Kcamount is not null && command.Kcamount.Value != row.kc;
            var setParts = new List<string>();
            var p = new DynamicParameters();
            p.Add("id", command.Id);
            void Set(string col, object? val, bool changed)
            {
                if (!changed) return;
                setParts.Add($"{col} = @{col}");
                p.Add(col, val);
            }
            Set("nno", new DbString { Value = (command.Nno ?? "").Trim(), IsAnsi = true, Length = 18 }, command.Nno is not null);
            Set("na1", new DbString { Value = (command.Na1 ?? "").Trim(), IsAnsi = false, Length = 40 }, command.Na1 is not null);
            Set("ty", new DbString { Value = (command.Ty ?? "").Trim(), IsAnsi = false, Length = 14 }, command.Ty is not null);
            Set("fa", new DbString { Value = (command.Fa ?? "").Trim(), IsAnsi = false, Length = 8 }, command.Fa is not null);
            Set("dc", new DbString { Value = (command.Dc ?? "").Trim(), IsAnsi = false, Length = 8 }, command.Dc is not null);
            Set("unit", new DbString { Value = (command.Unit ?? "").Trim(), IsAnsi = false, Length = 4 }, command.Unit is not null);
            Set("kcamount", command.Kcamount!.Value, kcChanged);
            Set("iprc", command.Iprc!.Value, command.Iprc is not null && command.Iprc.Value != row.iprc);
            Set("iprj", command.Iprj!.Value, command.Iprj is not null && command.Iprj.Value != row.iprj);
            Set("oprc", command.Oprc!.Value, command.Oprc is not null);
            Set("oprb", command.Oprb!.Value, command.Oprb is not null);
            Set("posi", new DbString { Value = (command.Posi ?? "").Trim(), IsAnsi = true, Length = 10 }, command.Posi is not null);
            Set("depotno", new DbString { Value = (command.Depotno ?? "").Trim(), IsAnsi = true, Length = 10 }, command.Depotno is not null);
            Set("note", new DbString { Value = (command.Note ?? "").Trim(), IsAnsi = false, Length = 100 }, command.Note is not null);

            var changeNote = "";
            if (setParts.Count > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    $"UPDATE tbisto SET {string.Join(", ", setParts)} WHERE id = @id",
                    p, transaction: tx, cancellationToken: ct));

                // 3) 属性联动 tbprnoty
                var propChanged = command.Na1 is not null || command.Ty is not null || command.Fa is not null
                    || command.Dc is not null || command.Unit is not null;
                if (propChanged)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbprnoty SET na1 = @na1, ty = @ty, fa = @fa, dc = @dc, unit = @unit WHERE cp = @cp AND nno = @nno",
                        new
                        {
                            na1 = Ansi(command.Na1 ?? row.na1, 40), ty = Ansi(command.Ty ?? row.ty, 14),
                            fa = Ansi(command.Fa ?? row.fa, 8), dc = Ansi(command.Dc ?? row.dc, 8),
                            unit = Ansi(command.Unit ?? row.unit, 4), cp = Ansi(row.cp, 10), nno = Ansi(row.nno, 18),
                        },
                        transaction: tx, cancellationToken: ct));
                }

                // 4) 修改流水 tbistoch（数量/进价任一变化）
                var amountChanged = kcChanged
                    || (command.Iprc is not null && command.Iprc.Value != row.iprc)
                    || (command.Iprj is not null && command.Iprj.Value != row.iprj);
                var result = new UpdateStockResult { Xgamount = newKc - row.kc, PropertySynced = propChanged };
                if (amountChanged)
                {
                    var noteParts = new List<string>();
                    if (kcChanged) noteParts.Add($"库存数量 {row.kc}->{newKc}");
                    if (command.Iprc is not null && command.Iprc.Value != row.iprc) noteParts.Add($"进价 {row.iprc}->{newIprc}");
                    if (command.Iprj is not null && command.Iprj.Value != row.iprj) noteParts.Add($"进价金额 {row.iprj}->{newIprj}");
                    changeNote = string.Join("；", noteParts);
                    result.ChangeNote = changeNote;
                    result.HasChangelog = true;

                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbistoch
                            (CP, NNO, NA1, ty, UNIT, XGAMOUNT, iprc, iprj, posi, depotno, XGDATE, per, cno, CXNNO, note, dpm)
                        VALUES
                            (@cp, @nno, @na1, @ty, @unit, @xgamount, @iprcdiff, @iprjdiff, @posi, @depotno, @xgdate, @per, @cno, @cxnno, @note, @dpm)
                        """,
                        new
                        {
                            cp = Ansi(row.cp, 10), nno = Ansi(row.nno, 18), na1 = Ansi(command.Na1 ?? row.na1, 40),
                            ty = Ansi(command.Ty ?? row.ty, 14), unit = Ansi(command.Unit ?? row.unit, 2),
                            xgamount = newKc - row.kc,
                            iprcdiff = newKc * newIprc - row.kc * row.iprc,
                            iprjdiff = newKc * newIprj - row.kc * row.iprj,
                            posi = Ansi(command.Posi ?? row.posi, 10), depotno = Ansi(command.Depotno ?? row.depotno, 10),
                            xgdate = DateTime.Now, per = Ansi(command.Per, 6), cno = Ansi("", 8),
                            cxnno = Ansi("", 18), note = Ansi(changeNote, 100), dpm = Ansi(dept, 10),
                        },
                        transaction: tx, cancellationToken: ct));
                }
                await tx.CommitAsync(ct);
                return result;
            }

            await tx.CommitAsync(ct);
            return new UpdateStockResult { Xgamount = 0 };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(int id, string dept, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var kc = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT kcamount FROM tbisto WITH (UPDLOCK) WHERE id = @id AND dpm = @dpm",
                new { id, dpm = Ansi(dept, 10) },
                transaction: tx, cancellationToken: ct));
            if (kc is null)
                throw new AppException("STOCK_NOT_FOUND", $"库存行 {id} 不存在", 404);
            if (kc.Value != 0)
                throw new AppException("STOCK_NOT_ZERO", "库存数量不为 0，不能删除", 409);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE tbisto WHERE id = @id", new { id },
                transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbString Ansi(string value, int length) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
