using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Api.Auth;
using QPGL.Domain.Auth;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Auth;

namespace QPGL.Api.Controllers;

/// <summary>认证（§7.1：POST /api/auth/login）。身份源 = tbkey。</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(IUserRepository users, IJwtTokenService jwt) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var per = (request.Per ?? "").Trim();
        if (per.Length == 0)
            throw new AppException("AUTH_FAILED", "请输入操作员", 401);

        // M1 兼容模式：按旧系统语义明文比较（fr_pwd：LOWER(ALLTRIM)）。
        // BCrypt 散列迁移（§9）在 M3 权限完善时落地。
        var row = await users.GetByOperatorAsync(per, ct);
        if (row is null ||
            !string.Equals(row.password.Trim(), (request.Password ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            throw new AppException("AUTH_FAILED", "操作员或密码错误", 401);

        var account = new UserAccount
        {
            Id = row.id,
            Operator = row.per.Trim(),
            Role = row.role.Trim(),
            DeptNo = row.dpmbh.Trim(),
            Permissions = row.usem.Trim(),
            DeptPermissions = row.dpmusem.Trim(),
            DeptStocks = row.dpmisto.Trim(),
        };

        var (token, expiresAt) = jwt.Issue(account);
        var response = new LoginResponse(
            Token: token,
            ExpiresAt: expiresAt,
            Operator: account.Operator,
            Role: account.Role,
            DeptNo: account.DeptNo,
            Permissions: SplitPermissions(account.Permissions));

        return Ok(ApiResponse<LoginResponse>.Success(response));
    }

    [HttpGet("operators")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<string>>>> Operators(CancellationToken ct)
        => Ok(ApiResponse<IReadOnlyList<string>>.Success(await users.GetOperatorsAsync(ct)));

    [HttpGet("me")]
    [Authorize]
    public ActionResult<ApiResponse<UserAccount>> Me()
    {
        var svc = HttpContext.RequestServices.GetRequiredService<IJwtTokenService>();
        var account = svc.ParsePrincipalClaims(User);
        if (account is null)
            throw new AppException("AUTH_FAILED", "令牌无效", 401);
        return Ok(ApiResponse<UserAccount>.Success(account));
    }

    private static string[] SplitPermissions(string usem) =>
        usem.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
