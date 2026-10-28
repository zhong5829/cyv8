using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Orders;

public interface IOrderBudRepository
{
    /// <summary>既有自动订单列表（type=''，Init 口径）。</summary>
    Task<IReadOnlyList<(string Cno, string Orderdate)>> GetOrderNosAsync(CancellationToken ct = default);
    /// <summary>按公式批量生成订单（fr_orderbud myclick，契约 §2.2；返回 项数/订货总数）。</summary>
    Task<(int Items, decimal TotalOqtyIprj)> GenerateAsync(OrderBudCommand cmd, string dpm, string per, CancellationToken ct = default);
}

/// <summary>自动订货生成参数（旧表单控件全集）。</summary>
public sealed class OrderBudCommand
{
    /// <summary>订单号（手输，判空 + 查重 + 覆盖/追加）。</summary>
    public required string Cno { get; set; }
    /// <summary>追加模式：已存在同号订单时报错（旧 CHEADD=1）。</summary>
    public bool AppendMode { get; set; }
    public DateTime OrderDate { get; set; } = DateTime.Today;
    /// <summary>订货公式（tborderexp.cexpress）。</summary>
    public required string CExpress { get; set; }
    /// <summary>订货依据：1=根据属性 2=根据供应商价格 3=根据进库历史。</summary>
    public int ComboType { get; set; } = 1;
    public string Gyname { get; set; } = "";
    /// <summary>进库历史窗口（依据③）。</summary>
    public DateTime InDate1 { get; set; }
    public DateTime InDate2 { get; set; }
    /// <summary>指定库号（可选）。</summary>
    public string Depot { get; set; } = "";
    /// <summary>出库统计窗口（M_DSTR：outdate between）。</summary>
    public DateTime Date1 { get; set; } = DateTime.Today.AddYears(-1);
    public DateTime Date2 { get; set; } = DateTime.Today;
    /// <summary>P02 月均：起始月/结束月（格式 yyyyMM；P02 不在公式中可空）。</summary>
    public string? BMon { get; set; }
    public string? EMon { get; set; }
    /// <summary>车型过滤（TyExclude=true 时为排除）。</summary>
    public string Ty { get; set; } = "";
    public bool TyExclude { get; set; }
    public string Dc { get; set; } = "";
    public string Fa { get; set; } = "";
    /// <summary>库存量区间（默认 -9999~9999 不追加条件）。</summary>
    public decimal BAmt { get; set; } = -9999;
    public decimal EAmt { get; set; } = 9999;
    /// <summary>厂牌（旧 p_cp；厂牌会话未接入，显式传值）。</summary>
    public required string Cp { get; set; }
}

/// <summary>
/// 自动订货仓储（菜单 F.4，权限码 F4；fr_orderbud myclick 契约 §2.2）。
/// 旧流程：DELETE tborder WHERE cno（覆盖模式）→ 巨型 INSERT...SELECT（订货数>=1 行）。
/// 旧系统两步独立提交（INSERT 失败订单已删——旧有风险）；新系统包进单事务（回放对齐最终状态）。
/// P_FPCW（废品仓位标识）运行时读 tbsysset type='system'，缺省「废」（pg_proc L3285）；
/// P_DPMUSEM 单店部署传当前部门；公式串来自 tborderexp（管理员维护），先白名单校验再内联（与旧 STRTRAN 语义一致）。
/// </summary>
public sealed partial class OrderBudRepository(SqlConnectionFactory factory) : IOrderBudRepository
{
    [GeneratedRegex(@"^[A-Za-z0-9_+*/().\- ]*$")]
    private static partial Regex ExpChars();

    public async Task<IReadOnlyList<(string Cno, string Orderdate)>> GetOrderNosAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string cno, string orderdate)>(new CommandDefinition(
            """
            SELECT RTRIM(cno) cno, CONVERT(char(10), MAX(orderdate), 120) orderdate
            FROM tborder WITH (NOLOCK) WHERE type = '' GROUP BY cno ORDER BY MAX(orderdate) DESC
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<(int Items, decimal TotalOqtyIprj)> GenerateAsync(OrderBudCommand cmd, string dpm, string per, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Cno)) throw new AppException("CNO_REQUIRED", "订单号不能为空", 422);
        if (cmd.ComboType != 1 && string.IsNullOrWhiteSpace(cmd.Gyname))
            throw new AppException("GYNAME_REQUIRED", "请输入供应商名称", 422);
        var cExpress = cmd.CExpress.Trim();
        if (string.IsNullOrEmpty(cExpress)) throw new AppException("EXP_MISSING", "订货公式不存在!", 422);
        if (!ExpChars().IsMatch(cExpress)) throw new AppException("EXP_INVALID", "订货公式含非法字符", 422);

        await using var conn = await factory.OpenAsync(ct);

        // 供应商存在性（依据②，fr_orderbud L270-276）
        if (cmd.ComboType == 2 && !string.IsNullOrWhiteSpace(cmd.Gyname))
        {
            var has = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM tbgys WITH (NOLOCK) WHERE gyname = @gyname",
                new { gyname = Ansi(cmd.Gyname, 40) }, cancellationToken: ct));
            if (has == 0) throw new AppException("GY_NOT_FOUND", "供应商价格库中找不到该供应商，所以不能根据供应商订货", 404);
        }

        // 公式变换（fr_orderbud L219-267）
        var exp = cExpress
            .Replace("P01", "kcamount").Replace("P03", "ztqty").Replace("P04", "amjj")
            .Replace("P05", "bmjj").Replace("P06", "Coeffict").Replace("P09", "ckamount")
            .Replace("P10", "ysamount").Replace("P11", "dbztamount");
        var depotFilter = string.IsNullOrWhiteSpace(cmd.Depot) ? "" : " and depotno=@m_depot";
        var hasP12 = exp.Contains("P12");
        if (hasP12) exp = exp.Replace("P12", "xqcs");
        var needP02 = exp.Contains("P02");

        // P07/P08 常量替换（tborexppara；数值内联）
        var paras = (await conn.QueryAsync<(string paraid, decimal nvalue)>(new CommandDefinition(
            "SELECT RTRIM(paraid) paraid, nvalue FROM tborexppara WITH (NOLOCK) WHERE paraid IN ('P07', 'P08')",
            cancellationToken: ct))).AsList();
        foreach (var (paraid, nvalue) in paras)
            exp = exp.Replace(paraid, nvalue.ToString("0.##"));

        // P02 月均（fr_orderbud L246-267：RE_MON=MIN(MAX(RB,RE),当前月)，月数上限 12）
        string? p02Expr = null;
        if (needP02)
        {
            if (string.IsNullOrWhiteSpace(cmd.BMon) || string.IsNullOrWhiteSpace(cmd.EMon))
                throw new AppException("P02_MON_REQUIRED", "公式含 P02，需输入起始月/结束月", 422);
            var rbMon = cmd.BMon.Trim();
            var reMonRaw = cmd.EMon.Trim();
            var curMon = $"{DateTime.Today.Year}{DateTime.Today.Month:00}";
            reMonRaw = string.CompareOrdinal(reMonRaw, rbMon) < 0 ? rbMon : reMonRaw;
            reMonRaw = string.CompareOrdinal(reMonRaw, curMon) > 0 ? curMon : reMonRaw;
            var months = (int.Parse(reMonRaw[..4]) - int.Parse(rbMon[..4])) * 12
                         + (int.Parse(reMonRaw[4..]) - int.Parse(rbMon[4..])) + 1;
            months = Math.Min(months, 12);
            var rb = int.Parse(rbMon[4..]);
            var parts = Enumerable.Range(1, months)
                .Select(m => { var ln = m + rb - 1; return $"oamou{(ln > 12 ? ln - 12 : ln)}"; });
            p02Expr = $"({string.Join("+", parts)})/{months}";
            exp = exp.Replace("P02", p02Expr);
        }

        // FROM 分支（fr_orderbud L268-285）
        string mFrom;
        var hasGy = !string.IsNullOrWhiteSpace(cmd.Gyname);
        if (hasGy && cmd.ComboType == 2)
        {
            mFrom = " from (select * from tbgys where dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem))) a left join tbprnoty p on a.cp=p.cp and a.nno=p.nno";
        }
        else if (hasGy)
        {
            const string newIprc =
                "select distinct n.cp,n.nno,MIN(n.iprc) as iprj,MAX(n.indate) as indate from " +
                "(select cp,nno,max(indate) max_indate from tbistoed group by cp,nno) m " +
                "right join tbistoed n on m.cp=n.cp and m.nno=n.nno and n.indate=m.max_indate";
            mFrom = " from tbprnoty p RIGHT join (" + newIprc +
                    " where indate>@m_indate1 and indate<=@m_indate2" + depotFilter + " and gyname=@m_gyname group by n.cp,n.nno) a on a.cp=p.cp and a.nno=p.nno ";
        }
        else
        {
            mFrom = " from tbprnoty p WITH(nolock) ";
        }

        // 巨型 INSERT...SELECT（fr_orderbud L286-309 TEXTMERGE 原样，参数化改写）
        var mDstr = "outdate>=@m_date1 and outdate<=@m_date2";
        var gyCol = hasGy ? ",gyname" : "";
        var gyVal = hasGy ? ",@m_gyname" : "";
        var iprcCol = hasGy && cmd.ComboType == 2 ? "a.iprj" : "p.iprj";
        var p12Select = hasP12 ? " ,ISNULL(xqcs,0) xqcs " : "";
        var p12Join = hasP12
            ? " left join (select cp,nno,count(nno) xqcs from (select cp,nno from tbsada with(nolock) where " +
              mDstr + depotFilter + " and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and type='需求') xx group by xx.cp,xx.nno) aaxq on p.cp=aaxq.cp and p.nno=aaxq.nno"
            : "";
        var depotJoin = string.IsNullOrWhiteSpace(cmd.Depot) ? "" : depotFilter;

        var sql = $"""
            INSERT INTO tborder(dpm,cno,nno,na1,dc,cp,ty,fa,iprj,oqty,unit,note,orderdate{gyCol},per)
            SELECT @p_dpm dpm, @c_cno cno, nno, na1, dc, cp, ty, fa, iprj, ({exp}) oqty, unit, note, @m_orderdate orderdate{gyVal}, @p_per per FROM
            (select p.nno,ISNULL(i.na1,p.na1) na1,ISNULL(i.ty,p.ty) ty,ISNULL(i.fa,p.fa) fa,isnull(k.kcamount,0)+isnull(mx.xam,0) kcamount,{iprcCol} iprj,isnull(z.ztqty,0) ztqty,
              isnull(p.uqty-k.kcamount-z.oqty,0) ydqty,p.nnp,p.nnq,ISNULL(i.note,p.note) note,ISNULL(i.unit,p.unit) unit,ISNULL(i.dc,p.dc) dc,ISNULL(y.coeffict,1) coeffict,p.uqty,p.code_ts,s.amjj,s.bmjj,p.cp,
              isnull(i.iprc,0) iprc,isnull(i.oprc,0) as oprc,isnull(i.oprb,0) as oprb,isnull(z.oqty,0) oqty,ISNULL(r.oamou1, 0) AS oamou1, ISNULL(r.oamou2, 0) AS oamou2,
              ISNULL(r.oamou3, 0) AS oamou3, ISNULL(r.oamou4,0) AS oamou4, ISNULL(r.oamou5, 0) AS oamou5, ISNULL(r.oamou6, 0) AS oamou6,ISNULL(r.oamou7, 0) AS oamou7,
              ISNULL(r.oamou8, 0) AS oamou8, ISNULL(r.oamou9,0) AS oamou9,ISNULL(r.oamou10, 0) AS oamou10, ISNULL(r.oamou11, 0) AS oamou11,convert(int,0) num,
              ISNULL(r.oamou12, 0) AS oamou12,ISNULL(c.low_rate,0) AS low_rate,isnull(ckamount,0) ckamount,isnull(ysamount,0) ysamount,ISNULL(dbztamount,0) dbztamount{p12Select}
              {mFrom}
              Left join tbistojj s with(nolock) on s.cp=@p_cp and s.nno=p.nno and s.dpm=@p_dpm{depotJoin}
              left join (select cp,nno,sum(oamou1) oamou1 ,sum(oamou2) oamou2 ,sum(oamou3) oamou3 ,sum(oamou4) oamou4 ,sum(oamou5) oamou5 ,sum(oamou6) oamou6 ,sum(oamou7) oamou7 ,sum(oamou8) oamou8 ,sum(oamou9) oamou9 ,sum(oamou10) oamou10 ,sum(oamou11) oamou11 ,sum(oamou12) oamou12 from tbrtsamo where dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) group by cp,nno ) r on p.cp=r.cp and p.nno=r.nno
              left join tbdccoef y on p.dc=y.dc
              left join (select cp,nno,sum(kcamount) kcamount,max(id) id from tbisto with(nolock) where posi<>@p_fpcw and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)){depotJoin} group by cp,nno) K on p.cp=k.cp and p.nno=k.nno
              left join tbisto i with(nolock) on p.cp=i.cp and p.nno=i.nno and k.id=i.id
              left join (SELECT cp, nno,SUM(oqty - qty) ztqty,sum(oqty) oqty FROM tborder with(nolock) where type='历史' and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) group by cp,nno) z on p.cp=z.cp and p.nno=z.nno
              left join tbcomplex c on p.code_ts = c.code_ts
              left join (select x.cp,x.nno,sum(kcamount) xam from tbprnoty x with(nolock) left join tbisto v with(nolock) on x.cp=v.cp{depotJoin} where v.posi<>@p_fpcw and v.nno in (case when x.nno=x.nnp then '' else x.nnp end,case when x.nno=x.nnq then '' else case when x.nnp=x.nnq then '' else x.nnq end end) group by x.cp,x.nno) mx on p.cp=mx.cp and p.nno=mx.nno
              left join (select p1.cp,p1.nno,sum(ckamount) ckamount from tbprnoty p1 with(nolock) left join (select cp,nno,ckamount from tbsada with(nolock) where {mDstr}{depotJoin} and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and (type='历史' or type='销售') union all select cp,nno,ckamount from tbxlsa with(nolock) where dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and {mDstr}{depotJoin} ) as aa on p1.cp=aa.cp where aa.nno in (p1.nno,p1.nnp,p1.nnq) group by p1.cp,p1.nno ) ck on p.nno=ck.nno and p.cp=ck.cp
              left join (select cp,nno,sum(ckamount) dbztamount from tbsada with(nolock) where type='调拨'{depotJoin} and mark<>'R' and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) group by cp,nno) dbzt on p.cp=dbzt.cp and p.nno=dbzt.nno
              left join (select cp,nno,sum(ckamount) ysamount from (select cp,nno,ckamount from tbsada with(nolock) where {mDstr}{depotJoin} and dpm in (select dpmbh from [dbo].[split_dpmusem](@p_dpmusem)) and type='预售') aa group by cp,nno) ys on p.cp=ys.cp and p.nno=ys.nno{p12Join}
            """;

        // 过滤条件（fr_orderbud L311-326）
        var cond = "p.cp=@p_cp and P.NNO not IN (SELECT NNP FROM TBPRNOTY WHERE NNP<>' ' union SELECT NNQ FROM TBPRNOTY WHERE NNQ<>' ')";
        if (!string.IsNullOrWhiteSpace(cmd.Ty))
            cond += cmd.TyExclude
                ? " and p.ty+p.note not like '%'+@ty_vou+'%'"
                : " and p.ty+p.note like '%'+@ty_vou+'%'";
        if (!string.IsNullOrWhiteSpace(cmd.Dc))
            cond += " and p.dc>=@dc_vou and p.dc<@dc_vou+'z'";
        if (!string.IsNullOrWhiteSpace(cmd.Fa))
            cond += " and p.fa>=@fa_vou and p.fa<@fa_vou+'z'";
        if (cmd.BAmt != -9999 || cmd.EAmt != 9999)
            cond += " and k.kcamount>=@n_bvou and k.kcamount<=@n_evou";
        if (hasGy && cmd.ComboType == 2)
            cond += " and a.gyname=@m_gyname";
        sql += $" where {cond} ) w where ({exp})>=1 order by cno,cp,nno";

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 查重 + 覆盖/追加（fr_orderbud L190-207；旧 DELETE 与 INSERT 两步独立提交——此处并入同事务）
            var existsCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM tborder WITH (NOLOCK) WHERE cno = @cno",
                new { cno = Ansi(cmd.Cno, 15) }, transaction: tx, cancellationToken: ct));
            if (existsCount > 0)
            {
                if (cmd.AppendMode) throw new AppException("CNO_EXISTS", "该订单号已存在,请输入新订单号.", 409);
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE tborder WHERE cno = @cno",
                    new { cno = Ansi(cmd.Cno, 15) }, transaction: tx, cancellationToken: ct));
            }

            var args = new DynamicParameters(new
            {
                p_dpm = Ansi(dpm, 10), c_cno = Ansi(cmd.Cno, 15), m_orderdate = cmd.OrderDate.Date, p_per = Ansi(per, 10),
                p_cp = Ansi(cmd.Cp, 10), m_gyname = Ansi(cmd.Gyname, 60), m_indate1 = cmd.InDate1.Date,
                m_indate2 = cmd.InDate2.Date, m_depot = Ansi(cmd.Depot, 10), p_dpmusem = Ansi(dpm, 20),
                m_date1 = cmd.Date1.Date, m_date2 = cmd.Date2.Date, p_fpcw = Ansi(await GetFpcwAsync(conn, tx, ct), 10),
                ty_vou = Ansi(cmd.Ty, 14), dc_vou = Ansi(cmd.Dc, 8), fa_vou = Ansi(cmd.Fa, 8),
                n_bvou = cmd.BAmt, n_evou = cmd.EAmt,
            });
            var inserted = await conn.ExecuteAsync(new CommandDefinition(sql, args, transaction: tx, cancellationToken: ct));
            var total = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                "SELECT ISNULL(SUM(oqty * iprj), 0) FROM tborder WITH (NOLOCK) WHERE cno = @cno",
                new { cno = Ansi(cmd.Cno, 15) }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return (inserted, total);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>P_FPCW（废品仓位标识）运行时读取（pg_proc L3284-3285：缺省「废」）。</summary>
    private static async Task<string> GetFpcwAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RTRIM(valuec) FROM tbsysset WITH (NOLOCK) WHERE name = N'废品仓位标识' AND type = 'system'",
            transaction: tx, cancellationToken: ct)) is { } v && v.Length > 0 ? v : "废";

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? "", IsAnsi = true, Length = length };
}
