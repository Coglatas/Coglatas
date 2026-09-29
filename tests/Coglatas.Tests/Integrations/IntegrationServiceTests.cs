using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Integrations;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Security;

namespace Coglatas.Tests.Integrations;

public sealed class IntegrationServiceTests
{
    [Fact]
    public async Task ApiTokenRawValueIsReturnedOnlyAtCreation()
    {
        var fixture = IntegrationFixture.Create();

        var created = await fixture.Service.CreateApiTokenAsync(new CreateApiTokenRequest("Roster sync", "[\"read:projects\"]", fixture.Clock.UtcNow.AddDays(7)));
        var listed = await fixture.Service.ListApiTokensAsync();

        Assert.True(created.IsSuccess);
        Assert.StartsWith("coglatas_", created.Value!.RawToken);
        Assert.True(listed.IsSuccess);
        Assert.Single(listed.Value!);
        Assert.DoesNotContain(created.Value.RawToken, listed.Value![0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokedTokenFailsValidation()
    {
        var fixture = IntegrationFixture.Create();
        var created = await fixture.Service.CreateApiTokenAsync(new CreateApiTokenRequest("Build bot", "[]", fixture.Clock.UtcNow.AddDays(1)));

        await fixture.Service.RevokeApiTokenAsync(created.Value!.Token.Id);
        var validation = await fixture.Validator.ValidateAsync(created.Value.RawToken);

        Assert.False(validation.IsValid);
        Assert.Equal("API token is revoked.", validation.FailureReason);
    }

    [Fact]
    public async Task ExpiredTokenFailsValidation()
    {
        var fixture = IntegrationFixture.Create();
        var rawToken = "coglatas_expired";
        fixture.Integrations.Tokens.Add(new ApiToken
        {
            TenantId = fixture.TenantId,
            Name = "Expired",
            TokenHash = fixture.TokenHasher.HashToken(rawToken),
            ScopesJson = "[]",
            ExpiresAt = fixture.Clock.UtcNow.AddMinutes(-1),
            CreatedByUserId = fixture.UserId
        });

        var validation = await fixture.Validator.ValidateAsync(rawToken);

        Assert.False(validation.IsValid);
        Assert.Equal("API token is expired.", validation.FailureReason);
    }

    [Fact]
    public async Task FeatureDisabledBlocksWebhookCreation()
    {
        var fixture = IntegrationFixture.Create();
        fixture.Features.Disabled.Add(FeatureKeys.WebhookIntegration);

        var result = await fixture.Service.CreateWebhookAsync(new CreateWebhookEndpointRequest("Notify", "https://example.com/hook", "secret", "[]"));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task HttpWebhookUrlIsRejected()
    {
        var fixture = IntegrationFixture.Create();

        var result = await fixture.Service.CreateWebhookAsync(new CreateWebhookEndpointRequest("Notify", "http://example.com/hook", null, "[]"));

        Assert.False(result.IsSuccess);
        Assert.Equal("Webhook URL must use HTTPS.", result.Error);
    }

    private sealed class IntegrationFixture
    {
        private IntegrationFixture()
        {
            var user = new User
            {
                DisplayName = "Tenant admin",
                Email = "admin@example.com",
                NormalizedEmail = "ADMIN@EXAMPLE.COM",
                PasswordHash = "hash",
                Status = UserStatus.Active
            };
            UserId = user.Id;
            Tenants.Users[UserId] = user;
            Tenants.Memberships.Add(new TenantUser
            {
                TenantId = TenantId,
                UserId = UserId,
                Role = TenantUserRole.Admin,
                Status = TenantUserStatus.Active
            });

            Service = new IntegrationService(
                Integrations,
                new TenantAuthorizationService(Tenants),
                Features,
                CurrentTenant,
                CurrentUser,
                Clock,
                TokenHasher,
                Audit,
                UnitOfWork);
            Validator = Service;
        }

        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid UserId { get; }
        public FakeIntegrationRepository Integrations { get; } = new();
        private FakeTenantRepository Tenants { get; } = new();
        public FakeFeatureFlags Features { get; } = new();
        public FakeClock Clock { get; } = new();
        public Sha256TokenHasher TokenHasher { get; } = new();
        private FakeAuditLogger Audit { get; } = new();
        private FakeUnitOfWork UnitOfWork { get; } = new();
        public IntegrationService Service { get; }
        public IApiTokenValidator Validator { get; }
        private FakeCurrentTenant CurrentTenant => new(TenantId);
        private FakeCurrentUser CurrentUser => new(UserId);

        public static IntegrationFixture Create() => new();
    }

    private sealed class FakeIntegrationRepository : IIntegrationRepository
    {
        private List<IntegrationAccount> Accounts { get; } = [];
        private List<WebhookEndpoint> Webhooks { get; } = [];
        public List<ApiToken> Tokens { get; } = [];
        public Guid TenantId { get; set; }

        public Task<IReadOnlyList<IntegrationAccount>> ListIntegrationAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IntegrationAccount>>(Accounts);
        public Task<IntegrationAccount?> GetIntegrationAccountAsync(Guid integrationId, CancellationToken cancellationToken = default) => Task.FromResult(Accounts.FirstOrDefault(account => account.Id == integrationId));
        public Task AddIntegrationAccountAsync(IntegrationAccount integrationAccount, CancellationToken cancellationToken = default) { Accounts.Add(integrationAccount); return Task.CompletedTask; }
        public Task<IReadOnlyList<WebhookEndpoint>> ListWebhookEndpointsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WebhookEndpoint>>(Webhooks);
        public Task<WebhookEndpoint?> GetWebhookEndpointAsync(Guid webhookId, CancellationToken cancellationToken = default) => Task.FromResult(Webhooks.FirstOrDefault(webhook => webhook.Id == webhookId));
        public Task AddWebhookEndpointAsync(WebhookEndpoint webhookEndpoint, CancellationToken cancellationToken = default) { Webhooks.Add(webhookEndpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<ApiToken>> ListApiTokensAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ApiToken>>(Tokens);
        public Task<ApiToken?> GetApiTokenAsync(Guid tokenId, CancellationToken cancellationToken = default) => Task.FromResult(Tokens.FirstOrDefault(token => token.Id == tokenId));
        public Task<ApiToken?> GetApiTokenByHashAsync(string tokenHash, CancellationToken cancellationToken = default) => Task.FromResult(Tokens.FirstOrDefault(token => token.TokenHash == tokenHash));
        public Task AddApiTokenAsync(ApiToken token, CancellationToken cancellationToken = default) { Tokens.Add(token); return Task.CompletedTask; }
    }

    private sealed class FakeTenantRepository : ITenantRepository
    {
        public Dictionary<Guid, User> Users { get; } = [];
        public List<TenantUser> Memberships { get; } = [];
        public Task<IReadOnlyList<Tenant>> ListTenantsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Tenant>>([]);
        public Task<Tenant?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult<Tenant?>(new Tenant(tenantId) { Name = "Tenant", Slug = "tenant", DisplayName = "Tenant" });
        public Task<Tenant?> GetTenantBySlugAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult<Tenant?>(null);
        public Task<Tenant?> GetTenantByPrimaryDomainAsync(string primaryDomain, CancellationToken cancellationToken = default) => Task.FromResult<Tenant?>(null);
        public Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TenantUser>> ListTenantUsersAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TenantUser>>(Memberships.Where(item => item.TenantId == tenantId).ToList());
        public Task<IReadOnlyList<TenantUser>> ListUserTenantMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TenantUser>>(Memberships.Where(item => item.UserId == userId).ToList());
        public Task<TenantUser?> GetTenantUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Memberships.FirstOrDefault(item => item.TenantId == tenantId && item.UserId == userId));
        public Task AddTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken = default) { Memberships.Add(tenantUser); return Task.CompletedTask; }
        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Users.GetValueOrDefault(userId));
    }

    private sealed class FakeFeatureFlags : IFeatureFlagService
    {
        public HashSet<string> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken cancellationToken = default) => Task.FromResult(!Disabled.Contains(featureKey));
        public async Task<Result> RequireEnabledAsync(string featureKey, CancellationToken cancellationToken = default) => await IsEnabledAsync(featureKey, cancellationToken) ? Result.Success() : Result.Failure($"Feature '{featureKey}' is disabled for this tenant.");
        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(FeatureKeys.All.Where(key => !Disabled.Contains(key)).ToList());
    }

    private sealed record FakeCurrentTenant(Guid TenantId) : ICurrentTenant
    {
        public bool IsAvailable => true;
        public string TenantSlug => "tenant";
        public bool IsPlatformScope => false;
    }

    private sealed record FakeCurrentUser(Guid UserIdValue) : ICurrentUser
    {
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => null;
        public string Email => "admin@example.com";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; } = new(2026, 6, 7, 0, 0, 0, TimeSpan.Zero); }
    private sealed class FakeAuditLogger : IAuditLogger { public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class FakeUnitOfWork : IUnitOfWork { public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1); }
}
