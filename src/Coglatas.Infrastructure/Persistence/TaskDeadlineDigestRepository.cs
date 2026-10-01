using System.Data;
using System.Text.Json;
using Coglatas.Application.Notifications;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Coglatas.Infrastructure.Persistence;

public sealed class TaskDeadlineDigestRepository(
    AppDbContext dbContext,
    ICurrentTenant currentTenant) : ITaskDeadlineDigestRepository
{
    private const int MaximumPageSize = 500;
    private const string ClaimExpiredErrorCode = "DigestClaimExpired";

    public async Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = BoundPage(page, pageSize);
        return await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Status == TenantStatus.Active && tenant.DeletedAt == null)
            .OrderBy(tenant => tenant.Id)
            .Skip(safePage * safePageSize)
            .Take(safePageSize)
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<string?> GetTenantTimeZoneIdAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        dbContext.TenantSettings
            .AsNoTracking()
            .Where(settings => settings.TenantId == tenantId)
            .Select(settings => settings.TimeZone)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<TaskDeadlineDigestScheduleCandidate>> ListScheduleCandidatesAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = BoundPage(page, pageSize);
        return await (
                from member in dbContext.WorkspaceMembers.AsNoTracking()
                join workspace in dbContext.Workspaces.AsNoTracking() on member.WorkspaceId equals workspace.Id
                join user in dbContext.Users.AsNoTracking() on member.UserId equals user.Id
                join tenantUser in dbContext.TenantUsers.AsNoTracking()
                    on new { member.TenantId, member.UserId } equals new { tenantUser.TenantId, tenantUser.UserId }
                where member.Status == MembershipStatus.Active &&
                      workspace.Status == WorkspaceStatus.Active &&
                      workspace.DeletedAt == null &&
                      user.Status == UserStatus.Active &&
                      user.DeletedAt == null &&
                      tenantUser.Status == TenantUserStatus.Active
                orderby member.WorkspaceId, member.UserId
                select new TaskDeadlineDigestScheduleCandidate(
                    member.WorkspaceId,
                    member.UserId,
                    workspace.TimeZone,
                    member.TaskDeadlineDigestLocalTime ?? workspace.DefaultTaskDeadlineDigestLocalTime))
            .Skip(safePage * safePageSize)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> UpsertSchedulesAsync(
        IReadOnlyCollection<TaskDeadlineDigestScheduleWrite> schedules,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (schedules.Count == 0)
            return 0;
        if (!currentTenant.IsAvailable)
            throw new InvalidOperationException("A tenant scope is required to schedule Task deadline digests.");

        if (dbContext.Database.IsNpgsql())
        {
            var payload = JsonSerializer.Serialize(schedules);
            return await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO task_deadline_digest_jobs
                    ("Id", "TenantId", "WorkspaceId", "UserId", "LocalDate", "PolicyVersion",
                     "Status", "AttemptCount", "AutomaticAttemptCount", "AttemptSequence",
                     "ScheduledForUtc", "NextAttemptAt", "CreatedAt", "UpdatedAt")
                SELECT schedule."JobId", tenant."TenantId", schedule."WorkspaceId", schedule."UserId",
                       schedule."LocalDate", schedule."PolicyVersion", 'Pending', 0, 0, 0,
                       schedule."ScheduledForUtc", schedule."ScheduledForUtc", {{now}}, {{now}}
                FROM jsonb_to_recordset(CAST({{payload}} AS jsonb)) AS schedule(
                    "JobId" uuid,
                    "WorkspaceId" uuid,
                    "UserId" uuid,
                    "LocalDate" date,
                    "PolicyVersion" integer,
                    "ScheduledForUtc" timestamptz)
                CROSS JOIN LATERAL (
                    SELECT "TenantId"
                    FROM workspaces
                    WHERE "Id" = schedule."WorkspaceId"
                      AND "TenantId" = {{currentTenant.TenantId}}
                ) AS tenant
                ON CONFLICT ("TenantId", "WorkspaceId", "UserId", "LocalDate", "PolicyVersion")
                DO UPDATE SET
                    "ScheduledForUtc" = EXCLUDED."ScheduledForUtc",
                    "NextAttemptAt" = EXCLUDED."NextAttemptAt",
                    "UpdatedAt" = {{now}}
                WHERE task_deadline_digest_jobs."Status" = 'Pending'
                  AND task_deadline_digest_jobs."AttemptCount" = 0
                  AND task_deadline_digest_jobs."AttemptSequence" = 0
                  AND (
                      task_deadline_digest_jobs."ScheduledForUtc"
                          IS DISTINCT FROM EXCLUDED."ScheduledForUtc"
                      OR task_deadline_digest_jobs."NextAttemptAt"
                          IS DISTINCT FROM EXCLUDED."NextAttemptAt"
                  );
                """, cancellationToken);
        }

        var workspaceIds = schedules.Select(schedule => schedule.WorkspaceId).Distinct().ToArray();
        var localDates = schedules.Select(schedule => schedule.LocalDate).Distinct().ToArray();
        var existing = await dbContext.TaskDeadlineDigestJobs
            .Where(job => Enumerable.Contains(workspaceIds, job.WorkspaceId) && Enumerable.Contains(localDates, job.LocalDate))
            .ToListAsync(cancellationToken);
        var byIdentity = existing.ToDictionary(job =>
            (job.WorkspaceId, job.UserId, job.LocalDate, job.PolicyVersion));
        var tenantByWorkspace = await dbContext.Workspaces
            .Where(workspace => Enumerable.Contains(workspaceIds, workspace.Id))
            .ToDictionaryAsync(workspace => workspace.Id, workspace => workspace.TenantId, cancellationToken);

        var changed = 0;
        foreach (var schedule in schedules)
        {
            var identity = (schedule.WorkspaceId, schedule.UserId, schedule.LocalDate, schedule.PolicyVersion);
            if (byIdentity.TryGetValue(identity, out var job))
            {
                if (job.Status != TaskDeadlineDigestJobStatus.Pending ||
                    job.AttemptCount != 0 ||
                    job.AttemptSequence != 0)
                    continue;

                if (job.ScheduledForUtc == schedule.ScheduledForUtc &&
                    job.NextAttemptAt == schedule.ScheduledForUtc)
                    continue;

                job.ScheduledForUtc = schedule.ScheduledForUtc;
                job.NextAttemptAt = schedule.ScheduledForUtc;
                changed++;
                continue;
            }

            if (!tenantByWorkspace.TryGetValue(schedule.WorkspaceId, out var tenantId))
                continue;

            await dbContext.TaskDeadlineDigestJobs.AddAsync(new TaskDeadlineDigestJob
            {
                TenantId = tenantId,
                WorkspaceId = schedule.WorkspaceId,
                UserId = schedule.UserId,
                LocalDate = schedule.LocalDate,
                PolicyVersion = schedule.PolicyVersion,
                Status = TaskDeadlineDigestJobStatus.Pending,
                ScheduledForUtc = schedule.ScheduledForUtc,
                NextAttemptAt = schedule.ScheduledForUtc
            }, cancellationToken);
            changed++;
        }

        if (changed > 0)
            await dbContext.SaveChangesAsync(cancellationToken);
        return changed;
    }

    public async Task<IReadOnlyList<TaskDeadlineDigestClaim>> ClaimDueAsync(
        string claimOwner,
        DateTimeOffset now,
        int batchSize,
        TimeSpan claimTimeout,
        CancellationToken cancellationToken = default)
    {
        var boundedOwner = BoundRequired(claimOwner, 160, nameof(claimOwner));
        if (!currentTenant.IsAvailable)
            throw new InvalidOperationException("A tenant scope is required to claim Task deadline digests.");
        var boundedBatch = Math.Clamp(batchSize, 1, 100);
        var boundedTimeout = claimTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : claimTimeout;
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var stale = await SelectStaleClaimsAsync(now, boundedBatch, cancellationToken);
        if (stale.Count > 0)
        {
            var staleTokens = stale.Where(job => job.ClaimToken.HasValue).Select(job => job.ClaimToken!.Value).ToArray();
            var staleAttempts = await dbContext.TaskDeadlineDigestAttempts
                .Where(attempt => attempt.Status == TaskDeadlineDigestAttemptStatus.Claimed &&
                                  attempt.ClaimToken.HasValue && Enumerable.Contains(staleTokens, attempt.ClaimToken.Value))
                .ToDictionaryAsync(attempt => attempt.ClaimToken!.Value, cancellationToken);
            foreach (var job in stale)
            {
                if (!job.ClaimToken.HasValue || !staleAttempts.TryGetValue(job.ClaimToken.Value, out var attempt))
                    continue;

                attempt.Status = TaskDeadlineDigestAttemptStatus.Expired;
                attempt.CompletedAt = now;
                attempt.LastErrorCode = ClaimExpiredErrorCode;
                ClearAttemptClaim(attempt);

                job.LastErrorCode = ClaimExpiredErrorCode;
                ClearJobClaim(job);
                if (attempt.Trigger == TaskDeadlineDigestAttemptTrigger.OperatorRestart ||
                    job.AutomaticAttemptCount >= TaskDeadlineDigestPolicy.MaximumAutomaticAttempts)
                {
                    job.Status = TaskDeadlineDigestJobStatus.Failed;
                    job.CompletedAt = now;
                    job.NextAttemptAt = null;
                }
                else
                {
                    job.Status = TaskDeadlineDigestJobStatus.Pending;
                    job.NextAttemptAt = now;
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var due = await SelectDueJobsAsync(now, boundedBatch, cancellationToken);
        if (due.Count == 0)
        {
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var dueIds = due.Select(job => job.Id).ToArray();
        var pendingRestarts = await dbContext.TaskDeadlineDigestAttempts
            .Where(attempt => Enumerable.Contains(dueIds, attempt.JobId) &&
                              attempt.Trigger == TaskDeadlineDigestAttemptTrigger.OperatorRestart &&
                              attempt.Status == TaskDeadlineDigestAttemptStatus.Pending)
            .ToDictionaryAsync(attempt => attempt.JobId, cancellationToken);
        var claims = new List<TaskDeadlineDigestClaim>(due.Count);
        foreach (var job in due)
        {
            if (!pendingRestarts.TryGetValue(job.Id, out var attempt))
            {
                if (job.AutomaticAttemptCount >= TaskDeadlineDigestPolicy.MaximumAutomaticAttempts)
                {
                    job.Status = TaskDeadlineDigestJobStatus.Failed;
                    job.CompletedAt = now;
                    job.NextAttemptAt = null;
                    job.LastErrorCode ??= "DigestAutomaticAttemptsExhausted";
                    continue;
                }

                job.AttemptSequence++;
                attempt = new TaskDeadlineDigestAttempt
                {
                    TenantId = job.TenantId,
                    JobId = job.Id,
                    AttemptNumber = job.AttemptSequence,
                    Trigger = TaskDeadlineDigestAttemptTrigger.Automatic
                };
                await dbContext.TaskDeadlineDigestAttempts.AddAsync(attempt, cancellationToken);
                job.AutomaticAttemptCount++;
            }

            job.AttemptCount++;
            var claimToken = Guid.NewGuid();
            var expiresAt = now + boundedTimeout;
            job.Status = TaskDeadlineDigestJobStatus.Claimed;
            job.ClaimOwner = boundedOwner;
            job.ClaimToken = claimToken;
            job.ClaimedAt = now;
            job.ClaimExpiresAt = expiresAt;
            job.NextAttemptAt = null;
            job.LastErrorCode = null;

            attempt.Status = TaskDeadlineDigestAttemptStatus.Claimed;
            attempt.ClaimOwner = boundedOwner;
            attempt.ClaimToken = claimToken;
            attempt.ClaimedAt = now;
            attempt.ClaimExpiresAt = expiresAt;
            attempt.LastErrorCode = null;

            claims.Add(new TaskDeadlineDigestClaim(
                job.Id,
                job.TenantId,
                job.WorkspaceId,
                job.UserId,
                job.LocalDate,
                job.PolicyVersion,
                claimToken,
                attempt.Trigger));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return claims;
    }

    public async Task<TaskDeadlineDigestClaim?> GetClaimedAsync(
        Guid jobId,
        Guid claimToken,
        bool forUpdate,
        CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAvailable)
            throw new InvalidOperationException("A tenant scope is required to load a Task deadline digest claim.");

        TaskDeadlineDigestJob? job;
        if (forUpdate && dbContext.Database.IsNpgsql())
        {
            job = await dbContext.TaskDeadlineDigestJobs
                .FromSqlInterpolated($$"""
                    SELECT * FROM task_deadline_digest_jobs
                    WHERE "Id" = {{jobId}}
                      AND "TenantId" = {{currentTenant.TenantId}}
                      AND "ClaimToken" = {{claimToken}}
                      AND "Status" = 'Claimed'
                    FOR UPDATE
                    """)
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            job = await dbContext.TaskDeadlineDigestJobs.SingleOrDefaultAsync(
                candidate => candidate.Id == jobId &&
                             candidate.TenantId == currentTenant.TenantId &&
                             candidate.ClaimToken == claimToken &&
                             candidate.Status == TaskDeadlineDigestJobStatus.Claimed,
                cancellationToken);
        }

        if (job is null)
            return null;

        TaskDeadlineDigestAttempt? attempt;
        if (forUpdate && dbContext.Database.IsNpgsql())
        {
            attempt = await dbContext.TaskDeadlineDigestAttempts
                .FromSqlInterpolated($$"""
                    SELECT * FROM task_deadline_digest_attempts
                    WHERE "JobId" = {{job.Id}}
                      AND "TenantId" = {{currentTenant.TenantId}}
                      AND "ClaimToken" = {{claimToken}}
                      AND "Status" = 'Claimed'
                    FOR UPDATE
                    """)
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            attempt = await dbContext.TaskDeadlineDigestAttempts
                .SingleOrDefaultAsync(candidate =>
                    candidate.JobId == job.Id &&
                    candidate.TenantId == currentTenant.TenantId &&
                    candidate.ClaimToken == claimToken &&
                    candidate.Status == TaskDeadlineDigestAttemptStatus.Claimed,
                    cancellationToken);
        }

        return attempt is not null
            ? new TaskDeadlineDigestClaim(
                job.Id,
                job.TenantId,
                job.WorkspaceId,
                job.UserId,
                job.LocalDate,
                job.PolicyVersion,
                claimToken,
                attempt.Trigger)
            : null;
    }

    /// <summary>
    /// Acquires the generation fence in the fixed Job then Attempt order
    /// before the transaction reads current context or requests the recipient
    /// User lock. The locked rows remain held until the enclosing generation
    /// transaction commits or rolls back, so the expiry scanner's
    /// <c>FOR UPDATE SKIP LOCKED</c> selection skips an active generation.
    /// </summary>
    public async Task<TaskDeadlineDigestClaim?> AcquireGenerationClaimFenceAsync(
        TaskDeadlineDigestClaim claim,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lockedClaim = await GetClaimedAsync(
                claim.JobId,
                claim.ClaimToken,
                forUpdate: true,
                cancellationToken);
            return lockedClaim is not null && HasSameClaimIdentity(claim, lockedClaim)
                ? lockedClaim
                : null;
        }
        catch (Exception exception) when (TaskDeadlineDigestPersistenceConflictClassifier.IsRetryable(exception))
        {
            throw new TaskDeadlineDigestRetryablePersistenceConflictException();
        }
    }

    public async Task<TaskDeadlineDigestCurrentContext?> GetCurrentContextAsync(
        Guid jobId,
        Guid claimToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await (
                from job in dbContext.TaskDeadlineDigestJobs.AsNoTracking()
                join workspace in dbContext.Workspaces.AsNoTracking() on job.WorkspaceId equals workspace.Id
                join member in dbContext.WorkspaceMembers.AsNoTracking()
                    on new { job.WorkspaceId, job.UserId } equals new { member.WorkspaceId, member.UserId }
                join user in dbContext.Users.AsNoTracking() on job.UserId equals user.Id
                join tenantUser in dbContext.TenantUsers.AsNoTracking()
                    on new { job.TenantId, job.UserId } equals new { tenantUser.TenantId, tenantUser.UserId }
                join tenant in dbContext.Tenants.AsNoTracking() on job.TenantId equals tenant.Id
                join tenantSettings in dbContext.TenantSettings.AsNoTracking() on job.TenantId equals tenantSettings.TenantId into settings
                from tenantSettings in settings.DefaultIfEmpty()
                where job.Id == jobId &&
                      job.ClaimToken == claimToken &&
                      job.Status == TaskDeadlineDigestJobStatus.Claimed &&
                      tenant.Status == TenantStatus.Active && tenant.DeletedAt == null &&
                      workspace.Status == WorkspaceStatus.Active && workspace.DeletedAt == null &&
                      member.Status == MembershipStatus.Active &&
                      user.Status == UserStatus.Active && user.DeletedAt == null &&
                      tenantUser.Status == TenantUserStatus.Active
                select new TaskDeadlineDigestCurrentContext(
                    job.TenantId,
                    job.WorkspaceId,
                    job.UserId,
                    workspace.TimeZone,
                    tenantSettings == null ? null : tenantSettings.TimeZone,
                    member.TaskDeadlineDigestLocalTime ?? workspace.DefaultTaskDeadlineDigestLocalTime))
                .SingleOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (TaskDeadlineDigestPersistenceConflictClassifier.IsRetryable(exception))
        {
            throw new TaskDeadlineDigestRetryablePersistenceConflictException();
        }
    }

    public async Task<IReadOnlyList<TaskDeadlineDigestCandidate>> ListCurrentCandidatesAsync(
        Guid jobId,
        Guid claimToken,
        DateTimeOffset deadlineBeforeUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (safePage, safePageSize) = BoundPage(page, pageSize);
            return await CurrentCandidatesQuery(jobId, claimToken, deadlineBeforeUtc)
                .Skip(safePage * safePageSize)
                .Take(safePageSize)
                .ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (TaskDeadlineDigestPersistenceConflictClassifier.IsRetryable(exception))
        {
            throw new TaskDeadlineDigestRetryablePersistenceConflictException();
        }
    }

    /// <summary>
    /// Acquires a deterministic PostgreSQL authorization, lifecycle, and
    /// candidate current-state fence after a bounded candidate page has been
    /// evaluated. Generation has already acquired the claimed Job/Attempt
    /// fence when it enters this method. This fence locks every remaining row
    /// whose mutation could make the evaluated page unauthorized or stale,
    /// then rechecks the exact predicate while those locks are held. A caller
    /// retries in a new transaction on CurrentStateChanged.
    /// </summary>
    public async Task<TaskDeadlineDigestGenerationFenceOutcome> AcquireGenerationFenceAsync(
        TaskDeadlineDigestClaim claim,
        TaskDeadlineDigestCurrentContext? evaluatedContext,
        IReadOnlyCollection<TaskDeadlineDigestCandidate> evaluatedCandidates,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!currentTenant.IsAvailable || currentTenant.TenantId != claim.TenantId)
                return TaskDeadlineDigestGenerationFenceOutcome.ClaimLost;

            if (!dbContext.Database.IsNpgsql())
            {
                // The non-relational fallback is exercised by service tests.
                // Its single-context execution has no provider row-locking
                // primitive; the production PostgreSQL path below is the
                // authoritative commit fence.
                return TaskDeadlineDigestGenerationFenceOutcome.Current;
            }

            // The generation transaction has already locked Digest Job then
            // claimed Attempt. The remaining fixed order is Tenant,
            // TenantSettings, active Subscription, Plan, recipient User,
            // TenantUser, Workspace, WorkspaceMember, Project, Group,
            // ProjectMember, GroupMember, Task, WorkflowStage, and
            // Watch/Collaborator.
            //
            // Authorization/lifecycle rows use FOR SHARE: independent Digest
            // readers coexist, while PostgreSQL UPDATE/DELETE (including the
            // usual FOR NO KEY UPDATE taken for non-key updates) waits through
            // commit. The recipient User remains FOR UPDATE so only that
            // recipient's NotificationUserState critical section serializes.
            // Job/Attempt ownership remains exclusively fenced by the
            // transaction-start AcquireGenerationClaimFenceAsync call.
            await LockRowsAsync($"""
                SELECT 1 FROM tenants
                WHERE "Id" = {claim.TenantId}
                ORDER BY "Id"
                FOR SHARE
                """, cancellationToken);
            await LockRowsAsync($"""
                SELECT 1 FROM tenant_settings
                WHERE "TenantId" = {claim.TenantId}
                ORDER BY "Id"
                FOR SHARE
                """, cancellationToken);

            // FeatureFlags derive tasks.notificationsV1 from every active
            // Subscription, its Plan, and TenantSettings. Lock all active
            // subscriptions (rather than only the current winner) before
            // resolving Plan IDs so an equal StartedAt tie cannot leave an
            // unprotected source row.
            await LockRowsAsync($"""
                SELECT 1 FROM subscriptions
                WHERE "TenantId" = {claim.TenantId}
                  AND "Status" IN ('Trial', 'Active')
                ORDER BY "Id"
                FOR SHARE
                """, cancellationToken);
            var featurePlanIds = await dbContext.Subscriptions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(subscription => subscription.TenantId == claim.TenantId &&
                    (subscription.Status == SubscriptionStatus.Trial ||
                     subscription.Status == SubscriptionStatus.Active))
                .Select(subscription => subscription.PlanId)
                .Distinct()
                .OrderBy(planId => planId)
                .ToArrayAsync(cancellationToken);
            if (featurePlanIds.Length > 0)
            {
                await LockRowsAsync($"""
                    SELECT 1 FROM plans
                    WHERE "Id" = ANY({featurePlanIds})
                    ORDER BY "Id"
                    FOR SHARE
                    """, cancellationToken);
            }
            await LockRowsAsync($"""
                SELECT 1 FROM users
                WHERE "Id" = {claim.UserId}
                ORDER BY "Id"
                FOR UPDATE
                """, cancellationToken);
            await LockRowsAsync($"""
                SELECT 1 FROM tenant_users
                WHERE "TenantId" = {claim.TenantId}
                  AND "UserId" = {claim.UserId}
                ORDER BY "Id"
                FOR SHARE
                """, cancellationToken);
            await LockRowsAsync($"""
                SELECT 1 FROM workspaces
                WHERE "Id" = {claim.WorkspaceId}
                  AND "TenantId" = {claim.TenantId}
                ORDER BY "Id"
                FOR SHARE
                """, cancellationToken);
            await LockRowsAsync($"""
                SELECT 1 FROM workspace_members
                WHERE "WorkspaceId" = {claim.WorkspaceId}
                  AND "UserId" = {claim.UserId}
                ORDER BY "Id"
                FOR SHARE
                """, cancellationToken);

            var currentContext = await GetCurrentContextAsync(
                claim.JobId,
                claim.ClaimToken,
                cancellationToken);
            if (currentContext != evaluatedContext)
                return TaskDeadlineDigestGenerationFenceOutcome.CurrentStateChanged;

            var candidateIds = evaluatedCandidates
                .Select(candidate => candidate.TaskId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

            if (candidateIds.Length > 0)
            {
                // Capture parent identities before locking children. If a Task
                // moved concurrently, its post-task-lock identity is compared
                // with the evaluated page below and the entire transaction is
                // retried before it can stage a Notification.
                var expectedProjects = evaluatedCandidates
                    .Where(candidate => candidate.ProjectId != Guid.Empty)
                    .Select(candidate => candidate.ProjectId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();

                if (expectedProjects.Length > 0)
                {
                    await LockRowsAsync($"""
                        SELECT 1 FROM projects
                        WHERE "Id" = ANY({expectedProjects})
                        ORDER BY "Id"
                        FOR SHARE
                        """, cancellationToken);
                }

                Guid[] groupIds;
                if (expectedProjects.Length == 0)
                {
                    groupIds = [];
                }
                else
                {
                    groupIds = await dbContext.Projects
                        .AsNoTracking()
                        .Where(project => Enumerable.Contains(expectedProjects, project.Id) && project.GroupId.HasValue)
                        .OrderBy(project => project.GroupId)
                        .Select(project => project.GroupId!.Value)
                        .Distinct()
                        .ToArrayAsync(cancellationToken);
                }
                if (groupIds.Length > 0)
                {
                    await LockRowsAsync($"""
                        SELECT 1 FROM groups
                        WHERE "Id" = ANY({groupIds})
                        ORDER BY "Id"
                        FOR SHARE
                        """, cancellationToken);
                }

                if (expectedProjects.Length > 0)
                {
                    await LockRowsAsync($"""
                        SELECT 1 FROM project_members
                        WHERE "ProjectId" = ANY({expectedProjects})
                          AND "UserId" = {claim.UserId}
                        ORDER BY "Id"
                        FOR SHARE
                        """, cancellationToken);
                }
                if (groupIds.Length > 0)
                {
                    await LockRowsAsync($"""
                        SELECT 1 FROM group_members
                        WHERE "GroupId" = ANY({groupIds})
                          AND "UserId" = {claim.UserId}
                        ORDER BY "Id"
                        FOR SHARE
                        """, cancellationToken);
                }

                await LockRowsAsync($"""
                    SELECT 1 FROM task_items
                    WHERE "Id" = ANY({candidateIds})
                    ORDER BY "Id"
                    FOR SHARE
                    """, cancellationToken);

                var expectedStages = evaluatedCandidates
                    .Where(candidate => candidate.WorkflowStageId.HasValue)
                    .Select(candidate => candidate.WorkflowStageId!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                if (expectedStages.Length > 0)
                {
                    await LockRowsAsync($"""
                        SELECT 1 FROM task_workflow_stages
                        WHERE "Id" = ANY({expectedStages})
                        ORDER BY "Id"
                        FOR SHARE
                        """, cancellationToken);
                }

                await LockRowsAsync($"""
                    SELECT 1 FROM work_item_watch_states
                    WHERE "TaskItemId" = ANY({candidateIds})
                      AND "UserId" = {claim.UserId}
                    ORDER BY "Id"
                    FOR SHARE
                    """, cancellationToken);
                await LockRowsAsync($"""
                    SELECT 1 FROM task_item_collaborators
                    WHERE "TaskItemId" = ANY({candidateIds})
                      AND "UserId" = {claim.UserId}
                    ORDER BY "Id"
                    FOR SHARE
                    """, cancellationToken);
            }

            if (candidateIds.Length == 0)
                return TaskDeadlineDigestGenerationFenceOutcome.Current;

            var currentCandidates = (await CurrentCandidatesQuery(
                    claim.JobId,
                    claim.ClaimToken,
                    ResolveFenceDeadlineBeforeUtc(),
                    candidateIds)
                .ToListAsync(cancellationToken))
                .OrderBy(candidate => candidate.TaskId)
                .ToArray();
            var expectedCandidates = evaluatedCandidates
                .OrderBy(candidate => candidate.TaskId)
                .ToArray();
            if (currentCandidates.Length != expectedCandidates.Length)
                return TaskDeadlineDigestGenerationFenceOutcome.CurrentStateChanged;

            for (var index = 0; index < expectedCandidates.Length; index++)
            {
                var expected = expectedCandidates[index];
                var current = currentCandidates[index];
                if (current.TaskId != expected.TaskId ||
                    current.DeadlineAt != expected.DeadlineAt ||
                    current.ProjectId != expected.ProjectId ||
                    current.WorkflowStageId != expected.WorkflowStageId)
                {
                    return TaskDeadlineDigestGenerationFenceOutcome.CurrentStateChanged;
                }
            }

            return TaskDeadlineDigestGenerationFenceOutcome.Current;
        }
        catch (Exception exception) when (TaskDeadlineDigestPersistenceConflictClassifier.IsRetryable(exception))
        {
            throw new TaskDeadlineDigestRetryablePersistenceConflictException();
        }
    }

    private IQueryable<TaskDeadlineDigestCandidate> CurrentCandidatesQuery(
        Guid jobId,
        Guid claimToken,
        DateTimeOffset deadlineBeforeUtc,
        Guid[]? onlyTaskIds = null)
    {
        var eligible =
            from job in dbContext.TaskDeadlineDigestJobs.AsNoTracking()
            from task in dbContext.TaskItems.AsNoTracking()
            where job.Id == jobId &&
                  job.ClaimToken == claimToken &&
                  job.Status == TaskDeadlineDigestJobStatus.Claimed &&
                  task.WorkspaceId == job.WorkspaceId &&
                  task.DeadlineAt.HasValue && task.DeadlineAt.Value < deadlineBeforeUtc &&
                  task.DeletedAt == null && task.CompletedAt == null && task.CancelledAt == null &&
                  task.Status != TaskItemStatus.Completed && task.Status != TaskItemStatus.Cancelled &&
                  (task.WorkflowStage == null ||
                   (task.WorkflowStage.InternalCategory != TaskStageCategory.Done &&
                    task.WorkflowStage.InternalCategory != TaskStageCategory.Cancelled)) &&
                  task.Project != null && task.Project.DeletedAt == null &&
                  task.Project.Status != ProjectStatus.Archived &&
                  task.Project.Status != ProjectStatus.Deleted &&
                  task.Project.WorkspaceId == job.WorkspaceId &&
                  task.Project.Workspace != null &&
                  task.Project.Workspace.Status == WorkspaceStatus.Active &&
                  task.Project.Workspace.DeletedAt == null &&
                  dbContext.Users.Any(user => user.Id == job.UserId &&
                                              user.Status == UserStatus.Active &&
                                              user.DeletedAt == null) &&
                  dbContext.TenantUsers.Any(tenantUser =>
                      tenantUser.TenantId == job.TenantId &&
                      tenantUser.UserId == job.UserId &&
                      tenantUser.Status == TenantUserStatus.Active) &&
                  dbContext.WorkspaceMembers.Any(member =>
                      member.WorkspaceId == job.WorkspaceId &&
                      member.UserId == job.UserId &&
                      member.Status == MembershipStatus.Active) &&
                  (dbContext.ProjectMembers.Any(member =>
                       member.ProjectId == task.ProjectId && member.UserId == job.UserId) ||
                   ((task.Project.Status != ProjectStatus.Planning && task.Project.Status != ProjectStatus.Suspended) &&
                    (!task.Project.GroupId.HasValue ||
                     dbContext.GroupMembers.Any(member =>
                         member.GroupId == task.Project.GroupId && member.UserId == job.UserId) ||
                     dbContext.WorkspaceMembers.Any(member =>
                         member.WorkspaceId == job.WorkspaceId &&
                         member.UserId == job.UserId &&
                         member.Status == MembershipStatus.Active &&
                         (member.Role == WorkspaceRole.Owner ||
                          member.Role == WorkspaceRole.Admin)) ||
                     dbContext.Users.Any(user =>
                         user.Id == job.UserId &&
                         user.Status == UserStatus.Active &&
                         user.DeletedAt == null &&
                         user.SystemRole == SystemRole.SystemAdmin)))) &&
                  (dbContext.WorkItemWatchStates.Any(watch =>
                       watch.TaskItemId == task.Id &&
                       watch.UserId == job.UserId &&
                       watch.IsManualWatch) ||
                   (!dbContext.WorkItemWatchStates.Any(watch =>
                        watch.TaskItemId == task.Id &&
                        watch.UserId == job.UserId &&
                        watch.IsExplicitOptOut) &&
                    (task.CreatedByUserId == job.UserId ||
                     task.PrimaryAssigneeUserId == job.UserId ||
                     task.ReviewerUserId == job.UserId ||
                     dbContext.WorkItemCollaborators.Any(collaborator =>
                         collaborator.TaskItemId == task.Id && collaborator.UserId == job.UserId))))
            select new
            {
                TaskId = task.Id,
                DeadlineAt = task.DeadlineAt!.Value,
                task.ProjectId,
                task.WorkflowStageId
            };

        if (onlyTaskIds is { Length: > 0 })
            eligible = eligible.Where(candidate => Enumerable.Contains(onlyTaskIds, candidate.TaskId));

        return eligible
            .OrderBy(candidate => candidate.DeadlineAt)
            .ThenBy(candidate => candidate.TaskId)
            .Select(candidate => new TaskDeadlineDigestCandidate(
                candidate.TaskId,
                candidate.DeadlineAt,
                candidate.ProjectId,
                candidate.WorkflowStageId));
    }

    public async Task<ITaskDeadlineDigestTransaction> BeginGenerationTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!dbContext.Database.IsRelational())
                return NoopTaskDeadlineDigestTransaction.Instance;

            var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            return new EfTaskDeadlineDigestTransaction(transaction);
        }
        catch (Exception exception) when (TaskDeadlineDigestPersistenceConflictClassifier.IsRetryable(exception))
        {
            throw new TaskDeadlineDigestRetryablePersistenceConflictException();
        }
    }

    public async Task<bool> MarkSucceededAsync(
        Guid jobId,
        Guid claimToken,
        Guid? notificationId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadClaimStateAsync(jobId, claimToken, cancellationToken);
        if (state is null)
            return false;

        state.Value.Job.Status = TaskDeadlineDigestJobStatus.Succeeded;
        state.Value.Job.NotificationId = notificationId;
        state.Value.Job.CompletedAt = completedAt;
        state.Value.Job.NextAttemptAt = null;
        state.Value.Job.LastErrorCode = null;
        ClearJobClaim(state.Value.Job);

        state.Value.Attempt.Status = TaskDeadlineDigestAttemptStatus.Succeeded;
        state.Value.Attempt.CompletedAt = completedAt;
        state.Value.Attempt.LastErrorCode = null;
        ClearAttemptClaim(state.Value.Attempt);
        return true;
    }

    public async Task<bool> DeferAsync(
        Guid jobId,
        Guid claimToken,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset deferredAt,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadClaimStateAsync(jobId, claimToken, cancellationToken);
        if (state is null)
            return false;

        state.Value.Job.Status = TaskDeadlineDigestJobStatus.Pending;
        state.Value.Job.ScheduledForUtc = scheduledForUtc;
        state.Value.Job.NextAttemptAt = scheduledForUtc;
        state.Value.Job.LastErrorCode = null;
        state.Value.Job.AttemptCount = Math.Max(0, state.Value.Job.AttemptCount - 1);
        if (state.Value.Attempt.Trigger == TaskDeadlineDigestAttemptTrigger.Automatic)
            state.Value.Job.AutomaticAttemptCount = Math.Max(0, state.Value.Job.AutomaticAttemptCount - 1);
        ClearJobClaim(state.Value.Job);

        // A not-yet-due operator restart remains the same audited restart
        // record. Consuming it as Deferred would strand the job after the
        // automatic-attempt budget is already exhausted. Automatic claims may
        // close as Deferred and receive a fresh automatic attempt when due.
        var preserveOperatorRestart =
            state.Value.Attempt.Trigger == TaskDeadlineDigestAttemptTrigger.OperatorRestart;
        state.Value.Attempt.Status = preserveOperatorRestart
            ? TaskDeadlineDigestAttemptStatus.Pending
            : TaskDeadlineDigestAttemptStatus.Deferred;
        state.Value.Attempt.CompletedAt = preserveOperatorRestart ? null : deferredAt;
        state.Value.Attempt.LastErrorCode = null;
        ClearAttemptClaim(state.Value.Attempt);
        await SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReleaseFeatureDisabledClaimAsync(
        Guid jobId,
        Guid claimToken,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ownsTransaction = dbContext.Database.IsRelational() &&
                                  dbContext.Database.CurrentTransaction is null;
            await using var transaction = ownsTransaction
                ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            // The job/attempt token fence is acquired before modifying either
            // row. A prior release, success, defer, or failure makes this a
            // safe ClaimLost result rather than consuming another attempt.
            if (await GetClaimedAsync(jobId, claimToken, forUpdate: true, cancellationToken) is null)
                return false;

            var state = await LoadClaimStateAsync(jobId, claimToken, cancellationToken);
            if (state is null)
                return false;

            state.Value.Job.Status = TaskDeadlineDigestJobStatus.Pending;
            state.Value.Job.CompletedAt = null;
            state.Value.Job.NextAttemptAt = releasedAt;
            state.Value.Job.LastErrorCode = null;
            state.Value.Job.AttemptCount = Math.Max(0, state.Value.Job.AttemptCount - 1);
            if (state.Value.Attempt.Trigger == TaskDeadlineDigestAttemptTrigger.Automatic)
            {
                state.Value.Job.AutomaticAttemptCount = Math.Max(
                    0,
                    state.Value.Job.AutomaticAttemptCount - 1);
                state.Value.Attempt.Status = TaskDeadlineDigestAttemptStatus.Deferred;
                state.Value.Attempt.CompletedAt = releasedAt;
            }
            else
            {
                // Preserve the one audited operator restart row for retry; do
                // not append another restart and do not alter automatic budget.
                state.Value.Attempt.Status = TaskDeadlineDigestAttemptStatus.Pending;
                state.Value.Attempt.CompletedAt = null;
            }

            state.Value.Attempt.LastErrorCode = null;
            ClearJobClaim(state.Value.Job);
            ClearAttemptClaim(state.Value.Attempt);

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (TaskDeadlineDigestPersistenceConflictClassifier.IsRetryable(exception))
        {
            throw new TaskDeadlineDigestRetryablePersistenceConflictException();
        }
    }

    public async Task<TaskDeadlineDigestTransition> MarkFailureAsync(
        Guid jobId,
        Guid claimToken,
        string errorCode,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadClaimStateAsync(jobId, claimToken, cancellationToken);
        if (state is null)
            return new TaskDeadlineDigestTransition(false, false);

        var safeCode = BoundRequired(errorCode, 100, nameof(errorCode));
        state.Value.Attempt.Status = TaskDeadlineDigestAttemptStatus.Failed;
        state.Value.Attempt.CompletedAt = failedAt;
        state.Value.Attempt.LastErrorCode = safeCode;
        ClearAttemptClaim(state.Value.Attempt);

        var terminal = state.Value.Attempt.Trigger == TaskDeadlineDigestAttemptTrigger.OperatorRestart ||
                       state.Value.Job.AutomaticAttemptCount >= TaskDeadlineDigestPolicy.MaximumAutomaticAttempts;
        state.Value.Job.Status = terminal
            ? TaskDeadlineDigestJobStatus.Failed
            : TaskDeadlineDigestJobStatus.Pending;
        state.Value.Job.CompletedAt = terminal ? failedAt : null;
        state.Value.Job.NextAttemptAt = terminal ? null : nextAttemptAt;
        state.Value.Job.LastErrorCode = safeCode;
        ClearJobClaim(state.Value.Job);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new TaskDeadlineDigestTransition(true, terminal);
    }

    public async Task<TaskDeadlineDigestRestartOutcome> RestartFailedAsync(
        Guid jobId,
        Guid actorUserId,
        string reason,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAvailable)
            throw new InvalidOperationException("A tenant scope is required to restart a Task deadline digest.");

        var safeReason = BoundRequired(reason, 500, nameof(reason));
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        TaskDeadlineDigestJob? job;
        if (dbContext.Database.IsNpgsql())
        {
            job = await dbContext.TaskDeadlineDigestJobs
                .FromSqlInterpolated($$"""
                    SELECT * FROM task_deadline_digest_jobs
                    WHERE "Id" = {{jobId}}
                      AND "TenantId" = {{currentTenant.TenantId}}
                    FOR UPDATE
                    """)
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            job = await dbContext.TaskDeadlineDigestJobs.SingleOrDefaultAsync(
                candidate => candidate.Id == jobId,
                cancellationToken);
        }

        if (job is null)
            return TaskDeadlineDigestRestartOutcome.NotFound;
        if (job.Status != TaskDeadlineDigestJobStatus.Failed)
            return TaskDeadlineDigestRestartOutcome.NotFailed;

        var activeAttempt = await dbContext.TaskDeadlineDigestAttempts.AnyAsync(
            attempt => attempt.JobId == job.Id &&
                       (attempt.Status == TaskDeadlineDigestAttemptStatus.Pending ||
                        attempt.Status == TaskDeadlineDigestAttemptStatus.Claimed),
            cancellationToken);
        if (activeAttempt)
            return TaskDeadlineDigestRestartOutcome.ActiveAttemptExists;

        var previousAttemptId = await dbContext.TaskDeadlineDigestAttempts
            .Where(attempt => attempt.JobId == job.Id)
            .OrderByDescending(attempt => attempt.AttemptNumber)
            .Select(attempt => (Guid?)attempt.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!previousAttemptId.HasValue)
            return TaskDeadlineDigestRestartOutcome.NotFailed;

        job.AttemptSequence++;
        await dbContext.TaskDeadlineDigestAttempts.AddAsync(new TaskDeadlineDigestAttempt
        {
            TenantId = job.TenantId,
            JobId = job.Id,
            AttemptNumber = job.AttemptSequence,
            Trigger = TaskDeadlineDigestAttemptTrigger.OperatorRestart,
            Status = TaskDeadlineDigestAttemptStatus.Pending,
            RestartedFromAttemptId = previousAttemptId,
            RequestedByUserId = actorUserId
        }, cancellationToken);

        job.Status = TaskDeadlineDigestJobStatus.Pending;
        job.NextAttemptAt = requestedAt;
        job.CompletedAt = null;
        ClearJobClaim(job);
        await dbContext.AuditLogs.AddAsync(new AuditLog
        {
            TenantId = job.TenantId,
            ActorUserId = actorUserId,
            Action = "TaskDeadlineDigestRestarted",
            EntityType = nameof(TaskDeadlineDigestJob),
            EntityId = job.Id,
            WorkspaceId = job.WorkspaceId,
            Summary = "A failed Task deadline digest generation was restarted by an operator.",
            MetadataJson = JsonSerializer.Serialize(new
            {
                reason = safeReason,
                attemptNumber = job.AttemptSequence,
                automaticAttemptCount = job.AutomaticAttemptCount
            }),
            CreatedAt = requestedAt
        }, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return TaskDeadlineDigestRestartOutcome.Restarted;
    }

    public async Task<TaskDeadlineDigestStoreDiagnostics> GetDiagnosticsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var due = await dbContext.TaskDeadlineDigestJobs.LongCountAsync(
            job => job.Status == TaskDeadlineDigestJobStatus.Pending && job.NextAttemptAt <= now,
            cancellationToken);
        var claimed = await dbContext.TaskDeadlineDigestJobs.LongCountAsync(
            job => job.Status == TaskDeadlineDigestJobStatus.Claimed,
            cancellationToken);
        var succeeded = await dbContext.TaskDeadlineDigestJobs.LongCountAsync(
            job => job.Status == TaskDeadlineDigestJobStatus.Succeeded,
            cancellationToken);
        var failed = await dbContext.TaskDeadlineDigestJobs.LongCountAsync(
            job => job.Status == TaskDeadlineDigestJobStatus.Failed,
            cancellationToken);
        var oldestDue = await dbContext.TaskDeadlineDigestJobs
            .Where(job => job.Status == TaskDeadlineDigestJobStatus.Pending && job.NextAttemptAt <= now)
            .OrderBy(job => job.NextAttemptAt)
            .Select(job => job.NextAttemptAt)
            .FirstOrDefaultAsync(cancellationToken);
        var oldestClaimed = await dbContext.TaskDeadlineDigestJobs
            .Where(job => job.Status == TaskDeadlineDigestJobStatus.Claimed)
            .OrderBy(job => job.ClaimedAt)
            .Select(job => job.ClaimedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return new TaskDeadlineDigestStoreDiagnostics(due, claimed, succeeded, failed, oldestDue, oldestClaimed);
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (TaskDeadlineDigestPersistenceConflictClassifier.IsRetryable(exception))
        {
            throw new TaskDeadlineDigestRetryablePersistenceConflictException();
        }
    }

    public void ResetGenerationState() => dbContext.ChangeTracker.Clear();

    private async Task<List<TaskDeadlineDigestJob>> SelectStaleClaimsAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            return await dbContext.TaskDeadlineDigestJobs
                .FromSqlInterpolated($$"""
                    SELECT * FROM task_deadline_digest_jobs
                    WHERE "Status" = 'Claimed'
                      AND "TenantId" = {{currentTenant.TenantId}}
                      AND "ClaimExpiresAt" <= {{now}}
                    ORDER BY "ClaimExpiresAt", "CreatedAt", "Id"
                    LIMIT {{batchSize}}
                    FOR UPDATE SKIP LOCKED
                    """)
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken);
        }

        return await dbContext.TaskDeadlineDigestJobs
            .Where(job => job.Status == TaskDeadlineDigestJobStatus.Claimed && job.ClaimExpiresAt <= now)
            .OrderBy(job => job.ClaimExpiresAt)
            .ThenBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<TaskDeadlineDigestJob>> SelectDueJobsAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            return await dbContext.TaskDeadlineDigestJobs
                .FromSqlInterpolated($$"""
                    SELECT * FROM task_deadline_digest_jobs
                    WHERE "Status" = 'Pending'
                      AND "TenantId" = {{currentTenant.TenantId}}
                      AND "NextAttemptAt" <= {{now}}
                    ORDER BY "NextAttemptAt", "CreatedAt", "Id"
                    LIMIT {{batchSize}}
                    FOR UPDATE SKIP LOCKED
                    """)
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken);
        }

        return await dbContext.TaskDeadlineDigestJobs
            .Where(job => job.Status == TaskDeadlineDigestJobStatus.Pending && job.NextAttemptAt <= now)
            .OrderBy(job => job.NextAttemptAt)
            .ThenBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<(TaskDeadlineDigestJob Job, TaskDeadlineDigestAttempt Attempt)?> LoadClaimStateAsync(
        Guid jobId,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.TaskDeadlineDigestJobs.SingleOrDefaultAsync(
            candidate => candidate.Id == jobId &&
                         candidate.Status == TaskDeadlineDigestJobStatus.Claimed &&
                         candidate.ClaimToken == claimToken,
            cancellationToken);
        if (job is null)
            return null;

        var attempt = await dbContext.TaskDeadlineDigestAttempts.SingleOrDefaultAsync(
            candidate => candidate.JobId == jobId &&
                         candidate.Status == TaskDeadlineDigestAttemptStatus.Claimed &&
                         candidate.ClaimToken == claimToken,
            cancellationToken);
        return attempt is null ? null : (job, attempt);
    }

    private async Task LockRowsAsync(
        FormattableString command,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(command, cancellationToken);
    }

    private static DateTimeOffset ResolveFenceDeadlineBeforeUtc()
    {
        // The validation is already narrowed by the evaluated task IDs. Keep
        // the deadline predicate broad so a concurrent deadline move outside
        // the original page horizon is observed as a value mismatch rather
        // than being silently omitted from the fence recheck.
        return DateTimeOffset.MaxValue;
    }

    private static bool HasSameClaimIdentity(
        TaskDeadlineDigestClaim expected,
        TaskDeadlineDigestClaim actual) =>
        actual.JobId == expected.JobId &&
        actual.TenantId == expected.TenantId &&
        actual.WorkspaceId == expected.WorkspaceId &&
        actual.UserId == expected.UserId &&
        actual.LocalDate == expected.LocalDate &&
        actual.PolicyVersion == expected.PolicyVersion &&
        actual.ClaimToken == expected.ClaimToken &&
        actual.Trigger == expected.Trigger;

    private static void ClearJobClaim(TaskDeadlineDigestJob job)
    {
        job.ClaimOwner = null;
        job.ClaimToken = null;
        job.ClaimedAt = null;
        job.ClaimExpiresAt = null;
    }

    private static void ClearAttemptClaim(TaskDeadlineDigestAttempt attempt)
    {
        attempt.ClaimOwner = null;
        attempt.ClaimToken = null;
        attempt.ClaimedAt = null;
        attempt.ClaimExpiresAt = null;
    }

    private static (int Page, int PageSize) BoundPage(int page, int pageSize) =>
        (Math.Max(0, page), Math.Clamp(pageSize, 1, MaximumPageSize));

    private static string BoundRequired(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty bounded value is required.", parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }

    private sealed class EfTaskDeadlineDigestTransaction(IDbContextTransaction transaction) : ITaskDeadlineDigestTransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception) when (TaskDeadlineDigestPersistenceConflictClassifier.IsRetryable(exception))
            {
                throw new TaskDeadlineDigestRetryablePersistenceConflictException();
            }
        }

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private sealed class NoopTaskDeadlineDigestTransaction : ITaskDeadlineDigestTransaction
    {
        public static NoopTaskDeadlineDigestTransaction Instance { get; } = new();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Provider-specific retry classification is intentionally contained in
/// Infrastructure. Application code receives only the safe marker exception.
/// </summary>
internal static class TaskDeadlineDigestPersistenceConflictClassifier
{
    public static bool IsRetryable(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException)
            return true;

        if (exception is PostgresException postgres)
        {
            return postgres.SqlState is PostgresErrorCodes.SerializationFailure or
                PostgresErrorCodes.DeadlockDetected;
        }

        return exception.InnerException is not null && IsRetryable(exception.InnerException);
    }
}
