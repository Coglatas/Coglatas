using System.Security.Cryptography;
using System.Text;
using Coglatas.Application.Artifacts;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Artifacts;

public sealed class ArtifactReportRefinementServiceTests
{
    [Fact]
    public async Task RefineClaimAsync_CreatesNewImmutableVersionAndOnlyAddsTargetEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preflight = await fixture.Service.PreflightAsync(
            fixture.ProjectId,
            fixture.BaseVersionId,
            ArtifactReportRefinementTargetKind.Claim,
            fixture.TargetLogicalClaimId);

        Assert.True(preflight.IsSuccess, preflight.Error ?? preflight.ErrorDetail?.Message);
        Assert.True(preflight.Value!.CanRefine);
        Assert.Equal(3, preflight.Value.Scope.ProjectScopeVersion);
        Assert.Equal(7, preflight.Value.Scope.ResearchPlanRevisionNo);

        var result = await fixture.Service.RefineAsync(
            fixture.ProjectId,
            fixture.BaseVersionId,
            new RefineArtifactReportRequest(
                ArtifactReportRefinementTargetKind.Claim,
                fixture.TargetLogicalClaimId,
                "Find stronger basalt evidence.",
                preflight.Value.Scope.ProjectScopeVersion,
                preflight.Value.Scope.TaskOverrideVersion,
                preflight.Value.Scope.ResearchPlanRevisionId,
                preflight.Value.Scope.ResearchPlanRevisionNo));

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Equal(2, result.Value!.VersionNumber);
        Assert.Equal(1, result.Value.RefreshedClaimCount);
        Assert.True(result.Value.EvidenceAdded > 0);

        fixture.Context.ChangeTracker.Clear();
        var artifact = await fixture.Context.Artifacts.AsNoTracking().SingleAsync();
        Assert.Equal(result.Value.ArtifactVersionId, artifact.CurrentVersionId);
        Assert.Equal(2, await fixture.Context.ArtifactVersions.CountAsync());

        var oldDocument = await fixture.Context.ArtifactReportDocuments
            .AsNoTracking()
            .Include(document => document.Sections)
            .SingleAsync(document => document.ArtifactVersionId == fixture.BaseVersionId);
        var newDocument = await fixture.Context.ArtifactReportDocuments
            .AsNoTracking()
            .Include(document => document.Sections)
            .SingleAsync(document => document.ArtifactVersionId == result.Value.ArtifactVersionId);
        Assert.Equal(oldDocument.Title, newDocument.Title);
        Assert.Equal(
            oldDocument.Sections.OrderBy(section => section.Ordinal).Select(section => (section.LogicalSectionId, section.Heading, section.BodyText)),
            newDocument.Sections.OrderBy(section => section.Ordinal).Select(section => (section.LogicalSectionId, section.Heading, section.BodyText)));

        var baseVersionId = fixture.BaseVersionId;
        var oldClaims = await fixture.Context.Set<ArtifactClaim>()
            .AsNoTracking()
            .Include(claim => claim.Evidence)
            .Where(claim => claim.ArtifactVersionId == baseVersionId)
            .ToListAsync();
        var newClaims = await fixture.Context.Set<ArtifactClaim>()
            .AsNoTracking()
            .Include(claim => claim.Evidence)
            .Where(claim => claim.ArtifactVersionId == result.Value.ArtifactVersionId)
            .ToListAsync();
        var targetLogicalClaimId = fixture.TargetLogicalClaimId;
        var untouchedLogicalClaimId = fixture.UntouchedLogicalClaimId;
        var oldTarget = Assert.Single(oldClaims, claim => claim.LogicalClaimId == targetLogicalClaimId);
        var newTarget = Assert.Single(newClaims, claim => claim.LogicalClaimId == targetLogicalClaimId);
        var oldUntouched = Assert.Single(oldClaims, claim => claim.LogicalClaimId == untouchedLogicalClaimId);
        var newUntouched = Assert.Single(newClaims, claim => claim.LogicalClaimId == untouchedLogicalClaimId);
        Assert.Equal(oldTarget.Text, newTarget.Text);
        Assert.True(newTarget.Evidence.Count > oldTarget.Evidence.Count);
        Assert.Equal(oldUntouched.Text, newUntouched.Text);
        Assert.Equal(oldUntouched.Evidence.Count, newUntouched.Evidence.Count);
        Assert.Contains(newTarget.Evidence, evidence =>
            evidence.SourceKind == ArtifactEvidenceSourceKind.FileAttachment &&
            evidence.VerificationStatus == ArtifactEvidenceVerificationStatus.Verified &&
            evidence.PassageSnapshot.Contains("basalt", StringComparison.OrdinalIgnoreCase));

        var activity = await fixture.Context.ActivityLogs.AsNoTracking().SingleAsync();
        Assert.Equal(ActivityLogType.Decision, activity.ActivityType);
        Assert.Contains("Report refinement created version 2", activity.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefineAsync_FailsClosedWhenConfirmedScopeChanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preflight = await fixture.Service.PreflightAsync(
            fixture.ProjectId,
            fixture.BaseVersionId,
            ArtifactReportRefinementTargetKind.Claim,
            fixture.TargetLogicalClaimId);
        Assert.True(preflight.IsSuccess);

        fixture.ExecutionScopes.ProjectScopeVersion = 4;
        var result = await fixture.Service.RefineAsync(
            fixture.ProjectId,
            fixture.BaseVersionId,
            new RefineArtifactReportRequest(
                ArtifactReportRefinementTargetKind.Claim,
                fixture.TargetLogicalClaimId,
                null,
                preflight.Value!.Scope.ProjectScopeVersion,
                preflight.Value.Scope.TaskOverrideVersion,
                preflight.Value.Scope.ResearchPlanRevisionId,
                preflight.Value.Scope.ResearchPlanRevisionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("ReportRefinementScopeChanged", result.ErrorDetail?.Code);
        Assert.Single(fixture.Context.ArtifactVersions);
        Assert.Empty(fixture.Context.ActivityLogs);
    }

    private sealed class Fixture(
        AppDbContext context,
        Guid projectId,
        Guid baseVersionId,
        Guid targetLogicalClaimId,
        Guid untouchedLogicalClaimId,
        FakeExecutionScopes executionScopes,
        DbArtifactReportRefinementService service) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public Guid ProjectId { get; } = projectId;
        public Guid BaseVersionId { get; } = baseVersionId;
        public Guid TargetLogicalClaimId { get; } = targetLogicalClaimId;
        public Guid UntouchedLogicalClaimId { get; } = untouchedLogicalClaimId;
        public FakeExecutionScopes ExecutionScopes { get; } = executionScopes;
        public DbArtifactReportRefinementService Service { get; } = service;

        public static async Task<Fixture> CreateAsync()
        {
            var tenantId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var context = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options,
                new CurrentTenant(tenantId));
            context.Tenants.Add(new Tenant(tenantId)
            {
                Name = "Refinement tenant",
                Slug = "refinement-test",
                DisplayName = "Refinement tenant",
                Status = TenantStatus.Active
            });
            await context.SaveChangesAsync();

            var task = new TaskItem
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Title = "Research the Moon",
                CreatedByUserId = userId
            };
            var artifact = new Artifact
            {
                TenantId = tenantId,
                ProjectId = projectId,
                TaskItemId = task.Id,
                Name = "Moon report",
                CreatedByUserId = userId
            };
            var artifactFile = new FileObject
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                UploadedByUserId = userId,
                OriginalFileName = "moon-report.pdf",
                StorageKey = "artifact/moon-report.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1,
                Status = FileObjectStatus.Active
            };
            var version = new ArtifactVersion
            {
                TenantId = tenantId,
                ArtifactId = artifact.Id,
                Artifact = artifact,
                VersionNumber = 1,
                FileObjectId = artifactFile.Id,
                FileObject = artifactFile,
                CreatedByUserId = userId
            };
            var artifactAttachment = new Attachment
            {
                TenantId = tenantId,
                FileObjectId = artifactFile.Id,
                FileObject = artifactFile,
                WorkspaceId = workspaceId,
                OwnerType = AttachmentOwnerType.ArtifactVersion,
                OwnerId = version.Id,
                OwnerUserId = userId,
                UploadedByUserId = userId,
                FileName = "moon-report.pdf",
                StoredFileName = "moon-report.pdf",
                FilePath = "artifact/moon-report.pdf",
                ContentType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                StorageProvider = "Test",
                StorageKey = "artifact/moon-report.pdf",
                ScanStatus = FileScanStatus.Clean
            };
            version.AttachmentId = artifactAttachment.Id;
            version.Attachment = artifactAttachment;
            artifact.CurrentVersionId = version.Id;
            artifact.CurrentVersion = version;

            var targetClaim = new ArtifactClaim
            {
                TenantId = tenantId,
                ArtifactVersionId = version.Id,
                ArtifactVersion = version,
                Ordinal = 1,
                Text = "The lunar surface contains basalt.",
                CitationPresent = true,
                SupportStatus = ArtifactClaimSupportStatus.Supported
            };
            targetClaim.Evidence.Add(new ArtifactEvidence
            {
                TenantId = tenantId,
                ArtifactClaimId = targetClaim.Id,
                ArtifactClaim = targetClaim,
                Ordinal = 1,
                SourceKind = ArtifactEvidenceSourceKind.WebSnapshot,
                SourceReference = "https://example.invalid/old",
                PassageSnapshot = "Older basalt evidence."
            });
            var untouchedClaim = new ArtifactClaim
            {
                TenantId = tenantId,
                ArtifactVersionId = version.Id,
                ArtifactVersion = version,
                Ordinal = 2,
                Text = "The control section remains unchanged.",
                CitationPresent = true
            };
            untouchedClaim.Evidence.Add(new ArtifactEvidence
            {
                TenantId = tenantId,
                ArtifactClaimId = untouchedClaim.Id,
                ArtifactClaim = untouchedClaim,
                Ordinal = 1,
                SourceKind = ArtifactEvidenceSourceKind.WebSnapshot,
                SourceReference = "https://example.invalid/control",
                PassageSnapshot = "Control evidence."
            });

            var document = new ArtifactReportDocument
            {
                TenantId = tenantId,
                ArtifactVersionId = version.Id,
                ArtifactVersion = version,
                Title = "Moon evidence report"
            };
            var targetBody = "The lunar surface contains basalt.";
            var targetSection = new ArtifactReportSection
            {
                TenantId = tenantId,
                ArtifactReportDocumentId = document.Id,
                Document = document,
                Ordinal = 1,
                Heading = "Lunar geology",
                BodyText = targetBody
            };
            targetSection.Citations.Add(new ArtifactReportCitation
            {
                TenantId = tenantId,
                ArtifactReportSectionId = targetSection.Id,
                Section = targetSection,
                Ordinal = 1,
                AnchorStartUtf16 = 0,
                AnchorLengthUtf16 = targetBody.Length,
                ArtifactClaimId = targetClaim.Id,
                Claim = targetClaim
            });
            document.Sections.Add(targetSection);
            var untouchedBody = "The control section remains unchanged.";
            var untouchedSection = new ArtifactReportSection
            {
                TenantId = tenantId,
                ArtifactReportDocumentId = document.Id,
                Document = document,
                Ordinal = 2,
                Heading = "Control",
                BodyText = untouchedBody
            };
            untouchedSection.Citations.Add(new ArtifactReportCitation
            {
                TenantId = tenantId,
                ArtifactReportSectionId = untouchedSection.Id,
                Section = untouchedSection,
                Ordinal = 1,
                AnchorStartUtf16 = 0,
                AnchorLengthUtf16 = untouchedBody.Length,
                ArtifactClaimId = untouchedClaim.Id,
                Claim = untouchedClaim
            });
            document.Sections.Add(untouchedSection);

            var sourceText = "Recent lunar geology observations provide basalt evidence for the Moon and its volcanic surface.";
            var sourceBytes = Encoding.UTF8.GetBytes(sourceText);
            var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
            var sourceFile = new FileObject
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                UploadedByUserId = userId,
                OriginalFileName = "lunar-geology.md",
                StorageKey = "task/lunar-geology.md",
                ContentType = "text/markdown",
                SizeBytes = sourceBytes.LongLength,
                HashSha256 = sourceHash,
                Status = FileObjectStatus.Active
            };
            var sourceAttachment = new Attachment
            {
                TenantId = tenantId,
                FileObjectId = sourceFile.Id,
                FileObject = sourceFile,
                WorkspaceId = workspaceId,
                OwnerType = AttachmentOwnerType.TaskItem,
                OwnerId = task.Id,
                OwnerUserId = userId,
                UploadedByUserId = userId,
                FileName = "lunar-geology.md",
                StoredFileName = "lunar-geology.md",
                FilePath = "task/lunar-geology.md",
                ContentType = "text/markdown",
                Extension = ".md",
                SizeBytes = sourceBytes.LongLength,
                StorageProvider = "Test",
                StorageKey = "task/lunar-geology.md",
                ScanStatus = FileScanStatus.Clean
            };

            context.TaskItems.Add(task);
            context.Artifacts.Add(artifact);
            context.FileObjects.AddRange(artifactFile, sourceFile);
            context.Attachments.AddRange(artifactAttachment, sourceAttachment);
            context.ArtifactVersions.Add(version);
            context.Set<ArtifactClaim>().AddRange(targetClaim, untouchedClaim);
            context.Set<ArtifactReportDocument>().Add(document);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var executionScopes = new FakeExecutionScopes();
            var planRevisionId = Guid.NewGuid();
            var service = new DbArtifactReportRefinementService(
                context,
                new ArtifactRepository(context),
                new ArtifactAuthorization(),
                new ProjectAuthorization(),
                executionScopes,
                new ResearchPlans(planRevisionId, 7),
                new FileRepository(context),
                new FileAuthorization(),
                new MemoryStorage("task/lunar-geology.md", sourceBytes),
                new CurrentUser(userId),
                new FixedClock(new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero)),
                new AuditLogger(),
                new UnitOfWork(context));
            return new Fixture(
                context,
                projectId,
                version.Id,
                targetClaim.LogicalClaimId,
                untouchedClaim.LogicalClaimId,
                executionScopes,
                service);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class FakeExecutionScopes : ITaskExecutionScopeService
    {
        public long ProjectScopeVersion { get; set; } = 3;
        public Task<Result<TaskExecutionScopeResponse>> GetTaskScopeAsync(Guid taskItemId, CancellationToken cancellationToken = default)
        {
            var policy = TaskExecutionSourcePolicyV2.FromLegacy(webEnabled: false, projectFilesEnabled: true);
            return Task.FromResult(Result<TaskExecutionScopeResponse>.Success(new TaskExecutionScopeResponse(
                new TaskExecutionSourcePolicyResponse(false, true, policy),
                TaskExecutionScopeOrigin.ProjectDefault,
                ProjectScopeVersion,
                null,
                null,
                true,
                null,
                "Future runs use the current scope.")));
        }
        public Task<Result<ProjectExecutionScopeResponse>> GetProjectScopeAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<ProjectExecutionScopeResponse>> UpdateProjectScopeAsync(Guid projectId, UpdateProjectExecutionScopeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<TaskExecutionScopeResponse>> UpdateTaskOverrideAsync(Guid taskItemId, UpdateTaskExecutionScopeOverrideRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<TaskExecutionScopeResponse>> ClearTaskOverrideAsync(Guid taskItemId, ClearTaskExecutionScopeOverrideRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<TaskExecutionRunResponse>> RequestRunAsync(Guid taskItemId, string? idempotencyKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ResearchPlans(Guid revisionId, long revisionNo) : IResearchPlanRepository
    {
        public Task<ResearchPlanExecutionSnapshot?> GetCurrentExecutionSnapshotForTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResearchPlanExecutionSnapshot?>(new(revisionId, revisionNo));
        public Task<ResearchPlan?> GetForTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResearchPlan?> GetForTaskForUpdateAsync(Guid taskItemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResearchPlanRevision?> GetRevisionAsync(Guid researchPlanId, Guid revisionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long?> GetLatestRevisionNumberAsync(Guid researchPlanId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddPlanAsync(ResearchPlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddRevisionAsync(ResearchPlanRevision revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddStepsAsync(IEnumerable<ResearchPlanStep> steps, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MemoryStorage(string storageKey, byte[] bytes) : IFileStorageService
    {
        public Task<Result> SaveAsync(string key, Stream stream, string contentType, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(key == storageKey ? new MemoryStream(bytes, writable: false) : throw new FileNotFoundException());
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(key == storageKey);
        public Task<string?> CreateSignedReadUrlAsync(string key, TimeSpan expiresIn, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
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
        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(Guid userId, Guid workspaceId, IReadOnlyCollection<Attachment> attachments, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class CurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string? Email => "refinement@example.invalid";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class CurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string? TenantSlug => "refinement-test";
        public bool IsPlatformScope => false;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class AuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnitOfWork(AppDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
    }
}
