namespace QPGL.Domain.Report;

/// <summary>日营业统计行（D1 网格，fr_rtj row3 Init 列集）。</summary>
public sealed class DailyStatRow
{
    public DateTime Outdate { get; set; }
    /// <summary>销售单数（COUNT(DISTINCT paper)）。</summary>
    public int PaperCount { get; set; }
    /// <summary>销售数量合计 Σckamount。</summary>
    public decimal Ckamount { get; set; }
    /// <summary>销售收入（外币口径 Σiprj*ckamount）。</summary>
    public decimal Iprj { get; set; }
    /// <summary>销售成本（Σiprc*ckamount）。</summary>
    public decimal Iprc { get; set; }
    /// <summary>销售额 Σoprc*ckamount*(100-yhl)/100。</summary>
    public decimal Oprc { get; set; }
    /// <summary>实收额 Σoprct*ckamount*(100-yhl)/100。</summary>
    public decimal Oprct { get; set; }
    /// <summary>毛利 = Oprct - (P_WBTOHS ? Iprj : Iprc)。</summary>
    public decimal Ml { get; set; }
    /// <summary>未统计/已统计（按当日是否存在未统计单据动态判定）。</summary>
    public string Mark { get; set; } = "未统计";
    /// <summary>入库单数 COUNT(DISTINCT seano+isord)。</summary>
    public int Rccno { get; set; }
    /// <summary>入库数量 Σjkamount。</summary>
    public decimal Rcamt { get; set; }
    public decimal Rciprj { get; set; }
    public decimal Rciprc { get; set; }
}

/// <summary>日营业统计确认结果（fr_rtj row5）。</summary>
public sealed class PostDailyStatResult
{
    /// <summary>处理的销售单组数（paper+guno+pno 分组）。</summary>
    public int SalesGroups { get; set; }
    /// <summary>写入 tbysyf 的销售应收行数（含 KH 实收差额行）。</summary>
    public int YsyfSaleRows { get; set; }
    /// <summary>处理的入库单组数。</summary>
    public int InStockGroups { get; set; }
    /// <summary>写入 tbysyf 的入库应付行数。</summary>
    public int YsyfInRows { get; set; }
}
