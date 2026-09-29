using System.Text.Json;
using Coglatas.Web.Middleware;
using Coglatas.Web.Models;
using Microsoft.AspNetCore.Http;

namespace Coglatas.Tests.Security;

[Trait("Scope", "WPC02E")]
public sealed class WpcFinal01VisibilityContractTests
{
    [Fact]
    public async Task VisibilityCommandUsesCanonicalWpcEnvelopeBoundary()
    {
        var projectId = Guid.NewGuid();
        var path = $"/api/projects/{projectId}/visibility";

        Assert.True(ApiEnvelope.IsWorkspaceCreationPath(path));
        Assert.True(ApiEnvelope.IsWorkspaceCreationPath(path + "/"));
        Assert.True(ApiEnvelope.IsProjectVisibilityPath(path));
        Assert.False(ApiEnvelope.IsProjectVisibilityPath("/api/projects/not-a-guid/visibility"));
        Assert.False(ApiEnvelope.IsProjectVisibilityPath($"/api/projects/{projectId}/visibility/extra"));

        var nextCalled = false;
        var middleware = new WpcApiContractMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "wpc-final01-visibility-415",
            Response = { Body = new MemoryStream() },
            Request =
            {
                Method = HttpMethods.Put,
                Path = path,
                ContentType = "text/plain"
            }
        };

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(httpContext.Response.Body);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, httpContext.Response.StatusCode);
        Assert.Equal(
            "UnsupportedMediaType",
            payload.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "wpc-final01-visibility-415",
            payload.RootElement.GetProperty("requestId").GetString());
    }
}
