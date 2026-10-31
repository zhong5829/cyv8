using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Sales;
using QPGL.Infrastructure;

namespace QPGL.Desktop.ViewModels;

/// <summary>上区可退行（按客户加载的原销售单正数出库行，只读展示，双击弹退货编辑窗）。</summary>
public class ReturnableRow
{
    public int RowId { get; init; }
    public string Paper { get; init; } = "";  // 原销售单号
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string DepotNo { get; init; } = "";
    public string Posi { get; init; } = "";
    public string Ty { get; init; } = "";     // 车型
    public string Fa { get; init; } = "";     // 产地
    public string Cxnno { get; init; } = "";  // 分类码
    public string Guno { get; init; } = "";   // 付款方式
    public string Gname { get; init; } = "";  // 客户名称
    public string Per { get; init; } = "";    // 操作员
    public string Note { get; init; } = "";   // 备注
    public string Pno { get; init; } = "";    // 发票号（确认窗默认值）
    public string TMode { get; init; } = "";  // 发货方式（确认窗默认值）
    public decimal Qty { get; init; }         // 原出库数
    public decimal Returned { get; set; }     // 已退数（保存成功后本地回写）
    public decimal Remain => Qty - Returned;  // 可退数
    public decimal Oprc { get; init; }        // 售价
    public decimal Oprct { get; init; }       // 实售价
    public decimal Iprc { get; init; }        // 进价（含税）
    public decimal Iprj { get; init; }        // 进价（不含税）
    public decimal Yhl { get; init; }         // 优惠率
    public DateTime OutDate { get; init; }    // 出库日期
}

/// <summary>下区退货明细行（退货编辑窗确认后生成；售价/实售价/库号/仓位为编辑后值）。</summary>
public partial class ReturnLineRow : ObservableObject
{
    public int RowId { get; init; }
    public string Paper { get; init; } = "";  // 原销售单号
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string DepotNo { get; set; } = ""; // 库号（编辑窗可改）
    public string Posi { get; set; } = "";    // 仓位（编辑窗可改）
    public string Ty { get; init; } = "";     // 车型
    public string Fa { get; init; } = "";     // 产地
    public string Cxnno { get; init; } = "";  // 分类码
    public string Guno { get; init; } = "";   // 付款方式
    public string Gname { get; init; } = "";  // 客户名称
    public string Per { get; init; } = "";    // 操作员
    public string Note { get; init; } = "";   // 备注
    public string Pno { get; init; } = "";    // 发票号
    public string TMode { get; init; } = "";  // 发货方式
    public decimal Qty { get; init; }         // 原出库数
    public decimal Returned { get; init; }    // 已退数
    public decimal Remain { get; init; }      // 可退数
    public decimal Oprc { get; set; }         // 售价改为
    public decimal Oprct { get; set; }        // 实售价改为
    public decimal Iprc { get; init; }        // 进价（含税）
    public decimal Iprj { get; init; }        // 进价（不含税）
    public decimal Yhl { get; init; }         // 优惠率
    public DateTime OutDate { get; init; }    // 出库日期
    public bool IsScrap { get; set; }         // 废品仓（2026-10-29 需求：勾选=不回补库存，自动备注[已作废]）

    [ObservableProperty] private decimal _returnQty;
    public decimal Amount => ReturnQty * Oprc;

    /// <summary>编辑后刷新金额通知（Oprc/ReturnQty 变更后调用）。</summary>
    public void RefreshAmount() => OnPropertyChanged(nameof(Amount));
}

/// <summary>
/// 销售退货页（fr_sata 契约）：客户名称联想加载可退行（上区）→ 双击弹退货编辑窗加入明细（下区）→ 按原单分组保存。
/// 直连模式直调 ISaleReturnRepository（回补走 sale_up 触发器 + ro/kcid 联动在仓储内）。
/// </summary>
public partial class SaleReturnViewModel : ObservableObject
{
    /// <summary>上区：可退行（配件列表）。整体替换赋值（数千行时逐条 Add 会拖垮 UI）。</summary>
    public ObservableCollection<ReturnableRow> ReturnableRows { get; private set; } = [];
    /// <summary>下区：退货明细。</summary>
    public ObservableCollection<ReturnLineRow> Lines { get; private set; } = [];
    public ObservableCollection<Domain.Partners.Partner> CustomerSuggestions { get; } = [];

    [ObservableProperty] private string _customerName = "";
    [ObservableProperty] private string _customerNo = "";
    [ObservableProperty] private string _sourceInfo = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _lastPaper = "";

    // ==== 三条件过滤（编码/名称/车型；内存过滤，实时+500ms 防抖，名称框支持拼音码）====
    [ObservableProperty] private string _partCp = "";
    [ObservableProperty] private string _partNa1 = "";
    [ObservableProperty] private string _partTy = "";
    private List<ReturnableRow> _allRows = [];                                  // 未过滤全集
    private Dictionary<string, string> _pyMap = new(StringComparer.Ordinal);    // 零件名称→拼音码（tbname0）
    private Dictionary<string, string> _tyMap = new(StringComparer.Ordinal);    // 车型→拼音码（tbty0，2026-10-30 需求）
    private readonly System.Windows.Threading.DispatcherTimer _filterDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    /// <summary>过滤输入变化时调用：重置防抖计时（View 的 TextChanged 转发）。</summary>
    public void DebounceFilter()
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    /// <summary>三条件内存过滤（语义同销售开单页）：编码=前缀（首字符 / 转包含，2026-10-30 编码双模式对齐）；
    /// 名称=Na1/Nno/Ty 包含或拼音码前缀；车型=Ty/Na1 包含或 tbty0 车型拼音码。</summary>
    public void ApplyFilter()
    {
        _filterDebounce.Stop();
        var cpKw = PartCp.Trim();
        var cpContains = cpKw.StartsWith("/");
        var cpWord = cpContains ? cpKw[1..].Trim() : cpKw;
        var na1Kw = PartNa1.Trim();
        var tyKw = PartTy.Trim();
        IEnumerable<ReturnableRow> q = _allRows;
        if (cpWord.Length > 0)
            q = cpContains
                ? q.Where(r => r.Nno.Contains(cpWord, StringComparison.OrdinalIgnoreCase))
                : q.Where(r => r.Nno.StartsWith(cpWord, StringComparison.OrdinalIgnoreCase));
        if (na1Kw.Length > 0)
            q = q.Where(r =>
                r.Na1.Contains(na1Kw, StringComparison.OrdinalIgnoreCase) ||
                r.Nno.Contains(na1Kw, StringComparison.OrdinalIgnoreCase) ||
                r.Ty.Contains(na1Kw, StringComparison.OrdinalIgnoreCase) ||
                (_pyMap.TryGetValue(r.Na1, out var code) && code.Contains(na1Kw, StringComparison.OrdinalIgnoreCase)));
        if (tyKw.Length > 0)
            q = q.Where(r =>
                r.Ty.Contains(tyKw, StringComparison.OrdinalIgnoreCase) ||
                r.Na1.Contains(tyKw, StringComparison.OrdinalIgnoreCase) ||
                (_tyMap.TryGetValue(r.Ty, out var tyCode) && tyCode.Contains(tyKw, StringComparison.OrdinalIgnoreCase)));
        ReturnableRows = new ObservableCollection<ReturnableRow>(q.ToList());
        OnPropertyChanged(nameof(ReturnableRows));
    }

    /// <summary>客户名称/拼音码联想防抖（300ms，与销售开单页同款）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _custDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };

    public SaleReturnViewModel()
    {
        _custDebounce.Tick += async (_, _) =>
        {
            _custDebounce.Stop();
            await SearchCustomerSuggestionsAsync(System.Threading.CancellationToken.None);
        };
        _filterDebounce.Tick += (_, _) => ApplyFilter();
    }

    /// <summary>客户输入变化时调用：重置联想防抖（View 的 TextChanged 转发）。</summary>
    public void DebounceCustomerSearch()
    {
        _custDebounce.Stop();
        _custDebounce.Start();
    }

    /// <summary>
    /// 客户联想搜索（与销售开单页同源）：
    /// tbgu 按 gname/gno/code（拼音码）/code1（数字码）/tel/mobile 前缀匹配。
    /// </summary>
    public async System.Threading.Tasks.Task SearchCustomerSuggestionsAsync(CancellationToken ct = default)
    {
        var kw = CustomerName?.Trim();
        if (string.IsNullOrEmpty(kw))
        {
            CustomerSuggestions.Clear();
            return;
        }
        try
        {
            var request = new PagedRequest { Page = 1, PageSize = 200, Sort = "partName", Order = "asc", Keyword = kw };
            var result = await Db.Partners.SearchAsync(request, ct);
            CustomerSuggestions.Clear();
            foreach (var p in result.Items) CustomerSuggestions.Add(p);
        }
        catch { /* 联想失败不阻断输入 */ }
    }

    /// <summary>选中联想项：回填编号/名称（等价销售开单页 gname→gno 带出）。</summary>
    public void ApplyCustomer(Domain.Partners.Partner p)
    {
        CustomerName = p.PartName.Trim();
        CustomerNo = p.PartNo.Trim();
        CustomerSuggestions.Clear();
        Message = "";
    }

    [RelayCommand]
    /// <summary>
    /// 加载可退行。远程链路优化（2026-10-29）：默认只拉近 3 个月（大客户 5 万+全量行在 frp 链路传输约 7 秒），
    /// 「加载全部」按钮传 mode="all" 取全量。
    /// </summary>
    public async System.Threading.Tasks.Task LoadSourceAsync(string? mode = null)
    {
        var ct = System.Threading.CancellationToken.None;
        var loadAll = mode == "all";
        Message = "";
        ReturnableRows = [];
        Lines = [];
        _allRows = [];
        OnPropertyChanged(nameof(ReturnableRows));
        OnPropertyChanged(nameof(Lines));
        SourceInfo = "";
        TotalAmount = 0;
        if (string.IsNullOrWhiteSpace(CustomerNo))
        {
            Message = "请先选择客户";
            return;
        }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DateTime? from = loadAll ? null : DateTime.Today.AddMonths(-3);
            var scope = loadAll ? "全部" : "近 3 个月";
            var result = await Db.SaleReturns.GetSourceByCustomerAsync(CustomerNo, AppSession.DeptNo, from, ct);
            var tDb = sw.ElapsedMilliseconds;
            SourceInfo = $"客户：{result.Gname}    范围：{scope}    原单数：{result.Lines.Select(l => l.Paper).Distinct().Count():0}    双击可退行加入退货明细"
                         + (loadAll ? "" : "    （更早的可退行请点【加载全部】）");
            // 批量构建后整体替换：数千行逐条 Add + CollectionChanged 会拖垮 UI
            var rows = new List<ReturnableRow>(result.Lines.Count);
            foreach (var l in result.Lines)
            {
                rows.Add(new ReturnableRow
                {
                    RowId = l.RowId, Paper = l.Paper, Cp = l.Cp, Nno = l.Nno, Na1 = l.Na1,
                    DepotNo = l.DepotNo, Posi = l.Posi, Ty = l.Ty, Fa = l.Fa, Cxnno = l.Cxnno,
                    Guno = l.Guno, Gname = l.Gname, Per = l.Per, Note = l.Note, Pno = l.Pno, TMode = l.TMode,
                    Qty = l.Qty, Returned = l.Returned, Oprc = l.Oprc, Oprct = l.Oprct,
                    Iprc = l.Iprc, Iprj = l.Iprj, Yhl = l.Yhl, OutDate = l.OutDate,
                });
            }
            DebugLog.Log($"[RetLoad] 总耗时={sw.ElapsedMilliseconds}ms (DB={tDb}ms 行构建={sw.ElapsedMilliseconds - tDb}ms) rows={rows.Count}");
            _allRows = rows;
            // 拼音码映射随加载同批返回（2026-10-29 远程优化：不再单独往返 tbname0）
            _pyMap = new Dictionary<string, string>(result.PinyinMap, StringComparer.Ordinal);
            // 车型拼音码映射（tbty0 整表，2026-10-30 需求；加载失败不阻断——车型框退化为纯中文包含）
            try
            {
                var tyMap = await Db.SaleReturns.GetTyPinyinMapAsync(ct);
                _tyMap = new Dictionary<string, string>(tyMap, StringComparer.Ordinal);
            }
            catch { _tyMap = []; }
            ApplyFilter();   // 无过滤条件时=全部；有条件时按当前关键字过滤新数据
            if (ReturnableRows.Count == 0)
                Message = _allRows.Count == 0
                    ? (loadAll ? "该客户无可退行" : "近 3 个月无可退行，更早的历史可退行请点【加载全部】")
                    : "无匹配当前过滤条件的可退行";
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Message = "加载失败：" + ex.Message;
        }
    }

    /// <summary>配件批次选项（退货编辑窗「库号/仓位」下拉）。</summary>
    public async System.Threading.Tasks.Task<IReadOnlyList<ReturnBatchOption>> GetBatchOptionsAsync(string cp, string nno, CancellationToken ct = default) =>
        await Db.SaleReturns.GetBatchesAsync(cp, nno, AppSession.DeptNo, ct);

    /// <summary>
    /// 退货编辑窗确认：新增或更新该原单行的明细（同一原行只保留一条明细，再次编辑覆盖）。
    /// </summary>
    public void ApplyDetail(ReturnableRow src, decimal returnQty, decimal oprc, decimal oprct, string depotNo, string posi, bool scrap = false)
    {
        var exist = Lines.FirstOrDefault(l => l.RowId == src.RowId);
        if (exist is not null)
        {
            exist.ReturnQty = returnQty;
            exist.Oprc = oprc;
            exist.Oprct = oprct;
            exist.DepotNo = scrap ? "废品仓" : depotNo;
            exist.Posi = posi;
            exist.IsScrap = scrap;
            exist.RefreshAmount();
        }
        else
        {
            var row = new ReturnLineRow
            {
                RowId = src.RowId, Paper = src.Paper, Cp = src.Cp, Nno = src.Nno, Na1 = src.Na1,
                DepotNo = scrap ? "废品仓" : depotNo, Posi = posi, Ty = src.Ty, Fa = src.Fa, Cxnno = src.Cxnno,
                Guno = src.Guno, Gname = src.Gname, Per = src.Per, Note = src.Note, Pno = src.Pno, TMode = src.TMode,
                Qty = src.Qty, Returned = src.Returned, Remain = src.Remain,
                Oprc = oprc, Oprct = oprct, ReturnQty = returnQty,
                Iprc = src.Iprc, Iprj = src.Iprj, Yhl = src.Yhl, OutDate = src.OutDate,
                IsScrap = scrap,
            };
            row.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ReturnLineRow.Amount)) Recalc(); };
            Lines.Add(row);
        }
        Recalc();
        Message = "";
    }

    /// <summary>下区明细行双击再编辑（ sale 页同款交互）。</summary>
    public ReturnableRow? FindSource(int rowId) =>
        _allRows.FirstOrDefault(r => r.RowId == rowId);   // 从全集找（被过滤隐藏的行仍可再编辑）

    [RelayCommand]
    private void RemoveLine(ReturnLineRow? row)
    {
        if (row is null) return;
        Lines.Remove(row);
        Recalc();
    }

    private void Recalc() => TotalAmount = Lines.Sum(l => l.Amount);

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveAsync(CancellationToken ct)
    {
        var lines = Lines.Where(l => l.ReturnQty > 0).ToList();
        if (lines.Count == 0)
        {
            Message = "请先双击可退行填写退货数量";
            return;
        }
        var over = lines.FirstOrDefault(l => l.ReturnQty > l.Remain);
        if (over is not null)
        {
            Message = $"配件 {over.Cp} 退货数 {over.ReturnQty} 超过未退量 {over.Remain}";
            return;
        }

        // 确认退货窗（旧 fr_sata Frmok）：先预览取号显示单号，确认后再正式取号保存
        string previewPaper;
        IReadOnlyList<string> refundDict, reasonDict, shipDict, paperTypeDict;
        string paperTypeDefault;
        try
        {
            previewPaper = await Db.DocNo.PreviewDocNoAsync("退货单", AppSession.DeptNo, ct);
            refundDict = await Db.SaleReturns.GetRefundMethodsAsync(ct);
            reasonDict = await Db.SaleReturns.GetDictByTypeAsync("退货原因", ct);
            shipDict = await Db.SaleReturns.GetDictByTypeAsync("fhfs", ct);
            paperTypeDict = await Db.SaleReturns.GetPaperTypesAsync(ct);
            // 销售类型默认 = 第一张原单头 papertype（旧 CBOPAPERTYPE 默认逻辑；空=门店）
            paperTypeDefault = await Db.SaleReturns.GetHeaderPapertypeAsync(lines[0].Paper, AppSession.DeptNo, ct);
        }
        catch (Exception ex)
        {
            Message = "保存失败：" + ex.Message;
            return;
        }
        var firstSrc = _allRows.FirstOrDefault(r => r.RowId == lines[0].RowId);
        var shipDefault = firstSrc?.TMode ?? "";                       // 旧：IIF(EMPTY(T_MODE), 默认值, T_MODE)
        var invoiceDefault = firstSrc?.Pno ?? "";                      // 旧：TXTPNO.VALUE = C_TH.PNO

        var dialog = new Views.ReturnConfirmDialog(
            previewPaper, invoiceDefault, refundDict, reasonDict, shipDict, shipDefault, paperTypeDict, paperTypeDefault);
        if (dialog.ShowDialog() != true) return;

        var printAfter = dialog.PrintAfterConfirm;
        Reason = dialog.Reason;
        IsBusy = true;
        Message = "";
        // 一次保存合并为一张退货单（旧 fr_sata row145 语义：GETNEWDH 只取一次号，SCAN 全部明细行，
        // 每行 ypaper=各自原单号——跨原单的行（含废品行）同单保存，单事务全成全败）
        var command = new CreateSaleReturnCommand
        {
            Dept = AppSession.DeptNo,
            SourcePaper = lines[0].Paper,      // 仅作 header 默认销售类型回查与校验锚点；行级 ypaper 各写各的原单号
            Reason = Reason.Trim(),
            RefundMethod = dialog.RefundMethod,
            InvoiceNo = dialog.InvoiceNo,
            ShipMode = dialog.ShipMode,
            PaperType = dialog.PaperType,
            Lines = lines.Select(l => new SaleReturnLineInput
            {
                RowId = l.RowId,
                Qty = l.ReturnQty,
                Oprc = l.Oprc,
                Oprct = l.Oprct,
                DepotNo = l.DepotNo,
                Posi = l.Posi,
                Scrap = l.IsScrap,
            }).ToList(),
        };
        try
        {
            var result = await Db.SaleReturns.CreateAsync(command, AppSession.Operator, ct);
            LastPaper = result.Paper;
            var status = $"退货成功，退货单号 {result.Paper}（{result.LineCount} 行，退款 {result.TotalAmount:N2} 元）";
            // 立即打印（2026-10-31 需求）：勾选时静默发送默认打印机，不弹预览窗；打印失败不阻断退货
            if (printAfter)
            {
                try
                {
                    await QPGL.Desktop.Printing.PrintService.PrintSaleSilentAsync(result.Paper, "销售", ct);
                    status += "，已发送打印";
                }
                catch (Exception pex)
                {
                    status += $"，打印失败：{pex.Message}";
                }
            }
            Message = status;
            // 上区已退数/未退量回写，退完的行移除（全集与显示集合同步）
            foreach (var l in lines)
            {
                var src = _allRows.FirstOrDefault(r => r.RowId == l.RowId);
                if (src is null) continue;
                src.Returned += l.ReturnQty;
                if (src.Remain <= 0)
                {
                    _allRows.Remove(src);
                    ReturnableRows.Remove(src);
                }
            }
            Lines.Clear();
            TotalAmount = 0;
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Message = "保存失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
