using QPGL.Domain.Common;

namespace QPGL.Domain.Query;

/// <summary>
/// 出库单查询请求（E2 出库单查询，M3-2）。
/// 旧实现出处：fr_sesa row4 —— WHERE type=?m_cc AND trigger_mark=0 AND header.papertype&lt;&gt;'内耗'，
/// 默认日期=本月 1 日至下月 1 日，ORDER BY outdate desc。
/// </summary>
public sealed class OutDocQueryRequest : PagedRequest
{
    /// <summary>单据类型（tbsada.type）：销售/退货/调拨…（默认 销售）。</summary>
    public string Type { get; set; } = "销售";

    /// <summary>出库日期起（含）。null = 本月 1 日。</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>出库日期止（含）。null = 本月末。</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>单号（包含匹配）。</summary>
    public string? Paper { get; set; }

    /// <summary>客户名称（包含匹配）。</summary>
    public string? Gname { get; set; }

    /// <summary>操作员（精确匹配）。</summary>
    public string? Per { get; set; }

    /// <summary>
    /// 部门数据权限串（旧 P_DPMUSEM；空 = 不过滤，单店部署语义）。
    /// 过滤条件 = 旧 tbxzb 自动补式 `?p_dpmusem like '%'+rtrim(dpm)+'%'`（fr_sesa row4 L138 PASSTH 语义）。
    /// </summary>
    public string DpmPermissions { get; set; } = "";
}

/// <summary>出库单查询排序白名单（§7.1 禁注入）。</summary>
public static class OutDocSortFields
{
    /// <summary>camelCase 请求名 → tbsada 列。</summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["outdate"] = "c.outdate",
        ["paper"] = "c.paper",
        ["gname"] = "c.gname",
        ["per"] = "c.per",
        ["type"] = "c.type",
        ["oprct"] = "c.oprct",
        ["ckamount"] = "c.ckamount",
        ["guno"] = "c.guno",
        ["id"] = "c.id",
    };

    public static bool TryResolve(string? sort, out string column)
    {
        column = "";
        if (string.IsNullOrWhiteSpace(sort)) return false;
        return Map.TryGetValue(sort.Trim(), out column!);
    }
}

/// <summary>出库单行（行级列表，列集对齐旧 fr_sesa c_sada 精简）。</summary>
public sealed class OutDocRow
{
    public int Id { get; set; }
    public string Paper { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime Outdate { get; set; }
    public string Gname { get; set; } = "";
    public string Gno { get; set; } = "";
    public string Per { get; set; } = "";
    public string Per1 { get; set; } = "";
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Ckamount { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprct { get; set; }
    /// <summary>折后金额 = CAST(oprct*(100-yhl)/100 AS numeric(12,2))（旧 yhloprct 列公式）。</summary>
    public decimal Yhloprct { get; set; }
    /// <summary>付款方式（收款确认标志之一）。</summary>
    public string Guno { get; set; } = "";
    /// <summary>'Y' = 已收款确认（fr_sk mark 语义）。</summary>
    public string Mark { get; set; } = "";
    public DateTime Qrdate { get; set; }
    public string Pno { get; set; } = "";
    public string TMode { get; set; } = "";
    public string Ypaper { get; set; } = "";
    public string Dpm { get; set; } = "";
    public string Depotno { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Spname { get; set; } = "";
    public string Papertype { get; set; } = "";
}

/// <summary>出库单明细行（明细窗，WHERE paper+type）。</summary>
public sealed class OutDocLine
{
    public int Id { get; set; }
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public decimal Ckamount { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprct { get; set; }
    public decimal Yhloprct { get; set; }
    /// <summary>实收总额 = oprct*ckamount（旧 sum(oprct*ckamount) 行级推广）。</summary>
    public decimal OprctTotal { get; set; }
    /// <summary>折后总额 = oprct*ckamount*(100-yhl)/100（旧 fr_clientsh 缘.row27 行级公式）。</summary>
    public decimal YhloprctTotal { get; set; }
    public string Ty { get; set; } = "";       // 车型（tbsada.ty）
    public string Posi { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Depotno { get; set; } = "";
    public string Remark { get; set; } = "";
}

/// <summary>
/// 出库单据级行（按 paper+type+dpm 聚合，一行 = 一张单；qp11 查询模式单据列表同构）。
/// </summary>
public sealed class OutDocBillRow
{
    public string Paper { get; set; } = "";
    public string Type { get; set; } = "";
    public string Dpm { get; set; } = "";
    public DateTime Outdate { get; set; }
    public string Gname { get; set; } = "";
    public string Per { get; set; } = "";
    /// <summary>明细行数。</summary>
    public int LineCount { get; set; }
    /// <summary>Σ数量。</summary>
    public decimal TotalQty { get; set; }
    /// <summary>Σ折后金额（旧 yhloprct 公式聚合）。</summary>
    public decimal TotalAmount { get; set; }
    /// <summary>Σ实收总额 = Σ(oprct*ckamount)（旧 sum(oprct*ckamount) 语义）。</summary>
    public decimal TotalOprct { get; set; }
    /// <summary>单价 = 单内 oprct（MAX 聚合；旧 fr_sk C_TMP1.OPRCT 单据级口径，组内一致）。</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>付款方式（组内 MAX，单内一致）。</summary>
    public string Guno { get; set; } = "";
    /// <summary>'Y' = 已收款确认（组内 MAX：任一行已收即视为已收）。</summary>
    public string Mark { get; set; } = "";
    /// <summary>
    /// 单据状态（旧 fr_sk __.row3 L74 派生列 SK：mark='Y'已确认 / 'T'已提货 / 'R'已入库；
    /// mark='' 且 guno='' → 开单中；其余空。出纳确认前按此拦截）。
    /// </summary>
    public string Sk { get; set; } = "";
    public DateTime Qrdate { get; set; }
    public string Remark { get; set; } = "";
    /// <summary>单据类型（tbsada_header.papertype，本库值域：门店/仓库；查询模式「门店/仓库」列）。</summary>
    public string Papertype { get; set; } = "";
}
