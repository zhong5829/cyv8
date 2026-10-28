using QPGL.Domain.Auth;
using QPGL.Domain.Common;

namespace QPGL.Domain.Stock;

/// <summary>
/// 库存修改命令（B3，M4-3）。
/// 旧实现出处：fr_istoch 禰.row71 —— 保存前重查 tbisto.kcamount 比对（并发守卫）；
/// UPDATE tbisto（数量变化含 KCAMOUNT、否则不含）WHERE id；
/// na1/ty/fa/dc/unit 变更联动 UPDATE tbprnoty；
/// 数量/进价/售价任一变化 → INSERT tbistoch 修改流水（XGAMOUNT=新-旧，IPRC=新kc*新iprc-旧kc*旧iprc，IPRJ 同）；
/// 删除：fr_istoch row31 —— kcamount=0 才可 DELETE tbisto WHERE id。
/// </summary>
public sealed class UpdateStockCommand
{
    public required int Id { get; set; }

    /// <summary>并发基线：查询时的 kcamount（旧 OLDVAL 语义，不匹配即拒绝）。</summary>
    public required decimal OrigKcamount { get; set; }

    // ---- 以下字段为 null 表示不修改 ----

    public string? Nno { get; set; }
    public string? Na1 { get; set; }
    public string? Ty { get; set; }
    public string? Fa { get; set; }
    public string? Dc { get; set; }
    public string? Unit { get; set; }
    public decimal? Kcamount { get; set; }
    public decimal? Iprc { get; set; }
    public decimal? Iprj { get; set; }
    public decimal? Oprc { get; set; }
    public decimal? Oprb { get; set; }
    public string? Posi { get; set; }
    public string? Depotno { get; set; }
    public string? Note { get; set; }

    /// <summary>修改人（写 tbistoch.per）。</summary>
    public required string Per { get; set; }
}

/// <summary>库存修改结果。</summary>
public sealed class UpdateStockResult
{
    /// <summary>数量变化量（新-旧；0=未变）。</summary>
    public decimal Xgamount { get; set; }
    /// <summary>变更说明（旧 M_NOTE 语义）。</summary>
    public string ChangeNote { get; set; } = "";
    /// <summary>是否写 tbistoch 流水。</summary>
    public bool HasChangelog { get; set; }
    /// <summary>是否联动 tbprnoty。</summary>
    public bool PropertySynced { get; set; }
}

/// <summary>
/// 库存修改权限点（fr_istoch _9.row45 权限矩阵；M4 按一期范围裁剪）：
///   B301=数量/仓库、B302=进价(iprc/iprj)、B303=售价(oprc/oprb)、B304=禁删、'/'=改零件号、
///   A1=属性(na1/ty/fa/dc/note)、'-'=成本价可见、'@'=超级。
/// 仓库禁改 B305、货位校验 P_POSIGL、跨库数据权限 P_USEMDEPOTNO 属多门店定制，一期不启用。
/// </summary>
public static class StockEditPermissions
{
    public const string Quantity = "B301";
    public const string PurchasePrice = "B302";
    public const string SalePrice = "B303";
    public const string ForbidDelete = "B304";
    public const string Property = "A1";
    public const string ChangeNnoFlag = "/";

    /// <summary>校验命令中字段的修改权限（('@' 超级全放）。</summary>
    public static void Validate(Auth.PermissionSet permission, UpdateStockCommand cmd)
    {
        static bool Ch(PermissionSet p, string code) => p.HasMenu(code);

        if (cmd.Nno is not null && !Ch(permission, ChangeNnoFlag) && !permission.IsSuper)
            throw new AppException("NO_PERMISSION", "无修改零件号权限（'/' 标志）", 403);
        if ((cmd.Na1 is not null || cmd.Ty is not null || cmd.Fa is not null || cmd.Dc is not null || cmd.Unit is not null) && !Ch(permission, Property))
            throw new AppException("NO_PERMISSION", "无属性修改权限（A1）", 403);
        if ((cmd.Iprc is not null || cmd.Iprj is not null) && !Ch(permission, PurchasePrice))
            throw new AppException("NO_PERMISSION", "无进价修改权限（B302）", 403);
        if ((cmd.Oprc is not null || cmd.Oprb is not null) && !Ch(permission, SalePrice))
            throw new AppException("NO_PERMISSION", "无售价修改权限（B303）", 403);
        if (cmd.Kcamount is not null && !Ch(permission, Quantity))
            throw new AppException("NO_PERMISSION", "无库存数量修改权限（B301）", 403);
        if (cmd.Na1 is not null && string.IsNullOrWhiteSpace(cmd.Na1))
            throw new AppException("NA1_REQUIRED", "名称不能改为空", 422);
        if (cmd.Kcamount is not null && cmd.Kcamount.Value == 0)
            throw new AppException("KC_ZERO_VIA_EDIT", "库存数量改为 0 请使用删除功能（需 kcamount=0 语义一致）", 422);
    }
}
