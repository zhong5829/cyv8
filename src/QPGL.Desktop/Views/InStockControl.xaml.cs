using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.Services;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class InStockControl : UserControl
{
    public InStockViewModel VM { get; }

    public InStockControl()
    {
        VM = new InStockViewModel();
        InitializeComponent();
        DataContext = VM;
        Loaded += async (_, _) => await VM.LoadedAsync(System.Threading.CancellationToken.None);
    }

    /// <summary>
    /// 添加配件：先弹配件选择窗口（三条件查询，双击选定），
    /// 再弹入库明细编辑窗口（数量/进价/售价/批发价/库号/仓位/分类码/产地/单位/含税价/备注），确认后写入明细行。
    /// 同配件已在明细中 → 询问是否修改：确认则重开编辑窗回填该行当前值，确认后覆盖原行（不新增行）。
    /// </summary>
    private async void AddPartButton_Click(object sender, RoutedEventArgs e)
    {
        var pick = new PartPickWindow(AppSession.DeptNo) { Owner = Window.GetWindow(this) };
        if (pick.ShowDialog() != true || pick.SelectedPart is null) return;
        var part = pick.SelectedPart;

        // 重复添加判定（qp11 AddLineViaDialogAsync 同构：按零件号）
        var existing = VM.Lines.FirstOrDefault(l => string.Equals(l.Nno.Trim(), part.Nno.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var go = MessageBox.Show(
                $"配件 {part.Nno}（{part.Na1}）已在明细中（数量 {existing.Jkamount:0.##}），是否修改该行？",
                "该配件已添加", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (go != MessageBoxResult.Yes) return;
            var edit = new InStockLineEditDialog(part, VM.Warehouses.ToList(), existing) { Owner = Window.GetWindow(this) };
            if (edit.ShowDialog() != true) return;
            VM.UpdatePartRow(existing, new InStockLineEditResult
            {
                Jkamount = edit.Jkamount, Iprc = edit.Iprc, Iprj = edit.Iprj,
                Oprc = edit.Oprc, Oprb = edit.Oprb,
                Depotno = edit.Depotno, Posi = edit.Posi,
                Fa = edit.Fa, Dc = edit.Dc, Unit = edit.Unit, Ty = edit.Ty, Note = edit.Note,
            });
            return;
        }

        var dialog = new InStockLineEditDialog(part, VM.Warehouses.ToList()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        VM.AddPartRow(part, new InStockLineEditResult
        {
            Jkamount = dialog.Jkamount, Iprc = dialog.Iprc, Iprj = dialog.Iprj,
            Oprc = dialog.Oprc, Oprb = dialog.Oprb,
            Depotno = dialog.Depotno, Posi = dialog.Posi,
            Fa = dialog.Fa, Dc = dialog.Dc, Unit = dialog.Unit, Ty = dialog.Ty, Note = dialog.Note,
        });
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// 表格导入（qp11 采购进货 ExcelImportDialog 同构）：弹导入对话框（xls/xlsx 解析 + 列映射 + 预览状态），
    /// 确认后行集写入明细网格（属性库命中带出名称/车型/进价，未命中新配件生效时自动建档）。
    /// </summary>
    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InStockImportDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || !dlg.ImportedRows.Any()) return;

        foreach (var item in dlg.ImportedRows)
            VM.AddImportedRow(item);
    }

    /// <summary>
    /// 双击明细行 = 弹入库编辑窗修改该行（2026-10-29 需求，与「重复添加询问修改」同一编辑窗）：
    /// 以行值构造最小 PartItem 传入编辑模式（预填该行当前值），确认后 UpdatePartRow 回写。
    /// </summary>
    private void Lines_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VM.SelectedLine is not InStockLineRow row) return;
        var part = new Domain.Parts.PartItem
        {
            Cp = row.Cp, Nno = row.Nno, Na1 = row.Na1,
            Depotno = row.Depotno, Posi = row.Posi,
        };
        var edit = new InStockLineEditDialog(part, VM.Warehouses.ToList(), row) { Owner = Window.GetWindow(this) };
        if (edit.ShowDialog() != true) return;
        VM.UpdatePartRow(row, new InStockLineEditResult
        {
            Jkamount = edit.Jkamount, Iprc = edit.Iprc, Iprj = edit.Iprj,
            Oprc = edit.Oprc, Oprb = edit.Oprb,
            Depotno = edit.Depotno, Posi = edit.Posi,
            Fa = edit.Fa, Dc = edit.Dc, Unit = edit.Unit, Ty = edit.Ty, Note = edit.Note,
        });
    }

    // 供应商联想（旧 cmm_class row326 FRDM 下拉机制，与 SaleControl GnameBox 同构）：
    // 输入即搜（300ms 防抖），支持拼音码/数字码/编号/名称
    private void GynameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        VM.DebounceSupplierSearch();
        GysPopup.IsOpen = true;
    }

    private void GynameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (GysList.Items.Count > 0)
                {
                    GysPopup.IsOpen = true;
                    GysList.SelectedIndex = System.Math.Min(GysList.SelectedIndex + 1, GysList.Items.Count - 1);
                    GysList.ScrollIntoView(GysList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (GysList.Items.Count > 0 && GysList.SelectedIndex > 0)
                {
                    GysList.SelectedIndex--;
                    GysList.ScrollIntoView(GysList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (GysPopup.IsOpen && GysList.SelectedItem is Domain.Partners.SupplierRow s)
                {
                    VM.ApplySupplier(s);
                    GysPopup.IsOpen = false;
                }
                else
                {
                    GysPopup.IsOpen = false;
                }
                e.Handled = true;
                break;
            case Key.Escape:
                GysPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private async void GysList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GysList.SelectedItem is Domain.Partners.SupplierRow s)
        {
            VM.ApplySupplier(s);
            GysPopup.IsOpen = false;
            GynameBox.Focus();
            GynameBox.CaretIndex = GynameBox.Text.Length;
        }
        await System.Threading.Tasks.Task.CompletedTask;
    }
}
