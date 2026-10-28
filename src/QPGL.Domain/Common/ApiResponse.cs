namespace QPGL.Domain.Common;

/// <summary>统一响应包装：{ ok, data, error }（开发文档 §7.1）。</summary>
public sealed class ApiResponse<T>
{
    public bool Ok { get; init; }
    public T? Data { get; init; }
    public ApiError? Error { get; init; }

    public static ApiResponse<T> Success(T data) => new() { Ok = true, Data = data };
    public static ApiResponse<T> Fail(ApiError error) => new() { Ok = false, Error = error };
}
