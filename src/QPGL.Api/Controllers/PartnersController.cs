using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
using QPGL.Infrastructure.Partners;

namespace QPGL.Api.Controllers;

/// <summary>
/// 客户资料查询（M1 首个查询页）。
/// §6.7：下拉/搜索走服务端实时查询，禁止一次性 TOP N。
/// </summary>
[ApiController]
[Route("api/partners")]
[Authorize]
public sealed class PartnersController(IPartnerRepository repo) : ControllerBase
{
    /// <summary>分页列表：?page=1&amp;pageSize=50&amp;sort=partName&amp;order=asc&amp;keyword=...</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<Partner>>>> Search(
        [FromQuery] PagedRequest request, CancellationToken ct)
    {
        var result = await repo.SearchAsync(request, ct);
        return Ok(ApiResponse<PagedResult<Partner>>.Success(result));
    }
}
