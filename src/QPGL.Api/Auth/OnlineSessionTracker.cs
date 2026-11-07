using System.Collections.Concurrent;

namespace QPGL.Api.Auth;

/// <summary>
/// Web 端在线会话跟踪（内存，单例）：每个带令牌的请求刷新操作员 lastSeen，
/// 在线 = 最近 5 分钟内有请求（去重操作员数）。桌面端工作台直读本单例显示在线人数。
/// </summary>
public sealed class OnlineSessionTracker
{
    public static OnlineSessionTracker Instance { get; } = new();

    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new();

    public void Touch(string operatorName)
    {
        if (string.IsNullOrWhiteSpace(operatorName)) return;
        var now = DateTime.UtcNow;
        _lastSeen[operatorName] = now;
        // 顺手清理过期项，防长驻进程字典膨胀
        foreach (var kv in _lastSeen)
            if (now - kv.Value > OnlineWindow)
                _lastSeen.TryRemove(kv.Key, out _);
    }

    /// <summary>在线人数（窗口内有请求的去重操作员数）。</summary>
    public int CountOnline()
    {
        var now = DateTime.UtcNow;
        return _lastSeen.Count(kv => now - kv.Value <= OnlineWindow);
    }

    /// <summary>在线操作员名单（按最近活跃倒序）。</summary>
    public IReadOnlyList<string> OnlineOperators()
    {
        var now = DateTime.UtcNow;
        return _lastSeen
            .Where(kv => now - kv.Value <= OnlineWindow)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
    }
}
