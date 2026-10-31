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
    /// <summary>跳过负库存拦截（2026-10-30 修复）：修改明细行路径——改单保存时服务端先删原单回补库存再重扣，
    /// 当前库存 0 不代表不可改（且改单回读行无批次快照）；新增行路径仍拦截，服务端 StockAllocator 硬拦兜底。</summary>
    private readonly bool _ignoreStockCheck;
    /// <summary>改单可售上限 = 当前库存 + 原单数量（2026-10-30 需求：改单保存时服务端先删原单回补库存再重扣）。</summary>
    private readonly decimal _editStockLimit;
    /// <summary>数量输入最近一次合法文本（超上限弹窗后回退到此值）。</summary>
    private string _lastValidQty = "1";
    /// <summary>数量回退写回时屏蔽 TextChanged 递归。</summary>
    private bool _qtyGuard;

    /// <summary>确认后的结果（车型/数量/单价/备注）。</summary>
    public string LineTy { get; private set; } = "";
    public decimal Qty { get; private set; } = 1;
    public decimal Price { get; private set; }
    public string LineMemo { get; private set; } = "";

    public SaleLineEditDialog(PartItem part, string dept, decimal? initQty = null, decimal? initPrice = null, string? initMemo = null,
        string? customerName = null, bool ignoreStockCheck = false)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _part = part;
        _dept = dept;
        _ignoreStockCheck = ignoreStockCheck;

        TxtPartNo.Text = part.Nno;
        TxtPartName.Text = part.Na1;
        TxtTy.Text = part.Ty;
        TxtPosi.Text = $"{part.Posi} / {part.Depotno}";
        TxtStock.Text = part.Kcamount.ToString("0.##");
        TxtIpInfo.Text = $"进价（不含税）：{part.Iprj:N2}    进价（含税）：{part.Iprc:N2}";
        TxtFaInfo.Text = $"产地：{part.Dc}    单位：{part.Unit}    方案：{part.Fa}";
        TxtDepotInfo.Text = $"内部编号：{part.Cp}    零售价：{part.Oprc:N2}    批发价：{part.Oprb:N2}";

        TxtQty.Text = (initQty ?? 1).ToString("0.##");
        _lastValidQty = TxtQty.Text;
        // 改单可售上限（2026-10-30 需求）：当前库存 + 原单数量，标注 + 超限弹窗回退
        _editStockLimit = part.Kcamount + (initQty ?? 0);
        if (ignoreStockCheck)
        {
            TxtEditLimit.Text = $"改单可售上限：{_editStockLimit:0.##}（当前库存 {part.Kcamount:0.##} + 原单数量 {initQty ?? 0:0.##}）";
            TxtEditLimit.Visibility = Visibility.Visible;
        }
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
            // 筛选后自动匹配最近价格填充单价框（2026-10-31 需求）：取筛选结果第一条（按日期倒序的最近成交价）。
            // 打开弹窗时已按客户名筛选（含先输入客户名称再打开的场景），同样自动带出该客户最近成交价
            if (history.Count > 0)
                TxtPrice.Text = history[0].Price.ToString("N2");
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

    private void QtyPrice_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 改单模式数量守卫（2026-10-30 需求）：超可售上限立即弹窗并回退输入
        if (_ignoreStockCheck && ReferenceEquals(sender, TxtQty) && !_qtyGuard && TxtQty is not null)
        {
            var qty = ParseDecimal(TxtQty.Text);
            if (qty > _editStockLimit)
            {
                MessageBox.Show($"改单可售上限为 {_editStockLimit:0.##}（当前库存 + 原单数量），数量 {qty:0.##} 已回退",
                    "销售编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
                _qtyGuard = true;
                TxtQty.Text = _lastValidQty;
                TxtQty.CaretIndex = TxtQty.Text.Length;
                _qtyGuard = false;
                return;
            }
            _lastValidQty = TxtQty.Text;
        }
        UpdateAmount();
    }

    private void UpdateAmount()
    {
        if (_part is null || TxtAmount is null || TxtOverStock is null) return;   // InitializeComponent 设默认 Text 时触发，_part 尚未赋值
        var qty = ParseDecimal(TxtQty.Text);
        var price = ParseDecimal(TxtPrice.Text);
        TxtAmount.Text = (qty * price).ToString("N2");

        // 拦截负库存（2026-10-28 需求）：库存 0 或数量超库存 → 确认按钮禁用 + 红字提示；
        // 修改明细行路径跳过（2026-10-30 修复：改单回读行无批次快照，且服务端会先回补库存）
        if (_ignoreStockCheck)
        {
            BtnOk.IsEnabled = true;
            TxtOverStock.Visibility = Visibility.Collapsed;
        }
        else if (_part.Kcamount <= 0)
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

    /// <summary>双击销售历史行 → 弹出该销售单整单明细（2026-10-31 需求）。type 用历史行自带值
    /// （旧账单 type='历史'，此前固定按'销售'查明细为空）。</summary>
    private async void DgSellHistory_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DgSellHistory.SelectedItem is not PartSaleHistory h) return;
        try
        {
            var lines = await Db.DocQuery.GetOutDocLinesAsync(h.Paper.Trim(), h.Type.Trim(), _dept);
            new HistoryDocDetailWindow($"销售单 {h.Paper.Trim()} 明细（{h.Gname.Trim()} {h.OutDate:yyyy-MM-dd}）", lines).ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载单据明细失败：{ex.Message}", "单据明细", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>双击进货历史行 → 弹出该入库单整单明细（2026-10-31 需求；专用查询，本库 tbistoed 无 gu 列）。</summary>
    private async void DgPurchaseHistory_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DgPurchaseHistory.SelectedItem is not PartPurchaseHistory h) return;
        try
        {
            var rows = await Db.Parts.GetPurchaseDocLinesAsync(h.Cno.Trim());
            new HistoryDocDetailWindow($"入库单 {h.Cno.Trim()} 明细（{h.Gyname.Trim()} {h.InDate:yyyy-MM-dd}）", rows).ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载单据明细失败：{ex.Message}", "单据明细", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var qty = ParseDecimal(TxtQty.Text);
        if (qty <= 0)
        {
            MessageBox.Show("数量必须大于 0", "销售编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 双保险：回车流转直接调本方法会绕过按钮 IsEnabled，负库存此处再拦一道（修改明细行路径按改单上限拦）
        var overLimit = _ignoreStockCheck ? qty > _editStockLimit : qty > _part.Kcamount;
        if (overLimit)
        {
            var limitText = _ignoreStockCheck
                ? $"改单可售上限为 {_editStockLimit:0.##}（当前库存 + 原单数量）"
                : $"销售数量 {qty:0.##} 超过实际库存 {_part.Kcamount:0.##}";
            MessageBox.Show($"{limitText}，不允许负库存出库", "销售编辑",
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
