using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Api.Auth;
using QPGL.Domain.Common;
using QPGL.Domain.Sales;
using QPGL.Infrastructure.Sales;

namespace QPGL.Api.Controllers;

/// <summary>
/// 销售开单（§7.3 命令示例）。
/// 服务端事务内：取号(getnewdh) → 领域校验 → 写单头/明细 → 触发器冲减库存 → 回读断言 → 提交。
/// </summary>
[ApiController]
[Route("api/sales")]
[Authorize]
public sealed class SalesController(ISaleRepository repo, IJwtTokenService jwt) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<CreateSaleResult>>> Create(
        [FromBody] CreateSaleCommand command, CancellationToken ct)
    {
        var account = jwt.ParsePrincipalClaims(User)
            ?? throw new AppException("AUTH_FAILED", "令牌无效", 401);

        // 部门以令牌为准（客户端不可指定他人部门）
        command.Dept = account.DeptNo;
        var result = await repo.CreateAsync(command, account.Operator, ct);

        return Created($"/api/sales/{result.Paper}",
            ApiResponse<CreateSaleResult>.Success(result));
    }
}
