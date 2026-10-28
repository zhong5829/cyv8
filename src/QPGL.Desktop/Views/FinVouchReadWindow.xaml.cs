using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

/// <summary>
/// 读凭证窗（fr_finrevouch 复刻）：按 vouchid 读取 finvouch 分录 + 表头，
/// 上一张/下一张按凭证清单顺序切换（Init/Prio/Next），借贷合计显示（Refresh L42-46）。
/// </summary>
namespace QPGL.Desktop.Views;

public partial class FinVouchReadWindow : Window, INotifyPropertyChanged
{
    private readonly IReadOnlyList<(int Vouchid, int Vcode, DateTime Date, string Maker, string Auditer, string Poster)> _vouches;
    private int _index;

    public FinVouchReadWindow(int startVouchid,
        IReadOnlyList<(int Vouchid, int Vcode, DateTime Date, string Maker, string Auditer, string Poster)> vouches)
    {
        InitializeComponent();
        _vouches = vouches;
        _index = Math.Max(0, vouches.ToList().FindIndex(v => v.Vouchid == startVouchid));
        DataContext = this;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (Lines.Count == 0) return;
        try
        {
            var vouchid = _vouches.Count > 0 ? _vouches[_index].Vouchid : 0;
            var head = await Db.FinOp.ReadVouchAsync(vouchid);
            if (head is null || head.Lines.Count == 0) return;
            var vprn = await Db.FinOp.GetFinVprnAsync();
            new FinVouchPrintWindow(head, head.Lines, vprn) { Owner = this }.ShowDialog();
        }
        catch (AppException ex) { MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public ObservableCollection<FinVouchRow> Lines { get; } = [];

    private string _header = "";
    public string Header { get => _header; private set { _header = value; On(); } }

    private string _vouword = "";
    public string Vouword { get => _vouword; private set { _vouword = value; On(); } }

    private string _totals = "";
    public string Totals { get => _totals; private set { _totals = value; On(); } }

    public bool HasPrio => _index > 0;
    public bool HasNext => _index < _vouches.Count - 1;

    private async void OnPrio(object sender, RoutedEventArgs e)
    {
        if (!HasPrio) return;
        _index--;
        await LoadAsync();
        On(nameof(HasPrio));
        On(nameof(HasNext));
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (!HasNext) return;
        _index++;
        await LoadAsync();
        On(nameof(HasPrio));
        On(nameof(HasNext));
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        if (_vouches.Count == 0) return;
        var v = _vouches[_index];
        var doc = await Db.FinOp.ReadVouchAsync(v.Vouchid);
        Lines.Clear();
        if (doc is null)
        {
            Header = $"凭证 {v.Vouchid} 无分录";
            Totals = "";
            return;
        }
        foreach (var l in doc.Lines) Lines.Add(l);
        var h = doc.Lines[0];
        Vouword = h.Vouword.Trim() + "字";
        Header = $"第 {h.Vcode:D4} 号　日期 {h.Date:yyyy年MM月dd日}　制单 {h.Maker}　审核 {h.Auditer}　过帐 {h.Poster}" +
                 (doc.Post ? "　[已过帐]" : "　[未过帐]");
        var td = doc.Lines.Sum(l => l.Debit);
        var tc = doc.Lines.Sum(l => l.Credit);
        Totals = $"借方合计 {td:N2}　贷方合计 {tc:N2}" + (td == tc ? "　（平）" : "　（不平！）");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
