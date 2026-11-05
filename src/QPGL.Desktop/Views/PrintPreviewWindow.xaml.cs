using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace QPGL.Desktop.Views;

/// <summary>
/// 打印预览窗（qp11 PrintPreviewWindow 同构复刻）：
/// 工具栏打印机下拉（LocalPrintServer 枚举 + 记忆上次选择）+ 份数 + 直接打印（PrintTicket.CopyCount），
/// 免去旧实现每次弹 PrintDialog 选择打印机；文档区保持 DocumentViewer 逐页缩放查看。
/// 2026-11-05 需求：销货/退货清单票面的零件编码列支持双击临时改编号（Popup 内联编辑框），
/// 仅本次打印生效——打印成功后自动恢复原编号，单据数据（tbsada.nno/库存/退货匹配）完全不动。
/// </summary>
public partial class PrintPreviewWindow : Window
{
    private const string NnoTagPrefix = "SaleNno:";
    private readonly FixedDocument _document;
    private readonly string _docName;
    /// <summary>点「打印」成功后是否自动关闭窗口（查询模式重打印入口传 true）。</summary>
    private readonly bool _closeAfterPrint;
    /// <summary>枚举结果缓存（名→队列）：共享打印机连接复用枚举成功的对象，打印时不再按名重建。</summary>
    private readonly Dictionary<string, PrintQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>票面编号临时覆盖（行 Seq → (编码单元格, 原文本)）：打印成功后按此恢复原编号。</summary>
    private readonly Dictionary<int, (TextBlock Cell, string Original)> _nnoOverrides = new();
    /// <summary>双击编号单元格弹出的内联编辑框（Enter 确认 / Esc 或点击别处取消）。</summary>
    private Popup? _nnoPopup;

    public PrintPreviewWindow(FixedDocument document, string title, bool closeAfterPrint = false)
    {
        InitializeComponent();
        _document = document;
        _docName = title;
        _closeAfterPrint = closeAfterPrint;
        DocInfoText.Text = title;
        Viewer.Document = document;
        Viewer.MouseDoubleClick += Viewer_MouseDoubleClick;
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
            // 编号覆盖仅本次打印生效：发送成功后恢复票面原编号（2026-11-05 需求）
            RestoreNnoEdits();
            // 查询模式重打印：发送成功后自动关闭预览窗（失败保持窗口便于重试）
            if (_closeAfterPrint) Close();
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

    /// <summary>ESC 关闭预览窗（2026-10-31 需求：确认后弹出的预览窗由用户手动关闭）。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    // ---- 票面零件编码临时改号（2026-11-05 需求：仅本次打印生效，打印成功后恢复原编号） ----

    /// <summary>双击销货/退货清单票面的零件编码单元格 → 弹内联编辑框（SaleTicketDocument 渲染时打了 SaleNno:Seq 标记）。</summary>
    private void Viewer_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBlock cell || cell.Tag is not string tag ||
            !tag.StartsWith(NnoTagPrefix, StringComparison.Ordinal) ||
            !int.TryParse(tag[NnoTagPrefix.Length..], out var seq))
            return;
        e.Handled = true;
        ShowNnoEditor(cell, seq);
    }

    /// <summary>在编码单元格上方弹 Popup 编辑框（浅黄可编辑样式，同旧系统可编辑输入框约定）。</summary>
    private void ShowNnoEditor(TextBlock cell, int seq)
    {
        CloseNnoEditor();
        var origin = cell.TransformToVisual(Viewer).Transform(new Point(0, 0));
        var box = new TextBox
        {
            Text = cell.Text,
            FontSize = 14,
            FontFamily = new FontFamily("Microsoft YaHei"),
            Width = 220,
            Height = 26,
            Padding = new Thickness(3, 0, 3, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.LightYellow,
            BorderBrush = Brushes.Orange,
            MaxLength = 18,   // tbsada.nno char(18) 同宽
        };
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
            {
                CommitNnoEdit(cell, seq, box.Text);
                ke.Handled = true;
            }
            else if (ke.Key == Key.Escape)
            {
                CloseNnoEditor();
                ke.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CloseNnoEditor();   // 点击别处视为取消
        _nnoPopup = new Popup
        {
            Placement = PlacementMode.Relative,
            PlacementTarget = Viewer,
            HorizontalOffset = origin.X,
            VerticalOffset = origin.Y - 1,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = Brushes.Orange,
                BorderThickness = new Thickness(1),
                Child = box,
            },
        };
        _nnoPopup.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        _nnoPopup.IsOpen = true;
    }

    /// <summary>确认改号：覆盖票面单元格文本并记录原文本（供打印成功后恢复）；空输入视为取消。</summary>
    private void CommitNnoEdit(TextBlock cell, int seq, string text)
    {
        CloseNnoEditor();
        var value = text.Trim();
        if (value.Length == 0 || value == cell.Text) return;
        if (!_nnoOverrides.TryGetValue(seq, out _))
            _nnoOverrides[seq] = (cell, cell.Text);   // 首次改号记录原编号；二次改号保留最初原文
        cell.Text = value;
    }

    private void CloseNnoEditor()
    {
        if (_nnoPopup is { IsOpen: true }) _nnoPopup.IsOpen = false;
        _nnoPopup = null;
    }

    /// <summary>打印成功后恢复票面原编号（覆盖仅本次打印生效；重新双击可再次改号打印）。</summary>
    private void RestoreNnoEdits()
    {
        CloseNnoEditor();
        foreach (var (_, (cell, original)) in _nnoOverrides)
            cell.Text = original;
        _nnoOverrides.Clear();
    }
}
