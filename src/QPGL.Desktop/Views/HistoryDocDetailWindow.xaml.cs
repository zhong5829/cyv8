using System.Windows;
using System.Windows.Input;
using QPGL.Domain.Parts;
using QPGL.Domain.Query;

namespace QPGL.Desktop.Views;

/// <summary>
/// 历史行整单明细窗（2026-10-31 需求）：销售编辑窗的销售/进货历史双击后弹出，
/// 只读展示该单据全部明细行。销售单走 tbsada（GetOutDocLinesAsync），入库单走 tbistoed（按 cno）。
/// </summary>
public partial class HistoryDocDetailWindow : Window
{
    /// <summary>销售单明细（tbsada 明细行）。</summary>
    public HistoryDocDetailWindow(string title, IReadOnlyList<OutDocLine> lines)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        Title = title;
        TxtHeader.Text = title;
        SalesGrid.ItemsSource = lines;
        SalesPanel.Visibility = Visibility.Visible;
    }

    /// <summary>入库单明细（tbistoed 按进货单号）。</summary>
    public HistoryDocDetailWindow(string title, IReadOnlyList<PartPurchaseDocLine> rows)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        Title = title;
        TxtHeader.Text = title;
        PurchaseGrid.ItemsSource = rows;
        PurchasePanel.Visibility = Visibility.Visible;
    }

    /// <summary>Esc 关闭（2026-10-31 需求）。必须标记已处理并延迟到输入处理结束后再关闭：
    /// 否则同一 Esc 的访问键后处理会落到父窗（销售编辑窗）的 IsCancel 取消按钮，一下关闭两个窗口。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Dispatcher.BeginInvoke(new Action(Close));
    }
}
