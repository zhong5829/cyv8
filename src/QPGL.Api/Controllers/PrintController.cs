using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Api.Printing;
using QPGL.Domain.Common;
using QPGL.Domain.Printing;
using QPGL.Infrastructure.Printing;

namespace QPGL.Api.Controllers;

/// <summary>
/// 打印服务（§8.3.3）：票样取数 / 票头尾设置（tbsysset RPSET 段，与旧 VFP 客户端共享）。
/// 打印动作由桌面端静默打印桥完成（Web 端不生成浏览器票面）。
/// </summary>
[ApiController]
[Route("api/print")]
[Authorize]
public sealed class PrintController(
    IPrintRepository printRepo,
    IPrintSettingsRepository settingsRepo,
    IDesktopPrintBridge printBridge) : ControllerBase
{
    /// <summary>销售单票样取数：GET /api/print/data/sale/{paper}</summary>
    [HttpGet("data/sale/{paper}")]
    public async Task<ActionResult<ApiResponse<SalePrintDoc>>> SaleData(string paper, CancellationToken ct)
        => Ok(ApiResponse<SalePrintDoc>.Success(await printRepo.GetSalePrintDataAsync(paper, "销售", ct)));

    /// <summary>
    /// 桌面端静默打印：POST /api/print/{paper}/desktop?type=销售
    /// 取数/票面/打印机全部复用桌面端 PrintService（本进程内调用，默认打印机），与桌面端输出一致。
    /// </summary>
    [HttpPost("{paper}/desktop")]
    public async Task<ActionResult<ApiResponse<object>>> PrintOnDesktop(
        string paper, [FromQuery] string? type, CancellationToken ct)
    {
        var target = (paper ?? "").Trim();
        if (target.Length == 0)
            return BadRequest(ApiResponse<object>.Fail(new ApiError("PAPER_REQUIRED", "单号不能为空")));

        var docType = string.IsNullOrWhiteSpace(type) ? "销售" : type.Trim();
        var result = await printBridge.PrintSaleAsync(target, docType, ct);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.Fail(new ApiError("PRINT_FAILED", result.Error ?? "打印失败")));
        }
        return Ok(ApiResponse<object>.Success(new { paper = target, printer = result.PrinterName }));
    }

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
