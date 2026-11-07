using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Documents;
using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Printing;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>退货车行（c_th 语义：源行快照 + 五个可编辑列）。</summary>
public partial class PurchaseCartRow : ObservableObject
{
    public int SourceId { get; init; }
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Ty { get; init; } = "";
    public string Unit { get; init; } = "";
    /// <summary>原进库数。</summary>
    public decimal Oamount { get; init; }
    public decimal Kshu { get; init; }
    public string Cno { get; init; } = "";
    public decimal Oprc { get; init; }
    public string Note { get; init; } = "";
    public decimal IprcOld { get; init; }

    [ObservableProperty] private decimal _amount;      // 退库数（可编辑）
    [ObservableProperty] private decimal _iprj;         // 外币价（可编辑；BA01 锁）
    [ObservableProperty] private decimal _iprc;         // 进价（可编辑；BA01 锁）
    [ObservableProperty] private string _posi = "";     // 仓位（可编辑）
    [ObservableProperty] private string _depotno = "";  // 库号（可编辑）
    /// <summary>不扣库存（2026-11-04 需求：弹窗勾选，默认未勾选=正常扣库存）。</summary>
    public bool NoStock { get; init; }
}

/// <summary>
/// 入库退货 VM（B.A，权限码 BA；fr_istk 契约）。
/// 拉单（tbistoed 可退流水）→ 退货车（可编辑数量/价格/仓位/库号）→ 确认（GETNEWDH 散件入库单 + 负数流水 + ISAPPE 冲减）。
/// BA01 细码：有此权限且非超级 → 进价/外币价只读（旧 __.row108 语义）。
/// </summary>
public partial class PurchaseReturnViewModel : ObservableObject
{
    public ObservableCollection<ReturnableLine> Lines { get; } = [];
    public ObservableCollection<PurchaseCartRow> Cart { get; } = [];
    public ObservableCollection<string> FkfsOptions { get; } = [];
    public ObservableCollection<string> ReasonOptions { get; } = [];
    public ObservableCollection<string> DepotOptions { get; } = [];

    // —— 单据查询模式（2026-11-04 需求：双模式切换，F3）——
    public ObservableCollection<ReturnDocRow> Docs { get; } = [];
    public ObservableCollection<ReturnDocLine> DocLines { get; } = [];

    [ObservableProperty] private bool _isQueryMode;
    [ObservableProperty] private ReturnDocRow? _selectedDoc;
    [ObservableProperty] private string _docCno = "";
    [ObservableProperty] private string _docGyname = "";
    [ObservableProperty] private DateTime? _docDateFrom;
    [ObservableProperty] private DateTime? _docDateTo;

    /// <summary>当前模式显示文本。</summary>
    public string CurrentModeText => IsQueryMode ? "当前：单据查询" : "当前：退货开单";

    partial void OnIsQueryModeChanged(bool value) => OnPropertyChanged(nameof(CurrentModeText));

    [ObservableProperty] private string _cno = "";
    [ObservableProperty] private string _nnoPrefix = "";
    [ObservableProperty] private string _na1Prefix = "";
    [ObservableProperty] private string _tyPrefix = "";
    [ObservableProperty] private string _gyname = "";
    [ObservableProperty] private DateTime? _inDateFrom;
    [ObservableProperty] private DateTime? _inDateTo;
    /// <summary>退货日期（2026-11-04 需求：确认后写入负数流水 indate，默认当天）。</summary>
    [ObservableProperty] private DateTime? _returnDate = DateTime.Today;
    [ObservableProperty] private string? _selectedFkfs;
    [ObservableProperty] private string? _selectedReason;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private decimal _totalIprc;

    /// <summary>查询实时防抖（2026-11-04 需求：条件变化 500ms 后自动查询，无需点"查询"按钮）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    /// <summary>单据查询实时防抖。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _docSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public PurchaseReturnViewModel()
    {
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await SearchAsync(System.Threading.CancellationToken.None);
        };
        _docSearchDebounce.Tick += async (_, _) =>
        {
            _docSearchDebounce.Stop();
            await SearchDocsAsync(System.Threading.CancellationToken.None);
        };
    }

    // 查询条件变化 → 防抖自动查询（实时检索）
    partial void OnCnoChanged(string value) => DebounceSearch();
    partial void OnNnoPrefixChanged(string value) => DebounceSearch();
    partial void OnNa1PrefixChanged(string value) => DebounceSearch();
    partial void OnTyPrefixChanged(string value) => DebounceSearch();
    partial void OnGynameChanged(string value) => DebounceSearch();
    partial void OnInDateFromChanged(DateTime? value) => DebounceSearch();
    partial void OnInDateToChanged(DateTime? value) => DebounceSearch();

    // 单据查询条件变化 → 防抖自动查询
    partial void OnDocCnoChanged(string value) => DebounceDocSearch();
    partial void OnDocGynameChanged(string value) => DebounceDocSearch();
    partial void OnDocDateFromChanged(DateTime? value) => DebounceDocSearch();
    partial void OnDocDateToChanged(DateTime? value) => DebounceDocSearch();

    /// <summary>选中历史退货单 → 加载该单明细。</summary>
    partial void OnSelectedDocChanged(ReturnDocRow? value)
    {
        if (value is not null) _ = LoadDocLinesAsync(value.Cno);
    }

    private void DebounceSearch()
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>选中供应商后立即按当前条件查询（2026-11-04 需求：进入页面默认不加载，选中供应商才加载），并取消待触发的防抖。</summary>
    public async Task SearchNowAsync()
    {
        _searchDebounce.Stop();
        await SearchAsync(System.Threading.CancellationToken.None);
    }

    // —— 单据查询模式（2026-11-04 需求）——

    /// <summary>切换双模式（F3/按钮）：进入单据查询模式时立即查一次。</summary>
    [RelayCommand]
    public async Task ToggleModeAsync(CancellationToken ct)
    {
        IsQueryMode = !IsQueryMode;
        Message = "";
        if (IsQueryMode) await SearchDocsAsync(ct);
    }

    /// <summary>单据查询条件防抖。</summary>
    private void DebounceDocSearch()
    {
        if (!IsQueryMode) return;
        _docSearchDebounce.Stop();
        _docSearchDebounce.Start();
    }

    /// <summary>查询历史退货单据（按 cno 汇总）。</summary>
    [RelayCommand]
    public async Task SearchDocsAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var rows = await Db.PurchaseReturns.SearchReturnDocsAsync(new ReturnDocQuery
            {
                Cno = DocCno,
                Gyname = DocGyname,
                DateFrom = DocDateFrom,
                DateTo = DocDateTo,
            }, AppSession.DeptNo, ct);
            Docs.Clear();
            DocLines.Clear();
            foreach (var r in rows) Docs.Add(r);
            if (rows.Count == 0) Message = "未找到符合条件的退货单据";
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>加载选中退货单的明细。</summary>
    public async Task LoadDocLinesAsync(string cno)
    {
        try
        {
            var rows = await Db.PurchaseReturns.GetReturnDocLinesAsync(cno, AppSession.DeptNo);
            DocLines.Clear();
            foreach (var r in rows) DocLines.Add(r);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
    }

    // —— 查询模式打印 / 导出（2026-11-04 需求）——

    private string _companyName = "";

    private async Task<string> GetCompanyNameAsync(System.Threading.CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_companyName))
        {
            try { _companyName = await Db.UniversalQuery.GetCompanyNameAsync(ct); }
            catch { _companyName = ""; }
        }
        return _companyName;
    }

    /// <summary>查询条件文本（打印页眉）。</summary>
    private string DocFindText =>
        $"退库单号：{DocCno.Trim()}   供应商：{DocGyname.Trim()}   退货日期：{DocDateFrom?.ToString("yyyy-MM-dd") ?? ""}~{DocDateTo?.ToString("yyyy-MM-dd") ?? ""}";

    /// <summary>打印：有选中单据 → 打印该单明细；否则打印单据列表（ListPrintDocument → 打印预览窗）。</summary>
    [RelayCommand]
    public async Task PrintDocsAsync(System.Threading.CancellationToken ct)
    {
        var company = await GetCompanyNameAsync(ct);
        string caption;
        FixedDocument doc;
        if (SelectedDoc is not null)
        {
            caption = $"退货单明细 {SelectedDoc.Cno.Trim()}";
            doc = ListPrintDocument.Build(new ListPrintRequest
            {
                Caption = caption,
                FindText = DocFindText,
                Company = company,
                Operator = AppSession.Operator,
                Columns =
                [
                    new QueryColumnSpec("Nno", "配件编号", 'C', 110),
                    new QueryColumnSpec("Na1", "名称", 'C', 200),
                    new QueryColumnSpec("Ty", "车型", 'C', 110),
                    new QueryColumnSpec("Amount", "退库数", 'N', 60, QueryGridHelper.QtyFormat),
                    new QueryColumnSpec("Iprj", "外币价", 'N', 60, "F2"),
                    new QueryColumnSpec("Iprc", "进价", 'N', 60, "F2"),
                    new QueryColumnSpec("Depotno", "库号", 'C', 50),
                    new QueryColumnSpec("Posi", "仓位", 'C', 55),
                    new QueryColumnSpec("Note", "备注", 'C', 160),
                ],
                Rows = QueryGridHelper.Wrap(DocLines),
            });
        }
        else
        {
            caption = "入库退货单查询";
            doc = ListPrintDocument.Build(new ListPrintRequest
            {
                Caption = caption,
                FindText = DocFindText,
                Company = company,
                Operator = AppSession.Operator,
                Columns =
                [
                    new QueryColumnSpec("Cno", "退库单号", 'C', 110),
                    new QueryColumnSpec("Indate", "退货日期", 'D', 85, "yyyy-MM-dd"),
                    new QueryColumnSpec("Gyname", "供应商", 'C', 200),
                    new QueryColumnSpec("Fkfs", "退款方式", 'C', 70),
                    new QueryColumnSpec("Per", "经办人", 'C', 70),
                    new QueryColumnSpec("LineCount", "行数", 'N', 55),
                    new QueryColumnSpec("TotalAmount", "退货数量", 'N', 70, QueryGridHelper.QtyFormat),
                ],
                Rows = QueryGridHelper.Wrap(Docs),
            });
        }
        var preview = new PrintPreviewWindow(doc, $"预览 - {caption}") { Owner = Application.Current.MainWindow };
        preview.ShowDialog();
    }

    /// <summary>导出表格：有选中单据 → 导出该单明细；否则导出单据列表（xlsx，ClosedXML）。</summary>
    [RelayCommand]
    public void ExportDocs()
    {
        var isLine = SelectedDoc is not null;
        if (isLine ? DocLines.Count == 0 : Docs.Count == 0)
        {
            MessageBox.Show("没有可导出的数据！", "入库退货单查询", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel文件|*.xlsx",
            FileName = isLine
                ? $"退货单明细_{SelectedDoc!.Cno.Trim()}.xlsx"
                : $"入库退货单查询_{DateTime.Today:yyyyMMdd}.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(isLine ? "退货单明细" : "退货单据");
            string[] heads;
            int col;
            if (isLine)
            {
                heads = ["配件编号", "名称", "车型", "退库数", "外币价", "进价", "库号", "仓位", "备注"];
                col = heads.Length;
                for (var i = 0; i < heads.Length; i++) ws.Cell(1, i + 1).Value = heads[i];
                ws.Range(1, 1, 1, col).Style.Font.Bold = true;
                var r = 2;
                foreach (var l in DocLines)
                {
                    ws.Cell(r, 1).Value = l.Nno.Trim();
                    ws.Cell(r, 2).Value = l.Na1.Trim();
                    ws.Cell(r, 3).Value = l.Ty.Trim();
                    ws.Cell(r, 4).Value = l.Amount;
                    ws.Cell(r, 5).Value = l.Iprj;
                    ws.Cell(r, 6).Value = l.Iprc;
                    ws.Cell(r, 7).Value = l.Depotno.Trim();
                    ws.Cell(r, 8).Value = l.Posi.Trim();
                    ws.Cell(r, 9).Value = l.Note.Trim();
                    r++;
                }
            }
            else
            {
                heads = ["退库单号", "退货日期", "供应商", "退款方式", "经办人", "行数", "退货数量"];
                col = heads.Length;
                for (var i = 0; i < heads.Length; i++) ws.Cell(1, i + 1).Value = heads[i];
                ws.Range(1, 1, 1, col).Style.Font.Bold = true;
                var r = 2;
                foreach (var d in Docs)
                {
                    ws.Cell(r, 1).Value = d.Cno.Trim();
                    ws.Cell(r, 2).Value = d.Indate.ToString("yyyy-MM-dd");
                    ws.Cell(r, 3).Value = d.Gyname.Trim();
                    ws.Cell(r, 4).Value = d.Fkfs.Trim();
                    ws.Cell(r, 5).Value = d.Per.Trim();
                    ws.Cell(r, 6).Value = d.LineCount;
                    ws.Cell(r, 7).Value = d.TotalAmount;
                    r++;
                }
            }
            ws.Columns(1, col).AdjustToContents();
            wb.SaveAs(dlg.FileName);
            MessageBox.Show($"已导出：{dlg.FileName}", "入库退货单查询", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "入库退货单查询", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public ReturnableLine? SelectedLine { get; set; }
    public PurchaseCartRow? SelectedCart { get; set; }

    /// <summary>BA01 锁价：旧 __.row108——'@' 超级或无 BA01 → 可编辑；有 BA01 且非超级 → 只读。</summary>
    public bool PriceLocked => AppSession.Permission.HasMenu("BA01") && !AppSession.Permission.IsSuper;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            foreach (var f in await Db.PurchaseReturns.GetSyssetNamesAsync("guno", ct)) FkfsOptions.Add(f);
            foreach (var r in await Db.PurchaseReturns.GetSyssetNamesAsync("退货原因", ct)) ReasonOptions.Add(r);
            foreach (var d in await Db.PurchaseReturns.GetSyssetNamesAsync("库号", ct)) DepotOptions.Add(d);
            // 退款方式默认"挂帐"（2026-11-04 需求；sysset guno 实际为"挂帐"），无该项时回退第一项
            SelectedFkfs = FkfsOptions.FirstOrDefault(f => f.Trim() == "挂帐") ?? FkfsOptions.FirstOrDefault();
            // 进入页面默认不加载流水，选中供应商后再按供应商加载（SelectSupplier 触发）
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var rows = await Db.PurchaseReturns.SearchReturnableAsync(new PurchaseReturnQuery
            {
                Cno = Cno,
                NnoPrefix = NnoPrefix,
                Na1Prefix = Na1Prefix,
                Ty = TyPrefix,
                Gyname = Gyname,
                InDateFrom = InDateFrom,
                InDateTo = InDateTo,
            }, AppSession.DeptNo, ct);
            Lines.Clear();
            foreach (var r in rows) Lines.Add(r);
            if (rows.Count == 0) Message = "未找到符合条件的入库流水（不含调拨入库：调拨退货须对方开调拨退货单）";
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>带值加入退货车（双击/回车弹窗确认后调用，2026-11-04 需求）：数量/价格/不扣库存来自弹窗，仓位/库号取源行。</summary>
    public bool AddToCart(decimal amount, decimal iprj, decimal iprc, bool noStock = false)
    {
        if (SelectedLine is null) return false;
        var s = SelectedLine;
        if (!string.Equals(s.Dpm.Trim(), AppSession.DeptNo.Trim(), StringComparison.Ordinal))
        {
            MessageBox.Show($"当前分店是 {AppSession.DeptNo.Trim()}, 入库历史是 {s.Dpm.Trim()}, 分店不一致不能退货.",
                "入库退货", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (Cart.Any(c => c.SourceId == s.Id))
        {
            MessageBox.Show("该流水已在退货车中。", "入库退货", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        Cart.Add(new PurchaseCartRow
        {
            SourceId = s.Id, Cp = s.Cp, Nno = s.Nno, Na1 = s.Na1,
            // 不扣库存行：车型前显示"未扣"标记（与确认后写入流水的 TY 一致；TY char(14) 按字节截断）
            Ty = noStock ? QPGL.Infrastructure.Db.AnsiBytes.Truncate("未扣" + s.Ty, 14) : s.Ty,
            Unit = s.Unit,
            Oamount = s.Oamount, Kshu = s.Kshu, Cno = s.Cno, Oprc = s.Oprc, Note = s.Note, IprcOld = s.IprcOld,
            Amount = amount, Iprj = iprj, Iprc = iprc, Posi = s.Posi, Depotno = s.Depotno, NoStock = noStock,
        });
        RecalcTotals();
        return true;
    }

    [RelayCommand]
    private void RemoveCart()
    {
        if (SelectedCart is null) return;
        Cart.Remove(SelectedCart);
        RecalcTotals();
    }

    [RelayCommand]
    private void ClearCart()
    {
        Cart.Clear();
        RecalcTotals();
    }

    private void RecalcTotals()  // 旧 heji：Σ IPRC×AMOUNT
    {
        TotalIprc = Cart.Sum(c => c.Iprc * c.Amount);
    }

    [RelayCommand]
    public async Task ConfirmAsync(CancellationToken ct)
    {
        if (Cart.Count == 0) return;
        if (string.IsNullOrWhiteSpace(SelectedFkfs))
        {
            MessageBox.Show("请选择退款方式!", "入库退货", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show("确认退货吗？", "入库退货", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            IsBusy = true;
            Message = "";
            var result = await Db.PurchaseReturns.ConfirmAsync(new ConfirmPurchaseReturnCommand
            {
                Fkfs = SelectedFkfs!,
                ReturnReason = SelectedReason ?? "",
                ReturnDate = ReturnDate,
                Lines = Cart.Select(c => new PurchaseReturnLineInput
                {
                    SourceId = c.SourceId, Amount = c.Amount, Iprj = c.Iprj,
                    Iprc = c.Iprc, Posi = c.Posi, Depotno = c.Depotno, NoStock = c.NoStock,
                }).ToList(),
            }, AppSession.DeptNo, AppSession.Operator, ct);

            MessageBox.Show($"退货完成，退库单号 {result.Cno}，共 {result.LineCount} 行。",
                "入库退货", MessageBoxButton.OK, MessageBoxImage.Information);
            Cart.Clear();
            RecalcTotals();
            await SearchAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "入库退货", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
