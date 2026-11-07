using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Api.Auth;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Infrastructure.Parts;

namespace QPGL.Api.Controllers;

/// <summary>配件库存查询（tbisto = 主档+批次，开单选行即得 kcid）。</summary>
[ApiController]
[Route("api/parts")]
[Authorize]
public sealed class PartsController(IPartRepository repo, IJwtTokenService jwt) : ControllerBase
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
        var result = await repo.SearchAsync(dpm.Trim(), cp, na1, ty, request, ct: ct);
        return Ok(ApiResponse<PagedResult<PartItem>>.Success(result));
    }

    /// <summary>销售历史（开单编辑窗定价参考）：GET /api/parts/{nno}/sale-history?keyword=张&amp;top=0。
    /// keyword 按客户名称/拼音码筛选（tbgu.code）；top&lt;=0 全部；退货行数量为负。</summary>
    [HttpGet("{nno}/sale-history")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PartSaleHistory>>>> SaleHistory(
        string nno, [FromQuery] string? keyword, [FromQuery] int top, CancellationToken ct)
    {
        var account = jwt.ParsePrincipalClaims(User)
            ?? throw new AppException("AUTH_FAILED", "令牌无效", 401);
        return Ok(ApiResponse<IReadOnlyList<PartSaleHistory>>.Success(
            await repo.GetSaleHistoryAsync(nno.Trim(), account.DeptNo, top, keyword, ct)));
    }

    /// <summary>进货历史（历史进价参考）：GET /api/parts/{nno}/purchase-history?keyword=&amp;top=0。
    /// keyword 按供应商名称/拼音码筛选；top&lt;=0 全部。</summary>
    [HttpGet("{nno}/purchase-history")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PartPurchaseHistory>>>> PurchaseHistory(
        string nno, [FromQuery] string? keyword, [FromQuery] int top, CancellationToken ct)
    {
        var account = jwt.ParsePrincipalClaims(User)
            ?? throw new AppException("AUTH_FAILED", "令牌无效", 401);
        return Ok(ApiResponse<IReadOnlyList<PartPurchaseHistory>>.Success(
            await repo.GetPurchaseHistoryAsync(nno.Trim(), account.DeptNo, top, keyword, ct)));
    }

    /// <summary>入库单整单明细（进货历史双击查看）：GET /api/parts/purchase-doc/{cno}/lines。</summary>
    [HttpGet("purchase-doc/{cno}/lines")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PartPurchaseDocLine>>>> PurchaseDocLines(
        string cno, CancellationToken ct)
        => Ok(ApiResponse<IReadOnlyList<PartPurchaseDocLine>>.Success(await repo.GetPurchaseDocLinesAsync(cno.Trim(), ct)));
}
