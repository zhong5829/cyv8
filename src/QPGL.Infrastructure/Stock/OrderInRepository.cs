using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Parts;

namespace QPGL.Infrastructure.Stock;

public interface IOrderInRepository
{
    /// <summary>订单下拉（row123，2026-11-03 放宽：计划单 type='' 与在途单 type='历史' 均可选）。</summary>
    Task<List<(string Cno, string Gyname)>> GetOpenCnosAsync(string cp, string dpm, CancellationToken ct = default);

    /// <summary>拉单查询（row166）：按 cno 取 tborder 未入完行（oqty&gt;qty），价率换算。</summary>
    Task<List<OrderInOrderLine>> GetOrderLinesAsync(string cno, string cp, string dpm,
        decimal bl1, decimal bl2, decimal bl3, bool wbtohs, CancellationToken ct = default);

    /// <summary>草稿网格（row3 setsource：tbisorsm + 库存/别名/订单连接）。</summary>
    Task<List<OrderInDraftLine>> GetDraftLinesAsync(string seano, string cp, string dpm, string pDepotno, CancellationToken ct = default);

    /// <summary>整单导入（row156）：tborder 缺口行 → tbisorsm 草稿。</summary>
    Task<int> ImportWholeOrderAsync(ImportOrderInCommand cmd, string cp, string dpm, string per, string pDepotno, string pFpcw,
        decimal bl1, decimal bl2, decimal bl3, bool wbtohs, CancellationToken ct = default);

    /// <summary>手工加行（row211 非 EMODE）。</summary>
    Task<int> AddLineAsync(AddOrderInLineCommand cmd, string cp, string dpm, string per, string pDepotno, CancellationToken ct = default);

    /// <summary>草稿行编辑（row211 EMODE：UPDATE tbisorsm）。</summary>
    Task<int> UpdateLineAsync(UpdateOrderInLineCommand cmd, string dpm, CancellationToken ct = default);

    /// <summary>删草稿行（鴕.row72）。</summary>
    Task<int> DeleteLineAsync(int id, string dpm, CancellationToken ct = default);

    /// <summary>弃单（W__.row186：整船单删除）。</summary>
    Task<int> DiscardAsync(string seano, string cp, string dpm, CancellationToken ct = default);

    /// <summary>分货（癌.row103）：同件多订单按订货时间/需求比例拆分到 seano+"_1"。</summary>
    Task<string?> DistributeAsync(string seano, OrderInDistributeMode mode, string cp, string dpm, string per, CancellationToken ct = default);

    /// <summary>整批调价（饰.row112 通用路径）。</summary>
    Task<int> BatchAdjustAsync(BatchAdjustOrderInCommand cmd, string cp, string dpm, CancellationToken ct = default);

    /// <summary>运费分摊（圚_.row306：按进价金额比例摊入 iprc）。</summary>
    Task<int> AllocateFreightAsync(string seano, decimal yf, string cp, string dpm, CancellationToken ct = default);

    /// <summary>确认入库（矑_.row220「更新库存」完整序列，行级事务）。</summary>
    Task<ConfirmOrderInResult> ConfirmAsync(ConfirmOrderInCommand cmd, string cp, string dpm, string per,
        string pDepotno, string pFpcw, CancellationToken ct = default);

    /// <summary>配件默认仓位（现有库存批次任一非废品仓位，row156 同语义；按 nno 去重）。</summary>
    Task<Dictionary<string, string>> GetDefaultPosisAsync(string cp, string dpm, string depotno, string fpcw,
        IEnumerable<string> nnos, CancellationToken ct = default);

    /// <summary>订单直接入库（2026-11-03 改版：无草稿，row220 写路径逐行事务，数据源 tborder 实时行）。</summary>
    Task<ConfirmOrderInResult> ConfirmByOrderAsync(ConfirmByOrderCommand cmd, string dpm, string per,
        string pDepotno, CancellationToken ct = default);

    /// <summary>入库相关系统开关：「仓位必须先定义」/「废品仓位标识」（tbsysset）。</summary>
    Task<(bool Posigl, bool Rkposinull, string Fpcw)> GetSwitchesAsync(CancellationToken ct = default);

    /// <summary>拉单价率（row3 Init：tbsysset 进价/售价/批价比率 valuec，缺省 0.080/0.050/0.090）+ 外币为含税价。</summary>
    Task<(decimal Bl1, decimal Bl2, decimal Bl3, bool Wbtohs)> GetPriceRatiosAsync(CancellationToken ct = default);
}

/// <summary>
/// 订单入库仓储（B2，二期）。旧实现出处：fr_isor（表单方法目录）。
/// 写路径契约（row220 确认，行级事务，与旧系统一致）：
///   1) INSERT tbistoed(pno,cno,box,seano,isord='Y',cp,nno,na1,ty,fa,unit,dc,item,posi,type='入库',
///      fkfs,gyname,gyno,depotno,jkamount,kcamount=入库前库存合计,iprj,iprc,oprc,indate,per,Freight,dpm)
///      ——注意 KCAMOUNT=入库前快照（跨批次 sum），与散件入库（入库后快照）不同
///   2) tbprnoty 缺档自动建档（uqty=0；tbname0 拼音码同步属 DM 函数遗留项暂缺）
///   3a) gname 非空（代订直发）：GETNEWDH('销售单',tbsada.paper) + INSERT tbsada(type='销售',
///       ckamount=jkamount,kcid 缺省=0)——sale_in 触发器仅 kcid&gt;0 扣库存 → 直发不动库存，仅台账
///   3b) 否则 ISAPPE('tbisorsm',id,jkamount)（IsappeEngine Client 库版，运行时 P_WAY）
///   4) UPDATE tborder SET qty=MIN(qty+行量,oqty) WHERE cp+cno+nno+dpm(+orno 非空)
///   5) CHKIPRJ：UPDATE tbprnoty SET iprj（更新档案进价）
///   6) DELETE tbisorsm WHERE id（行处理完即删）
/// P_POSIGL（tbsysset「仓位必须先定义」）开启时校验仓位存在且可存该件（tbposi.nno 匹配或 isfree）。
/// 展示/确认规则（row3/row156 本地游标 REPLACE 语义）：gname 非空行 depotno 显示与确认均用 P_DEPOTNO（代订货库号，缺省 Z）。
/// </summary>
public sealed class OrderInRepository(SqlConnectionFactory factory, IDocNoService docNo, Parts.IDmService dm) : IOrderInRepository
{
    private const string PosiSuffixChars = "!@#$%^&*";

    public async Task<List<(string Cno, string Gyname)>> GetOpenCnosAsync(string cp, string dpm, CancellationToken ct = default)
    {
        // 2026-11-03 改版：只列在途单（并入在途后 orno=原计划单号、cno 清空），计划单 type='' 不再作为入库数据源
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string cno, string gyname)>(
            new CommandDefinition(
                """
                SELECT RTRIM(orno), MAX(ISNULL(RTRIM(gyname), ''))
                FROM tborder WITH (NOLOCK)
                WHERE RTRIM(ISNULL(type, '')) = '历史' AND cp = @cp AND oqty > qty AND RTRIM(ISNULL(orno, '')) <> '' AND dpm = @dpm
                GROUP BY orno
                ORDER BY 1
                """,
                new { cp = Ansi(cp, 10), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<OrderInOrderLine>> GetOrderLinesAsync(string cno, string cp, string dpm,
        decimal bl1, decimal bl2, decimal bl3, bool wbtohs, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // row166：进价 iprc=iprj*m_bl1（外币含税价时为除），批发 oprb=iprj*m_bl3；
        // 首行 gname 为空 → 整批改用 oprc=iprj*m_bl2（无客户订单按售价比率推售价）
        var iprcExpr = wbtohs
            ? "CASE WHEN @bl1 = 0 THEN CAST(0 AS numeric(18,4)) ELSE iprj / @bl1 END"
            : "iprj * @bl1";
        const string baseCols = """
            ISNULL(gname, '') gname, item, RTRIM(nno) nno, RTRIM(na1) na1, oqty, qty, (oqty - qty) zt,
            iprj, {oprc} oprc, RTRIM(orno) orno, {iprc} iprc, iprj * @bl3 oprb,
            RTRIM(ty) ty, RTRIM(fa) fa, RTRIM(cp) cp, RTRIM(unit) unit, RTRIM(dc) dc,
            RTRIM(note) note, RTRIM(fkfs) fkfs, RTRIM(gyname) gyname, RTRIM(cno) cno
            """;
        async Task<List<OrderInOrderLine>> QueryAsync(string oprcExpr)
        {
            var sql = baseCols.Replace("{oprc}", oprcExpr).Replace("{iprc}", iprcExpr);
            var rows = await conn.QueryAsync<OrderInOrderLine>(
                new CommandDefinition(
                    $"SELECT {sql} FROM tborder WITH (NOLOCK) WHERE orno = @cno AND RTRIM(ISNULL(type, '')) = '历史' AND cp = @cp AND oqty > qty AND dpm = @dpm ORDER BY nno, orno",
                    new { cno = Ansi(cno, 15), cp = Ansi(cp, 10), dpm = Ansi(dpm, 10), bl1, bl2, bl3 },
                    cancellationToken: ct));
            return rows.AsList();
        }

        var lines = await QueryAsync("oprc");
        if (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0].Gname))
            lines = await QueryAsync("iprj * @bl2");
        return lines;
    }

    public async Task<List<OrderInDraftLine>> GetDraftLinesAsync(string seano, string cp, string dpm, string pDepotno, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OrderInDraftLine>(
            new CommandDefinition(
                """
                SELECT a.id, RTRIM(a.pno) pno, RTRIM(a.box) box, RTRIM(a.cno) cno, a.item, RTRIM(a.nno) nno,
                       RTRIM(a.na1) na1, a.jkamount, a.fpamount, a.iprj, a.iprc, a.oprc, a.oprb,
                       RTRIM(a.ty) ty, RTRIM(a.fa) fa, RTRIM(a.posi) posi,
                       depotno = CASE WHEN ISNULL(g.gname, '') <> '' THEN @pDepotno ELSE RTRIM(a.depotno) END,
                       RTRIM(a.seano) seano, RTRIM(a.orno) orno, RTRIM(a.unit) unit, RTRIM(a.fkfs) fkfs,
                       RTRIM(a.gyname) gyname, RTRIM(a.dc) dc, RTRIM(a.cp) cp, a.indate, a.nvol, a.suttle,
                       ISNULL(g.gname, '') gname,
                       kcamount = ISNULL(kc1.kc, 0) + ISNULL(kc2.kc, 0),
                       ISNULL(g.zt, 0) zt, ISNULL(a.Freight, 0) freight
                FROM tbisorsm a WITH (NOLOCK)
                OUTER APPLY (SELECT TOP (1) o.gname, (o.oqty - o.qty) zt FROM tborder o WITH (NOLOCK)
                             WHERE o.dpm = @dpm AND o.cno = a.cno AND o.orno = a.orno
                               AND RTRIM(o.type) = '历史' AND o.cp = a.cp AND o.nno = a.nno) g
                OUTER APPLY (SELECT SUM(x.kcamount) kc FROM tbisto x WITH (NOLOCK)
                             WHERE x.cp = a.cp AND x.nno = a.nno AND (x.dpm = @dpm OR x.dpm = '')) kc1
                OUTER APPLY (SELECT SUM(x.kcamount) kc FROM tbisto x WITH (NOLOCK)
                             WHERE x.cp = a.cp AND x.nno IN
                                   (SELECT p.nnp FROM tbprnoty p WHERE p.cp = a.cp AND p.nno = a.nno AND ISNULL(p.nnp, '') <> ''
                                    UNION ALL
                                    SELECT p.nnq FROM tbprnoty p WHERE p.cp = a.cp AND p.nno = a.nno AND ISNULL(p.nnq, '') <> '')
                               AND (x.dpm = @dpm OR x.dpm = '')) kc2
                WHERE a.seano = @seano AND a.cp = @cp AND a.dpm = @dpm
                ORDER BY a.id
                """,
                new { seano = Ansi(seano, 15), cp = Ansi(cp, 10), dpm = Ansi(dpm, 10), pDepotno = Ansi(pDepotno, 10) },
                cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> ImportWholeOrderAsync(ImportOrderInCommand cmd, string cp, string dpm, string per, string pDepotno, string pFpcw,
        decimal bl1, decimal bl2, decimal bl3, bool wbtohs, CancellationToken ct = default)
    {
        cmd.Seano = cmd.Seano.Trim();
        if (string.IsNullOrWhiteSpace(cmd.Seano)) throw new AppException("SEANO_REQUIRED", "请输入船单号", 422);
        if (string.IsNullOrWhiteSpace(cmd.Cno)) throw new AppException("CNO_REQUIRED", "请选择到货单号", 422);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // row166：iprc=iprj*bl1（外币含税价为除），oprb=iprj*bl3；首行 gname 空 → 整批 oprc=iprj*bl2
            var iprcExpr = wbtohs
                ? "CASE WHEN @bl1 = 0 THEN CAST(0 AS numeric(18,4)) ELSE iprj / @bl1 END"
                : "iprj * @bl1";
            const string cols = """
                item, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa, RTRIM(dc) dc, RTRIM(unit) unit,
                (oqty - qty) zt, iprj, {iprc} iprc, {oprc} oprc, iprj * @bl3 oprb,
                RTRIM(orno) orno, RTRIM(fkfs) fkfs, RTRIM(gyname) gyname, RTRIM(note) note, ISNULL(gname, '') gname
                """;
            async Task<List<(int item, string nno, string na1, string ty, string fa, string dc, string unit,
                decimal zt, decimal iprj, decimal iprc, decimal oprc, decimal oprb, string orno,
                string fkfs, string gyname, string note, string gname)>> QueryAsync(string oprcExpr)
            {
                var sql = cols.Replace("{iprc}", iprcExpr).Replace("{oprc}", oprcExpr);
                var rows = (await conn.QueryAsync<(int item, string nno, string na1, string ty, string fa, string dc,
                    string unit, decimal zt, decimal iprj, decimal iprc, decimal oprc, decimal oprb, string orno,
                    string fkfs, string gyname, string note, string gname)>(
                    new CommandDefinition(
                        $"SELECT {sql} FROM tborder WITH (UPDLOCK) WHERE cno = @cno AND cp = @cp AND oqty > qty AND dpm = @dpm ORDER BY nno, orno",
                        new { cno = Ansi(cmd.Cno, 15), cp = Ansi(cp, 10), dpm = Ansi(dpm, 10), bl1, bl2, bl3 },
                        transaction: tx, cancellationToken: ct))).AsList();
                return rows;
            }

            var orders = await QueryAsync("oprc");
            if (orders.Count == 0)
                throw new AppException("ORDER_LINES_EMPTY", $"到货单 {cmd.Cno} 没有未入完的订单行", 422);
            if (string.IsNullOrWhiteSpace(orders[0].gname))
                orders = await QueryAsync("iprj * @bl2");

            var count = 0;
            foreach (var o in orders)
            {
                // row156：仓位取该件现库存批次任一非废品仓位（旧 TOP 1 无排序语义）
                var posi = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT TOP (1) RTRIM(posi) FROM tbisto WITH (NOLOCK) WHERE nno = @nno AND cp = @cp " +
                    "AND depotno = @depotno AND dpm = @dpm AND RTRIM(posi) <> @fpcw",
                    new { nno = Ansi(o.nno, 18), cp = Ansi(cp, 10), depotno = Ansi(cmd.Depotno, 10), dpm = Ansi(dpm, 10), fpcw = Ansi(pFpcw, 10) },
                    transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbisorsm
                        (pno, seano, box, cno, item, nno, na1, fa, ty, jkamount, fpamount, iprj, iprc, oprc, oprb,
                         posi, depotno, cp, fkfs, gyname, unit, dc, note, indate, per, orno, dpm)
                    VALUES
                        (@pno, @seano, @box, @cno, @item, @nno, @na1, @fa, @ty, @jkamount, @fpamount, @iprj, @iprc, @oprc, @oprb,
                         @posi, @depotno, @cp, @fkfs, @gyname, @unit, @dc, @note, @indate, @per, @orno, @dpm)
                    """,
                    new
                    {
                        pno = Ansi(cmd.Pno, 12), seano = Ansi(cmd.Seano, 15), box = Ansi(cmd.Box, 15),
                        cno = Ansi(cmd.Cno, 15), item = o.item, nno = Ansi(o.nno, 18), na1 = Ansi(o.na1, 40),
                        fa = Ansi(o.fa, 8), ty = Ansi(o.ty, 14), jkamount = o.zt, fpamount = o.zt,
                        iprj = o.iprj, iprc = o.iprc, oprc = o.oprc, oprb = o.oprb,
                        posi = Ansi(posi ?? "", 10), depotno = Ansi(cmd.Depotno, 10), cp = Ansi(cp, 10),
                        fkfs = Ansi(o.fkfs, 10), gyname = Ansi(o.gyname, 60), unit = Ansi(o.unit, 4),
                        dc = Ansi(o.dc, 8), note = Ansi(o.note, 100), indate = DateTime.Today,
                        per = Ansi(per, 6), orno = Ansi(o.orno, 14), dpm = Ansi(dpm, 10),
                    },
                    transaction: tx, cancellationToken: ct));
                count++;
            }
            await tx.CommitAsync(ct);
            return count;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> AddLineAsync(AddOrderInLineCommand cmd, string cp, string dpm, string per, string pDepotno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Nno)) throw new AppException("NNO_REQUIRED", "零件号不能为空", 422);
        if (string.IsNullOrWhiteSpace(cmd.Na1)) throw new AppException("NA1_REQUIRED", "名称不能为空（不允许入库）", 422);
        var depotno = cmd.Depotno;
        if (!string.IsNullOrWhiteSpace(cmd.Gname)) depotno = pDepotno;   // row211 L44-46：gname 非空 → 代订货库号

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var n = await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO tbisorsm
                    (pno, seano, box, cno, item, nno, na1, fa, ty, jkamount, fpamount, iprj, iprc, oprc, oprb,
                     posi, depotno, cp, fkfs, gyname, unit, dc, note, indate, per, orno, dpm)
                VALUES
                    (@pno, @seano, @box, @cno, @item, @nno, @na1, @fa, @ty, @jkamount, @fpamount, @iprj, @iprc, @oprc, @oprb,
                     @posi, @depotno, @cp, @fkfs, @gyname, @unit, @dc, @note, @indate, @per, @orno, @dpm)
                """,
                new
                {
                    pno = Ansi(cmd.Pno, 12), seano = Ansi(cmd.Seano, 15), box = Ansi(cmd.Box, 15),
                    cno = Ansi(cmd.Cno, 15), item = cmd.Item, nno = Ansi(cmd.Nno, 18), na1 = Ansi(cmd.Na1, 40),
                    fa = Ansi(cmd.Fa, 8), ty = Ansi(cmd.Ty, 14), jkamount = cmd.Jkamount, fpamount = cmd.Fpamount,
                    iprj = cmd.Iprj, iprc = cmd.Iprc, oprc = cmd.Oprc, oprb = cmd.Oprb,
                    posi = Ansi(cmd.Posi, 10), depotno = Ansi(depotno, 10), cp = Ansi(cp, 10),
                    fkfs = Ansi(cmd.Fkfs, 10), gyname = Ansi(cmd.Gyname, 60), unit = Ansi(cmd.Unit, 4),
                    dc = Ansi(cmd.Dc, 8), note = Ansi(cmd.Note, 100), indate = DateTime.Today,
                    per = Ansi(per, 6), orno = Ansi(cmd.Orno, 14), dpm = Ansi(dpm, 10),
                },
                transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return n;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> UpdateLineAsync(UpdateOrderInLineCommand cmd, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 保存前校验（row211 L4-24）：P_RKPOSINULL（配件入库不许空仓位）/ P_POSIGL（仓位必须先定义）
            var (posigl, rkposinull, _) = await LoadSwitchesAsync(conn, tx, ct);
            if (rkposinull && string.IsNullOrWhiteSpace(cmd.Posi))
                throw new AppException("POSI_REQUIRED", "仓位为空，无法入库！", 422);
            if (posigl)
            {
                if (string.IsNullOrWhiteSpace(cmd.Posi))
                    throw new AppException("POSI_REQUIRED", "仓位不能为空！（系统参数：仓位必须先定义）", 422);
                var p = await conn.QueryFirstOrDefaultAsync<(string nno, bool isfree)>(
                    new CommandDefinition(
                        "SELECT TOP (1) RTRIM(nno), ISNULL(isfree, 0) FROM tbposi WITH (NOLOCK) WHERE posi = @posi",
                        new { posi = Ansi(cmd.Posi, 10) }, transaction: tx, cancellationToken: ct));
                if (p.nno == null)
                    throw new AppException("POSI_UNDEFINED", "仓位未定义！（系统参数：仓位必须先定义）", 422);
                if (p.nno != cmd.Nno.Trim() && !p.isfree)
                    throw new AppException("POSI_OCCUPIED", $"{cmd.Posi} 仓定义为存放 {p.nno}，不能存放其它零件！", 422);
            }

            var changed = cmd.OriginalNno.Length > 0 && cmd.Nno.Trim() != cmd.OriginalNno.Trim();
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbisorsm SET nno = @nno, jkamount = @jkamount, iprj = @iprj, fa = @fa, iprc = @iprc, " +
                "oprc = @oprc, oprb = @oprb, posi = @posi, depotno = @depotno, suttle = @suttle WHERE id = @id AND dpm = @dpm",
                new
                {
                    nno = Ansi(cmd.Nno, 18), cmd.Jkamount, cmd.Iprj, fa = Ansi(cmd.Fa, 8), cmd.Iprc,
                    cmd.Oprc, cmd.Oprb, posi = Ansi(cmd.Posi, 10), depotno = Ansi(cmd.Depotno, 10),
                    cmd.Suttle, cmd.Id, dpm = Ansi(dpm, 10),
                }, transaction: tx, cancellationToken: ct));

            // 编码修改联动（row211 L85-105；SYSLOG 由 UI/审计承接——DbrkRepository 先例）：
            // 原编码属性行存在且新编码属性行不存在 → tbprnoty nno→新编码（改绑属性）
            if (changed)
            {
                var oldExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tbprnoty WITH (NOLOCK) WHERE nno = @nno AND cp = @cp",
                    new { nno = Ansi(cmd.OriginalNno, 18), cp = Ansi(cmd.Cp, 10) }, transaction: tx, cancellationToken: ct));
                if (oldExists > 0)
                {
                    var newExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(1) FROM tbprnoty WITH (NOLOCK) WHERE nno = @nno AND cp = @cp",
                        new { nno = Ansi(cmd.Nno, 18), cp = Ansi(cmd.Cp, 10) }, transaction: tx, cancellationToken: ct));
                    if (newExists > 0)
                        throw new AppException("NNO_EXISTS", "更新属性失败! 新编码在属性表中已存在!", 409);
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbprnoty SET nno = @newNno WHERE nno = @oldNno AND cp = @cp",
                        new { newNno = Ansi(cmd.Nno, 18), oldNno = Ansi(cmd.OriginalNno, 18), cp = Ansi(cmd.Cp, 10) },
                        transaction: tx, cancellationToken: ct));
                }
            }
            await tx.CommitAsync(ct);
            return 1;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> DeleteLineAsync(int id, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbisorsm WHERE id = @id AND dpm = @dpm",
            new { id, dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    public async Task<int> DiscardAsync(string seano, string cp, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbisorsm WHERE cp = @cp AND seano = @seano AND dpm = @dpm",
            new { cp = Ansi(cp, 10), seano = Ansi(seano, 15), dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    public async Task<string?> DistributeAsync(string seano, OrderInDistributeMode mode, string cp, string dpm, string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var src = (await conn.QueryAsync<DraftRow>(new CommandDefinition(
                "SELECT id, RTRIM(cp) cp, RTRIM(cno) cno, item, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, " +
                "RTRIM(fa) fa, jkamount, fpamount, iprj, iprc, oprc, oprb, RTRIM(posi) posi, RTRIM(depotno) depotno, " +
                "RTRIM(seano) seano, RTRIM(orno) orno, RTRIM(unit) unit, RTRIM(fkfs) fkfs, RTRIM(gyname) gyname, " +
                "RTRIM(dc) dc, RTRIM(note) note, RTRIM(box) box, RTRIM(pno) pno " +
                "FROM tbisorsm WITH (UPDLOCK) WHERE seano = @seano AND cp = @cp AND dpm = @dpm ORDER BY id",
                new { seano = Ansi(seano, 15), cp = Ansi(cp, 10), dpm = Ansi(dpm, 10) },
                transaction: tx, cancellationToken: ct))).AsList();
            if (src.Count == 0) throw new AppException("NO_DRAFT", "没有到货明细，不能分货！", 422);

            var newSeano = seano.Trim() + "_1";
            // 先清既有分货结果（row103 L31）
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tbisorsm WHERE seano = @seano AND dpm = @dpm",
                new { seano = Ansi(newSeano, 15), dpm = Ansi(dpm, 10) },
                transaction: tx, cancellationToken: ct));

            foreach (var s in src)
            {
                // row103 L40-56：候选订单行（gname 非空优先，按订货时间或缺口排序）
                var orderSql = mode == OrderInDistributeMode.ByOrderDate
                    ? """
                      SELECT id, RTRIM(orno) orno, (oqty - qty) gap, ISNULL(gname, '') gname FROM (
                          SELECT TOP (100) PERCENT id, orno, (oqty - qty) gap, gname, oqty, qty, orderdate FROM tborder WITH (NOLOCK)
                          WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND oqty <> 0 AND ISNULL(gname, '') <> '' AND oqty > qty
                            AND cno IN (SELECT cno FROM tbisorsm WHERE seano = @seano AND dpm = @dpm)
                          UNION ALL
                          SELECT TOP (100) PERCENT id, orno, (oqty - qty) gap, gname, oqty, qty, orderdate FROM tborder WITH (NOLOCK)
                          WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND oqty <> 0 AND ISNULL(gname, '') = '' AND oqty > qty
                            AND cno IN (SELECT cno FROM tbisorsm WHERE seano = @seano AND dpm = @dpm)
                      ) a ORDER BY gname DESC, orderdate ASC
                      """
                    : """
                      SELECT id, RTRIM(orno) orno, (oqty - qty) gap, ISNULL(gname, '') gname FROM (
                          SELECT TOP (100) PERCENT id, orno, (oqty - qty) gap, gname, oqty, qty FROM tborder WITH (NOLOCK)
                          WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND oqty <> 0 AND ISNULL(gname, '') <> '' AND oqty > qty
                            AND cno IN (SELECT cno FROM tbisorsm WHERE seano = @seano AND dpm = @dpm)
                          UNION ALL
                          SELECT TOP (100) PERCENT id, orno, (oqty - qty) gap, gname, oqty, qty FROM tborder WITH (NOLOCK)
                          WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND oqty <> 0 AND ISNULL(gname, '') = '' AND oqty > qty
                            AND cno IN (SELECT cno FROM tbisorsm WHERE seano = @seano AND dpm = @dpm)
                      ) a ORDER BY gname DESC, (oqty - qty) DESC
                      """;
                var candidates = (await conn.QueryAsync<(int id, string orno, decimal gap, string gname)>(
                    new CommandDefinition(
                        orderSql, new { dpm = Ansi(dpm, 10), cp = Ansi(s.Cp, 10), nno = Ansi(s.Nno, 18), seano = Ansi(seano.Trim(), 15) },
                        transaction: tx, cancellationToken: ct))).AsList();

                var remain = s.Jkamount;
                if (candidates.Count == 0)
                {
                    // 无候选订单 → 整行挂空单（row103 L164-168）
                    await InsertDistributedAsync(conn, tx, s, newSeano, "", s.Jkamount, 0, dpm, per, ct);
                    continue;
                }
                var totalGap = candidates.Sum(c => c.gap);
                if (remain >= totalGap)
                {
                    // 全部缺口满足 + 余量挂空单（row103 L63-81）
                    foreach (var c in candidates)
                    {
                        await InsertDistributedAsync(conn, tx, s, newSeano, c.orno, c.gap, c.id, dpm, per, ct);
                        remain -= c.gap;
                    }
                    if (remain > 0)
                        await InsertDistributedAsync(conn, tx, s, newSeano, "", remain, 0, dpm, per, ct);
                }
                else if (mode == OrderInDistributeMode.ByOrderDate)
                {
                    // 顺序填充（row103 L83-105）
                    foreach (var c in candidates)
                    {
                        if (remain <= 0) break;
                        var take = Math.Min(remain, c.gap);
                        await InsertDistributedAsync(conn, tx, s, newSeano, c.orno, take, c.id, dpm, per, ct);
                        remain -= take;
                    }
                }
                else
                {
                    // 按需求比例（row103 L107-161）：AA=全部缺口，AA1=有客户行缺口
                    var namedGap = candidates.Where(c => c.gname != "").Sum(c => c.gap);
                    if (remain > namedGap)
                    {
                        // 余量覆盖全部有客户行 → 顺序填满（L115-135）
                        foreach (var c in candidates)
                        {
                            if (remain <= 0) break;
                            var take = Math.Min(remain, c.gap);
                            await InsertDistributedAsync(conn, tx, s, newSeano, c.orno, take, c.id, dpm, per, ct);
                            remain -= take;
                        }
                    }
                    else
                    {
                        // 仅按有客户行比例分配（L137-159）：末个有客户行吃余额
                        var named = candidates.Where(c => c.gname != "").ToList();
                        decimal allocated = 0;
                        for (var i = 0; i < named.Count; i++)
                        {
                            decimal take = i == named.Count - 1
                                ? remain - allocated
                                : Math.Round(remain * (named[i].gap / namedGap), 0, MidpointRounding.AwayFromZero);
                            if (take > 0)
                                await InsertDistributedAsync(conn, tx, s, newSeano, named[i].orno, take, named[i].id, dpm, per, ct);
                            allocated += take;
                        }
                    }
                }
            }
            await tx.CommitAsync(ct);
            return newSeano;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task InsertDistributedAsync(SqlConnection conn, SqlTransaction tx, DraftRow s,
        string newSeano, string orno, decimal jkamount, int orderItemId, string dpm, string per, CancellationToken ct)
    {
        // item 取订单行 item（row103 L71：m.ITEM = C_ORDER1.ITEM）；空单行 item=0
        var item = orderItemId != 0
            ? await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT item FROM tborder WHERE id = @id", new { id = orderItemId },
                transaction: tx, cancellationToken: ct)) ?? 0
            : 0;
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tbisorsm
                (pno, seano, box, cno, item, nno, na1, fa, ty, jkamount, fpamount, iprj, iprc, oprc, oprb,
                 posi, depotno, cp, fkfs, gyname, unit, dc, note, indate, per, orno, dpm)
            VALUES
                (@pno, @seano, @box, @cno, @item, @nno, @na1, @fa, @ty, @jkamount, @fpamount, @iprj, @iprc, @oprc, @oprb,
                 @posi, @depotno, @cp, @fkfs, @gyname, @unit, @dc, @note, @indate, @per, @orno, @dpm)
            """,
            new
            {
                pno = Ansi(s.Pno, 12), seano = Ansi(newSeano, 15), box = Ansi(s.Box, 15),
                cno = Ansi(s.Cno, 15), item, nno = Ansi(s.Nno, 18), na1 = Ansi(s.Na1, 40),
                fa = Ansi(s.Fa, 8), ty = Ansi(s.Ty, 14), jkamount, fpamount = jkamount,
                iprj = s.Iprj, iprc = s.Iprc, oprc = s.Oprc, oprb = s.Oprb,
                posi = Ansi(s.Posi, 10), depotno = Ansi(s.Depotno, 10), cp = Ansi(s.Cp, 10),
                fkfs = Ansi(s.Fkfs, 10), gyname = Ansi(s.Gyname, 60), unit = Ansi(s.Unit, 4),
                dc = Ansi(s.Dc, 8), note = Ansi("", 100), indate = DateTime.Today,
                per = Ansi(per, 6), orno = Ansi(orno, 14), dpm = Ansi(dpm, 10),
            },
            transaction: tx, cancellationToken: ct));
    }

    public async Task<int> BatchAdjustAsync(BatchAdjustOrderInCommand cmd, string cp, string dpm, CancellationToken ct = default)
    {
        var ufield = cmd.Ufield.Trim().ToLowerInvariant();
        if (ufield is not ("iprc" or "oprc" or "oprb"))
            throw new AppException("UFIELD_INVALID", "整批调价仅支持 iprc/oprc/oprb", 422);
        var srcCol = cmd.SourceColumn.Trim().ToLowerInvariant();
        if (srcCol is not ("iprc" or "oprc" or "oprb" or "iprj"))
            throw new AppException("SRC_INVALID", "调价基准仅支持 iprc/oprc/oprb/iprj", 422);

        // 旧 饰.row112：ROUND 表达式（OPGXS=1，iprc 按库小数位其余 2 位）/ INT(+0.5) / INT(+0.99)
        var digits = ufield == "iprc" ? cmd.IprcCount : 2;
        var expr = cmd.RoundMode switch
        {
            2 => $"CAST(ROUND({srcCol} * @xs + 0.5, 0, 1) AS numeric(18,4))",          // INT(+0.5)
            3 => $"CAST(ROUND({srcCol} * @xs + 0.99, 0, 1) AS numeric(18,4))",         // INT(+0.99)
            _ => $"ROUND({srcCol} * @xs, {digits})",
        };

        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tbisorsm SET {ufield} = {expr} WHERE cp = @cp AND seano = @seano AND dpm = @dpm",
            new { xs = cmd.Factor, cp = Ansi(cp, 10), seano = Ansi(cmd.Seano, 15), dpm = Ansi(dpm, 10) },
            cancellationToken: ct));
    }

    public async Task<int> AllocateFreightAsync(string seano, decimal yf, string cp, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var total = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(jkamount * iprc) FROM tbisorsm WITH (UPDLOCK) WHERE cp = @cp AND seano = @seano AND dpm = @dpm",
                new { cp = Ansi(cp, 10), seano = Ansi(seano, 15), dpm = Ansi(dpm, 10) },
                transaction: tx, cancellationToken: ct)) ?? 0m;
            if (total == 0) return 0;
            // 圐_.row306 L19-30：Freight 留原 iprc（仅空值），iprc 按金额比例摊入运费
            var n = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbisorsm SET Freight = CASE WHEN ISNULL(Freight, 0) = 0 THEN iprc ELSE Freight END, " +
                "iprc = iprc + (iprc * @yf) / @total WHERE cp = @cp AND seano = @seano AND dpm = @dpm",
                new { yf, total, cp = Ansi(cp, 10), seano = Ansi(seano, 15), dpm = Ansi(dpm, 10) },
                transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return n;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ConfirmOrderInResult> ConfirmAsync(ConfirmOrderInCommand cmd, string cp, string dpm, string per,
        string pDepotno, string pFpcw, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var result = new ConfirmOrderInResult();

        await using var conn = await factory.OpenAsync(ct);
        var (posigl, _, _) = await LoadSwitchesAsync(conn, null, ct);

        // 确认前清空空编码草稿行（row220 L3-4：PASSTH Delete TbIsorsm NNO=''，全表无部门条件——照抄）
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbisorsm WHERE RTRIM(ISNULL(nno, '')) = ''", cancellationToken: ct));

        // 范围选择（row220 OPT1：1=按箱号 2=勾选行 3=全部；ORDER BY gname,id 与旧 c_jktmp 一致）
        var where = cmd.Mode switch
        {
            1 when string.IsNullOrWhiteSpace(cmd.Box) => throw new AppException("BOX_REQUIRED", "请输入箱号", 422),
            1 => "a.box = @box",
            2 when cmd.Ids is not { Count: > 0 } => throw new AppException("NO_DRAFT", "现在入库选项没有入库数据可以更新库存！", 422),
            2 => "a.id IN @ids",
            _ => "1 = 1",
        };
        var rows = (await conn.QueryAsync<ConfirmRow>(new CommandDefinition(
            $"""
            SELECT a.id, RTRIM(a.cp) cp, RTRIM(a.cno) cno, a.item, RTRIM(a.nno) nno, RTRIM(a.na1) na1,
                   RTRIM(a.ty) ty, RTRIM(a.fa) fa, RTRIM(a.dc) dc, RTRIM(a.unit) unit, a.jkamount,
                   a.iprj, a.iprc, a.oprc, a.oprb, a.xno, RTRIM(a.posi) posi,
                   depotno = CASE WHEN ISNULL(g.gname, '') <> '' THEN @pDepotno ELSE RTRIM(a.depotno) END,
                   RTRIM(a.box) box, RTRIM(a.seano) seano, RTRIM(a.fkfs) fkfs, RTRIM(a.gyname) gyname,
                   RTRIM(a.orno) orno, RTRIM(a.note) note, ISNULL(g.gname, '') gname, ISNULL(a.Freight, 0) freight
            FROM tbisorsm a WITH (UPDLOCK)
            OUTER APPLY (SELECT TOP (1) o.gname FROM tborder o WITH (NOLOCK)
                         WHERE o.dpm = @dpm AND o.cno = a.cno AND o.orno = a.orno
                           AND RTRIM(o.type) = '历史' AND o.cp = a.cp AND o.nno = a.nno) g
            WHERE a.seano = @seano AND a.cp = @cp AND a.dpm = @dpm AND {where}
            ORDER BY ISNULL(g.gname, ''), a.id
            """,
            new { seano = Ansi(cmd.Seano, 15), cp = Ansi(cp, 10), dpm = Ansi(dpm, 10), box = Ansi(cmd.Box, 15),
                  pDepotno = Ansi(pDepotno, 10), ids = cmd.Ids },
            cancellationToken: ct))).AsList();
        if (rows.Count == 0)
            throw new AppException("NO_DRAFT", "现在入库选项没有入库数据可以更新库存！", 422);

        // 仓位标识后缀（row220 L68-82：MARK0 须为 '! @ # $ % ^ & * ' 子串——实际只接受单字符）
        var suffix = cmd.PosiSuffix?.Trim() ?? "";
        if (suffix.Length > 0)
        {
            if (suffix.Length != 1 || !PosiSuffixChars.Contains(suffix))
                throw new AppException("POSI_SUFFIX_INVALID", "请选规定的标识", 422);
            foreach (var r in rows)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbisorsm SET posi = RTRIM(posi) + @suffix WHERE id = @id",
                    new { suffix, id = r.id }, cancellationToken: ct));
                r.Posi = (r.Posi + suffix).Trim();
            }
        }

        // P_POSIGL 仓位校验（row220 L32-51）
        if (posigl)
        {
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Posi))
                    throw new AppException("POSI_REQUIRED", $"{r.Nno} 仓位不能为空！（系统参数：仓位必须先定义）", 422);
                var p = await conn.QueryFirstOrDefaultAsync<(string nno, bool isfree)>(
                    "SELECT TOP (1) RTRIM(nno), ISNULL(isfree, 0) FROM tbposi WITH (NOLOCK) WHERE posi = @posi",
                    new { posi = Ansi(r.Posi, 10) });
                if (p.nno == null)
                    throw new AppException("POSI_UNDEFINED", $"{r.Nno},{r.Posi} 仓未定义！（系统参数：仓位必须先定义）", 422);
                if (p.nno != r.Nno.Trim() && !p.isfree)
                    throw new AppException("POSI_OCCUPIED", $"{r.Posi} 仓定义为存放 {p.nno}，不能存放 {r.Nno}！", 422);
            }
        }

        var prm = await IsappeEngine.LoadParamsAsync(conn, null, ct);
        string? lastGyname = null, lastSaleGname = null, paper = "", gyno = "";

        foreach (var r in rows)
        {
            // 行级事务（row220 L118-198：SQLSETPROP Transactions=2 → COMMIT/ROLLBACK 每行）
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                var presum = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WITH (UPDLOCK, HOLDLOCK) " +
                    "WHERE cp = @cp AND nno = @nno AND depotno = @depotno AND dpm = @dpm",
                    new { cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18), depotno = Ansi(r.Depotno, 10), dpm = Ansi(dpm, 10) },
                    transaction: tx, cancellationToken: ct));

                // 供应商号（gyname 变化时重查，row220 L120-124）
                if (r.Gyname.Trim() != lastGyname)
                {
                    gyno = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT TOP (1) RTRIM(gyno) FROM tbgugys WITH (NOLOCK) WHERE gyname = @gyname",
                        new { gyname = Ansi(r.Gyname, 60) }, transaction: tx, cancellationToken: ct)) ?? "";
                    lastGyname = r.Gyname.Trim();
                }

                // 1) 入库台账（row220 L125：27 列原样 + PASSTH 补 dpm；pno=表单 TXTPNO）
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbistoed
                        (pno, cno, box, seano, isord, cp, nno, na1, ty, fa, unit, dc, item, posi, type, fkfs,
                         gyname, gyno, depotno, jkamount, kcamount, iprj, iprc, oprc, indate, per, Freight, dpm)
                    VALUES
                        (@pno, @cno, @box, @seano, 'Y', @cp, @nno, @na1, @ty, @fa, @unit, @dc, @item, @posi, N'入库', @fkfs,
                         @gyname, @gyno, @depotno, @jkamount, @presum, @iprj, @iprc, @oprc, @indate, @per, @freight, @dpm)
                    """,
                    new
                    {
                        pno = Ansi(cmd.Pno, 12), cno = Ansi(r.Cno, 15), box = Ansi(r.Box, 10), seano = Ansi(r.Seano, 15),
                        cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 40), ty = Ansi(r.Ty, 14),
                        fa = Ansi(r.Fa, 8), unit = Ansi(r.Unit, 4), dc = Ansi(r.Dc, 8), r.Item,
                        posi = Ansi(r.Posi, 10), fkfs = Ansi(r.Fkfs, 10), gyname = Ansi(r.Gyname, 60),
                        gyno = Ansi(gyno, 10), depotno = Ansi(r.Depotno, 10), r.Jkamount, presum,
                        iprj = r.Iprj, iprc = r.Iprc, oprc = r.Oprc, indate = today, per = Ansi(per, 10),
                        freight = r.Freight, dpm = Ansi(dpm, 10),
                    },
                    transaction: tx, cancellationToken: ct));

                // 2) tbprnoty 缺档自动建档（row220 L127-153 + tbname0 同步 L143-152）
                var profile = await conn.QueryFirstOrDefaultAsync<(int id, decimal iprj, string na1)>(new CommandDefinition(
                    "SELECT TOP (1) id, iprj, RTRIM(na1) FROM tbprnoty WITH (UPDLOCK) WHERE cp = @cp AND nno = @nno",
                    new { cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18) },
                    transaction: tx, cancellationToken: ct));
                if (profile.id == 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, note, iprj, uqty, cxnno, oprc_cp, oprc_min, cpict)
                        VALUES (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @note, @iprj, 0, @cxnno, 0, 0, '')
                        """,
                        new
                        {
                            cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 40), ty = Ansi(r.Ty, 14),
                            fa = Ansi(r.Fa, 8), dc = Ansi(r.Dc, 8), unit = Ansi(r.Unit, 4), note = Ansi("", 254),
                            iprj = r.Iprj, cxnno = Ansi("", 18),
                        },
                        transaction: tx, cancellationToken: ct));
                    await Name0Sync.SyncAsync(conn, tx, dm, r.Na1, ct);
                    await Name0Sync.SyncTyAsync(conn, tx, dm, r.Ty, ct);   // 车型拼音码（2026-10-30 需求）
                }

                // 3a) 直发（gname 非空且数量≠0）：销售单 + tbsada（kcid 缺省 0 → sale_in 不扣库存）
                var direct = !string.IsNullOrWhiteSpace(r.Gname) && r.Jkamount != 0;
                if (direct)
                {
                    if (string.IsNullOrEmpty(paper) || lastSaleGname != r.Gname)
                    {
                        paper = await docNo.GetNewDocNoAsync("销售单", dpm, DocNoService.SaleTarget, ct);
                        result.Papers.Add(paper);
                        lastSaleGname = r.Gname;
                    }
                    // xno/tno/cxnno/dc 取 tbprnoty（row220 L174-175 SCATTER 覆盖语义）
                    var (txno, tno, tcxnno, tdc) = await conn.QueryFirstOrDefaultAsync<(int, int, string, string)>(
                        new CommandDefinition(
                            "SELECT TOP (1) ISNULL(xno, 0), ISNULL(tno, 0), RTRIM(ISNULL(cxnnO, '')), RTRIM(ISNULL(dc, '')) " +
                            "FROM tbprnoty WITH (NOLOCK) WHERE cp = @cp AND nno = @nno",
                            new { cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18) },
                            transaction: tx, cancellationToken: ct));
                    var gno = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT TOP (1) RTRIM(gno) FROM tbgu WITH (NOLOCK) WHERE gname = @gname",
                        new { gname = Ansi(r.Gname, 40) }, transaction: tx, cancellationToken: ct)) ?? "";

                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbsada
                            (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc, paper,
                             gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, outdate, depotno, dc, dpm)
                        VALUES
                            (@cp, @nno, @na1, @fa, @ty, @unit, N'销售', @posi, @jkamount, @oprc, @oprc, @iprj, @iprc, @paper,
                             @gno, @gname, @per, @xno, @tno, @cxnno, @nno, @na1, @ty, @fa, @presum, @outdate, @depotno, @dc, @dpm)
                        """,
                        new
                        {
                            cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 40), fa = Ansi(r.Fa, 8),
                            ty = Ansi(r.Ty, 14), unit = Ansi(r.Unit, 4), posi = Ansi(r.Posi, 10), r.Jkamount,
                            oprc = r.Oprc, iprj = r.Iprj, iprc = r.Iprc, paper = Ansi(paper, 15),
                            gno = Ansi(gno, 10), gname = Ansi(r.Gname, 40), per = Ansi(per, 10),
                            xno = txno, tno, cxnno = Ansi(tcxnno, 18), presum, outdate = today,
                            depotno = Ansi(r.Depotno, 10), dc = Ansi(tdc, 8), dpm = Ansi(dpm, 10),
                        },
                        transaction: tx, cancellationToken: ct));
                    result.DirectSaleLines++;
                }
                else
                {
                    // 3b) ISAPPE('tbisorsm', id, jkamount) 入库（源行字段取草稿行：oprb/xno/note/dc）
                    await IsappeEngine.ApplyAsync(
                        conn, tx,
                        new IsappeSource(dpm, r.Cp, r.Nno, r.Na1, r.Ty, r.Fa, r.Unit,
                            r.Iprj, r.Iprc, r.Oprc, 0, r.Oprb, r.Posi, r.Depotno, r.Note, r.Xno, r.Dc),
                        r.Jkamount, prm, IsappeLibrary.Client, today, ct);
                    result.StockInLines++;
                }

                // 4) 订单累计（row220 L181-183：PASSTH 4 参 update=游标首行 MIN(qty+行量,oqty) 后
                //    按条件 UPDATE 全部匹配行（无 id 限定）——同键多行时每行都累计到同一值，2026-11-02 修正）
                var orderWhere = "cp = @cp AND cno = @cno AND nno = @nno AND dpm = @dpm" +
                                 (string.IsNullOrWhiteSpace(r.Orno) ? "" : " AND orno = @orno");
                var orderArgs = new
                {
                    cp = Ansi(r.Cp, 10), cno = Ansi(r.Cno, 15), nno = Ansi(r.Nno, 18), dpm = Ansi(dpm, 10), orno = Ansi(r.Orno, 14)
                };
                var order = await conn.QueryFirstOrDefaultAsync<(int id, decimal oqty, decimal qty)>(
                    new CommandDefinition($"SELECT TOP (1) id, oqty, qty FROM tborder WITH (UPDLOCK) WHERE {orderWhere}",
                        orderArgs, transaction: tx, cancellationToken: ct));
                if (order.id != 0)
                {
                    var newQty = Math.Min(order.qty + r.Jkamount, order.oqty);
                    await conn.ExecuteAsync(new CommandDefinition(
                        $"UPDATE tborder SET qty = @qty WHERE {orderWhere}",
                        new { qty = newQty, cp = orderArgs.cp, cno = orderArgs.cno, nno = orderArgs.nno, dpm = orderArgs.dpm, orno = orderArgs.orno },
                        transaction: tx, cancellationToken: ct));
                }

                // 5) 更新档案进价（row220 L184-186 CHKIPRJ）
                if (cmd.UpdateProfileIprj)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbprnoty SET iprj = @iprj WHERE cp = @cp AND nno = @nno",
                        new { iprj = r.Iprj, cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18) },
                        transaction: tx, cancellationToken: ct));
                }

                // 6) 删草稿行（row220 L187）
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tbisorsm WHERE id = @id AND dpm = @dpm",
                    new { r.id, dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));

                await tx.CommitAsync(ct);
                result.ProcessedLines++;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        return result;
    }

    /// <summary>配件默认仓位（现有库存批次任一非废品仓位；row156 TOP 1 语义，此处按 nno 聚合一次取回）。</summary>
    public async Task<Dictionary<string, string>> GetDefaultPosisAsync(string cp, string dpm, string depotno, string fpcw,
        IEnumerable<string> nnos, CancellationToken ct = default)
    {
        var list = nnos.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (list.Count == 0) return [];
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string nno, string posi)>(new CommandDefinition(
            "SELECT RTRIM(nno), RTRIM(MIN(posi)) FROM tbisto WITH (NOLOCK) " +
            "WHERE cp = @cp AND depotno = @depotno AND dpm = @dpm AND RTRIM(posi) <> @fpcw AND nno IN @nnos GROUP BY nno",
            new { cp = Ansi(cp, 10), depotno = Ansi(depotno, 10), dpm = Ansi(dpm, 10), fpcw = Ansi(fpcw, 10), nnos = list },
            cancellationToken: ct));
        return rows.AsList().ToDictionary(r => r.nno, r => r.posi);
    }

    /// <summary>
    /// 订单直接入库（2026-11-03 改版）：无 tbisorsm 草稿，到货数由前端传入，row220 写路径逐行事务：
    /// 重读 tborder 行（校验缺口）→ tbistoed 台账 → tbprnoty 缺档建档 → 直发 tbsada / ISAPPE 入库 →
    /// tborder.qty 累计（封顶订数）→ 可选更新档案进价。
    /// </summary>
    public async Task<ConfirmOrderInResult> ConfirmByOrderAsync(ConfirmByOrderCommand cmd, string dpm, string per,
        string pDepotno, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var result = new ConfirmOrderInResult();
        if (cmd.Lines.Count == 0)
            throw new AppException("NO_DRAFT", "现在入库选项没有入库数据可以更新库存！", 422);

        await using var conn = await factory.OpenAsync(ct);
        var (posigl, _, fpcw) = await LoadSwitchesAsync(conn, null, ct);
        var prm = await IsappeEngine.LoadParamsAsync(conn, null, ct);
        string? lastGyname = null, lastSaleGname = null, paper = "", gyno = "";

        foreach (var l in cmd.Lines)
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                // 0) 重读订单行（UPDLOCK；gname/note 以库内为准）并校验缺口
                //    2026-11-03 改版：在途单以 orno 标识（并入在途后 cno 清空、type='历史'）
                var orderWhere = "cp = @cp AND orno = @orno AND nno = @nno AND dpm = @dpm AND RTRIM(ISNULL(type, '')) = '历史'";
                var orderArgs = new
                {
                    cp = Ansi(l.Cp, 10), orno = Ansi(cmd.Cno, 15), nno = Ansi(l.Nno, 18), dpm = Ansi(dpm, 10),
                };
                var order = await conn.QueryFirstOrDefaultAsync<(int id, decimal oqty, decimal qty, string gname, string note)>(
                    new CommandDefinition(
                        $"SELECT TOP (1) id, oqty, qty, ISNULL(gname, ''), RTRIM(ISNULL(note, '')) FROM tborder WITH (UPDLOCK) WHERE {orderWhere}",
                        orderArgs, transaction: tx, cancellationToken: ct));
                if (order.id == 0)
                    throw new AppException("ORDER_ROW_MISSING", $"订单行不存在：{l.Nno.Trim()}", 422);
                if (l.ArriveQty < 0)
                    throw new AppException("QTY_NEGATIVE", $"{l.Nno.Trim()} 到货数不能为负数", 422);
                if (l.ArriveQty > order.oqty - order.qty)
                    throw new AppException("QTY_OVER", $"{l.Nno.Trim()} 到货数 {l.ArriveQty:0.##} 超过缺口 {order.oqty - order.qty:0.##}", 422);

                var direct = !string.IsNullOrWhiteSpace(order.gname) && l.ArriveQty != 0;
                var depotno = direct ? pDepotno : cmd.Depotno.Trim();

                // 1) 仓位（空 = 现有库存批次任一非废品仓位，row156 同语义）+ P_POSIGL 校验（row220 L32-51）
                var posi = l.Posi?.Trim() ?? "";
                if (posi.Length == 0 && !direct)
                {
                    posi = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT TOP (1) RTRIM(posi) FROM tbisto WITH (NOLOCK) WHERE nno = @nno AND cp = @cp " +
                        "AND depotno = @depotno AND dpm = @dpm AND RTRIM(posi) <> @fpcw",
                        new { nno = Ansi(l.Nno, 18), cp = Ansi(l.Cp, 10), depotno = Ansi(depotno, 10), dpm = Ansi(dpm, 10), fpcw = Ansi(fpcw, 10) },
                        transaction: tx, cancellationToken: ct)) ?? "";
                }
                if (posigl)
                {
                    if (posi.Length == 0)
                        throw new AppException("POSI_REQUIRED", $"{l.Nno.Trim()} 仓位不能为空！（系统参数：仓位必须先定义）", 422);
                    var p = await conn.QueryFirstOrDefaultAsync<(string nno, bool isfree)>(
                        new CommandDefinition(
                            "SELECT TOP (1) RTRIM(nno), ISNULL(isfree, 0) FROM tbposi WITH (NOLOCK) WHERE posi = @posi",
                            new { posi = Ansi(posi, 10) }, transaction: tx, cancellationToken: ct));
                    if (p.nno == null)
                        throw new AppException("POSI_UNDEFINED", $"{l.Nno.Trim()},{posi} 仓未定义！（系统参数：仓位必须先定义）", 422);
                    if (p.nno != l.Nno.Trim() && !p.isfree)
                        throw new AppException("POSI_OCCUPIED", $"{posi} 仓定义为存放 {p.nno}，不能存放 {l.Nno.Trim()}！", 422);
                }

                var presum = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WITH (UPDLOCK, HOLDLOCK) " +
                    "WHERE cp = @cp AND nno = @nno AND depotno = @depotno AND dpm = @dpm",
                    new { cp = Ansi(l.Cp, 10), nno = Ansi(l.Nno, 18), depotno = Ansi(depotno, 10), dpm = Ansi(dpm, 10) },
                    transaction: tx, cancellationToken: ct));

                // 供应商号（gyname 变化时重查，row220 L120-124）
                if (l.Gyname.Trim() != lastGyname)
                {
                    gyno = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT TOP (1) RTRIM(gyno) FROM tbgugys WITH (NOLOCK) WHERE gyname = @gyname",
                        new { gyname = Ansi(l.Gyname, 60) }, transaction: tx, cancellationToken: ct)) ?? "";
                    lastGyname = l.Gyname.Trim();
                }

                // 2) 入库台账（row220 L125：27 列原样；seano=订单号便于追溯）
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbistoed
                        (pno, cno, box, seano, isord, cp, nno, na1, ty, fa, unit, dc, item, posi, type, fkfs,
                         gyname, gyno, depotno, jkamount, kcamount, iprj, iprc, oprc, indate, per, Freight, dpm)
                    VALUES
                        (@pno, @cno, @box, @seano, 'Y', @cp, @nno, @na1, @ty, @fa, @unit, @dc, @item, @posi, N'入库', @fkfs,
                         @gyname, @gyno, @depotno, @jkamount, @presum, @iprj, @iprc, @oprc, @indate, @per, 0, @dpm)
                    """,
                    new
                    {
                        pno = Ansi(cmd.Pno, 12), cno = Ansi(cmd.Cno, 15), box = Ansi(cmd.Box, 10), seano = Ansi(cmd.Cno, 15),
                        cp = Ansi(l.Cp, 10), nno = Ansi(l.Nno, 18), na1 = Ansi(l.Na1, 40), ty = Ansi(l.Ty, 14),
                        fa = Ansi(l.Fa, 8), unit = Ansi(l.Unit, 4), dc = Ansi(l.Dc, 8), l.Item,
                        posi = Ansi(posi, 10), fkfs = Ansi(l.Fkfs, 10), gyname = Ansi(l.Gyname, 60),
                        gyno = Ansi(gyno, 10), depotno = Ansi(depotno, 10), jkamount = l.ArriveQty, presum,
                        iprj = l.Iprj, iprc = l.Iprc, oprc = l.Oprc, indate = today, per = Ansi(per, 10),
                        dpm = Ansi(dpm, 10),
                    },
                    transaction: tx, cancellationToken: ct));

                // 3) tbprnoty 缺档自动建档（row220 L127-153 + tbname0 同步）
                var profile = await conn.QueryFirstOrDefaultAsync<(int id, decimal iprj, string na1)>(new CommandDefinition(
                    "SELECT TOP (1) id, iprj, RTRIM(na1) FROM tbprnoty WITH (UPDLOCK) WHERE cp = @cp AND nno = @nno",
                    new { cp = Ansi(l.Cp, 10), nno = Ansi(l.Nno, 18) },
                    transaction: tx, cancellationToken: ct));
                if (profile.id == 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, note, iprj, uqty, cxnno, oprc_cp, oprc_min, cpict)
                        VALUES (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @note, @iprj, 0, @cxnno, 0, 0, '')
                        """,
                        new
                        {
                            cp = Ansi(l.Cp, 10), nno = Ansi(l.Nno, 18), na1 = Ansi(l.Na1, 40), ty = Ansi(l.Ty, 14),
                            fa = Ansi(l.Fa, 8), dc = Ansi(l.Dc, 8), unit = Ansi(l.Unit, 4), note = Ansi("", 254),
                            iprj = l.Iprj, cxnno = Ansi("", 18),
                        },
                        transaction: tx, cancellationToken: ct));
                    await Name0Sync.SyncAsync(conn, tx, dm, l.Na1, ct);
                    await Name0Sync.SyncTyAsync(conn, tx, dm, l.Ty, ct);
                }

                // 4a) 直发（gname 非空且数量≠0）：销售单 + tbsada（kcid 缺省 0 → 不扣库存）
                if (direct)
                {
                    if (string.IsNullOrEmpty(paper) || lastSaleGname != order.gname)
                    {
                        paper = await docNo.GetNewDocNoAsync("销售单", dpm, DocNoService.SaleTarget, ct);
                        result.Papers.Add(paper);
                        lastSaleGname = order.gname;
                    }
                    var (txno, tno, tcxnno, tdc) = await conn.QueryFirstOrDefaultAsync<(int, int, string, string)>(
                        new CommandDefinition(
                            "SELECT TOP (1) ISNULL(xno, 0), ISNULL(tno, 0), RTRIM(ISNULL(cxnnO, '')), RTRIM(ISNULL(dc, '')) " +
                            "FROM tbprnoty WITH (NOLOCK) WHERE cp = @cp AND nno = @nno",
                            new { cp = Ansi(l.Cp, 10), nno = Ansi(l.Nno, 18) },
                            transaction: tx, cancellationToken: ct));
                    var gno = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT TOP (1) RTRIM(gno) FROM tbgu WITH (NOLOCK) WHERE gname = @gname",
                        new { gname = Ansi(order.gname, 40) }, transaction: tx, cancellationToken: ct)) ?? "";

                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO tbsada
                            (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc, paper,
                             gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, outdate, depotno, dc, dpm)
                        VALUES
                            (@cp, @nno, @na1, @fa, @ty, @unit, N'销售', @posi, @jkamount, @oprc, @oprc, @iprj, @iprc, @paper,
                             @gno, @gname, @per, @xno, @tno, @cxnno, @nno, @na1, @ty, @fa, @presum, @outdate, @depotno, @dc, @dpm)
                        """,
                        new
                        {
                            cp = Ansi(l.Cp, 10), nno = Ansi(l.Nno, 18), na1 = Ansi(l.Na1, 40), fa = Ansi(l.Fa, 8),
                            ty = Ansi(l.Ty, 14), unit = Ansi(l.Unit, 4), posi = Ansi(posi, 10), jkamount = l.ArriveQty,
                            oprc = l.Oprc, iprj = l.Iprj, iprc = l.Iprc, paper = Ansi(paper, 15),
                            gno = Ansi(gno, 10), gname = Ansi(order.gname, 40), per = Ansi(per, 10),
                            xno = txno, tno, cxnno = Ansi(tcxnno, 18), presum, outdate = today,
                            depotno = Ansi(depotno, 10), dc = Ansi(tdc, 8), dpm = Ansi(dpm, 10),
                        },
                        transaction: tx, cancellationToken: ct));
                    result.DirectSaleLines++;
                }
                else
                {
                    // 4b) ISAPPE 入库（源行字段：note 取订单行备注，xno=0 与旧导入路径一致）
                    await IsappeEngine.ApplyAsync(
                        conn, tx,
                        new IsappeSource(dpm, l.Cp, l.Nno, l.Na1, l.Ty, l.Fa, l.Unit,
                            l.Iprj, l.Iprc, l.Oprc, 0, l.Oprb, posi, depotno, order.note, 0, l.Dc),
                        l.ArriveQty, prm, IsappeLibrary.Client, today, ct);
                    result.StockInLines++;
                }

                // 5) 订单累计（row220 L181-183：MIN(qty+行量, oqty)）
                var newQty = Math.Min(order.qty + l.ArriveQty, order.oqty);
                await conn.ExecuteAsync(new CommandDefinition(
                    $"UPDATE tborder SET qty = @qty WHERE {orderWhere}",
                    new { qty = newQty, cp = orderArgs.cp, orno = orderArgs.orno, nno = orderArgs.nno, dpm = orderArgs.dpm },
                    transaction: tx, cancellationToken: ct));

                // 6) 更新档案进价（row220 L184-186 CHKIPRJ）
                if (cmd.UpdateProfileIprj)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbprnoty SET iprj = @iprj WHERE cp = @cp AND nno = @nno",
                        new { iprj = l.Iprj, cp = Ansi(l.Cp, 10), nno = Ansi(l.Nno, 18) },
                        transaction: tx, cancellationToken: ct));
                }

                await tx.CommitAsync(ct);
                result.ProcessedLines++;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        // 7) 2026-11-03 需求：确认入库完成后删除该在途单（含剩余未到货缺口行——到货一次性处理语义）
        if (result.ProcessedLines > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tborder WHERE cp = @cp AND orno = @orno AND dpm = @dpm AND RTRIM(ISNULL(type, '')) = '历史'",
                new { cp = Ansi(cmd.Lines[0].Cp, 10), orno = Ansi(cmd.Cno, 15), dpm = Ansi(dpm, 10) },
                cancellationToken: ct));
        }
        return result;
    }

    /// <summary>tbsysset 开关读取：「仓位必须先定义」/「配件入库不许空仓位」（P_RKPOSINULL）/
    /// 「废品仓位标识」（缺省 废）。</summary>
    public async Task<(bool Posigl, bool Rkposinull, string Fpcw)> GetSwitchesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await LoadSwitchesAsync(conn, null, ct);
    }

    /// <summary>拉单价率（row3 Init 语义）+ 外币为含税价（L3273）。</summary>
    public async Task<(decimal Bl1, decimal Bl2, decimal Bl3, bool Wbtohs)> GetPriceRatiosAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(string name, decimal? valuen, string? valuec)>(
            new CommandDefinition(
                "SELECT RTRIM(name), valuen, valuec FROM tbsysset WITH (NOLOCK) WHERE type = 'system' " +
                "AND name IN (N'进价比率', N'售价比率', N'批价比率', N'外币为含税价')",
                cancellationToken: ct))).AsList();
        decimal Parse(string name, decimal dflt) =>
            decimal.TryParse(rows.FirstOrDefault(x => x.name == name).valuec?.Trim(), out var v) ? v : dflt;
        var wbtohs = rows.Any(x => x.name == "外币为含税价" && (x.valuen ?? 0) != 0);
        return (Parse("进价比率", 0.080m), Parse("售价比率", 0.050m), Parse("批价比率", 0.090m), wbtohs);
    }

    internal static async Task<(bool Posigl, bool Rkposinull, string Fpcw)> LoadSwitchesAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<(string name, bool? valuel, string? valuec)>(
            new CommandDefinition(
                "SELECT RTRIM(name), valuel, valuec FROM tbsysset WITH (NOLOCK) WHERE type = 'system' " +
                "AND name IN (N'仓位必须先定义', N'配件入库不许空仓位', N'废品仓位标识')",
                transaction: tx, cancellationToken: ct))).AsList();
        var posigl = rows.Any(x => x.name == "仓位必须先定义" && (x.valuel ?? false));
        var rkposinull = rows.Any(x => x.name == "配件入库不许空仓位" && (x.valuel ?? false));
        var fpcw = rows.FirstOrDefault(x => x.name == "废品仓位标识").valuec;
        return (posigl, rkposinull, string.IsNullOrWhiteSpace(fpcw) ? "废" : fpcw.Trim());
    }

    private sealed class ConfirmRow
    {
        public int id { get; set; }
        public string Cp { get; set; } = "";
        public string Cno { get; set; } = "";
        public int Item { get; set; }
        public string Nno { get; set; } = "";
        public string Na1 { get; set; } = "";
        public string Ty { get; set; } = "";
        public string Fa { get; set; } = "";
        public string Dc { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal Jkamount { get; set; }
        public decimal Iprj { get; set; }
        public decimal Iprc { get; set; }
        public decimal Oprc { get; set; }
        public decimal Oprb { get; set; }
        public int Xno { get; set; }
        public string Posi { get; set; } = "";
        public string Depotno { get; set; } = "";
        public string Box { get; set; } = "";
        public string Seano { get; set; } = "";
        public string Fkfs { get; set; } = "";
        public string Gyname { get; set; } = "";
        public string Orno { get; set; } = "";
        public string Note { get; set; } = "";
        public string Gname { get; set; } = "";
        public decimal Freight { get; set; }
    }

    private sealed class DraftRow
    {
        public int id { get; set; }
        public string Cp { get; set; } = "";
        public string Cno { get; set; } = "";
        public int Item { get; set; }
        public string Nno { get; set; } = "";
        public string Na1 { get; set; } = "";
        public string Ty { get; set; } = "";
        public string Fa { get; set; } = "";
        public decimal Jkamount { get; set; }
        public decimal Fpamount { get; set; }
        public decimal Iprj { get; set; }
        public decimal Iprc { get; set; }
        public decimal Oprc { get; set; }
        public decimal Oprb { get; set; }
        public string Posi { get; set; } = "";
        public string Depotno { get; set; } = "";
        public string Seano { get; set; } = "";
        public string Orno { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Fkfs { get; set; } = "";
        public string Gyname { get; set; } = "";
        public string Dc { get; set; } = "";
        public string Note { get; set; } = "";
        public string Box { get; set; } = "";
        public string Pno { get; set; } = "";
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
