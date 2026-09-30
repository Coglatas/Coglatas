using Coglatas.Application.Audit;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Audit;

public sealed class AuditAuthorizationServiceTests
{
    [Fact]
    public async Task TenantAdminGetsViewAndReviewButNotHigherRiskCapabilities()
    {
        var fixture = new Fixture(isTenantAdmin: true);

        var capabilities = await fixture.Service.GetCapabilitiesAsync();

        Assert.True(capabilities.CanView);
        Assert.True(capabilities.CanReview);
        Assert.False(capabilities.CanApprove);
        Assert.False(capabilities.CanExport);
        Assert.False(capabilities.CanViewSensitiveMetadata);
    }

    [Fact]
    public async Task ExplicitGrantsUnlockOnlyTheGrantedAuditCapabilities()
    {
        var fixture = new Fixture(
            grants:
            [
                CapabilityKeys.AuditView,
                CapabilityKeys.AuditExport
            ]);

        var capabilities = await fixture.Service.GetCapabilitiesAsync();

        Assert.True(capabilities.CanView);
        Assert.False(capabilities.CanReview);
        Assert.False(capabilities.CanApprove);
        Assert.True(capabilities.CanExport);
        Assert.False(capabilities.CanViewSensitiveMetadata);
    }

    [Fact]
    public async Task PlatformAdminGetsCompleteAuditCapabilitySet()
    {
        var fixture = new Fixture(systemRole: SystemRole.PlatformAdmin);

        var capabilities = await fixture.Service.GetCapabilitiesAsync();

        Assert.True(capabilities.CanView);
        Assert.True(capabilities.CanReview);
        Assert.True(capabilities.CanApprove);
        Assert.True(capabilities.CanExport);
        Assert.True(capabilities.CanViewSensitiveMetadata);
    }

    [Fact]
    public async Task DeniedOperationIsAuditedWithoutRequestOrSensitiveValues()
    {
        var fixture = new Fixture(grants: [CapabilityKeys.AuditView]);

        var result = await fixture.Service.AuthorizeAsync(
            CapabilityKeys.AuditSensitiveMetadataView,
            "audit.security-events.filter.identity");

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        var entry = Assert.Single(fixture.AuditLogger.Entries);
        Assert.Equal("AuditCapabilityDenied", entry.Action);
        Assert.Equal("AuditCapability", entry.EntityType);
        Assert.Equal("Audit operation denied.", entry.Summary);
        Assert.Null(entry.EntityId);
        Assert.Null(entry.IpAddress);
        Assert.Null(entry.UserAgent);
        Assert.Equal(2, entry.Metadata?.Count);
        Assert.Equal(CapabilityKeys.AuditSensitiveMetadataView, entry.Metadata?["capability"]);
        Assert.Equal("audit.security-events.filter.identity", entry.Metadata?["operation"]);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    private sealed class Fixture
    {
        private readonly Guid _userId = Guid.NewGuid();
        private readonly Guid _tenantId = Guid.NewGuid();

        public Fixture(
            bool isTenantAdmin = false,
            SystemRole systemRole = SystemRole.User,
            IReadOnlyCollection<string>? grants = null)
        {
            AuditLogger = new CapturingAuditLogger();
            UnitOfWork = new CapturingUnitOfWork();
            Service = new AuditAuthorizationService(
                new StubCurrentUser(_userId, systemRole),
                new StubCurrentTenant(_tenantId),
                new StubTenantAuthorizationService(isTenantAdmin),
                new StubCapabilityGrantEvaluator(grants ?? []),
                AuditLogger,
                UnitOfWork);
        }

        public AuditAuthorizationService Service { get; }
        public CapturingAuditLogger AuditLogger { get; }
        public CapturingUnitOfWork UnitOfWork { get; }
    }

    private sealed class StubCurrentUser(Guid userId, SystemRole systemRole) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string Email => "audit-test@example.invalid";
        public SystemRole? SystemRole { get; } = systemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class StubCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string TenantSlug => "audit-test";
        public bool IsPlatformScope => false;
    }

    private sealed class StubTenantAuthorizationService(bool canManageTenant) : ITenantAuthorizationService
    {
        public Task<bool> CanAccessTenantAsync(
            Guid userId,
            Guid tenantId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> CanManageTenantAsync(
            Guid userId,
            Guid tenantId,
            CancellationToken cancellationToken = default) => Task.FromResult(canManageTenant);

        public Task<bool> IsPlatformAdminAsync(
            Guid userId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> CanSwitchTenantAsync(
            Guid userId,
            Guid tenantId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubCapabilityGrantEvaluator(IReadOnlyCollection<string> grants) : ICapabilityGrantEvaluator
    {
        private readonly HashSet<string> _granted = new(grants, StringComparer.Ordinal);

        public Task<bool> HasActiveGrantAsync(
            Guid subjectUserId,
            Guid tenantId,
            string capabilityKey,
            CapabilityScopeType scopeType,
            Guid? scopeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_granted.Contains(capabilityKey));
        }
    }

    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }
}
