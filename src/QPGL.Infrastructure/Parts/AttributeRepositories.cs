using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Parts;

public interface IPrnotyRepository
{
    /// <summary>A1 属性库查询（tbxzb 追加 cp=?p_cp）。</summary>
    Task<IReadOnlyList<PrnotyRow>> SearchAsync(string where, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>A1 保存（add/edit；edit 改码触发 updatenno + EditAll；tbname0 同步 DM 拼音）。</summary>
    Task<PrnotySaveResult> SaveAsync(PrnotySaveCommand cmd, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>A1 删除（物理删无拦截，契约 §1）。</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>A1 通用码/套件码（按厂牌 max+1 → update + EditAll）。</summary>
    Task<long> AssignCodeAsync(long id, bool isTno, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>A1 解除码（update WHERE nno，跨厂牌无 cp）。</summary>
    Task ClearCodeAsync(string nno, bool isTno, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>A1 税号批改（kind: 1=按名称 2=按旧税号 3=编码区间；跨厂牌无 cp）。</summary>
    Task<int> BatchCodeTsAsync(int kind, string value1, string value2, string newCodeTs, CancellationToken ct = default);

    /// <summary>A1 属性批改（网格多选逐行 update dc,na1 WHERE id）。</summary>
    Task UpdateAttrRowsAsync(IReadOnlyList<(long Id, string Dc, string Na1)> rows, CancellationToken ct = default);

    /// <summary>A1 最低售价（max(基准±×/系数) from tbisto → tbprnoty.oprc_min）。</summary>
    Task<decimal> CalcOprcMinAsync(long id, string nno, string cp, string dpmusem, int baseKind, int oper, decimal factor, int roundMode, CancellationToken ct = default);

    /// <summary>A1 属性传递到库存（row127：无行 INSERT / 有行 UPDATE）。</summary>
    Task<int> TransferToStockAsync(IReadOnlyList<long> ids, string dpm, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>A1 大类定义（tbsysset type='ljdl'，自动追加 dpm）。</summary>
    Task<IReadOnlyList<(int Id, string Name, string Valuec)>> GetLjdlAsync(string dpm, CancellationToken ct = default);

    Task SaveLjdlAsync(int id, string name, string valuec, bool isAdd, string dpm, CancellationToken ct = default);

    Task DeleteLjdlAsync(int id, CancellationToken ct = default);
}

public sealed class PrnotyRow
{
    public long Id { get; set; }
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Note { get; set; } = "";
    public decimal Iprj { get; set; }
    public int Xno { get; set; }
    public string Nnp { get; set; } = "";
    public string Nnq { get; set; } = "";
    public decimal Uqty { get; set; }
    public int Tno { get; set; }
    public decimal Amjj { get; set; }
    public decimal Bmjj { get; set; }
    public string Code_ts { get; set; } = "";
    public string Cxnno { get; set; } = "";
    public string Eng { get; set; } = "";
    public string Nvol { get; set; } = "";
    public decimal Suttle { get; set; }
    public string Note1 { get; set; } = "";
    public decimal? Oprc_cp { get; set; }
    public decimal? Oprc_min { get; set; }
}

public sealed class PrnotySaveCommand
{
    public bool IsAdd { get; set; }
    public long Id { get; set; }
    public required string Cp { get; set; }
    public required string Nno { get; set; }
    public string OldNno { get; set; } = "";
    public required string Na1 { get; set; }
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Iprj { get; set; }
    public string Code_ts { get; set; } = "";
    public string Eng { get; set; } = "";
    public string Nnp { get; set; } = "";
    public string Nnq { get; set; } = "";
    public decimal Uqty { get; set; }
    public int Xno { get; set; }
    public int Tno { get; set; }
    public decimal Amjj { get; set; }
    public decimal Bmjj { get; set; }
    public decimal Nvol { get; set; }
    public decimal Suttle { get; set; }
    public string Note { get; set; } = "";
    public string Note1 { get; set; } = "";
    public decimal? Oprc_cp { get; set; }
    public decimal? Oprc_min { get; set; }
    /// <summary>旧契约 24 列含 cxnno（表单层 S_NNO(nno)；触发器 insert_cxnno 按厂牌位串重算）。</summary>
    public string Cxnno { get; set; } = "";
}

public sealed record PrnotySaveResult(long Id, string Cxnno, int Xno, int Tno, int Name0Id);

/// <summary>tbname0 拼音码仓储（A2 零件代码定义 + A1/A4 同步）。</summary>
public interface INameCodeRepository
{
    Task<IReadOnlyList<Name0Row>> SearchAsync(string where, CancellationToken ct = default);

    /// <summary>新增/修改（防重 name；code=DM(1,name)/code1=DM(2,name) 由调用方传入或此处计算）。</summary>
    Task<(int Id, string Code, string Code1)> SaveAsync(bool isAdd, int id, string name, string name1, string? code, string? code1, CancellationToken ct = default);

    Task DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>提取（row10：exec up_name0 + getpywb 三表）。</summary>
    Task<int> ExtractAsync(CancellationToken ct = default);
}

public sealed class Name0Row
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name1 { get; set; } = "";
    public string Code1 { get; set; } = "";
}

/// <summary>DM 拼音：逐汉字查 tbcode（way=1 全拼首字母 / 否则五笔首字母），非汉字跳过（契约 §0）。</summary>
public interface IDmService
{
    Task<string> DmAsync(SqlConnection conn, int way, string text, CancellationToken ct = default, SqlTransaction? tx = null);
}

public sealed class DmService : IDmService
{
    public async Task<string> DmAsync(SqlConnection conn, int way, string text, CancellationToken ct = default, SqlTransaction? tx = null)
    {
        var chars = new List<string>();
        foreach (var ch in text ?? "")
        {
            if (ch > 127) chars.Add(ch.ToString()); // ASC>129 语义：VFP 双字节字符 → C# char>127
        }
        if (chars.Count == 0) return "";
        var rows = (await conn.QueryAsync<(string Chn, string Qp, string Wbh)>(
            new CommandDefinition(
                "SELECT rtrim(chn), rtrim(qp), rtrim(wbh) FROM tbcode WITH (NOLOCK) WHERE rtrim(chn) IN @chns",
                new { chns = chars }, transaction: tx, cancellationToken: ct))).AsList();
        var map = rows.ToDictionary(r => r.Chn, r => way == 1 ? FirstChar(r.Qp) : FirstChar(r.Wbh));
        return string.Concat(chars.Select(c => map.GetValueOrDefault(c, "")));
    }

    private static string FirstChar(string s) => s.Length > 0 ? s[..1] : "";
}

/// <summary>
/// tbname0 同步公共助手（旧自动建档路径统一语义）：
/// fr_isto row31 L254-263（散件入库）/ fr_isor row220 L143-152（订单入库）/
/// fr_order3 Qs.row99 L34-42（订货库 W1）：INSERT tbprnoty 后查 tbname0(name=na1)，
/// 无行则 INSERT tbname0(name,name1=na1, code=DM(1,na1), code1=DM(2,na1))。
/// 旧 fr_sale/fsw（手动开单路径不建 tbname0）；A1 页 PrnotyRepository.SaveAsync 自有同构实现。
/// </summary>
public static class Name0Sync
{
    /// <summary>tbprnoty 新档后调用：tbname0 无 name=na1 行时插入（code/code1 由 DM 拼音计算）。</summary>
    public static async Task SyncAsync(SqlConnection conn, SqlTransaction? tx, IDmService dm,
        string na1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(na1)) return;
        var name = new DbString { Value = na1, IsAnsi = true, Length = 20 };
        var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT id FROM tbname0 WITH (NOLOCK) WHERE rtrim(name)=@name", new { name },
            transaction: tx, cancellationToken: ct));
        if (exists is not null) return;
        var code = await dm.DmAsync(conn, 1, na1, ct, tx);
        var code1 = await dm.DmAsync(conn, 2, na1, ct, tx);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tbname0 (name, name1, code, code1) VALUES (@name, @name1, @code, @code1)",
            new
            {
                name,
                name1 = new DbString { Value = na1, IsAnsi = true, Length = 20 },
                code = new DbString { Value = code, IsAnsi = true, Length = 10 },
                code1 = new DbString { Value = code1, IsAnsi = true, Length = 10 },
            }, transaction: tx, cancellationToken: ct));
    }

    /// <summary>
    /// tbprnoty 写入车型后调用：tbty0 无 ty 行且可产出拼音码时插入（2026-10-30 车型拼音码需求，方案 B）。
    /// 车型码为混合码（汉字→tbcode 首字母 + 字母数字原样小写，符号跳过）：如 宝骏510→bj510、天籁J31→tlj31。
    /// 纯符号/无有效字符车型码为空跳过（原 LIKE 可搜）。只增不删：车型修改后旧行保留（与 tbname0 语义一致）。
    /// </summary>
    public static async Task SyncTyAsync(SqlConnection conn, SqlTransaction? tx, IDmService dm,
        string? ty, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ty)) return;
        var tyTrimmed = ty.Trim();
        try
        {
            var built = await BuildTyCodeAsync(conn, tx, tyTrimmed, ct);
            if (built is null) return;
            var (code, code1) = built.Value;
            await conn.ExecuteAsync(new CommandDefinition(
                """
                IF NOT EXISTS (SELECT 1 FROM tbty0 WITH (NOLOCK) WHERE rtrim(ty) = @ty)
                    INSERT tbty0 (ty, code, code1) VALUES (@ty, @code, @code1)
                """,
                new
                {
                    ty = new DbString { Value = tyTrimmed, IsAnsi = true, Length = 14 },
                    code = new DbString { Value = code, IsAnsi = true, Length = 10 },
                    code1 = new DbString { Value = code1, IsAnsi = true, Length = 10 },
                }, transaction: tx, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 并发窗口：另一连接抢先插入同车型（ty 唯一索引拦截）。目标已达成，静默跳过，
            // 避免中断外层入库/保存事务（语句级错误不回滚事务，后续语句可继续）
        }
    }

    /// <summary>车型混合码（带连接版）：收集 ty 中汉字一次查 tbcode 取首字母后按位拼装。</summary>
    public static async Task<(string Code, string Code1)?> BuildTyCodeAsync(SqlConnection conn, SqlTransaction? tx,
        string ty, CancellationToken ct = default)
    {
        var chns = ty.Where(c => c > 127).Select(c => c.ToString()).Distinct().ToList();
        var map = new Dictionary<string, (string Qp, string Wb)>();
        if (chns.Count > 0)
        {
            var rows = await conn.QueryAsync<(string Chn, string Qp, string Wbh)>(
                new CommandDefinition(
                    "SELECT rtrim(chn), rtrim(qp), rtrim(wbh) FROM tbcode WITH (NOLOCK) WHERE rtrim(chn) IN @chns",
                    new { chns }, transaction: tx, cancellationToken: ct));
            foreach (var r in rows) map[r.Chn] = (r.Qp.Length > 0 ? r.Qp[..1] : "", r.Wbh.Length > 0 ? r.Wbh[..1] : "");
        }
        return BuildTyCode(ty, map);
    }

    /// <summary>
    /// 车型混合码（纯内存版，ExtractAsync 批量用）：汉字→tbcode 首字母、字母/数字→原样小写、其余符号跳过；
    /// 拼装结果为空（纯符号/无有效字符）返回 null。超 10 字符截断（tbty0.code char(10)）。
    /// 注意：与 DmService（纯汉字首字母，tbname0 用）口径不同——车型需携带数字/字母才能支撑如 BJ510 检索。
    /// </summary>
    public static (string Code, string Code1)? BuildTyCode(string ty, IReadOnlyDictionary<string, (string Qp, string Wb)> pyMap)
    {
        var code = new System.Text.StringBuilder();
        var code1 = new System.Text.StringBuilder();
        foreach (var ch in ty)
        {
            if (ch > 127)
            {
                if (pyMap.TryGetValue(ch.ToString(), out var v))
                {
                    code.Append(v.Qp);
                    code1.Append(v.Wb);
                }
            }
            else if (char.IsLetterOrDigit(ch))
            {
                var low = char.ToLowerInvariant(ch).ToString();
                code.Append(low);
                code1.Append(low);
            }
            // 其余符号（/ 空格 - . 等）跳过
        }
        if (code.Length == 0) return null;
        return (code.ToString()[..Math.Min(code.Length, 10)], code1.ToString()[..Math.Min(code1.Length, 10)]);
    }
}

/// <summary>A3 属性传递（fr_sxcd）。</summary>
public interface IAttrTransferRepository
{
    /// <summary>字段清单（tbstru 交集 + FA/IPRJ/OPRC 默认勾选语义，契约 §3）。</summary>
    Task<IReadOnlyList<AttrFieldRow>> GetFieldsAsync(string dpm, CancellationToken ct = default);

    /// <summary>执行传递（目标表固定五组，勾选字段；价格 CASE 保留现价）。</summary>
    Task<AttrTransferResult> TransferAsync(IReadOnlyList<string> fields, IReadOnlyList<string> targets, string dpm, CancellationToken ct = default);
}

public sealed class AttrFieldRow
{
    public string Field_name { get; set; } = "";
    public string Explain { get; set; } = "";
    public bool Is_cd { get; set; }
}

public sealed record AttrTransferResult(IReadOnlyList<(string Table, string Field, int Rows)> Updated);

// ---------- 实现 ----------

/// <summary>
/// A 组属性定义族仓储（第十二批）。契约：开发文档/契约摘录/A组属性定义族契约.md。
/// </summary>
public sealed class PrnotyRepository(SqlConnectionFactory factory, IDmService dm) : IPrnotyRepository
{
    public async Task<IReadOnlyList<PrnotyRow>> SearchAsync(string where, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // tbxzb 规则：variable = 'cp=?p_cp'（where 未含 'cp=' 时追加一条）
        var useWhere = where != null && where.ToLowerInvariant().Contains("cp=")
            ? $"({where})"
            : $"({where}) and cp=@p_cp2";
        var sql = $"""
            SELECT id, rtrim(cp) cp, rtrim(nno) nno, rtrim(na1) na1, rtrim(ty) ty, rtrim(fa) fa, rtrim(dc) dc,
                   rtrim(unit) unit, rtrim(note) note, iprj, xno, rtrim(nnp) nnp, rtrim(nnq) nnq, uqty, tno,
                   amjj, bmjj, rtrim(code_ts) code_ts, rtrim(cxnno) cxnno, rtrim(eng) eng, rtrim(nvol) nvol,
                   ISNULL(suttle,0) suttle, rtrim(note1) note1, oprc_cp, oprc_min
            FROM tbprnoty WITH (NOLOCK)
            WHERE {useWhere}
            ORDER BY nno
            """;
        var p = new DynamicParameters();
        p.Add("p_cp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        p.Add("p_cp2", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        p.Add("p_dpmusem", new DbString { Value = dpmusem ?? "", IsAnsi = true, Length = 60 });
        return (await conn.QueryAsync<PrnotyRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
    }

    public async Task<PrnotySaveResult> SaveAsync(PrnotySaveCommand c, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 重码拦截（旧 CMDOK 语义）：添加必查；编辑仅在改码时查新码——未改码时旧行自身不算重码
        if (c.IsAdd || (!string.IsNullOrEmpty(c.OldNno) && c.OldNno != c.Nno))
        {
            if (await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT CASE WHEN EXISTS (SELECT 1 FROM tbprnoty WITH (NOLOCK) WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp) THEN 1 ELSE 0 END",
                    new { nno = new DbString { Value = c.Nno, IsAnsi = true, Length = 18 }, cp = new DbString { Value = c.Cp, IsAnsi = true, Length = 10 } },
                    cancellationToken: ct)) > 0)
                throw new QPGL.Domain.Common.AppException("NNO_DUP", $"该零件编码 {c.Nno} 已存在（同厂牌），请重新输入！", 409);
        }

        var p = new DynamicParameters();
        p.Add("cp", new DbString { Value = c.Cp, IsAnsi = true, Length = 10 });
        p.Add("nno", new DbString { Value = c.Nno, IsAnsi = true, Length = 18 });
        p.Add("na1", new DbString { Value = c.Na1, IsAnsi = true, Length = 40 });
        p.Add("ty", new DbString { Value = c.Ty ?? "", IsAnsi = true, Length = 14 });
        p.Add("fa", new DbString { Value = c.Fa ?? "", IsAnsi = true, Length = 8 });
        p.Add("dc", new DbString { Value = c.Dc ?? "", IsAnsi = true, Length = 8 });
        p.Add("unit", new DbString { Value = c.Unit ?? "", IsAnsi = true, Length = 4 });
        p.Add("iprj", c.Iprj);
        p.Add("code_ts", new DbString { Value = c.Code_ts ?? "", IsAnsi = true, Length = 12 });
        p.Add("eng", new DbString { Value = c.Eng ?? "", IsAnsi = true, Length = 60 });
        p.Add("nnp", new DbString { Value = c.Nnp ?? "", IsAnsi = true, Length = 18 });
        p.Add("nnq", new DbString { Value = c.Nnq ?? "", IsAnsi = true, Length = 18 });
        p.Add("uqty", c.Uqty);
        p.Add("xno", c.Xno);
        p.Add("tno", c.Tno);
        p.Add("amjj", c.Amjj);
        p.Add("bmjj", c.Bmjj);
        p.Add("nvol", c.Nvol);
        p.Add("suttle", c.Suttle);
        p.Add("note", new DbString { Value = c.Note ?? "", IsAnsi = true, Length = 50 });
        p.Add("note1", new DbString { Value = c.Note1 ?? "", IsAnsi = true, Length = 50 });
        // oprc_min 列 NOT NULL（副本库实测）：新增时编辑窗无最低售价输入（null）须落 0（旧 VFP frmAdd Value=0 同款）；
        // oprc_cp 库内无 NULL 行，null 一并落 0
        p.Add("oprc_cp", c.Oprc_cp ?? 0m);
        p.Add("oprc_min", c.Oprc_min ?? 0m);
        p.Add("cxnno", new DbString { Value = c.Cxnno ?? "", IsAnsi = true, Length = 18 });
        long id;
        if (c.IsAdd)
        {
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, iprj, code_ts, eng, nnp, nnq, uqty, xno, tno, amjj, bmjj, nvol, suttle, note, note1, oprc_cp, oprc_min, cxnno)
                VALUES (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @iprj, @code_ts, @eng, @nnp, @nnq, @uqty, @xno, @tno, @amjj, @bmjj, @nvol, @suttle, @note, @note1, @oprc_cp, @oprc_min, @cxnno);
                SELECT CAST(SCOPE_IDENTITY() AS bigint);
                """, p, cancellationToken: ct));
        }
        else
        {
            id = c.Id;
            p.Add("id", id);
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE tbprnoty SET cp=@cp, nno=@nno, na1=@na1, ty=@ty, fa=@fa, dc=@dc, unit=@unit, iprj=@iprj,
                       code_ts=@code_ts, eng=@eng, nnp=@nnp, nnq=@nnq, uqty=@uqty, xno=@xno, tno=@tno,
                       amjj=@amjj, bmjj=@bmjj, nvol=@nvol, suttle=@suttle, note=@note, note1=@note1,
                       oprc_cp=@oprc_cp, oprc_min=@oprc_min
                WHERE id=@id
                """, p, cancellationToken: ct));
        }

        // 改码联动（P_UPDATENNO=1 激活）：exec dbo.updatenno(@p_cp, 旧, 新, @fa, @p_xzfa=0)
        if (!c.IsAdd && !string.IsNullOrEmpty(c.OldNno) && c.OldNno != c.Nno)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "EXEC dbo.updatenno @p_cp, @old, @new, @fa, @xzfa",
                new
                {
                    p_cp = new DbString { Value = cp, IsAnsi = true, Length = 10 },
                    old = new DbString { Value = c.OldNno, IsAnsi = true, Length = 18 },
                    @new = new DbString { Value = c.Nno, IsAnsi = true, Length = 18 },
                    fa = new DbString { Value = c.Fa ?? "", IsAnsi = true, Length = 8 },
                    xzfa = false,
                }, cancellationToken: ct));
        }

        // EditAll（pg_proc：读回属性行 → update tbisto/tborder WHERE nno=属性行 nno（SCATTER 覆盖后=新码）；tborder 加 type<>'历史'）
        var cur = await conn.QuerySingleAsync<(string Nno, string Na1, string Ty, string Note, string Unit, string Dc, string Fa, int Xno, int Tno, string Cxnno)>(
            new CommandDefinition("""
                SELECT rtrim(nno), rtrim(na1), rtrim(ty), rtrim(note), rtrim(unit), rtrim(dc), rtrim(fa), ISNULL(xno,0), ISNULL(tno,0), rtrim(cxnno)
                FROM tbprnoty WITH (NOLOCK) WHERE id=@id
                """, new { id }, cancellationToken: ct));
        var ep = new DynamicParameters();
        ep.Add("nno", new DbString { Value = cur.Nno, IsAnsi = true, Length = 18 });
        ep.Add("na1", new DbString { Value = cur.Na1, IsAnsi = true, Length = 40 });
        ep.Add("ty", new DbString { Value = cur.Ty, IsAnsi = true, Length = 14 });
        ep.Add("note", new DbString { Value = cur.Note, IsAnsi = true, Length = 50 });
        ep.Add("unit", new DbString { Value = cur.Unit, IsAnsi = true, Length = 4 });
        ep.Add("dc", new DbString { Value = cur.Dc, IsAnsi = true, Length = 8 });
        ep.Add("fa", new DbString { Value = cur.Fa, IsAnsi = true, Length = 8 });
        ep.Add("xno", cur.Xno);
        ep.Add("tno", cur.Tno);
        ep.Add("cxnno", new DbString { Value = cur.Cxnno, IsAnsi = true, Length = 18 });
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tbisto SET nno=@nno, na1=@na1, ty=@ty, note=@note, unit=@unit, dc=@dc, xno=@xno, tno=@tno, cxnno=@cxnno, fa=@fa
            WHERE nno=@nno
            """, ep, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tborder SET nno=@nno, na1=@na1, ty=@ty, note=@note, unit=@unit, dc=@dc, cxnno=@cxnno, fa=@fa
            WHERE nno=@nno AND type<>'历史'
            """, ep, cancellationToken: ct));

        // tbname0 同步（无行 insert，code=DM(1)/code1=DM(2) 表单层填充）
        var name0Id = 0;
        var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT id FROM tbname0 WITH (NOLOCK) WHERE rtrim(name)=@name", new { name = new DbString { Value = c.Na1, IsAnsi = true, Length = 20 } }, cancellationToken: ct));
        if (exists is null)
        {
            var code = await dm.DmAsync(conn, 1, c.Na1, ct);
            var code1 = await dm.DmAsync(conn, 2, c.Na1, ct);
            name0Id = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                INSERT INTO tbname0 (name, name1, code, code1) VALUES (@name, @name1, @code, @code1);
                SELECT CAST(SCOPE_IDENTITY() AS int);
                """, new
            {
                name = new DbString { Value = c.Na1, IsAnsi = true, Length = 20 },
                name1 = new DbString { Value = c.Na1, IsAnsi = true, Length = 20 },
                code = new DbString { Value = code, IsAnsi = true, Length = 10 },
                code1 = new DbString { Value = code1, IsAnsi = true, Length = 10 },
            }, cancellationToken: ct));
        }
        else
        {
            name0Id = exists.Value;
        }

        // 车型拼音码同步（2026-10-30 需求：A1 新增/修改车型后 tbty0 无行则插入；只增不删）
        await Name0Sync.SyncTyAsync(conn, null, dm, cur.Ty, ct);

        // 触发器 insert_cxnno 已回填 cxnno/xno/tno（INSERT 后读回）
        var after = await conn.QuerySingleAsync<(string Cxnno, int Xno, int Tno)>(
            new CommandDefinition("SELECT rtrim(cxnno), ISNULL(xno,0), ISNULL(tno,0) FROM tbprnoty WITH (NOLOCK) WHERE id=@id", new { id }, cancellationToken: ct));
        return new PrnotySaveResult(id, after.Cxnno, after.Xno, after.Tno, name0Id);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM tbprnoty WHERE id=@id", new { id }, cancellationToken: ct));
    }

    public async Task<long> AssignCodeAsync(long id, bool isTno, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var col = isTno ? "tno" : "xno";
        var max = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            $"SELECT MAX({col}) FROM tbprnoty WITH (NOLOCK) WHERE rtrim(cp)=@cp", new { cp = new DbString { Value = cp, IsAnsi = true, Length = 10 } }, cancellationToken: ct)) ?? 0;
        var next = max + 1;
        await conn.ExecuteAsync(new CommandDefinition($"UPDATE tbprnoty SET {col}=@v WHERE id=@id", new { v = next, id }, cancellationToken: ct));
        await EditAllAsync(conn, id, ct);
        return next;
    }

    public async Task ClearCodeAsync(string nno, bool isTno, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var col = isTno ? "tno" : "xno";
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tbprnoty SET {col}=0 WHERE rtrim(nno)=@nno", new { nno = new DbString { Value = nno, IsAnsi = true, Length = 18 } }, cancellationToken: ct));
    }

    public async Task<int> BatchCodeTsAsync(int kind, string value1, string value2, string newCodeTs, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM tbcomplex WITH (NOLOCK) WHERE rtrim(code_ts)=@c) THEN 1 ELSE 0 END",
            new { c = new DbString { Value = newCodeTs, IsAnsi = true, Length = 12 } }, cancellationToken: ct));
        if (exists == 0)
            throw new QPGL.Domain.Common.AppException("CODETS_NOT_FOUND", "税则号不存在！", 404);
        var where = kind switch
        {
            1 => "rtrim(na1)=@v1",
            2 => "rtrim(code_ts)=@v1",
            _ => "rtrim(nno)>=@v1 AND rtrim(nno)<=@v2",
        };
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tbprnoty SET code_ts=@new WHERE {where}",
            new
            {
                v1 = new DbString { Value = value1, IsAnsi = true, Length = 18 },
                v2 = new DbString { Value = value2 ?? value1, IsAnsi = true, Length = 18 },
                @new = new DbString { Value = newCodeTs, IsAnsi = true, Length = 12 },
            }, cancellationToken: ct));
    }

    public async Task UpdateAttrRowsAsync(IReadOnlyList<(long Id, string Dc, string Na1)> rows, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        foreach (var r in rows)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbprnoty SET dc=@dc, na1=@na1 WHERE id=@id",
                new { dc = new DbString { Value = r.Dc, IsAnsi = true, Length = 8 }, na1 = new DbString { Value = r.Na1, IsAnsi = true, Length = 40 }, r.Id },
                cancellationToken: ct));
    }

    public async Task<decimal> CalcOprcMinAsync(long id, string nno, string cp, string dpmusem, int baseKind, int oper, decimal factor, int roundMode, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var col = baseKind switch { 2 => "iprj", 3 => "iprc", 4 => "oprb", _ => "oprc" };
        var expr = oper switch { 1 => $"{col} + @f", 2 => $"{col} - @f", 3 => $"{col} * @f", _ => $"CASE WHEN @f = 0 THEN 0 ELSE {col} / @f END" };
        var wrapped = roundMode switch
        {
            1 => $"ROUND({expr}, 2)",
            2 => $"CEILING({expr})",
            _ => expr,
        };
        var val = await conn.ExecuteScalarAsync<decimal?>(
            $"SELECT MAX({wrapped}) FROM tbisto WHERE rtrim(cp)=@cp AND rtrim(nno)=@nno AND @p_dpmusem LIKE '%'+rtrim(dpm)+'%'",
            new
            {
                f = factor,
                cp = new DbString { Value = cp, IsAnsi = true, Length = 10 },
                nno = new DbString { Value = nno, IsAnsi = true, Length = 18 },
                p_dpmusem = new DbString { Value = dpmusem ?? "", IsAnsi = true, Length = 60 },
            });
        var v = val ?? 0m;
        await conn.ExecuteAsync(
            "UPDATE tbprnoty SET oprc_min=@v WHERE rtrim(cp)=@cp AND id=@id",
            new { v, cp = new DbString { Value = cp, IsAnsi = true, Length = 10 }, id });
        return v;
    }

    public async Task<int> TransferToStockAsync(IReadOnlyList<long> ids, string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var n = 0;
        foreach (var id in ids)
        {
            var r = await conn.QuerySingleOrDefaultAsync<(string Cp, string Nno, string Na1, string Ty, string Fa, decimal Iprj, string Unit, string Dc, int Xno, int Tno, string Cxnno, string Note) >(
                new CommandDefinition("""
                    SELECT rtrim(cp), rtrim(nno), rtrim(na1), rtrim(ty), rtrim(fa), iprj, rtrim(unit), rtrim(dc), ISNULL(xno,0), ISNULL(tno,0), rtrim(cxnno), rtrim(note)
                    FROM tbprnoty WITH (NOLOCK) WHERE id=@id
                    """, new { id }, cancellationToken: ct));
            if (r.Nno is null) continue;
            var exists = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT TOP 1 rtrim(nno) FROM tbisto WITH (NOLOCK) WHERE rtrim(cp)=@cp AND rtrim(nno)=@nno AND dpm=@dpm",
                new { cp = new DbString { Value = r.Cp, IsAnsi = true, Length = 10 }, nno = new DbString { Value = r.Nno, IsAnsi = true, Length = 18 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } },
                cancellationToken: ct));
            if (exists is null)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO tbisto (dpm, cp, nno, na1, ty, fa, iprj, unit, dc, xno, tno, cxnno, note)
                    VALUES (@dpm, @cp, @nno, @na1, @ty, @fa, @iprj, @unit, @dc, @xno, @tno, @cxnno, @note)
                    """, new
                {
                    dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 },
                    cp = new DbString { Value = r.Cp, IsAnsi = true, Length = 10 },
                    nno = new DbString { Value = r.Nno, IsAnsi = true, Length = 18 },
                    na1 = new DbString { Value = r.Na1, IsAnsi = true, Length = 40 },
                    ty = new DbString { Value = r.Ty, IsAnsi = true, Length = 14 },
                    fa = new DbString { Value = r.Fa, IsAnsi = true, Length = 8 },
                    r.Iprj,
                    unit = new DbString { Value = r.Unit, IsAnsi = true, Length = 4 },
                    dc = new DbString { Value = r.Dc, IsAnsi = true, Length = 8 },
                    r.Xno, r.Tno,
                    cxnno = new DbString { Value = r.Cxnno, IsAnsi = true, Length = 18 },
                    note = new DbString { Value = r.Note, IsAnsi = true, Length = 50 },
                }, cancellationToken: ct));
            }
            else
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE tbisto SET na1=@na1, ty=@ty, fa=@fa, unit=@unit, dc=@dc, xno=@xno, tno=@tno, cxnno=@cxnno, note=@note
                    WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp
                    """, new
                {
                    na1 = new DbString { Value = r.Na1, IsAnsi = true, Length = 40 },
                    ty = new DbString { Value = r.Ty, IsAnsi = true, Length = 14 },
                    fa = new DbString { Value = r.Fa, IsAnsi = true, Length = 8 },
                    unit = new DbString { Value = r.Unit, IsAnsi = true, Length = 4 },
                    dc = new DbString { Value = r.Dc, IsAnsi = true, Length = 8 },
                    r.Xno, r.Tno,
                    cxnno = new DbString { Value = r.Cxnno, IsAnsi = true, Length = 18 },
                    note = new DbString { Value = r.Note, IsAnsi = true, Length = 50 },
                    nno = new DbString { Value = r.Nno, IsAnsi = true, Length = 18 },
                    cp = new DbString { Value = r.Cp, IsAnsi = true, Length = 10 },
                }, cancellationToken: ct));
            }
            n++;
        }
        return n;
    }

    public async Task<IReadOnlyList<(int Id, string Name, string Valuec)>> GetLjdlAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int Id, string Name, string Valuec)>(
            new CommandDefinition("SELECT id, rtrim(name), rtrim(valuec) FROM tbsysset WITH (NOLOCK) WHERE type='ljdl' AND dpm=@dpm ORDER BY id",
                new { dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SaveLjdlAsync(int id, string name, string valuec, bool isAdd, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (isAdd)
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tbsysset (name, type, valuec, dpm) VALUES (@name, 'ljdl', @valuec, @dpm)",
                new { name = new DbString { Value = name, IsAnsi = true, Length = 40 }, valuec = new DbString { Value = valuec, IsAnsi = true, Length = 40 }, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 10 } },
                cancellationToken: ct));
        else
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbsysset SET name=@name, valuec=@valuec WHERE id=@id",
                new { name = new DbString { Value = name, IsAnsi = true, Length = 40 }, valuec = new DbString { Value = valuec, IsAnsi = true, Length = 40 }, id },
                cancellationToken: ct));
    }

    public async Task DeleteLjdlAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM tbsysset WHERE id=@id", new { id }, cancellationToken: ct));
    }

    /// <summary>EDITALL 复刻（读回属性行 → update tbisto/tborder WHERE nno=行值；tborder 加 type&lt;&gt;'历史'）。</summary>
    internal static async Task EditAllAsync(SqlConnection conn, long id, CancellationToken ct)
    {
        var cur = await conn.QuerySingleOrDefaultAsync<(string Nno, string Na1, string Ty, string Note, string Unit, string Dc, string Fa, int Xno, int Tno, string Cxnno)>(
            new CommandDefinition("""
                SELECT rtrim(nno), rtrim(na1), rtrim(ty), rtrim(note), rtrim(unit), rtrim(dc), rtrim(fa), ISNULL(xno,0), ISNULL(tno,0), rtrim(cxnno)
                FROM tbprnoty WITH (NOLOCK) WHERE id=@id
                """, new { id }, cancellationToken: ct));
        if (cur.Nno is null) return;
        var ep = new DynamicParameters();
        ep.Add("nno", new DbString { Value = cur.Nno, IsAnsi = true, Length = 18 });
        ep.Add("na1", new DbString { Value = cur.Na1, IsAnsi = true, Length = 40 });
        ep.Add("ty", new DbString { Value = cur.Ty, IsAnsi = true, Length = 14 });
        ep.Add("note", new DbString { Value = cur.Note, IsAnsi = true, Length = 50 });
        ep.Add("unit", new DbString { Value = cur.Unit, IsAnsi = true, Length = 4 });
        ep.Add("dc", new DbString { Value = cur.Dc, IsAnsi = true, Length = 8 });
        ep.Add("fa", new DbString { Value = cur.Fa, IsAnsi = true, Length = 8 });
        ep.Add("xno", cur.Xno);
        ep.Add("tno", cur.Tno);
        ep.Add("cxnno", new DbString { Value = cur.Cxnno, IsAnsi = true, Length = 18 });
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tbisto SET nno=@nno, na1=@na1, ty=@ty, note=@note, unit=@unit, dc=@dc, xno=@xno, tno=@tno, cxnno=@cxnno, fa=@fa WHERE nno=@nno
            """, ep, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tborder SET nno=@nno, na1=@na1, ty=@ty, note=@note, unit=@unit, dc=@dc, cxnno=@cxnno, fa=@fa WHERE nno=@nno AND type<>'历史'
            """, ep, cancellationToken: ct));
    }
}

public sealed class NameCodeRepository(SqlConnectionFactory factory, IDmService dm) : INameCodeRepository
{
    public async Task<IReadOnlyList<Name0Row>> SearchAsync(string where, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<Name0Row>(new CommandDefinition(
            $"SELECT id, rtrim(name) name, rtrim(code) code, rtrim(name1) name1, rtrim(code1) code1 FROM tbname0 WITH (NOLOCK) WHERE {(string.IsNullOrWhiteSpace(where) ? "1=1" : $"({where})")} ORDER BY name",
            null, cancellationToken: ct))).AsList();
    }

    public async Task<(int Id, string Code, string Code1)> SaveAsync(bool isAdd, int id, string name, string name1, string? code, string? code1, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // 旧码 row26：TXTNAME1（name1）LostFocus → code=DM(1,name1)/code1=DM(2,name1)——DM 源=别名
        var dmSource = string.IsNullOrWhiteSpace(name1) ? name : name1;
        var c = code ?? await dm.DmAsync(conn, 1, dmSource, ct);
        var c1 = code1 ?? await dm.DmAsync(conn, 2, dmSource, ct);
        int newId;
        if (isAdd)
        {
            newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "INSERT INTO tbname0 (name, name1, code, code1) VALUES (@n, @n1, @c, @c1); SELECT CAST(SCOPE_IDENTITY() AS int);",
                new { n = new DbString { Value = name, IsAnsi = true, Length = 20 }, n1 = new DbString { Value = name1, IsAnsi = true, Length = 20 }, c = new DbString { Value = c, IsAnsi = true, Length = 10 }, c1 = new DbString { Value = c1, IsAnsi = true, Length = 10 } },
                cancellationToken: ct));
        }
        else
        {
            newId = id;
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbname0 SET name=@n, name1=@n1, code=@c, code1=@c1 WHERE id=@id",
                new { n = new DbString { Value = name, IsAnsi = true, Length = 20 }, n1 = new DbString { Value = name1, IsAnsi = true, Length = 20 }, c = new DbString { Value = c, IsAnsi = true, Length = 10 }, c1 = new DbString { Value = c1, IsAnsi = true, Length = 10 }, id },
                cancellationToken: ct));
        }
        return (newId, c, c1);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbname0 WHERE rtrim(name)=@n", new { n = new DbString { Value = name, IsAnsi = true, Length = 20 } }, cancellationToken: ct));
    }

    public async Task<int> ExtractAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var amou = new DynamicParameters();
        amou.Add("amou", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);
        await conn.ExecuteAsync(new CommandDefinition("EXEC dbo.up_name0 @amou = @amou OUTPUT", amou, cancellationToken: ct));
        var n = amou.Get<int?>("amou") ?? 0;
        foreach (var (tbl, col) in new[] { ("tbgu", "gname"), ("tbgugys", "gyname"), ("tbxlcode", "xlxm") })
            await conn.ExecuteAsync(new CommandDefinition(
                "EXEC dbo.getpywb @tbl, @col",
                new { tbl = new DbString { Value = tbl, IsAnsi = true, Length = 20 }, col = new DbString { Value = col, IsAnsi = true, Length = 20 } },
                cancellationToken: ct));
        return n;
    }
}

/// <summary>tbty0 车型拼音码行（2026-10-30 方案 B：车型拼音码独立维护表，结构对齐 tbname0）。</summary>
public sealed class Ty0Row
{
    public int Id { get; set; }
    public string Ty { get; set; } = "";
    public string Code { get; set; } = "";
    public string Code1 { get; set; } = "";
}

/// <summary>tbty0 车型拼音码仓储（A2 维护页签 + 存量提取；查询侧 PartRepository 车型条件消费）。</summary>
public interface ITyCodeRepository
{
    /// <summary>一键初始化第一步：幂等建表（表/索引已存在时跳过；生产部署点一次按钮即完成建表+灌数）。</summary>
    Task EnsureTableAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Ty0Row>> SearchAsync(string where, CancellationToken ct = default);

    /// <summary>新增/修改（防重 ty；code=DM(1,ty)/code1=DM(2,ty) 恒重算，与 tbname0 DM 语义一致）。</summary>
    Task<(int Id, string Code, string Code1)> SaveAsync(bool isAdd, int id, string ty, CancellationToken ct = default);

    Task DeleteAsync(string ty, CancellationToken ct = default);

    /// <summary>存量提取：tbprnoty distinct 车型逐条 DM 算码（应用层拼码——本库 SQL 2012 无 STRING_AGG，
    /// 且排序规则非 Chinese_PRC，SQL 端汉字判定不可靠），幂等跳过已存在 ty。</summary>
    Task<int> ExtractAsync(CancellationToken ct = default);
}

public sealed class TyCodeRepository(SqlConnectionFactory factory) : ITyCodeRepository
{
    /// <summary>幂等建表（2026-10-30 一键初始化）：tbty0 结构对齐 tbname0（无主键、id identity、NOT NULL 默认 ''），
    /// 表已存在时整块跳过——重复点击/多客户端并发点击均安全。需连接账号具备建表权限（现有部署多为管理员直连）。
    /// ty 唯一索引（2026-10-30 并发加固）：彻底杜绝多客户端同时点初始化按钮的重复行边界；
    /// 各写入点（SyncTyAsync/SaveAsync/ExtractAsync）均对唯一键冲突（SQL 2601/2627）做了容错。</summary>
    public async Task EnsureTableAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            IF OBJECT_ID('tbty0') IS NULL
            BEGIN
                CREATE TABLE tbty0 (
                    id    int IDENTITY NOT NULL,
                    ty    char(14) NOT NULL CONSTRAINT df_tbty0_ty    DEFAULT (''),
                    code  char(10) NOT NULL CONSTRAINT df_tbty0_code  DEFAULT (''),
                    code1 char(10) NOT NULL CONSTRAINT df_tbty0_code1 DEFAULT ('')
                );
                CREATE UNIQUE INDEX ux_tbty0_ty ON tbty0 (ty);
                CREATE INDEX ix_tbty0_code  ON tbty0 (code);
                CREATE INDEX ix_tbty0_code1 ON tbty0 (code1);
            END
            """, null, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Ty0Row>> SearchAsync(string where, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<Ty0Row>(new CommandDefinition(
            $"SELECT id, rtrim(ty) ty, rtrim(code) code, rtrim(code1) code1 FROM tbty0 WITH (NOLOCK) WHERE {(string.IsNullOrWhiteSpace(where) ? "1=1" : $"({where})")} ORDER BY ty",
            null, cancellationToken: ct))).AsList();
    }

    public async Task<(int Id, string Code, string Code1)> SaveAsync(bool isAdd, int id, string ty, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM tbty0 WITH (NOLOCK) WHERE rtrim(ty)=@t AND id<>@id) THEN 1 ELSE 0 END",
            new { t = new DbString { Value = ty, IsAnsi = true, Length = 14 }, id }, cancellationToken: ct));
        if (dup == 1) throw new QPGL.Domain.Common.AppException("TY_DUPLICATE", "该车型已存在！", 409);
        var built = await Name0Sync.BuildTyCodeAsync(conn, null, ty, ct);
        var c = built?.Code ?? "";
        var c1 = built?.Code1 ?? "";
        int newId;
        try
        {
            if (isAdd)
            {
                newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "INSERT INTO tbty0 (ty, code, code1) VALUES (@t, @c, @c1); SELECT CAST(SCOPE_IDENTITY() AS int);",
                    new { t = new DbString { Value = ty, IsAnsi = true, Length = 14 }, c = new DbString { Value = c, IsAnsi = true, Length = 10 }, c1 = new DbString { Value = c1, IsAnsi = true, Length = 10 } },
                    cancellationToken: ct));
            }
            else
            {
                newId = id;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbty0 SET ty=@t, code=@c, code1=@c1 WHERE id=@id",
                    new { t = new DbString { Value = ty, IsAnsi = true, Length = 14 }, c = new DbString { Value = c, IsAnsi = true, Length = 10 }, c1 = new DbString { Value = c1, IsAnsi = true, Length = 10 }, id },
                    cancellationToken: ct));
            }
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 并发窗口：dup 预检与插入之间另一连接抢先插入了同车型（ty 唯一索引拦截）
            throw new QPGL.Domain.Common.AppException("TY_DUPLICATE", "该车型已存在！", 409);
        }
        return (newId, c, c1);
    }

    public async Task DeleteAsync(string ty, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbty0 WHERE rtrim(ty)=@t", new { t = new DbString { Value = ty, IsAnsi = true, Length = 14 } }, cancellationToken: ct));
    }

    public async Task<int> ExtractAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        // tbcode 全量入内存（混合码源：汉字→首字母 + 字母数字原样，与 Name0Sync.BuildTyCode 同口径）
        var pyMap = new Dictionary<string, (string Qp, string Wb)>();
        foreach (var (chn, qp, wbh) in await conn.QueryAsync<(string Chn, string Qp, string Wbh)>(
            new CommandDefinition("SELECT rtrim(chn), rtrim(qp), rtrim(wbh) FROM tbcode WITH (NOLOCK)", null, cancellationToken: ct)))
        {
            pyMap[chn] = (qp.Length > 0 ? qp[..1] : "", wbh.Length > 0 ? wbh[..1] : "");
        }

        var existing = (await conn.QueryAsync<string>(
            new CommandDefinition("SELECT rtrim(ty) FROM tbty0 WITH (NOLOCK)", null, cancellationToken: ct))).ToHashSet();

        var toInsert = new List<(string Ty, string Code, string Code1)>();
        foreach (var ty in await conn.QueryAsync<string>(
            new CommandDefinition("SELECT DISTINCT rtrim(ty) FROM tbprnoty WITH (NOLOCK) WHERE rtrim(ty)<>''", null, cancellationToken: ct)))
        {
            if (existing.Contains(ty)) continue;
            var built = Name0Sync.BuildTyCode(ty, pyMap);
            if (built is null) continue;   // 纯符号/无有效字符车型（原 LIKE 可搜，空码无检索意义）
            toInsert.Add((ty, built.Value.Code, built.Value.Code1));
        }

        // 批量多值 INSERT（每批 100 行×3 参数，远低于 2100 参数上限）
        var inserted = 0;
        foreach (var batch in toInsert.Chunk(100))
        {
            var values = new List<string>();
            var p = new DynamicParameters();
            for (var i = 0; i < batch.Length; i++)
            {
                values.Add($"(@ty{i}, @c{i}, @c1_{i})");
                p.Add($"ty{i}", new DbString { Value = batch[i].Ty, IsAnsi = true, Length = 14 });
                p.Add($"c{i}", new DbString { Value = batch[i].Code, IsAnsi = true, Length = 10 });
                p.Add($"c1_{i}", new DbString { Value = batch[i].Code1, IsAnsi = true, Length = 10 });
            }
            try
            {
                inserted += await conn.ExecuteAsync(new CommandDefinition(
                    $"INSERT INTO tbty0 (ty, code, code1) VALUES {string.Join(", ", values)}", p, cancellationToken: ct));
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // 并发窗口兜底（ty 唯一索引拦截）：另一客户端在本次 existing 快照之后插入了本批部分车型 →
                // 本批降级为逐行防重插入（INSERT..SELECT..WHERE NOT EXISTS，返回 0/1 计数准确），
                // 保证不重不漏、初始化永不因并发失败
                foreach (var (ty, code, code1) in batch)
                {
                    inserted += await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbty0 (ty, code, code1)
                        SELECT @t, @c, @c1
                        WHERE NOT EXISTS (SELECT 1 FROM tbty0 WITH (NOLOCK) WHERE rtrim(ty) = @t)
                        """,
                        new
                        {
                            t = new DbString { Value = ty, IsAnsi = true, Length = 14 },
                            c = new DbString { Value = code, IsAnsi = true, Length = 10 },
                            c1 = new DbString { Value = code1, IsAnsi = true, Length = 10 },
                        }, cancellationToken: ct));
                }
            }
        }
        return inserted;
    }
}
