using System.Text;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Files;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

/// <summary>
/// Service-path acceptance for the Task/File canonical attachment boundary.
/// SQLite/InMemory cannot establish the tenant filters and live relational
/// owner-scope queries used by FileService and FileAuthorizationService.
/// </summary>
[Collection("PostgreSqlTaskV1")]
[Trait("Category", "PostgreSQLIntegration")]
[Trait("Scope", "TaskV1Prompt2D")]
public sealed class TaskV1FileOpenDownloadReauthorizationPostgreSqlTests
{
    [PostgreSqlFact]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task ActiveCleanTaskAssociationCanOpenIssueAndUseAGrant()
    {
        await using var fixture = await Fixture.CreateAsync();

        var open = await fixture.Service.GetAsync(fixture.AssociationId);
        var grant = await fixture.Service.RequestDownloadGrantAsync(fixture.AssociationId, new FileDownloadGrantRequest("task-detail"));
        var download = await fixture.Service.DownloadWithGrantAsync(grant.Value!.FileDownloadGrantId, grant.Value.Token);

        Assert.True(open.IsSuccess);
        Assert.True(grant.IsSuccess);
        Assert.True(download.IsSuccess);
        Assert.Equal(1, fixture.Storage.OpenReadCount);
        Assert.DoesNotContain(fixture.StorageKey, JsonSerializer.Serialize(await fixture.AuditRowsAsync()));
        Assert.DoesNotContain(grant.Value.Token, JsonSerializer.Serialize(await fixture.AuditRowsAsync()));
    }

    [PostgreSqlFact]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task MembershipLossDeniesOpenGrantIssueAndGrantUseBeforeStorageRead()
    {
        await using var fixture = await Fixture.CreateAsync();
        var grant = await fixture.Service.RequestDownloadGrantAsync(fixture.AssociationId, new FileDownloadGrantRequest());
        Assert.True(grant.IsSuccess);

        await fixture.SetWorkspaceMembershipAsync(MembershipStatus.Suspended);

        var open = await fixture.Service.GetAsync(fixture.AssociationId);
        var issue = await fixture.Service.RequestDownloadGrantAsync(fixture.AssociationId, new FileDownloadGrantRequest());
        var use = await fixture.Service.DownloadWithGrantAsync(grant.Value!.FileDownloadGrantId, grant.Value.Token);

        Assert.False(open.IsSuccess);
        Assert.False(issue.IsSuccess);
        Assert.False(use.IsSuccess);
        Assert.Equal(0, fixture.Storage.OpenReadCount);
        var audits = await fixture.AuditRowsAsync();
        Assert.Contains(audits, row => row.Action == "file_download.metadata_open_denied");
        Assert.Contains(audits, row => row.Action == "file_download.grant_issue_denied");
        Assert.Contains(audits, row => row.Action == "file_download.grant_use_denied");
    }

    [PostgreSqlFact]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task AssociationFileStateAndScopeChangesInvalidateExistingGrantWithoutReadingStorage()
    {
        foreach (var change in new Func<Fixture, Task>[]
                 {
                     fixture => fixture.MarkAssociationDeletedAsync(),
                     fixture => fixture.SetFileStatusAsync(FileObjectStatus.Quarantined),
                     fixture => fixture.SetFileStatusAsync(FileObjectStatus.Archived),
                     fixture => fixture.MarkFileDeletedAsync(),
                     fixture => fixture.SetScanStatusAsync(FileScanStatus.Failed),
                     fixture => fixture.SetFileWorkspaceAsync(),
                     fixture => fixture.SetFileProjectAsync()
                 })
        {
            await using var fixture = await Fixture.CreateAsync();
            var grant = await fixture.Service.RequestDownloadGrantAsync(fixture.AssociationId, new FileDownloadGrantRequest());
            Assert.True(grant.IsSuccess);

            await change(fixture);
            var result = await fixture.Service.DownloadWithGrantAsync(grant.Value!.FileDownloadGrantId, grant.Value.Token);

            Assert.False(result.IsSuccess);
            Assert.Equal(0, fixture.Storage.OpenReadCount);
            Assert.Contains(await fixture.AuditRowsAsync(), row => row.Action == "file_download.grant_use_denied");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AppDbContext db;

        private Fixture(AppDbContext db, FileService service, StorageSpy storage, Guid tenantId, Guid workspaceId, Guid projectId, Guid taskId, Guid associationId, Guid fileObjectId, Guid userId, string storageKey)
        {
            this.db = db;
            Service = service;
            Storage = storage;
            TenantId = tenantId;
            WorkspaceId = workspaceId;
            ProjectId = projectId;
            TaskId = taskId;
            AssociationId = associationId;
            FileObjectId = fileObjectId;
            UserId = userId;
            StorageKey = storageKey;
        }

        public FileService Service { get; }
        public StorageSpy Storage { get; }
        private Guid TenantId { get; }
        private Guid WorkspaceId { get; }
        public Guid ProjectId { get; }
        public Guid TaskId { get; }
        public Guid AssociationId { get; }
        private Guid FileObjectId { get; }
        private Guid UserId { get; }
        public string StorageKey { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
            var suffix = Guid.NewGuid().ToString("N");
            var tenant = new Tenant { Name = $"Task file {suffix}", DisplayName = "Task file", Slug = $"task-file-{suffix}" };
            var user = new User { DisplayName = "Task file reader", Email = $"task-file-{suffix}@example.test", NormalizedEmail = $"TASK-FILE-{suffix}@EXAMPLE.TEST", PasswordHash = "hash", Status = UserStatus.Active };

            await using (var platform = CreateContext(connectionString, null))
            {
                platform.AddRange(tenant, user);
                await platform.SaveChangesAsync();
            }

            var currentTenant = new CurrentTenantService();
            currentTenant.SetTenant(tenant.Id, tenant.Slug);
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, currentTenant);
            var workspace = new Workspace { TenantId = tenant.Id, Name = "Task file workspace", Slug = $"task-file-ws-{suffix}", CreatedByUserId = user.Id };
            var project = new Project { TenantId = tenant.Id, WorkspaceId = workspace.Id, OwnerUserId = user.Id, CreatedByUserId = user.Id, Name = "Task file project", Slug = $"task-file-project-{suffix}" };
            var task = new TaskItem { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, Title = "Task file", CreatedByUserId = user.Id, VersionNo = 1 };
            var storageKey = $"task-file/{suffix}";
            var file = new FileObject { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, UploadedByUserId = user.Id, OriginalFileName = "safe.txt", StorageKey = storageKey, ContentType = "text/plain", SizeBytes = 4, Classification = DataClassification.Private, Status = FileObjectStatus.Active };
            var association = new Attachment { TenantId = tenant.Id, FileObjectId = file.Id, WorkspaceId = workspace.Id, OwnerType = AttachmentOwnerType.TaskItem, OwnerId = task.Id, OwnerUserId = user.Id, UploadedByUserId = user.Id, FileName = "safe.txt", StoredFileName = "safe.txt", FilePath = "internal/not-returned", ContentType = "text/plain", Extension = ".txt", SizeBytes = 4, StorageProvider = "test", StorageKey = storageKey, ScanStatus = FileScanStatus.Clean };
            db.AddRange(workspace, project, task, file, association,
                new TenantUser { TenantId = tenant.Id, UserId = user.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = user.Id, Role = ProjectRole.Contributor, JoinedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            var currentUser = new TestCurrentUser(user.Id);
            var fileRepository = new FileRepository(db);
            var users = new UserRepository(db);
            var workspaces = new WorkspaceRepository(db);
            var groups = new GroupRepository(db);
            var workspaceAuthorization = new WorkspaceAuthorizationService(users, workspaces);
            var groupAuthorization = new GroupAuthorizationService(groups, workspaces, workspaceAuthorization);
            var projectAuthorization = new ProjectAuthorizationService(new ProjectRepository(db), workspaceAuthorization, groupAuthorization, groups);
            var authorization = new FileAuthorizationService(fileRepository, projectAuthorization, null!, null!, workspaceAuthorization);
            var storage = new StorageSpy();
            var service = new FileService(fileRepository, new FileDownloadGrantRepository(db), storage, authorization, new UploadPolicy(), new FeatureFlags(), new Quota(), currentUser, currentTenant, new Clock(), new DbAuditLogger(db, new Clock(), currentUser, currentTenant), new Sha256TokenHasher(), new NoopInvalidations(), new EfUnitOfWork(db));
            return new Fixture(db, service, storage, tenant.Id, workspace.Id, project.Id, task.Id, association.Id, file.Id, user.Id, storageKey);
        }

        public async Task SetWorkspaceMembershipAsync(MembershipStatus status)
        {
            var member = await db.WorkspaceMembers.SingleAsync(value => value.WorkspaceId == WorkspaceId && value.UserId == UserId);
            member.Status = status;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        public async Task MarkAssociationDeletedAsync()
        {
            var item = await db.Attachments.SingleAsync(value => value.Id == AssociationId);
            item.MarkDeleted(DateTimeOffset.UtcNow, UserId, "removed");
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        public async Task SetFileStatusAsync(FileObjectStatus status)
        {
            var item = await db.FileObjects.SingleAsync(value => value.Id == FileObjectId);
            item.Status = status;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        public async Task MarkFileDeletedAsync()
        {
            var item = await db.FileObjects.SingleAsync(value => value.Id == FileObjectId);
            item.MarkDeleted(DateTimeOffset.UtcNow, UserId, "deleted");
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        public async Task SetScanStatusAsync(FileScanStatus status)
        {
            var item = await db.Attachments.SingleAsync(value => value.Id == AssociationId);
            item.ScanStatus = status;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        public async Task SetFileWorkspaceAsync()
        {
            var marker = Guid.NewGuid().ToString("N");
            var workspace = new Workspace { TenantId = TenantId, Name = $"Other {marker}", Slug = $"other-{marker}", CreatedByUserId = UserId };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            var item = await db.FileObjects.SingleAsync(value => value.Id == FileObjectId);
            item.WorkspaceId = workspace.Id;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        public async Task SetFileProjectAsync()
        {
            var marker = Guid.NewGuid().ToString("N");
            var project = new Project { TenantId = TenantId, WorkspaceId = WorkspaceId, OwnerUserId = UserId, CreatedByUserId = UserId, Name = $"Other {marker}", Slug = $"other-{marker}" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var item = await db.FileObjects.SingleAsync(value => value.Id == FileObjectId);
            item.ProjectId = project.Id;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        public Task<List<AuditLog>> AuditRowsAsync() => db.AuditLogs.AsNoTracking().ToListAsync();

        public async ValueTask DisposeAsync() => await db.DisposeAsync();

        private static AppDbContext CreateContext(string connectionString, Tenant? tenant)
        {
            var currentTenant = new CurrentTenantService();
            if (tenant is null) currentTenant.SetPlatformScope(); else currentTenant.SetTenant(tenant.Id, tenant.Slug);
            return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, currentTenant);
        }
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => "task-file@example.test";
        public SystemRole? SystemRole => global::Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class UploadPolicy : IFileUploadPolicy { public long MaxFileSizeBytes => 1024; public IReadOnlyCollection<string> AllowedExtensions => [".txt"]; public IReadOnlyCollection<string> AllowedContentTypes => ["text/plain"]; }
    private sealed class FeatureFlags : IFeatureFlagService { public Task<bool> IsEnabledAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(true); public Task<Result> RequireEnabledAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success()); public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]); }
    private sealed class Quota : IQuotaService { public Task<TenantUsageSnapshot> GetCurrentUsageAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException(); public Task<Result> CanCreateUserAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success()); public Task<Result> CanCreateProjectAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success()); public Task<Result> CanUploadFileAsync(Guid tenantId, long size, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success()); public Task<Result> CanInviteGuestAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success()); public Task RecordApiRequestAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class StorageSpy : IFileStorageService { public int OpenReadCount { get; private set; } public Task<Result> SaveAsync(string key, Stream stream, string contentType, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success()); public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) { OpenReadCount++; return Task.FromResult<Stream>(new MemoryStream("safe"u8.ToArray())); } public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask; public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(true); public Task<string?> CreateSignedReadUrlAsync(string key, TimeSpan expiresIn, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null); }
    private sealed class NoopInvalidations : IBusinessInvalidationPublisher { public Task TaskChangedAsync(TaskItem task, Guid actor, string change, IEnumerable<string>? fields = null, IEnumerable<Guid>? affected = null, CancellationToken cancellationToken = default) => Task.CompletedTask; public Task ProjectChangedAsync(Project project, Guid actor, string change, CancellationToken cancellationToken = default) => Task.CompletedTask; public Task AnnouncementChangedAsync(Announcement announcement, Guid actor, string change, IEnumerable<Guid> audience, CancellationToken cancellationToken = default) => Task.CompletedTask; public Task FileChangedAsync(FileObject file, Attachment attachment, Guid actor, string change, CancellationToken cancellationToken = default) => Task.CompletedTask; }
}
