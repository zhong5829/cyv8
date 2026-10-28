using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QPGL.Desktop.Services;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.Views;

/// <summary>
/// 提取销售/入库窗（旧 fr_lsz 内嵌 CMDTIQU 面板复刻，谛.row139 + 肱.row133）：
/// 单选 销售/入库 + 日期窗 → 未提取候选（paper 不在 tblsz）→ 勾选 → AddTiquAsync 写 tblsz。
/// </summary>
public partial class LszTiquWindow : Window
{
    public LszTiquViewModel ViewModel { get; }

    public LszTiquWindow(string bankName)
    {
        ViewModel = new LszTiquViewModel(bankName);
        InitializeComponent();
        DataContext = ViewModel;
        DpFrom.SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        DpTo.SelectedDate = DateTime.Today;
        RbSale.Checked += async (_, _) => await ReloadAsync();
        RbIn.Checked += async (_, _) => await ReloadAsync();
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>提取条数（确认成功后）。</summary>
    public int InsertedCount { get; private set; }

    private async void OnReload(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async System.Threading.Tasks.Task ReloadAsync()
    {
        var kind = RbSale.IsChecked == true ? "销售" : "入库";
        var d1 = DpFrom.SelectedDate ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var d2 = DpTo.SelectedDate ?? DateTime.Today;
        await ViewModel.ReloadAsync(kind, d1, d2);
        Grid.ItemsSource = ViewModel.Rows;
    }

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in ViewModel.Rows) r.IsChecked = true;
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var picked = ViewModel.Rows.Where(r => r.IsChecked).ToList();
        if (picked.Count == 0)
        {
            MessageBox.Show("请先勾选要提取的单据", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var kind = RbSale.IsChecked == true ? "销售" : "入库";
        var n = await ViewModel.ConfirmAsync(kind, picked);
        InsertedCount = n;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class LszTiquViewModel(string bankName) : INotifyPropertyChanged
{
    public ObservableCollection<TiquRowVm> Rows { get; } = [];
    public string Message { get; private set; } = "";

    public async Task ReloadAsync(string kind, DateTime d1, DateTime d2)
    {
        Rows.Clear();
        foreach (var r in await Db.Lsz.GetTiquAsync(kind, d1, d2))
            Rows.Add(new TiquRowVm { Outdate = r.Outdate, Paper = r.Paper, Jine = r.Jine, Gname = r.Gname, Guno = r.Guno });
    }

    public async Task<int> ConfirmAsync(string kind, List<TiquRowVm> picked)
    {
        var rows = picked.Select(p => new LszTiquRow
        {
            Outdate = p.Outdate, Paper = p.Paper, Jine = p.Jine, Gname = p.Gname, Guno = p.Guno,
        }).ToList();
        var n = await Db.Lsz.AddTiquAsync(kind, rows, bankName, AppSession.DeptNo);
        Message = $"已提取 {n} 条{(kind == "销售" ? "销售" : "入库")}流水";
        return n;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TiquRowVm : INotifyPropertyChanged
{
    public DateTime Outdate { get; init; }
    public string Paper { get; init; } = "";
    public decimal Jine { get; init; }
    public string Gname { get; init; } = "";
    public string Guno { get; init; } = "";

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; On(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
