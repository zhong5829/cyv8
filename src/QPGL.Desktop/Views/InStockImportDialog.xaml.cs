using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QPGL.Desktop.Services;

namespace QPGL.Desktop.Views;

/// <summary>导入预览行状态。</summary>
public enum ImportRowStatus
{
    /// <summary>属性库已存在。</summary>
    Exists,
    /// <summary>属性库无此零件号（新配件，生效时自动建档）。</summary>
    NewPart,
    /// <summary>同编号重复行（可合并）。</summary>
    Duplicate,
    /// <summary>缺零件号/数量无效。</summary>
    MissingRequired,
}

/// <summary>导入预览行（qp11 ImportRowResult 同构）。</summary>
public sealed class ImportRowResult
{
    public int RowIndex { get; set; }
    public string? Nno { get; set; }
    public string? Na1 { get; set; }
    public decimal Qty { get; set; }
    public decimal Iprj { get; set; }
    public string? Ty { get; set; }
    public string? Posi { get; set; }
    public string? Note { get; set; }
    public ImportRowStatus Status { get; set; }
    public string? StatusText { get; set; }
    public bool IsDuplicate { get; set; }
    public int DuplicateCount { get; set; }
    /// <summary>属性库带出（qp11 匹配配件档案语义）。</summary>
    public Domain.Parts.PartProfileRow? MatchedProfile { get; set; }
    public string? OriginalName { get; set; }
}

/// <summary>
/// 散件入库表格导入对话框（qp11 采购进货 ExcelImportDialog 同构）：
/// xls/xlsx（NPOI）解析 → 工作表/起始行选择 → 列映射（别名自动匹配：条码/编号/零件号…）→
/// 预览状态标注（✓已存在/✗新配件/⚠重复/✗无效）→ 合并同编号 → 确认回传行集。
/// 属性库匹配（tbprnoty）：命中带出名称/车型/进价（表格价优先，空则用库价）；未命中=新配件行。
/// </summary>
public partial class InStockImportDialog : Window
{
    /// <summary>列名别名映射（qp11 AliasMap 同构，散件入库语境：编号=条码）。</summary>
    private static readonly Dictionary<string, List<string>> AliasMap = new()
    {
        ["Nno"] = new() { "条码", "编号", "零件号", "配件编号", "件号", "货号", "编码", "partno", "零件编号", "内部编号", "nno" },
        ["Na1"] = new() { "名称", "品名", "配件名称", "零件名称", "商品名称", "name", "品名描述" },
        ["Qty"] = new() { "数量", "qty", "amount", "进货数量", "入库数量", "采购数量", "订货数量" },
        ["Iprj"] = new() { "进价", "单价", "采购价", "成本价", "price", "入库价", "含税价" },
        ["Ty"] = new() { "车型", "适用车型", "车系", "cartype", "适配车型", "图号" },
        ["Posi"] = new() { "仓位", "货位", "库位", "位置", "place", "货架" },
        ["Note"] = new() { "备注", "说明", "memo", "描述", "备注说明" },
    };

    private ExcelParseResult? _parseResult;
    private List<ImportRowResult> _previewData = [];
    /// <summary>进行中的预览匹配任务（导入前必须等它完成，否则命中行拿不到 MatchedProfile）。</summary>
    private System.Threading.Tasks.Task? _previewTask;

    /// <summary>确认后的有效行（DialogResult=true 时有效）。</summary>
    public List<ImportRowResult> ImportedRows { get; private set; } = [];

    public InStockImportDialog()
    {
        InitializeComponent();
    }

    private void ShowLoading(string message)
    {
        TxtLoading.Text = message;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoading() => LoadingOverlay.Visibility = Visibility.Collapsed;

    private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel文件|*.xls;*.xlsx|所有文件|*.*",
            Title = "选择采购清单文件",
        };
        if (dlg.ShowDialog() != true) return;

        TxtFilePath.Text = dlg.FileName;
        ShowLoading("正在解析文件...");
        try
        {
            var parser = new ExcelParserService();
            Exception? parseError = null;
            _parseResult = await System.Threading.Tasks.Task.Run(() =>
            {
                try { return parser.Parse(dlg.FileName); }
                catch (Exception ex) { parseError = ex; return new ExcelParseResult(); }
            });

            if (parseError != null)
            {
                MessageBox.Show($"解析文件失败:\n{parseError.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            CmbSheet.Items.Clear();
            foreach (var name in _parseResult.SheetNames)
                CmbSheet.Items.Add(name);
            if (CmbSheet.Items.Count > 0)
                CmbSheet.SelectedIndex = 0;

            RefreshMappingAndPreview();
        }
        finally
        {
            HideLoading();
        }
    }

    private async void CmbSheet_SelectionChanged(object sender, SelectionChangedEventArgs e) => await ReparseAsync();
    private async void TxtStartRow_TextChanged(object sender, TextChangedEventArgs e) => await ReparseAsync();

    private async System.Threading.Tasks.Task ReparseAsync()
    {
        if (_parseResult is null || CmbSheet.SelectedIndex < 0) return;
        if (TxtFilePath.Text.Length == 0 || !File.Exists(TxtFilePath.Text)) return;
        ShowLoading("正在重新解析...");
        try
        {
            var parser = new ExcelParserService();
            var startRow = int.TryParse(TxtStartRow.Text, out var sr) ? sr : 2;
            var filePath = TxtFilePath.Text;
            var sheetIndex = CmbSheet.SelectedIndex;
            _parseResult = await System.Threading.Tasks.Task.Run(
                () => parser.Parse(filePath, sheetIndex, 0, startRow - 1));
            RefreshMappingAndPreview();
        }
        catch
        {
            // 重解析失败保持上次结果
        }
        finally
        {
            HideLoading();
        }
    }

    private void RefreshMappingAndPreview()
    {
        if (_parseResult is null) return;

        var headers = _parseResult.Headers;
        var comboBoxes = new[] { MapNno, MapNa1, MapQty, MapIprj, MapTy, MapPosi, MapNote };
        var fieldKeys = new[] { "Nno", "Na1", "Qty", "Iprj", "Ty", "Posi", "Note" };

        for (var i = 0; i < comboBoxes.Length; i++)
        {
            var cb = comboBoxes[i];
            cb.Items.Clear();
            cb.Items.Add("(不导入)");
            foreach (var h in headers)
                cb.Items.Add(h);

            var autoIdx = AutoMatchHeader(headers, fieldKeys[i]);
            cb.SelectedIndex = autoIdx >= 0 ? autoIdx + 1 : 0;
        }

        RefreshPreview();
    }

    /// <summary>列名自动匹配（qp11 AutoMatchHeader 同构：先全等后包含）。</summary>
    private static int AutoMatchHeader(List<string> headers, string fieldKey)
    {
        if (!AliasMap.TryGetValue(fieldKey, out var aliases)) return -1;

        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i].Trim();
            foreach (var alias in aliases)
                if (h.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i].Trim();
            foreach (var alias in aliases)
                if (h.Contains(alias, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        return -1;
    }

    private void Mapping_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();

    /// <summary>
    /// 预览构建（qp11 RefreshPreview 同构）：映射取数 → 校验（缺零件号/数量≤0）→ 重复标注 →
    /// 属性库匹配（qp11 _allParts 匹配语义；这里按零件号批量查 tbprnoty）。
    /// </summary>
    private async System.Threading.Tasks.Task RefreshPreviewAsync()
    {
        if (_parseResult is null || _parseResult.Headers.Count == 0) return;

        var rows = BuildPreviewRows();
        // 批量查属性库（按零件号）
        var nnos = rows.Where(r => !string.IsNullOrWhiteSpace(r.Nno)).Select(r => r.Nno!.Trim()).Distinct().ToList();
        Dictionary<string, Domain.Parts.PartProfileRow> byNno = new(StringComparer.OrdinalIgnoreCase);
        if (nnos.Count > 0)
        {
            try
            {
                var profiles = await Db.PartProfiles.GetByNnosAsync(nnos);
                byNno = profiles.GroupBy(p => p.Nno.Trim())
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch { /* 查询失败时全部按新配件处理 */ }
        }

        foreach (var item in rows)
        {
            if (item.Status != ImportRowStatus.Exists && item.Status != ImportRowStatus.NewPart) continue;
            var key = item.Nno!.Trim();
            if (byNno.TryGetValue(key, out var pf))
            {
                item.MatchedProfile = pf;
                item.OriginalName = pf.Na1;
                // 表格空字段用属性库带出（qp11 L349-358 同构）
                if (string.IsNullOrWhiteSpace(item.Na1)) item.Na1 = pf.Na1;
                if (string.IsNullOrWhiteSpace(item.Ty)) item.Ty = pf.Ty;
                if (item.Iprj == 0) item.Iprj = pf.Iprj;
                item.Status = ImportRowStatus.Exists;
                item.StatusText = "✓ 已存在";
            }
            else
            {
                item.Status = ImportRowStatus.NewPart;
                item.StatusText = "✗ 新配件";
            }
        }

        _previewData = rows;
        DgPreview.ItemsSource = null;
        DgPreview.ItemsSource = _previewData;

        var existsCount = _previewData.Count(r => r.Status == ImportRowStatus.Exists);
        var dupCount = _previewData.Count(r => r.Status == ImportRowStatus.Duplicate);
        var newCount = _previewData.Count(r => r.Status == ImportRowStatus.NewPart);
        var missingCount = _previewData.Count(r => r.Status == ImportRowStatus.MissingRequired);

        TxtStats.Text = $"总计 {_previewData.Count} 行 | ✓已存在 {existsCount} 个 | ⚠重复 {dupCount} 个 | ✗新配件 {newCount} 个(生效时自动建档) | ✗无效 {missingCount} 个";
        BtnImport.IsEnabled = _previewData.Any(r => r.Status is ImportRowStatus.Exists or ImportRowStatus.NewPart or ImportRowStatus.Duplicate);
    }

    private List<ImportRowResult> BuildPreviewRows()
    {
        var preview = new List<ImportRowResult>();
        var mapNnoIdx = MapNno.SelectedIndex - 1;
        var mapNa1Idx = MapNa1.SelectedIndex - 1;
        var mapQtyIdx = MapQty.SelectedIndex - 1;
        var mapIprjIdx = MapIprj.SelectedIndex - 1;
        var mapTyIdx = MapTy.SelectedIndex - 1;
        var mapPosiIdx = MapPosi.SelectedIndex - 1;
        var mapNoteIdx = MapNote.SelectedIndex - 1;

        var seenPartNos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = _parseResult?.Rows ?? [];

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var item = new ImportRowResult { RowIndex = r + 1 };

            item.Nno = mapNnoIdx >= 0 && mapNnoIdx < row.Count ? row[mapNnoIdx] : null;
            item.Na1 = mapNa1Idx >= 0 && mapNa1Idx < row.Count ? row[mapNa1Idx] : null;
            item.Ty = mapTyIdx >= 0 && mapTyIdx < row.Count ? row[mapTyIdx] : null;
            item.Posi = mapPosiIdx >= 0 && mapPosiIdx < row.Count ? row[mapPosiIdx] : null;
            item.Note = mapNoteIdx >= 0 && mapNoteIdx < row.Count ? row[mapNoteIdx] : null;

            var qtyStr = mapQtyIdx >= 0 && mapQtyIdx < row.Count ? row[mapQtyIdx] : null;
            item.Qty = decimal.TryParse(qtyStr, out var amt) ? amt : 0;

            var priceStr = mapIprjIdx >= 0 && mapIprjIdx < row.Count ? row[mapIprjIdx] : null;
            item.Iprj = decimal.TryParse(priceStr, out var price) ? price : 0;

            if (string.IsNullOrWhiteSpace(item.Nno))
            {
                item.Status = ImportRowStatus.MissingRequired;
                item.StatusText = "✗ 缺少零件号";
                preview.Add(item);
                continue;
            }

            if (item.Qty <= 0)
            {
                item.Status = ImportRowStatus.MissingRequired;
                item.StatusText = "✗ 数量无效";
                preview.Add(item);
                continue;
            }

            var partNoKey = item.Nno.Trim().ToUpperInvariant();
            if (seenPartNos.TryGetValue(partNoKey, out var seen))
            {
                item.Status = ImportRowStatus.Duplicate;
                item.IsDuplicate = true;
                seenPartNos[partNoKey] = seen + 1;
                item.DuplicateCount = seen + 1;
                item.StatusText = $"⚠ 重复×{item.DuplicateCount}";
                preview.Add(item);
                continue;
            }
            seenPartNos[partNoKey] = 1;
            // 先占位，属性库状态由 RefreshPreviewAsync 批量回填
            item.Status = ImportRowStatus.NewPart;
            item.StatusText = "...";
            preview.Add(item);
        }
        return preview;
    }

    private void RefreshPreview()
    {
        // 同步壳：真实预览含属性库查询，异步执行；任务句柄供导入前等待
        _previewTask = RefreshPreviewAsync();
    }

    private void DgPreview_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not ImportRowResult item) return;
        e.Row.Background = item.Status switch
        {
            ImportRowStatus.Exists => new SolidColorBrush(Color.FromArgb(30, 0, 200, 0)),
            ImportRowStatus.NewPart => new SolidColorBrush(Color.FromArgb(40, 0, 100, 255)),
            ImportRowStatus.Duplicate => new SolidColorBrush(Color.FromArgb(40, 255, 165, 0)),
            ImportRowStatus.MissingRequired => new SolidColorBrush(Color.FromArgb(50, 255, 0, 0)),
            _ => Brushes.Transparent,
        };
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        // 等待属性库匹配完成（竞态：预览异步回填未完成即点导入 → 命中行按新配件取表格名）
        if (_previewTask is not null)
        {
            try { await _previewTask; }
            catch { /* 匹配异常按未命中处理 */ }
        }

        var validItems = _previewData
            .Where(r => r.Status is ImportRowStatus.Exists or ImportRowStatus.NewPart)
            .ToList();

        var duplicates = _previewData.Where(r => r.Status == ImportRowStatus.Duplicate).ToList();

        // 合并同编号（qp11 L429-457：勾选则数量累加进首行，未勾选原样保留）
        if (ChkMerge.IsChecked == true)
        {
            foreach (var dup in duplicates)
            {
                var existing = validItems.FirstOrDefault(v =>
                    string.Equals(v.Nno?.Trim(), dup.Nno?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    existing.Qty += dup.Qty;
                else
                {
                    dup.Status = ImportRowStatus.NewPart;
                    dup.StatusText = "✗ 新配件";
                    dup.IsDuplicate = false;
                    validItems.Add(dup);
                }
            }
        }
        else
        {
            foreach (var dup in duplicates)
            {
                dup.Status = ImportRowStatus.NewPart;
                dup.StatusText = "✗ 新配件";
                dup.IsDuplicate = false;
                validItems.Add(dup);
            }
        }

        // 重复行转"新配件"后补属性库匹配（RefreshPreviewAsync 跳过 Duplicate 行，MatchedProfile 恒空）：
        //   命中则带出库内名称/车型——明细行不按表格覆盖库内原值（2026-10-26 用户确认）
        var unmatched = validItems
            .Where(r => r.MatchedProfile is null && !string.IsNullOrWhiteSpace(r.Nno))
            .GroupBy(r => r.Nno!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (unmatched.Count > 0)
        {
            try
            {
                var profiles = await Db.PartProfiles.GetByNnosAsync(unmatched.Select(r => r.Nno!.Trim()));
                var byNno = profiles.GroupBy(p => p.Nno.Trim())
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var r in unmatched)
                {
                    if (!byNno.TryGetValue(r.Nno!.Trim(), out var pf)) continue;
                    r.MatchedProfile = pf;
                    r.OriginalName = pf.Na1;
                    if (string.IsNullOrWhiteSpace(r.Na1)) r.Na1 = pf.Na1;
                    if (string.IsNullOrWhiteSpace(r.Ty)) r.Ty = pf.Ty;
                    if (r.Iprj == 0) r.Iprj = pf.Iprj;
                    r.Status = ImportRowStatus.Exists;
                    r.StatusText = "✓ 已存在";
                }
            }
            catch { /* 匹配失败按未命中处理 */ }
        }

        var invalidItems = _previewData.Where(r => r.Status == ImportRowStatus.MissingRequired).ToList();
        if (invalidItems.Count > 0)
        {
            var msg = $"有 {invalidItems.Count} 行数据无效（缺零件号或数量≤0），将跳过这些行。\n\n确认导入 {validItems.Count} 条有效数据？";
            if (MessageBox.Show(msg, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }

        ImportedRows = validItems;
        DialogResult = true;
    }
}
