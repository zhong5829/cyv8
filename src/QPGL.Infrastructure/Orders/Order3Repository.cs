using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.Parts;

namespace QPGL.Infrastructure.Orders;

public interface IOrder3Repository
{
    /// <summary>主查询（fr_order3 Q2：type='历史' 工作集 + 4 个 left join 派生列）。</summary>
    Task<IReadOnlyList<OrderRow>> SearchAsync(OrderSearchQuery query, string dpm, string cp, CancellationToken ct = default);

    /// <summary>未合并散单汇总（Q7：cno=' ' 单空格，按 orno 聚合）。</summary>
    Task<IReadOnlyList<UnmergedOrderRow>> GetUnmergedAsync(string dpm, string cp, CancellationToken ct = default);

    /// <summary>合同号带出表头默认值（Q3：cno 必须已存在；max fkfs/gyname/orderdate/bzlx）。</summary>
    Task<ContractHeader?> GetContractHeaderAsync(string cno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>付款方式下拉（Q8：tbsysset type='guno'）。</summary>
    Task<IReadOnlyList<string>> GetFkfsOptionsAsync(CancellationToken ct = default);

    /// <summary>新增订单行（W1；cno 已存在校验 + orno 跨合同唯一校验 + 产品建档联动）。</summary>
    Task<int> CreateAsync(OrderLineInput input, string dpm, string cp, string per, CancellationToken ct = default);

    /// <summary>修改订单行（W2：16 列，where id= 不附加 tbxzb）。</summary>
    Task UpdateAsync(int id, OrderLineInput input, string cp, CancellationToken ct = default);

    /// <summary>删除当前行（W3：delete by id）。</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>批改：改合同号（W4，可选 item 范围）。</summary>
    Task<int> ChangeCnoAsync(string fromCno, string toCno, int? itemFrom, int? itemTo, string dpm, string cp, CancellationToken ct = default);

    /// <summary>批改：重排 item（W5：按 id 序 1..n）。</summary>
    Task<int> ReorderItemsAsync(string cno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>批改：删除合同（W6：by cno）。</summary>
    Task<int> DeleteContractAsync(string cno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>批改：恢复合同号（W7：type 清空即退出'历史'工作集）。</summary>
    Task<int> RestoreContractAsync(string cno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>批改：改订单号（W8：按合同号或订单号定位）。</summary>
    Task<int> ChangeOrnoAsync(string locateCno, string newOrno, bool locateByOrno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>批改：改客户名称（W9）。</summary>
    Task<int> ChangeGnameAsync(string locateCno, string gname, string dpm, string cp, CancellationToken ct = default);

    /// <summary>批改：改供应商名称（W10）。</summary>
    Task<int> ChangeGynameAsync(string locateCno, string gyname, string dpm, string cp, CancellationToken ct = default);

    /// <summary>批改：改预计到货日期（W11：tborder.getdate，2000~2089 校验）。</summary>
    Task<int> ChangeEtaAsync(string locateCno, DateTime eta, string dpm, string cp, CancellationToken ct = default);

    /// <summary>合并散单入合同（W12：update cno,gyname,fkfs where orno=）。</summary>
    Task<int> MergeOrdersAsync(string toCno, string gyname, string fkfs, IEnumerable<string> ornos, string dpm, string cp, CancellationToken ct = default);
}

/// <summary>主网格行（Q2 列集）。</summary>
public sealed class OrderRow
{
    public string Cno { get; set; } = "";
    public string Orno { get; set; } = "";
    public string Gyname { get; set; } = "";
    public int Item { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    /// <summary>当前库存（tbisto 按 cp+nno 汇总，P_DPMUSEM 包含匹配）。</summary>
    public decimal Kcamount { get; set; }
    public decimal Oqty { get; set; }
    public decimal Qty { get; set; }
    public decimal Iprj { get; set; }
    public string Oenno { get; set; } = "";
    public DateTime Orderdate { get; set; }
    public decimal Oprc { get; set; }
    public string Per { get; set; } = "";
    public string Bzlx { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Cp { get; set; } = "";
    public string Note { get; set; } = "";
    public string Nnp { get; set; } = "";
    public string Nnq { get; set; } = "";
    public int Id { get; set; }
    public string Ch { get; set; } = "";
    public string Jdy { get; set; } = "";
    public string Jcdate { get; set; } = "";
    /// <summary>预计到货日期（tborder.getdate）。</summary>
    public DateTime Eta { get; set; }
}

public sealed class UnmergedOrderRow
{
    public string Orno { get; set; } = "";
    public decimal Iprj { get; set; }
    public DateTime Orderdate { get; set; }
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public bool Mark { get; set; }
}

public sealed class ContractHeader
{
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public DateTime Orderdate { get; set; }
    public string Bzlx { get; set; } = "";
}

public sealed class OrderLineInput
{
    public string Cno { get; set; } = "";
    public string Orno { get; set; } = "";
    public int Item { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Oqty { get; set; }
    public decimal Iprj { get; set; }
    public decimal Oprc { get; set; }
    public DateTime Orderdate { get; set; } = DateTime.Today;
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Note { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Bzlx { get; set; } = "";
}

public sealed class OrderSearchQuery
{
    public string Cno { get; set; } = "";
    public string Orno { get; set; } = "";
    public string NnoPrefix { get; set; } = "";
    public string Na1Contains { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
    /// <summary>快捷：已到 oqty=qty / 在途 qty&lt;oqty。</summary>
    public string Quick { get; set; } = "";
}

/// <summary>
/// 订货库管理仓储（菜单 F.2，权限码 F2；fr_order3 契约：开发文档/契约摘录/订货库fr_order3契约.md）。
/// tbxzb 自动过滤（PASSTH 语义，rscar 实测）：tborder → `cp=?p_cp and ?p_dpmusem like '%'+rtrim(dpm)+'%'`；
/// where 以 id= 开头（W2/W3）不追加；update/delete/select 同受影响。cno/orno 全人工（无取号）。
/// 旧系统逐语句自动提交；多行批改（W5/W12）新系统包单事务（备案行为差异）。
/// </summary>
public sealed class Order3Repository(SqlConnectionFactory factory, Parts.IDmService dm) : IOrder3Repository
{
    // tbxzb 条件（tborder 两行）：select/update/delete/含 cno= 等非 id 前缀 where 时追加
    private const string Xzb = " AND cp = @xcp AND @p_dpmusem LIKE '%' + RTRIM(dpm) + '%'";

    private static string XzbArgs(DynamicParameters args, string cp)
    {
        args.Add("xcp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        args.Add("p_dpmusem", new DbString { Value = cp, IsAnsi = true, Length = 50 });
        return "";
    }

    public async Task<IReadOnlyList<OrderRow>> SearchAsync(OrderSearchQuery query, string dpm, string cp, CancellationToken ct = default)
    {
        // 查询为 TEXT+裸 SQLEXEC 手工拼 READXZB（Q2 L24），等价追加两条件
        var where = "a.type = '历史'" + Xzb;
        var args = new DynamicParameters();
        XzbArgs(args, cp);
        args.Add("dpm", new DbString { Value = dpm, IsAnsi = true, Length = 10 });
        if (!string.IsNullOrWhiteSpace(query.Cno))
        {
            where += " AND a.cno = @cno";
            args.Add("cno", new DbString { Value = query.Cno.Trim(), IsAnsi = true, Length = 15 });
        }
        if (!string.IsNullOrWhiteSpace(query.Orno))
        {
            where += " AND a.orno = @orno";
            args.Add("orno", new DbString { Value = query.Orno.Trim(), IsAnsi = true, Length = 15 });
        }
        if (!string.IsNullOrWhiteSpace(query.NnoPrefix))
        {
            where += " AND a.nno LIKE @nno";
            args.Add("nno", new DbString { Value = query.NnoPrefix.Trim() + "%", IsAnsi = true, Length = 19 });
        }
        if (!string.IsNullOrWhiteSpace(query.Na1Contains))
        {
            where += " AND a.na1 LIKE @na1";
            args.Add("na1", new DbString { Value = "%" + query.Na1Contains.Trim() + "%", IsAnsi = true, Length = 42 });
        }
        if (!string.IsNullOrWhiteSpace(query.Gyname))
        {
            where += " AND a.gyname = @gyname";
            args.Add("gyname", new DbString { Value = query.Gyname.Trim(), IsAnsi = true, Length = 60 });
        }
        if (!string.IsNullOrWhiteSpace(query.Gname))
        {
            where += " AND a.gname = @gname";
            args.Add("gname", new DbString { Value = query.Gname.Trim(), IsAnsi = true, Length = 40 });
        }
        if (query.Quick == "已到") where += " AND a.oqty = a.qty";
        else if (query.Quick == "在途") where += " AND a.qty < a.oqty";

        // Q2 L30-47 原样（旧式 *= 改写 LEFT JOIN；NULL 口径 ISNULL 保持）
        var sql = $"""
            SELECT RTRIM(a.cno) cno, RTRIM(a.orno) orno, RTRIM(a.gyname) gyname, a.item, RTRIM(a.nno) nno,
                   RTRIM(a.na1) na1, RTRIM(a.ty) ty, ISNULL(c.kcamount, 0) kcamount, a.oqty, a.qty, a.iprj,
                   CASE WHEN g.oenno IS NULL OR RTRIM(LTRIM(g.oenno)) = '' THEN RTRIM(a.nno) ELSE RTRIM(g.oenno) END oenno,
                   a.orderdate, a.oprc, RTRIM(a.per) per, RTRIM(a.bzlx) bzlx, RTRIM(a.fa) fa, RTRIM(a.unit) unit,
                   RTRIM(a.fkfs) fkfs, RTRIM(a.gname) gname, RTRIM(a.cp) cp, RTRIM(a.note) note,
                   ISNULL(b.nnp, '') nnp, ISNULL(b.nnq, '') nnq, a.id,
                   ISNULL(d.ch, '') ch, ISNULL(d.jdy, '') jdy, ISNULL(d.jcdate, '') jcdate,
                   ISNULL(a.getdate, '1900-01-01') eta
            FROM tborder a WITH (NOLOCK)
            LEFT JOIN (SELECT cp cp1, nno nno1, nnp, nnq FROM tbprnoty) b ON a.cp = b.cp1 AND a.nno = b.nno1
            LEFT JOIN (SELECT cp cp2, nno nno2, SUM(kcamount) kcamount FROM tbisto WHERE @p_dpmusem LIKE '%' + RTRIM(dpm) + '%' GROUP BY cp, nno) c ON a.cp = c.cp2 AND a.nno = c.nno2
            LEFT JOIN (SELECT gh, ch, jdy, jcdate FROM tbxlrc) d ON a.orno = d.gh
            LEFT JOIN (SELECT cp cp3, nno nno3, MAX(oenno) oenno, gyname gyname3 FROM tbgys GROUP BY cp, nno, gyname) g ON a.nno = g.nno3 AND a.cp = g.cp3 AND a.gyname = g.gyname3
            WHERE {where}
            ORDER BY a.cno, a.id
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OrderRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<UnmergedOrderRow>> GetUnmergedAsync(string dpm, string cp, CancellationToken ct = default)
    {
        // Q7（_T.row67:L4）：cno=' '（单空格）=未合并；PASSTH 自动追加 tbxzb
        var sql = $"""
            SELECT RTRIM(orno) orno, SUM(iprj * oqty) iprj, MAX(orderdate) orderdate,
                   MAX(RTRIM(fkfs)) fkfs, MAX(RTRIM(gyname)) gyname, CAST(0 AS bit) mark
            FROM tborder WITH (NOLOCK)
            WHERE cno = ' '{Xzb}
            GROUP BY orno
            ORDER BY orno
            """;
        var args = new DynamicParameters();
        XzbArgs(args, cp);
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UnmergedOrderRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ContractHeader?> GetContractHeaderAsync(string cno, string dpm, string cp, CancellationToken ct = default)
    {
        // Q3（A_.row113:L5）：max(fkfs/gyname/orderdate/bzlx) from tborder where cno=?；查无 → 无此合同号
        var sql = $"""
            SELECT MAX(RTRIM(fkfs)) fkfs, MAX(RTRIM(gyname)) gyname, MAX(orderdate) orderdate, MAX(RTRIM(bzlx)) bzlx
            FROM tborder WITH (NOLOCK) WHERE cno = @cno{Xzb}
            """;
        var args = new DynamicParameters(new { cno = new DbString { Value = cno.Trim(), IsAnsi = true, Length = 15 } });
        XzbArgs(args, cp);
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ContractHeader>(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> GetFkfsOptionsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'guno' ORDER BY id", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> CreateAsync(OrderLineInput input, string dpm, string cp, string per, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Cno)) throw new AppException("CNO_REQUIRED", "合同号不能为空", 422);
        if (string.IsNullOrWhiteSpace(input.Nno)) throw new AppException("NNO_REQUIRED", "零件号不能为空", 422);
        if (input.Oqty <= 0) throw new AppException("QTY_INVALID", "订货数量必须大于 0", 422);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // orno 跨合同唯一（Qs.row99:L9-14）
            if (!string.IsNullOrWhiteSpace(input.Orno))
            {
                var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tborder WITH (NOLOCK) WHERE cno <> @cno AND orno = @orno",
                    new { cno = Ansi(input.Cno, 15), orno = Ansi(input.Orno, 15) }, transaction: tx, cancellationToken: ct));
                if (dup > 0) throw new AppException("ORNO_DUP", "该订单号在其他合同号下已存在！", 409);
            }

            // cno 必须已存在（Q3）+ item 取当前合同 max(item)+1（模糊点 A2 重建口径）
            var header = await GetContractHeaderInTxAsync(conn, tx, input.Cno, cp, ct);
            if (header is null) throw new AppException("CNO_NOT_FOUND", "无此合同号，请重新输入！", 404);
            var maxItem = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT MAX(item) FROM tborder WITH (NOLOCK) WHERE cno = @cno",
                new { cno = Ansi(input.Cno, 15) }, transaction: tx, cancellationToken: ct));
            var item = input.Item > 0 ? input.Item : (maxItem ?? 0) + 1;

            // W1 insert（19 列 + dpm 自动补）
            var newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT INTO tborder
                    (cp, cno, item, nno, na1, oqty, iprj, orderdate, ty, fa, note, type, gyname, gname, fkfs, bzlx, oprc, orno, per, dpm)
                OUTPUT INSERTED.id
                VALUES
                    (@cp, @cno, @item, @nno, @na1, @oqty, @iprj, @orderdate, @ty, @fa, @note, '历史', @gyname, @gname, @fkfs, @bzlx, @oprc, @orno, @per, @dpm)
                """,
                new
                {
                    cp = Ansi(cp, 10), cno = Ansi(input.Cno, 15), item, nno = Ansi(input.Nno, 18),
                    na1 = Ansi(input.Na1, 40), oqty = input.Oqty, iprj = input.Iprj, orderdate = input.Orderdate.Date,
                    ty = Ansi(input.Ty, 14), fa = Ansi(input.Fa, 8), note = Ansi(input.Note, 25),
                    gyname = Ansi(input.Gyname, 60), gname = Ansi(input.Gname, 40), fkfs = Ansi(input.Fkfs, 10),
                    bzlx = Ansi(input.Bzlx, 10), oprc = input.Oprc, orno = Ansi(input.Orno, 15), per = Ansi(per, 10),
                    dpm = Ansi(dpm, 10),
                }, transaction: tx, cancellationToken: ct));

            // 产品建档联动（W1 尾部：tbprnoty 只按 nno 无 cp——旧原样 + tbname0 同步 Qs.row99 L34-42）
            var profileExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM tbprnoty WITH (NOLOCK) WHERE nno = @nno",
                new { nno = Ansi(input.Nno, 18) }, transaction: tx, cancellationToken: ct));
            if (profileExists == 0 && !string.IsNullOrWhiteSpace(input.Na1))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO tbprnoty (cp, nno, na1, fa, ty, unit, iprj, dc, note) VALUES (@cp, @nno, @na1, @fa, @ty, @unit, @iprj, @dc, @note)",
                    new
                    {
                        cp = Ansi(cp, 10), nno = Ansi(input.Nno, 18), na1 = Ansi(input.Na1, 40),
                        fa = Ansi(input.Fa, 8), ty = Ansi(input.Ty, 14), unit = Ansi(input.Unit, 4),
                        iprj = input.Iprj, dc = Ansi(input.Dc, 8), note = Ansi(input.Note, 254),
                    }, transaction: tx, cancellationToken: ct));
                await Name0Sync.SyncAsync(conn, tx, dm, input.Na1, ct);
            }

            await tx.CommitAsync(ct);
            return newId;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UpdateAsync(int id, OrderLineInput input, string cp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // W2（Qs.row99:L25）：16 列；where id= 不追加 tbxzb；type/getdate/qty 不动
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tborder SET orno = @orno, cno = @cno, item = @item, nno = @nno, na1 = @na1, oqty = @oqty,
                   iprj = @iprj, orderdate = @orderdate, ty = @ty, fa = @fa, note = @note, gyname = @gyname,
                   fkfs = @fkfs, bzlx = @bzlx, oprc = @oprc, gname = @gname
            WHERE id = @id
            """,
            new
            {
                id, orno = Ansi(input.Orno, 15), cno = Ansi(input.Cno, 15), input.Item, nno = Ansi(input.Nno, 18),
                na1 = Ansi(input.Na1, 40), input.Oqty, input.Iprj, orderdate = input.Orderdate.Date,
                ty = Ansi(input.Ty, 14), fa = Ansi(input.Fa, 8), note = Ansi(input.Note, 25),
                gyname = Ansi(input.Gyname, 60), fkfs = Ansi(input.Fkfs, 10), bzlx = Ansi(input.Bzlx, 10),
                input.Oprc, gname = Ansi(input.Gname, 40),
            }, cancellationToken: ct));
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tborder WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<int> ChangeCnoAsync(string fromCno, string toCno, int? itemFrom, int? itemTo, string dpm, string cp, CancellationToken ct = default)
    {
        // W4（F_.row154:L11-28）：update cno where cno=旧 [+item 范围]；tbxzb 追加
        var where = "cno = @fromCno" + Xzb;
        var args = new DynamicParameters(new { toCno = new DbString { Value = toCno, IsAnsi = true, Length = 15 } });
        args.Add("fromCno", new DbString { Value = fromCno, IsAnsi = true, Length = 15 });
        XzbArgs(args, cp);
        if (itemFrom is { } f && itemTo is { } t)
        {
            where += " AND item >= @itemFrom AND item <= @itemTo";
            args.Add("itemFrom", f);
            args.Add("itemTo", t);
        }
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition($"UPDATE tborder SET cno = @toCno WHERE {where}", args, cancellationToken: ct));
    }

    public async Task<int> ReorderItemsAsync(string cno, string dpm, string cp, CancellationToken ct = default)
    {
        // W5（F_.row154:L34-47）：按 id 序 item=1..n
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var ids = (await conn.QueryAsync<int>(new CommandDefinition(
                "SELECT id FROM tborder WITH (NOLOCK) WHERE cno = @cno ORDER BY id",
                new { cno = Ansi(cno, 15) }, transaction: tx, cancellationToken: ct))).AsList();
            var n = 0;
            foreach (var id in ids)
            {
                n++;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tborder SET item = @item WHERE id = @id", new { item = n, id },
                    transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return n;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> DeleteContractAsync(string cno, string dpm, string cp, CancellationToken ct = default)
    {
        // W6（F_.row154:L64）：delete where cno=（tbxzb 追加）
        await using var conn = await factory.OpenAsync(ct);
        var args = new DynamicParameters(new { cno = new DbString { Value = cno, IsAnsi = true, Length = 15 } });
        XzbArgs(args, cp);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"DELETE tborder WHERE cno = @cno{Xzb}", args, cancellationToken: ct));
    }

    public async Task<int> RestoreContractAsync(string cno, string dpm, string cp, CancellationToken ct = default)
    {
        // W7（F_.row154:L74-75）：type 清空
        await using var conn = await factory.OpenAsync(ct);
        var args = new DynamicParameters(new { cno = new DbString { Value = cno, IsAnsi = true, Length = 15 } });
        XzbArgs(args, cp);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tborder SET type = '' WHERE cno = @cno{Xzb}", args, cancellationToken: ct));
    }

    public async Task<int> ChangeOrnoAsync(string locateCno, string newOrno, bool locateByOrno, string dpm, string cp, CancellationToken ct = default)
    {
        // W8（F_.row154:L87/L91）：按合同号或订单号定位
        await using var conn = await factory.OpenAsync(ct);
        var args = new DynamicParameters(new { newOrno = new DbString { Value = newOrno, IsAnsi = true, Length = 15 } });
        XzbArgs(args, cp);
        var where = locateByOrno ? "orno = @loc" : "cno = @loc";
        args.Add("loc", new DbString { Value = locateCno, IsAnsi = true, Length = 15 });
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tborder SET orno = @newOrno WHERE {where}{Xzb}", args, cancellationToken: ct));
    }

    public async Task<int> ChangeGnameAsync(string locateCno, string gname, string dpm, string cp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var args = new DynamicParameters(new { gname = new DbString { Value = gname, IsAnsi = true, Length = 40 } });
        XzbArgs(args, cp);
        args.Add("loc", new DbString { Value = locateCno, IsAnsi = true, Length = 15 });
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tborder SET gname = @gname WHERE cno = @loc{Xzb}", args, cancellationToken: ct));
    }

    public async Task<int> ChangeGynameAsync(string locateCno, string gyname, string dpm, string cp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var args = new DynamicParameters(new { gyname = new DbString { Value = gyname, IsAnsi = true, Length = 60 } });
        XzbArgs(args, cp);
        args.Add("loc", new DbString { Value = locateCno, IsAnsi = true, Length = 15 });
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tborder SET gyname = @gyname WHERE cno = @loc{Xzb}", args, cancellationToken: ct));
    }

    public async Task<int> ChangeEtaAsync(string locateCno, DateTime eta, string dpm, string cp, CancellationToken ct = default)
    {
        // W11（F_.row154:L148-153）：2000.01.01~2089.01.01 校验
        if (eta < new DateTime(2000, 1, 1) || eta > new DateTime(2089, 1, 1))
            throw new AppException("ETA_INVALID", "预计到货日期超出允许范围（2000.01.01~2089.01.01）", 422);
        await using var conn = await factory.OpenAsync(ct);
        var args = new DynamicParameters(new { eta = eta.Date });
        XzbArgs(args, cp);
        args.Add("loc", new DbString { Value = locateCno, IsAnsi = true, Length = 15 });
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE tborder SET getdate = @eta WHERE cno = @loc{Xzb}", args, cancellationToken: ct));
    }

    public async Task<int> MergeOrdersAsync(string toCno, string gyname, string fkfs, IEnumerable<string> ornos, string dpm, string cp, CancellationToken ct = default)
    {
        // W12（_4_.row208:L20-26）：逐散单 update cno,gyname,fkfs where orno=
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var total = 0;
            foreach (var orno in ornos)
            {
                var args = new DynamicParameters(new
                {
                    cno = new DbString { Value = toCno, IsAnsi = true, Length = 15 },
                    gyname = new DbString { Value = gyname, IsAnsi = true, Length = 60 },
                    fkfs = new DbString { Value = fkfs, IsAnsi = true, Length = 10 },
                    orno = new DbString { Value = orno, IsAnsi = true, Length = 15 },
                });
                XzbArgs(args, cp);
                total += await conn.ExecuteAsync(new CommandDefinition(
                    $"UPDATE tborder SET cno = @cno, gyname = @gyname, fkfs = @fkfs WHERE orno = @orno{Xzb}",
                    args, transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return total;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<ContractHeader?> GetContractHeaderInTxAsync(SqlConnection conn, SqlTransaction tx, string cno, string cp, CancellationToken ct)
    {
        var sql = $"""
            SELECT MAX(RTRIM(fkfs)) fkfs, MAX(RTRIM(gyname)) gyname, MAX(orderdate) orderdate, MAX(RTRIM(bzlx)) bzlx
            FROM tborder WITH (NOLOCK) WHERE cno = @cno{Xzb}
            """;
        var args = new DynamicParameters(new { cno = new DbString { Value = cno.Trim(), IsAnsi = true, Length = 15 } });
        XzbArgs(args, cp);
        return await conn.QueryFirstOrDefaultAsync<ContractHeader>(new CommandDefinition(sql, args, transaction: tx, cancellationToken: ct));
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
