using System.Text;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Security;

namespace Coglatas.Tests.Files;

[Trait("Scope", "TaskV1Prompt2D")]
public sealed class FileDownloadGrantBoundaryTests
{
    [Fact]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task FileDownloadGrantCanBeCreatedOnlyForAuthorizedActor()
    {
        var fixture = new Fixture();

        var grant = await fixture.Service.RequestDownloadGrantAsync(fixture.Attachment.Id, new FileDownloadGrantRequest("case-file"));

        Assert.True(grant.IsSuccess);
        var stored = Assert.Single(fixture.Grants.Grants);
        Assert.Equal(fixture.UserId, stored.ActorUserId);
        Assert.Equal(fixture.TenantId, stored.TenantId);
        Assert.Equal(fixture.WorkspaceId, stored.WorkspaceId);
        Assert.Equal(fixture.Attachment.FileObjectId, stored.FileObjectId);
        Assert.Equal(fixture.Attachment.OwnerType, stored.TargetScopeType);
        Assert.Equal(fixture.Attachment.OwnerId, stored.TargetScopeId);
        Assert.Equal(DataClassification.Private, stored.Classification);
        Assert.NotEqual(grant.Value!.Token, stored.TokenHash);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "file_download.reauthorization_passed");
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "file_download.grant_issued");

        fixture.Authorization.CanDownload = false;
        var denied = await fixture.Service.RequestDownloadGrantAsync(fixture.Attachment.Id, new FileDownloadGrantRequest("case-file"));

        Assert.False(denied.IsSuccess);
        Assert.Single(fixture.Grants.Grants);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "file_download.grant_issue_denied");
        AssertAuditContainsReason(fixture, "current_authorization_failed");
    }

    [Fact]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task FileDownloadGrantDownloadRequiresCurrentAuthorization()
    {
        var fixture = new Fixture();
        var grant = await CreateGrantAsync(fixture);
        fixture.Authorization.CanDownload = false;

        var result = await fixture.Service.DownloadWithGrantAsync(grant.FileDownloadGrantId, grant.Token);

        Assert.False(result.IsSuccess);
        AssertAuditContainsReason(fixture, "current_authorization_failed");
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "file_download.reauthorization_failed");
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "file_download.grant_use_denied");
        Assert.Equal(0, fixture.Storage.OpenReadCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task TaskFileOpenReauthorizesAndDoesNotTreatDetailStateAsACapability()
    {
        var fixture = new Fixture { Authorization = { CanDownload = false } };

        var result = await fixture.Service.GetAsync(fixture.Attachment.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, fixture.Storage.OpenReadCount);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "file_download.metadata_open_denied");
        AssertAuditContainsReason(fixture, "current_authorization_failed");
    }

    [Theory]
    [InlineData(FileScanStatus.Pending)]
    [InlineData(FileScanStatus.Infected)]
    [InlineData(FileScanStatus.Failed)]
    [InlineData(FileScanStatus.Skipped)]
    public async Task TaskFileGrantRequiresCleanScan(FileScanStatus scanStatus)
    {
        var fixture = new Fixture { Attachment = { ScanStatus = scanStatus } };

        var result = await fixture.Service.RequestDownloadGrantAsync(fixture.Attachment.Id, new FileDownloadGrantRequest());

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.Grants.Grants);
        Assert.Equal(0, fixture.Storage.OpenReadCount);
        AssertAuditContainsReason(fixture, "task_file_not_available");
    }

    [Theory]
    [InlineData(FileObjectStatus.Quarantined)]
    [InlineData(FileObjectStatus.Archived)]
    [InlineData(FileObjectStatus.Deleted)]
    public async Task CurrentFileObjectStateInvalidatesExistingTaskGrant(FileObjectStatus status)
    {
        var fixture = new Fixture();
        var grant = await CreateGrantAsync(fixture);
        fixture.FileObject.Status = status;

        var result = await fixture.Service.DownloadWithGrantAsync(grant.FileDownloadGrantId, grant.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, fixture.Storage.OpenReadCount);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "file_download.grant_use_denied");
    }

    [Fact]
    public async Task ExpiredAndRevokedFileDownloadGrantsAreDenied()
    {
        var expiredFixture = new Fixture();
        var expiredGrant = await CreateGrantAsync(expiredFixture);
        expiredFixture.Clock.UtcNow = expiredGrant.ExpiresAt.AddSeconds(1);

        var expired = await expiredFixture.Service.DownloadWithGrantAsync(expiredGrant.FileDownloadGrantId, expiredGrant.Token);

        Assert.False(expired.IsSuccess);
        AssertAuditContainsReason(expiredFixture, "grant_expired");
        Assert.Contains(expiredFixture.Audit.Entries, entry => entry.Action == "file_download.grant_expired");

        var revokedFixture = new Fixture();
        var revokedGrant = await CreateGrantAsync(revokedFixture);
        revokedFixture.Grants.Grants[0].RevokedAt = revokedFixture.Clock.UtcNow;

        var revoked = await revokedFixture.Service.DownloadWithGrantAsync(revokedGrant.FileDownloadGrantId, revokedGrant.Token);

        Assert.False(revoked.IsSuccess);
        AssertAuditContainsReason(revokedFixture, "grant_revoked");
        Assert.Contains(revokedFixture.Audit.Entries, entry => entry.Action == "file_download.grant_revoked");
    }

    [Fact]
    public async Task ActorMismatchAndTokenMismatchAreDeniedWithoutLeakingToken()
    {
        var fixture = new Fixture();
        var grant = await CreateGrantAsync(fixture);
        fixture.CurrentUser.UserIdValue = Guid.NewGuid();

        var actorMismatch = await fixture.Service.DownloadWithGrantAsync(grant.FileDownloadGrantId, grant.Token);

        Assert.False(actorMismatch.IsSuccess);
        AssertAuditContainsReason(fixture, "actor_mismatch");

        var tokenFixture = new Fixture();
        var tokenGrant = await CreateGrantAsync(tokenFixture);
        var tokenMismatch = await tokenFixture.Service.DownloadWithGrantAsync(tokenGrant.FileDownloadGrantId, "leaked-token");

        Assert.False(tokenMismatch.IsSuccess);
        var audit = JsonSerializer.Serialize(tokenFixture.Audit.Entries);
        Assert.Contains("invalid_grant_token", audit);
        Assert.DoesNotContain(tokenGrant.Token, audit);
        Assert.DoesNotContain("leaked-token", audit);
    }

    [Fact]
    public async Task TenantWorkspaceAndTargetScopeMismatchAreDenied()
    {
        var tenantFixture = new Fixture();
        var tenantGrant = await CreateGrantAsync(tenantFixture);
        tenantFixture.CurrentTenant.TenantIdValue = Guid.NewGuid();

        var tenantMismatch = await tenantFixture.Service.DownloadWithGrantAsync(tenantGrant.FileDownloadGrantId, tenantGrant.Token);

        Assert.False(tenantMismatch.IsSuccess);

        var workspaceFixture = new Fixture();
        var workspaceGrant = await CreateGrantAsync(workspaceFixture);
        workspaceFixture.Attachment.WorkspaceId = Guid.NewGuid();

        var workspaceMismatch = await workspaceFixture.Service.DownloadWithGrantAsync(workspaceGrant.FileDownloadGrantId, workspaceGrant.Token);

        Assert.False(workspaceMismatch.IsSuccess);
        AssertAuditContainsReason(workspaceFixture, "scope_mismatch");

        var scopeFixture = new Fixture();
        var scopeGrant = await CreateGrantAsync(scopeFixture);
        scopeFixture.Attachment.OwnerId = Guid.NewGuid();

        var scopeMismatch = await scopeFixture.Service.DownloadWithGrantAsync(scopeGrant.FileDownloadGrantId, scopeGrant.Token);

        Assert.False(scopeMismatch.IsSuccess);
        AssertAuditContainsReason(scopeFixture, "scope_mismatch");
    }

    [Fact]
    public async Task StalePolicyAndClassificationChangesInvalidateOldFileGrant()
    {
        var staleFixture = new Fixture();
        var staleGrant = await CreateGrantAsync(staleFixture);
        staleFixture.Attachment.FileObject!.WorkspaceId = Guid.NewGuid();

        var stale = await staleFixture.Service.DownloadWithGrantAsync(staleGrant.FileDownloadGrantId, staleGrant.Token);

        Assert.False(stale.IsSuccess);
        AssertAuditContainsReason(staleFixture, "scope_mismatch");

        var classificationFixture = new Fixture();
        var classificationGrant = await CreateGrantAsync(classificationFixture);
        classificationFixture.FileObject.Classification = DataClassification.Internal;

        var mismatch = await classificationFixture.Service.DownloadWithGrantAsync(classificationGrant.FileDownloadGrantId, classificationGrant.Token);

        Assert.False(mismatch.IsSuccess);
        AssertAuditContainsReason(classificationFixture, "policy_changed");
    }

    [Fact]
    public async Task InaccessibleFilesAndUnknownOrMissingClassificationFailClosed()
    {
        var deletedFixture = new Fixture();
        var deletedGrant = await CreateGrantAsync(deletedFixture);
        deletedFixture.FileObject.MarkDeleted(deletedFixture.Clock.UtcNow, deletedFixture.UserId, "cleanup");

        var deleted = await deletedFixture.Service.DownloadWithGrantAsync(deletedGrant.FileDownloadGrantId, deletedGrant.Token);

        Assert.False(deleted.IsSuccess);
        AssertAuditContainsReason(deletedFixture, "target_deleted");

        var unknownFixture = new Fixture { FileObject = { Classification = DataClassification.UnknownSensitive } };
        var unknown = await unknownFixture.Service.RequestDownloadGrantAsync(unknownFixture.Attachment.Id, new FileDownloadGrantRequest());

        Assert.False(unknown.IsSuccess);
        AssertAuditContainsReason(unknownFixture, "unknown_sensitive_classification");

        var missingFixture = new Fixture { FileObject = { Classification = null } };
        var missing = await missingFixture.Service.RequestDownloadGrantAsync(missingFixture.Attachment.Id, new FileDownloadGrantRequest());

        Assert.False(missing.IsSuccess);
        AssertAuditContainsReason(missingFixture, "missing_classification");
    }

    [Fact]
    public async Task AuditDoesNotContainRawTokenSignedUrlStoragePathOrRestrictedValues()
    {
        var fixture = new Fixture();
        var grant = await CreateGrantAsync(fixture);
        await fixture.Service.DownloadWithGrantAsync(grant.FileDownloadGrantId, grant.Token);

        var audit = JsonSerializer.Serialize(fixture.Audit.Entries);
        Assert.Contains("file_download.grant_used", audit);
        Assert.Contains("file_download.reauthorization_passed", audit);
        Assert.DoesNotContain(grant.Token, audit);
        Assert.DoesNotContain(fixture.FileObject.StorageKey, audit);
        Assert.DoesNotContain("https://storage.example.test/signed", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file content", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("peanut allergy", audit, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<FileDownloadGrantResponse> CreateGrantAsync(Fixture fixture)
    {
        var result = await fixture.Service.RequestDownloadGrantAsync(fixture.Attachment.Id, new FileDownloadGrantRequest("test"));
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static void AssertAuditContainsReason(Fixture fixture, string reason)
    {
        Assert.Contains(reason, JsonSerializer.Serialize(fixture.Audit.Entries));
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            FileObject.TenantId = TenantId;
            FileObject.WorkspaceId = WorkspaceId;
            FileObject.ProjectId = ProjectId;
            FileObject.UploadedByUserId = UserId;
            FileObject.StorageKey = $"tenants/{TenantId:D}/projects/{ProjectId:D}/files/{FileObject.Id:D}";
            Attachment.TenantId = TenantId;
            Attachment.WorkspaceId = WorkspaceId;
            Attachment.FileObjectId = FileObject.Id;
            Attachment.FileObject = FileObject;
            Attachment.OwnerId = TaskId;
            Attachment.OwnerUserId = UserId;
            Attachment.UploadedByUserId = UserId;
            Attachment.StorageKey = FileObject.StorageKey;
            Files.Attachment = Attachment;
            CurrentUser = new FakeCurrentUser(UserId);
            CurrentTenant = new FakeCurrentTenant(TenantId);
            Service = new FileService(
                Files,
                Grants,
                Storage,
                Authorization,
                new FakeUploadPolicy(),
                new FakeFeatureFlags(),
                new FakeQuotaService(),
                CurrentUser,
                CurrentTenant,
                Clock,
                Audit,
                new Sha256TokenHasher(),
                new NoopInvalidations(),
                new FakeUnitOfWork());
        }

        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid WorkspaceId { get; } = Guid.NewGuid();
        private Guid ProjectId { get; } = Guid.NewGuid();
        private Guid TaskId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public FileObject FileObject { get; } = new()
        {
            OriginalFileName = "student-note.txt",
            ContentType = "text/plain",
            SizeBytes = 12,
            Classification = DataClassification.Private,
            Status = FileObjectStatus.Active
        };
        public Attachment Attachment { get; } = new()
        {
            OwnerType = AttachmentOwnerType.TaskItem,
            FileName = "student-note.txt",
            StoredFileName = "stored",
            FilePath = "internal/path/student-note.txt",
            ContentType = "text/plain",
            Extension = ".txt",
            SizeBytes = 12,
            StorageProvider = "Local",
            ScanStatus = FileScanStatus.Clean
        };

        private FakeFileRepository Files { get; } = new();
        public FakeFileDownloadGrantRepository Grants { get; } = new();
        public FakeStorage Storage { get; } = new();
        public FakeAuthorization Authorization { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeAuditLogger Audit { get; } = new();
        public FakeCurrentUser CurrentUser { get; }
        public FakeCurrentTenant CurrentTenant { get; }
        public FileService Service { get; }
    }

    private sealed class NoopInvalidations : IBusinessInvalidationPublisher
    {
        public Task TaskChangedAsync(TaskItem task, Guid actorUserId, string change, IEnumerable<string>? changedFields = null, IEnumerable<Guid>? affectedUserIds = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProjectChangedAsync(Project project, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AnnouncementChangedAsync(Announcement announcement, Guid actorUserId, string change, IEnumerable<Guid> audienceUserIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FileChangedAsync(FileObject fileObject, Attachment attachment, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeFileRepository : IFileRepository
    {
        public Attachment Attachment { get; set; } = null!;
        public Task<FileObject?> GetFileObjectAsync(Guid fileObjectId, CancellationToken cancellationToken = default) => Task.FromResult(Attachment.FileObjectId == fileObjectId ? Attachment.FileObject : null);
        public Task<PagedResponse<Attachment>> ListWorkspaceFileObjectsAsync(Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResponse<Attachment>([], page, pageSize, 0));
        public Task AddFileObjectAsync(FileObject fileObject, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Attachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult(Attachment.Id == attachmentId ? Attachment : null);
        public Task<Attachment?> GetAttachmentByFileObjectAsync(Guid fileObjectId, CancellationToken cancellationToken = default) => Task.FromResult(Attachment.FileObjectId == fileObjectId ? Attachment : null);
        public Task AddAttachmentAsync(Attachment attachment, CancellationToken cancellationToken = default) { Attachment = attachment; return Task.CompletedTask; }
        public Task<FileOwnerContext?> ResolveOwnerAsync(AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult<FileOwnerContext?>(new FileOwnerContext(Attachment.WorkspaceId, Attachment.FileObject?.ProjectId, null, null, Attachment.OwnerUserId));
    }

    private sealed class FakeFileDownloadGrantRepository : IFileDownloadGrantRepository
    {
        public List<FileDownloadGrant> Grants { get; } = [];
        public Task<FileDownloadGrant?> GetAsync(Guid fileDownloadGrantId, CancellationToken cancellationToken = default) => Task.FromResult(Grants.FirstOrDefault(grant => grant.Id == fileDownloadGrantId));
        public Task AddAsync(FileDownloadGrant grant, CancellationToken cancellationToken = default) { Grants.Add(grant); return Task.CompletedTask; }
    }

    private sealed class FakeAuthorization : IFileAuthorizationService
    {
        public bool CanDownload { get; set; } = true;
        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(CanDownload);
        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(CanDownload);
        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(CanDownload);
        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(Guid userId, Guid workspaceId, IReadOnlyCollection<Attachment> attachments, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(CanDownload ? attachments.Select(attachment => attachment.Id).ToHashSet() : new HashSet<Guid>());
        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(CanDownload);
    }

    private sealed class FakeStorage : IFileStorageService
    {
        public int OpenReadCount { get; private set; }
        public Task<Result> SaveAsync(string storageKey, Stream stream, string contentType, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            OpenReadCount++;
            return Task.FromResult<Stream>(new MemoryStream("file content"u8.ToArray()));
        }
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> CreateSignedReadUrlAsync(string storageKey, TimeSpan expiresIn, CancellationToken cancellationToken = default) => Task.FromResult<string?>("https://storage.example.test/signed");
    }

    private sealed class FakeUploadPolicy : IFileUploadPolicy
    {
        public long MaxFileSizeBytes => 1024;
        public IReadOnlyCollection<string> AllowedExtensions => [".txt"];
        public IReadOnlyCollection<string> AllowedContentTypes => ["text/plain"];
    }

    private sealed class FakeFeatureFlags : IFeatureFlagService
    {
        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Result> RequireEnabledAsync(string featureKey, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([FeatureKeys.FileSharing]);
    }

    private sealed class FakeQuotaService : IQuotaService
    {
        public Task<TenantUsageSnapshot> GetCurrentUsageAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(new TenantUsageSnapshot(tenantId, 0, 0, 0, 0, 0, 0, 0));
        public Task<Result> CanCreateUserAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> CanCreateProjectAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> CanUploadFileAsync(Guid tenantId, long fileSizeBytes, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> CanInviteGuestAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task RecordApiRequestAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserIdValue { get; set; } = userId;
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => "file-reader@example.test";
        public SystemRole? SystemRole => global::Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => UserIdValue.HasValue;
    }

    private sealed class FakeCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantIdValue { get; set; } = tenantId;
        public Guid TenantId => TenantIdValue;
        public bool IsAvailable => true;
        public string TenantSlug => "tenant-a";
        public bool IsPlatformScope => false;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry); return Task.CompletedTask; }
    }
}
