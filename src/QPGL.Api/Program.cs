using QPGL.Api;
using Serilog;

// 独立运行入口（开发调试/服务器单独部署）：dotnet QPGL.Api.dll
// 进程内托管（桌面端工作台启动/停止）走 WebApiRunner.StartAsync/StopAsync。
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    return await WebApiRunner.RunStandaloneAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
