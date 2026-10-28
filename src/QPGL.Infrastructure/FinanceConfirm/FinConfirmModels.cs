using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.FinanceConfirm;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.FinanceConfirm;

/// <summary>
/// 财务确认仓储（G.1，旧 fr_sk 复刻：出库/入库/调拨/代销付款四页签）。
/// 出库确认/还原复用 IReceiptRepository（ReceiptRepository 已按 row358/row369/row34 实现）；
/// 本仓储补齐页1列表查询/合计、页2/3/7 的查询+确认+还原（SQL 照抄逆向 prg）。
/// </summary>
public interface IFinConfirmRepository
{
    // ---- 页1 出库确认 ----
    /// <summary>出库确认列表（旧 setsource CASE 1：tbsada 按单聚合 + sk 派生 + ssje）。</summary>
    Task<IReadOnlyList<FinConfirmOutRow>> SearchOutAsync(FinConfirmOutRequest request, CancellationToken ct = default);
    /// <summary>出库确认合计（旧 Z_.row54 CASE 1）。</summary>
    Task<IReadOnlyList<FinConfirmTotalRow>> TotalOutAsync(FinConfirmOutRequest request, CancellationToken ct = default);

    // ---- 页2 入库确认 ----
    Task<IReadOnlyList<FinConfirmInRow>> SearchInAsync(FinConfirmInRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<FinConfirmTotalRow>> TotalInAsync(FinConfirmInRequest request, CancellationToken ct = default);
    /// <summary>入库确认明细（旧 ___.row302：seano+gyname+indate+isord）。</summary>
    Task<IReadOnlyList<FinConfirmLine>> GetInLinesAsync(string seano, string gyname, DateTime indate, string isord, CancellationToken ct = default);
    /// <summary>tbysyf 已有该单记录（旧 row33 CASE 2 ysyfcno 检查：锁定 fkfs/gyname 只改码单）。</summary>
    Task<bool> HasInYsyfAsync(string seano, string isord, CancellationToken ct = default);
    /// <summary>入库确认（旧 __.row410 FRM_PNO 确定）。</summary>
    Task ConfirmInAsync(FinConfirmInCommand command, string per, CancellationToken ct = default);
    /// <summary>入库还原（旧 蓽.row34 CASE 2）。</summary>
    Task CancelInAsync(string seano, string isord, string per, CancellationToken ct = default);

    // ---- 页3 调拨确认 ----
    Task<IReadOnlyList<FinConfirmTransferRow>> SearchTransferAsync(FinConfirmTransferRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<FinConfirmTotalRow>> TotalTransferAsync(FinConfirmTransferRequest request, CancellationToken ct = default);
    /// <summary>调拨确认明细（旧 ⒀.row35：type='调拨' and paper）。</summary>
    Task<IReadOnlyList<FinConfirmLine>> GetTransferLinesAsync(string paper, CancellationToken ct = default);
    /// <summary>调拨确认（旧 妴_.row478：挂帐类写 tbysyf 转出方 + update tbsada）。</summary>
    Task ConfirmTransferAsync(FinConfirmTransferCommand command, string per, CancellationToken ct = default);
    /// <summary>调拨还原（旧 蓽.row34 CASE 3：双方分公司 tbysyf 各删、mark='R' 拒绝）。</summary>
    Task CancelTransferAsync(string paper, string gname, string dpm, string per, CancellationToken ct = default);

    // ---- 页7 代销付款确认 ----
    Task<IReadOnlyList<FinConfirmConsignRow>> SearchConsignAsync(FinConfirmConsignRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<FinConfirmTotalRow>> TotalConsignAsync(FinConfirmConsignRequest request, CancellationToken ct = default);
    /// <summary>代销付款确认（旧 ___.row454：逐勾选行 update skper/skdate）。</summary>
    Task ConfirmConsignAsync(IReadOnlyList<FinConfirmConsignCommand> commands, string per, CancellationToken ct = default);
    /// <summary>代销付款还原（旧 蓽.row34 CASE 7：逐勾选行 skper=''/skdate=1900）。</summary>
    Task CancelConsignAsync(IReadOnlyList<FinConfirmConsignCommand> commands, string per, CancellationToken ct = default);

    /// <summary>月结保护（旧 蓽.row34 P_YJ：tbyj.max(yjdate)+1 &gt; 单据确认日期 → 拒绝）。</summary>
    Task<bool> IsMonthlyLockedAsync(DateTime confirmDate, CancellationToken ct = default);

    /// <summary>单据类型字典（旧 P__.row73 CBOPAPERTYPE Init：tbsysset name='papertype' group by valuec）。</summary>
    Task<IReadOnlyList<string>> GetPaperTypesAsync(CancellationToken ct = default);
}

/// <summary>页1 出库确认查询请求（旧 CBO1 销售类型 + CBOPAPERTYPE 过滤；旧 fr_sk 无部门过滤）。</summary>
public sealed class FinConfirmOutRequest
{
    /// <summary>销售类型（旧 CBO1：销售/预售）。</summary>
    public string Type { get; set; } = "销售";
    /// <summary>单据类型（旧 CBOPAPERTYPE；空=全部）。</summary>
    public string? PaperType { get; set; }
    /// <summary>状态：0 全部 / 1 未确认（mark=''）/ 2 已确认（mark<>''，旧 fr_find BB 选项）。</summary>
    public int Status { get; set; } = 1;
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Paper { get; set; }
    public string? Gname { get; set; }
    public string? Per1 { get; set; }
}

/// <summary>页2 入库确认查询请求（旧 setsource CASE 2：cp<>'急件' and indate>getdate()-7 and mark<>'已确认'）。</summary>
public sealed class FinConfirmInRequest
{
    /// <summary>状态：0 全部 / 1 未确认（mark<>'已确认'）/ 2 已确认。</summary>
    public int Status { get; set; } = 1;
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Cno { get; set; }
    public string? Gyname { get; set; }
}

/// <summary>页3 调拨确认查询请求（旧 setsource CASE 3：type='调拨' and mark='' and outdate>getdate()-7）。</summary>
public sealed class FinConfirmTransferRequest
{
    /// <summary>状态：0 全部 / 1 未确认（mark=''）/ 2 已确认。</summary>
    public int Status { get; set; } = 1;
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Paper { get; set; }
    public string? Gname { get; set; }
}

/// <summary>页7 代销付款确认查询请求（旧 setsource CASE 7：type in('销售','历史') and mark<>''，默认 1=2 须先查询）。</summary>
public sealed class FinConfirmConsignRequest
{
    /// <summary>状态：0 全部 / 1 未确认（skper=''）/ 2 已确认（skper<>''）。</summary>
    public int Status { get; set; } = 1;
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Paper { get; set; }
    public string? Gname { get; set; }
}
