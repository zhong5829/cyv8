using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QPGL.Api.Auth;
using QPGL.Domain.Common;
using QPGL.Domain.Sales;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.Sales;

namespace QPGL.Api.Controllers;

/// <summary>原单行（退货页加载，§8.2 退货流程）。</summary>
public sealed class SaleSourceLine
{
    public int RowId { get; init; }
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Posi { get; init; } = "";
    public decimal Qty { get; init; }        // 原出库数
    public decimal Returned { get; init; }   // 已退数 ro
    public decimal Remain => Qty - Returned; // 未退量
    public decimal Oprc { get; init; }
}

public sealed record SaleSourceResult(string Paper, string Gname, DateTime OutDate, IReadOnlyList<SaleSourceLine> Lines);

internal sealed class SourceHead
{
    public string gname { get; set; } = "";
    public DateTime outdate { get; set; }
}

/// <summary>
/// 销售退货（fr_sata row145 契约）：
/// 退货单 AH 前缀，行 type='销售'、ckamount 负数、ypaper/ro 联动原单，库存由 sale_up 触发器回补。
/// </summary>
[ApiController]
[Route("api/sale-returns")]
[Authorize]
public sealed class SaleReturnsController(ISaleReturnRepository repo, SqlConnectionFactory db) : ControllerBase
{
    /// <summary>原单可退行：GET /api/sale-returns/source/{paper}</summary>
    [HttpGet("source/{paper}")]
    public async Task<ActionResult<ApiResponse<SaleSourceResult>>> Source(string paper, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var head = await conn.QueryFirstOrDefaultAsync<SourceHead>(new CommandDefinition(
            """
            SELECT TOP 1 RTRIM(gname) AS gname, outdate
            FROM tbsada WITH (NOLOCK)
            WHERE paper = @paper AND type = N'销售' AND ckamount > 0
            """,
            new { paper = paper.Trim() },
            cancellationToken: ct));
        if (head?.gname is null)
            throw new AppException("RETURN_SOURCE_INVALID", $"原单 {paper} 不存在或无正数出库行", 422);

        var lines = (await conn.QueryAsync<SaleSourceLine>(new CommandDefinition(
            """
            SELECT id AS RowId,
                   RTRIM(cp) AS Cp, RTRIM(nno) AS Nno, RTRIM(na1) AS Na1, RTRIM(posi) AS Posi,
                   ckamount AS Qty, ro AS Returned, oprc AS Oprc
            FROM tbsada WITH (NOLOCK)
            WHERE paper = @paper AND type = N'销售' AND ckamount > 0
            ORDER BY id
            """,
            new { paper = paper.Trim() },
            cancellationToken: ct))).AsList();

        return Ok(ApiResponse<SaleSourceResult>.Success(
            new SaleSourceResult(paper.Trim(), head.gname, head.outdate, lines)));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<CreateSaleReturnResult>>> Create(
        [FromBody] CreateSaleReturnCommand command, CancellationToken ct)
    {
        var account = HttpContext.RequestServices.GetRequiredService<IJwtTokenService>().ParsePrincipalClaims(User)
            ?? throw new AppException("AUTH_FAILED", "令牌无效", 401);
        command.Dept = account.DeptNo;
        var result = await repo.CreateAsync(command, account.Operator, ct);
        return Created($"/api/sale-returns/{result.Paper}", ApiResponse<CreateSaleReturnResult>.Success(result));
    }
}
