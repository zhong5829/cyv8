using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.Parts;

namespace QPGL.Infrastructure.Orders;

public interface IOrder4Repository
{
    /// <summary>计划单列表（c_dhht：type='' 按 cno 聚合，契约 §3.4）。</summary>
    Task<IReadOnlyList<PlanHeadRow>> GetPlanListAsync(string dpm, string cp, string dpmusem, CancellationToken ct = default);
    /// <summary>确保单头行存在（nno=''、oqty=0；新建/保存按钮用，无行时插入 → 空单也出现在计划单列表）。</summary>
    Task<bool> EnsureHeadRowAsync(string cno, DateTime orderdate, string gyname, string dpm, string cp, string dpmusem, string per, CancellationToken ct = default);

    /// <summary>新增计划单号防重（cno or orno 双向，契约 §4）。</summary>
    Task ValidateNewCnoAsync(string cno, CancellationToken ct = default);

    /// <summary>打开计划单明细（c_dhorder，契约 §3.5；suggestExp=选中公式表达式，明细 ydqty 列公式求值）。</summary>
    Task<IReadOnlyList<PlanLineRow>> GetPlanLinesAsync(string cno, string dpm, string cp, string dpmusem,
        string? suggestExp, DateTime sugDate1, DateTime sugDate2, CancellationToken ct = default);

    /// <summary>属性检索（PR_FILT 普通分支 ISOLD=false，契约 §3.2/§3.3）。</summary>
    Task<IReadOnlyList<PartPickRow>> SearchPartsAsync(PartPickQuery query, string dpm, string cp, string dpmusem, string? suggestExp, CancellationToken ct = default);

    /// <summary>选料页批量实时聚合（2026-11-04 弱网缓存）：按本页 nno 集合一次往返取
    /// 库存汇总（含互配件 mx）/最新批次价/在途/警戒值/区间出库量，键=RTRIM(nno)；
    /// 同时带回 tbprnoty 增量水位与新增属性行（其他电脑新增配件实时可见）。</summary>
    Task<PickAggLiveResult> GetPickAggregatesAsync(string cp, string dpm, string dpmusem,
        IReadOnlyCollection<string> nnos, DateTime date1, DateTime date2, int lastAttrMaxId, CancellationToken ct = default);

    /// <summary>参考公式建议订数（契约 §3.8：对单件执行公式返回 oqty；0=不足订。日期区间=选料查询条日期；P02=区间月均，2026-11-04 起跟随选料条日期不再固定近三月）。</summary>
    Task<decimal> GetSuggestQtyAsync(string cExpress, string nno, string dpm, string cp, string dpmusem,
        DateTime date1, DateTime date2, CancellationToken ct = default);

    /// <summary>新增订货行（I1/I2；同编码已存在时相加=U4 由调用方决定）。</summary>
    Task<int> AddLineAsync(OrderLineAddInput input, string dpm, string cp, string dpmusem, string per, bool addQty, CancellationToken ct = default);

    /// <summary>修改行（U5；oqty=0 → D4 删行，返回 deleted=true）。</summary>
    Task<bool> UpdateLineAsync(int id, decimal oqty, decimal iprj, decimal oprc, string fa, string dpm, string cp, CancellationToken ct = default);

    /// <summary>删除行（D1）。</summary>
    Task DeleteLineAsync(int id, string dpm, string cp, CancellationToken ct = default);

    /// <summary>修改单头（U2/U3：cno,orderdate,bzlx,fkfs,gyname[,gname]；先防重）。</summary>
    Task UpdateHeadAsync(string oldCno, PlanHeadInput head, string dpm, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>删除整张计划单（D3：type='' + tbxzb 条件）。</summary>
    Task<int> DeletePlanAsync(string cno, string dpm, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>并入在途（U1：整单 type='历史'、orno=原cno、cno=''、item 重排、orderdate=今天）。</summary>
    Task<int> MergeToTransitAsync(string cno, string dpm, string cp, string dpmusem, CancellationToken ct = default);

    /// <summary>同单号改数量（U8）。</summary>
    Task ChangeQtyAsync(int id, decimal oqty, string dpm, string cp, CancellationToken ct = default);

    /// <summary>拆数量：新单号新增行（I5）+ 旧行余量（U9）。</summary>
    Task SplitLineAsync(int id, string newCno, decimal newQty, string dpm, string cp, string per, CancellationToken ct = default);

    /// <summary>整行迁移到新单号（U10）。</summary>
    Task MoveLineAsync(int id, string newCno, string dpm, string cp, CancellationToken ct = default);

    /// <summary>修改库存售价（2026-11-04 销售开单右键需求，U7 扩展）：tbisto 该编码全行
    /// oprc/oprb/posi/dc（null=不改）+ tbprnoty.oprc_min（null=不改）+ tbistoch 日志。</summary>
    Task ChangeStockPriceAsync(string cp, string nno, string? posi, string? dc,
        decimal oprc, decimal oprb, decimal? oprcMin, string per, string dpm, CancellationToken ct = default);

    /// <summary>售价/批发价修改（U7：该编码库存全行 + I6：tbistoch 日志）。</summary>
    Task ChangeSalePriceAsync(string cp, string nno, decimal oprc, decimal oprb, string per, string dpm, CancellationToken ct = default);

    /// <summary>属性新增（I3）/修改（U6）/删除（D2）。</summary>
    Task<int> AddProfileAsync(PartProfileInput input, string dpm, CancellationToken ct = default);
    Task UpdateProfileAsync(string oldNno, PartProfileInput input, string dpm, string cp, CancellationToken ct = default);
    Task DeleteProfileAsync(string cp, string nno, string dpm, CancellationToken ct = default);

    /// <summary>行联动明细：进仓记录（c_jk）/在途（c_zt）/供应商价（c_gys，BZ 系数折算）。</summary>
    Task<IReadOnlyList<PartInRecordRow>> GetInRecordsAsync(string cp, string nno, CancellationToken ct = default);
    Task<IReadOnlyList<PartSaleRecordRow>> GetSaleRecordsAsync(string cp, string nno, CancellationToken ct = default);
    Task<IReadOnlyList<PartTransitRow>> GetTransitAsync(string cp, string nno, CancellationToken ct = default);
    Task<IReadOnlyList<PartSupplyRow>> GetSupplyPricesAsync(string cp, string nno, CancellationToken ct = default);
}

public sealed class PlanHeadRow
{
    public string Cno { get; set; } = "";
    public decimal Iprj { get; set; }
    public DateTime Orderdate { get; set; }
    public string Bzlx { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
}

public sealed class PlanLineRow
{
    public int Id { get; set; }
    public int Item { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Dc { get; set; } = "";
    public decimal Kcamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Ztqty { get; set; }
    public decimal Oqty { get; set; }
    public decimal Ydqty { get; set; }
    public decimal Oprc { get; set; }
    public string Cno { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Bzlx { get; set; } = "";
    public DateTime Orderdate { get; set; }
    public string Oenno { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Per { get; set; } = "";
}

/// <summary>
/// 选料行（订单生成·选择配件窗）。警戒双档（2026-11-04 需求）：amjj=A 警戒值（黄档）/ bmjj=B 警戒值（红档），
/// 库存 Kcamount &lt; A → 黄、&lt; B → 红（红优先），编辑警戒值实时刷新行色。
/// </summary>
public sealed class PartPickRow : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>序号列（2026-11-04 需求）：VM 按当前列表位置 1..n 编号，不落 SQL。</summary>
    public int Seq { get; set; }
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Kcamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Ztqty { get; set; }
    public decimal Ydqty { get; set; }
    public string Nnp { get; set; } = "";
    public string Nnq { get; set; } = "";
    public string Note { get; set; } = "";
    public string Tno { get; set; } = "";
    public string Xno { get; set; } = "";
    public string Dc { get; set; } = "";
    public decimal Uqty { get; set; }

    private decimal _amjj;
    /// <summary>警戒值A（tbistojj，黄档，选料窗可编辑）。</summary>
    public decimal Amjj { get => _amjj; set { _amjj = value; OnChanged(); RefreshLevel(); } }

    private decimal _bmjj;
    /// <summary>警戒值B（tbistojj，红档，选料窗可编辑）。</summary>
    public decimal Bmjj { get => _bmjj; set { _bmjj = value; OnChanged(); RefreshLevel(); } }

    private bool _isYellow;
    /// <summary>库存 &lt; A 警戒值 → 黄字预警。</summary>
    public bool IsYellow { get => _isYellow; private set { _isYellow = value; OnChanged(); } }

    private bool _isRed;
    /// <summary>库存 &lt; B 警戒值 → 红字告急（同时低于双档时红色优先）。</summary>
    public bool IsRed { get => _isRed; private set { _isRed = value; OnChanged(); } }

    private void RefreshLevel()
    {
        var red = Bmjj > 0 && Kcamount < Bmjj;
        IsRed = red;
        IsYellow = !red && Amjj > 0 && Kcamount < Amjj;
    }

    private decimal _num;
    /// <summary>已订数（c_dhorder 同码合计）。已订（&gt;0）→ 绿档行色，优先于黄/红。</summary>
    public decimal Num { get => _num; set { _num = value; OnChanged(); } }
    /// <summary>已订标记（行样式触发器用）。</summary>
    public bool IsOrdered => Num > 0;
    public decimal Ckamount { get; set; }
    public string Cp { get; set; } = "";
    public decimal Oprc { get; set; }
    public decimal Oprb { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));
}

public sealed class PartPickQuery
{
    /// <summary>编码：'' 全部；'*'前缀→按 cxnno；'/'前缀→包含；否则前缀（长度≥6 或含编码链）。</summary>
    public string Nno { get; set; } = "";
    /// <summary>名称：名称/编码/车型包含 + tbname0 拼音码（PartPickWindow 同语义）。</summary>
    public string Na1 { get; set; } = "";
    /// <summary>车型：车型/名称包含 + tbty0 车型拼音码。</summary>
    public string Ty { get; set; } = "";
    public DateTime Date1 { get; set; } = DateTime.Today.AddMonths(-1);
    public DateTime Date2 { get; set; } = DateTime.Today;
    public decimal Num1 { get; set; }
    public decimal Num2 { get; set; } = 9999;
    public decimal Kc1 { get; set; }
    public decimal Kc2 { get; set; } = 9999;
    /// <summary>页码（1 起；无限滑动加载 2026-11-03 需求）。</summary>
    public int Page { get; set; } = 1;
    /// <summary>页大小（0 = 不分页全量，兼容旧口径）。</summary>
    public int PageSize { get; set; } = 100;
}

/// <summary>选料页单件实时聚合值（2026-11-04 弱网缓存；键=RTRIM(nno)；Kcamount 已含互配件 mx，Iprc=加权进价 CASE 口径）。</summary>
public sealed record PickAggregate(
    decimal Kcamount, decimal Iprc, decimal Oprc, decimal Oprb,
    decimal Ztqty, decimal Amjj, decimal Bmjj, decimal Ckamount);

/// <summary>选料页实时聚合 + tbprnoty 增量（2026-11-04 弱网缓存：其他电脑新增配件实时并入快照）。</summary>
public sealed record PickAggLiveResult(
    IReadOnlyDictionary<string, PickAggregate> Aggs,
    int AttrMaxId,
    List<PartCacheAttrRow> NewAttrRows);

public sealed class OrderLineAddInput
{
    public string Cno { get; set; } = "";
    public required string Nno { get; set; }
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Dc { get; set; } = "";
    public decimal Oqty { get; set; }
    public decimal Iprj { get; set; }
    /// <summary>专订售价（P_是否专订时写入）。</summary>
    public decimal? Oprc { get; set; }
    public DateTime Orderdate { get; set; } = DateTime.Today;
    public string Bzlx { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
}

public sealed class PlanHeadInput
{
    public string NewCno { get; set; } = "";
    public DateTime Orderdate { get; set; } = DateTime.Today;
    public string Bzlx { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Gname { get; set; } = "";
}

public sealed class PartProfileInput
{
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Iprj { get; set; }
    public string CodeTs { get; set; } = "";
    public string Eng { get; set; } = "";
    public string Nnp { get; set; } = "";
    public string Nnq { get; set; } = "";
    public decimal Uqty { get; set; }
    public string Xno { get; set; } = "";
    public string Tno { get; set; } = "";
    public decimal Amjj { get; set; }
    public decimal Bmjj { get; set; }
    public string Note { get; set; } = "";
}

public sealed class PartInRecordRow
{
    public DateTime Indate { get; set; }
    public string Gyname { get; set; } = "";
    public decimal Jkamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
}

/// <summary>销售历史（2026-11-03 选料窗口下方历史区；tbsada 出库记录，含配套码，TOP 500）。</summary>
public sealed class PartSaleRecordRow
{
    public DateTime Outdate { get; set; }
    public string Nno { get; set; } = "";
    public string Gname { get; set; } = "";
    public decimal Ckamount { get; set; }
    public decimal Oprc { get; set; }
    public string Per { get; set; } = "";
}

public sealed class PartTransitRow
{
    public DateTime Orderdate { get; set; }
    public string Cno { get; set; } = "";
    public decimal Ztqty { get; set; }
    public decimal Iprj { get; set; }
}

public sealed class PartSupplyRow
{
    public string Gyname { get; set; } = "";
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Zq { get; set; }
}

/// <summary>
/// 订单生成仓储（菜单 F.1，权限码 F1；fr_order4 契约：开发文档/契约摘录/订单生成fr_order4契约.md）。
/// 无取号（cno 全人工）；写路径 I1/I2/I3/I5/I6/U1-U10/D1-D4；type=''=计划态、'历史'=在途态。
/// tbxzb 过滤：tborder → cp=@xcp + dpmusem like；tbprnoty/tbisto → cp=?p_cp（+tbisto dpmusem like）。
/// 旧系统逐语句自动提交；多行 U1/U9-U12 新系统包单事务（备案行为差异）。
/// ISOLD（订货旧编码统计）分支未实现——当前库该配置为 0（备案：若开启需补子件聚合查询）。
/// </summary>
public sealed class Order4Repository(SqlConnectionFactory factory, DocNo.IDocNoService docNo) : IOrder4Repository
{
    // tbxzb 条件（与 Order3Repository 同源：tborder cp+dpmusem）
    private const string TborderXzb = " AND cp = @xcp AND @p_dpmusem LIKE '%' + RTRIM(dpm) + '%'";

    // p_dpmusem = 登录角色部门串（tbrole.dpmusem，旧 fr_login _0.row12:P_DPMUSEM=ALLTRIM(DPMUSEM)），
    // 由 VM 传 AppSession.DeptPermissions；传厂牌会把 tborder（dpm='A'）全部过滤掉（2026-11-02 修复）。
    private static void AddXzb(DynamicParameters args, string cp, string dpmusem)
    {
        args.Add("xcp", new DbString { Value = cp, IsAnsi = true, Length = 10 });
        args.Add("p_dpmusem", new DbString { Value = dpmusem, IsAnsi = true, Length = 60 });
    }

    /// <summary>P_FPCW 运行时读 tbsysset（pg_proc L3285，缺省「废」）。</summary>
    private async Task<string> GetFpcwAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE name = N'废品仓位标识' AND type = 'system'",
            transaction: tx, cancellationToken: ct)) is { } v && v.Length > 0 ? v : "废";

    public async Task<IReadOnlyList<PlanHeadRow>> GetPlanListAsync(string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        // §3.4（___.row176:L12）：type='    ' 等价 type=''；orderdate desc
        var sql = $"""
            SELECT RTRIM(cno) cno, SUM(iprj * oqty) iprj, MAX(orderdate) orderdate,
                   MAX(RTRIM(bzlx)) bzlx, MAX(RTRIM(fkfs)) fkfs, MAX(RTRIM(gyname)) gyname, MAX(RTRIM(gname)) gname
            FROM tborder WITH (NOLOCK)
            WHERE type = ''{TborderXzb}
            GROUP BY cno
            ORDER BY MAX(orderdate) DESC
            """;
        var args = new DynamicParameters();
        AddXzb(args, cp, dpmusem);
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PlanHeadRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task ValidateNewCnoAsync(string cno, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tborder WITH (NOLOCK) WHERE cno = @cno OR orno = @cno",
            new { cno = Ansi(cno, 15) }, cancellationToken: ct));
        if (exists > 0) throw new AppException("CNO_EXISTS", "订货单已存在该合同号", 409);
    }

    public async Task<bool> EnsureHeadRowAsync(string cno, DateTime orderdate, string gyname, string dpm, string cp, string dpmusem, string per, CancellationToken ct = default)
    {
        // 2026-11-03 需求：新建计划单/保存按钮——tborder 行式存储，空单无行 → 列表不显示；
        // 插入单头行（nno=''、oqty=0）使计划单立即可见，关闭软件后可继续订货。
        // 明细查询/并入在途已排除 nno=''；AddLine 去重按 nno 匹配不受影响；删除整单/修改单头按 cno 覆盖。
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(1) FROM tborder WITH (UPDLOCK) WHERE cno = @cno AND type = ''{TborderXzb}",
                HeadArgs(cno, cp, dpmusem), transaction: tx, cancellationToken: ct));
            if (exists > 0)
            {
                await tx.CommitAsync(ct);
                return false;
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tborder (cp, nno, na1, ty, fa, unit, dc, oqty, iprj, cno, orderdate, bzlx, fkfs, gyname, gname, per, dpm) " +
                "VALUES (@cp, '', '', '', '', '', '', 0, 0, @cno, @orderdate, '', '', @gyname, '', @per, @dpm)",
                new
                {
                    cp = Ansi(cp, 10), cno = Ansi(cno, 15), orderdate = orderdate.Date,
                    gyname = Ansi(gyname, 60), per = Ansi(per, 10), dpm = Ansi(dpm, 10),
                }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>单头行查询参数（cno + tbxzb）。</summary>
    private static DynamicParameters HeadArgs(string cno, string cp, string dpmusem)
    {
        var args = new DynamicParameters(new { cno = Ansi(cno, 15) });
        AddXzb(args, cp, dpmusem);
        return args;
    }

    public async Task<IReadOnlyList<PlanLineRow>> GetPlanLinesAsync(string cno, string dpm, string cp, string dpmusem,
        string? suggestExp, DateTime sugDate1, DateTime sugDate2, CancellationToken ct = default)
    {
        // §3.5（___.row183）：主表 tborder o，type='' + READXZB(别名 o) → tbxzb 条件前缀 o.
        // iprc=k 加权进价（row183 L58 CASE when isnull(k.iprc,0)>0）、oenno 空回落 o.nno（L58）、
        // ydqty=建议公式相关子查询（L22 M_YDQTY：IIF(EMPTY(SETEXP),0,...)）
        var fpcw = await GetFpcwCoreAsync(ct);
        string ydCol = "0 AS ydqty";
        if (!string.IsNullOrWhiteSpace(suggestExp))
        {
            var sug = await BuildSuggestAsync(suggestExp, sugDate1.Date, sugDate2.Date, ct);
            if (sug.Exp is not null)
                ydCol = $"ISNULL((SELECT ({sug.Exp}) FROM ({sug.InnerFrom}) w WHERE ({sug.Exp}) >= 1 AND w.nno = o.nno), 0) ydqty";
        }
        var sql = $"""
            SELECT o.id, o.item, RTRIM(o.nno) nno, ISNULL(i.na1, p.na1) na1, ISNULL(i.ty, p.ty) ty,
                   ISNULL(i.fa, p.fa) fa, ISNULL(i.unit, p.unit) unit, ISNULL(i.dc, p.dc) dc,
                   ISNULL(k.kcamount, 0) + ISNULL(mx.xam, 0) kcamount,
                   o.iprj, CASE WHEN k.kcweight > 0 THEN k.kcweight ELSE ISNULL(i.iprc, 0) END iprc,
                   ISNULL(z.ztqty, 0) ztqty, o.oqty,
                   {ydCol},
                   o.oprc, o.orderdate, RTRIM(o.cno) cno, RTRIM(o.fkfs) fkfs, RTRIM(o.gyname) gyname, RTRIM(o.bzlx) bzlx,
                   CASE WHEN ISNULL(g.oenno, '') = '' THEN o.nno ELSE g.oenno END oenno, RTRIM(o.gname) gname, RTRIM(o.per) per
            FROM tborder o WITH (NOLOCK)
            LEFT JOIN tbprnoty p WITH (NOLOCK) ON o.cp = p.cp AND o.nno = p.nno
            LEFT JOIN tbisto i WITH (NOLOCK) ON o.cp = i.cp AND o.nno = i.nno AND i.id = (SELECT MAX(id) FROM tbisto WHERE cp = o.cp AND nno = o.nno)
            LEFT JOIN (SELECT cp, nno, SUM(kcamount) kcamount, MAX(id) id, SUM(kcamount * iprc) / NULLIF(SUM(kcamount), 0) kcweight FROM tbisto WITH (NOLOCK) WHERE posi <> @fpcw AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) k ON o.cp = k.cp AND o.nno = k.nno
            LEFT JOIN (SELECT cp, nno, SUM(oqty - qty) ztqty, SUM(oqty) oqty FROM tborder WITH (NOLOCK) WHERE type = '历史' AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) z ON o.cp = z.cp AND o.nno = z.nno
            LEFT JOIN (SELECT x.cp, x.nno, SUM(v.kcamount) xam FROM tbprnoty x WITH (NOLOCK) LEFT JOIN tbisto v WITH (NOLOCK) ON x.cp = v.cp WHERE v.posi <> @fpcw AND v.nno IN (CASE WHEN x.nno = x.nnp THEN '' ELSE x.nnp END, CASE WHEN x.nno = x.nnq THEN '' ELSE CASE WHEN x.nnp = x.nnq THEN '' ELSE x.nnq END END) GROUP BY x.cp, x.nno) mx ON o.cp = mx.cp AND o.nno = mx.nno
            LEFT JOIN (SELECT cp, nno, MAX(oenno) oenno FROM tbgys WHERE gyname = @gyname GROUP BY cp, nno) g ON o.cp = g.cp AND o.nno = g.nno
            WHERE o.cno = @cno AND o.type = '' AND o.cp = @xcp AND RTRIM(o.nno) <> '' AND @p_dpmusem LIKE '%' + RTRIM(o.dpm) + '%'
            ORDER BY o.id
            """;
        var gyname = await GetCurrentGynameAsync(ct);
        var args = new DynamicParameters(new
        {
            cno = Ansi(cno, 15), fpcw = Ansi(fpcw, 10), gyname = Ansi(gyname, 40),
            p_cp = Ansi(cp, 10), p_dpm = Ansi(dpm, 10),
            m_date1 = sugDate1.Date, m_date2 = sugDate2.Date,
        });
        AddXzb(args, cp, dpmusem);
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PlanLineRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PartPickRow>> SearchPartsAsync(PartPickQuery query, string dpm, string cp, string dpmusem, string? suggestExp, CancellationToken ct = default)
    {
        // §3.2 PR_FILT 普通分支（ISOLD=false）；§3.3 条件构造
        // 远端链路优化（2026-11-03 实测）：fpcw 不再单独一次往返（GetFpcwCoreAsync），
        // 改为随主查询同批 DECLARE（NULLIF 处理空值，与 C# 侧「空→默认废」语义一致），每次检索省 ~1 RTT
        var where = "p.cp = @p_cp";
        var args = new DynamicParameters(new
        {
            p_cp = Ansi(cp, 10), p_dpm = Ansi(dpm, 10), p_dpmusem = Ansi(dpmusem, 60),
            m_date1 = query.Date1.Date, m_date2 = query.Date2.Date,
        });
        var nno = query.Nno?.Trim() ?? "";
        if (nno.Length > 0)
        {
            if (nno.StartsWith("*"))
            {
                var m = nno[1..];
                where += " AND p.cxnno BETWEEN @nno AND @nno + '组组组组'";
                args.Add("nno", Ansi(m, 18));
            }
            else if (nno.StartsWith("/"))
            {
                where += " AND p.nno LIKE @nnoLike";
                args.Add("nnoLike", new DbString { Value = "%" + nno[1..] + "%", IsAnsi = true, Length = 20 });
            }
            else
            {
                where += " AND (p.nno BETWEEN @nno AND @nno + '组组组组'";
                args.Add("nno", Ansi(nno, 18));
                if (nno.Length >= 6 && !nno.Contains('+'))
                    where += " OR p.nnp = @nno OR p.nnq = @nno";
                where += ")";
            }
        }
        // 名称框（PartPickWindow 同语义）：名称/编码/车型包含 + tbname0 拼音码（code→name）带出（旧系统 DM() 取码机制）
        // 车型框：车型/名称包含 + tbty0 车型拼音码。注意：本查询不可加 OPTION (HASH JOIN)——
        // 与 split_dpmusem/CASE 派生表组合会报 8622 无法生成计划（2026-11-03 实测），拼音派生表
        // 按码过滤后仅数行，依赖优化器自然计划即可
        var na1 = query.Na1?.Trim() ?? "";
        var na1Join = "";
        if (na1.Length > 0)
        {
            na1Join = "\nLEFT JOIN (SELECT DISTINCT rtrim(name) AS name FROM tbname0 WITH (NOLOCK) WHERE code LIKE '%' + @na1 + '%') nb ON rtrim(p.na1) = nb.name";
            where += " AND (p.na1 LIKE '%' + @na1 + '%' OR p.nno LIKE '%' + @na1 + '%' OR p.ty LIKE '%' + @na1 + '%' OR nb.name IS NOT NULL)";
            args.Add("na1", new DbString { Value = na1, IsAnsi = true, Length = 40 });
        }
        var tyKw = query.Ty?.Trim() ?? "";
        var tyJoin = "";
        if (tyKw.Length > 0)
        {
            tyJoin = "\nLEFT JOIN (SELECT DISTINCT rtrim(ty) AS ty FROM tbty0 WITH (NOLOCK) WHERE code LIKE '%' + @tyKw + '%') tb2 ON rtrim(p.ty) = tb2.ty";
            where += " AND (p.ty LIKE '%' + @tyKw + '%' OR p.na1 LIKE '%' + @tyKw + '%' OR tb2.ty IS NOT NULL)";
            args.Add("tyKw", new DbString { Value = tyKw, IsAnsi = true, Length = 40 });
        }
        if (query.Num1 != 0 || query.Num2 != 9999)
        {
            where += " AND ckamount >= @num1 AND ckamount <= @num2";
            args.Add("num1", query.Num1);
            args.Add("num2", query.Num2);
        }
        if (query.Kc1 != 0 || query.Kc2 != 9999)
        {
            where += " AND k.kcamount >= @kc1 AND k.kcamount <= @kc2";
            args.Add("kc1", query.Kc1);
            args.Add("kc2", query.Kc2);
        }

        // ydqty = ISNULL((SETEXP + p.nno), 0)（row2 L7 M_YDQTY）；suggestExp 为空 → 0
        string ydCol = "0 AS ydqty";
        if (!string.IsNullOrWhiteSpace(suggestExp))
        {
            var sug = await BuildSuggestAsync(suggestExp, query.Date1.Date, query.Date2.Date, ct);
            if (sug.Exp is not null)
                ydCol = $"ISNULL((SELECT ({sug.Exp}) FROM ({sug.InnerFrom}) w WHERE ({sug.Exp}) >= 1 AND w.nno = p.nno), 0) AS ydqty";
        }

        // 近1/2/3月取数已随选料窗月销列移除（2026-11-04 需求）；P02 建议公式走 BuildSuggestAsync 独立内层查询，不受影响
        var sql = $"""
            DECLARE @fpcw varchar(10) = ISNULL(NULLIF((SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE name = N'废品仓位标识' AND type = 'system'), ''), N'废');
            SELECT RTRIM(p.nno) nno, RTRIM(p.na1) na1, RTRIM(p.ty) ty, RTRIM(p.fa) fa, RTRIM(p.unit) unit,
                   ISNULL(k.kcamount, 0) + ISNULL(mx.xam, 0) kcamount, p.iprj,
                   ISNULL(z.ztqty, 0) ztqty, {ydCol},
                   RTRIM(p.nnp) nnp, RTRIM(p.nnq) nnq, RTRIM(p.note) note, RTRIM(p.tno) tno, RTRIM(p.xno) xno,
                   RTRIM(p.dc) dc, p.uqty, ISNULL(jj.amjj, 0) amjj, ISNULL(jj.bmjj, 0) bmjj, RTRIM(p.cp) cp,
                   CASE WHEN k.kcweight > 0 THEN k.kcweight ELSE ISNULL(i.iprc, 0) END iprc,
                   ISNULL(i.oprc, 0) oprc, ISNULL(i.oprb, 0) oprb, 0 AS num,
                   ISNULL(ck.ckamount, 0) ckamount
            FROM tbprnoty p WITH (NOLOCK){na1Join}{tyJoin}
            LEFT JOIN (SELECT dpm, cp, nno, MAX(amjj) amjj, MAX(bmjj) bmjj FROM tbistojj WITH (NOLOCK) WHERE dpm = @p_dpm AND cp = @p_cp GROUP BY dpm, cp, nno) jj ON p.cp = jj.cp AND p.nno = jj.nno
            LEFT JOIN (SELECT cp, nno, SUM(kcamount) kcamount, MAX(id) id, SUM(kcamount * iprc) / NULLIF(SUM(kcamount), 0) kcweight FROM tbisto WITH (NOLOCK) WHERE cp = @p_cp AND posi <> @fpcw AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) k ON p.cp = k.cp AND p.nno = k.nno
            LEFT JOIN tbisto i WITH (NOLOCK) ON p.cp = i.cp AND p.nno = i.nno AND k.id = i.id
            LEFT JOIN (SELECT x.cp, x.nno, SUM(v.kcamount) xam FROM tbprnoty x WITH (NOLOCK) LEFT JOIN tbisto v WITH (NOLOCK) ON x.cp = v.cp AND v.cp = @p_cp WHERE x.cp = @p_cp AND v.posi <> @fpcw AND v.nno IN (CASE WHEN x.nno = x.nnp THEN '' ELSE x.nnp END, CASE WHEN x.nno = x.nnq THEN '' ELSE CASE WHEN x.nnp = x.nnq THEN '' ELSE x.nnq END END) GROUP BY x.cp, x.nno) mx ON p.cp = mx.cp AND p.nno = mx.nno
            LEFT JOIN (SELECT cp, nno, SUM(oqty - qty) ztqty FROM tborder WITH (NOLOCK) WHERE type = '历史' AND cp = @p_cp AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) GROUP BY cp, nno) z ON p.cp = z.cp AND p.nno = z.nno
            LEFT JOIN (SELECT p1.cp, p1.nno, SUM(ISNULL(a1.ck, 0) + ISNULL(a2.ck, 0) + ISNULL(a3.ck, 0)) ckamount FROM tbprnoty p1 WITH (NOLOCK) LEFT JOIN (SELECT cp, nno, SUM(ckamount) ck FROM tbsada WITH (NOLOCK) WHERE cp = @p_cp AND outdate >= @m_date1 AND outdate <= @m_date2 AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND (type = '历史' OR type = '销售') GROUP BY cp, nno UNION ALL SELECT cp, nno, SUM(ckamount) ck FROM tbxlsa WITH (NOLOCK) WHERE cp = @p_cp AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND outdate >= @m_date1 AND outdate <= @m_date2 GROUP BY cp, nno) a1 ON a1.cp = p1.cp AND a1.nno = p1.nno LEFT JOIN (SELECT cp, nno, SUM(ckamount) ck FROM tbsada WITH (NOLOCK) WHERE cp = @p_cp AND outdate >= @m_date1 AND outdate <= @m_date2 AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND (type = '历史' OR type = '销售') GROUP BY cp, nno UNION ALL SELECT cp, nno, SUM(ckamount) ck FROM tbxlsa WITH (NOLOCK) WHERE cp = @p_cp AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND outdate >= @m_date1 AND outdate <= @m_date2 GROUP BY cp, nno) a2 ON a2.cp = p1.cp AND a2.nno = p1.nnp AND p1.nnp <> p1.nno LEFT JOIN (SELECT cp, nno, SUM(ckamount) ck FROM tbsada WITH (NOLOCK) WHERE cp = @p_cp AND outdate >= @m_date1 AND outdate <= @m_date2 AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND (type = '历史' OR type = '销售') GROUP BY cp, nno UNION ALL SELECT cp, nno, SUM(ckamount) ck FROM tbxlsa WITH (NOLOCK) WHERE cp = @p_cp AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND outdate >= @m_date1 AND outdate <= @m_date2 GROUP BY cp, nno) a3 ON a3.cp = p1.cp AND a3.nno = p1.nnq AND p1.nnq <> p1.nno AND p1.nnq <> p1.nnp WHERE p1.cp = @p_cp GROUP BY p1.cp, p1.nno) ck ON p.nno = ck.nno AND p.cp = ck.cp
            WHERE {where}
            ORDER BY p.nno{(query.PageSize > 0 ? "\nOFFSET @skip ROWS FETCH NEXT @take ROWS ONLY" : "")}
            """;
        if (query.PageSize > 0)
        {
            args.Add("skip", (Math.Max(query.Page, 1) - 1) * query.PageSize);
            args.Add("take", query.PageSize);
        }
        await using var conn = await factory.OpenAsync(ct);
        // 120s：聚合子查询量大（含在途/出库历史全表），默认 30s 在大数据量下会超时
        var rows = (await conn.QueryAsync<PartPickRow>(new CommandDefinition(sql, args, commandTimeout: 120, cancellationToken: ct))).AsList();
        return rows;
    }

    // 批量聚合中间行（列名与 SQL 对齐，Dapper 大小写不敏感映射）
    private sealed class AggStockRow { public string nno { get; set; } = ""; public decimal kc { get; set; } public decimal kcweight { get; set; } public decimal iprc { get; set; } public decimal oprc { get; set; } public decimal oprb { get; set; } }
    private sealed class AggMxRow { public string nno { get; set; } = ""; public decimal xam { get; set; } }
    private sealed class AggZtRow { public string nno { get; set; } = ""; public decimal ztqty { get; set; } }
    private sealed class AggJjRow { public string nno { get; set; } = ""; public decimal amjj { get; set; } public decimal bmjj { get; set; } }
    private sealed class AggCkRow { public string nno { get; set; } = ""; public decimal ckamount { get; set; } }

    /// <inheritdoc/>
    public async Task<PickAggLiveResult> GetPickAggregatesAsync(string cp, string dpm, string dpmusem,
        IReadOnlyCollection<string> nnos, DateTime date1, DateTime date2, int lastAttrMaxId, CancellationToken ct = default)
    {
        // 与 SearchPartsAsync 主查询各聚合 LEFT JOIN 完全同构，仅追加 nno IN 限定（本页 100 行，
        // 传输量从全厂牌聚合行缩到本页；服务端 ck 子查询带 IN 过滤后扫描也显著减小）；
        // 末尾两个结果集 = tbprnoty 增量水位 + 新增属性行（其他电脑新增配件实时可见）
        var list = nnos.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, PickAggregate>(StringComparer.OrdinalIgnoreCase);

        var sql = $"""
            DECLARE @fpcw varchar(10) = ISNULL(NULLIF((SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE name = N'废品仓位标识' AND type = 'system'), ''), N'废');
            SELECT RTRIM(k.nno) nno, k.kc, ISNULL(k.kcweight, 0) kcweight, ISNULL(i.iprc, 0) iprc, ISNULL(i.oprc, 0) oprc, ISNULL(i.oprb, 0) oprb
            FROM (SELECT nno, SUM(kcamount) kc, SUM(kcamount * iprc) / NULLIF(SUM(kcamount), 0) kcweight, MAX(id) id
                  FROM tbisto WITH (NOLOCK)
                  WHERE cp = @p_cp AND posi <> @fpcw AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND nno IN @nnos
                  GROUP BY nno) k
            LEFT JOIN tbisto i WITH (NOLOCK) ON i.id = k.id;
            SELECT RTRIM(x.nno) nno, SUM(v.kcamount) xam
            FROM tbprnoty x WITH (NOLOCK)
            LEFT JOIN tbisto v WITH (NOLOCK) ON x.cp = v.cp AND v.cp = @p_cp AND v.posi <> @fpcw AND v.nno IN (CASE WHEN x.nno = x.nnp THEN '' ELSE x.nnp END, CASE WHEN x.nno = x.nnq THEN '' ELSE CASE WHEN x.nnp = x.nnq THEN '' ELSE x.nnq END END)
            WHERE x.cp = @p_cp AND x.nno IN @nnos
            GROUP BY x.nno;
            SELECT RTRIM(nno) nno, SUM(oqty - qty) ztqty
            FROM tborder WITH (NOLOCK)
            WHERE type = '历史' AND cp = @p_cp AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND nno IN @nnos
            GROUP BY nno;
            SELECT RTRIM(nno) nno, MAX(amjj) amjj, MAX(bmjj) bmjj
            FROM tbistojj WITH (NOLOCK)
            WHERE dpm = @p_dpm AND cp = @p_cp AND nno IN @nnos
            GROUP BY nno;
            SELECT RTRIM(p1.nno) nno, SUM(ISNULL(a1.ck, 0) + ISNULL(a2.ck, 0) + ISNULL(a3.ck, 0)) ckamount
            FROM tbprnoty p1 WITH (NOLOCK)
            LEFT JOIN (SELECT cp, nno, SUM(ckamount) ck FROM tbsada WITH (NOLOCK) WHERE cp = @p_cp AND outdate >= @m_date1 AND outdate <= @m_date2 AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND (type = '历史' OR type = '销售') GROUP BY cp, nno UNION ALL SELECT cp, nno, SUM(ckamount) ck FROM tbxlsa WITH (NOLOCK) WHERE cp = @p_cp AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND outdate >= @m_date1 AND outdate <= @m_date2 GROUP BY cp, nno) a1 ON a1.cp = p1.cp AND a1.nno = p1.nno
            LEFT JOIN (SELECT cp, nno, SUM(ckamount) ck FROM tbsada WITH (NOLOCK) WHERE cp = @p_cp AND outdate >= @m_date1 AND outdate <= @m_date2 AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND (type = '历史' OR type = '销售') GROUP BY cp, nno UNION ALL SELECT cp, nno, SUM(ckamount) ck FROM tbxlsa WITH (NOLOCK) WHERE cp = @p_cp AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND outdate >= @m_date1 AND outdate <= @m_date2 GROUP BY cp, nno) a2 ON a2.cp = p1.cp AND a2.nno = p1.nnp AND p1.nnp <> p1.nno
            LEFT JOIN (SELECT cp, nno, SUM(ckamount) ck FROM tbsada WITH (NOLOCK) WHERE cp = @p_cp AND outdate >= @m_date1 AND outdate <= @m_date2 AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND (type = '历史' OR type = '销售') GROUP BY cp, nno UNION ALL SELECT cp, nno, SUM(ckamount) ck FROM tbxlsa WITH (NOLOCK) WHERE cp = @p_cp AND dpm IN (SELECT dpmbh FROM [dbo].[split_dpmusem](@p_dpmusem)) AND outdate >= @m_date1 AND outdate <= @m_date2 GROUP BY cp, nno) a3 ON a3.cp = p1.cp AND a3.nno = p1.nnq AND p1.nnq <> p1.nno AND p1.nnq <> p1.nnp
            WHERE p1.cp = @p_cp AND p1.nno IN @nnos
            GROUP BY p1.nno;
            SELECT ISNULL(MAX(id), 0) FROM tbprnoty WITH (NOLOCK);
            SELECT TOP 500 id, rtrim(cp) AS Cp, rtrim(nno) AS Nno, rtrim(na1) AS Na1, rtrim(ty) AS Ty, rtrim(fa) AS Fa,
                   rtrim(dc) AS Dc, rtrim(unit) AS Unit, rtrim(nnp) AS Nnp, rtrim(nnq) AS Nnq,
                   rtrim(note) AS Note, rtrim(tno) AS Tno, rtrim(xno) AS Xno, rtrim(cxnno) AS Cxnno,
                   ISNULL(uqty, 0) AS Uqty, ISNULL(iprj, 0) AS Iprj,
                   ISNULL(oprc_cp, 0) AS OprcCp, ISNULL(oprc_min, 0) AS OprcMin
            FROM tbprnoty WITH (NOLOCK) WHERE id > @lastAttrMaxId ORDER BY id;
            """;
        var args = new DynamicParameters(new
        {
            p_cp = Ansi(cp, 10), p_dpm = Ansi(dpm, 10), p_dpmusem = Ansi(dpmusem, 60),
            m_date1 = date1.Date, m_date2 = date2.Date, lastAttrMaxId,
        });
        args.Add("nnos", list);

        await using var conn = await factory.OpenAsync(ct);
        await using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, args, commandTimeout: 60, cancellationToken: ct));
        var stocks = (await multi.ReadAsync<AggStockRow>()).AsList();
        var mx = (await multi.ReadAsync<AggMxRow>()).AsList().ToDictionary(r => r.nno, r => r.xam, StringComparer.OrdinalIgnoreCase);
        var zt = (await multi.ReadAsync<AggZtRow>()).AsList().ToDictionary(r => r.nno, r => r.ztqty, StringComparer.OrdinalIgnoreCase);
        var jj = (await multi.ReadAsync<AggJjRow>()).AsList().ToDictionary(r => r.nno, r => r, StringComparer.OrdinalIgnoreCase);
        var ck = (await multi.ReadAsync<AggCkRow>()).AsList().ToDictionary(r => r.nno, r => r.ckamount, StringComparer.OrdinalIgnoreCase);
        foreach (var s in stocks)
        {
            mx.TryGetValue(s.nno, out var xam);
            zt.TryGetValue(s.nno, out var ztqty);
            ck.TryGetValue(s.nno, out var ckamount);
            var amjj = jj.TryGetValue(s.nno, out var j) ? j.amjj : 0;
            var bmjj = jj.TryGetValue(s.nno, out var j2) ? j2.bmjj : 0;
            result[s.nno] = new PickAggregate(
                s.kc + xam,
                s.kcweight > 0 ? s.kcweight : s.iprc,   // 与主查询 iprc CASE 同口径
                s.oprc, s.oprb, ztqty, amjj, bmjj, ckamount);
        }
        var attrMaxId = await multi.ReadFirstAsync<int>();
        var newAttrRows = (await multi.ReadAsync<PartCacheAttrRow>()).AsList();
        return new PickAggLiveResult(result, attrMaxId, newAttrRows);
    }

    /// <summary>
    /// 建议公式构造（___.row589 L16-93：P01..P12 列名替换 + P07/P08 参数替换 → 内层 w 子查询，外层公式求值）。
    /// P02 月均（2026-11-04 改）：区间月均 = 区间总出库 ÷ 区间月数，跟随选料查询条日期（默认全部）；
    /// 旧口径先后为 P_MONTHAVGSTRING 宏（tbrtsamo，月末统计缺失）、tbsada 实时近三月 (mo1+mo2+mo3)/3（已随月销列移除）。
    /// 日期区间备案：旧为公式窗独立日期 TXTBDATE/TXTEDATE，新系统统一用选料查询条日期。
    /// </summary>
    private async Task<(string Exp, string InnerFrom)> BuildSuggestAsync(string cExpress, DateTime date1, DateTime date2, CancellationToken ct)
    {
        var exp = cExpress.Trim()
            .Replace("P01", "kcamount").Replace("P03", "ztqty").Replace("P04", "amjj")
            .Replace("P05", "bmjj").Replace("P06", "Coeffict").Replace("P09", "ckamount")
            .Replace("P10", "ysamount").Replace("P11", "dbztamount");
        var hasP12 = exp.Contains("P12");
        if (hasP12) exp = exp.Replace("P12", "xqcs");
        var paras = (await GetParamsCoreAsync(ct)).Where(p => p.paraid is "P07" or "P08");
        foreach (var (paraid, nvalue) in paras)
            exp = exp.Replace(paraid, nvalue.ToString("0.##"));
        if (exp.Contains("P02"))
            // P02 月均（2026-11-04 改）：不再固定近三月窗口，跟随选料查询条日期区间（出货日期默认全部）——
            // 区间月均 = 区间总出库（ck 同码合计，与 P09 同源）÷ 区间月数
            exp = exp.Replace("P02", "(ISNULL(ckamount,0) / NULLIF(DATEDIFF(month, @m_date1, @m_date2) + 1, 0))");
        var p12Select = hasP12 ? " ,ISNULL(xqcs,0) xqcs " : "";
        var p12Join = hasP12
            ? " left join (select cp,nno,count(nno) xqcs from (select cp,nno from tbsada with(nolock) where outdate>=@m_date1 and outdate<=@m_date2 and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and type='需求') xx group by xx.cp,xx.nno) aaxq on p.cp=aaxq.cp and p.nno=aaxq.nno"
            : "";
        var mDstr = "outdate>=@m_date1 and outdate<=@m_date2";
        var inner = $"""
              select p.nno, isnull(k.kcamount,0) kcamount, isnull(p.uqty-k.kcamount-z.oqty,0) ydqty,
                     ISNULL(z.ztqty,0) ztqty,
                     ISNULL(s.amjj,0) amjj, ISNULL(s.bmjj,0) bmjj, ISNULL(y.coeffict,1) coeffict,
                     ISNULL(ck.ckamount,0) ckamount, ISNULL(ys.ysamount,0) ysamount, ISNULL(dbzt.dbztamount,0) dbztamount{p12Select}
              from tbprnoty p
              left join tbistojj s on s.cp=@p_cp and s.nno=p.nno and s.dpm=@p_dpm
              left join tbdccoef y on p.dc=y.dc
              left join (select cp,nno,sum(kcamount) kcamount,max(id) id from tbisto with(nolock) group by cp,nno) k on p.cp=k.cp and p.nno=k.nno
              left join tbisto i with(nolock) on p.cp=i.cp and p.nno=i.nno and k.id=i.id
              left join (SELECT cp, nno, SUM(oqty - qty) ztqty, sum(oqty) oqty FROM tborder with(nolock) where type='历史' and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) group by cp,nno) z on p.cp=z.cp and p.nno=z.nno
              left join tbcomplex c on p.code_ts = c.code_ts
              left join (select cp,nno,sum(ckamount) dbztamount from tbsada with(nolock) where type='调拨' and mark<>'R' and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) group by cp,nno) dbzt on p.cp=dbzt.cp and p.nno=dbzt.nno
              left join (select p1.cp,p1.nno,sum(ckamount) ckamount from tbprnoty p1 with(nolock) left join (select cp,nno,ckamount from tbsada with(nolock) where {mDstr} and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and (type='历史' or type='销售') union all select cp,nno,ckamount from tbxlsa with(nolock) where dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and {mDstr}) as aa on p1.cp=aa.cp where aa.nno in (p1.nno,p1.nnp,p1.nnq) group by p1.cp,p1.nno) ck on p.nno=ck.nno and p.cp=ck.cp
              left join (select cp,nno,sum(ckamount) ysamount from (select cp,nno,ckamount from tbsada with(nolock) where {mDstr} and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and type='预售') aa group by cp,nno) ys on p.cp=ys.cp and p.nno=ys.nno{p12Join}
              where p.cp = @p_cp
            """;
        return (exp, inner);
    }

    /// <summary>参考公式建议订数（契约 §3.8：对单件执行公式返回 oqty；0=不足订。日期区间=选料查询条日期）。</summary>
    public async Task<decimal> GetSuggestQtyAsync(string cExpress, string nno, string dpm, string cp, string dpmusem,
        DateTime date1, DateTime date2, CancellationToken ct = default)
    {
        // §3.8（___.row589）：公式变换 → 内层 w 子查询 → 外层单件求值
        var (exp, inner) = await BuildSuggestAsync(cExpress, date1.Date, date2.Date, ct);
        var sql = $"SELECT ({exp}) FROM ({inner}) w WHERE ({exp}) >= 1 AND w.nno = @nno";
        var args = new DynamicParameters(new
        {
            p_cp = Ansi(cp, 10), p_dpm = Ansi(dpm, 10), p_dpmusem = Ansi(dpmusem, 60),
            nno = Ansi(nno, 18), m_date1 = date1.Date, m_date2 = date2.Date,
        });
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(sql, args, cancellationToken: ct)) ?? 0;
    }

    public async Task<int> AddLineAsync(OrderLineAddInput input, string dpm, string cp, string dpmusem, string per, bool addQty, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Cno)) throw new AppException("CNO_REQUIRED", "合同号不能为空", 422);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 同编码已存在 → 相加（U4，___.row352:L32）
            var existArgs = new DynamicParameters(new { cno = Ansi(input.Cno, 15), nno = Ansi(input.Nno, 18) });
            AddXzb(existArgs, cp, dpmusem);
            var existing = await conn.QueryFirstOrDefaultAsync<(int id, decimal oqty)>(new CommandDefinition(
                $"SELECT id, oqty FROM tborder WITH (UPDLOCK) WHERE cno = @cno AND type = '' AND nno = @nno{TborderXzb}",
                existArgs, transaction: tx, cancellationToken: ct));
            int newId;
            if (existing.id != 0 && addQty)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tborder SET oqty = @oqty WHERE id = @id",
                    new { oqty = existing.oqty + input.Oqty, id = existing.id }, transaction: tx, cancellationToken: ct));
                newId = existing.id;
            }
            else if (existing.id != 0)
            {
                throw new AppException("LINE_EXISTS", "该编码已在当前单中，是否相加？", 409);
            }
            else
            {
                // I1（专订含 oprc）/I2（非专订）——列集原样 + dpm 自动补
                var hasOprc = input.Oprc is > 0;
                var cols = "cp,nno,na1,ty,fa,unit,dc,oqty,iprj,cno,orderdate,bzlx,fkfs,gyname,gname,per" + (hasOprc ? ",oprc" : "") + ",dpm";
                var values = "@cp,@nno,@na1,@ty,@fa,@unit,@dc,@oqty,@iprj,@cno,@orderdate,@bzlx,@fkfs,@gyname,@gname,@per" + (hasOprc ? ",@oprc" : "") + ",@dpm";
                newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    $"INSERT INTO tborder ({cols}) OUTPUT INSERTED.id VALUES ({values})",
                    new
                    {
                        cp = Ansi(cp, 10), nno = Ansi(input.Nno, 18), na1 = Ansi(input.Na1, 40),
                        ty = Ansi(input.Ty, 14), fa = Ansi(input.Fa, 8), unit = Ansi(input.Unit, 4),
                        dc = Ansi(input.Dc, 8), oqty = input.Oqty, iprj = input.Iprj, cno = Ansi(input.Cno, 15),
                        orderdate = input.Orderdate.Date, bzlx = Ansi(input.Bzlx, 10), fkfs = Ansi(input.Fkfs, 10),
                        gyname = Ansi(input.Gyname, 60), gname = Ansi(input.Gname, 40), per = Ansi(per, 10),
                        oprc = input.Oprc ?? 0, dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
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

    public async Task<bool> UpdateLineAsync(int id, decimal oqty, decimal iprj, decimal oprc, string fa, string dpm, string cp, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // U5（___.row352:L55）：oqty,iprj,oprc,fa
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tborder SET oqty = @oqty, iprj = @iprj, oprc = @oprc, fa = @fa WHERE id = @id",
                new { oqty, iprj, oprc, fa = Ansi(fa, 8), id }, transaction: tx, cancellationToken: ct));
            // D4：oqty=0 → 删行
            if (oqty == 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE tborder WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                return true;
            }
            await tx.CommitAsync(ct);
            return false;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteLineAsync(int id, string dpm, string cp, CancellationToken ct = default)
    {
        // D1（k_.row35:L8）：delete by id（id= 前缀无 tbxzb）
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tborder WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task UpdateHeadAsync(string oldCno, PlanHeadInput head, string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 防重（___.row180:L32）：cno=?m_cno or orno=?m_cno
            if (!string.Equals(head.NewCno.Trim(), oldCno.Trim(), StringComparison.Ordinal))
            {
                var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(1) FROM tborder WITH (NOLOCK) WHERE cno = @cno OR orno = @cno",
                    new { cno = Ansi(head.NewCno, 15) }, transaction: tx, cancellationToken: ct));
                if (dup > 0) throw new AppException("CNO_EXISTS", "订货单已存在该合同号", 409);
            }
            // U2/U3（___.row180:L52/L55）：gname 非空才更新该列
            var setGname = !string.IsNullOrWhiteSpace(head.Gname);
            var sql = setGname
                ? "UPDATE tborder SET cno = @newCno, orderdate = @orderdate, bzlx = @bzlx, fkfs = @fkfs, gyname = @gyname, gname = @gname WHERE cno = @oldCno" + TborderXzb
                : "UPDATE tborder SET cno = @newCno, orderdate = @orderdate, bzlx = @bzlx, fkfs = @fkfs, gyname = @gyname WHERE cno = @oldCno" + TborderXzb;
            var args = new DynamicParameters(new
            {
                newCno = Ansi(head.NewCno, 15), orderdate = head.Orderdate.Date, bzlx = Ansi(head.Bzlx, 10),
                fkfs = Ansi(head.Fkfs, 10), gyname = Ansi(head.Gyname, 60), gname = Ansi(head.Gname, 40),
                oldCno = Ansi(oldCno, 15),
            });
            AddXzb(args, cp, dpmusem);
            await conn.ExecuteAsync(new CommandDefinition(sql, args, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> DeletePlanAsync(string cno, string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        // D3（o__.row181:L10）：type=''（旧 '    ' 尾随空格等价）+ tbxzb
        await using var conn = await factory.OpenAsync(ct);
        var args = new DynamicParameters(new { cno = Ansi(cno, 15) });
        AddXzb(args, cp, dpmusem);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"DELETE tborder WHERE cno = @cno AND type = ''{TborderXzb}", args, cancellationToken: ct));
    }

    public async Task<int> MergeToTransitAsync(string cno, string dpm, string cp, string dpmusem, CancellationToken ct = default)
    {
        // U1（燂.row162:L30）：SCAN 逐行 type='历史'、item=1..n、orderdate=P_RQ、cno=''（沙河丰田保留——不激活）
        // 2026-11-03 需求：orno 不再用原计划单号，自动取 RK 单号（散件入库单前缀，防重查 tborder.orno type='历史'）
        // ——订单入库页按 orno 拉单，单号自取号序列
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var args = new DynamicParameters(new { cno = Ansi(cno, 15) });
            AddXzb(args, cp, dpmusem);
            var ids = (await conn.QueryAsync<int>(new CommandDefinition(
                $"SELECT id FROM tborder WITH (UPDLOCK) WHERE cno = @cno AND type = '' AND RTRIM(nno) <> ''{TborderXzb} ORDER BY id",
                args, transaction: tx, cancellationToken: ct))).AsList();
            if (ids.Count == 0)
            {
                await tx.CommitAsync(ct);
                return 0;
            }
            // RK 单号在事务外取号（getnewdh 自带取号事务，避免嵌套锁）
            var transitNo = await docNo.GetNewDocNoAsync("散件入库单", dpm,
                new DocNo.DocNoService.DocNoCheckTarget("tborder", "orno", "历史"), ct);
            var n = 0;
            foreach (var id in ids)
            {
                n++;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tborder SET type = '历史', orno = @orno, cno = '', item = @item, orderdate = @orderdate WHERE id = @id",
                    new { orno = Ansi(transitNo, 15), item = n, orderdate = DateTime.Today, id },
                    transaction: tx, cancellationToken: ct));
            }
            // 2026-11-03 需求：并入在途后删除单头行（nno=''）——计划单从列表彻底消失，单据以 RK 单号进入在途
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE tborder WHERE cno = @cno AND type = '' AND RTRIM(ISNULL(nno, '')) = ''{TborderXzb}",
                args, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return n;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task ChangeQtyAsync(int id, decimal oqty, string dpm, string cp, CancellationToken ct = default)
    {
        // U8（g__.row510:L33）
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tborder SET oqty = @oqty WHERE id = @id", new { oqty, id }, cancellationToken: ct));
    }

    public async Task SplitLineAsync(int id, string newCno, decimal newQty, string dpm, string cp, string per, CancellationToken ct = default)
    {
        // I5 + U9（g__.row510:L46/L48/L55）：按新单号新增行 + 旧行余量
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var src = await conn.QueryFirstOrDefaultAsync<PlanLineRow>(new CommandDefinition(
                "SELECT * FROM tborder WITH (UPDLOCK) WHERE id = @id", new { id },
                transaction: tx, cancellationToken: ct));
            if (src is null) throw new AppException("LINE_NOT_FOUND", "订单行不存在", 404);

            var cpRow = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT RTRIM(cp) FROM tborder WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
            // 旧 g__.row510 L42-45：item=SUM(item)+1（旧码原样，SUM 跨单偏大 bug 照抄）
            var sumItem = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(item) FROM tborder WITH (NOLOCK) WHERE cno = @cno",
                new { cno = Ansi(newCno, 15) }, transaction: tx, cancellationToken: ct));

            // U9：旧行余量
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tborder SET oqty = @remain WHERE id = @id",
                new { remain = Math.Max(src.Oqty - newQty, 0), id }, transaction: tx, cancellationToken: ct));

            if (newQty < src.Oqty)
            {
                // I5：新单号新增一行（列集照旧 g__.row510 L46，不含 item——旧 PASSTH insert 不写 item）
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tborder (cp, cno, nno, na1, ty, fa, unit, dc, oqty, iprj, orderdate, bzlx, fkfs, gyname, gname, per, dpm)
                    SELECT cp, @newCno, nno, na1, ty, fa, unit, dc, @newQty, iprj, orderdate, bzlx, fkfs, gyname, gname, per, dpm
                    FROM tborder WHERE id = @id
                    """,
                    new { newCno = Ansi(newCno, 15), newQty, id },
                    transaction: tx, cancellationToken: ct));
            }
            else
            {
                // U10：整行迁移到新单号（item=SUM+1）
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tborder SET cno = @newCno, item = @item WHERE id = @id",
                    new { newCno = Ansi(newCno, 15), item = (sumItem ?? 0) + 1, id },
                    transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task MoveLineAsync(int id, string newCno, string dpm, string cp, CancellationToken ct = default)
    {
        // U10（g__.row510:L55）
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 旧 g__.row510 L51-55：item=SUM(item)+1（旧码原样）
            var sumItem = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(item) FROM tborder WITH (NOLOCK) WHERE cno = @cno",
                new { cno = Ansi(newCno, 15) }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tborder SET cno = @newCno, item = @item WHERE id = @id",
                new { newCno = Ansi(newCno, 15), item = (sumItem ?? 0) + 1, id }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>售价/批发价修改（U7：该编码库存全行 + I6：tbistoch 日志）。</summary>
    public async Task ChangeSalePriceAsync(string cp, string nno, decimal oprc, decimal oprb, string per, string dpm, CancellationToken ct = default)
        => await ChangeStockPriceAsync(cp, nno, null, null, oprc, oprb, null, per, dpm, ct);

    /// <summary>
    /// 修改库存售价（2026-11-04 销售开单右键需求；U7 扩展 + A1 最低售价落库）：
    /// tbisto 该编码全行 oprc/oprb/posi/dc（ISNULL 保留不改项）+ tbprnoty.oprc_min + tbistoch 日志（I6 同格式）。
    /// 无库存批次行（tbisto 无记录）时不改库存档只改属性最低售价，不写日志。
    /// </summary>
    public async Task ChangeStockPriceAsync(string cp, string nno, string? posi, string? dc,
        decimal oprc, decimal oprb, decimal? oprcMin, string per, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await conn.QueryFirstOrDefaultAsync<(int id, decimal oprc, decimal oprb, string posi, string dc)>(
                new CommandDefinition(
                    "SELECT TOP (1) id, oprc, oprb, RTRIM(ISNULL(posi,'')) posi, RTRIM(ISNULL(dc,'')) dc " +
                    "FROM tbisto WITH (UPDLOCK) WHERE cp = @cp AND nno = @nno ORDER BY id DESC",
                    new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, transaction: tx, cancellationToken: ct));
            var note = "";
            if (old.id != 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbisto SET oprc = @oprc, oprb = @oprb, " +
                    "posi = ISNULL(@posi, posi), dc = ISNULL(@dc, dc) WHERE cp = @cp AND nno = @nno",
                    new
                    {
                        oprc, oprb,
                        posi = posi is null ? null : Ansi(posi, 10),
                        dc = dc is null ? null : Ansi(dc, 8),
                        cp = Ansi(cp, 10), nno = Ansi(nno, 18),
                    }, transaction: tx, cancellationToken: ct));

                note = (oprc != old.oprc ? $"  售价:{old.oprc}-->{oprc}" : "") +
                       (oprb != old.oprb ? $"  批发价:{old.oprb}-->{oprb}" : "") +
                       (posi is not null && posi.Trim() != old.posi ? $"  仓位:{old.posi}-->{posi.Trim()}" : "") +
                       (dc is not null && dc.Trim() != old.dc ? $"  产地:{old.dc}-->{dc.Trim()}" : "");
                if (note.Length > 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO tbistoch (CP, NNO, NA1, XGDATE, per, note, dpm) VALUES (@cp, @nno, @na1, @xgdate, @per, @note, @dpm)",
                        new
                        {
                            cp = Ansi(cp, 10), nno = Ansi(nno, 18),
                            na1 = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                                "SELECT TOP (1) RTRIM(na1) FROM tbisto WHERE cp = @cp AND nno = @nno",
                                new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, transaction: tx, cancellationToken: ct)) ?? "",
                            xgdate = DateTime.Now, per = Ansi(per, 6), note = Ansi(note, 100), dpm = Ansi(dpm, 10),
                        }, transaction: tx, cancellationToken: ct));
                }
            }
            if (oprcMin is { } min)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbprnoty SET oprc_min = @min WHERE RTRIM(cp) = @cp AND RTRIM(nno) = @nno",
                    new { min, cp = cp.Trim(), nno = nno.Trim() }, transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> AddProfileAsync(PartProfileInput input, string dpm, CancellationToken ct = default)
    {
        // I3（鵎_.row422:L25）
        if (string.IsNullOrWhiteSpace(input.Nno)) throw new AppException("NNO_REQUIRED", "编码不能为空", 422);
        await using var conn = await factory.OpenAsync(ct);
        var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbprnoty WITH (NOLOCK) WHERE nno = @nno",
            new { nno = Ansi(input.Nno, 18) }, cancellationToken: ct));
        if (dup > 0) throw new AppException("NNO_DUP", "该编码已存在", 409);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO tbprnoty
                (cp, nno, na1, ty, fa, dc, unit, iprj, code_ts, eng, nnp, nnq, uqty, xno, tno, amjj, bmjj, note)
            OUTPUT INSERTED.id
            VALUES
                (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @iprj, @codeTs, @eng, @nnp, @nnq, @uqty, @xno, @tno, @amjj, @bmjj, @note)
            """,
            new
            {
                cp = Ansi(input.Cp, 10), nno = Ansi(input.Nno, 18), na1 = Ansi(input.Na1, 40),
                ty = Ansi(input.Ty, 14), fa = Ansi(input.Fa, 8), dc = Ansi(input.Dc, 8), unit = Ansi(input.Unit, 4),
                iprj = input.Iprj, codeTs = Ansi(input.CodeTs, 20), eng = Ansi(input.Eng, 20),
                nnp = Ansi(input.Nnp, 18), nnq = Ansi(input.Nnq, 18), uqty = input.Uqty,
                xno = Ansi(input.Xno, 10), tno = Ansi(input.Tno, 10), amjj = input.Amjj, bmjj = input.Bmjj,
                note = Ansi(input.Note, 254),
            }, cancellationToken: ct));
    }

    public async Task UpdateProfileAsync(string oldNno, PartProfileInput input, string dpm, string cp, CancellationToken ct = default)
    {
        // U6（鵎_.row422:L57）：17 列 + 改码
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tbprnoty SET nno = @nno, na1 = @na1, ty = @ty, fa = @fa, dc = @dc, unit = @unit, iprj = @iprj,
                   code_ts = @codeTs, eng = @eng, nnp = @nnp, nnq = @nnq, uqty = @uqty, xno = @xno, tno = @tno,
                   amjj = @amjj, bmjj = @bmjj, note = @note
            WHERE cp = @cp AND nno = @oldNno
            """,
            new
            {
                nno = Ansi(input.Nno, 18), na1 = Ansi(input.Na1, 40), ty = Ansi(input.Ty, 14), fa = Ansi(input.Fa, 8),
                dc = Ansi(input.Dc, 8), unit = Ansi(input.Unit, 4), iprj = input.Iprj, codeTs = Ansi(input.CodeTs, 20),
                eng = Ansi(input.Eng, 20), nnp = Ansi(input.Nnp, 18), nnq = Ansi(input.Nnq, 18), uqty = input.Uqty,
                xno = Ansi(input.Xno, 10), tno = Ansi(input.Tno, 10), amjj = input.Amjj, bmjj = input.Bmjj,
                note = Ansi(input.Note, 254), cp = Ansi(cp, 10), oldNno = Ansi(oldNno, 18),
            }, cancellationToken: ct));
        // U14 dbo.updatenno / U15 EDITALL 未复刻（备案：属性改码后的历史单据编码同步）
    }

    public async Task DeleteProfileAsync(string cp, string nno, string dpm, CancellationToken ct = default)
    {
        // D2（卒_.row333:L11）
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE tbprnoty WHERE cp = @cp AND nno = @nno",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PartInRecordRow>> GetInRecordsAsync(string cp, string nno, CancellationToken ct = default)
    {
        // §3.6（Y_.row7:L20）：c_jk
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PartInRecordRow>(new CommandDefinition(
            "SELECT indate, RTRIM(gyname) gyname, jkamount, iprj, iprc FROM tbistoed WITH (NOLOCK) " +
            "WHERE cp = @cp AND (nno = @nno OR nno IN (SELECT ISNULL(NULLIF(nnp, ''), nno) FROM tbprnoty WHERE cp = @cp AND nno = @nno)) ORDER BY indate DESC",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PartSaleRecordRow>> GetSaleRecordsAsync(string cp, string nno, CancellationToken ct = default)
    {
        // 2026-11-03 选料窗口历史区：tbsada 出库记录（含配套码 nnp/nnq 带出，nno 列显示实际出库编码）
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PartSaleRecordRow>(new CommandDefinition(
            "SELECT TOP 500 outdate, RTRIM(nno) nno, RTRIM(gname) gname, ckamount, oprc, RTRIM(per) per " +
            "FROM tbsada WITH (NOLOCK) WHERE cp = @cp AND nno IN (" +
            "SELECT @nno UNION " +
            "SELECT ISNULL(NULLIF(nnp, ''), nno) FROM tbprnoty WITH (NOLOCK) WHERE cp = @cp AND nno = @nno UNION " +
            "SELECT ISNULL(NULLIF(nnq, ''), nno) FROM tbprnoty WITH (NOLOCK) WHERE cp = @cp AND nno = @nno) " +
            "ORDER BY outdate DESC",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PartTransitRow>> GetTransitAsync(string cp, string nno, CancellationToken ct = default)
    {
        // §3.6（Y_.row7:L21）：c_zt
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PartTransitRow>(new CommandDefinition(
            "SELECT MAX(orderdate) orderdate, RTRIM(cno) cno, SUM(oqty - qty) ztqty, MAX(iprj) iprj " +
            "FROM tborder WITH (NOLOCK) WHERE cp = @cp AND nno = @nno AND type = '历史' AND oqty > qty " +
            "GROUP BY cno ORDER BY MAX(orderdate) DESC",
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PartSupplyRow>> GetSupplyPricesAsync(string cp, string nno, CancellationToken ct = default)
    {
        // §3.6（Y_.row7:L22）：c_gys（BZ 系数折算）
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PartSupplyRow>(new CommandDefinition(
            """
            SELECT RTRIM(a.gyname) gyname, a.iprj,
                   CAST(a.iprj * (CASE WHEN ISNULL(b.valuen, 0) = 0 THEN 1 ELSE b.valuen END) AS numeric(18, 2)) iprc, a.zq
            FROM tbgys a WITH (NOLOCK)
            LEFT JOIN (SELECT name, valuen FROM tbsysset WHERE type = 'BZ') b ON a.bzlx = b.name
            WHERE a.cp = @cp AND a.nno = @nno
            ORDER BY iprc
            """,
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18) }, cancellationToken: ct));
        return rows.AsList();
    }

    // ---------- 私有 ----------

    private async Task<string> GetFpcwCoreAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE name = N'废品仓位标识' AND type = 'system'",
            cancellationToken: ct)) is { } v && v.Length > 0 ? v : "废";
    }

    private async Task<IEnumerable<(string paraid, decimal nvalue)>> GetParamsCoreAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string paraid, decimal nvalue)>(new CommandDefinition(
            "SELECT RTRIM(paraid) paraid, nvalue FROM tborexppara WITH (NOLOCK) WHERE paraid IN ('P07', 'P08')",
            cancellationToken: ct));
        return rows;
    }

    private async Task<string> GetCurrentGynameAsync(CancellationToken ct) => "";

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
