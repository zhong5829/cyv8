using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.Views;

/// <summary>库存调价（B4）+ 警戒线设置（B5，2026-11-04 改手动逐行编辑）。</summary>
public partial class PriceAdjustControl : UserControl
{
    private readonly PriceAdjustViewModel _vm = new();

    public PriceAdjustControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.LoadAsync(System.Threading.CancellationToken.None);
        await _vm.LoadJjAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>警戒值编辑中标记（PreparingCellForEdit 置位 / CellEditEnding 复位，供回车提交判断）。</summary>
    private bool _jjEditing;

    /// <summary>进入警戒值编辑自动全选（2026-11-04 需求）：双击进入编辑模式，文本全选便于整体覆盖输入。</summary>
    private void JjGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
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

    /// <summary>查询条件框 ←/→ 边界流转并全选、聚焦全选（2026-11-04 需求，选料窗同款）。</summary>
    private void JjSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var next = e.Key switch
        {
            Key.Left when box.CaretIndex == 0 => box == JjNa1Box ? JjCpBox : box == JjTyBox ? JjNa1Box : null,
            Key.Right when box.CaretIndex == box.Text.Length => box == JjCpBox ? JjNa1Box : box == JjNa1Box ? JjTyBox : null,
            _ => null,
        };
        if (next is null) return;
        e.Handled = true;
        next.Focus();
        next.SelectAll();
    }

    /// <summary>条件框聚焦即全选（覆盖输入）。</summary>
    private void JjSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    /// <summary>回车确定（2026-11-04 需求）：提交单元格编辑触发落库，焦点留在原单元格。</summary>
    private void JjGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || !_jjEditing) return;
        _jjEditing = false;
        JjGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        e.Handled = true;
    }

    /// <summary>警戒值单元格编辑提交（B5 手动编辑）：校验数字≥0 后单档落库。</summary>
    private async void JjGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        _jjEditing = false;
        if (e.EditAction != DataGridEditAction.Commit) return;
        var header = e.Column.Header as string;
        var isA = header == "警戒值A";
        var isB = header == "警戒值B";
        if (!isA && !isB) return;
        if (e.Row.Item is not JjLineRow row) return;
        if (e.EditingElement is not TextBox tb) return;

        if (!decimal.TryParse(tb.Text.Trim(), out var value) || value < 0)
        {
            e.Cancel = true;   // 非法输入：取消本次编辑，单元格回退原值
            MessageBox.Show("请输入有效的警戒值（≥0 数字）。", "警戒线设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var oldValue = isA ? row.Amjj : row.Bmjj;
        if (value == oldValue) return;   // 未变化不落库
        await _vm.CommitJjEditAsync(row, isA, value, oldValue);
    }
}
