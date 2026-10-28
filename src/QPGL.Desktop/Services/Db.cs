using System.IO;
using Microsoft.Extensions.Configuration;
using QPGL.Infrastructure.Auth;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Finance;
using QPGL.Infrastructure.FinanceConfirm;
using QPGL.Infrastructure.Orders;
using QPGL.Infrastructure.Partners;
using QPGL.Infrastructure.Parts;
using QPGL.Infrastructure.Printing;
using QPGL.Infrastructure.Query;
using QPGL.Infrastructure.Receipt;
using QPGL.Infrastructure.Report;
using QPGL.Infrastructure.Sales;
using QPGL.Infrastructure.Stock;

namespace QPGL.Desktop.Services;

/// <summary>
/// 直连数据服务（开发文档 §3.1 直连架构 / §8.1 Services/Db）：
/// 客户端启动时初始化——读本机连接串 → 连接工厂/自检/各仓储单例。
/// 仓储无状态可作单例；ViewModel 一切数据操作经由本类持有的仓储（禁止直持 SqlConnection）。
/// 连接串安全：现阶段明文 appsettings.json（开发期），发布版按 §9 用 DPAPI(CurrentUser) 加密为密文。
/// </summary>
public static class Db
{
    public static SqlConnectionFactory Factory { get; private set; } = null!;
    public static DatabaseHealthChecker Health { get; private set; } = null!;
    public static IUserRepository Users { get; private set; } = null!;
    public static IBrandRepository Brands { get; private set; } = null!;
    public static IPartnerRepository Partners { get; private set; } = null!;
    public static IPartRepository Parts { get; private set; } = null!;
    public static IDocNoService DocNo { get; private set; } = null!;
    public static ISaleRepository Sales { get; private set; } = null!;
    public static ISaleReturnRepository SaleReturns { get; private set; } = null!;
    public static IPrintRepository Print { get; private set; } = null!;
    public static IPrintSettingsRepository PrintSettings { get; private set; } = null!;
    public static IPermissionRepository Permissions { get; private set; } = null!;
    public static IDocQueryRepository DocQuery { get; private set; } = null!;
    public static IReceiptRepository Receipts { get; private set; } = null!;
    public static IFinConfirmRepository FinConfirm { get; private set; } = null!;
    public static IInStockRepository InStock { get; private set; } = null!;
    public static IStockRepository Stock { get; private set; } = null!;
    public static IStockEditRepository StockEdit { get; private set; } = null!;
    public static IPartnerManageRepository PartnerManage { get; private set; } = null!;
    public static ISupplierRepository Suppliers { get; private set; } = null!;
    public static IPartProfileRepository PartProfiles { get; private set; } = null!;
    public static IDocNoPrefixRepository DocNoPrefix { get; private set; } = null!;
    public static IStocktakeRepository Stocktake { get; private set; } = null!;
    public static IStockAdjustRepository StockAdjust { get; private set; } = null!;
    public static IStockDistributeRepository StockDistribute { get; private set; } = null!;
    public static IDbrkRepository Dbrk { get; private set; } = null!;
    public static IOrderInRepository OrderIn { get; private set; } = null!;
    public static IStatisticsRepository Statistics { get; private set; } = null!;
    public static IReportRepository Reports { get; private set; } = null!;
    public static IPurchaseReturnRepository PurchaseReturns { get; private set; } = null!;
    public static IDailyStocktakeRepository DailyStocktake { get; private set; } = null!;
    public static IDeliveryRepository Delivery { get; private set; } = null!;
    public static IOrderExpRepository OrderExp { get; private set; } = null!;
    public static ISupplierPriceRepository SupplierPrices { get; private set; } = null!;
    public static IWebStockRepository WebStock { get; private set; } = null!;
    public static IOrderBudRepository OrderBud { get; private set; } = null!;
    public static IOrder3Repository Order3 { get; private set; } = null!;
    public static IOrder4Repository Order4 { get; private set; } = null!;
    public static IXlskRepository Xlsk { get; private set; } = null!;
    public static IYsyfRepository Ysyf { get; private set; } = null!;
    public static ILszRepository Lsz { get; private set; } = null!;
    public static IFinSubRepository FinSub { get; private set; } = null!;
    public static IFinBasicRepository FinBasic { get; private set; } = null!;
    public static IFinVouchRepository FinVouch { get; private set; } = null!;
    public static IFinOpVouchRepository FinOp { get; private set; } = null!;
    public static IFinClosingRepository FinClosing { get; private set; } = null!;
    public static IFinLedgerRepository FinLedger { get; private set; } = null!;
    public static IFinReportRepository FinReport { get; private set; } = null!;
    public static IPriceAdjustRepository PriceAdjust { get; private set; } = null!;
    public static IUniversalQueryRepository UniversalQuery { get; private set; } = null!;
    public static IPrnotyRepository Prnoty { get; private set; } = null!;
    public static INameCodeRepository NameCode { get; private set; } = null!;
    public static IAttrTransferRepository AttrTransfer { get; private set; } = null!;
    public static IKitRepository Kit { get; private set; } = null!;
    public static IComplexRepository Complex { get; private set; } = null!;
    public static IDcCoefRepository DcCoef { get; private set; } = null!;
    public static ISysDefineRepository SysDefine { get; private set; } = null!;
    public static IOrsaRepository Orsa { get; private set; } = null!;
    public static IDmService Dm { get; private set; } = null!;

    public static bool Initialized { get; private set; }

    public static void Init()
    {
        if (Initialized) return;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("缺少连接串配置 ConnectionStrings:Default（appsettings.json）");

        var provider = new StaticConnectionStringProvider(connectionString);
        Factory = new SqlConnectionFactory(provider);
        Dm = new DmService();
        Health = new DatabaseHealthChecker(provider);
        Users = new UserRepository(Factory);
        Partners = new PartnerRepository(Factory);
        Parts = new PartRepository(Factory);
        DocNo = new DocNoService(Factory);
        Sales = new SaleRepository(Factory, DocNo);
        SaleReturns = new SaleReturnRepository(Factory, DocNo);
        Print = new PrintRepository(Factory);
        PrintSettings = new PrintSettingsRepository(Factory);
        Permissions = new PermissionRepository(Factory);
        Brands = new BrandRepository(Factory);
        DocQuery = new DocQueryRepository(Factory);
        Receipts = new ReceiptRepository(Factory);
        FinConfirm = new FinConfirmRepository(Factory);
        InStock = new InStockRepository(Factory, Dm, DocNo);
        Stock = new StockRepository(Factory);
        StockEdit = new StockEditRepository(Factory);
        PartnerManage = new PartnerManageRepository(Factory);
        Suppliers = new SupplierRepository(Factory);
        PartProfiles = new PartProfileRepository(Factory);
        DocNoPrefix = new DocNoPrefixRepository(Factory);
        Stocktake = new StocktakeRepository(Factory, DocNo);
        StockAdjust = new StockAdjustRepository(Factory);
        StockDistribute = new StockDistributeRepository(Factory);
        Dbrk = new DbrkRepository(Factory);
        OrderIn = new OrderInRepository(Factory, DocNo, Dm);
        Statistics = new StatisticsRepository(Factory);
        Reports = new ReportRepository(Factory);
        PurchaseReturns = new PurchaseReturnRepository(Factory, DocNo);
        DailyStocktake = new DailyStocktakeRepository(Factory);
        Delivery = new DeliveryRepository(Factory);
        OrderExp = new OrderExpRepository(Factory);
        SupplierPrices = new SupplierPriceRepository(Factory);
        WebStock = new WebStockRepository(Factory);
        OrderBud = new OrderBudRepository(Factory);
        Order3 = new Order3Repository(Factory, Dm);
        Order4 = new Order4Repository(Factory);
        Xlsk = new XlskRepository(Factory, DocNo);
        Ysyf = new YsyfRepository(Factory);
        Lsz = new LszRepository(Factory);
        FinSub = new FinSubRepository(Factory);
        FinBasic = new FinBasicRepository(Factory);
        FinVouch = new FinVouchRepository(Factory);
        FinOp = new FinOpVouchRepository(Factory);
        FinClosing = new FinClosingRepository(Factory);
        FinLedger = new FinLedgerRepository(Factory);
        FinReport = new FinReportRepository(Factory);
        PriceAdjust = new PriceAdjustRepository(Factory);
        UniversalQuery = new UniversalQueryRepository(Factory);
        Prnoty = new PrnotyRepository(Factory, Dm);
        NameCode = new NameCodeRepository(Factory, Dm);
        AttrTransfer = new AttrTransferRepository(Factory);
        Kit = new KitRepository(Factory, Dm);
        Complex = new ComplexRepository(Factory);
        DcCoef = new DcCoefRepository(Factory);
        SysDefine = new SysDefineRepository(Factory);
        Orsa = new OrsaRepository(Factory);

        Initialized = true;
    }

    private sealed class StaticConnectionStringProvider(string connectionString) : IConnectionStringProvider
    {
        public string ConnectionString { get; } = connectionString;
    }
}
