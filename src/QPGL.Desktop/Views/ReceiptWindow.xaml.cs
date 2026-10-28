using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Receipt;

namespace QPGL.Desktop.Views;

/// <summary>
/// 收款确认窗（G2 收款 / 旧 fr_sk 复刻）。
/// 打开时按单据取汇总与付款方式清单（tbsysset type='guno'），确认调用 ReceiptRepository.ConfirmAsync。
/// </summary>
public partial class ReceiptWindow : Window
{
    public ReceiptViewModel ViewModel { get; }

    public ReceiptWindow(string paper, string type, string dpm)
    {
        ViewModel = new ReceiptViewModel(paper, type, dpm);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.OnClosingRequested += Close;
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}

/// <summary>收款窗 VM（INPC，窗内封闭使用）。</summary>
public sealed class ReceiptViewModel : INotifyPropertyChanged
{
    private readonly string _dpm;
    private bool _alreadyConfirmed;

    public ReceiptViewModel(string paper, string type, string dpm)
    {
        Paper = paper;
        Type = type;
        _dpm = dpm;
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
        CancelCommand = new RelayCommand(() => OnClosingRequested?.Invoke());
    }

    public ObservableCollection<string> PayTerms { get; } = [];

    public IAsyncRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public string Paper { get; }
    public string Type { get; }

    private decimal _totalOprc;
    private decimal _totalOprct;
    private string _gname = "";
    private DateTime _outdate;
    private string _markText = "";
    private string _selectedPayTerm = "";
    private string _payNo = "";
    private string _remark = "";
    private decimal _amount;
    private string _message = "";

    public decimal TotalOprc { get => _totalOprc; private set { _totalOprc = value; On(); } }
    public decimal TotalOprct { get => _totalOprct; private set { _totalOprct = value; On(); } }
    public string Gname { get => _gname; private set { _gname = value; On(); } }
    public DateTime Outdate { get => _outdate; private set { _outdate = value; On(); } }
    public string MarkText { get => _markText; private set { _markText = value; On(); } }
    public string SelectedPayTerm { get => _selectedPayTerm; set { _selectedPayTerm = value; On(); } }
    public string PayNo { get => _payNo; set { _payNo = value; On(); } }
    public string Remark { get => _remark; set { _remark = value; On(); } }
    public decimal Amount { get => _amount; set { _amount = value; On(); } }
    public string Message { get => _message; private set { _message = value; On(); } }
    public bool AlreadyConfirmed => _alreadyConfirmed;

    public bool CanConfirm => !_alreadyConfirmed;

    public async System.Threading.Tasks.Task LoadAsync()
    {
        try
        {
            var summary = await Db.DocQuery.GetOutDocSummaryAsync(Paper, Type, _dpm);
            Gname = summary.Gname;
            Outdate = summary.Outdate;
            TotalOprc = summary.TotalOprc;
            TotalOprct = summary.TotalOprct;
            var confirmed = summary.Mark.Trim() == "Y";
            MarkText = confirmed ? $"已收款（{summary.Guno}）" : "未收款";
            Amount = summary.TotalOprct; // 旧 TAMT=oprct 默认实收
            _alreadyConfirmed = confirmed;
            On(nameof(CanConfirm));

            PayTerms.Clear();
            foreach (var m in await Db.Receipts.GetPaymentMethodsAsync())
                PayTerms.Add(m.Name);
            SelectedPayTerm = confirmed && PayTerms.Contains(summary.Guno)
                ? summary.Guno
                : PayTerms.FirstOrDefault() ?? "";
            On(nameof(PayTerms));

            if (confirmed)
                Message = "该单已做收款，不能重复确认";
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "加载失败：" + ex.Message;
        }
    }

    public async System.Threading.Tasks.Task ConfirmAsync()
    {
        if (_alreadyConfirmed) return;
        Message = "";
        if (string.IsNullOrWhiteSpace(SelectedPayTerm))
        {
            Message = "请选择付款方式";
            return;
        }
        try
        {
            var result = await Db.Receipts.ConfirmAsync(new ConfirmReceiptCommand
            {
                Paper = Paper,
                Type = Type,
                Dept = _dpm,
                PayTerm = SelectedPayTerm,
                Amount = Amount,
                PayNo = PayNo,
                Remark = Remark,
            }, AppSession.Operator);
            _alreadyConfirmed = true;
            On(nameof(CanConfirm));
            MessageBox.Show(
                $"收款成功：{result.Paper} 实收 {result.Amount:N2}" +
                (result.OnCredit ? $"（挂账应收已入 tbysyf{(result.DiffAmount != 0 ? $"，差额 {result.DiffAmount:N2}" : "")}）" : ""),
                "收款确认", MessageBoxButton.OK, MessageBoxImage.Information);
            OnClosingRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "收款失败：" + ex.Message;
        }
    }

    public event Action? OnClosingRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
