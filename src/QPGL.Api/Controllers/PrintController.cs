using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Domain.Common;
using QPGL.Domain.Printing;
using QPGL.Infrastructure.Printing;

namespace QPGL.Api.Controllers;

/// <summary>
/// 打印服务（§8.3.3）：票样取数 / 票头尾设置（tbsysset RPSET 段，与旧 VFP 客户端共享）。
/// 打印动作在客户端渲染（WPF FixedDocument），API 提供数据与设置。
/// </summary>
[ApiController]
[Route("api/print")]
[Authorize]
public sealed class PrintController(IPrintRepository printRepo, IPrintSettingsRepository settingsRepo) : ControllerBase
{
    /// <summary>销售单票样取数：GET /api/print/data/sale/{paper}</summary>
    [HttpGet("data/sale/{paper}")]
    public async Task<ActionResult<ApiResponse<SalePrintDoc>>> SaleData(string paper, CancellationToken ct)
        => Ok(ApiResponse<SalePrintDoc>.Success(await printRepo.GetSalePrintDataAsync(paper, "销售", ct)));

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<PrintSettings>>> Settings(CancellationToken ct)
        => Ok(ApiResponse<PrintSettings>.Success(await settingsRepo.GetAsync(ct)));

    /// <summary>保存票尾（RPSET1/RPSET2，写回 tbsysset，旧系统共享）</summary>
    [HttpPut("settings/footer")]
    public async Task<ActionResult<ApiResponse<object>>> SaveFooter(
        [FromBody] SaveFooterRequest request, CancellationToken ct)
    {
        await settingsRepo.SaveFooterAsync(request.Part1, request.Part2, ct);
        return Ok(ApiResponse<object>.Success(new { saved = true }));
    }
}

public sealed record SaveFooterRequest(string Part1, string Part2);
