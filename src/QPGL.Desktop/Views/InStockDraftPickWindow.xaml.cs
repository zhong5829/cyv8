using System.Windows;
using System.Windows.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.Views;

/// <summary>
/// 未生效暂存单选择窗（2026-11-02 暂存需求，旧 P__.row194 列表语义）：
/// 页面打开时检测到 tbistosm 有本部门暂存单 → 弹出列出 单号/日期/供应商/行数，
/// 选中恢复（确定返回单号），取消则空白页继续。
/// </summary>
public partial class InStockDraftPickWindow : Window
{
    /// <summary>选中的暂存单号（DialogResult=true 时有效；null=用户取消）。</summary>
    public string? SelectedCno { get; private set; }

    public InStockDraftPickWindow(IReadOnlyList<InStockDraftHeader> drafts)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        Grid.ItemsSource = drafts;
        if (drafts.Count > 0) Grid.SelectedIndex = 0;
        Loaded += (_, _) => Grid.Focus();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not InStockDraftHeader h)
        {
            MessageBox.Show("请先选中一张暂存单", "未生效暂存单", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedCno = h.Cno;
        DialogResult = true;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => BtnOk_Click(sender, e);

    /// <summary>右键删除选中暂存单（2026-11-02 需求）：确认后 DELETE tbistosm 该 cno 全部行，并刷新列表。</summary>
    private async void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not InStockDraftHeader h) return;
        var go = MessageBox.Show(
            $"确定删除暂存单 {h.Cno}（{h.Gyname}，{h.LineCount} 行）？\n删除后不可恢复。",
            "删除暂存单", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (go != MessageBoxResult.Yes) return;
        try
        {
            await Db.InStock.DeleteDraftAsync(h.Cno, AppSession.DeptNo);
            var drafts = await Db.InStock.GetDraftsAsync(AppSession.DeptNo);
            Grid.ItemsSource = drafts;
            if (drafts.Count > 0) Grid.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("删除失败：" + ex.Message, "删除暂存单", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
