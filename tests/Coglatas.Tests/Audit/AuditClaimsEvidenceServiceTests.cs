using Coglatas.Application.Artifacts;
using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Audit;

public sealed class AuditClaimsEvidenceServiceTests
{
    [Fact]
    public async Task AuthorizedArtifactReturnsClaimEvidenceAndAuthorizedEventTrace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var eventId = Guid.NewGuid();
        fixture.AddClaim(
            ArtifactClaimSupportStatus.Contradicted,
            new ArtifactEvidence
            {
                TenantId = fixture.TenantId,
                Ordinal = 1,
                SourceKind = ArtifactEvidenceSourceKind.WebSnapshot,
                SourceReference = "https://example.invalid/source",
                SourceTitleSnapshot = "Authorized source",
                PassageSnapshot = "A bounded passage that contradicts the claim.",
                LocationSnapshot = "Section 2",
                SourceEventAuditId = eventId,
            });
        fixture.Context.AuditLogs.Add(new AuditLog
        {
            Id = eventId,
            TenantId = fixture.TenantId,
            Action = "SourceCaptured",
            EntityType = "ArtifactVersion",
            EntityId = fixture.Version.Id,
            Summary = "Source captured.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetAsync(fixture.Version.Id);

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        var claim = Assert.Single(result.Value!.Claims);
        Assert.True(claim.CitationPresent);
        Assert.Equal("Contradicted", claim.SupportStatus);
        var evidence = Assert.Single(claim.Evidence);
        Assert.Equal("Authorized source", evidence.SourceTitle);
        Assert.Equal("A bounded passage that contradicts the claim.", evidence.Passage);
        Assert.Equal(eventId, evidence.SourceEventAuditId);
    }

    [Fact]
    public async Task UnauthorizedEvidenceIsOmittedWithoutMetadataLeakage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deniedAttachmentId = Guid.NewGuid();
        fixture.AddClaim(
            ArtifactClaimSupportStatus.Insufficient,
            new ArtifactEvidence
            {
                TenantId = fixture.TenantId,
                Ordinal = 1,
                SourceKind = ArtifactEvidenceSourceKind.WebSnapshot,
                SourceReference = "web-ref",
                SourceTitleSnapshot = "Visible source",
                PassageSnapshot = "Visible passage",
            },
            new ArtifactEvidence
            {
                TenantId = fixture.TenantId,
                Ordinal = 2,
                SourceKind = ArtifactEvidenceSourceKind.FileAttachment,
                SourceReference = deniedAttachmentId.ToString("D"),
                SourceTitleSnapshot = "PROTECTED-SOURCE-TITLE",
                PassageSnapshot = "PROTECTED-SOURCE-PASSAGE",
                LocationSnapshot = "PROTECTED-SOURCE-LOCATION",
            });
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetAsync(fixture.Version.Id);

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        var evidence = Assert.Single(Assert.Single(result.Value!.Claims).Evidence);
        Assert.Equal("Visible source", evidence.SourceTitle);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("PROTECTED-SOURCE", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(deniedAttachmentId.ToString("D"), serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingEventIdIsNotProjectedAsTraceTarget()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddClaim(
            ArtifactClaimSupportStatus.Supported,
            new ArtifactEvidence
            {
                TenantId = fixture.TenantId,
                Ordinal = 1,
                SourceKind = ArtifactEvidenceSourceKind.WebSnapshot,
                SourceReference = "web-ref",
                PassageSnapshot = "Passage",
                SourceEventAuditId = Guid.NewGuid(),
            });
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetAsync(fixture.Version.Id);

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Null(Assert.Single(Assert.Single(result.Value!.Claims).Evidence).SourceEventAuditId);
    }

    [Fact]
    public async Task CrossTenantEventIdIsNotProjectedEvenInPlatformScope()
    {
        await using var fixture = await Fixture.CreateAsync(platformScope: true);
        var crossTenantId = Guid.NewGuid();
        var crossTenantEventId = Guid.NewGuid();
        fixture.Context.Tenants.Add(new Tenant(crossTenantId)
        {
            Name = "Other tenant",
            Slug = "other-tenant",
            DisplayName = "Other tenant",
            Status = TenantStatus.Active,
        });
        fixture.AddClaim(
            ArtifactClaimSupportStatus.Supported,
            new ArtifactEvidence
            {
                TenantId = fixture.TenantId,
                Ordinal = 1,
                SourceKind = ArtifactEvidenceSourceKind.WebSnapshot,
                SourceReference = "web-ref",
                PassageSnapshot = "Passage",
                SourceEventAuditId = crossTenantEventId,
            });
        fixture.Context.AuditLogs.Add(new AuditLog
        {
            Id = crossTenantEventId,
            TenantId = crossTenantId,
            Action = "OtherTenantSourceCaptured",
            EntityType = "ArtifactVersion",
            EntityId = Guid.NewGuid(),
            Summary = "Other tenant source captured.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetAsync(fixture.Version.Id);

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        var evidence = Assert.Single(Assert.Single(result.Value!.Claims).Evidence);
        Assert.Null(evidence.SourceEventAuditId);
    }

    [Fact]
    public async Task AuditViewIsCheckedBeforeArtifactAuthorization()
    {
        await using var fixture = await Fixture.CreateAsync(canViewAudit: false);

        var result = await fixture.Service.GetAsync(fixture.Version.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        Assert.Equal(1, fixture.AuditAuthorization.AuthorizeCalls);
        Assert.Equal(0, fixture.ArtifactAuthorization.ViewCalls);
    }

    [Fact]
    public async Task ArtifactAuthorizationFailureUsesGenericNotAvailableBoundary()
    {
        await using var fixture = await Fixture.CreateAsync(canViewArtifact: false);

        var result = await fixture.Service.GetAsync(fixture.Version.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("ArtifactVersionNotFound", result.ErrorDetail?.Code);
        Assert.DoesNotContain(fixture.Version.Id.ToString("D"), result.ErrorDetail!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Artifact.Name, result.ErrorDetail.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            Guid tenantId,
            AppDbContext context,
            Artifact artifact,
            ArtifactVersion version,
            StubArtifactAuthorization artifactAuthorization,
            StubAuditAuthorization auditAuthorization,
            DbAuditClaimsEvidenceService service)
        {
            TenantId = tenantId;
            Context = context;
            Artifact = artifact;
            Version = version;
            ArtifactAuthorization = artifactAuthorization;
            AuditAuthorization = auditAuthorization;
            Service = service;
        }

        public Guid TenantId { get; }
        public AppDbContext Context { get; }
        public Artifact Artifact { get; }
        public ArtifactVersion Version { get; }
        public StubArtifactAuthorization ArtifactAuthorization { get; }
        public StubAuditAuthorization AuditAuthorization { get; }
        public DbAuditClaimsEvidenceService Service { get; }

        public static async Task<Fixture> CreateAsync(
            bool canViewAudit = true,
            bool canViewArtifact = true,
            bool platformScope = false)
        {
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var currentTenant = new StubCurrentTenant(tenantId, platformScope);
            var context = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options,
                currentTenant);
            context.Tenants.Add(new Tenant(tenantId)
            {
                Name = "Audit test tenant",
                Slug = "audit-test",
                DisplayName = "Audit test tenant",
                Status = TenantStatus.Active,
            });
            await context.SaveChangesAsync();

            var artifact = new Artifact
            {
                TenantId = tenantId,
                ProjectId = Guid.NewGuid(),
                Name = "Research report",
                ArtifactType = ArtifactType.Other,
                Status = ArtifactStatus.Draft,
                CreatedByUserId = userId,
            };
            var version = new ArtifactVersion
            {
                TenantId = tenantId,
                ArtifactId = artifact.Id,
                Artifact = artifact,
                VersionNumber = 1,
                CreatedByUserId = userId,
            };
            var attachment = new Attachment
            {
                TenantId = tenantId,
                FileObjectId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                OwnerType = AttachmentOwnerType.ArtifactVersion,
                OwnerId = version.Id,
                OwnerUserId = userId,
                UploadedByUserId = userId,
                FileName = "research-report.pdf",
                StoredFileName = "research-report.pdf",
                FilePath = "test/research-report.pdf",
                ContentType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                StorageProvider = "Test",
                StorageKey = "test/research-report.pdf",
                ScanStatus = FileScanStatus.Skipped,
            };
            version.AttachmentId = attachment.Id;
            version.Attachment = attachment;
            context.Artifacts.Add(artifact);
            context.Attachments.Add(attachment);
            context.ArtifactVersions.Add(version);
            await context.SaveChangesAsync();

            var artifactAuthorization = new StubArtifactAuthorization(canViewArtifact);
            var auditAuthorization = new StubAuditAuthorization(canViewAudit);
            var service = new DbAuditClaimsEvidenceService(
                context,
                new ArtifactRepository(context),
                new ArtifactEvidenceRepository(context),
                artifactAuthorization,
                new FileRepository(context),
                new StubFileAuthorization(),
                auditAuthorization,
                new StubCurrentUser(userId));

            return new Fixture(tenantId, context, artifact, version, artifactAuthorization, auditAuthorization, service);
        }

        public void AddClaim(ArtifactClaimSupportStatus supportStatus, params ArtifactEvidence[] evidence)
        {
            var claim = new ArtifactClaim
            {
                TenantId = TenantId,
                ArtifactVersionId = Version.Id,
                ArtifactVersion = Version,
                Ordinal = 1,
                Text = "The audited claim.",
                CitationPresent = true,
                SupportStatus = supportStatus,
                ReviewStatus = ArtifactClaimReviewStatus.Reviewed,
            };
            foreach (var item in evidence)
            {
                item.ArtifactClaimId = claim.Id;
                item.ArtifactClaim = claim;
                claim.Evidence.Add(item);
            }
            Context.Set<ArtifactClaim>().Add(claim);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class StubCurrentTenant(Guid tenantId, bool isPlatformScope) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string? TenantSlug => isPlatformScope ? null : "audit-test";
        public bool IsPlatformScope { get; } = isPlatformScope;
    }

    private sealed class StubCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string? Email => "audit@example.invalid";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class StubAuditAuthorization(bool canView) : IAuditAuthorizationService
    {
        public int AuthorizeCalls { get; private set; }
        public Task<AuditCapabilityResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditCapabilityResponse(canView, false, false, false, false));
        public Task<Result> AuthorizeAsync(string capabilityKey, string operation, CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            return Task.FromResult(canView
                ? Result.Success()
                : Result.Failure(new ApplicationErrorDetail("CapabilityDenied", "Audit operation denied.")));
        }
    }

    private sealed class StubArtifactAuthorization(bool canView) : IArtifactAuthorizationService
    {
        public int ViewCalls { get; private set; }
        public Task<bool> CanViewArtifact(Guid userId, Guid artifactId, CancellationToken cancellationToken = default)
        {
            ViewCalls++;
            return Task.FromResult(canView);
        }
        public Task<bool> CanUploadArtifact(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanUpdateArtifact(Guid userId, Guid artifactId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanDownloadArtifactVersion(Guid userId, Guid versionId, CancellationToken cancellationToken = default) => Task.FromResult(canView);
    }

    private sealed class StubFileAuthorization : IFileAuthorizationService
    {
        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(Guid userId, Guid workspaceId, IReadOnlyCollection<Attachment> attachments, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
