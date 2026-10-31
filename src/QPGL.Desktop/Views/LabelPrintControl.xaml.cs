using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QPGL.Desktop.Services;
using QPGL.Desktop.Services.LabelPrint;

namespace QPGL.Desktop.Views;

/// <summary>标签打印行模型（qp11 LabelPrintRow 复刻；默认不勾选，文本属性支持变更通知）</summary>
public class LabelPrintRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private bool _isSelected;
    private string? _partNo;
    private string? _name;
    private string? _carType;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnChanged(nameof(IsSelected));
            }
        }
    }
    public string? PartNo
    {
        get => _partNo;
        set
        {
            if (_partNo != value)
            {
                _partNo = value;
                OnChanged(nameof(PartNo));
            }
        }
    }
    public string? Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnChanged(nameof(Name));
            }
        }
    }
    public string? CarType
    {
        get => _carType;
        set
        {
            if (_carType != value)
            {
                _carType = value;
                OnChanged(nameof(CarType));
            }
        }
    }
    public string? Place { get; set; }
    public decimal? Amount { get; set; }

    private void OnChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 标签打印独立页（qp11 LabelPrintWindow 复刻，适配本系统标签页宿主改为 UserControl）：
/// 三条件实时查询（防抖）、左右方向键切换输入框并自动全选、
/// 车型可编辑（仅本次打印）、模板/份数/打印机设置、勾选打印。
/// 预览画布由 LabelPreviewEditor 提供（标尺/拖动/字号/旋转/加粗/颜色/双击修改/180°）。
/// 数据源 tbisto（nno 去重），按 AppSession.DeptNo + 当前厂牌过滤（用户确认口径）。
/// </summary>
public partial class LabelPrintControl : UserControl
{
    private CancellationTokenSource? _searchCts;
    private readonly ObservableCollection<LabelPrintRow> _items = new();
    /// <summary>抑制勾选批量更新时的重复预览刷新</summary>
    private bool _updatingSelection;
    private bool _uiReady;

    public LabelPrintControl()
    {
        InitializeComponent();
        dgItems.ItemsSource = _items;
        dgItems.SelectionChanged += (_, _) => RefreshPreview();   // 无勾选时预览跟随当前高亮行
        editor.ItemsChanged += (_, _) => SyncFromEditor();
        txtScope.Text = $"（自动查询 · 左右方向键切换输入框 · 厂牌：{(string.IsNullOrWhiteSpace(AppSession.Brand) ? "全部" : AppSession.Brand)}）";
        LoadTemplates();       // 下拉选中会触发 SelectionChanged → RefreshPreview，此时 _uiReady=false 被跳过
        LoadPrinters();
        ApplyPrinterBinding();
        _uiReady = true;
        _ = LoadAsync();       // 首屏数据
    }

    // ── 查询 ──

    private async void Txt_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            await Task.Delay(300, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _items.Clear();
            dgItems.UnselectAll();
            var data = await Db.Parts.GetLabelItemsAsync(
                AppSession.DeptNo, AppSession.Brand,
                txtPartNo.Text, txtName.Text, txtCarType.Text);
            foreach (var d in data)
            {
                var row = new LabelPrintRow
                {
                    IsSelected = false,   // 默认不勾选
                    PartNo = d.Nno,
                    Name = d.Na1,
                    CarType = d.Ty,
                    Place = d.Posi,
                    Amount = d.Kcamount
                };
                row.PropertyChanged += Item_PropertyChanged;
                _items.Add(row);
            }
            chkAll.IsChecked = false;
            txtStatus.Text = $"共 {_items.Count} 条 · 标签按 仓位→零件编码 排序输出";
            RefreshPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"查询失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── 输入框交互：自动全选 + 左右方向键切换 ──

    private void Txt_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.Dispatcher.BeginInvoke(() => tb.SelectAll(), DispatcherPriority.Input);
    }

    private void Txt_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        bool atStart = tb.CaretIndex == 0;
        bool atEnd = tb.CaretIndex >= tb.Text.Length;
        bool allSelected = tb.SelectionLength > 0 && tb.SelectionLength == tb.Text.Length;

        // 光标在端部或全选状态时，左右方向键切换输入框
        if (e.Key == Key.Left && (atStart || allSelected))
        {
            e.Handled = true;
            MoveFocus(-1);
        }
        else if (e.Key == Key.Right && (atEnd || allSelected))
        {
            e.Handled = true;
            MoveFocus(1);
        }
    }

    private void MoveFocus(int delta)
    {
        var boxes = new[] { txtPartNo, txtName, txtCarType };
        int idx = -1;
        for (int i = 0; i < boxes.Length; i++)
        {
            if (boxes[i].IsKeyboardFocusWithin) { idx = i; break; }
        }
        if (idx < 0) return;
        int next = idx + delta;
        if (next >= 0 && next < boxes.Length)
            boxes[next].Dispatcher.BeginInvoke(() =>
            {
                boxes[next].Focus();
                boxes[next].SelectAll();
            }, DispatcherPriority.Input);
    }

    // ── 模板 ──

    private void LoadTemplates()
    {
        cboTemplate.Items.Clear();
        foreach (var tpl in LabelTemplateService.GetAll())
            cboTemplate.Items.Add(tpl);
        if (cboTemplate.Items.Count > 0)
            cboTemplate.SelectedIndex = 0;
    }

    private void Template_Changed(object sender, SelectionChangedEventArgs e)
    {
        RefreshPreview();
        ApplyPrinterBinding();
    }

    /// <summary>切换/加载模板时联动打印机：当前模板有绑定则自动选中该打印机（用户此后可手动改选，本次打印以最终下拉为准）</summary>
    private void ApplyPrinterBinding()
    {
        if (cboTemplate.SelectedItem is not LabelTemplate tpl) { UpdateBindButton(); return; }
        var bound = LabelTemplateService.GetBoundPrinter(tpl.Name);
        if (!string.IsNullOrEmpty(bound))
        {
            for (int i = 0; i < cboPrinter.Items.Count; i++)
            {
                if (cboPrinter.Items[i]?.ToString() == bound)
                {
                    cboPrinter.SelectedIndex = i;
                    break;
                }
            }
        }
        UpdateBindButton();
    }

    /// <summary>绑定按钮文案随状态切换：未绑定→「绑定到模板」，已绑定→「解除绑定」</summary>
    private void UpdateBindButton()
    {
        if (cboTemplate.SelectedItem is not LabelTemplate tpl)
        {
            btnBindPrinter.Content = "绑定到模板";
            return;
        }
        btnBindPrinter.Content = string.IsNullOrEmpty(LabelTemplateService.GetBoundPrinter(tpl.Name)) ? "绑定到模板" : "解除绑定";
    }

    private void BtnBindPrinter_Click(object sender, RoutedEventArgs e)
    {
        if (cboTemplate.SelectedItem is not LabelTemplate tpl)
        {
            MessageBox.Show("请先选择模板", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 已绑定 → 解除
        if (!string.IsNullOrEmpty(LabelTemplateService.GetBoundPrinter(tpl.Name)))
        {
            LabelTemplateService.ClearPrinterBinding(tpl.Name);
            UpdateBindButton();
            MessageBox.Show("已解除该模板的打印机绑定", "提示");
            return;
        }

        // 未绑定 → 将当前下拉打印机绑定到当前模板
        var printerName = cboPrinter.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(printerName))
        {
            MessageBox.Show("请先选择打印机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LabelTemplateService.SavePrinterBinding(tpl.Name, printerName);
        UpdateBindButton();
        MessageBox.Show($"已绑定：模板「{tpl.Name}」 → {printerName}", "提示");
    }

    private void BtnNewTemplate_Click(object sender, RoutedEventArgs e)
    {
        var cur = cboTemplate.SelectedItem as LabelTemplate;
        var dlg = new TemplateSpecWindow(
            cur == null ? "" : cur.Name + " 副本",
            cur?.LabelWidthMm ?? 50,
            cur?.LabelHeightMm ?? 30);
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() != true) return;

        // 继承当前模板的字号，按新尺寸生成默认字段布局
        var tpl = cur?.Clone() ?? new LabelTemplate();
        tpl.Name = dlg.TemplateName;
        tpl.LabelWidthMm = dlg.WidthMm;
        tpl.LabelHeightMm = dlg.HeightMm;
        tpl.IsBuiltIn = false;
        tpl.Fields = new System.Collections.Generic.List<LabelField>();
        tpl.EnsureFields();

        if (!LabelTemplateService.AddCustom(tpl))
        {
            MessageBox.Show("模板名称已存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LoadTemplates();
        SelectTemplate(tpl.Name);
    }

    private void BtnSaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (cboTemplate.SelectedItem is not LabelTemplate cur)
        {
            MessageBox.Show("请先选择模板", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (cur.IsBuiltIn)
        {
            MessageBox.Show("内置模板不可保存，请用「新建」创建自定义模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!LabelTemplateService.SaveCustom(cur))
        {
            MessageBox.Show("保存失败：未找到同名自定义模板，请用「新建」创建。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show("模板已保存，可直接复用", "提示");
    }

    /// <summary>按名称选中下拉模板</summary>
    private void SelectTemplate(string name)
    {
        for (int i = 0; i < cboTemplate.Items.Count; i++)
        {
            if (cboTemplate.Items[i] is LabelTemplate t && t.Name == name)
            {
                cboTemplate.SelectedIndex = i;
                break;
            }
        }
    }

    private void BtnDeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (cboTemplate.SelectedItem is not LabelTemplate cur)
        {
            MessageBox.Show("请先选择模板", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"确定删除模板「{cur.Name}」吗？", "删除模板", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (!LabelTemplateService.DeleteTemplate(cur.Name))
        {
            MessageBox.Show("删除失败", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LoadTemplates();
        MessageBox.Show("已删除", "提示");
    }

    // ── 打印机与份数 ──

    private void LoadPrinters()
    {
        cboPrinter.Items.Clear();
        var printServer = new System.Printing.LocalPrintServer();
        foreach (var queue in printServer.GetPrintQueues())
            cboPrinter.Items.Add(queue.Name);

        // 默认打印机：模板绑定 → 上次使用 → 第一台
        var last = LabelTemplateService.LoadLastPrinter();
        if (!string.IsNullOrEmpty(last))
        {
            for (int i = 0; i < cboPrinter.Items.Count; i++)
            {
                if (cboPrinter.Items[i]?.ToString() == last)
                {
                    cboPrinter.SelectedIndex = i;
                    break;
                }
            }
        }
        if (cboPrinter.SelectedIndex < 0 && cboPrinter.Items.Count > 0)
            cboPrinter.SelectedIndex = 0;
    }

    private int CurrentCopies()
    {
        if (int.TryParse(txtCopies.Text, out var c) && c > 0 && c <= 9999) return c;
        return 1;
    }

    private void Copies_Changed(object sender, TextChangedEventArgs e)
        => RefreshPreview();

    private void ChkAll_Changed(object sender, RoutedEventArgs e)
    {
        var all = chkAll.IsChecked == true;
        _updatingSelection = true;
        try
        {
            foreach (var item in _items)
                item.IsSelected = all;
        }
        finally
        {
            _updatingSelection = false;
        }
        RefreshPreview();
    }

    /// <summary>手动勾选/取消勾选时实时刷新预览</summary>
    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LabelPrintRow.IsSelected) && !_updatingSelection)
            RefreshPreview();
    }

    /// <summary>勾选列点击：立即提交单元格（否则要等失去焦点才写回）并刷新预览</summary>
    private void ChkItem_Click(object sender, RoutedEventArgs e)
    {
        dgItems.CommitEdit(DataGridEditingUnit.Cell, true);
        dgItems.CommitEdit(DataGridEditingUnit.Row, true);
        RefreshPreview();
    }

    // ── 预览同步（LabelPreviewEditor） ──

    private void RefreshPreview()
    {
        // 构造期模板下拉触发的 SelectionChanged 回调直接跳过（UI 未就绪）
        if (!_uiReady || editor == null) return;
        if (cboTemplate.SelectedItem is not LabelTemplate tpl) return;
        tpl.EnsureFields();
        editor.SetTemplate(tpl);
        editor.SetItems(SampleRows().Select(r => new LabelPrintItem { PartNo = r.PartNo, Name = r.Name, CarType = r.CarType }));
    }

    /// <summary>预览取行：勾选行优先；无勾选时跟随 DataGrid 当前行（点击切换行预览立即跟随），再退回首行</summary>
    private System.Collections.Generic.List<LabelPrintRow> SampleRows()
    {
        var checkedRows = _items.Where(i => i.IsSelected).ToList();
        if (checkedRows.Count > 0) return checkedRows;
        if (dgItems.SelectedItem is LabelPrintRow sel) return new System.Collections.Generic.List<LabelPrintRow> { sel };
        var first = _items.FirstOrDefault();
        return first != null ? new System.Collections.Generic.List<LabelPrintRow> { first } : new System.Collections.Generic.List<LabelPrintRow>();
    }

    /// <summary>预览中双击修改文字后，把改动回写到对应数据行（仅内存，不落库；取行口径与 RefreshPreview 一致）</summary>
    private void SyncFromEditor()
    {
        var rows = SampleRows();
        for (int i = 0; i < rows.Count && i < editor.Items.Count; i++)
        {
            var row = rows[i];
            var it = editor.Items[i];
            row.PartNo = it.PartNo;
            row.Name = it.Name;
            row.CarType = it.CarType;
        }
    }

    private System.Collections.Generic.List<LabelPrintItem> SelectedRows()
    {
        dgItems.CommitEdit(DataGridEditingUnit.Cell, true);
        dgItems.CommitEdit(DataGridEditingUnit.Row, true);

        var list = new System.Collections.Generic.List<LabelPrintItem>();
        int copies = CurrentCopies();
        foreach (var item in _items.Where(i => i.IsSelected))
        {
            for (int c = 0; c < copies; c++)
                list.Add(new LabelPrintItem { PartNo = item.PartNo, Name = item.Name, CarType = item.CarType });
        }
        return list;
    }

    private void BtnPrint_Click(object sender, RoutedEventArgs e)
    {
        var printerName = cboPrinter.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(printerName))
        {
            MessageBox.Show("请选择打印机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (cboTemplate.SelectedItem is not LabelTemplate tpl)
        {
            MessageBox.Show("请选择标签模板", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            MessageBox.Show("请先勾选要打印的零件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var (doc, pageCount) = LabelLayoutBuilder.Build(rows, tpl);
            if (pageCount == 0)
            {
                MessageBox.Show("没有可打印的标签", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LabelPrintHelper.Print(doc, printerName, tpl);
            LabelTemplateService.SaveLastPrinter(printerName);
            MessageBox.Show($"已发送 {rows.Count} 张标签到打印机：{printerName}", "提示");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打印失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
