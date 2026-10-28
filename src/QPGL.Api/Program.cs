using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using QPGL.Api;
using QPGL.Api.Auth;
using QPGL.Api.Middlewares;
using QPGL.Infrastructure.Auth;
using QPGL.Infrastructure.Db;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Partners;
using QPGL.Infrastructure.Parts;
using QPGL.Infrastructure.Printing;
using QPGL.Infrastructure.Sales;
using Serilog;

// Serilog（§9：登录/命令/断言失败落盘；滚动文件）
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/qpgl-api-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

    // ---- DI ----
    builder.Services.AddSingleton<IConnectionStringProvider>(sp =>
        new ConfigConnectionStringProvider(builder.Configuration));
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

    // ---- 启动库自检（D5：连库失败立即终止，不返回兜底数据）----
    var health = app.Services.GetRequiredService<DatabaseHealthChecker>();
    var check = await health.CheckAsync();
    if (!check.Success)
    {
        Log.Fatal("数据库自检失败（{Elapsed}ms）：{Error}。按约定拒绝启动——请检查连接配置与数据库状态。",
            check.ElapsedMs, check.Error);
        await Log.CloseAndFlushAsync();
        return 1;
    }
    Log.Information("数据库自检通过（{Elapsed}ms）", check.ElapsedMs);

    // ---- Pipeline ----
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseMiddleware<ApiExceptionMiddleware>();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    var url = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://0.0.0.0:5100";
    Log.Information("QPGL.Api 启动：{Url}", url);
    app.Run(url);

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "QPGL.Api 启动异常终止");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>连接串来自 appsettings（仅 API 持有，§6.9）。</summary>
internal sealed class ConfigConnectionStringProvider(IConfiguration config) : IConnectionStringProvider
{
    public string ConnectionString =>
        config.GetConnectionString("Default")
        ?? throw new InvalidOperationException("缺少连接串配置 ConnectionStrings:Default");
}
