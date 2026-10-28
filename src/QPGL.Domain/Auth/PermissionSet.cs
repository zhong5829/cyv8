namespace QPGL.Domain.Auth;

/// <summary>
/// 权限集（开发文档 §9 / M3 权限完整）。
/// 旧实现出处（fr_login row12 / fr_setpower row16 / fr_main row2 pwcheck）：
///   - 登录后权限 = tbrole 中 dpmbh+role 对应行的 usem/usem1（tbkey.usem 备用数据全空，不参与）
///   - usem 格式：前缀标记 + 逗号分隔的 tbmymenu.Number（如 ",_,A0,A1,C1,C5,E2,"）
///     '@' = 超级（跳过全部权限检查）；'$' = 数据范围管理员（全部门）
///   - 菜单过滤：",C1," 是否为 usem 子串（fr_main：REPLACE SKIPFOR WITH ... "('," + KEYID + ",' $ p_usem"）
///   - usem1 操作级标志（fr_setpower 保存序）：'/'允许修改 'C'拆分 'D'行员工价 'S' 'E'挂外价
///     '+'调价 'A/B/I'应收应付选项 '*'抹零 '-'成本价可见 'F'积分 'G'挂账 'H'删除工单 + 尾数字客户类型
/// </summary>
public sealed class PermissionSet
{
    private readonly string _usem;
    private readonly string _usem1;
    private readonly HashSet<string> _menus;

    private PermissionSet(string usem, string usem1)
    {
        _usem = usem;
        _usem1 = usem1 ?? "";
        _menus = usem
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static PermissionSet Parse(string? usem, string? usem1 = null) =>
        new((usem ?? "").Trim(), (usem1 ?? "").Trim());

    /// <summary>空权限集（角色不存在或未配置）。</summary>
    public static PermissionSet None { get; } = Parse("", "");

    /// <summary>超级标记 '@'：跳过全部权限检查（旧 fr_main / fr_login 语义）。</summary>
    public bool IsSuper => _usem.Contains('@');

    /// <summary>管理员标记 '$'：数据范围全部门（dpmusem/dpmisto 全量）。</summary>
    public bool IsDataAdmin => _usem.Contains('$');

    /// <summary>
    /// 是否拥有菜单权限点（如 C1=销售管理、C5=退货管理、E2=出库单查询、G2=收款、A5=客户资料管理）。
    /// 旧语义：",code," 为 usem 子串；'@' 直接过。
    /// </summary>
    public bool HasMenu(string code)
    {
        if (IsSuper) return true;
        if (string.IsNullOrWhiteSpace(code)) return false;
        return _menus.Contains(code);
    }

    /// <summary>是否拥有操作级标志位（usem1，如 '/'允许修改、'*'抹零、'-'成本价可见、'G'挂账）。</summary>
    public bool HasFlag(char flag) => IsSuper || _usem1.Contains(flag);

    // ── 价格列掩码（旧 cmm_class ___.row331 L873-880 范式：'@' $ P_USEM 直过）──
    /// <summary>成本价（IPRC）列可见：'@' $ P_USEM 或 '-' $ P_USEM1。</summary>
    public bool CanSeeCost => HasFlag('-');
    /// <summary>毛利（ML）列可见：'@' $ P_USEM 或 '*' $ P_USEM1（旧 fr_jpglsh 范式）。</summary>
    public bool CanSeeProfit => HasFlag('*');
    /// <summary>外币价（IPRJ）列可见：'@' $ P_USEM 或 '~' $ P_USEM1（旧 cmm_class ___.row331 L877）。</summary>
    public bool CanSeeIprj => HasFlag('~');

    /// <summary>usem1 尾数字客户类型选项（旧 OPTCLKH，0/1/2）。</summary>
    public int ClientTypeOption
    {
        get
        {
            var tail = _usem1.TrimEnd();
            return tail.Length > 0 && char.IsDigit(tail[^1]) ? tail[^1] - '0' : 0;
        }
    }

    /// <summary>原始权限串（诊断显示用）。</summary>
    public string Raw => _usem;
}
