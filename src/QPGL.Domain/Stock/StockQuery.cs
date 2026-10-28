using QPGL.Domain.Common;

namespace QPGL.Domain.Stock;

/// <summary>
/// 库存查询请求（E3，M4-2）。
/// 旧实现出处：fr_istoch row3 —— SELECT nno,na1,posi,oprb,kcamount,iprj,iprc,oprc,ty,depotno,dpm,id,cp,fa,dc,unit,kcbhdate,note,xno,tno,cxnno FROM tbisto；
/// 仓库下拉=tbsysset WHERE type='仓库'；汇总=SUM kcamount, iprj*kcamount, iprc*kcamount, oprc*kcamount。
/// </summary>
public sealed class StockQueryRequest : PagedRequest
{
    /// <summary>配件编码（前缀匹配）。</summary>
    public string? Cp { get; set; }

    /// <summary>零件号（包含匹配）。</summary>
    public string? Nno { get; set; }

    /// <summary>名称（包含匹配）。</summary>
    public string? Na1 { get; set; }

    /// <summary>车型（包含匹配）。</summary>
    public string? Ty { get; set; }

    /// <summary>货位（前缀匹配）。</summary>
    public string? Posi { get; set; }

    /// <summary>仓库（tbsysset type='仓库' 的 name；空=全部）。</summary>
    public string? Depotno { get; set; }

    /// <summary>仅显示有库存（kcamount<>0）。</summary>
    public bool OnlyInStock { get; set; }
}

/// <summary>库存查询排序白名单（§7.1 禁注入）。</summary>
public static class StockSortFields
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cp"] = "cp",
        ["nno"] = "nno",
        ["na1"] = "na1",
        ["ty"] = "ty",
        ["posi"] = "posi",
        ["depotno"] = "depotno",
        ["kcamount"] = "kcamount",
        ["iprc"] = "iprc",
        ["iprj"] = "iprj",
        ["oprc"] = "oprc",
        ["id"] = "id",
    };

    public static bool TryResolve(string? sort, out string column)
    {
        column = "";
        if (string.IsNullOrWhiteSpace(sort)) return false;
        return Map.TryGetValue(sort.Trim(), out column!);
    }
}

/// <summary>库存行（tbisto 全列对齐 fr_istoch 查询）。</summary>
public sealed class StockRow
{
    public int Id { get; set; }
    public string Dpm { get; set; } = "";
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Kcamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprb { get; set; }
    public string Posi { get; set; } = "";
    public DateTime Kcbhdate { get; set; }
    public string Note { get; set; } = "";
    public int Xno { get; set; }
    public int Tno { get; set; }
    public string Cxnno { get; set; } = "";
    public string Depotno { get; set; } = "";
}

/// <summary>库存汇总（旧 _-.row35：SUM KCAMOUNT, IPRJ*KC, IPRC*KC, OPRC*KC）。</summary>
public sealed class StockSummary
{
    public decimal TotalKcamount { get; set; }
    public decimal TotalIprjAmount { get; set; }
    public decimal TotalIprcAmount { get; set; }
    public decimal TotalOprcAmount { get; set; }
}
