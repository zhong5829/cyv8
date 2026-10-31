using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace QPGL.Desktop.Views;

/// <summary>
/// 配件图片大图查看窗（2026-10-31 需求）：开单页选中配件有图（tbImage base64）时双击缩略图打开。
/// 支持复制图片到剪贴板（按钮 / Ctrl+C，剪贴板被占重试不闪退）、Esc / 双击大图关闭。
/// </summary>
public partial class PartImageViewerWindow : Window
{
    private readonly BitmapImage _bitmap;

    public PartImageViewerWindow(string nno, byte[] imageBytes)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        Title = $"配件图片 - {nno}";

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(imageBytes);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        _bitmap = bmp;
        ImgMain.Source = bmp;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C &&
                     Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopyToClipboard();
                e.Handled = true;
            }
        };
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ImgMain_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Close();

    /// <summary>复制位图到剪贴板：被占用时快速重试（30ms×10），失败仅提示不闪退。</summary>
    private void CopyToClipboard()
    {
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
