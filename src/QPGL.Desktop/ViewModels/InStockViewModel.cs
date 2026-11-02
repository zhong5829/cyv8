using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.ViewModels;

/// <summary>散件入库明细行（可编辑 Grid 行）。</summary>
public partial class InStockLineRow : ObservableObject
{
    [ObservableProperty] private string _cp = "";
    [ObservableProperty] private string _nno = "";
    [ObservableProperty] private string _na1 = "";
    [ObservableProperty] private string _ty = "";
    [ObservableProperty] private string _fa = "";
    [ObservableProperty] private string _dc = "";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private decimal _jkamount = 1;
    [ObservableProperty] private decimal _iprc;
    [ObservableProperty] private decimal _iprj;
    [ObservableProperty] private decimal _oprc;
    [ObservableProperty] private decimal _oprb;
    [ObservableProperty] private string _posi = "";
    [ObservableProperty] private string _depotno = "";
    [ObservableProperty] private string _note = "";
    /// <summary>tbistosm 暂存行 id（0=未暂存；保存暂存后回填，删行时精确删暂存行，2026-11-02）。</summary>
    public int SmId { get; set; }
}

/// <summary>
/// 散件入库 VM（B1，M4-1）。直调 IInStockRepository（复刻 fr_isor 收货 + fr_isto1 生效序列）。
/// </summary>
public partial class InStockViewModel : ObservableObject
{
    public ObservableCollection<InStockLineRow> Lines { get; } = [];

    [ObservableProperty] private string _cno = "";
    [ObservableProperty] private string _gyno = "";
    [ObservableProperty] private string _gyname = "";
    [ObservableProperty] private string _pno = "";
    [ObservableProperty] private string _fkfs = "";
    [ObservableProperty] private string _ywr = "";
    [ObservableProperty] private ObservableCollection<string> _payTerms = [];
    [ObservableProperty] private ObservableCollection<string> _warehouses = [];
    [ObservableProperty] private ObservableCollection<string> _operators = [];

    /// <summary>供应商联想结果（旧 cmm_class FRDM 机制：拼音码/数字码/名称/编号前缀匹配）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Domain.Partners.SupplierRow> SupplierSuggestions { get; } = [];

    /// <summary>供应商输入变化时调用：重置联想防抖（View 的 TextChanged 转发）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _gysDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private decimal _totalAmount;
    /// <summary>脏标记（2026-11-02 暂存需求）：加/改/删明细后为 true，保存暂存或生效后清零，关闭页面/退出时据此提示。</summary>
    [ObservableProperty] private bool _isDirty;

    public InStockLineRow? SelectedLine { get; set; }

    public InStockViewModel()
    {
        // 供应商联想防抖（与 SaleViewModel 同机制）
        _gysDebounce.Tick += async (_, _) =>
        {
            _gysDebounce.Stop();
            await SearchSupplierSuggestionsAsync(System.Threading.CancellationToken.None);
        };
    }

    /// <summary>供应商输入变化时调用：重置联想防抖计时（View 的 TextChanged 转发）。</summary>
    public void DebounceSupplierSearch()
    {
        _gysDebounce.Stop();
        _gysDebounce.Start();
    }

    /// <summary>
    /// 供应商联想搜索（tbgugys 按 gyname/gyno/code 拼音码/code1 数字码匹配）。
    /// </summary>
    public async System.Threading.Tasks.Task SearchSupplierSuggestionsAsync(CancellationToken ct = default)
    {
        var kw = Gyname?.Trim();
        if (string.IsNullOrEmpty(kw))
        {
            SupplierSuggestions.Clear();
            return;
        }
        try
        {
            var request = new PagedRequest { Page = 1, PageSize = 200, Sort = "gyno", Order = "asc", Keyword = kw };
            var result = await Db.Suppliers.SearchAsync(request, ct);
            SupplierSuggestions.Clear();
            foreach (var s in result.Items) SupplierSuggestions.Add(s);
        }
        catch { /* 联想失败不阻断输入 */ }
    }

    /// <summary>选中联想项：回填供应商编号/名称。</summary>
    public void ApplySupplier(Domain.Partners.SupplierRow s)
    {
        Gyname = s.Gyname.Trim();
        Gyno = s.Gyno.Trim();
        SupplierSuggestions.Clear();
    }

    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            PayTerms = [.. (await Db.Receipts.GetPaymentMethodsAsync(ct)).Select(m => m.Name)];
            // 默认「挂帐」（旧 fr_isto 辎_.row192 L18-20：非 NS 部署生效按钮默认置挂帐）
            Fkfs = PayTerms.Contains("挂帐") ? "挂帐" : PayTerms.FirstOrDefault() ?? "";
            Warehouses = [.. await Db.Stock.GetWarehousesAsync(ct)];
            // 业务员下拉（旧 fr_isto row203：tbsysset type='ywr' 优先，无则 tbkey.per 全量）
            var ywrList = await Db.InStock.GetYwrListAsync(ct);
            Operators = [.. ywrList];
            // 默认当前登录操作员
            if (Operators.Contains(AppSession.Operator)) Ywr = AppSession.Operator;
            else if (Operators.Count > 0 && string.IsNullOrWhiteSpace(Ywr)) Ywr = Operators[0];
            if (Warehouses.Count > 0) AddLine();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private Task Loaded(CancellationToken ct) => LoadedAsync(ct);

    [RelayCommand]
    private void AddLine()
    {
        var row = new InStockLineRow { Depotno = Warehouses.FirstOrDefault() ?? "", Cp = AppSession.Brand };   // 旧行厂牌默认 P_CP
        HookRow(row);
        Lines.Add(row);
    }

    /// <summary>行事件统一挂钩：数量/进价联动合计 + 任意改动置脏标记（2026-11-02 暂存需求）。
    /// 注意必须在行属性初始化完成之后挂钩（构造期赋值不算脏）。</summary>
    private void HookRow(InStockLineRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InStockLineRow.Iprj) or nameof(InStockLineRow.Jkamount))
                RecalcTotal();
            IsDirty = true;
        };
    }

    /// <summary>
    /// 添加配件（View 弹 PartPickWindow 三条件选配件 → 双击弹 InStockLineEditDialog 录入数量价格等 → 写入明细行）。
    /// VM 侧只负责把对话框结果装配成行（弹窗交互在 View，与 SaleControl.AddLineViaDialogAsync 同构）。
    /// </summary>
    public void AddPartRow(Domain.Parts.PartItem part, Views.InStockLineEditResult r)
    {
        var row = new InStockLineRow
        {
            Cp = part.Cp,
            Nno = part.Nno,
            Na1 = part.Na1,
            Ty = r.Ty,
            Fa = r.Fa,
            Dc = r.Dc,
            Unit = r.Unit,
            Jkamount = r.Jkamount,
            Iprc = r.Iprc,
            Iprj = r.Iprj,
            Oprc = r.Oprc,
            Oprb = r.Oprb,
            Posi = r.Posi,
            Depotno = r.Depotno,
            Note = r.Note,
        };
        HookRow(row);
        Lines.Add(row);
        IsDirty = true;
        RecalcTotal();
    }

    /// <summary>回写已有明细行（重复添加同配件 → 用户确认修改后，编辑窗结果覆盖该行）。</summary>
    public void UpdatePartRow(ViewModels.InStockLineRow row, Views.InStockLineEditResult r)
    {
        row.Jkamount = r.Jkamount;
        row.Iprc = r.Iprc;
        row.Iprj = r.Iprj;
        row.Oprc = r.Oprc;
        row.Oprb = r.Oprb;
        row.Posi = r.Posi;
        row.Depotno = r.Depotno;
        row.Fa = r.Fa;
        row.Dc = r.Dc;
        row.Unit = r.Unit;
        row.Ty = r.Ty;
        row.Note = r.Note;
        IsDirty = true;
        RecalcTotal();
    }

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine is null) return;
        var smId = SelectedLine.SmId;
        Lines.Remove(SelectedLine);
        RecalcTotal();
        IsDirty = true;
        // 已暂存行同步删除（2026-11-02）：避免不点保存直接关页面后重开恢复出已删行
        if (smId > 0) _ = Db.InStock.DeleteDraftLineAsync(smId);
    }

    /// <summary>
    /// 表格导入确认后写明细（qp11 BuyControl.BtnExcelImport_Click L265-293 同构）：
    /// 属性库命中行带出名称/车型/进价/单位（表格值优先），未命中新配件行（生效时自动建档）。
    /// 价格（旧 ___.row214 L52-58）：进价(不含税)取表格值、空则属性库价；有进价时按
    /// tbsysset 进价/售价/批价比率推导 iprc/oprc/oprb（「外币为含税价」定方向）。
    /// </summary>
    public async Task AddImportedRowAsync(Views.ImportRowResult item)
    {
        if (string.IsNullOrWhiteSpace(item.Nno) || item.Qty <= 0) return;

        var pf = item.MatchedProfile;
        var iprj = item.Iprj > 0 ? item.Iprj : (pf?.Iprj ?? 0);

        decimal iprc = 0, oprc = 0, oprb = 0;
        if (iprj > 0)
        {
            // 比率推导（旧 L38-58）：tbsysset 缺省 0.08/0.05/0.09，外币为含税价定方向
            var (rJj, rSj, rPj, wbtohs) = await Db.InStock.GetImportRatiosAsync();
            iprc = wbtohs ? Math.Round(iprj / rJj, 2) : Math.Round(iprj * rJj, 2);
            oprc = wbtohs ? Math.Round(iprj * rSj, 2) : Math.Round(iprc * rSj, 2);
            oprb = wbtohs ? Math.Round(iprj * rPj, 2) : Math.Round(iprc * rPj, 2);
        }

        // 2026-10-27 用户确认：表格未填仓位且属性库命中 → 带出库内现有批次仓位/仓库
        //   （ISAPPE 批次键 cp+nno+posi+depotno+dpm：空仓位与库内批次不同键会另插新批次行，表现为“多出一个配件”）；
        //   表格填了仓位仍按表格；库内无批次（新配件）维持空仓位/默认仓库
        var posi = item.Posi?.Trim() ?? "";
        var depotno = Warehouses.FirstOrDefault() ?? "";
        if (pf is not null && posi.Length == 0)
        {
            var batch = await Db.InStock.GetTopBatchPosiAsync(pf.Cp.Trim(), item.Nno.Trim(), AppSession.DeptNo);
            if (batch is not null)
            {
                posi = batch.Value.Posi;
                depotno = batch.Value.Depotno;
            }
        }

        AddPartRowDirect(new InStockLineRow
        {
            Cp = pf?.Cp.Trim() ?? AppSession.Brand,
            Nno = item.Nno.Trim(),
            // 名称/车型：属性库命中一律用库内原值（不按表格更新，2026-10-26 用户确认）；
            // 仅未命中（新配件建档）才用表格值
            Na1 = pf is not null ? pf.Na1.Trim() : item.Na1?.Trim() ?? "",
            Ty = pf is not null ? pf.Ty.Trim() : item.Ty?.Trim() ?? "",
            Fa = pf?.Fa.Trim() ?? "",
            Dc = pf?.Dc.Trim() ?? "",
            Unit = pf?.Unit.Trim() ?? "",
            Jkamount = item.Qty,
            Iprj = iprj,
            Iprc = iprc,
            Oprc = oprc,
            Oprb = oprb,
            Posi = posi,
            Depotno = depotno,
            Note = item.Note?.Trim().Length > 0 ? item.Note.Trim() : pf is null ? "未建档" : "",
        });
    }

    /// <summary>同步包装（View 事件处理器用 async void 语义自由调用）。</summary>
    public void AddImportedRow(Views.ImportRowResult item) =>
        _ = AddImportedRowAsync(item);

    /// <summary>直接追加导入行（不弹窗，含金额联动）。</summary>
    private void AddPartRowDirect(InStockLineRow row)
    {
        HookRow(row);
        Lines.Add(row);
        IsDirty = true;
        RecalcTotal();   // 行价格在初始化器中已赋值，追加后主动重算（修复导入后总金额为 0）
    }

    /// <summary>明细行 → 生效/暂存共用输入模型。</summary>
    private IEnumerable<InStockLineInput> ToLineInputs() => Lines.Select(l => new InStockLineInput
    {
        Cp = l.Cp, Nno = l.Nno, Na1 = l.Na1, Ty = l.Ty, Unit = l.Unit,
        Fa = l.Fa, Dc = l.Dc,
        Jkamount = l.Jkamount, Iprc = l.Iprc, Iprj = l.Iprj,
        Oprc = l.Oprc, Oprb = l.Oprb, Posi = l.Posi, Depotno = l.Depotno, Note = l.Note,
    });

    /// <summary>
    /// 保存暂存（2026-11-02 需求）：全部明细写 tbistosm，不入库、不更新库存；
    /// 首次保存自动取「散件入库单」号（页头回显），再次保存全删重插。
    /// </summary>
    [RelayCommand]
    public Task<bool> SaveDraftAsync(CancellationToken ct) => SaveDraftCoreAsync(ct);

    /// <summary>保存暂存核心（生效入库前的自动同步、关闭页面脏提示共用）。返回是否成功。</summary>
    public async Task<bool> SaveDraftCoreAsync(CancellationToken ct = default)
    {
        Message = "";
        if (Lines.Count == 0) { Message = "入库明细为空"; return false; }
        // 供应商必填：暂存单列表要显示供应商名称，生效路径同样必填（旧 X__.row212 L74-78）
        if (string.IsNullOrWhiteSpace(Gyname)) { Message = "请输入供应商！"; return false; }
        IsBusy = true;
        try
        {
            var result = await Db.InStock.SaveDraftAsync(new InStockDraftSaveCommand
            {
                Cno = Cno.Trim(),   // 空 = 自动取号；非空 = 已有暂存单全删重插
                Gyno = Gyno, Gyname = Gyname, Pno = Pno, Fkfs = Fkfs, Ywr = Ywr,
                Lines = ToLineInputs().ToList(),
            }, AppSession.DeptNo, AppSession.Operator, ct);
            Cno = result.Cno;
            // 回填各行暂存 id（删行时精确删 tbistosm）
            for (var i = 0; i < Lines.Count; i++)
                Lines[i].SmId = i < result.SmIds.Count ? result.SmIds[i] : 0;
            IsDirty = false;
            Message = $"已暂存（单号 {result.Cno}），未生效入库，重开页面可恢复继续";
            return true;
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "暂存失败：" + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>恢复暂存单（页面打开选择恢复时调用）：读 tbistosm 行 + 回填单头冗余字段。</summary>
    public async Task<bool> RestoreDraftAsync(string cno, CancellationToken ct = default)
    {
        try
        {
            var rows = await Db.InStock.GetDraftLinesAsync(cno.Trim(), AppSession.DeptNo, ct);
            if (rows.Count == 0) { Message = $"暂存单 {cno} 无明细行"; return false; }
            Lines.Clear();
            foreach (var r in rows)
            {
                var row = new InStockLineRow
                {
                    SmId = r.SmId, Cp = r.Cp, Nno = r.Nno, Na1 = r.Na1, Ty = r.Ty, Fa = r.Fa, Dc = r.Dc,
                    Unit = r.Unit, Jkamount = r.Jkamount, Iprj = r.Iprj, Iprc = r.Iprc, Oprc = r.Oprc,
                    Oprb = r.Oprb, Posi = r.Posi, Depotno = r.Depotno, Note = r.Note,
                };
                HookRow(row);
                Lines.Add(row);
            }
            var h = rows[0];
            Cno = cno.Trim(); Gyno = h.Gyno; Gyname = h.Gyname; Pno = h.Pno; Fkfs = h.Fkfs; Ywr = h.Ywr;
            RecalcTotal();
            IsDirty = false;
            Message = $"已恢复暂存单 {cno.Trim()}（{rows.Count} 行），可继续添加或点「生效入库」";
            return true;
        }
        catch (Exception ex)
        {
            Message = "恢复暂存单失败：" + ex.Message;
            return false;
        }
    }

    private void RecalcTotal() => TotalAmount = Lines.Sum(l => l.Jkamount * l.Iprj);

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        Message = "";
        if (Lines.Count == 0) { Message = "入库明细为空"; return; }
        // 供应商必填（旧 X__.row212 L74-78 生效拦截语义）
        if (string.IsNullOrWhiteSpace(Gyname)) { Message = "请输入供应商！"; return; }
        IsBusy = true;
        try
        {
            // 暂存单有未保存改动 → 先同步暂存（生效以本页明细为准；仓储侧会清同 cno 残留行）
            if (IsDirty && Cno.Trim().Length > 0 && !await SaveDraftCoreAsync(ct))
            {
                Message = "暂存同步失败，" + Message;
                return;
            }
            var command = new CreateInStockCommand
            {
                Cno = Cno.Trim(),   // 空 = 生效时自动取号（旧 P_AUTO 关闭语义）
                Gyno = Gyno,
                Gyname = Gyname,
                Pno = Pno,
                Fkfs = Fkfs,
                Ywr = Ywr,
                Lines = Lines.Select(l => new InStockLineInput
                {
                    Cp = l.Cp, Nno = l.Nno, Na1 = l.Na1, Ty = l.Ty, Unit = l.Unit,
                    Fa = l.Fa, Dc = l.Dc,
                    Jkamount = l.Jkamount, Iprc = l.Iprc, Iprj = l.Iprj,
                    Oprc = l.Oprc, Oprb = l.Oprb, Posi = l.Posi, Depotno = l.Depotno, Note = l.Note,
                }).ToList(),
            };
            command.Validate();
            var result = await Db.InStock.CreateAsync(command, AppSession.DeptNo, AppSession.Operator, allowReentry: false, ct);
            Cno = result.Cno;   // 回显生成的合同单号
            Message = $"入库成功：{result.Cno} 共 {result.LineCount} 行，总进价 {result.TotalIprcAmount:N2}";
            // 清空重开：生效时焦点在网格内会有未提交编辑会话，Clear+AddLine 会产生 ghost 空白行——
            // 这里不再预置空行（用户由「添加配件/表格导入」加行），重进页面时才由 LoadedAsync 预置
            Lines.Clear();
            TotalAmount = 0;
            Cno = "";
            Pno = "";
            IsDirty = false;
        }
        catch (Exception ex)
        {
            if (ex is AppException { Code: "CNO_REENTRY_CONFIRM" })
            {
                var cmd = new CreateInStockCommand
                {
                    Cno = Cno.Trim(), Gyno = Gyno, Gyname = Gyname, Pno = Pno, Fkfs = Fkfs, Ywr = Ywr,
                    Lines = Lines.Select(l => new InStockLineInput
                    {
                        Cp = l.Cp, Nno = l.Nno, Na1 = l.Na1, Ty = l.Ty, Unit = l.Unit,
                        Jkamount = l.Jkamount, Iprc = l.Iprc, Iprj = l.Iprj,
                        Oprc = l.Oprc, Oprb = l.Oprb, Posi = l.Posi, Depotno = l.Depotno, Note = l.Note,
                    }).ToList(),
                };
                try
                {
                    var result = await Db.InStock.CreateAsync(cmd, AppSession.DeptNo, AppSession.Operator, allowReentry: true, ct);
                    Cno = result.Cno;   // 回显生成的合同单号
                    Message = $"重入成功：{result.Cno} 共 {result.LineCount} 行";
                    Lines.Clear();
                    TotalAmount = 0;
                    Cno = "";
                    IsDirty = false;
                }
                catch (Exception ex2)
                {
                    Message = ex2 is AppException ae2 ? ae2.Message : "入库失败：" + ex2.Message;
                }
                return;
            }
            Message = ex is AppException ae ? ae.Message : "入库失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
