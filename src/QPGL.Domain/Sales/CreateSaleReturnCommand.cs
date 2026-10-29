namespace QPGL.Domain.Sales;

/// <summary>退货行输入：原单行 id + 退货数量（可带编辑后的售价/实售价/库号/仓位）。</summary>
public sealed class SaleReturnLineInput
{
    public int RowId { get; set; }             // 原销售单行 tbsada.id
    public decimal Qty { get; set; }           // 退货数量（正数；落库为负数 ckamount）
    public decimal? Oprc { get; set; }         // 售价改为（空=原行 oprc；旧编辑窗「售价改为」）
    public decimal? Oprct { get; set; }        // 实售价改为（空=原行 oprct；旧编辑窗「实售价改为」）
    public string? DepotNo { get; set; }       // 库号（空=原行；旧编辑窗「库号」下拉）
    public string? Posi { get; set; }          // 仓位（空=原行；旧编辑窗「仓位」下拉）
    public bool Scrap { get; set; }            // 废品仓（2026-10-29 需求：勾选=不回补库存 kcid=0，自动备注[已作废]）
}

/// <summary>
/// 销售退货命令（fr_sata row145 契约）。
/// 退货 = 新单（tbdjgs"退货单"AH 前缀）+ 行 type='销售'、ckamount 负数、ypaper=原单号、ro=原行id。
/// 库存回补由 sale_up 触发器完成（INSERT ckamount=0 避开 sale_in，再 UPDATE 为负数）。
/// </summary>
public sealed class CreateSaleReturnCommand
{
    public string Dept { get; set; } = "";     // 服务端从 JWT 注入
    public required string SourcePaper { get; set; }   // 原销售单号
    public required IReadOnlyList<SaleReturnLineInput> Lines { get; set; }
    public string? Reason { get; set; }        // 退货原因（追加到 remark）
    public string? RefundMethod { get; set; }  // 退款方式（guno；旧 Frmok COMBO1 必选，字典 tbsysset type='guno'）
    public string? InvoiceNo { get; set; }     // 发票号（旧 Frmok TxtPno → pno；空=原行 pno）
    public string? ShipMode { get; set; }      // 发货方式（旧 Frmok Cbot_mode → t_mode；空=原行 t_mode；字典 tbsysset type='fhfs'）
    public string? PaperType { get; set; }     // 销售类型（旧 Frmok CBOPAPERTYPE → tbsada_header.papertype；空=原单头，再空=门店）
}

/// <summary>退货结果。</summary>
public sealed class CreateSaleReturnResult
{
    public required string Paper { get; init; }
    public required int LineCount { get; init; }
    public required decimal TotalAmount { get; init; }   // 退货金额（正数绝对值合计）
}

/// <summary>退货领域校验。</summary>
public static class SaleReturnValidator
{
    public static void Validate(CreateSaleReturnCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Dept))
            throw new Common.AppException("SALE_DEPT_REQUIRED", "缺少部门", 422);
        if (string.IsNullOrWhiteSpace(command.SourcePaper))
            throw new Common.AppException("RETURN_PAPER_REQUIRED", "缺少原单号", 422);
        if (string.IsNullOrWhiteSpace(command.RefundMethod))
            throw new Common.AppException("RETURN_REFUND_REQUIRED", "退款方式不能为空！", 422);
        if (command.Lines is null || command.Lines.Count == 0)
            throw new Common.AppException("RETURN_LINES_EMPTY", "退货明细不能为空", 422);
        foreach (var line in command.Lines)
        {
            if (line.RowId <= 0)
                throw new Common.AppException("RETURN_LINE_INVALID", "退货行缺少原单行 id", 422);
            if (line.Qty <= 0)
                throw new Common.AppException("RETURN_LINE_INVALID", "退货数量必须大于 0", 422);
        }
    }
}
