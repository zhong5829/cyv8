namespace QPGL.Domain.Stock;

/// <summary>生成盘点单条件（fr_istopd row114：tbisto 行筛选，全表快照式拷贝）。</summary>
public sealed class GenerateStocktakeCommand
{
    /// <summary>配件编号前缀（空=不限）。</summary>
    public string? NnoPrefix { get; set; }
    /// <summary>名称包含（空=不限）。</summary>
    public string? Na1Contains { get; set; }
    /// <summary>仓库（空=不限）。</summary>
    public string? Depotno { get; set; }
    /// <summary>厂牌（旧系统恒按当前厂牌 P_CP 过滤；新系统厂牌切换未实现，空=全部厂牌）。</summary>
    public string? Cp { get; set; }
}

/// <summary>盘点行保存命令（fr_istopd row95：实盘数 + 实盘仓位，nyk=npd-kcamount 由仓储计算）。</summary>
public sealed class SaveStocktakeLineCommand
{
    public int Id { get; set; }
    public decimal Npd { get; set; }        // 实盘数
    public string Pdposi { get; set; } = ""; // 实盘仓位
}

/// <summary>盘点行（tbistopd，明细展示）。</summary>
public sealed class StocktakeLine
{
    public int Id { get; init; }
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Fa { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Depotno { get; init; } = "";
    public string Posi { get; init; } = "";
    public string Pdposi { get; init; } = "";
    public decimal Kcamount { get; init; }   // 账面数（生成时快照）
    public decimal Npd { get; init; }        // 实盘数
    public decimal Nyk { get; init; }        // 盈亏 = npd - kcamount
    public decimal Iprc { get; init; }
    public decimal Iprj { get; init; }
    public string Note { get; init; } = "";
    public int Kcid { get; init; }           // tbisto.id
    public int Mark { get; init; }           // 0 未确认 / 1 已确认 / -1 重复标记
    public string Pdpaper { get; init; } = "";
    public string Per { get; init; } = "";
    public DateTime PdDate { get; init; }
}

/// <summary>盘点单据头（tbistopd GROUP BY pdpaper）。</summary>
public sealed class StocktakePaper
{
    public string Pdpaper { get; init; } = "";
    public string Per { get; init; } = "";
    public DateTime PdDate { get; init; }
    public int LineCount { get; init; }
    public int Mark { get; init; }
}

/// <summary>确认盘点单结果（fr_istopd row49）。</summary>
public sealed class ConfirmStocktakeResult
{
    public int MarkedLines { get; init; }    // nyk<>0 → zt='1',mark='1'
    public int DeletedLines { get; init; }   // nyk=0 → DELETE
}

/// <summary>生成散件入库草稿结果（fr_istopd row92）。</summary>
public sealed class GenerateInStockDraftResult
{
    public string Cno { get; init; } = "";
    public int InsertedLines { get; init; }  // 新建草稿行数
    public int UpdatedLines { get; init; }   // 已存在草稿时更新行数
}
