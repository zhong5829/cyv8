using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using QPGL.Desktop.Services;
using QPGL.Domain.Parts;

namespace QPGL.Desktop.Views;

/// <summary>数量为负（退货行）→ True，用于历史网格整行红字触发器。</summary>
public sealed class NegativeQtyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is decimal d && d < 0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 销售行编辑对话框（复刻 qp11 SellEditDialog 布局）：
/// 双击配件搜索行弹出，设置数量/零售批发/单价/备注，右侧显示该配件历史售价供定价参考。
/// 确认后经 Owner（SaleViewModel.AddLine）写入明细。
/// 2026-10-28 需求：拦截负库存——库存 0 确认按钮禁用；数量超实际库存禁用确认并提示。
/// </summary>
public partial class SaleLineEditDialog : Window
{
    private readonly PartItem _part;
    private readonly string _dept;

    /// <summary>确认后的结果（车型/数量/单价/备注）。</summary>
    public string LineTy { get; private set; } = "";
    public decimal Qty { get; private set; } = 1;
    public decimal Price { get; private set; }
    public string LineMemo { get; private set; } = "";

    public SaleLineEditDialog(PartItem part, string dept, decimal? initQty = null, decimal? initPrice = null, string? initMemo = null,
        string? customerName = null)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _part = part;
        _dept = dept;

        TxtPartNo.Text = part.Nno;
        TxtPartName.Text = part.Na1;
        TxtTy.Text = part.Ty;
        TxtPosi.Text = $"{part.Posi} / {part.Depotno}";
        TxtStock.Text = part.Kcamount.ToString("0.##");
        TxtIpInfo.Text = $"进价（不含税）：{part.Iprj:N2}    进价（含税）：{part.Iprc:N2}";
        TxtFaInfo.Text = $"产地：{part.Dc}    单位：{part.Unit}    方案：{part.Fa}";
        TxtDepotInfo.Text = $"内部编号：{part.Cp}    零售价：{part.Oprc:N2}    批发价：{part.Oprb:N2}";

        TxtQty.Text = (initQty ?? 1).ToString("0.##");
        // 默认价：编辑时用原价；新增时按零售/批发单选给默认
        RbRetail.IsChecked = true;
        TxtPrice.Text = (initPrice ?? part.Oprc).ToString("N2");
        TxtMemo.Text = initMemo ?? "";

        // 销售历史筛选默认匹配开单客户名称（2026-10-30 需求）：客户为空则筛选框为空；
        // 构造期设 Text 会触发 TextChanged，用 _filterReady 拦住防抖，初始加载直接按关键字查
        TxtSellFilter.Text = customerName ?? "";

        _sellFilterDebounce.Tick += async (_, _) =>
        {
            _sellFilterDebounce.Stop();
            await LoadHistoryAsync(TxtSellFilter.Text.Trim());
        };
        _purchaseFilterDebounce.Tick += async (_, _) =>
        {
            _purchaseFilterDebounce.Stop();
            await LoadPurchaseHistoryAsync(TxtPurchaseFilter.Text.Trim());
        };

        _ = LoadHistoryAsync(TxtSellFilter.Text.Trim());
        _ = LoadPurchaseHistoryAsync();
        _filterReady = true;
        UpdateAmount();
        // 打开即聚焦数量输入框并全选（2026-10-28 需求：不再定位车型；回车/上下键流转：车型 → 数量 → 单价 → 确定）
        Loaded += (_, _) => { TxtQty.Focus(); TxtQty.SelectAll(); };
    }

    /// <summary>回车/上下方向键在 车型→数量→单价→确定 间流转并全选（qp11 开单键盘流）。</summary>
    private void Dialog_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var forward = e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Down;
        var backward = e.Key == System.Windows.Input.Key.Up;
        if (!forward && !backward) return;

        if (ReferenceEquals(e.Source, TxtTy))
        {
            TxtQty.Focus();
            TxtQty.SelectAll();
        }
        else if (ReferenceEquals(e.Source, TxtQty))
        {
            if (backward) { TxtTy.Focus(); TxtTy.SelectAll(); }
            else { TxtPrice.Focus(); TxtPrice.SelectAll(); }
        }
        else if (ReferenceEquals(e.Source, TxtPrice))
        {
            if (backward) { TxtQty.Focus(); TxtQty.SelectAll(); }
            else BtnOk_Click(sender, e);   // 回车确认加入明细
        }
        e.Handled = true;
    }

    // 历史筛选防抖（300ms，与客户联想一致）：输入即按名称/拼音码筛
    private readonly System.Windows.Threading.DispatcherTimer _sellFilterDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly System.Windows.Threading.DispatcherTimer _purchaseFilterDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    /// <summary>构造期默认筛选文本赋值不启动防抖（初始加载已带关键字查询）</summary>
    private bool _filterReady;

    private void SellFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filterReady || TxtSellFilter is null) return;   // InitializeComponent 初始化期触发
        _sellFilterDebounce.Stop();
        _sellFilterDebounce.Start();
    }

    private void PurchaseFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtPurchaseFilter is null) return;
        _purchaseFilterDebounce.Stop();
        _purchaseFilterDebounce.Start();
    }

    private async System.Threading.Tasks.Task LoadHistoryAsync(string keyword = "")
    {
        try
        {
            var history = await Db.Parts.GetSaleHistoryAsync(_part.Nno, _dept, 0, keyword);   // 0 = 全部记录
            DgSellHistory.ItemsSource = history;
        }
        catch
        {
            // 历史加载失败不阻断编辑
        }
    }

    private async System.Threading.Tasks.Task LoadPurchaseHistoryAsync(string keyword = "")
    {
        try
        {
            var history = await Db.Parts.GetPurchaseHistoryAsync(_part.Nno, _dept, 0, keyword);   // 0 = 全部记录
            DgPurchaseHistory.ItemsSource = history;
        }
        catch
        {
            // 历史加载失败不阻断编辑
        }
    }

    private void PriceMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_part is null || TxtPrice is null || RbRetail is null) return;   // InitializeComponent 初始化期触发
        var retail = RbRetail.IsChecked == true;
        var current = ParseDecimal(TxtPrice.Text);
        // 仅当当前价等于另一模式默认价（未被手改）时切换，避免覆盖手工输入
        if (current == _part.Oprc || current == _part.Oprb || current == 0)
            TxtPrice.Text = (retail ? _part.Oprc : _part.Oprb).ToString("N2");
    }

    private void QtyPrice_TextChanged(object sender, TextChangedEventArgs e) => UpdateAmount();

    private void UpdateAmount()
    {
        if (_part is null || TxtAmount is null || TxtOverStock is null) return;   // InitializeComponent 设默认 Text 时触发，_part 尚未赋值
        var qty = ParseDecimal(TxtQty.Text);
        var price = ParseDecimal(TxtPrice.Text);
        TxtAmount.Text = (qty * price).ToString("N2");

        // 拦截负库存（2026-10-28 需求）：库存 0 或数量超库存 → 确认按钮禁用 + 红字提示
        if (_part.Kcamount <= 0)
        {
            BtnOk.IsEnabled = false;
            TxtOverStock.Text = "⚠ 该配件实际库存为 0，不允许销售（负库存拦截）";
            TxtOverStock.Visibility = Visibility.Visible;
        }
        else if (qty > _part.Kcamount)
        {
            BtnOk.IsEnabled = false;
            TxtOverStock.Text = $"⚠ 销售数量 {qty:0.##} 超过实际库存 {_part.Kcamount:0.##}，不允许负库存出库";
            TxtOverStock.Visibility = Visibility.Visible;
        }
        else
        {
            BtnOk.IsEnabled = true;
            TxtOverStock.Visibility = Visibility.Collapsed;
        }
    }

    private void DgSellHistory_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DgSellHistory.SelectedItem is PartSaleHistory h)
            TxtPrice.Text = h.Price.ToString("N2");
    }

    /// <summary>双击进货历史行 → 切到零售并回填历史进价（不含税）作为售价参考。</summary>
    private void DgPurchaseHistory_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DgPurchaseHistory.SelectedItem is PartPurchaseHistory h)
            TxtPrice.Text = h.Iprj.ToString("N2");
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var qty = ParseDecimal(TxtQty.Text);
        if (qty <= 0)
        {
            MessageBox.Show("数量必须大于 0", "销售编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 双保险：回车流转直接调本方法会绕过按钮 IsEnabled，负库存此处再拦一道
        if (qty > _part.Kcamount)
        {
            MessageBox.Show($"销售数量 {qty:0.##} 超过实际库存 {_part.Kcamount:0.##}，不允许负库存出库", "销售编辑",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var price = ParseDecimal(TxtPrice.Text);
        if (price < 0)
        {
            MessageBox.Show("单价不能为负", "销售编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Qty = qty;
        Price = price;
        LineTy = TxtTy.Text.Trim();
        LineMemo = TxtMemo.Text.Trim();
        DialogResult = true;
    }

    private static decimal ParseDecimal(string? text) =>
        decimal.TryParse((text ?? "").Trim(), out var v) ? v : 0m;
}
