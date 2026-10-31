using System.IO;
using System.Printing;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;
using QPGL.Domain.Printing;

namespace QPGL.Desktop.Printing;

/// <summary>
/// 打印服务（§8.3.1，直连模式）：PrintDataService 进程内直查（复用业务 Repository）
/// → FixedDocument 渲染 → 打印机输出 / 预览窗。打印动作写客户端审计日志。
/// </summary>
public static class PrintService
{
    /// <summary>票样取数。docType=单据类型（旧 fr_sale __.row46 L30 type=THISFORMSET.FLAG，默认销售）。</summary>
    public static async System.Threading.Tasks.Task<SalePrintDoc> LoadSaleDocAsync(string paper, string docType = "销售", CancellationToken ct = default)
        => await Db.Print.GetSalePrintDataAsync(paper.Trim(), docType, ct);

    /// <summary>打印单据（选打印机输出；docType 仅影响取数类型，票样同 re_sada）。</summary>
    public static async System.Threading.Tasks.Task PrintSaleAsync(string paper, string docType = "销售", CancellationToken ct = default)
    {
        var doc = await LoadSaleDocAsync(paper, docType, ct);
        var fixedDoc = SaleTicketDocument.Build(doc);

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        FitToPrintableArea(dialog, fixedDoc);

        dialog.PrintDocument(fixedDoc.DocumentPaginator, $"{docType}单 {paper}");
        LogPrinted(paper);
    }

    /// <summary>打印预览（DocumentViewer 全屏逐页查看，§8.3.1）。
    /// closeAfterPrint=true 时，预览窗内点「打印」成功后自动关闭（查询模式重打印用）。</summary>
    public static async System.Threading.Tasks.Task PreviewSaleAsync(string paper, string docType = "销售", CancellationToken ct = default, bool closeAfterPrint = false)
    {
        var doc = await LoadSaleDocAsync(paper, docType, ct);
        var fixedDoc = SaleTicketDocument.Build(doc);

        var window = new PrintPreviewWindow(fixedDoc, $"预览 - {docType}单 {paper}", closeAfterPrint);
        window.ShowDialog();
    }

    /// <summary>打印后直接输出默认打印机（免选对话框，用于保存后快速打印）。</summary>
    public static async System.Threading.Tasks.Task PrintSaleSilentAsync(string paper, string docType = "销售", CancellationToken ct = default)
    {
        var doc = await LoadSaleDocAsync(paper, docType, ct);
        var fixedDoc = SaleTicketDocument.Build(doc);

        var dialog = new PrintDialog(); // 默认打印机
        FitToPrintableArea(dialog, fixedDoc);
        dialog.PrintDocument(fixedDoc.DocumentPaginator, $"{docType}单 {paper}");
        LogPrinted(paper);
    }

    /// <summary>
    /// 票据页适配打印机可打印区（2026-10-28 针式打印机抬头被裁）：
    /// 票样 FixedPage 固定 760×640 直接输出时，针打硬件边距会裁掉页顶公司抬头与右侧内容——
    /// 按所选打印机的 PageImageableArea 先等比缩小（只缩不放大）再平移到可打印区原点；
    /// PDF/虚拟打印机 extent 覆盖全页时 scale=1、偏移 0，版面不变。
    /// </summary>
    internal static void FitToPrintableArea(PrintDialog dialog, FixedDocument doc)
    {
        try
        {
            if (doc.Pages.Count == 0 || doc.Pages[0].Child is not FixedPage page) return;
            var area = dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket).PageImageableArea;
            if (area is null || area.ExtentWidth <= 0 || area.ExtentHeight <= 0) return;
            var scale = Math.Min(1.0,
                Math.Min(area.ExtentWidth / page.Width, area.ExtentHeight / page.Height));
            var matrix = Matrix.Identity;
            matrix.Scale(scale, scale);                                  // 行向量约定：先缩放
            matrix.Translate(area.OriginWidth, area.OriginHeight);       // 后平移到可打印区原点
            page.RenderTransform = new MatrixTransform(matrix);
        }
        catch
        {
            // 打印能力读取失败（驱动不支持）按原样输出
        }
    }

    private static void LogPrinted(string paper)
    {
        // 客户端打印审计日志（本地滚动文件；§6-10 写操作审计）
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "print.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [INF] 打印单据 {paper} 操作员 {AppSession.Operator}{Environment.NewLine}");
    }
}
