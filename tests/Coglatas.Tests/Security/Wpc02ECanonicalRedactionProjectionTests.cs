using System.Text.Json;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Security.Redaction;
using Coglatas.Domain.Enums;
using Coglatas.Web.Middleware;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.Security;

[Trait("Scope", "WPC02E")]
public sealed class Wpc02ECanonicalRedactionProjectionTests
{
    [Theory]
    [InlineData(RedactionProfile.UiList)]
    [InlineData(RedactionProfile.UiDetail)]
    [InlineData(RedactionProfile.SearchSnippet)]
    [InlineData(RedactionProfile.ExportRow)]
    [InlineData(RedactionProfile.AuditDisplay)]
    [InlineData(RedactionProfile.NotificationPayload)]
    [InlineData(RedactionProfile.FileMetadata)]
    public void AuthorizedProjection_UsesTheDeclaredCanonicalProfile(
        RedactionProfile profile)
    {
        var redactor = new RecordingRedactionService();
        var httpContext = CreateHttpContext(redactor);
        var source = new ProjectionProbe("authorized output");

        var projected = CanonicalRedactionProjection.Apply(
            httpContext,
            source,
            profile,
            "Wpc02ETest",
            RedactionAuthorizationState.Allowed);

        Assert.Same(source, projected);
        Assert.Equal(profile, redactor.Profile);
        Assert.NotNull(redactor.Context);
        Assert.Equal(RedactionAuthorizationState.Allowed, redactor.Context!.AuthorizationState);
        Assert.Equal("Wpc02ETest", redactor.Context.ModuleKey);
        Assert.Equal(RedactionPurpose.NormalOperation, redactor.Context.Purpose);
        Assert.NotNull(redactor.Context.ActorId);
        Assert.NotNull(redactor.Context.TenantId);
        Assert.Equal(httpContext.TraceIdentifier, redactor.Context.RequestId);
        Assert.True(redactor.Context.FieldAccessPolicy.Allows(
            CanonicalDataClassification.Internal,
            "safeField"));
        Assert.False(redactor.Context.FieldAccessPolicy.Allows(
            CanonicalDataClassification.Confidential,
            "confidentialField"));
    }

    [Fact]
    public void AuthorizedProjection_PropagatesExplicitFieldAccessPolicy()
    {
        var redactor = new RecordingRedactionService();
        var httpContext = CreateHttpContext(redactor);
        var source = new ProjectionProbe("authorized confidential output");
        var fieldAccessPolicy = FieldAccessPolicySnapshot.ThroughConfidential;

        var projected = CanonicalRedactionProjection.Apply(
            httpContext,
            source,
            RedactionProfile.UiDetail,
            "Wpc02ETest",
            RedactionAuthorizationState.Allowed,
            fieldAccessPolicy: fieldAccessPolicy);

        Assert.Same(source, projected);
        Assert.NotNull(redactor.Context);
        Assert.Same(fieldAccessPolicy, redactor.Context!.FieldAccessPolicy);
        Assert.True(redactor.Context.FieldAccessPolicy.Allows(
            CanonicalDataClassification.Confidential,
            "confidentialField"));
        Assert.False(redactor.Context.FieldAccessPolicy.Allows(
            CanonicalDataClassification.Restricted,
            "healthNotes"));
    }

    [Fact]
    public void AuthorizedProjection_FailsClosed_WhenTheCanonicalServiceCannotReturnTheEndpointContract()
    {
        var httpContext = CreateHttpContext(new ClosedRedactionService());
        var source = new ProjectionProbe("must not pass through");

        Assert.Throws<InvalidOperationException>(() =>
            CanonicalRedactionProjection.Apply(
                httpContext,
                source,
                RedactionProfile.UiDetail,
                "Wpc02ETest",
                RedactionAuthorizationState.Allowed));
    }

    [Fact]
    public void AuthorizedProjection_FailsClosed_WhenTheCanonicalServiceIsMissing()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentUser>(new TestCurrentUser(Guid.NewGuid()))
            .AddSingleton<ICurrentTenant>(new TestCurrentTenant(Guid.NewGuid()))
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "wpc02e-no-redactor",
            RequestServices = services
        };
        var source = new ProjectionProbe("must not pass through");

        Assert.Throws<InvalidOperationException>(() =>
            CanonicalRedactionProjection.Apply(
                httpContext,
                source,
                RedactionProfile.UiDetail,
                "Wpc02ETest",
                RedactionAuthorizationState.Allowed));
    }

    [Fact]
    public void AuthorizedProjection_FailsClosed_WhenCurrentUserIsMissing()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenant>(new TestCurrentTenant(Guid.NewGuid()))
            .AddSingleton<IRedactionService, CanonicalRedactionService>()
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };

        Assert.Throws<InvalidOperationException>(() =>
            CanonicalRedactionProjection.Apply(
                httpContext,
                new ProjectionProbe("must not pass through"),
                RedactionProfile.UiDetail,
                "Wpc02ETest",
                RedactionAuthorizationState.Allowed));
    }

    [Fact]
    public void AuthorizedProjection_FailsClosed_WhenTenantContextIsMissing()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentUser>(new TestCurrentUser(Guid.NewGuid()))
            .AddSingleton<IRedactionService, CanonicalRedactionService>()
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };

        Assert.Throws<InvalidOperationException>(() =>
            CanonicalRedactionProjection.Apply(
                httpContext,
                new ProjectionProbe("must not pass through"),
                RedactionProfile.UiDetail,
                "Wpc02ETest",
                RedactionAuthorizationState.Allowed));
    }

    [Fact]
    public void UnknownAuthorizationState_FailsClosed()
    {
        var httpContext = CreateHttpContext(new CanonicalRedactionService());
        var source = new ProjectionProbe("must not pass through");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CanonicalRedactionProjection.Apply(
                httpContext,
                source,
                RedactionProfile.UiDetail,
                "Wpc02ETest",
                RedactionAuthorizationState.Unknown));

        Assert.Contains("endpoint-compatible", exception.Message);
    }

    [Fact]
    public void SensitiveResultError_UsesTheCanonicalErrorResponseProfile()
    {
        var httpContext = CreateHttpContext(new CanonicalRedactionService());

        var envelope = CanonicalErrorEnvelope.FromResult(
            httpContext,
            StatusCodes.Status400BadRequest,
            detail: null,
            fallbackError: "private search failure",
            fallbackCode: "SearchFailed");

        Assert.Equal("SearchFailed", envelope.Error.Code);
        Assert.Equal("The request could not be completed.", envelope.Error.Message);
        Assert.Null(envelope.Error.Target);
        Assert.Empty(envelope.Error.Details);
        Assert.True(envelope.Error.RedactionApplied);
    }

    [Theory]
    [InlineData("/api/workspaces")]
    [InlineData("/api/workspaces/00000000-0000-0000-0000-000000000001/projects")]
    [InlineData("/api/projects/00000000-0000-0000-0000-000000000001/activate")]
    public async Task WpcContractMiddleware_UsesTheCanonicalErrorEnvelopeForEveryWpcCommandRoute(
        string path)
    {
        var nextCalled = false;
        var middleware = new WpcApiContractMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "wpc02e-415",
            Response = { Body = new MemoryStream() },
            Request =
            {
                Method = HttpMethods.Post,
                Path = path,
                ContentType = "text/plain"
            }
        };

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(httpContext.Response.Body);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, httpContext.Response.StatusCode);
        Assert.Equal("wpc02e-415", payload.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(415, payload.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "UnsupportedMediaType",
            payload.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task WpcContractMiddleware_DelegatesPostForGetOnlyCapabilitiesRoute()
    {
        var nextCalled = false;
        var middleware = new WpcApiContractMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var httpContext = new DefaultHttpContext
        {
            Request = { Method = HttpMethods.Post Path = "/api/workspaces/capabilities" ContentType = "text/plain" }
        };

        await middleware.InvokeAsync(httpContext);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
    }

    private static DefaultHttpContext CreateHttpContext(IRedactionService redactor)
    {
        var services = new ServiceCollection()
            .AddSingleton(redactor)
            .AddSingleton<ICurrentUser>(new TestCurrentUser(Guid.NewGuid()))
            .AddSingleton<ICurrentTenant>(new TestCurrentTenant(Guid.NewGuid()))
            .BuildServiceProvider();

        return new DefaultHttpContext
        {
            TraceIdentifier = "wpc02e-projection",
            RequestServices = services
        };
    }

    private sealed record ProjectionProbe(string Value);

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => "redaction-test@example.invalid";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class TestCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId => tenantId;
        public bool IsAvailable => true;
        public string? TenantSlug => "redaction-test";
        public bool IsPlatformScope => false;
    }

    private sealed class RecordingRedactionService : IRedactionService
    {
        public AuthorizationContext? Context { get; private set; }
        public RedactionProfile? Profile { get; private set; }

        public RedactionResult Redact(
            AuthorizationContext context,
            object source,
            RedactionProfile profile)
        {
            Context = context;
            Profile = profile;
            return new RedactionResult(source, RedactionApplied: false);
        }
    }

    private sealed class ClosedRedactionService : IRedactionService
    {
        public RedactionResult Redact(
            AuthorizationContext context,
            object source,
            RedactionProfile profile) =>
            new(new RedactedPayload(profile, "authorization"), RedactionApplied: true);
    }
}
