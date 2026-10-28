using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.Printing;

/// <summary>
/// 记帐凭证打印文档（fr_finvprint 六版式契约，re 报表 Finvouch/1/2/3/4 + finvprn 分页）：
/// 版式差异 = 借/贷列显示与外币列（fr_finvprint myprint AMOUNT0=Σamount FORMONEY0=Σformoney≠0 计数）：
/// Finvouch（有外币行）、Finvouch1（无金额无外币）、Finvouch2（无金额有外币）、Finvouch3（有金额无外币）。
/// Finvouch4 独立版式（含外币金额列，REPORT FORM 直出）。
/// 每页行数 = tbsysset name='finvprn' valuen（0 缺省 5），尾部补空行对齐整页。
/// </summary>
public static class FinVouchPrintDocument
{
    public const int LayoutAuto = 1;   // 自动判定
    public const int LayoutFinvouch3 = 2; // 金额（无外币）
    public const int LayoutFinvouch2 = 3; // 外币（无金额）
    public const int LayoutFinvouch = 4;  // 全栏
    public const int LayoutFinvouch1 = 5; // 简表（无金额无外币）
    public const int LayoutFinvouch4 = 6; // 独立版式（含外币原币金额）

    private const double PageW = 793;  // A4 纵向 px (96dpi)
    private const double PageH = 1122;
    private const double Margin = 40;
    private const double RowH = 22;

    /// <summary>自动判定版式（myprint Value=1 四选一口径）。</summary>
    public static int AutoLayout(IReadOnlyList<FinVouchRow> lines)
    {
        var amount0 = lines.Sum(l => l.Debit + l.Credit); // 旧 AMOUNT0=ΣAMOUNT；新行无 amount，取借贷合计口径（借贷任一≠0 即金额行）
        var formoney0 = 0; // 本部署 finvouch 写路径恒无外币（save 固定 fcash=0），formoney 恒空 → 0
        return (amount0 != 0, formoney0 != 0) switch
        {
            (true, true) => LayoutFinvouch,
            (false, false) => LayoutFinvouch1,
            (false, true) => LayoutFinvouch2,
            (true, false) => LayoutFinvouch3,
        };
    }

    /// <summary>构建 FixedDocument（head：凭证字/编号/日期/制单审核过帐；lines 按 vouchid 序分录）。</summary>
    public static FixedDocument Build(FinOpVouchRead head, IReadOnlyList<FinVouchRow> lines, int layout, int rowsPerPage, string company)
    {
        rowsPerPage = rowsPerPage <= 0 ? 5 : rowsPerPage;
        var showAmount = layout != LayoutFinvouch1 && layout != LayoutFinvouch2;
        var showFormoney = layout == LayoutFinvouch || layout == LayoutFinvouch2 || layout == LayoutFinvouch4;

        var doc = new FixedDocument();
        // finvprn 分页（myprint L14-38：PAGEID=RECNO()/RENUM 语义在此等价于每 rowsPerPage 行一页）
        var pages = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling(lines.Count / (double)rowsPerPage)))
            .Select(p => lines.Skip(p * rowsPerPage).Take(rowsPerPage).ToList()).ToList();
        // 尾页补空行对齐整页（myprint L32-38）
        if (pages.Count > 0 && pages[^1].Count < rowsPerPage)
            while (pages[^1].Count < rowsPerPage) pages[^1].Add(new FinVouchRow());

        var h = head.Lines.Count > 0 ? head.Lines[0] : new FinVouchRow();
        var td = lines.Sum(l => l.Debit);
        var tc = lines.Sum(l => l.Credit);

        foreach (var pageLines in pages)
        {
            var canvas = new Canvas();
            double y = Margin;

            // 页眉（报表头：编制单位 + 标题）
            DrawText(canvas, company, PageW / 2, y, 12, FontWeights.Normal, TextAlignment.Left);
            y += 18;
            var title = h.Vouword.Trim() + "字 凭证";
            if (layout == LayoutFinvouch4) title += "（外币）";
            DrawText(canvas, title, PageW / 2, y, 16, FontWeights.Bold, TextAlignment.Center);
            y += 24;
            DrawText(canvas, $"第 {h.Vcode} 号", Margin, y, 11, FontWeights.Normal, TextAlignment.Left);
            DrawText(canvas, $"{h.Date:yyyy年MM月dd日}", PageW - Margin, y, 11, FontWeights.Normal, TextAlignment.Right);
            y += 14;

            // 列头（格子表）
            var x = Margin;
            var colW = new[]
            {
                36.0,                    // 录
                PageW - Margin * 2 - 36 - 150 - 110 - (showAmount ? 90 : 0) - (showFormoney ? 90 : 0), // 摘要
                110.0,                   // 科目
                150.0,                   // 科目名
            }.ToList();
            if (showAmount) colW.Add(90);   // 金额
            if (showFormoney) colW.Add(90); // 外币
            colW.Add(90); colW.Add(90);     // 借 / 贷
            var captions = new List<string> { "录", "摘要", "科目", "科目名" };
            if (showAmount) captions.Add("金额");
            if (showFormoney) captions.Add("外币");
            captions.Add("借方"); captions.Add("贷方");
            DrawRow(canvas, x, y, colW, captions, bold: true);
            y += RowH;

            foreach (var l in pageLines)
            {
                var cells = new List<string>
                {
                    l.Recoid == 0 ? "" : l.Recoid.ToString(),
                    l.Note ?? "",
                    l.Subcode ?? "",
                    l.Subname ?? "",
                };
                if (showAmount) cells.Add(l.Debit + l.Credit == 0 ? "" : (l.Debit + l.Credit).ToString("N2"));
                if (showFormoney) cells.Add(""); // 本部署无外币列数据
                cells.Add(l.Debit == 0 ? "" : l.Debit.ToString("N2"));
                cells.Add(l.Credit == 0 ? "" : l.Credit.ToString("N2"));
                DrawRow(canvas, x, y, colW, cells, bold: false);
                y += RowH;
            }

            // 合计行（借/贷合计）
            var totals = new List<string> { "", "合  计", "", "" };
            if (showAmount) totals.Add("");
            if (showFormoney) totals.Add("");
            totals.Add(td.ToString("N2")); totals.Add(tc.ToString("N2"));
            DrawRow(canvas, x, y, colW, totals, bold: true);
            y += RowH;
            DrawText(canvas, td == tc ? "（平）" : "（不平！）", x, y, 11, FontWeights.Bold, TextAlignment.Left);
            y += 16;
            // 表尾（制单/审核/过帐 三签）
            DrawText(canvas, $"制单：{h.Maker}　审核：{h.Auditer}　过帐：{h.Poster}", x, y, 11,
                FontWeights.Normal, TextAlignment.Left);

            var fixedPage = new FixedPage { Width = PageW, Height = PageH };
            fixedPage.Children.Add(canvas);
            doc.Pages.Add(new PageContent { Child = fixedPage });
        }
        return doc;
    }

    private static void DrawRow(Canvas c, double x, double y, IReadOnlyList<double> colW,
        IReadOnlyList<string> cells, bool bold)
    {
        double cx = x;
        for (var i = 0; i < colW.Count; i++)
        {
            var border = new Border
            {
                Width = colW[i], Height = RowH,
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(0.5),
                Background = Brushes.Transparent,
            };
            Canvas.SetLeft(border, cx);
            Canvas.SetTop(border, y);
            c.Children.Add(border);
            var tb = new TextBlock
            {
                Text = i < cells.Count ? cells[i] : "",
                FontSize = 10.5,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontFamily = new FontFamily("宋体"),
                Width = colW[i] - 4,
                Height = RowH,
                TextAlignment = i is 5 or 6 ? TextAlignment.Right : TextAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
            };
            Canvas.SetLeft(tb, cx);
            Canvas.SetTop(tb, y);
            c.Children.Add(tb);
            cx += colW[i];
        }
    }

    private static void DrawText(Canvas c, string text, double x, double y, double size,
        FontWeight weight, TextAlignment align)
    {
        if (string.IsNullOrEmpty(text)) return;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            FontFamily = new FontFamily("宋体"),
            TextAlignment = align,
        };
        if (align == TextAlignment.Right)
        {
            tb.Width = 300;
            Canvas.SetLeft(tb, x - 300);
        }
        else if (align == TextAlignment.Center)
        {
            tb.Width = PageW - Margin * 2;
            Canvas.SetLeft(tb, Margin);
        }
        else
        {
            Canvas.SetLeft(tb, x);
        }
        Canvas.SetTop(tb, y);
        c.Children.Add(tb);
    }
}
