using System.Text.Json;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Workspaces;

public sealed class WorkspaceDashboardProjectionTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 22, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task ListUsesBackendProjectionWithoutRoleOrCountRemapping()
    {
        var userId = Guid.NewGuid();
        var tenant = new CurrentTenantService();
        tenant.SetTenant(Guid.NewGuid(), "dashboard-test");
        var expected = new WorkspaceDashboardListItemResponse(
            Guid.NewGuid(),
            "Canonical Workspace",
            "Description",
            "briefcase",
            WorkspaceStatus.Active,
            CreatedAt,
            CreatedAt.AddHours(1),
            WorkspaceRole.Adviser,
            WorkspaceDashboardAccessSource.WorkspaceMembership,
            true,
            true,
            true,
            false,
            true,
            2,
            3,
            4,
            3,
            1);
        var query = new StubDashboardQuery([expected]);
        var service = Service(new TestCurrentUser(userId), tenant, query);

        var result = await service.ListAsync();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(userId, query.UserId);
        var item = Assert.Single(result.Value!);
        Assert.Same(expected, item);
        Assert.Equal(WorkspaceRole.Adviser, item.CurrentUserRole);
        Assert.False(item.CanCreateProject);
        Assert.True(item.CanAddFiles);
        Assert.Equal(2, item.UnreadAnnouncementCount);
        Assert.Equal(3, item.UnreadConversationCount);
        Assert.Equal(4, item.InProgressProjectCount);
        Assert.Equal(3, item.RunningProjectCount);
        Assert.Equal(1, item.NeedsReviewProjectCount);
    }

    [Fact]
    public async Task ListFailsClosedWhenDashboardQueryIsUnavailable()
    {
        var tenant = new CurrentTenantService();
        tenant.SetTenant(Guid.NewGuid(), "dashboard-test");
        var service = Service(
            new TestCurrentUser(Guid.NewGuid()),
            tenant,
            new StubDashboardQuery([], isAvailable: false));

        var result = await service.ListAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("DependencyUnavailable", result.ErrorDetail?.Code);
    }

    [Fact]
    public async Task ListFailsClosedWithoutCurrentTenant()
    {
        var query = new StubDashboardQuery([]);
        var service = Service(
            new TestCurrentUser(Guid.NewGuid()),
            new CurrentTenantService(),
            query);

        var result = await service.ListAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("TenantMembershipRequired", result.ErrorDetail?.Code);
        Assert.Null(query.UserId);
    }

    [Fact]
    public async Task ListReturnsTypedAuthenticationFailureWithoutExecutingProjection()
    {
        var tenant = new CurrentTenantService();
        tenant.SetTenant(Guid.NewGuid(), "dashboard-test");
        var query = new StubDashboardQuery([]);
        var service = Service(new TestCurrentUser(null), tenant, query);

        var result = await service.ListAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("AuthenticationRequired", result.ErrorDetail?.Code);
        Assert.Null(query.UserId);
    }

    [Fact]
    public void ContractSerializesCanonicalRoleAccessSourceAndQuickCreateCapabilities()
    {
        var response = new WorkspaceDashboardListItemResponse(
            Guid.NewGuid(),
            "Canonical Workspace",
            null,
            null,
            WorkspaceStatus.Active,
            CreatedAt,
            CreatedAt,
            WorkspaceRole.ReadOnly,
            WorkspaceDashboardAccessSource.WorkspaceMembership,
            true,
            true,
            true,
            false,
            false,
            0,
            0,
            0,
            0,
            0,
            true);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var root = json.RootElement;
        Assert.Equal("ReadOnly", root.GetProperty("currentUserRole").GetString());
        Assert.Equal("WorkspaceMembership", root.GetProperty("accessSource").GetString());
        Assert.False(root.GetProperty("canCreateProject").GetBoolean());
        Assert.True(root.GetProperty("canOpenProjectCreate").GetBoolean());
        Assert.False(root.GetProperty("canAddFiles").GetBoolean());
        Assert.Equal(0, root.GetProperty("unreadAnnouncementCount").GetInt32());
        Assert.Equal(0, root.GetProperty("unreadConversationCount").GetInt32());
        Assert.Equal(0, root.GetProperty("inProgressProjectCount").GetInt32());
        Assert.Equal(0, root.GetProperty("runningProjectCount").GetInt32());
        Assert.Equal(0, root.GetProperty("needsReviewProjectCount").GetInt32());
        Assert.False(root.TryGetProperty("activeProjectCount", out _));

        var legacyBasicConsumer = JsonSerializer.Deserialize<WorkspaceListItemResponse>(
            root.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(legacyBasicConsumer);
        Assert.Equal(response.Id, legacyBasicConsumer.Id);
        Assert.Equal(response.Name, legacyBasicConsumer.Name);
        Assert.Equal(response.Status, legacyBasicConsumer.Status);

        var systemAdminResponse = response with
        {
            CurrentUserRole = null,
            AccessSource = WorkspaceDashboardAccessSource.SystemAdmin
        };
        using var systemAdminJson = JsonDocument.Parse(JsonSerializer.Serialize(
            systemAdminResponse,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(JsonValueKind.Null, systemAdminJson.RootElement.GetProperty("currentUserRole").ValueKind);
        Assert.Equal(
            "SystemAdmin",
            systemAdminJson.RootElement.GetProperty("accessSource").GetString());
    }

    private static WorkspaceService Service(
        ICurrentUser currentUser,
        ICurrentTenant currentTenant,
        IWorkspaceDashboardQuery dashboardQuery) =>
        new(
            null!,
            null!,
            null!,
            currentUser,
            null!,
            null!,
            null!,
            currentTenant,
            dashboardQuery: dashboardQuery);

    private sealed class StubDashboardQuery(
        IReadOnlyList<WorkspaceDashboardListItemResponse> items,
        bool isAvailable = true) : IWorkspaceDashboardQuery
    {
        public bool IsAvailable => isAvailable;
        public Guid? UserId { get; private set; }

        public Task<IReadOnlyList<WorkspaceDashboardListItemResponse>> ListAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            UserId = userId;
            return Task.FromResult(items);
        }
    }

    private sealed class TestCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => userId.HasValue ? Guid.NewGuid() : null;
        public string? Email => userId.HasValue ? "dashboard@example.test" : null;
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => userId.HasValue;
    }
}
