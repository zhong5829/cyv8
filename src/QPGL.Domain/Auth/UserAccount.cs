namespace QPGL.Domain.Auth;

/// <summary>
/// 操作员账号（旧系统身份源：tbkey 表，结构零改动）。
/// 字段语义来自逆向源码 fr_pwd（密码不区分大小写比较）与 pg_proc（tbkey role→tbrole.usem 权限串）。
/// </summary>
public sealed class UserAccount
{
    public int Id { get; init; }

    /// <summary>部门编号（tbkey.dpmbh）。</summary>
    public string DeptNo { get; init; } = "";

    /// <summary>角色名（tbkey.role）。</summary>
    public string Role { get; init; } = "";

    /// <summary>操作员（tbkey.per）。</summary>
    public string Operator { get; init; } = "";

    /// <summary>权限串（tbkey.usem，逗号分隔的旧菜单权限点，如 A.1,C.1）。</summary>
    public string Permissions { get; init; } = "";

    /// <summary>部门权限（tbkey.dpmusem）。</summary>
    public string DeptPermissions { get; init; } = "";

    /// <summary>可操作部门（tbkey.dpmisto）。</summary>
    public string DeptStocks { get; init; } = "";
}
