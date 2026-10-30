using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Parts;

/// <summary>A4 套件管理（tbprtj 单表双角色：nno='' 头 / nno&lt;&gt;'' 组件；组套/拆套动 tbisto）。</summary>
public interface IKitRepository
{
    Task<IReadOnlyList<KitHeadRow>> GetHeadsAsync(string where, string cp, string dpm, CancellationToken ct = default);

    Task<IReadOnlyList<KitLineRow>> GetLinesAsync(string tj, string cp, CancellationToken ct = default);

    /// <summary>套件头保存（防重 + 同步 tbprnoty/tbname0；edit 改码全改）。</summary>
    Task SaveHeadAsync(KitHeadCommand c, string dpm, CancellationToken ct = default);

    /// <summary>组件保存（防重 + 套件价=Σ iprj×num 回写头行）。</summary>
    Task SaveLineAsync(KitLineCommand c, string cp, CancellationToken ct = default);

    /// <summary>删套件/组件（库存>0 拦截；删套件连删 tbisto）。</summary>
    Task DeleteKitAsync(string tj, string cp, string dpmusem, CancellationToken ct = default);

    Task DeleteKitLineAsync(string tj, string nno, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>组套（FIFO 扣组件 + 加权成本入套件）。</summary>
    Task AssembleAsync(string tj, string nno, decimal qty, string cp, string? depotno, string dpm, string dpmusem, CancellationToken ct = default);

    /// <summary>拆套（扣套件 + 组件回库不带成本字段，契约 §4 不对称照抄）。</summary>
    Task DisassembleAsync(string tj, decimal qty, string cp, string? depotno, string dpm, string dpmusem, CancellationToken ct = default);
}

public sealed class KitHeadRow
{
    public string Tj { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Note { get; set; } = "";
    public decimal Iprj { get; set; }
    public decimal Kcamou { get; set; }
    public long Kcid { get; set; }
}

public sealed class KitLineRow
{
    public long Id { get; set; }
    public string Tj { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Num { get; set; }
    public decimal Iprj { get; set; }
    public string Note { get; set; } = "";
    public decimal Kcamou { get; set; }
}

public sealed class KitHeadCommand
{
    public bool IsAdd { get; set; }
    public required string Tj { get; set; }
    public string OldTj { get; set; } = "";
    public required string Cp { get; set; }
    public required string Na1 { get; set; }
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class KitLineCommand
{
    public bool IsAdd { get; set; }
    public long Id { get; set; }
    public required string Tj { get; set; }
    public required string Nno { get; set; }
    public string OldNno { get; set; } = "";
    public required string Cp { get; set; }
    public required string Na1 { get; set; }
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Num { get; set; }
    public decimal Iprj { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>A7 商品税则号（只读 + low_rate 修改）。</summary>
public interface IComplexRepository
{
    Task<IReadOnlyList<ComplexRow>> GetAllAsync(CancellationToken ct = default);

    Task UpdateLowRateAsync(string codeTs, decimal lowRate, CancellationToken ct = default);
}

public sealed class ComplexRow
{
    public string Code_ts { get; set; } = "";
    public string G_name { get; set; } = "";
    public decimal Low_rate { get; set; }
    public decimal High_rate { get; set; }
    public string Unit_1 { get; set; } = "";
    public string Note_s { get; set; } = "";
}

/// <summary>AA 分类码定义（tbdccoef CRUD）。</summary>
public interface IDcCoefRepository
{
    Task<IReadOnlyList<(string Dc, decimal Coeffict)>> GetAllAsync(CancellationToken ct = default);

    Task SaveAsync(bool isAdd, string dc, decimal coeffict, CancellationToken ct = default);

    Task DeleteAsync(string dc, CancellationToken ct = default);
}

/// <summary>A8 系统定义（公司信息/厂牌/字典/参数四页；契约 §6）。</summary>
public interface ISysDefineRepository
{
    Task<(string DpmName, IReadOnlyList<(string Name, string Valuec)> Items)> GetCompanyAsync(string dpm, CancellationToken ct = default);

    Task SaveCompanyAsync(string dpm, IReadOnlyList<(string Name, string Valuec)> items, string dpmName, CancellationToken ct = default);

    Task<IReadOnlyList<CpRow>> GetCpsAsync(string dpm, CancellationToken ct = default);

    /// <summary>厂牌整组删+重插（row39 语义）。</summary>
    Task SaveCpsAsync(string dpm, IReadOnlyList<CpRow> rows, CancellationToken ct = default);

    /// <summary>字典组查询（row73：dpm 过滤 ORDER BY type,id）。</summary>
    Task<IReadOnlyList<SysSetRow>> GetDictAsync(string dpm, string? type, CancellationToken ct = default);

    Task SaveDictAsync(bool isAdd, int id, string type, string name, string? valuec, decimal? valuen, string dpm, CancellationToken ct = default);

    Task DeleteDictAsync(int id, CancellationToken ct = default);

    /// <summary>参数页（findrecord 自动补建：先补 xtype 再补行）。</summary>
    Task<IReadOnlyList<SysSetRow>> GetParasAsync(IReadOnlyList<(string Name, string Type, char Kind, string Sysname, string Xtype)> defs, string dpm, CancellationToken ct = default);

    /// <summary>参数保存（按 Kind 分派列：L→valuel C→valuec N→valuen；Sysname='销售' 写 note）。</summary>
    Task SaveParasAsync(IReadOnlyList<(int Id, char Kind, string Sysname, string? ValueC, decimal? ValueN, bool? ValueL)> items, CancellationToken ct = default);
}

public sealed class CpRow
{
    public string Name { get; set; } = "";
    public string Valuec { get; set; } = "";
    public string Note { get; set; } = "";
    public string Cxnno { get; set; } = "";
    public decimal Valuen { get; set; }
}

public sealed class SysSetRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Valuec { get; set; } = "";
    public decimal? Valuen { get; set; }
    public bool? Valuel { get; set; }
    public string Note { get; set; } = "";
    public string Xtype { get; set; } = "";
    public string Dpm { get; set; } = "";
}

// ---------- 实现 ----------

public sealed class KitRepository(SqlConnectionFactory factory, IDmService dm) : IKitRepository
{
    public async Task<IReadOnlyList<KitHeadRow>> GetHeadsAsync(string where, string cp, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var sql = $"""
            SELECT a.tj, a.na1, a.ty, a.fa, a.dc, a.unit, a.note, a.iprj,
                   ISNULL(b.kcamount, 0) kcamou, ISNULL(b.id, 0) kcid
            FROM (SELECT cp, tj, na1, ty, fa, dc, unit, note, iprj, MAX(id) id FROM tbprtj WITH (NOLOCK)
                  WHERE ({where}) AND tj >= 'T-' AND nno = '' AND cp = @cp AND ISNULL(dpm, @dpm) = @dpm
                  GROUP BY cp, tj, na1, ty, fa, dc, unit, note, iprj) a
            LEFT JOIN tbisto b WITH (NOLOCK) ON a.tj = b.nno AND a.cp = b.cp
            ORDER BY a.tj
            """;
        return (await conn.QueryAsync<KitHeadRow>(new CommandDefinition(sql,
            new { cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } },
            cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<KitLineRow>> GetLinesAsync(string tj, string cp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<KitLineRow>(new CommandDefinition("""
            SELECT id, rtrim(tj) tj, rtrim(nno) nno, rtrim(na1) na1, rtrim(ty) ty, rtrim(fa) fa, rtrim(dc) dc,
                   rtrim(unit) unit, num, iprj, rtrim(note) note, 0 kcamou
            FROM tbprtj WITH (NOLOCK) WHERE rtrim(tj)=@tj AND NOT (nno = '' OR nno IS NULL) AND rtrim(cp)=@cp ORDER BY id
            """, new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } },
            cancellationToken: ct))).AsList();
        foreach (var r in rows)
            r.Kcamou = await conn.ExecuteScalarAsync<decimal?>(
                "SELECT SUM(kcamount) FROM tbisto WITH (NOLOCK) WHERE rtrim(cp)=@cp AND rtrim(nno)=@nno",
                new { cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, nno = new DbString { Value = r.Nno, IsAnsi = true, Length = 18 } }) ?? 0m;
        return rows;
    }

    public async Task SaveHeadAsync(KitHeadCommand c, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM tbprtj WITH (NOLOCK) WHERE rtrim(tj)=@tj AND rtrim(cp)=@cp) THEN 1 ELSE 0 END",
                new { tj = new DbString { Value = c.Tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = c.Cp, IsAnsi = true, Length = 10 } },
                cancellationToken: ct)) > 0 && c.IsAdd)
            throw new QPGL.Domain.Common.AppException("TJ_DUP", $"套件码 {c.Tj} 已存在！", 409);
        if (c.IsAdd)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tbprtj (cp, tj, na1, ty, fa, dc, unit, note) VALUES (@cp, @tj, @na1, @ty, @fa, @dc, @unit, @note)",
                HeadParams(c), cancellationToken: ct));
        }
        else if (c.OldTj != c.Tj)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbprtj SET tj=@tj WHERE rtrim(tj)=@old", new { tj = new DbString { Value = c.Tj, IsAnsi = true, Length = 18 }, old = new DbString { Value = c.OldTj, IsAnsi = true, Length = 18 } }, cancellationToken: ct));
        }
        // 同步 tbprnoty（套件在属性库占一行）
        var p = new DynamicParameters();
        p.Add("cp", new DbString { Value = c.Cp, IsAnsi = true, Length = 10 });
        p.Add("nno", new DbString { Value = c.Tj, IsAnsi = true, Length = 18 });
        p.Add("na1", new DbString { Value = c.Na1, IsAnsi = true, Length = 40 });
        p.Add("ty", new DbString { Value = c.Ty ?? "", IsAnsi = true, Length = 14 });
        p.Add("fa", new DbString { Value = c.Fa ?? "", IsAnsi = true, Length = 8 });
        p.Add("dc", new DbString { Value = c.Dc ?? "", IsAnsi = true, Length = 8 });
        p.Add("unit", new DbString { Value = c.Unit ?? "", IsAnsi = true, Length = 4 });
        p.Add("note", new DbString { Value = c.Note ?? "", IsAnsi = true, Length = 50 });
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM tbprnoty WITH (NOLOCK) WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp) THEN 1 ELSE 0 END", p, cancellationToken: ct));
        if (exists == 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, note) VALUES (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @note)", p, cancellationToken: ct));
            // tbname0 同步（DM 拼音）
            var code = await dm.DmAsync(conn, 1, c.Na1, ct);
            var code1 = await dm.DmAsync(conn, 2, c.Na1, ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "IF NOT EXISTS (SELECT 1 FROM tbname0 WITH (NOLOCK) WHERE rtrim(name)=@name) INSERT INTO tbname0 (name, name1, code, code1) VALUES (@name, @name1, @code, @code1)",
                new
                {
                    name = new DbString { Value = c.Na1, IsAnsi = true, Length = 20 },
                    name1 = new DbString { Value = c.Na1, IsAnsi = true, Length = 20 },
                    code = new DbString { Value = code, IsAnsi = true, Length = 10 },
                    code1 = new DbString { Value = code1, IsAnsi = true, Length = 10 },
                }, cancellationToken: ct));
            // 车型拼音码同步（2026-10-30 需求：tbty0 无行则插入；只增不删）
            await Name0Sync.SyncTyAsync(conn, null, dm, c.Ty, ct);
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbprnoty SET cp=@cp, nno=@nno, na1=@na1, ty=@ty, fa=@fa, dc=@dc, unit=@unit, note=@note WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp", p, cancellationToken: ct));
        }
    }

    private static DynamicParameters HeadParams(KitHeadCommand c)
    {
        var p = new DynamicParameters();
        p.Add("cp", new DbString { Value = c.Cp, IsAnsi = true, Length = 10 });
        p.Add("tj", new DbString { Value = c.Tj, IsAnsi = true, Length = 18 });
        p.Add("na1", new DbString { Value = c.Na1, IsAnsi = true, Length = 40 });
        p.Add("ty", new DbString { Value = c.Ty ?? "", IsAnsi = true, Length = 14 });
        p.Add("fa", new DbString { Value = c.Fa ?? "", IsAnsi = true, Length = 8 });
        p.Add("dc", new DbString { Value = c.Dc ?? "", IsAnsi = true, Length = 8 });
        p.Add("unit", new DbString { Value = c.Unit ?? "", IsAnsi = true, Length = 4 });
        p.Add("note", new DbString { Value = c.Note ?? "", IsAnsi = true, Length = 50 });
        return p;
    }

    public async Task SaveLineAsync(KitLineCommand c, string cp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (c.IsAdd)
        {
            if (await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT CASE WHEN EXISTS (SELECT 1 FROM tbprtj WITH (NOLOCK) WHERE rtrim(nno)=@nno AND rtrim(tj)=@tj AND rtrim(cp)=@cp) THEN 1 ELSE 0 END",
                    new { nno = new DbString { Value = c.Nno, IsAnsi = true, Length = 18 }, tj = new DbString { Value = c.Tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } },
                    cancellationToken: ct)) > 0)
                throw new QPGL.Domain.Common.AppException("KITLINE_DUP", "该组件已存在！", 409);
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO tbprtj (cp, tj, nno, na1, ty, fa, dc, unit, note, num, iprj)
                VALUES (@cp, @tj, @nno, @na1, @ty, @fa, @dc, @unit, @note, @num, @iprj)
                """, new
            {
                cp = new DbString { Value = cp, IsAnsi = true, Length = 10 },
                tj = new DbString { Value = c.Tj, IsAnsi = true, Length = 18 },
                nno = new DbString { Value = c.Nno, IsAnsi = true, Length = 18 },
                na1 = new DbString { Value = c.Na1, IsAnsi = true, Length = 40 },
                ty = new DbString { Value = c.Ty ?? "", IsAnsi = true, Length = 14 },
                fa = new DbString { Value = c.Fa ?? "", IsAnsi = true, Length = 8 },
                dc = new DbString { Value = c.Dc ?? "", IsAnsi = true, Length = 8 },
                unit = new DbString { Value = c.Unit ?? "", IsAnsi = true, Length = 4 },
                note = new DbString { Value = c.Note ?? "", IsAnsi = true, Length = 50 },
                c.Num, c.Iprj,
            }, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE tbprtj SET nno=@nno, na1=@na1, ty=@ty, fa=@fa, dc=@dc, unit=@unit, note=@note, num=@num, iprj=@iprj
                WHERE id=@id
                """, new
            {
                nno = new DbString { Value = c.Nno, IsAnsi = true, Length = 18 },
                na1 = new DbString { Value = c.Na1, IsAnsi = true, Length = 40 },
                ty = new DbString { Value = c.Ty ?? "", IsAnsi = true, Length = 14 },
                fa = new DbString { Value = c.Fa ?? "", IsAnsi = true, Length = 8 },
                dc = new DbString { Value = c.Dc ?? "", IsAnsi = true, Length = 8 },
                unit = new DbString { Value = c.Unit ?? "", IsAnsi = true, Length = 4 },
                note = new DbString { Value = c.Note ?? "", IsAnsi = true, Length = 50 },
                c.Num, c.Iprj, c.Id,
            }, cancellationToken: ct));
        }
        // 套件价 = Σ 组件 iprj×num（row99）
        var sum = await conn.ExecuteScalarAsync<decimal?>(
            "SELECT SUM(num * iprj) FROM tbprtj WITH (NOLOCK) WHERE rtrim(tj)=@tj AND NOT (nno='' OR nno IS NULL) AND rtrim(cp)=@cp",
            new { tj = new DbString { Value = c.Tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } }) ?? 0m;
        await conn.ExecuteAsync(
            "UPDATE tbprtj SET iprj=@sum WHERE (nno='' OR nno IS NULL) AND rtrim(tj)=@tj AND rtrim(cp)=@cp",
            new { sum, tj = new DbString { Value = c.Tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } });
    }

    public async Task DeleteKitAsync(string tj, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var kc = await StockAsync(conn, tj, cp, dpmusem, ct);
        if (kc != 0) throw new QPGL.Domain.Common.AppException("KIT_STOCK", "该套件有库存，不能删除！", 409);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbisto WHERE rtrim(nno)=@tj", new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 } }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbprtj WHERE rtrim(tj)=@tj", new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 } }, cancellationToken: ct));
    }

    public async Task DeleteKitLineAsync(string tj, string nno, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var kc = await StockAsync(conn, nno, cp, dpmusem, ct);
        if (kc != 0) throw new QPGL.Domain.Common.AppException("KIT_STOCK", "该组件有库存，不能删除！", 409);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbprtj WHERE rtrim(tj)=@tj AND rtrim(nno)=@nno",
            new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 }, nno = new DbString { Value = nno, IsAnsi = true, Length = 18 } },
            cancellationToken: ct));
    }

    private static async Task<decimal> StockAsync(SqlConnection conn, string nno, string cp, string dpmusem, CancellationToken ct)
        => await conn.ExecuteScalarAsync<decimal?>(
            "SELECT SUM(kcamount) FROM tbisto WITH (NOLOCK) WHERE rtrim(nno)=@nno AND @p_dpmusem LIKE '%'+rtrim(dpm)+'%'",
            new { nno = new DbString { Value = nno, IsAnsi = true, Length = 18 }, p_dpmusem = new DbString { Value = dpmusem ?? "", IsAnsi = true, Length = 60 } }) ?? 0m;

    public async Task AssembleAsync(string tj, string nno, decimal qty, string cp, string? depotno, string dpm, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 旧码 row79：qty=套数，扣减量=套数×组件 num（row99 定义）
        var num = await conn.ExecuteScalarAsync<decimal?>(
            "SELECT num FROM tbprtj WITH (NOLOCK) WHERE rtrim(tj)=@tj AND rtrim(nno)=@nno AND rtrim(cp)=@cp",
            new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 }, nno = new DbString { Value = nno, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } }) ?? 0m;
        // 组件行（FIFO 逐行扣减）
        var rows = (await conn.QueryAsync<(long Id, decimal Kc, decimal Oprc, decimal Iprc, decimal Iprj)>(
            new CommandDefinition(
                "SELECT id, kcamount, oprc, iprc, iprj FROM tbisto WITH (NOLOCK) WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp" +
                (string.IsNullOrEmpty(depotno) ? "" : " AND rtrim(depotno)=@dep") + " AND @p_dpmusem LIKE '%'+rtrim(dpm)+'%' ORDER BY id",
                new { nno = new DbString { Value = nno, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, dep = new DbString { Value = depotno ?? "", IsAnsi = true, Length = 10 }, p_dpmusem = new DbString { Value = dpmusem ?? "", IsAnsi = true, Length = 60 } },
                cancellationToken: ct))).AsList();
        var need = qty * num;
        decimal wOprc = 0, wIprc = 0, wIprj = 0, taken = 0;
        foreach (var r in rows)
        {
            if (need <= 0) break;
            var take = Math.Min(need, r.Kc);
            if (take <= 0) continue;
            await conn.ExecuteAsync(
                "UPDATE tbisto SET kcamount=kcamount-@take WHERE id=@id",
                new { take, r.Id });
            wOprc += take * r.Oprc; wIprc += take * r.Iprc; wIprj += take * r.Iprj; taken += take;
            need -= take;
        }
        if (need > 0) throw new QPGL.Domain.Common.AppException("KIT_NOSTOCK", "组件库存不足，组套失败！", 409);
        // 套件侧（原文 tj=?m_tj and nno='' and cp=?m_cp，缺空格语义照抄）
        var head = await conn.QuerySingleOrDefaultAsync<(long Id, decimal Kc)>(
            "SELECT id, kcamount FROM tbisto WITH (NOLOCK) WHERE rtrim(nno)=@tj AND rtrim(cp)=@cp" +
            (string.IsNullOrEmpty(depotno) ? "" : " AND rtrim(depotno)=@dep"),
            new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, dep = new DbString { Value = depotno ?? "", IsAnsi = true, Length = 10 } });
        if (head.Id == 0)
        {
            var meta = await conn.QuerySingleOrDefaultAsync<(string Tj, string Na1, string Ty, string Fa, string Dc, string Note)>(
                "SELECT rtrim(tj), rtrim(na1), rtrim(ty), rtrim(fa), rtrim(dc), rtrim(note) FROM tbprtj WITH (NOLOCK) WHERE rtrim(tj)=@tj AND (nno='' OR nno IS NULL) AND rtrim(cp)=@cp",
                new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } });
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO tbisto (dpm, cp, nno, na1, ty, fa, dc, note, iprj, oprc, iprc, kcamount, depotno)
                VALUES (@dpm, @cp, @nno, @na1, @ty, @fa, @dc, @note, @iprj, @oprc, @iprc, @kc, @depotno)
                """, new
            {
                dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 }, // PASSTH 自动补 dpm=p_dpm
                cp = new DbString { Value = cp, IsAnsi = true, Length = 10 },
                nno = new DbString { Value = tj, IsAnsi = true, Length = 18 },
                na1 = new DbString { Value = meta.Na1 ?? "", IsAnsi = true, Length = 40 },
                ty = new DbString { Value = meta.Ty ?? "", IsAnsi = true, Length = 14 },
                fa = new DbString { Value = meta.Fa ?? "", IsAnsi = true, Length = 8 },
                dc = new DbString { Value = meta.Dc ?? "", IsAnsi = true, Length = 8 },
                note = new DbString { Value = meta.Note ?? "", IsAnsi = true, Length = 50 },
                iprj = taken > 0 ? wIprj / taken : 0m,
                oprc = taken > 0 ? wOprc / taken : 0m,
                iprc = taken > 0 ? wIprc / taken : 0m,
                kc = qty,
                depotno = new DbString { Value = depotno ?? "01", IsAnsi = true, Length = 10 },
            }, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(
                "UPDATE tbisto SET kcamount=@kc WHERE id=@id",
                new { kc = head.Kc + qty, head.Id });
        }
    }

    public async Task DisassembleAsync(string tj, decimal qty, string cp, string? depotno, string dpm, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var head = await conn.QuerySingleOrDefaultAsync<(long Id, decimal Kc, string Dep)>(
            "SELECT id, kcamount, rtrim(depotno) FROM tbisto WITH (NOLOCK) WHERE rtrim(nno)=@tj AND rtrim(cp)=@cp" +
            (string.IsNullOrEmpty(depotno) ? "" : " AND rtrim(depotno)=@dep"),
            new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, dep = new DbString { Value = depotno ?? "", IsAnsi = true, Length = 10 } });
        if (head.Id == 0) throw new QPGL.Domain.Common.AppException("KIT_NOTSTOCK", "套件无库存！", 409);
        await conn.ExecuteAsync(
            "UPDATE tbisto SET kcamount=kcamount-@qty WHERE id=@id", new { qty, head.Id });
        var dep = string.IsNullOrEmpty(depotno) ? head.Dep : depotno;
        var lines = (await conn.QueryAsync<KitLineRow>(new CommandDefinition(
            "SELECT id, rtrim(tj) tj, rtrim(nno) nno, rtrim(na1) na1, rtrim(ty) ty, rtrim(fa) fa, rtrim(dc) dc, rtrim(unit) unit, num, iprj, rtrim(note) note, 0 kcamou FROM tbprtj WITH (NOLOCK) WHERE rtrim(tj)=@tj AND NOT (nno='' OR nno IS NULL) AND rtrim(cp)=@cp ORDER BY id",
            new { tj = new DbString { Value = tj, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } },
            cancellationToken: ct))).AsList();
        foreach (var l in lines)
        {
            var add = qty * l.Num;
            var stk = await conn.QuerySingleOrDefaultAsync<(long Id, decimal Kc)>(
                "SELECT id, kcamount FROM tbisto WITH (NOLOCK) WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp AND rtrim(depotno)=@dep AND @p_dpmusem LIKE '%'+rtrim(dpm)+'%'",
                new { nno = new DbString { Value = l.Nno, IsAnsi = true, Length = 18 }, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, dep = new DbString { Value = dep, IsAnsi = true, Length = 10 }, p_dpmusem = new DbString { Value = dpmusem ?? "", IsAnsi = true, Length = 60 } });
            if (stk.Id == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO tbisto (dpm, cp, nno, na1, ty, fa, dc, note, iprj, kcamount, depotno)
                    VALUES (@dpm, @cp, @nno, @na1, @ty, @fa, @dc, @note, @iprj, @kc, @dep)
                    """, new
                {
                    dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                    cp = new DbString { Value = cp, IsAnsi = true, Length = 10 },
                    nno = new DbString { Value = l.Nno, IsAnsi = true, Length = 18 },
                    na1 = new DbString { Value = l.Na1, IsAnsi = true, Length = 40 },
                    ty = new DbString { Value = l.Ty, IsAnsi = true, Length = 14 },
                    fa = new DbString { Value = l.Fa, IsAnsi = true, Length = 8 },
                    dc = new DbString { Value = l.Dc, IsAnsi = true, Length = 8 },
                    note = new DbString { Value = l.Note, IsAnsi = true, Length = 50 },
                    l.Iprj,
                    kc = add,
                    dep = new DbString { Value = dep, IsAnsi = true, Length = 10 },
                }, cancellationToken: ct));
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE tbisto SET kcamount=kcamount+@add WHERE id=@id", new { add, stk.Id });
            }
        }
    }
}

public sealed class ComplexRepository(SqlConnectionFactory factory) : IComplexRepository
{
    public async Task<IReadOnlyList<ComplexRow>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<ComplexRow>(new CommandDefinition("""
            SELECT rtrim(code_ts) code_ts, rtrim(g_name) g_name, low_rate, high_rate, rtrim(unit_1) unit_1, rtrim(note_s) note_s
            FROM tbcomplex WITH (NOLOCK) ORDER BY code_ts
            """, cancellationToken: ct))).AsList();
    }

    public async Task UpdateLowRateAsync(string codeTs, decimal lowRate, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbcomplex SET low_rate=@lr WHERE rtrim(code_ts)=@c",
            new { lr = lowRate, c = new DbString { Value = codeTs, IsAnsi = true, Length = 12 } }, cancellationToken: ct));
    }
}

public sealed class DcCoefRepository(SqlConnectionFactory factory) : IDcCoefRepository
{
    public async Task<IReadOnlyList<(string Dc, decimal Coeffict)>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<(string Dc, decimal Coeffict)>(
            new CommandDefinition("SELECT rtrim(dc), coeffict FROM tbdccoef WITH (NOLOCK) ORDER BY dc", cancellationToken: ct))).AsList();
    }

    public async Task SaveAsync(bool isAdd, string dc, decimal coeffict, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (isAdd)
        {
            if (await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT CASE WHEN EXISTS (SELECT 1 FROM tbdccoef WITH (NOLOCK) WHERE UPPER(rtrim(dc))=UPPER(@dc)) THEN 1 ELSE 0 END",
                    new { dc = new DbString { Value = dc, IsAnsi = true, Length = 8 } }, cancellationToken: ct)) > 0)
                throw new QPGL.Domain.Common.AppException("DC_DUP", "分类码重复!", 409);
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tbdccoef (dc, coeffict) VALUES (@dc, @cf)",
                new { dc = new DbString { Value = dc, IsAnsi = true, Length = 8 }, cf = coeffict }, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbdccoef SET coeffict=@cf WHERE rtrim(dc)=@dc",
                new { cf = coeffict, dc = new DbString { Value = dc, IsAnsi = true, Length = 8 } }, cancellationToken: ct));
        }
    }

    public async Task DeleteAsync(string dc, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbdccoef WHERE rtrim(dc)=@dc", new { dc = new DbString { Value = dc, IsAnsi = true, Length = 8 } }, cancellationToken: ct));
    }
}

public sealed class SysDefineRepository(SqlConnectionFactory factory) : ISysDefineRepository
{
    public async Task<(string DpmName, IReadOnlyList<(string Name, string Valuec)> Items)> GetCompanyAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var name = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT TOP 1 dpm FROM tbdpm WITH (NOLOCK) WHERE dpmbh=@dpm", new { dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } }, cancellationToken: ct)) ?? "";
        var rows = await conn.QueryAsync<(string Name, string Valuec)>(new CommandDefinition(
            "SELECT rtrim(name), rtrim(valuec) FROM tbsysset WITH (NOLOCK) WHERE type='system' AND dpm=@dpm AND name IN ('电话','传真','公司名称','地址','开户银行','帐号')",
            new { dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } }, cancellationToken: ct));
        return (name?.Trim() ?? "", rows.AsList());
    }

    public async Task SaveCompanyAsync(string dpm, IReadOnlyList<(string Name, string Valuec)> items, string dpmName, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbdpm SET dpm=@n WHERE dpmbh=@dpm", new { n = new DbString { Value = dpmName, IsAnsi = false, Length = 20 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } }, cancellationToken: ct));
        foreach (var (name, valuec) in items)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsysset SET valuec=@v WHERE name=@n AND type='system' AND dpm=@dpm",
                new { v = new DbString { Value = valuec ?? "", IsAnsi = true, Length = 40 }, n = new DbString { Value = name, IsAnsi = true, Length = 40 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CpRow>> GetCpsAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<CpRow>(new CommandDefinition("""
            SELECT rtrim(name) name, rtrim(valuec) valuec, rtrim(note) note, rtrim(cxnno) cxnno, ISNULL(valuen,0) valuen
            FROM tbsysset WITH (NOLOCK) WHERE type='cp' AND dpm=@dpm ORDER BY id
            """, new { dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } }, cancellationToken: ct))).AsList();
    }

    public async Task SaveCpsAsync(string dpm, IReadOnlyList<CpRow> rows, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tran = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tbsysset WHERE type='cp' AND dpm=@dpm", new { dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } }, tran, cancellationToken: ct));
            foreach (var r in rows)
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO tbsysset (name, type, valuen, valuec, valuel, valued, note, cxnno, dpm)
                    VALUES (@name, 'cp', @valuen, @valuec, 0, '1900-01-01', @note, @cxnno, @dpm)
                    """, new
                {
                    name = new DbString { Value = r.Name, IsAnsi = true, Length = 40 },
                    r.Valuen,
                    valuec = new DbString { Value = r.Valuec ?? "", IsAnsi = true, Length = 40 },
                    note = new DbString { Value = r.Note ?? "", IsAnsi = true, Length = 50 },
                    cxnno = new DbString { Value = r.Cxnno ?? "", IsAnsi = true, Length = 18 },
                    dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                }, tran, cancellationToken: ct));
            tran.Commit();
        }
        catch
        {
            await tran.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<SysSetRow>> GetDictAsync(string dpm, string? type, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var sql = "SELECT id, rtrim(name) name, rtrim(type) type, rtrim(valuec) valuec, valuen, valuel, rtrim(note) note, rtrim(xtype) xtype, rtrim(dpm) dpm FROM tbsysset WITH (NOLOCK) WHERE dpm=@dpm";
        if (!string.IsNullOrEmpty(type)) sql += " AND type=@t";
        sql += " ORDER BY type, id";
        return (await conn.QueryAsync<SysSetRow>(new CommandDefinition(sql,
            new { dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 }, t = type }, cancellationToken: ct))).AsList();
    }

    public async Task SaveDictAsync(bool isAdd, int id, string type, string name, string? valuec, decimal? valuen, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (isAdd)
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tbsysset (name, type, valuen, valuec, dpm) VALUES (@n, @t, @vn, @vc, @dpm)",
                new
                {
                    n = new DbString { Value = name, IsAnsi = true, Length = 40 },
                    t = new DbString { Value = type, IsAnsi = true, Length = 10 },
                    vn = valuen ?? 0m,
                    vc = new DbString { Value = valuec ?? "", IsAnsi = true, Length = 40 },
                    dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                }, cancellationToken: ct));
        else
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsysset SET name=@n, valuen=@vn, valuec=@vc WHERE id=@id",
                new
                {
                    n = new DbString { Value = name, IsAnsi = true, Length = 40 },
                    vn = valuen ?? 0m,
                    vc = new DbString { Value = valuec ?? "", IsAnsi = true, Length = 40 },
                    id,
                }, cancellationToken: ct));
    }

    public async Task DeleteDictAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM tbsysset WHERE id=@id", new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SysSetRow>> GetParasAsync(IReadOnlyList<(string Name, string Type, char Kind, string Sysname, string Xtype)> defs, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var result = new List<SysSetRow>();
        foreach (var d in defs)
        {
            // findrecord：先补 xtype（无行→整行 INSERT）
            var row = await conn.QuerySingleOrDefaultAsync<SysSetRow>(new CommandDefinition(
                "SELECT TOP 1 id, rtrim(name) name, rtrim(type) type, rtrim(valuec) valuec, valuen, valuel, rtrim(note) note, rtrim(xtype) xtype, rtrim(dpm) dpm FROM tbsysset WITH (NOLOCK) WHERE name=@n AND type=@t AND dpm=@dpm",
                new { n = new DbString { Value = d.Name, IsAnsi = true, Length = 40 }, t = new DbString { Value = d.Type, IsAnsi = true, Length = 10 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } },
                cancellationToken: ct));
            if (row is null)
            {
                var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "INSERT INTO tbsysset (name, type, dpm, xtype) VALUES (@n, @t, @dpm, @x); SELECT CAST(SCOPE_IDENTITY() AS int);",
                    new { n = new DbString { Value = d.Name, IsAnsi = true, Length = 40 }, t = new DbString { Value = d.Type, IsAnsi = true, Length = 10 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 }, x = new DbString { Value = d.Xtype ?? "", IsAnsi = true, Length = 10 } },
                    cancellationToken: ct));
                row = new SysSetRow { Id = id, Name = d.Name, Type = d.Type, Xtype = d.Xtype ?? "", Dpm = dpm, Valuec = "", Note = "" };
            }
            else if (string.IsNullOrEmpty(row.Xtype))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbsysset SET xtype=@x WHERE id=@id",
                    new { x = new DbString { Value = d.Xtype ?? "", IsAnsi = true, Length = 10 }, row.Id }, cancellationToken: ct));
                row.Xtype = d.Xtype ?? "";
            }
            result.Add(row);
        }
        return result;
    }

    public async Task SaveParasAsync(IReadOnlyList<(int Id, char Kind, string Sysname, string? ValueC, decimal? ValueN, bool? ValueL)> items, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        foreach (var it in items)
        {
            var col = it.Kind switch
            {
                'L' => "valuel",
                'N' => "valuen",
                _ => "valuec",
            };
            if (it.Sysname == "销售") col = "note"; // SYSNAME='销售' 写 note（type='xs' 行）
            if (col == "valuel")
                await conn.ExecuteAsync("UPDATE tbsysset SET valuel=@v WHERE id=@id", new { v = it.ValueL ?? false, it.Id });
            else if (col == "valuen")
                await conn.ExecuteAsync("UPDATE tbsysset SET valuen=@v WHERE id=@id", new { v = it.ValueN ?? 0m, it.Id });
            else
                await conn.ExecuteAsync(new CommandDefinition(
                    $"UPDATE tbsysset SET {col}=@v WHERE id=@id",
                    new { v = new DbString { Value = it.ValueC ?? "", IsAnsi = true, Length = 254 }, it.Id }, cancellationToken: ct));
        }
    }
}

/// <summary>A3 属性传递（fr_sxcd row14，契约 §3）。</summary>
public sealed class AttrTransferRepository(SqlConnectionFactory factory) : IAttrTransferRepository
{
    private static readonly string[] TargetTables = ["isto", "istosm", "isorsm", "order", "sada"];

    public async Task<IReadOnlyList<AttrFieldRow>> GetFieldsAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<AttrFieldRow>(new CommandDefinition("""
            SELECT rtrim(f.field_name) field_name, rtrim(f.explain) explain, CONVERT(bit, 1) is_cd
            FROM tbstru f WITH (NOLOCK)
            WHERE f.table_name='tbprnoty' AND f.field_name<>'id' AND f.field_name<>'cp' AND f.field_name<>'nno'
              AND f.field_name IN (SELECT field_name FROM tbstru WITH (NOLOCK) WHERE table_name='tbisto')
            ORDER BY f.field_name
            """, cancellationToken: ct))).AsList();
        // FA 按"库存是否分产地"（=F. → FA 传递）；IPRJ/OPRC 强制不勾（含追加行 OPRC）
        var xzfa = await conn.ExecuteScalarAsync<bool?>(
            "SELECT valuel FROM tbsysset WITH (NOLOCK) WHERE type='system' AND name='库存是否分产地'") ?? false;
        foreach (var r in rows)
        {
            if (r.Field_name.Equals("fa", StringComparison.OrdinalIgnoreCase)) r.Is_cd = !xzfa;
            if (r.Field_name.Equals("iprj", StringComparison.OrdinalIgnoreCase) ||
                r.Field_name.Equals("oprc", StringComparison.OrdinalIgnoreCase)) r.Is_cd = false;
        }
        return rows;
    }

    public async Task<AttrTransferResult> TransferAsync(IReadOnlyList<string> fields, IReadOnlyList<string> targets, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var updated = new List<(string Table, string Field, int Rows)>();
        foreach (var t in targets)
        {
            var tbl = "tb" + t;
            var setParts = new List<string>();
            var whereParts = new List<string>();
            foreach (var f in fields)
            {
                var fld = f.Trim();
                // 字段须存在于目标表（旧码 PASSTH WHERE id=-1 探测）
                var exists = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(@t) AND name=@c",
                    new { t = tbl, c = fld });
                if (exists == 0) continue;
                if (fld.Equals("iprj", StringComparison.OrdinalIgnoreCase))
                {
                    setParts.Add("iprj=(CASE WHEN ISNULL(p.iprj,0)=0 THEN t.iprj ELSE p.iprj END)");
                    whereParts.Add("p.iprj<>t.iprj"); // 旧码 NOT p.iprj=t.iprj（NULL→UNKNOWN 不更新）
                }
                else if (fld.Equals("oprc", StringComparison.OrdinalIgnoreCase))
                {
                    setParts.Add("oprc=(CASE WHEN ISNULL(p.oprc_cp,0)=0 THEN t.oprc ELSE p.oprc_cp END)");
                    whereParts.Add("p.oprc_cp<>t.oprc");
                }
                else
                {
                    setParts.Add($"{fld}=p.{fld}");
                    whereParts.Add($"NOT p.{fld}=t.{fld}");
                }
                updated.Add((tbl, fld, 0));
            }
            if (setParts.Count == 0) continue;
            var join = "ON t.cp=p.cp AND t.nno=p.nno"; // P_XZFA=.F. 不带 fa
            var sql = $"UPDATE {tbl} SET {string.Join(", ", setParts)} FROM {tbl} t LEFT JOIN tbprnoty p {join} WHERE t.cp=p.cp AND t.nno=p.nno AND ({string.Join(" OR ", whereParts)})";
            var n = await conn.ExecuteAsync(sql);
            updated = updated.Select(u => u.Table == tbl ? (u.Table, u.Field, n) : u).ToList();
        }
        return new AttrTransferResult(updated);
    }
}
