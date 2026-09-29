using System.Text.Json;
using System.Text.Json.Nodes;
using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Security.Redaction;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Enums;
using Coglatas.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.Audit;

public sealed class AuditControllerTests
{
    [Fact]
    public async Task AdminAuditGrid_UsesGridDefaultWhenPageSizeIsOmitted()
    {
        var audit = new CapturingAuditQueryService();
        var controller = CreateController(audit);

        var result = await controller.AdminAuditGrid(new AuditLogQuery(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var capturedQuery = Assert.IsType<AuditLogQuery>(audit.LastGridQuery);
        Assert.Equal(100, capturedQuery.PageSize);
        Assert.Equal(1, capturedQuery.Page);
    }

    [Fact]
    public async Task AdminAuditGrid_PreservesExplicitPageSize()
    {
        var audit = new CapturingAuditQueryService();
        var controller = CreateController(audit, "?page=2&pageSize=50");

        var result = await controller.AdminAuditGrid(
            new AuditLogQuery(Page: 2, PageSize: 50),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var capturedQuery = Assert.IsType<AuditLogQuery>(audit.LastGridQuery);
        Assert.Equal(50, capturedQuery.PageSize);
        Assert.Equal(2, capturedQuery.Page);
    }

    [Fact]
    public async Task AdminAuditGrid_PreservesServerOwnedFilterContract()
    {
        var audit = new CapturingAuditQueryService();
        var controller = CreateController(audit, "?q=retention&severity=critical&action=file.export.failed&actor=Admin&entityType=ExportJob&result=failed");
        var query = new AuditLogQuery(
            Action: "file.export.failed",
            EntityType: "ExportJob",
            PageSize: 100,
            Q: "retention",
            Actor: "Admin",
            Severity: "critical",
            Result: "failed");

        var result = await controller.AdminAuditGrid(query, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(query, audit.LastGridQuery);
    }

    [Fact]
    public async Task AdminAuditGridRow_UsesGenericNotFoundForMalformedOrUnavailableIdentifiers()
    {
        var audit = new CapturingAuditQueryService(
            gridRowResult: Result<AuditGridRowResponse>.Failure(new ApplicationErrorDetail(
                "AuditEventNotFound",
                "The requested audit event is not available.")));
        var controller = CreateController(audit);

        var result = await controller.AdminAuditGridRow("not-an-audit-id", CancellationToken.None);

        var notFound = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal(Guid.Empty, audit.LastGridRowId);
        var payload = JsonSerializer.Serialize(notFound.Value);
        Assert.Contains("AuditEventNotFound", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("The requested audit event is not available.", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("not-an-audit-id", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminAuditGridRow_ReturnsCapabilityDenialBeforeIdentifierLookup()
    {
        var audit = new CapturingAuditQueryService(
            gridRowResult: Result<AuditGridRowResponse>.Failure(new ApplicationErrorDetail(
                "CapabilityDenied",
                "The requested Audit operation is not permitted.")));
        var controller = CreateController(audit);

        var result = await controller.AdminAuditGridRow(Guid.NewGuid().ToString("D"), CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("CapabilityDenied", audit.LastGridRowResult?.ErrorDetail?.Code);
    }

    [Fact]
    public async Task AdminAuditGridRow_ResponseBoundaryReturnsOnlyTheGridRowContract()
    {
        var row = new AuditGridRowResponse(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "audit.detail.read",
            "Redacted actor",
            "AuditLog",
            "Workspace A",
            "info",
            "success",
            "A metadata-safe summary.",
            null);
        var audit = new CapturingAuditQueryService(
            gridRowResult: Result<AuditGridRowResponse>.Success(row));
        var controller = CreateController(
            audit,
            capabilities: new AuditCapabilityResponse(
                CanView: true,
                CanReview: false,
                CanApprove: false,
                CanExport: false,
                CanViewSensitiveMetadata: false));

        var result = await controller.AdminAuditGridRow(row.Id.ToString("D"), CancellationToken.None);

        var payload = SerializeOk(result);
        Assert.Contains("A metadata-safe summary.", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("MetadataJson", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ActorUserId", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EntityId", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Claims", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Evidence", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Duration", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminAuditSensitiveMetadata_UsesGenericNotFoundForMalformedIdentifier()
    {
        var audit = new CapturingAuditQueryService(
            sensitiveMetadataResult: Result<AuditSensitiveMetadataResponse>.Failure(
                new ApplicationErrorDetail(
                    "AuditEventNotFound",
                    "The requested audit event is not available.")));
        var controller = CreateController(
            audit,
            capabilities: new AuditCapabilityResponse(true, false, false, false, true));

        var result = await controller.AdminAuditSensitiveMetadata(
            "not-an-audit-id",
            CancellationToken.None);

        var notFound = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal(Guid.Empty, audit.LastSensitiveMetadataId);
        var payload = JsonSerializer.Serialize(notFound.Value);
        Assert.Contains("AuditEventNotFound", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("not-an-audit-id", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("The requested audit event is not available.", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminAuditSensitiveMetadata_ReturnsCapabilityDenialWithoutSensitiveValues()
    {
        var audit = new CapturingAuditQueryService(
            sensitiveMetadataResult: Result<AuditSensitiveMetadataResponse>.Failure(
                new ApplicationErrorDetail(
                    "CapabilityDenied",
                    "The requested Audit operation is not permitted.")));
        var controller = CreateController(
            audit,
            capabilities: new AuditCapabilityResponse(true, true, false, false, false));

        var result = await controller.AdminAuditSensitiveMetadata(
            Guid.NewGuid().ToString("D"),
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var payload = JsonSerializer.Serialize(forbidden.Value);
        Assert.Contains("CapabilityDenied", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminAuditSensitiveMetadata_AppliesCanonicalServerRedaction()
    {
        var auditId = Guid.NewGuid();
        var metadata = JsonNode.Parse(
            """{"outcome":"Allowed","nested":{"secret":"must-not-render","category":"Policy"}}""")!
            .AsObject();
        var audit = new CapturingAuditQueryService(
            sensitiveMetadataResult: Result<AuditSensitiveMetadataResponse>.Success(
                new AuditSensitiveMetadataResponse(auditId, metadata, RedactionApplied: true)));
        var controller = CreateController(
            audit,
            capabilities: new AuditCapabilityResponse(true, true, false, false, true));

        var result = await controller.AdminAuditSensitiveMetadata(
            auditId.ToString("D"),
            CancellationToken.None);

        var payload = SerializeOk(result);
        Assert.Contains("Allowed", payload, StringComparison.Ordinal);
        Assert.Contains("Policy", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-render", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecurityEvents_ResponseBoundaryRedactsRestrictedFieldsWithoutSensitiveCapability()
    {
        const string ipAddress = "203.0.113.42";
        const string userAgent = "audit-sensitive-agent";
        const string summary = "Visible audit summary";
        var audit = new CapturingAuditQueryService(
            securityEvents:
            [
                new SecurityEventListItemResponse(
                    Guid.NewGuid(),
                    SecurityEventType.AccessDenied,
                    null,
                    null,
                    ipAddress,
                    userAgent,
                    SecurityEventSeverity.Warning,
                    summary,
                    null,
                    DateTimeOffset.UtcNow)
            ]);
        var controller = CreateController(
            audit,
            capabilities: new AuditCapabilityResponse(
                CanView: true,
                CanReview: true,
                CanApprove: false,
                CanExport: false,
                CanViewSensitiveMetadata: false));

        var result = await controller.SecurityEvents(new SecurityEventQuery(), CancellationToken.None);

        var payload = SerializeOk(result);
        Assert.Contains(summary, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(ipAddress, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(userAgent, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityEvents_ResponseBoundaryPreservesRestrictedFieldsWithSensitiveCapability()
    {
        const string ipAddress = "203.0.113.43";
        const string userAgent = "audit-authorized-sensitive-agent";
        var audit = new CapturingAuditQueryService(
            securityEvents:
            [
                new SecurityEventListItemResponse(
                    Guid.NewGuid(),
                    SecurityEventType.AccessDenied,
                    null,
                    null,
                    ipAddress,
                    userAgent,
                    SecurityEventSeverity.Warning,
                    "Visible audit summary",
                    null,
                    DateTimeOffset.UtcNow)
            ]);
        var controller = CreateController(
            audit,
            capabilities: new AuditCapabilityResponse(
                CanView: true,
                CanReview: true,
                CanApprove: false,
                CanExport: false,
                CanViewSensitiveMetadata: true));

        var result = await controller.SecurityEvents(new SecurityEventQuery(), CancellationToken.None);

        var payload = SerializeOk(result);
        Assert.Contains(ipAddress, payload, StringComparison.Ordinal);
        Assert.Contains(userAgent, payload, StringComparison.Ordinal);
    }

    private static string SerializeOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.Serialize(ok.Value);
    }

    private static AuditController CreateController(
        CapturingAuditQueryService audit,
        string? queryString = null,
        AuditCapabilityResponse? capabilities = null)
    {
        var tenant = new CurrentTenantService();
        tenant.SetTenant(Guid.NewGuid(), "audit-test");
        var services = new ServiceCollection()
            .AddSingleton<IRedactionService, CanonicalRedactionService>()
            .AddSingleton<ICurrentUser>(new TestCurrentUser(Guid.NewGuid()))
            .AddSingleton<ICurrentTenant>(tenant)
            .BuildServiceProvider();
        var controller = new AuditController(
            audit,
            capabilities is null ? null : new StubAuditAuthorizationService(capabilities))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(queryString))
        {
            controller.Request.QueryString = new QueryString(queryString);
        }

        return controller;
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => "audit-test@example.invalid";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class StubAuditAuthorizationService(AuditCapabilityResponse capabilities)
        : IAuditAuthorizationService
    {
        public Task<AuditCapabilityResponse> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(capabilities);

        public Task<Result> AuthorizeAsync(
            string capabilityKey,
            string operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(IsGranted(capabilityKey)
                ? Result.Success()
                : Result.Failure(new ApplicationErrorDetail(
                    "CapabilityDenied",
                    "The requested Audit operation is not permitted.")));

        private bool IsGranted(string capabilityKey) => capabilityKey switch
        {
            CapabilityKeys.AuditView => capabilities.CanView,
            CapabilityKeys.AuditReview => capabilities.CanReview,
            CapabilityKeys.AuditApprove => capabilities.CanApprove,
            CapabilityKeys.AuditExport => capabilities.CanExport,
            CapabilityKeys.AuditSensitiveMetadataView => capabilities.CanViewSensitiveMetadata,
            _ => false
        };
    }

    private sealed class CapturingAuditQueryService(
        IReadOnlyList<SecurityEventListItemResponse>? securityEvents = null,
        Result<AuditGridRowResponse>? gridRowResult = null,
        Result<AuditSensitiveMetadataResponse>? sensitiveMetadataResult = null) : IAuditQueryService
    {
        private readonly IReadOnlyList<SecurityEventListItemResponse> _securityEvents = securityEvents ?? [];
        private readonly Result<AuditGridRowResponse> _gridRowResult = gridRowResult ??
            Result<AuditGridRowResponse>.Failure(new ApplicationErrorDetail(
                "AuditEventNotFound",
                "The requested audit event is not available."));
        private readonly Result<AuditSensitiveMetadataResponse> _sensitiveMetadataResult =
            sensitiveMetadataResult ?? Result<AuditSensitiveMetadataResponse>.Failure(
                new ApplicationErrorDetail(
                    "AuditEventNotFound",
                    "The requested audit event is not available."));

        public AuditLogQuery? LastGridQuery { get; private set; }
        public Guid? LastGridRowId { get; private set; }
        public Result<AuditGridRowResponse>? LastGridRowResult { get; private set; }
        public Guid? LastSensitiveMetadataId { get; private set; }

        public Task<Result<PagedResponse<AuditLogListItemResponse>>> ListAuditLogsAsync(
            AuditLogQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<PagedResponse<AuditLogListItemResponse>>.Success(
                new PagedResponse<AuditLogListItemResponse>(
                    Array.Empty<AuditLogListItemResponse>(),
                    query.Page,
                    query.PageSize,
                    0)));
        }

        public Task<Result<PagedResponse<AuditGridRowResponse>>> ListAuditGridAsync(
            AuditLogQuery query,
            CancellationToken cancellationToken = default)
        {
            LastGridQuery = query;
            return Task.FromResult(Result<PagedResponse<AuditGridRowResponse>>.Success(
                new PagedResponse<AuditGridRowResponse>(
                    Array.Empty<AuditGridRowResponse>(),
                    query.Page,
                    query.PageSize,
                    0)));
        }

        public Task<Result<AuditGridRowResponse>> GetAuditGridRowAsync(
            Guid auditId,
            CancellationToken cancellationToken = default)
        {
            LastGridRowId = auditId;
            LastGridRowResult = _gridRowResult;
            return Task.FromResult(_gridRowResult);
        }

        public Task<Result<AuditSensitiveMetadataResponse>> GetAuditSensitiveMetadataAsync(
            Guid auditId,
            CancellationToken cancellationToken = default)
        {
            LastSensitiveMetadataId = auditId;
            return Task.FromResult(_sensitiveMetadataResult);
        }

        public Task<Result<PagedResponse<SecurityEventListItemResponse>>> ListSecurityEventsAsync(
            SecurityEventQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<PagedResponse<SecurityEventListItemResponse>>.Success(
                new PagedResponse<SecurityEventListItemResponse>(
                    _securityEvents,
                    query.Page,
                    query.PageSize,
                    _securityEvents.Count)));
        }
    }
}
