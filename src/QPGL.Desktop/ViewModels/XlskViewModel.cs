﻿using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using QPGL.Desktop.Printing;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.ViewModels;

/// <summary>收款/付款明细行 VM（fr_xlsk 游标 XLSK 列子集）。</summary>
public partial class XlskLineVm : ObservableObject
{
    public int Id { get; init; }
    public string Type { get; init; } = "";
    public string Paper { get; init; } = "";
    public DateTime Date { get; init; }
    public decimal Je { get; init; }
    public decimal Ysje { get; set; }
    public decimal Wsje { get; set; }
    public string Isord { get; init; } = "";
    public string Ch { get; init; } = "";
    public string Dp { get; init; } = "";
    public string Remark { get; init; } = "";

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private decimal _bcje;
}

/// <summary>
/// 收款/付款管理 VM（G.2/G.3，fr_xlsk 契约：契约摘录/出纳管理G组契约.md §1）。
/// 拉单=客户未结清单（CmdGU 规则），勾选/全选/按金额分配 → 确认（汇总行+流水+结清联动）；
/// 还原（权限 G21）按载入流水回退；下一张/上一张浏览。
/// 预收定金（skinfor）/提取未收款（fr_xlsk_tq）/收款导入（fr_skdr 分析→回填）已接入（第十七批）。
/// 已知偏差：skdr 为分析+网格回填（旧总部模式直接写库不适用单店部署）；财务凭证联动不实现（备案）。
/// </summary>
public partial class XlskViewModel : ObservableObject
{
    private readonly string _type;   // 收款 / 付款

    public ObservableCollection<XlskLineVm> Lines { get; } = [];
    public ObservableCollection<string> BankNames { get; } = [];    // 去向（tbsysset bankname）
    public ObservableCollection<string> FkfsList { get; } = [];     // 方式（tbsysset guno，同收款确认）

    [ObservableProperty] private string _gname = "";
    [ObservableProperty] private string _xm = "";
    [ObservableProperty] private DateTime _docDate = DateTime.Today;
    [ObservableProperty] private DateTime _lszDate = DateTime.Today;
    [ObservableProperty] private string _bankName = "";
    [ObservableProperty] private string _fkfs = "现金";
    [ObservableProperty] private string _pno = "";
    [ObservableProperty] private decimal _paySum;
    [ObservableProperty] private decimal _yue;          // TXTYUE 客户余额
    [ObservableProperty] private string _cno = "";      // 当前单号（载入已有单）
    [ObservableProperty] private bool _isEditMode;      // EMODE：已载入单据
    [ObservableProperty] private bool _includeRebate;   // CHK1：含回扣
    [ObservableProperty] private bool _lszDateEnabled = true;
    [ObservableProperty] private bool _finVouchEnabled;   // P_FINANCE && P_FININVOU：联动启用时去向列表切换为会计科目
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    public string Title { get; }

    public XlskViewModel(string type)
    {
        _type = type;
        Title = type + "管理";
    }

    /// <summary>流水日期可编辑性（Init L311-323）：配置「可修改收付款日期」valuel=0 → 锁定；G22/@ 放开。
    /// 去向列表（Init L293-303）：finvouch 联动启用 → c_skqx（finsub detacode=1 type C/B）；否则 tbsysset bankname。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var (luse, finInvou) = await Db.FinOp.GetFinSwitchAsync(ct);
        FinVouchEnabled = luse >= 1 && finInvou == 2;
        IEnumerable<string> sources = FinVouchEnabled
            ? await Db.FinOp.GetSkqxNamesAsync(ct)
            : await Db.Xlsk.GetBankNamesAsync(ct);
        BankNames.Clear();
        foreach (var b in sources) BankNames.Add(b);
        if (BankNames.Count > 0) BankName = BankNames[0];

        var methods = await Db.Receipts.GetPaymentMethodsAsync(ct);
        FkfsList.Clear();
        foreach (var m in methods) FkfsList.Add(m.Name);
        if (FkfsList.Count > 0 && !FkfsList.Contains(Fkfs)) Fkfs = FkfsList[0];

        var locked = await Db.Xlsk.IsLszDateLockedAsync(AppSession.DeptNo, ct);
        LszDateEnabled = !locked || AppSession.Permission.HasMenu("G22");
    }

    partial void OnIncludeRebateChanged(bool value) { }

    private void RecalcPaySum()
    {
        PaySum = Lines.Where(l => l.IsChecked).Sum(l => l.Bcje);
    }

    private void HookLine(XlskLineVm line)
    {
        line.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(XlskLineVm.IsChecked) or nameof(XlskLineVm.Bcje))
                RecalcPaySum();
        };
    }

    /// <summary>LJ 落库值（TXT2，myrefresh L241-249）。</summary>
    private decimal LjValue => _type == "收款" ? PaySum - Yue : PaySum + Yue;

    /// <summary>拉单（CmdGU，鏤.row41）：载入客户未结清单；余额=Σ(ys-yf) 全行累计。</summary>
    [RelayCommand]
    public async Task LoadCustomerAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Gname)) { Message = "请先输入客户名称"; return; }
        try
        {
            IsBusy = true;
            Message = "";
            var rows = await Db.Xlsk.GetOutstandingAsync(Gname, _type, IncludeRebate, ct);
            Yue = rows.Sum(r => r.Ys - r.Yf);
            Lines.Clear();
            foreach (var r in rows.Where(r => r.Included))
            {
                var vm = new XlskLineVm
                {
                    Id = r.Id, Type = r.Type, Paper = r.Paper, Date = r.Datime,
                    Je = r.Je, Ysje = r.Skmoney, Wsje = r.Wsje,
                    Isord = r.Isord, Ch = r.Ch, Dp = r.Dp, Remark = r.Remark,
                };
                HookLine(vm);
                Lines.Add(vm);
            }
            Cno = "";
            IsEditMode = false;
            RecalcPaySum();
            Message = $"共 {Lines.Count} 行未结，客户余额 {Yue:N2}";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>载入已有单（skno）：表头+流水行（默认全勾，BCJE=原始流水额）。</summary>
    [RelayCommand]
    public async Task LoadDocAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Cno)) { Message = "请输入单号"; return; }
        try
        {
            IsBusy = true;
            Message = "";
            var doc = await Db.Xlsk.LoadDocAsync(Cno, _type, ct);
            if (doc is null) { Message = "没有找到该收款单."; return; }
            Gname = doc.Gname;
            Xm = doc.Xm;
            DocDate = doc.Datime == default ? DateTime.Today : doc.Datime;
            LszDate = doc.LszDate;
            Fkfs = string.IsNullOrEmpty(doc.Fkfs) ? Fkfs : doc.Fkfs;
            Pno = doc.Pno;
            PaySum = doc.PaySum;
            Yue = doc.Yue;
            Lines.Clear();
            foreach (var l in doc.Lines)
            {
                var vm = new XlskLineVm
                {
                    Id = l.Id, Type = l.Type, Paper = l.Paper, Date = l.Date,
                    Je = l.Je, Ysje = l.Ysje, Wsje = l.Wsje, Bcje = l.Bcje, IsChecked = l.Checked,
                    Isord = l.Isord, Ch = l.Ch, Dp = l.Dp, Remark = l.Remark,
                };
                HookLine(vm);
                Lines.Add(vm);
            }
            IsEditMode = true;
            Message = $"已载入 {Cno}";
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>新增（add）：清屏取新号（号在保存时才实际发出，预览留空）。</summary>
    [RelayCommand]
    private void NewDoc()
    {
        Lines.Clear();
        Gname = ""; Xm = ""; Pno = ""; PaySum = 0; Yue = 0; Cno = "";
        DocDate = DateTime.Today;
        LszDate = DateTime.Today;
        IsEditMode = false;
        IsPresaleDeposit = false;
        RecalcPaySum();
    }

    [RelayCommand]
    private void CheckAll()
    {
        foreach (var l in Lines) { l.IsChecked = true; l.Bcje = l.Wsje; }
        RecalcPaySum();
    }

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var l in Lines) { l.IsChecked = false; l.Bcje = 0; }
        RecalcPaySum();
    }

    /// <summary>按收款金额智能分配（__.row86）：顺序填 WSJE 直到金额耗尽。</summary>
    [RelayCommand]
    private void Allocate()
    {
        if (Lines.Count == 0) { Message = "没有要收款的单据！"; return; }
        if (PaySum <= 0) { Message = "请输入收款金额！"; return; }
        var reset = MessageBox.Show("列表中存在已勾选的记录是否重置？", Title, MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!reset && Lines.Any(l => l.IsChecked)) return;
        foreach (var l in Lines) { l.IsChecked = false; l.Bcje = 0; }
        decimal remain = PaySum, filled = 0;
        foreach (var l in Lines)
        {
            if (remain <= 0) break;
            var amt = remain - l.Wsje >= 0 ? l.Wsje : remain;
            l.Bcje = amt;
            l.IsChecked = amt != 0;
            filled += amt;
            remain -= l.Wsje;
        }
        if (PaySum > filled)
        {
            if (MessageBox.Show($"您输入的收款金额 {PaySum:N2}，大于实际勾选总金额 {filled:N2}，是否保留输入金额？",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
                PaySum = filled;
        }
        else
        {
            PaySum = filled;
        }
        Message = $"实际勾选总金额 {filled:N2}";
    }

    /// <summary>确认保存（A_.row75）。</summary>
    [RelayCommand]
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var checkedLines = Lines.Where(l => l.IsChecked).ToList();
        if (IsEditMode) { Message = "当前为已载入单据，请先还原或新增。"; return; }
        if (string.IsNullOrWhiteSpace(Gname)) { Message = "请先选择客户！"; return; }
        var sumChecked = checkedLines.Sum(l => l.Bcje);
        if (PaySum != sumChecked)
        {
            if (MessageBox.Show($"您输入的收款金额为：{PaySum:N2}，实际勾选总金额为：{sumChecked:N2}，是否继续？",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        if (MessageBox.Show($"确认要{_type}吗？", "提示", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // finvouch 联动（A_.row75 L26-30）：启用时去向必选
        if (FinVouchEnabled && string.IsNullOrWhiteSpace(BankName))
        {
            Message = $"请确定{_type}去向.";
            return;
        }

        try
        {
            IsBusy = true;
            Message = "";
            var result = await Db.Xlsk.SaveAsync(new XlskSaveCommand
            {
                Type = _type,
                Date = DocDate,
                LszDate = LszDate,
                Fkfs = Fkfs,
                Pno = Pno,
                PaySum = PaySum,
                Gname = Gname,
                Xm = Xm,
                BankName = BankName,
                LjValue = LjValue,
                FinVouchEnabled = FinVouchEnabled,
                SkqxSubname = BankName,
                IsPresaleDeposit = IsPresaleDeposit,   // row73 IS_YSK 语义（第十五批配置分支）
                Lines = checkedLines.Select(l => new XlskSaveLine
                {
                    Id = l.Id, Paper = l.Paper, LineType = l.Type, Bcje = l.Bcje, Wsje = l.Wsje,
                }).ToList(),
            }, AppSession.DeptNo, AppSession.Operator, ct);
            Cno = result.DocNo;
            IsEditMode = true;
            Message = FinVouchEnabled
                ? $"已确认并加入应收应付/收支流水帐/业务凭证。单号 {result.DocNo}，余额 {result.Yue:N2}"
                : $"已确认并加入应收应付和收支流水帐。单号 {result.DocNo}，余额 {result.Yue:N2}";
            // 保存后回到新增态（旧码 CONTAINER1.CMD增加.CLICK()）
            NewDoc();
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>还原（c_.row74，权限 G21/'@'；月结保护）。</summary>
    [RelayCommand]
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        if (!AppSession.Permission.HasMenu("G21")) { Message = "权限不够,不能还原！"; return; }
        if (!IsEditMode || string.IsNullOrWhiteSpace(Cno)) { Message = "没有单据可还原！"; return; }
        try
        {
            IsBusy = true;
            Message = "";
            if (await Db.Xlsk.HasMonthlyCloseAsync(ct) && await Db.Xlsk.IsMonthlyLockedAsync(DocDate, ct))
            {
                Message = "该单已做月结，不允许还原！";
                return;
            }
            if (MessageBox.Show("真的要还原吗？", Title, MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var yue = await Db.Xlsk.RestoreAsync(Cno, _type, Gname, Lines.Select(l => new XlskRestoreLine
            {
                Id = l.Id, Paper = l.Paper, LineType = l.Type, Bcje = l.Bcje, Wsje = l.Wsje, Checked = l.IsChecked,
            }).ToList(), AppSession.DeptNo, AppSession.Operator, FinVouchEnabled, ct);
            Message = $"{Cno} 单号已经还原！客户余额 {yue:N2}";
            NewDoc();
        }
        catch (AppException ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>下一张/上一张（手.row77 / 掠.row76）。</summary>
    [RelayCommand]
    public async Task GotoDocAsync(bool forward, CancellationToken ct = default)
    {
        if (IsEditMode && Lines.Any(l => l.IsChecked))
        {
            if (MessageBox.Show($"是否确认该{_type}单？", "提示", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) await SaveAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(Cno)) { Message = "请先载入单据"; return; }
        var next = await Db.Xlsk.GetNextDocNoAsync(Cno, _type, forward, ct);
        if (string.IsNullOrEmpty(next)) { Message = forward ? "已是最后一张单。" : "已是最前一张单。"; return; }
        Cno = next;
        await LoadDocAsync(ct);
    }

    [RelayCommand]
    private Task NextAsync(CancellationToken ct) => GotoDocAsync(true, ct);

    [RelayCommand]
    private Task PrioAsync(CancellationToken ct) => GotoDocAsync(false, ct);

    // ── 预收定金（fr_xlsk __.row73 + skinfor）──
    /// <summary>预收定金路径标志：IS_YSK=.T. 且 YSK_JE=勾选总额时保存受「关闭预收款已结帐」配置控制。</summary>
    public bool IsPresaleDeposit { get; private set; }

    /// <summary>预收定金提取（row73）：弹 skinfor 输入类型/单号/金额 → ZAP 后单行（JE=BCJE=金额，勾选）。</summary>
    [RelayCommand]
    private void PresaleDeposit()
    {
        if (IsEditMode) { Message = "当前为已载入单据，请先还原或新增。"; return; }
        var win = new Views.SkinforWindow(_type) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || string.IsNullOrEmpty(win.Result)) return;
        // 解析旧协议 |type|paper|ys|（__.row73 L8-10）
        var seg = win.Result.Split('|');
        var bizType = seg[1];
        var paper = seg[2];
        if (!decimal.TryParse(seg[3], out var lcje)) { Message = "金额解析失败"; return; }
        Lines.Clear();
        var vm = new XlskLineVm
        {
            Id = 0, Type = bizType, Paper = paper, Date = DateTime.Now,
            Je = lcje, Ysje = 0, Wsje = 0, Bcje = lcje, IsChecked = true,
        };
        HookLine(vm);
        Lines.Add(vm);
        IsPresaleDeposit = lcje > 0;   // row73：LCJE>0 → IS_YSK=.T.
        RecalcPaySum();
        Message = $"预收定金 {lcje:N2} 已录入，确认时受「关闭预收款已结帐」配置控制";
    }

    // ── 提取未收款（fr_xlsk 5_.row71 + fr_xlsk_tq）──
    /// <summary>提取未收款：选择窗（返回单条 tbysyf.id）→ 单行 APPEND（JE=YS/YF，YSJE=skmoney，WSJE=差额，勾选）。</summary>
    [RelayCommand]
    private async Task ExtractUnsettledAsync(CancellationToken ct)
    {
        if (IsEditMode) { Message = "当前为已载入单据，请先还原或新增。"; return; }
        var win = new Views.UnsettledPickWindow(_type) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Selected is null) return;
        var r = win.Selected;
        // 5_.row71 L15-22：JE=收款?YS:YF（这里 amount 已= sign-skmoney，但 JE 应取发生额全额 sign）
        // 旧码 JE=IIF(M_TYPE='收款', m.YS, m.YF)——原始 tbysyf 行发生额；amount 为未收差额。
        // row71 SCATTER 后 REPLACE JE WITH M_JE（全额）、YSJE=SKMONEY、WSJE=JE-YSJE。
        // amount(=sign-skmoney) 即 WSJE；发生额全额 = amount + |skmoney| 方向不确定，直接重查不可得 → 取 amount+skmoney 符号处理：
        var je = r.Amount + r.Skmoney;   // sign 全额 = (sign - skmoney) + skmoney
        var vm = new XlskLineVm
        {
            Id = r.Id, Type = r.Type, Paper = r.Cno, Date = r.Datime,
            Je = je, Ysje = r.Skmoney, Wsje = r.Amount,
            IsChecked = true, Bcje = r.Amount,   // row71 未赋 BCJE/BZ；分配语义=默认全额勾选
            Ch = r.Ch, Dp = r.Tgh, Remark = r.Xm,
        };
        HookLine(vm);
        Lines.Add(vm);
        Gname = string.IsNullOrWhiteSpace(Gname) ? r.Gname : Gname;
        IsPresaleDeposit = false;
        RecalcPaySum();
        // TXTYUE 客户累计（row71 L23-25）
        Yue = await Db.Xlsk.GetCustomerYueAsync(Gname, ct);
        Message = $"已提取 {r.Cno}（{r.Type}）";
    }

    // ── 入库单随付打印（fr_xlsk 墌.row67）──
    /// <summary>
    /// 随付打印：收集勾选 type='入库' 行单号 → tbistoed 明细（沙河丰田部署附加人员分派）→
    /// re_istofk 列表打印预览（PG_PRINT 当前游标语义，打印前不落库）。
    /// </summary>
    [RelayCommand]
    private async Task IstofkPrintAsync(CancellationToken ct)
    {
        var cnos = Lines.Where(l => l.IsChecked && l.Type.Trim() == "入库")
            .Select(l => l.Paper.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        if (cnos.Count == 0) { Message = "请选择入库单！"; return; }
        try
        {
            var rows = await Db.Xlsk.GetIstofkRowsAsync(cnos, Client, ct);
            var company = await Db.UniversalQuery.GetCompanyNameAsync(ct);
            var specs = new List<QueryColumnSpec>
            {
                new("Cno", "单号", 'C', 110),
                new("Indate", "日期", 'D', 80),
                new("Nno", "工号", 'C', 70),
                new("Na1", "名称", 'C', 150),
                new("Ty", "规格", 'C', 90),
                new("Jkamount", "数量", 'N', 70),
                new("Iprc", "进价", 'N', 80),
                new("Gu", "仓库", 'C', 70),
                new("Gyname", "供货单位", 'C', 120),
                new("Zuc", "分派人员", 'C', 70),
            };
            var printRows = rows.Select(r => new QueryRow(new Dictionary<string, object?>
            {
                ["Cno"] = r.Cno, ["Indate"] = r.Indate, ["Nno"] = r.Nno, ["Na1"] = r.Na1,
                ["Ty"] = r.Ty, ["Jkamount"] = r.Jkamount, ["Iprc"] = r.Iprc,
                ["Gu"] = r.Gu, ["Gyname"] = r.Gyname, ["Zuc"] = r.Zuc,
            })).ToList();
            var doc = ListPrintDocument.Build(new ListPrintRequest
            {
                Caption = "入库单随付",
                FindText = "单号:" + string.Join(",", cnos),
                Company = company,
                Operator = AppSession.Operator,
                Columns = specs,
                Rows = printRows,
            });
            var preview = new PrintPreviewWindow(doc, "预览 - 入库单随付");
            preview.ShowDialog();
        }
        catch (AppException ex) { Message = ex.Message; }
    }

    /// <summary>旧 P_CLIENT（部署变体识别，沙河丰田人员分派）。</summary>
    private static string Client => new ConfigurationBuilder()
        .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build()["ClientName"] ?? "";

    // ── 收款导入（fr_skdr 分析 → 网格回填）──
    /// <summary>
    /// 收款导入（仅收款；s_.row82）：输入 单号/车牌/导入金额 多行 → AnalyzeSkdrAsync 校验 →
    /// 通过行回填网格（匹配 paper=cno 且 drmoney≤剩余 且车牌匹配 → BCJE=导入额, BZ='√'）。
    /// 旧 M_GR=.T. 总部直写库分支不适单店部署（备案）。
    /// </summary>
    [RelayCommand]
    private async Task SkdrImportAsync(CancellationToken ct)
    {
        if (_type != "收款") { Message = "收款导入仅限收款管理（s_.row82 M_TITLE='SK'）"; return; }
        if (IsEditMode) { Message = "当前为已载入单据，请先还原或新增。"; return; }
        if (string.IsNullOrWhiteSpace(Gname)) { Message = "请先输入客户名称"; return; }
        var win = new Views.SkdrImportWindow() { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.InputRows.Count == 0) return;
        var analyzed = await Db.Xlsk.AnalyzeSkdrAsync(win.InputRows, Gname, ct);
        var okRows = analyzed.Where(a => a.Ok).ToList();
        var failed = analyzed.Where(a => !a.Ok).ToList();
        // __.row6 L110-124：逐网格行匹配回填
        var filled = 0;
        foreach (var line in Lines)
        {
            if (line.IsChecked) continue;
            var remain = line.Je - line.Ysje;
            var hit = okRows.FirstOrDefault(a =>
                a.Cno == line.Paper.Trim() && a.DrMoney <= remain &&
                (a.Ch.Length == 0 || a.Ch == line.Ch));
            if (hit is null) continue;
            line.Bcje = hit.DrMoney;
            line.IsChecked = true;
            filled++;
        }
        RecalcPaySum();
        Message = failed.Count > 0
            ? $"导入完成：回填 {filled} 行；{failed.Count} 行未通过（{string.Join("；", failed.Take(3).Select(f => $"{f.Cno}:{f.Status}"))}）"
            : $"导入完成：回填 {filled} 行";
    }
}
