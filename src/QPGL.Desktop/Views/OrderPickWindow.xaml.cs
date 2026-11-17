using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Desktop.ViewModels;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.Views;

/// <summary>
/// 订单生成·选择配件窗口（2026-11-03 需求）：
/// 原订单生成页左下选料区（检索条件 + 选料结果表格）整体移入本窗口，页面通过明细区「选择配件」按钮打开。
/// 绑定共享 Order4ViewModel（查询条件 QNno/QNa1/QTy、结果 PickRows、选中 SelectedPick 均与页面同源）。
/// 三条件查询（编码=*前缀 //包含；名称=拼音码/编码/名称/车型；车型=车型拼音码/名称），输入即搜 500ms 防抖
/// （日期/出货量/库存区间变化同样防抖触发），←/→ 方向键在条件框间流转并全选，↓/↑ 驱动表格选行；
/// 回车/F4/双击选定配件 → 直接走订货流程（OrderLineAsync，弹订货录入框写入明细），
/// 完成后重查刷新「已订」列；窗口保持打开可连续选件，Esc/× 关闭（PartPickWindow 同模式）。
/// </summary>
public partial class OrderPickWindow : Window
{
    private readonly Order4ViewModel _vm;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // 用户滚动手势门（2026-11-17 需求：拖住滚动条贴底不再无限加载）：
    // 每次自动加载后必须有「新的」用户手势（滚轮/拖滚动条/键盘翻页）才允许下一次加载。
    // 贴底不动时列表增长产生的 ScrollChanged 不带新手势 → 不再触发，杜绝拖住贴底拉完全表。
    private DateTime _lastUserScrollGesture = DateTime.MinValue;
    private DateTime _lastLoadRequested = DateTime.MinValue;

    public OrderPickWindow(Order4ViewModel vm)
    {
        InitializeComponent();
        // 2026-11-13 需求：非模态且不挂 Owner —— 窗口可被主窗口/销售开单盖住，不再强制置顶
        _vm = vm;
        DataContext = vm;
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await _vm.SearchPartsAsync(System.Threading.CancellationToken.None);
        };
        Loaded += async (_, _) =>
        {
            if (_vm.PickRows.Count == 0)
                await _vm.SearchPartsAsync(System.Threading.CancellationToken.None);
            // 无限滑动：配件列表滚到底自动加载下一页（PartPickWindow 同机制）
            var sv = FindDescendantScrollViewer(PickGrid);
            if (sv is not null) sv.ScrollChanged += PickGrid_ScrollChanged;
            // 用户手势采样：滚动条拖动/按钮 + 滚轮（键盘导航在 PickGrid_PreviewKeyDown 里记录）
            PickGrid.AddHandler(System.Windows.Controls.Primitives.ScrollBar.ScrollEvent,
                new RoutedEventHandler((_, _) => _lastUserScrollGesture = DateTime.Now));
            PickGrid.PreviewMouseWheel += (_, _) => _lastUserScrollGesture = DateTime.Now;
            // 2026-11-17 需求：打开历史计划单的选择配件窗口时，自动定位到最后一次的已订行并选中
            await LocateLastOrderedLineAsync();
        };
        // 窗口激活时若焦点未落在选中行（Loaded 阶段定位的焦点被激活前的容器再生成带走），补一次聚焦保持蓝色选中
        Activated += (_, _) =>
        {
            if (!PickGrid.IsKeyboardFocusWithin && PickGrid.SelectedItem is not null) RestoreSelectedRowFocus();
        };
    }

    /// <summary>
    /// 定位到当前计划单最后一次已订的配件行并选中（2026-11-17 需求）。
    /// 目标 = 明细列表最后一行（最近一次订货）的编码；目标行不在已加载页时逐页加载直到命中或数据耗尽。
    /// 无明细（新单）时静默跳过。窗口复用（OpenPick 对已开窗口 Activate）时由 OpenPick 再次调用实现重新定位。
    /// </summary>
    public async Task LocateLastOrderedLineAsync()
    {
        var target = _vm.Lines.LastOrDefault()?.Nno?.Trim();
        if (string.IsNullOrEmpty(target)) return;
        try
        {
            // 逐页加载直到目标行出现或数据耗尽
            while (_vm.PickRows.All(r => !string.Equals(r.Nno?.Trim(), target, StringComparison.OrdinalIgnoreCase)))
            {
                var before = _vm.PickRows.Count;
                await _vm.LoadMorePartsAsync(System.Threading.CancellationToken.None);
                if (_vm.PickRows.Count == before) return;   // 没有新增 → 数据耗尽仍未命中
            }
            var row = _vm.PickRows.First(r => string.Equals(r.Nno?.Trim(), target, StringComparison.OrdinalIgnoreCase));
            _vm.SelectedPick = row;
            PickGrid.UpdateLayout();
            PickGrid.ScrollIntoView(row);
            if (PickGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow rr)
            {
                rr.ApplyTemplate();
                var cell = FindFirstCell(rr);
                if (cell is not null) cell.Focus();
                else rr.Focus();
            }
            else
            {
                PickGrid.Focus();
            }
            // Loaded 阶段窗口尚未激活且行容器可能再生成，键盘焦点易被带走（选中呈灰色）：
            // 延迟到输入优先级再补一次聚焦（此时窗口已激活、容器已稳定）
            PickGrid.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!PickGrid.IsKeyboardFocusWithin) RestoreSelectedRowFocus();
            }));
        }
        catch
        {
            // 定位失败不阻断（保持默认第 1 页视角）
        }
    }

    /// <summary>把键盘焦点恢复到当前选中行的单元格（蓝色选中态要求 DataGrid 持有键盘焦点）。</summary>
    private void RestoreSelectedRowFocus()
    {
        if (PickGrid.SelectedItem is null) return;
        PickGrid.UpdateLayout();
        PickGrid.ScrollIntoView(PickGrid.SelectedItem);
        if (PickGrid.ItemContainerGenerator.ContainerFromItem(PickGrid.SelectedItem) is DataGridRow rr)
        {
            rr.ApplyTemplate();
            var cell = FindFirstCell(rr);
            if (cell is not null) cell.Focus();
            else rr.Focus();
        }
        else
        {
            PickGrid.Focus();
        }
        if (!PickGrid.IsKeyboardFocusWithin) PickGrid.Focus();
    }

    private static ScrollViewer? FindDescendantScrollViewer(System.Windows.DependencyObject root)
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
    /// 选中指定行并把键盘焦点移入表格：蓝色选中态要求 DataGrid 持有键盘焦点（否则默认模板渲染灰色）。
    /// DataGridRow 自身 Focus 不可靠，焦点落到该行第一个单元格；全部失败再兜底 DataGrid.Focus()。
    /// </summary>
    private void FocusPickRow(int index)
    {
        if (index < 0 || PickGrid.Items.Count == 0) return;
        PickGrid.SelectedIndex = Math.Clamp(index, 0, PickGrid.Items.Count - 1);
        PickGrid.UpdateLayout();
        PickGrid.ScrollIntoView(PickGrid.SelectedItem);
        if (PickGrid.ItemContainerGenerator.ContainerFromItem(PickGrid.SelectedItem) is DataGridRow row)
        {
            row.ApplyTemplate();
            var cell = FindFirstCell(row);
            if (cell is not null) cell.Focus();
            else row.Focus();
        }
        else
        {
            PickGrid.Focus();
        }
        if (!PickGrid.IsKeyboardFocusWithin) PickGrid.Focus();
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

    /// <summary>滚动到距底部 2 行内即触发加载下一页；触发要求「上次加载之后有过新的用户滚动手势」。</summary>
    private async void PickGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 40 && e.VerticalChange != 0
            && _lastUserScrollGesture > _lastLoadRequested)
        {
            _lastLoadRequested = DateTime.Now;   // 消耗本次手势：下一次加载需新手势
            await _vm.LoadMorePartsAsync(System.Threading.CancellationToken.None);
        }
    }

    /// <summary>条件变化（输入）：重置防抖计时。</summary>
    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// 条件框键盘导航（PartPickWindow 同构）：↓/↑ 焦点留在条件框直接驱动表格选中行（首 ↓ 跳到第一行，
    /// 之后逐行移动，↑ 反向），回车确认选中行；←/→ 方向键在 编码→名称→车型 条件框间流转并全选
    /// （光标在边界时才切换，框内移动不受影响）。
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        switch (e.Key)
        {
            case Key.Down:
                if (PickGrid.Items.Count > 0)
                    FocusPickRow(PickGrid.SelectedIndex + 1);   // 焦点随选中行移入表格 → 蓝色选中态，后续 ↑/↓ 由表格原生导航
                e.Handled = true;
                break;
            case Key.Up:
                if (PickGrid.Items.Count > 0)
                    FocusPickRow(PickGrid.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (PickGrid.SelectedItem is not null) Confirm();
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

    /// <summary>选定配件 → 直接走订货流程；完成后原地刷新「已订」列并保持位置与选中，窗口保持打开。</summary>
    private async void Confirm()
    {
        if (PickGrid.SelectedItem is not PartPickRow row)
        {
            MessageBox.Show("请先选中一行配件", "选择配件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _vm.SelectedPick = row;
        try
        {
            await _vm.OrderLineAsync(System.Threading.CancellationToken.None);
            // 2026-11-17 需求：订货确认后保持原位与选中（订序号 415 确定后仍停留 415）。
            // 订货仅影响「已订」列（c_dhorder 同码合计，Lines 已随 AddLineCoreAsync 重载为最新），
            // 原地按 Nno 刷新即可；不再整表重查——重查会把列表重置回第 1 页（第 415 行在第 5 页），
            // 确认后窗口跳回 80-100 区间且选中丢失。
            var ordered = _vm.Lines.FirstOrDefault(l => l.Cno == _vm.CurrentCno && l.Nno == row.Nno)?.Oqty ?? 0;
            foreach (var r in _vm.PickRows.Where(r => r.Nno == row.Nno))
                r.Num = ordered;
            // 行未重建、位置与选中不变；确保键盘焦点留在行单元格（连续选件 ↑/↓ 导航可用）
            PickGrid.UpdateLayout();
            PickGrid.ScrollIntoView(row);
            if (PickGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow rr)
            {
                rr.ApplyTemplate();
                var cell = FindFirstCell(rr);
                if (cell is not null) cell.Focus();
                else rr.Focus();
            }
            else if (!PickGrid.IsKeyboardFocusWithin)
            {
                PickGrid.Focus();
            }
        }
        catch (OperationCanceledException)
        {
            // 不阻断
        }
    }

    private void PickGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击警戒值列 → 进入单元格编辑（2026-11-04 需求），不触发订货。
        // 双保险：当前列 + 点击命中列（OriginalSource 向上找 DataGridCell），任一为警戒列即跳过
        if (PickGrid.CurrentColumn?.Header is "警戒值A" or "警戒值B") return;
        if (e.OriginalSource is DependencyObject d &&
            FindAncestorCell(d)?.Column?.Header is "警戒值A" or "警戒值B") return;
        Confirm();
    }

    /// <summary>警戒值编辑中标记（PreparingCellForEdit 置位 / CellEditEnding 复位，供回车提交判断）。</summary>
    private bool _jjEditing;

    /// <summary>进入警戒值编辑自动全选（2026-11-04 需求）：双击进入编辑模式，文本全选便于整体覆盖输入。</summary>
    private void PickGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        var header = e.Column.Header as string;
        if (header is not ("警戒值A" or "警戒值B")) return;
        _jjEditing = true;
        if (e.EditingElement is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    /// <summary>警戒值单元格编辑提交（2026-11-04 需求）：校验数字≥0 后单档落库（tbistojj），失败恢复原值。</summary>
    private async void PickGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        _jjEditing = false;
        if (e.EditAction != DataGridEditAction.Commit) return;
        var header = e.Column.Header as string;
        var isA = header == "警戒值A";
        var isB = header == "警戒值B";
        if (!isA && !isB) return;
        if (e.Row.Item is not PartPickRow row) return;
        if (e.EditingElement is not TextBox tb) return;

        // 整数校验先于落库：小数/空值在此弹提示并还原原值，避免写进 tbistojj（2026-11-07 需求）
        if (e.Cancel || !IntegerInput.ValidateCell(e, "选择配件")) return;

        if (!decimal.TryParse(tb.Text.Trim(), out var value) || value < 0)
        {
            e.Cancel = true;   // 非法输入：取消本次编辑，单元格回退原值
            MessageBox.Show("请输入有效的警戒值（≥0 数字）。", "选择配件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var oldValue = isA ? row.Amjj : row.Bmjj;
        if (value == oldValue) return;
        try
        {
            await Db.PriceAdjust.UpdateJjLineAsync(AppSession.DeptNo, row.Cp, row.Nno,
                isA ? value : null, isA ? null : value);
            if (isA) row.Amjj = value; else row.Bmjj = value;   // INPC → 行色即时刷新
        }
        catch (Exception ex)
        {
            if (isA) row.Amjj = oldValue; else row.Bmjj = oldValue;
            MessageBox.Show("保存警戒值失败：" + ex.Message, "选择配件", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>从事件源向上找所在 DataGridCell。</summary>
    private static DataGridCell? FindAncestorCell(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is DataGridCell cell) return cell;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void PickGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _lastUserScrollGesture = DateTime.Now;   // 键盘导航也算用户滚动手势
        // 警戒值编辑中（2026-11-04 需求）：回车=确定提交（不触发订货），Esc 默认取消，F4 屏蔽
        if (_jjEditing)
        {
            if (e.Key == Key.Enter)
            {
                _jjEditing = false;
                PickGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            }
            if (e.Key is Key.Enter or Key.F4) e.Handled = true;
            return;
        }
        if (e.Key is Key.Enter or Key.F4)
        {
            Confirm();
            e.Handled = true;
        }
    }
}
