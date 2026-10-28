using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Printing;

/// <summary>列表打印请求（rptlib.ReportMain 契约）。</summary>
public sealed class ListPrintRequest
{
    /// <summary>报表标题（re_seissm"进库单查询报表"/fr_total"数据汇总结果"等）。</summary>
    public string Caption { get; init; } = "";

    /// <summary>查询条件文本（页眉"查询条件【...】"，P_FIND 去换行）。</summary>
    public string FindText { get; init; } = "";

    /// <summary>编制单位（tbsysset '公司名称'，P_COMPANY）。</summary>
    public string Company { get; init; } = "";

    /// <summary>CPRTITL（fr_total 传"数据汇总结果"，E 组直打为空）。</summary>
    public string CprTitle { get; init; } = "";

    /// <summary>操作员（p_per）。</summary>
    public string Operator { get; init; } = "";

    public IReadOnlyList<QueryColumnSpec> Columns { get; init; } = [];

    public IReadOnlyList<QueryRow> Rows { get; init; } = [];

    /// <summary>行号列（fr_total Grd1 首列"行号"；E 组直打无）。</summary>
    public bool ShowRowNumber { get; init; }
}

/// <summary>
/// 通用列表打印器（§8.3 打印链，对齐旧 rptlib 动态报表 createtempfrx 布局）：
/// 标题(14pt bold) + 查询条件【..】(仅首页) + 编制单位：公司+CPRTITL + 列头(加粗+竖线+外框)
/// + 明细行 + 尾行"操作员：p_per / 打印日期：..."；PageFooter cFooter E 组不用省略。
/// 数据全部来自客户端结果集（旧 PG_PRINT 打印当前游标，无 SQL，不回放）。
/// </summary>
public static class ListPrintDocument
{
    private const double PageW = 1122; // A4 横向 px (96dpi)
    private const double PageH = 793;
    private const double Margin = 24;
    private const double RowH = 18;

    /// <summary>构建 FixedDocument（预览/打印共用）。</summary>
    public static FixedDocument Build(ListPrintRequest req)
    {
        var cols = req.Columns.Where(c => c.Width > 0).ToList();
        var rowNumW = req.ShowRowNumber ? 40 : 0;
        var totalW = cols.Sum(c => c.Width) + rowNumW;
        // 列总宽超可打印区等比压缩（旧 CHECKWIDTH 仅提示"页宽太大"，此处等比缩放保可用）
        var avail = PageW - Margin * 2;
        var scale = totalW > avail ? avail / totalW : 1.0;

        var doc = new FixedDocument();
        var rowIndex = 0;
        var canvas = NewCanvas();
        double y = Margin;

        double ColX(int i) => Margin + rowNumW * scale + cols.Take(i).Sum(c => c.Width * scale);

        // ---------- 首页页眉 ----------
        y = DrawHeader(canvas, req, cols, y, ColX, rowNumW, scale, withFindText: true);
        var bodyTop = y;
        var pageBottom = PageH - 60;

        // ---------- 明细 ----------
        foreach (var row in req.Rows)
        {
            if (y + RowH > pageBottom)
            {
                DrawBottomLine(canvas, Margin, y + 2, totalW * scale);
                DrawVLines(canvas, req, ColX, bodyTop, y + 2, cols, scale);
                doc.Pages.Add(Assemble(canvas));
                canvas = NewCanvas();
                y = DrawHeader(canvas, req, cols, Margin, ColX, rowNumW, scale, withFindText: false);
                bodyTop = y;
            }
            if (req.ShowRowNumber)
                DrawCellText(canvas, (rowIndex + 1).ToString(), Margin, y, rowNumW * scale, RowH, 10.5,
                    FontWeights.Normal, TextAlignment.Center);
            for (var i = 0; i < cols.Count; i++)
            {
                var text = FormatValue(row.Get(cols[i].Field), cols[i]);
                DrawCellText(canvas, text, ColX(i), y, cols[i].Width * scale, RowH, 10.5,
                    FontWeights.Normal, cols[i].Type == 'N' ? TextAlignment.Right : TextAlignment.Left);
            }
            y += RowH;
            rowIndex++;
        }

        // ---------- 表体底线 + Summary ----------
        DrawBottomLine(canvas, Margin, y + 2, totalW * scale);
        DrawVLines(canvas, req, ColX, bodyTop, y + 2, cols, scale);
        y += 16;
        DrawText(canvas, "操作员：" + req.Operator, Margin + 12, y, 11, FontWeights.Normal);
        DrawTextRight(canvas, "打印日期：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Margin + 12, y, 11, PageW - Margin * 2 - 24);
        doc.Pages.Add(Assemble(canvas));
        return doc;
    }

    private static double DrawHeader(Canvas c, ListPrintRequest req, List<QueryColumnSpec> cols, double y,
        Func<int, double> colX, double rowNumW, double scale, bool withFindText)
    {
        var companyLine = "编制单位：" + req.Company + (string.IsNullOrEmpty(req.CprTitle) ? "" : "     " + req.CprTitle);
        DrawText(c, req.Caption, Margin, y, 16, FontWeights.Bold);
        y += 26;
        if (withFindText)
        {
            var find = "查询条件【" + (req.FindText ?? "").Replace("\r", "").Replace("\n", "") + "】";
            DrawText(c, find, Margin + 12, y, 11, FontWeights.Normal);
            y += 18;
            DrawText(c, companyLine, Margin + 12, y, 11, FontWeights.Normal);
            y += 20;
        }
        else
        {
            DrawText(c, companyLine, Margin + 12, y, 11, FontWeights.Normal);
            y += 20;
        }
        // 列头（加粗 + 竖线 + 底线）
        var headTop = y;
        if (req.ShowRowNumber)
            DrawCellText(c, "行号", Margin, headTop, rowNumW * scale, RowH, 11, FontWeights.Bold, TextAlignment.Center);
        for (var i = 0; i < cols.Count; i++)
            DrawCellText(c, cols[i].Caption, colX(i), headTop, cols[i].Width * scale, RowH, 11,
                FontWeights.Bold, cols[i].Type == 'N' ? TextAlignment.Right : TextAlignment.Left);
        y = headTop + RowH + 8;
        DrawHLine(c, Margin, y, cols.Sum(cc => cc.Width) * scale + rowNumW * scale);
        return y + 2;
    }

    private static string FormatValue(object? v, QueryColumnSpec spec)
    {
        if (v is null) return "";
        if (v is DateTime d)
            return spec.Type == 'T' ? d.ToString("yyyy-MM-dd HH:mm") : d.ToString("yyyy-MM-dd");
        if (v is bool b) return b ? ".T." : ".F.";
        if (v is decimal m)
            return spec.Format is { Length: > 0 } && spec.Format.Contains("N0")
                ? m.ToString("N0")
                : m.ToString("0.####");
        return v.ToString() ?? "";
    }

    private static Canvas NewCanvas() => new();

    private static PageContent Assemble(Canvas canvas)
    {
        var fixedPage = new FixedPage { Width = PageW, Height = PageH };
        fixedPage.Children.Add(canvas);
        return new PageContent { Child = fixedPage };
    }

    private static void DrawText(Canvas c, string text, double x, double y, double size, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text)) return;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            FontFamily = new FontFamily("宋体"),
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        c.Children.Add(tb);
    }

    private static void DrawTextRight(Canvas c, string text, double x, double y, double size, double width)
    {
        if (string.IsNullOrEmpty(text)) return;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = new FontFamily("宋体"),
            Width = width,
            TextAlignment = TextAlignment.Right,
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        c.Children.Add(tb);
    }

    private static void DrawCellText(Canvas c, string text, double x, double y, double w, double h, double size,
        FontWeight weight, TextAlignment align)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            FontFamily = new FontFamily("宋体"),
            Width = Math.Max(w - 4, 4),
            Height = h,
            TextAlignment = align,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Canvas.SetLeft(tb, x + 2);
        Canvas.SetTop(tb, y + Math.Max((h - size * 1.35) / 2, 0));
        c.Children.Add(tb);
    }

    private static void DrawVLines(Canvas c, ListPrintRequest req, Func<int, double> colX, double top, double bottom,
        IReadOnlyList<QueryColumnSpec> cols, double scale)
    {
        if (req.ShowRowNumber) DrawVLine(c, Margin, top, bottom);
        for (var i = 0; i <= cols.Count; i++)
            DrawVLine(c, colX(i), top, bottom);
    }

    private static void DrawHLine(Canvas c, double x, double y, double w) =>
        c.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x, Y1 = y, X2 = x + w, Y2 = y,
            Stroke = Brushes.Black, StrokeThickness = 1,
        });

    private static void DrawBottomLine(Canvas c, double x, double y, double w) => DrawHLine(c, x, y, w);

    private static void DrawVLine(Canvas c, double x, double top, double bottom) =>
        c.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x, Y1 = top, X2 = x, Y2 = bottom,
            Stroke = Brushes.Black, StrokeThickness = 1,
        });

    /// <summary>打印（选打印机，A4 横向）。</summary>
    public static void Print(FixedDocument doc, string title)
    {
        var dialog = new PrintDialog
        {
            PrintTicket = new PrintTicket
            {
                PageOrientation = PageOrientation.Landscape,
                PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4),
            },
        };
        if (dialog.ShowDialog() != true) return;
        PrintService.FitToPrintableArea(dialog, doc);
        dialog.PrintDocument(doc.DocumentPaginator, title);
    }
}
