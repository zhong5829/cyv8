using QPGL.Domain.Common;

namespace QPGL.Domain.Stock;

/// <summary>
/// 入库退货确认命令（菜单 B.A，权限码 BA；契约：开发文档/契约摘录/入库退货fr_istk契约.md §6）。
/// 旧实现出处：fr_istk 嗟.row132 确认序列（UPDATE ro → 定位仓位 → INSERT 负数流水 → ISAPPE 冲减 → 回写 kcamount/iprc）。
/// </summary>
public sealed class ConfirmPurchaseReturnCommand
{
    /// <summary>退款方式（fkfs，tbsysset type='guno'；旧确认窗必选校验）。</summary>
    public required string Fkfs { get; set; }

    /// <summary>退货原因（tbsysset type='退货原因'；非空时追加至行 NOTE '[退货原因：X]'）。</summary>
    public string ReturnReason { get; set; } = "";

    public required List<PurchaseReturnLineInput> Lines { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Fkfs)) throw new AppException("FKFS_REQUIRED", "请选择退款方式", 422);
        if (Lines.Count == 0) throw new AppException("LINES_EMPTY", "退货明细为空", 422);
        foreach (var l in Lines)
        {
            if (l.Amount <= 0) throw new AppException("QTY_INVALID", $"行 {l.SourceId} 退库数必须大于 0", 422);
        }
    }
}

/// <summary>退货行输入（c_th 退货车行：源入库流水 id + 用户可编辑五列）。</summary>
public sealed class PurchaseReturnLineInput
{
    /// <summary>原入库流水行 id（tbistoed.id，ro 列回写目标）。</summary>
    public required int SourceId { get; set; }

    /// <summary>退库数（正数；≤ 可退数 jkamount-ro，≤ 当前库存 kshu）。</summary>
    public required decimal Amount { get; set; }

    /// <summary>外币价（iprj，可编辑；BA01 细码锁）。</summary>
    public decimal Iprj { get; set; }

    /// <summary>进价（iprc，可编辑；P_WAY=1 时确认插入用原行 iprcold，此值仅回写展示）。</summary>
    public decimal Iprc { get; set; }

    /// <summary>仓位（posi，可编辑；确认时按库存充足性定位）。</summary>
    public string Posi { get; set; } = "";

    /// <summary>库号（depotno，可编辑；须与入库时一致才能退）。</summary>
    public string Depotno { get; set; } = "";
}

/// <summary>确认退货结果。</summary>
public sealed class ConfirmPurchaseReturnResult
{
    /// <summary>退货流水单号（GETNEWDH '散件入库单'，新插入行 cno/seano）。</summary>
    public string Cno { get; set; } = "";

    public int LineCount { get; set; }

    public List<PurchaseReturnLineResult> Lines { get; set; } = [];
}

public sealed class PurchaseReturnLineResult
{
    public int SourceId { get; set; }
    public int NewRowId { get; set; }
    public decimal Amount { get; set; }
    /// <summary>冲减前批次库存（tbistoed.kcamount 回写值）。</summary>
    public decimal KcBefore { get; set; }
}
