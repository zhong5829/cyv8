using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace QPGL.Desktop.Views;

/// <summary>
/// 打印预览窗（qp11 PrintPreviewWindow 同构复刻）：
/// 工具栏打印机下拉（LocalPrintServer 枚举 + 记忆上次选择）+ 份数 + 直接打印（PrintTicket.CopyCount），
/// 免去旧实现每次弹 PrintDialog 选择打印机；文档区保持 DocumentViewer 逐页缩放查看。
/// </summary>
public partial class PrintPreviewWindow : Window
{
    private readonly FixedDocument _document;
    private readonly string _docName;
    /// <summary>枚举结果缓存（名→队列）：共享打印机连接复用枚举成功的对象，打印时不再按名重建。</summary>
    private readonly Dictionary<string, PrintQueue> _queues = new(StringComparer.OrdinalIgnoreCase);

    public PrintPreviewWindow(FixedDocument document, string title)
    {
        InitializeComponent();
        _document = document;
        _docName = title;
        DocInfoText.Text = title;
        Viewer.Document = document;
        LoadPrinters();
    }

    /// <summary>枚举本机打印机并记忆上次选择（qp11 LoadPrinters 同构；本工程记忆值存本地 print.log 同目录 ini）。
    /// 枚举类型须含 Connections——无参默认只列 Local，识别不到其它电脑共享过来的打印机（\\主机\共享名）。</summary>
    private void LoadPrinters()
    {
        var server = new LocalPrintServer();
        foreach (var queue in server.GetPrintQueues(new[]
                 {
                     EnumeratedPrintQueueTypes.Local,
                     EnumeratedPrintQueueTypes.Connections,
                 }))
        {
            _queues[queue.Name] = queue;
            CmbPrinter.Items.Add(queue.Name);
        }

        var saved = ReadLastPrinter();
        if (!string.IsNullOrEmpty(saved))
        {
            for (var i = 0; i < CmbPrinter.Items.Count; i++)
            {
                if (CmbPrinter.Items[i]?.ToString() == saved)
                {
                    CmbPrinter.SelectedIndex = i;
                    break;
                }
            }
        }
        if (CmbPrinter.SelectedIndex < 0 && CmbPrinter.Items.Count > 0)
            CmbPrinter.SelectedIndex = 0;
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var printerName = CmbPrinter.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(printerName))
        {
            MessageBox.Show("未选择打印机", "打印预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var copies = 1;
        if (int.TryParse(TxtCopies.Text, out var c) && c > 0) copies = c;

        try
        {
            WriteToPrinter(printerName, copies);
            SaveLastPrinter(printerName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"打印失败: {ex.Message}\n\n" +
                "共享打印机排查：1) 在本机【设置→打印机和扫描仪】删除该共享打印机后重新添加连接；" +
                "2) 确认本机已安装该型号驱动（位数与主机一致）；3) 重启本机 Print Spooler 服务后重试。",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>输出到所选打印机：优先复用枚举成功的队列对象（共享连接按名重建会报 Win32"打印机无效"）；
    /// 读默认票失败（共享驱动 DevMode 异常）兜底用空票；队列对象取 writer 失败再按名重试一次。</summary>
    private void WriteToPrinter(string printerName, int copies)
    {
        var server = new LocalPrintServer();
        var queue = _queues.TryGetValue(printerName, out var cached)
            ? cached
            : new PrintQueue(server, printerName);   // 共享打印机（\\主机\共享名）不走 GetPrintQueue 按名查找（其只解析本地队列）

        PrintTicket ticket;
        try
        {
            ticket = queue.DefaultPrintTicket?.Clone() ?? new PrintTicket();
        }
        catch
        {
            ticket = new PrintTicket();   // 默认票读取失败 → 按驱动默认设置打印
        }
        ticket.CopyCount = copies;

        try
        {
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(_document.DocumentPaginator, ticket);
        }
        catch
        {
            // 队列对象输出失败 → 重建队列 + 空票重试一次
            var fresh = new PrintQueue(server, printerName);
            var writer = PrintQueue.CreateXpsDocumentWriter(fresh);
            writer.Write(_document.DocumentPaginator, new PrintTicket { CopyCount = copies });
        }
    }

    /// <summary>记忆上次打印机（exe 同目录 print.ini，单行：打印机名）。</summary>
    private static string ReadLastPrinter()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "print.ini");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch { return ""; }
    }

    private static void SaveLastPrinter(string printerName)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "print.ini"), printerName);
        }
        catch { /* 记忆失败不影响打印 */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
