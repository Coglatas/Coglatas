using Coglatas.Application.Artifacts;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Adds the commit-time authorization and compare-and-swap fence required by
/// localized report refinement. The underlying refinement service still owns
/// validation, source materialization, copy-on-write projection and auditing;
/// this decorator owns the mutation boundary that must remain fail-closed while
/// those potentially slow source reads are in flight.
/// </summary>
public sealed class GuardedArtifactReportRefinementService : IArtifactReportRefinementService
{
    private readonly DbArtifactReportRefinementService inner;
    private readonly ArtifactReportRefinementCommitGuardUnitOfWork commitGuard;
    private readonly ICurrentUser currentUser;

    public GuardedArtifactReportRefinementService(
        AppDbContext db,
        IArtifactRepository artifacts,
        IArtifactAuthorizationService artifactAuthorization,
        IProjectAuthorizationService projectAuthorization,
        ITaskExecutionScopeService executionScopes,
        IResearchPlanRepository researchPlans,
        IFileRepository files,
        IFileAuthorizationService fileAuthorization,
        IFileStorageService storage,
        ICurrentUser currentUser,
        IClock clock,
        IAuditLogger auditLogger,
        IUnitOfWork unitOfWork)
    {
        this.currentUser = currentUser;
        commitGuard = new ArtifactReportRefinementCommitGuardUnitOfWork(
            db,
            unitOfWork,
            artifactAuthorization,
            projectAuthorization);
        inner = new DbArtifactReportRefinementService(
            db,
            artifacts,
            artifactAuthorization,
            projectAuthorization,
            executionScopes,
            researchPlans,
            files,
            fileAuthorization,
            storage,
            currentUser,
            clock,
            auditLogger,
            commitGuard);
    }

    public Task<Result<ArtifactReportRefinementPreflightResponse>> PreflightAsync(
        Guid projectId,
        Guid baseArtifactVersionId,
        ArtifactReportRefinementTargetKind targetKind,
        Guid targetLogicalId,
        CancellationToken cancellationToken = default) =>
        inner.PreflightAsync(projectId, baseArtifactVersionId, targetKind, targetLogicalId, cancellationToken);

    public async Task<Result<ArtifactReportRefinementResponse>> RefineAsync(
        Guid projectId,
        Guid baseArtifactVersionId,
        RefineArtifactReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        if (!currentUser.IsAuthenticated || userId == Guid.Empty)
            return await inner.RefineAsync(projectId, baseArtifactVersionId, request, cancellationToken);

        commitGuard.Begin(projectId, baseArtifactVersionId, userId);
        try
        {
            return await inner.RefineAsync(projectId, baseArtifactVersionId, request, cancellationToken);
        }
        catch (RefinementCommitAuthorizationChangedException)
        {
            return Result<ArtifactReportRefinementResponse>.Failure(new ApplicationErrorDetail(
                "ReportNotFound",
                "The report is not available."));
        }
        catch (RefinementCommitStaleVersionException)
        {
            return Result<ArtifactReportRefinementResponse>.Failure(new ApplicationErrorDetail(
                "ReportRefinementStaleVersion",
                "A newer report version already exists. Review that version before refining again."));
        }
        finally
        {
            commitGuard.End();
        }
    }
}

/// <summary>
/// Commit fence for report refinement. On relational providers the staged
/// immutable snapshot is written inside a transaction first, then the Artifact
/// current-version pointer is advanced with an atomic compare-and-swap. If the
/// expected base is no longer current, the entire transaction is rolled back.
/// </summary>
public sealed class ArtifactReportRefinementCommitGuardUnitOfWork(
    AppDbContext db,
    IUnitOfWork inner,
    IArtifactAuthorizationService artifactAuthorization,
    IProjectAuthorizationService projectAuthorization) : IUnitOfWork
{
    private CommitContext? context;

    public void Begin(Guid projectId, Guid baseArtifactVersionId, Guid userId) =>
        context = new CommitContext(projectId, baseArtifactVersionId, userId);

    public void End() => context = null;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (context is not { } commit)
            return await inner.SaveChangesAsync(cancellationToken);

        var artifactEntry = db.ChangeTracker.Entries<Artifact>()
            .SingleOrDefault(entry =>
                entry.Entity.ProjectId == commit.ProjectId &&
                entry.Property(artifact => artifact.CurrentVersionId).IsModified &&
                entry.Property(artifact => artifact.CurrentVersionId).OriginalValue == commit.BaseArtifactVersionId);
        if (artifactEntry is null)
            throw new RefinementCommitStaleVersionException();

        var currentVersion = artifactEntry.Property(artifact => artifact.CurrentVersionId);
        var desiredCurrentVersionId = currentVersion.CurrentValue;
        if (desiredCurrentVersionId is null || desiredCurrentVersionId == commit.BaseArtifactVersionId)
            throw new RefinementCommitStaleVersionException();

        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ReauthorizeAsync(commit, artifactEntry.Entity.Id, cancellationToken);

            // Persist the immutable snapshot while the tracked Artifact still
            // carries the confirmed base pointer. This prevents DetectChanges
            // from reconstructing a blind CurrentVersionId update; the CAS below
            // is the only statement allowed to advance the pointer.
            currentVersion.CurrentValue = commit.BaseArtifactVersionId;
            currentVersion.OriginalValue = commit.BaseArtifactVersionId;
            currentVersion.IsModified = false;
            var saved = await inner.SaveChangesAsync(cancellationToken);

            var advanced = await db.Artifacts
                .Where(artifact =>
                    artifact.Id == artifactEntry.Entity.Id &&
                    artifact.TenantId == artifactEntry.Entity.TenantId &&
                    artifact.ProjectId == commit.ProjectId &&
                    !artifact.DeletedAt.HasValue &&
                    artifact.CurrentVersionId == commit.BaseArtifactVersionId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        artifact => artifact.CurrentVersionId,
                        desiredCurrentVersionId),
                    cancellationToken);

            if (advanced != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                throw new RefinementCommitStaleVersionException();
            }

            await transaction.CommitAsync(cancellationToken);
            currentVersion.CurrentValue = desiredCurrentVersionId;
            currentVersion.OriginalValue = desiredCurrentVersionId;
            currentVersion.IsModified = false;
            return saved;
        }

        // The InMemory provider used by focused unit tests cannot execute the
        // relational CAS. Re-read the store before SaveChanges so the same stale
        // base and authorization behavior is covered; production uses the atomic
        // transaction/CAS path above.
        await ReauthorizeAsync(commit, artifactEntry.Entity.Id, cancellationToken);
        var persistedCurrentVersionId = await db.Artifacts
            .AsNoTracking()
            .Where(artifact =>
                artifact.Id == artifactEntry.Entity.Id &&
                artifact.TenantId == artifactEntry.Entity.TenantId &&
                artifact.ProjectId == commit.ProjectId &&
                !artifact.DeletedAt.HasValue)
            .Select(artifact => artifact.CurrentVersionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (persistedCurrentVersionId != commit.BaseArtifactVersionId)
            throw new RefinementCommitStaleVersionException();

        return await inner.SaveChangesAsync(cancellationToken);
    }

    private async Task ReauthorizeAsync(
        CommitContext commit,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        if (!await projectAuthorization.CanViewProject(commit.UserId, commit.ProjectId, cancellationToken) ||
            !await artifactAuthorization.CanViewArtifact(commit.UserId, artifactId, cancellationToken) ||
            !await artifactAuthorization.CanUpdateArtifact(commit.UserId, artifactId, cancellationToken))
            throw new RefinementCommitAuthorizationChangedException();
    }

    private sealed record CommitContext(Guid ProjectId, Guid BaseArtifactVersionId, Guid UserId);
}

public sealed class RefinementCommitAuthorizationChangedException : Exception;

public sealed class RefinementCommitStaleVersionException : Exception;
