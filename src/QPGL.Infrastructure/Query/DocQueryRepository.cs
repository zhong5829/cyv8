using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Query;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Query;

public interface IDocQueryRepository
{
    /// <summary>出库单行级查询（分页 + 排序白名单）。</summary>
    Task<PagedResult<OutDocRow>> SearchOutDocsAsync(OutDocQueryRequest request, CancellationToken ct = default);

    /// <summary>出库单据级查询（按 paper+type+dpm 聚合，一行 = 一张单；qp11 查询模式单据列表）。</summary>
    Task<PagedResult<OutDocBillRow>> SearchOutDocBillsAsync(OutDocQueryRequest request, CancellationToken ct = default);

    /// <summary>出库单明细行（明细窗，WHERE paper+type+dpm）。</summary>
    Task<IReadOnlyList<OutDocLine>> GetOutDocLinesAsync(string paper, string type, string dept, CancellationToken ct = default);

    /// <summary>单据金额汇总（收款窗：Σoprct 折前实收 / Σoprc 原价 / Σ折后）。</summary>
    Task<OutDocSummary> GetOutDocSummaryAsync(string paper, string type, string dept, CancellationToken ct = default);
}

/// <summary>单据金额汇总（收款确认窗口依据，旧 fr_sk TAMT/TOPRC 单据级推广）。</summary>
public sealed class OutDocSummary
{
    public required string Paper { get; set; }
    public required string Type { get; set; }
    public required string Dpm { get; set; }
    public string Gname { get; set; } = "";
    public string Gno { get; set; } = "";
    public string Pno { get; set; } = "";
    /// <summary>开单操作员（tbsada.per；查询模式单头栏「操作员」显示来源）。</summary>
    public string Per { get; set; } = "";
    public string TMode { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Mark { get; set; } = "";
    public string Guno { get; set; } = "";
    public DateTime Outdate { get; set; }
    public string Spname { get; set; } = "";
    /// <summary>Σoprct（实收参考，旧 TAMT 来源字段）。</summary>
    public decimal TotalOprct { get; set; }
    /// <summary>Σoprc（原价应收，旧 TOPRC 来源字段）。</summary>
    public decimal TotalOprc { get; set; }
    /// <summary>Σ折后 = Σ CAST(oprct*(100-yhl)/100)（旧 yhloprct 公式，收款默认金额）。</summary>
    public decimal TotalDiscounted { get; set; }
}

/// <summary>
/// 单据查询仓储（M3-2 出库单查询，只读 NOLOCK）。
/// 旧实现出处：fr_sesa row4 ——
///   WHERE type=?m_cc AND trigger_mark=0 AND ISNULL((select top 1 papertype from tbsada_header where paper=c.paper),'')&lt;&gt;'内耗'
///   列含 yhloprct=cast(oprct*(100-yhl)/100 as numeric(12,2))，ORDER BY outdate desc。
/// 新增约束（§7.1）：强制分页 + 排序白名单，禁 SELECT *。
/// </summary>
public sealed class DocQueryRepository(SqlConnectionFactory factory) : IDocQueryRepository
{
    private const string ListColumns = """
        c.id, c.paper, c.type, c.outdate, c.gname, c.gno, c.per, c.per1,
        c.cp, c.nno, c.na1, c.ckamount, c.oprc, c.oprct,
        CAST(c.oprct * (100 - c.yhl) / 100 AS numeric(12,2)) AS yhloprct,
        c.guno, c.mark, c.qrdate, c.pno, c.t_mode, c.ypaper, c.dpm, c.depotno, c.remark,
        ISNULL(h.spname, '') AS spname, ISNULL(h.papertype, '') AS papertype
        """;

    public async Task<PagedResult<OutDocRow>> SearchOutDocsAsync(OutDocQueryRequest request, CancellationToken ct = default)
    {
        var type = (request.Type ?? "销售").Trim();
        if (type.Length == 0) type = "销售";

        var from = request.DateFrom ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = request.DateTo ?? from.AddMonths(1).AddDays(-1);

        var where = "c.type = @type AND c.trigger_mark = 0 AND c.outdate >= @from AND c.outdate < @toEnd" +
                    " AND ISNULL(h.papertype, '') <> N'内耗'";
        var p = new DynamicParameters();
        p.Add("type", new DbString { Value = type, IsAnsi = false, Length = 10 });
        p.Add("from", from);
        // 旧系统为 outdate<下月1日；这里取用户可见语义「止(含)」转换为止次日零点，保持边界等价
        p.Add("toEnd", to.AddDays(1).Date);
        if (!string.IsNullOrWhiteSpace(request.Paper))
        {
            where += " AND c.paper LIKE @paper";
            p.Add("paper", $"%{request.Paper.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Gname))
        {
            where += " AND c.gname LIKE @gname";
            p.Add("gname", $"%{request.Gname.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Per))
        {
            where += " AND c.per = @per";
            p.Add("per", new DbString { Value = request.Per.Trim(), IsAnsi = true, Length = 10 });
        }
        // 旧 fr_sesa row4 L138：PASSTH 自动补 tbxzb(tbsada) 条件 `?p_dpmusem like '%'+rtrim(dpm)+'%'`；
        // 权限串空 = 不过滤（旧系统单店部署 P_DPMUSEM='' 恒真语义）
        var dpmusem = (request.DpmPermissions ?? "").Trim();
        if (dpmusem.Length > 0)
        {
            where += " AND @dpmusem LIKE '%' + RTRIM(c.dpm) + '%'";
            p.Add("dpmusem", new DbString { Value = dpmusem, IsAnsi = true, Length = 100 });
        }

        var orderBy = OutDocSortFields.TryResolve(request.Sort, out var col)
            ? $"{col} {(request.IsDescending ? "DESC" : "ASC")}, c.id DESC"
            : "c.outdate DESC, c.id DESC";

        await using var conn = await factory.OpenAsync(ct);
        var sql = $"""
            SELECT {ListColumns}
            FROM tbsada c WITH (NOLOCK)
            LEFT JOIN (SELECT paper, spname, papertype FROM tbsada_header WITH (NOLOCK)) h ON h.paper = c.paper
            WHERE {where}
            ORDER BY {orderBy}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            SELECT COUNT(1) FROM tbsada c WITH (NOLOCK)
            LEFT JOIN (SELECT paper, spname, papertype FROM tbsada_header WITH (NOLOCK)) h ON h.paper = c.paper
            WHERE {where};
            """;
        p.Add("skip", (request.Page - 1) * request.PageSize);
        p.Add("take", request.PageSize);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = (await multi.ReadAsync<OutDocRow>()).AsList();
        var total = await multi.ReadFirstAsync<int>();
        return new PagedResult<OutDocRow>
        {
            Items = rows,
            Total = total,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    /// <summary>
    /// 单据级查询：按 paper+type+dpm 聚合（一行 = 一张单），列语义与行级查询一致。
    /// mark 取 MAX（任一行已收即已收）；聚合条件复用行级 WHERE 构造（提取为 BuildOutDocWhere）。
    /// </summary>
    public async Task<PagedResult<OutDocBillRow>> SearchOutDocBillsAsync(OutDocQueryRequest request, CancellationToken ct = default)
    {
        var type = (request.Type ?? "销售").Trim();
        if (type.Length == 0) type = "销售";

        var from = request.DateFrom ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = request.DateTo ?? from.AddMonths(1).AddDays(-1);

        var where = "c.type = @type AND c.trigger_mark = 0 AND c.outdate >= @from AND c.outdate < @toEnd" +
                    " AND ISNULL(h.papertype, '') <> N'内耗'";
        var p = new DynamicParameters();
        p.Add("type", new DbString { Value = type, IsAnsi = false, Length = 10 });
        p.Add("from", from);
        p.Add("toEnd", to.AddDays(1).Date);
        if (!string.IsNullOrWhiteSpace(request.Paper))
        {
            where += " AND c.paper LIKE @paper";
            p.Add("paper", $"%{request.Paper.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Gname))
        {
            where += " AND c.gname LIKE @gname";
            p.Add("gname", $"%{request.Gname.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Per))
        {
            where += " AND c.per = @per";
            p.Add("per", new DbString { Value = request.Per.Trim(), IsAnsi = true, Length = 10 });
        }
        var dpmusem = (request.DpmPermissions ?? "").Trim();
        if (dpmusem.Length > 0)
        {
            where += " AND @dpmusem LIKE '%' + RTRIM(c.dpm) + '%'";
            p.Add("dpmusem", new DbString { Value = dpmusem, IsAnsi = true, Length = 100 });
        }

        var orderBy = OutDocSortFields.TryResolve(request.Sort, out var col)
            ? $"{col} {(request.IsDescending ? "DESC" : "ASC")}"
            : "MAX(c.outdate) DESC";
        // 聚合查询排序列必须包聚合：非白名单日期列统一用 MAX(col)（组内单值，MAX 语义无损）
        if (orderBy.StartsWith("c.", StringComparison.Ordinal)) orderBy = $"MAX({orderBy})";
        orderBy += ", MIN(c.id) DESC";

        await using var conn = await factory.OpenAsync(ct);
        var sql = $"""
            SELECT c.paper, MAX(c.type) AS type, MAX(c.dpm) AS dpm, MAX(c.outdate) AS outdate,
                   MAX(c.gname) AS gname, MAX(c.per) AS per,
                   COUNT(1) AS linecount, SUM(c.ckamount) AS totalqty,
                   SUM(CAST(c.oprct * (100 - c.yhl) / 100 AS numeric(12,2))) AS totalamount,
                   SUM(CAST(c.oprct * c.ckamount AS numeric(12,2))) AS totalOprct,
                   MAX(c.oprct) AS unitPrice,
                   MAX(c.guno) AS guno, MAX(c.mark) AS mark, MAX(c.qrdate) AS qrdate,
                   -- 单据备注排除废品行标记（2026-10-29：[已作废]只进配件行备注，单据区不显示，LIKE 中 [ 需转义）
                   MAX(CASE WHEN c.remark LIKE '%\[已作废]' ESCAPE '\' THEN '' ELSE c.remark END) AS remark,
                   CASE WHEN MAX(c.mark) = 'Y' THEN N'已确认'
                        WHEN MAX(c.mark) = 'T' THEN N'已提货'
                        WHEN MAX(c.mark) = 'R' THEN N'已入库'
                        WHEN MAX(c.mark) = '' AND MAX(c.guno) = '' THEN N'开单中'
                        ELSE '' END AS sk
            FROM tbsada c WITH (NOLOCK)
            LEFT JOIN (SELECT paper, spname, papertype FROM tbsada_header WITH (NOLOCK)) h ON h.paper = c.paper
            WHERE {where}
            GROUP BY c.paper, c.type, c.dpm
            ORDER BY {orderBy}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            SELECT COUNT(DISTINCT c.paper + '|' + c.type + '|' + c.dpm) FROM tbsada c WITH (NOLOCK)
            LEFT JOIN (SELECT paper, spname, papertype FROM tbsada_header WITH (NOLOCK)) h ON h.paper = c.paper
            WHERE {where};
            """;
        p.Add("skip", (request.Page - 1) * request.PageSize);
        p.Add("take", request.PageSize);

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = (await multi.ReadAsync<OutDocBillRow>()).AsList();
        var total = await multi.ReadFirstAsync<int>();
        return new PagedResult<OutDocBillRow>
        {
            Items = rows,
            Total = total,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    public async Task<IReadOnlyList<OutDocLine>> GetOutDocLinesAsync(string paper, string type, string dept, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            $"""
            SELECT c.id, c.cp, c.nno, c.na1, c.ckamount, c.oprc, c.oprct,
                   CAST(c.oprct * (100 - c.yhl) / 100 AS numeric(12,2)) AS yhloprct,
                   CAST(c.oprct * c.ckamount AS numeric(12,2)) AS oprctTotal,
                   CAST(c.oprct * c.ckamount * (100 - c.yhl) / 100 AS numeric(12,2)) AS yhloprctTotal,
                   c.ty, c.posi, c.unit, c.depotno, c.remark
            FROM tbsada c WITH (NOLOCK)
            WHERE c.paper = @paper AND c.type = @type AND c.dpm = @dpm
            ORDER BY c.id
            """,
            new
            {
                paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 },
            },
            cancellationToken: ct);
        return (await conn.QueryAsync<OutDocLine>(cmd)).AsList();
    }

    public async Task<OutDocSummary> GetOutDocSummaryAsync(string paper, string type, string dept, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            $"""
            SELECT TOP (1)
                   c.paper, c.type, c.dpm, MAX(c.gname) AS gname, MAX(c.gno) AS gno, MAX(c.pno) AS pno,
                   MAX(c.per) AS per,
                   MAX(c.t_mode) AS t_mode,
                   MAX(CASE WHEN c.remark LIKE '%\[已作废]' ESCAPE '\' THEN '' ELSE c.remark END) AS remark,
                   MAX(c.mark) AS mark, MAX(c.guno) AS guno,
                   MAX(c.outdate) AS outdate,
                   SUM(CAST(c.oprct * c.ckamount AS numeric(12,2))) AS total_oprct,
                   SUM(CAST(c.oprc * c.ckamount AS numeric(12,2))) AS total_oprc,
                   SUM(CAST(c.oprct * c.ckamount * (100 - c.yhl) / 100 AS numeric(12,2))) AS total_discounted,
                   ISNULL(MAX(h.spname), '') AS spname
            FROM tbsada c WITH (NOLOCK)
            LEFT JOIN (SELECT paper, spname FROM tbsada_header WITH (NOLOCK)) h ON h.paper = c.paper
            WHERE c.paper = @paper AND c.type = @type AND c.dpm = @dpm
            GROUP BY c.paper, c.type, c.dpm
            """,
            new
            {
                paper = new DbString { Value = paper, IsAnsi = true, Length = 10 },
                type = new DbString { Value = type, IsAnsi = false, Length = 10 },
                dpm = new DbString { Value = dept, IsAnsi = true, Length = 10 },
            },
            cancellationToken: ct);
        // 元组按位置映射（Dapper）：字段序必须与 SELECT 列序一致（perf B6 抓出 spname/decimal 错位）
        var row = await conn.QueryFirstOrDefaultAsync<(string paper, string type, string dpm, string gname, string gno,
            string pno, string per, string t_mode, string remark, string mark, string guno, DateTime outdate,
            decimal total_oprct, decimal total_oprc, decimal total_discounted, string spname)>(cmd);
        if (row.paper is null)
            throw new AppException("DOC_NOT_FOUND", $"单据 {paper} 不存在", 404);
        return new OutDocSummary
        {
            Paper = row.paper.Trim(),
            Type = row.type.Trim(),
            Dpm = row.dpm.Trim(),
            Gname = row.gname.Trim(),
            Gno = row.gno.Trim(),
            Pno = row.pno.Trim(),
            Per = row.per.Trim(),
            TMode = row.t_mode.Trim(),
            Remark = row.remark.Trim(),
            Mark = row.mark.Trim(),
            Guno = row.guno.Trim(),
            Outdate = row.outdate,
            Spname = row.spname.Trim(),
            TotalOprct = row.total_oprct,
            TotalOprc = row.total_oprc,
            TotalDiscounted = row.total_discounted,
        };
    }
}
