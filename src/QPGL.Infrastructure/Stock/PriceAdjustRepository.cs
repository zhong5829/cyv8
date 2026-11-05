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
// B5：2026-11-04 改版（QP11 库存预警同构）——废弃 tbrtsamo×系数公式批量，改为手动逐行编辑
//     tbistojj 双档警戒值（amjj/bmjj），列表=未设警戒或已低于警戒的配件，编辑即落库。
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

/// <summary>
/// 警戒线行（B5 手动编辑，2026-11-04 改版）。tbistojj 双档：amjj=A 警戒值 / bmjj=B 警戒值，
/// 库存=k.camount（tbisto 按分店+配件汇总，排除废品仓位）。
/// 分级提示（2026-11-04）：库存&lt;A → 黄字预警；&lt;B → 红字告急（红优先）。
/// </summary>
public sealed class JjLineRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Posi { get; set; } = "";
    public decimal Oprc { get; set; }

    private decimal _stock;
    /// <summary>当前库存（汇总）。</summary>
    public decimal Stock { get => _stock; set { _stock = value; OnChanged(); RefreshLevel(); } }

    private decimal _amjj;
    /// <summary>A 警戒值（可编辑，黄档）。</summary>
    public decimal Amjj { get => _amjj; set { _amjj = value; OnChanged(); RefreshLevel(); } }

    private decimal _bmjj;
    /// <summary>B 警戒值（可编辑，红档）。</summary>
    public decimal Bmjj { get => _bmjj; set { _bmjj = value; OnChanged(); RefreshLevel(); } }

    private bool _isYellow;
    /// <summary>库存 &lt; A 警戒值 → 黄字预警。</summary>
    public bool IsYellow { get => _isYellow; private set { _isYellow = value; OnChanged(); } }

    private bool _isRed;
    /// <summary>库存 &lt; B 警戒值 → 红字告急（同时低于双档时红色优先）。</summary>
    public bool IsRed { get => _isRed; private set { _isRed = value; OnChanged(); } }

    private void RefreshLevel()
    {
        var red = Bmjj > 0 && Stock < Bmjj;
        IsRed = red;
        IsYellow = !red && Amjj > 0 && Stock < Amjj;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));
}

public interface IPriceAdjustRepository
{
    Task<IReadOnlyList<IstoTjRow>> GetIstoAsync(string? depotno, string? dcFilter, CancellationToken ct = default);
    Task<int> AdjustAsync(PriceAdjustCommand cmd, string cp, string dpm, CancellationToken ct = default);

    /// <summary>警戒线清单（B5，2026-11-04 改手动编辑）：当前厂牌配件 + 汇总库存 + 双档警戒值，
    /// 仅列 未设警戒 或 库存≤警戒值 的行（库存升序，QP11 库存预警同语义）。</summary>
    Task<IReadOnlyList<JjLineRow>> GetJjLinesAsync(string cp, string dpm, CancellationToken ct = default);

    /// <summary>保存单行警戒值（null=不改该档）；有行 UPDATE / 无行 INSERT（任一档>0）；
    /// 双档都为 0 → 删行（旧 fr_istojj 收尾语义）。</summary>
    Task<int> UpdateJjLineAsync(string dpm, string cp, string nno, decimal? amjj, decimal? bmjj, CancellationToken ct = default);
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

    /// <summary>
    /// 警戒线清单（B5 手动编辑，2026-11-04 改版；QP11 GetStockAlertItemsAsync 同语义）：
    /// tbprnoty（当前厂牌）LEFT JOIN tbisto 汇总库存（排除废品仓位）/ tbistojj 双档警戒值（depotno='' 行），
    /// 仅列 未设警戒（双 0）或 库存≤任一已设警戒值 的行，库存升序。
    /// </summary>
    public async Task<IReadOnlyList<JjLineRow>> GetJjLinesAsync(string cp, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 120s：tbprnoty 全厂牌清单 × tbisto 汇总/最新批次聚合，大数据量下默认 30s 会超时
        var rows = (await conn.QueryAsync<JjLineRow>(new CommandDefinition(
            """
            DECLARE @fpcw varchar(10) = ISNULL(NULLIF((SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE name = N'废品仓位标识' AND type = 'system'), ''), N'废');
            SELECT rtrim(p.cp) cp, rtrim(p.nno) nno, rtrim(p.na1) na1, rtrim(ISNULL(p.ty,'')) ty, rtrim(ISNULL(p.unit,'')) unit,
                   ISNULL(k.kcamount, 0) stock, ISNULL(j.amjj, 0) amjj, ISNULL(j.bmjj, 0) bmjj,
                   ISNULL(i.oprc, 0) oprc, rtrim(ISNULL(i.posi,'')) posi
            FROM tbprnoty p WITH (NOLOCK)
            LEFT JOIN (SELECT cp, nno, SUM(kcamount) kcamount FROM tbisto WITH (NOLOCK)
                       WHERE dpm = @dpm AND rtrim(ISNULL(posi,'')) <> @fpcw GROUP BY cp, nno) k
                   ON rtrim(k.cp) = rtrim(p.cp) AND rtrim(k.nno) = rtrim(p.nno)
            LEFT JOIN tbistojj j WITH (NOLOCK)
                   ON rtrim(j.cp) = rtrim(p.cp) AND rtrim(j.nno) = rtrim(p.nno) AND j.dpm = @dpm AND rtrim(ISNULL(j.depotno,'')) = ''
            LEFT JOIN (SELECT cp, nno, MAX(id) maxid FROM tbisto WITH (NOLOCK) WHERE dpm = @dpm GROUP BY cp, nno) im
                   ON rtrim(im.cp) = rtrim(p.cp) AND rtrim(im.nno) = rtrim(p.nno)
            LEFT JOIN tbisto i WITH (NOLOCK) ON i.id = im.maxid
            WHERE rtrim(p.cp) = @cp
              AND ((ISNULL(j.amjj,0) = 0 AND ISNULL(j.bmjj,0) = 0)
                   OR (ISNULL(j.amjj,0) > 0 AND ISNULL(k.kcamount,0) <= j.amjj)
                   OR (ISNULL(j.bmjj,0) > 0 AND ISNULL(k.kcamount,0) <= j.bmjj))
            ORDER BY ISNULL(k.kcamount, 0)
            """, new { cp = Ansi(cp, 10), dpm = Ansi(dpm, 10) }, commandTimeout: 120, cancellationToken: ct))).AsList();
        return rows;
    }

    /// <summary>
    /// 保存单行警戒值（B5 手动编辑，2026-11-04 改版）：有 tbistojj 行（depotno=''）→ UPDATE（双 0 删行）；
    /// 无行且任一档 >0 → INSERT（na1/ty 取 tbprnoty，旧 V_.row20 INSERT 字段口径）。
    /// </summary>
    public async Task<int> UpdateJjLineAsync(string dpm, string cp, string nno, decimal? amjj, decimal? bmjj, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var existing = await conn.QueryFirstOrDefaultAsync<(int id, decimal amjj, decimal bmjj)>(new CommandDefinition(
                "SELECT TOP (1) id, ISNULL(amjj,0) amjj, ISNULL(bmjj,0) bmjj FROM tbistojj WITH (UPDLOCK) " +
                "WHERE dpm = @dpm AND rtrim(cp) = @cp AND rtrim(nno) = @nno AND rtrim(ISNULL(depotno,'')) = ''",
                new { dpm = Ansi(dpm, 10), cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, transaction: tx, cancellationToken: ct));
            var newA = amjj ?? existing.amjj;
            var newB = bmjj ?? existing.bmjj;
            var affected = 0;
            if (existing.id != 0)
            {
                affected = newA == 0 && newB == 0
                    ? await conn.ExecuteAsync(new CommandDefinition("DELETE FROM tbistojj WHERE id = @id",
                        new { id = existing.id }, transaction: tx, cancellationToken: ct))
                    : await conn.ExecuteAsync(new CommandDefinition("UPDATE tbistojj SET amjj = @a, bmjj = @b WHERE id = @id",
                        new { a = newA, b = newB, id = existing.id }, transaction: tx, cancellationToken: ct));
            }
            else if (newA > 0 || newB > 0)
            {
                affected = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbistojj (dpm, cp, nno, na1, ty, amjj, bmjj)
                    SELECT @dpm, @cp, @nno, rtrim(ISNULL(p.na1,'')), rtrim(ISNULL(p.ty,'')), @a, @b
                    FROM tbprnoty p WITH (NOLOCK) WHERE rtrim(p.cp) = @cp AND rtrim(p.nno) = @nno
                    """, new { dpm = Ansi(dpm, 10), cp = Ansi(cp, 10), nno = Ansi(nno, 18), a = newA, b = newB },
                    transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return affected;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}
