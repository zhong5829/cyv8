using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;
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

    /// <summary>双击进入金额编辑（旧 C_GUNO 网格 PAYAMT 列；未编辑时只读）。</summary>
    private void PayAmount_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.CanConfirm) return;
        if (sender is not TextBox tb || tb.DataContext is not PayTermRow row) return;
        row.BeginEdit();
        tb.Focus();
        tb.SelectAll();
        e.Handled = true;
    }

    /// <summary>金额失焦提交（旧 __.row380 LostFocus：压行 + 算减收）。</summary>
    private void PayAmount_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not PayTermRow row) return;
        if (!row.IsEditing) return;
        // TwoWay/LostFocus 绑定先回写 Amount，再在消息循环空闲时结算，避免读到旧值
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (row.IsEditing) ViewModel.CommitRowEdit(row);
        }));
    }

    /// <summary>挂帐支付方输入 → 300ms 防抖联想（沿用入库页供应商联想行为）。</summary>
    private void PayerBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ViewModel.PayerEnabled) return;
        PayerPopup.IsOpen = (ViewModel.Payer ?? "").Trim().Length > 0;
        ViewModel.DebouncePayerSearch();
    }

    /// <summary>联想列表键盘交互：上下移动、回车选中、Esc 关闭。</summary>
    private void PayerBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ViewModel.PayerEnabled || !PayerPopup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                if (PayerList.Items.Count > 0)
                {
                    PayerList.SelectedIndex = Math.Min(PayerList.SelectedIndex + 1, PayerList.Items.Count - 1);
                    PayerList.ScrollIntoView(PayerList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (PayerList.Items.Count > 0 && PayerList.SelectedIndex > 0)
                {
                    PayerList.SelectedIndex--;
                    PayerList.ScrollIntoView(PayerList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (PayerList.SelectedItem is QPGL.Domain.Partners.Partner p)
                {
                    ApplyPayer(p);
                    e.Handled = true;   // 避免触发窗口默认「确定」按钮
                }
                break;
            case Key.Escape:
                PayerPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>失焦收起联想；点击下拉项时延后判断，避免选中前被关掉。</summary>
    private void PayerBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!PayerList.IsMouseOver) PayerPopup.IsOpen = false;
        }));
    }

    private void PayerList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (PayerList.SelectedItem is QPGL.Domain.Partners.Partner p) ApplyPayer(p);
    }

    private void ApplyPayer(QPGL.Domain.Partners.Partner p)
    {
        ViewModel.ApplyPayer(p);
        PayerPopup.IsOpen = false;
    }
}

/// <summary>收款窗 VM（INPC，窗内封闭使用）。</summary>
public sealed class ReceiptViewModel : INotifyPropertyChanged
{
    private readonly string _dpm;
    private bool _alreadyConfirmed;
    private readonly DispatcherTimer _payerDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public ReceiptViewModel(string paper, string type, string dpm)
    {
        Paper = paper;
        Type = type;
        _dpm = dpm;
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
        CancelCommand = new RelayCommand(() => OnClosingRequested?.Invoke());
        _payerDebounce.Tick += async (_, _) =>
        {
            _payerDebounce.Stop();
            await SearchPayerSuggestionsAsync();
            await RefreshCustomerYueAsync();
        };
    }

    public ObservableCollection<string> PayTerms { get; } = [];

    /// <summary>发票号前缀（旧 CBO_PNO：ADDITEM '*','Y','N'，LISTINDEX=1）。</summary>
    public ObservableCollection<string> PnoPrefixOptions { get; } = ["*", "Y", "N"];

    /// <summary>发货方式下拉（字典 tbsysset type='fhfs'，与出库确认同源）。</summary>
    public ObservableCollection<string> TModeOptions { get; } = [];

    /// <summary>收款方式/金额表格行（旧 C_GUNO NAME/PAYAMT）：所有行均可双击编辑。</summary>
    public ObservableCollection<PayTermRow> PayTermRows { get; } = [];

    /// <summary>挂帐支付方查找候选（旧 n_dm：table_source=tbgu / filed_source=gname）。</summary>
    public ObservableCollection<Partner> PayerSuggestions { get; } = [];

    public IAsyncRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public string Paper { get; }
    public string Type { get; }

    /// <summary>窗口标题（截图：收款：    单号：xxx）。</summary>
    public string WindowTitle => $"收款：    单号：{Paper}";

    private decimal _totalOprc;
    private decimal _totalOprct;
    private string _gname = "";
    private DateTime _outdate;
    private string _markText = "";
    private string _selectedPayTerm = "";
    private string _payNo = "";
    private string _pnoPrefix = "*";
    private string _remark = "";
    private decimal _payTotal;
    private decimal _jianshou;
    private decimal _difference;
    private string _message = "";
    private decimal _customerYue;
    private string _tMode = "";
    private string _confirmDate = "";
    private bool _payerEnabled;
    private string _payer = "";

    /// <summary>折后开单金额（旧 TOPRC：Σ ckamount*oprc*(100-yhl)/100，先求和再取 2 位）。</summary>
    public decimal TotalOprc
    {
        get => _totalOprc;
        private set
        {
            _totalOprc = value;
            On();
            On(nameof(DifferenceText));
        }
    }
    /// <summary>折后实售金额（旧 TAMT：Σ ckamount*oprct*(100-yhl)/100，先求和再取 2 位）。</summary>
    public decimal TotalOprct
    {
        get => _totalOprct;
        private set
        {
            _totalOprct = value;
            On();
            On(nameof(DifferenceText));
        }
    }
    public string Gname { get => _gname; private set { _gname = value; On(); } }

    /// <summary>
    /// 系统开关「启用挂账支付方」（tbcskg，旧 CSKG）：开 → 支付方可编辑/查找；关 → 置灰只读占位。
    /// </summary>
    public bool PayerEnabled
    {
        get => _payerEnabled;
        private set
        {
            if (_payerEnabled == value) return;
            _payerEnabled = value;
            On();
            On(nameof(IsPayerReadOnly));
        }
    }
    public bool IsPayerReadOnly => !_payerEnabled;

    /// <summary>
    /// 挂帐支付方（旧 TXTGZGNAME）：手工输入/从 tbgu.gname 查找；无列可回显，确认后重开窗为空。
    /// </summary>
    public string Payer
    {
        get => _payer;
        set
        {
            if (_payer == value) return;
            _payer = value;
            On();
            On(nameof(EffectiveGname));
            DebouncePayerSearch();
        }
    }

    /// <summary>界面展示的生效挂账单位（挂帐且支付方非空 → 支付方，否则客户名）。</summary>
    public string EffectiveGname =>
        ReceiptSettlement.EffectiveGname(Gname, Payer, SelectedPayTerm);

    public DateTime Outdate { get => _outdate; private set { _outdate = value; On(); } }
    public string MarkText { get => _markText; private set { _markText = value; On(); } }
    public string SelectedPayTerm
    {
        get => _selectedPayTerm;
        set
        {
            if (_selectedPayTerm == value) return;
            _selectedPayTerm = value;
            On();
            On(nameof(EffectiveGname));
            ResetPayTermRows();   // 旧 C__.row382：切换方式清空各行，选中行填限额，减收=0
            _ = RefreshCustomerYueAsync();   // 挂账额度/余额展示主体随生效 gname 变化
        }
    }
    public string PayNo { get => _payNo; set { _payNo = value; On(); } }
    /// <summary>发票号前缀（写 tbsada.pno 首位）。</summary>
    public string PnoPrefix { get => _pnoPrefix; set { _pnoPrefix = value; On(); } }
    public string Remark { get => _remark; set { _remark = value; On(); } }
    /// <summary>收款合计（旧 Σ PAYAMT）。</summary>
    public decimal PayTotal { get => _payTotal; private set { _payTotal = value; On(); } }
    /// <summary>减收（旧 TEXT2，只读自动值）：限额 − 收款合计；超额时压当前行后归 0。</summary>
    public decimal Jianshou { get => _jianshou; private set { _jianshou = value; On(); } }
    /// <summary>收款合计 − 折后实售金额（旧确认前 AMT0−TAMT 提示口径）。</summary>
    public decimal Difference
    {
        get => _difference;
        private set
        {
            _difference = value;
            On();
            On(nameof(DifferenceText));
        }
    }
    public string DifferenceText => Difference == 0m
        ? "收款合计与实售金额一致"
        : $"收款合计与实售金额差额 {Difference:N2}";
    public string Message { get => _message; private set { _message = value; On(); } }
    /// <summary>客户欠款（只读）：tbysyf Σ(ys-yf)，沿用现有 Xlsk 查询。</summary>
    public decimal CustomerYue { get => _customerYue; private set { _customerYue = value; On(); } }
    /// <summary>发货方式（可编辑下拉）：tbsada.t_mode，确认时写回。</summary>
    public string TMode { get => _tMode; set { _tMode = value; On(); } }
    /// <summary>确认日期（只读展示）：仓储按 GETDATE() 写入。</summary>
    public string ConfirmDate { get => _confirmDate; private set { _confirmDate = value; On(); } }
    public bool AlreadyConfirmed => _alreadyConfirmed;

    public bool CanConfirm => !_alreadyConfirmed;

    public async System.Threading.Tasks.Task LoadAsync()
    {
        try
        {
            var summary = await Db.DocQuery.GetOutDocSummaryAsync(Paper, Type, _dpm);
            Gname = summary.Gname;
            Outdate = summary.Outdate;
            // 旧 _B_.row364 L30-31：TAMT=ROUND(C_CK.OPRCT,2)、TOPRC=ROUND(C_CK.OPRC,2)，
            // C_CK 已在 __.row3 L76 按折后金额求和，故取折后汇总而非折前 Σ。
            TotalOprc = summary.TotalOprcDiscounted;
            TotalOprct = summary.TotalOprctDiscounted;
            var confirmed = summary.Mark.Trim() == "Y";
            MarkText = confirmed ? $"已收款（{summary.Guno}）" : "未收款";

            // 发票号前缀/号码回填（旧 _B_.row364 L69-70：CBO_PNO=LEFT(pno,1)、TXTPAYNO=SUBSTR(pno,2)）
            var pno = summary.Pno.Trim();
            var hasPrefix = pno.Length >= 1 && PnoPrefixOptions.Contains(pno[..1]);
            PnoPrefix = hasPrefix ? pno[..1] : "*";
            PayNo = hasPrefix ? pno[1..] : pno;
            Remark = summary.Remark.Trim(); // 旧 _B_.row364 L67：TEXTREMARK=C_CK.REMARK；只写应收备注

            // 发货方式：沿用出库确认的字典来源 tbsysset type='fhfs'
            TModeOptions.Clear();
            foreach (var name in await Db.Stock.GetSyssetNamesAsync("fhfs"))
                TModeOptions.Add(name);
            TMode = summary.TMode.Trim();
            if (TMode.Length > 0 && !TModeOptions.Contains(TMode))
                TModeOptions.Insert(0, TMode); // 字典无历史值时保留原值可选

            ConfirmDate = DateTime.Now.ToString("yyyy.MM.dd HH:mm");
            _alreadyConfirmed = confirmed;
            On(nameof(CanConfirm));

            // 挂账支付方开关（tbcskg，旧 CSKG）；读取失败按关闭处理，不阻断收款
            try { PayerEnabled = await Db.Receipts.GetPayerEnabledAsync(); }
            catch { PayerEnabled = false; }

            // 客户欠款（只读展示，失败不阻断收款）：挂账时按生效 gname（支付方优先）
            await RefreshCustomerYueAsync();

            PayTerms.Clear();
            PayTermRows.Clear();
            foreach (var m in await Db.Receipts.GetPaymentMethodsAsync())
            {
                PayTerms.Add(m.Name);
                var row = new PayTermRow(m.Name);
                row.AmountChanged += OnRowAmountChanged;
                PayTermRows.Add(row);
            }
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

    /// <summary>输入 300ms 防抖（沿用入库页供应商联想口径）。</summary>
    public void DebouncePayerSearch()
    {
        _payerDebounce.Stop();
        _payerDebounce.Start();
    }

    /// <summary>
    /// 挂帐支付方联想：复用 PartnerRepository.SearchAsync（已支持名称/编号/拼音码 tbgu.code/code1）。
    /// 旧 n_dm：table_source=tbgu、filed_source=gname（名称包含匹配）。
    /// </summary>
    public async System.Threading.Tasks.Task SearchPayerSuggestionsAsync()
    {
        var keyword = (Payer ?? "").Trim();
        if (!PayerEnabled || keyword.Length == 0)
        {
            PayerSuggestions.Clear();
            return;
        }
        try
        {
            var result = await Db.Partners.SearchAsync(new PagedRequest
            {
                Page = 1,
                PageSize = 100,
                Sort = "partName",
                Keyword = keyword,
            });
            PayerSuggestions.Clear();
            foreach (var p in result.Items) PayerSuggestions.Add(p);
        }
        catch { PayerSuggestions.Clear(); }
    }

    /// <summary>选中候选只回填名称（不写编号，确认仍按名称落库）。</summary>
    public void ApplyPayer(Partner partner)
    {
        Payer = partner.PartName.Trim();
        _payerDebounce.Stop();   // 选中后不再触发联想，避免下拉重新弹出
        PayerSuggestions.Clear();
    }

    /// <summary>客户余额/额度展示：挂账时主体取生效 gname（支付方优先，旧 row369 L233/L241/L270）。</summary>
    public async System.Threading.Tasks.Task RefreshCustomerYueAsync()
    {
        var gname = EffectiveGname.Trim();
        if (gname.Length == 0)
        {
            CustomerYue = 0m;
            return;
        }
        try { CustomerYue = await Db.Xlsk.GetCustomerYueAsync(gname); }
        catch { /* 余额查询失败不阻断收款 */ }
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

        // 点「确定」时先把仍在编辑的金额行按旧口径结算（压行/减收），避免读到未提交值
        foreach (var row in PayTermRows.Where(r => r.IsEditing).ToList())
            CommitRowEdit(row);

        // 旧 row369 L38-48：Σ PAYAMT != TAMT → 提示“实收金额与应收金额不符!,是否继续?”（默认 Yes）
        var sum = ReceiptSettlement.RoundMoney(PayTermRows.Sum(r => r.Amount));
        if (sum != TotalOprct)
        {
            var answer = MessageBox.Show(
                $"实收金额与应收金额不符！\n收款合计 {sum:N2}，实售金额 {TotalOprct:N2}，差额 {sum - TotalOprct:N2}。\n是否继续？",
                "收款确认", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;
        }

        // 旧 row369 L487-490：支付方与客户名相同 → 提示「客户挂帐到自己的名下时，无需输入挂帐支付方！」，按客户名处理
        var payer = PayerEnabled ? (Payer ?? "").Trim() : "";
        if (payer.Length > 0 && ReceiptSettlement.IsSamePayer(Gname, payer))
        {
            MessageBox.Show("客户挂帐到自己的名下时，无需输入挂帐支付方！",
                "收款确认", MessageBoxButton.OK, MessageBoxImage.Information);
            payer = "";
            Payer = "";
        }

        try
        {
            var prefix = (PnoPrefix ?? "").Trim();
            var invoiceNo = (PayNo ?? "").Trim();
            var result = await Db.Receipts.ConfirmAsync(new ConfirmReceiptCommand
            {
                Paper = Paper,
                Type = Type,
                Dept = _dpm,
                PayTerm = SelectedPayTerm,
                Lines = PayTermRows
                    .Select(r => new ReceiptPayLine { PayTerm = r.Name, Amount = r.Amount })
                    .ToList(),
                Jianshou = Jianshou,
                Payer = payer.Length > 0 ? payer : null,
                PayNo = invoiceNo,
                Pno = prefix.Length > 0 ? prefix + invoiceNo : invoiceNo, // 旧 row369 L447：CBO_PNO + TXTPAYNO
                TMode = (TMode ?? "").Trim(),
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

    /// <summary>
    /// 旧 C__.row382 L9-25：切换主付款方式时清空所有行金额；
    /// 选中方式为挂帐/托收/汇款则填折后开单金额，否则填折后实售金额；减收置 0。
    /// </summary>
    private void ResetPayTermRows()
    {
        var limit = ReceiptSettlement.Limit(SelectedPayTerm, TotalOprc, TotalOprct);
        foreach (var row in PayTermRows)
        {
            row.EndEdit();
            row.SetAmount(0m);
        }
        PayTermRows.FirstOrDefault(r => r.Name == SelectedPayTerm)?.SetAmount(limit);
        RecalcTotals();
        Jianshou = 0m;
    }

    /// <summary>任一金额变化（绑定回写）后刷新合计与差额；压行/减收在失焦提交时处理。</summary>
    private void OnRowAmountChanged(PayTermRow row) => RecalcTotals();

    /// <summary>失焦提交（旧 __.row380 L25-44）：超额压当前行，否则减收=限额−合计。</summary>
    public void CommitRowEdit(PayTermRow row)
    {
        var value = ReceiptSettlement.RoundMoney(row.Amount);
        row.EndEdit();
        if (value != row.Amount) row.SetAmount(value);

        var limit = ReceiptSettlement.Limit(SelectedPayTerm, TotalOprc, TotalOprct);
        var sum = PayTermRows.Sum(r => r.Amount);
        var (rowAmount, jianshou) = ReceiptSettlement.ApplyRowEdit(limit, sum, value);
        if (rowAmount != value) row.SetAmount(ReceiptSettlement.RoundMoney(rowAmount));

        RecalcTotals();
        Jianshou = ReceiptSettlement.RoundMoney(jianshou);
    }

    private void RecalcTotals()
    {
        PayTotal = ReceiptSettlement.RoundMoney(PayTermRows.Sum(r => r.Amount));
        Difference = PayTotal - TotalOprct;
    }

    public event Action? OnClosingRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>收款方式/金额表格行（截图 C_GUNO 的 NAME/PAYAMT 两列；双击进入编辑）。</summary>
public sealed class PayTermRow : INotifyPropertyChanged
{
    private decimal _amount;
    private bool _isEditing;

    public PayTermRow(string name) => Name = name;

    public string Name { get; }

    public decimal Amount
    {
        get => _amount;
        set
        {
            if (_amount == value) return;
            _amount = value;
            On();
            AmountChanged?.Invoke(this);
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            On();
            On(nameof(IsReadOnly));
        }
    }

    /// <summary>未双击进入编辑时只读（保持正常前景色，与截图一致）。</summary>
    public bool IsReadOnly => !_isEditing;

    public void BeginEdit() => IsEditing = true;

    public void EndEdit() => IsEditing = false;

    /// <summary>程序同步金额（不触发编辑回写）。</summary>
    public void SetAmount(decimal value)
    {
        if (_amount == value) return;
        _amount = value;
        On(nameof(Amount));
    }

    public event Action<PayTermRow>? AmountChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
