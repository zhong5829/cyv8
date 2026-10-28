namespace QPGL.Domain.Stock;

using QPGL.Domain.Common;

/// <summary>调拨入库确认结果（fr_dbrk row7）。</summary>
public sealed class DbrkConfirmResult
{
    /// <summary>调出单号（入参回传）。</summary>
    public required string Paper { get; init; }
    /// <summary>SP dbrk 生成的进库单号（c_rfh.CNO）。</summary>
    public required string NewPaper { get; init; }
    /// <summary>是否写了 tbysyf 挂帐行（guno∈挂帐/托收/汇款 且金额≠0）。</summary>
    public bool CreditWritten { get; init; }
}

/// <summary>批量确认结果（__.row68：逐单独立确认）。</summary>
public sealed class DbrkBatchResult
{
    public List<DbrkConfirmResult> Confirmed { get; } = [];
    /// <summary>失败单据（单号 → 错误信息）；每单独立事务，失败不影响其它单。</summary>
    public Dictionary<string, string> Failed { get; } = [];
}

public sealed class DbrkConfirmCommand
{
    public void Validate()
    {
        if (Papers.Count == 0)
            throw new AppException("NO_PAPER", "没有数据，不能调拨确认！", 422);
    }

    /// <summary>待确认调出单号列表（单条确认传 1 个）。</summary>
    public required IReadOnlyList<string> Papers { get; init; }

    /// <summary>客户端标识（旧 P_CLIENT，进 dbrk SP @client——非「长沙贻惠」走含售价比率分支）。</summary>
    public required string Client { get; init; }
}
