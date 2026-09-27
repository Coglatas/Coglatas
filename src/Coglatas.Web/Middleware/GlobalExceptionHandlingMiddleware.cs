using Coglatas.Web.Models;

namespace Coglatas.Web.Middleware;

public sealed class GlobalExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (BadHttpRequestException exception) when (
            exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            logger.LogWarning(
                "Rejected oversized request body. TraceId: {TraceId}",
                context.TraceIdentifier);
            await RequestBodyLimitMiddleware.WriteTooLargeResponseAsync(context);
        }
        catch (BadHttpRequestException exception) when (
            exception.StatusCode is >= StatusCodes.Status400BadRequest and < StatusCodes.Status500InternalServerError)
        {
            logger.LogWarning(
                "Rejected malformed request body with status {StatusCode}. TraceId: {TraceId}",
                exception.StatusCode,
                context.TraceIdentifier);
            await WriteClientRequestErrorAsync(context, exception.StatusCode);
        }
        catch (InvalidDataException) when (IsFormRequest(context.Request))
        {
            logger.LogWarning(
                "Rejected malformed form request body. TraceId: {TraceId}",
                context.TraceIdentifier);
            await WriteClientRequestErrorAsync(context, StatusCodes.Status400BadRequest);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled request exception. TraceId: {TraceId}", context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            if (IsWpcPath(context.Request.Path.Value))
            {
                await context.Response.WriteAsJsonAsync(ApiEnvelope.Error(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "UnexpectedServerError",
                    "An unexpected server error occurred."));
            }
            else if (IsPr06Path(context.Request.Path.Value))
            {
                var dependency =
                    context.Request.Path.Value?.Contains(
                        "/dependencies",
                        StringComparison.OrdinalIgnoreCase) == true;
                var snapshot = IsPr06SnapshotPath(context.Request.Path.Value);
                await context.Response.WriteAsJsonAsync(new
                {
                    requestId = context.TraceIdentifier,
                    error = new
                    {
                        code = dependency
                            ? "TASK_DEPENDENCY_COMMAND_FAILED"
                            : snapshot
                                ? "GANTT_REQUEST_FAILED"
                                : "GANTT_COMMAND_FAILED",
                        message = snapshot
                            ? "The schedule could not be loaded."
                            : "The command could not be completed.",
                        target = (string?)null,
                        details = Array.Empty<object>(),
                        redactionApplied = false
                    }
                });
            }
            else
            {
                await context.Response.WriteAsJsonAsync(new ErrorResponse(
                    "InternalServerError",
                    "An unexpected server error occurred.",
                    context.TraceIdentifier));
            }
        }
    }

    private static Task WriteClientRequestErrorAsync(HttpContext context, int statusCode)
    {
        var unsupportedMediaType = statusCode == StatusCodes.Status415UnsupportedMediaType;
        context.Response.StatusCode = unsupportedMediaType
            ? StatusCodes.Status415UnsupportedMediaType
            : StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new ErrorResponse(
            unsupportedMediaType ? "UnsupportedMediaType" : "InvalidRequest",
            unsupportedMediaType
                ? "The request Content-Type is not supported."
                : "The request body is invalid.",
            context.TraceIdentifier));
    }

    private static bool IsFormRequest(HttpRequest request) =>
        request.HasFormContentType ||
        request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true ||
        request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPr06Path(string? path) =>
        IsPr06SnapshotPath(path) ||
        IsPr06CommandPath(path);

    private static bool IsWpcPath(string? path)
    {
        return ApiEnvelope.IsCanonicalCreatePath(path);
    }

    private static bool IsPr06SnapshotPath(string? path) =>
        NormalizePath(path).StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase) &&
        NormalizePath(path).EndsWith("/gantt", StringComparison.OrdinalIgnoreCase);

    private static bool IsPr06CommandPath(string? path) =>
        NormalizePath(path).StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase) &&
        (NormalizePath(path).EndsWith("/schedule", StringComparison.OrdinalIgnoreCase) ||
         NormalizePath(path).EndsWith("/progress", StringComparison.OrdinalIgnoreCase) ||
         NormalizePath(path).Contains("/dependencies", StringComparison.OrdinalIgnoreCase));

    private static string NormalizePath(string? path) =>
        path?.TrimEnd('/') ?? string.Empty;
}
