using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Sales;

public interface IOrsaRepository
{
    /// <summary>其他公司报价查询（fr_orsa setsource：PASSTH select tbqtgs WHERE 用户条件 ORDER BY nno,oprc）。</summary>
    Task<IReadOnlyList<OrsaRow>> SearchAsync(string where, CancellationToken ct = default);

    /// <summary>新增报价（fr_orsa row58 EMODE=0：查重 cp+gno+nno → INSERT，cp=p_cp、updadate=GETDATE()）。</summary>
    Task<OrsaRow> CreateAsync(OrsaSaveCommand command, string cp, CancellationToken ct = default);

    /// <summary>编辑报价（row58 EMODE=1：UPDATE tbqtgs SET qtamount,oprc WHERE id）。</summary>
    Task UpdateAsync(int id, decimal qtamount, decimal oprc, CancellationToken ct = default);

    /// <summary>删除单条（row7：DELETE tbqtgs WHERE id）。</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>按供应商整删（row51：DELETE tbqtgs WHERE gname）。</summary>
    Task<int> DeleteByGnameAsync(string gname, CancellationToken ct = default);

    /// <summary>明细面板（row29 AfterRowColChange）：本公司库存（tbisto cp+nno，gname='本公司库存'）+ 同件全部报价（tbqtgs nno）。</summary>
    Task<IReadOnlyList<OrsaDetailRow>> DetailAsync(string cp, string nno, CancellationToken ct = default);
}

/// <summary>tbqtgs 报价行（fr_orsa GRDCX 9 列）。</summary>
public sealed class OrsaRow
{
    public int Id { get; set; }
    public string Gno { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Oprc { get; set; }
    public decimal Qtamount { get; set; }
    public DateTime Updadate { get; set; }
    public string Ty { get; set; } = "";
    public decimal Iprj { get; set; }
}

/// <summary>明细面板行（本公司库存行 + 其他公司报价行合并）。</summary>
public sealed class OrsaDetailRow
{
    public string Gno { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Oprc { get; set; }
    public decimal Qtamount { get; set; }
    public DateTime? Updadate { get; set; }
    public string Ty { get; set; } = "";
    public decimal Iprj { get; set; }
    /// <summary>true=本公司库存行（tbisto），false=其他公司报价行（tbqtgs）。</summary>
    public bool IsLocalStock { get; set; }
}

/// <summary>报价保存命令（row58 TXTGNO/TXTNNO/TXTOPRC/TXTQTAMOUNT）。</summary>
public sealed class OrsaSaveCommand
{
    public required string Gno { get; set; }
    public required string Gname { get; set; }
    public required string Nno { get; set; }
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public decimal Oprc { get; set; }
    public decimal Qtamount { get; set; }
}

/// <summary>
/// 其他公司报价仓储（fr_orsa，销售管理 BAR8；tbqtgs 单表 CRUD，无 dpm 列——跨公司共享报价档案）。
/// tbqtgs 基线 0 行（部署实测）；沙河丰田变体（FRMEDIT 按钮隐藏）休眠。
/// </summary>
public sealed class OrsaRepository(SqlConnectionFactory factory) : IOrsaRepository
{
    private const string Columns = "id, rtrim(gno) gno, rtrim(gname) gname, rtrim(nno) nno, rtrim(na1) na1, oprc, qtamount, updadate, rtrim(ty) ty, isnull(iprj,0) iprj";

    public async Task<IReadOnlyList<OrsaRow>> SearchAsync(string where, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var w = string.IsNullOrWhiteSpace(where) ? "1=1" : where;
        var rows = await conn.QueryAsync<OrsaRow>(new CommandDefinition(
            $"SELECT {Columns} FROM tbqtgs WITH (NOLOCK) WHERE ({w}) ORDER BY nno, oprc",
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<OrsaRow> CreateAsync(OrsaSaveCommand command, string cp, CancellationToken ct = default)
    {
        var gno = command.Gno.Trim();
        var gname = command.Gname.Trim();
        var nno = command.Nno.Trim();
        if (gno.Length == 0 && gname.Length == 0)
            throw new AppException("ORSA_GNAME_REQUIRED", "请输入单位名称！", 422);
        if (nno.Length == 0)
            throw new AppException("ORSA_NNO_REQUIRED", "请输入零件编码！", 422);

        await using var conn = await factory.OpenAsync(ct);
        // row58 查重：cp+gno+nno → "该单位的零件X已有报价！"
        var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbqtgs WITH (NOLOCK) WHERE cp = @cp AND gno = @gno AND nno = @nno",
            new { cp = Ansi(cp, 10), gno = Ansi(gno, 10), nno = Ansi(nno, 18) },
            cancellationToken: ct));
        if (dup > 0)
            throw new AppException("ORSA_DUPLICATE", $"该单位的零件 {nno} 已有报价！", 422);

        // tbprnoty 自动补 na1/ty（row61 LostFocus：编码失焦回填，禁手工编辑）
        var part = await conn.QuerySingleOrDefaultAsync<(string Na1, string Ty)>(new CommandDefinition(
            "SELECT TOP 1 rtrim(na1), rtrim(ty) FROM tbprnoty WITH (NOLOCK) WHERE cp = @cp AND nno = @nno",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) },
            cancellationToken: ct));
        var na1 = part.Na1.Length > 0 ? part.Na1 : command.Na1.Trim();
        var ty = part.Ty.Length > 0 ? part.Ty : command.Ty.Trim();

        var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO tbqtgs (cp, nno, gno, gname, na1, ty, oprc, qtamount, updadate)
            VALUES (@cp, @nno, @gno, @gname, @na1, @ty, @oprc, @qtamount, GETDATE());
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new
            {
                cp = Ansi(cp, 10),
                nno = Ansi(nno, 18),
                gno = Ansi(gno, 10),
                gname = Ansi(gname, 60),
                na1 = Ansi(na1, 20),
                ty = Ansi(ty, 14),
                oprc = command.Oprc,
                qtamount = command.Qtamount,
            },
            cancellationToken: ct));
        return new OrsaRow
        {
            Id = id,
            Gno = gno,
            Gname = gname,
            Nno = nno,
            Na1 = na1,
            Oprc = command.Oprc,
            Qtamount = command.Qtamount,
            Updadate = DateTime.Today,
            Ty = ty,
        };
    }

    public async Task UpdateAsync(int id, decimal qtamount, decimal oprc, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbqtgs SET qtamount = @qt, oprc = @oprc WHERE id = @id",
            new { qt = qtamount, oprc, id },
            cancellationToken: ct));
        if (n == 0)
            throw new AppException("ORSA_NOT_FOUND", $"报价记录 {id} 不存在", 422);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbqtgs WHERE id = @id",
            new { id },
            cancellationToken: ct));
    }

    public async Task<int> DeleteByGnameAsync(string gname, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbqtgs WHERE gname = @gname",
            new { gname = Ansi(gname.Trim(), 60) },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<OrsaDetailRow>> DetailAsync(string cp, string nno, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // row29：本公司库存（tbisto cp+nno，gname='本公司库存'，qtamount=kcamount）+ 同件全部报价（tbqtgs nno）
        // 旧码 APPEND FROM 顺序 = 先库存后报价；新侧 UNION ALL 同序（库存行无 updadate）
        var rows = await conn.QueryAsync<OrsaDetailRow>(new CommandDefinition(
            """
            SELECT rtrim('') gno, N'本公司库存' gname, rtrim(nno) nno, rtrim(na1) na1, oprc, kcamount qtamount,
                   CAST(NULL AS datetime) updadate, rtrim(ty) ty, iprj, CAST(1 AS bit) islocal
            FROM tbisto WITH (NOLOCK)
            WHERE cp = @cp AND nno = @nno
            UNION ALL
            SELECT rtrim(gno) gno, rtrim(gname) gname, rtrim(nno) nno, rtrim(na1) na1, oprc, qtamount,
                   updadate, rtrim(ty) ty, isnull(iprj,0) iprj, CAST(0 AS bit) islocal
            FROM tbqtgs WITH (NOLOCK)
            WHERE nno = @nno
            """,
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) },
            cancellationToken: ct));
        return rows.AsList();
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value, IsAnsi = true, Length = length };
}
