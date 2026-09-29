using System.Text.Json;
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

[Trait("Issue", "529")]
public sealed class WorkspaceMemberPrivacyProjectionTests
{
    [Fact]
    public async Task OrdinaryListOmitsEmailAndRemovedMemberships()
    {
        var workspaceId = Guid.NewGuid();
        var active = Member(workspaceId, "Active Member", "active@example.test", MembershipStatus.Active);
        var removed = Member(workspaceId, "Removed Member", "removed@example.test", MembershipStatus.Suspended);
        var repository = new WorkspaceRepositoryStub
        {
            Members = [active, removed]
        };
        var service = ProjectionService(repository, canView: true, canManage: false);

        var result = await service.ListAsync(workspaceId);

        Assert.True(result.IsSuccess);
        var member = Assert.Single(result.Value!);
        Assert.Equal(active.UserId, member.UserId);
        Assert.Equal(active.User!.DisplayName, member.DisplayName);
        Assert.Equal(MembershipStatus.Active, member.Status);
        Assert.DoesNotContain(
            typeof(WorkspaceMemberResponse).GetProperties(),
            property => string.Equals(property.Name, "Email", StringComparison.Ordinal) ||
                        string.Equals(property.Name, "JoinedAt", StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("joinedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(active.User.Email, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(removed.User!.DisplayName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(removed.User.Email, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrdinaryDetailReauthorizesWorkspaceBeforeReadingTargetMember()
    {
        var workspaceId = Guid.NewGuid();
        var repository = new WorkspaceRepositoryStub
        {
            Member = Member(workspaceId, "Hidden Member", "hidden@example.test", MembershipStatus.Active)
        };
        var service = ProjectionService(repository, canView: false, canManage: false);

        var result = await service.GetAsync(workspaceId, repository.Member!.UserId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NotFound", result.ErrorDetail?.Code);
        Assert.Equal(0, repository.GetMemberCalls);
    }

    [Fact]
    public async Task OrdinaryDetailTreatsRevokedMembershipAsNotFound()
    {
        var workspaceId = Guid.NewGuid();
        var repository = new WorkspaceRepositoryStub
        {
            Member = Member(workspaceId, "Removed Member", "removed@example.test", MembershipStatus.Suspended)
        };
        var service = ProjectionService(repository, canView: true, canManage: false);

        var result = await service.GetAsync(workspaceId, repository.Member!.UserId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NotFound", result.ErrorDetail?.Code);
        Assert.Equal(1, repository.GetMemberCalls);
    }

    [Fact]
    public async Task ManagementProjectionRequiresExplicitManageCapabilityBeforeReadingMembers()
    {
        var workspaceId = Guid.NewGuid();
        var repository = new WorkspaceRepositoryStub
        {
            Members = [Member(workspaceId, "Member", "member@example.test", MembershipStatus.Active)]
        };
        var service = ProjectionService(repository, canView: true, canManage: false);

        var result = await service.ListManagementAsync(workspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        Assert.Equal(0, repository.ListMembersCalls);
    }

    [Fact]
    public async Task ManagementProjectionReturnsEmailOnlyAfterManageCapability()
    {
        var workspaceId = Guid.NewGuid();
        var active = Member(workspaceId, "Managed Member", "managed@example.test", MembershipStatus.Active);
        var repository = new WorkspaceRepositoryStub
        {
            Members = [active]
        };
        var service = ProjectionService(repository, canView: true, canManage: true);

        var result = await service.ListManagementAsync(workspaceId);

        Assert.True(result.IsSuccess);
        var member = Assert.Single(result.Value!);
        Assert.Equal(active.User!.Email, member.Email);
        Assert.Equal(active.User.Status, member.AccountStatus);
        Assert.Equal(active.Status, member.Status);
    }

    [Fact]
    public async Task OrdinaryHttpBoundaryNeverSerializesEmail()
    {
        var workspaceId = Guid.NewGuid();
        var member = new WorkspaceMemberResponse(
            Guid.NewGuid(),
            "HTTP Member",
            WorkspaceRole.Member,
            MembershipStatus.Active);
        var projection = new WorkspaceMemberProjectionStub
        {
            ListResult = Result<IReadOnlyList<WorkspaceMemberResponse>>.Success([member])
        };
        var controller = Controller(projection);

        var action = await controller.ListMembers(workspaceId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("HTTP Member", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":1", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("joinedAt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagementHttpBoundaryCanExposeAuthorizedEmail()
    {
        var workspaceId = Guid.NewGuid();
        const string email = "manager-visible@example.test";
        var projection = new WorkspaceMemberProjectionStub
        {
            ManagementListResult = Result<IReadOnlyList<WorkspaceMemberManagementResponse>>.Success(
            [
                new WorkspaceMemberManagementResponse(
                    Guid.NewGuid(),
                    "Managed Member",
                    email,
                    WorkspaceRole.Member,
                    MembershipStatus.Active,
                    UserStatus.Active,
                    DateTimeOffset.UtcNow)
            ])
        };
        var controller = Controller(projection);

        var action = await controller.ListManagedMembers(workspaceId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(email, json, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceMemberProjectionService ProjectionService(
        WorkspaceRepositoryStub repository,
        bool canView,
        bool canManage)
    {
        return new WorkspaceMemberProjectionService(
            repository,
            new WorkspaceAuthorizationStub(canView, canManage),
            new CurrentUserStub(Guid.NewGuid()));
    }

    private static WorkspaceMember Member(
        Guid workspaceId,
        string displayName,
        string email,
        MembershipStatus status)
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Status = UserStatus.Active
        };
        return new WorkspaceMember
        {
            TenantId = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            User = user,
            Role = WorkspaceRole.Member,
            Status = status,
            JoinedAt = DateTimeOffset.UtcNow
        };
    }

    private static WorkspacesController Controller(IWorkspaceMemberProjectionService projection)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(Guid.NewGuid(), "issue-529");
        var services = new ServiceCollection()
            .AddSingleton<IRedactionService, CanonicalRedactionService>()
            .AddSingleton<ICurrentUser>(new CurrentUserStub(Guid.NewGuid()))
            .AddSingleton<ICurrentTenant>(currentTenant)
            .BuildServiceProvider();

        return new WorkspacesController(
            new WorkspaceServiceStub(),
            tenants: null,
            currentTenant,
            projection)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    TraceIdentifier = "issue-529",
                    RequestServices = services
                }
            }
        };
    }

    private sealed class WorkspaceRepositoryStub : IWorkspaceRepository
    {
        public IReadOnlyList<WorkspaceMember> Members { get; init; } = [];
        public WorkspaceMember? Member { get; init; }
        public int ListMembersCalls { get; private set; }
        public int GetMemberCalls { get; private set; }

        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(
            Guid workspaceId,
            CancellationToken cancellationToken = default)
        {
            ListMembersCalls++;
            return Task.FromResult(Members);
        }

        public Task<WorkspaceMember?> GetMemberAsync(
            Guid workspaceId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            GetMemberCalls++;
            return Task.FromResult(Member);
        }

        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<WorkspaceMember?> GetMemberWithWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class WorkspaceAuthorizationStub(bool canView, bool canManage) : IWorkspaceAuthorizationService
    {
        public Task<bool> CanViewWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(canView);
        public Task<bool> CanContributeWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(canView);
        public Task<bool> CanManageWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(canManage);
        public Task<bool> CanCreateWorkspace(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class CurrentUserStub(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => "issue-529@example.test";
        public SystemRole? SystemRole => global::Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class WorkspaceMemberProjectionStub : IWorkspaceMemberProjectionService
    {
        public Result<IReadOnlyList<WorkspaceMemberResponse>> ListResult { get; init; } =
            Result<IReadOnlyList<WorkspaceMemberResponse>>.Failure("Not configured.");
        public Result<IReadOnlyList<WorkspaceMemberManagementResponse>> ManagementListResult { get; init; } =
            Result<IReadOnlyList<WorkspaceMemberManagementResponse>>.Failure("Not configured.");

        public Task<Result<IReadOnlyList<WorkspaceMemberResponse>>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ListResult);
        public Task<Result<WorkspaceMemberResponse>> GetAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceMemberManagementResponse>>> ListManagementAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ManagementListResult);
        public Task<Result<WorkspaceMemberManagementResponse>> GetManagementAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class WorkspaceServiceStub : IWorkspaceService
    {
        public Task<Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceListItemResponse>>> ListArchivedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceCapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceDetailResponse>> CreateAsync(CreateWorkspaceRequest request, string? clientRequestIdentity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceDetailResponse>> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceDetailResponse>> UpdateAsync(Guid workspaceId, UpdateWorkspaceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ArchiveAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RestoreAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceMemberResponse>>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceMemberResponse>> AddMemberAsync(Guid workspaceId, AddWorkspaceMemberRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkspaceMemberResponse>> UpdateMemberAsync(Guid workspaceId, Guid userId, UpdateWorkspaceMemberRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RemoveMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
