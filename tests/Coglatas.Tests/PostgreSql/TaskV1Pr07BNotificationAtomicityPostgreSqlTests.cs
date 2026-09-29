using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Groups;
using Coglatas.Application.Messaging;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.PostgreSql;

/// <summary>
/// Real-provider evidence that the PR07-B Task producer only becomes visible
/// through the Task command's single PostgreSQL save boundary. These tests use
/// the production command, recipient policy, notification service, audit
/// logger, semantic publisher, transactional Outbox, and EF unit of work.
/// </summary>
[Collection("PostgreSqlTaskV1")]
public sealed class TaskV1Pr07BNotificationAtomicityPostgreSqlTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task AssigneeMutationCommitsRelationshipNotificationSignalsAuditAndSemanticEventsAtomically()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var before = await harness.SnapshotAsync();

        await using (var request = harness.CreateScope())
        {
            var result = await request.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, before.TaskVersion));

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(harness.Graph.Recipient.Id, result.Value!.Task.PrimaryAssignee?.UserId);
            Assert.Equal(before.TaskVersion + 1, result.Value.Task.Version);
        }

        var after = await harness.SnapshotAsync();
        Assert.Equal(before.TaskVersion + 1, after.TaskVersion);
        Assert.Equal(harness.Graph.Recipient.Id, after.PrimaryAssigneeUserId);
        Assert.Equal(before.WatchStateCount + 2, after.WatchStateCount);
        Assert.Equal(before.NotificationCount + 1, after.NotificationCount);
        Assert.Equal(before.NotificationUserStateCount + 1, after.NotificationUserStateCount);
        Assert.Equal(before.AuditCount + 1, after.AuditCount);
        Assert.Equal(before.OutboxCount + 3, after.OutboxCount);

        var notifications = await harness.LoadNotificationsAsync();
        var notification = Assert.Single(notifications);
        Assert.Equal(harness.Graph.Recipient.Id, notification.UserId);
        Assert.Equal(NotificationType.TaskAssigned, notification.NotificationType);
        Assert.Null(notification.Body);
        Assert.Equal("TaskItem", notification.RelatedEntityType);
        Assert.Equal(harness.Graph.Task.Id, notification.RelatedEntityId);
        Assert.Equal(
            $"task:{harness.Graph.Task.Id:N}:event:TaskAssignmentChanged:version:{after.TaskVersion}",
            notification.LogicalKey);

        var audit = Assert.Single(await harness.LoadAuditLogsAsync());
        Assert.Equal("TaskAssigneeChanged", audit.Action);
        Assert.Equal("TaskItem", audit.EntityType);
        Assert.Equal(harness.Graph.Task.Id, audit.EntityId);

        var outbox = await harness.LoadOutboxAsync();
        Assert.Equal(
            [
                "Notifications.NotificationCreated.v1",
                "Projects.TaskAssignmentChanged.v1",
                "Projects.TaskChanged.v1"
            ],
            outbox.Select(item => item.EventType).Order().ToArray());

        var notificationSignal = outbox.Single(item => item.EventType == "Notifications.NotificationCreated.v1");
        using var envelope = JsonDocument.Parse(notificationSignal.PayloadJson);
        var payload = envelope.RootElement.GetProperty("payload");
        Assert.Equal(
            ["notificationId", "requiresRefetch", "stateVersion"],
            payload.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(notification.Id, payload.GetProperty("notificationId").GetGuid());
        Assert.True(payload.GetProperty("requiresRefetch").GetBoolean());

        var durablePayload = string.Join('\n', outbox.Select(item => item.PayloadJson));
        Assert.DoesNotContain(harness.Graph.Task.Title, durablePayload, StringComparison.Ordinal);
        Assert.DoesNotContain("body", notificationSignal.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("route", notificationSignal.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("watch", durablePayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipient", durablePayload, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task DeadlineWithNonUtcOffsetPersistsAsTheSameUtcInstant()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var before = await harness.SnapshotAsync();
        var requestedDeadline = new DateTimeOffset(
            2026,
            8,
            3,
            0,
            15,
            0,
            TimeSpan.FromHours(9));

        await using (var request = harness.CreateScope())
        {
            var result = await request.Commands.UpdateDetailsAsync(
                harness.Graph.Task.Id,
                new TaskUpdateDetailsRequest(
                    harness.Graph.Task.Title,
                    null,
                    TaskPriority.Medium,
                    null,
                    null,
                    0,
                    before.TaskVersion,
                    new OptionalDateTimeOffset(true, requestedDeadline)));

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(requestedDeadline.ToUniversalTime(), result.Value!.DeadlineAt);
            Assert.Equal(TimeSpan.Zero, result.Value.DeadlineAt!.Value.Offset);
        }

        var persistedDeadline = await harness.LoadDeadlineAsync();
        Assert.Equal(requestedDeadline.ToUniversalTime(), persistedDeadline);
        Assert.Equal(TimeSpan.Zero, persistedDeadline!.Value.Offset);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task StaleExpectedVersionProducesNoTaskNotificationOutboxOrAuditDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var before = await harness.SnapshotAsync();

        await using (var request = harness.CreateScope())
        {
            var result = await request.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, before.TaskVersion + 1));

            Assert.False(result.IsSuccess);
            Assert.Equal("TASK_STALE_VERSION", ErrorCode(result.Error));
        }

        Assert.Equal(before, await harness.SnapshotAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task AuthorizationDenialProducesNoTaskNotificationOutboxOrAuditDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var before = await harness.SnapshotAsync();

        await using (var request = harness.CreateScope(harness.Graph.Outsider.Id))
        {
            var result = await request.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, before.TaskVersion));

            Assert.False(result.IsSuccess);
            Assert.Equal("TASK_NOT_FOUND", ErrorCode(result.Error));
        }

        Assert.Equal(before, await harness.SnapshotAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task TaskAuditOrOutboxDatabaseFailureRollsBackTheWholeTaskMutation()
    {
        foreach (var failureTarget in new[]
                 {
                     SaveFailureTarget.Task,
                     SaveFailureTarget.Audit,
                     SaveFailureTarget.Outbox
                 })
        {
            await using var harness = await ServiceHarness.CreateAsync(failureTarget: failureTarget);
            var before = await harness.SnapshotAsync();

            await using (var request = harness.CreateScope())
            {
                var exception = await Record.ExceptionAsync(() => request.Commands.SetAssigneeAsync(
                    harness.Graph.Task.Id,
                    new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, before.TaskVersion)));

                Assert.IsType<DbUpdateException>(exception);
            }

            Assert.Equal(before, await harness.SnapshotAsync());
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityAssignmentTaskAuditOrOutboxFailureRollsBackTheWholeMutation()
    {
        foreach (var failureTarget in new[]
                 {
                     SaveFailureTarget.Task,
                     SaveFailureTarget.Audit,
                     SaveFailureTarget.Outbox
                 })
        {
            await using var harness = await ServiceHarness.CreateAsync(failureTarget: failureTarget);
            var before = await harness.SnapshotAsync();

            await using (var request = harness.CreateScope())
            {
                var exception = await Record.ExceptionAsync(() => request.Compatibility.AddAssignmentAsync(
                    harness.Graph.Task.Id,
                    new AddTaskAssignmentRequest(
                        harness.Graph.Recipient.Id,
                        TaskAssignmentRole.Assignee,
                        1)));

                Assert.IsType<DbUpdateException>(exception);
            }

            Assert.Equal(before, await harness.SnapshotAsync());
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ConcurrentSameAssignmentAndLogicalRetryLeaveOneVisibleNotification()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var expectedVersion = harness.Graph.Task.VersionNo;
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();

        Assert.Equal(expectedVersion, (await first.Commands.GetAsync(harness.Graph.Task.Id)).Value!.Version);
        Assert.Equal(expectedVersion, (await second.Commands.GetAsync(harness.Graph.Task.Id)).Value!.Version);

        harness.SaveRace.Arm();
        var results = await Task.WhenAll(
            first.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, expectedVersion)),
            second.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, expectedVersion)));

        Assert.Single(results, result => result.IsSuccess);
        var loser = Assert.Single(results, result => !result.IsSuccess);
        Assert.Contains(ErrorCode(loser.Error), new[] { "TASK_STALE_VERSION", "TASK_CONFLICT" });

        var afterRace = await harness.SnapshotAsync();
        Assert.Equal(expectedVersion + 1, afterRace.TaskVersion);
        Assert.Equal(harness.Graph.Recipient.Id, afterRace.PrimaryAssigneeUserId);
        Assert.Equal(1, afterRace.NotificationCount);
        Assert.Equal(1, afterRace.NotificationUserStateCount);
        Assert.Equal(1, afterRace.AuditCount);
        Assert.Equal(3, afterRace.OutboxCount);
        Assert.Single(await harness.LoadNotificationsAsync());

        await using (var retry = harness.CreateScope())
        {
            var retried = await retry.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, afterRace.TaskVersion));
            Assert.True(retried.IsSuccess, retried.Error);
        }

        Assert.Equal(afterRace, await harness.SnapshotAsync());
        Assert.Single(await harness.LoadNotificationsAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ExistingImportantCommentNoOpPatchesDoNotMutateNotificationsOrOutbox()
    {
        await using var harness = await ServiceHarness.CreateAsync();

        await using (var assignment = harness.CreateScope())
        {
            var assigned = await assignment.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, harness.Graph.Task.VersionNo));
            Assert.True(assigned.IsSuccess, assigned.Error);
        }

        TaskCommentResponse comment;
        await using (var create = harness.CreateScope())
        {
            var created = await create.Subresources.CreateCommentAsync(
                harness.Graph.Task.Id,
                new CreateTaskCommentRequest("important comment", IsImportant: true));
            Assert.True(created.IsSuccess, created.Error);
            comment = created.Value!;
        }

        var existingImportant = Assert.Single(
            await harness.LoadNotificationsAsync(),
            notification => notification.LogicalKey?.Contains("TaskCommentSignificant", StringComparison.Ordinal) == true);
        Assert.Equal(harness.Graph.Recipient.Id, existingImportant.UserId);

        var before = await harness.SnapshotAsync();
        var commentBefore = await harness.LoadCommentSnapshotAsync(comment.Id);
        var stateVersionBefore = await harness.LoadNotificationUserStateVersionAsync(harness.Graph.Recipient.Id);

        await using (var sameValue = harness.CreateScope())
        {
            var result = await sameValue.Subresources.UpdateCommentAsync(
                comment.Id,
                new UpdateTaskCommentRequest(null, true, comment.Version));
            Assert.True(result.IsSuccess, result.Error);
        }

        Assert.Equal(before, await harness.SnapshotAsync());
        Assert.Equal(commentBefore, await harness.LoadCommentSnapshotAsync(comment.Id));
        Assert.Equal(stateVersionBefore, await harness.LoadNotificationUserStateVersionAsync(harness.Graph.Recipient.Id));

        await using (var emptyPatch = harness.CreateScope())
        {
            var result = await emptyPatch.Subresources.UpdateCommentAsync(
                comment.Id,
                new UpdateTaskCommentRequest(null, null, comment.Version));
            Assert.False(result.IsSuccess);
            Assert.Equal("TASK_COMMENT_UPDATE_REQUIRED", ErrorCode(result.Error));
        }

        Assert.Equal(before, await harness.SnapshotAsync());
        Assert.Equal(commentBefore, await harness.LoadCommentSnapshotAsync(comment.Id));
        Assert.Equal(stateVersionBefore, await harness.LoadNotificationUserStateVersionAsync(harness.Graph.Recipient.Id));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedCommentAuthorCannotUpdateAndLeavesNoPersistenceDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var comment = await harness.AddTaskCommentAsync(harness.Graph.Actor.Id, "original");
        var before = await harness.SnapshotAsync();
        var commentBefore = await harness.LoadCommentSnapshotAsync(comment.Id);
        await harness.SetWorkspaceMembershipStatusAsync(harness.Graph.Actor.Id, MembershipStatus.Suspended);

        await using (var request = harness.CreateScope())
        {
            var result = await request.Subresources.UpdateCommentAsync(
                comment.Id,
                new UpdateTaskCommentRequest("must not change", true, comment.VersionNo));

            Assert.False(result.IsSuccess);
            Assert.Equal("TASK_COMMENT_FORBIDDEN", ErrorCode(result.Error));
        }

        Assert.Equal(before, await harness.SnapshotAsync());
        Assert.Equal(commentBefore, await harness.LoadCommentSnapshotAsync(comment.Id));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RateLimitedImportantOnlyUpdateLeavesNoPersistenceDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync(new CommunicationSafetyOptions
        {
            MaxPostsPerMinutePerUser = 1,
            MaxPostsPerMinutePerConversation = 10
        });
        var first = await harness.AddTaskCommentAsync(harness.Graph.Actor.Id, "first");
        var second = await harness.AddTaskCommentAsync(harness.Graph.Actor.Id, "second");

        await using (var request = harness.CreateScope())
        {
            var result = await request.Subresources.UpdateCommentAsync(
                first.Id,
                new UpdateTaskCommentRequest(null, true, first.VersionNo));
            Assert.True(result.IsSuccess, result.Error);
        }

        var before = await harness.SnapshotAsync();
        var commentBefore = await harness.LoadCommentSnapshotAsync(second.Id);
        await using (var request = harness.CreateScope())
        {
            var result = await request.Subresources.UpdateCommentAsync(
                second.Id,
                new UpdateTaskCommentRequest(null, true, second.VersionNo));

            Assert.False(result.IsSuccess);
            Assert.Equal("TASK_COMMENT_RATE_LIMITED", result.ErrorDetail?.Code);
            Assert.True(result.ErrorDetail?.RetryAfterSeconds >= 1);
        }

        Assert.Equal(before, await harness.SnapshotAsync());
        Assert.Equal(commentBefore, await harness.LoadCommentSnapshotAsync(second.Id));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedWorkspaceMemberMentionLeavesNoPersistenceDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        await harness.SetWorkspaceMembershipStatusAsync(harness.Graph.Recipient.Id, MembershipStatus.Suspended);
        var before = await harness.SnapshotAsync();

        await using (var request = harness.CreateScope())
        {
            var result = await request.Subresources.CreateCommentAsync(
                harness.Graph.Task.Id,
                new CreateTaskCommentRequest($"@{{{harness.Graph.Recipient.Id:D}}}"));

            Assert.False(result.IsSuccess);
            Assert.Equal("TASK_MENTION_NOT_ELIGIBLE", ErrorCode(result.Error));
        }

        Assert.Equal(before, await harness.SnapshotAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedRelationshipTargetCannotBeAssignedAndLeavesNoPersistenceDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        await harness.SetWorkspaceMembershipStatusAsync(harness.Graph.Recipient.Id, MembershipStatus.Suspended);
        var before = await harness.SnapshotAsync();
        await using var request = harness.CreateScope();

        var result = await request.Commands.SetAssigneeAsync(
            harness.Graph.Task.Id,
            new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, before.TaskVersion));

        Assert.False(result.IsSuccess); Assert.Equal("TASK_FORBIDDEN", ErrorCode(result.Error));
        Assert.Equal(before, await harness.SnapshotAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedRelationshipTargetCannotBecomeReviewerAndLeavesNoPersistenceDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        await harness.SetWorkspaceMembershipStatusAsync(harness.Graph.Recipient.Id, MembershipStatus.Suspended);
        var before = await harness.SnapshotAsync();
        await using var request = harness.CreateScope();

        var result = await request.Commands.SetReviewerAsync(
            harness.Graph.Task.Id,
            new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, before.TaskVersion));

        Assert.False(result.IsSuccess); Assert.Equal("TASK_FORBIDDEN", ErrorCode(result.Error));
        Assert.Equal(before, await harness.SnapshotAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedRelationshipTargetCannotBecomeCollaboratorAndLeavesNoPersistenceDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        await harness.SetWorkspaceMembershipStatusAsync(harness.Graph.Recipient.Id, MembershipStatus.Suspended);
        var before = await harness.SnapshotAsync();
        await using var request = harness.CreateScope();

        var result = await request.Commands.AddCollaboratorAsync(
            harness.Graph.Task.Id,
            new TaskCollaboratorRequest(harness.Graph.Recipient.Id, before.TaskVersion));

        Assert.False(result.IsSuccess); Assert.Equal("TASK_FORBIDDEN", ErrorCode(result.Error));
        Assert.Equal(before, await harness.SnapshotAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityAssignmentRejectsRevokedTargetAndLeavesNoPersistenceDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        await harness.SetWorkspaceMembershipStatusAsync(harness.Graph.Recipient.Id, MembershipStatus.Suspended);
        var before = await harness.SnapshotAsync();
        await using var request = harness.CreateScope();

        var result = await request.Compatibility.AddAssignmentAsync(
            harness.Graph.Task.Id,
            new AddTaskAssignmentRequest(harness.Graph.Recipient.Id, TaskAssignmentRole.Assignee, 1));

        Assert.False(result.IsSuccess); Assert.Equal("TASK_FORBIDDEN", ErrorCode(result.Error));
        Assert.Equal(before, await harness.SnapshotAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityRoleChangeRejectsRevokedTargetAndLeavesNoPersistenceDelta()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        Guid assignmentId;
        await using (var setup = harness.CreateScope())
        {
            var created = await setup.Compatibility.AddAssignmentAsync(
                harness.Graph.Task.Id,
                new AddTaskAssignmentRequest(harness.Graph.Recipient.Id, TaskAssignmentRole.Assignee, 1));
            Assert.True(created.IsSuccess, created.Error);
            assignmentId = created.Value!.Id;
        }

        await harness.SetWorkspaceMembershipStatusAsync(harness.Graph.Recipient.Id, MembershipStatus.Suspended);
        var before = await harness.SnapshotAsync();
        await using var request = harness.CreateScope();
        var result = await request.Compatibility.UpdateAssignmentAsync(
            assignmentId,
            new UpdateTaskAssignmentRequest(TaskAssignmentRole.Reviewer, 2, 0));

        Assert.False(result.IsSuccess); Assert.Equal("TASK_FORBIDDEN", ErrorCode(result.Error));
        Assert.Equal(before, await harness.SnapshotAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedRelationshipCanStillBeRemovedAtomically()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        await using (var setup = harness.CreateScope())
        {
            var assigned = await setup.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(harness.Graph.Recipient.Id, harness.Graph.Task.VersionNo));
            Assert.True(assigned.IsSuccess, assigned.Error);
        }

        await harness.SetWorkspaceMembershipStatusAsync(harness.Graph.Recipient.Id, MembershipStatus.Suspended);
        var before = await harness.SnapshotAsync();
        await using (var request = harness.CreateScope())
        {
            var result = await request.Commands.SetAssigneeAsync(
                harness.Graph.Task.Id,
                new TaskRelationshipUserRequest(null, before.TaskVersion));
            Assert.True(result.IsSuccess, result.Error);
        }

        var after = await harness.SnapshotAsync();
        Assert.Null(after.PrimaryAssigneeUserId);
        Assert.Equal(before.TaskVersion + 1, after.TaskVersion);
        Assert.Equal(before.TaskAssignmentCount, after.TaskAssignmentCount);
        Assert.Equal(before.NotificationCount, after.NotificationCount);
        Assert.Equal(before.NotificationUserStateCount, after.NotificationUserStateCount);
        Assert.Equal(before.AuditCount + 1, after.AuditCount);
        Assert.True(after.OutboxCount > before.OutboxCount);
    }

    private static string? ErrorCode(string? error) => error?.Split('|', 2)[0];

    private enum SaveFailureTarget
    {
        None,
        Task,
        Audit,
        Outbox
    }

    private sealed record PersistenceSnapshot(
        long TaskVersion,
        Guid? PrimaryAssigneeUserId,
        int TaskAssignmentCount,
        int WatchStateCount,
        int NotificationCount,
        int NotificationUserStateCount,
        int AuditCount,
        int OutboxCount);

    private sealed record CommentSnapshot(string Body, long Version, DateTimeOffset? UpdatedAt, bool IsImportant);

    private sealed class ServiceHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _connectionString;
        private readonly ICommunicationSafetyGuard _safetyGuard;

        private ServiceHarness(ServiceProvider provider, string connectionString, Graph graph, SaveRaceCoordinator saveRace, ICommunicationSafetyGuard safetyGuard)
        {
            _provider = provider;
            _connectionString = connectionString;
            Graph = graph;
            SaveRace = saveRace;
            _safetyGuard = safetyGuard;
        }

        public Graph Graph { get; }
        public SaveRaceCoordinator SaveRace { get; }

        public static async Task<ServiceHarness> CreateAsync(
            CommunicationSafetyOptions? communicationSafetyOptions = null,
            SaveFailureTarget failureTarget = SaveFailureTarget.None)
        {
            var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
            var graph = await SeedAsync(connectionString);
            var saveRace = new SaveRaceCoordinator();
            var failure = new SaveFailureInterceptor(failureTarget);
            var safetyGuard = new InMemoryCommunicationSafetyGuard(communicationSafetyOptions ?? new CommunicationSafetyOptions());
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(connectionString).AddInterceptors(failure));
            services.AddScoped<CurrentTenantService>();
            services.AddScoped<ICurrentTenant>(serviceProvider => serviceProvider.GetRequiredService<CurrentTenantService>());
            services.AddScoped<TestCurrentUser>();
            services.AddScoped<ICurrentUser>(serviceProvider => serviceProvider.GetRequiredService<TestCurrentUser>());
            services.AddSingleton<IClock, FixedClock>();
            services.AddSingleton<IFeatureFlagService, EnabledTaskNotificationFeatureFlags>();
            services.AddScoped<IProjectRepository, ProjectRepository>();
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
            services.AddScoped<IGroupRepository, GroupRepository>();
            services.AddScoped<IOutboxEventRepository, OutboxEventRepository>();
            services.AddScoped<ITransactionalOutbox, TransactionalOutbox>();
            services.AddScoped<IBusinessInvalidationPublisher, BusinessInvalidationPublisher>();
            services.AddScoped<INotificationService, DbNotificationService>();
            services.AddScoped<IAuthorizationStateChangePublisher, NoopAuthorizationStateChangePublisher>();
            services.AddScoped<IAuditLogger, DbAuditLogger>();
            services.AddScoped<EfUnitOfWork>();
            services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<EfUnitOfWork>());
            services.AddScoped<ITaskCommandUnitOfWork>(serviceProvider => new CoordinatedTaskCommandUnitOfWork(
                serviceProvider.GetRequiredService<EfUnitOfWork>(),
                saveRace));
            services.AddScoped<IWorkspaceAuthorizationService, WorkspaceAuthorizationService>();
            services.AddScoped<IGroupAuthorizationService, GroupAuthorizationService>();
            services.AddScoped<ProjectAuthorizationService>();
            services.AddScoped<IProjectAuthorizationService>(serviceProvider => serviceProvider.GetRequiredService<ProjectAuthorizationService>());
            services.AddScoped<ITaskAuthorizationService>(serviceProvider => serviceProvider.GetRequiredService<ProjectAuthorizationService>());
            services.AddScoped<ICommentAuthorizationService>(serviceProvider => serviceProvider.GetRequiredService<ProjectAuthorizationService>());
            services.AddScoped<ITaskWorkspaceTimeZoneResolver, UtcTimeZoneResolver>();
            services.AddScoped<ITaskNotificationRecipientPolicy, TaskNotificationRecipientPolicy>();
            services.AddScoped<ITaskNotificationProducer, TaskNotificationProducer>();
            services.AddScoped<ITaskCommandService, TaskCommandService>();
            services.AddScoped<IProjectService, ProjectService>();

            return new ServiceHarness(
                services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }),
                connectionString,
                graph,
                saveRace,
                safetyGuard);
        }

        public RequestScope CreateScope(Guid? actorUserId = null)
        {
            var scope = _provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenantService>()
                .SetTenant(Graph.Tenant.Id, Graph.Tenant.Slug);
            scope.ServiceProvider.GetRequiredService<TestCurrentUser>()
                .SetUser(actorUserId ?? Graph.Actor.Id);
            var serviceProvider = scope.ServiceProvider;
            var subresources = new TaskSubresourceService(
                serviceProvider.GetRequiredService<IProjectRepository>(),
                serviceProvider.GetRequiredService<IUserRepository>(),
                serviceProvider.GetRequiredService<IProjectAuthorizationService>(),
                serviceProvider.GetRequiredService<ITaskAuthorizationService>(),
                serviceProvider.GetRequiredService<ICommentAuthorizationService>(),
                null!,
                null!,
                serviceProvider.GetRequiredService<ITaskCommandService>(),
                _safetyGuard,
                serviceProvider.GetRequiredService<ICurrentUser>(),
                serviceProvider.GetRequiredService<IClock>(),
                serviceProvider.GetRequiredService<IAuditLogger>(),
                serviceProvider.GetRequiredService<IBusinessInvalidationPublisher>(),
                serviceProvider.GetRequiredService<ITaskCommandUnitOfWork>(),
                serviceProvider.GetRequiredService<ITaskWorkspaceTimeZoneResolver>(),
                serviceProvider.GetRequiredService<ITaskNotificationProducer>());
            return new RequestScope(
                scope,
                serviceProvider.GetRequiredService<ITaskCommandService>(),
                subresources,
                serviceProvider.GetRequiredService<IProjectService>());
        }

        public async Task<PersistenceSnapshot> SnapshotAsync()
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            var task = await db.TaskItems.AsNoTracking().SingleAsync(item => item.Id == Graph.Task.Id);
            return new PersistenceSnapshot(
                task.VersionNo,
                task.PrimaryAssigneeUserId,
                await db.TaskAssignments.CountAsync(item => item.TaskItemId == Graph.Task.Id),
                await db.WorkItemWatchStates.CountAsync(item => item.TaskItemId == Graph.Task.Id),
                await db.Notifications.CountAsync(item => item.RelatedEntityType == "TaskItem" && item.RelatedEntityId == Graph.Task.Id),
                await db.NotificationUserStates.CountAsync(),
                await db.AuditLogs.CountAsync(item => item.EntityType == "TaskItem" && item.EntityId == Graph.Task.Id),
                await db.OutboxEvents.CountAsync());
        }

        public async Task<IReadOnlyList<Notification>> LoadNotificationsAsync()
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            return await db.Notifications.AsNoTracking()
                .Where(item => item.RelatedEntityType == "TaskItem" && item.RelatedEntityId == Graph.Task.Id)
                .OrderBy(item => item.CreatedAt)
                .ToListAsync();
        }

        public async Task<CommentSnapshot> LoadCommentSnapshotAsync(Guid commentId)
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            return await db.TaskComments.AsNoTracking()
                .Where(comment => comment.Id == commentId)
                .Select(comment => new CommentSnapshot(comment.BodyPlainText, comment.VersionNo, comment.UpdatedAt, comment.IsImportant))
                .SingleAsync();
        }

        public async Task<TaskComment> AddTaskCommentAsync(Guid authorUserId, string body)
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            var comment = new TaskComment
            {
                TenantId = Graph.Tenant.Id,
                WorkspaceId = Graph.Workspace.Id,
                ProjectId = Graph.Project.Id,
                TaskItemId = Graph.Task.Id,
                AuthorUserId = authorUserId,
                BodyPlainText = body,
                CreatedAt = FixedClock.Instance.UtcNow,
                VersionNo = 1
            };
            db.TaskComments.Add(comment);
            await db.SaveChangesAsync();
            return comment;
        }

        public async Task SetWorkspaceMembershipStatusAsync(Guid userId, MembershipStatus status)
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            var member = await db.WorkspaceMembers.SingleAsync(item =>
                item.WorkspaceId == Graph.Workspace.Id && item.UserId == userId);
            member.Status = status;
            await db.SaveChangesAsync();
        }

        public async Task<long> LoadNotificationUserStateVersionAsync(Guid userId)
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            return await db.NotificationUserStates.AsNoTracking()
                .Where(state => state.UserId == userId)
                .Select(state => state.Version)
                .SingleAsync();
        }

        public async Task<DateTimeOffset?> LoadDeadlineAsync()
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            return await db.TaskItems
                .AsNoTracking()
                .Where(item => item.Id == Graph.Task.Id)
                .Select(item => item.DeadlineAt)
                .SingleAsync();
        }

        public async Task<IReadOnlyList<AuditLog>> LoadAuditLogsAsync()
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            return await db.AuditLogs.AsNoTracking()
                .Where(item => item.EntityType == "TaskItem" && item.EntityId == Graph.Task.Id)
                .OrderBy(item => item.CreatedAt)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<OutboxEvent>> LoadOutboxAsync()
        {
            await using var db = CreateTenantContext(_connectionString, Graph.Tenant);
            return await db.OutboxEvents.AsNoTracking()
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            SaveRace.Dispose();
            await _provider.DisposeAsync();
        }

        private static async Task<Graph> SeedAsync(string connectionString)
        {
            var suffix = Guid.NewGuid().ToString("N");
            await using var platform = CreatePlatformContext(connectionString);
            var tenant = new Tenant
            {
                Name = $"PR07-B atomicity {suffix}",
                DisplayName = "PR07-B atomicity",
                Slug = $"pr07b-atomicity-{suffix}",
                Status = TenantStatus.Active
            };
            var actor = UserFor("actor", suffix);
            var recipient = UserFor("recipient", suffix);
            var outsider = UserFor("outsider", suffix);
            platform.AddRange(tenant, actor, recipient, outsider);
            await platform.SaveChangesAsync();

            await using var db = CreateTenantContext(connectionString, tenant);
            var workspace = new Workspace
            {
                TenantId = tenant.Id,
                Name = "PR07-B workspace",
                Slug = $"pr07b-workspace-{suffix}",
                CreatedByUserId = actor.Id
            };
            var project = new Project
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                OwnerUserId = actor.Id,
                CreatedByUserId = actor.Id,
                Name = "PR07-B project",
                Slug = $"pr07b-project-{suffix}",
                Status = ProjectStatus.Active,
                ActivationState = ProjectActivationState.Activated,
                ActivatedAtUtc = FixedClock.Instance.UtcNow,
                ActivationVersion = 1
            };
            var workflow = new TaskWorkflowDefinition
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Name = "PR07-B workflow",
                ReviewEnforcementEnabled = false
            };
            var todo = new TaskWorkflowStage
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                DefinitionId = workflow.Id,
                Name = "Todo",
                InternalCategory = TaskStageCategory.Todo,
                SortKey = 1024,
                IsInitialStage = true
            };
            var task = new TaskItem
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Title = "restricted PR07-B task title",
                CreatedByUserId = actor.Id,
                WorkflowStageId = todo.Id,
                Status = TaskItemStatus.NotStarted,
                Kind = WorkItemKind.Task,
                VersionNo = 1
            };

            db.AddRange(
                workspace,
                project,
                workflow,
                todo,
                task,
                new TenantUser
                {
                    TenantId = tenant.Id,
                    UserId = actor.Id,
                    Role = TenantUserRole.Member,
                    Status = TenantUserStatus.Active,
                    JoinedAt = FixedClock.Instance.UtcNow
                },
                new TenantUser
                {
                    TenantId = tenant.Id,
                    UserId = recipient.Id,
                    Role = TenantUserRole.Member,
                    Status = TenantUserStatus.Active,
                    JoinedAt = FixedClock.Instance.UtcNow
                },
                new WorkspaceMember
                {
                    TenantId = tenant.Id,
                    WorkspaceId = workspace.Id,
                    UserId = actor.Id,
                    Role = WorkspaceRole.Owner,
                    Status = MembershipStatus.Active,
                    JoinedAt = FixedClock.Instance.UtcNow
                },
                new WorkspaceMember
                {
                    TenantId = tenant.Id,
                    WorkspaceId = workspace.Id,
                    UserId = recipient.Id,
                    Role = WorkspaceRole.Member,
                    Status = MembershipStatus.Active,
                    JoinedAt = FixedClock.Instance.UtcNow
                },
                new ProjectMember
                {
                    TenantId = tenant.Id,
                    ProjectId = project.Id,
                    UserId = actor.Id,
                    Role = ProjectRole.Owner,
                    JoinedAt = FixedClock.Instance.UtcNow
                },
                new ProjectMember
                {
                    TenantId = tenant.Id,
                    ProjectId = project.Id,
                    UserId = recipient.Id,
                    Role = ProjectRole.Contributor,
                    JoinedAt = FixedClock.Instance.UtcNow
                });
            await db.SaveChangesAsync();
            return new Graph(tenant, workspace, project, actor, recipient, outsider, task);
        }

        private static User UserFor(string role, string suffix) => new()
        {
            DisplayName = $"PR07-B {role}",
            Email = $"pr07b-{role}-{suffix}@example.test",
            NormalizedEmail = $"PR07B-{role}-{suffix}@EXAMPLE.TEST",
            PasswordHash = "hash",
            Status = UserStatus.Active
        };

        private static AppDbContext CreatePlatformContext(string connectionString)
        {
            var tenant = new CurrentTenantService();
            tenant.SetPlatformScope();
            return new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
                tenant);
        }

        private static AppDbContext CreateTenantContext(string connectionString, Tenant tenantEntity)
        {
            var tenant = new CurrentTenantService();
            tenant.SetTenant(tenantEntity.Id, tenantEntity.Slug);
            return new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
                tenant);
        }
    }

    private sealed record Graph(
        Tenant Tenant,
        Workspace Workspace,
        Project Project,
        User Actor,
        User Recipient,
        User Outsider,
        TaskItem Task);

    private sealed record RequestScope(
        AsyncServiceScope Scope,
        ITaskCommandService Commands,
        ITaskSubresourceService Subresources,
        IProjectService Compatibility) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        private Guid _userId;

        public void SetUser(Guid value) => _userId = value;
        public Guid? UserId => _userId;
        public Guid? SessionId => null;
        public string Email => "pr07b-atomicity@example.test";
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => true;
    }

    private sealed class FixedClock : IClock
    {
        public static FixedClock Instance { get; } = new();
        public DateTimeOffset UtcNow => new(2026, 8, 2, 6, 0, 0, TimeSpan.Zero);
    }

    private sealed class UtcTimeZoneResolver : ITaskWorkspaceTimeZoneResolver
    {
        public Task<TimeZoneInfo> ResolveAsync(
            Guid tenantId,
            Guid workspaceId,
            CancellationToken cancellationToken = default) => Task.FromResult(TimeZoneInfo.Utc);
    }

    private sealed class NoopAuthorizationStateChangePublisher : IAuthorizationStateChangePublisher
    {
        public Task PublishAsync(
            Guid tenantId,
            Guid affectedUserId,
            string scopeType,
            Guid? scopeId,
            string change,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EnabledTaskNotificationFeatureFlags : IFeatureFlagService
    {
        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(
                FeatureKeys.Normalize(featureKey),
                FeatureKeys.TasksNotificationsV1,
                StringComparison.Ordinal));

        public async Task<Result> RequireEnabledAsync(string featureKey, CancellationToken cancellationToken = default) =>
            await IsEnabledAsync(featureKey, cancellationToken)
                ? Result.Success()
                : Result.Failure($"Feature '{featureKey}' is disabled.");

        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([FeatureKeys.TasksNotificationsV1]);
    }

    private sealed class SaveFailureInterceptor(SaveFailureTarget target) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (target == SaveFailureTarget.Task)
            {
                var task = eventData.Context?.ChangeTracker.Entries<TaskItem>()
                    .FirstOrDefault(entry => entry.State == EntityState.Modified);
                if (task is not null)
                {
                    task.Property(item => item.Title).CurrentValue = new string('t', 241);
                    task.Property(item => item.Title).IsModified = true;
                }
            }
            else if (target == SaveFailureTarget.Audit)
            {
                var audit = eventData.Context?.ChangeTracker.Entries<AuditLog>()
                    .FirstOrDefault(entry => entry.State == EntityState.Added);
                if (audit is not null)
                {
                    audit.Entity.Action = new string('a', 161);
                }
            }
            else if (target == SaveFailureTarget.Outbox)
            {
                var outbox = eventData.Context?.ChangeTracker.Entries<OutboxEvent>()
                    .FirstOrDefault(entry => entry.State == EntityState.Added);
                if (outbox is not null)
                {
                    outbox.Entity.EventType = new string('e', 161);
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class SaveRaceCoordinator : IDisposable
    {
        private readonly System.Threading.Lock _gate = new();
        private TaskCompletionSource? _release;
        private bool _armed;
        private int _remaining;

        public void Arm()
        {
            lock (_gate)
            {
                if (_armed)
                    throw new InvalidOperationException("A save race is already _armed.");

                _armed = true;
                _remaining = 2;
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public async Task WaitBeforeSaveAsync(CancellationToken cancellationToken)
        {
            Task wait;
            lock (_gate)
            {
                if (!_armed)
                    return;

                _remaining--;
                wait = _release!.Task;
                if (_remaining == 0)
                {
                    _armed = false;
                    _release.TrySetResult();
                }
            }

            await wait.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _release?.TrySetCanceled();
                _release = null;
                _armed = false;
                _remaining = 0;
            }
        }
    }

    private sealed class CoordinatedTaskCommandUnitOfWork(
        EfUnitOfWork inner,
        SaveRaceCoordinator coordinator) : ITaskCommandUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            inner.SaveChangesAsync(cancellationToken);

        public void ClearTaskCommandTracking() => inner.ClearTaskCommandTracking();

        public async Task<TaskCommandSaveOutcome> SaveTaskCommandAsync(CancellationToken cancellationToken = default)
        {
            await coordinator.WaitBeforeSaveAsync(cancellationToken);
            return await inner.SaveTaskCommandAsync(cancellationToken);
        }
    }
}
