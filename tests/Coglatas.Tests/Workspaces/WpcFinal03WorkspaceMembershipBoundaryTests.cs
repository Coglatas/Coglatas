using System.Reflection;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Security.Redaction;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.Workspaces;

[Trait("Scope", "WPCFinal03")]
public sealed class WpcFinal03WorkspaceMembershipBoundaryTests
{
    [Fact]
    public async Task HttpBoundaryRejectsUserWithoutCurrentTenantMembershipBeforeWorkspaceMutation()
    {
        var tenant = NewCurrentTenant();
        var service = new WorkspaceServiceStub();
        var controller = CreateController(service, new TenantRepositoryStub(null), tenant);

        var action = await controller.AddMember(
            Guid.NewGuid(),
            new AddWorkspaceMemberRequest(Guid.NewGuid(), WorkspaceRole.Member),
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(action);
        Assert.NotNull(notFound.Value);
        Assert.Equal(0, service.AddMemberCalls);
    }

    [Fact]
    public async Task HttpBoundaryRejectsMismatchedTenantMembershipRecord()
    {
        var tenant = NewCurrentTenant();
        var user = NewUser();
        var foreignTenant = new Tenant(Guid.NewGuid())
        {
            Name = "Foreign Tenant",
            DisplayName = "Foreign Tenant",
            Slug = $"foreign-{Guid.NewGuid():N}",
            Status = TenantStatus.Active
        };
        var mismatchedMembership = new TenantUser
        {
            TenantId = foreignTenant.Id,
            UserId = user.Id,
            User = user,
            Tenant = foreignTenant,
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        };
        var service = new WorkspaceServiceStub();
        var controller = CreateController(
            service,
            new TenantRepositoryStub(mismatchedMembership),
            tenant);

        var action = await controller.AddMember(
            Guid.NewGuid(),
            new AddWorkspaceMemberRequest(user.Id, WorkspaceRole.Member),
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(action);
        Assert.Equal(0, service.AddMemberCalls);
    }

    [Fact]
    public async Task HttpBoundaryForwardsOnlyActiveCurrentTenantUser()
    {
        var tenant = NewCurrentTenant();
        var user = NewUser();
        var tenantEntity = new Tenant(tenant.TenantId)
        {
            Name = "WPC Final03",
            DisplayName = "WPC Final03",
            Slug = "wpc-final03",
            Status = TenantStatus.Active
        };
        var membership = new TenantUser
        {
            TenantId = tenantEntity.Id,
            UserId = user.Id,
            User = user,
            Tenant = tenantEntity,
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        };
        var service = new WorkspaceServiceStub
        {
            AddMemberResult = Result<WorkspaceMemberResponse>.Success(
                new WorkspaceMemberResponse(
                    user.Id,
                    user.DisplayName,
                    user.Email,
                    WorkspaceRole.Member,
                    MembershipStatus.Active,
                    DateTimeOffset.UtcNow))
        };
        var controller = CreateController(service, new TenantRepositoryStub(membership), tenant);

        var action = await controller.AddMember(
            Guid.NewGuid(),
            new AddWorkspaceMemberRequest(user.Id, WorkspaceRole.Member),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(action);
        Assert.Equal(1, service.AddMemberCalls);
    }

    [Fact]
    public async Task GeneralSynchronizerRejectsActiveWorkspaceMemberAfterTenantSuspension()
    {
        var tenant = NewCurrentTenant();
        var user = NewUser();
        var tenantEntity = new Tenant(tenant.TenantId)
        {
            Name = "Suspended tenant membership",
            DisplayName = "Suspended tenant membership",
            Slug = "wpc-final03-suspended",
            Status = TenantStatus.Active
        };
        var tenantMembership = new TenantUser
        {
            TenantId = tenantEntity.Id,
            UserId = user.Id,
            User = user,
            Tenant = tenantEntity,
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Suspended,
            JoinedAt = DateTimeOffset.UtcNow
        };
        var synchronizer = new WorkspaceGeneralMembershipSynchronizer(
            null!,
            tenant,
            null!,
            null!,
            new TenantRepositoryStub(tenantMembership));
        var workspaceMembership = new WorkspaceMember
        {
            TenantId = tenantEntity.Id,
            WorkspaceId = Guid.NewGuid(),
            UserId = user.Id,
            Role = WorkspaceRole.Member,
            Status = MembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        };

        var result = await synchronizer.StageAsync(workspaceMembership, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "WorkspaceGeneral membership requires an active Tenant membership.",
            result.Error);
    }

    [Fact]
    public async Task WorkspaceMemberMutationFailsClosedWhenGeneralSynchronizerIsUnavailable()
    {
        var service = new WorkspaceService(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var method = typeof(WorkspaceService).GetMethod(
            "StageWorkspaceGeneralMembershipAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var invocation = method.Invoke(
            service,
            [
                new WorkspaceMember
                {
                    TenantId = Guid.NewGuid(),
                    WorkspaceId = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    Role = WorkspaceRole.Member,
                    Status = MembershipStatus.Active,
                    JoinedAt = DateTimeOffset.UtcNow
                },
                Guid.NewGuid(),
                CancellationToken.None
            ]);
        var task = Assert.IsAssignableFrom<Task<Result>>(invocation);

        var result = await task;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Canonical Workspace general membership synchronization is unavailable.",
            result.Error);
    }

    private static WorkspacesController CreateController(
        IWorkspaceService service,
        ITenantRepository tenants,
        CurrentTenantService currentTenant)
    {
        var services = new ServiceCollection()
            .AddSingleton<CanonicalRedactionService>()
            .AddSingleton<IRedactionService, CanonicalFileMetadataRedactionService>()
            .AddSingleton<ICurrentUser>(new CurrentUserStub(Guid.NewGuid()))
            .AddSingleton<ICurrentTenant>(currentTenant)
            .BuildServiceProvider();

        return new WorkspacesController(service, tenants, currentTenant)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    TraceIdentifier = "wpc-final03-workspace-membership",
                    RequestServices = services
                }
            }
        };
    }

    private static CurrentTenantService NewCurrentTenant()
    {
        var tenant = new CurrentTenantService();
        tenant.SetTenant(Guid.NewGuid(), "wpc-final03");
        return tenant;
    }

    private static User NewUser() => new()
    {
        DisplayName = "WPC Final03 User",
        Email = $"wpc-final03-{Guid.NewGuid():N}@example.test",
        NormalizedEmail = $"WPC-FINAL03-{Guid.NewGuid():N}@EXAMPLE.TEST",
        Status = UserStatus.Active
    };

    private sealed class CurrentUserStub(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => "wpc-final03@example.test";
        public SystemRole? SystemRole => global::Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class TenantRepositoryStub(TenantUser? membership) : ITenantRepository
    {
        public Task<TenantUser?> GetTenantUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(membership);

        public Task<IReadOnlyList<Tenant>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Tenant?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Tenant?> GetTenantBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Tenant?> GetTenantByPrimaryDomainAsync(string primaryDomain, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TenantUser>> ListTenantUsersAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TenantUser>> ListUserTenantMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AddTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class WorkspaceServiceStub : IWorkspaceService
    {
        public Result<WorkspaceMemberResponse> AddMemberResult { get; init; } =
            Result<WorkspaceMemberResponse>.Failure("Not configured.");
        public int AddMemberCalls { get; private set; }

        public Task<Result<WorkspaceMemberResponse>> AddMemberAsync(
            Guid workspaceId,
            AddWorkspaceMemberRequest request,
            CancellationToken cancellationToken = default)
        {
            AddMemberCalls++;
            return Task.FromResult(AddMemberResult);
        }

        public Task<Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceListItemResponse>>> ListArchivedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceCapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceDetailResponse>> CreateAsync(CreateWorkspaceRequest request, string? clientRequestIdentity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceDetailResponse>> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceDetailResponse>> UpdateAsync(Guid workspaceId, UpdateWorkspaceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ArchiveAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RestoreAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceMemberResponse>>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceMemberResponse>> UpdateMemberAsync(Guid workspaceId, Guid userId, UpdateWorkspaceMemberRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RemoveMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
