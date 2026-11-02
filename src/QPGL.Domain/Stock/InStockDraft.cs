namespace QPGL.Domain.Stock;

/// <summary>
/// 散件入库暂存（2026-11-02 需求，复刻旧系统 tbistosm 暂存机制——fr_isto 添加行即落 tbistosm，
/// 重开页面回读，生效入库 rkqr 后清空；新系统按用户口径改为点「保存暂存」按钮落库）。
/// </summary>

/// <summary>暂存单头（tbistosm GROUP BY cno，供打开页面时的恢复选择列表：单号/日期/供应商/行数）。</summary>
public sealed record InStockDraftHeader(string Cno, DateTime InDate, string Gyname, int LineCount);

/// <summary>暂存单明细行（tbistosm 行读回，含单头冗余字段供恢复回填）。</summary>
public sealed record InStockDraftLine(
    int SmId, string Cp, string Nno, string Na1, string Ty, string Fa, string Dc, string Unit,
    decimal Jkamount, decimal Iprj, decimal Iprc, decimal Oprc, decimal Oprb,
    string Posi, string Depotno, string Note,
    string Gyno, string Gyname, string Pno, string Fkfs, string Ywr);

/// <summary>保存暂存命令：Lines 全量写 tbistosm（不入库、不碰库存）。</summary>
public sealed class InStockDraftSaveCommand
{
    /// <summary>暂存单号：空 = 自动取号（getnewdh 散件入库单）；非空 = 已有暂存单，全删重插（幂等）。</summary>
    public string? Cno { get; set; }
    public string Gyno { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Pno { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public string Ywr { get; set; } = "";
    public required IReadOnlyList<InStockLineInput> Lines { get; set; }
}

/// <summary>保存暂存结果：单号 + 各行 tbistosm.id（按输入序回填行模型，删行时精确删除暂存行）。</summary>
public sealed record InStockDraftSaveResult(string Cno, IReadOnlyList<int> SmIds);
