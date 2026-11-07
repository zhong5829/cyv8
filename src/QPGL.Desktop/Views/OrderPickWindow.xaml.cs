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

    public OrderPickWindow(Order4ViewModel vm)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
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
        };
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

    /// <summary>滚动到距底部 2 行内即触发加载下一页。</summary>
    private async void PickGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 40 && e.VerticalChange != 0)
            await _vm.LoadMorePartsAsync(System.Threading.CancellationToken.None);
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

    /// <summary>选定配件 → 直接走订货流程；完成后重查刷新已订数，窗口保持打开。</summary>
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
            await _vm.SearchPartsAsync(System.Threading.CancellationToken.None);
            // 重查会整表重建 PickRows 导致选中丢失：按编号恢复选中并滚动到可见，保证连续选件
            var again = _vm.PickRows.FirstOrDefault(r => r.Nno == row.Nno);
            if (again is not null)
            {
                _vm.SelectedPick = again;
                PickGrid.ScrollIntoView(again);
            }
            // 订货弹窗关闭 + 列表重建后键盘焦点掉到窗外：选中行呈灰色（失焦态）且 ↑/↓ 失效。
            // 把焦点显式还回选中行单元格（Input 优先级等容器生成完毕），恢复蓝色选中与方向键导航
            PickGrid.Dispatcher.BeginInvoke(() =>
            {
                if (PickGrid.SelectedItem is null) return;
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
            }, DispatcherPriority.Input);
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
