using QPGL.Domain.Common;

namespace QPGL.Domain.Stock;

/// <summary>调仓明细行（tbsadc，fr_sadc）。</summary>
public sealed class StockAdjustLine
{
    public int Id { get; init; }
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Fa { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Depotno { get; init; } = "";   // 源仓库
    public string Posi { get; init; } = "";      // 源仓位
    public string Depotno1 { get; init; } = "";  // 目标仓库
    public string Posi1 { get; init; } = "";     // 目标仓位
    public decimal Ckamount { get; init; }       // 调仓数量
    public decimal Iprc { get; init; }
    public decimal Iprj { get; init; }
    public decimal Oprc { get; init; }
    public string Paper { get; init; } = "";
    public string Per { get; init; } = "";
    public DateTime Outdate { get; init; }
    public string Note { get; init; } = "";
    public int Kcid { get; init; }               // tbisto.id（源批次）
    public string M { get; init; } = "";         // 确认标志（'*'=已确认）
}

/// <summary>新增调仓明细行命令（fr_sadc a_.row150）。</summary>
public sealed class AddStockAdjustLineCommand
{
    public int Kcid { get; set; }                // 源批次 tbisto.id
    public decimal Ckamount { get; set; }        // 数量（≥0）
    public string Depotno1 { get; set; } = "";   // 目标仓库
    public string Posi1 { get; set; } = "";      // 目标仓位
    public decimal Oprc { get; set; }
    public string? Note { get; set; }

    public void Validate()
    {
        if (Ckamount < 0)
            throw new AppException("INVALID_AMOUNT", "请输入有效调仓数量！", 422);
        // 旧系统 a_.row150 无目标仓库必填校验（仅校验目标≠源仓位，由仓储实现 SamePosition）
    }
}

/// <summary>修改调仓明细行命令（fr_sadc -_.row168：oprc,depotno1,posi1,ckamount,per）。</summary>
public sealed class UpdateStockAdjustLineCommand
{
    public int Id { get; set; }
    public decimal Ckamount { get; set; }
    public string Depotno1 { get; set; } = "";
    public string Posi1 { get; set; } = "";
    public decimal Oprc { get; set; }
}

/// <summary>确认调仓结果（fr_sadc __.row11）。</summary>
public sealed class ConfirmStockAdjustResult
{
    public int ConfirmedLines { get; init; }
    public int LostLines { get; init; }          // 校验失败行数（库存不足/批次变动）
}
