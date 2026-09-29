using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;

namespace Coglatas.Tests.Projects;

public sealed class TaskWorkspaceTimeZoneResolverTests
{
    [Theory]
    [InlineData("Asia/Tokyo")]
    [InlineData("America/New_York")]
    public async Task ValidWorkspaceZoneWinsOverTenantFallback(string zoneId)
    {
        var fixture = Fixture.WithWorkspace(zoneId, "UTC");

        var result = await fixture.Resolver.ResolveAsync(fixture.TenantId, fixture.WorkspaceId);

        Assert.Equal(zoneId, result.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/A_Zone")]
    public async Task MissingOrInvalidWorkspaceZoneFallsBackToValidTenantZone(string? workspaceZone)
    {
        var fixture = Fixture.WithWorkspace(workspaceZone, "Asia/Tokyo");

        var result = await fixture.Resolver.ResolveAsync(fixture.TenantId, fixture.WorkspaceId);

        Assert.Equal("Asia/Tokyo", result.Id);
    }

    [Fact]
    public async Task WorkspaceFromAnotherTenantIsIgnored()
    {
        var fixture = Fixture.WithWorkspace("America/New_York", "Asia/Tokyo", Guid.NewGuid());

        var result = await fixture.Resolver.ResolveAsync(fixture.TenantId, fixture.WorkspaceId);

        Assert.Equal("Asia/Tokyo", result.Id);
    }

    [Theory]
    [InlineData("Not/A_Zone", "Still/Not_A_Zone")]
    [InlineData(null, null)]
    public async Task InvalidOrMissingCandidatesFallBackToUtc(string? workspaceZone, string? tenantZone)
    {
        var fixture = Fixture.WithWorkspace(workspaceZone, tenantZone);

        var result = await fixture.Resolver.ResolveAsync(fixture.TenantId, fixture.WorkspaceId);

        Assert.Equal(TimeZoneInfo.Utc, result);
    }

    [Fact]
    public async Task PropagatesCancellationTokenToBothRepositories()
    {
        var fixture = Fixture.WithWorkspace(null, "UTC");
        using var source = new CancellationTokenSource();

        await fixture.Resolver.ResolveAsync(fixture.TenantId, fixture.WorkspaceId, source.Token);

        Assert.Equal(source.Token, fixture.Workspaces.LastToken);
        Assert.Equal(source.Token, fixture.Tenants.LastToken);
    }

    private sealed class Fixture
    {
        private Fixture(Guid tenantId, Guid workspaceId, FakeWorkspaces workspaces, FakeTenantPlans tenants)
        {
            TenantId = tenantId;
            WorkspaceId = workspaceId;
            Workspaces = workspaces;
            Tenants = tenants;
            Resolver = new TaskWorkspaceTimeZoneResolver(workspaces, tenants);
        }

        public Guid TenantId { get; }
        public Guid WorkspaceId { get; }
        public FakeWorkspaces Workspaces { get; }
        public FakeTenantPlans Tenants { get; }
        public TaskWorkspaceTimeZoneResolver Resolver { get; }

        public static Fixture WithWorkspace(string? workspaceZone, string? tenantZone, Guid? workspaceTenantId = null)
        {
            var tenantId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var workspaces = new FakeWorkspaces
            {
                Items =
                {
                    [workspaceId] = new Workspace { TenantId = workspaceTenantId ?? tenantId, TimeZone = workspaceZone }
                }
            };
            var tenants = new FakeTenantPlans();
            if (tenantZone is not null)
                tenants.Settings[tenantId] = new TenantSettings { TenantId = tenantId, TimeZone = tenantZone };
            return new Fixture(tenantId, workspaceId, workspaces, tenants);
        }
    }

    private sealed class FakeWorkspaces : IWorkspaceRepository
    {
        public Dictionary<Guid, Workspace> Items { get; } = [];
        public CancellationToken LastToken { get; private set; }
        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Workspace>>([]);
        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default) { LastToken = cancellationToken; return Task.FromResult(Items.GetValueOrDefault(workspaceId)); }
        public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<WorkspaceMember?>(null);
        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceMember>>([]);
        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTenantPlans : ITenantPlanRepository
    {
        public Dictionary<Guid, TenantSettings> Settings { get; } = [];
        public CancellationToken LastToken { get; private set; }
        public Task<TenantSettings?> GetTenantSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default) { LastToken = cancellationToken; return Task.FromResult(Settings.GetValueOrDefault(tenantId)); }
        public Task<TenantSettings> GetOrCreateTenantSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Plan>> ListPlansAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Plan?> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddPlanAsync(Plan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Subscription?> GetActiveSubscriptionAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Subscription?> GetSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TenantUsageSnapshot> GetCurrentUsageAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
