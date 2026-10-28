using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Views;

/// <summary>
/// fr_sort 复刻：对结果集多字段排序（≤5 字段、升/降序单选）。
/// 旧语义：对本地游标 INDE on 键串 SET ORDER [DESCENDING]，纯客户端不落库。
/// </summary>
public partial class SortDialogWindow : Window
{
    private readonly List<QueryColumnSpec> _candidates;

    /// <summary>确认输出的排序键（确认时有效）。</summary>
    public IReadOnlyList<(QueryColumnSpec Spec, bool Descending)>? ResultKeys { get; private set; }

    /// <param name="candidates">当前页签列规格（显示名对齐 tbstru.EXPLAIN）。</param>
    public SortDialogWindow(IReadOnlyList<QueryColumnSpec> candidates)
    {
        InitializeComponent();
        _candidates = candidates.Select(c => c.Clone()).ToList();
        foreach (var c in _candidates) Lst1.Items.Add(c);
        Lst1.DisplayMemberPath = nameof(QueryColumnSpec.Caption);
        Lst2.DisplayMemberPath = nameof(QueryColumnSpec.Caption);
        if (Lst1.Items.Count > 0) Lst1.SelectedIndex = 0;
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (Lst2.Items.Count > 5)
        {
            MessageBox.Show("已选择排序的字段太多！", "提示");
            return;
        }
        if (Lst1.SelectedIndex < 0) return;
        Lst2.Items.Add(Lst1.SelectedItem);
        Lst1.Items.RemoveAt(Lst1.SelectedIndex);
        if (Lst1.Items.Count > 0) Lst1.SelectedIndex = 0;
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Lst2.SelectedIndex < 0) return;
        Lst1.Items.Add(Lst2.SelectedItem);
        Lst2.Items.RemoveAt(Lst2.SelectedIndex);
    }

    private void BtnAddAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Lst1.Items.Cast<QueryColumnSpec>().ToList())
        {
            if (Lst2.Items.Count > 5) { MessageBox.Show("已选择排序的字段太多！", "提示"); break; }
            Lst2.Items.Add(item);
            Lst1.Items.Remove(item);
        }
    }

    private void BtnRemoveAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Lst2.Items.Cast<QueryColumnSpec>().ToList())
        {
            Lst1.Items.Add(item);
            Lst2.Items.Remove(item);
        }
    }

    private void Lst1_DoubleClick(object sender, MouseButtonEventArgs e) => BtnAdd_Click(sender, e);
    private void Lst2_DoubleClick(object sender, MouseButtonEventArgs e) => BtnRemove_Click(sender, e);

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (Lst2.Items.Count == 0)
        {
            MessageBox.Show("请选择要排序的数据！", "提示");
            return;
        }
        var desc = OptAsc.IsChecked != true;
        ResultKeys = Lst2.Items.Cast<QueryColumnSpec>().Select(s => (s, desc)).ToList();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
