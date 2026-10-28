using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Stock;

// =====================================================================================
// 库存第十批（B4 库存调价 / B5 警戒线设置）—— 契约：开发文档/契约摘录/库存第十批契约.md
// B4：tbisto 整批 UPDATE（目标字段 oprc/oprb/iprc/iprj = 基准表达式±*/系数，CONVERT(int,x+0.5/+0.99) 取整；
//     库存基准价 / 供应商基准价（tbgys.iprj，nno+cp 关联 + READXZB 数据权限）。
// B5：tbistojj 警戒线（数据源 tbrtsamo 月销售数 × 系数 / 月 → ROUND(,0)；UPDATE 有行 / INSERT 双系数>0；尾部清 0 行）。
// =====================================================================================

/// <summary>库存调价行（tbisto 展示列）。</summary>
public sealed class IstoTjRow
{
    public int Id { get; set; }
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Kcamount { get; set; }
    public string Unit { get; set; } = "";
    public decimal Iprc { get; set; }
    public decimal Iprj { get; set; }
    public decimal Oprc { get; set; }
    public string Dc { get; set; } = "";
    public string Depotno { get; set; } = "";
}

/// <summary>调价命令（fr_istotj row121）。</summary>
public sealed class PriceAdjustCommand
{
    public int FieldKind { get; set; }        // 1=oprc 2=oprb 3=iprc 4=iprj
    public int BaseKind { get; set; }         // 1=oprc 2=iprj 3=iprc 4=oprb
    public string Oper { get; set; } = "+";   // + - * /
    public decimal Factor { get; set; }
    public int RoundMode { get; set; }        // 1=不取整 2=int(x+0.5) 3=int(x+0.99)
    public bool BySupplier { get; set; }      // OPG1=2 供应商基准价
    public string Gyname { get; set; } = "";
    public string? Depotno { get; set; }
    public string? Dc { get; set; }
    public string? DcCon { get; set; }        // 且/或
    public string? Ty { get; set; }
    /// <summary>主窗筛选 WHERE（列带 isto. 前缀，应用层白名单拼装）。</summary>
    public string ExtraWhere { get; set; } = "";
}

/// <summary>警戒线设置命令（fr_istojj V_.row20）。</summary>
public sealed class JjLineCommand
{
    public int Months { get; set; }           // 最近 N 月
    public decimal FactorA { get; set; }      // MS0（CHK2 启用 amjj）
    public decimal FactorB { get; set; }      // MS1（CHK3 启用 bmjj）
    public bool SetAmjj { get; set; }
    public bool SetBmjj { get; set; }
    public bool AllBrand { get; set; }        // 所有厂牌（cp<>'急件'）
    public string Cp { get; set; } = "";      // 当前厂牌（AllBrand=false 时启用）
    public string? Fa { get; set; }
    public string? Dc { get; set; }
    public string? Ty { get; set; }
}

public interface IPriceAdjustRepository
{
    Task<IReadOnlyList<IstoTjRow>> GetIstoAsync(string? depotno, string? dcFilter, CancellationToken ct = default);
    Task<int> AdjustAsync(PriceAdjustCommand cmd, string cp, string dpm, CancellationToken ct = default);
    Task<int> SetJjLinesAsync(JjLineCommand cmd, string dpm, CancellationToken ct = default);
}

public sealed class PriceAdjustRepository(SqlConnectionFactory factory) : IPriceAdjustRepository
{
    private static readonly string[] FieldNames = ["", "oprc", "oprb", "iprc", "iprj"];
    private static readonly string[] BaseNames = ["", "oprc", "iprj", "iprc", "oprb"];

    /// <summary>库存列表（tbisto，ORDER BY depotno+nno）。</summary>
    public async Task<IReadOnlyList<IstoTjRow>> GetIstoAsync(string? depotno, string? dcFilter, CancellationToken ct = default)
    {
        var cond = "1 = 1";
        if (!string.IsNullOrWhiteSpace(depotno)) cond += " AND rtrim(depotno) = @depotno";
        if (!string.IsNullOrWhiteSpace(dcFilter)) cond += " AND rtrim(dc) = @dc";
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<IstoTjRow>(new CommandDefinition(
            $"""
            SELECT id, rtrim(cp) cp, rtrim(nno) nno, rtrim(ISNULL(na1,'')) na1, ISNULL(kcamount,0) kcamount, rtrim(ISNULL(unit,'')) unit,
                   ISNULL(iprc,0) iprc, ISNULL(iprj,0) iprj, ISNULL(oprc,0) oprc, rtrim(ISNULL(dc,'')) dc, rtrim(ISNULL(depotno,'')) depotno
            FROM tbisto WITH (NOLOCK) WHERE {cond} ORDER BY rtrim(depotno), rtrim(nno)
            """,
            new { depotno = Ansi(depotno ?? "", 10), dc = Ansi(dcFilter ?? "", 8) }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>整批调价（row121）：目标字段 = 基准表达式（±*/系数 [+取整]），返回受影响行数。</summary>
    public async Task<int> AdjustAsync(PriceAdjustCommand cmd, string cp, string dpm, CancellationToken ct = default)
    {
        // 条件校验（row121 L46-53：dc/ty/连接符不能只缺中间）
        var hasDc = !string.IsNullOrWhiteSpace(cmd.Dc);
        var hasTy = !string.IsNullOrWhiteSpace(cmd.Ty);
        var hasCon = !string.IsNullOrWhiteSpace(cmd.DcCon);
        if (hasDc && hasTy && !hasCon) throw new AppException("COND_INVALID", "条件错误", 400);
        if (!hasDc && !hasTy && hasCon) throw new AppException("COND_INVALID", "条件错误", 400);
        var fieldName = FieldNames[cmd.FieldKind];
        var baseName = BaseNames[cmd.BaseKind];
        var factor = cmd.Factor.ToString("0.####", CultureInfo.InvariantCulture);
        var value = $"{baseName} {cmd.Oper} {factor}";
        if (cmd.RoundMode == 2) value = $"CONVERT(int, {value} + 0.5)";
        else if (cmd.RoundMode == 3) value = $"CONVERT(int, {value} + 0.99)";
        // 条件拼装（isto. 前缀固定列 + 主窗白名单 WHERE）
        var condition = "";
        if (hasDc) condition += $" isto.dc = '{cmd.Dc!.Trim().Replace("'", "''")}'";
        if (hasCon && hasDc && hasTy) condition += $" {cmd.DcCon!.Trim()}";
        if (hasTy) condition += $" isto.ty = '{cmd.Ty!.Trim().Replace("'", "''")}'";
        condition = condition.Trim();
        if (condition.StartsWith("and", StringComparison.OrdinalIgnoreCase)) condition = condition[3..].Trim();
        if (condition.EndsWith("and", StringComparison.OrdinalIgnoreCase)) condition = condition[..^3].Trim();
        var where = cmd.ExtraWhere.Trim();
        if (condition.Length > 0) where = (where.Length > 0 ? where + " AND " : "") + "(" + condition + ")";
        if (!string.IsNullOrWhiteSpace(cmd.Depotno))
            where = (where.Length > 0 ? where + " AND " : "") + $"rtrim(isto.depotno) = '{cmd.Depotno!.Trim().Replace("'", "''")}'";
        if (cmd.BySupplier)
        {
            // 供应商基准价：表达式内基准列→gys.iprj，nno+cp 关联 tbgys（数据权限 READXZB 备案：tbxzb 自动过滤按用户权限，回放种子无权限行=不过滤）
            var gysValue = value.Replace(baseName, "gys.iprj");
            var sql = $"""
                UPDATE isto SET {fieldName} = {gysValue}
                FROM tbisto isto, tbgys gys
                WHERE rtrim(isto.cp) = @cp AND {where}
                  AND rtrim(isto.nno) = rtrim(gys.nno) AND rtrim(isto.cp) = rtrim(gys.cp)
                  AND rtrim(gys.gyname) = @gyname AND isto.dpm = gys.dpm AND isto.dpm = @dpm
                """;
            await using var conn = await factory.OpenAsync(ct);
            return await conn.ExecuteAsync(new CommandDefinition(sql,
                new { cp = Ansi(cp, 10), gyname = Ansi(cmd.Gyname, 40), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        }
        var sql2 = $"""
            UPDATE isto SET {fieldName} = {value}
            FROM tbisto isto
            WHERE rtrim(isto.cp) = @cp AND {where} AND isto.dpm = @dpm
            """;
        await using var conn2 = await factory.OpenAsync(ct);
        return await conn2.ExecuteAsync(new CommandDefinition(sql2,
            new { cp = Ansi(cp, 10), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    /// <summary>警戒线设置（V_.row20）：tbrtsamo × 系数 → tbistojj UPDATE/INSERT + 尾部清 0 行。</summary>
    public async Task<int> SetJjLinesAsync(JjLineCommand cmd, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 数据源（fr_istojj：tbrtsamo LEFT JOIN tbprnoty，厂牌/产地/类型过滤）
        var cpCond = cmd.AllBrand ? "rtrim(a.cp) <> '急件'" : "rtrim(a.cp) = @cp0";
        var extra = "";
        if (!string.IsNullOrWhiteSpace(cmd.Fa)) extra += $" AND rtrim(ISNULL(b.fa,'')) = '{cmd.Fa!.Trim().Replace("'", "''")}'";
        if (!string.IsNullOrWhiteSpace(cmd.Dc)) extra += $" AND rtrim(ISNULL(b.dc,'')) = '{cmd.Dc!.Trim().Replace("'", "''")}'";
        if (!string.IsNullOrWhiteSpace(cmd.Ty)) extra += $" AND ISNULL(b.ty,'') + ISNULL(b.note,'') LIKE '%{cmd.Ty!.Trim().Replace("'", "''")}%'";
        var months = cmd.Months;
        // oamou{m} 连加字段（最近 N 月，超 12 减 12——与旧码一致按当前月推算）
        var fields = "";
        for (var i = 0; i < months; i++)
        {
            var m = DateTime.Today.Month + 12 - (months - i);
            if (m > 12) m -= 12;
            fields += (fields.Length > 0 ? "+" : "") + $"ISNULL(oamou{m},0)";
        }
        var source = (await conn.QueryAsync<(string dpm, string cp, string nno, string na1, string ty)>(new CommandDefinition(
            $"""
            SELECT rtrim(a.dpm), rtrim(a.cp), rtrim(a.nno), rtrim(a.na1), rtrim(ISNULL(b.ty,''))
            FROM tbrtsamo a WITH (NOLOCK) LEFT JOIN tbprnoty b WITH (NOLOCK) ON rtrim(a.nno) = rtrim(b.nno) AND rtrim(a.cp) = rtrim(b.cp)
            WHERE {(string.IsNullOrWhiteSpace(dpm) ? "" : "a.dpm = @dpm AND ")}{cpCond} AND rtrim(a.nno) <> ''{extra}
            """, new { dpm = Ansi(dpm, 10), cp0 = Ansi(cmd.Cp, 10) }, cancellationToken: ct))).AsList();
        var affected = 0;
        foreach (var s in source)
        {
            var sumSql = $"SELECT ISNULL(ROUND(SUM({fields}) / @months * @factor, 0), 0) FROM tbrtsamo WITH (NOLOCK) WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm";
            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM tbistojj WITH (NOLOCK) WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm",
                new { nno = Ansi(s.nno, 18), cp = Ansi(s.cp, 10), dpm = Ansi(s.dpm, 10) }, cancellationToken: ct));
            if (exists > 0)
            {
                var set = "";
                if (cmd.SetAmjj)
                    set += "amjj = (SELECT ISNULL(ROUND(SUM(" + fields + ") / @months * @factorA, 0), 0) FROM tbrtsamo WITH (NOLOCK) WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm)";
                if (cmd.SetBmjj)
                    set += (set.Length > 0 ? ", " : "") + "bmjj = (SELECT ISNULL(ROUND(SUM(" + fields + ") / @months * @factorB, 0), 0) FROM tbrtsamo WITH (NOLOCK) WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm)";
                if (set.Length == 0) continue;
                affected += await conn.ExecuteAsync(new CommandDefinition(
                    $"UPDATE tbistojj SET {set} WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm AND rtrim(ISNULL(depotno,'')) = ''",
                    new { nno = Ansi(s.nno, 18), cp = Ansi(s.cp, 10), dpm = Ansi(s.dpm, 10), months, factorA = (double)cmd.FactorA, factorB = (double)cmd.FactorB },
                    cancellationToken: ct));
            }
            else
            {
                var amjj = cmd.SetAmjj
                    ? await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(sumSql,
                        new { nno = Ansi(s.nno, 18), cp = Ansi(s.cp, 10), dpm = Ansi(s.dpm, 10), months, factor = (double)cmd.FactorA }, cancellationToken: ct))
                    : 0m;
                var bmjj = cmd.SetBmjj
                    ? await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(sumSql,
                        new { nno = Ansi(s.nno, 18), cp = Ansi(s.cp, 10), dpm = Ansi(s.dpm, 10), months, factor = (double)cmd.FactorB }, cancellationToken: ct))
                    : 0m;
                if (amjj > 0 && bmjj > 0)
                    affected += await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO tbistojj (dpm, cp, nno, na1, ty, amjj, bmjj) VALUES (@dpm, @cp, @nno, @na1, @ty, @amjj, @bmjj)",
                        new
                        {
                            dpm = Ansi(s.dpm, 10), cp = Ansi(s.cp, 10), nno = Ansi(s.nno, 18),
                            na1 = Ansi(s.na1, 40), ty = Ansi(s.ty, 14), amjj, bmjj,
                        }, cancellationToken: ct));
            }
        }
        // 尾部清 0 行（旧码全表）
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbistojj WHERE ISNULL(amjj,0) = 0 AND ISNULL(bmjj,0) = 0", cancellationToken: ct));
        return affected;
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}
