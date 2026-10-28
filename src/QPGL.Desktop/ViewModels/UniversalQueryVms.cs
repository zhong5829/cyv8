using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Printing;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;
using QPGL.Infrastructure.Query;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// E 组万能查询族 VM（第十一批：fr_find 构造器 + fr_se* 八表单；第十二批备案补齐：排序/分组汇总/自定义字段/本地删除/打印）。
/// 契约：开发文档/契约摘录/E组万能查询族契约.md。
/// 行模型 QueryRow（动态字典，fr_mdefi 加列/row56 本地删除语义）；列规格静态定义与 XAML 静态列对齐。
/// </summary>
public partial class UniversalQueryViewModel : ObservableObject
{
    private readonly IUniversalQueryRepository _repo;

    public UniversalQueryViewModel() : this(Db.UniversalQuery) { }

    public UniversalQueryViewModel(IUniversalQueryRepository repo)
    {
        _repo = repo;
        Dept = AppSession.DeptNo;
    }

    public string Dept { get; }

    /// <summary>当前厂牌（旧 P_CP 会话，万能查询仓储按 @p_cp 注入）。</summary>
    [ObservableProperty]
    private string cp = AppSession.Brand;

    [ObservableProperty]
    private string dpmusem = "A"; // 回放/单店部署 tbrole.dpmusem='A'

    [ObservableProperty]
    private string message = "";

    /// <summary>当前页签索引（View TabControl 同步）：0 E1 / 1 E4 / 2 E5 / 3 E6 / 4 E7 / 5 E8 / 6 E9 / 7 EA。</summary>
    [ObservableProperty]
    private int currentTab;

    private string _companyName = "";

    // ---------- 列规格（与 XAML 静态列对齐；显示名对齐旧 tbstru.EXPLAIN） ----------

    private List<QueryColumnSpec> OrderSpecs { get; } =
    [
        new("Cno", "合同号", 'C', 110),
        new("Item", "行号", 'N', 44, "N0"),
        new("Nno", "配件编码", 'C', 100),
        new("Na1", "配件名称", 'C', 120),
        new("Oqty", "订数", 'N', 70, "N0"),
        new("Qty", "到货数", 'N', 70, "N0"),
        new("Zt", "在途", 'N', 60, "N0"),
        new("Iprj", "外币价", 'N', 70, "N2"),
        new("Ty", "车型", 'C', 90),
        new("Fa", "产地", 'C', 70),
        new("Orderdate", "订货日期", 'D', 90, "d"),
        new("Fkfs", "付款方式", 'C', 80),
    ];
    private List<QueryColumnSpec> KcxgSpecs { get; } =
    [
        new("Nno", "配件编码", 'C', 100),
        new("Na1", "配件名称", 'C', 130),
        new("Ty", "车型", 'C', 90),
        new("Xgamount", "修改数量", 'N', 70, "N0"),
        new("Iprc", "成本金额", 'N', 80, "N2"),
        new("Xgdate", "修改日期", 'D', 90, "d"),
        new("Per", "操作员", 'C', 70),
        new("Posi", "库位", 'C', 60),
        new("Depotno", "仓位", 'C', 50),
    ];
    private List<QueryColumnSpec> IoSpecs() => BuildIoSpecs();
    private List<QueryColumnSpec> RtmoSpecs { get; } =
    [
        new("Na1", "配件名称", 'C', 120),
        .. Enumerable.Range(1, 12).Select(i => new QueryColumnSpec($"Oamou{i}", $"oamou{i}", 'N', 58, "N0")),
        new("月均销售数", "月均销售数", 'N', 80, "N2"),
        new("Nno", "编码", 'C', 100),
        new("Cp", "厂牌", 'C', 60),
    ];
    private List<QueryColumnSpec> SesadaSpecs { get; } =
    [
        new("日期", "日期", 'C', 90),
        new("Dpm", "部门", 'C', 90),
        new("单数", "单数", 'N', 60, "N0"),
        new("出库数量", "出库数量", 'N', 70, "N0"),
        new("日元", "日元", 'N', 100, "N2"),
        new("人进价", "人进价", 'N', 100, "N2"),
        new("售额", "售额", 'N', 100, "N2"),
        new("实售额", "实售额", 'N', 100, "N2"),
        new("ML", "毛利", 'N', 100, "N2"),
        new("牟利率", "牟利率", 'N', 70, "N2"),
    ];
    private List<QueryColumnSpec> CheckSpecs { get; } =
    [
        new("Nno", "编码", 'C', 100),
        new("Na1", "名称", 'C', 110),
        new("Ty", "车型", 'C', 80),
        new("Sav", "备份", 'N', 60, "N0"),
        new("Outs", "出库合计", 'N', 70, "N0"),
        new("In", "进库", 'N', 60, "N0"),
        new("Istoch", "库存修改", 'N', 70, "N0"),
        new("Saxg", "出库修改", 'N', 70, "N0"),
        new("逻辑库存", "逻辑库存", 'N', 70, "N0"),
        new("Isto", "实际库存", 'N', 70, "N0"),
        new("误差", "误差", 'N', 60, "N0"),
    ];
    private List<QueryColumnSpec> IsiosmSpecs { get; } =
    [
        new("Per", "操作员", 'C', 60),
        new("No", "单号/库位", 'C', 100),
        new("Date", "日期", 'D', 80, "d"),
        new("Mark", "标志", 'C', 50),
        new("Jqty", "进库数", 'N', 64, "N0"),
        new("Cqty", "出库数", 'N', 64, "N0"),
        new("Amount", "结存数量", 'N', 70, "N0"),
        new("Note", "备注", 'C', 160),
        new("Iprc", "进价", 'N', 66, "N2"),
        new("结存金额", "结存金额", 'N', 90, "N2"),
        new("Depotno", "仓位", 'C', 46),
        new("Iprj", "外币价", 'N', 66, "N2"),
        new("外币价金额", "外币价金额", 'N', 90, "N2"),
        new("Posi", "库位", 'C', 54),
        new("Ty", "车型", 'C', 76),
        new("Fa", "产地", 'C', 60),
    ];

    /// <summary>E1 列规格（含动态扩展列：自定义字段/剩余列）。</summary>
    private List<QueryColumnSpec> _issmSpecList =
    [
        new("Nno", "配件编码", 'C', 90),
        new("Na1", "配件名称", 'C', 110),
        new("Ty", "车型", 'C', 80),
        new("Jkamount", "进库数", 'N', 60, "N0"),
        new("Iprj", "外币价", 'N', 70, "N2"),
        new("Iprc", "进价", 'N', 70, "N2"),
        new("Oprc", "售价", 'N', 70, "N2"),
        new("Posi", "库位", 'C', 50),
        new("Cp", "厂牌", 'C', 60),
        new("Depotno", "仓位", 'C', 44),
        new("Indate", "进库日期", 'D', 80, "d"),
        new("Fkfs", "付款方式", 'C', 70),
        new("Per", "操作员", 'C', 60),
        new("Cno", "进库单号", 'C', 90),
        new("Seano", "装箱单号", 'C', 90),
        new("Gyname", "供应商", 'C', 110),
        new("Gyno", "供应商编号", 'C', 80),
        new("Xkcamou", "剩余库存数", 'N', 70, "N0"),
        new("Xsamou", "销售数", 'N', 60, "N0"),
        new("Dc", "分类码", 'C', 60),
        new("Cxnno", "串号", 'C', 110),
        new("Isord", "订单标志", 'C', 56),
        new("Freight", "运费", 'N', 60, "N2"),
        new("Qrdate", "确认日期", 'D', 80, "d"),
    ];

    private List<QueryColumnSpec> BuildIoSpecs()
    {
        var how = IoDimKind switch
        {
            2 => "车型",
            3 => "零件名",
            4 => "分类码",
            _ => "编码",
        };
        return
        [
            new("How", how, 'C', 110),
            new("销售数", "销售数", 'N', 64, "N0"),
            new("销售成本", "销售成本", 'N', 76, "N2"),
            new("销售额", "销售额", 'N', 86, "N2"),
            new("进库数", "进库数", 'N', 64, "N0"),
            new("进库额", "进库额", 'N', 86, "N2"),
            new("库存数", "库存数", 'N', 64, "N0"),
            new("毛利", "毛利", 'N', 76, "N2"),
            new("毛利率", "毛利率", 'C', 70),
            new("预售数", "预售数", 'N', 64, "N0"),
            new("预售成本", "预售成本", 'N', 76, "N2"),
            new("预售额", "预售额", 'N', 86, "N2"),
            new("调拔数", "调拔数", 'N', 64, "N0"),
            new("调拔成本", "调拔成本", 'N', 76, "N2"),
            new("调拔额", "调拔额", 'N', 86, "N2"),
        ];
    }

    /// <summary>当前页签列规格（排序/分组合计/自定义字段/打印共用）。</summary>
    private List<QueryColumnSpec> CurrentSpecs => CurrentTab switch
    {
        0 => _issmSpecList,
        1 => OrderSpecs,
        2 => KcxgSpecs,
        3 => BuildIoSpecs(),
        4 => RtmoSpecs,
        5 => SesadaSpecs,
        6 => CheckSpecs,
        _ => IsiosmSpecs,
    };

    /// <summary>当前页签行集合（可变引用，本地删除/自定义字段回填后仍指向同一集合）。</summary>
    private ObservableCollection<QueryRow> CurrentRows => CurrentTab switch
    {
        0 => IssmRows,
        1 => OrderRows,
        2 => KcxgRows,
        3 => IoRows,
        4 => RtmoRows,
        5 => SesadaRows,
        6 => CheckRows,
        _ => IsiosmRows,
    };

    /// <summary>当前页签查询条件文本（页眉"查询条件【..】"）。</summary>
    private string CurrentFindText => CurrentTab switch
    {
        0 => _issmWhere,
        1 => _orderWhere,
        2 => _kcxgWhere,
        3 => IoFindText(),
        4 => RtmoFindText(),
        5 => $"日期 {SesadaD1:yyyy-MM-dd} ~ {SesadaD2:yyyy-MM-dd}，分公司 {SesadaDpm}",
        6 => $"检测日期 {CheckDate:yyyy-MM-dd}",
        _ => $"编码 {IsiosmNno}，日期 {IsiosmD1:yyyy-MM-dd} ~ {IsiosmD2:yyyy-MM-dd}",
    };

    private string IoFindText()
    {
        var tag = _ioTagCaption ?? "";
        return $"{tag}：{IoDimKind switch { 2 => IoTy, 3 => IoNa1, 4 => $"{IoDc1}~{IoDc2}", 5 => IoHz, _ => $"{IoNno1}~{IoNno2}" }}，" +
               $"日期 {IoD1:yyyy-MM-dd} ~ {IoD2:yyyy-MM-dd}";
    }

    private string RtmoFindText()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(RtmoNno)) parts.Add($"编码 {RtmoNno.Trim()}");
        if (!string.IsNullOrWhiteSpace(RtmoTy)) parts.Add($"车型 {RtmoTy.Trim()}");
        if (!string.IsNullOrWhiteSpace(RtmoNa1)) parts.Add($"品名 {RtmoNa1.Trim()}");
        parts.Add(RtmoAllBrand ? "全部厂牌" : $"厂牌 {Cp}");
        return string.Join("，", parts);
    }

    // ---------- E1 进库单查询 ----------

    [ObservableProperty]
    private ObservableCollection<QueryRow> issmRows = new();

    [ObservableProperty]
    private bool issmShowRemain;

    private string _issmWhere = "";
    private string? _ioTagCaption;

    partial void OnIssmShowRemainChanged(bool value) => IssmSearchCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task IssmFindAsync()
    {
        var where = FindDialogWindow.Show("进库单查询（tbistoed）", IssmFields(), IssmShortcuts());
        if (where is null) return;
        _issmWhere = where;
        await IssmSearchAsync();
    }

    [RelayCommand]
    private async Task IssmSearchAsync()
    {
        try
        {
            Message = "正在查询进库单……";
            var where = _issmWhere.Length == 0
                ? BuildDefaultIssmWhere()
                : _issmWhere;
            var rows = await _repo.SearchIssmAsync(where, IssmShowRemain, Dept, Dpmusem);
            if (IssmShowRemain)
                rows = await _repo.RecalcIssmRemainAsync(rows, Dept);
            IssmRows = new ObservableCollection<QueryRow>(QueryGridHelper.Wrap(rows));
            Message = IssmRows.Count == 0 ? "没有符合条件的记录！" : $"共 {IssmRows.Count} 行";
        }
        catch (Exception ex)
        {
            Message = "查询失败：" + ex.Message;
        }
    }

    /// <summary>华美波箱部署默认本月区间（本部署休眠）；缺省 = 全部（无条件 1=1）。</summary>
    private static string BuildDefaultIssmWhere() => "1=1";

    [RelayCommand]
    private async Task IssmCalcAsync()
    {
        if (IssmRows.Count == 0) { Message = "没有符合条件的记录！"; return; }
        Message = "计算完成！";
        await Task.CompletedTask;
    }

    /// <summary>合计（fr_seissm row62：DO FORM fr_total 分组汇总弹窗；SUMSTR 按行62 条件构造）。</summary>
    [RelayCommand]
    private async Task IssmTotalAsync()
    {
        if (IssmRows.Count == 0) { Message = "请查询数据"; return; }
        await OpenGroupTotalAsync(BuildIssmTotals());
    }

    /// <summary>row62 SUMSTR（P_WN=厂牌 valuec、CHECK1 剩余、@/- 人进价权限、P_WBTOHS=1 成本取 iprj）。</summary>
    private List<TotalColumn> BuildIssmTotals()
    {
        var wn = Cp == "门市" ? "人民币" : "含税价";
        var totals = new List<TotalColumn>
        {
            new("进库项目", 'N', rows => (decimal)rows.Count()),
            new("进库数", 'N', rows => rows.Sum(r => Math.Truncate(r.GetDecimal("Jkamount")))),
            new(wn, 'N', rows => Math.Round(rows.Sum(r => r.GetDecimal("Jkamount") * r.GetDecimal("Iprj")), 2)),
        };
        if (IssmShowRemain)
            totals.Add(new TotalColumn("销售数", 'N', rows => rows.Sum(r => r.GetDecimal("Xsamou"))));
        totals.Add(new TotalColumn("人进价", 'N', rows => Math.Round(rows.Sum(r => r.GetDecimal("Iprc") * r.GetDecimal("Jkamount")), 4)));
        if (IssmShowRemain)
        {
            // P_WBTOHS=1（部署实测）→ 成本取 iprj
            totals.Add(new TotalColumn("销售成本", 'N', rows => Math.Round(rows.Sum(r => r.GetDecimal("Iprj") * r.GetDecimal("Xsamou")), 2)));
            totals.Add(new TotalColumn("剩余成本", 'N', rows => Math.Round(rows.Sum(r => r.GetDecimal("Iprj") * r.GetDecimal("Xkcamou")), 2)));
        }
        totals.Add(new TotalColumn("运费成本", 'N', rows => rows.Sum(r => r.GetDecimal("Freight"))));
        return totals;
    }

    // ---------- fr_sort（row57/row22：结果集多字段排序，纯客户端） ----------

    [RelayCommand]
    private void SortCurrent()
    {
        if (CurrentRows.Count == 0) { Message = "请先查询数据"; return; }
        var dialog = new SortDialogWindow(CurrentSpecs);
        if (dialog.ShowDialog() != true || dialog.ResultKeys is null) return;
        var sorted = QueryGridHelper.SortRows(CurrentRows.ToList(), dialog.ResultKeys).ToList();
        ReplaceRows(sorted);
        Message = "排序完成";
    }

    /// <summary>排序/加列后重建当前集合（保持 CurrentRows 引用路由语义：直接整体替换页签行属性）。</summary>
    private void ReplaceRows(List<QueryRow> sorted)
    {
        switch (CurrentTab)
        {
            case 0: IssmRows = new ObservableCollection<QueryRow>(sorted); break;
            case 1: OrderRows = new ObservableCollection<QueryRow>(sorted); break;
            case 2: KcxgRows = new ObservableCollection<QueryRow>(sorted); break;
            case 3: IoRows = new ObservableCollection<QueryRow>(sorted); break;
            case 4: RtmoRows = new ObservableCollection<QueryRow>(sorted); break;
            case 5: SesadaRows = new ObservableCollection<QueryRow>(sorted); break;
            case 6: CheckRows = new ObservableCollection<QueryRow>(sorted); break;
            default: IsiosmRows = new ObservableCollection<QueryRow>(sorted); break;
        }
    }

    // ---------- fr_total 分组汇总（E1 row62 / E4 row26） ----------

    private async Task OpenGroupTotalAsync(List<TotalColumn> totals)
    {
        if (CurrentRows.Count == 0) { Message = "请查询数据"; return; }
        if (string.IsNullOrEmpty(_companyName))
            _companyName = await _repo.GetCompanyNameAsync();
        var dpmNames = await _repo.GetDpmNamesAsync();
        var dialog = new GroupTotalWindow(CurrentRows, CurrentSpecs, totals, dpmNames, _companyName)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };
        dialog.ShowDialog();
    }

    // ---------- fr_mdefi 自定义字段（row79/row28） ----------

    [RelayCommand]
    private async Task CustomFieldAsync()
    {
        if (CurrentRows.Count == 0) { Message = "请先查询数据"; return; }
        var rows = CurrentRows;
        var dialog = new CustomFieldWindow(
            rows,
            CurrentSpecs,
            () => _repo.GetSysTablesAsync(),
            t => _repo.GetSysFieldsAsync(t),
            (t, items) => _repo.AggregateSysTableAsync(
                t, items,
                rows[0].GetDynamicMemberNames().Any(f => f.Equals("Cp", StringComparison.OrdinalIgnoreCase)),
                rows[0].GetDynamicMemberNames().Any(f => f.Equals("Nno", StringComparison.OrdinalIgnoreCase))),
            PickCsvFileAsync,
            LoadCsvAsync)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };
        if (dialog.ShowDialog() != true || dialog.Outcome is null) return;
        // 加列（旧 .COLUMNCOUNT+1 / HEADER1.CAPTION=FN）
        foreach (var spec in dialog.Outcome.NewColumns)
        {
            CurrentSpecs.Add(spec);
            CustomFieldColumnAdded?.Invoke(spec);
        }
        Message = "自定义字段建立完成";
    }

    /// <summary>View 订阅：自定义字段加列后向当前 DataGrid 追加动态列。</summary>
    public event Action<QueryColumnSpec>? CustomFieldColumnAdded;

    private static async Task<string> PickCsvFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择文件",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : "";
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> LoadCsvAsync(string file)
    {
        // 旧 IMPORT XL5/USE DBF → 新侧 CSV（XLS/DBF 备案）；GBK/UTF8 自动尝试
        string text;
        try { text = await File.ReadAllTextAsync(file, System.Text.Encoding.UTF8); }
        catch { text = await File.ReadAllTextAsync(file, System.Text.Encoding.GetEncoding("GB18030")); }
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return [];
        var headers = SplitCsvLine(lines[0]);
        var result = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = SplitCsvLine(lines[i]);
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Length; c++)
            {
                var raw = c < cells.Length ? cells[c].Trim() : "";
                dict[headers[c]] = decimal.TryParse(raw, out var m) ? m
                    : DateTime.TryParse(raw, out var d) ? d
                    : raw;
            }
            result.Add(dict);
        }
        await Task.CompletedTask;
        return result;
    }

    private static string[] SplitCsvLine(string line)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuote = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (ch == ',' && !inQuote) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        list.Add(sb.ToString());
        return list.ToArray();
    }

    // ---------- row56/row26/row40/row28 本地删除（会话内隐藏行，不落库） ----------

    [RelayCommand]
    private void DeleteRowCurrent(object parameter)
    {
        if (parameter is not QueryRow row) { Message = "请先选中要删除的行"; return; }
        if (MessageBox.Show("确认删除吗", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question,
                MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        CurrentRows.Remove(row);
        Message = "已删除（仅本次结果集，不影响数据库）";
    }

    // ---------- 打印（row59/row39-41/row26/row25/row39/row27/row26/row33；通用列表打印器） ----------

    [RelayCommand]
    private async Task PrintCurrentAsync(object parameter)
    {
        if (CurrentRows.Count == 0) { Message = "请先查询数据"; return; }
        if (string.IsNullOrEmpty(_companyName))
            _companyName = await _repo.GetCompanyNameAsync();

        List<QueryRow> printRows = CurrentRows.ToList();
        string caption;
        var specs = CurrentSpecs.Select(s => s.Clone()).ToList();

        switch (CurrentTab)
        {
            case 0:
            {
                // E1 row59 弹菜单（P_CLIENT=标准：南海/沙河/讯息变体休眠备案）
                var menu = new PrintMenuWindow(["顺序打印", "按单据（清单）打印", "按供应商分组打印"]);
                if (menu.ShowDialog() != true || menu.SelectedBar is null) return;
                switch (menu.SelectedBar.Value)
                {
                    case 1:
                        // 顺序打印：本部署不做 ORDER BY POSI；首行 jkamount>0 → 进库单查询报表，否则退库清单
                        caption = (printRows[0].GetDecimal("Jkamount") > 0) ? "进库单查询报表" : "退库清单";
                        break;
                    case 2:
                        // 按单据打印：结果集含多个 CNO → ORDER BY CNO；标题"入库清单"
                        if (printRows.Select(r => (r.Get("Cno")?.ToString() ?? "")).Distinct().Count() > 1)
                            printRows = printRows.OrderBy(r => r.Get("Cno")?.ToString() ?? "").ToList();
                        caption = "入库清单";
                        break;
                    default:
                        // 按供应商分组打印：ORDER BY GYNAME,CNO；标题"进库报表"
                        printRows = printRows
                            .OrderBy(r => r.Get("Gyname")?.ToString() ?? "")
                            .ThenBy(r => r.Get("Cno")?.ToString() ?? "").ToList();
                        caption = "进库报表";
                        break;
                }
                break;
            }
            case 1:
            {
                // E4 三打印按钮（row39/40/41）→ 菜单三项
                var menu = new PrintMenuWindow(["订货清单打印", "未到单报表打印", "订货未到单报表打印"]);
                if (menu.ShowDialog() != true || menu.SelectedBar is null) return;
                caption = menu.SelectedBar.Value switch
                {
                    1 => "订货清单",
                    2 => "未到单报表",
                    _ => "订货未到单报表",
                };
                break;
            }
            case 2: caption = "库存修改查询"; break;
            case 3: caption = "商品出入查询"; break;
            case 4: caption = "月销售单查询报表"; break;
            case 5: caption = "营业额报表"; break;
            case 6: caption = "数据检查结果表"; break;
            default: caption = "进出流水帐查询表"; break;
        }

        var doc = ListPrintDocument.Build(new ListPrintRequest
        {
            Caption = caption,
            FindText = CurrentFindText,
            Company = _companyName,
            Operator = AppSession.Operator,
            Columns = specs,
            Rows = printRows,
        });
        var preview = new PrintPreviewWindow(doc, $"预览 - {caption}");
        preview.ShowDialog();
        Message = "";
    }

    // ---------- E4 订货单查询 ----------

    [ObservableProperty]
    private ObservableCollection<QueryRow> orderRows = new();

    private string _orderWhere = "";

    [RelayCommand]
    private async Task OrderFindAsync()
    {
        var where = FindDialogWindow.Show("订货单查询（tborder）", OrderFields(), OrderShortcuts());
        if (where is null) return;
        _orderWhere = where;
        await OrderSearchAsync();
    }

    [RelayCommand]
    private async Task OrderSearchAsync()
    {
        try
        {
            Message = "正在查询订货单……";
            var rows = await _repo.SearchOrderAsync(_orderWhere.Length == 0 ? "1=1" : _orderWhere, Dept, Cp, Dpmusem);
            OrderRows = new ObservableCollection<QueryRow>(QueryGridHelper.Wrap(rows));
            Message = OrderRows.Count == 0 ? "没有符合条件的记录！" : $"共 {OrderRows.Count} 行";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
    }

    /// <summary>合计（fr_seorder row26：DO FORM fr_total 分组汇总弹窗）。</summary>
    [RelayCommand]
    private async Task OrderTotalAsync()
    {
        await OpenGroupTotalAsync(
        [
            new TotalColumn("订货项目", 'N', rows => (decimal)rows.Count()),
            new TotalColumn("订数", 'N', rows => rows.Sum(r => Math.Truncate(r.GetDecimal("Oqty")))),
            new TotalColumn("订货金额", 'N', rows => Math.Round(rows.Sum(r => r.GetDecimal("Oqty") * r.GetDecimal("Iprj")), 2)),
            new TotalColumn("在途", 'N', rows => rows.Sum(r => Math.Truncate(r.GetDecimal("Oqty") - r.GetDecimal("Qty")))),
            new TotalColumn("在途金额", 'N', rows => Math.Round(rows.Sum(r => (r.GetDecimal("Oqty") - r.GetDecimal("Qty")) * r.GetDecimal("Iprj")), 2)),
        ]);
    }

    // ---------- E5 库存修改查询 ----------

    [ObservableProperty]
    private ObservableCollection<QueryRow> kcxgRows = new();

    private string _kcxgWhere = "";

    [RelayCommand]
    private async Task KcxgFindAsync()
    {
        var where = FindDialogWindow.Show("库存修改查询（tbistoch）", KcxgFields(), null);
        if (where is null) return;
        _kcxgWhere = where;
        await KcxgSearchAsync();
    }

    [RelayCommand]
    private async Task KcxgSearchAsync()
    {
        try
        {
            Message = "正在查询库存修改……";
            var rows = await _repo.SearchKcxgAsync(_kcxgWhere.Length == 0 ? "1=1" : _kcxgWhere, Dept, Dpmusem);
            KcxgRows = new ObservableCollection<QueryRow>(QueryGridHelper.Wrap(rows));
            Message = KcxgRows.Count == 0 ? "没有符合条件的记录！" : $"共 {KcxgRows.Count} 行";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
    }

    /// <summary>益损合计（fr_sekcxg row25：SUM XGAMOUNT/IPRC 本地游标）。</summary>
    [RelayCommand]
    private void KcxgTotal()
    {
        if (KcxgRows.Count == 0) { Message = "请查询数据"; return; }
        Message = $"益损:{KcxgRows.Sum(r => r.GetDecimal("Xgamount")),8} 益损成本:{KcxgRows.Sum(r => r.GetDecimal("Iprc")),12:N2}";
    }

    // ---------- E6 商品出入查询 ----------

    [ObservableProperty]
    private ObservableCollection<QueryRow> ioRows = new();

    /// <summary>E6 维度（row31：1 编码区间/2 车型/3 零件名/4 分类码区间/5 汇总项）。</summary>
    [ObservableProperty]
    private int ioDimKind = 1;

    partial void OnIoDimKindChanged(int value)
    {
        IoShowNno = value == 1;
        IoShowTy = value == 2;
        IoShowNa1 = value == 3;
        IoShowDc = value == 4;
        IoShowHz = value == 5;
    }

    [ObservableProperty] private bool ioShowNno = true;
    [ObservableProperty] private bool ioShowTy;
    [ObservableProperty] private bool ioShowNa1;
    [ObservableProperty] private bool ioShowDc;
    [ObservableProperty] private bool ioShowHz;

    [ObservableProperty]
    private string ioNno1 = "";

    [ObservableProperty]
    private string ioNno2 = "";

    [ObservableProperty]
    private string ioTy = "";

    [ObservableProperty]
    private string ioNa1 = "";

    [ObservableProperty]
    private string ioDc1 = "";

    [ObservableProperty]
    private string ioDc2 = "";

    /// <summary>汇总项：NNO/TY/NA1/DC。</summary>
    [ObservableProperty]
    private string ioHz = "";

    [ObservableProperty]
    private DateTime ioD1 = DateTime.Today;

    [ObservableProperty]
    private DateTime ioD2 = DateTime.Today;

    /// <summary>维度条件 TAG（row31）。</summary>
    private (string How, string Tag, string Caption)? IoBuildTag()
    {
        switch (IoDimKind)
        {
            case 1:
                if (string.IsNullOrWhiteSpace(IoNno1) || string.IsNullOrWhiteSpace(IoNno2)) { Message = "必须输入起止编码!"; return null; }
                return ("nno", $"nno between '{IoNno1.Trim()}' and '{IoNno2.Trim()}z'", "编码");
            case 2:
                if (string.IsNullOrWhiteSpace(IoTy)) { Message = "必须输入查询车型!"; return null; }
                return ("ty", $"ty between '{IoTy.Trim()}' and '{IoTy.Trim()}组'", "车型");
            case 3:
                if (string.IsNullOrWhiteSpace(IoNa1)) { Message = "必须输入查询零件名!"; return null; }
                return ("na1", $"na1 between '{IoNa1.Trim()}' and '{IoNa1.Trim()}组'", "零件名");
            case 4:
                if (string.IsNullOrWhiteSpace(IoDc1) || string.IsNullOrWhiteSpace(IoDc2)) { Message = "必须输入起止分类码!"; return null; }
                return ("dc", $"dc between '{IoDc1.Trim()}' and '{IoDc2.Trim()}组'", "分类码");
            case 5:
                if (string.IsNullOrWhiteSpace(IoHz)) { Message = "请选择汇总项!"; return null; }
                return (IoHz.ToLowerInvariant(), "", IoHz.ToUpperInvariant());
            default:
                Message = "请选择查询维度！";
                return null;
        }
    }

    [RelayCommand]
    private async Task IoSearchAsync()
    {
        var tag = IoBuildTag();
        if (tag is null) return;
        _ioTagCaption = tag.Value.Caption;
        try
        {
            Message = "正在处理,请稍候.....";
            var rows = await _repo.SearchIoAsync(tag.Value.How, IoD1, IoD2, tag.Value.Tag, Dept, Cp, Dpmusem);
            IoRows = new ObservableCollection<QueryRow>(QueryGridHelper.Wrap(rows));
            Message = IoRows.Count == 0 ? "没有符合条件的记录！" : $"共 {IoRows.Count} 行（{tag.Value.Caption}）";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
    }

    // ---------- E7 月销售数查询 ----------

    [ObservableProperty]
    private ObservableCollection<QueryRow> rtmoRows = new();

    [ObservableProperty]
    private string rtmoNno = "";

    [ObservableProperty]
    private string rtmoTy = "";

    [ObservableProperty]
    private string rtmoNa1 = "";

    /// <summary>厂牌选项（row54：2=全部厂牌跳过 cp 条件）。</summary>
    [ObservableProperty]
    private bool rtmoAllBrand;

    [RelayCommand]
    private async Task RtmoSearchAsync()
    {
        var w = "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(RtmoNno)) parts.Add($"nno between '{RtmoNno.Trim()}' and '{RtmoNno.Trim()}z'");
        if (!string.IsNullOrWhiteSpace(RtmoTy)) parts.Add($"ty between '{RtmoTy.Trim()}' and '{RtmoTy.Trim()}作作'");
        if (!string.IsNullOrWhiteSpace(RtmoNa1)) parts.Add($"na1 between '{RtmoNa1.Trim()}' and '{RtmoNa1.Trim()}作作'");
        if (!RtmoAllBrand) parts.Add($"cp='{Cp}'");
        w = string.Join(" and ", parts);
        try
        {
            Message = "正在处理，请稍候......";
            var rows = await _repo.SearchRtmoAsync(w, Dept, Dpmusem, 12);
            RtmoRows = new ObservableCollection<QueryRow>(QueryGridHelper.Wrap(rows));
            Message = RtmoRows.Count == 0 ? "没有符合条件的记录！" : $"共 {RtmoRows.Count} 行";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
    }

    /// <summary>"全部"按钮（row59：清条件查全表）。</summary>
    [RelayCommand]
    private async Task RtmoSearchAllAsync()
    {
        RtmoNno = RtmoTy = RtmoNa1 = "";
        RtmoAllBrand = true;
        await RtmoSearchAsync();
        RtmoAllBrand = false;
    }

    // ---------- E8 营业额查询 ----------

    [ObservableProperty]
    private ObservableCollection<QueryRow> sesadaRows = new();

    [ObservableProperty]
    private DateTime sesadaD1 = DateTime.Today.AddDays(-30);

    [ObservableProperty]
    private DateTime sesadaD2 = DateTime.Today;

    /// <summary>分公司（P_FGS=.T.）：null/''=当前部门（dpmbh）、'*'=全部分公司。</summary>
    [ObservableProperty]
    private string sesadaDpm = "A";

    [RelayCommand]
    private async Task SesadaSearchAsync()
    {
        if (SesadaD1 > SesadaD2) { Message = "请输入正确的起止日期！"; return; }
        try
        {
            Message = "正在处理，请稍候......";
            var cldpm = SesadaDpm == "*" ? "*" : SesadaDpm;
            var rows = await _repo.SearchSesadaAsync(SesadaD1, SesadaD2, cldpm, Dept, Dpmusem);
            SesadaRows = new ObservableCollection<QueryRow>(QueryGridHelper.Wrap(rows));
            Message = SesadaRows.Count == 0 ? "没有符合条件的记录！" : $"共 {SesadaRows.Count} 行";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
    }

    // ---------- E9 库存检测 ----------

    [ObservableProperty]
    private ObservableCollection<QueryRow> checkRows = new();

    [ObservableProperty]
    private string checkSummary = "";

    [ObservableProperty]
    private DateTime checkDate = DateTime.Today;

    [RelayCommand]
    private async Task CheckRunAsync()
    {
        try
        {
            Message = "正在检测,请稍候....";
            var r = await _repo.CheckStockAsync(CheckDate, Dept, Cp, Dpmusem);
            if (!r.HasBackup)
            {
                CheckSummary = $" 无{CheckDate:yyyy.MM.dd}的库存备份!";
                CheckRows = new();
                Message = "";
                return;
            }
            CheckRows = new ObservableCollection<QueryRow>(QueryGridHelper.Wrap(r.Rows));
            CheckSummary =
                $"{CheckDate:yyyy.MM.dd}" + Environment.NewLine +
                $" 库    存:{Str7(r.SavA)}" + Environment.NewLine +
                $" 进    库:{Str7(r.IstoedA)}" + Environment.NewLine +
                $" 销售历史:{Str7(r.SaedA)}" + Environment.NewLine +
                $" 日 销 售:{Str7(r.SadaA)}   预售转销售:{Str7(r.SadaAll - r.SadaA)}" + Environment.NewLine +
                $" 预    售:{Str7(r.SabeA)}" + Environment.NewLine +
                $" 调    拨:{Str7(r.SadbA)}" + Environment.NewLine +
                $" 领    料:{Str7(r.XlsaA)}" + Environment.NewLine +
                $" 库存修改:{Str7(r.IstochA)}" + Environment.NewLine +
                $" 出库修改:{Str7(r.SaxgA)}" + Environment.NewLine +
                $" 逻辑库存:{Str7(r.NowA)}" + Environment.NewLine +
                $" 实际库存:{Str7(r.IstoA)}" + Environment.NewLine +
                $" 误    差:{Str7(r.DiffA)}";
            Message = $"检测完成! 错误数:{r.Rows.Count}";
        }
        catch (Exception ex) { Message = "检测失败：" + ex.Message; }
    }

    /// <summary>旧 STR(x,7)：右对齐 7 位整数。</summary>
    private static string Str7(decimal v) => Math.Round(v).ToString("0").PadLeft(7);

    // ---------- EA 进出流水帐 ----------

    [ObservableProperty]
    private ObservableCollection<QueryRow> isiosmRows = new();

    [ObservableProperty]
    private string isiosmNno = "";

    [ObservableProperty]
    private DateTime isiosmD1 = DateTime.Today;

    [ObservableProperty]
    private DateTime isiosmD2 = DateTime.Today;

    [RelayCommand]
    private async Task IsiosmSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(IsiosmNno)) { Message = "请输入全编码"; return; }
        try
        {
            Message = "请稍候...";
            var rows = await _repo.SearchIsiosmAsync(IsiosmNno.Trim(), IsiosmD1, IsiosmD2, Dept, Cp, Dpmusem);
            IsiosmRows = new ObservableCollection<QueryRow>(QueryGridHelper.Wrap(rows));
            Message = IsiosmRows.Count == 0 ? "错误编码,请重新输入！" : $"共 {IsiosmRows.Count} 行";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
    }

    // ---------- fr_find 字段/快捷条件定义 ----------

    private static IReadOnlyList<FindFieldSpec> IssmShortcuts() => new[]
    {
        new FindFieldSpec("退货", "jkamount<0", FindKind.Text),
        new FindFieldSpec("进库单号", "cno", FindKind.Text),
        new FindFieldSpec("已确认", "mark='已确认'", FindKind.Text),
        new FindFieldSpec("未确认", "mark<>'已确认'", FindKind.Text),
        new FindFieldSpec("已统计", "type='历史'", FindKind.Text),
        new FindFieldSpec("未统计", "type<>'历史'", FindKind.Text),
        new FindFieldSpec("散件入库", "(isord='N' or (seano = '' or seano is null) or (isord='' and seano <> '' and seano is not null and seano=cno))", FindKind.Text),
        new FindFieldSpec("订单入库", "(isord='Y' or (seano <> '' and seano is not null and seano<>cno))", FindKind.Text),
    };

    private static IReadOnlyList<FindFieldSpec> IssmFields() => new[]
    {
        new FindFieldSpec("配件编码", "nno", FindKind.Text),
        new FindFieldSpec("配件名称", "na1", FindKind.Text),
        new FindFieldSpec("产地", "fa", FindKind.Text),
        new FindFieldSpec("分类码", "dc", FindKind.Text),
        new FindFieldSpec("供应商名称", "gyname", FindKind.Text),
        new FindFieldSpec("进库日期", "indate", FindKind.Date),
        new FindFieldSpec("进库单号", "cno", FindKind.Text),
        new FindFieldSpec("厂牌", "cp", FindKind.Text),
        new FindFieldSpec("付款方式", "fkfs", FindKind.Text),
        new FindFieldSpec("采购单号", "pno", FindKind.Text),
        new FindFieldSpec("部门", "dpm", FindKind.Text),
        new FindFieldSpec("装箱单号", "seano", FindKind.Text),
        new FindFieldSpec("箱号", "box", FindKind.Text),
        new FindFieldSpec("车型", "ty", FindKind.Text),
        new FindFieldSpec("仓位", "depotno", FindKind.Text),
        new FindFieldSpec("供应商编号", "gyno", FindKind.Text),
        new FindFieldSpec("业务员", "ywr", FindKind.Text),
        new FindFieldSpec("进库数", "jkamount", FindKind.Numeric),
        new FindFieldSpec("串号", "cxnno", FindKind.Text),
        new FindFieldSpec("库位", "posi", FindKind.Text),
        new FindFieldSpec("操作员", "per", FindKind.Text),
        new FindFieldSpec("备注", "note", FindKind.Memo),
        new FindFieldSpec("订单标志", "isord", FindKind.Text),
        new FindFieldSpec("确认人", "qrr", FindKind.Text),
        new FindFieldSpec("确认日期", "qrdate", FindKind.Date),
    };

    private static IReadOnlyList<FindFieldSpec> OrderShortcuts() => new[]
    {
        new FindFieldSpec("在途", "oqty>qty", FindKind.Text),
        new FindFieldSpec("历史", "type='历史'", FindKind.Text),
    };

    private static IReadOnlyList<FindFieldSpec> OrderFields() => new[]
    {
        new FindFieldSpec("配件编码", "nno", FindKind.Text),
        new FindFieldSpec("配件名称", "na1", FindKind.Text),
        new FindFieldSpec("车型", "ty", FindKind.Text),
        new FindFieldSpec("产地", "fa", FindKind.Text),
        new FindFieldSpec("合同号", "cno", FindKind.Text),
        new FindFieldSpec("订货日期", "orderdate", FindKind.Date),
        new FindFieldSpec("行号", "item", FindKind.Numeric),
    };

    private static IReadOnlyList<FindFieldSpec> KcxgFields() => new[]
    {
        new FindFieldSpec("配件编码", "nno", FindKind.Text),
        new FindFieldSpec("配件名称", "na1", FindKind.Text),
        new FindFieldSpec("修改日期", "xgdate", FindKind.Date),
    };
}
