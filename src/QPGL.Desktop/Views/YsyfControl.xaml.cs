using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QPGL.Desktop.Services;
using QPGL.Desktop.ViewModels;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.Views;

/// <summary>应收应付页（G.4，fr_ysyf）：上=客户汇总，下=选中客户明细。</summary>
public partial class YsyfControl : UserControl
{
    private readonly YsyfViewModel _vm = new();

    public YsyfControl()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            // 添加/修改客户后：滚动到选中的新客户行（汇总区仅 2 行高，选中项在视口外时用户感知不到）
            if (e.PropertyName == nameof(YsyfViewModel.SelectedSummary) && _vm.SelectedSummary is not null)
                SummariesGrid.ScrollIntoView(_vm.SelectedSummary);
        };
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.SyncAsync(System.Threading.CancellationToken.None);
        await _vm.SearchAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>查询条回车 = 执行查询（2026-10-29：查询按钮已移除）。
    /// 用冒泡 KeyDown：客户联想框内有选中项时回车已在 PreviewKeyDown 被处理（确认选中不查询），其余回车冒泡到此触发查询。</summary>
    private void QueryBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    // 客户联想（同 SaleControl cmm_class row326 FRDM 下拉机制）：输入即搜（300ms 防抖），支持拼音码/数字码/编号/名称
    private void GnameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.DebounceCustomerSearch();
        CustPopup.IsOpen = GnameBox.Text.Length > 0;   // 清空时不开空弹窗
    }

    private void GnameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (CustList.Items.Count > 0)
                {
                    CustPopup.IsOpen = true;
                    // 焦点保持在输入框，手动下移选中项（避免焦点跳入列表后按键失效）
                    CustList.SelectedIndex = Math.Min(CustList.SelectedIndex + 1, CustList.Items.Count - 1);
                    CustList.ScrollIntoView(CustList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (CustList.Items.Count > 0 && CustList.SelectedIndex > 0)
                {
                    CustList.SelectedIndex--;
                    CustList.ScrollIntoView(CustList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (CustPopup.IsOpen && CustList.SelectedItem is Domain.Partners.Partner p)
                {
                    _ = _vm.ApplyCustomerAsync(p);
                    CustPopup.IsOpen = false;
                    e.Handled = true;   // 确认选中，不触发查询（KeyDown 冒泡被阻断）
                }
                else
                {
                    // 无联想选中：关弹窗并放行，KeyDown 冒泡到查询条触发查询
                    CustPopup.IsOpen = false;
                }
                break;
            case Key.Escape:
                CustPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void CustList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CustList.SelectedItem is Domain.Partners.Partner p)
        {
            _ = _vm.ApplyCustomerAsync(p);
            CustPopup.IsOpen = false;
            GnameBox.Focus();
            GnameBox.CaretIndex = GnameBox.Text.Length;
        }
    }

    /// <summary>明细行双击 = 业务明细跳转（fr_ysyf 网格双击习惯；勾选列除外，避免与勾选冲突）。</summary>
    private async void Details_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is not null && cell.Column == DetailsGrid.Columns[0]) return;
        if (_vm.SelectedDetail is null) return;
        await _vm.ShowBizDetailCommand.ExecuteAsync(null);
    }

    // ==== 明细勾选列：Shift+鼠标左键拖动连续勾选（沿用起点状态；收款/付款行无勾选框，跳过） ====
    private bool _draggingCheck;
    private bool _dragCheckValue;

    private void DetailsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift) return;
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is null || cell.Column != DetailsGrid.Columns[0]) return;
        if (cell.DataContext is not YsyfDetailRow row || !CanCheck(row)) return;
        _draggingCheck = true;
        _dragCheckValue = !row.IsChecked;   // 起点已勾 → 拖动取消；起点未勾 → 拖动全勾
        row.IsChecked = _dragCheckValue;
        DetailsGrid.CaptureMouse();
        e.Handled = true;
    }

    private void DetailsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingCheck) return;
        if (e.LeftButton != MouseButtonState.Pressed || Keyboard.Modifiers != ModifierKeys.Shift)
        {
            EndCheckDrag();
            return;
        }
        if (RowAt(e.GetPosition(DetailsGrid))?.Item is YsyfDetailRow row
            && CanCheck(row) && row.IsChecked != _dragCheckValue)
            row.IsChecked = _dragCheckValue;
        e.Handled = true;
    }

    private void DetailsGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingCheck) return;
        EndCheckDrag();
        e.Handled = true;
    }

    private void DetailsGrid_LostMouseCapture(object sender, MouseEventArgs e) => _draggingCheck = false;

    private void EndCheckDrag()
    {
        _draggingCheck = false;
        if (DetailsGrid.IsMouseCaptured) DetailsGrid.ReleaseMouseCapture();
    }

    /// <summary>收款/付款行不显示勾选框（2026-10-31 需求），拖动勾选同样跳过。</summary>
    private static bool CanCheck(YsyfDetailRow row)
    {
        var type = row.Type.Trim();
        return type != "收款" && type != "付款";
    }

    /// <summary>
    /// 图片导出（2026-10-31 需求，参考 qp11 销售明细）：勾选的明细行（无勾选则全部）导出为两张图片——
    /// 单据图（按单据号归并：日期取组内最早、类型/客户取首行、应收/应付/已收求和、结账全√才√）+
    /// 明细图（双击单据弹窗 fr_finopdet 中的配件明细：销售/调拨→SRO 出库明细、入库→BBI 入库明细，
    /// 列=单据号/编号/名称/车型/数量/单价/金额）；预览窗内复制到剪贴板。
    /// </summary>
    private async void BtnExportImage_Click(object sender, RoutedEventArgs e)
    {
        // 按明细网格当前显示顺序（日期倒序）取数：勾选优先，否则全部（同 qp11 口径）
        var rows = DetailsGrid.Items.OfType<YsyfDetailRow>().ToList();
        var checkedRows = rows.Where(r => r.IsChecked).ToList();
        var source = checkedRows.Count > 0 ? checkedRows : rows;
        if (source.Count == 0)
        {
            MessageBox.Show("没有可导出的明细行！", "应收应付", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 单据图：按单据号归并（GroupBy 保持首次出现顺序 = 明细显示顺序）
        var billRows = new List<string[]>();
        var billRed = new HashSet<int>();
        foreach (var g in source.GroupBy(r => r.Cno.Trim()))
        {
            var ys = g.Sum(r => r.Ys);
            var yf = g.Sum(r => r.Yf);
            billRows.Add(
            [
                g.Min(r => r.Datime).ToString("yyyy-MM-dd"),
                g.Key,
                g.First().Type.Trim(),
                g.First().Gname.Trim(),
                ys.ToString(), yf.ToString(), g.Sum(r => r.Skmoney).ToString(),
                g.All(r => r.Mark == "√") ? "√" : "",
            ]);
            if (ys < 0 || yf < 0) billRed.Add(billRows.Count - 1);
        }

        // 明细图：拉取每张单据的业务明细（配件行），口径同双击弹窗（ShowBizDetail）
        var detailRows = new List<string[]>();
        var detailRed = new HashSet<int>();
        decimal detailTotal = 0m;
        try
        {
            if (source.Any(r => r.Type.Trim() is "销售" or "调拨") && !AppSession.Permission.HasMenu("E2"))
            {
                MessageBox.Show("您没有查看出库明细的权限！", "应收应付", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (source.Any(r => r.Type.Trim() == "入库") && !AppSession.Permission.HasMenu("E1"))
            {
                MessageBox.Show("您没有查看入库明细的权限！", "应收应付", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 去重取单据键：销售/调拨按 cno（SRO）；入库按 cno+isord（BBI）；维修/收款/付款无配件明细跳过
            var billKeys = source
                .Select(r => (Cno: r.Cno.Trim(), Isord: r.Isord.Trim(), Type: r.Type.Trim()))
                .Distinct().ToList();
            foreach (var (cno, isord, type) in billKeys)
            {
                IReadOnlyList<FinOpDetailRow> lines = type switch
                {
                    "销售" or "调拨" => await Db.FinOp.GetOpDetailAsync("SRO", cno),
                    "入库" => await Db.FinOp.GetOpDetailBySeanoAsync(cno, isord),
                    _ => [],
                };
                foreach (var d in lines)
                {
                    detailRows.Add([cno, d.Date.ToString("yyyy-MM-dd"), d.Nno, d.Na1, d.Ty,
                        d.Amount.ToString(), d.Oprc.ToString(), d.Jine.ToString(), d.Per.Trim()]);
                    detailTotal += d.Jine;
                    if (d.Amount < 0 || d.Jine < 0) detailRed.Add(detailRows.Count - 1);   // 退货（负数）行红字
                }
            }
        }
        catch (AppException ex)
        {
            _vm.Message = ex.Message;
            return;
        }

        if (detailRows.Count == 0)
        {
            MessageBox.Show("所选单据没有业务明细！", "应收应付", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var billCount = source.GroupBy(r => r.Cno.Trim()).Count();

        // 副标题（2026-10-31 需求）：勾选行的日期范围 + 共计金额，两张图都加
        var dMin = source.Min(r => r.Datime).Date;
        var dMax = source.Max(r => r.Datime).Date;
        var dateRange = dMin == dMax
            ? $"{dMin:yyyy年M月d号}"
            : $"{dMin:yyyy年M月d号}-{dMax:yyyy年M月d号}";
        var summary = $"{(checkedRows.Count > 0 ? "勾选" : "共")} {billCount} 单 / {source.Count} 行，" +
                      $"应收 {source.Sum(r => r.Ys):N2}，应付 {source.Sum(r => r.Yf):N2}，已收 {source.Sum(r => r.Skmoney):N2}";
        var win = new ImageExportPreviewWindow(
            _vm.SelectedSummary is null ? "应收应付" : _vm.SelectedSummary.Gname.Trim(),
            summary,
            new ImageExportSheet
            {
                Title = "单据",
                Subtitle = $"{dateRange}  共计金额 {source.Sum(r => r.Ys - r.Yf):N2}",
                Headers = ["日期", "单据号", "类型", "客户", "应收", "应付", "已收", "结账"],
                Rows = billRows, RedRows = billRed, SumHeaders = ["应收", "应付", "已收"],
            },
            new ImageExportSheet
            {
                Title = "明细",
                Subtitle = $"{dateRange}  共计金额 {detailTotal:N2}",
                Headers = ["单据号", "日期", "编号", "名称", "车型", "数量", "单价", "金额", "营业员"],
                Rows = detailRows, RedRows = detailRed, SumHeaders = ["数量", "金额"],
            })
        { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    /// <summary>命中测试定位鼠标下的明细行（同 XlskControl）。</summary>
    private DataGridRow? RowAt(Point p)
    {
        DataGridRow? row = null;
        VisualTreeHelper.HitTest(DetailsGrid, null, r =>
        {
            var found = FindAncestor<DataGridRow>(r.VisualHit);
            if (found is null) return HitTestResultBehavior.Continue;
            row = found;
            return HitTestResultBehavior.Stop;
        }, new PointHitTestParameters(p));
        return row;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
