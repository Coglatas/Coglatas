using Coglatas.Application.Artifacts;
using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Audit;

public sealed class ArtifactEvidenceManifestServiceTests
{
    [Fact]
    public async Task AuditReviewDenialHappensBeforeArtifactLookup()
    {
        await using var fixture = await Fixture.CreateAsync(canReview: false, canUpdateArtifact: true);
        var recordingArtifacts = new RecordingArtifactRepository(fixture.Artifacts);
        var service = fixture.CreateService(recordingArtifacts);

        var result = await service.AttachAsync(fixture.Version.Id, WebSnapshotRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        Assert.Equal(1, fixture.AuditAuthorization.AuthorizeCalls);
        Assert.Equal(CapabilityKeys.AuditReview, fixture.AuditAuthorization.LastCapabilityKey);
        Assert.Equal(0, recordingArtifacts.GetVersionCalls);
        Assert.Equal(0, fixture.ArtifactAuthorization.UpdateCalls);
        Assert.Empty(fixture.AuditLogger.Entries);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task ArtifactUpdateDenialUsesGenericNotAvailableBoundary()
    {
        await using var fixture = await Fixture.CreateAsync(canReview: true, canUpdateArtifact: false);

        var result = await fixture.Service.AttachAsync(fixture.Version.Id, WebSnapshotRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ArtifactVersionNotFound", result.ErrorDetail?.Code);
        Assert.DoesNotContain(fixture.Version.Id.ToString("D"), result.ErrorDetail!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Artifact.Name, result.ErrorDetail.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.ArtifactAuthorization.UpdateCalls);
        Assert.False(await fixture.Evidence.HasClaimsAsync(fixture.Version.Id));
        Assert.Empty(fixture.AuditLogger.Entries);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task UnauthorizedFileSourceIsRejectedWithoutPersistingManifest()
    {
        await using var fixture = await Fixture.CreateAsync(
            canReview: true,
            canUpdateArtifact: true,
            canViewFile: false);
        var sourceAttachmentId = fixture.Version.Attachment!.Id;

        var result = await fixture.Service.AttachAsync(
            fixture.Version.Id,
            RequestForSource(
                ArtifactEvidenceSourceKind.FileAttachment.ToString(),
                sourceAttachmentId.ToString("D")));

        Assert.False(result.IsSuccess);
        Assert.Equal("SourceNotAuthorized", result.ErrorDetail?.Code);
        Assert.Equal(1, fixture.FileAuthorization.ViewCalls);
        Assert.False(await fixture.Evidence.HasClaimsAsync(fixture.Version.Id));
        Assert.Empty(fixture.AuditLogger.Entries);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task UnauthorizedArtifactVersionSourceIsRejectedWithoutPersistingManifest()
    {
        await using var fixture = await Fixture.CreateAsync(
            canReview: true,
            canUpdateArtifact: true,
            canDownloadArtifactVersion: false);

        var result = await fixture.Service.AttachAsync(
            fixture.Version.Id,
            RequestForSource(
                ArtifactEvidenceSourceKind.ArtifactVersion.ToString(),
                fixture.SecondVersion.Id.ToString("D")));

        Assert.False(result.IsSuccess);
        Assert.Equal("SourceNotAuthorized", result.ErrorDetail?.Code);
        Assert.Equal(1, fixture.ArtifactAuthorization.DownloadCalls);
        Assert.False(await fixture.Evidence.HasClaimsAsync(fixture.Version.Id));
        Assert.Empty(fixture.AuditLogger.Entries);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task SecondAttachIsRejectedAndFirstManifestRemainsImmutable()
    {
        await using var fixture = await Fixture.CreateAsync(canReview: true, canUpdateArtifact: true);
        var firstRequest = WebSnapshotRequest(
            claimText: "First immutable claim.",
            passage: "First immutable passage.");

        var first = await fixture.Service.AttachAsync(fixture.Version.Id, firstRequest);
        var second = await fixture.Service.AttachAsync(
            fixture.Version.Id,
            WebSnapshotRequest(
                claimText: "Replacement claim must not be stored.",
                passage: "Replacement passage must not be stored."));

        Assert.True(first.IsSuccess, first.Error ?? first.ErrorDetail?.Message);
        Assert.False(second.IsSuccess);
        Assert.Equal("EvidenceManifestAlreadyAttached", second.ErrorDetail?.Code);
        var claim = Assert.Single(await fixture.Evidence.ListClaimsAsync(fixture.Version.Id));
        Assert.Equal("First immutable claim.", claim.Text);
        Assert.Equal("First immutable passage.", Assert.Single(claim.Evidence).PassageSnapshot);
        Assert.DoesNotContain(
            (await fixture.Evidence.ListClaimsAsync(fixture.Version.Id)).Select(item => item.Text),
            text => text.Contains("Replacement", StringComparison.Ordinal));
        Assert.Single(fixture.AuditLogger.Entries);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task SuccessfulAttachPersistsOnlyToRequestedArtifactVersionAndAuditsTheCommit()
    {
        await using var fixture = await Fixture.CreateAsync(canReview: true, canUpdateArtifact: true);
        var request = WebSnapshotRequest(
            claimText: "Artifact version one claim.",
            passage: "Bounded evidence passage for version one.");

        var result = await fixture.Service.AttachAsync(fixture.Version.Id, request);

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Equal(fixture.Version.Id, result.Value!.ArtifactVersionId);
        Assert.Equal(1, result.Value.ClaimCount);

        var claim = Assert.Single(await fixture.Evidence.ListClaimsAsync(fixture.Version.Id));
        Assert.Equal(fixture.TenantId, claim.TenantId);
        Assert.Equal(fixture.Version.Id, claim.ArtifactVersionId);
        Assert.Equal(1, claim.Ordinal);
        Assert.Equal("Artifact version one claim.", claim.Text);
        Assert.True(claim.CitationPresent);
        Assert.Equal(ArtifactClaimSupportStatus.Supported, claim.SupportStatus);
        Assert.Equal(ArtifactClaimReviewStatus.Reviewed, claim.ReviewStatus);

        var evidence = Assert.Single(claim.Evidence);
        Assert.Equal(fixture.TenantId, evidence.TenantId);
        Assert.Equal(ArtifactEvidenceSourceKind.WebSnapshot, evidence.SourceKind);
        Assert.Equal("web:test-source", evidence.SourceReference);
        Assert.Equal("Authorized source", evidence.SourceTitleSnapshot);
        Assert.Equal("Bounded evidence passage for version one.", evidence.PassageSnapshot);
        Assert.Equal("Section 1", evidence.LocationSnapshot);

        Assert.Empty(await fixture.Evidence.ListClaimsAsync(fixture.SecondVersion.Id));
        var audit = Assert.Single(fixture.AuditLogger.Entries);
        Assert.Equal("ArtifactClaimsEvidenceAttached", audit.Action);
        Assert.Equal("ArtifactVersion", audit.EntityType);
        Assert.Equal(fixture.Version.Id, audit.EntityId);
        Assert.Equal(fixture.TenantId, audit.TenantId);
        Assert.NotNull(audit.Metadata);
        Assert.Equal(1, audit.Metadata!["claimCount"]);
        Assert.Equal("artifact-claims-evidence-v1", audit.Metadata["schema"]);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task AttachAcceptsTheExactClaimExecutionLimit()
    {
        await using var fixture = await Fixture.CreateAsync(canReview: true, canUpdateArtifact: true);
        var request = new AttachArtifactEvidenceManifestRequest(
            Enumerable.Range(1, 200)
                .Select(ordinal => new ArtifactClaimManifestItem(
                    ordinal,
                    $"Bounded claim {ordinal}.",
                    true,
                    ArtifactClaimSupportStatus.Supported.ToString(),
                    ArtifactClaimReviewStatus.Reviewed.ToString(),
                    [new ArtifactEvidenceManifestItem(
                        1,
                        ArtifactEvidenceSourceKind.WebSnapshot.ToString(),
                        $"web:bounded-{ordinal}",
                        "Authorized source",
                        "Bounded evidence passage.",
                        "Section 1",
                        null)]))
                .ToArray());

        var result = await fixture.Service.AttachAsync(fixture.Version.Id, request);

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Equal(200, result.Value!.ClaimCount);
        Assert.Equal(200, (await fixture.Evidence.ListClaimsAsync(fixture.Version.Id)).Count);
    }

    private static AttachArtifactEvidenceManifestRequest WebSnapshotRequest(
        string claimText = "Audited claim.",
        string passage = "Bounded evidence passage.") =>
        RequestForSource(
            ArtifactEvidenceSourceKind.WebSnapshot.ToString(),
            "web:test-source",
            claimText,
            passage);

    private static AttachArtifactEvidenceManifestRequest RequestForSource(
        string sourceKind,
        string sourceReference,
        string claimText = "Audited claim.",
        string passage = "Bounded evidence passage.") =>
        new(new[]
        {
            new ArtifactClaimManifestItem(
                1,
                claimText,
                true,
                ArtifactClaimSupportStatus.Supported.ToString(),
                ArtifactClaimReviewStatus.Reviewed.ToString(),
                new[]
                {
                    new ArtifactEvidenceManifestItem(
                        1,
                        sourceKind,
                        sourceReference,
                        "Authorized source",
                        passage,
                        "Section 1",
                        null)
                })
        });

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            Guid tenantId,
            AppDbContext context,
            Artifact artifact,
            ArtifactVersion version,
            ArtifactVersion secondVersion,
            ArtifactRepository artifacts,
            ArtifactEvidenceRepository evidence,
            FileRepository files,
            StubArtifactAuthorization artifactAuthorization,
            StubFileAuthorization fileAuthorization,
            StubAuditAuthorization auditAuthorization,
            StubCurrentUser currentUser,
            FakeAuditLogger auditLogger,
            DbUnitOfWork unitOfWork)
        {
            TenantId = tenantId;
            Context = context;
            Artifact = artifact;
            Version = version;
            SecondVersion = secondVersion;
            Artifacts = artifacts;
            Evidence = evidence;
            Files = files;
            ArtifactAuthorization = artifactAuthorization;
            FileAuthorization = fileAuthorization;
            AuditAuthorization = auditAuthorization;
            CurrentUser = currentUser;
            AuditLogger = auditLogger;
            UnitOfWork = unitOfWork;
            Service = CreateService();
        }

        public Guid TenantId { get; }
        public AppDbContext Context { get; }
        public Artifact Artifact { get; }
        public ArtifactVersion Version { get; }
        public ArtifactVersion SecondVersion { get; }
        public ArtifactRepository Artifacts { get; }
        public ArtifactEvidenceRepository Evidence { get; }
        public FileRepository Files { get; }
        public StubArtifactAuthorization ArtifactAuthorization { get; }
        public StubFileAuthorization FileAuthorization { get; }
        public StubAuditAuthorization AuditAuthorization { get; }
        public StubCurrentUser CurrentUser { get; }
        public FakeAuditLogger AuditLogger { get; }
        public DbUnitOfWork UnitOfWork { get; }
        public ArtifactEvidenceManifestService Service { get; }

        public static async Task<Fixture> CreateAsync(
            bool canReview,
            bool canUpdateArtifact,
            bool canDownloadArtifactVersion = true,
            bool canViewFile = true)
        {
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var currentTenant = new StubCurrentTenant(tenantId);
            var context = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options,
                currentTenant);

            context.Tenants.Add(new Tenant(tenantId)
            {
                Name = "Evidence manifest test tenant",
                Slug = "evidence-manifest-test",
                DisplayName = "Evidence manifest test tenant",
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
            var (version, attachment, fileObject) = CreateVersion(tenantId, userId, artifact, 1);
            var (secondVersion, secondAttachment, secondFileObject) = CreateVersion(tenantId, userId, artifact, 2);

            context.Artifacts.Add(artifact);
            context.FileObjects.AddRange(fileObject, secondFileObject);
            context.Attachments.AddRange(attachment, secondAttachment);
            context.ArtifactVersions.AddRange(version, secondVersion);
            await context.SaveChangesAsync();

            var artifacts = new ArtifactRepository(context);
            var evidence = new ArtifactEvidenceRepository(context);
            var files = new FileRepository(context);
            var artifactAuthorization = new StubArtifactAuthorization(canUpdateArtifact, canDownloadArtifactVersion);
            var fileAuthorization = new StubFileAuthorization(canViewFile);
            var auditAuthorization = new StubAuditAuthorization(canReview);
            var currentUser = new StubCurrentUser(userId);
            var auditLogger = new FakeAuditLogger();
            var unitOfWork = new DbUnitOfWork(context);

            return new Fixture(
                tenantId,
                context,
                artifact,
                version,
                secondVersion,
                artifacts,
                evidence,
                files,
                artifactAuthorization,
                fileAuthorization,
                auditAuthorization,
                currentUser,
                auditLogger,
                unitOfWork);
        }

        public ArtifactEvidenceManifestService CreateService(IArtifactRepository? artifactRepository = null) =>
            new(
                artifactRepository ?? Artifacts,
                Evidence,
                ArtifactAuthorization,
                Files,
                FileAuthorization,
                AuditAuthorization,
                CurrentUser,
                AuditLogger,
                UnitOfWork);

        public ValueTask DisposeAsync() => Context.DisposeAsync();

        private static (ArtifactVersion Version, Attachment Attachment, FileObject FileObject) CreateVersion(
            Guid tenantId,
            Guid userId,
            Artifact artifact,
            int versionNumber)
        {
            var version = new ArtifactVersion
            {
                TenantId = tenantId,
                ArtifactId = artifact.Id,
                Artifact = artifact,
                VersionNumber = versionNumber,
                CreatedByUserId = userId,
            };
            var workspaceId = Guid.NewGuid();
            var fileObject = new FileObject
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = artifact.ProjectId,
                UploadedByUserId = userId,
                OriginalFileName = $"research-report-v{versionNumber}.pdf",
                StorageKey = $"test/research-report-v{versionNumber}.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1,
                Status = FileObjectStatus.Active,
            };
            var attachment = new Attachment
            {
                TenantId = tenantId,
                FileObjectId = fileObject.Id,
                FileObject = fileObject,
                WorkspaceId = workspaceId,
                OwnerType = AttachmentOwnerType.ArtifactVersion,
                OwnerId = version.Id,
                OwnerUserId = userId,
                UploadedByUserId = userId,
                FileName = $"research-report-v{versionNumber}.pdf",
                StoredFileName = $"research-report-v{versionNumber}.pdf",
                FilePath = $"test/research-report-v{versionNumber}.pdf",
                ContentType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                StorageProvider = "Test",
                StorageKey = $"test/research-report-v{versionNumber}.pdf",
                ScanStatus = FileScanStatus.Skipped,
            };
            version.AttachmentId = attachment.Id;
            version.Attachment = attachment;
            version.FileObjectId = fileObject.Id;
            version.FileObject = fileObject;
            return (version, attachment, fileObject);
        }
    }

    private sealed class RecordingArtifactRepository(IArtifactRepository inner) : IArtifactRepository
    {
        public int GetVersionCalls { get; private set; }

        public Task<IReadOnlyList<Artifact>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            inner.ListByProjectAsync(projectId, cancellationToken);

        public Task<Artifact?> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default) =>
            inner.GetArtifactAsync(artifactId, cancellationToken);

        public Task<ArtifactVersion?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
        {
            GetVersionCalls++;
            return inner.GetVersionAsync(versionId, cancellationToken);
        }

        public Task<IReadOnlyList<ArtifactVersion>> ListVersionsAsync(Guid artifactId, CancellationToken cancellationToken = default) =>
            inner.ListVersionsAsync(artifactId, cancellationToken);

        public Task<int> GetNextVersionNumberAsync(Guid artifactId, CancellationToken cancellationToken = default) =>
            inner.GetNextVersionNumberAsync(artifactId, cancellationToken);

        public Task AddArtifactAsync(Artifact artifact, CancellationToken cancellationToken = default) =>
            inner.AddArtifactAsync(artifact, cancellationToken);

        public Task AddVersionAsync(ArtifactVersion version, CancellationToken cancellationToken = default) =>
            inner.AddVersionAsync(version, cancellationToken);
    }

    private sealed class StubCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string? TenantSlug => "evidence-manifest-test";
        public bool IsPlatformScope => false;
    }

    private sealed class StubCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string? Email => "audit-reviewer@example.invalid";
        public SystemRole? SystemRole => global::Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class StubAuditAuthorization(bool canReview) : IAuditAuthorizationService
    {
        public int AuthorizeCalls { get; private set; }
        public string? LastCapabilityKey { get; private set; }

        public Task<AuditCapabilityResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditCapabilityResponse(true, canReview, false, false, false));

        public Task<Result> AuthorizeAsync(
            string capabilityKey,
            string operation,
            CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            LastCapabilityKey = capabilityKey;
            return Task.FromResult(canReview && capabilityKey == CapabilityKeys.AuditReview
                ? Result.Success()
                : Result.Failure(new ApplicationErrorDetail("CapabilityDenied", "Audit review is not permitted.")));
        }
    }

    private sealed class StubArtifactAuthorization(bool canUpdateArtifact, bool canDownloadArtifactVersion) : IArtifactAuthorizationService
    {
        public int UpdateCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<bool> CanViewArtifact(Guid userId, Guid artifactId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CanUploadArtifact(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CanUpdateArtifact(Guid userId, Guid artifactId, CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            return Task.FromResult(canUpdateArtifact);
        }

        public Task<bool> CanDownloadArtifactVersion(Guid userId, Guid versionId, CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.FromResult(canDownloadArtifactVersion);
        }
    }

    private sealed class StubFileAuthorization(bool canView) : IFileAuthorizationService
    {
        public int ViewCalls { get; private set; }

        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default)
        {
            ViewCalls++;
            return Task.FromResult(canView);
        }

        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(
            Guid userId,
            Guid workspaceId,
            IReadOnlyCollection<Attachment> attachments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = new();

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class DbUnitOfWork(AppDbContext context) : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return await context.SaveChangesAsync(cancellationToken);
        }
    }
}
