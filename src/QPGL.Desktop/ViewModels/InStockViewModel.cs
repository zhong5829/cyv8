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
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InStockLineRow.Iprj) or nameof(InStockLineRow.Jkamount))
                RecalcTotal();
        };
        Lines.Add(row);
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
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InStockLineRow.Iprj) or nameof(InStockLineRow.Jkamount))
                RecalcTotal();
        };
        Lines.Add(row);
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
        RecalcTotal();
    }

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine is not null) Lines.Remove(SelectedLine);
        RecalcTotal();
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
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InStockLineRow.Iprj) or nameof(InStockLineRow.Jkamount))
                RecalcTotal();
        };
        Lines.Add(row);
        RecalcTotal();   // 行价格在初始化器中已赋值，追加后主动重算（修复导入后总金额为 0）
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
