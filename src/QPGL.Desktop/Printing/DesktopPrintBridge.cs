using System.Windows;
using QPGL.Api.Printing;

namespace QPGL.Desktop.Printing;

/// <summary>
/// Web 端静默打印桥（Web 端与桌面端同进程，经 WebApiRunner 注入）。
/// WPF 打印对象（FixedDocument 渲染、PrintDialog/PrintQueue）只能在 UI(STA) 线程创建与操作，
/// 因此这里把 <see cref="PrintService.PrintSaleSilentAsync"/> 调度到 Application.Current.Dispatcher 执行，
/// API 请求线程只 await 结果；打印机沿用桌面端静默打印逻辑（系统默认打印机，不弹选择框）。
/// </summary>
public sealed class DesktopPrintBridge : IDesktopPrintBridge
{
    public async Task<DesktopPrintResult> PrintSaleAsync(string paper, string docType, CancellationToken ct = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return DesktopPrintResult.Fail("桌面端未运行，无法调用静默打印服务");

        try
        {
            // InvokeAsync 把渲染/打印整个调度到 UI 线程；内部 await 数据库取数不阻塞 UI（Dispatcher 继续泵消息）
            var task = await dispatcher.InvokeAsync(() => PrintService.PrintSaleSilentAsync(paper.Trim(), docType, ct));
            var printer = await task;
            return DesktopPrintResult.Ok(printer);
        }
        catch (Exception ex)
        {
            return DesktopPrintResult.Fail(ex.GetBaseException().Message);
        }
    }
}
