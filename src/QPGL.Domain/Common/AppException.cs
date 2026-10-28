namespace QPGL.Domain.Common;

/// <summary>
/// 业务异常：携带 API 错误码与 HTTP 状态码（§7.2 错误码表）。
/// 中间件统一转成 ApiResponse 失败结构。
/// </summary>
public class AppException : Exception
{
    public string Code { get; }
    public int HttpStatus { get; }

    public AppException(string code, string message, int httpStatus = 422, string? detail = null)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
        Detail = detail;
    }

    public string? Detail { get; }

    public ApiError ToError() => new(Code, Message, Detail);
}
