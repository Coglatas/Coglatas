using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Security.Redaction;
using Coglatas.Application.TenantExports;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Tenancy;

[Trait("Scope", "WPC02E")]
public sealed class TenantExportServiceAuthorizationTests
{
    [Fact]
    public async Task Export_DiscardsBuiltArchive_WhenAuthorizationIsRevokedBeforeDelivery()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var exports = new RecordingExportRepository(tenantId);
        var authorization = new SequenceTenantAuthorizationService(true, true, false);
        var audit = new RecordingAuditLogger();
        var unitOfWork = new RecordingUnitOfWork();
        var service = new TenantExportService(
            exports,
            authorization,
            new EnabledFeatureFlagService(),
            new TestCurrentTenant(tenantId),
            new TestCurrentUser(userId),
            new TestClock(),
            audit,
            unitOfWork);

        var result = await service.ExportAsync(
            new TenantExportRequest(tenantId),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Tenant export could not be completed.", result.Error);
        Assert.True(exports.BuildCalled);
        Assert.NotNull(exports.AddedJob);
        Assert.Equal(ExportJobStatus.Failed, exports.AddedJob!.Status);
        Assert.Equal("Authorization changed before export delivery.", exports.AddedJob.ErrorMessage);
        Assert.Equal(3, authorization.CanManageCalls);
        Assert.Empty(audit.Entries);
        Assert.Equal(2, unitOfWork.SaveCalls);
    }

    private sealed class RecordingExportRepository(Guid tenantId) : ITenantExportRepository
    {
        public ExportJob? AddedJob { get; private set; }
        public bool BuildCalled { get; private set; }

        public Task<Tenant?> GetTenantAsync(Guid requestedTenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(requestedTenantId == tenantId ? new Tenant(tenantId) : null);

        public Task<ExportJob?> GetExportJobAsync(Guid exportJobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExportJob?>(null);

        public Task AddExportJobAsync(ExportJob exportJob, CancellationToken cancellationToken = default)
        {
            AddedJob = exportJob;
            return Task.CompletedTask;
        }

        public Task<byte[]> CreateMetadataZipAsync(
            Guid requestedTenantId,
            AuthorizationContext authorizationContext,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(tenantId, requestedTenantId);
            Assert.Equal(RedactionAuthorizationState.Allowed, authorizationContext.AuthorizationState);
            Assert.True(authorizationContext.FieldAccessPolicy.Allows(
                CanonicalDataClassification.Confidential,
                "email"));
            Assert.False(authorizationContext.FieldAccessPolicy.Allows(
                CanonicalDataClassification.Restricted,
                "healthNotes"));
            BuildCalled = true;
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }
    }

    private sealed class SequenceTenantAuthorizationService : ITenantAuthorizationService
    {
        private readonly Queue<bool> _responses;

        public SequenceTenantAuthorizationService(params bool[] canManageResponses)
        {
            _responses = new Queue<bool>(canManageResponses);
        }

        public int CanManageCalls { get; private set; }

        public Task<bool> CanAccessTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CanManageTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
        {
            CanManageCalls++;
            return Task.FromResult(_responses.Dequeue());
        }

        public Task<bool> IsPlatformAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CanSwitchTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class EnabledFeatureFlagService : IFeatureFlagService
    {
        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<Result> RequireEnabledAsync(string featureKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([FeatureKeys.TenantExport]);
    }

    private sealed class TestCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId => tenantId;
        public bool IsAvailable => true;
        public string TenantSlug => "tenant";
        public bool IsPlatformScope => false;
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.Empty;
        public string Email => "export-test@example.invalid";
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => true;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-08-20T00:00:00Z");
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int SaveCalls { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return Task.FromResult(1);
        }
    }
}
