using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Db;

namespace QPGL.Api.Controllers;

/// <summary>健康检查：客户端状态栏「●已连接」依据（§8.2）。</summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController(DatabaseHealthChecker health) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> Get(CancellationToken ct)
    {
        var result = await health.CheckAsync(ct);
        if (!result.Success)
            throw new AppException("DB_UNAVAILABLE", "数据库不可用", 503, result.Error);

        return Ok(ApiResponse<object>.Success(new { db = "up", elapsedMs = result.ElapsedMs }));
    }
}
