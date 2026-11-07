namespace QPGL.Api.Printing;

/// <summary>桌面端静默打印结果：成功时带实际使用的打印机名，失败带原因。</summary>
public sealed record DesktopPrintResult(bool Success, string PrinterName, string? Error)
{
    public static DesktopPrintResult Ok(string printerName) => new(true, printerName, null);
    public static DesktopPrintResult Fail(string error) => new(false, "", error);
}

/// <summary>
/// 桌面端静默打印桥（Web 端仅在桌面进程内托管时可用）。
/// 实现者（QPGL.Desktop）负责把 WPF 打印对象（FixedDocument / PrintDialog）调度到 UI(STA) 线程执行；
/// 独立部署时注册 <see cref="UnavailableDesktopPrintBridge"/>，明确返回失败原因，不静默失败。
/// </summary>
public interface IDesktopPrintBridge
{
    /// <summary>按单号静默打印（docType 仅影响取数类型，票面同桌面端 SaleTicketDocument）。</summary>
    Task<DesktopPrintResult> PrintSaleAsync(string paper, string docType, CancellationToken ct = default);
}

/// <summary>非桌面进程内托管时的占位实现：Web 端不使用浏览器打印，直接报不可用。</summary>
public sealed class UnavailableDesktopPrintBridge : IDesktopPrintBridge
{
    public static readonly UnavailableDesktopPrintBridge Instance = new();

    public Task<DesktopPrintResult> PrintSaleAsync(string paper, string docType, CancellationToken ct = default)
        => Task.FromResult(DesktopPrintResult.Fail(
            "未连接桌面端静默打印服务：Web 端需由桌面工作台托管运行（WebApiRunner 进程内），独立部署模式不支持打印"));
}
