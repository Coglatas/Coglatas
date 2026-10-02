using System.Data.Common;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Notifications;
using Coglatas.Application.Realtime;
using Coglatas.Application.TenantAdministration;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Coglatas.Tests.PostgreSql;

[Trait("Category", "PostgreSQLIntegration")]
[Trait("Scope", "TaskV1PR07C")]
public sealed class TaskV1Pr07CDigestCandidateAtomicityPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly LocalDate = new(2026, 8, 3);

    [PostgreSqlFact]
    public async Task CandidateQueryUsesCurrentEffectiveWatchAndExcludesVisibilityOrTeamQueueAlone()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);

            await using (var db = CreateTenantContext(database, graph.Tenant))
            {
                var creator = NewTask(graph, "creator", 1);
                creator.CreatedByUserId = graph.Recipient.Id;
                var assignee = NewTask(graph, "primary assignee", 2);
                assignee.PrimaryAssigneeUserId = graph.Recipient.Id;
                var reviewer = NewTask(graph, "reviewer", 3);
                reviewer.ReviewerUserId = graph.Recipient.Id;
                var collaborator = NewTask(graph, "collaborator", 4);
                var manualWatch = NewTask(graph, "manual watch", 5);
                var optedOutCreator = NewTask(graph, "opted-out creator", 6);
                optedOutCreator.CreatedByUserId = graph.Recipient.Id;
                var visibilityOnly = NewTask(graph, "visibility only", 7);
                var teamQueueOnly = NewTask(graph, "team queue only", 8);
                teamQueueOnly.TargetGroupId = graph.Team.Id;

                db.AddRange(
                    creator,
                    assignee,
                    reviewer,
                    collaborator,
                    manualWatch,
                    optedOutCreator,
                    visibilityOnly,
                    teamQueueOnly,
                    new WorkItemCollaborator
                    {
                        TenantId = graph.Tenant.Id,
                        TaskItemId = collaborator.Id,
                        UserId = graph.Recipient.Id,
                        AddedAt = Now,
                        AddedByUserId = graph.Actor.Id
                    },
                    new WorkItemWatchState
                    {
                        TenantId = graph.Tenant.Id,
                        TaskItemId = manualWatch.Id,
                        UserId = graph.Recipient.Id,
                        IsManualWatch = true,
                        IsWatching = true,
                        UpdatedAt = Now
                    },
                    new WorkItemWatchState
                    {
                        TenantId = graph.Tenant.Id,
                        TaskItemId = optedOutCreator.Id,
                        UserId = graph.Recipient.Id,
                        AutomaticSources = WorkItemWatchAutomaticSource.Creator,
                        IsExplicitOptOut = true,
                        IsWatching = false,
                        UpdatedAt = Now
                    });
                await db.SaveChangesAsync();

                await AddAndClaimJobThenAssertCandidatesAsync(
                    database,
                    graph,
                    [creator.Id, assignee.Id, reviewer.Id, collaborator.Id, manualWatch.Id],
                    [optedOutCreator.Id, visibilityOnly.Id, teamQueueOnly.Id]);
            }
        });
    }

    [PostgreSqlFact]
    public async Task CandidateQueryUsesCanonicalGroupRestrictedVisibilityAndFailsClosedForSuspendedWithoutExplicitMembership()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            var suffix = Guid.NewGuid().ToString("N");
            var adviser = UserFor("adviser-only", suffix);
            var systemAdmin = UserFor("system-admin", suffix);
            systemAdmin.SystemRole = SystemRole.SystemAdmin;
            var ownerFieldOnly = UserFor("owner-field-only", suffix);

            await using (var platform = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database))
            {
                platform.AddRange(adviser, systemAdmin, ownerFieldOnly);
                await platform.SaveChangesAsync();
            }

            TaskItem adviserTask;
            TaskItem systemAdminTask;
            TaskItem ownerFieldTask;
            TaskItem completedProjectTask;
            TaskItem suspendedProjectTask;
            await using (var db = CreateTenantContext(database, graph.Tenant))
            {
                var restricted = NewProject(graph, "group-restricted");
                restricted.GroupId = graph.Team.Id;
                restricted.OwnerUserId = ownerFieldOnly.Id;
                var completed = NewProject(graph, "completed-visible");
                completed.GroupId = graph.Team.Id;
                completed.Status = ProjectStatus.Completed;
                var suspended = NewProject(graph, "suspended-visible");
                suspended.GroupId = graph.Team.Id;
                suspended.Status = ProjectStatus.Suspended;

                adviserTask = NewTask(graph, "adviser-created restricted", 1, restricted);
                adviserTask.CreatedByUserId = adviser.Id;
                systemAdminTask = NewTask(graph, "admin-created restricted", 2, restricted);
                systemAdminTask.CreatedByUserId = systemAdmin.Id;
                ownerFieldTask = NewTask(graph, "owner-field-created restricted", 3, restricted);
                ownerFieldTask.CreatedByUserId = ownerFieldOnly.Id;
                completedProjectTask = NewTask(graph, "completed project remains visible", 4, completed);
                completedProjectTask.CreatedByUserId = systemAdmin.Id;
                suspendedProjectTask = NewTask(graph, "suspended project remains visible", 5, suspended);
                suspendedProjectTask.CreatedByUserId = systemAdmin.Id;

                db.AddRange(
                    restricted,
                    completed,
                    suspended,
                    adviserTask,
                    systemAdminTask,
                    ownerFieldTask,
                    completedProjectTask,
                    suspendedProjectTask,
                    ActiveTenantUser(graph.Tenant, adviser),
                    ActiveTenantUser(graph.Tenant, systemAdmin),
                    ActiveTenantUser(graph.Tenant, ownerFieldOnly),
                    ActiveWorkspaceMember(graph, adviser, WorkspaceRole.Adviser),
                    ActiveWorkspaceMember(graph, systemAdmin, WorkspaceRole.Member),
                    ActiveWorkspaceMember(graph, ownerFieldOnly, WorkspaceRole.Member));
                await db.SaveChangesAsync();
            }

            _ = await AddJobAsync(database, graph.Tenant, graph.Workspace, adviser, LocalDate);
            _ = await AddJobAsync(database, graph.Tenant, graph.Workspace, systemAdmin, LocalDate);
            _ = await AddJobAsync(database, graph.Tenant, graph.Workspace, ownerFieldOnly, LocalDate);

            var currentTenant = TenantScope(graph.Tenant);
            await using var queryContext = CreateTenantContext(database, graph.Tenant, currentTenant);
            var repository = new TaskDeadlineDigestRepository(queryContext, currentTenant);
            var claims = await repository.ClaimDueAsync(
                "group-visibility-test",
                Now,
                batchSize: 3,
                claimTimeout: TimeSpan.FromMinutes(2));
            Assert.Equal(3, claims.Count);
            var claimsByUser = claims.ToDictionary(claim => claim.UserId);

            Assert.Empty(await CandidateIdsAsync(repository, claimsByUser[adviser.Id]));
            Assert.Empty(await CandidateIdsAsync(repository, claimsByUser[ownerFieldOnly.Id]));
            Assert.Equal(
                [systemAdminTask.Id, completedProjectTask.Id],
                await CandidateIdsAsync(repository, claimsByUser[systemAdmin.Id]));
            Assert.DoesNotContain(suspendedProjectTask.Id, await CandidateIdsAsync(repository, claimsByUser[systemAdmin.Id]));
            Assert.DoesNotContain(adviserTask.Id, await CandidateIdsAsync(repository, claimsByUser[systemAdmin.Id]));
            Assert.DoesNotContain(ownerFieldTask.Id, await CandidateIdsAsync(repository, claimsByUser[systemAdmin.Id]));
        });
    }

    [PostgreSqlFact]
    public async Task CandidateQueryRechecksMembershipWorkspaceProjectTaskLifecycleAndRelationship()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);

            TaskItem active;
            TaskItem relationshipLost;
            TaskItem deleted;
            TaskItem completed;
            TaskItem cancelled;
            TaskItem archivedProjectTask;
            await using (var db = CreateTenantContext(database, graph.Tenant))
            {
                active = NewTask(graph, "active", 1);
                active.PrimaryAssigneeUserId = graph.Recipient.Id;
                relationshipLost = NewTask(graph, "relationship can be lost", 2);
                relationshipLost.PrimaryAssigneeUserId = graph.Recipient.Id;
                deleted = NewTask(graph, "deleted", 3);
                deleted.CreatedByUserId = graph.Recipient.Id;
                deleted.MarkDeleted(Now, graph.Actor.Id, "test lifecycle exclusion");
                completed = NewTask(graph, "completed", 4);
                completed.CreatedByUserId = graph.Recipient.Id;
                completed.Status = TaskItemStatus.Completed;
                completed.CompletedAt = Now;
                cancelled = NewTask(graph, "cancelled", 5);
                cancelled.CreatedByUserId = graph.Recipient.Id;
                cancelled.Status = TaskItemStatus.Cancelled;
                cancelled.CancelledAt = Now;

                var archivedProject = NewProject(graph, "archived");
                archivedProject.Status = ProjectStatus.Archived;
                archivedProjectTask = NewTask(graph, "archived project", 6, archivedProject);
                archivedProjectTask.CreatedByUserId = graph.Recipient.Id;

                db.AddRange(
                    active,
                    relationshipLost,
                    deleted,
                    completed,
                    cancelled,
                    archivedProject,
                    new ProjectMember
                    {
                        TenantId = graph.Tenant.Id,
                        ProjectId = archivedProject.Id,
                        UserId = graph.Recipient.Id,
                        Role = ProjectRole.Contributor,
                        JoinedAt = Now
                    },
                    archivedProjectTask);
                await db.SaveChangesAsync();
            }

            await AddJobAsync(database, graph);
            var currentTenant = TenantScope(graph.Tenant);
            await using var queryContext = CreateTenantContext(database, graph.Tenant, currentTenant);
            var repository = new TaskDeadlineDigestRepository(queryContext, currentTenant);
            var claim = Assert.Single(await repository.ClaimDueAsync(
                "candidate-state-test",
                Now,
                batchSize: 1,
                claimTimeout: TimeSpan.FromMinutes(2)));

            Assert.Equal(
                [active.Id, relationshipLost.Id],
                await CandidateIdsAsync(repository, claim));
            Assert.DoesNotContain(deleted.Id, await CandidateIdsAsync(repository, claim));
            Assert.DoesNotContain(completed.Id, await CandidateIdsAsync(repository, claim));
            Assert.DoesNotContain(cancelled.Id, await CandidateIdsAsync(repository, claim));
            Assert.DoesNotContain(archivedProjectTask.Id, await CandidateIdsAsync(repository, claim));

            await using (var mutation = CreateTenantContext(database, graph.Tenant))
            {
                var task = await mutation.TaskItems.SingleAsync(item => item.Id == relationshipLost.Id);
                task.PrimaryAssigneeUserId = null;
                await mutation.SaveChangesAsync();
            }
            Assert.Equal([active.Id], await CandidateIdsAsync(repository, claim));

            await using (var mutation = CreateTenantContext(database, graph.Tenant))
            {
                var member = await mutation.WorkspaceMembers.SingleAsync(item =>
                    item.WorkspaceId == graph.Workspace.Id && item.UserId == graph.Recipient.Id);
                member.Status = MembershipStatus.Suspended;
                await mutation.SaveChangesAsync();
            }
            Assert.Empty(await CandidateIdsAsync(repository, claim));

            await using (var mutation = CreateTenantContext(database, graph.Tenant))
            {
                var member = await mutation.WorkspaceMembers.SingleAsync(item =>
                    item.WorkspaceId == graph.Workspace.Id && item.UserId == graph.Recipient.Id);
                member.Status = MembershipStatus.Active;
                await mutation.SaveChangesAsync();
            }
            Assert.Equal([active.Id], await CandidateIdsAsync(repository, claim));

            await using (var mutation = CreateTenantContext(database, graph.Tenant))
            {
                var workspace = await mutation.Workspaces.SingleAsync(item => item.Id == graph.Workspace.Id);
                workspace.Status = WorkspaceStatus.Archived;
                await mutation.SaveChangesAsync();
            }
            Assert.Empty(await CandidateIdsAsync(repository, claim));
        });
    }

    [PostgreSqlFact]
    public async Task GeneratorCommitsGenericNotificationSignalUserStateAndSucceededLedgerTogether()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddCategoryTasksAsync(database, graph);
            await AddJobAsync(database, graph);

            var result = await GenerateAsync(database, graph);
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
            Assert.Equal(1, result.Counts.ThreeDays);
            Assert.Equal(1, result.Counts.OneDay);
            Assert.Equal(1, result.Counts.Today);
            Assert.Equal(1, result.Counts.Overdue);
            Assert.Equal(4, result.Counts.Total);
            Assert.NotNull(result.NotificationId);

            await using var verification = CreateTenantContext(database, graph.Tenant);
            var job = await verification.TaskDeadlineDigestJobs.AsNoTracking().SingleAsync();
            Assert.Equal(TaskDeadlineDigestJobStatus.Succeeded, job.Status);
            Assert.Equal(result.NotificationId, job.NotificationId);
            Assert.Equal(Now, job.CompletedAt);
            Assert.Null(job.ClaimToken);
            var attempt = await verification.TaskDeadlineDigestAttempts.AsNoTracking().SingleAsync();
            Assert.Equal(TaskDeadlineDigestAttemptStatus.Succeeded, attempt.Status);
            Assert.Equal(Now, attempt.CompletedAt);

            var notification = await verification.Notifications.AsNoTracking().SingleAsync();
            Assert.Equal(result.NotificationId, notification.Id);
            Assert.Equal(graph.Recipient.Id, notification.UserId);
            Assert.Equal(NotificationType.TaskDueSoon, notification.NotificationType);
            Assert.Equal(TaskDeadlineDigestPolicy.NotificationTitle, notification.Title);
            Assert.Null(notification.Body);
            Assert.Equal(TaskDeadlineDigestPolicy.RelatedEntityType, notification.RelatedEntityType);
            Assert.Equal(job.Id, notification.RelatedEntityId);
            Assert.Equal(
                TaskDeadlineDigestPolicy.BuildNotificationLogicalKey(graph.Workspace.Id, LocalDate, TaskDeadlineDigestPolicy.PolicyVersion),
                notification.LogicalKey);

            var state = await verification.NotificationUserStates.AsNoTracking().SingleAsync();
            Assert.Equal(graph.Recipient.Id, state.UserId);
            Assert.Equal(1, state.Version);

            var outbox = await verification.OutboxEvents.AsNoTracking().SingleAsync();
            Assert.Equal("Notifications.NotificationCreated.v1", outbox.EventType);
            Assert.Equal("Notification", outbox.AggregateType);
            Assert.Equal(notification.Id, outbox.AggregateId);
            Assert.DoesNotContain("restricted digest task title", outbox.PayloadJson, StringComparison.Ordinal);
            using var envelope = JsonDocument.Parse(outbox.PayloadJson);
            var payload = envelope.RootElement.GetProperty("payload");
            Assert.Equal(notification.Id, payload.GetProperty("notificationId").GetGuid());
            Assert.Equal(notification.StateVersion, payload.GetProperty("stateVersion").GetInt64());
            Assert.True(payload.GetProperty("requiresRefetch").GetBoolean());
            Assert.Equal(3, payload.EnumerateObject().Count());
            Assert.Contains(graph.Recipient.Id.ToString(), outbox.RoutingJson, StringComparison.OrdinalIgnoreCase);
        });
    }

    [PostgreSqlFact]
    public async Task ConcurrentSameUserWorkspaceTimeZonesSerializeStateVersionsAndBothSucceed()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, graph);
            var utcJob = await AddJobAsync(database, graph);
            var pacific = await AddWorkspaceAsync(
                database,
                graph,
                timeZoneId: "America/Los_Angeles",
                digestLocalTime: new TimeOnly(21, 0));
            var pacificJob = await AddJobAsync(
                database,
                graph.Tenant,
                pacific.Workspace,
                graph.Recipient,
                new DateOnly(2026, 8, 2));

            var claims = new List<TaskDeadlineDigestClaim>(2);
            for (var worker = 0; worker < 2; worker++)
            {
                var claimTenant = TenantScope(graph.Tenant);
                await using var claimContext = CreateTenantContext(database, graph.Tenant, claimTenant);
                var claimRepository = new TaskDeadlineDigestRepository(claimContext, claimTenant);
                claims.Add(Assert.Single(await claimRepository.ClaimDueAsync(
                    $"timezone-worker-{worker}",
                    Now,
                    batchSize: 1,
                    claimTimeout: TimeSpan.FromMinutes(2))));
            }

            Assert.Equal(
                new[] { utcJob.Id, pacificJob.Id }.Order(),
                claims.Select(claim => claim.JobId).Order());
            var results = await Task.WhenAll(claims.Select(claim =>
                GenerateClaimAsync(database, graph.Tenant, claim)));

            Assert.All(results, result =>
            {
                Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
                Assert.Equal(1, result.Counts.Today);
                Assert.Equal(1, result.Counts.Total);
                Assert.NotNull(result.NotificationId);
            });
            Assert.Equal(2, results.Select(result => result.NotificationId).Distinct().Count());

            await using var verification = CreateTenantContext(database, graph.Tenant);
            var jobs = await verification.TaskDeadlineDigestJobs.AsNoTracking()
                .OrderBy(job => job.WorkspaceId)
                .ToListAsync();
            Assert.Equal(2, jobs.Count);
            Assert.All(jobs, job => Assert.Equal(TaskDeadlineDigestJobStatus.Succeeded, job.Status));
            Assert.Contains(jobs, job => job.WorkspaceId == graph.Workspace.Id && job.LocalDate == LocalDate);
            Assert.Contains(jobs, job =>
                job.WorkspaceId == pacific.Workspace.Id && job.LocalDate == new DateOnly(2026, 8, 2));

            var notifications = await verification.Notifications.AsNoTracking()
                .OrderBy(notification => notification.StateVersion)
                .ToListAsync();
            Assert.Equal(2, notifications.Count);
            Assert.Equal([1L, 2L], notifications.Select(notification => notification.StateVersion));
            Assert.All(notifications, notification => Assert.Equal(graph.Recipient.Id, notification.UserId));
            Assert.Equal(2, await verification.OutboxEvents.CountAsync());
            Assert.Equal(2, (await verification.NotificationUserStates.AsNoTracking().SingleAsync()).Version);
        });
    }

    [PostgreSqlFact]
    public async Task DifferentUsersInSameTenantGenerateConcurrently()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await EnablePersistedNotificationsFeatureAsync(database, graph);
            await AddQualifyingTaskAsync(database, graph);
            var secondRecipient = await AddRecipientToWorkspaceAsync(database, graph, "same-tenant-recipient");
            await AddQualifyingTaskAsync(
                database,
                graph.Tenant,
                graph.Actor,
                graph.Workspace,
                graph.Project,
                secondRecipient,
                "same tenant second recipient task",
                deadlineMinute: 2);

            var firstJob = await AddJobAsync(database, graph);
            var secondJob = await AddJobAsync(
                database,
                graph.Tenant,
                graph.Workspace,
                secondRecipient,
                LocalDate);
            var claims = await ClaimDueAsync(
                database,
                graph.Tenant,
                "different-users-same-tenant",
                batchSize: 2,
                claimTimeout: TimeSpan.FromMinutes(2));
            var claimsByJob = claims.ToDictionary(claim => claim.JobId);

            var results = await GenerateWithFirstCandidateFencePausedAsync(
                database,
                graph.Tenant,
                claimsByJob[firstJob.Id],
                claimsByJob[secondJob.Id]);

            Assert.All(results, result =>
            {
                Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
                Assert.Equal(1, result.Counts.Total);
                Assert.NotNull(result.NotificationId);
            });
            await AssertClaimsSucceededWithoutExpiryAsync(database, graph.Tenant, claims);
            await using var verification = CreateTenantContext(database, graph.Tenant);
            Assert.Equal(2, await verification.Notifications.CountAsync());
            Assert.Equal(2, await verification.OutboxEvents.CountAsync());
        });
    }

    [PostgreSqlFact]
    public async Task DifferentUsersInSameWorkspaceDoNotShareExclusiveFence()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await EnablePersistedNotificationsFeatureAsync(database, graph);
            await AddQualifyingTaskAsync(database, graph);
            var secondRecipient = await AddRecipientToWorkspaceAsync(database, graph, "shared-workspace-recipient");
            await AddQualifyingTaskAsync(
                database,
                graph.Tenant,
                graph.Actor,
                graph.Workspace,
                graph.Project,
                secondRecipient,
                "shared workspace second recipient task",
                deadlineMinute: 2);

            var firstJob = await AddJobAsync(database, graph);
            var secondJob = await AddJobAsync(
                database,
                graph.Tenant,
                graph.Workspace,
                secondRecipient,
                LocalDate);
            var claims = await ClaimDueAsync(
                database,
                graph.Tenant,
                "different-users-same-workspace",
                batchSize: 2,
                claimTimeout: TimeSpan.FromMinutes(2));
            var claimsByJob = claims.ToDictionary(claim => claim.JobId);

            var results = await GenerateWithFirstCandidateFencePausedAsync(
                database,
                graph.Tenant,
                claimsByJob[firstJob.Id],
                claimsByJob[secondJob.Id]);

            Assert.All(results, result => Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome));
            await AssertClaimsSucceededWithoutExpiryAsync(database, graph.Tenant, claims);
            await using var verification = CreateTenantContext(database, graph.Tenant);
            Assert.Equal(2, await verification.Notifications.CountAsync());
            Assert.Equal(2, await verification.OutboxEvents.CountAsync());
        });
    }

    [PostgreSqlFact]
    public async Task DifferentWorkspacesInSameTenantDoNotShareExclusiveFence()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await EnablePersistedNotificationsFeatureAsync(database, graph);
            await AddQualifyingTaskAsync(database, graph);
            var secondRecipient = await AddUserAsync(database, "different-workspace-recipient");
            var secondWorkspace = await AddWorkspaceForRecipientAsync(database, graph, secondRecipient);

            var firstJob = await AddJobAsync(database, graph);
            var secondJob = await AddJobAsync(
                database,
                graph.Tenant,
                secondWorkspace.Workspace,
                secondRecipient,
                LocalDate);
            var claims = await ClaimDueAsync(
                database,
                graph.Tenant,
                "different-workspaces-same-tenant",
                batchSize: 2,
                claimTimeout: TimeSpan.FromMinutes(2));
            var claimsByJob = claims.ToDictionary(claim => claim.JobId);

            var results = await GenerateWithFirstCandidateFencePausedAsync(
                database,
                graph.Tenant,
                claimsByJob[firstJob.Id],
                claimsByJob[secondJob.Id]);

            Assert.All(results, result => Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome));
            await AssertClaimsSucceededWithoutExpiryAsync(database, graph.Tenant, claims);
            await using var verification = CreateTenantContext(database, graph.Tenant);
            Assert.Equal(2, await verification.Notifications.CountAsync());
            Assert.Equal(2, await verification.OutboxEvents.CountAsync());
        });
    }

    [PostgreSqlFact]
    public async Task SlowFirstClaimDoesNotExpireLaterSameTenantClaims()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await EnablePersistedNotificationsFeatureAsync(database, graph);
            await AddQualifyingTaskAsync(database, graph);
            var laterRecipient = await AddRecipientToWorkspaceAsync(database, graph, "later-claim-recipient");
            await AddQualifyingTaskAsync(
                database,
                graph.Tenant,
                graph.Actor,
                graph.Workspace,
                graph.Project,
                laterRecipient,
                "later same tenant claim task",
                deadlineMinute: 2);

            var firstJob = await AddJobAsync(
                database,
                graph.Tenant,
                graph.Workspace,
                graph.Recipient,
                LocalDate,
                scheduledForUtc: Now.AddMinutes(-31));
            var laterJob = await AddJobAsync(
                database,
                graph.Tenant,
                graph.Workspace,
                laterRecipient,
                LocalDate,
                scheduledForUtc: Now.AddMinutes(-30));
            var firstClaim = Assert.Single(await ClaimDueAsync(
                database,
                graph.Tenant,
                "slow-first-claim",
                batchSize: 1,
                claimTimeout: TimeSpan.FromMinutes(5)));
            Assert.Equal(firstJob.Id, firstClaim.JobId);
            var laterClaim = Assert.Single(await ClaimDueAsync(
                database,
                graph.Tenant,
                "later-claim",
                batchSize: 1,
                claimTimeout: TimeSpan.FromSeconds(1)));
            Assert.Equal(laterJob.Id, laterClaim.JobId);

            var gate = new CandidateFenceGate();
            var firstGeneration = GenerateClaimAsync(database, graph.Tenant, firstClaim, gate);
            await gate.WaitForArrivalAsync();
            try
            {
                var laterGeneration = GenerateClaimAsync(database, graph.Tenant, laterClaim);
                var laterResult = await laterGeneration.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(gate.IsHolding);
                Assert.False(firstGeneration.IsCompleted);
                Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, laterResult.Outcome);
                Assert.Equal(1, laterResult.Counts.Total);

                var staleClaims = await ClaimDueAsync(
                    database,
                    graph.Tenant,
                    "lease-expiry-probe",
                    batchSize: 2,
                    claimTimeout: TimeSpan.FromSeconds(1),
                    now: Now.AddSeconds(2));
                Assert.Empty(staleClaims);
            }
            finally
            {
                gate.Release();
            }

            var firstResult = await firstGeneration.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, firstResult.Outcome);
            await AssertClaimsSucceededWithoutExpiryAsync(database, graph.Tenant, [firstClaim, laterClaim]);

            await using var verification = CreateTenantContext(database, graph.Tenant);
            var laterPersisted = await verification.TaskDeadlineDigestJobs.AsNoTracking()
                .SingleAsync(job => job.Id == laterJob.Id);
            Assert.Equal(1, laterPersisted.AutomaticAttemptCount);
            Assert.Equal(1, await verification.TaskDeadlineDigestAttempts.CountAsync(attempt => attempt.JobId == laterJob.Id));
            Assert.DoesNotContain(
                await verification.TaskDeadlineDigestAttempts.AsNoTracking()
                    .Where(attempt => attempt.JobId == laterJob.Id)
                    .ToListAsync(),
                attempt => attempt.Status == TaskDeadlineDigestAttemptStatus.Expired);
        });
    }

    [PostgreSqlFact]
    public async Task SlowFirstSameRecipientClaimDoesNotExpireQueuedClaim()
    {
        var observation = await ExerciseSameRecipientQueuedClaimLeaseAsync(expiryProbeCount: 1);

        Assert.Equal([0], observation.ExpiryProbeClaimCounts);
        Assert.Equal(TaskDeadlineDigestJobStatus.Claimed, observation.SecondJobStatusDuringProbe);
        Assert.Equal(TaskDeadlineDigestAttemptStatus.Claimed, observation.SecondAttemptStatusDuringProbe);
        Assert.Equal(observation.SecondClaim.ClaimToken, observation.SecondClaimTokenDuringProbe);
        Assert.Equal(1, observation.SecondAttemptCountDuringProbe);
        Assert.Equal(1, observation.SecondAutomaticAttemptCountDuringProbe);
        Assert.Equal(0, observation.ExpiredAttemptCountDuringProbe);
        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, observation.FirstResult.Outcome);
        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, observation.SecondResult.Outcome);
        Assert.Equal(2, observation.NotificationCount);
        Assert.Equal(2, observation.OutboxCount);
        Assert.Equal([1L, 2L], observation.NotificationStateVersions);
        Assert.Equal(0, observation.ExpiredAttemptCountAfterCompletion);
        Assert.Equal(0, observation.ClaimLostCount);
    }

    [PostgreSqlFact]
    public async Task SameRecipientWaitingClaimIsSkippedByExpiryScanner()
    {
        var observation = await ExerciseSameRecipientQueuedClaimLeaseAsync(expiryProbeCount: 1);

        Assert.Equal([0], observation.ExpiryProbeClaimCounts);
        Assert.True(observation.FirstRecipientUserLockWasHeld);
        Assert.True(observation.SecondJobFenceWasAcquired);
        Assert.True(observation.SecondAttemptFenceWasAcquired);
        Assert.True(observation.SecondUserLockWasRequested);
        Assert.False(observation.SecondGenerationCompletedBeforeFirstRelease);
        Assert.True(observation.SecondClaimExpiresBeforeProbe);
        Assert.Equal(TaskDeadlineDigestJobStatus.Claimed, observation.SecondJobStatusDuringProbe);
        Assert.Equal(TaskDeadlineDigestAttemptStatus.Claimed, observation.SecondAttemptStatusDuringProbe);
        Assert.Equal(observation.SecondClaim.ClaimToken, observation.SecondClaimTokenDuringProbe);
        Assert.Equal(1, observation.SecondAutomaticAttemptCountDuringProbe);
        Assert.Equal(0, observation.ExpiredAttemptCountDuringProbe);
        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, observation.SecondResult.Outcome);
    }

    [PostgreSqlFact]
    public async Task SameRecipientQueuedClaimKeepsAutomaticAttemptBudget()
    {
        var observation = await ExerciseSameRecipientQueuedClaimLeaseAsync(expiryProbeCount: 3);

        Assert.Equal([0, 0, 0], observation.ExpiryProbeClaimCounts);
        Assert.Equal(1, observation.SecondAttemptRowCountDuringProbe);
        Assert.Equal(1, observation.SecondAttemptCountDuringProbe);
        Assert.Equal(1, observation.SecondAutomaticAttemptCountDuringProbe);
        Assert.Equal(0, observation.ExpiredAttemptCountDuringProbe);
        Assert.Equal(observation.SecondClaim.ClaimToken, observation.SecondClaimTokenDuringProbe);
        Assert.Equal(TaskDeadlineDigestJobStatus.Claimed, observation.SecondJobStatusDuringProbe);
        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, observation.SecondResult.Outcome);
        Assert.Equal(1, observation.SecondAttemptCountAfterCompletion);
        Assert.Equal(1, observation.SecondAutomaticAttemptCountAfterCompletion);
        Assert.Equal(1, observation.SecondAttemptRowCountAfterCompletion);
        Assert.Equal(0, observation.ExpiredAttemptCountAfterCompletion);
    }

    [PostgreSqlFact]
    public async Task ClaimLostBeforeTransactionFenceStagesNothing()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, graph);
            var job = await AddJobAsync(database, graph);
            var oldClaim = Assert.Single(await ClaimDueAsync(
                database,
                graph.Tenant,
                "claim-lost-before-fence",
                batchSize: 1,
                claimTimeout: TimeSpan.FromSeconds(1)));
            Assert.Equal(job.Id, oldClaim.JobId);

            // Expire and reclaim before GenerateAsync can acquire its first
            // Job/Attempt fence. The stale worker must be fenced before it can
            // stage Notification, NotificationUserState, or Outbox state.
            var replacementClaim = Assert.Single(await ClaimDueAsync(
                database,
                graph.Tenant,
                "claim-lost-recovery",
                batchSize: 1,
                claimTimeout: TimeSpan.FromMinutes(2),
                now: Now.AddSeconds(2)));
            Assert.Equal(job.Id, replacementClaim.JobId);
            Assert.NotEqual(oldClaim.ClaimToken, replacementClaim.ClaimToken);

            var result = await GenerateClaimAsync(database, graph.Tenant, oldClaim);
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.ClaimLost, result.Outcome);
            Assert.Null(result.NotificationId);

            await using var verification = CreateTenantContext(database, graph.Tenant);
            Assert.Empty(await verification.Notifications.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.NotificationUserStates.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.OutboxEvents.AsNoTracking().ToListAsync());

            var persistedJob = await verification.TaskDeadlineDigestJobs.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == job.Id);
            Assert.Equal(TaskDeadlineDigestJobStatus.Claimed, persistedJob.Status);
            Assert.Equal(replacementClaim.ClaimToken, persistedJob.ClaimToken);
            Assert.NotEqual(TaskDeadlineDigestJobStatus.Succeeded, persistedJob.Status);

            var attempts = await verification.TaskDeadlineDigestAttempts.AsNoTracking()
                .Where(candidate => candidate.JobId == job.Id)
                .OrderBy(candidate => candidate.AttemptNumber)
                .ToListAsync();
            Assert.Equal(2, attempts.Count);
            Assert.Equal(TaskDeadlineDigestAttemptStatus.Expired, attempts[0].Status);
            Assert.Equal(TaskDeadlineDigestAttemptStatus.Claimed, attempts[1].Status);
        });
    }

    [PostgreSqlFact]
    public async Task SameRecipientStillSerializesNotificationStateVersion()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await EnablePersistedNotificationsFeatureAsync(database, graph);
            await AddQualifyingTaskAsync(database, graph);
            var secondWorkspace = await AddWorkspaceForRecipientAsync(database, graph, graph.Recipient);
            var firstJob = await AddJobAsync(database, graph);
            var secondJob = await AddJobAsync(
                database,
                graph.Tenant,
                secondWorkspace.Workspace,
                graph.Recipient,
                LocalDate);
            var claims = await ClaimDueAsync(
                database,
                graph.Tenant,
                "same-recipient-state-version",
                batchSize: 2,
                claimTimeout: TimeSpan.FromMinutes(2));
            var claimsByJob = claims.ToDictionary(claim => claim.JobId);

            // SavingChanges is reached only after the generation fence has
            // acquired the recipient User FOR UPDATE lock. Hold that first
            // transaction there, then prove the second same-user digest has
            // reached (and cannot complete past) its own User lock request.
            var recipientGate = new FinalCandidateCommitGate();
            var firstGeneration = GenerateClaimAsync(
                database,
                graph.Tenant,
                claimsByJob[firstJob.Id],
                recipientGate);
            await recipientGate.WaitForArrivalAsync();
            var secondArrival = new UserLockArrivalInterceptor();
            var secondGeneration = GenerateClaimAsync(
                database,
                graph.Tenant,
                claimsByJob[secondJob.Id],
                secondArrival);
            try
            {
                await secondArrival.WaitForArrivalAsync();
                Assert.False(secondGeneration.IsCompleted);
            }
            finally
            {
                recipientGate.Release();
            }
            var results = await Task.WhenAll(
                firstGeneration.WaitAsync(TimeSpan.FromSeconds(15)),
                secondGeneration.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.All(results, result => Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome));
            await AssertClaimsSucceededWithoutExpiryAsync(database, graph.Tenant, claims);
            await using var verification = CreateTenantContext(database, graph.Tenant);
            var notifications = await verification.Notifications.AsNoTracking()
                .OrderBy(notification => notification.StateVersion)
                .ToListAsync();
            Assert.Equal([1L, 2L], notifications.Select(notification => notification.StateVersion));
            Assert.Equal(2, notifications.Select(notification => notification.StateVersion).Distinct().Count());
            Assert.All(notifications, notification => Assert.Equal(graph.Recipient.Id, notification.UserId));
            Assert.Equal(2, await verification.OutboxEvents.CountAsync());
            Assert.Equal(2, (await verification.NotificationUserStates.AsNoTracking().SingleAsync()).Version);
        });
    }

    [PostgreSqlFact]
    public async Task ConcurrentTenantMutationWaitsForGenerationFence()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await EnablePersistedNotificationsFeatureAsync(database, graph);
            await AddQualifyingTaskAsync(database, graph);
            var job = await AddJobAsync(database, graph);
            var claim = Assert.Single(await ClaimDueAsync(
                database,
                graph.Tenant,
                "tenant-mutation-fence",
                batchSize: 1,
                claimTimeout: TimeSpan.FromMinutes(2)));
            Assert.Equal(job.Id, claim.JobId);

            var gate = new CandidateFenceGate();
            var generation = GenerateClaimAsync(database, graph.Tenant, claim, gate);
            await gate.WaitForArrivalAsync();
            bool mutationCommittedBeforeGeneration;
            try
            {
                mutationCommittedBeforeGeneration = await TryMutateWithLockTimeoutAsync(
                    database,
                    graph,
                    async db =>
                    {
                        var tenant = await db.Tenants.SingleAsync(item => item.Id == graph.Tenant.Id);
                        tenant.Status = TenantStatus.Suspended;
                    });
            }
            finally
            {
                gate.Release();
            }

            if (mutationCommittedBeforeGeneration)
            {
                await AssertMutationFirstCannotLeaveVisibleDigestAsync(generation, database, graph);
                return;
            }

            var result = await generation.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
            Assert.NotNull(result.NotificationId);
            await AssertVisibleDigestArtifactsAsync(database, graph, result.NotificationId.Value);
            await CommitMutationAsync(database, graph, async db =>
            {
                var tenant = await db.Tenants.SingleAsync(item => item.Id == graph.Tenant.Id);
                tenant.Status = TenantStatus.Suspended;
            });
        });
    }

    [PostgreSqlFact]
    public async Task ConcurrentFeatureDisableWaitsOrPreventsDigestCommit()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {

            var settingsGraph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, settingsGraph);
            var settingsSources = await EnablePersistedNotificationsFeatureAsync(database, settingsGraph);
            await AssertFeatureDisableIsFencedAsync(database, settingsGraph, async (db, sources) =>
            {
                var settings = await db.TenantSettings.SingleAsync(item => item.Id == sources.Settings!.Id);
                settings.FeatureFlagsJson = JsonSerializer.Serialize(new Dictionary<string, bool>
                {
                    [FeatureKeys.TasksNotificationsV1] = false
                });
            }, settingsSources);

            var subscriptionGraph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, subscriptionGraph);
            var subscriptionSources = await EnablePersistedNotificationsFeatureAsync(database, subscriptionGraph);
            await AssertFeatureDisableIsFencedAsync(database, subscriptionGraph, async (db, sources) =>
            {
                var subscription = await db.Subscriptions.SingleAsync(item => item.Id == sources.Subscription.Id);
                subscription.PlanId = sources.DisabledPlan.Id;
            }, subscriptionSources);

            var planGraph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, planGraph);
            var planSources = await EnablePersistedNotificationsFeatureAsync(database, planGraph);
            await AssertFeatureDisableIsFencedAsync(database, planGraph, async (db, sources) =>
            {
                var plan = await db.Plans.SingleAsync(item => item.Id == sources.EnabledPlan.Id);
                plan.EnabledFeaturesJson = "[]";
            }, planSources);

            var missingSettingsGraph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, missingSettingsGraph);
            var missingSettingsSources = await EnablePersistedNotificationsFeatureAsync(
                database,
                missingSettingsGraph,
                includeTenantSettings: false);
            await AssertFeatureDisableIsFencedAsync(database, missingSettingsGraph, async (db, _) =>
            {
                db.TenantSettings.Add(new TenantSettings
                {
                    TenantId = missingSettingsGraph.Tenant.Id,
                    DisplayName = "late feature override",
                    FeatureFlagsJson = JsonSerializer.Serialize(new Dictionary<string, bool>
                    {
                        [FeatureKeys.TasksNotificationsV1] = false
                    })
                });
            }, missingSettingsSources);
        });
    }

    [PostgreSqlFact]
    public async Task MissingWatchRowOptOutInsertCannotBypassFence()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await EnablePersistedNotificationsFeatureAsync(database, graph);
            var task = await AddQualifyingTaskAsync(database, graph);
            await using (var setup = CreateTenantContext(database, graph.Tenant))
            {
                Assert.Empty(await setup.WorkItemWatchStates.AsNoTracking()
                    .Where(watch => watch.TaskItemId == task.Id && watch.UserId == graph.Recipient.Id)
                    .ToListAsync());
            }

            var job = await AddJobAsync(database, graph);
            var claim = Assert.Single(await ClaimDueAsync(
                database,
                graph.Tenant,
                "missing-watch-phantom",
                batchSize: 1,
                claimTimeout: TimeSpan.FromMinutes(2)));
            Assert.Equal(job.Id, claim.JobId);

            var gate = new CandidateFenceGate();
            var generation = GenerateClaimAsync(database, graph.Tenant, claim, gate);
            await gate.WaitForArrivalAsync();
            bool mutationCommittedBeforeGeneration;
            try
            {
                mutationCommittedBeforeGeneration = await TryMutateWithLockTimeoutAsync(
                    database,
                    graph,
                    db =>
                    {
                        db.WorkItemWatchStates.Add(new WorkItemWatchState
                        {
                            TenantId = graph.Tenant.Id,
                            TaskItemId = task.Id,
                            UserId = graph.Recipient.Id,
                            AutomaticSources = WorkItemWatchAutomaticSource.PrimaryAssignee,
                            IsExplicitOptOut = true,
                            IsWatching = false,
                            UpdatedAt = Now
                        });
                        return Task.CompletedTask;
                    });
            }
            finally
            {
                gate.Release();
            }

            if (mutationCommittedBeforeGeneration)
            {
                await AssertMutationFirstCannotLeaveVisibleDigestAsync(generation, database, graph);
            }
            else
            {
                var result = await generation.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
                Assert.NotNull(result.NotificationId);
                await CommitMutationAsync(database, graph, db =>
                {
                    db.WorkItemWatchStates.Add(new WorkItemWatchState
                    {
                        TenantId = graph.Tenant.Id,
                        TaskItemId = task.Id,
                        UserId = graph.Recipient.Id,
                        AutomaticSources = WorkItemWatchAutomaticSource.PrimaryAssignee,
                        IsExplicitOptOut = true,
                        IsWatching = false,
                        UpdatedAt = Now
                    });
                    return Task.CompletedTask;
                });
            }

            await using var verification = CreateTenantContext(database, graph.Tenant);
            var watch = await verification.WorkItemWatchStates.AsNoTracking().SingleAsync(item =>
                item.TaskItemId == task.Id && item.UserId == graph.Recipient.Id);
            Assert.True(watch.IsExplicitOptOut);
            Assert.False(watch.IsWatching);
        });
    }

    [PostgreSqlFact]
    public async Task MembershipRevokedWhileRecipientLockWaitsIsRecheckedBeforeNotificationCommit()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, graph);
            await AddJobAsync(database, graph);

            TaskDeadlineDigestClaim claim;
            var claimTenant = TenantScope(graph.Tenant);
            await using (var claimContext = CreateTenantContext(database, graph.Tenant, claimTenant))
            {
                var claimRepository = new TaskDeadlineDigestRepository(claimContext, claimTenant);
                claim = Assert.Single(await claimRepository.ClaimDueAsync(
                    "membership-recheck-worker",
                    Now,
                    batchSize: 1,
                    claimTimeout: TimeSpan.FromMinutes(2)));
            }

            var lockTenant = TenantScope(graph.Tenant);
            await using var lockContext = CreateTenantContext(database, graph.Tenant, lockTenant);
            await using var lockTransaction = await lockContext.Database.BeginTransactionAsync();
            await lockContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM users WHERE \"Id\" = {graph.Recipient.Id} FOR UPDATE");

            var lockArrival = new UserLockArrivalInterceptor();
            var generation = GenerateClaimAsync(database, graph.Tenant, claim, lockArrival);
            await lockArrival.WaitForArrivalAsync();

            await using (var mutation = CreateTenantContext(database, graph.Tenant))
            {
                var membership = await mutation.WorkspaceMembers.SingleAsync(member =>
                    member.WorkspaceId == graph.Workspace.Id &&
                    member.UserId == graph.Recipient.Id);
                membership.Status = MembershipStatus.Suspended;
                await mutation.SaveChangesAsync();
            }

            var remainedBlockedUntilLockRelease = !generation.IsCompleted;
            await lockTransaction.CommitAsync();
            var result = await generation.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(remainedBlockedUntilLockRelease);
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.SucceededWithoutCandidates, result.Outcome);
            Assert.Equal(0, result.Counts.Total);
            Assert.Null(result.NotificationId);

            await using var verification = CreateTenantContext(database, graph.Tenant);
            var job = await verification.TaskDeadlineDigestJobs.AsNoTracking().SingleAsync();
            Assert.Equal(TaskDeadlineDigestJobStatus.Succeeded, job.Status);
            Assert.Null(job.NotificationId);
            Assert.Equal(
                TaskDeadlineDigestAttemptStatus.Succeeded,
                (await verification.TaskDeadlineDigestAttempts.AsNoTracking().SingleAsync()).Status);
            Assert.Empty(await verification.Notifications.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.NotificationUserStates.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.OutboxEvents.AsNoTracking().ToListAsync());
        });
    }

    [PostgreSqlFact]
    public async Task MembershipRevokedAfterFinalEvaluationCannotCommitDigest()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, graph);

            await AssertFinalCandidateMutationIsFencedAsync(database, graph, async db =>
            {
                var membership = await db.WorkspaceMembers.SingleAsync(member =>
                    member.WorkspaceId == graph.Workspace.Id &&
                    member.UserId == graph.Recipient.Id);
                membership.Status = MembershipStatus.Suspended;
            });
        });
    }

    [PostgreSqlFact]
    public async Task WorkspaceArchivedAfterFinalEvaluationCannotCommitDigest()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, graph);

            await AssertFinalCandidateMutationIsFencedAsync(database, graph, async db =>
            {
                var workspace = await db.Workspaces.SingleAsync(item => item.Id == graph.Workspace.Id);
                workspace.Status = WorkspaceStatus.Archived;
            });
        });
    }

    [PostgreSqlFact]
    public async Task ProjectArchivedAfterFinalEvaluationCannotCommitDigest()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, graph);

            await AssertFinalCandidateMutationIsFencedAsync(database, graph, async db =>
            {
                var project = await db.Projects.SingleAsync(item => item.Id == graph.Project.Id);
                project.Status = ProjectStatus.Archived;
            });
        });
    }

    [PostgreSqlFact]
    public async Task TaskCompletedAfterFinalEvaluationCannotCommitDigest()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            var task = await AddQualifyingTaskAsync(database, graph);

            await AssertFinalCandidateMutationIsFencedAsync(database, graph, async db =>
            {
                var currentTask = await db.TaskItems.SingleAsync(item => item.Id == task.Id);
                currentTask.Status = TaskItemStatus.Completed;
                currentTask.CompletedAt = Now;
            });
        });
    }

    [PostgreSqlFact]
    public async Task WatchOptOutAfterFinalEvaluationCannotCommitDigest()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            var task = await AddQualifyingTaskAsync(database, graph);
            await using (var setup = CreateTenantContext(database, graph.Tenant))
            {
                setup.WorkItemWatchStates.Add(new WorkItemWatchState
                {
                    TenantId = graph.Tenant.Id,
                    TaskItemId = task.Id,
                    UserId = graph.Recipient.Id,
                    AutomaticSources = WorkItemWatchAutomaticSource.PrimaryAssignee,
                    IsWatching = true,
                    UpdatedAt = Now
                });
                await setup.SaveChangesAsync();
            }

            await AssertFinalCandidateMutationIsFencedAsync(database, graph, async db =>
            {
                var watch = await db.WorkItemWatchStates.SingleAsync(item =>
                    item.TaskItemId == task.Id && item.UserId == graph.Recipient.Id);
                watch.IsExplicitOptOut = true;
                watch.IsWatching = false;
                watch.UpdatedAt = Now.AddMinutes(1);
            });
        });
    }

    [PostgreSqlFact]
    public async Task RelationshipRemovedAfterFinalEvaluationCannotCommitDigest()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            TaskItem task;
            await using (var setup = CreateTenantContext(database, graph.Tenant))
            {
                task = NewTask(graph, "relationship fence candidate", 1);
                setup.AddRange(
                    task,
                    new WorkItemCollaborator
                    {
                        TenantId = graph.Tenant.Id,
                        TaskItemId = task.Id,
                        UserId = graph.Recipient.Id,
                        AddedAt = Now,
                        AddedByUserId = graph.Actor.Id
                    });
                await setup.SaveChangesAsync();
            }

            await AssertFinalCandidateMutationIsFencedAsync(database, graph, async db =>
            {
                var collaborator = await db.WorkItemCollaborators.SingleAsync(item =>
                    item.TaskItemId == task.Id && item.UserId == graph.Recipient.Id);
                db.WorkItemCollaborators.Remove(collaborator);
            });
        });
    }

    [PostgreSqlFact]
    public async Task GeneratorWithNoCurrentCandidatesSucceedsWithoutNotificationOrOutbox()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddJobAsync(database, graph);

            var result = await GenerateAsync(database, graph);
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.SucceededWithoutCandidates, result.Outcome);
            Assert.Equal(0, result.Counts.Total);
            Assert.Null(result.NotificationId);

            await using var verification = CreateTenantContext(database, graph.Tenant);
            var job = await verification.TaskDeadlineDigestJobs.AsNoTracking().SingleAsync();
            Assert.Equal(TaskDeadlineDigestJobStatus.Succeeded, job.Status);
            Assert.Null(job.NotificationId);
            Assert.Equal(TaskDeadlineDigestAttemptStatus.Succeeded,
                (await verification.TaskDeadlineDigestAttempts.AsNoTracking().SingleAsync()).Status);
            Assert.Empty(await verification.Notifications.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.NotificationUserStates.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.OutboxEvents.AsNoTracking().ToListAsync());
        });
    }

    [PostgreSqlFact]
    public async Task GeneratorReusesPersistedLogicalNotificationWithoutSecondSignalOrStateAdvance()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, graph);
            var job = await AddJobAsync(database, graph);
            var logicalKey = TaskDeadlineDigestPolicy.BuildNotificationLogicalKey(
                graph.Workspace.Id,
                LocalDate,
                TaskDeadlineDigestPolicy.PolicyVersion);

            Guid existingNotificationId;
            var clock = new FixedClock(Now);
            var stagingTenant = TenantScope(graph.Tenant);
            await using (var staging = CreateTenantContext(database, graph.Tenant, stagingTenant))
            {
                var outbox = new TransactionalOutbox(
                    new OutboxEventRepository(staging),
                    stagingTenant,
                    clock);
                var notifications = new DbNotificationService(staging, clock, stagingTenant, outbox);
                existingNotificationId = await notifications.StageTaskDeadlineDigestByLogicalKeyAsync(
                    graph.Recipient.Id,
                    job.Id,
                    logicalKey);
                await staging.SaveChangesAsync();
            }

            var result = await GenerateAsync(database, graph);
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
            Assert.Equal(existingNotificationId, result.NotificationId);

            await using var verification = CreateTenantContext(database, graph.Tenant);
            Assert.Equal(1, await verification.Notifications.CountAsync());
            Assert.Equal(1, await verification.OutboxEvents.CountAsync());
            var state = await verification.NotificationUserStates.AsNoTracking().SingleAsync();
            Assert.Equal(1, state.Version);
            var persisted = await verification.TaskDeadlineDigestJobs.AsNoTracking().SingleAsync();
            Assert.Equal(TaskDeadlineDigestJobStatus.Succeeded, persisted.Status);
            Assert.Equal(existingNotificationId, persisted.NotificationId);
        });
    }

    [PostgreSqlFact]
    public async Task SaveFailureAfterStagingRollsBackNotificationSignalUserStateAndLedgerSuccess()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await AddQualifyingTaskAsync(database, graph);
            await AddJobAsync(database, graph);

            var interceptor = new ThrowAfterSaveInterceptor();
            var currentTenant = TenantScope(graph.Tenant);
            await using (var db = CreateTenantContext(database, graph.Tenant, currentTenant, interceptor))
            {
                var repository = new TaskDeadlineDigestRepository(db, currentTenant);
                var claim = Assert.Single(await repository.ClaimDueAsync(
                    "atomic-rollback-test",
                    Now,
                    batchSize: 1,
                    claimTimeout: TimeSpan.FromMinutes(2)));
                var clock = new FixedClock(Now);
                var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
                var notifications = new DbNotificationService(db, clock, currentTenant, outbox);
                var generator = new TaskDeadlineDigestGenerator(
                    repository,
                    notifications,
                    EnabledFeatureFlags.Instance,
                    new TaskDeadlineDigestDiagnostics());

                interceptor.Arm();
                await Assert.ThrowsAsync<InjectedSaveFailureException>(() =>
                    generator.GenerateAsync(claim, Now, candidatePageSize: 50));
            }

            await using var verification = CreateTenantContext(database, graph.Tenant);
            var job = await verification.TaskDeadlineDigestJobs.AsNoTracking().SingleAsync();
            Assert.Equal(TaskDeadlineDigestJobStatus.Claimed, job.Status);
            Assert.NotNull(job.ClaimToken);
            Assert.Null(job.NotificationId);
            Assert.Equal(TaskDeadlineDigestAttemptStatus.Claimed,
                (await verification.TaskDeadlineDigestAttempts.AsNoTracking().SingleAsync()).Status);
            Assert.Empty(await verification.Notifications.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.NotificationUserStates.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.OutboxEvents.AsNoTracking().ToListAsync());
        });
    }

    /// <summary>
    /// Stops the generator only after its final bounded candidate evaluation
    /// has finished and Notification/UserState/Outbox are staged, but before
    /// their SaveChanges/commit. A competing mutation therefore has exactly
    /// the problematic interval in which it could otherwise make the digest
    /// stale.
    ///
    /// The assertion deliberately accepts either approved fence strategy:
    /// a row-lock fence makes the mutation hit PostgreSQL lock_timeout and
    /// lets the current digest commit first; a version/recheck implementation
    /// may instead let the mutation commit first, in which case no visible
    /// digest artifact may survive.
    /// </summary>
    private static async Task AssertFinalCandidateMutationIsFencedAsync(
        string database,
        Graph graph,
        Func<AppDbContext, Task> mutateAsync)
    {
        await AddJobAsync(database, graph);
        TaskDeadlineDigestClaim claim;
        var claimTenant = TenantScope(graph.Tenant);
        await using (var claimContext = CreateTenantContext(database, graph.Tenant, claimTenant))
        {
            var claimRepository = new TaskDeadlineDigestRepository(claimContext, claimTenant);
            claim = Assert.Single(await claimRepository.ClaimDueAsync(
                "final-candidate-fence-test",
                Now,
                batchSize: 1,
                claimTimeout: TimeSpan.FromMinutes(2)));
        }

        var gate = new FinalCandidateCommitGate();
        var generation = GenerateClaimAsync(database, graph.Tenant, claim, gate);
        await gate.WaitForArrivalAsync();

        bool mutationCommittedBeforeGeneration;
        try
        {
            mutationCommittedBeforeGeneration = await TryMutateWithLockTimeoutAsync(
                database,
                graph,
                mutateAsync);
        }
        finally
        {
            // Always release the generator, including assertion failures, so
            // a failed race test cannot strand a temporary test database.
            gate.Release();
        }

        if (mutationCommittedBeforeGeneration)
        {
            await AssertMutationFirstCannotLeaveVisibleDigestAsync(generation, database, graph);
            return;
        }

        var result = await generation.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.Counts.Total);
        Assert.NotNull(result.NotificationId);
        await AssertVisibleDigestArtifactsAsync(database, graph, result.NotificationId.Value);

        // The first attempt observed the PostgreSQL lock. Re-run the same
        // mutation only after the generation transaction has committed to
        // prove the ordering rather than merely cancelling it.
        await CommitMutationAsync(database, graph, mutateAsync);
    }

    private static async Task<bool> TryMutateWithLockTimeoutAsync(
        string database,
        Graph graph,
        Func<AppDbContext, Task> mutateAsync)
    {
        try
        {
            await CommitMutationAsync(database, graph, mutateAsync, lockTimeout: "500ms");
            return true;
        }
        catch (Exception exception) when (IsPostgreSqlLockTimeout(exception))
        {
            return false;
        }
    }

    private static async Task CommitMutationAsync(
        string database,
        Graph graph,
        Func<AppDbContext, Task> mutateAsync,
        string? lockTimeout = null)
    {
        await using var db = CreateTenantContext(database, graph.Tenant);
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (lockTimeout is not null)
        {
            // SET LOCAL applies only to this mutation transaction. It makes a
            // blocked UPDATE an explicit race-test result without sleeps or
            // process-level timing assumptions.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('lock_timeout', {lockTimeout}, true);");
        }

        await mutateAsync(db);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task AssertMutationFirstCannotLeaveVisibleDigestAsync(
        Task<TaskDeadlineDigestGenerationResult> generation,
        string database,
        Graph graph)
    {
        try
        {
            var result = await generation.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.SucceededWithoutCandidates, result.Outcome);
            Assert.Equal(0, result.Counts.Total);
            Assert.Null(result.NotificationId);
        }
        catch (TaskDeadlineDigestRetryablePersistenceConflictException)
        {
            // A bounded, safe conflict rollback is also acceptable only when
            // it leaves no user-visible artifact. The worker's normal retry
            // will later evaluate the now-current state.
        }

        await using var verification = CreateTenantContext(database, graph.Tenant);
        Assert.Empty(await verification.Notifications.AsNoTracking().ToListAsync());
        Assert.Empty(await verification.NotificationUserStates.AsNoTracking().ToListAsync());
        Assert.Empty(await verification.OutboxEvents.AsNoTracking().ToListAsync());
        var job = await verification.TaskDeadlineDigestJobs.AsNoTracking().SingleAsync();
        Assert.Null(job.NotificationId);
    }

    private static async Task AssertVisibleDigestArtifactsAsync(
        string database,
        Graph graph,
        Guid notificationId)
    {
        await using var verification = CreateTenantContext(database, graph.Tenant);
        var job = await verification.TaskDeadlineDigestJobs.AsNoTracking().SingleAsync();
        Assert.Equal(TaskDeadlineDigestJobStatus.Succeeded, job.Status);
        Assert.Equal(notificationId, job.NotificationId);
        var notification = await verification.Notifications.AsNoTracking().SingleAsync();
        Assert.Equal(notificationId, notification.Id);
        Assert.Null(notification.Body);
        Assert.Equal(1, (await verification.NotificationUserStates.AsNoTracking().SingleAsync()).Version);
        Assert.Single(await verification.OutboxEvents.AsNoTracking().ToListAsync());
    }

    private static bool IsPostgreSqlLockTimeout(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } ||
        exception.InnerException is not null && IsPostgreSqlLockTimeout(exception.InnerException);

    private static async Task AddAndClaimJobThenAssertCandidatesAsync(
        string database,
        Graph graph,
        IReadOnlyCollection<Guid> included,
        IReadOnlyCollection<Guid> excluded)
    {
        await AddJobAsync(database, graph);
        var currentTenant = TenantScope(graph.Tenant);
        await using var queryContext = CreateTenantContext(database, graph.Tenant, currentTenant);
        var repository = new TaskDeadlineDigestRepository(queryContext, currentTenant);
        var claim = Assert.Single(await repository.ClaimDueAsync(
            "candidate-policy-test",
            Now,
            batchSize: 1,
            claimTimeout: TimeSpan.FromMinutes(2)));
        var actual = await CandidateIdsAsync(repository, claim);
        Assert.Equal(included.Order(), actual.Order());
        Assert.All(excluded, taskId => Assert.DoesNotContain(taskId, actual));
    }

    private static async Task<IReadOnlyList<Guid>> CandidateIdsAsync(
        TaskDeadlineDigestRepository repository,
        TaskDeadlineDigestClaim claim) =>
        (await repository.ListCurrentCandidatesAsync(
            claim.JobId,
            claim.ClaimToken,
            Now.AddDays(4),
            page: 0,
            pageSize: 100)).Select(candidate => candidate.TaskId).ToArray();

    private static async Task<TaskDeadlineDigestGenerationResult> GenerateAsync(
        string database,
        Graph graph)
    {
        var currentTenant = TenantScope(graph.Tenant);
        await using var db = CreateTenantContext(database, graph.Tenant, currentTenant);
        var repository = new TaskDeadlineDigestRepository(db, currentTenant);
        var claim = Assert.Single(await repository.ClaimDueAsync(
            "generator-test",
            Now,
            batchSize: 1,
            claimTimeout: TimeSpan.FromMinutes(2)));
        var clock = new FixedClock(Now);
        var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
        var notifications = new DbNotificationService(db, clock, currentTenant, outbox);
        var generator = new TaskDeadlineDigestGenerator(
            repository,
            notifications,
            EnabledFeatureFlags.Instance,
            new TaskDeadlineDigestDiagnostics());
        return await generator.GenerateAsync(claim, Now, candidatePageSize: 50);
    }

    private static async Task<TaskDeadlineDigestGenerationResult> GenerateClaimAsync(
        string database,
        Tenant tenant,
        TaskDeadlineDigestClaim claim,
        IInterceptor? interceptor = null,
        bool usePersistedFeatureFlags = false)
    {
        var currentTenant = TenantScope(tenant);
        await using var db = CreateTenantContext(database, tenant, currentTenant, interceptor);
        var repository = new TaskDeadlineDigestRepository(db, currentTenant);
        var clock = new FixedClock(Now);
        var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
        var notifications = new DbNotificationService(db, clock, currentTenant, outbox);
        var generator = new TaskDeadlineDigestGenerator(
            repository,
            notifications,
            usePersistedFeatureFlags
                ? new FeatureFlagService(new TenantPlanRepository(db), currentTenant)
                : EnabledFeatureFlags.Instance,
            new TaskDeadlineDigestDiagnostics());
        return await generator.GenerateAsync(claim, Now, candidatePageSize: 50);
    }

    private static async Task<TaskDeadlineDigestGenerationResult[]> GenerateWithFirstCandidateFencePausedAsync(
        string database,
        Tenant tenant,
        TaskDeadlineDigestClaim firstClaim,
        TaskDeadlineDigestClaim secondClaim)
    {
        var gate = new CandidateFenceGate();
        var firstGeneration = GenerateClaimAsync(database, tenant, firstClaim, gate);
        await gate.WaitForArrivalAsync();
        try
        {
            var secondGeneration = GenerateClaimAsync(database, tenant, secondClaim);
            var secondResult = await secondGeneration.WaitAsync(TimeSpan.FromSeconds(15));

            // This is the proof point: the second real PostgreSQL generation
            // reached commit while the first transaction still held its
            // candidate fence. Starting two Tasks alone would not prove it.
            Assert.True(gate.IsHolding);
            Assert.False(firstGeneration.IsCompleted);

            gate.Release();
            var firstResult = await firstGeneration.WaitAsync(TimeSpan.FromSeconds(15));
            return [firstResult, secondResult];
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<IReadOnlyList<TaskDeadlineDigestClaim>> ClaimDueAsync(
        string database,
        Tenant tenant,
        string claimOwner,
        int batchSize,
        TimeSpan claimTimeout,
        DateTimeOffset? now = null)
    {
        var currentTenant = TenantScope(tenant);
        await using var db = CreateTenantContext(database, tenant, currentTenant);
        var repository = new TaskDeadlineDigestRepository(db, currentTenant);
        return await repository.ClaimDueAsync(
            claimOwner,
            now ?? Now,
            batchSize,
            claimTimeout);
    }

    private static async Task<SameRecipientQueuedClaimLeaseObservation>
        ExerciseSameRecipientQueuedClaimLeaseAsync(int expiryProbeCount)
    {
        if (expiryProbeCount < 1)
            throw new ArgumentOutOfRangeException(nameof(expiryProbeCount));

        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        SameRecipientQueuedClaimLeaseObservation? observation = null;

        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedGraphAsync(database);
            await EnablePersistedNotificationsFeatureAsync(database, graph);
            await AddQualifyingTaskAsync(database, graph);
            var secondWorkspace = await AddWorkspaceForRecipientAsync(database, graph, graph.Recipient);
            var firstJob = await AddJobAsync(
                database,
                graph.Tenant,
                graph.Workspace,
                graph.Recipient,
                LocalDate,
                scheduledForUtc: Now.AddMinutes(-31));
            var secondJob = await AddJobAsync(
                database,
                graph.Tenant,
                secondWorkspace.Workspace,
                graph.Recipient,
                LocalDate,
                scheduledForUtc: Now.AddMinutes(-30));
            var firstClaim = Assert.Single(await ClaimDueAsync(
                database,
                graph.Tenant,
                "same-recipient-first",
                batchSize: 1,
                claimTimeout: TimeSpan.FromMinutes(5)));
            Assert.Equal(firstJob.Id, firstClaim.JobId);
            var secondClaim = Assert.Single(await ClaimDueAsync(
                database,
                graph.Tenant,
                "same-recipient-second",
                batchSize: 1,
                claimTimeout: TimeSpan.FromSeconds(1)));
            Assert.Equal(secondJob.Id, secondClaim.JobId);

            // A is paused only after PostgreSQL has completed recipient User
            // FOR UPDATE. Its claim Job/Attempt fence is therefore held while
            // B first acquires its own Job/Attempt fence, then blocks trying
            // to take the same recipient User row.
            var firstRecipientGate = new RecipientUserLockGate();
            var firstGeneration = GenerateClaimAsync(
                database,
                graph.Tenant,
                firstClaim,
                firstRecipientGate);
            await firstRecipientGate.WaitForRecipientUserLockAsync();
            var firstRecipientUserLockWasHeld = firstRecipientGate.IsHolding;
            Assert.True(firstRecipientUserLockWasHeld);

            var secondFence = new QueuedSameRecipientClaimProbe();
            var secondGeneration = GenerateClaimAsync(
                database,
                graph.Tenant,
                secondClaim,
                secondFence);

            var expiryProbeClaimCounts = new List<int>(expiryProbeCount);
            TaskDeadlineDigestJobStatus secondJobStatusDuringProbe;
            TaskDeadlineDigestAttemptStatus secondAttemptStatusDuringProbe;
            Guid? secondClaimTokenDuringProbe;
            int secondAttemptCountDuringProbe;
            int secondAutomaticAttemptCountDuringProbe;
            int secondAttemptRowCountDuringProbe;
            int expiredAttemptCountDuringProbe;
            bool secondClaimExpiresBeforeProbe;
            bool secondGenerationCompletedBeforeFirstRelease;
            var probeNow = Now.AddSeconds(2);

            try
            {
                await secondFence.WaitForUserLockRequestAsync();
                Assert.True(secondFence.JobFenceWasAcquired);
                Assert.True(secondFence.AttemptFenceWasAcquired);
                Assert.True(secondFence.UserLockWasRequested);
                secondGenerationCompletedBeforeFirstRelease = secondGeneration.IsCompleted;
                Assert.False(secondGenerationCompletedBeforeFirstRelease);

                for (var probe = 0; probe < expiryProbeCount; probe++)
                {
                    var scannerClaims = await ClaimDueAsync(
                        database,
                        graph.Tenant,
                        $"same-recipient-expiry-probe-{probe}",
                        batchSize: 2,
                        claimTimeout: TimeSpan.FromSeconds(1),
                        now: probeNow.AddSeconds(probe));
                    expiryProbeClaimCounts.Add(scannerClaims.Count);
                    Assert.Empty(scannerClaims);
                }

                // A plain MVCC read is intentional: both locks are held by B,
                // so this observes the last committed state while proving the
                // expiry scanner did not rewrite its leased Job or Attempt.
                await using var intermediate = CreateTenantContext(database, graph.Tenant);
                var persistedSecondJob = await intermediate.TaskDeadlineDigestJobs.AsNoTracking()
                    .SingleAsync(job => job.Id == secondJob.Id);
                var persistedSecondAttempts = await intermediate.TaskDeadlineDigestAttempts.AsNoTracking()
                    .Where(attempt => attempt.JobId == secondJob.Id)
                    .OrderBy(attempt => attempt.AttemptNumber)
                    .ToListAsync();
                var persistedSecondAttempt = Assert.Single(persistedSecondAttempts);
                secondJobStatusDuringProbe = persistedSecondJob.Status;
                secondAttemptStatusDuringProbe = persistedSecondAttempt.Status;
                secondClaimTokenDuringProbe = persistedSecondJob.ClaimToken;
                secondAttemptCountDuringProbe = persistedSecondJob.AttemptCount;
                secondAutomaticAttemptCountDuringProbe = persistedSecondJob.AutomaticAttemptCount;
                secondAttemptRowCountDuringProbe = persistedSecondAttempts.Count;
                expiredAttemptCountDuringProbe = persistedSecondAttempts.Count(attempt =>
                    attempt.Status == TaskDeadlineDigestAttemptStatus.Expired);
                secondClaimExpiresBeforeProbe = persistedSecondJob.ClaimExpiresAt < probeNow;

                Assert.Equal(TaskDeadlineDigestJobStatus.Claimed, secondJobStatusDuringProbe);
                Assert.Equal(TaskDeadlineDigestAttemptStatus.Claimed, secondAttemptStatusDuringProbe);
                Assert.Equal(secondClaim.ClaimToken, secondClaimTokenDuringProbe);
                Assert.Equal(1, secondAttemptCountDuringProbe);
                Assert.Equal(1, secondAutomaticAttemptCountDuringProbe);
                Assert.Equal(1, secondAttemptRowCountDuringProbe);
                Assert.Equal(0, expiredAttemptCountDuringProbe);
                Assert.True(secondClaimExpiresBeforeProbe);
            }
            finally
            {
                firstRecipientGate.Release();
            }

            var results = await Task.WhenAll(
                firstGeneration.WaitAsync(TimeSpan.FromSeconds(15)),
                secondGeneration.WaitAsync(TimeSpan.FromSeconds(15)));
            var firstResult = results[0];
            var secondResult = results[1];
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, firstResult.Outcome);
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, secondResult.Outcome);
            await AssertClaimsSucceededWithoutExpiryAsync(database, graph.Tenant, [firstClaim, secondClaim]);

            await using var verification = CreateTenantContext(database, graph.Tenant);
            var jobIds = new[] { firstJob.Id, secondJob.Id };
            var completedSecondJob = await verification.TaskDeadlineDigestJobs.AsNoTracking()
                .SingleAsync(job => job.Id == secondJob.Id);
            var completedSecondAttempts = await verification.TaskDeadlineDigestAttempts.AsNoTracking()
                .Where(attempt => attempt.JobId == secondJob.Id)
                .ToListAsync();
            var allAttempts = await verification.TaskDeadlineDigestAttempts.AsNoTracking()
                .Where(attempt => Enumerable.Contains(jobIds, attempt.JobId))
                .ToListAsync();
            var notifications = await verification.Notifications.AsNoTracking()
                .OrderBy(notification => notification.StateVersion)
                .ToListAsync();

            observation = new SameRecipientQueuedClaimLeaseObservation(
                secondClaim,
                expiryProbeClaimCounts,
                firstRecipientUserLockWasHeld,
                secondFence.JobFenceWasAcquired,
                secondFence.AttemptFenceWasAcquired,
                secondFence.UserLockWasRequested,
                secondGenerationCompletedBeforeFirstRelease,
                secondClaimExpiresBeforeProbe,
                secondJobStatusDuringProbe,
                secondAttemptStatusDuringProbe,
                secondClaimTokenDuringProbe,
                secondAttemptCountDuringProbe,
                secondAutomaticAttemptCountDuringProbe,
                secondAttemptRowCountDuringProbe,
                expiredAttemptCountDuringProbe,
                firstResult,
                secondResult,
                await verification.Notifications.CountAsync(),
                await verification.OutboxEvents.CountAsync(),
                notifications.Select(notification => notification.StateVersion).ToArray(),
                completedSecondJob.AttemptCount,
                completedSecondJob.AutomaticAttemptCount,
                completedSecondAttempts.Count,
                allAttempts.Count(attempt => attempt.Status == TaskDeadlineDigestAttemptStatus.Expired),
                results.Count(result => result.Outcome == TaskDeadlineDigestGenerationOutcome.ClaimLost));
        });

        return observation ?? throw new InvalidOperationException(
            "The same-recipient lease scenario did not produce an observation.");
    }

    private static async Task AssertClaimsSucceededWithoutExpiryAsync(
        string database,
        Tenant tenant,
        IReadOnlyCollection<TaskDeadlineDigestClaim> claims)
    {
        var jobIds = claims.Select(claim => claim.JobId).ToArray();
        await using var verification = CreateTenantContext(database, tenant);
        var jobs = await verification.TaskDeadlineDigestJobs.AsNoTracking()
            .Where(job => Enumerable.Contains(jobIds, job.Id))
            .ToListAsync();
        var attempts = await verification.TaskDeadlineDigestAttempts.AsNoTracking()
            .Where(attempt => Enumerable.Contains(jobIds, attempt.JobId))
            .ToListAsync();

        Assert.Equal(claims.Count, jobs.Count);
        Assert.Equal(claims.Count, attempts.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(TaskDeadlineDigestJobStatus.Succeeded, job.Status);
            Assert.Equal(1, job.AttemptCount);
            Assert.Equal(1, job.AutomaticAttemptCount);
            Assert.Null(job.ClaimToken);
            Assert.Null(job.ClaimExpiresAt);
        });
        Assert.All(attempts, attempt =>
        {
            Assert.Equal(TaskDeadlineDigestAttemptStatus.Succeeded, attempt.Status);
            Assert.Null(attempt.ClaimToken);
            Assert.Null(attempt.ClaimExpiresAt);
        });
        Assert.DoesNotContain(attempts, attempt => attempt.Status == TaskDeadlineDigestAttemptStatus.Expired);
    }

    private static async Task AssertFeatureDisableIsFencedAsync(
        string database,
        Graph graph,
        Func<AppDbContext, FeatureFlagSources, Task> mutateAsync,
        FeatureFlagSources sources)
    {
        var job = await AddJobAsync(database, graph);
        var claim = Assert.Single(await ClaimDueAsync(
            database,
            graph.Tenant,
            "feature-disable-fence",
            batchSize: 1,
            claimTimeout: TimeSpan.FromMinutes(2)));
        Assert.Equal(job.Id, claim.JobId);

        var gate = new FinalCandidateCommitGate();
        var generation = GenerateClaimAsync(
            database,
            graph.Tenant,
            claim,
            gate,
            usePersistedFeatureFlags: true);
        await gate.WaitForArrivalAsync();
        bool mutationCommittedBeforeGeneration;
        try
        {
            mutationCommittedBeforeGeneration = await TryMutateWithLockTimeoutAsync(
                database,
                graph,
                db => mutateAsync(db, sources));
        }
        finally
        {
            gate.Release();
        }

        if (mutationCommittedBeforeGeneration)
        {
            await AssertFeatureDisableFirstCannotLeaveVisibleDigestAsync(generation, database, graph);
            return;
        }

        var result = await generation.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.NotificationId);
        await AssertVisibleDigestArtifactsAsync(database, graph, result.NotificationId.Value);
        await CommitMutationAsync(database, graph, db => mutateAsync(db, sources));
    }

    private static async Task AssertFeatureDisableFirstCannotLeaveVisibleDigestAsync(
        Task<TaskDeadlineDigestGenerationResult> generation,
        string database,
        Graph graph)
    {
        try
        {
            var result = await generation.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(TaskDeadlineDigestGenerationOutcome.FeatureDisabled, result.Outcome);
            Assert.Null(result.NotificationId);
        }
        catch (TaskDeadlineDigestRetryablePersistenceConflictException)
        {
            // A bounded safe retry is acceptable only when it leaves no
            // Notification, UserState, or Outbox artifact behind.
        }

        await using var verification = CreateTenantContext(database, graph.Tenant);
        Assert.Empty(await verification.Notifications.AsNoTracking().ToListAsync());
        Assert.Empty(await verification.NotificationUserStates.AsNoTracking().ToListAsync());
        Assert.Empty(await verification.OutboxEvents.AsNoTracking().ToListAsync());
        Assert.Null((await verification.TaskDeadlineDigestJobs.AsNoTracking().SingleAsync()).NotificationId);
    }

    private static async Task<FeatureFlagSources> EnablePersistedNotificationsFeatureAsync(
        string database,
        Graph graph,
        bool includeTenantSettings = true)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var enabledPlan = new Plan
        {
            Name = $"PR07-C enabled digest {suffix}",
            Status = PlanStatus.Active,
            EnabledFeaturesJson = JsonSerializer.Serialize(new[] { FeatureKeys.TasksNotificationsV1 })
        };
        var disabledPlan = new Plan
        {
            Name = $"PR07-C disabled digest {suffix}",
            Status = PlanStatus.Active,
            EnabledFeaturesJson = "[]"
        };
        var subscription = new Subscription
        {
            TenantId = graph.Tenant.Id,
            PlanId = enabledPlan.Id,
            Status = SubscriptionStatus.Active,
            StartedAt = Now.AddDays(-1)
        };
        var settings = includeTenantSettings
            ? new TenantSettings
            {
                TenantId = graph.Tenant.Id,
                DisplayName = graph.Tenant.DisplayName,
                FeatureFlagsJson = "{}"
            }
            : null;

        await using var db = CreateTenantContext(database, graph.Tenant);
        db.AddRange(enabledPlan, disabledPlan, subscription);
        if (settings is not null)
            db.TenantSettings.Add(settings);
        await db.SaveChangesAsync();

        return new FeatureFlagSources(enabledPlan, disabledPlan, subscription, settings);
    }

    private static Task<TaskDeadlineDigestJob> AddJobAsync(string database, Graph graph) =>
        AddJobAsync(database, graph.Tenant, graph.Workspace, graph.Recipient, LocalDate);

    private static async Task<TaskDeadlineDigestJob> AddJobAsync(
        string database,
        Tenant tenant,
        Workspace workspace,
        User recipient,
        DateOnly localDate,
        DateTimeOffset? scheduledForUtc = null)
    {
        await using var db = CreateTenantContext(database, tenant);
        var scheduledFor = scheduledForUtc ?? Now.AddMinutes(-30);
        var job = new TaskDeadlineDigestJob
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            UserId = recipient.Id,
            LocalDate = localDate,
            PolicyVersion = TaskDeadlineDigestPolicy.PolicyVersion,
            Status = TaskDeadlineDigestJobStatus.Pending,
            ScheduledForUtc = scheduledFor,
            NextAttemptAt = scheduledFor
        };
        db.TaskDeadlineDigestJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private static Task<TaskItem> AddQualifyingTaskAsync(string database, Graph graph) =>
        AddQualifyingTaskAsync(
            database,
            graph.Tenant,
            graph.Actor,
            graph.Workspace,
            graph.Project,
            graph.Recipient,
            "restricted digest task title",
            deadlineMinute: 1);

    private static async Task<TaskItem> AddQualifyingTaskAsync(
        string database,
        Tenant tenant,
        User actor,
        Workspace workspace,
        Project project,
        User recipient,
        string title,
        int deadlineMinute)
    {
        await using var db = CreateTenantContext(database, tenant);
        var task = new TaskItem
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Title = title,
            Status = TaskItemStatus.InProgress,
            Kind = WorkItemKind.Task,
            DeadlineAt = Now.AddMinutes(deadlineMinute),
            PrimaryAssigneeUserId = recipient.Id,
            CreatedByUserId = actor.Id,
            VersionNo = 1
        };
        db.TaskItems.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private static async Task<User> AddUserAsync(string database, string role)
    {
        var user = UserFor(role, Guid.NewGuid().ToString("N"));
        await using var platform = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
        platform.Users.Add(user);
        await platform.SaveChangesAsync();
        return user;
    }

    private static async Task<User> AddRecipientToWorkspaceAsync(
        string database,
        Graph graph,
        string role)
    {
        var recipient = await AddUserAsync(database, role);
        await using var db = CreateTenantContext(database, graph.Tenant);
        db.AddRange(
            new TenantUser
            {
                TenantId = graph.Tenant.Id,
                UserId = recipient.Id,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = Now
            },
            new WorkspaceMember
            {
                TenantId = graph.Tenant.Id,
                WorkspaceId = graph.Workspace.Id,
                UserId = recipient.Id,
                Role = WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = Now
            },
            new ProjectMember
            {
                TenantId = graph.Tenant.Id,
                ProjectId = graph.Project.Id,
                UserId = recipient.Id,
                Role = ProjectRole.Contributor,
                JoinedAt = Now
            });
        await db.SaveChangesAsync();
        return recipient;
    }

    private static async Task<WorkspaceGraph> AddWorkspaceForRecipientAsync(
        string database,
        Graph graph,
        User recipient)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workspace = new Workspace
        {
            TenantId = graph.Tenant.Id,
            Name = "PR07-C concurrent workspace",
            Slug = $"pr07c-concurrent-workspace-{suffix}",
            TimeZone = "UTC",
            DefaultTaskDeadlineDigestLocalTime = new TimeOnly(4, 0),
            Status = WorkspaceStatus.Active,
            CreatedByUserId = graph.Actor.Id
        };
        var project = new Project
        {
            TenantId = graph.Tenant.Id,
            WorkspaceId = workspace.Id,
            OwnerUserId = graph.Actor.Id,
            CreatedByUserId = graph.Actor.Id,
            Name = "PR07-C concurrent project",
            Slug = $"pr07c-concurrent-project-{suffix}",
            Status = ProjectStatus.Active,
            VersionNo = 1
        };
        var task = new TaskItem
        {
            TenantId = graph.Tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Title = "concurrent workspace digest task",
            Status = TaskItemStatus.InProgress,
            Kind = WorkItemKind.Task,
            DeadlineAt = Now.AddMinutes(1),
            PrimaryAssigneeUserId = recipient.Id,
            CreatedByUserId = graph.Actor.Id,
            VersionNo = 1
        };

        await using var db = CreateTenantContext(database, graph.Tenant);
        var isExistingTenantUser = await db.TenantUsers.AnyAsync(member =>
            member.TenantId == graph.Tenant.Id && member.UserId == recipient.Id);
        db.AddRange(
            workspace,
            project,
            task,
            new WorkspaceMember
            {
                TenantId = graph.Tenant.Id,
                WorkspaceId = workspace.Id,
                UserId = graph.Actor.Id,
                Role = WorkspaceRole.Owner,
                Status = MembershipStatus.Active,
                JoinedAt = Now
            },
            new WorkspaceMember
            {
                TenantId = graph.Tenant.Id,
                WorkspaceId = workspace.Id,
                UserId = recipient.Id,
                Role = WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = Now
            },
            new ProjectMember
            {
                TenantId = graph.Tenant.Id,
                ProjectId = project.Id,
                UserId = recipient.Id,
                Role = ProjectRole.Contributor,
                JoinedAt = Now
            });
        if (!isExistingTenantUser)
        {
            db.TenantUsers.Add(new TenantUser
            {
                TenantId = graph.Tenant.Id,
                UserId = recipient.Id,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = Now
            });
        }

        await db.SaveChangesAsync();
        return new WorkspaceGraph(workspace);
    }

    private static async Task AddCategoryTasksAsync(string database, Graph graph)
    {
        await using var db = CreateTenantContext(database, graph.Tenant);
        var overdue = NewTask(graph, "restricted overdue title", 1);
        overdue.DeadlineAt = Now.AddMinutes(-1);
        var today = NewTask(graph, "restricted today title", 2);
        today.DeadlineAt = new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        var oneDay = NewTask(graph, "restricted one-day title", 3);
        oneDay.DeadlineAt = new DateTimeOffset(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);
        var threeDays = NewTask(graph, "restricted three-day title", 4);
        threeDays.DeadlineAt = new DateTimeOffset(2026, 8, 6, 18, 0, 0, TimeSpan.Zero);
        foreach (var task in new[] { overdue, today, oneDay, threeDays })
            task.PrimaryAssigneeUserId = graph.Recipient.Id;
        db.AddRange(overdue, today, oneDay, threeDays);
        await db.SaveChangesAsync();
    }

    private static async Task<WorkspaceGraph> AddWorkspaceAsync(
        string database,
        Graph graph,
        string timeZoneId,
        TimeOnly digestLocalTime)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workspace = new Workspace
        {
            TenantId = graph.Tenant.Id,
            Name = "PR07-C Pacific Workspace",
            Slug = $"pr07c-pacific-{suffix}",
            TimeZone = timeZoneId,
            DefaultTaskDeadlineDigestLocalTime = digestLocalTime,
            Status = WorkspaceStatus.Active,
            CreatedByUserId = graph.Actor.Id
        };
        var project = new Project
        {
            TenantId = graph.Tenant.Id,
            WorkspaceId = workspace.Id,
            OwnerUserId = graph.Actor.Id,
            CreatedByUserId = graph.Actor.Id,
            Name = "PR07-C Pacific Project",
            Slug = $"pr07c-pacific-project-{suffix}",
            Status = ProjectStatus.Active,
            VersionNo = 1
        };
        var task = new TaskItem
        {
            TenantId = graph.Tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Title = "restricted Pacific task title",
            Status = TaskItemStatus.InProgress,
            Kind = WorkItemKind.Task,
            DeadlineAt = Now.AddMinutes(1),
            PrimaryAssigneeUserId = graph.Recipient.Id,
            CreatedByUserId = graph.Actor.Id,
            VersionNo = 1
        };

        await using var db = CreateTenantContext(database, graph.Tenant);
        db.AddRange(
            workspace,
            project,
            task,
            new WorkspaceMember
            {
                TenantId = graph.Tenant.Id,
                WorkspaceId = workspace.Id,
                UserId = graph.Actor.Id,
                Role = WorkspaceRole.Owner,
                Status = MembershipStatus.Active,
                JoinedAt = Now
            },
            new WorkspaceMember
            {
                TenantId = graph.Tenant.Id,
                WorkspaceId = workspace.Id,
                UserId = graph.Recipient.Id,
                Role = WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = Now
            },
            new ProjectMember
            {
                TenantId = graph.Tenant.Id,
                ProjectId = project.Id,
                UserId = graph.Recipient.Id,
                Role = ProjectRole.Contributor,
                JoinedAt = Now
            });
        await db.SaveChangesAsync();
        return new WorkspaceGraph(workspace);
    }

    private static TaskItem NewTask(
        Graph graph,
        string title,
        int deadlineMinute,
        Project? project = null) =>
        new()
        {
            TenantId = graph.Tenant.Id,
            WorkspaceId = graph.Workspace.Id,
            ProjectId = (project ?? graph.Project).Id,
            Title = title,
            Status = TaskItemStatus.InProgress,
            Kind = WorkItemKind.Task,
            DeadlineAt = Now.AddMinutes(deadlineMinute),
            VersionNo = 1,
            CreatedByUserId = graph.Actor.Id
        };

    private static Project NewProject(Graph graph, string name) => new()
    {
        TenantId = graph.Tenant.Id,
        WorkspaceId = graph.Workspace.Id,
        OwnerUserId = graph.Actor.Id,
        CreatedByUserId = graph.Actor.Id,
        Name = $"PR07-C {name}",
        Slug = $"pr07c-{name}-{Guid.NewGuid():N}",
        Status = ProjectStatus.Active,
        VersionNo = 1
    };

    private static async Task<Graph> SeedGraphAsync(string database)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new Tenant
        {
            Name = $"PR07-C candidate atomicity {suffix}",
            DisplayName = "PR07-C candidate atomicity",
            Slug = $"pr07c-candidate-{suffix}",
            Status = TenantStatus.Active
        };
        var actor = UserFor("actor", suffix);
        var recipient = UserFor("recipient", suffix);

        await using (var platform = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database))
        {
            platform.AddRange(tenant, actor, recipient);
            await platform.SaveChangesAsync();
        }

        var workspace = new Workspace
        {
            TenantId = tenant.Id,
            Name = "PR07-C Workspace",
            Slug = $"pr07c-workspace-{suffix}",
            TimeZone = "UTC",
            DefaultTaskDeadlineDigestLocalTime = new TimeOnly(4, 0),
            Status = WorkspaceStatus.Active,
            CreatedByUserId = actor.Id
        };
        var project = new Project
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            OwnerUserId = actor.Id,
            CreatedByUserId = actor.Id,
            Name = "PR07-C Project",
            Slug = $"pr07c-project-{suffix}",
            Status = ProjectStatus.Active,
            VersionNo = 1
        };
        var team = new Group
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            Name = "PR07-C Team Queue",
            Slug = $"pr07c-team-{suffix}",
            GroupType = GroupType.Team,
            Status = GroupStatus.Active,
            CreatedByUserId = actor.Id
        };

        await using (var db = CreateTenantContext(database, tenant))
        {
            db.AddRange(
                workspace,
                project,
                team,
                new TenantUser
                {
                    TenantId = tenant.Id,
                    UserId = actor.Id,
                    Role = TenantUserRole.Member,
                    Status = TenantUserStatus.Active,
                    JoinedAt = Now
                },
                new TenantUser
                {
                    TenantId = tenant.Id,
                    UserId = recipient.Id,
                    Role = TenantUserRole.Member,
                    Status = TenantUserStatus.Active,
                    JoinedAt = Now
                },
                new WorkspaceMember
                {
                    TenantId = tenant.Id,
                    WorkspaceId = workspace.Id,
                    UserId = actor.Id,
                    Role = WorkspaceRole.Owner,
                    Status = MembershipStatus.Active,
                    JoinedAt = Now
                },
                new WorkspaceMember
                {
                    TenantId = tenant.Id,
                    WorkspaceId = workspace.Id,
                    UserId = recipient.Id,
                    Role = WorkspaceRole.Member,
                    Status = MembershipStatus.Active,
                    JoinedAt = Now
                },
                new ProjectMember
                {
                    TenantId = tenant.Id,
                    ProjectId = project.Id,
                    UserId = actor.Id,
                    Role = ProjectRole.Owner,
                    JoinedAt = Now
                },
                new ProjectMember
                {
                    TenantId = tenant.Id,
                    ProjectId = project.Id,
                    UserId = recipient.Id,
                    Role = ProjectRole.Contributor,
                    JoinedAt = Now
                },
                new GroupMember
                {
                    TenantId = tenant.Id,
                    GroupId = team.Id,
                    UserId = recipient.Id,
                    Role = GroupRole.Member,
                    JoinedAt = Now
                });
            await db.SaveChangesAsync();
        }

        return new Graph(tenant, workspace, project, team, actor, recipient);
    }

    private static User UserFor(string role, string suffix) => new()
    {
        DisplayName = $"PR07-C {role}",
        Email = $"pr07c-{role}-{suffix}@example.test",
        NormalizedEmail = $"PR07C-{role}-{suffix}@EXAMPLE.TEST",
        PasswordHash = "hash",
        Status = UserStatus.Active
    };

    private static TenantUser ActiveTenantUser(Tenant tenant, User user) => new()
    {
        TenantId = tenant.Id,
        UserId = user.Id,
        Role = TenantUserRole.Member,
        Status = TenantUserStatus.Active,
        JoinedAt = Now
    };

    private static WorkspaceMember ActiveWorkspaceMember(
        Graph graph,
        User user,
        WorkspaceRole role) => new()
    {
        TenantId = graph.Tenant.Id,
        WorkspaceId = graph.Workspace.Id,
        UserId = user.Id,
        Role = role,
        Status = MembershipStatus.Active,
        JoinedAt = Now
    };

    private static AppDbContext CreateTenantContext(
        string database,
        Tenant tenant,
        CurrentTenantService? currentTenant = null,
        IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database);
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new AppDbContext(options.Options, currentTenant ?? TenantScope(tenant));
    }

    private static CurrentTenantService TenantScope(Tenant tenant)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        return currentTenant;
    }

    private sealed record Graph(
        Tenant Tenant,
        Workspace Workspace,
        Project Project,
        Group Team,
        User Actor,
        User Recipient);

    private sealed record WorkspaceGraph(Workspace Workspace);

    private sealed record FeatureFlagSources(
        Plan EnabledPlan,
        Plan DisabledPlan,
        Subscription Subscription,
        TenantSettings? Settings);

    private sealed record SameRecipientQueuedClaimLeaseObservation(
        TaskDeadlineDigestClaim SecondClaim,
        IReadOnlyList<int> ExpiryProbeClaimCounts,
        bool FirstRecipientUserLockWasHeld,
        bool SecondJobFenceWasAcquired,
        bool SecondAttemptFenceWasAcquired,
        bool SecondUserLockWasRequested,
        bool SecondGenerationCompletedBeforeFirstRelease,
        bool SecondClaimExpiresBeforeProbe,
        TaskDeadlineDigestJobStatus SecondJobStatusDuringProbe,
        TaskDeadlineDigestAttemptStatus SecondAttemptStatusDuringProbe,
        Guid? SecondClaimTokenDuringProbe,
        int SecondAttemptCountDuringProbe,
        int SecondAutomaticAttemptCountDuringProbe,
        int SecondAttemptRowCountDuringProbe,
        int ExpiredAttemptCountDuringProbe,
        TaskDeadlineDigestGenerationResult FirstResult,
        TaskDeadlineDigestGenerationResult SecondResult,
        int NotificationCount,
        int OutboxCount,
        IReadOnlyList<long> NotificationStateVersions,
        int SecondAttemptCountAfterCompletion,
        int SecondAutomaticAttemptCountAfterCompletion,
        int SecondAttemptRowCountAfterCompletion,
        int ExpiredAttemptCountAfterCompletion,
        int ClaimLostCount);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class EnabledFeatureFlags : IFeatureFlagService
    {
        public static EnabledFeatureFlags Instance { get; } = new();

        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(
                FeatureKeys.Normalize(featureKey),
                FeatureKeys.TasksNotificationsV1,
                StringComparison.Ordinal));

        public async Task<Result> RequireEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default) =>
            await IsEnabledAsync(featureKey, cancellationToken)
                ? Result.Success()
                : Result.Failure($"Feature '{featureKey}' is disabled.");

        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([FeatureKeys.TasksNotificationsV1]);
    }

    private sealed class ThrowAfterSaveInterceptor : SaveChangesInterceptor
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                throw new InjectedSaveFailureException();

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class UserLockArrivalInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForArrivalAsync() =>
            _arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT 1 FROM users", StringComparison.Ordinal) &&
                command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                _arrived.TrySetResult();
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Holds the first generator after its non-empty candidate page has
    /// acquired the deterministic <c>task_items FOR SHARE</c> fence. The
    /// gate observes actual PostgreSQL command completion, so a second
    /// generator or mutation that completes while it is held has crossed the
    /// candidate/phantom fence rather than merely being scheduled by
    /// <c>Task.WhenAll</c>.
    /// </summary>
    private sealed class CandidateFenceGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _candidateFenceCount;
        private int _holding;

        public bool IsHolding => Volatile.Read(ref _holding) == 1;

        public Task WaitForArrivalAsync() =>
            _arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsCandidateTaskFence(command) && Interlocked.Increment(ref _candidateFenceCount) == 1)
            {
                Volatile.Write(ref _holding, 1);
                _arrived.TrySetResult();
                try
                {
                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                }
                finally
                {
                    Volatile.Write(ref _holding, 0);
                }
            }

            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Holds the first generator only after PostgreSQL has completed the
    /// recipient User <c>FOR UPDATE</c> command. Claim-first generation has
    /// exactly one Job/Attempt acquisition at transaction start, so this gate
    /// deliberately does not rely on a second Attempt lock that no longer
    /// exists. A second generator completing while this is held has crossed
    /// the real recipient fence rather than merely being scheduled by
    /// <c>Task.WhenAll</c>.
    /// </summary>
    private sealed class RecipientUserLockGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _userLockCount;
        private int _holding;

        public bool IsHolding => Volatile.Read(ref _holding) == 1;

        public Task WaitForRecipientUserLockAsync() =>
            _arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsRecipientUserLock(command) && Interlocked.Increment(ref _userLockCount) == 1)
            {
                Volatile.Write(ref _holding, 1);
                _arrived.TrySetResult();
                try
                {
                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                }
                finally
                {
                    Volatile.Write(ref _holding, 0);
                }
            }

            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Records B's transaction-start claim fence after its Job and Attempt
    /// commands have completed, then records its subsequent request for the
    /// same recipient User lock. The request interceptor runs before the
    /// command can block, which lets the test run an expiry scanner while B is
    /// demonstrably waiting with both claim rows protected.
    /// </summary>
    private sealed class QueuedSameRecipientClaimProbe : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _userLockRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _jobFenceWasAcquired;
        private int _attemptFenceWasAcquired;
        private int _userLockWasRequested;

        public bool JobFenceWasAcquired => Volatile.Read(ref _jobFenceWasAcquired) == 1;
        public bool AttemptFenceWasAcquired => Volatile.Read(ref _attemptFenceWasAcquired) == 1;
        public bool UserLockWasRequested => Volatile.Read(ref _userLockWasRequested) == 1;

        public Task WaitForUserLockRequestAsync() =>
            _userLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsClaimJobLock(command))
                Interlocked.Exchange(ref _jobFenceWasAcquired, 1);
            if (IsClaimAttemptLock(command))
                Interlocked.Exchange(ref _attemptFenceWasAcquired, 1);

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsRecipientUserLock(command))
            {
                Interlocked.Exchange(ref _userLockWasRequested, 1);
                if (!JobFenceWasAcquired || !AttemptFenceWasAcquired)
                {
                    _userLockRequested.TrySetException(new InvalidOperationException(
                        "The queued generator requested the recipient User lock before its Job/Attempt claim fence completed."));
                }
                else
                {
                    _userLockRequested.TrySetResult();
                }
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static bool IsClaimJobLock(DbCommand command) =>
        command.CommandText.Contains("FROM task_deadline_digest_jobs", StringComparison.OrdinalIgnoreCase) &&
        command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaimAttemptLock(DbCommand command) =>
        command.CommandText.Contains("FROM task_deadline_digest_attempts", StringComparison.OrdinalIgnoreCase) &&
        command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);

    private static bool IsCandidateTaskFence(DbCommand command) =>
        command.CommandText.Contains("SELECT 1 FROM task_items", StringComparison.OrdinalIgnoreCase) &&
        command.CommandText.Contains("FOR SHARE", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecipientUserLock(DbCommand command) =>
        command.CommandText.Contains("FROM users", StringComparison.OrdinalIgnoreCase) &&
        command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);

    private sealed class FinalCandidateCommitGate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForArrivalAsync() =>
            _arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _arrived.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private sealed class InjectedSaveFailureException : Exception;
}
