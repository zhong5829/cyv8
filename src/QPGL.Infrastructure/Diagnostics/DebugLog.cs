using System.IO;

namespace QPGL.Infrastructure;

/// <summary>
/// 轻量调试日志（exe 同目录 debug.log，逐行毫秒时间戳）。
/// 用于远程链路性能定位（2026-10-29）：搜索/加载各段耗时埋点；正式版可整体移除。
/// </summary>
public static class DebugLog
{
    private static readonly object _lock = new();

    public static void Log(string message)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
            lock (_lock)
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
        }
        catch { /* 日志失败不影响业务 */ }
    }
}
