using Coglatas.Application.Artifacts;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Artifacts;

public sealed class ArtifactReportServiceTests
{
    [Fact]
    public async Task AttachAsync_RejectsZeroLengthCitationWithoutPersistingReport()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.AttachAsync(
            fixture.Version.Id,
            new AttachArtifactReportRequest(
                "Evidence report",
                [new ArtifactReportSectionRequest(
                    null,
                    1,
                    "Summary",
                    "Claim text",
                    [new ArtifactReportCitationRequest(1, 0, 0, fixture.Claim.Id)])]));

        Assert.False(result.IsSuccess);
        Assert.Equal("ValidationFailed", result.ErrorDetail?.Code);
        Assert.Empty(fixture.Context.ArtifactReportDocuments);
    }

    [Fact]
    public async Task AttachAndGetAsync_ReturnsServerSplitCitationRun()
    {
        await using var fixture = await Fixture.CreateAsync();

        var attached = await fixture.Service.AttachAsync(
            fixture.Version.Id,
            new AttachArtifactReportRequest(
                "Evidence report",
                [new ArtifactReportSectionRequest(
                    null,
                    1,
                    "Summary",
                    "Claim text continues.",
                    [new ArtifactReportCitationRequest(1, 0, 10, fixture.Claim.Id)])]));

        Assert.True(attached.IsSuccess, attached.Error ?? attached.ErrorDetail?.Message);

        var report = await fixture.Service.GetAsync(fixture.ProjectId, fixture.Version.Id, null);

        Assert.True(report.IsSuccess, report.Error ?? report.ErrorDetail?.Message);
        var section = Assert.Single(report.Value!.Sections);
        Assert.Collection(
            section.Runs,
            citation =>
            {
                Assert.Equal("citation", citation.Kind);
                Assert.Equal("Claim text", citation.Text);
                Assert.Equal(fixture.Claim.Id, citation.Citation?.ClaimId);
                Assert.Equal("Source passage", Assert.Single(citation.Citation!.Evidence).Passage);
            },
            text =>
            {
                Assert.Equal("text", text.Kind);
                Assert.Equal(" continues.", text.Text);
            });
    }

    private sealed class Fixture(
        AppDbContext context,
        Guid projectId,
        ArtifactVersion version,
        ArtifactClaim claim,
        DbArtifactReportService service) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public Guid ProjectId { get; } = projectId;
        public ArtifactVersion Version { get; } = version;
        public ArtifactClaim Claim { get; } = claim;
        public DbArtifactReportService Service { get; } = service;

        public static async Task<Fixture> CreateAsync()
        {
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var context = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options,
                new CurrentTenant(tenantId));
            context.Tenants.Add(new Tenant(tenantId)
            {
                Name = "Report test tenant",
                Slug = "report-test",
                DisplayName = "Report test tenant",
                Status = TenantStatus.Active
            });
            await context.SaveChangesAsync();

            var artifact = new Artifact
            {
                TenantId = tenantId,
                ProjectId = projectId,
                Name = "Research report",
                CreatedByUserId = userId
            };
            var version = new ArtifactVersion
            {
                TenantId = tenantId,
                ArtifactId = artifact.Id,
                Artifact = artifact,
                VersionNumber = 1,
                CreatedByUserId = userId
            };
            var fileObject = new FileObject
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                UploadedByUserId = userId,
                OriginalFileName = "research-report.pdf",
                StorageKey = "test/research-report.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1,
                Status = FileObjectStatus.Active
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
                FileName = "research-report.pdf",
                StoredFileName = "research-report.pdf",
                FilePath = "test/research-report.pdf",
                ContentType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                StorageProvider = "Test",
                StorageKey = "test/research-report.pdf",
                ScanStatus = FileScanStatus.Skipped
            };
            version.AttachmentId = attachment.Id;
            version.Attachment = attachment;
            version.FileObjectId = fileObject.Id;
            version.FileObject = fileObject;
            var claim = new ArtifactClaim
            {
                TenantId = tenantId,
                ArtifactVersionId = version.Id,
                ArtifactVersion = version,
                Ordinal = 1,
                Text = "The claim.",
                CitationPresent = true
            };
            claim.Evidence.Add(new ArtifactEvidence
            {
                TenantId = tenantId,
                ArtifactClaimId = claim.Id,
                ArtifactClaim = claim,
                Ordinal = 1,
                SourceKind = ArtifactEvidenceSourceKind.WebSnapshot,
                SourceReference = "https://example.invalid/source",
                PassageSnapshot = "Source passage"
            });
            context.Artifacts.Add(artifact);
            context.FileObjects.Add(fileObject);
            context.Attachments.Add(attachment);
            context.ArtifactVersions.Add(version);
            context.Set<ArtifactClaim>().Add(claim);
            await context.SaveChangesAsync();

            var service = new DbArtifactReportService(
                context,
                new ArtifactRepository(context),
                new ArtifactAuthorization(),
                new ProjectAuthorization(),
                new FileRepository(context),
                new FileAuthorization(),
                new CurrentUser(userId),
                new UnitOfWork(context));
            return new Fixture(context, projectId, version, claim, service);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class CurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string TenantSlug => "report-test";
        public bool IsPlatformScope => false;
    }

    private sealed class CurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string Email => "report@example.invalid";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class ArtifactAuthorization : IArtifactAuthorizationService
    {
        public Task<bool> CanViewArtifact(Guid userId, Guid artifactId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanUploadArtifact(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanUpdateArtifact(Guid userId, Guid artifactId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanDownloadArtifactVersion(Guid userId, Guid versionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class ProjectAuthorization : IProjectAuthorizationService
    {
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FileAuthorization : IFileAuthorizationService
    {
        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(Guid userId, Guid workspaceId, IReadOnlyCollection<Attachment> attachments, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class UnitOfWork(AppDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
    }
}
