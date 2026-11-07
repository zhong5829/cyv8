using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using QPGL.Api;
using QPGL.Api.Auth;
using QPGL.Api.Middlewares;
using QPGL.Api.Printing;
using QPGL.Infrastructure.Auth;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Finance;
using QPGL.Infrastructure.Partners;
using QPGL.Infrastructure.Parts;
using QPGL.Infrastructure.Printing;
using QPGL.Infrastructure.Query;
using QPGL.Infrastructure.Receipt;
using QPGL.Infrastructure.Sales;
using QPGL.Infrastructure.Stock;
using Serilog;
using Serilog.Events;

namespace QPGL.Api;

/// <summary>
/// Web 端托管入口：封装 QPGL.Api 完整管线（配置/DI/JWT/中间件/控制器/wwwroot）。
/// 两种运行方式：
///   ① 独立进程：dotnet QPGL.Api.dll（Program.cs 调 RunStandaloneAsync）；
///   ② 进程内托管：桌面端工作台调 StartAsync/StopAsync（启动/停止/状态/在线人数）。
/// </summary>
public sealed class WebApiRunner
{
    private static IHost? _host;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>当前是否运行中。</summary>
    public static bool IsRunning => _host is not null;

    /// <summary>本次启动时间（未运行为 null）。</summary>
    public static DateTime? StartedAt { get; private set; }

    /// <summary>监听地址（实际绑定，如 http://0.0.0.0:5100）。</summary>
    public static string ListenUrl { get; private set; } = "";

    public sealed class StartOptions
    {
        /// <summary>内容根目录（appsettings/wwwroot/logs 所在目录）；null = 程序目录。</summary>
        public string? ContentRoot { get; init; }

        /// <summary>配置文件名；null = 优先 appsettings.web.json（桌面端托管），否则 appsettings.json。</summary>
        public string? SettingsFile { get; init; }

        /// <summary>连接串覆盖（桌面端传入当前所选库，保证 Web 端与桌面端同一库）；null = 读配置文件。</summary>
        public string? ConnectionString { get; init; }

        /// <summary>监听地址；null = 配置 Kestrel:Endpoints:Http:Url，默认 http://0.0.0.0:5100。</summary>
        public string? Url { get; init; }

        /// <summary>桌面端静默打印桥（进程内托管时由桌面端注入；null = 不可用，打印接口明确报错）。</summary>
        public IDesktopPrintBridge? PrintBridge { get; init; }
    }

    /// <summary>启动 Web 端（非阻塞：监听就绪后即返回，宿主在后台运行）。启动失败抛异常（库自检/端口占用等）。</summary>
    public static async Task StartAsync(StartOptions? options = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_host is not null) throw new InvalidOperationException("Web 端已在运行中");
            options ??= new StartOptions();
            var contentRoot = Path.GetFullPath(options.ContentRoot ?? AppContext.BaseDirectory);
            var settingsFile = options.SettingsFile
                ?? (File.Exists(Path.Combine(contentRoot, "appsettings.web.json")) ? "appsettings.web.json" : "appsettings.json");

            // ---- 连接串提供者（覆盖优先：桌面端托管传当前所选库）----
            IConnectionStringProvider provider = !string.IsNullOrWhiteSpace(options.ConnectionString)
                ? new FixedConnectionStringProvider(options.ConnectionString)
                : new ConfigConnectionStringProvider(LoadConfiguration(settingsFile, contentRoot));

            // ---- 启动库自检（D5：连库失败立即终止，不返回兜底数据）----
            // 注意：必须 await（桌面端从 UI 线程调用，同步阻塞会死锁 WPF Dispatcher）
            var health = new DatabaseHealthChecker(provider);
            var check = await health.CheckAsync(ct);
            if (!check.Success)
                throw new InvalidOperationException($"数据库自检失败（{check.ElapsedMs}ms）：{check.Error}。请检查连接配置与数据库状态。");
            Log.Information("数据库自检通过（{Elapsed}ms）", check.ElapsedMs);

            var app = BuildHost(contentRoot, settingsFile, provider, options.Url, options.PrintBridge);
            await app.StartAsync(ct);
            _host = app;
            StartedAt = DateTime.Now;
            Log.Information("QPGL.Api 已启动：{Url}", ListenUrl);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>停止 Web 端（优雅关闭，释放监听端口）。</summary>
    public static async Task StopAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_host is null) return;
            await _host.StopAsync(ct);
            if (_host is IAsyncDisposable ad) await ad.DisposeAsync();
            else _host.Dispose();
            _host = null;
            StartedAt = null;
            Log.Information("QPGL.Api 已停止");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>独立运行入口（阻塞直到 Ctrl+C/终止；返回进程退出码）。</summary>
    public static async Task<int> RunStandaloneAsync(CancellationToken ct = default)
    {
        try
        {
            await StartAsync(new StartOptions(), ct);
            await (_host?.WaitForShutdownAsync(ct) ?? Task.CompletedTask);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "QPGL.Api 启动异常终止");
            return 1;
        }
    }

    // ------------------------------------------------------------------

    private static IConfiguration LoadConfiguration(string settingsFile, string contentRoot) =>
        new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile(settingsFile, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

    private static WebApplication BuildHost(string contentRoot, string settingsFile, IConnectionStringProvider provider, string? urlOverride,
        IDesktopPrintBridge? printBridge = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            ApplicationName = "QPGL.Api",
        });
        // 自定义配置源：桌面端进程内托管用 appsettings.web.json（避免与桌面 appsettings.json 冲突）
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonFile(settingsFile, optional: false, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();

        // Serilog（§9：登录/命令/断言失败落盘；滚动文件）——两种运行方式统一在代码中配置
        builder.Host.UseSerilog((_, cfg) => cfg
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(contentRoot, "logs", "qpgl-api-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"));

        // ---- DI ----
        builder.Services.AddSingleton<IConnectionStringProvider>(provider);
        builder.Services.AddSingleton<SqlConnectionFactory>();
        builder.Services.AddSingleton<DatabaseHealthChecker>();
        builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
        builder.Services.AddScoped<IUserRepository, UserRepository>();
        builder.Services.AddScoped<IPartnerRepository, PartnerRepository>();
        builder.Services.AddScoped<IPartRepository, PartRepository>();
        builder.Services.AddScoped<IDocNoService, DocNoService>();
        builder.Services.AddScoped<ISaleRepository, SaleRepository>();
        builder.Services.AddScoped<ISaleReturnRepository, SaleReturnRepository>();
        builder.Services.AddScoped<IPrintRepository, PrintRepository>();
        builder.Services.AddScoped<IPrintSettingsRepository, PrintSettingsRepository>();
        builder.Services.AddScoped<IDocQueryRepository, DocQueryRepository>();
        builder.Services.AddScoped<IReceiptRepository, ReceiptRepository>();   // 整单确认：付款方式字典
        builder.Services.AddScoped<IStockRepository, StockRepository>();       // 整单确认：发货方式/销售类型字典
        builder.Services.AddScoped<IXlskRepository, XlskRepository>();         // 整单确认：客户余额
        // Web 端静默打印：桌面进程内托管时由桌面端注入实现；独立部署给不可用占位（接口明确报错）
        builder.Services.AddSingleton<IDesktopPrintBridge>(printBridge ?? UnavailableDesktopPrintBridge.Instance);

        // JWT 认证（§9：8h 有效期）
        var jwtSection = builder.Configuration.GetSection("Jwt");
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;   // 保留原始 sub/dept/usem claim（禁默认入站映射）
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtSection["Issuer"],
                    ValidateAudience = true,
                    ValidAudience = jwtSection["Audience"],
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["Key"]!)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });
        builder.Services.AddAuthorization();

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        // ---- 监听地址 ----
        ListenUrl = urlOverride
            ?? builder.Configuration["Kestrel:Endpoints:Http:Url"]
            ?? "http://0.0.0.0:5100";
        app.Urls.Clear();
        app.Urls.Add(ListenUrl);

        // ---- Pipeline ----
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseDefaultFiles();   // wwwroot/index.html 作为首页（Web 端：销售开单/查询/打印）
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        // 在线会话跟踪：认证后的每个请求刷新操作员 lastSeen（工作台「在线人数」数据源）
        app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
                OnlineSessionTracker.Instance.Touch(ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "");
            await next();
        });
        app.MapControllers();

        return app;
    }
}

/// <summary>连接串来自构造参数（桌面端托管：与桌面端同一所选库）。</summary>
public sealed class FixedConnectionStringProvider(string connectionString) : IConnectionStringProvider
{
    public string ConnectionString { get; } = connectionString;
}

/// <summary>连接串来自 appsettings（仅独立运行时使用，§6.9）。</summary>
public sealed class ConfigConnectionStringProvider(IConfiguration config) : IConnectionStringProvider
{
    public string ConnectionString =>
        config.GetConnectionString("Default")
        ?? throw new InvalidOperationException("缺少连接串配置 ConnectionStrings:Default");
}
