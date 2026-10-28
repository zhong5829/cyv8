namespace QPGL.Api.Auth;

/// <summary>
/// 登录请求。per=操作员，password=密码（旧系统明文比较语义）。
/// </summary>
public sealed record LoginRequest(string Per, string Password);

/// <summary>登录响应：JWT + 用户信息（含权限点，供前端菜单控制）。</summary>
public sealed record LoginResponse(
    string Token,
    DateTime ExpiresAt,
    string Operator,
    string Role,
    string DeptNo,
    string[] Permissions);
