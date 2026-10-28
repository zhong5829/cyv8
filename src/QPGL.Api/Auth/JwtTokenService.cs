using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using QPGL.Domain.Auth;

namespace QPGL.Api.Auth;

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAt) Issue(UserAccount user);
    UserAccount? ParsePrincipalClaims(ClaimsPrincipal principal);
}

/// <summary>
/// JWT 签发（§9：8h 有效期）。Claims 携带操作员/角色/部门/权限串，
/// 权限点编码对齐旧菜单编号（A.1、C.1…）。
/// </summary>
public sealed class JwtTokenService(IConfiguration config) : IJwtTokenService
{
    public (string Token, DateTime ExpiresAt) Issue(UserAccount user)
    {
        var section = config.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(section["Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddHours(double.Parse(section["ExpireHours"] ?? "8"));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Operator),
            new("role", user.Role),
            new("dept", user.DeptNo),
            new("usem", user.Permissions),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var token = new JwtSecurityToken(
            issuer: section["Issuer"],
            audience: section["Audience"],
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public UserAccount? ParsePrincipalClaims(ClaimsPrincipal principal)
    {
        var op = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                 ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value; // 兼容映射过的令牌
        if (string.IsNullOrEmpty(op)) return null;
        return new UserAccount
        {
            Operator = op,
            Role = principal.FindFirst("role")?.Value ?? "",
            DeptNo = principal.FindFirst("dept")?.Value ?? "",
            Permissions = principal.FindFirst("usem")?.Value ?? "",
        };
    }
}
