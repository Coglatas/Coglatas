namespace Coglatas.Application.Common;

public sealed record ApplicationErrorDetail(
    string Code,
    string Message,
    int? RetryAfterSeconds = null,
    string? Target = null);

public sealed record Result(bool IsSuccess, string? Error = null, ApplicationErrorDetail? ErrorDetail = null)
{
    public static Result Success() => new(true);

    public static Result Failure(string error) => new(false, error);

    public static Result Failure(string error, ApplicationErrorDetail errorDetail) => new(false, error, errorDetail);

    public static Result Failure(ApplicationErrorDetail error) => new(false, $"{error.Code}|{error.Message}", error);
}

public sealed record Result<T>(bool IsSuccess, T? Value = default, string? Error = null, ApplicationErrorDetail? ErrorDetail = null)
{
    public static Result<T> Success(T value) => new(true, value);

    public static Result<T> Failure(string error) => new(false, default, error);

    public static Result<T> Failure(string error, ApplicationErrorDetail errorDetail) => new(false, default, error, errorDetail);

    public static Result<T> Failure(ApplicationErrorDetail error) => new(false, default, $"{error.Code}|{error.Message}", error);
}
