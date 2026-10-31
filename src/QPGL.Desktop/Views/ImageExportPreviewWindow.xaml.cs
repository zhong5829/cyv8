using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SD2D = System.Drawing.Drawing2D;
using SDT = System.Drawing.Text;

namespace QPGL.Desktop.Views;

/// <summary>
/// 一张导出图片的表格描述：标题 + 列头 + 行数据 + 红字行 + 需合计列。
/// 调用方自行组装数据（本窗口不依赖 DataGrid，供应收应付等页面复用）。
/// </summary>
public sealed class ImageExportSheet
{
    public string Title { get; set; } = "";

    /// <summary>标题栏下方的副标题（2026-10-31 需求：勾选日期范围 + 共计金额），空则不占位。</summary>
    public string? Subtitle { get; set; }

    public string[] Headers { get; set; } = [];
    public List<string[]> Rows { get; set; } = [];

    /// <summary>整行红字显示的行索引（应收/应付为负，与界面 NumCellRightNeg 习惯一致）。</summary>
    public HashSet<int> RedRows { get; set; } = [];

    /// <summary>需要合计的列头（合计行求和 N2；第 1 列显示「合 计」）。</summary>
    public string[] SumHeaders { get; set; } = [];
}

/// <summary>
/// 图片导出预览窗（移植 qp11 销售明细 ImagePreviewWindow，2026-10-31 需求）：
/// GDI+ 重绘表格为 PNG（非截屏）：钢蓝标题栏 + 表头 + 斑马纹 + 数值列 N2 + 合计行 + 页脚；
/// 超长表每 120 行分批渲染防内存暴涨；左侧缩略图 + 右侧大图；
/// 复制到剪贴板 = FileDrop 文件 + 合并大图（可直接粘贴微信/文档）；双击缩略图复制单张；关闭自动清理临时 PNG。
/// </summary>
public partial class ImageExportPreviewWindow : Window
{
    private readonly List<string> _tempFiles = [];
    private readonly List<BitmapSource> _displayImages = [];
    private readonly List<string> _labels = [];
    private readonly string _customerTitle;
    private const int MaxDisplaySize = 4096;

    /// <summary>文本型列（值可能纯数字但不做 N2 千分位格式化）。</summary>
    private static readonly HashSet<string> TextHeaders = ["单据号", "发票号", "收(付)款单号"];

    public ImageExportPreviewWindow(string customerTitle, string? amountSummary, params ImageExportSheet[] sheets)
    {
        InitializeComponent();
        _customerTitle = customerTitle;
        Title = $"{customerTitle}  图片导出";
        for (var i = 0; i < sheets.Length; i++)
        {
            _labels.Add(sheets[i].Title);
            RenderSheet(sheets[i]);
            if (i == 0)
            {
                btnCopyImg1.Content = "复制" + sheets[0].Title;
                btnCopyImg1.ToolTip = $"复制图片1（{sheets[0].Title}）到剪贴板";
            }
            else if (i == 1)
            {
                btnCopyImg2.Content = "复制" + sheets[1].Title;
                btnCopyImg2.ToolTip = $"复制图片2（{sheets[1].Title}）到剪贴板";
            }
        }
        ShowThumbnails();
        txtCount.Text = _displayImages.Count.ToString();
        txtStatus.Text = $"共 {_displayImages.Count} 张图片，点击图片可查看大图，双击复制";
        txtAmount.Text = amountSummary ?? "";
        Closed += (_, _) => CleanupTempFiles();
    }

    private void RenderSheet(ImageExportSheet sheet)
    {
        if (sheet.Headers.Length == 0 || sheet.Rows.Count == 0) return;
        RenderPage(sheet, CalculateColumnWidths(sheet.Headers, sheet.Rows));
    }

    private static double[] CalculateColumnWidths(string[] headers, List<string[]> rows)
    {
        var widths = new double[headers.Length];
        const string fontName = "Microsoft YaHei";
        const double fontSize = 13.0;

        for (var i = 0; i < headers.Length; i++)
        {
            var maxContentWidth = 0.0;
            foreach (var row in rows.Take(200))
                if (i < row.Length)
                    maxContentWidth = Math.Max(maxContentWidth, MeasureTextWidth(row[i], fontName, fontSize));
            widths[i] = Math.Max(MeasureTextWidth(headers[i], fontName, fontSize), maxContentWidth) + 16;
        }

        const double targetWidth = 1100.0;
        var totalWidth = widths.Sum();
        if (totalWidth < targetWidth)
        {
            var scale = targetWidth / totalWidth;
            for (var i = 0; i < widths.Length; i++) widths[i] *= scale;
        }
        return widths;
    }

    private static double MeasureTextWidth(string text, string fontName, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        using var bmp = new SD.Bitmap(1, 1);
        using var g = SD.Graphics.FromImage(bmp);
        return g.MeasureString(text, new SD.Font(fontName, (float)fontSize)).Width;
    }

    private void RenderPage(ImageExportSheet sheet, double[] colWidths)
    {
        const int rowH = 22, headerH = 28, titleH = 42, footerH = 24, margin = 16;
        var subtitleH = string.IsNullOrEmpty(sheet.Subtitle) ? 0 : 26;
        var tableTop = margin + titleH + subtitleH;   // 表格区起始 Y（标题栏 + 副标题行之下）
        var tableW = (int)colWidths.Sum(w => w);
        var width = tableW + margin * 2;
        var height = titleH + subtitleH + headerH + sheet.Rows.Count * rowH + footerH + margin * 2;

        var tempFile = Path.Combine(Path.GetTempPath(), $"QPGL_Export_{Guid.NewGuid():N}.png");

        using (var fullBmp = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb))
        {
            fullBmp.SetResolution(96, 96);
            using (var g = SD.Graphics.FromImage(fullBmp))
            {
                g.SmoothingMode = SD2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = SDT.TextRenderingHint.AntiAliasGridFit;
                g.Clear(SD.Color.White);

                using (var titleBrush = new SD.SolidBrush(SD.Color.FromArgb(230, 70, 130, 180)))
                    g.FillRectangle(titleBrush, margin, margin, width - margin * 2, titleH - 4);
                using (var titleFont = new SD.Font("Microsoft YaHei", 18, SD.FontStyle.Bold))
                using (var titleBrush2 = new SD.SolidBrush(SD.Color.White))
                {
                    var pageTitle = $"{_customerTitle}  应收应付{sheet.Title}";
                    var titleSize = g.MeasureString(pageTitle, titleFont);
                    g.DrawString(pageTitle, titleFont, titleBrush2, (width - titleSize.Width) / 2, margin + 8);
                }

                // 副标题行（2026-10-31 需求：勾选日期范围 + 共计金额）
                if (!string.IsNullOrEmpty(sheet.Subtitle))
                {
                    using var subFont = new SD.Font("Microsoft YaHei", 11, SD.FontStyle.Bold);
                    using var subBrush = new SD.SolidBrush(SD.Color.FromArgb(60, 60, 60));
                    var subSize = g.MeasureString(sheet.Subtitle, subFont);
                    g.DrawString(sheet.Subtitle, subFont, subBrush, (width - subSize.Width) / 2, margin + titleH + 3);
                }

                var y = tableTop;
                using (var headerBgBrush = new SD.SolidBrush(SD.Color.FromArgb(255, 70, 130, 180)))
                    g.FillRectangle(headerBgBrush, margin, y, tableW, headerH);
                using (var headerFont = new SD.Font("Microsoft YaHei", 11, SD.FontStyle.Bold))
                using (var whiteBrush = new SD.SolidBrush(SD.Color.White))
                {
                    var x = margin;
                    for (var i = 0; i < sheet.Headers.Length; i++)
                    {
                        g.DrawString(sheet.Headers[i], headerFont, whiteBrush, x + 6, y + 6);
                        x += (int)colWidths[i];
                    }
                }

                y += headerH;
                using (var altBrush = new SD.SolidBrush(SD.Color.FromArgb(12, 0, 0, 0)))
                using (var borderPen = new SD.Pen(SD.Color.FromArgb(35, 200, 200, 200), 0.5f))
                {
                    for (var r = 0; r < sheet.Rows.Count; r++)
                    {
                        if (r % 2 == 1) g.FillRectangle(altBrush, margin, y, tableW, rowH);
                        else g.DrawRectangle(borderPen, margin, y, tableW, rowH);
                        y += rowH;
                    }
                }
            }
            fullBmp.Save(tempFile, SD.Imaging.ImageFormat.Png);
        }

        // 分批写单元格文本（每批 120 行，重存 PNG，防一次 GDI+ 大图内存暴涨）
        const int batchSize = 120;
        var totalBatches = (sheet.Rows.Count + batchSize - 1) / batchSize;

        for (var batch = 0; batch < totalBatches; batch++)
        {
            var startRow = batch * batchSize;
            var endRow = Math.Min(startRow + batchSize, sheet.Rows.Count);
            var startY = tableTop + headerH + startRow * rowH;

            using (var bmp = SD.Image.FromFile(tempFile) as SD.Bitmap)
            {
                if (bmp == null) return;
                using (var g = SD.Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = SDT.TextRenderingHint.AntiAliasGridFit;
                    using var cellFont = new SD.Font("Microsoft YaHei", 10);
                    using var blackBrush = new SD.SolidBrush(SD.Color.Black);
                    using var redBrush = new SD.SolidBrush(SD.Color.FromArgb(200, 30, 30));

                    var y = startY;
                    for (var r = startRow; r < endRow; r++)
                    {
                        using (var rowBmp = new SD.Bitmap(tableW, rowH, SD.Imaging.PixelFormat.Format32bppArgb))
                        {
                            rowBmp.SetResolution(96, 96);
                            using (var rg = SD.Graphics.FromImage(rowBmp))
                            {
                                rg.TextRenderingHint = SDT.TextRenderingHint.AntiAliasGridFit;
                                rg.Clear(SD.Color.Transparent);
                                var row = sheet.Rows[r];
                                var fore = sheet.RedRows.Contains(r) ? redBrush : blackBrush;
                                var x = 0;
                                for (var i = 0; i < sheet.Headers.Length; i++)
                                {
                                    var text = i < row.Length ? row[i] : "";
                                    // 数值列统一 N2 千分位（单据号/发票号等文本列除外）
                                    if (!string.IsNullOrEmpty(text) && decimal.TryParse(text, out var numVal)
                                        && !TextHeaders.Contains(sheet.Headers[i]))
                                        text = numVal.ToString("N2");
                                    rg.DrawString(text, cellFont, fore, x + 5, 5);
                                    x += (int)colWidths[i];
                                }
                            }
                            g.DrawImage(rowBmp, margin, y);
                        }
                        y += rowH;
                    }

                    if (batch == totalBatches - 1)
                    {
                        // 合计行（钢蓝底白字）+ 页脚
                        var footerY = tableTop + headerH + sheet.Rows.Count * rowH;
                        using (var sumBgBrush = new SD.SolidBrush(SD.Color.FromArgb(255, 70, 130, 180)))
                            g.FillRectangle(sumBgBrush, margin, footerY, tableW, rowH + 3);
                        using (var sumFont = new SD.Font("Microsoft YaHei", 11, SD.FontStyle.Bold))
                        using (var whiteBrush = new SD.SolidBrush(SD.Color.White))
                        {
                            var x = margin;
                            for (var i = 0; i < sheet.Headers.Length; i++)
                            {
                                string sumText = "";
                                if (sheet.SumHeaders.Contains(sheet.Headers[i]))
                                    sumText = sheet.Rows
                                        .Sum(rr => decimal.TryParse(rr.Length > i ? rr[i] : "", out var v) ? v : 0m)
                                        .ToString("N2");
                                else if (i == 0)
                                    sumText = "合 计";
                                g.DrawString(sumText, sumFont, whiteBrush, x + 5, footerY + 5);
                                x += (int)colWidths[i];
                            }
                        }
                        var lineY = footerY + rowH + 3;
                        using (var linePen = new SD.Pen(SD.Color.LightGray, 1))
                            g.DrawLine(linePen, margin, lineY, margin + tableW, lineY);
                        using (var footerFont = new SD.Font("Microsoft YaHei", 9))
                        using (var grayBrush = new SD.SolidBrush(SD.Color.Gray))
                        {
                            g.DrawString($"共 {sheet.Rows.Count} 条记录", footerFont, grayBrush, margin, lineY + 5);
                            g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), footerFont, grayBrush, width - margin - 90, lineY + 5);
                        }
                    }

                    var newFile = Path.Combine(Path.GetTempPath(), $"QPGL_Export_{Guid.NewGuid():N}.png");
                    bmp.Save(newFile, SD.Imaging.ImageFormat.Png);
                    try { File.Delete(tempFile); } catch { /* 临时文件删除失败忽略 */ }
                    tempFile = newFile;
                }
            }
        }

        _tempFiles.Add(tempFile);

        // 预览用位图（超 4096 缩放，防 WPF 显示端内存爆炸）
        SD.Bitmap displayBmp;
        if (width > MaxDisplaySize || height > MaxDisplaySize)
        {
            var ratio = Math.Min((double)MaxDisplaySize / width, (double)MaxDisplaySize / height);
            var newW = Math.Max(1, (int)(width * ratio));
            var newH = Math.Max(1, (int)(height * ratio));
            displayBmp = new SD.Bitmap(newW, newH, SD.Imaging.PixelFormat.Format32bppArgb);
            displayBmp.SetResolution(96, 96);
            using (var dg = SD.Graphics.FromImage(displayBmp))
            {
                dg.InterpolationMode = SD2D.InterpolationMode.HighQualityBilinear;
                dg.SmoothingMode = SD2D.SmoothingMode.AntiAlias;
                using var src = SD.Image.FromFile(tempFile) as SD.Bitmap;
                if (src != null) dg.DrawImage(src, new SD.Rectangle(0, 0, newW, newH));
            }
        }
        else
        {
            displayBmp = SD.Image.FromFile(tempFile) as SD.Bitmap ?? new SD.Bitmap(width, height);
        }

        using (displayBmp)
        {
            _displayImages.Add(GdiBitmapToWpf(displayBmp));
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static BitmapSource GdiBitmapToWpf(SD.Bitmap bmp)
    {
        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private void ShowThumbnails()
    {
        var items = new List<object>();
        for (var i = 0; i < _displayImages.Count; i++)
            items.Add(new { Image = CreateThumbnail(_displayImages[i], 200, 150), Label = $"{i + 1}.{_labels[i]}" });
        lstThumbs.ItemsSource = items;
        if (_displayImages.Count > 0)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                lstThumbs.SelectedIndex = 0;
                imgPreview.Source = _displayImages[0];
                UpdatePageSizeText(0);
                lstThumbs.ScrollIntoView(lstThumbs.SelectedItem);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static BitmapSource CreateThumbnail(BitmapSource source, int maxWidth, int maxHeight)
    {
        var ratio = Math.Min((double)maxWidth / source.PixelWidth, (double)maxHeight / source.PixelHeight);
        return BitmapFrame.Create(new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(ratio, ratio)));
    }

    private void LstThumbs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = lstThumbs.SelectedIndex;
        if (idx >= 0 && idx < _displayImages.Count)
        {
            imgPreview.Source = _displayImages[idx];
            UpdatePageSizeText(idx);
        }
    }

    private void UpdatePageSizeText(int idx)
    {
        if (idx < 0 || idx >= _tempFiles.Count) return;
        using var bmp = SD.Image.FromFile(_tempFiles[idx]) as SD.Bitmap;
        if (bmp != null) txtPageSize.Text = $"原始尺寸: {bmp.Width} × {bmp.Height}";
    }

    private async void BtnCopyAll_Click(object sender, RoutedEventArgs e)
    {
        btnCopyAll.IsEnabled = false;
        txtStatus.Text = "正在合并图片...";
        try
        {
            await Dispatcher.InvokeAsync(CopyAllImagesToClipboard, System.Windows.Threading.DispatcherPriority.Background);
            txtStatus.Text = "已复制全部图片到剪贴板";
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            txtStatus.Text = "复制失败";
        }
        finally
        {
            btnCopyAll.IsEnabled = true;
        }
    }

    private void CopyAllImagesToClipboard()
    {
        if (_tempFiles.Count == 0) return;

        for (var retry = 0; retry < 10; retry++)
        {
            try
            {
                Clipboard.Clear();
                var dataObj = new DataObject();
                dataObj.SetData(DataFormats.FileDrop, _tempFiles.ToArray());

                var images = new List<SD.Bitmap>();
                try
                {
                    foreach (var f in _tempFiles)
                        if (SD.Image.FromFile(f) is SD.Bitmap bmp) images.Add(bmp);

                    if (images.Count > 0)
                    {
                        // 纵向拼接成一张大图一并放剪贴板（粘贴端只认单图时用）
                        const int gap = 8;
                        var totalWidth = images.Max(b => b.Width);
                        var totalHeight = images.Sum(b => b.Height) + gap * (images.Count - 1);
                        using var combined = new SD.Bitmap(totalWidth, totalHeight, SD.Imaging.PixelFormat.Format32bppArgb);
                        combined.SetResolution(96, 96);
                        using (var g = SD.Graphics.FromImage(combined))
                        {
                            g.Clear(SD.Color.White);
                            var y = 0;
                            foreach (var img in images)
                            {
                                g.DrawImage(img, 0, y, img.Width, img.Height);
                                y += img.Height + gap;
                            }
                        }
                        dataObj.SetImage(GdiBitmapToWpf(combined));
                    }
                }
                finally
                {
                    foreach (var img in images) img.Dispose();
                }

                Clipboard.SetDataObject(dataObj, true);
                return;
            }
            catch { Thread.Sleep(100); }
        }
        throw new Exception("无法访问剪贴板");
    }

    private void CopyImageToClipboard(int index)
    {
        if (index < 0 || index >= _tempFiles.Count) return;
        var filePath = _tempFiles[index];

        for (var retry = 0; retry < 10; retry++)
        {
            try
            {
                Clipboard.Clear();
                var dataObj = new DataObject();
                dataObj.SetData(DataFormats.FileDrop, new[] { filePath });
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    dataObj.SetData("PNG", fs);
                }
                using (var bmp = SD.Image.FromFile(filePath) as SD.Bitmap)
                {
                    if (bmp != null) dataObj.SetImage(GdiBitmapToWpf(bmp));
                }
                Clipboard.SetDataObject(dataObj, true);
                return;
            }
            catch { Thread.Sleep(100); }
        }
        throw new Exception("无法访问剪贴板");
    }

    public void CopySingleImage(int index)
    {
        if (index < 0 || index >= _tempFiles.Count) return;
        try
        {
            CopyImageToClipboard(index);
            txtStatus.Text = $"已复制图片：{_labels[index]}";
            Close();
        }
        catch
        {
            MessageBox.Show(this, "无法访问剪贴板，请稍后重试", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CleanupTempFiles()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* 临时文件清理失败忽略 */ }
        }
    }

    private void BtnCopyImg1_Click(object sender, RoutedEventArgs e)
    {
        if (_tempFiles.Count >= 1) CopySingleImage(0);
        else MessageBox.Show(this, "没有第1张图片", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnCopyImg2_Click(object sender, RoutedEventArgs e)
    {
        if (_tempFiles.Count >= 2) CopySingleImage(1);
        else MessageBox.Show(this, "没有第2张图片", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        CopySingleImage(lstThumbs.SelectedIndex);
    }
}
