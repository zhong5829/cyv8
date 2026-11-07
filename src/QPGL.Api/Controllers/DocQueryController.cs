using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Api.Auth;
using QPGL.Domain.Common;
using QPGL.Domain.Query;
using QPGL.Infrastructure.Query;

namespace QPGL.Api.Controllers;

/// <summary>
/// 出库单查询（Web 端：销售出库单列表/详情）。
/// 复用桌面端 DocQueryRepository 同一套 SQL（fr_sesa 语义），部门数据权限取 JWT（旧 P_DPMUSEM）。
/// </summary>
[ApiController]
[Route("api/docs")]
[Authorize]
public sealed class DocQueryController(IDocQueryRepository repo, IJwtTokenService jwt) : ControllerBase
{
    /// <summary>单据级分页列表：GET /api/docs?dateFrom=2026-11-01&dateTo=2026-11-30&paper=XS&gname=张&per=admin&page=1&pageSize=20（类型固定=销售）。</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<OutDocBillRow>>>> SearchBills(
        [FromQuery] DateTime? dateFrom, [FromQuery] DateTime? dateTo,
        [FromQuery] string? paper, [FromQuery] string? gname, [FromQuery] string? per,
        [FromQuery] PagedRequest request, CancellationToken ct)
    {
        var account = jwt.ParsePrincipalClaims(User)
            ?? throw new AppException("AUTH_FAILED", "令牌无效", 401);

        var req = new OutDocQueryRequest
        {
            Type = "销售",
            DateFrom = dateFrom,
            DateTo = dateTo,
            Paper = paper,
            Gname = gname,
            Per = per,
            DpmPermissions = account.DeptPermissions,   // 旧 P_DPMUSEM 部门数据权限
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            Order = request.Order,
        };
        return Ok(ApiResponse<PagedResult<OutDocBillRow>>.Success(await repo.SearchOutDocBillsAsync(req, ct)));
    }

    /// <summary>单据详情（票头汇总 + 明细行）：GET /api/docs/{paper}?dpm=A&amp;type=销售。dpm 取列表行部门（与桌面端同语义）；
    /// type 默认「销售」，销售历史双击查看整单明细时传历史行自带 type（如「历史」）。</summary>
    [HttpGet("{paper}")]
    public async Task<ActionResult<ApiResponse<OutDocDetail>>> Detail(
        string paper, [FromQuery] string dpm, [FromQuery] string? type, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dpm))
            throw new AppException("PARAM_MISSING", "缺少部门参数 dpm", 422);
        var docType = (type ?? "销售").Trim();
        if (docType.Length == 0) docType = "销售";

        var summary = await repo.GetOutDocSummaryAsync(paper.Trim(), docType, dpm.Trim(), ct);
        var lines = await repo.GetOutDocLinesAsync(paper.Trim(), docType, dpm.Trim(), ct);
        return Ok(ApiResponse<OutDocDetail>.Success(new OutDocDetail(summary, lines)));
    }
}

/// <summary>单据详情（票头汇总 + 明细行）。</summary>
public sealed record OutDocDetail(OutDocSummary? Summary, IReadOnlyList<OutDocLine> Lines);
