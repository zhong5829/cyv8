using QPGL.Domain.Auth;
using QPGL.Domain.Common;

namespace QPGL.Desktop.Services;

/// <summary>
/// 当前登录会话（直连模式，开发文档 §9）：登录态存进程内存，不做本地密码缓存。
/// 权限（M3）：登录时按 tbkey.dpmbh+role 加载 tbrole.usem/usem1 解析为 PermissionSet
/// （旧 fr_login row12 语义：权限一律取角色表；'@'超级、'$'数据范围管理员）。
/// </summary>
public static class AppSession
{
    public static string Operator { get; private set; } = "";
    public static string Role { get; private set; } = "";
    public static string DeptNo { get; private set; } = "";
    public static string[] Permissions { get; private set; } = [];
    public static PermissionSet Permission { get; private set; } = PermissionSet.None;

    /// <summary>部门数据权限串（tbkey.dpmusem，旧 P_DPMUSEM；空 = 不过滤，旧系统单店部署语义）。</summary>
    public static string DeptPermissions { get; private set; } = "";

    // ── 厂牌会话（旧 P_CP 机制，fr_cp 表单 T_.row5 语义）──
    /// <summary>当前厂牌（tbsysset type='cp' name 列，旧 P_CP）。空 = 未选择（登录后必选一次）。</summary>
    public static string Brand { get; private set; } = "";
    /// <summary>货币单位名（tbsysset valuec，旧 P_WN，如"含税价"/"人民币"）。</summary>
    public static string BrandCurrency { get; private set; } = "";
    /// <summary>查询码（tbsysset cxnno，旧 P_SNNO）。</summary>
    public static string BrandCxnno { get; private set; } = "";
    /// <summary>厂牌白名单（角色 usemcp，恒含"急件"；空 = 无限制看全部）。</summary>
    public static string[] BrandWhitelist { get; private set; } = [];

    public static void SignIn(string operatorName, string role, string deptNo, string[] permissions,
        PermissionSet? permission = null, string deptPermissions = "", string[]? brandWhitelist = null)
    {
        Operator = operatorName;
        Role = role;
        DeptNo = deptNo;
        Permissions = permissions;
        Permission = permission ?? PermissionSet.None;
        DeptPermissions = deptPermissions;
        BrandWhitelist = brandWhitelist ?? [];
    }

    /// <summary>选择/切换厂牌（旧 fr_cp T_.row5：会话级变量，已开页面不刷新，下次取数生效）。</summary>
    public static void SetBrand(string name, string currency, string cxnno)
    {
        Brand = name;
        BrandCurrency = currency;
        BrandCxnno = cxnno;
    }

    public static void SignOut()
    {
        Operator = "";
        Role = "";
        DeptNo = "";
        Permissions = [];
        Permission = PermissionSet.None;
        DeptPermissions = "";
        Brand = "";
        BrandCurrency = "";
        BrandCxnno = "";
        BrandWhitelist = [];
    }

    /// <summary>菜单/操作权限拦截（§7.2 NO_PERMISSION）：无权限抛业务异常由 VM 转 UI 提示。</summary>
    public static void RequireMenu(string code, string featureName)
    {
        if (!Permission.HasMenu(code))
            throw new AppException("NO_PERMISSION", $"无「{featureName}」({code}) 权限，请联系管理员分配角色权限", 403);
    }
}
