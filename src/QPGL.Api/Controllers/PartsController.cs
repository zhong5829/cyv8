using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Infrastructure.Parts;

namespace QPGL.Api.Controllers;

/// <summary>配件库存查询（tbisto = 主档+批次，开单选行即得 kcid）。</summary>
[ApiController]
[Route("api/parts")]
[Authorize]
public sealed class PartsController(IPartRepository repo) : ControllerBase
{
    /// <summary>
    /// 三条件搜索（实时防抖由客户端处理）：?dpm=A&amp;cp=中&amp;na1=减震&amp;ty=N312
    /// cp=编码（含内部编号前缀）、na1=名称前缀、ty=车型/图号包含。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<PartItem>>>> Search(
        [FromQuery] string dpm, [FromQuery] string? cp, [FromQuery] string? na1,
        [FromQuery] string? ty, [FromQuery] PagedRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dpm))
            throw new AppException("PARAM_MISSING", "缺少部门参数 dpm", 422);
        var result = await repo.SearchAsync(dpm.Trim(), cp, na1, ty, request, ct);
        return Ok(ApiResponse<PagedResult<PartItem>>.Success(result));
    }
}
