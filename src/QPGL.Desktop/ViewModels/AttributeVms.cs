using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;
using QPGL.Infrastructure.Parts;

namespace QPGL.Desktop.ViewModels;

/// <summary>A 组属性定义族 VM（第十二批）。契约：开发文档/契约摘录/A组属性定义族契约.md。</summary>
public partial class PrnotyViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<PrnotyRow> rows = new();

    [ObservableProperty]
    private PrnotyRow? selected;

    [ObservableProperty]
    private string findWhere = "1=1";

    [ObservableProperty]
    private string message = "";

    /// <summary>当前厂牌（旧 P_CP 会话，tbxzb 对 tbprnoty 追加 cp=?p_cp）。</summary>
    [ObservableProperty]
    private string cp = AppSession.Brand;

    private string Dpmusem => "A";

    // ==== 三条件过滤（编码/名称/车型；内存过滤，实时+500ms 防抖，名称框支持拼音码）====
    [ObservableProperty] private string _filterNno = "";
    [ObservableProperty] private string _filterNa1 = "";
    [ObservableProperty] private string _filterTy = "";
    private List<PrnotyRow> _allRows = [];                                  // 未过滤全集
    private Dictionary<string, string> _pyMap = new(StringComparer.Ordinal); // 零件名称→拼音码（tbname0）
    private readonly System.Windows.Threading.DispatcherTimer _filterDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    public PrnotyViewModel()
    {
        _filterDebounce.Tick += (_, _) => ApplyFilter();   // 必须订阅 Tick，否则输入后无响应
    }

    /// <summary>过滤输入变化时调用：重置防抖计时（View 的 TextChanged 转发）。</summary>
    public void DebounceFilter()
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    /// <summary>三条件内存过滤（语义同销售开单页）：编码=Nno 包含；名称=Na1/Nno/Ty 包含或拼音码前缀；车型=Ty/Na1 包含。</summary>
    public void ApplyFilter()
    {
        _filterDebounce.Stop();
        var nnoKw = FilterNno.Trim();
        var na1Kw = FilterNa1.Trim();
        var tyKw = FilterTy.Trim();
        IEnumerable<PrnotyRow> q = _allRows;
        if (nnoKw.Length > 0)
            q = q.Where(r => r.Nno.Contains(nnoKw, StringComparison.OrdinalIgnoreCase));
        if (na1Kw.Length > 0)
            q = q.Where(r =>
                r.Na1.Contains(na1Kw, StringComparison.OrdinalIgnoreCase) ||
                r.Nno.Contains(na1Kw, StringComparison.OrdinalIgnoreCase) ||
                r.Ty.Contains(na1Kw, StringComparison.OrdinalIgnoreCase) ||
                (_pyMap.TryGetValue(r.Na1, out var code) && code.StartsWith(na1Kw, StringComparison.OrdinalIgnoreCase)));
        if (tyKw.Length > 0)
            q = q.Where(r =>
                r.Ty.Contains(tyKw, StringComparison.OrdinalIgnoreCase) ||
                r.Na1.Contains(tyKw, StringComparison.OrdinalIgnoreCase));
        Rows = new ObservableCollection<PrnotyRow>(q.ToList());
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        try
        {
            var rows = await Db.Prnoty.SearchAsync(FindWhere, Cp, Dpmusem);
            _allRows = rows.ToList();
            // 拼音码映射整表一次取回（tbname0 数千行；名称框拼音过滤，销售页同源）
            try { _pyMap = new Dictionary<string, string>(await Db.SaleReturns.GetPinyinMapAsync(), StringComparer.Ordinal); }
            catch { _pyMap = []; }
            ApplyFilter();   // 无过滤条件时=全部；有条件时按当前关键字过滤新数据
            Message = _allRows.Count == 0 ? "没有符合条件的记录！" : $"共 {_allRows.Count} 行";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Selected is null) { Message = "请先查询并选中行（新增用新增按钮）"; return; }
        await SaveRowAsync(Selected);
    }

    /// <summary>
    /// 保存行（工具栏保存/属性编辑窗共用）。
    /// oldNno：编辑弹窗传入改码前旧编码（网格编辑路径编码已被绑定覆盖，无法取旧值）。
    /// </summary>
    public async Task<bool> SaveRowAsync(PrnotyRow r, string? oldNno = null)
    {
        try
        {
            var result = await Db.Prnoty.SaveAsync(new PrnotySaveCommand
            {
                IsAdd = r.Id == 0,
                Id = r.Id,
                Cp = r.Cp,
                Nno = r.Nno,
                OldNno = oldNno ?? (r.Id == 0 ? "" : r.Nno),
                Na1 = r.Na1, Ty = r.Ty, Fa = r.Fa, Dc = r.Dc, Unit = r.Unit, Iprj = r.Iprj,
                Code_ts = r.Code_ts, Eng = r.Eng, Nnp = r.Nnp, Nnq = r.Nnq, Uqty = r.Uqty,
                Xno = r.Xno, Tno = r.Tno, Amjj = r.Amjj, Bmjj = r.Bmjj, Nvol = decimal.TryParse(r.Nvol, out var nv) ? nv : 0,
                Suttle = r.Suttle, Note = r.Note, Note1 = r.Note1, Oprc_cp = r.Oprc_cp, Oprc_min = r.Oprc_min,
            }, Cp, Dpmusem);
            r.Cxnno = result.Cxnno; r.Xno = result.Xno; r.Tno = result.Tno;
            Message = $"保存成功 id={result.Id}（触发器回填 cxnno={result.Cxnno} xno={result.Xno} tno={result.Tno}）";
            return true;
        }
        catch (Exception ex) { Message = "保存失败：" + ex.Message; return false; }
    }

    /// <summary>新增：弹零件属性编辑窗（复刻旧 frmAdd），确定后保存并加入列表。</summary>
    [RelayCommand]
    private async Task NewRowAsync()
    {
        var dlg = new PartAttributeEditWindow(null, Cp) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var r = dlg.Row;
        if (await SaveRowAsync(r))
        {
            _allRows.Insert(0, r);
            ApplyFilter();
            Selected = r;
        }
    }

    /// <summary>修改：双击行/修改按钮 → 编辑窗（副本编辑，取消不动原行），确定后写回保存。</summary>
    [RelayCommand]
    public async Task EditRowAsync()
    {
        if (Selected is not { Id: > 0 } row) { Message = "请选中已保存的行"; return; }
        var oldNno = row.Nno;   // 弹窗只改副本，原行在确定前不变
        var dlg = new PartAttributeEditWindow(row, Cp) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        CopyInto(row, dlg.Row);
        await SaveRowAsync(row, oldNno);
    }

    public static void CopyInto(PrnotyRow dst, PrnotyRow src)
    {
        dst.Nno = src.Nno; dst.Na1 = src.Na1; dst.Ty = src.Ty; dst.Fa = src.Fa; dst.Dc = src.Dc;
        dst.Unit = src.Unit; dst.Note = src.Note; dst.Iprj = src.Iprj; dst.Nnp = src.Nnp;
        dst.Nnq = src.Nnq; dst.Uqty = src.Uqty; dst.Code_ts = src.Code_ts; dst.Eng = src.Eng;
        dst.Nvol = src.Nvol; dst.Suttle = src.Suttle; dst.Note1 = src.Note1;
        dst.Oprc_cp = src.Oprc_cp; dst.Oprc_min = src.Oprc_min;
        dst.Xno = src.Xno; dst.Tno = src.Tno;
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { Id: > 0 }) { Message = "请选中已保存的行"; return; }
        if (MessageBox.Show($"确认删除属性 {Selected.Nno} 吗？（无引用拦截，物理删除）", "删除",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await Db.Prnoty.DeleteAsync(Selected.Id);
        _allRows.Remove(Selected);
        Rows.Remove(Selected);
        Message = "已删除";
    }

    /// <summary>通用码/套件码（按厂牌 max+1 + EditAll）。</summary>
    [RelayCommand]
    private async Task AssignCodeAsync(string kind)
    {
        if (Selected is not { Id: > 0 }) { Message = "请选中已保存的行"; return; }
        var isTno = kind == "tno";
        var v = await Db.Prnoty.AssignCodeAsync(Selected.Id, isTno, Selected.Cp, Dpmusem);
        if (isTno) Selected.Tno = (int)v; else Selected.Xno = (int)v;
        Message = $"{(isTno ? "套件码" : "通用码")}={v} 已写库并联动 tbisto/tborder";
    }

    [RelayCommand]
    private async Task ClearCodeAsync(string kind)
    {
        if (Selected is null || string.IsNullOrWhiteSpace(Selected.Nno)) return;
        await Db.Prnoty.ClearCodeAsync(Selected.Nno, kind == "tno", Selected.Cp, Dpmusem);
        if (kind == "tno") Selected.Tno = 0; else Selected.Xno = 0;
        Message = "解除码完成（跨厂牌 update WHERE nno）";
    }

    [RelayCommand]
    private async Task TransferToStockAsync()
    {
        var ids = Rows.Where(r => r.Id > 0).Select(r => r.Id).ToList();
        var n = await Db.Prnoty.TransferToStockAsync(ids, AppSession.DeptNo, Cp, Dpmusem);
        Message = $"属性传递到库存完成：{n} 行";
    }

    [RelayCommand]
    private async Task BatchCodeTsAsync(string kind)
    {
        var k = int.Parse(kind);
        var v1 = Microsoft.VisualBasic.Interaction.InputBox(k switch { 1 => "按名称批改：输入零件名称", 2 => "按旧税号批改：输入旧税则号", _ => "编码区间起" }, "税号批改");
        if (string.IsNullOrWhiteSpace(v1)) return;
        var v2 = k == 3 ? PromptWindow.Ask("编码区间止", "") : "";
        var nc = PromptWindow.Ask("新税则号（须存在于 tbcomplex）", "");
        if (string.IsNullOrWhiteSpace(nc)) return;
        var n = await Db.Prnoty.BatchCodeTsAsync(k, v1.Trim(), v2?.Trim() ?? "", nc.Trim());
        Message = $"税号批改完成：{n} 行";
    }

    [RelayCommand]
    private async Task CalcOprcMinAsync()
    {
        if (Selected is not { Id: > 0 }) { Message = "请选中已保存的行"; return; }
        var f = decimal.TryParse(PromptWindow.Ask("系数（如 1.1）", "1"), out var fv) ? fv : 1m;
        var v = await Db.Prnoty.CalcOprcMinAsync(Selected.Id, Selected.Nno, Selected.Cp, Dpmusem, 1, 3, f, 0);
        Selected.Oprc_min = v;
        Message = $"oprc_min = max(oprc×{f}) = {v}";
    }
}

public partial class PrnameViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<Name0Row> rows = new();

    [ObservableProperty]
    private Name0Row? selected;

    [ObservableProperty]
    private string findWhere = "1=1";

    [ObservableProperty]
    private string message = "";

    [RelayCommand]
    public async Task SearchAsync()
    {
        var rows = await Db.NameCode.SearchAsync(FindWhere);
        Rows = new ObservableCollection<Name0Row>(rows);
        Message = rows.Count == 0 ? "没有符合条件的记录！" : $"共 {rows.Count} 行";
    }

    [RelayCommand]
    private void NewRow() { Rows.Insert(0, new Name0Row()); Selected = Rows[0]; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(Selected.Name)) { Message = "请输入零件名！"; return; }
        var r = Selected;
        var (id, code, code1) = await Db.NameCode.SaveAsync(r.Id == 0, r.Id, r.Name, r.Name1, null, null);
        r.Id = id; r.Code = code; r.Code1 = code1;
        Message = $"已保存 id={id}（code={code} code1={code1}）";
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        await Db.NameCode.DeleteAsync(Selected.Name);
        Rows.Remove(Selected);
        Message = "已删除";
    }

    [RelayCommand]
    private async Task ExtractAsync()
    {
        if (MessageBox.Show("从属性库中提取零件名，可能需要较长时间！确实要提取吗？", "提取",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var n = await Db.NameCode.ExtractAsync();
        Message = $"提取完毕（tbname0 新增 {n} 行 + getpywb 客户/供应商/维修代码）";
    }
}

public partial class SxcdViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<AttrFieldRow> fields = new();

    [ObservableProperty]
    private ObservableCollection<AttrTargetRow> targets = new();

    [ObservableProperty]
    private string message = "";

    public partial class AttrTargetRow : ObservableObject
    {
        public string Tbname { get; init; } = "";
        public string Explain { get; init; } = "";
        [ObservableProperty]
        private bool isCd;
    }

    public SxcdViewModel() { }

    public async Task LoadAsync()
    {
        if (Fields.Count > 0) return;
        var fs = await Db.AttrTransfer.GetFieldsAsync(AppSession.DeptNo);
        Fields = new ObservableCollection<AttrFieldRow>(fs);
        Targets = new ObservableCollection<AttrTargetRow>(new[]
        {
            new AttrTargetRow { Tbname = "isto", Explain = "库存表", IsCd = true },
            new AttrTargetRow { Tbname = "istosm", Explain = "散件入库表" },
            new AttrTargetRow { Tbname = "isorsm", Explain = "装箱单入库表" },
            new AttrTargetRow { Tbname = "order", Explain = "订货库" },
            new AttrTargetRow { Tbname = "sada", Explain = "报价库" },
        });
    }

    [RelayCommand]
    private void SelectAll() { foreach (var f in Fields) f.Is_cd = true; }

    [RelayCommand]
    private void SelectNone() { foreach (var f in Fields) f.Is_cd = false; }

    [RelayCommand]
    private async Task TransferAsync()
    {
        var fs = Fields.Where(f => f.Is_cd).Select(f => f.Field_name).ToList();
        var ts = Targets.Where(t => t.IsCd).Select(t => t.Tbname).ToList();
        if (fs.Count == 0 || ts.Count == 0) { Message = "请勾选字段与目标表！"; return; }
        var r = await Db.AttrTransfer.TransferAsync(fs, ts, AppSession.DeptNo);
        Message = $"属性传递已完毕（{string.Join("；", r.Updated.Where(u => u.Rows > 0).Select(u => $"{u.Table}.{u.Field}:{u.Rows}行"))}）";
    }
}

public partial class TjglViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<KitHeadRow> heads = new();

    [ObservableProperty]
    private ObservableCollection<KitLineRow> lines = new();

    [ObservableProperty]
    private KitHeadRow? selectedHead;

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private string cp = AppSession.Brand;   // 旧 a.cp=?p_cp（A组属性定义族契约）

    public async Task LoadAsync()
    {
        Heads = new ObservableCollection<KitHeadRow>(await Db.Kit.GetHeadsAsync("1=1", Cp, AppSession.DeptNo));
    }

    [RelayCommand]
    public async Task LoadLinesAsync()
    {
        if (SelectedHead is null) return;
        Lines = new ObservableCollection<KitLineRow>(await Db.Kit.GetLinesAsync(SelectedHead.Tj, Cp));
    }

    [RelayCommand]
    private async Task SaveHeadAsync()
    {
        if (SelectedHead is null) { Message = "请选中/新增套件头行"; return; }
        await Db.Kit.SaveHeadAsync(new KitHeadCommand
        {
            IsAdd = SelectedHead.Kcid == 0 && !Heads.Any(h => h != SelectedHead && h.Tj == SelectedHead.Tj),
            Tj = SelectedHead.Tj, Cp = Cp, Na1 = SelectedHead.Na1, Ty = SelectedHead.Ty,
            Fa = SelectedHead.Fa, Dc = SelectedHead.Dc, Unit = SelectedHead.Unit, Note = SelectedHead.Note,
        }, AppSession.DeptNo);
        Message = $"套件头 {SelectedHead.Tj} 已保存（同步 tbprnoty/tbname0）";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SaveLineAsync()
    {
        if (SelectedHead is null) return;
        var l = Lines.LastOrDefault();
        if (l is null || string.IsNullOrWhiteSpace(l.Nno)) { Message = "请输入组件行（编码/数量/单价）"; return; }
        await Db.Kit.SaveLineAsync(new KitLineCommand
        {
            IsAdd = l.Id == 0, Id = l.Id, Tj = SelectedHead.Tj, Nno = l.Nno, Cp = Cp,
            Na1 = l.Na1, Ty = l.Ty, Fa = l.Fa, Dc = l.Dc, Unit = l.Unit, Num = l.Num, Iprj = l.Iprj, Note = l.Note,
        }, Cp);
        Message = $"组件已保存，套件价已重算（Σ iprj×num）";
        await LoadLinesAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteLineAsync()
    {
        if (SelectedHead is null || Lines.LastOrDefault() is not { } l) return;
        await Db.Kit.DeleteKitLineAsync(SelectedHead.Tj, l.Nno, Cp, "A");
        Lines.Remove(l);
        Message = "组件已删除";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AssembleAsync()
    {
        if (SelectedHead is null) return;
        var l = Lines.LastOrDefault();
        if (l is null || string.IsNullOrWhiteSpace(l.Nno)) { Message = "请先选中组件行（组套按最后行编码）"; return; }
        var qty = decimal.TryParse(PromptWindow.Ask("组套数量", "1"), out var q) ? q : 0;
        await Db.Kit.AssembleAsync(SelectedHead.Tj, l.Nno, qty, Cp, null, AppSession.DeptNo, "A");
        Message = $"组套完成：{l.Nno} ×{qty} → {SelectedHead.Tj}（FIFO 扣减+加权成本）";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DisassembleAsync()
    {
        if (SelectedHead is null) return;
        var qty = decimal.TryParse(PromptWindow.Ask("拆套数量", "1"), out var q) ? q : 0;
        await Db.Kit.DisassembleAsync(SelectedHead.Tj, qty, Cp, null, AppSession.DeptNo, "A");
        Message = $"拆套完成：{SelectedHead.Tj} ×{qty}（组件回库不带成本字段，契约不对称照抄）";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteKitAsync()
    {
        if (SelectedHead is null) return;
        await Db.Kit.DeleteKitAsync(SelectedHead.Tj, Cp, "A");
        Heads.Remove(SelectedHead);
        Message = "套件已删除（连删 tbisto）";
        await LoadAsync();
    }
}

public partial class ComplexViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<ComplexRow> rows = new();

    [ObservableProperty]
    private string findText = "";

    [ObservableProperty]
    private string message = "";

    public async Task LoadAsync()
    {
        var rows = await Db.Complex.GetAllAsync();
        Rows = new ObservableCollection<ComplexRow>(rows);
    }

    [RelayCommand]
    private void Find()
    {
        var t = FindText.Trim();
        IEnumerable<ComplexRow> q = Rows;
        if (t.Length == 12 && t.All(char.IsDigit))
            q = q.Where(r => r.Code_ts.StartsWith(t));
        else
            q = q.Where(r => r.G_name.Contains(t, StringComparison.OrdinalIgnoreCase));
        Filtered = new ObservableCollection<ComplexRow>(q.Take(500));
        Message = $"匹配 {Filtered.Count} 行";
    }

    [ObservableProperty]
    private ObservableCollection<ComplexRow> filtered = new();

    [RelayCommand]
    private async Task SaveLowRateAsync(ComplexRow row)
    {
        if (row is null || row.Low_rate == 0) { Message = "税率不能为 0"; return; }
        await Db.Complex.UpdateLowRateAsync(row.Code_ts, row.Low_rate);
        Message = $"税则号 {row.Code_ts} low_rate={row.Low_rate} 已更新";
    }
}

public partial class DcCoefViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<DcRow> rows = new();

    [ObservableProperty]
    private string message = "";

    public partial class DcRow : ObservableObject
    {
        public string Dc { get; set; } = "";
        [ObservableProperty]
        private decimal coeffict;
        public bool IsNew { get; set; }
    }

    public async Task LoadAsync()
    {
        var rows = await Db.DcCoef.GetAllAsync();
        Rows = new ObservableCollection<DcRow>(rows.Select(r => new DcRow { Dc = r.Dc, Coeffict = r.Coeffict }));
    }

    [RelayCommand]
    private void NewRow() { Rows.Add(new DcRow { IsNew = true }); }

    [RelayCommand]
    private async Task SaveAsync(DcRow row)
    {
        if (row is null) return;
        await Db.DcCoef.SaveAsync(row.IsNew, row.Dc, row.Coeffict);
        row.IsNew = false;
        Message = $"分类码 {row.Dc}={row.Coeffict} 已保存";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(DcRow row)
    {
        if (row is null) return;
        if (MessageBox.Show($"确认删除分类码 {row.Dc} 吗？", "删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await Db.DcCoef.DeleteAsync(row.Dc);
        await LoadAsync();
        Message = "已删除";
    }
}

public partial class SysDefineViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<CpRow> cpRows = new();

    [ObservableProperty]
    private ObservableCollection<SysSetRow> dictRows = new();

    [ObservableProperty]
    private ObservableCollection<ParaRow> paraRows = new();

    [ObservableProperty]
    private string companyName = "";

    [ObservableProperty]
    private string companyTel = "";

    [ObservableProperty]
    private string companyAddr = "";

    [ObservableProperty]
    private string message = "";

    private string Dpm => AppSession.DeptNo;

    public async Task LoadAsync()
    {
        var (name, items) = await Db.SysDefine.GetCompanyAsync(Dpm);
        CompanyName = name;
        CompanyTel = items.FirstOrDefault(i => i.Name == "电话").Valuec ?? "";
        CompanyAddr = items.FirstOrDefault(i => i.Name == "地址").Valuec ?? "";
        CpRows = new ObservableCollection<CpRow>(await Db.SysDefine.GetCpsAsync(Dpm));
    }

    [RelayCommand]
    private async Task SaveCompanyAsync()
    {
        await Db.SysDefine.SaveCompanyAsync(Dpm, new[]
        {
            ("电话", CompanyTel), ("传真", ""), ("公司名称", CompanyName), ("地址", CompanyAddr), ("开户银行", ""), ("帐号", ""),
        }, CompanyName);
        Message = "公司信息已保存";
    }

    [RelayCommand]
    private async Task SaveCpsAsync()
    {
        await Db.SysDefine.SaveCpsAsync(Dpm, CpRows.ToList());
        Message = $"厂牌整组已保存（{CpRows.Count} 行，整组删+重插）";
        await LoadAsync();
    }

    [RelayCommand]
    public async Task LoadDictAsync(string type)
    {
        DictRows = new ObservableCollection<SysSetRow>(await Db.SysDefine.GetDictAsync(Dpm, type == "全部" ? null : type));
        Message = $"字典 {type}：{DictRows.Count} 行";
    }

    [RelayCommand]
    private async Task SaveDictAsync(SysSetRow row)
    {
        await Db.SysDefine.SaveDictAsync(row.Id == 0, row.Id, row.Type, row.Name, row.Valuec, row.Valuen, Dpm);
        Message = $"字典行 {row.Name} 已保存";
    }

    [RelayCommand]
    private async Task DeleteDictAsync(SysSetRow row)
    {
        if (row is not { Id: > 0 }) return;
        await Db.SysDefine.DeleteDictAsync(row.Id);
        DictRows.Remove(row);
        Message = "字典行已删除（按 id 硬删）";
    }

    [RelayCommand]
    private async Task SaveParasAsync()
    {
        await Db.SysDefine.SaveParasAsync(ParaRows.Select(p => (p.Id, p.Kind, p.Sysname, p.ValueC, p.ValueN, p.ValueL)).ToList());
        Message = "参数已保存（按类型分派列）";
    }

    [RelayCommand]
    private void NewDictRow()
    {
        var t = DictRows.FirstOrDefault()?.Type ?? "unit";
        DictRows.Add(new SysSetRow { Type = t, Name = "", Valuec = "", Dpm = Dpm });
    }

    [RelayCommand]
    public async Task LoadParasAsync()
    {
        // 参数页=system 配置行编辑（findrecord 自动补建行为对已存在行等价；缺失行补建走场景回放验证）
        var rows = await Db.SysDefine.GetDictAsync(Dpm, "system");
        ParaRows = new ObservableCollection<ParaRow>(rows.Select(r => new ParaRow
        {
            Id = r.Id,
            Name = r.Name,
            Sysname = "",
            Kind = r.Valuel.HasValue ? 'L' : r.Valuen is > 0 ? 'N' : 'C',
            Xtype = r.Xtype,
            ValueC = r.Valuec,
            ValueN = r.Valuen,
            ValueL = r.Valuel,
        }));
        Message = $"system 参数 {ParaRows.Count} 行（已按值类型分派编辑列）";
    }

    public partial class ParaRow : ObservableObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Sysname { get; set; } = "";
        public char Kind { get; set; }
        public string Xtype { get; set; } = "";
        public bool IsText => Kind != 'L';
        [ObservableProperty] private string? valueC;
        [ObservableProperty] private decimal? valueN;
        [ObservableProperty] private bool? valueL;
    }
}
