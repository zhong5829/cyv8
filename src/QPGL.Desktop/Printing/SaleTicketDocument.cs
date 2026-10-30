using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using QPGL.Domain.Printing;

namespace QPGL.Desktop.Printing;

/// <summary>
/// 销货/退货清单票样（对照旧系统打印模板截图复刻）：
/// 公司抬头（居中）→ 公司信息行（电话/传真/地址）→ 系统信息区（开户银行/账号/税号 + 开票日期/单号）→
/// 收货单位区（带框）→ 明细表（带框：序/零件编码/名称/车型/出库数/售价/金额/仓位）→
/// 营业员/制单人 + 合计 → 付款方式/发货方式 + 大写 + 合计 → 票尾声明 → 备注。
/// </summary>
public static class SaleTicketDocument
{
    private static readonly Brush Ink = Brushes.Black;

    public static FixedDocument Build(SalePrintDoc doc)
    {
        var page = new FixedPage
        {
            Width = 760,
            Height = 640,
            Background = Brushes.White,
        };

        double y = 5;
        var title = doc.Discount < 0 || HasNegativeLine(doc) ? "退货清单" : "销货清单";
        page.Children.Add(Title(doc.CompanyName + title, 20, FontWeights.Bold));
        y += 27;

        // 公司信息行（紧贴抬头）：电话/传真/地址 + 开户银行/帐号（旧图两行，公司信息始终显示）
        AddCentered(page, $"电话：{doc.Tel}  传真：{doc.Fax}  地址：{doc.Address}", 11, ref y);
        AddCentered(page, $"开户银行：{doc.Bank}    帐号：{doc.Account}", 11, ref y);
        y += 2;

        // 系统信息区（旧图：开户银行：xx  帐号：xx  税号：xx / 开票日期、单号；pno='Y' 才显示银行/账号/税号）
        if (doc.PrintCompanyBank)
        {
            AddCentered(page, $"开户银行：{doc.Bank}  帐号：{doc.Account}  税号：{doc.TaxNo}", 11, ref y);
            AddCentered(page, $"开票日期：{doc.OutDate:yyyy-MM-dd}  单号：{doc.Paper}", 11, ref y);
        }
        y += 2;

        // 收货单位区（带框，三列三行：左=收货单位/地址，中=日期/电话，右=单号/打印时间；右列止于 730 不出血）
        var boxTop = y;
        AddBoxText(page, $"收货单位: {Trunc(doc.CustomerName, 18)}", 15, y, 260, x: 26);
        AddBoxText(page, $"{(title == "退货清单" ? "退货" : "出库")}日期: {doc.OutDate:yyyy-MM-dd}", 15, y, 230, x: 295);
        AddBoxText(page, $"单号: {doc.PaperSuffix}", 15, y, 150, x: 545);
        y += 21;
        AddBoxText(page, $"地    址: {Trunc(doc.CustomerAddress, 18)}", 15, y, 260, x: 26);
        AddBoxText(page, $"电话: {doc.CustomerTel}", 15, y, 230, x: 295);
        AddBoxText(page, $"打印时间: {doc.PrintTime:yyyy-MM-dd HH:mm}", 15, y, 185, x: 545);
        y += 21;
        // 收货区外框
        page.Children.Add(new Rectangle
        {
            Width = 724,
            Height = y - boxTop,
            Stroke = Ink,
            StrokeThickness = 1,
        });
        FixedPage.SetLeft(page.Children[^1], 18);
        FixedPage.SetTop(page.Children[^1], boxTop);
        y += 2;

        // 明细表（带框）：序号 | 零件编码 | 零件名 | 车型 | 出库数 | 售价 | 金额 | 仓位
        double[] widths = { 34, 130, 175, 120, 60, 60, 75, 60 };   // 合计 714
        string[] headers = { "序号", "零件编码", "零件名", "车型", "出库数", "售价", "金额", "仓位" };
        var tableTop = y;   // 表头+明细行区域顶（外框/竖线起点）
        DrawTableHeader(page, widths, headers, ref y);

        foreach (var line in doc.Lines)
        {
            DrawTableRow(page, widths, new[]
            {
                line.Seq.ToString(),
                Trunc(line.Nno, 15),
                Trunc(line.Na1, 17),
                Trunc(line.Ty, 11),
                line.Qty.ToString("0.##") + line.Unit,
                line.Price.ToString("N2"),
                line.Amount.ToString("N2"),
                line.Posi,
            }, ref y, boldQty: true);
        }
        // 表格外框（表头到最后一行）
        page.Children.Add(new Rectangle
        {
            Width = 724,
            Height = y - tableTop,
            Stroke = Ink,
            StrokeThickness = 1,
        });
        FixedPage.SetLeft(page.Children[^1], 18);
        FixedPage.SetTop(page.Children[^1], tableTop);

        // 列间竖线（表头+明细行区域，各列分隔）
        double vx = 22;
        for (var i = 0; i < widths.Length - 1; i++)
        {
            vx += widths[i];
            page.Children.Add(new Rectangle { Width = 0.8, Height = y - tableTop, Fill = Ink });
            FixedPage.SetLeft(page.Children[^1], vx);
            FixedPage.SetTop(page.Children[^1], tableTop);
        }

        // 营业员/制单 + 付款/发货方式 + 大写 + 合计（单行）
        y += 6;
        AddBoxText(page, $"营业员: {doc.Operator}   制单: {doc.DocMaker}   【付款方式: {doc.PayMode}】【发货方式: 自提】", 13, y, 400, x: 26);
        AddBoxText(page, $"人民币大写:  {doc.AmountUpper}", 13, y, 200, x: 430);
        AddBoxText(page, $"合 计: ¥ {doc.FinalAmount:N2}", 13, y, 105, x: 634);
        y += 20;

        // 票尾声明
        AddText(page, doc.Footer, 10, ref y, wrap: true);

        // 票尾备注（单据备注内容；旧图票尾"备注："）
        y += 2;
        AddBoxText(page, $"备注：{doc.Remark}", 18, y, 700);

        var fixedDoc = new FixedDocument();
        var pageContent = new PageContent { Child = page };
        fixedDoc.Pages.Add(pageContent);
        return fixedDoc;
    }

    private static bool HasNegativeLine(SalePrintDoc doc) => doc.Lines.Any(l => l.Qty < 0);

    // ---- 渲染原语 ----

    private static TextBlock Title(string text, double size, FontWeight weight)
        => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = Ink,
            // FixedPage 子元素无自动布局：给固定宽度 + TextAlignment.Center 才能居中
            Width = 720,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei"),
        };

    private static void AddCentered(FixedPage page, string text, double size, ref double y)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = size, Foreground = Ink,
            Width = 720, TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei"),
        };
        FixedPage.SetTop(tb, y);
        FixedPage.SetLeft(tb, 20);
        page.Children.Add(tb);
        y += size + 3;
    }

    private static void AddText(FixedPage page, string text, double size, ref double y,
        bool bold = false, bool right = false, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = Ink,
            FontFamily = new FontFamily("Microsoft YaHei"),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Width = 720,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        };
        FixedPage.SetTop(tb, y);
        FixedPage.SetLeft(tb, 20);
        page.Children.Add(tb);
        y += wrap ? EstimateWrappedHeight(text, size) : size + 6;
    }

    /// <summary>绝对定位单元格文本（列式排版用）：x 显式指定列起点，避免缺省左位导致列间重叠。</summary>
    private static void AddBoxText(FixedPage page, string text, double size, double y, double width,
        double? x = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = Ink,
            FontFamily = new FontFamily("Microsoft YaHei"),
            Width = width,
            TextAlignment = TextAlignment.Left,
        };
        FixedPage.SetTop(tb, y);
        FixedPage.SetLeft(tb, x ?? 24);
        page.Children.Add(tb);
    }

    private static void DrawTableHeader(FixedPage page, double[] widths, string[] headers, ref double y)
    {
        double x = 22;
        for (var i = 0; i < widths.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = headers[i],
                FontSize = i == 2 || i == 4 ? 16 : 14,   // 零件名/出库数 列加大
                FontWeight = FontWeights.Bold,
                Foreground = Ink,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Width = widths[i],
                TextAlignment = i >= 4 ? TextAlignment.Center : TextAlignment.Left,
            };
            FixedPage.SetTop(tb, y + 3);
            FixedPage.SetLeft(tb, x);
            page.Children.Add(tb);
            x += widths[i];
        }
        y += 25;
        // 表头底线
        page.Children.Add(new Rectangle { Width = 714, Height = 0.8, Fill = Ink });
        FixedPage.SetLeft(page.Children[^1], 22);
        FixedPage.SetTop(page.Children[^1], y);
        y += 3;
    }

    private static void DrawTableRow(FixedPage page, double[] widths, string[] cells, ref double y,
        bool boldQty = false)
    {
        double x = 22;
        for (var i = 0; i < widths.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = cells[i],
                FontSize = i == 2 || i == 4 ? 16 : 14,   // 零件名/出库数 列加大
                Foreground = Ink,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Width = widths[i],
                TextAlignment = i >= 4 ? TextAlignment.Center : TextAlignment.Left,
                FontWeight = i == 2 || (boldQty && i == 4) ? FontWeights.Bold : FontWeights.Normal,
            };
            FixedPage.SetTop(tb, y + 3);
            FixedPage.SetLeft(tb, x);
            page.Children.Add(tb);
            x += widths[i];
        }
        y += 25;
        // 行分隔线
        page.Children.Add(new Rectangle { Width = 714, Height = 0.5, Fill = Ink });
        FixedPage.SetLeft(page.Children[^1], 22);
        FixedPage.SetTop(page.Children[^1], y);
    }

    private static double EstimateWrappedHeight(string text, double size)
    {
        var charsPerLine = (int)(700 / size);
        var lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)charsPerLine));
        return lines * (size + 2);
    }

    private static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];
}
