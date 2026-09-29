using System.Security.Cryptography;
using System.Text;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Files;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Projects;

public sealed class TaskExecutionResultServiceTests
{
    [Fact]
    [Trait("Scope", "Issue463")]
    public async Task AuthorizedSucceededResultSurvivesARepositoryReload()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.GetLatestAsync(fixture.Task.Id);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(TaskExecutionRunStatus.Succeeded, result.Value!.Status);
        Assert.NotNull(result.Value.Report);
        Assert.Equal(fixture.Result.Id, result.Value.Report!.Id);
        Assert.Equal(fixture.Result.BodyMarkdown, result.Value.Report.BodyMarkdown);
        Assert.Equal(fixture.Run.FinishedAtUtc, result.Value.FinishedAtUtc);
    }

    [Fact]
    [Trait("Scope", "Issue463")]
    public async Task SourceRevocationOrContentVersionMismatchRedactsTheEntireReport()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.Service.GetLatestAsync(fixture.Task.Id);
        Assert.True(before.IsSuccess, before.Error);

        var attachment = await fixture.Db.Attachments.SingleAsync(item => item.Id == fixture.Attachment.Id);
        attachment.ScanStatus = FileScanStatus.Pending;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var revoked = await fixture.Service.GetLatestAsync(fixture.Task.Id);

        Assert.False(revoked.IsSuccess);
        Assert.Equal("TASK_EXECUTION_RESULT_NOT_FOUND", revoked.ErrorDetail!.Code);
        Assert.DoesNotContain(fixture.Result.BodyMarkdown, revoked.Error ?? string.Empty, StringComparison.Ordinal);

        attachment = await fixture.Db.Attachments.Include(item => item.FileObject).SingleAsync(item => item.Id == fixture.Attachment.Id);
        attachment.ScanStatus = FileScanStatus.Clean;
        attachment.FileObject!.HashSha256 = new string('b', 64);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var changed = await fixture.Service.GetLatestAsync(fixture.Task.Id);
        Assert.False(changed.IsSuccess);
        Assert.Equal("TASK_EXECUTION_RESULT_NOT_FOUND", changed.ErrorDetail!.Code);
    }

    [Fact]
    [Trait("Scope", "Issue463")]
    public async Task CurrentProjectAuthorizationAndTenantScopeAreRecheckedOnEveryRead()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.ProjectAuthorization.CanView = false;
        var denied = await fixture.Service.GetAsync(fixture.Task.Id, fixture.Run.Id);
        Assert.False(denied.IsSuccess);
        Assert.Equal("TASK_EXECUTION_RESULT_NOT_FOUND", denied.ErrorDetail!.Code);

        fixture.ProjectAuthorization.CanView = true;
        await fixture.SwitchToOtherTenantAsync();
        var crossTenant = await fixture.Service.GetAsync(fixture.Task.Id, fixture.Run.Id);
        Assert.False(crossTenant.IsSuccess);
        Assert.Equal("TASK_EXECUTION_RESULT_NOT_FOUND", crossTenant.ErrorDetail!.Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext db,
            CurrentTenantService currentTenant,
            Tenant tenant,
            User actor,
            Workspace workspace,
            Project project,
            TaskItem task,
            TaskExecutionRun run,
            FileObject fileObject,
            Attachment attachment,
            TaskExecutionPersistedResult result,
            ControllableProjectAuthorization projectAuthorization,
            TaskExecutionResultService service)
        {
            Db = db;
            CurrentTenant = currentTenant;
            Tenant = tenant;
            Actor = actor;
            Workspace = workspace;
            Project = project;
            Task = task;
            Run = run;
            FileObject = fileObject;
            Attachment = attachment;
            Result = result;
            ProjectAuthorization = projectAuthorization;
            Service = service;
        }

        public AppDbContext Db { get; }
        private CurrentTenantService CurrentTenant { get; }
        public Tenant Tenant { get; }
        public User Actor { get; }
        public Workspace Workspace { get; }
        public Project Project { get; }
        public TaskItem Task { get; }
        public TaskExecutionRun Run { get; }
        public FileObject FileObject { get; }
        public Attachment Attachment { get; }
        public TaskExecutionPersistedResult Result { get; }
        public ControllableProjectAuthorization ProjectAuthorization { get; }
        public TaskExecutionResultService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var currentTenant = new CurrentTenantService();
            currentTenant.SetPlatformScope();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"issue463-{Guid.NewGuid():N}")
                    .Options,
                currentTenant);
            var tenant = new Tenant
            {
                Name = "Issue 463 tenant",
                DisplayName = "Issue 463 tenant",
                Slug = $"issue463-{Guid.NewGuid():N}"
            };
            var actor = new User
            {
                DisplayName = "Issue 463 reader",
                Email = $"issue463-{Guid.NewGuid():N}@example.test",
                NormalizedEmail = $"ISSUE463-{Guid.NewGuid():N}@EXAMPLE.TEST",
                PasswordHash = "not-used-by-test"
            };
            db.Tenants.Add(tenant);
            db.Users.Add(actor);
            await db.SaveChangesAsync();

            currentTenant.SetTenant(tenant.Id, tenant.Slug);
            var workspace = new Workspace
            {
                Name = "Issue 463 workspace",
                Slug = $"issue463-{Guid.NewGuid():N}",
                CreatedByUserId = actor.Id
            };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            var project = new Project
            {
                WorkspaceId = workspace.Id,
                OwnerUserId = actor.Id,
                CreatedByUserId = actor.Id,
                Name = "Issue 463 project",
                Slug = $"issue463-{Guid.NewGuid():N}",
                Status = ProjectStatus.Active
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var task = new TaskItem
            {
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = actor.Id,
                Title = "Durable result task"
            };
            db.TaskItems.Add(task);
            await db.SaveChangesAsync();

            var requestedAt = new DateTimeOffset(2026, 8, 30, 22, 0, 0, TimeSpan.Zero);
            var run = new TaskExecutionRun
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                TaskItemId = task.Id,
                RequestedByUserId = actor.Id,
                RequestedAtUtc = requestedAt,
                SnapshotScopeOrigin = TaskExecutionScopeOrigin.ProjectDefault,
                SnapshotProjectScopeVersion = 1,
                SnapshotWebEnabled = false,
                SnapshotProjectFilesEnabled = true
            };
            db.TaskExecutionRuns.Add(run);
            await db.SaveChangesAsync();
            run.Status = TaskExecutionRunStatus.Queued;
            run.QueuedAtUtc = requestedAt.AddSeconds(1);
            run.VersionNo++;
            await db.SaveChangesAsync();
            run.Status = TaskExecutionRunStatus.Running;
            run.StartedAtUtc = requestedAt.AddSeconds(2);
            run.VersionNo++;
            await db.SaveChangesAsync();
            run.Status = TaskExecutionRunStatus.Succeeded;
            run.FinishedAtUtc = requestedAt.AddSeconds(3);
            run.VersionNo++;
            await db.SaveChangesAsync();

            var hash = new string('a', 64);
            var fileObject = new FileObject
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                UploadedByUserId = actor.Id,
                OriginalFileName = "not-returned.txt",
                StorageKey = "not-returned/storage-key",
                ContentType = "text/plain",
                SizeBytes = 17,
                HashSha256 = hash,
                Status = FileObjectStatus.Active
            };
            db.FileObjects.Add(fileObject);
            await db.SaveChangesAsync();

            var attachment = new Attachment
            {
                TenantId = tenant.Id,
                FileObjectId = fileObject.Id,
                WorkspaceId = workspace.Id,
                OwnerType = AttachmentOwnerType.TaskItem,
                OwnerId = task.Id,
                OwnerUserId = actor.Id,
                UploadedByUserId = actor.Id,
                FileName = "not-returned.txt",
                StoredFileName = "not-returned.txt",
                FilePath = "not-returned/path",
                ContentType = "text/plain",
                Extension = ".txt",
                SizeBytes = 17,
                StorageProvider = "LocalFileSystem",
                StorageKey = fileObject.StorageKey,
                ScanStatus = FileScanStatus.Clean,
                FileObject = fileObject
            };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            const string reportBody = "# Durable report\n\nAuthorized metadata only.\n";
            var reportHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reportBody))).ToLowerInvariant();
            var result = new TaskExecutionPersistedResult(
                Guid.NewGuid(),
                tenant.Id,
                workspace.Id,
                project.Id,
                task.Id,
                run.Id,
                FirstPartyProjectFilesReportV1.SchemaVersion,
                TaskExecutionRunStatus.Succeeded.ToString(),
                FirstPartyProjectFilesReportV1.Title,
                reportBody,
                reportHash,
                requestedAt.AddSeconds(3),
                requestedAt.AddSeconds(3));
            var source = new TaskExecutionResultSourceReference(
                Guid.NewGuid(),
                fileObject.Id,
                attachment.Id,
                hash,
                "text/plain",
                17);
            var resultRepository = new StaticResultRepository(result, [source]);
            var projectAuthorization = new ControllableProjectAuthorization();
            var service = new TaskExecutionResultService(
                new ProjectRepository(db),
                projectAuthorization,
                new TaskExecutionScopeRepository(db),
                resultRepository,
                new FileRepository(db),
                new ControllableFileAuthorization(),
                new TestCurrentUser(actor.Id));

            return new Fixture(
                db,
                currentTenant,
                tenant,
                actor,
                workspace,
                project,
                task,
                run,
                fileObject,
                attachment,
                result,
                projectAuthorization,
                service);
        }

        public async Task SwitchToOtherTenantAsync()
        {
            CurrentTenant.SetPlatformScope();
            var other = new Tenant
            {
                Name = "Issue 463 other tenant",
                DisplayName = "Issue 463 other tenant",
                Slug = $"issue463-other-{Guid.NewGuid():N}"
            };
            Db.Tenants.Add(other);
            await Db.SaveChangesAsync();
            CurrentTenant.SetTenant(other.Id, other.Slug);
            Db.ChangeTracker.Clear();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class StaticResultRepository(
        TaskExecutionPersistedResult result,
        IReadOnlyList<TaskExecutionResultSourceReference> sources) : ITaskExecutionResultRepository
    {
        public Task<TaskExecutionPersistedResult?> GetByRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskExecutionPersistedResult?>(runId == result.TaskExecutionRunId ? result : null);

        public Task<IReadOnlyList<TaskExecutionResultSourceReference>> ListSourceReferencesAsync(
            Guid resultId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(resultId == result.Id ? sources : (IReadOnlyList<TaskExecutionResultSourceReference>)[]);
    }

    private sealed class ControllableProjectAuthorization : IProjectAuthorizationService
    {
        public bool CanView { get; set; } = true;

        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CanView);

        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class ControllableFileAuthorization : IFileAuthorizationService
    {
        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(
            Guid userId,
            Guid workspaceId,
            IReadOnlyCollection<Attachment> attachments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => global::Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }
}
