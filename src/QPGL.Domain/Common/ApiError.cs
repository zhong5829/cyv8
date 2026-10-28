namespace QPGL.Domain.Common;

/// <summary>统一 API 错误（开发文档 §7.1/§7.2）。</summary>
public sealed record ApiError(string Code, string Message, string? Detail = null)
{
    /// <summary>构造业务错误码 + HTTP 状态码的错误。</summary>
    public static ApiError Of(string code, string message, string? detail = null) => new(code, message, detail);
}
