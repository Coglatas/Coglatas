using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Security.Redaction;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Enums;
using Coglatas.Web.Controllers;
using Coglatas.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.Workspaces;

public sealed class WorkspacesControllerTests
{
    [Fact]
    public async Task ListAuthenticationFailureUsesCanonicalSafeError()
    {
        var service = new StubWorkspaceService
        {
            ListResult = Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>.Failure(
                new ApplicationErrorDetail(
                    "AuthenticationRequired",
                    "Authentication is required."))
        };
        var controller = Controller(service);

        var action = await controller.List(CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(action);
        var envelope = Assert.IsType<ApiErrorEnvelope>(unauthorized.Value);
        Assert.Equal(StatusCodes.Status401Unauthorized, envelope.Status);
        Assert.Equal("AuthenticationRequired", envelope.Error.Code);
        Assert.Empty(envelope.Error.Details);
    }

    [Fact]
    public async Task ListPreservesArrayShapeAndDashboardProjection()
    {
        var value = new WorkspaceDashboardListItemResponse(
            Guid.NewGuid(),
            "Workspace",
            null,
            null,
            WorkspaceStatus.Active,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            WorkspaceDashboardAccessSource.SystemAdmin,
            true,
            true,
            true,
            false,
            true,
            1,
            2,
            3,
            2,
            1);
        var service = new StubWorkspaceService
        {
            ListResult = Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>.Success([value])
        };
        var controller = Controller(service);

        var action = await controller.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var items = Assert.IsAssignableFrom<IReadOnlyList<WorkspaceDashboardListItemResponse>>(ok.Value);
        var item = Assert.Single(items);
        Assert.Equal(value.Id, item.Id);
        Assert.Null(item.CurrentUserRole);
        Assert.Equal(WorkspaceDashboardAccessSource.SystemAdmin, item.AccessSource);
        Assert.False(item.CanCreateProject);
        Assert.True(item.CanAddFiles);
        Assert.Equal(3, item.InProgressProjectCount);
        Assert.Equal(2, item.RunningProjectCount);
        Assert.Equal(1, item.NeedsReviewProjectCount);
    }

    [Fact]
    public void CreateDocumentsCreatedSuccessEnvelope()
    {
        var method = typeof(WorkspacesController).GetMethod(nameof(WorkspacesController.Create));
        Assert.NotNull(method);

        var response = Assert.Single(
            method.GetCustomAttributes(typeof(ProducesResponseTypeAttribute), inherit: false)
                .Cast<ProducesResponseTypeAttribute>(),
            attribute => attribute.StatusCode == StatusCodes.Status201Created);

        Assert.Equal(typeof(ApiSuccessEnvelope<WorkspaceDetailResponse>), response.Type);
    }

    [Fact]
    public async Task CreateForwardsIdempotencyIdentityAndReturnsCreatedResult()
    {
        var value = new WorkspaceDetailResponse(
            Guid.NewGuid(),
            "Workspace",
            null,
            null,
            WorkspaceStatus.Active,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null);
        var service = new StubWorkspaceService
        {
            CreateResult = Result<WorkspaceDetailResponse>.Success(value)
        };
        var controller = Controller(service);

        var action = await controller.Create(
            new CreateWorkspaceRequest("Workspace", null, null),
            "browser-request-001",
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(action);
        Assert.Equal(nameof(WorkspacesController.Get), created.ActionName);
        Assert.Equal(value.Id, created.RouteValues!["workspaceId"]);
        var envelope = Assert.IsType<ApiSuccessEnvelope<object>>(created.Value);
        Assert.Equal("wpc01-request", envelope.RequestId);
        Assert.Same(value, envelope.Data);
        Assert.Empty(envelope.Warnings);
        Assert.Equal("browser-request-001", service.ClientRequestIdentity);
    }

    [Fact]
    public async Task ReusedIdentityMismatchReturnsSafeConflictEnvelope()
    {
        var service = new StubWorkspaceService
        {
            CreateResult = Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "IdempotencyConflict",
                "The Idempotency-Key was already used with a different Workspace request."))
        };
        var controller = Controller(service);

        var action = await controller.Create(
            new CreateWorkspaceRequest("Workspace", null, null),
            "browser-request-001",
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(action);
        var envelope = Assert.IsType<ApiErrorEnvelope>(conflict.Value);
        Assert.Equal("wpc01-request", envelope.RequestId);
        Assert.Equal("IdempotencyConflict", envelope.Error.Code);
        Assert.Equal("header.Idempotency-Key", envelope.Error.Target);
        Assert.Empty(envelope.Error.Details);
        Assert.False(envelope.Error.RedactionApplied);
        Assert.Equal(StatusCodes.Status409Conflict, envelope.Status);
        Assert.False(string.IsNullOrWhiteSpace(envelope.TraceId));
    }

    [Fact]
    public async Task CapabilityProjectionIsBackendOwned()
    {
        var service = new StubWorkspaceService
        {
            CapabilityResult = Result<WorkspaceCapabilitiesResponse>.Success(
                new WorkspaceCapabilitiesResponse(true))
        };
        var controller = Controller(service);

        var action = await controller.Capabilities(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var envelope = Assert.IsType<ApiSuccessEnvelope<object>>(ok.Value);
        var capability = Assert.IsType<WorkspaceCapabilitiesResponse>(envelope.Data);
        Assert.True(capability.CanCreate);
        Assert.Empty(envelope.Warnings);
    }

    [Fact]
    public async Task UnrecoverableReplayUsesRedactedNotFoundEnvelope()
    {
        var service = new StubWorkspaceService
        {
            CreateResult = Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "NotFound",
                "The requested resource was not found."))
        };
        var controller = Controller(service);

        var action = await controller.Create(
            new CreateWorkspaceRequest("Workspace", null, null),
            "browser-request-001",
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(action);
        var envelope = Assert.IsType<ApiErrorEnvelope>(notFound.Value);
        Assert.Equal(StatusCodes.Status404NotFound, envelope.Status);
        Assert.Equal("NotFound", envelope.Error.Code);
        Assert.Null(envelope.Error.Target);
        Assert.Empty(envelope.Error.Details);
        Assert.True(envelope.Error.RedactionApplied);
    }

    [Fact]
    public async Task ArchivedWorkspaceMutationUsesRedactedConflictEnvelope()
    {
        var service = new StubWorkspaceService
        {
            UpdateResult = Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "InvalidStateTransition",
                "Archived Workspaces are read-only.",
                Target: "workspace.status"))
        };
        var controller = Controller(service);

        var action = await controller.Update(
            Guid.NewGuid(),
            new UpdateWorkspaceRequest(null, null, null, null),
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(action);
        var envelope = Assert.IsType<ApiErrorEnvelope>(conflict.Value);
        Assert.Equal(StatusCodes.Status409Conflict, envelope.Status);
        Assert.Equal("InvalidStateTransition", envelope.Error.Code);
        Assert.Equal("The request could not be completed.", envelope.Error.Message);
        Assert.Null(envelope.Error.Target);
        Assert.Empty(envelope.Error.Details);
        Assert.True(envelope.Error.RedactionApplied);
    }

    [Fact]
    public async Task WorkspaceAdminRestoreDenialUsesForbiddenEnvelope()
    {
        var service = new StubWorkspaceService
        {
            RestoreResult = Result.Failure(new ApplicationErrorDetail(
                "CapabilityDenied",
                "Only a current Workspace Owner can restore an archived Workspace.",
                Target: "workspace"))
        };
        var controller = Controller(service);

        var action = await controller.Restore(Guid.NewGuid(), CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var envelope = Assert.IsType<ApiErrorEnvelope>(forbidden.Value);
        Assert.Equal("CapabilityDenied", envelope.Error.Code);
        Assert.Equal("workspace", envelope.Error.Target);
        Assert.False(envelope.Error.RedactionApplied);
    }

    private static WorkspacesController Controller(IWorkspaceService service)
    {
        var tenant = new CurrentTenantService();
        tenant.SetTenant(Guid.NewGuid(), "workspace-test");
        var services = new ServiceCollection()
            .AddSingleton<IRedactionService, CanonicalRedactionService>()
            .AddSingleton<ICurrentUser>(new TestCurrentUser(Guid.NewGuid()))
            .AddSingleton<ICurrentTenant>(tenant)
            .BuildServiceProvider();

        return new WorkspacesController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    TraceIdentifier = "wpc01-request",
                    RequestServices = services
                }
            }
        };
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => "workspace-test@example.invalid";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class StubWorkspaceService : IWorkspaceService
    {
        public Result<WorkspaceDetailResponse> CreateResult { get; init; } =
            Result<WorkspaceDetailResponse>.Failure("Not configured.");
        public Result<WorkspaceDetailResponse> UpdateResult { get; init; } =
            Result<WorkspaceDetailResponse>.Failure("Not configured.");
        public Result<WorkspaceCapabilitiesResponse> CapabilityResult { get; init; } =
            Result<WorkspaceCapabilitiesResponse>.Failure("Not configured.");
        public Result<IReadOnlyList<WorkspaceDashboardListItemResponse>> ListResult { get; init; } =
            Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>.Failure("Not configured.");
        public Result RestoreResult { get; init; } = Result.Failure("Not configured.");
        public string? ClientRequestIdentity { get; private set; }

        public Task<Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ListResult);

        public Task<Result<IReadOnlyList<WorkspaceListItemResponse>>> ListArchivedAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<WorkspaceCapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CapabilityResult);

        public Task<Result<WorkspaceDetailResponse>> CreateAsync(
            CreateWorkspaceRequest request,
            string? clientRequestIdentity,
            CancellationToken cancellationToken = default)
        {
            ClientRequestIdentity = clientRequestIdentity;
            return Task.FromResult(CreateResult);
        }

        public Task<Result<WorkspaceDetailResponse>> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Result<WorkspaceDetailResponse>> UpdateAsync(Guid workspaceId, UpdateWorkspaceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(UpdateResult);
        public Task<Result> ArchiveAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Result> RestoreAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(RestoreResult);
        public Task<Result<IReadOnlyList<WorkspaceMemberResponse>>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Result<WorkspaceMemberResponse>> AddMemberAsync(Guid workspaceId, AddWorkspaceMemberRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Result<WorkspaceMemberResponse>> UpdateMemberAsync(Guid workspaceId, Guid userId, UpdateWorkspaceMemberRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Result> RemoveMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
