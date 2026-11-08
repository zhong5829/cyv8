using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.Services;
using QPGL.Desktop.ViewModels;
using QPGL.Domain.Parts;

namespace QPGL.Desktop.Views;

public partial class InStockControl : UserControl
{
    public InStockViewModel VM { get; }

    public InStockControl()
    {
        VM = new InStockViewModel();
        InitializeComponent();
        DataContext = VM;
        Loaded += async (_, _) =>
        {
            await VM.LoadedAsync(System.Threading.CancellationToken.None);
            // 2026-11-02 暂存需求：页面打开时检测本部门未生效暂存单，弹窗让用户选择恢复
            await TryRestoreDraftAsync();
        };
    }

    /// <summary>Loaded 可能多次触发（tab 切换），恢复检查只做一次。</summary>
    private bool _restoreChecked;

    private async System.Threading.Tasks.Task TryRestoreDraftAsync()
    {
        if (_restoreChecked) return;
        _restoreChecked = true;
        try
        {
            var drafts = await Db.InStock.GetDraftsAsync(AppSession.DeptNo);
            if (drafts.Count == 0) return;
            var pick = new InStockDraftPickWindow(drafts) { Owner = Window.GetWindow(this) };
            if (pick.ShowDialog() == true && !string.IsNullOrWhiteSpace(pick.SelectedCno))
                await VM.RestoreDraftAsync(pick.SelectedCno);
        }
        catch
        {
            // 恢复检查失败不阻断页面
        }
    }

    /// <summary>
    /// 切换暂存单（2026-11-02 需求）：重开未生效暂存单选择窗恢复另一张。
    /// 当前明细有未暂存改动时先三态询问（是=保存当前再选 / 否=丢弃 / 取消=不动）；
    /// 选回当前已加载的同一张单则不做任何变更。
    /// </summary>
    private async void SwitchDraftButton_Click(object sender, RoutedEventArgs e)
    {
        // 脏数据三态（与关闭检查同口径）：是=保存当前再切换，否=丢弃，取消=不切换
        if (VM.IsDirty && VM.Lines.Count > 0)
        {
            var answer = MessageBox.Show(
                "当前明细有未暂存的改动。\n\n是——保存当前暂存单后再切换\n否——丢弃改动直接切换\n取消——不切换",
                "切换暂存单", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes && !await VM.SaveDraftCoreAsync()) return;
        }
        try
        {
            var drafts = await Db.InStock.GetDraftsAsync(AppSession.DeptNo);
            if (drafts.Count == 0)
            {
                MessageBox.Show("没有未生效暂存单", "暂存单", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var pick = new InStockDraftPickWindow(drafts) { Owner = Window.GetWindow(this) };
            if (pick.ShowDialog() == true && !string.IsNullOrWhiteSpace(pick.SelectedCno))
            {
                if (string.Equals(pick.SelectedCno.Trim(), VM.Cno.Trim(), StringComparison.OrdinalIgnoreCase)) return;
                await VM.RestoreDraftAsync(pick.SelectedCno);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开暂存单列表失败：" + ex.Message, "暂存单", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 关闭前脏数据检查（2026-11-02 暂存需求，页签关闭与软件退出共用）：
    /// 是=保存暂存并关闭；否=丢弃关闭；取消=留在本页（返回 false 阻止关闭）。
    /// </summary>
    public async System.Threading.Tasks.Task<bool> ConfirmCloseAsync()
    {
        if (!VM.IsDirty || VM.Lines.Count == 0) return true;
        var answer = MessageBox.Show(
            "散件入库页面有未暂存的明细改动，直接关闭将丢失。\n\n是——保存暂存并关闭\n否——不保存直接关闭\n取消——留在本页",
            "散件入库", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes)
            return await VM.SaveDraftCoreAsync();
        return true;
    }

    /// <summary>
    /// 添加配件：弹配件选择窗口（三条件查询，双击选定）。2026-11-02 需求：窗口保持打开连续选件——
    /// 每次选定回调 AddPickedPartAsync 处理（编辑窗确认后写入明细行，选件窗不关闭），Esc 关闭结束。
    /// </summary>
    private void AddPartButton_Click(object sender, RoutedEventArgs e)
    {
        var pick = new PartPickWindow(AppSession.DeptNo) { Owner = Window.GetWindow(this) };
        pick.PartPicked += part => _ = AddPickedPartAsync(pick, part);
        pick.ShowDialog();
    }

    /// <summary>处理一个选定配件：重复添加询问修改，否则弹入库明细编辑窗（数量/进价/含税价等），确认后写入明细行。</summary>
    private async System.Threading.Tasks.Task AddPickedPartAsync(Window pick, PartItem part)
    {
        // 重复添加判定（qp11 AddLineViaDialogAsync 同构：按零件号）
        var existing = VM.Lines.FirstOrDefault(l => string.Equals(l.Nno.Trim(), part.Nno.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var go = MessageBox.Show(
                $"配件 {part.Nno}（{part.Na1}）已在明细中（数量 {existing.Jkamount:0.##}），是否修改该行？",
                "该配件已添加", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (go != MessageBoxResult.Yes) return;
            var edit = new InStockLineEditDialog(part, VM.Warehouses.ToList(), existing) { Owner = pick };
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

        var dialog = new InStockLineEditDialog(part, VM.Warehouses.ToList()) { Owner = pick };
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
    /// 明细网格直接改「进价(不含税)」时同步「含税价」（2026-11-08 修复：生效后「总进价」全为 0）。
    /// 与行编辑窗的 2026-11-02 口径一致（含税价自动同步同金额，不换算税率）；
    /// 不在网格侧同步的话，直接在单元格改价会让 Iprc 留在 0，生效后 Σ(iprc×数量) 即为 0，库存批次成本也被写成 0。
    /// </summary>
    private void Lines_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not InStockLineRow row) return;
        if (e.Column is not DataGridTextColumn col) return;
        if ((col.Binding as System.Windows.Data.Binding)?.Path?.Path != nameof(InStockLineRow.Iprj)) return;
        if (e.EditingElement is not TextBox tb) return;
        if (!decimal.TryParse(tb.Text.Trim(), out var iprj) || iprj < 0) return;
        row.Iprc = iprj;
    }

    /// <summary>明细表格 Del 键删除选中行（2026-11-02 需求）：与"删除行"按钮同一命令；单元格编辑态不拦截。</summary>
    private void Lines_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        // 单元格编辑中（正在删文本）不删行
        if (grid.ItemContainerGenerator.ContainerFromItem(grid.CurrentItem) is System.Windows.Controls.DataGridRow row && row.IsEditing) return;
        if (VM.SelectedLine is null) return;
        VM.RemoveLineCommand.Execute(null);
        e.Handled = true;
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

    private async void GynameBox_PreviewKeyDown(object sender, KeyEventArgs e)
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
                // 回车冲突处理（2026-11-07 需求）：联想下拉「有高亮项」时回车仍为选中该项（完全保持原行为）；
                // 下拉未展开，或已展开但无高亮（用户只用鼠标点了输入框、没按 ↑/↓ 定位，SelectedIndex 仍为 -1）时，
                // 才进入供应商名称检测。这样不会出现"想选中联想项却弹出新增询问"。
                if (GysPopup.IsOpen && GysList.SelectedItem is Domain.Partners.SupplierRow s)
                {
                    VM.ApplySupplier(s);
                    GysPopup.IsOpen = false;
                }
                else
                {
                    GysPopup.IsOpen = false;
                    await CheckSupplierNameAsync();
                }
                e.Handled = true;
                break;
            case Key.Escape:
                GysPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 供应商名称检测（2026-11-07 需求，回车触发）：按名称精确比对（Trim + 忽略大小写，不做忽略中间空格宽松匹配，
    /// 输入编号不算命中）；命中 → 回填名称+编号（等同联想选中，避免编号与名称不匹配）；
    /// 未命中 → 清空残留编号并询问「是否新增供应商」，确认后打开统一 SupplierEditWindow，
    /// 保存成功直接把新供应商的名称+编号回填（与联想选中效果一致）。
    /// </summary>
    private async System.Threading.Tasks.Task CheckSupplierNameAsync()
    {
        var name = VM.Gyname.Trim();
        if (name.Length == 0) return;
        Domain.Partners.SupplierRow? hit;
        try
        {
            hit = await VM.FindExactSupplierAsync(name);
        }
        catch (Exception ex)
        {
            VM.Message = "供应商校验失败：" + ex.Message;
            return;
        }
        if (hit is not null)
        {
            VM.ApplySupplier(hit);
            return;
        }

        VM.Gyno = "";   // 名称没有对应供应商：清掉可能残留的编号，避免编号与名称错配
        if (System.Windows.MessageBox.Show(
                $"供应商「{name}」不存在，是否新增？", "供应商",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes)
            return;   // 选「否」：保留输入，但入库时会被供应商存在性校验拦下

        var w = new SupplierEditWindow(null, name) { Owner = Window.GetWindow(this) };
        w.ShowDialog();          // 必须真正以模态方式显示；此前漏掉本调用，导致选"是"后窗口不出现
        if (!w.Saved) return;
        VM.ApplySupplier(new Domain.Partners.SupplierRow { Gyno = w.SavedGyno, Gyname = w.SavedGyname });
        GynameBox.Focus();
        GynameBox.CaretIndex = GynameBox.Text.Length;
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
