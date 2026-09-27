using System.Text.Json;
using Coglatas.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coglatas.Tests.Auth;

public sealed class GlobalExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task UnhandledExceptionResponseDoesNotExposeExceptionDetails()
    {
        var middleware = new GlobalExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("Password=leaked; C:\\internal\\path; SELECT * FROM Users"),
            NullLogger<GlobalExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-for-test",
            Response =
            {
                Body = new MemoryStream()
            }
        };

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(context.Response.Body);
        var root = payload.RootElement;

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("InternalServerError", root.GetProperty("code").GetString());
        Assert.Equal("An unexpected server error occurred.", root.GetProperty("message").GetString());
        Assert.Equal("trace-for-test", root.GetProperty("traceId").GetString());

        var body = root.GetRawText();
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\internal\\path", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leaked", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task GanttSnapshotExceptionUsesSafePr06Envelope()
    {
        var middleware = new GlobalExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("SELECT hidden schedule"),
            NullLogger<GlobalExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "gantt-trace",
            Response = { Body = new MemoryStream() }
        };
        context.Request.Path = "/api/projects/00000000-0000-0000-0000-000000000001/gantt/";

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(context.Response.Body);
        var root = payload.RootElement;
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("gantt-trace", root.GetProperty("requestId").GetString());
        Assert.Equal("GANTT_REQUEST_FAILED", root.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("SELECT", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task WorkspaceCreateExceptionUsesCompleteSafeEnvelope()
    {
        var middleware = new GlobalExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("Password=leaked; SELECT tenant secrets"),
            NullLogger<GlobalExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "wpc01-exception-request",
            Response = { Body = new MemoryStream() }
        };
        context.Request.Path = "/api/workspaces";

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(context.Response.Body);
        var root = payload.RootElement;
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("wpc01-exception-request", root.GetProperty("requestId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        var error = root.GetProperty("error");
        Assert.Equal("UnexpectedServerError", error.GetProperty("code").GetString());
        Assert.Equal(0, error.GetProperty("details").GetArrayLength());
        Assert.False(error.GetProperty("redactionApplied").GetBoolean());
        Assert.DoesNotContain("Password", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task RequestAbortCancellationPropagatesWithoutWritingAnErrorResponse()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationToken = cancellation.Token;
        var middleware = new GlobalExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException(cancellationToken),
            NullLogger<GlobalExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext
        {
            RequestAborted = cancellationToken,
            Response = { Body = new MemoryStream() }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }
}
