using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.Services;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.Views;

/// <summary>
/// 提取未收款选择窗（旧 fr_xlsk_tq 复刻）：
/// 来源 tbysyf 未结条件（Init row17/19），条件行=类型/单号/客户（fr_find 简化），
/// 确认/双击/回车返回选中行（__.row4 row11 M_.row3），取消返回 null。不写库。
/// </summary>
public partial class UnsettledPickWindow : Window
{
    private readonly string _type;
    private readonly ObservableCollection<XlskTqRow> _all = [];

    public UnsettledPickWindow(string type)
    {
        InitializeComponent();
        _type = type;
        Title = $"选择未{(type == "收款" ? "收" : "付")}业务单";
        TypeBox.Items.Clear();
        foreach (var t in new[] { "全部", "销售", "维修", "其他" }) TypeBox.Items.Add(t);
        TypeBox.SelectedIndex = 0;
        DataContext = this;
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>选中行（确定/双击）；取消 null。</summary>
    public XlskTqRow? Selected { get; private set; }

    private async void OnReload(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async System.Threading.Tasks.Task ReloadAsync()
    {
        var where = "";
        var cno = CnoBox.Text.Trim();
        var gname = GnameBox.Text.Trim();
        if (cno.Length > 0) where += $" and cno like '%{cno.Replace("'", "''")}%'".TrimStart();
        if (gname.Length > 0) where += $" and gname like '%{gname.Replace("'", "''")}%'".TrimStart();
        _all.Clear();
        foreach (var r in await Db.Xlsk.GetUnsettledAsync(_type, where))
            _all.Add(r);
        ApplyTypeFilter();
    }

    private void ApplyTypeFilter()
    {
        var t = TypeBox.SelectedItem as string ?? "全部";
        Grid.ItemsSource = t == "全部" ? _all : new ObservableCollection<XlskTqRow>(
            _all.Where(r => r.Type == t).ToList());
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyTypeFilter();
    private void OnFilterChanged(object sender, TextChangedEventArgs e) { /* 输入中不重查，点查询生效 */ }

    private void OnDblClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Confirm();

    private void OnOk(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (Grid.SelectedItem is not XlskTqRow row)
        {
            MessageBox.Show("请先选择单据行", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Selected = row;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
