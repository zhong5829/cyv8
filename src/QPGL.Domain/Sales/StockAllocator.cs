using QPGL.Domain.Common;

namespace QPGL.Domain.Sales;

/// <summary>开单领域校验错误。</summary>
public static class SaleValidator
{
    public static void Validate(CreateSaleCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Dept))
            throw new AppException("SALE_DEPT_REQUIRED", "缺少部门", 422);
        if (command.Lines is null || command.Lines.Count == 0)
            throw new AppException("SALE_LINES_EMPTY", "明细不能为空", 422);

        foreach (var line in command.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Cp))
                throw new AppException("SALE_LINE_INVALID", "明细行缺少配件编号", 422);
            if (line.Qty <= 0)
                throw new AppException("SALE_LINE_INVALID", $"配件 {line.Cp} 数量必须大于 0", 422);
        }
    }
}

/// <summary>
/// 凑批出库分配算法（旧系统 fr_sale row548 语义的纯函数复刻）：
/// 按批次库存量降序逐批分配；总量不足时硬拦截报错
/// （2026-10-30 用户确认：不允许负库存产生，覆盖旧系统"剩余量挂末位批次"语义）。
/// </summary>
public static class StockAllocator
{
    public sealed record Allocation(int Kcid, decimal Qty);

    /// <param name="batches">tbisto 批次（调用方按 kcamount DESC 传入；内部再稳排一次）</param>
    /// <param name="totalQty">需求总量（&gt;0）</param>
    public static IReadOnlyList<Allocation> Allocate(
        IEnumerable<(int Kcid, decimal Kcamount)> batches, decimal totalQty)
    {
        if (totalQty <= 0)
            throw new AppException("SALE_LINE_INVALID", "数量必须大于 0", 422);

        var ordered = batches
            .OrderByDescending(b => b.Kcamount)
            .ThenBy(b => b.Kcid)               // 稳定顺序（同量按 id，保证回放确定性）
            .ToList();

        var result = new List<Allocation>();
        var remaining = totalQty;

        foreach (var (kcid, kcamount) in ordered)
        {
            if (remaining <= 0) break;
            var take = Math.Min(kcamount, remaining);
            if (take > 0)
            {
                result.Add(new Allocation(kcid, take));
                remaining -= take;
            }
        }

        // 库存不足（2026-10-30 用户确认：不允许负库存产生）——含两场景：
        // a) 批次行存在但全部 kcamount<=0（已被先出清）；b) 多批凑批后仍有缺口（挂末位会产生负库存）
        if (remaining > 0)
            throw new AppException("STOCK_NOT_ENOUGH", "库存不足，不允许负库存出库", 422);

        return result;
    }
}
