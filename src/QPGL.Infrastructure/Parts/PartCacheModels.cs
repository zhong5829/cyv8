using QPGL.Domain.Common;

namespace QPGL.Infrastructure.Parts;

/// <summary>
/// 配件选择缓存快照行（tbisto 本部门窄列，2026-11-04 远程弱网缓存需求）。
/// 列语义与 PartRepository.SearchAsync 的 tbisto 侧一致；nno 保留原值（与 DB 行为一致）。
/// </summary>
public sealed class PartCacheStockRow
{
    public int id { get; set; }
    public string dpm { get; set; } = "";
    public string cp { get; set; } = "";
    public string nno { get; set; } = "";
    public string na1 { get; set; } = "";
    public string ty { get; set; } = "";
    public string fa { get; set; } = "";
    public string dc { get; set; } = "";
    public string unit { get; set; } = "";
    public decimal kcamount { get; set; }
    public decimal iprj { get; set; }
    public decimal iprc { get; set; }
    public decimal oprc { get; set; }
    public decimal oprb { get; set; }
    public string posi { get; set; } = "";
    public string depotno { get; set; } = "";
    public string note { get; set; } = "";
}

/// <summary>属性库快照行（tbprnoty 全厂牌窄列；id=自增水位用于增量同步，nno 已 RTRIM，与销售补集/订单检索 SQL 输出口径一致）。</summary>
public sealed class PartCacheAttrRow
{
    public int Id { get; set; }
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Nnp { get; set; } = "";
    public string Nnq { get; set; } = "";
    public string Note { get; set; } = "";
    public string Tno { get; set; } = "";
    public string Xno { get; set; } = "";
    public string Cxnno { get; set; } = "";
    public decimal Uqty { get; set; }
    public decimal Iprj { get; set; }
    public decimal OprcCp { get; set; }
    public decimal OprcMin { get; set; }
}

/// <summary>tbname0 拼音码（name→code，内存匹配用）。</summary>
public sealed class PartCacheNameRow
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
}

/// <summary>tbty0 车型拼音码（ty→code，内存匹配用）。</summary>
public sealed class PartCacheTyRow
{
    public string Ty { get; set; } = "";
    public string Code { get; set; } = "";
}

/// <summary>
/// 配件选择缓存快照（2026-11-04 远程弱网缓存需求）：
/// tbisto（本部门）+ tbprnoty（全厂牌）+ tbname0/tbty0 拼音映射，一次性 QueryMultiple 拉回内存。
/// 使用方：PartCacheService（QPGL.Desktop/Services）——库存列/聚合列仍实时查询，本快照只承担
/// 行集筛选与分页，避免弱网下每次搜索都全表扫描 + 传输。
/// </summary>
public sealed class PartCacheSnapshot
{
    public List<PartCacheStockRow> StockRows { get; set; } = [];
    public List<PartCacheAttrRow> AttrRows { get; set; } = [];
    public List<PartCacheNameRow> Name0 { get; set; } = [];
    public List<PartCacheTyRow> Ty0 { get; set; } = [];
}

/// <summary>
/// 快照路径单页实时刷新结果（2026-11-04 弱网缓存）：一次往返同时带回
/// ①本页批次 id 的最新库存 ②tbisto 当前最大 id（增量水位）③水位之后的新批次行
/// ④tbprnoty 当前最大 id + 水位后新增属性行——其他电脑新入库批次行/新增配件属性
/// 随本次搜索即时并入快照并进入结果（用户确认：都要实时可见）。
/// </summary>
public sealed record StockLiveResult(
    IReadOnlyDictionary<int, decimal> StockByIds,
    int MaxId,
    List<PartCacheStockRow> NewRows,
    int AttrMaxId,
    List<PartCacheAttrRow> NewAttrRows);


