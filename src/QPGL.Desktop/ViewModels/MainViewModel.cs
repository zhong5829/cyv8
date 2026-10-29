using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;

namespace QPGL.Desktop.ViewModels;

/// <summary>多标签文档页（§8.2 多标签 TabControl，✕ 可关闭）。</summary>
public partial class DocumentTab : ObservableObject
{
    public required string Key { get; init; }
    public required string Header { get; init; }
    public required object Content { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimerEx _clock = new();
    private readonly DispatcherTimerEx _health = new();

    public ObservableCollection<DocumentTab> Tabs { get; } = [];

    [ObservableProperty]
    private DocumentTab? _activeTab;

    [ObservableProperty]
    private string _statusOperator = "";

    [ObservableProperty]
    private string _statusRole = "";

    [ObservableProperty]
    private string _statusConnection = "连接中…";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusTime = "";

    [ObservableProperty]
    private int _unprintedCount;

    public event Action? RequestClose;

    public MainViewModel()
    {
        StatusOperator = AppSession.Operator;
        StatusRole = string.IsNullOrEmpty(AppSession.Role) ? "-" : AppSession.Role;

        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => StatusTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clock.Start();

        // 状态栏「●已连接」（§8.2）：定时健康检查
        _health.Interval = TimeSpan.FromSeconds(30);
        _health.Tick += async (_, _) => await CheckHealthAsync();
        _health.Start();
        _ = CheckHealthAsync();
    }

    public void OpenDefault()
    {
        OpenTab("desktop", "工作台", () => new DesktopControl(this));
    }

    /// <summary>按 Key 去重开标签；已存在则激活。</summary>
    public void OpenTab(string key, string header, Func<object> contentFactory)
    {
        var existing = Tabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        var tab = new DocumentTab { Key = key, Header = header, Content = contentFactory() };
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    private void CloseTab(DocumentTab? tab) => Close(tab ?? ActiveTab);

    private void Close(DocumentTab? tab)
    {
        if (tab is null) return;
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (tab.Content is IDisposable d) d.Dispose();
        ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(idx, Tabs.Count - 1)];
    }

    // ---- 菜单/工具条命令（M3 权限拦截：菜单过滤 + 调用拦截，§7.2 NO_PERMISSION）----

    /// <summary>权限守卫：无权限弹提示不打开（旧 fr_main pwcheck 禁用语义）。</summary>
    private bool Guard(string code, string featureName)
    {
        if (AppSession.Permission.HasMenu(code)) return true;
        MessageBox.Show($"无「{featureName}」({code}) 权限，请联系管理员分配角色权限。",
            "权限提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    [RelayCommand]
    private void OpenPartners()
    {
        if (!Guard("A5", "客户资料管理")) return;
        OpenTab("partners", "客户资料", () => new PartnerListControl());
    }

    /// <summary>工具条「查询 F3」：激活 Tab 为销售开单页时切换双模式，否则打开客户资料。</summary>
    [RelayCommand]
    private void SwitchSaleMode()
    {
        if (ActiveTab?.Content is SaleControl sale)
            sale.OnQuery();
        else
            OpenPartners();
    }

    [RelayCommand]
    private void OpenSale()
    {
        if (!Guard("C1", "销售管理")) return;
        OpenTab("sale", "销售开单", () => new SaleControl());
    }

    /// <summary>销售退货（C6，旧菜单 BAR 7「退货管理」fr_sata 权限码 C6）。</summary>
    [RelayCommand]
    private void OpenSaleReturn()
    {
        if (!Guard("C6", "退货管理")) return;
        OpenTab("saleReturn", "销售退货", () => new SaleReturnControl());
    }

    /// <summary>出库单查询（E2，M3-2）；收款结算（G2）一期随查询页选中单据收款。</summary>
    [RelayCommand]
    private void OpenOutDocQuery()
    {
        if (!Guard("E2", "出库单查询")) return;
        OpenTab("outDocQuery", "出库单查询", () => new OutDocQueryControl());
    }

    /// <summary>
    /// 财务确认（G.1，旧 fr_sk 复刻；菜单权限码 G1 —— mn_main.mpr L293 SKIP FOR ',G1,'$p_usem）。
    /// 多页签窗体：出库/入库/调拨/代销付款四页实现（维修 3 页按附录A排除、服务卡/精品/增值卡未建置灰）。
    /// </summary>
    [RelayCommand]
    private void OpenFinanceConfirm()
    {
        if (!Guard("G1", "财务确认")) return;
        OpenTab("financeConfirm", "财务确认", () => new FinanceConfirmControl());
    }

    /// <summary>
    /// A 组属性定义族（第十二批）：A1 零件属性 / A2 零件代码 / A3 属性传递 / A4 套件管理 / A7 税则号 / A8 系统定义 / AA 分类码。
    /// A5 客户资料 / A6 供应商 / A9 单号前缀一期已交。
    /// </summary>
    [RelayCommand]
    private void OpenAttrBasic(string code)
    {
        var name = code switch
        {
            "A1" => "零件属性定义",
            "A2" => "零件代码定义",
            "A3" => "属性传递",
            "A4" => "套件管理",
            "A7" => "商品税则号查询",
            "A8" => "系统定义",
            "AA" => "分类码定义",
            _ => "属性定义",
        };
        if (!Guard(code, name)) return;
        switch (code)
        {
            case "A1": OpenTab("prnoty", "零件属性定义", () => new PrnotyControl()); break;
            case "A2": OpenTab("prname", "零件代码定义", () => new BasicAttrControls()); break;
            case "A3": OpenTab("sxcd", "属性传递", () => new AttrTransferKitControl()); break;
            case "A4": OpenTab("tjgl", "套件管理", () => new AttrTransferKitControl()); break;
            case "A7": OpenTab("complex", "商品税则号查询", () => new BasicAttrControls()); break;
            case "A8": OpenTab("sysdef", "系统定义", () => new SystemDefineControl()); break;
            default: OpenTab("dccoef", "分类码定义", () => new BasicAttrControls()); break;
        }
    }

    /// <summary>
    /// E 组万能查询族（第十一批）：统一组件一页覆盖 E1/E4/E5/E6/E7/E8/E9/EA 八页签。
    /// </summary>
    [RelayCommand]
    private void OpenUniversalQuery(string tabKey)
    {
        var (code, name) = tabKey switch
        {
            "E1" => ("E1", "进库单查询"),
            "E4" => ("E4", "订货单查询"),
            "E5" => ("E5", "库存修改查询"),
            "E6" => ("E6", "商品出入查询"),
            "E7" => ("E7", "月销售数查询"),
            "E8" => ("E8", "营业额查询"),
            "E9" => ("E9", "库存检测"),
            "EA" => ("EA", "进出流水帐查询"),
            _ => ("E1", "业务查询"),
        };
        if (!Guard(code, name)) return;
        OpenTab("universalQuery", "业务查询", () => new UniversalQueryControl());
    }

    /// <summary>散件入库（B1，M4-1）。</summary>
    [RelayCommand]
    private void OpenInStock()
    {
        if (!Guard("B1", "散件入库")) return;
        OpenTab("inStock", "散件入库", () => new InStockControl());
    }

    /// <summary>订单入库（B2，二期：fr_isor 契约，tbisorsm 草稿 + row220 确认）。</summary>
    [RelayCommand]
    private void OpenOrderIn()
    {
        if (!Guard("B2", "订单入库")) return;
        OpenTab("orderIn", "订单入库", () => new OrderInControl());
    }

    /// <summary>日营业统计（D1/D2，二期：fr_rtj/fr_hftj 契约，回放场景 11/12）。</summary>
    [RelayCommand]
    private void OpenDailyStat()
    {
        if (!Guard("D1", "日营业统计")) return;
        OpenTab("dailyStat", "日营业统计", () => new DailyStatControl());
    }

    /// <summary>统计报表（D3 日报表 / D4 月销售数统计 / D5 进销存分类统计，二期）。</summary>
    [RelayCommand]
    private void OpenReports()
    {
        if (!Guard("D3", "统计报表")) return;
        OpenTab("reports", "统计报表", () => new ReportsControl());
    }

    /// <summary>库存查询（E3，M4-2），含库存修改入口（B3，M4-3）。</summary>
    [RelayCommand]
    private void OpenStockQuery()
    {
        if (!Guard("E3", "库存查询")) return;
        OpenTab("stockQuery", "库存查询", () => new StockQueryControl());
    }

    /// <summary>库存盘点（B8，二期：fr_istopd 契约，回放场景 5）。</summary>
    [RelayCommand]
    private void OpenStocktake()
    {
        if (!Guard("B8", "库存盘点")) return;
        OpenTab("stocktake", "库存盘点", () => new StocktakeControl());
    }

    /// <summary>调拨管理（C3，二期：fr_sale('调拨') 契约，取号「调拨单」，回放场景 6）。</summary>
    [RelayCommand]
    private void OpenTransfer()
    {
        if (!Guard("C3", "调拨管理")) return;
        OpenTab("transfer", "调拨管理", () => new SaleControl("调拨"));
    }

    /// <summary>预售管理（C2，fr_sale('预售')：旧菜单 BAR 2「批售管理」权限码 C2，sale_in 同扣库存）。</summary>
    [RelayCommand]
    private void OpenPresale()
    {
        if (!Guard("C2", "预售管理")) return;
        OpenTab("presale", "预售管理", () => new SaleControl("预售"));
    }

    /// <summary>内耗管理（C4，fr_sale('内耗')：旧菜单 BAR 4 权限码 C4，sale_in 同扣库存；取号无'内耗单'前缀弹错照旧备案）。</summary>
    [RelayCommand]
    private void OpenNeihao()
    {
        if (!Guard("C4", "内耗管理")) return;
        OpenTab("neihao", "内耗管理", () => new SaleControl("内耗"));
    }

    /// <summary>报价管理（C5，fr_sale('报价')：开单 kcid=0 不占库存、AJ 前缀、可确认 FRMOK）。</summary>
    [RelayCommand]
    private void OpenQuote()
    {
        if (!Guard("C5", "报价管理")) return;
        OpenTab("quote", "报价管理", () => new SaleControl("报价"));
    }

    /// <summary>报价单查询（C5 随报价管理；确认/转销售/打印）。</summary>
    [RelayCommand]
    private void OpenQuoteQuery()
    {
        if (!Guard("C5", "报价单查询")) return;
        OpenTab("quoteQuery", "报价单查询", () => new QuoteQueryControl());
    }

    /// <summary>其他公司报价（fr_orsa，销售管理 BAR 9；旧权限码 C8；tbqtgs CRUD + 明细面板）。</summary>
    [RelayCommand]
    private void OpenOrsa()
    {
        if (!Guard("C8", "其他公司报价")) return;
        OpenTab("orsa", "其他公司报价", () => new OrsaControl());
    }

    /// <summary>调拨入库（C9，fr_dbrk：mark='Y' 待入库调拨单 → 负库存预演 → exec dbrk → 挂帐写 tbysyf；旧系统无主菜单入口，备案超集）。</summary>
    [RelayCommand]
    private void OpenDbrk()
    {
        if (!Guard("C9", "调拨入库")) return;
        OpenTab("dbrk", "调拨入库", () => new DbrkControl());
    }

    /// <summary>调仓管理（C7，二期：fr_sadc 契约，tbsadc + ISAPPE，回放场景 6）。</summary>
    [RelayCommand]
    private void OpenStockAdjust()
    {
        if (!Guard("C7", "调仓管理")) return;
        OpenTab("stockAdjust", "调仓管理", () => new StockAdjustControl());
    }

    /// <summary>入库退货管理（B.A，权限码 BA；fr_istk 契约，本批三期）。</summary>
    [RelayCommand]
    private void OpenPurchaseReturn()
    {
        if (!Guard("BA", "入库退货管理")) return;
        OpenTab("purchaseReturn", "入库退货管理", () => new PurchaseReturnControl());
    }

    /// <summary>G.2 收款 / G.3 付款（fr_xlsk，同一页面按类型复用）。</summary>
    [RelayCommand]
    private void OpenXlskReceipt()
    {
        if (!Guard("G2", "收款管理")) return;
        OpenTab("xlskSK", "收款管理", () => new XlskControl("收款"));
    }

    [RelayCommand]
    private void OpenXlskPayment()
    {
        if (!Guard("G3", "付款管理")) return;
        OpenTab("xlskFK", "付款管理", () => new XlskControl("付款"));
    }

    /// <summary>G.4 应收应付（fr_ysyf；2026-10-29 曾按用户要求对调，现按用户要求调回原接线）。</summary>
    [RelayCommand]
    private void OpenYsyf()
    {
        if (!Guard("G4", "应收应付")) return;
        OpenTab("ysyf", "应收应付", () => new YsyfControl());
    }

    /// <summary>G.5 收支流水帐（fr_lsz）。</summary>
    [RelayCommand]
    private void OpenLsz()
    {
        if (!Guard("G5", "收支流水帐")) return;
        OpenTab("lsz", "收支流水帐", () => new LszControl());
    }

    /// <summary>I.1 凭证录入（fr_finvouch，凭证核心闭环本批）。</summary>
    [RelayCommand]
    private void OpenFinVouch()
    {
        if (!Guard("I1", "凭证录入")) return;
        OpenTab("finvouch", "凭证录入", () => new FinVouchControl());
    }

    /// <summary>I.2 业务凭证（fr_finopvouch + fr_finoperation 生成，本批交付 BBI/SRO/SMO）。</summary>
    [RelayCommand]
    private void OpenFinOpVouch()
    {
        if (!Guard("I2", "业务凭证")) return;
        OpenTab("finopvouch", "业务凭证", () => new FinOpVouchControl());
    }

    /// <summary>I.9-1 会计科目（fr_finsub）。</summary>
    [RelayCommand]
    private void OpenFinSub()
    {
        if (!Guard("I91", "会计科目")) return;
        OpenTab("finsub", "会计科目", () => new FinSubControl());
    }

    /// <summary>I.9 基础资料组（fr_finmoney/fr_finsumary/fr_finset/fr_finEmplf/fr_finaccount）。</summary>
    [RelayCommand]
    private void OpenFinBasic()
    {
        if (!Guard("I9", "账务基础资料")) return;
        OpenTab("finbasic", "账务基础资料", () => new FinBasicControl());
    }

    /// <summary>I.3-4 结转损益（I34，fr_fincaforw → exec fincaforw）。</summary>
    [RelayCommand]
    private void OpenFinCaforw()
    {
        if (!Guard("I34", "结转损益")) return;
        OpenTab("fincaforw", "结转损益", () => new FinCaforwControl());
    }

    /// <summary>I.3-5 期末结帐（I35，fr_fincheout → exec getfincheout/finuncheout）。</summary>
    [RelayCommand]
    private void OpenFinCheout()
    {
        if (!Guard("I35", "期末结帐")) return;
        OpenTab("fincheout", "期末结帐", () => new FinCheoutControl());
    }

    /// <summary>N 组 帐务初始数据（BAR11，fr_fininit；旧菜单无 SKIP FOR 权限控制，对齐不设守卫）。</summary>
    [RelayCommand]
    private void OpenFinInit() => OpenTab("fininit", "帐务初始数据", () => new FinInitControl());

    /// <summary>N 组 帐套选项（BAR12，fr_finpara；同上不设守卫）。</summary>
    [RelayCommand]
    private void OpenFinPara() => OpenTab("finpara", "帐套选项", () => new FinParaControl());

    /// <summary>N 组 启用帐套（BAR13，fr_finuse；同上不设守卫）。</summary>
    [RelayCommand]
    private void OpenFinUse() => OpenTab("finuse", "启用帐套", () => new FinUseControl());

    /// <summary>I.4 五账（总账/明细账/数量金额账/核算项目账/多栏账，权限码 I4）。</summary>
    [RelayCommand]
    private void OpenFinLedger()
    {
        if (!Guard("I4", "账簿查询（五账）")) return;
        OpenTab("finledger", "账簿查询（五账）", () => new FinLedgerControl());
    }

    /// <summary>I.6 应收应付帐（权限码 I6，fr_finysyf；2026-10-29 曾对调，现调回原接线）。</summary>
    [RelayCommand]
    private void OpenFinYsyfLedger()
    {
        if (!Guard("I6", "应收应付帐")) return;
        OpenTab("finysyfledger", "应收应付帐", () => new FinYsyfLedgerControl());
    }

    /// <summary>I.7 出纳帐（权限码 I7，fr_finrnp）。</summary>
    [RelayCommand]
    private void OpenFinRnp()
    {
        if (!Guard("I7", "出纳帐")) return;
        OpenTab("finrnp", "出纳帐", () => new FinRnpControl());
    }

    /// <summary>I.5 报表中心（试算平衡/凭证汇总/损益/资产负债/现金流，I51~I57）。</summary>
    [RelayCommand]
    private void OpenFinReport()
    {
        if (!Guard("I51", "账务报表")) return;
        OpenTab("finreport", "账务报表", () => new FinReportControl());
    }

    /// <summary>I.8 辅助工具 + N.7/N.8 固定资产（I8/N7/N8）。</summary>
    [RelayCommand]
    private void OpenFinTool()
    {
        if (!Guard("I8", "账务辅助工具")) return;
        OpenTab("fintool", "账务辅助工具", () => new FinToolControl());
    }

    /// <summary>B4 库存调价（权限码 B4，fr_istotj）。</summary>
    [RelayCommand]
    private void OpenPriceAdjust()
    {
        if (!Guard("B4", "库存调价")) return;
        OpenTab("priceadjust", "库存调价", () => new PriceAdjustControl());
    }

    /// <summary>B5 警戒线设置（权限码 B5，fr_istojj）。</summary>
    [RelayCommand]
    private void OpenJjLine()
    {
        if (!Guard("B5", "警戒线设置")) return;
        OpenTab("jjline", "警戒线设置", () => new PriceAdjustControl());
    }

    /// <summary>每日动态盘点表（B.C，旧菜单权限码复用 BB；重建契约只读，本批三期）。</summary>
    [RelayCommand]
    private void OpenDailyStocktake()
    {
        if (!Guard("BB", "每日动态盘点表")) return;
        OpenTab("dailyStocktake", "每日动态盘点表", () => new DailyStocktakeControl());
    }

    /// <summary>提货管理（B.B，权限码 BB；fr_th 契约，本批三期）。</summary>
    [RelayCommand]
    private void OpenDelivery()
    {
        if (!Guard("BB", "提货管理")) return;
        OpenTab("delivery", "提货管理", () => new DeliveryControl());
    }

    /// <summary>订货公式（F.3，权限码 F3；fr_orderexp 契约）。</summary>
    [RelayCommand]
    private void OpenOrderExp()
    {
        if (!Guard("F3", "订货公式")) return;
        OpenTab("orderExp", "订货公式", () => new OrderExpControl());
    }

    /// <summary>自动订货（F.4，权限码 F4；fr_orderbud 契约）。</summary>
    [RelayCommand]
    private void OpenOrderBud()
    {
        if (!Guard("F4", "自动订货")) return;
        OpenTab("orderBud", "自动订货", () => new OrderBudControl());
    }

    /// <summary>供应商价格（F.5，权限码 F5；fr_gysjg 契约，表=tbgys）。</summary>
    [RelayCommand]
    private void OpenSupplierPrice()
    {
        if (!Guard("F5", "供应商价格")) return;
        OpenTab("supplierPrice", "供应商价格", () => new SupplierPriceControl());
    }

    /// <summary>库存上网（F.6，权限码 F6；fr_kc 契约，表=tbkc）。</summary>
    [RelayCommand]
    private void OpenWebStock()
    {
        if (!Guard("F6", "库存上网")) return;
        OpenTab("webStock", "库存上网", () => new WebStockControl());
    }

    /// <summary>订货库管理（F.2，权限码 F2；fr_order3 契约，type='历史' 工作集）。</summary>
    [RelayCommand]
    private void OpenOrder3()
    {
        if (!Guard("F2", "订货库管理")) return;
        OpenTab("order3", "订货库管理", () => new Order3Control());
    }

    /// <summary>订单生成（F.1，权限码 F1；fr_order4 契约，type='' 计划单）。</summary>
    [RelayCommand]
    private void OpenOrder4()
    {
        if (!Guard("F1", "订单生成")) return;
        OpenTab("order4", "订单生成", () => new Order4Control());
    }

    [RelayCommand]
    private void OpenDesktop() => OpenTab("desktop", "工作台", () => new DesktopControl(this));

    /// <summary>供应商管理（A6，M5）。</summary>
    [RelayCommand]
    private void OpenSuppliers()
    {
        if (!Guard("A6", "供应商管理")) return;
        OpenTab("suppliers", "供应商管理", () => new SupplierControl());
    }

    /// <summary>配件资料（A1 属性库查询/修改，M5）。</summary>
    [RelayCommand]
    private void OpenPartProfile()
    {
        if (!Guard("A1", "零件属性定义")) return;
        OpenTab("partProfile", "配件资料", () => new PartProfileControl());
    }

    /// <summary>单号前缀设置（A9，M5）。</summary>
    [RelayCommand]
    private void OpenDocNoPrefix()
    {
        if (!Guard("A9", "单号前缀设置")) return;
        OpenTab("docNoPrefix", "单号前缀设置", () => new DocNoPrefixControl());
    }

    [RelayCommand]
    private void NotImplemented()
        => MessageBox.Show("该功能按分期计划在后续里程碑交付（见开发文档 §4.2）。",
            "功能提示", MessageBoxButton.OK, MessageBoxImage.Information);

    [RelayCommand]
    private void CloseActiveTab() => Close(ActiveTab);

    [RelayCommand]
    private void Exit()
    {
        SignOutAndClose();
    }

    private void SignOutAndClose()
    {
        _clock.Stop();
        _health.Stop();
        AppSession.SignOut();
        RequestClose?.Invoke();
    }

    private async System.Threading.Tasks.Task CheckHealthAsync()
    {
        try
        {
            var result = await Db.Health.CheckAsync();
            IsConnected = result.Success;
            StatusConnection = result.Success ? "已连接" : "连接断开";
        }
        catch
        {
            IsConnected = false;
            StatusConnection = "连接断开";
        }
    }

    public void Dispose()
    {
        _clock.Stop();
        _health.Stop();
    }
}

/// <summary>WPF DispatcherTimer 薄封装（避免 VM 直接依赖 Dispatcher 类型的歧义）。</summary>
public sealed class DispatcherTimerEx : System.Windows.Threading.DispatcherTimer
{
}
