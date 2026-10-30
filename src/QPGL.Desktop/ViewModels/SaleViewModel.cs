using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Domain.Partners;
using QPGL.Domain.Sales;
using QPGL.Infrastructure;

namespace QPGL.Desktop.ViewModels;

/// <summary>开单明细行（可编辑数量/单价，自动算金额）。</summary>
public partial class SaleLineRow : ObservableObject
{
    public int Kcid { get; init; }
    public string Cp { get; init; } = "";
    public string Nno { get; init; } = "";
    public string Na1 { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Posi { get; init; } = "";
    public decimal BatchStock { get; init; }   // 批次当时库存
    public decimal Oprb { get; init; }         // 批发价（编辑对话框切换用）

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount))]
    private decimal _qty = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount))]
    private decimal _price;

    [ObservableProperty] private string _memo = "";

    [ObservableProperty] private string _ty = "";   // 车型（编辑对话框可改，随行保存）

    public decimal Amount => Qty * Price;
}

/// <summary>
/// 销售开单（qp11 SellControl 单屏复合布局）。
/// 直连模式：直调 IPartRepository / ISaleRepository（触发器语义感知 + 回读断言在仓储内）。
/// </summary>
public partial class SaleViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<PartItem> PartResults { get; } = [];
    public ObservableCollection<SaleLineRow> Lines { get; } = [];

    [ObservableProperty] private string _partCp = "";
    [ObservableProperty] private string _partNa1 = "";
    [ObservableProperty] private string _partTy = "";
    [ObservableProperty] private string _partNote = "";   // 备注查询框（2026-10-30 需求）
    [ObservableProperty] private string _gno = "";
    [ObservableProperty] private string _gname = "";
    [ObservableProperty] private string _remark = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _lastPaper = "";
    /// <summary>查询条厂牌提示（2026-10-29 需求）：正常="厂牌：X"，跨厂牌回退后="临时查看：Y"，未选厂牌为空。</summary>
    [ObservableProperty] private string _brandHint = "";

    /// <summary>改单来源单号（查询模式「修改」载入的原单；保存新单成功后作废它，改单=删旧开新）。</summary>
    private string? _editSourcePaper;

    private string Dept => AppSession.DeptNo;

    /// <summary>单据类型：'销售'（默认）/ '调拨'（菜单 C.3，指定批次单批出库，取号「调拨单」）。</summary>
    public string DocType { get; init; } = "销售";

    public string Title => DocType + "管理";

    /// <summary>实时搜索防抖（300ms，三条件任一变化触发；2026-10-29 远程链路由 500ms 调低缩短感知延迟）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _debounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };

    /// <summary>「最近进价」列当前是否可见（远程优化：默认隐藏时搜索跳过进价回填，省一次 tbistoed 查询+往返；View 空格切换时同步）。</summary>
    public bool IsLastIprjVisible { get; set; }

    /// <summary>配件搜索请求序号（竞态守卫：只接受最新一次请求的结果）。</summary>
    private int _searchSeq;

    /// <summary>当前结果集实际生效的厂牌过滤（空=未过滤；跨厂牌回退后=门市等），LoadMore 翻页沿用同一口径。</summary>
    private string _effectiveBrand = "";

    /// <summary>无限滚动分页状态。</summary>
    private const int PartPageSize = 100;
    private int _partPage = 1;
    private int _partTotal;
    private bool _loadingMore;

    /// <summary>客户名称/拼音码联想防抖（300ms）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _custDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };

    public ObservableCollection<Partner> CustomerSuggestions { get; } = [];

    public SaleViewModel()
    {
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await SearchPartsAsync(System.Threading.CancellationToken.None);
        };
        _custDebounce.Tick += async (_, _) =>
        {
            _custDebounce.Stop();
            await SearchCustomerSuggestionsAsync(System.Threading.CancellationToken.None);
        };
    }

    /// <summary>客户输入变化时调用：重置联想防抖（View 的 TextChanged 转发）。</summary>
    public void DebounceCustomerSearch()
    {
        _custDebounce.Stop();
        _custDebounce.Start();
    }

    /// <summary>
    /// 客户联想搜索（旧 cmm_class row326 FRDM 下拉机制 WPF 化）：
    /// tbgu 按 gname/gno/code（拼音码）/code1（数字码）/tel/mobile 前缀匹配，取全量匹配（PageSize=200）。
    /// </summary>
    public async System.Threading.Tasks.Task SearchCustomerSuggestionsAsync(CancellationToken ct = default)
    {
        var kw = Gname?.Trim();
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

    /// <summary>选中联想项：回填编号/名称（等价旧 row298 gname→gno 带出）。</summary>
    public void ApplyCustomer(Partner p)
    {
        Gname = p.PartName.Trim();
        Gno = p.PartNo.Trim();
        CustomerSuggestions.Clear();
        Message = "";
    }

    /// <summary>输入变化时调用：重置防抖计时（View 的 TextChanged 转发）。</summary>
    public void DebounceSearch()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>取消挂起的防抖搜索（回车立即搜时调用，避免防抖到期重复查询）。</summary>
    public void CancelPendingSearch() => _debounce.Stop();

    /// <summary>「最近进价」列切换为可见后：对当前页按需补取进价（远程优化：搜索时不预取）。</summary>
    [RelayCommand]
    public async System.Threading.Tasks.Task FillLastIprjAsync()
    {
        if (PartResults.Count == 0) return;
        try
        {
            await Db.Parts.FillLastIprjAsync(PartResults, Dept);
        }
        catch { /* 进价回填失败不影响搜索结果 */ }
    }

    /// <summary>客户名称带出编号（row298：查 tbgu by gname 取首行 gno）。</summary>
    public async System.Threading.Tasks.Task LookupCustomerAsync(CancellationToken ct = default)
    {
        var name = Gname?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var request = new PagedRequest { Page = 1, PageSize = 1, Sort = "partName", Order = "asc", Keyword = name };
            var result = await Db.Partners.SearchAsync(request, ct);
            var hit = result.Items.FirstOrDefault(p => p.PartName.Trim() == name) ?? result.Items.FirstOrDefault();
            if (hit is not null)
            {
                Gno = hit.PartNo;
                Message = "";
            }
            else
            {
                Message = $"客户「{name}」不存在，请先到基础资料建档后再选择";
            }
        }
        catch { /* 客户带出失败不阻断开单 */ }
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task SearchPartsAsync(CancellationToken ct)
    {
        // 竞态守卫：递增序号，慢的旧请求返回后直接丢弃（防止空条件查询晚到覆盖过滤结果）
        var seq = ++_searchSeq;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DebugLog.Log($"[SaleSearch] 触发 cp='{PartCp.Trim()}' na1='{PartNa1.Trim()}' ty='{PartTy.Trim()}' note='{PartNote.Trim()}'");
        Message = "";
        // 厂牌过滤（2026-10-29 需求）：登录选择的厂牌初始只显示该厂牌编码；未选厂牌不过滤
        var brand = AppSession.Brand.Trim();
        BrandHint = brand.Length > 0 ? $"厂牌：{brand}" : "";
        try
        {
            var request = new PagedRequest { Page = 1, PageSize = PartPageSize };
            var result = await Db.Parts.SearchAsync(Dept, PartCp.Trim(), PartNa1.Trim(), PartTy.Trim(), request,
                PartNote.Trim(), fillLastIprj: IsLastIprjVisible, brand, ct: ct);
            if (seq != _searchSeq) return;
            var tDb = sw.ElapsedMilliseconds;
            PartResults.Clear();
            foreach (var p in result.Items) PartResults.Add(p);
            _partPage = 1;
            _partTotal = result.Total;
            _loadingMore = false;
            _effectiveBrand = brand;
            DebugLog.Log($"[SaleSearch] 总耗时={sw.ElapsedMilliseconds}ms (DB={tDb}ms UI替换={sw.ElapsedMilliseconds - tDb}ms) rows={result.Items.Count}");
            var hasCondition = PartCp.Trim().Length > 0 || PartNa1.Trim().Length > 0 || PartTy.Trim().Length > 0 || PartNote.Trim().Length > 0;

            // 跨厂牌回退（2026-10-29 需求）：当前厂牌无命中且带条件时，查其它厂牌命中数，弹窗确认后临时加载
            if (result.Total == 0 && hasCondition && brand.Length > 0)
            {
                var others = await Db.Parts.CountOtherBrandsAsync(Dept, brand, PartCp.Trim(), PartNa1.Trim(), PartTy.Trim(), ct, PartNote.Trim());
                if (seq != _searchSeq) return;
                // 厂牌白名单（角色 usemcp）：非空时仅提示白名单内的厂牌；空 = 无限制
                var wl = AppSession.BrandWhitelist;
                if (wl.Length > 0) others = [.. others.Where(o => wl.Contains(o.Cp))];
                if (others.Count > 0)
                {
                    var detail = string.Join("，", others.Select(o => $"【{o.Cp}】{o.Count} 条"));
                    var top = others.OrderByDescending(o => o.Count).First().Cp;
                    var answer = System.Windows.MessageBox.Show(
                        $"当前厂牌【{brand}】未检索到该配件，{detail}。\n\n是否加载{top}编码？",
                        "跨厂牌检索", System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question,
                        System.Windows.MessageBoxResult.No);
                    if (answer == System.Windows.MessageBoxResult.Yes)
                    {
                        var alt = await Db.Parts.SearchAsync(Dept, PartCp.Trim(), PartNa1.Trim(), PartTy.Trim(), request,
                            PartNote.Trim(), fillLastIprj: IsLastIprjVisible, top, ct: ct);
                        if (seq != _searchSeq) return;
                        PartResults.Clear();
                        foreach (var p in alt.Items) PartResults.Add(p);
                        _partTotal = alt.Total;
                        _effectiveBrand = top;
                        BrandHint = $"临时查看：{top}";
                        Message = alt.Total == 0 ? "无匹配配件" : "";
                        DebugLog.Log($"[SaleSearch] 跨厂牌回退 brand='{top}' rows={alt.Items.Count} total={alt.Total}");
                        return;
                    }
                }
            }

            Message = result.Total == 0 && hasCondition ? "无匹配配件" : "";
        }
        catch (Exception ex)
        {
            if (seq == _searchSeq) Message = "查询失败：" + ex.Message;
        }
    }

    /// <summary>无限滚动：滚动到底自动加载下一页（竞态与防重入由 _loadingMore + seq 守卫）。</summary>
    public async System.Threading.Tasks.Task LoadMorePartsAsync(CancellationToken ct = default)
    {
        if (_loadingMore || PartResults.Count == 0 || PartResults.Count >= _partTotal) return;
        _loadingMore = true;
        var seq = _searchSeq;
        try
        {
            var request = new PagedRequest { Page = _partPage + 1, PageSize = PartPageSize };
            var result = await Db.Parts.SearchAsync(Dept, PartCp.Trim(), PartNa1.Trim(), PartTy.Trim(), request,
                PartNote.Trim(), fillLastIprj: IsLastIprjVisible, _effectiveBrand, ct: ct);
            if (seq != _searchSeq) return;
            _partPage++;
            _partTotal = result.Total;
            foreach (var p in result.Items) PartResults.Add(p);
        }
        catch (Exception ex)
        {
            if (seq == _searchSeq) Message = "加载失败：" + ex.Message;
        }
        finally
        {
            _loadingMore = false;
        }
    }

    /// <summary>双击配件搜索行 → 弹销售编辑对话框（qp11 SellEditDialog 风格），确认后加入明细。</summary>
    public async System.Threading.Tasks.Task AddLineViaDialogAsync(PartItem part)
    {
        // 报价单行 kcid=0（row469 报价行不占批次），防重按零件编码；销售/调拨按批次
        var isQuote = DocType == "报价";
        if (Lines.Any(l => isQuote ? l.Cp == part.Cp && l.Nno == part.Nno : l.Kcid == part.Id))
        {
            Message = "该批次已在明细中，请双击明细行修改数量";
            return;
        }

        var dialog = new Views.SaleLineEditDialog(part, Dept, customerName: Gname?.Trim());
        if (dialog.ShowDialog() != true) return;

        var row = new SaleLineRow
        {
            Kcid = part.Id,
            Cp = part.Cp,
            Nno = part.Nno,
            Na1 = part.Na1,
            Unit = part.Unit,
            Posi = part.Posi,
            BatchStock = part.Kcamount,
            Oprb = part.Oprb,
            Price = dialog.Price,
            Qty = dialog.Qty,
            Ty = dialog.LineTy,
            Memo = dialog.LineMemo,
        };
        row.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(SaleLineRow.Amount)) RecalcTotal(); };
        Lines.Add(row);
        RecalcTotal();
        Message = "";
    }

    /// <summary>双击明细行 → 再次弹窗修改车型/数量/单价/备注。</summary>
    public void EditExistingLine(SaleLineRow row)
    {
        var part = new PartItem
        {
            Id = row.Kcid,
            Cp = row.Cp,
            Nno = row.Nno,
            Na1 = row.Na1,
            Ty = row.Ty,
            Fa = "",
            Dc = "",
            Unit = row.Unit,
            Kcamount = row.BatchStock,
            Oprc = row.Price,
            Oprb = row.Oprb,
            Posi = row.Posi,
        };
        var dialog = new Views.SaleLineEditDialog(part, Dept, row.Qty, row.Price, row.Memo, customerName: Gname?.Trim())
        {
            Title = "销售编辑 - 修改明细行",
        };
        if (dialog.ShowDialog() != true) return;

        row.Qty = dialog.Qty;
        row.Price = dialog.Price;
        row.Ty = dialog.LineTy;
        row.Memo = dialog.LineMemo;
        RecalcTotal();
    }

    [RelayCommand]
    private void RemoveLine(SaleLineRow? row)
    {
        if (row is null) return;
        Lines.Remove(row);
        RecalcTotal();
    }

    private void RecalcTotal() => TotalAmount = Lines.Sum(l => l.Amount);

    /// <summary>保存后打印（F7）：预览确认后输出打印机（§8.3.1）。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task PrintPreviewAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(LastPaper))
        {
            Message = "请先保存单据再打印";
            return;
        }
        try
        {
            await Printing.PrintService.PreviewSaleAsync(LastPaper, DocType, ct);
        }
        catch (Exception ex) { Message = "打印失败：" + ex.Message; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task PrintAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(LastPaper))
        {
            Message = "请先保存单据再打印";
            return;
        }
        try
        {
            await Printing.PrintService.PrintSaleAsync(LastPaper, DocType, ct);
            Message = $"已发送打印：{LastPaper}";
        }
        catch (Exception ex) { Message = "打印失败：" + ex.Message; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveAsync(CancellationToken ct)
    {
        if (Lines.Count == 0)
        {
            Message = "明细为空";
            return;
        }
        if (string.IsNullOrWhiteSpace(Gname) || string.IsNullOrWhiteSpace(Gno))
        {
            Message = "请选择客户";
            return;
        }
        IsBusy = true;
        Message = "";
        try
        {
            var command = new CreateSaleCommand
            {
                Dept = Dept,
                Gno = Gno.Trim(),
                Gname = Gname.Trim(),
                Remark = Remark.Trim(),
                DocType = DocType,
                // 改单（EMODE）：复用原单号原地更新（旧 ___.row480 语义：mark<>'' 已确认禁改；
                // 删旧行重插同一事务，sale_del 回补 + sale_in 重扣，库存净差额正确）
                Paper = _editSourcePaper,
                Lines = Lines.Select(l => new SaleLineInput
                {
                    Cp = l.Cp,
                    Nno = l.Nno,
                    Qty = l.Qty,
                    Price = l.Price,
                    Remark = l.Memo,
                    // 调拨（Lf_.row511）：明细行即所选批次，指定批次单批出库不拆批；销售走自动凑批
                    Kcid = DocType == "调拨" ? l.Kcid : null,
                }).ToList(),
            };
            var result = await Db.Sales.CreateAsync(command, AppSession.Operator, ct);
            LastPaper = result.Paper;
            var wasEdit = !string.IsNullOrEmpty(_editSourcePaper);
            _editSourcePaper = null;
            Message = wasEdit
                ? $"改单完成：{result.Paper} 已更新（{result.LineCount} 行，合计 {result.TotalAmount:N2} 元）"
                : $"保存成功，单号 {result.Paper}（{result.LineCount} 行，合计 {result.TotalAmount:N2} 元）";
            Lines.Clear();
            RecalcTotal();
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

    /// <summary>确认单据（保存 + row298 FRMOK）：未保存先自动保存取号，再弹整单确认窗采集支付/优惠等，报价跳过应收检查。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ConfirmDocAsync(CancellationToken ct)
    {
        var amountBefore = TotalAmount;          // 保存会清空明细，先取优惠前合计
        if (string.IsNullOrEmpty(LastPaper))
        {
            await SaveAsync(ct);
            if (string.IsNullOrEmpty(LastPaper)) return;   // 保存失败（Message 已提示）
        }
        if (amountBefore == 0 && !string.IsNullOrEmpty(LastPaper))
        {
            // 已保存过的单（重开确认）：从单据汇总回读合计，避免显示 0
            try
            {
                var doc = await Db.DocQuery.GetOutDocSummaryAsync(LastPaper.Trim(), DocType, Dept, ct);
                amountBefore = doc.TotalDiscounted;
            }
            catch { /* 回读失败保持 0 */ }
        }
        var dialog = new Views.ConfirmSaleDialog(LastPaper, DocType, Gno.Trim(), Gname.Trim(), amountBefore, Remark.Trim());
        if (dialog.ShowDialog() != true) return;
        var printAfter = dialog.PrintAfterConfirm;

        IsBusy = true;
        Message = "";
        try
        {
            var cmd = dialog.Command;
            cmd.Dept = Dept;
            var result = await Db.Sales.ConfirmAsync(cmd, AppSession.Operator, ct);
            // 确认成功：清空开单面板。打印勾选（2026-10-30 需求）→ 留在开单模式直接发送打印机（打印失败不阻断确认）；
            // 未勾选 → 通知宿主切到查询模式（旧系统确认后回到单据浏览语义）。宿主经 SkipQuerySwitchAfterConfirm 区分。
            SkipQuerySwitchAfterConfirm = printAfter;
            ClearBill();
            if (printAfter)
            {
                try
                {
                    // 静默打印（2026-10-30 需求）：直接输出默认打印机，不弹系统打印对话框
                    await Printing.PrintService.PrintSaleSilentAsync(result.Paper, DocType, ct);
                    Message = $"已确认 {result.Paper}（结帐日期 {result.Jzdate:yyyy-MM-dd}），已发送打印";
                }
                catch (Exception pex)
                {
                    Message = $"已确认 {result.Paper}（结帐日期 {result.Jzdate:yyyy-MM-dd}），打印失败：{pex.Message}";
                }
            }
            else
            {
                Message = $"已确认 {result.Paper}（结帐日期 {result.Jzdate:yyyy-MM-dd}）";
            }
            ConfirmSucceeded?.Invoke(this, EventArgs.Empty);
        }
        catch (AppException ex) { Message = ex.Message; }
        catch (Exception ex) { Message = "确认失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>整单确认成功事件（宿主 SaleControl 订阅：清屏后自动切查询模式）。</summary>
    public event EventHandler? ConfirmSucceeded;

    /// <summary>确认后是否跳过切查询模式（2026-10-30 需求：勾选"确认后立即打印"时留在开单模式，宿主读取后自行处理）。</summary>
    public bool SkipQuerySwitchAfterConfirm { get; private set; }

    /// <summary>销售转预售（row53 销售分支 M_PD=6：UPDATE 原单改 type/paper/ypaper/note/outdate 换预售号，库存零变动，需 C2 权限）。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ConvertSaleToPresaleAsync(CancellationToken ct)
    {
        var paper = PromptPaper("销售转预售", "输入要转为预售的销售单号：", LastPaper);
        if (string.IsNullOrEmpty(paper)) return;
        IsBusy = true;
        Message = "";
        try
        {
            var result = await Db.Sales.ConvertSaleToPresaleAsync(paper, Dept, AppSession.Operator, ct);
            LastPaper = result.NewPaper;
            Message = $"已生成预售单 {result.NewPaper}（{result.LineCount} 行），原销售单 {result.OldPaper} 已转换";
        }
        catch (AppException ex) { Message = ex.Message; }
        catch (Exception ex) { Message = "转单失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>预售转销售（row53 预售分支：UPDATE 原单改 5 列 + 换销售号，note='预售转销售'+日期，需 CA 权限）。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ConvertPresaleToSaleAsync(CancellationToken ct)
    {
        var paper = PromptPaper("预售转销售", "输入要转为销售的预售单号：", LastPaper);
        if (string.IsNullOrEmpty(paper)) return;
        IsBusy = true;
        Message = "";
        try
        {
            var result = await Db.Sales.ConvertPresaleToSaleAsync(paper, Dept, AppSession.Operator, ct);
            LastPaper = result.NewPaper;
            Message = $"已生成销售单 {result.NewPaper}（{result.LineCount} 行），原预售单 {result.OldPaper} 已转换";
        }
        catch (AppException ex) { Message = ex.Message; }
        catch (Exception ex) { Message = "转单失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>销售单转报价（row53 销售分支）：本页输入原销售单号，生成新报价单并删除原销售单。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ConvertToQuoteAsync(CancellationToken ct)
    {
        var paper = PromptPaper("销售转报价", "输入要转为报价的销售单号：", LastPaper);
        if (string.IsNullOrEmpty(paper)) return;
        IsBusy = true;
        Message = "";
        try
        {
            var result = await Db.Sales.ConvertSaleToQuoteAsync(paper, Dept, AppSession.Operator, ct);
            LastPaper = result.NewPaper;
            Message = $"已生成报价单 {result.NewPaper}（{result.LineCount} 行），原销售单 {result.OldPaper} 已删除";
        }
        catch (AppException ex) { Message = ex.Message; }
        catch (Exception ex) { Message = "转单失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// 查询模式「修改」：回读选中单据的单头+明细填充开单面板（旧 fr_sesa 修改入口语义）。
    /// 明细行单价取实收价 oprct，库存/零售价回读批次补齐；编辑保存将生成新单（旧系统改单=删旧开新）。
    /// </summary>
    public async System.Threading.Tasks.Task LoadForEditAsync(string paper, string type, string dpm, CancellationToken ct = default)
    {
        try
        {
            var doc = await Db.DocQuery.GetOutDocSummaryAsync(paper.Trim(), type.Trim(), dpm.Trim(), ct);
            var lines = await Db.DocQuery.GetOutDocLinesAsync(paper.Trim(), type.Trim(), dpm.Trim(), ct);

            ClearBill();
            Message = "";
            Gno = doc.Gno;
            Gname = doc.Gname;
            Remark = doc.Remark;
            _editSourcePaper = doc.Paper;   // 改单来源（保存成功后作废），不占 LastPaper——确认时须走保存生成新单

            foreach (var l in lines)
            {
                var row = new SaleLineRow
                {
                    Cp = l.Cp.Trim(),
                    Nno = l.Nno.Trim(),
                    Na1 = l.Na1.Trim(),
                    Unit = l.Unit.Trim(),
                    Posi = l.Posi.Trim(),
                    Price = l.Oprct,          // 实收价（旧系统改单按折后价带出）
                    Qty = l.Ckamount,
                    Memo = l.Remark.Trim(),
                };
                row.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(SaleLineRow.Amount)) RecalcTotal(); };
                Lines.Add(row);
            }
            RecalcTotal();
            Message = $"已载入 {doc.Paper}（{Lines.Count} 行），修改后保存将生成新单";
        }
        catch (Exception ex)
        {
            Message = "载入单据失败：" + ex.Message;
        }
    }

    /// <summary>库存分货（fr_kcfh row23）：仅调拨页（用户决策：分货并入调拨管理页按钮打开对话框）。</summary>
    [RelayCommand]
    private void OpenDistribute()
    {
        var owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
        var dlg = new Views.StockDistributeDialog { Owner = owner };
        dlg.ShowDialog();
        if (dlg.DataContext is StockDistributeDialogViewModel vm && vm.Result is { LineCount: > 0 } r)
            Message = $"分货完成，生成调拨单 {r.FirstPaper} 至 {r.LastPaper}，共 {r.LineCount} 行";
    }

    /// <summary>F9：清空销售开单所有输入内容和已添加的明细。</summary>
    public void ClearBill()
    {
        Gno = "";
        Gname = "";
        Remark = "";
        LastPaper = "";
        _editSourcePaper = null;
        PartCp = "";
        PartNa1 = "";
        PartTy = "";
        PartNote = "";
        Lines.Clear();
        CustomerSuggestions.Clear();
        RecalcTotal();
        Message = "已清空开单内容 (F9)";
    }

    /// <summary>单行文本输入小窗（SupplierPriceViewModel.ShowInputBox 同构）；取消返回 null。</summary>
    private static string? PromptPaper(string title, string label, string value)
    {
        var box = new System.Windows.Controls.TextBox { Text = value, Width = 220, Margin = new System.Windows.Thickness(0, 4, 0, 0) };
        var win = new System.Windows.Window
        {
            Title = title, Width = 380, SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.NoResize, ShowInTaskbar = false,
        };
        string? result = null;
        var ok = new System.Windows.Controls.Button { Content = "确定", IsDefault = true, Padding = new System.Windows.Thickness(22, 4, 22, 4) };
        if (System.Windows.Application.Current.TryFindResource("PrimaryButtonStyle") is System.Windows.Style s) ok.Style = s;
        ok.Click += (_, _) => { result = box.Text.Trim(); win.DialogResult = true; };
        var cancel = new System.Windows.Controls.Button { Content = "取消", IsCancel = true, Padding = new System.Windows.Thickness(22, 4, 22, 4), Margin = new System.Windows.Thickness(10, 0, 0, 0) };
        var body = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
        body.Children.Add(new System.Windows.Controls.TextBlock { Text = label });
        body.Children.Add(box);
        var btns = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 12, 0, 0) };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        body.Children.Add(btns);
        win.Content = body;
        win.Owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
        return win.ShowDialog() == true && result?.Length > 0 ? result : null;
    }

    public void Dispose()
    {
        _debounce.Stop();
    }
}
