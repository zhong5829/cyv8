using QPGL.Domain.Common;

namespace QPGL.Domain.Stock;

/// <summary>
/// 散件入库命令（B1，M4-1）。
/// 旧实现出处：fr_isor（收货登记 tbistosm）+ fr_isto1 row31（生效：ISAPPE 更新 tbisto → INSERT tbistoed → 删 tbistosm）。
/// 一期 UI 合并「收货+生效」为一步，仓储内部按旧契约逐 SQL 重放（tbistosm 过程表前后状态与旧一致）。
/// </summary>
public sealed class CreateInStockCommand
{
    /// <summary>合同/送货单号（tbistosm.cno；空 = 生效时按 GETNEWDH('散件入库单') 自动取号，
    /// 旧 tbsysset「是否自动合同号」开关语义。非空 = 人工合同号（FRMCNO 输入），同 cno 重复入库校验依据）。</summary>
    public string Cno { get; set; } = "";

    /// <summary>供应商（gyno/gyname；挂帐必填 gyname）。</summary>
    public string Gyno { get; set; } = "";
    public string Gyname { get; set; } = "";

    /// <summary>票据号（pno）。</summary>
    public string Pno { get; set; } = "";

    /// <summary>付款方式（fkfs，tbsysset type='guno'；必填，挂帐须有供应商）。</summary>
    public required string Fkfs { get; set; }

    /// <summary>业务员（ywr，可空）。</summary>
    public string Ywr { get; set; } = "";

    /// <summary>运费（Freight，分摊前总额，可空）。</summary>
    public decimal Freight { get; set; }

    public required List<InStockLineInput> Lines { get; set; } = [];

    public void Validate()
    {
        // cno 可空 = 自动取号（旧 P_AUTO 开关语义），仓储层生效时 GETNEWDH 补齐
        if (string.IsNullOrWhiteSpace(Fkfs)) throw new AppException("FKFS_REQUIRED", "请输入支付方式", 422);
        // 供应商必填（旧 X__.row212 L74-78：EMPTY(TXTGYNAME) → '请输入供应商！' 拦截）
        if (string.IsNullOrWhiteSpace(Gyname)) throw new AppException("GYNAME_REQUIRED", "请输入供应商！", 422);
        if (string.Equals(Fkfs.Trim(), "挂帐", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(Gyname))
            throw new AppException("GYNAME_REQUIRED", "支付方式为挂帐时，请输入供货单位", 422);
        if (Lines.Count == 0) throw new AppException("LINES_EMPTY", "入库明细为空", 422);
        foreach (var l in Lines)
        {
            if (string.IsNullOrWhiteSpace(l.Nno)) throw new AppException("NNO_REQUIRED", "存在零件号为空的明细行", 422);
            if (l.Jkamount <= 0) throw new AppException("QTY_INVALID", $"配件 {l.Nno} 入库数量必须大于 0", 422);
            if (string.IsNullOrWhiteSpace(l.Na1)) throw new AppException("NA1_REQUIRED", "存在名称为空的明细行（不允许入库）", 422);
        }
    }
}

/// <summary>散件入库明细行输入（tbistosm 行字段子集）。</summary>
public sealed class InStockLineInput
{
    public required string Cp { get; set; }
    public required string Nno { get; set; }
    public required string Na1 { get; set; }
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>入库数量（jkamount，正数）。</summary>
    public decimal Jkamount { get; set; }
    /// <summary>进价单价（iprc）。</summary>
    public decimal Iprc { get; set; }
    /// <summary>进价金额（iprj）。</summary>
    public decimal Iprj { get; set; }
    /// <summary>售价（oprc，0 或低于进价时按旧公式推导——一期保持输入值，公式属客户定制不迁移）。</summary>
    public decimal Oprc { get; set; }
    /// <summary>批发价（oprb）。</summary>
    public decimal Oprb { get; set; }
    /// <summary>货位（posi）。</summary>
    public string Posi { get; set; } = "";
    /// <summary>仓库（depotno，tbsysset type='仓库'）。</summary>
    public string Depotno { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>关联订单号（isord；散件入库旧契约固定 'N'，订单入库后补）。</summary>
    public string Isord { get; set; } = "N";
}

/// <summary>散件入库结果。</summary>
public sealed class CreateInStockResult
{
    public required string Cno { get; set; }
    public int LineCount { get; set; }
    /// <summary>Σ(jkamount*iprc) 总进价金额（旧打印口径）。</summary>
    public decimal TotalIprcAmount { get; set; }
    /// <summary>Σ(jkamount*iprj) 总含税金额。</summary>
    public decimal TotalIprjAmount { get; set; }
    public List<InStockLineResult> Lines { get; set; } = [];
}

/// <summary>入库明细行结果（批次匹配结果）。</summary>
public sealed class InStockLineResult
{
    public required string Nno { get; set; }
    /// <summary>命中既有批次 tbisto.id；新批次为 null。</summary>
    public int? MatchedBatchId { get; set; }
    /// <summary>ISAPPE 返回的入库后库存数量（tbisto.kcamount，亦为 tbistoed.kcamount 快照）。</summary>
    public decimal KcamountAfter { get; set; }
    public decimal Jkamount { get; set; }
}
