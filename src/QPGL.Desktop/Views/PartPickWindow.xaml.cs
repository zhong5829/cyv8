using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;

namespace QPGL.Desktop.Views;

/// <summary>
/// 配件选择窗口（散件入库「添加配件」入口）：
/// 三条件查询（编码=内部编号包含 / 名称=拼音码编码名称车型 / 车型），输入即搜 500ms 防抖，
/// 无限滚动加载（滚到底自动下一页，与 SaleControl 配件列表同机制），
/// 双击（或 Enter/F4）结果行确认返回 PartItem。
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

    /// <summary>双击选中的配件（DialogResult=true 时有效）。</summary>
    public PartItem? SelectedPart { get; private set; }

    public PartPickWindow(string dept)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _dept = dept;
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

    /// <summary>←/→ 方向键在 编码→名称→车型 条件框间流转并全选（光标在边界时才切换，框内移动不受影响）；
    /// ↓ 方向键切换到配件列表选中第一行（列表为空时先尝试加载）。</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        switch (e.Key)
        {
            case Key.Down:
                if (Parts.Count > 0)
                {
                    PartGrid.Focus();
                    PartGrid.SelectedIndex = 0;
                    PartGrid.ScrollIntoView(PartGrid.SelectedItem);
                }
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
            var result = await Db.Parts.SearchAsync(_dept, cp, na1, ty, request, fillLastIprj: false, brand, includeAttrOnly: true);
            if (seq != _searchSeq) return;

            // 跨厂牌回退（2026-10-29，与 SaleViewModel 同构）：当前厂牌无命中且带条件时，
            // 查其它厂牌命中数（含属性库独有行——属性定义新增配件可能挂错厂牌），弹窗确认后临时加载
            if (result.Total == 0 && _hasCondition && brand.Length > 0)
            {
                var others = await Db.Parts.CountOtherBrandsAsync(_dept, brand, cp, na1, ty);
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
                        var alt = await Db.Parts.SearchAsync(_dept, cp, na1, ty, request, fillLastIprj: false, top, includeAttrOnly: true);
                        if (seq != _searchSeq) return;
                        Parts.Clear();
                        foreach (var p in alt.Items) Parts.Add(p);
                        _page = 1;
                        _total = alt.Total;
                        _effectiveBrand = top;
                        _loadingMore = false;
                        TxtStatus.Text = $"临时查看：{top}（共 {alt.Total} 条）";
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
            TxtStatus.Text = result.Total == 0 && _hasCondition ? "无匹配配件" : $"共 {result.Total} 条";
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
            var result = await Db.Parts.SearchAsync(_dept, CpBox.Text.Trim(), Na1Box.Text.Trim(), TyBox.Text.Trim(), request, fillLastIprj: false, _effectiveBrand, includeAttrOnly: true);
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
        DialogResult = true;
    }

    private void PartGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void PartGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.F4)
        {
            Confirm();
            e.Handled = true;
        }
    }
}
