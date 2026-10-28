namespace QPGL.Tests.Replay;

/// <summary>
/// 契约回放配置：隔离副本库「rscar_replay」（本地 SQLEXPRESS，完整 RESTORE 自生产隔离副本 rscar，
/// 触发器/函数/存储过程全量；快照秒级重置）。不再连远程 frp。
/// 可用环境变量 QPGL_REPLAY_CS 覆盖连接串。
/// </summary>
public static class ReplayConfig
{
    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("QPGL_REPLAY_CS")
        ?? "Server=localhost\\SQLEXPRESS;Trusted_Connection=True;Database=rscar_replay;Max Pool Size=50;Connection Timeout=30;TrustServerCertificate=True;Encrypt=True";

    /// <summary>回放统一参数（两边完全一致的输入）。Operator/Gname 为回放专用标记，
    /// 与生产数据零碰撞（已验证 rscar 中 per='REPLAY' 与「回放专用客户R」均 0 行），
    /// 用作 diff 窗口过滤依据（rscar 生产库 XS26 有 9.4 万行，paper 前缀窗口不可用）。</summary>
    public const string Dept = "A";
    public const string Operator = "REPLAY";
    public const string Gno = "KH0001";
    public const string Gname = "回放专用客户R";
}
