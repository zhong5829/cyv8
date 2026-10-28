using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Views;

/// <summary>
/// 收/付款信息录入对话框（旧 skinfor 复刻）：
/// Init __.row2（付款/收款标题）、下拉 p_.row11（tbsysset type='业务类型'）、
/// 确定 __.row9 返回协议 `|type|paper|ys|`（四竖线五段，取消返回空）。
/// 不写库，仅返回输入。
/// </summary>
public partial class SkinforWindow : Window
{
    public SkinforViewModel ViewModel { get; }

    public SkinforWindow(string lctype)
    {
        ViewModel = new SkinforViewModel(lctype);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += async (_, _) => await ViewModel.LoadTypesAsync();
    }

    /// <summary>确认返回 `|type|paper|ys|`；取消返回 null。</summary>
    public string? Result { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Validate(out var ys))
        {
            MessageBox.Show(ViewModel.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 旧 __.row9：AA = '|' + MTYPE + '|' + MPAPER + '|' + STR(MYS,18,2) + '|'
        Result = $"|{ViewModel.BizType}|{ViewModel.Paper.Trim()}|{ys:N2}|";
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class SkinforViewModel(string lctype) : INotifyPropertyChanged
{
    public string Title => lctype == "付款" ? "付款信息" : "收款信息";
    public string AmountLabel => lctype == "付款" ? "付款金额：" : "收款金额：";

    public IReadOnlyList<string> BizTypes { get; private set; } = [];
    public string Message { get; private set; } = "";

    private string _bizType = "", _paper = "", _amountText = "0.00";
    public string BizType { get => _bizType; set { _bizType = value; On(); } }
    public string Paper { get => _paper; set { _paper = value; On(); } }
    public string AmountText { get => _amountText; set { _amountText = value; On(); } }

    public async Task LoadTypesAsync()
    {
        var types = await Db.Xlsk.GetBizTypesAsync();
        BizTypes = types;
        On(nameof(BizTypes));
        if (BizTypes.Count > 0) BizType = BizTypes[0];
    }

    public bool Validate(out decimal ys)
    {
        Message = "";
        ys = 0;
        if (string.IsNullOrWhiteSpace(BizType)) { Message = "请选择业务类型"; return false; }
        if (!decimal.TryParse(AmountText.Trim(), out ys))
        { Message = "金额输入不正确！"; return false; }
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
