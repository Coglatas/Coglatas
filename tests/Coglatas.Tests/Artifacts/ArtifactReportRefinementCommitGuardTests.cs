using Coglatas.Application.Artifacts;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Artifacts;

public sealed class ArtifactReportRefinementCommitGuardTests
{
    [Fact]
    public async Task SaveChangesAsync_FailsClosedWhenUpdateAuthorizationIsRevokedBeforeCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var desiredVersion = fixture.StageDesiredVersion(versionNumber: 2);
        fixture.ArtifactAuthorization.CanUpdate = false;

        fixture.Guard.Begin(fixture.ProjectId, fixture.BaseVersionId, fixture.UserId);
        await Assert.ThrowsAsync<RefinementCommitAuthorizationChangedException>(
            () => fixture.Guard.SaveChangesAsync());
        fixture.Guard.End();

        fixture.Context.ChangeTracker.Clear();
        var artifact = await fixture.Context.Artifacts.AsNoTracking().SingleAsync();
        Assert.Equal(fixture.BaseVersionId, artifact.CurrentVersionId);
        Assert.Equal(1, await fixture.Context.ArtifactVersions.CountAsync());
        Assert.DoesNotContain(
            await fixture.Context.ArtifactVersions.AsNoTracking().Select(version => version.Id).ToListAsync(),
            id => id == desiredVersion.Id);
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsStaleBaseWhenAnotherVersionBecomesCurrentDuringRefinement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var desiredVersion = fixture.StageDesiredVersion(versionNumber: 3);

        Guid competingVersionId;
        await using (var competingContext = fixture.CreateSiblingContext())
        {
            var artifact = await competingContext.Artifacts.SingleAsync();
            var competingVersion = new ArtifactVersion
            {
                TenantId = fixture.TenantId,
                ArtifactId = fixture.ArtifactId,
                VersionNumber = 2,
                FileObjectId = fixture.FileObjectId,
                CreatedByUserId = fixture.UserId
            };
            competingContext.ArtifactVersions.Add(competingVersion);
            artifact.CurrentVersionId = competingVersion.Id;
            await competingContext.SaveChangesAsync();
            competingVersionId = competingVersion.Id;
        }

        fixture.Guard.Begin(fixture.ProjectId, fixture.BaseVersionId, fixture.UserId);
        await Assert.ThrowsAsync<RefinementCommitStaleVersionException>(
            () => fixture.Guard.SaveChangesAsync());
        fixture.Guard.End();

        fixture.Context.ChangeTracker.Clear();
        var persistedArtifact = await fixture.Context.Artifacts.AsNoTracking().SingleAsync();
        Assert.Equal(competingVersionId, persistedArtifact.CurrentVersionId);
        Assert.Equal(2, await fixture.Context.ArtifactVersions.CountAsync());
        Assert.DoesNotContain(
            await fixture.Context.ArtifactVersions.AsNoTracking().Select(version => version.Id).ToListAsync(),
            id => id == desiredVersion.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly CurrentTenant _currentTenant;

        private Fixture(
            AppDbContext context,
            DbContextOptions<AppDbContext> options,
            CurrentTenant currentTenant,
            Guid tenantId,
            Guid projectId,
            Guid artifactId,
            Guid baseVersionId,
            Guid fileObjectId,
            Guid userId,
            ArtifactAuthorization artifactAuthorization,
            ArtifactReportRefinementCommitGuardUnitOfWork guard)
        {
            Context = context;
            _options = options;
            _currentTenant = currentTenant;
            TenantId = tenantId;
            ProjectId = projectId;
            ArtifactId = artifactId;
            BaseVersionId = baseVersionId;
            FileObjectId = fileObjectId;
            UserId = userId;
            ArtifactAuthorization = artifactAuthorization;
            Guard = guard;
        }

        public AppDbContext Context { get; }
        public Guid TenantId { get; }
        public Guid ProjectId { get; }
        public Guid ArtifactId { get; }
        public Guid BaseVersionId { get; }
        public Guid FileObjectId { get; }
        public Guid UserId { get; }
        public ArtifactAuthorization ArtifactAuthorization { get; }
        public ArtifactReportRefinementCommitGuardUnitOfWork Guard { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var databaseName = Guid.NewGuid().ToString("N");
            var tenantId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var currentTenant = new CurrentTenant(tenantId);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
            var context = new AppDbContext(options, currentTenant);

            context.Tenants.Add(new Tenant(tenantId)
            {
                Name = "Refinement guard tenant",
                Slug = "refinement-guard",
                DisplayName = "Refinement guard tenant",
                Status = TenantStatus.Active
            });
            await context.SaveChangesAsync();

            var fileObject = new FileObject
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                UploadedByUserId = userId,
                OriginalFileName = "report.pdf",
                StorageKey = "artifact/report.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1,
                Status = FileObjectStatus.Active
            };
            var artifact = new Artifact
            {
                TenantId = tenantId,
                ProjectId = projectId,
                Name = "Guarded report",
                CreatedByUserId = userId
            };
            var baseVersion = new ArtifactVersion
            {
                TenantId = tenantId,
                ArtifactId = artifact.Id,
                Artifact = artifact,
                VersionNumber = 1,
                FileObjectId = fileObject.Id,
                FileObject = fileObject,
                CreatedByUserId = userId
            };
            artifact.CurrentVersionId = baseVersion.Id;
            artifact.CurrentVersion = baseVersion;

            context.FileObjects.Add(fileObject);
            context.Artifacts.Add(artifact);
            context.ArtifactVersions.Add(baseVersion);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var artifactAuthorization = new ArtifactAuthorization();
            var guard = new ArtifactReportRefinementCommitGuardUnitOfWork(
                context,
                new UnitOfWork(context),
                artifactAuthorization,
                new ProjectAuthorization());
            return new Fixture(
                context,
                options,
                currentTenant,
                tenantId,
                projectId,
                artifact.Id,
                baseVersion.Id,
                fileObject.Id,
                userId,
                artifactAuthorization,
                guard);
        }

        public ArtifactVersion StageDesiredVersion(int versionNumber)
        {
            var artifact = Context.Artifacts.Single();
            var desiredVersion = new ArtifactVersion
            {
                TenantId = TenantId,
                ArtifactId = ArtifactId,
                Artifact = artifact,
                VersionNumber = versionNumber,
                FileObjectId = FileObjectId,
                CreatedByUserId = UserId
            };
            Context.ArtifactVersions.Add(desiredVersion);
            artifact.CurrentVersionId = desiredVersion.Id;
            return desiredVersion;
        }

        public AppDbContext CreateSiblingContext() => new(_options, _currentTenant);

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class ArtifactAuthorization : IArtifactAuthorizationService
    {
        public bool CanUpdate { get; set; } = true;
        public Task<bool> CanViewArtifact(Guid userId, Guid artifactId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanUploadArtifact(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanUpdateArtifact(Guid userId, Guid artifactId, CancellationToken cancellationToken = default) => Task.FromResult(CanUpdate);
        public Task<bool> CanDownloadArtifactVersion(Guid userId, Guid versionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class ProjectAuthorization : IProjectAuthorizationService
    {
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class CurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string? TenantSlug => "refinement-guard";
        public bool IsPlatformScope => false;
    }

    private sealed class UnitOfWork(AppDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
    }
}