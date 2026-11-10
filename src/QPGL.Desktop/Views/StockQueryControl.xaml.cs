﻿using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.Services;
using QPGL.Infrastructure;
using QPGL.Desktop.ViewModels;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.Views;

/// <summary>库存查询页：三条件输入 500ms 防抖实时查询 + 回车立即查询 + 无限下滑加载 + 双击/回车打开修改窗（2026-11-09）。</summary>
public partial class StockQueryControl : UserControl
{
    private readonly StockQueryViewModel _vm = new();
    private ScrollViewer? _scrollViewer;

    /// <summary>滑块（Thumb）拖动中：拖住垂直滚动条期间禁止触发加载——追加内容后 thumb 追随鼠标被持续推到底，
    /// Offset 持续变化会绕过 VerticalChange 守卫连发加载（页面自动下滑）。</summary>
    private bool _thumbDragging;

    /// <summary>条件防抖定时器（500ms；必须显式订阅 Tick，仅定义不订阅不会触发——历史教训）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _debounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    public StockQueryControl()
    {
        InitializeComponent();
        DataContext = _vm;
        // DataGrid 无 ScrollChanged 直挂属性——ScrollViewer.ScrollChanged 路由事件从内层 ScrollViewer 冒泡，AddHandler 接
        Grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Grid_ScrollChanged));
        // 滚动条 Thumb 拖动状态（Thumb 路由事件从 ScrollBar 内部冒泡）：拖动中禁止触发加载，松手补一次到底检查
        Grid.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => _thumbDragging = true));
        Grid.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
            {
                _thumbDragging = false;
                TryLoadMoreAtBottom();
            }));
        // 警戒两列就地编辑（2026-11-09）：进入编辑全选+数字白名单，提交校验整数并落库 tbistojj
        Grid.PreparingCellForEdit += Grid_PreparingCellForEdit;
        Grid.CellEditEnding += Grid_CellEditEnding;
        _debounce.Tick += async (_, _) =>
        {
            if (_vm.IsBusy)
            {
                // 正在加载（如滚动追加中）：延后 500ms 重试，避免条件变更查询被静默丢弃
                _debounce.Stop();
                _debounce.Start();
                return;
            }
            _debounce.Stop();
            await _vm.SearchAsync(System.Threading.CancellationToken.None);
        };
        Loaded += async (_, _) => await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>是否警戒可编辑列（网格其余列全只读，用列 IsReadOnly 判定最稳）。</summary>
    private static bool IsWarnColumn(DataGridColumn? column) => column is { IsReadOnly: false };

    /// <summary>警戒单元格进入编辑：自动全选 + 仅数字白名单（警戒值整数口径，小数点/粘贴含小数由提交校验拦截）。</summary>
    private void Grid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (!IsWarnColumn(e.Column) || e.EditingElement is not TextBox box) return;
        box.SelectAll();
        box.PreviewTextInput += (_, ev) => ev.Handled = !IntegerInput.IsAllowedInput(ev.Text);
    }

    /// <summary>警戒单元格提交：整数校验（失败弹提示+取消+还原原值），成功 fire-and-forget 落库（复用 B5 UpdateJjLineAsync：
    /// UPDATE 双 0 删行 / 无行 INSERT；行对象已写回 → WarnLevel 通知 → 行色即时刷新）。</summary>
    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || !IsWarnColumn(e.Column)) return;
        if (e.EditingElement is not TextBox box || e.Row.Item is not StockRow row) return;
        var isUpper = e.Column.Header as string == "警戒上限";
        var isLower = e.Column.Header as string == "警戒下限";
        if (!isUpper && !isLower) return;

        var old = isUpper ? row.Amjj : row.Bmjj;
        if (!IntegerInput.IsInteger(box.Text.Trim(), out var value) || value < 0)
        {
            MessageBox.Show("警戒值必须是大于等于 0 的整数（不能带小数）。", "警戒值录入",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Cancel = true;
            box.Text = old.ToString("0");
            box.SelectAll();
            return;
        }
        if (value == old) return;   // 未变：不落库
        _ = SaveJjAsync(row, isUpper, value);
    }

    private async Task SaveJjAsync(StockRow row, bool isUpper, decimal value)
    {
        try
        {
            await Db.PriceAdjust.UpdateJjLineAsync(AppSession.DeptNo, row.Cp, row.Nno,
                isUpper ? value : null, isUpper ? null : value);
            _vm.Message = $"警戒{(isUpper ? "上限" : "下限")}已保存：{row.Nno.Trim()} = {value:0}"
                + (value == 0 && (isUpper ? row.Bmjj : row.Amjj) == 0 ? "（双零已删除警戒行）" : "");
        }
        catch (Exception ex)
        {
            _vm.Message = "警戒值保存失败：" + ex.Message;
        }
    }

    /// <summary>任一条件框文本变化 → 重置 500ms 防抖（防抖到期自动查询）。</summary>
    private void TxtCond_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async void Grid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer sv) _scrollViewer = sv;
        if (_vm.IsBusy || !_vm.HasMore) return;
        // 只有用户真实滚动动作（VerticalOffset 变化）才触发加载；
        // 追加行引起的 ExtentHeight 变化（VerticalChange=0）不再自feed
        if (e.VerticalChange == 0) return;
        // 拖动滑块期间不加载（thumb 追随鼠标 + 内容追加 = Offset 持续变化，会连发加载；松手时 DragCompleted 补载）
        if (_thumbDragging) return;
        TryLoadMoreAtBottom();
    }

    /// <summary>距底部 24px 内则追加下一批（IsBusy 守卫在 LoadMoreAsync 同步段内置位，重复调用安全）。</summary>
    private void TryLoadMoreAtBottom()
    {
        if (_scrollViewer is null || _vm.IsBusy || !_vm.HasMore) return;
        if (_scrollViewer.VerticalOffset + _scrollViewer.ViewportHeight >= _scrollViewer.ExtentHeight - 24)
            _ = _vm.LoadMoreAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>双击配件行打开修改窗（命中行才触发，列头/空白区不弹；双击警戒可编辑列 → 让 DataGrid 进入就地编辑，不开窗）。</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)e.OriginalSource).DataContext is not StockRow) return;
        // OriginalSource 向上找所属单元格：可编辑列（警戒上限/下限）交回 DataGrid 编辑
        var source = (DependencyObject)e.OriginalSource;
        while (source is not null and not DataGridCell)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        if (source is DataGridCell { Column.IsReadOnly: false }) return;
        // 双击处理器的鼠标捕获/激活与 ShowDialog 窗口激活竞争 → 弹窗"出现→被盖→前置"闪一帧，
        // Dispatcher 延迟一拍脱离鼠标消息上下文（2026-11-09 用户反馈修复）
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            () => _vm.EditCommand.Execute(null));
    }

    /// <summary>回车打开修改窗。</summary>
    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SelectedRow is not null)
        {
            e.Handled = true;
            _vm.EditCommand.Execute(null);
        }
    }

    /// <summary>三条件框回车=立即查询（跳过防抖）；←/→ 方向键边界切换上/下一框并全选（销售退货页同款）。</summary>
    private void TxtCond_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _debounce.Stop();
            _ = _vm.SearchAsync(System.Threading.CancellationToken.None);
            return;
        }
        var tb = (TextBox)sender;
        TextBox? target = null;
        if (e.Key == Key.Left && tb.SelectionStart == 0 && tb.SelectionLength == 0)
            target = TxtNno == tb ? null : TxtNa1 == tb ? TxtNno : TxtTy == tb ? TxtNa1 : null;
        else if (e.Key == Key.Right && tb.SelectionStart == tb.Text.Length && tb.SelectionLength == 0)
            target = TxtNno == tb ? TxtNa1 : TxtNa1 == tb ? TxtTy : null;
        if (target is null) return;
        e.Handled = true;
        target.Focus();
        target.SelectAll();
    }
}
