using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Files;

public sealed class FileActivityServiceTests
{
    [Fact]
    public async Task ActivityCombinesVersionsAndSharingUsingOnlyTheTypedAllowList()
    {
        var fixture = new Fixture();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var firstVersionId = Guid.NewGuid();
        var secondVersionId = Guid.NewGuid();
        fixture.Files.Versions.AddRange([
            new FileVersionRecord(
                firstVersionId,
                fixture.File.Id,
                1,
                "notes.txt",
                "tenant/private/version-1",
                "text/plain",
                10,
                null,
                fixture.ActorUserId,
                "Uploader",
                now.AddHours(-2)),
            new FileVersionRecord(
                secondVersionId,
                fixture.File.Id,
                2,
                "notes.txt",
                "tenant/private/version-2",
                "text/plain",
                12,
                null,
                fixture.ActorUserId,
                "Editor",
                now),
        ]);
        fixture.Files.Sharing.Add(new FileSharingActivityRecord(
            Guid.NewGuid(),
            "Workspace Admin",
            now.AddHours(-1),
            """{"sharingVersion":3,"accessState":"Workspace","change":"recipientGranted","recipientEmail":"secret@example.com","storageKey":"tenant/private/secret"}"""));

        var result = await fixture.Service.GetAsync(fixture.File.Id);

        Assert.True(result.IsSuccess, result.Error);
        Assert.NotNull(result.Value);
        Assert.Equal(fixture.File.Id, result.Value!.FileObjectId);
        Assert.Equal(3, result.Value.Items.Count);
        Assert.Equal("versionCreated", result.Value.Items[0].Kind);
        Assert.Equal(2, result.Value.Items[0].Version?.VersionNumber);
        Assert.True(result.Value.Items[0].Version?.IsCurrent);
        Assert.Equal("sharingChanged", result.Value.Items[1].Kind);
        Assert.Equal("recipientGranted", result.Value.Items[1].Sharing?.Change);
        Assert.Equal("workspace", result.Value.Items[1].Sharing?.AccessState);
        Assert.Equal(3, result.Value.Items[1].Sharing?.SharingVersion);
        Assert.Equal("uploaded", result.Value.Items[2].Kind);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result.Value));
        Assert.Equal(1, fixture.Authorization.ViewCalls);
        Assert.Equal(0, fixture.Authorization.DownloadCalls);
    }

    [Fact]
    public async Task ActivityFailsClosedBeforeReadingHistoryWhenViewAuthorizationIsRevoked()
    {
        var fixture = new Fixture { Authorization = { CanView = false } };

        var result = await fixture.Service.GetAsync(fixture.File.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorDetail?.Code);
        Assert.Equal(1, fixture.Authorization.ViewCalls);
        Assert.Equal(0, fixture.Files.VersionListCalls);
        Assert.Equal(0, fixture.Files.SharingListCalls);
    }

    [Fact]
    public async Task HistoricalContentReauthorizesDownloadBeforeOpeningItsImmutableStorageKey()
    {
        var fixture = new Fixture();
        var versionId = Guid.NewGuid();
        fixture.Files.Versions.Add(new FileVersionRecord(
            versionId,
            fixture.File.Id,
            2,
            "notes.txt",
            "tenant/private/version-2",
            "text/plain",
            12,
            null,
            fixture.ActorUserId,
            "Editor",
            DateTimeOffset.UtcNow));

        var result = await fixture.Service.ViewVersionAsync(fixture.File.Id, versionId);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, fixture.Authorization.DownloadCalls);
        Assert.Equal(0, fixture.Authorization.ViewCalls);
        Assert.Equal("tenant/private/version-2", fixture.Storage.LastOpenedStorageKey);
        Assert.Equal("notes.txt", result.Value?.FileName);
        Assert.Equal("text/plain", result.Value?.ContentType);
    }

    [Fact]
    public async Task HistoricalContentDoesNotReadStorageAfterDownloadAuthorizationIsRevoked()
    {
        var fixture = new Fixture();
        var versionId = Guid.NewGuid();
        fixture.Files.Versions.Add(new FileVersionRecord(
            versionId,
            fixture.File.Id,
            2,
            "notes.txt",
            "tenant/private/version-2",
            "text/plain",
            12,
            null,
            fixture.ActorUserId,
            "Editor",
            DateTimeOffset.UtcNow));
        fixture.Authorization.CanDownload = false;

        var result = await fixture.Service.ViewVersionAsync(fixture.File.Id, versionId);

        Assert.False(result.IsSuccess);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorDetail?.Code);
        Assert.Equal(1, fixture.Authorization.DownloadCalls);
        Assert.Null(fixture.Storage.LastOpenedStorageKey);
        Assert.Equal(0, fixture.Files.VersionReadCalls);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            File = new FileObject
            {
                TenantId = TenantId,
                WorkspaceId = WorkspaceId,
                UploadedByUserId = ActorUserId,
                OriginalFileName = "notes.txt",
                StorageKey = "tenant/private/current",
                ContentType = "text/plain",
                SizeBytes = 12,
                Classification = DataClassification.Private,
                Status = FileObjectStatus.Active,
            };
            Attachment = new Attachment
            {
                TenantId = TenantId,
                FileObjectId = File.Id,
                WorkspaceId = WorkspaceId,
                OwnerType = AttachmentOwnerType.Workspace,
                OwnerId = WorkspaceId,
                OwnerUserId = ActorUserId,
                UploadedByUserId = ActorUserId,
                FileName = File.OriginalFileName,
                StoredFileName = File.Id.ToString("N"),
                FilePath = File.StorageKey,
                ContentType = File.ContentType,
                Extension = ".txt",
                SizeBytes = File.SizeBytes,
                StorageProvider = "test",
                StorageKey = File.StorageKey,
                ScanStatus = FileScanStatus.Clean,
                FileObject = File,
            };
            Grants.Attachment = Attachment;
            Service = new FileActivityService(
                Files,
                Grants,
                Authorization,
                Storage,
                new CurrentUser(ActorUserId),
                new CurrentTenant(TenantId));
        }

        private Guid TenantId { get; } = Guid.NewGuid();
        private Guid WorkspaceId { get; } = Guid.NewGuid();
        public Guid ActorUserId { get; } = Guid.NewGuid();
        public FileObject File { get; }
        private Attachment Attachment { get; }
        public FakeFileRepository Files { get; } = new();
        private FakeGrantRepository Grants { get; } = new();
        public FakeFileAuthorization Authorization { get; } = new();
        public FakeStorage Storage { get; } = new();
        public FileActivityService Service { get; }
    }

    private sealed class FakeFileRepository : IFileRepository
    {
        public List<FileVersionRecord> Versions { get; } = [];
        public List<FileSharingActivityRecord> Sharing { get; } = [];
        public int VersionListCalls { get; private set; }
        public int VersionReadCalls { get; private set; }
        public int SharingListCalls { get; private set; }

        public Task<IReadOnlyList<FileVersionRecord>> ListFileVersionsAsync(Guid tenantId, Guid fileObjectId, int limit, CancellationToken cancellationToken = default)
        {
            VersionListCalls++;
            return Task.FromResult<IReadOnlyList<FileVersionRecord>>(Versions
                .Where(version => version.FileObjectId == fileObjectId)
                .Take(limit)
                .ToList());
        }

        public Task<FileVersionRecord?> GetFileVersionAsync(Guid tenantId, Guid fileObjectId, Guid versionId, CancellationToken cancellationToken = default)
        {
            VersionReadCalls++;
            return Task.FromResult(Versions.FirstOrDefault(version =>
                version.FileObjectId == fileObjectId && version.Id == versionId));
        }

        public Task<IReadOnlyList<FileSharingActivityRecord>> ListFileSharingActivityAsync(Guid tenantId, Guid fileObjectId, int limit, CancellationToken cancellationToken = default)
        {
            SharingListCalls++;
            return Task.FromResult<IReadOnlyList<FileSharingActivityRecord>>(Sharing.Take(limit).ToList());
        }

        public Task<FileObject?> GetFileObjectAsync(Guid fileObjectId, CancellationToken cancellationToken = default) => Task.FromResult<FileObject?>(null);
        public Task<PagedResponse<Attachment>> ListWorkspaceFileObjectsAsync(Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResponse<Attachment>([], page, pageSize, 0));
        public Task AddFileObjectAsync(FileObject fileObject, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Attachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult<Attachment?>(null);
        public Task<Attachment?> GetAttachmentByFileObjectAsync(Guid fileObjectId, CancellationToken cancellationToken = default) => Task.FromResult<Attachment?>(null);
        public Task AddAttachmentAsync(Attachment attachment, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<FileOwnerContext?> ResolveOwnerAsync(AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult<FileOwnerContext?>(null);
    }

    private sealed class FakeGrantRepository : IFileAccessGrantRepository
    {
        public Attachment? Attachment { get; set; }

        public Task<Attachment?> GetWorkspaceAttachmentAsync(Guid fileObjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Attachment?.FileObjectId == fileObjectId ? Attachment : null);
        public Task<IReadOnlyDictionary<Guid, FileAccessGrantSummary>> GetEffectiveSummariesAsync(IReadOnlyCollection<Guid> fileObjectIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FileAccessGrantSummary>>(new Dictionary<Guid, FileAccessGrantSummary>());
        public Task<bool> HasEffectiveGrantAsync(Guid fileObjectId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<FileAccessGrantRecipient>> ListEffectiveRecipientsAsync(Guid fileObjectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FileAccessGrantRecipient>>([]);
        public Task<IReadOnlyList<FileAccessGrantCandidate>> ListEligibleRecipientsAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FileAccessGrantCandidate>>([]);
        public Task<FileAccessGrantCandidate?> FindEligibleRecipientAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<FileAccessGrantCandidate?>(null);
        public Task<FileAccessGrant?> GetActiveGrantAsync(Guid fileObjectId, Guid grantId, CancellationToken cancellationToken = default) => Task.FromResult<FileAccessGrant?>(null);
        public Task<FileAccessGrant?> GetActiveGrantForRecipientAsync(Guid fileObjectId, Guid recipientUserId, CancellationToken cancellationToken = default) => Task.FromResult<FileAccessGrant?>(null);
        public Task AddAsync(FileAccessGrant grant, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeFileAuthorization : IFileAuthorizationService
    {
        public bool CanView { get; set; } = true;
        public bool CanDownload { get; set; } = true;
        public int ViewCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default)
        {
            ViewCalls++;
            return Task.FromResult(CanView);
        }
        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.FromResult(CanDownload);
        }
        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(Guid userId, Guid workspaceId, IReadOnlyCollection<Attachment> attachments, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeStorage : IFileStorageService
    {
        public string? LastOpenedStorageKey { get; private set; }

        public Task<Result> SaveAsync(string storageKey, Stream stream, string contentType, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            LastOpenedStorageKey = storageKey;
            return Task.FromResult<Stream>(new MemoryStream("version"u8.ToArray()));
        }
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> CreateSignedReadUrlAsync(string storageKey, TimeSpan expiresIn, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed record CurrentUser(Guid Id) : ICurrentUser
    {
        public Guid? UserId => Id;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => null;
        public SystemRole? SystemRole => global::Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed record CurrentTenant(Guid Id) : ICurrentTenant
    {
        public Guid TenantId => Id;
        public bool IsAvailable => true;
        public string? TenantSlug => "test";
        public bool IsPlatformScope => false;
    }
}