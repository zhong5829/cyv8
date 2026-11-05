using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.Views;

/// <summary>
/// 配件选择窗口（散件入库「添加配件」入口）：
/// 三条件查询（编码=内部编号包含 / 名称=拼音码编码名称车型 / 车型），输入即搜 500ms 防抖，
/// 无限滚动加载（滚到底自动下一页，与 SaleControl 配件列表同机制），
/// 双击（或 Enter/F4）结果行确认 → 触发 PartPicked 回调，窗口保持打开可连续选件（2026-11-02）。
/// </summary>
public partial class PartPickWindow : Window
{
    private readonly string _dept;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>无限滚动分页状态（SaleViewModel 同机制）。</summary>
    private const int PageSize = 100;
    private int _page = 1;
    private int _total;
    private bool _loadingMore;
    private int _searchSeq;
    private bool _hasCondition;
    private string _effectiveBrand = "";   // 当前生效厂牌（跨厂牌回退后为临时厂牌，LoadMore 沿用）

    public ObservableCollection<PartItem> Parts { get; } = [];

    /// <summary>历史区（2026-11-03 双区域需求）：选中配件的进货历史与销售历史。</summary>
    public ObservableCollection<PartInRecordRow> InRecords { get; } = [];
    public ObservableCollection<PartSaleRecordRow> SaleRecords { get; } = [];
    private int _histSeq;

    /// <summary>双击选中的配件（兼容单选模式：订阅 PartPicked 前的旧用法）。</summary>
    public PartItem? SelectedPart { get; private set; }

    /// <summary>
    /// 连续选件回调（2026-11-02 需求）：确认一个配件后窗口保持打开，调用方处理后可继续选下一个；
    /// 取消（Esc/×）仍走 ShowDialog 返回值结束。
    /// </summary>
    public event Action<PartItem>? PartPicked;

    public PartPickWindow(string dept)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _dept = dept;
        DataContext = this;   // 历史区 DataGrid 以 {Binding InRecords/SaleRecords} 绑定，需 DataContext 指向自身
        PartGrid.ItemsSource = Parts;
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await SearchAsync();
        };
        Loaded += async (_, _) =>
        {
            await SearchAsync();
            // 无限滚动：配件列表滚到底自动加载下一页
            var sv = FindDescendantScrollViewer(PartGrid);
            if (sv is not null) sv.ScrollChanged += PartGrid_ScrollChanged;
        };
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var hit = FindDescendantScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// 选中指定行并把键盘焦点移入表格（2026-11-03 需求：蓝色选中态要求 DataGrid 持有键盘焦点，
    /// 否则默认单元格模板渲染灰色）。DataGridRow 自身 Focus 不可靠，焦点落到该行第一个单元格；
    /// 全部失败再兜底 DataGrid.Focus()。与原生键盘导航同构，不复现旧「程序化选中与内部导航脱节」问题。
    /// </summary>
    private void FocusPartRow(int index)
    {
        if (index < 0 || Parts.Count == 0) return;
        PartGrid.SelectedIndex = Math.Clamp(index, 0, Parts.Count - 1);
        PartGrid.UpdateLayout();
        PartGrid.ScrollIntoView(PartGrid.SelectedItem);
        if (PartGrid.ItemContainerGenerator.ContainerFromItem(PartGrid.SelectedItem) is DataGridRow row)
        {
            row.ApplyTemplate();
            var cell = FindFirstCell(row);
            if (cell is not null) cell.Focus();
            else row.Focus();
        }
        else
        {
            PartGrid.Focus();
        }
        if (!PartGrid.IsKeyboardFocusWithin) PartGrid.Focus();
    }

    private static DataGridCell? FindFirstCell(System.Windows.DependencyObject parent)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is DataGridCell cell) return cell;
            var found = FindFirstCell(child);
            if (found is not null) return found;
        }
        return null;
    }

    // 滚动到距底部 2 行内即触发加载下一页
    private async void PartGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 40 && e.VerticalChange != 0)
            await LoadMoreAsync();
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// ↓/↑ 在条件框内驱动表格选中行并把焦点移到行容器（2026-11-03：蓝色选中态要求焦点在行上；
    /// 焦点落在行容器与原生导航同构，不触发旧「程序化选中与内部导航脱节」问题）。
    /// 首次 ↓ 从无选中跳到第一行，之后逐行移动（表格原生导航），↑ 反向，回车确认选中行；
    /// ←/→ 方向键在 编码→名称→车型 条件框间流转并全选（光标在边界时才切换，框内移动不受影响）。
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        switch (e.Key)
        {
            case Key.Down:
                if (Parts.Count > 0)
                    FocusPartRow(PartGrid.SelectedIndex + 1);   // 焦点随选中行移入表格 → 蓝色选中态，后续 ↑/↓ 由表格原生导航
                e.Handled = true;
                break;
            case Key.Up:
                if (Parts.Count > 0)
                    FocusPartRow(PartGrid.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (PartGrid.SelectedItem is not null) Confirm();
                e.Handled = true;
                break;
            case Key.Left when box.CaretIndex == 0:
            {
                var target = box == Na1Box ? CpBox : box == TyBox ? Na1Box : null;
                if (target is not null)
                {
                    target.Focus();
                    target.SelectAll();
                }
                e.Handled = true;
                break;
            }
            case Key.Right when box.CaretIndex == box.Text.Length:
            {
                var target = box == CpBox ? Na1Box : box == Na1Box ? TyBox : null;
                if (target is not null)
                {
                    target.Focus();
                    target.SelectAll();
                }
                e.Handled = true;
                break;
            }
        }
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        // 竞态守卫：递增序号，慢的旧请求返回后直接丢弃
        var seq = ++_searchSeq;
        try
        {
            var cp = CpBox.Text.Trim();
            var na1 = Na1Box.Text.Trim();
            var ty = TyBox.Text.Trim();
            var brand = AppSession.Brand.Trim();
            _hasCondition = cp.Length > 0 || na1.Length > 0 || ty.Length > 0;
            var request = new PagedRequest { Page = 1, PageSize = PageSize };
            var result = await Services.PartCacheService.SearchAsync(_dept, cp, na1, ty, request, note: null, fillLastIprj: false, brand, includeAttrOnly: true);
            if (seq != _searchSeq) return;

            // 跨厂牌回退（2026-10-29，与 SaleViewModel 同构）：当前厂牌无命中且带条件时，
            // 查其它厂牌命中数（含属性库独有行——属性定义新增配件可能挂错厂牌），弹窗确认后临时加载
            if (result.Total == 0 && _hasCondition && brand.Length > 0)
            {
                var others = await Services.PartCacheService.CountOtherBrandsAsync(_dept, brand, cp, na1, ty);
                if (seq != _searchSeq) return;
                // 厂牌白名单（角色 usemcp）：非空时仅提示白名单内的厂牌；空 = 无限制
                var wl = AppSession.BrandWhitelist;
                if (wl.Length > 0) others = [.. others.Where(o => wl.Contains(o.Cp))];
                if (others.Count > 0)
                {
                    var detail = string.Join("，", others.Select(o => $"【{o.Cp}】{o.Count} 条"));
                    var top = others.OrderByDescending(o => o.Count).First().Cp;
                    var answer = MessageBox.Show(
                        $"当前厂牌【{brand}】未检索到该配件，{detail}。\n\n是否加载{top}编码？",
                        "跨厂牌检索", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (answer == MessageBoxResult.Yes)
                    {
                        var alt = await Services.PartCacheService.SearchAsync(_dept, cp, na1, ty, request, note: null, fillLastIprj: false, top, includeAttrOnly: true);
                        if (seq != _searchSeq) return;
                        Parts.Clear();
                        foreach (var p in alt.Items) Parts.Add(p);
                        _page = 1;
                        _total = alt.Total;
                        _effectiveBrand = top;
                        _loadingMore = false;
                        TxtStatus.Text = $"临时查看：{top}（共 {alt.Total} 条）{(Services.PartCacheService.LastHint is { Length: > 0 } hAlt ? " " + hAlt : "")}";
                        QPGL.Infrastructure.DebugLog.Log($"[PartPick] 跨厂牌回退 brand='{top}' rows={alt.Items.Count} total={alt.Total}");
                        return;
                    }
                }
            }

            Parts.Clear();
            foreach (var p in result.Items) Parts.Add(p);
            _page = 1;
            _total = result.Total;
            _effectiveBrand = brand;
            _loadingMore = false;
            TxtStatus.Text = result.Total == 0 && _hasCondition
                ? "无匹配配件"
                : $"共 {result.Total} 条{(Services.PartCacheService.LastHint is { Length: > 0 } h ? " " + h : "")}";
        }
        catch (Exception ex)
        {
            if (seq == _searchSeq) TxtStatus.Text = "查询失败：" + ex.Message;
        }
    }

    /// <summary>无限滚动：滚动到底自动加载下一页（竞态与防重入由 _loadingMore + seq 守卫）。</summary>
    private async System.Threading.Tasks.Task LoadMoreAsync()
    {
        if (_loadingMore || Parts.Count == 0 || Parts.Count >= _total) return;
        _loadingMore = true;
        var seq = _searchSeq;
        try
        {
            var request = new PagedRequest { Page = _page + 1, PageSize = PageSize };
            var result = await Services.PartCacheService.SearchAsync(_dept, CpBox.Text.Trim(), Na1Box.Text.Trim(), TyBox.Text.Trim(), request, note: null, fillLastIprj: false, _effectiveBrand, includeAttrOnly: true);
            if (seq != _searchSeq) return;
            _page++;
            _total = result.Total;
            foreach (var p in result.Items) Parts.Add(p);
        }
        catch
        {
            // 加载失败保持当前列表
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void Confirm()
    {
        if (PartGrid.SelectedItem is not PartItem part)
        {
            MessageBox.Show("请先选中一行配件", "选择配件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedPart = part;
        // 2026-11-02 需求：确认后窗口保持打开（不设 DialogResult），回调调用方处理后可继续选件
        TxtStatus.Text = $"已选定：{part.Nno} {part.Na1}（可继续选择，Esc 关闭）";
        PartPicked?.Invoke(part);
    }

    private void PartGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    /// <summary>
    /// 历史区（2026-11-03 双区域需求）：选中行变化 → 加载该配件的进货/销售历史（复用 Order4 仓储，带竞态守卫）。
    /// 2026-11-04 弱网缓存：先秒显会话内缓存（如有），随即后台重查最新数据回填——
    /// 其他电脑新开的单据约 1 次往返后自动出现（用户确认口径：缓存秒显+后台刷新）。
    /// </summary>
    private async void PartGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartGrid.SelectedItem is not PartItem part) return;
        var seq = ++_histSeq;
        InRecords.Clear();
        SaleRecords.Clear();
        var cp = (part.Cp ?? "").Trim();
        var nno = (part.Nno ?? "").Trim();
        if (nno.Length == 0) return;
        // 秒显缓存（命中时网格立即有数据，下方重查覆盖为最新）
        if (Services.PartCacheService.TryGetHistory(cp, nno, out var cachedIns, out var cachedSales))
        {
            foreach (var r in cachedIns) InRecords.Add(r);
            foreach (var r in cachedSales) SaleRecords.Add(r);
        }
        try
        {
            var ins = await Db.Order4.GetInRecordsAsync(cp, nno);
            var sales = await Db.Order4.GetSaleRecordsAsync(cp, nno);
            if (seq != _histSeq) return;
            InRecords.Clear();
            SaleRecords.Clear();
            foreach (var r in ins) InRecords.Add(r);
            foreach (var r in sales) SaleRecords.Add(r);
            Services.PartCacheService.SetHistory(cp, nno, ins.ToList(), sales.ToList());
        }
        catch
        {
            // 历史加载失败不阻断选件（有缓存秒显数据时保留显示）
        }
    }

    private void PartGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.F4)
        {
            Confirm();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 新增编码（2026-10-30 需求）：弹零件属性编辑窗（PartAttributeEditWindow，新增模式）录入新配件，
    /// 确定后保存属性库（Db.Prnoty.SaveAsync，与 A1 属性定义页 AttributeVms.SaveRowAsync 同构），
    /// 保存成功直接选定该配件返回——调用方（散件入库）随即弹入库明细编辑窗继续入库。
    /// </summary>
    private async void BtnNewPart_Click(object sender, RoutedEventArgs e)
    {
        var cp = AppSession.Brand.Trim();
        if (cp.Length == 0)
        {
            MessageBox.Show("请先选择厂牌（F11）再新增编码", "新增编码", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new PartAttributeEditWindow(null, cp) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var r = dlg.Row;
        try
        {
            await Db.Prnoty.SaveAsync(new QPGL.Infrastructure.Parts.PrnotySaveCommand
            {
                IsAdd = true,
                Id = 0,
                Cp = r.Cp,
                Nno = r.Nno,
                OldNno = "",
                Na1 = r.Na1, Ty = r.Ty, Fa = r.Fa, Dc = r.Dc, Unit = r.Unit, Iprj = r.Iprj,
                Code_ts = r.Code_ts, Eng = r.Eng, Nnp = r.Nnp, Nnq = r.Nnq, Uqty = r.Uqty,
                Xno = r.Xno, Tno = r.Tno, Amjj = r.Amjj, Bmjj = r.Bmjj,
                Nvol = decimal.TryParse(r.Nvol, out var nv) ? nv : 0,
                Suttle = r.Suttle, Note = r.Note, Note1 = r.Note1, Oprc_cp = r.Oprc_cp, Oprc_min = r.Oprc_min,
            }, r.Cp, "A");
            Services.PartCacheService.Invalidate();   // 属性库新增 → 配件选择缓存失效

            // 直接选定新配件回调（2026-11-02 连续选件：窗口保持打开，属性库新行尚无库存行：
            // id=0、无货位，与列表 includeAttrOnly 属性行同构；零售价取属性库统一售价 oprc_cp，进价取录入 iprj）
            SelectedPart = new PartItem
            {
                Id = 0, Dpm = _dept, Cp = r.Cp, Nno = r.Nno, Na1 = r.Na1, Ty = r.Ty,
                Fa = r.Fa, Dc = r.Dc, Unit = r.Unit, Kcamount = 0,
                Iprj = r.Iprj, Oprc = r.Oprc_cp ?? 0, Note = r.Note,
            };
            TxtStatus.Text = $"已选定新增配件：{r.Nno} {r.Na1}（可继续选择，Esc 关闭）";
            PartPicked?.Invoke(SelectedPart);
        }
        catch (Exception ex)
        {
            MessageBox.Show("新增编码保存失败：" + ex.Message, "新增编码", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
