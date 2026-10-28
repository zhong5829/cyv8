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

    public PrintPreviewWindow(FixedDocument document, string title)
    {
        InitializeComponent();
        _document = document;
        _docName = title;
        DocInfoText.Text = title;
        Viewer.Document = document;
        LoadPrinters();
    }

    /// <summary>枚举本机打印机并记忆上次选择（qp11 LoadPrinters 同构；本工程记忆值存本地 print.log 同目录 ini）。</summary>
    private void LoadPrinters()
    {
        var server = new LocalPrintServer();
        foreach (var queue in server.GetPrintQueues())
            CmbPrinter.Items.Add(queue.Name);

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
            var server = new LocalPrintServer();
            var queue = server.GetPrintQueue(printerName)
                        ?? throw new InvalidOperationException($"未找到打印机: {printerName}");

            // 独立 PrintTicket 设份数（qp11 同款：避免默认票 Commit 需管理员权限）
            var ticket = queue.DefaultPrintTicket.Clone();
            ticket.CopyCount = copies;

            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(_document.DocumentPaginator, ticket);

            SaveLastPrinter(printerName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打印失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
