using System.Windows;
using System.Windows.Controls;
using System.Printing;
using QPGL.Desktop.Printing;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

/// <summary>
/// 凭证打印版式窗（fr_finvprint 复刻）：六选一（自动判定=按 AMOUNT0/FORMONEY0 四选一；
/// 每页行数 finvprn 缺省 5）→ FinVouchPrintDocument 构建 → 预览或直打。
/// </summary>
namespace QPGL.Desktop.Views;

public partial class FinVouchPrintWindow : Window
{
    private readonly FinOpVouchRead _head;
    private readonly IReadOnlyList<FinVouchRow> _lines;

    public FinVouchPrintWindow(FinOpVouchRead head, IReadOnlyList<FinVouchRow> lines, int finvprn)
    {
        InitializeComponent();
        _head = head;
        _lines = lines;
        TxtRows.Text = (finvprn <= 0 ? 5 : finvprn).ToString();
        Owner = Application.Current.MainWindow;
    }

    private int SelectedLayout => Opt4.IsChecked == true ? FinVouchPrintDocument.LayoutFinvouch4
        : Opt3.IsChecked == true ? FinVouchPrintDocument.LayoutFinvouch3
        : Opt2.IsChecked == true ? FinVouchPrintDocument.LayoutFinvouch2
        : Opt1.IsChecked == true ? FinVouchPrintDocument.LayoutFinvouch
        : Opt5.IsChecked == true ? FinVouchPrintDocument.LayoutFinvouch1
        : FinVouchPrintDocument.AutoLayout(_lines);

    private int RowsPerPage => int.TryParse(TxtRows.Text, out var n) && n > 0 ? n : 5;

    private void OnPreview(object sender, RoutedEventArgs e) => Run(preview: true);

    private void OnPrint(object sender, RoutedEventArgs e) => Run(preview: false);

    private void Run(bool preview)
    {
        if (_lines.Count == 0) { MessageBox.Show("没有可打印的分录！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var doc = FinVouchPrintDocument.Build(_head, _lines, SelectedLayout, RowsPerPage,
            Services.Db.UniversalQuery.GetCompanyNameAsync().GetAwaiter().GetResult());
        if (preview)
        {
            new PrintPreviewWindow(doc, "预览 - 记帐凭证") { Owner = this }.ShowDialog();
        }
        else
        {
            var dialog = new PrintDialog
            {
                PrintTicket = new PrintTicket { PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4) },
            };
            if (dialog.ShowDialog() == true)
            {
                Printing.PrintService.FitToPrintableArea(dialog, doc);
                dialog.PrintDocument(doc.DocumentPaginator, "记帐凭证");
            }
        }
        Close();
    }
}
