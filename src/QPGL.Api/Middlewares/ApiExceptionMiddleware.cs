using System.Text.Json;
using QPGL.Domain.Common;

namespace QPGL.Api.Middlewares;

/// <summary>
/// 统一异常出口：AppException → ApiResponse 失败结构；
/// 未预期异常 → CONTRACT_VIOLATION/INTERNAL，记 Serilog，不泄漏堆栈。
/// </summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            logger.LogWarning("业务拒绝 {Code}: {Message}", ex.Code, ex.Message);
            await WriteAsync(context, ex.HttpStatus, ex.ToError());
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端取消（§10.4 全链路取消支持）：不打错误日志
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "未处理异常 {Path} {Type}", context.Request.Path, ex.GetType().Name);
            await WriteAsync(context, 500, new ApiError("INTERNAL", "服务器内部错误",
                context.Request.Host.Host.Contains("localhost")
                    ? $"{ex.GetType().Name}: {ex.Message}"
                    : null));
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, ApiError error)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = ApiResponse<object>.Fail(error);
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}
