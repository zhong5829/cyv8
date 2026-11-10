using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace QPGL.Desktop.Views;

/// <summary>
/// 配件图片大图查看窗（2026-10-31 需求）：开单页选中配件有图（tbImage base64）时单击缩略图打开。
/// 2026-11-10 多图修复：同配件可有 2 张以上图片（tbImage 多行），←/→ 按钮（或方向键）翻页、角标显示当前序号。
/// 支持复制图片到剪贴板（按钮 / Ctrl+C，剪贴板被占重试不闪退）、Esc / 双击大图关闭。
/// </summary>
public partial class PartImageViewerWindow : Window
{
    private readonly IReadOnlyList<byte[]> _images;
    private int _index;
    private BitmapImage? _bitmap;   // 当前帧（复制到剪贴板用，翻页时重建）

    public PartImageViewerWindow(string nno, IReadOnlyList<byte[]> images, int startIndex = 0)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        Title = $"配件图片 - {nno}";
        _images = images;
        _index = Math.Clamp(startIndex, 0, images.Count - 1);

        ShowCurrent();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Left && _index > 0) { _index--; ShowCurrent(); e.Handled = true; }
            else if (e.Key == Key.Right && _index < _images.Count - 1) { _index++; ShowCurrent(); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopyToClipboard();
                e.Handled = true;
            }
        };
    }

    /// <summary>显示当前序号图片，并按多图/单图刷新角标与翻页按钮可用性。</summary>
    private void ShowCurrent()
    {
        var bytes = _images[_index];
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        _bitmap = bmp;
        ImgMain.Source = bmp;

        var multi = _images.Count > 1;
        BtnPrev.IsEnabled = multi && _index > 0;
        BtnNext.IsEnabled = multi && _index < _images.Count - 1;
        Badge.Text = multi ? $"{_index + 1}/{_images.Count}" : "";
        Badge.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_index > 0) { _index--; ShowCurrent(); }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_index < _images.Count - 1) { _index++; ShowCurrent(); }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ImgMain_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Close();

    /// <summary>复制位图到剪贴板：被占用时快速重试（30ms×10），失败仅提示不闪退。</summary>
    private void CopyToClipboard()
    {
        if (_bitmap is null) return;
        for (var i = 0; ; i++)
        {
            try
            {
                Clipboard.SetImage(_bitmap);
                MessageBox.Show("图片已复制到剪贴板", "配件图片", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (i < 9)
            {
                System.Threading.Thread.Sleep(30);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复制失败：{ex.Message}", "配件图片", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
    }
}
