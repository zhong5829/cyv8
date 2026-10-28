namespace QPGL.Domain.Report;

/// <summary>日报表销售行（D3 页1/页3/页4，fr_dyrbb row3 B1 列集）。</summary>
public sealed class DailyReportSaleRow
{
    public string Paper { get; set; } = "";
    public string Gname { get; set; } = "";
    public decimal Ckamount { get; set; }
    public decimal Iprc { get; set; }
    public decimal Oprc { get; set; }
    public decimal Oprct { get; set; }
    public string Yhl { get; set; } = "";
    public string Guno { get; set; } = "";
    public string Per { get; set; } = "";
    public DateTime Outdate { get; set; }
}

/// <summary>日报表入库行（D3 页2，B2 列集）。</summary>
public sealed class DailyReportInRow
{
    public string Cno { get; set; } = "";
    public string Gyname { get; set; } = "";
    public int Item { get; set; }
    public decimal Jkamount { get; set; }
    public decimal Iprj { get; set; }
    public decimal Iprc { get; set; }
    public string Depotno { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Per { get; set; } = "";
    public DateTime Indate { get; set; }
    public string Isord { get; set; } = "";
}

/// <summary>月销售数统计行（D4，tbrtsamo 行：nno × 12 月销量矩阵）。</summary>
public sealed class MonthlySaleRow
{
    public int Id { get; set; }
    public string Dpm { get; set; } = "";
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Oamou1 { get; set; }
    public decimal Oamou2 { get; set; }
    public decimal Oamou3 { get; set; }
    public decimal Oamou4 { get; set; }
    public decimal Oamou5 { get; set; }
    public decimal Oamou6 { get; set; }
    public decimal Oamou7 { get; set; }
    public decimal Oamou8 { get; set; }
    public decimal Oamou9 { get; set; }
    public decimal Oamou10 { get; set; }
    public decimal Oamou11 { get; set; }
    public decimal Oamou12 { get; set; }
}

/// <summary>客户分析月度行（D7 新增 / D8 流失，fr_tjfxb 盰.row26 / 竑.row27）。</summary>
public sealed class CustomerAnalysisMonthRow
{
    /// <summary>「yyyy年M月」；末行「总计」。</summary>
    public string Ym { get; set; } = "";
    /// <summary>新增客户数（D7）或流失客户数（D8）。</summary>
    public int Count { get; set; }
}

/// <summary>客户分析明细行。</summary>
public sealed class CustomerAnalysisDetailRow
{
    /// <summary>所属「yyyy年M月」。</summary>
    public string Months { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Lxr { get; set; } = "";
    public string Lxrtel { get; set; } = "";
    public string Ch { get; set; } = "";
    public string Lxradr { get; set; } = "";
    /// <summary>D7=首单日期；D8=最后交易日期。</summary>
    public DateTime Jcdate { get; set; }
    /// <summary>D7=客户累计消费 Σckamount*oprct*(100-yhl)/100（仅 D7 填写）。</summary>
    public decimal Oprc { get; set; }
}

public sealed class JxcDailyRow
{
    public string Date { get; set; } = "";
    public decimal Qckc { get; set; }        // 期初库存
    public decimal Qckcj { get; set; }       // 期初金额
    public decimal Jk { get; set; }          // 进库数
    public decimal Jkj { get; set; }         // 进库金额
    public decimal Xs { get; set; }          // 销售数
    public decimal Xsj { get; set; }         // 销售金额
    public decimal Db { get; set; }          // 调拨数
    public decimal Dbj { get; set; }         // 调拨金额
    public decimal Wx { get; set; }          // 领料数（维修 tbxlsa）
    public decimal Wxj { get; set; }         // 领料金额
    public decimal Xg { get; set; }          // 纠正数（tbistoch）
    public decimal Xgj { get; set; }         // 纠正金额
    public decimal Gk { get; set; }          // 归库数（tbsaxg）
    public decimal Gkj { get; set; }         // 归库金额
    public decimal Cj { get; set; }          // 差价数
    public decimal Cjj { get; set; }         // 差价金额
    public decimal Qmkc { get; set; }        // 期末库存
    public decimal Qmkcj { get; set; }       // 期末金额
    public decimal Ck { get; set; }          // 出库合计数
    public decimal Ckj { get; set; }         // 出库金额
    public decimal Ckml { get; set; }        // 出库毛利
    public decimal Ckoprc { get; set; }      // 出库售价
}

public sealed class JxcCategoryRow
{
    public string Cp { get; set; } = "";
    /// <summary>销售数量 Σckamount（含负数退货行）。</summary>
    public int XsAmou { get; set; }
    /// <summary>销售收入 Σckamount*oprct*(100-yhl)/100。</summary>
    public decimal XsJine { get; set; }
    /// <summary>毛利 = XsJine - 成本（行级）。</summary>
    public decimal Ml { get; set; }
    /// <summary>毛利率 = Ml/IPRC 成本 * 100%。</summary>
    public string Mll { get; set; } = "";
    /// <summary>退货数量 Σ(-ckamount)（ckamount<0 行）。</summary>
    public int ThAmou { get; set; }
    public decimal ThJine { get; set; }
    /// <summary>入库数量 Σjkamount。</summary>
    public int InAmou { get; set; }
    public decimal InJine { get; set; }
    /// <summary>期末库存 Σkcamount。</summary>
    public int KcAmou { get; set; }
    public decimal KcJine { get; set; }
    /// <summary>销售成本 Σckamount*iprc（毛利率分母）。</summary>
    public decimal Iprc { get; set; }
}
