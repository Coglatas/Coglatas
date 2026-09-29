using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Realtime;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Files;

public sealed class FileSharingServiceTests
{
    [Fact]
    public async Task ListProjectionUsesExactServerStatesAndRedactsExternalCountForViewer()
    {
        var fixture = new Fixture(canManage: false);
        var privateFile = fixture.NewFile(FileSharingPolicy.Private);
        var workspaceFile = fixture.NewFile(FileSharingPolicy.Workspace);
        var externalFile = fixture.NewFile(FileSharingPolicy.Private);
        fixture.Grants.Grants.Add(fixture.NewGrant(
            externalFile.Id,
            fixture.ExternalUserId,
            FileAccessGrantRecipientKind.ExternalProjectMember));

        var viewerProjection = await fixture.Service.GetListPresentationsAsync(
            fixture.WorkspaceId,
            fixture.ActorUserId,
            [privateFile, workspaceFile, externalFile]);

        Assert.Equal("Private", viewerProjection[privateFile.Id].AccessState);
        Assert.Equal("Workspace", viewerProjection[workspaceFile.Id].AccessState);
        Assert.Equal("External", viewerProjection[externalFile.Id].AccessState);
        Assert.Null(viewerProjection[externalFile.Id].ExternalRecipientCount);
        Assert.False(viewerProjection[externalFile.Id].CanManageSharing);

        fixture.Workspaces.CanManage = true;
        var managerProjection = await fixture.Service.GetListPresentationsAsync(
            fixture.WorkspaceId,
            fixture.ActorUserId,
            [externalFile]);

        Assert.Equal("External", managerProjection[externalFile.Id].AccessState);
        Assert.Equal(1, managerProjection[externalFile.Id].ExternalRecipientCount);
        Assert.True(managerProjection[externalFile.Id].CanManageSharing);
    }

    [Fact]
    public async Task ReadProjectionDoesNotLeakExternalCountOrRecipientIdentityWithoutSharingAuthority()
    {
        var fixture = new Fixture(canManage: false);
        var file = fixture.NewFile(FileSharingPolicy.Private);
        fixture.Grants.Attachment = fixture.NewAttachment(file);
        fixture.Grants.Grants.Add(fixture.NewGrant(
            file.Id,
            fixture.ExternalUserId,
            FileAccessGrantRecipientKind.ExternalProjectMember));
        fixture.Grants.DisplayNames[fixture.ExternalUserId] = "External recipient";

        var result = await fixture.Service.GetAsync(file.Id);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("External", result.Value!.AccessState);
        Assert.Null(result.Value.ExternalRecipientCount);
        Assert.False(result.Value.CanInspectSharing);
        Assert.False(result.Value.CanManageSharing);
        Assert.Empty(result.Value.Recipients);
        Assert.Empty(result.Value.AvailableRecipients);
        Assert.Equal(0, fixture.Grants.ListRecipientCalls);
        Assert.Equal(0, fixture.Grants.ListCandidateCalls);
    }

    [Fact]
    public async Task ShareMutationFailsClosedWhenCallerLacksWorkspaceManagementAuthority()
    {
        var fixture = new Fixture(canManage: false);
        var file = fixture.NewFile(FileSharingPolicy.Private);
        fixture.Grants.Attachment = fixture.NewAttachment(file);
        fixture.Grants.Candidates.Add(new FileAccessGrantCandidate(
            fixture.ExternalUserId,
            "External recipient",
            FileAccessGrantRecipientKind.ExternalProjectMember));

        var result = await fixture.Service.GrantAsync(
            file.Id,
            new FileShareGrantCreateRequest(fixture.ExternalUserId, file.SharingVersion));

        Assert.False(result.IsSuccess);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorDetail?.Code);
        Assert.Empty(fixture.Grants.Grants);
        Assert.Equal(0, fixture.UnitOfWork.SaveCalls);
    }

    [Fact]
    public async Task RevokeRefreshesTheAuthoritativeProjectionAndInvalidatesPriorDownloadPolicy()
    {
        var fixture = new Fixture(canManage: true);
        var file = fixture.NewFile(FileSharingPolicy.Private);
        fixture.Grants.Attachment = fixture.NewAttachment(file);
        var grant = fixture.NewGrant(
            file.Id,
            fixture.ExternalUserId,
            FileAccessGrantRecipientKind.ExternalProjectMember);
        fixture.Grants.Grants.Add(grant);

        var result = await fixture.Service.RevokeAsync(file.Id, grant.Id, file.SharingVersion);

        Assert.True(result.IsSuccess, result.Error);
        Assert.NotNull(grant.RevokedAt);
        Assert.Equal(fixture.ActorUserId, grant.RevokedByUserId);
        Assert.Equal("Private", result.Value!.AccessState);
        Assert.Null(result.Value.ExternalRecipientCount);
        Assert.Equal(2, result.Value.SharingVersion);
        Assert.Equal(1, fixture.UnitOfWork.SaveCalls);
        Assert.Equal(1, fixture.Invalidations.FileChangeCalls);
        Assert.Equal("sharingChanged", fixture.Invalidations.LastChange);
    }

    [Fact]
    public async Task GrantRequiresServerEligibleRecipientRatherThanAClientSuppliedIdentity()
    {
        var fixture = new Fixture(canManage: true);
        var file = fixture.NewFile(FileSharingPolicy.Private);
        fixture.Grants.Attachment = fixture.NewAttachment(file);

        var result = await fixture.Service.GrantAsync(
            file.Id,
            new FileShareGrantCreateRequest(Guid.NewGuid(), file.SharingVersion));

        Assert.False(result.IsSuccess);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorDetail?.Code);
        Assert.Empty(fixture.Grants.Grants);
        Assert.Equal(0, fixture.UnitOfWork.SaveCalls);
    }

    private sealed class Fixture
    {
        public Fixture(bool canManage)
        {
            Workspaces.CanManage = canManage;
            Service = new FileSharingService(
                Grants,
                Authorization,
                Workspaces,
                new CurrentUser(ActorUserId),
                new CurrentTenant(TenantId),
                Clock,
                Audit,
                Invalidations,
                UnitOfWork);
        }

        private Guid TenantId { get; } = Guid.NewGuid();
        public Guid WorkspaceId { get; } = Guid.NewGuid();
        public Guid ActorUserId { get; } = Guid.NewGuid();
        public Guid ExternalUserId { get; } = Guid.NewGuid();
        public FakeGrantRepository Grants { get; } = new();
        private FakeFileAuthorization Authorization { get; } = new();
        public FakeWorkspaceAuthorization Workspaces { get; } = new();
        private FixedClock Clock { get; } = new();
        private FakeAudit Audit { get; } = new();
        public FakeInvalidations Invalidations { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FileSharingService Service { get; }

        public FileObject NewFile(FileSharingPolicy policy) => new()
        {
            TenantId = TenantId,
            WorkspaceId = WorkspaceId,
            UploadedByUserId = ActorUserId,
            OriginalFileName = "sharing-test.txt",
            StorageKey = $"files/{Guid.NewGuid():N}",
            ContentType = "text/plain",
            SizeBytes = 42,
            Classification = DataClassification.Private,
            SharingPolicy = policy,
            SharingVersion = 1,
            Status = FileObjectStatus.Active
        };

        public Attachment NewAttachment(FileObject file) => new()
        {
            TenantId = TenantId,
            FileObjectId = file.Id,
            WorkspaceId = WorkspaceId,
            OwnerType = AttachmentOwnerType.Workspace,
            OwnerId = WorkspaceId,
            OwnerUserId = ActorUserId,
            UploadedByUserId = ActorUserId,
            FileName = file.OriginalFileName,
            StoredFileName = file.Id.ToString("N"),
            FilePath = file.StorageKey,
            ContentType = file.ContentType,
            Extension = ".txt",
            SizeBytes = file.SizeBytes,
            StorageProvider = "test",
            StorageKey = file.StorageKey,
            ScanStatus = FileScanStatus.Clean,
            FileObject = file
        };

        public FileAccessGrant NewGrant(
            Guid fileObjectId,
            Guid recipientUserId,
            FileAccessGrantRecipientKind kind) => new()
        {
            TenantId = TenantId,
            WorkspaceId = WorkspaceId,
            FileObjectId = fileObjectId,
            RecipientUserId = recipientUserId,
            RecipientKind = kind,
            GrantedByUserId = ActorUserId
        };
    }

    private sealed class FakeGrantRepository : IFileAccessGrantRepository
    {
        public Attachment? Attachment { get; set; }
        public List<FileAccessGrant> Grants { get; } = [];
        public List<FileAccessGrantCandidate> Candidates { get; } = [];
        public Dictionary<Guid, string> DisplayNames { get; } = [];
        public int ListRecipientCalls { get; private set; }
        public int ListCandidateCalls { get; private set; }

        public Task<Attachment?> GetWorkspaceAttachmentAsync(Guid fileObjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Attachment?.FileObjectId == fileObjectId ? Attachment : null);

        public Task<IReadOnlyDictionary<Guid, FileAccessGrantSummary>> GetEffectiveSummariesAsync(
            IReadOnlyCollection<Guid> fileObjectIds,
            CancellationToken cancellationToken = default)
        {
            var summaries = Grants
                .Where(grant => grant.RevokedAt is null && fileObjectIds.Contains(grant.FileObjectId))
                .GroupBy(grant => grant.FileObjectId)
                .ToDictionary(
                    group => group.Key,
                    group => new FileAccessGrantSummary(
                        group.Count(grant => grant.RecipientKind == FileAccessGrantRecipientKind.WorkspaceMember),
                        group.Count(grant => grant.RecipientKind == FileAccessGrantRecipientKind.ExternalProjectMember)));
            return Task.FromResult<IReadOnlyDictionary<Guid, FileAccessGrantSummary>>(summaries);
        }

        public Task<bool> HasEffectiveGrantAsync(Guid fileObjectId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Grants.Any(grant =>
                grant.FileObjectId == fileObjectId &&
                grant.RecipientUserId == userId &&
                grant.RevokedAt is null));

        public Task<IReadOnlyList<FileAccessGrantRecipient>> ListEffectiveRecipientsAsync(
            Guid fileObjectId,
            CancellationToken cancellationToken = default)
        {
            ListRecipientCalls++;
            return Task.FromResult<IReadOnlyList<FileAccessGrantRecipient>>(Grants
                .Where(grant => grant.FileObjectId == fileObjectId && grant.RevokedAt is null)
                .Select(grant => new FileAccessGrantRecipient(
                    grant.Id,
                    grant.RecipientUserId,
                    DisplayNames.GetValueOrDefault(grant.RecipientUserId, "Recipient"),
                    grant.RecipientKind))
                .ToList());
        }

        public Task<IReadOnlyList<FileAccessGrantCandidate>> ListEligibleRecipientsAsync(
            Guid workspaceId,
            CancellationToken cancellationToken = default)
        {
            ListCandidateCalls++;
            return Task.FromResult<IReadOnlyList<FileAccessGrantCandidate>>(Candidates);
        }

        public Task<FileAccessGrantCandidate?> FindEligibleRecipientAsync(
            Guid workspaceId,
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FileAccessGrantCandidate?>(Candidates.FirstOrDefault(candidate => candidate.UserId == userId));

        public Task<FileAccessGrant?> GetActiveGrantAsync(
            Guid fileObjectId,
            Guid grantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FileAccessGrant?>(Grants.FirstOrDefault(grant =>
                grant.FileObjectId == fileObjectId && grant.Id == grantId && grant.RevokedAt is null));

        public Task<FileAccessGrant?> GetActiveGrantForRecipientAsync(
            Guid fileObjectId,
            Guid recipientUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FileAccessGrant?>(Grants.FirstOrDefault(grant =>
                grant.FileObjectId == fileObjectId && grant.RecipientUserId == recipientUserId && grant.RevokedAt is null));

        public Task AddAsync(FileAccessGrant grant, CancellationToken cancellationToken = default)
        {
            Grants.Add(grant);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileAuthorization : IFileAuthorizationService
    {
        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(Guid userId, Guid workspaceId, IReadOnlyCollection<Attachment> attachments, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeWorkspaceAuthorization : IWorkspaceAuthorizationService
    {
        public bool CanManage { get; set; }
        public Task<bool> CanViewWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanContributeWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanManageWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(CanManage);
        public Task<bool> CanCreateWorkspace(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(false);
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

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeAudit : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeInvalidations : IBusinessInvalidationPublisher
    {
        public int FileChangeCalls { get; private set; }
        public string? LastChange { get; private set; }
        public Task TaskChangedAsync(TaskItem task, Guid actorUserId, string change, IEnumerable<string>? changedFields = null, IEnumerable<Guid>? affectedUserIds = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProjectChangedAsync(Project project, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AnnouncementChangedAsync(Announcement announcement, Guid actorUserId, string change, IEnumerable<Guid> audienceUserIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FileChangedAsync(FileObject fileObject, Attachment attachment, Guid actorUserId, string change, CancellationToken cancellationToken = default)
        {
            FileChangeCalls++;
            LastChange = change;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCalls { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return Task.FromResult(1);
        }
    }
}
