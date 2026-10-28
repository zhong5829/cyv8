namespace QPGL.Domain.Partners;

/// <summary>
/// 客户资料（tbgu 表，M1 列表/搜索所需窄列集，禁 SELECT *）。
/// 命名对齐旧系统：gno 客户编号、gname 客户名称、yhl 优惠率。
/// </summary>
public sealed class Partner
{
    public int Id { get; init; }
    public string PartNo { get; init; } = "";      // gno 客户编号
    public string PartName { get; init; } = "";    // gname 客户名称
    public string LinkMan { get; init; } = "";     // 联系人
    public string Tel { get; init; } = "";
    public string Mobile { get; init; } = "";
    public string Address { get; init; } = "";     // adr
    public string Type1 { get; init; } = "";       // 客户类别
    public string Type2 { get; init; } = "";
    public decimal DiscountRate { get; init; }     // yhl 优惠率
    public string SalesMan { get; init; } = "";    // ywy 业务员
    public string Note { get; init; } = "";
}
