using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Api.Auth;
using QPGL.Domain.Common;
using QPGL.Domain.Sales;
using QPGL.Infrastructure.Finance;
using QPGL.Infrastructure.Partners;
using QPGL.Infrastructure.Receipt;
using QPGL.Infrastructure.Sales;
using QPGL.Infrastructure.Stock;

namespace QPGL.Api.Controllers;

/// <summary>
/// 销售开单（§7.3 命令示例）。
/// 服务端事务内：取号(getnewdh) → 领域校验 → 写单头/明细 → 触发器冲减库存 → 回读断言 → 提交。
/// 确认（row298 语义）：写付款方式/客户/优惠等单头字段，单据状态由「开单中」转出。
/// </summary>
[ApiController]
[Route("api/sales")]
[Authorize]
public sealed class SalesController(
    ISaleRepository repo, IJwtTokenService jwt,
    IReceiptRepository receipts, IStockRepository stock,
    IPartnerRepository partners, IXlskRepository xlsk) : ControllerBase
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

    /// <summary>整单确认字典（付款方式/发货方式/销售类型，桌面端 ConfirmSaleDialog 同源 tbsysset）。</summary>
    [HttpGet("confirm-options")]
    public async Task<ActionResult<ApiResponse<object>>> ConfirmOptions(CancellationToken ct)
    {
        var methods = await receipts.GetPaymentMethodsAsync(ct);
        var fhfs = await stock.GetSyssetNamesAsync("fhfs", ct);
        var paperTypes = await stock.GetPaperTypesAsync(ct);
        return Ok(ApiResponse<object>.Success(new
        {
            paymentMethods = methods.Select(m => m.Name).ToList(),
            fhfs,
            paperTypes,
        }));
    }

    /// <summary>客户信用参考（限额/预警限额/余额/发票号，整单确认弹窗显示；gname 空=散客）。</summary>
    [HttpGet("customer-credit")]
    public async Task<ActionResult<ApiResponse<object>>> CustomerCredit([FromQuery] string? gname, CancellationToken ct)
    {
        var name = (gname ?? "").Trim();
        if (name.Length == 0)
            return Ok(ApiResponse<object>.Success(new { xede = 0m, yjxe = 0m, yue = 0m, pno = "" }));

        var (xede, yjxe) = await partners.GetLimitAsync(name, ct);
        var yue = await xlsk.GetCustomerYueAsync(name, ct);
        var pno = await partners.GetCustomerPnoAsync(name, ct);
        return Ok(ApiResponse<object>.Success(new { xede, yjxe, yue, pno }));
    }

    /// <summary>整单确认（fr_sale row298 语义）：UPDATE 单头 gno/gname/pno/t_mode/yhl/guno/outdate/jzdate/remark +
    /// tbsada_header upsert。确认后单据不再是「开单中」；mark='Y' 仍由收款流程（fr_sk）写。</summary>
    [HttpPost("{paper}/confirm")]
    public async Task<ActionResult<ApiResponse<ConfirmSaleResult>>> Confirm(
        string paper, [FromBody] ConfirmSaleCommand command, CancellationToken ct)
    {
        var account = jwt.ParsePrincipalClaims(User)
            ?? throw new AppException("AUTH_FAILED", "令牌无效", 401);

        command.Dept = account.DeptNo;      // 部门以令牌为准
        command.Paper = paper.Trim();
        if ((command.Guno ?? "").Trim().Length == 0)
            throw new AppException("GUNO_REQUIRED", "请选择付款方式", 422);

        var result = await repo.ConfirmAsync(command, account.Operator, ct);
        return Ok(ApiResponse<ConfirmSaleResult>.Success(result));
    }
}
