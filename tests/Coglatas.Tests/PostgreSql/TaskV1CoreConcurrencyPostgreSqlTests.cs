using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Channels;
using Coglatas.Application.Files;
using Coglatas.Application.Messaging;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Workspaces;
using Coglatas.Application.Groups;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Coglatas.Tests.PostgreSql;

/// <summary>
/// Exercises the versioned Task command boundary with two independent request
/// scopes.  The scopes share only a deterministic clock coordinator; each owns
/// its DbContext, repositories, authorization services, audit logger, outbox,
/// and EfUnitOfWork.
/// </summary>
[Collection("PostgreSqlTaskV1")]
public sealed class TaskV1CoreConcurrencyPostgreSqlTests
{
    private static readonly JsonSerializerOptions RealtimeJsonOptions = new(JsonSerializerDefaults.Web);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task UpdateDetails_OneServiceWriterWins_LoserIsCleanAndCanRetry()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var expected = graph.Task.VersionNo;
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();

        // Both requests read the same authoritative version before their writes.
        Assert.Equal(expected, (await first.Commands.GetAsync(graph.Task.Id)).Value!.Version);
        Assert.Equal(expected, (await second.Commands.GetAsync(graph.Task.Id)).Value!.Version);

        harness.Race.Arm();
        var firstTask = ExecuteAsync(first, request => request.Commands.UpdateDetailsAsync(graph.Task.Id, Details("first", expected)));
        var secondTask = ExecuteAsync(second, request => request.Commands.UpdateDetailsAsync(graph.Task.Id, Details("second", expected)));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, results.Count(result => result.Result.IsSuccess));
        var winner = results.Single(result => result.Result.IsSuccess);
        var loser = results.Single(result => !result.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using (var verify = harness.CreateScope())
        {
            var task = await verify.Db.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
            Assert.Equal(winner.Result.Value!.Title, task.Title);
            Assert.Equal(2, task.VersionNo);
            Assert.Single(await verify.Db.AuditLogs.Where(log => log.EntityId == task.Id && log.Action == "TaskDetailsUpdated").ToListAsync());
            Assert.Single(await verify.Db.OutboxEvents.Where(evt => evt.AggregateId == task.Id && evt.EventType == "Projects.TaskChanged.v1").ToListAsync());
            Assert.Equal("unrelated", await verify.Db.TaskItems.Where(item => item.Id == graph.UnrelatedTask.Id).Select(item => item.Title).SingleAsync());
        }

        await using var retry = harness.CreateScope();
        var current = (await retry.Commands.GetAsync(graph.Task.Id)).Value!;
        var retried = await retry.Commands.UpdateDetailsAsync(graph.Task.Id, Details("retry", current.Version));
        Assert.True(retried.IsSuccess);
        Assert.Equal("retry", retried.Value!.Title);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task CreateSubtask_ParentConflictRollsBackLoserChildWatchAuditAndOutbox()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();

        harness.Race.Arm();
        var firstTask = ExecuteAsync(first, request => request.Subresources.CreateSubtaskAsync(graph.Task.Id, new CreateTaskSubtaskRequest("first child", null, TaskPriority.Medium)));
        var secondTask = ExecuteAsync(second, request => request.Subresources.CreateSubtaskAsync(graph.Task.Id, new CreateTaskSubtaskRequest("second child", null, TaskPriority.Medium)));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, results.Count(result => result.Result.IsSuccess));
        var winner = results.Single(result => result.Result.IsSuccess);
        var loser = results.Single(result => !result.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using (var verify = harness.CreateScope())
        {
            var children = await verify.Db.TaskItems.Where(item => item.ParentTaskItemId == graph.Task.Id).ToListAsync();
            var child = Assert.Single(children);
            Assert.Equal(winner.Result.Value!.Title, child.Title);
            Assert.Single(await verify.Db.WorkItemWatchStates.Where(state => state.TaskItemId == child.Id && state.UserId == graph.User.Id && state.AutomaticSources == WorkItemWatchAutomaticSource.Creator).ToListAsync());
            Assert.Equal(2, (await verify.Db.TaskItems.SingleAsync(item => item.Id == graph.Task.Id)).VersionNo);
            Assert.Single(await verify.Db.AuditLogs.Where(log => log.EntityId == child.Id && log.Action == "TaskCreated").ToListAsync());
            Assert.Single(await verify.Db.AuditLogs.Where(log => log.EntityId == graph.Task.Id && log.Action == "TaskSubtasksChanged").ToListAsync());
            Assert.Single(await verify.Db.OutboxEvents.Where(evt => evt.AggregateId == child.Id).ToListAsync());
            Assert.Single(await verify.Db.OutboxEvents.Where(evt => evt.AggregateId == graph.Task.Id).ToListAsync());
        }

        await using var retry = harness.CreateScope();
        var retried = await retry.Subresources.CreateSubtaskAsync(graph.Task.Id, new CreateTaskSubtaskRequest("retry child", null, TaskPriority.Medium));
        Assert.True(retried.IsSuccess);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task Checklist_CreateUpdateDeleteAndReorder_AreAggregateAtomic()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var firstItem = await CreateChecklistAsync(harness, "first");
        var secondItem = await CreateChecklistAsync(harness, "second");

        await using (var createA = harness.CreateScope())
        await using (var createB = harness.CreateScope())
        {
            var before = await SnapshotAsync(harness);
            harness.Race.Arm();
            var creates = await Task.WhenAll(
                ExecuteAsync(createA, request => request.Subresources.CreateChecklistAsync(graph.Task.Id, new CreateTaskChecklistRequest("racing a"))),
                ExecuteAsync(createB, request => request.Subresources.CreateChecklistAsync(graph.Task.Id, new CreateTaskChecklistRequest("racing b"))));
            Assert.Equal(1, creates.Count(result => result.Result.IsSuccess));
            var loser = creates.Single(result => !result.Result.IsSuccess);
            Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
            Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());
            await using var verify = harness.CreateScope();
            Assert.Equal(3, await verify.Db.TaskChecklistItems.CountAsync(value => value.TaskItemId == graph.Task.Id));
            await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskChecklistCreated", 1, 1);
        }

        await using (var updateA = harness.CreateScope())
        await using (var updateB = harness.CreateScope())
        {
            var before = await SnapshotAsync(harness);
            harness.Race.Arm();
            var updates = await Task.WhenAll(
                ExecuteAsync(updateA, request => request.Subresources.UpdateChecklistAsync(graph.Task.Id, firstItem.Id, new UpdateTaskChecklistRequest("completed", true, firstItem.Version))),
                ExecuteAsync(updateB, request => request.Subresources.UpdateChecklistAsync(graph.Task.Id, firstItem.Id, new UpdateTaskChecklistRequest("other", false, firstItem.Version))));
            Assert.Equal(1, updates.Count(result => result.Result.IsSuccess));
            var winner = updates.Single(result => result.Result.IsSuccess).Result.Value!;
            var loser = updates.Single(result => !result.Result.IsSuccess);
            Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
            Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());
            await using var verify = harness.CreateScope();
            var item = await verify.Db.TaskChecklistItems.SingleAsync(value => value.Id == firstItem.Id);
            Assert.Equal(winner.Text, item.Text);
            Assert.Equal(winner.IsCompleted, item.IsCompleted);
            Assert.Equal(winner.IsCompleted ? graph.User.Id : null, item.CompletedByUserId);
            Assert.Equal(winner.IsCompleted, item.CompletedAt.HasValue);
            Assert.Equal(2, item.VersionNo);
            await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskChecklistUpdated", 1, 1);
        }

        await using (var reorderA = harness.CreateScope())
        await using (var reorderB = harness.CreateScope())
        {
            var current = await ChecklistAsync(harness);
            var ids = current.Items.Select(item => item.Id).ToArray();
            var before = await SnapshotAsync(harness);
            harness.Race.Arm();
            var reorders = await Task.WhenAll(
                ExecuteAsync(reorderA, request => request.Subresources.ReorderChecklistAsync(graph.Task.Id, new ReorderTaskChecklistRequest(ids.Reverse().ToArray(), current.TaskVersion))),
                ExecuteAsync(reorderB, request => request.Subresources.ReorderChecklistAsync(graph.Task.Id, new ReorderTaskChecklistRequest(ids, current.TaskVersion))));
            Assert.Equal(1, reorders.Count(result => result.Result.IsSuccess));
            var winner = reorders.Single(result => result.Result.IsSuccess).Result.Value!;
            var loser = reorders.Single(result => !result.Result.IsSuccess);
            Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
            Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());
            Assert.Equal(winner.Items.Select(item => item.Id), (await ChecklistAsync(harness)).Items.Select(item => item.Id));
            await using var verify = harness.CreateScope();
            var expectedSortKeys = winner.Items.Select((item, index) => (item.Id, SortKey: (index + 1) * 1024L)).ToDictionary(value => value.Id, value => value.SortKey);
            var persisted = await verify.Db.TaskChecklistItems.Where(value => value.TaskItemId == graph.Task.Id).ToListAsync();
            Assert.All(persisted, item => Assert.Equal(expectedSortKeys[item.Id], item.SortKey));
            await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskChecklistReordered", 1, 1);
        }

        await using (var delete = harness.CreateScope())
        {
            var current = await delete.Subresources.ListChecklistAsync(graph.Task.Id);
            var item = current.Value!.Single(value => value.Id == secondItem.Id);
            Assert.True((await delete.Subresources.DeleteChecklistAsync(graph.Task.Id, item.Id, item.Version)).IsSuccess);
        }

        await using var final = harness.CreateScope();
        Assert.DoesNotContain(await final.Db.TaskChecklistItems.ToListAsync(), item => item.Id == secondItem.Id);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task Checklist_CompleteAndReopen_SetAndClearMetadata()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var item = await CreateChecklistAsync(harness, "reopen me");
        var before = await SnapshotAsync(harness);

        await using (var complete = harness.CreateScope())
        {
            var result = await complete.Subresources.UpdateChecklistAsync(graph.Task.Id, item.Id, new UpdateTaskChecklistRequest(null, true, item.Version));
            Assert.True(result.IsSuccess);
        }

        await using (var check = harness.CreateScope())
        {
            var persisted = await check.Db.TaskChecklistItems.SingleAsync(value => value.Id == item.Id);
            Assert.True(persisted.IsCompleted);
            Assert.Equal(new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero), persisted.CompletedAt);
            Assert.Equal(graph.User.Id, persisted.CompletedByUserId);
            Assert.Equal(item.Version + 1, persisted.VersionNo);
        }

        await using (var reopen = harness.CreateScope())
        {
            var current = await reopen.Subresources.ListChecklistAsync(graph.Task.Id);
            var result = await reopen.Subresources.UpdateChecklistAsync(graph.Task.Id, item.Id, new UpdateTaskChecklistRequest(null, false, current.Value!.Single(value => value.Id == item.Id).Version));
            Assert.True(result.IsSuccess);
        }

        await using var verify = harness.CreateScope();
        var reopened = await verify.Db.TaskChecklistItems.SingleAsync(value => value.Id == item.Id);
        Assert.False(reopened.IsCompleted);
        Assert.Null(reopened.CompletedAt);
        Assert.Null(reopened.CompletedByUserId);
        Assert.Equal(item.Version + 2, reopened.VersionNo);
        await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskChecklistUpdated", 2, 2);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task CompatibilityTaskAndAssignmentMutationsKeepCommittedTaskAndEventVersionsAligned()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var taskId = graph.UnrelatedTask.Id;

        await using (var update = harness.CreateScope())
        {
            var before = await SnapshotAsync(update.Db, taskId);
            var result = await update.Compatibility.UpdateTaskAsync(taskId, new UpdateTaskItemRequest(null, "compatibility update", null, null, null, null, null, null));
            Assert.True(result.IsSuccess);
            await using var verify = harness.CreateScope();
            await AssertTaskMutationSequenceAsync(verify.Db, before, taskId, ["TaskUpdated"]);
        }

        TaskAssignmentResponse assignment;
        await using (var add = harness.CreateScope())
        {
            var before = await SnapshotAsync(add.Db, taskId);
            var result = await add.Compatibility.AddAssignmentAsync(taskId, new AddTaskAssignmentRequest(graph.MentionUser.Id, TaskAssignmentRole.Assignee, 2));
            Assert.True(result.IsSuccess);
            assignment = result.Value!;
            await using var verify = harness.CreateScope();
            await AssertTaskMutationSequenceAsync(verify.Db, before, taskId, ["TaskAssigned"]);
        }

        await using (var update = harness.CreateScope())
        {
            var before = await SnapshotAsync(update.Db, taskId);
            var result = await update.Compatibility.UpdateAssignmentAsync(assignment.Id, new UpdateTaskAssignmentRequest(TaskAssignmentRole.Reviewer, 3, 1));
            Assert.True(result.IsSuccess);
            await using var verify = harness.CreateScope();
            await AssertTaskMutationSequenceAsync(verify.Db, before, taskId, ["TaskAssignmentUpdated"]);
        }

        await using (var delete = harness.CreateScope())
        {
            var before = await SnapshotAsync(delete.Db, taskId);
            Assert.True((await delete.Compatibility.DeleteAssignmentAsync(assignment.Id)).IsSuccess);
            await using var verify = harness.CreateScope();
            Assert.Null(await verify.Db.TaskAssignments.SingleOrDefaultAsync(value => value.Id == assignment.Id));
            await AssertTaskMutationSequenceAsync(verify.Db, before, taskId, ["TaskAssignmentRemoved"]);
        }

        await using (var delete = harness.CreateScope())
        {
            var before = await SnapshotAsync(delete.Db, graph.Task.Id);
            Assert.True((await delete.Compatibility.DeleteTaskAsync(graph.Task.Id)).IsSuccess);
            await using var verify = harness.CreateScope();
            Assert.NotNull((await verify.Db.TaskItems.SingleAsync(value => value.Id == graph.Task.Id)).DeletedAt);
            await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, ["TaskArchived"]);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task CompatibilityAssignmentRaceCommitsOnlyOneWriterAndAllowsRetry()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var taskId = graph.Task.Id;
        var before = await SnapshotAsync(harness);
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Compatibility.AddAssignmentAsync(taskId, new AddTaskAssignmentRequest(graph.MentionUser.Id, TaskAssignmentRole.Assignee, 1))),
            ExecuteAsync(second, request => request.Compatibility.AddAssignmentAsync(taskId, new AddTaskAssignmentRequest(graph.ReviewerUser.Id, TaskAssignmentRole.Reviewer, 1))));

        Assert.Equal(1, results.Count(value => value.Result.IsSuccess));
        var loser = results.Single(value => !value.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using (var verify = harness.CreateScope())
        {
            Assert.Single(await verify.Db.TaskAssignments.Where(value => value.TaskItemId == taskId).ToListAsync());
            await AssertTaskMutationSequenceAsync(verify.Db, before, taskId, ["TaskAssigned"]);
        }

        var winnerRole = results.Single(value => value.Result.IsSuccess).Result.Value!.Role;
        var retryRole = winnerRole == TaskAssignmentRole.Assignee
            ? TaskAssignmentRole.Reviewer
            : TaskAssignmentRole.Assignee;
        var retryUserId = retryRole == TaskAssignmentRole.Assignee
            ? graph.MentionUser.Id
            : graph.ReviewerUser.Id;
        await using var retry = harness.CreateScope();
        var retried = await retry.Compatibility.AddAssignmentAsync(taskId, new AddTaskAssignmentRequest(retryUserId, retryRole, 1));
        Assert.True(retried.IsSuccess);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityAssigneeRaceCommitsOneAtomicIntentAndCompositeRetryDedupesLogicalNotification()
    {
        await using var harness = await ServiceHarness.CreateAsync(useRealNotifications: true);
        var graph = harness.Graph;
        var taskId = graph.Task.Id;
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Compatibility.AddAssignmentAsync(taskId, new AddTaskAssignmentRequest(graph.MentionUser.Id, TaskAssignmentRole.Assignee, 1))),
            ExecuteAsync(second, request => request.Compatibility.AddAssignmentAsync(taskId, new AddTaskAssignmentRequest(graph.CollaboratorUser.Id, TaskAssignmentRole.Assignee, 1))));

        Assert.Equal(1, results.Count(value => value.Result.IsSuccess));
        var loser = results.Single(value => !value.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        var winner = results.Single(value => value.Result.IsSuccess).Result.Value!;
        Guid assignmentId;
        long committedVersion;
        await using (var verify = harness.CreateScope())
        {
            var assignment = Assert.Single(await verify.Db.TaskAssignments.Where(value => value.TaskItemId == taskId).ToListAsync());
            assignmentId = assignment.Id;
            Assert.Equal(winner.UserId, assignment.UserId);
            var task = await verify.Db.TaskItems.SingleAsync(value => value.Id == taskId);
            committedVersion = task.VersionNo;
            Assert.Equal(winner.UserId, task.PrimaryAssigneeUserId);
            var notifications = await verify.Db.Notifications
                .Where(value => value.UserId == winner.UserId && value.RelatedEntityId == taskId)
                .ToListAsync();
            var notification = Assert.Single(notifications);
            var notificationSignal = Assert.Single(await verify.Db.OutboxEvents
                .Where(value => value.EventType == "Notifications.NotificationCreated.v1")
                .ToListAsync());
            Assert.Equal(notification.Id, notificationSignal.AggregateId);
            using (var envelope = JsonDocument.Parse(notificationSignal.PayloadJson))
            {
                Assert.Equal(
                    notification.Id,
                    envelope.RootElement.GetProperty("payload").GetProperty("notificationId").GetGuid());
            }
            Assert.Single(await verify.Db.OutboxEvents.Where(value => value.EventType == "Projects.TaskAssignmentChanged.v1" && value.AggregateId == taskId).ToListAsync());
            Assert.Single(await verify.Db.OutboxEvents.Where(value => value.EventType == "Projects.TaskChanged.v1" && value.AggregateId == taskId).ToListAsync());
            Assert.Single(await verify.Db.AuditLogs.Where(value => value.EntityId == taskId && value.Action == "TaskAssigned").ToListAsync());
            Assert.Contains(
                await verify.Db.WorkItemWatchStates.Where(value => value.TaskItemId == taskId && value.UserId == winner.UserId).ToListAsync(),
                value => value.AutomaticSources.HasFlag(WorkItemWatchAutomaticSource.PrimaryAssignee));
        }

        await using (var canonicalRetry = harness.CreateScope())
        {
            var retried = await canonicalRetry.Commands.SetAssigneeAsync(
                taskId,
                new TaskRelationshipUserRequest(winner.UserId, committedVersion));
            Assert.True(retried.IsSuccess, retried.Error);
            Assert.Equal(committedVersion, retried.Value!.Task.Version);
            Assert.Equal(0, canonicalRetry.SaveRecorder.SaveTaskCommandCallCount);
        }

        await using (var roleChange = harness.CreateScope())
        {
            var changed = await roleChange.Compatibility.UpdateAssignmentAsync(
                assignmentId,
                new UpdateTaskAssignmentRequest(TaskAssignmentRole.Reviewer, 1, 0));
            Assert.True(changed.IsSuccess, changed.Error);
        }

        await using (var verify = harness.CreateScope())
        {
            var task = await verify.Db.TaskItems.SingleAsync(value => value.Id == taskId);
            Assert.Equal(committedVersion + 1, task.VersionNo);
            Assert.Null(task.PrimaryAssigneeUserId);
            Assert.Equal(winner.UserId, task.ReviewerUserId);
            Assert.Equal(TaskAssignmentRole.Reviewer, (await verify.Db.TaskAssignments.SingleAsync(value => value.Id == assignmentId)).Role);
            var notifications = await verify.Db.Notifications
                .Where(value => value.UserId == winner.UserId && value.RelatedEntityId == taskId)
                .OrderBy(value => value.CreatedAt)
                .ToListAsync();
            Assert.Equal(2, notifications.Count);
            Assert.Single(notifications, value => value.LogicalKey == $"task:{taskId:N}:event:TaskAssignmentChanged:version:{task.VersionNo}");
            var notificationSignals = await verify.Db.OutboxEvents
                .Where(value => value.EventType == "Notifications.NotificationCreated.v1")
                .ToListAsync();
            Assert.Equal(2, notificationSignals.Count);
            Assert.Equal(
                notifications.Select(value => value.Id).Order().ToArray(),
                notificationSignals.Select(value => value.AggregateId).Order().ToArray());
            foreach (var signal in notificationSignals)
            {
                using var envelope = JsonDocument.Parse(signal.PayloadJson);
                Assert.Equal(
                    signal.AggregateId,
                    envelope.RootElement.GetProperty("payload").GetProperty("notificationId").GetGuid());
            }
            Assert.Equal(2, await verify.Db.OutboxEvents.CountAsync(value => value.EventType == "Projects.TaskAssignmentChanged.v1" && value.AggregateId == taskId));
            Assert.Equal(2, await verify.Db.OutboxEvents.CountAsync(value => value.EventType == "Projects.TaskChanged.v1" && value.AggregateId == taskId));
            Assert.Equal(2, await verify.Db.AuditLogs.CountAsync(value => value.EntityId == taskId && (value.Action == "TaskAssigned" || value.Action == "TaskAssignmentUpdated")));
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task Checklist_UpdateVsDelete_OneAtomicWinner()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var item = await CreateChecklistAsync(harness, "original");
        var before = await SnapshotAsync(harness);
        await using var update = harness.CreateScope();
        await using var delete = harness.CreateScope();
        harness.Race.Arm();
        var updateTask = ExecuteChecklistUpdateAsync(update, request => request.Subresources.UpdateChecklistAsync(graph.Task.Id, item.Id, new UpdateTaskChecklistRequest("updated", true, item.Version)));
        var deleteTask = ExecuteChecklistDeleteAsync(delete, request => request.Subresources.DeleteChecklistAsync(graph.Task.Id, item.Id, item.Version));
        var results = await Task.WhenAll(updateTask, deleteTask);
        var loser = AssertOneWinner(results);
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using var verify = harness.CreateScope();
        var persisted = await verify.Db.TaskChecklistItems.SingleOrDefaultAsync(value => value.Id == item.Id);
        if (persisted is null)
            await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskChecklistDeleted", 1, 1);
        else
        {
            Assert.Equal("updated", persisted.Text);
            Assert.True(persisted.IsCompleted);
            Assert.Equal(graph.User.Id, persisted.CompletedByUserId);
            await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskChecklistUpdated", 1, 1);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task Checklist_DeleteVsDelete_OneAtomicWinner()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var item = await CreateChecklistAsync(harness, "delete twice");
        var before = await SnapshotAsync(harness);
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Subresources.DeleteChecklistAsync(graph.Task.Id, item.Id, item.Version)),
            ExecuteAsync(second, request => request.Subresources.DeleteChecklistAsync(graph.Task.Id, item.Id, item.Version)));
        var loser = AssertOneWinner(results.Select(value => (value.Scope, value.Result.IsSuccess, value.Result.Error)).ToArray());
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using var verify = harness.CreateScope();
        Assert.Null(await verify.Db.TaskChecklistItems.SingleOrDefaultAsync(value => value.Id == item.Id));
        await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskChecklistDeleted", 1, 1);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ChildDetailMutation_ParentVersionAuditAndOutboxCommitOrRollbackTogether()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using var creator = harness.CreateScope();
        var created = await creator.Subresources.CreateSubtaskAsync(graph.Task.Id, new CreateTaskSubtaskRequest("child", null, TaskPriority.Medium));
        Assert.True(created.IsSuccess);
        var child = created.Value!;

        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Commands.UpdateDetailsAsync(child.Id, Details(child.Title, child.Version, TaskPriority.Medium, 25, new DateOnly(2026, 7, 27), new DateOnly(2026, 7, 28)))),
            ExecuteAsync(second, request => request.Commands.UpdateDetailsAsync(child.Id, Details(child.Title, child.Version, TaskPriority.Medium, 75, new DateOnly(2026, 7, 29), new DateOnly(2026, 7, 30)))));

        Assert.Equal(1, results.Count(result => result.Result.IsSuccess));
        var loser = results.Single(result => !result.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using var verify = harness.CreateScope();
        var persistedChild = await verify.Db.TaskItems.SingleAsync(item => item.Id == child.Id);
        var persistedParent = await verify.Db.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
        Assert.Equal(2, persistedChild.VersionNo);
        Assert.Equal(3, persistedParent.VersionNo);
        Assert.Equal(2, await verify.Db.AuditLogs.CountAsync(log => log.EntityId == child.Id));
        Assert.Equal(2, await verify.Db.AuditLogs.CountAsync(log => log.EntityId == persistedParent.Id && log.Action == "TaskSubtasksChanged"));
        Assert.Equal(2, await verify.Db.OutboxEvents.CountAsync(evt => evt.AggregateId == child.Id));
        Assert.Equal(2, await verify.Db.OutboxEvents.CountAsync(evt => evt.AggregateId == persistedParent.Id));
        var detail = (await verify.Commands.GetAsync(persistedParent.Id)).Value!;
        Assert.Equal(persistedChild.ProgressPercent, detail.ProgressPercent);
        Assert.Equal(persistedChild.PlannedStartDate, detail.PlannedStartDate);
        Assert.Equal(persistedChild.PlannedEndDate, detail.PlannedEndDate);

        await using var retry = harness.CreateScope();
        var authoritative = (await retry.Commands.GetAsync(child.Id)).Value!;
        var retried = await retry.Commands.UpdateDetailsAsync(child.Id, Details(child.Title, authoritative.Version, TaskPriority.Medium, 60, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2)));
        Assert.True(retried.IsSuccess);
        await using var afterRetry = harness.CreateScope();
        var retriedChild = await afterRetry.Db.TaskItems.SingleAsync(item => item.Id == child.Id);
        var retriedParent = await afterRetry.Db.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
        Assert.Equal(3, retriedChild.VersionNo);
        Assert.Equal(4, retriedParent.VersionNo);
        Assert.Equal(60, retriedChild.ProgressPercent);
        Assert.Equal(60, (await afterRetry.Commands.GetAsync(retriedParent.Id)).Value!.ProgressPercent);
        Assert.Single(await afterRetry.Db.OutboxEvents.Where(value => value.AggregateId == retriedChild.Id && value.AggregateVersion == retriedChild.VersionNo).ToListAsync());
        Assert.Single(await afterRetry.Db.OutboxEvents.Where(value => value.AggregateId == retriedParent.Id && value.AggregateVersion == retriedParent.VersionNo).ToListAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ChildTransitionRace_ParentAndChildCommitTogether()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var child = await CreateSubtaskAsync(harness, "transition child");
        var result = await AssertChildMutationRaceAsync(
            harness, child.Id, child.Version, "TaskTransitioned",
            scope => scope.Commands.TransitionAsync(child.Id, new TaskTransitionRequest(graph.DoneStage.Id, child.Version)));
        Assert.Equal(TaskItemStatus.Completed, result.Child.Status);
        Assert.Equal(100, result.Child.ProgressPercent);
        await using var verify = harness.CreateScope();
        Assert.Equal(100, (await verify.Commands.GetAsync(graph.Task.Id)).Value!.ProgressPercent);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ChildCancelRace_ParentAndChildCommitTogether()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var child = await CreateSubtaskAsync(harness, "cancel child");
        var result = await AssertChildMutationRaceAsync(
            harness, child.Id, child.Version, "TaskTransitioned",
            scope => scope.Commands.CancelAsync(child.Id, new TaskReviewRequest(child.Version, "duplicate cancellation")));
        Assert.Equal(TaskItemStatus.Cancelled, result.Child.Status);
        await using var verify = harness.CreateScope();
        Assert.Equal(0, (await verify.Commands.GetAsync(graph.Task.Id)).Value!.ProgressPercent);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ChildReopenRace_ParentAndChildCommitTogether()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var child = await CreateSubtaskAsync(harness, "reopen child");
        await using (var complete = harness.CreateScope())
        {
            Assert.True((await complete.Commands.TransitionAsync(child.Id, new TaskTransitionRequest(graph.DoneStage.Id, child.Version))).IsSuccess);
        }
        var completed = await TaskRowAsync(harness, child.Id);
        var result = await AssertChildMutationRaceAsync(
            harness, child.Id, completed.VersionNo, "TaskTransitioned",
            scope => scope.Commands.ReopenAsync(child.Id, new TaskReviewRequest(completed.VersionNo)));
        Assert.Equal(TaskItemStatus.NotStarted, result.Child.Status);
        await using var verify = harness.CreateScope();
        Assert.Equal(0, (await verify.Commands.GetAsync(graph.Task.Id)).Value!.ProgressPercent);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ChildDeleteRace_ParentAndChildCommitTogether()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var child = await CreateSubtaskAsync(harness, "delete child");
        var result = await AssertChildMutationRaceAsync(
            harness, child.Id, child.Version, "TaskDeleted",
            scope => scope.Commands.DeleteAsync(child.Id, new TaskDeleteRequest(child.Version)));
        Assert.NotNull(result.Child.DeletedAt);
        await using var verify = harness.CreateScope();
        Assert.Empty((await verify.Subresources.ListSubtasksAsync(graph.Task.Id)).Value!.Items);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ChildRestoreRace_ParentAndChildCommitTogether()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var child = await CreateSubtaskAsync(harness, "restore child");
        await using (var delete = harness.CreateScope())
        {
            Assert.True((await delete.Commands.DeleteAsync(child.Id, new TaskDeleteRequest(child.Version))).IsSuccess);
        }
        var deleted = await TaskRowAsync(harness, child.Id);
        var result = await AssertChildMutationRaceAsync(
            harness, child.Id, deleted.VersionNo, "TaskRestored",
            scope => scope.Commands.RestoreAsync(child.Id, new TaskRestoreRequest(deleted.VersionNo)),
            includeDeleted: true);
        Assert.Null(result.Child.DeletedAt);
        await using var verify = harness.CreateScope();
        Assert.Single((await verify.Subresources.ListSubtasksAsync(graph.Task.Id)).Value!.Items);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task Comment_UpdateDeleteAndLegacyAdapterRemainAtomicAndPrivate()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var created = await CreateCommentAsync(harness, "sensitive @mention text");
        var before = await SnapshotAsync(harness);
        CommentRaceOperation winner;

        await using (var update = harness.CreateScope())
        await using (var delete = harness.CreateScope())
        {
            harness.Race.Arm();
            var updateTask = ExecuteCommentUpdateAsync(update, request => request.Subresources.UpdateCommentAsync(created.Id, new UpdateTaskCommentRequest("winner body", null, created.Version)));
            var deleteTask = ExecuteCommentDeleteAsync(delete, request => request.Subresources.DeleteCommentAsync(created.Id, created.Version));
            var results = await Task.WhenAll(updateTask, deleteTask);
            Assert.Equal(1, results.Count(result => result.IsSuccess));
            winner = results.Single(result => result.IsSuccess).Operation;
            var loser = results.Single(result => !result.IsSuccess);
            Assert.Equal("TASK_STALE_VERSION", Code(loser.Error));
            Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());
        }

        await using (var verify = harness.CreateScope())
        {
            var row = await verify.Db.TaskComments.SingleAsync(comment => comment.Id == created.Id);
            Assert.Equal(2, row.VersionNo);
            if (winner == CommentRaceOperation.Update)
            {
                Assert.Null(row.DeletedAt);
                Assert.Null(row.DeletedByUserId);
                Assert.Equal("winner body", row.BodyPlainText);
                await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskCommentUpdated", 1, 1);
            }
            else
            {
                Assert.NotNull(row.DeletedAt);
                Assert.Equal(graph.User.Id, row.DeletedByUserId);
                await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskCommentDeleted", 1, 1);
            }
            var commentActions = new[] { "TaskCommentCreated", "TaskCommentUpdated", "TaskCommentDeleted" };
            var audit = await verify.Db.AuditLogs.Where(log => log.EntityId == graph.Task.Id && commentActions.Contains(log.Action)).ToListAsync();
            Assert.All(audit, log => Assert.DoesNotContain("sensitive", $"{log.Summary} {log.MetadataJson}", StringComparison.OrdinalIgnoreCase));
        }

        // The compatibility read returns the canonical row; it never materializes a generic Comment.
        await using var compatibility = harness.CreateScope();
        var canonical = await compatibility.Subresources.GetCommentForCompatibilityAsync(created.Id);
        Assert.True(canonical.IsSuccess);
        if (winner == CommentRaceOperation.Update)
            Assert.Equal("winner body", canonical.Value!.BodyPlainText);
        else
            Assert.Null(canonical.Value!.BodyPlainText);
        Assert.Equal(0, await compatibility.Db.Comments.CountAsync(comment => comment.TargetType == CommentTargetType.TaskItem && comment.TargetId == graph.Task.Id));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task Comment_UpdateVsUpdate_OneWinnerCanAuthoritativelyRetry()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var comment = await CreateCommentAsync(harness, "original body");
        var before = await SnapshotAsync(harness);
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var firstTask = ExecuteAsync(first, request => request.Subresources.UpdateCommentAsync(comment.Id, new UpdateTaskCommentRequest("first body", true, comment.Version)));
        var secondTask = ExecuteAsync(second, request => request.Subresources.UpdateCommentAsync(comment.Id, new UpdateTaskCommentRequest("second body", false, comment.Version)));
        var results = await Task.WhenAll(firstTask, secondTask);
        var winner = results.Single(value => value.Result.IsSuccess);
        var loser = results.Single(value => !value.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using (var verify = harness.CreateScope())
        {
            var persisted = await verify.Db.TaskComments.SingleAsync(value => value.Id == comment.Id);
            Assert.Equal(winner.Result.Value!.BodyPlainText, persisted.BodyPlainText);
            Assert.Equal(winner.Result.Value.IsImportant, persisted.IsImportant);
            Assert.Equal(comment.Version + 1, persisted.VersionNo);
            await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskCommentUpdated", 1, 1);
        }

        await using var retry = harness.CreateScope();
        var current = (await retry.Subresources.GetCommentForCompatibilityAsync(comment.Id)).Value!;
        var retried = await retry.Subresources.UpdateCommentAsync(comment.Id, new UpdateTaskCommentRequest("retry body", null, current!.Version));
        Assert.True(retried.IsSuccess);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task Comment_DeleteVsDelete_OneTombstoneWinnerAndNoAuditBody()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var body = $"sensitive-marker @{{{graph.MentionUser.Id}}}";
        var comment = await CreateCommentAsync(harness, body);
        var before = await SnapshotAsync(harness);
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Subresources.DeleteCommentAsync(comment.Id, comment.Version)),
            ExecuteAsync(second, request => request.Subresources.DeleteCommentAsync(comment.Id, comment.Version)));
        var loser = AssertOneWinner(results.Select(value => (value.Scope, value.Result.IsSuccess, value.Result.Error)).ToArray());
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using var verify = harness.CreateScope();
        var persisted = await verify.Db.TaskComments.SingleAsync(value => value.Id == comment.Id);
        Assert.NotNull(persisted.DeletedAt);
        Assert.Equal(graph.User.Id, persisted.DeletedByUserId);
        Assert.Equal(comment.Version + 1, persisted.VersionNo);
        await AssertDeltaAsync(verify.Db, before, graph.Task.Id, "TaskCommentDeleted", 1, 1);
        var audit = await verify.Db.AuditLogs.Where(log => log.EntityId == graph.Task.Id && log.Action == "TaskCommentDeleted").ToListAsync();
        Assert.All(audit, log =>
        {
            var text = $"{log.Summary} {log.MetadataJson}";
            Assert.DoesNotContain(body, text, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive-marker", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@{", text, StringComparison.Ordinal);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelDefinition_CreateRaceMapsOnlyTheNormalizedNameConstraintToDuplicate()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();

        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", null))),
            ExecuteAsync(second, request => request.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest(" release ", null))));

        Assert.Equal(1, results.Count(value => value.Result.IsSuccess));
        var loser = results.Single(value => !value.Result.IsSuccess);
        Assert.Equal("TASK_LABEL_DUPLICATE", Code(loser.Result.Error));
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using (var verify = harness.CreateScope())
        {
            Assert.Single(await verify.Db.ProjectTaskLabels.Where(value => value.ProjectId == graph.Project.Id).ToListAsync());
            Assert.Single(await verify.Db.AuditLogs.Where(value => value.Action == "TaskLabelCreated").ToListAsync());
            Assert.Single(await verify.Db.OutboxEvents.Where(value => value.AggregateId == graph.Project.Id && value.EventType == "Projects.ProjectChanged.v1").ToListAsync());
        }

        await using var retry = harness.CreateScope();
        var repeated = await retry.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("RELEASE", null));
        Assert.Equal("TASK_LABEL_DUPLICATE", Code(repeated.Error));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelDefinition_UpdateRaceCommitsOnlyTheWinnerAndAuthoritativeRetryAdvancesOnce()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse label;
        await using (var setup = harness.CreateScope())
        {
            var created = await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", "original"));
            Assert.True(created.IsSuccess);
            label = created.Value!;
        }

        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Subresources.UpdateLabelAsync(graph.Project.Id, label.Id, new UpdateProjectTaskLabelRequest("Release A", default, default, label.Version))),
            ExecuteAsync(second, request => request.Subresources.UpdateLabelAsync(graph.Project.Id, label.Id, new UpdateProjectTaskLabelRequest(default, "Release B description", default, label.Version))));

        Assert.Equal(1, results.Count(value => value.Result.IsSuccess));
        Assert.Equal(2, harness.Race.SaveCallCount);
        Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.Saved);
        var loser = Assert.Single(results, value => !value.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Equal(TaskCommandSaveResult.ConcurrencyConflict, loser.Scope.SaveRecorder.LastSaveOutcome?.Result);
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using (var verify = harness.CreateScope())
        {
            var persisted = await verify.Db.ProjectTaskLabels.SingleAsync(value => value.Id == label.Id);
            var winner = results.Single(value => value.Result.IsSuccess).Result.Value!;
            Assert.Equal(winner.Name, persisted.Name);
            Assert.Equal(winner.Description, persisted.Description);
            Assert.Equal(label.Version + 1, persisted.VersionNo);
            Assert.Single(await verify.Db.AuditLogs.Where(value => value.Action == "TaskLabelUpdated").ToListAsync());
            Assert.Equal(2, await verify.Db.OutboxEvents.CountAsync(value => value.AggregateId == graph.Project.Id && value.EventType == "Projects.ProjectChanged.v1"));
        }

        await using var retry = harness.CreateScope();
        var current = (await retry.Subresources.ListLabelsAsync(graph.Project.Id, true)).Value!.Single(value => value.Id == label.Id);
        var retried = await retry.Subresources.UpdateLabelAsync(graph.Project.Id, label.Id, new UpdateProjectTaskLabelRequest(default, "retry", default, current.Version));
        Assert.True(retried.IsSuccess);
        Assert.Equal(current.Version + 1, retried.Value!.Version);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelDefinition_ArchiveUpdateRaceCommitsOnlyTheWinnerAndClearsTheLoser()
    {
        await AssertLabelArchiveUpdateRaceAsync(archivedInitially: false, archiveCommand: true);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelDefinition_RestoreUpdateRaceCommitsOnlyTheWinnerAndClearsTheLoser()
    {
        await AssertLabelArchiveUpdateRaceAsync(archivedInitially: true, archiveCommand: false);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelDefinition_RenameDuplicateIsSideEffectFreeAndClearsRequestTracking()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse release;
        ProjectTaskLabelResponse candidate;
        await using (var setup = harness.CreateScope())
        {
            release = (await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", null))).Value!;
            candidate = (await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Candidate", null))).Value!;
        }

        await using var command = harness.CreateScope();
        var auditBefore = await command.Db.AuditLogs.CountAsync();
        var outboxBefore = await command.Db.OutboxEvents.CountAsync();
        var result = await command.Subresources.UpdateLabelAsync(graph.Project.Id, candidate.Id, new UpdateProjectTaskLabelRequest(" release ", default, default, candidate.Version));

        Assert.Equal("TASK_LABEL_DUPLICATE", Code(result.Error));
        Assert.Equal(0, command.SaveRecorder.SaveTaskCommandCallCount);
        Assert.Equal(1, command.SaveRecorder.ClearTrackingCallCount);
        Assert.Empty(command.Db.ChangeTracker.Entries());
        await using var verify = harness.CreateScope();
        var persisted = await verify.Db.ProjectTaskLabels.SingleAsync(value => value.Id == candidate.Id);
        Assert.Equal("Candidate", persisted.Name);
        Assert.Equal(candidate.Version, persisted.VersionNo);
        Assert.Equal(auditBefore, await verify.Db.AuditLogs.CountAsync());
        Assert.Equal(outboxBefore, await verify.Db.OutboxEvents.CountAsync());
        Assert.Equal("Release", (await verify.Db.ProjectTaskLabels.SingleAsync(value => value.Id == release.Id)).Name);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelDefinition_DescriptionPatchDistinguishesOmittedNullAndString()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse label;
        await using (var setup = harness.CreateScope())
        {
            label = (await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", "original"))).Value!;
            var omitted = await setup.Subresources.UpdateLabelAsync(graph.Project.Id, label.Id, new UpdateProjectTaskLabelRequest(default, default, default, label.Version));
            Assert.True(omitted.IsSuccess);
            Assert.Equal("original", omitted.Value!.Description);
            var cleared = await setup.Subresources.UpdateLabelAsync(graph.Project.Id, label.Id, new UpdateProjectTaskLabelRequest(default, new OptionalString(true, null), default, omitted.Value.Version));
            Assert.True(cleared.IsSuccess);
            Assert.Null(cleared.Value!.Description);
            var set = await setup.Subresources.UpdateLabelAsync(graph.Project.Id, label.Id, new UpdateProjectTaskLabelRequest(default, "  revised  ", default, cleared.Value.Version));
            Assert.True(set.IsSuccess);
            Assert.Equal("revised", set.Value!.Description);
        }

        await using var verify = harness.CreateScope();
        var persisted = await verify.Db.ProjectTaskLabels.SingleAsync(value => value.Id == label.Id);
        Assert.Equal("revised", persisted.Description);
        Assert.Equal(label.Version + 3, persisted.VersionNo);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task ApplyLabelRaceReturnsCanonicalAssociationForBothServiceRequests()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse label;
        await using (var setup = harness.CreateScope())
        {
            var created = await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", null));
            Assert.True(created.IsSuccess);
            label = created.Value!;
        }

        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        var expected = (await first.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.Equal(expected, (await second.Commands.GetAsync(graph.Task.Id)).Value!.Version);
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(expected))),
            ExecuteAsync(second, request => request.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(expected))));

        Assert.All(results, value => Assert.True(value.Result.IsSuccess));
        Assert.Equal(2, harness.Race.SaveCallCount);
        Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.Saved);
        var recovery = Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result is TaskCommandSaveResult.ConcurrencyConflict or TaskCommandSaveResult.UniqueConflict);
        Assert.True(recovery.Scope.SaveRecorder.RecoveryPathEntered);
        Assert.True(recovery.Scope.SaveRecorder.ClearTrackingCallCount >= 1);
        Assert.Empty(recovery.Scope.Db.ChangeTracker.Entries());
        await using var verify = harness.CreateScope();
        Assert.Single(await verify.Db.WorkItemLabels.Where(value => value.TaskItemId == graph.Task.Id && value.LabelId == label.Id).ToListAsync());
        Assert.Equal(expected + 1, await verify.Db.TaskItems.Where(value => value.Id == graph.Task.Id).Select(value => value.VersionNo).SingleAsync());
        Assert.Single(await verify.Db.AuditLogs.Where(value => value.EntityId == graph.Task.Id && value.Action == "TaskLabelApplied").ToListAsync());
        Assert.Single(await verify.Db.OutboxEvents.Where(value => value.AggregateId == graph.Task.Id && value.EventType == "Projects.TaskChanged.v1").ToListAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task WatchMissingStateRaceHasOneMutationAndNoLeakedSideEffects()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using (var setup = harness.CreateScope())
        {
            var existing = await setup.Db.WorkItemWatchStates.SingleAsync(value => value.TaskItemId == graph.Task.Id && value.UserId == graph.User.Id);
            setup.Db.WorkItemWatchStates.Remove(existing);
            await setup.Db.SaveChangesAsync();
        }

        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Commands.WatchAsync(graph.Task.Id, new TaskWatchRequest(0))),
            ExecuteAsync(second, request => request.Commands.WatchAsync(graph.Task.Id, new TaskWatchRequest(0))));

        Assert.Equal(1, results.Count(value => value.Result.IsSuccess));
        Assert.Equal(2, harness.Race.SaveCallCount);
        Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.Saved);
        var loser = results.Single(value => !value.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Equal(TaskCommandSaveResult.UniqueConflict, loser.Scope.SaveRecorder.LastSaveOutcome?.Result);
        Assert.Equal(TaskCommandConstraintNames.WorkItemWatchStateIdentity, loser.Scope.SaveRecorder.LastSaveOutcome?.ConstraintName);
        Assert.True(loser.Scope.SaveRecorder.RecoveryPathEntered);
        Assert.True(loser.Scope.SaveRecorder.ClearTrackingCallCount >= 1);
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());
        await using var verify = harness.CreateScope();
        Assert.Single(await verify.Db.WorkItemWatchStates.Where(value => value.TaskItemId == graph.Task.Id && value.UserId == graph.User.Id).ToListAsync());
        Assert.Single(await verify.Db.AuditLogs.Where(value => value.EntityId == graph.Task.Id && value.Action == "TaskWatchEnabled").ToListAsync());
        Assert.Single(await verify.Db.OutboxEvents.Where(value => value.AggregateId == graph.Task.Id && value.EventType == "Projects.TaskChanged.v1").ToListAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task WatchOptOutSurvivesAutomaticSourceReconciliationUntilManualRewatch()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using (var optOut = harness.CreateScope())
        {
            var state = (await optOut.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            var result = await optOut.Commands.UnwatchAsync(graph.Task.Id, new TaskWatchRequest(state.Version));
            Assert.True(result.IsSuccess);
            Assert.True(result.Value!.IsExplicitOptOut);
            Assert.False(result.Value.IsWatching);
        }

        await using (var add = harness.CreateScope())
        {
            var version = (await add.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await add.Commands.AddCollaboratorAsync(graph.Task.Id, new TaskCollaboratorRequest(graph.User.Id, version))).IsSuccess);
        }

        await using (var remove = harness.CreateScope())
        {
            var version = (await remove.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await remove.Commands.RemoveCollaboratorAsync(graph.Task.Id, graph.User.Id, version)).IsSuccess);
        }

        await using (var verify = harness.CreateScope())
        {
            var state = (await verify.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.True(state.IsExplicitOptOut);
            Assert.False(state.IsWatching);
            Assert.Contains(nameof(WorkItemWatchAutomaticSource.Creator), state.AutomaticSources);
            var rewound = await verify.Commands.WatchAsync(graph.Task.Id, new TaskWatchRequest(state.Version));
            Assert.True(rewound.IsSuccess);
            Assert.False(rewound.Value!.IsExplicitOptOut);
            Assert.True(rewound.Value.IsWatching);
        }

        await using var reload = harness.CreateScope();
        var persisted = (await reload.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
        Assert.True(persisted.IsWatching);
        Assert.False(persisted.IsExplicitOptOut);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task FileAssociationRaceReturnsTheCanonicalRowAndRemoveDoesNotDeleteTheFile()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        var expected = (await first.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.Equal(expected, (await second.Commands.GetAsync(graph.Task.Id)).Value!.Version);

        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Subresources.AssociateFileAsync(graph.Task.Id, new CreateTaskFileAssociationRequest(graph.SourceAttachment.Id, expected))),
            ExecuteAsync(second, request => request.Subresources.AssociateFileAsync(graph.Task.Id, new CreateTaskFileAssociationRequest(graph.SourceAttachment.Id, expected))));

        Assert.All(results, value => Assert.True(value.Result.IsSuccess));
        Assert.Equal(2, harness.Race.SaveCallCount);
        Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.Saved);
        var associateRecovery = Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result is TaskCommandSaveResult.ConcurrencyConflict or TaskCommandSaveResult.UniqueConflict);
        Assert.True(associateRecovery.Scope.SaveRecorder.RecoveryPathEntered);
        Assert.True(associateRecovery.Scope.SaveRecorder.ClearTrackingCallCount >= 1);
        Assert.Empty(associateRecovery.Scope.Db.ChangeTracker.Entries());
        var associationId = Assert.Single(results.Select(value => value.Result.Value!.Id).Distinct());
        await using (var verify = harness.CreateScope())
        {
            Assert.Single(await verify.Db.Attachments.Where(value => value.Id == associationId && value.OwnerType == AttachmentOwnerType.TaskItem && value.OwnerId == graph.Task.Id).ToListAsync());
            Assert.Equal(expected + 1, await verify.Db.TaskItems.Where(value => value.Id == graph.Task.Id).Select(value => value.VersionNo).SingleAsync());
            Assert.Single(await verify.Db.AuditLogs.Where(value => value.EntityId == graph.Task.Id && value.Action == "TaskFileAssociated").ToListAsync());
            Assert.Single(await verify.Db.OutboxEvents.Where(value => value.AggregateId == graph.Task.Id && value.EventType == "Projects.TaskChanged.v1").ToListAsync());
        }

        await using var removeFirst = harness.CreateScope();
        await using var removeSecond = harness.CreateScope();
        var removeVersion = (await removeFirst.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.Equal(removeVersion, (await removeSecond.Commands.GetAsync(graph.Task.Id)).Value!.Version);
        harness.Race.Arm();
        var removals = await Task.WhenAll(
            ExecuteAsync(removeFirst, request => request.Subresources.RemoveFileAsync(graph.Task.Id, associationId, removeVersion)),
            ExecuteAsync(removeSecond, request => request.Subresources.RemoveFileAsync(graph.Task.Id, associationId, removeVersion)));
        Assert.All(removals, value => Assert.True(value.Result.IsSuccess));
        Assert.Equal(2, harness.Race.SaveCallCount);
        Assert.Single(removals, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.Saved);
        var removeRecovery = Assert.Single(removals, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.ConcurrencyConflict);
        Assert.True(removeRecovery.Scope.SaveRecorder.RecoveryPathEntered);
        Assert.True(removeRecovery.Scope.SaveRecorder.ClearTrackingCallCount >= 1);
        Assert.Empty(removeRecovery.Scope.Db.ChangeTracker.Entries());

        await using (var remove = harness.CreateScope())
        {
            var versionAfterDelete = (await remove.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            var auditCount = await remove.Db.AuditLogs.CountAsync(value => value.EntityId == graph.Task.Id && value.Action == "TaskFileAssociationRemoved");
            var outboxCount = await remove.Db.OutboxEvents.CountAsync(value => value.AggregateId == graph.Task.Id && value.EventType == "Projects.TaskChanged.v1");
            Assert.Equal(removeVersion + 1, versionAfterDelete);
            Assert.Equal(1, auditCount);
            // The association winner already emitted one Task event; the
            // remove-race winner adds exactly one more.
            Assert.Equal(2, outboxCount);
            Assert.True((await remove.Subresources.RemoveFileAsync(graph.Task.Id, associationId, removeVersion)).IsSuccess);
            Assert.Equal(versionAfterDelete, (await remove.Commands.GetAsync(graph.Task.Id)).Value!.Version);
            Assert.Equal(auditCount, await remove.Db.AuditLogs.CountAsync(value => value.EntityId == graph.Task.Id && value.Action == "TaskFileAssociationRemoved"));
            Assert.Equal(outboxCount, await remove.Db.OutboxEvents.CountAsync(value => value.AggregateId == graph.Task.Id && value.EventType == "Projects.TaskChanged.v1"));
        }

        await using (var verify = harness.CreateScope())
        {
            var tombstone = await verify.Db.Attachments.SingleAsync(value => value.Id == associationId);
            Assert.NotNull(tombstone.DeletedAt);
            Assert.Equal(graph.User.Id, tombstone.DeletedByUserId);
            Assert.Equal("Removed from task.", tombstone.DeleteReason);
            Assert.Equal(0, await verify.Db.Attachments.CountAsync(value => value.OwnerType == AttachmentOwnerType.TaskItem && value.OwnerId == graph.Task.Id && !value.DeletedAt.HasValue));
            Assert.NotNull(await verify.Db.FileObjects.SingleOrDefaultAsync(value => value.Id == graph.SourceAttachment.FileObjectId));
        }

        await using var retry = harness.CreateScope();
        var retryVersion = (await retry.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await retry.Subresources.AssociateFileAsync(graph.Task.Id, new CreateTaskFileAssociationRequest(graph.SourceAttachment.Id, retryVersion))).IsSuccess);
        await using var reattached = harness.CreateScope();
        Assert.True(await reattached.Db.Attachments.CountAsync(value => value.OwnerType == AttachmentOwnerType.TaskItem && value.OwnerId == graph.Task.Id && value.FileObjectId == graph.SourceAttachment.FileObjectId && value.DeletedAt.HasValue) >= 1);
        Assert.Equal(1, await reattached.Db.Attachments.CountAsync(value => value.OwnerType == AttachmentOwnerType.TaskItem && value.OwnerId == graph.Task.Id && value.FileObjectId == graph.SourceAttachment.FileObjectId && !value.DeletedAt.HasValue));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task FileAssociationRejectsNonCleanSourcesWithoutChangingTheTask()
    {
        foreach (var state in Enum.GetValues<FileScanStatus>().Where(value => value != FileScanStatus.Clean))
        {
            await using var harness = await ServiceHarness.CreateAsync();
            var graph = harness.Graph;
            await using (var setup = harness.CreateScope())
            {
                var source = await setup.Db.Attachments.SingleAsync(value => value.Id == graph.SourceAttachment.Id);
                source.ScanStatus = state;
                await setup.Db.SaveChangesAsync();
            }

            await using var command = harness.CreateScope();
            await AssertFileAssociationRejectedAsync(harness, command, graph.SourceAttachment.Id, "TASK_FILE_SCAN_NOT_READY");
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task FileAssociationRejectsInactiveFileObjectsWithoutChangingTheTask()
    {
        foreach (var status in new[] { FileObjectStatus.Quarantined, FileObjectStatus.Archived })
        {
            await using var harness = await ServiceHarness.CreateAsync();
            var graph = harness.Graph;
            await using (var setup = harness.CreateScope())
            {
                var file = await setup.Db.FileObjects.SingleAsync(value => value.Id == graph.SourceAttachment.FileObjectId);
                file.Status = status;
                await setup.Db.SaveChangesAsync();
            }

            await using var command = harness.CreateScope();
            await AssertFileAssociationRejectedAsync(harness, command, graph.SourceAttachment.Id,
                status == FileObjectStatus.Quarantined ? "TASK_FILE_QUARANTINED" : "TASK_FILE_ASSOCIATION_FORBIDDEN");
        }

        await using (var inconsistentDeletedHarness = await ServiceHarness.CreateAsync())
        {
            await using (var setup = inconsistentDeletedHarness.CreateScope())
            {
                var file = await setup.Db.FileObjects.SingleAsync(value => value.Id == inconsistentDeletedHarness.Graph.SourceAttachment.FileObjectId);
                file.Status = FileObjectStatus.Deleted;
                await setup.Db.SaveChangesAsync();
            }

            await using var command = inconsistentDeletedHarness.CreateScope();
            await AssertFileAssociationRejectedAsync(inconsistentDeletedHarness, command, inconsistentDeletedHarness.Graph.SourceAttachment.Id, "TASK_FILE_ASSOCIATION_FORBIDDEN");
        }

        await using (var deletedHarness = await ServiceHarness.CreateAsync())
        {
            await using (var setup = deletedHarness.CreateScope())
            {
                var file = await setup.Db.FileObjects.SingleAsync(value => value.Id == deletedHarness.Graph.SourceAttachment.FileObjectId);
                file.MarkDeleted(DateTimeOffset.UtcNow, deletedHarness.Graph.User.Id, "test");
                await setup.Db.SaveChangesAsync();
            }

            await using var command = deletedHarness.CreateScope();
            await AssertFileAssociationRejectedAsync(deletedHarness, command, deletedHarness.Graph.SourceAttachment.Id, "TASK_FILE_ASSOCIATION_FORBIDDEN");
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelAssociationSequentialCommandsAreIdempotentAndArchiveDoesNotRewriteExistingAssociations()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse label;
        await using (var setup = harness.CreateScope())
        {
            label = (await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", null))).Value!;
        }

        await using (var apply = harness.CreateScope())
        {
            var version = (await apply.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await apply.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(version))).IsSuccess);
            var afterApply = await SnapshotAsync(apply.Db, graph.Task.Id);
            Assert.True((await apply.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(version))).IsSuccess);
            Assert.Equal(1, apply.SaveRecorder.SaveTaskCommandCallCount);
            await using var verify = harness.CreateScope();
            await AssertTaskMutationSequenceAsync(verify.Db, afterApply, graph.Task.Id, []);
        }

        await using (var archive = harness.CreateScope())
        {
            var before = await SnapshotAsync(archive.Db, graph.Task.Id);
            var currentLabel = (await archive.Subresources.ListLabelsAsync(graph.Project.Id, true)).Value!.Single(value => value.Id == label.Id);
            Assert.True((await archive.Subresources.SetLabelArchiveAsync(graph.Project.Id, label.Id, currentLabel.Version, true)).IsSuccess);
            Assert.Single(await archive.Db.WorkItemLabels.Where(value => value.TaskItemId == graph.Task.Id && value.LabelId == label.Id).ToListAsync());
            await using var verify = harness.CreateScope();
            await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, []);
            Assert.Contains((await verify.Subresources.GetDetailAsync(graph.Task.Id)).Value!.Labels, value => value.Id == label.Id && value.IsArchived);
            var otherVersion = (await verify.Commands.GetAsync(graph.UnrelatedTask.Id)).Value!.Version;
            var rejected = await verify.Subresources.ApplyLabelAsync(graph.UnrelatedTask.Id, label.Id, new TaskLabelAssociationRequest(otherVersion));
            Assert.Equal("TASK_LABEL_ARCHIVED", Code(rejected.Error));
        }

        await using (var duplicate = harness.CreateScope())
        {
            var before = await SnapshotAsync(duplicate.Db, graph.Task.Id);
            var currentVersion = (await duplicate.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            var staleVersion = Math.Max(1, currentVersion - 1);

            // Archived labels remain visible through the existing association.
            // Both duplicate PUT forms are no-ops: no task version, audit,
            // outbox, or task-command save may be produced.
            Assert.True((await duplicate.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(currentVersion))).IsSuccess);
            Assert.True((await duplicate.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(staleVersion))).IsSuccess);
            Assert.Equal(0, duplicate.SaveRecorder.SaveTaskCommandCallCount);

            await using var verify = harness.CreateScope();
            await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, []);
            Assert.Single(await verify.Db.WorkItemLabels.Where(value => value.TaskItemId == graph.Task.Id && value.LabelId == label.Id).ToListAsync());
        }

        await using (var remove = harness.CreateScope())
        {
            var version = (await remove.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await remove.Subresources.RemoveLabelAsync(graph.Task.Id, label.Id, version)).IsSuccess);
            var afterRemove = await SnapshotAsync(remove.Db, graph.Task.Id);
            Assert.True((await remove.Subresources.RemoveLabelAsync(graph.Task.Id, label.Id, version)).IsSuccess);
            Assert.Equal(1, remove.SaveRecorder.SaveTaskCommandCallCount);
            await using var verify = harness.CreateScope();
            await AssertTaskMutationSequenceAsync(verify.Db, afterRemove, graph.Task.Id, []);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task LabelScopeIsolationRejectsOtherProjectAndOtherTenantWithoutSideEffects()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse otherProjectLabel;
        await using (var setup = harness.CreateScope())
        {
            otherProjectLabel = (await setup.Subresources.CreateLabelAsync(graph.OtherProject.Id, new CreateProjectTaskLabelRequest("Other project", "secret label description"))).Value!;
        }
        var otherTenantLabelId = await harness.SeedOtherTenantLabelAsync();

        await using var command = harness.CreateScope();
        var before = await SnapshotAsync(command.Db, graph.Task.Id);
        var version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        var projectMismatch = await command.Subresources.ApplyLabelAsync(graph.Task.Id, otherProjectLabel.Id, new TaskLabelAssociationRequest(version));
        var tenantMismatch = await command.Subresources.ApplyLabelAsync(graph.Task.Id, otherTenantLabelId, new TaskLabelAssociationRequest(version));
        Assert.Equal("TASK_LABEL_PROJECT_MISMATCH", Code(projectMismatch.Error));
        Assert.Equal("TASK_LABEL_NOT_FOUND", Code(tenantMismatch.Error));
        Assert.DoesNotContain("secret", projectMismatch.Error!, StringComparison.OrdinalIgnoreCase);
        await using var verify = harness.CreateScope();
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, []);
        Assert.Empty(await verify.Db.WorkItemLabels.Where(value => value.TaskItemId == graph.Task.Id).ToListAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task WatchSourcesNoOpsAndPrivacyAreActorSpecific()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using (var creator = harness.CreateScope())
        {
            var state = (await creator.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.True(state.IsWatching);
            Assert.False((await creator.Db.WorkItemWatchStates.SingleAsync(value => value.TaskItemId == graph.Task.Id && value.UserId == graph.User.Id)).IsManualWatch);
            Assert.False(state.IsExplicitOptOut);
            Assert.Contains(nameof(WorkItemWatchAutomaticSource.Creator), state.AutomaticSources);
        }

        await using (var owner = harness.CreateScope())
        {
            var version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.SetAssigneeAsync(graph.Task.Id, new TaskRelationshipUserRequest(graph.MentionUser.Id, version))).IsSuccess);
            version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.AddCollaboratorAsync(graph.Task.Id, new TaskCollaboratorRequest(graph.CollaboratorUser.Id, version))).IsSuccess);
            version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.SetReviewerAsync(graph.Task.Id, new TaskRelationshipUserRequest(graph.ReviewerUser.Id, version))).IsSuccess);
        }
        await using (var assignee = harness.CreateScope(graph.MentionUser.Id))
            Assert.Contains(nameof(WorkItemWatchAutomaticSource.PrimaryAssignee), (await assignee.Commands.GetWatchStateAsync(graph.Task.Id)).Value!.AutomaticSources);
        await using (var collaborator = harness.CreateScope(graph.CollaboratorUser.Id))
            Assert.Contains(nameof(WorkItemWatchAutomaticSource.Collaborator), (await collaborator.Commands.GetWatchStateAsync(graph.Task.Id)).Value!.AutomaticSources);
        await using (var reviewer = harness.CreateScope(graph.ReviewerUser.Id))
            Assert.Contains(nameof(WorkItemWatchAutomaticSource.Reviewer), (await reviewer.Commands.GetWatchStateAsync(graph.Task.Id)).Value!.AutomaticSources);

        // Removing each relationship independently clears only its own source;
        // the command and reconciliation remain one Task save boundary.
        Guid fallbackGroupId;
        await using (var setup = harness.CreateScope())
        {
            var group = new Group
            {
                TenantId = graph.Tenant.Id,
                WorkspaceId = graph.Workspace.Id,
                Name = "Assignee fallback",
                Slug = $"assignee-fallback-{Guid.NewGuid():N}",
                GroupType = GroupType.Other,
                Status = GroupStatus.Active,
                CreatedByUserId = graph.User.Id
            };
            setup.Db.Groups.Add(group);
            await setup.Db.SaveChangesAsync();
            fallbackGroupId = group.Id;
        }
        await using (var owner = harness.CreateScope())
        {
            var version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.SetTargetGroupAsync(graph.Task.Id, new TaskTargetGroupRequest(fallbackGroupId, version))).IsSuccess);
            version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.SetAssigneeAsync(graph.Task.Id, new TaskRelationshipUserRequest(null, version))).IsSuccess);
            version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.RemoveCollaboratorAsync(graph.Task.Id, graph.CollaboratorUser.Id, version)).IsSuccess);
            version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.SetReviewerAsync(graph.Task.Id, new TaskRelationshipUserRequest(null, version))).IsSuccess);
            version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.AddCollaboratorAsync(graph.Task.Id, new TaskCollaboratorRequest(graph.User.Id, version))).IsSuccess);
            version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.RemoveCollaboratorAsync(graph.Task.Id, graph.User.Id, version)).IsSuccess);
        }
        await using (var assignee = harness.CreateScope(graph.MentionUser.Id))
        {
            var state = (await assignee.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.DoesNotContain(nameof(WorkItemWatchAutomaticSource.PrimaryAssignee), state.AutomaticSources);
            Assert.False(state.IsWatching);
        }
        await using (var collaborator = harness.CreateScope(graph.CollaboratorUser.Id))
        {
            var state = (await collaborator.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.DoesNotContain(nameof(WorkItemWatchAutomaticSource.Collaborator), state.AutomaticSources);
            Assert.False(state.IsWatching);
        }
        await using (var reviewer = harness.CreateScope(graph.ReviewerUser.Id))
        {
            var state = (await reviewer.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.DoesNotContain(nameof(WorkItemWatchAutomaticSource.Reviewer), state.AutomaticSources);
            Assert.False(state.IsWatching);
        }
        await using (var creator = harness.CreateScope())
        {
            var state = (await creator.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.Contains(nameof(WorkItemWatchAutomaticSource.Creator), state.AutomaticSources);
            Assert.DoesNotContain(nameof(WorkItemWatchAutomaticSource.Collaborator), state.AutomaticSources);
            Assert.True(state.IsWatching);
        }

        await using (var owner = harness.CreateScope())
        {
            var version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.AddCollaboratorAsync(graph.Task.Id, new TaskCollaboratorRequest(graph.OptOutUser.Id, version))).IsSuccess);
        }
        await using (var optOut = harness.CreateScope(graph.OptOutUser.Id))
        {
            var state = (await optOut.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.True((await optOut.Commands.UnwatchAsync(graph.Task.Id, new TaskWatchRequest(state.Version))).IsSuccess);
        }
        await using (var owner = harness.CreateScope())
        {
            var version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.RemoveCollaboratorAsync(graph.Task.Id, graph.OptOutUser.Id, version)).IsSuccess);
            version = (await owner.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await owner.Commands.AddCollaboratorAsync(graph.Task.Id, new TaskCollaboratorRequest(graph.OptOutUser.Id, version))).IsSuccess);
        }
        await using (var optOut = harness.CreateScope(graph.OptOutUser.Id))
        {
            var state = (await optOut.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.True(state.IsExplicitOptOut);
            Assert.False(state.IsWatching);
            Assert.Contains(nameof(WorkItemWatchAutomaticSource.Collaborator), state.AutomaticSources);
        }

        await using (var manual = harness.CreateScope(graph.ManualWatchUser.Id))
        {
            var first = await manual.Commands.WatchAsync(graph.Task.Id, new TaskWatchRequest(0));
            Assert.True(first.IsSuccess);
            var firstState = first.Value!;
            Assert.True((await manual.Db.WorkItemWatchStates.SingleAsync(value => value.TaskItemId == graph.Task.Id && value.UserId == graph.ManualWatchUser.Id)).IsManualWatch);
            var repeated = await manual.Commands.WatchAsync(graph.Task.Id, new TaskWatchRequest(firstState.Version));
            Assert.True(repeated.IsSuccess);
            Assert.Equal(firstState.Version, repeated.Value!.Version);
            Assert.Equal(1, manual.SaveRecorder.SaveTaskCommandCallCount);
        }
        await using (var optOut = harness.CreateScope(graph.OptOutUser.Id))
        {
            var current = (await optOut.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            var first = await optOut.Commands.UnwatchAsync(graph.Task.Id, new TaskWatchRequest(current.Version));
            Assert.True(first.IsSuccess);
            var repeated = await optOut.Commands.UnwatchAsync(graph.Task.Id, new TaskWatchRequest(first.Value!.Version));
            Assert.True(repeated.IsSuccess);
            Assert.True(repeated.Value!.IsExplicitOptOut);
            Assert.Equal(0, optOut.SaveRecorder.SaveTaskCommandCallCount);
        }
        await using (var creator = harness.CreateScope())
        {
            var detail = (await creator.Subresources.GetDetailAsync(graph.Task.Id)).Value!;
            var own = (await creator.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
            Assert.Equal(own.IsExplicitOptOut, detail.WatchState.IsExplicitOptOut);
            Assert.NotEqual((await harness.GetWatchStateAsync(graph.Task.Id, graph.OptOutUser.Id)).IsExplicitOptOut, detail.WatchState.IsExplicitOptOut);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task WatchAndUnwatchRaceCommitsOneWinnerAndClearsTheLoser()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        var expected = (await first.Commands.GetWatchStateAsync(graph.Task.Id)).Value!.Version;
        Assert.Equal(expected, (await second.Commands.GetWatchStateAsync(graph.Task.Id)).Value!.Version);
        var before = await SnapshotAsync(first.Db, graph.Task.Id);
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Commands.WatchAsync(graph.Task.Id, new TaskWatchRequest(expected))),
            ExecuteAsync(second, request => request.Commands.UnwatchAsync(graph.Task.Id, new TaskWatchRequest(expected))));

        Assert.Equal(1, results.Count(value => value.Result.IsSuccess));
        Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.Saved);
        var loser = Assert.Single(results, value => !value.Result.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Equal(TaskCommandSaveResult.ConcurrencyConflict, loser.Scope.SaveRecorder.LastSaveOutcome?.Result);
        Assert.True(loser.Scope.SaveRecorder.ClearTrackingCallCount >= 1);
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());
        await using var verify = harness.CreateScope();
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, [results.Single(value => value.Result.IsSuccess).Result.Value!.IsExplicitOptOut ? "TaskWatchOptOut" : "TaskWatchEnabled"]);
        var final = (await verify.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
        Assert.Equal(results.Single(value => value.Result.IsSuccess).Result.Value!.IsExplicitOptOut, final.IsExplicitOptOut);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelRemoveRaceProducesOneMutationAndAnIdempotentRecovery()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse label;
        await using (var setup = harness.CreateScope())
        {
            label = (await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", null))).Value!;
            var version = (await setup.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await setup.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(version))).IsSuccess);
        }
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        var expected = (await first.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        var before = await SnapshotAsync(first.Db, graph.Task.Id);
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Subresources.RemoveLabelAsync(graph.Task.Id, label.Id, expected)),
            ExecuteAsync(second, request => request.Subresources.RemoveLabelAsync(graph.Task.Id, label.Id, expected)));
        Assert.All(results, value => Assert.True(value.Result.IsSuccess));
        Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.Saved);
        var recovery = Assert.Single(results, value => value.Scope.SaveRecorder.LastSaveOutcome?.Result == TaskCommandSaveResult.ConcurrencyConflict);
        Assert.True(recovery.Scope.SaveRecorder.RecoveryPathEntered);
        Assert.True(recovery.Scope.SaveRecorder.ClearTrackingCallCount >= 1);
        Assert.Empty(recovery.Scope.Db.ChangeTracker.Entries());
        await using var verify = harness.CreateScope();
        Assert.Empty(await verify.Db.WorkItemLabels.Where(value => value.TaskItemId == graph.Task.Id && value.LabelId == label.Id).ToListAsync());
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, ["TaskLabelRemoved"]);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelApplyRemoveOverlapCommitsOnlyTheActualRemove()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse label;
        await using (var setup = harness.CreateScope())
        {
            label = (await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", null))).Value!;
            var version = (await setup.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            Assert.True((await setup.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(version))).IsSuccess);
        }

        await using var apply = harness.CreateScope();
        await using var remove = harness.CreateScope();
        var expected = (await apply.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.Equal(expected, (await remove.Commands.GetAsync(graph.Task.Id)).Value!.Version);
        var before = await SnapshotAsync(apply.Db, graph.Task.Id);

        harness.Race.ArmSingleWriterHold();
        var removeTask = remove.Subresources.RemoveLabelAsync(graph.Task.Id, label.Id, expected);
        await harness.Race.WaitForSingleWriterArrivalAsync();
        var applyResult = await apply.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(expected));
        Assert.True(applyResult.IsSuccess);
        Assert.Equal(0, apply.SaveRecorder.SaveTaskCommandCallCount);
        harness.Race.ReleaseSingleWriter();
        Assert.True((await removeTask).IsSuccess);
        Assert.Equal(1, remove.SaveRecorder.SaveTaskCommandCallCount);

        await using var verify = harness.CreateScope();
        Assert.Empty(await verify.Db.WorkItemLabels.Where(value => value.TaskItemId == graph.Task.Id && value.LabelId == label.Id).ToListAsync());
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, ["TaskLabelRemoved"]);
        Assert.Equal("unrelated", await verify.Db.TaskItems.Where(value => value.Id == graph.UnrelatedTask.Id).Select(value => value.Title).SingleAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task LabelApplyRemoveOverlapCommitsOnlyTheActualApplyWhenAssociationIsAbsent()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using var setup = harness.CreateScope();
        var label = (await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", null))).Value!;
        var expected = (await setup.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        var before = await SnapshotAsync(setup.Db, graph.Task.Id);
        await using var apply = harness.CreateScope();
        await using var remove = harness.CreateScope();

        harness.Race.ArmSingleWriterHold();
        var applyTask = apply.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(expected));
        await harness.Race.WaitForSingleWriterArrivalAsync();
        var removeResult = await remove.Subresources.RemoveLabelAsync(graph.Task.Id, label.Id, expected);
        Assert.True(removeResult.IsSuccess);
        Assert.Equal(0, remove.SaveRecorder.SaveTaskCommandCallCount);
        harness.Race.ReleaseSingleWriter();
        Assert.True((await applyTask).IsSuccess);
        Assert.Equal(1, apply.SaveRecorder.SaveTaskCommandCallCount);

        await using var verify = harness.CreateScope();
        Assert.Single(await verify.Db.WorkItemLabels.Where(value => value.TaskItemId == graph.Task.Id && value.LabelId == label.Id).ToListAsync());
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, ["TaskLabelApplied"]);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task TaskFileAssociateRemoveOverlapCommitsOnlyTheActualRemoveAndKeepsTheFile()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        Guid associationId;
        await using (var setup = harness.CreateScope())
        {
            var version = (await setup.Commands.GetAsync(graph.Task.Id)).Value!.Version;
            associationId = (await setup.Subresources.AssociateFileAsync(graph.Task.Id, new CreateTaskFileAssociationRequest(graph.SourceAttachment.Id, version))).Value!.Id;
        }

        await using var associate = harness.CreateScope();
        await using var remove = harness.CreateScope();
        var expected = (await associate.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.Equal(expected, (await remove.Commands.GetAsync(graph.Task.Id)).Value!.Version);
        var before = await SnapshotAsync(associate.Db, graph.Task.Id);

        harness.Race.ArmSingleWriterHold();
        var removeTask = remove.Subresources.RemoveFileAsync(graph.Task.Id, associationId, expected);
        await harness.Race.WaitForSingleWriterArrivalAsync();
        var associateResult = await associate.Subresources.AssociateFileAsync(graph.Task.Id, new CreateTaskFileAssociationRequest(graph.SourceAttachment.Id, expected));
        Assert.True(associateResult.IsSuccess);
        Assert.Equal(0, associate.SaveRecorder.SaveTaskCommandCallCount);
        harness.Race.ReleaseSingleWriter();
        Assert.True((await removeTask).IsSuccess);
        Assert.Equal(1, remove.SaveRecorder.SaveTaskCommandCallCount);

        await using var verify = harness.CreateScope();
        Assert.Empty(await verify.Db.Attachments.Where(value => value.Id == associationId && !value.DeletedAt.HasValue).ToListAsync());
        Assert.NotNull(await verify.Db.FileObjects.SingleOrDefaultAsync(value => value.Id == graph.SourceAttachment.FileObjectId));
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, ["TaskFileAssociationRemoved"]);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task TaskFileAssociateRemoveOverlapCommitsOnlyTheActualAssociateWhenAssociationIsAbsent()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using var apply = harness.CreateScope();
        await using var remove = harness.CreateScope();
        var expected = (await apply.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        var before = await SnapshotAsync(apply.Db, graph.Task.Id);

        harness.Race.ArmSingleWriterHold();
        var associateTask = apply.Subresources.AssociateFileAsync(graph.Task.Id, new CreateTaskFileAssociationRequest(graph.SourceAttachment.Id, expected));
        await harness.Race.WaitForSingleWriterArrivalAsync();
        var removeResult = await remove.Subresources.RemoveFileAsync(graph.Task.Id, Guid.NewGuid(), expected);
        Assert.True(removeResult.IsSuccess);
        Assert.Equal(0, remove.SaveRecorder.SaveTaskCommandCallCount);
        harness.Race.ReleaseSingleWriter();
        Assert.True((await associateTask).IsSuccess);
        Assert.Equal(1, apply.SaveRecorder.SaveTaskCommandCallCount);

        await using var verify = harness.CreateScope();
        Assert.Single(await verify.Db.Attachments.Where(value => value.OwnerType == AttachmentOwnerType.TaskItem && value.OwnerId == graph.Task.Id && !value.DeletedAt.HasValue).ToListAsync());
        Assert.NotNull(await verify.Db.FileObjects.SingleOrDefaultAsync(value => value.Id == graph.SourceAttachment.FileObjectId));
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, ["TaskFileAssociated"]);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task Prompt2CRepresentativeTaskMutationsKeepOutboxEnvelopeVersionsAligned()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        await using var command = harness.CreateScope();
        var label = (await command.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", null))).Value!;
        var before = await SnapshotAsync(command.Db, graph.Task.Id);

        var version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Subresources.ApplyLabelAsync(graph.Task.Id, label.Id, new TaskLabelAssociationRequest(version))).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Subresources.RemoveLabelAsync(graph.Task.Id, label.Id, version)).IsSuccess);

        var watch = (await command.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
        Assert.True((await command.Commands.WatchAsync(graph.Task.Id, new TaskWatchRequest(watch.Version))).IsSuccess);
        watch = (await command.Commands.GetWatchStateAsync(graph.Task.Id)).Value!;
        Assert.True((await command.Commands.UnwatchAsync(graph.Task.Id, new TaskWatchRequest(watch.Version))).IsSuccess);

        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetAssigneeAsync(graph.Task.Id, new TaskRelationshipUserRequest(graph.MentionUser.Id, version))).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.AddCollaboratorAsync(graph.Task.Id, new TaskCollaboratorRequest(graph.CollaboratorUser.Id, version))).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetReviewerAsync(graph.Task.Id, new TaskRelationshipUserRequest(graph.ReviewerUser.Id, version))).IsSuccess);

        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        var association = await command.Subresources.AssociateFileAsync(graph.Task.Id, new CreateTaskFileAssociationRequest(graph.SourceAttachment.Id, version));
        Assert.True(association.IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Subresources.RemoveFileAsync(graph.Task.Id, association.Value!.Id, version)).IsSuccess);

        await using var verify = harness.CreateScope();
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id,
        [
            "TaskLabelApplied", "TaskLabelRemoved", "TaskWatchEnabled", "TaskWatchOptOut",
            "TaskAssigneeChanged", "TaskCollaboratorAdded", "TaskReviewerChanged",
            "TaskFileAssociated", "TaskFileAssociationRemoved"
        ]);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task PrimaryAssigneeClearWithoutReviewerRemovesAutomaticSourceAndCommitsOneEvent()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var groupId = await CreateTargetGroupAsync(harness, "assignee-clear");
        await using var command = harness.CreateScope();
        var version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetTargetGroupAsync(graph.Task.Id, new TaskTargetGroupRequest(groupId, version))).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetAssigneeAsync(graph.Task.Id, new TaskRelationshipUserRequest(graph.MentionUser.Id, version))).IsSuccess);
        Assert.Null((await command.Commands.GetAsync(graph.Task.Id)).Value!.Reviewer);
        var creatorWatchBefore = await harness.GetWatchStateAsync(graph.Task.Id, graph.User.Id);
        var before = await SnapshotAsync(command.Db, graph.Task.Id);

        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        var result = await command.Commands.SetAssigneeAsync(graph.Task.Id, new TaskRelationshipUserRequest(null, version));
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Task.PrimaryAssignee);

        await using var verify = harness.CreateScope();
        var task = await verify.Db.TaskItems.SingleAsync(value => value.Id == graph.Task.Id);
        Assert.Null(task.PrimaryAssigneeUserId);
        var assigneeWatch = await harness.GetWatchStateAsync(graph.Task.Id, graph.MentionUser.Id);
        Assert.False(assigneeWatch.AutomaticSources.HasFlag(WorkItemWatchAutomaticSource.PrimaryAssignee));
        Assert.False(assigneeWatch.IsWatching);
        var creatorWatchAfter = await harness.GetWatchStateAsync(graph.Task.Id, graph.User.Id);
        Assert.Equal(creatorWatchBefore.IsManualWatch, creatorWatchAfter.IsManualWatch);
        Assert.Equal(creatorWatchBefore.AutomaticSources, creatorWatchAfter.AutomaticSources);
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, ["TaskAssigneeChanged"]);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task AutomaticWatchSourceAddAndRemoveMutationsKeepOutboxEnvelopeVersionsAligned()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        var groupId = await CreateTargetGroupAsync(harness, "automatic-envelope");
        await using var command = harness.CreateScope();
        var version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetTargetGroupAsync(graph.Task.Id, new TaskTargetGroupRequest(groupId, version))).IsSuccess);
        var before = await SnapshotAsync(command.Db, graph.Task.Id);

        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetAssigneeAsync(graph.Task.Id, new TaskRelationshipUserRequest(graph.MentionUser.Id, version))).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetAssigneeAsync(graph.Task.Id, new TaskRelationshipUserRequest(null, version))).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.AddCollaboratorAsync(graph.Task.Id, new TaskCollaboratorRequest(graph.CollaboratorUser.Id, version))).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.RemoveCollaboratorAsync(graph.Task.Id, graph.CollaboratorUser.Id, version)).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetReviewerAsync(graph.Task.Id, new TaskRelationshipUserRequest(graph.ReviewerUser.Id, version))).IsSuccess);
        version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        Assert.True((await command.Commands.SetReviewerAsync(graph.Task.Id, new TaskRelationshipUserRequest(null, version))).IsSuccess);

        await using var verify = harness.CreateScope();
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id,
        [
            "TaskAssigneeChanged", "TaskAssigneeChanged", "TaskCollaboratorAdded",
            "TaskCollaboratorRemoved", "TaskReviewerChanged", "TaskReviewerChanged"
        ]);
        Assert.False((await harness.GetWatchStateAsync(graph.Task.Id, graph.MentionUser.Id)).IsWatching);
        Assert.False((await harness.GetWatchStateAsync(graph.Task.Id, graph.CollaboratorUser.Id)).IsWatching);
        Assert.False((await harness.GetWatchStateAsync(graph.Task.Id, graph.ReviewerUser.Id)).IsWatching);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task FileAssociationRejectsCanonicalFileObjectWorkspaceMismatch()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        Guid attachmentId;
        await using (var setup = harness.CreateScope())
        {
            var otherWorkspace = new Workspace
            {
                TenantId = graph.Tenant.Id,
                Name = "Canonical file workspace mismatch",
                Slug = $"canonical-file-mismatch-{Guid.NewGuid():N}",
                CreatedByUserId = graph.User.Id
            };
            var file = new FileObject
            {
                TenantId = graph.Tenant.Id,
                WorkspaceId = otherWorkspace.Id,
                UploadedByUserId = graph.User.Id,
                OriginalFileName = "mismatch.txt",
                StorageKey = $"test/{Guid.NewGuid():N}",
                ContentType = "text/plain",
                SizeBytes = 1,
                Status = FileObjectStatus.Active
            };
            var attachment = SourceAttachment(graph.Tenant.Id, graph.Workspace.Id, graph.User.Id, file, "mismatch.txt");
            setup.Db.AddRange(otherWorkspace, file, attachment);
            await setup.Db.SaveChangesAsync();
            attachmentId = attachment.Id;
        }

        await AssertFileAssociationRejectedAsync(harness, attachmentId, "TASK_FILE_ASSOCIATION_FORBIDDEN");
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1Prompt2C")]
    public async Task FileScopeAndDeletedSourceRejectionsUseRealAuthorizationWithoutSideEffects()
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        Guid otherProjectAttachmentId;
        Guid otherWorkspaceAttachmentId;
        await using (var setup = harness.CreateScope())
        {
            var otherProjectFile = new FileObject { TenantId = graph.Tenant.Id, WorkspaceId = graph.Workspace.Id, ProjectId = graph.OtherProject.Id, UploadedByUserId = graph.User.Id, OriginalFileName = "project-b.txt", StorageKey = $"test/{Guid.NewGuid():N}", ContentType = "text/plain", SizeBytes = 1, Status = FileObjectStatus.Active };
            var otherProjectAttachment = SourceAttachment(graph.Tenant.Id, graph.Workspace.Id, graph.User.Id, otherProjectFile, "project-b.txt");
            var otherWorkspace = new Workspace { TenantId = graph.Tenant.Id, Name = "Isolated workspace", Slug = $"isolated-workspace-{Guid.NewGuid():N}", CreatedByUserId = graph.OtherWorkspaceUser.Id };
            var otherWorkspaceFile = new FileObject { TenantId = graph.Tenant.Id, WorkspaceId = otherWorkspace.Id, UploadedByUserId = graph.OtherWorkspaceUser.Id, OriginalFileName = "workspace-b.txt", StorageKey = $"test/{Guid.NewGuid():N}", ContentType = "text/plain", SizeBytes = 1, Status = FileObjectStatus.Active };
            var otherWorkspaceAttachment = SourceAttachment(graph.Tenant.Id, otherWorkspace.Id, graph.OtherWorkspaceUser.Id, otherWorkspaceFile, "workspace-b.txt");
            setup.Db.AddRange(otherProjectFile, otherProjectAttachment, otherWorkspace, otherWorkspaceFile, otherWorkspaceAttachment);
            await setup.Db.SaveChangesAsync();
            otherProjectAttachmentId = otherProjectAttachment.Id;
            otherWorkspaceAttachmentId = otherWorkspaceAttachment.Id;
        }
        var otherTenantAttachmentId = await harness.SeedOtherTenantAttachmentAsync();
        await AssertFileAssociationRejectedAsync(harness, otherProjectAttachmentId, "TASK_FILE_ASSOCIATION_FORBIDDEN");
        await AssertFileAssociationRejectedAsync(harness, otherWorkspaceAttachmentId, "TASK_FILE_ASSOCIATION_FORBIDDEN");
        await AssertFileAssociationRejectedAsync(harness, otherTenantAttachmentId, "TASK_FILE_ASSOCIATION_FORBIDDEN");

        await using (var deleted = harness.CreateScope())
        {
            var source = await deleted.Db.Attachments.SingleAsync(value => value.Id == graph.SourceAttachment.Id);
            source.MarkDeleted(DateTimeOffset.UtcNow, graph.User.Id, "Source removed");
            await deleted.Db.SaveChangesAsync();
        }
        await AssertFileAssociationRejectedAsync(harness, graph.SourceAttachment.Id, "TASK_FILE_ASSOCIATION_FORBIDDEN");
    }

    private static async Task<Guid> CreateTargetGroupAsync(ServiceHarness harness, string purpose)
    {
        var graph = harness.Graph;
        await using var setup = harness.CreateScope();
        var group = new Group
        {
            TenantId = graph.Tenant.Id,
            WorkspaceId = graph.Workspace.Id,
            Name = $"Target group {purpose}",
            Slug = $"target-group-{purpose}-{Guid.NewGuid():N}",
            GroupType = GroupType.Other,
            Status = GroupStatus.Active,
            CreatedByUserId = graph.User.Id
        };
        setup.Db.Groups.Add(group);
        await setup.Db.SaveChangesAsync();
        return group.Id;
    }

    private static Attachment SourceAttachment(Guid tenantId, Guid workspaceId, Guid userId, FileObject file, string fileName) => new()
    {
        TenantId = tenantId,
        FileObjectId = file.Id,
        FileObject = file,
        WorkspaceId = workspaceId,
        OwnerType = AttachmentOwnerType.Workspace,
        OwnerId = workspaceId,
        OwnerUserId = userId,
        UploadedByUserId = userId,
        FileName = fileName,
        StoredFileName = fileName,
        FilePath = fileName,
        ContentType = "text/plain",
        Extension = ".txt",
        SizeBytes = 1,
        StorageProvider = "test",
        StorageKey = file.StorageKey,
        ScanStatus = FileScanStatus.Clean
    };

    private static async Task AssertFileAssociationRejectedAsync(ServiceHarness harness, Guid attachmentId, string code)
    {
        await using var command = harness.CreateScope();
        await AssertFileAssociationRejectedAsync(harness, command, attachmentId, code);
    }

    private static async Task AssertFileAssociationRejectedAsync(ServiceHarness harness, RequestScope command, Guid attachmentId, string code)
    {
        var graph = harness.Graph;
        var sourceBefore = await harness.SnapshotAttachmentAsync(attachmentId);
        var before = await SnapshotAsync(command.Db, graph.Task.Id);
        var version = (await command.Commands.GetAsync(graph.Task.Id)).Value!.Version;
        var result = await command.Subresources.AssociateFileAsync(graph.Task.Id, new CreateTaskFileAssociationRequest(attachmentId, version));
        Assert.Equal(code, Code(result.Error));
        Assert.Equal(0, command.SaveRecorder.SaveTaskCommandCallCount);
        await using var verify = harness.CreateScope();
        await AssertTaskMutationSequenceAsync(verify.Db, before, graph.Task.Id, []);
        Assert.Empty(await verify.Db.Attachments.Where(value => value.OwnerType == AttachmentOwnerType.TaskItem && value.OwnerId == graph.Task.Id && !value.DeletedAt.HasValue).ToListAsync());
        Assert.Equal(sourceBefore, await harness.SnapshotAttachmentAsync(attachmentId));
    }

    private static TaskUpdateDetailsRequest Details(
        string title,
        long expectedVersion,
        TaskPriority priority = TaskPriority.Medium,
        int progress = 0,
        DateOnly? plannedStart = null,
        DateOnly? plannedEnd = null) =>
        new(title, null, priority, plannedStart, plannedEnd, progress, expectedVersion);

    private static async Task<(RequestScope Scope, Coglatas.Application.Common.Result<T> Result)> ExecuteAsync<T>(
        RequestScope scope,
        Func<RequestScope, Task<Coglatas.Application.Common.Result<T>>> command) =>
        (scope, await command(scope));

    private static async Task<(RequestScope Scope, Coglatas.Application.Common.Result Result)> ExecuteAsync(
        RequestScope scope,
        Func<RequestScope, Task<Coglatas.Application.Common.Result>> command) =>
        (scope, await command(scope));

    private static async Task<(CommentRaceOperation Operation, RequestScope Scope, bool IsSuccess, string? Error)> ExecuteCommentUpdateAsync(
        RequestScope scope,
        Func<RequestScope, Task<Coglatas.Application.Common.Result<TaskCommentResponse>>> command)
    {
        var result = await command(scope);
        return (CommentRaceOperation.Update, scope, result.IsSuccess, result.Error);
    }

    private static async Task<(CommentRaceOperation Operation, RequestScope Scope, bool IsSuccess, string? Error)> ExecuteCommentDeleteAsync(
        RequestScope scope,
        Func<RequestScope, Task<Coglatas.Application.Common.Result>> command)
    {
        var result = await command(scope);
        return (CommentRaceOperation.Delete, scope, result.IsSuccess, result.Error);
    }

    private static async Task<(RequestScope Scope, bool IsSuccess, string? Error)> ExecuteChecklistUpdateAsync(
        RequestScope scope,
        Func<RequestScope, Task<Coglatas.Application.Common.Result<TaskChecklistResponse>>> command)
    {
        var result = await command(scope);
        return (scope, result.IsSuccess, result.Error);
    }

    private static async Task<(RequestScope Scope, bool IsSuccess, string? Error)> ExecuteChecklistDeleteAsync(
        RequestScope scope,
        Func<RequestScope, Task<Coglatas.Application.Common.Result>> command)
    {
        var result = await command(scope);
        return (scope, result.IsSuccess, result.Error);
    }

    private static (RequestScope Scope, bool IsSuccess, string? Error) AssertOneWinner(
        IReadOnlyList<(RequestScope Scope, bool IsSuccess, string? Error)> results)
    {
        Assert.Equal(1, results.Count(value => value.IsSuccess));
        var loser = results.Single(value => !value.IsSuccess);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Error));
        return loser;
    }

    private static async Task<SideEffectSnapshot> SnapshotAsync(ServiceHarness harness)
    {
        var graph = harness.Graph;
        await using var scope = harness.CreateScope();
        return await SnapshotAsync(scope.Db, graph.Task.Id);
    }

    private static async Task<SideEffectSnapshot> SnapshotAsync(AppDbContext db, Guid taskId)
    {
        var task = await db.TaskItems.SingleAsync(value => value.Id == taskId);
        var audit = await db.AuditLogs.Where(value => value.EntityId == taskId).ToListAsync();
        return new SideEffectSnapshot(
            task.VersionNo,
            audit.Count,
            // Semantic Task events can share the aggregate identity. This
            // snapshot tracks only the general invalidation sequence asserted
            // by AssertTaskMutationSequenceAsync below.
            await db.OutboxEvents.CountAsync(value =>
                value.AggregateId == taskId &&
                value.EventType == "Projects.TaskChanged.v1"),
            audit.GroupBy(value => value.Action).ToDictionary(group => group.Key, group => group.Count()));
    }

    private static async Task<TaskSubtaskResponse> CreateSubtaskAsync(ServiceHarness harness, string title)
    {
        var graph = harness.Graph;
        await using var scope = harness.CreateScope();
        var result = await scope.Subresources.CreateSubtaskAsync(graph.Task.Id, new CreateTaskSubtaskRequest(title, null, TaskPriority.Medium));
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static async Task<TaskItem> TaskRowAsync(ServiceHarness harness, Guid taskId)
    {
        await using var scope = harness.CreateScope();
        return await scope.Db.TaskItems.SingleAsync(value => value.Id == taskId);
    }

    private static async Task<(TaskItem Child, TaskItem Parent)> AssertChildMutationRaceAsync(
        ServiceHarness harness,
        Guid childId,
        long expectedVersion,
        string childAction,
        Func<RequestScope, Task<Coglatas.Application.Common.Result<TaskCommandResponse>>> command,
        bool includeDeleted = false)
    {
        var graph = harness.Graph;
        var beforeChild = await TaskRowAsync(harness, childId);
        var beforeParent = await TaskRowAsync(harness, graph.Task.Id);
        await using var baseline = harness.CreateScope();
        var childAuditCount = await baseline.Db.AuditLogs.CountAsync(value => value.EntityId == childId && value.Action == childAction);
        var parentAuditCount = await baseline.Db.AuditLogs.CountAsync(value => value.EntityId == graph.Task.Id && value.Action == "TaskSubtasksChanged");
        var childOutboxCount = await baseline.Db.OutboxEvents.CountAsync(value => value.AggregateId == childId && value.EventType == "Projects.TaskChanged.v1");
        var parentOutboxCount = await baseline.Db.OutboxEvents.CountAsync(value => value.AggregateId == graph.Task.Id && value.EventType == "Projects.TaskChanged.v1");
        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        if (includeDeleted)
        {
            Assert.Equal(expectedVersion, await first.Db.TaskItems.Where(value => value.Id == childId).Select(value => value.VersionNo).SingleAsync());
            Assert.Equal(expectedVersion, await second.Db.TaskItems.Where(value => value.Id == childId).Select(value => value.VersionNo).SingleAsync());
        }
        else
        {
            Assert.Equal(expectedVersion, (await first.Commands.GetAsync(childId)).Value!.Version);
            Assert.Equal(expectedVersion, (await second.Commands.GetAsync(childId)).Value!.Version);
        }
        harness.Race.Arm();
        var results = await Task.WhenAll(ExecuteAsync(first, command), ExecuteAsync(second, command));
        var loser = results.Single(value => !value.Result.IsSuccess);
        Assert.Equal(1, results.Count(value => value.Result.IsSuccess));
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using var verify = harness.CreateScope();
        var child = await verify.Db.TaskItems.SingleAsync(value => value.Id == childId);
        var parent = await verify.Db.TaskItems.SingleAsync(value => value.Id == graph.Task.Id);
        Assert.Equal(beforeChild.VersionNo + 1, child.VersionNo);
        Assert.Equal(beforeParent.VersionNo + 1, parent.VersionNo);
        Assert.Equal(childAuditCount + 1, await verify.Db.AuditLogs.CountAsync(value => value.EntityId == childId && value.Action == childAction));
        Assert.Equal(parentAuditCount + 1, await verify.Db.AuditLogs.CountAsync(value => value.EntityId == parent.Id && value.Action == "TaskSubtasksChanged"));
        Assert.Equal(childOutboxCount + 1, await verify.Db.OutboxEvents.CountAsync(value => value.AggregateId == childId && value.EventType == "Projects.TaskChanged.v1"));
        Assert.Equal(parentOutboxCount + 1, await verify.Db.OutboxEvents.CountAsync(value => value.AggregateId == parent.Id && value.EventType == "Projects.TaskChanged.v1"));
        Assert.Single(await verify.Db.OutboxEvents.Where(value => value.AggregateId == childId && value.AggregateVersion == child.VersionNo && value.EventType == "Projects.TaskChanged.v1").ToListAsync());
        Assert.Single(await verify.Db.OutboxEvents.Where(value => value.AggregateId == parent.Id && value.AggregateVersion == parent.VersionNo && value.EventType == "Projects.TaskChanged.v1").ToListAsync());
        Assert.Equal("unrelated", await verify.Db.TaskItems.Where(item => item.Id == graph.UnrelatedTask.Id).Select(item => item.Title).SingleAsync());
        return (child, parent);
    }

    private static async Task AssertDeltaAsync(
        AppDbContext db,
        SideEffectSnapshot before,
        Guid taskId,
        string action,
        int auditDelta,
        int outboxDelta)
    {
        Assert.Equal(auditDelta, outboxDelta);
        await AssertTaskMutationSequenceAsync(db, before, taskId, Enumerable.Repeat(action, auditDelta).ToArray());
    }

    private static async Task AssertLabelArchiveUpdateRaceAsync(bool archivedInitially, bool archiveCommand)
    {
        await using var harness = await ServiceHarness.CreateAsync();
        var graph = harness.Graph;
        ProjectTaskLabelResponse label;
        await using (var setup = harness.CreateScope())
        {
            label = (await setup.Subresources.CreateLabelAsync(graph.Project.Id, new CreateProjectTaskLabelRequest("Release", "original"))).Value!;
            if (archivedInitially)
                label = (await setup.Subresources.SetLabelArchiveAsync(graph.Project.Id, label.Id, label.Version, true)).Value!;
        }

        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        harness.Race.Arm();
        var results = await Task.WhenAll(
            ExecuteAsync(first, request => request.Subresources.SetLabelArchiveAsync(graph.Project.Id, label.Id, label.Version, archiveCommand)),
            ExecuteAsync(second, request => request.Subresources.UpdateLabelAsync(graph.Project.Id, label.Id, new UpdateProjectTaskLabelRequest("Updated", default, default, label.Version))));

        var winner = Assert.Single(results, result => result.Result.IsSuccess);
        var loser = Assert.Single(results, result => !result.Result.IsSuccess);
        Assert.Equal(2, harness.Race.SaveCallCount);
        Assert.Equal(TaskCommandSaveResult.Saved, winner.Scope.SaveRecorder.LastSaveOutcome?.Result);
        Assert.Equal(TaskCommandSaveResult.ConcurrencyConflict, loser.Scope.SaveRecorder.LastSaveOutcome?.Result);
        Assert.Equal("TASK_STALE_VERSION", Code(loser.Result.Error));
        Assert.Equal(1, loser.Scope.SaveRecorder.ClearTrackingCallCount);
        Assert.Empty(loser.Scope.Db.ChangeTracker.Entries());

        await using (var verify = harness.CreateScope())
        {
            var persisted = await verify.Db.ProjectTaskLabels.SingleAsync(value => value.Id == label.Id);
            Assert.Equal(label.Version + 1, persisted.VersionNo);
            if (winner.Result.Value is ProjectTaskLabelResponse archiveWinner && archiveWinner.IsArchived == archiveCommand)
                Assert.Equal(archiveCommand, persisted.IsArchived);
            else
                Assert.Equal("Updated", persisted.Name);

            var archiveWon = winner.Result.Value!.IsArchived == archiveCommand;
            Assert.Single(await verify.Db.AuditLogs.Where(value => value.Action == (archiveWon ? (archiveCommand ? "TaskLabelArchived" : "TaskLabelRestored") : "TaskLabelUpdated")).ToListAsync());
            Assert.Equal(archivedInitially ? 3 : 2, await verify.Db.AuditLogs.CountAsync(value => value.EntityId == label.Id));
            Assert.Equal(archivedInitially ? 3 : 2, await verify.Db.OutboxEvents.CountAsync(value => value.AggregateId == graph.Project.Id && value.EventType == "Projects.ProjectChanged.v1"));
        }

        await using var retry = harness.CreateScope();
        var current = (await retry.Subresources.ListLabelsAsync(graph.Project.Id, true)).Value!.Single(value => value.Id == label.Id);
        var retried = await retry.Subresources.UpdateLabelAsync(graph.Project.Id, label.Id, new UpdateProjectTaskLabelRequest(default, "retry", default, current.Version));
        Assert.True(retried.IsSuccess);
        Assert.Equal(current.Version + 1, retried.Value!.Version);
    }

    private static async Task AssertTaskMutationSequenceAsync(
        AppDbContext db,
        SideEffectSnapshot before,
        Guid taskId,
        IReadOnlyList<string> expectedAuditActions)
    {
        var task = await db.TaskItems.SingleAsync(value => value.Id == taskId);
        Assert.Equal(before.TaskVersion + expectedAuditActions.Count, task.VersionNo);

        var audit = await db.AuditLogs.Where(value => value.EntityId == taskId).ToListAsync();
        Assert.Equal(before.AuditCount + expectedAuditActions.Count, audit.Count);
        foreach (var expected in expectedAuditActions.GroupBy(value => value))
            Assert.Equal(before.AuditActionCounts.GetValueOrDefault(expected.Key) + expected.Count(), audit.Count(value => value.Action == expected.Key));

        var outbox = await db.OutboxEvents
            .Where(value => value.AggregateId == taskId && value.EventType == "Projects.TaskChanged.v1")
            .ToListAsync();
        Assert.Equal(before.OutboxCount + expectedAuditActions.Count, outbox.Count);
        var newEvents = outbox.Where(value => value.AggregateVersion > before.TaskVersion).OrderBy(value => value.AggregateVersion).ToList();
        Assert.Equal(expectedAuditActions.Count, newEvents.Count);
        Assert.Equal(Enumerable.Range(1, expectedAuditActions.Count).Select(offset => before.TaskVersion + offset), newEvents.Select(value => value.AggregateVersion!.Value));
        foreach (var value in newEvents)
        {
            Assert.Equal("Task", value.AggregateType);
            Assert.Equal(taskId, value.AggregateId);
            Assert.Equal("Projects.TaskChanged.v1", value.EventType);
            var envelope = JsonSerializer.Deserialize<DurableEventEnvelope>(value.PayloadJson, RealtimeJsonOptions);
            Assert.NotNull(envelope);
            Assert.Equal(1, envelope.PayloadSchemaVersion);
            Assert.Equal(value.TenantId, envelope.TenantId);
            Assert.Equal(value.EventType, envelope.EventType);
            Assert.Equal(value.AggregateType, envelope.AggregateType);
            Assert.Equal(value.AggregateId, envelope.AggregateId);
            Assert.Equal(value.AggregateVersion, envelope.AggregateVersion);
            Assert.True(envelope.Payload.TryGetProperty("taskId", out var payloadTaskId));
            Assert.Equal(taskId, payloadTaskId.GetGuid());
            Assert.True(envelope.Payload.TryGetProperty("requiresRefetch", out var requiresRefetch));
            Assert.True(requiresRefetch.GetBoolean());
            Assert.True(envelope.Payload.TryGetProperty("taskVersion", out var payloadVersion));
            Assert.Equal(value.AggregateVersion!.Value, payloadVersion.GetInt64());
        }
    }

    private static string? Code(string? error) => error?.Split('|', 2)[0];

    private enum CommentRaceOperation { Update, Delete }

    private sealed record SideEffectSnapshot(long TaskVersion, int AuditCount, int OutboxCount, IReadOnlyDictionary<string, int> AuditActionCounts);

    private sealed record AttachmentSnapshot(
        Guid FileObjectId,
        Guid WorkspaceId,
        FileScanStatus ScanStatus,
        DateTimeOffset? DeletedAt,
        FileObjectStatus FileStatus,
        DateTimeOffset? FileDeletedAt,
        Guid? FileWorkspaceId,
        Guid? FileProjectId);

    private static async Task<TaskChecklistResponse> CreateChecklistAsync(ServiceHarness harness, string text)
    {
        var graph = harness.Graph;
        await using var scope = harness.CreateScope();
        var result = await scope.Subresources.CreateChecklistAsync(graph.Task.Id, new CreateTaskChecklistRequest(text));
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static async Task<TaskCommentResponse> CreateCommentAsync(ServiceHarness harness, string text)
    {
        var graph = harness.Graph;
        await using var scope = harness.CreateScope();
        var result = await scope.Subresources.CreateCommentAsync(graph.Task.Id, new CreateTaskCommentRequest(text));
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static async Task<(IReadOnlyList<TaskChecklistResponse> Items, long TaskVersion)> ChecklistAsync(ServiceHarness harness)
    {
        var graph = harness.Graph;
        await using var scope = harness.CreateScope();
        var checklist = await scope.Subresources.ListChecklistAsync(graph.Task.Id);
        var task = (await scope.Commands.GetAsync(graph.Task.Id)).Value!;
        return (checklist.Value!, task.Version);
    }

    private sealed class ServiceHarness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly string connectionString;

        private ServiceHarness(ServiceProvider provider, string connectionString, Graph graph, SaveRaceCoordinator race)
        {
            this.provider = provider;
            this.connectionString = connectionString;
            Graph = graph;
            Race = race;
        }

        public Graph Graph { get; }
        public SaveRaceCoordinator Race { get; }

        public static async Task<ServiceHarness> CreateAsync(bool useRealNotifications = false)
        {
            var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
            var graph = await SeedAsync(connectionString);
            var race = new SaveRaceCoordinator();
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
            services.AddScoped<CurrentTenantService>();
            services.AddScoped<ICurrentTenant>(serviceProvider => serviceProvider.GetRequiredService<CurrentTenantService>());
            services.AddScoped<TestCurrentUser>();
            services.AddScoped<ICurrentUser>(serviceProvider => serviceProvider.GetRequiredService<TestCurrentUser>());
            services.AddScoped<IClock, FixedClock>();
            services.AddScoped<IProjectRepository, ProjectRepository>();
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
            services.AddScoped<IGroupRepository, GroupRepository>();
            services.AddScoped<IChannelRepository, ChannelRepository>();
            services.AddScoped<IMessagingRepository, MessagingRepository>();
            services.AddScoped<IFileRepository, FileRepository>();
            services.AddScoped<IOutboxEventRepository, OutboxEventRepository>();
            services.AddScoped<ITransactionalOutbox, TransactionalOutbox>();
            services.AddScoped<IBusinessInvalidationPublisher, BusinessInvalidationPublisher>();
            if (useRealNotifications)
            {
                services.AddScoped<INotificationService, DbNotificationService>();
                services.AddSingleton<IFeatureFlagService, EnabledTaskNotificationFeatureFlags>();
                services.AddScoped<ITaskNotificationRecipientPolicy, TaskNotificationRecipientPolicy>();
                services.AddScoped<ITaskNotificationProducer, TaskNotificationProducer>();
            }
            else
                services.AddScoped<INotificationService, NoopNotificationService>();
            services.AddScoped<IAuthorizationStateChangePublisher, NoopAuthorizationStateChangePublisher>();
            services.AddScoped<IAuditLogger, DbAuditLogger>();
            services.AddScoped<EfUnitOfWork>();
            services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<EfUnitOfWork>());
            services.AddScoped<RequestSaveOutcomeRecorder>();
            services.AddScoped<ITaskCommandUnitOfWork>(serviceProvider => new RecordingTaskCommandUnitOfWork(
                serviceProvider.GetRequiredService<EfUnitOfWork>(), race, serviceProvider.GetRequiredService<RequestSaveOutcomeRecorder>()));
            services.AddScoped<IWorkspaceAuthorizationService, WorkspaceAuthorizationService>();
            services.AddScoped<IGroupAuthorizationService, GroupAuthorizationService>();
            services.AddScoped<IChannelAuthorizationService, ChannelAuthorizationService>();
            services.AddScoped<ProjectAuthorizationService>();
            services.AddScoped<IProjectAuthorizationService>(serviceProvider => serviceProvider.GetRequiredService<ProjectAuthorizationService>());
            services.AddScoped<ITaskAuthorizationService>(serviceProvider => serviceProvider.GetRequiredService<ProjectAuthorizationService>());
            services.AddScoped<ICommentAuthorizationService>(serviceProvider => serviceProvider.GetRequiredService<ProjectAuthorizationService>());
            services.AddScoped<IConversationAuthorizationService, ConversationAuthorizationService>();
            // Scope-isolation tests must execute the production authorization
            // graph.  The task's source attachment is workspace-owned, so this
            // exercises WorkspaceAuthorizationService rather than a permissive
            // test double.
            services.AddScoped<IFileAuthorizationService, FileAuthorizationService>();
            services.AddSingleton(new CommunicationSafetyOptions());
            services.AddSingleton<ICommunicationSafetyGuard, InMemoryCommunicationSafetyGuard>();
            services.AddScoped<ITaskWorkspaceTimeZoneResolver, UtcTimeZoneResolver>();
            services.AddScoped<ITaskCommandService, TaskCommandService>();
            services.AddScoped<ITaskSubresourceService, TaskSubresourceService>();
            services.AddScoped<IProjectService, ProjectService>();
            return new ServiceHarness(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), connectionString, graph, race);
        }

        /// <summary>Creates a real request scope.  Tenant selection is always
        /// explicit so cross-tenant acceptance cases cannot accidentally run
        /// through the platform scope used only for seeding.</summary>
        public RequestScope CreateScope(Guid? actorUserId = null, Guid? tenantId = null, string? tenantSlug = null)
        {
            var scope = provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenantService>().SetTenant(tenantId ?? Graph.Tenant.Id, tenantSlug ?? Graph.Tenant.Slug);
            scope.ServiceProvider.GetRequiredService<TestCurrentUser>().SetUser(actorUserId ?? Graph.User.Id);
            return new RequestScope(scope, scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<ITaskCommandService>(), scope.ServiceProvider.GetRequiredService<ITaskSubresourceService>(), scope.ServiceProvider.GetRequiredService<IProjectService>(), scope.ServiceProvider.GetRequiredService<RequestSaveOutcomeRecorder>());
        }

        public async Task<WorkItemWatchState> GetWatchStateAsync(Guid taskId, Guid userId)
        {
            await using var scope = CreateScope(userId);
            return await scope.Db.WorkItemWatchStates.AsNoTracking().SingleAsync(value => value.TaskItemId == taskId && value.UserId == userId);
        }

        public async Task<AttachmentSnapshot> SnapshotAttachmentAsync(Guid attachmentId)
        {
            await using var db = CreatePlatformContext(connectionString);
            var attachment = await db.Attachments
                .AsNoTracking()
                .Include(value => value.FileObject)
                .SingleAsync(value => value.Id == attachmentId);
            var file = Assert.IsType<FileObject>(attachment.FileObject);
            return new AttachmentSnapshot(
                attachment.FileObjectId,
                attachment.WorkspaceId,
                attachment.ScanStatus,
                attachment.DeletedAt,
                file.Status,
                file.DeletedAt,
                file.WorkspaceId,
                file.ProjectId);
        }

        public async Task<Guid> SeedOtherTenantLabelAsync()
        {
            await using var db = CreatePlatformContext(connectionString);
            var workspace = new Workspace
            {
                TenantId = Graph.OtherTenant.Id,
                Name = "Other tenant workspace",
                Slug = $"other-tenant-label-workspace-{Guid.NewGuid():N}",
                CreatedByUserId = Graph.OtherTenantUser.Id
            };
            var project = new Project
            {
                TenantId = Graph.OtherTenant.Id,
                WorkspaceId = workspace.Id,
                OwnerUserId = Graph.OtherTenantUser.Id,
                CreatedByUserId = Graph.OtherTenantUser.Id,
                Name = "Other tenant project",
                Slug = $"other-tenant-label-project-{Guid.NewGuid():N}"
            };
            var label = new ProjectTaskLabel
            {
                TenantId = Graph.OtherTenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Name = "Tenant secret label",
                Description = "Must not be disclosed",
                SortKey = 1024,
                VersionNo = 1
            };
            db.AddRange(workspace, project, label);
            await db.SaveChangesAsync();
            return label.Id;
        }

        public async Task<Guid> SeedOtherTenantAttachmentAsync()
        {
            await using var db = CreatePlatformContext(connectionString);
            var workspace = new Workspace
            {
                TenantId = Graph.OtherTenant.Id,
                Name = "Other tenant file workspace",
                Slug = $"other-tenant-file-workspace-{Guid.NewGuid():N}",
                CreatedByUserId = Graph.OtherTenantUser.Id
            };
            var file = new FileObject
            {
                TenantId = Graph.OtherTenant.Id,
                WorkspaceId = workspace.Id,
                UploadedByUserId = Graph.OtherTenantUser.Id,
                OriginalFileName = "tenant-secret.txt",
                StorageKey = $"other-tenant/{Guid.NewGuid():N}",
                ContentType = "text/plain",
                SizeBytes = 1,
                Status = FileObjectStatus.Active
            };
            var attachment = SourceAttachment(Graph.OtherTenant.Id, workspace.Id, Graph.OtherTenantUser.Id, file, "tenant-secret.txt");
            db.AddRange(workspace, file, attachment);
            await db.SaveChangesAsync();
            return attachment.Id;
        }

        public async ValueTask DisposeAsync()
        {
            Race.Dispose();
            await provider.DisposeAsync();
        }

        private static async Task<Graph> SeedAsync(string connectionString)
        {
            var suffix = Guid.NewGuid().ToString("N");
            await using var platform = CreatePlatformContext(connectionString);
            var tenant = new Tenant { Name = $"Task concurrency {suffix}", DisplayName = "Task concurrency", Slug = $"task-concurrency-{suffix}" };
            var user = UserFor("creator", suffix);
            var mentionUser = UserFor("primary", suffix);
            var collaboratorUser = UserFor("collaborator", suffix);
            var reviewerUser = UserFor("reviewer", suffix);
            var manualWatchUser = UserFor("manual-watch", suffix);
            var optOutUser = UserFor("opt-out", suffix);
            var sameProjectUnrelatedUser = UserFor("same-project-unrelated", suffix);
            var otherWorkspaceUser = UserFor("other-workspace", suffix);
            var otherTenantUser = UserFor("other-tenant", suffix);
            var otherTenant = new Tenant { Name = $"Other tenant {suffix}", DisplayName = "Other tenant", Slug = $"other-task-concurrency-{suffix}" };
            platform.AddRange(tenant, otherTenant, user, mentionUser, collaboratorUser, reviewerUser, manualWatchUser, optOutUser, sameProjectUnrelatedUser, otherWorkspaceUser, otherTenantUser);
            await platform.SaveChangesAsync();

            var currentTenant = new CurrentTenantService();
            currentTenant.SetTenant(tenant.Id, tenant.Slug);
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, currentTenant);
            var workspace = new Workspace { TenantId = tenant.Id, Name = "Task concurrency workspace", Slug = $"task-concurrency-ws-{suffix}", CreatedByUserId = user.Id };
            var project = new Project { TenantId = tenant.Id, WorkspaceId = workspace.Id, OwnerUserId = user.Id, CreatedByUserId = user.Id, Name = "Task concurrency project", Slug = $"task-concurrency-project-{suffix}", Status = ProjectStatus.Active, ActivationState = ProjectActivationState.Activated, ActivatedAtUtc = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero), ActivationVersion = 1 };
            var otherProject = new Project { TenantId = tenant.Id, WorkspaceId = workspace.Id, OwnerUserId = user.Id, CreatedByUserId = user.Id, Name = "Other task concurrency project", Slug = $"other-task-concurrency-project-{suffix}", Status = ProjectStatus.Active, ActivationState = ProjectActivationState.Activated, ActivatedAtUtc = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero), ActivationVersion = 1 };
            var workflow = new TaskWorkflowDefinition { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "Task concurrency workflow", ReviewEnforcementEnabled = false };
            var todo = new TaskWorkflowStage { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, DefinitionId = workflow.Id, Name = "Todo", InternalCategory = TaskStageCategory.Todo, SortKey = 1024, IsInitialStage = true };
            var inProgress = new TaskWorkflowStage { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, DefinitionId = workflow.Id, Name = "In progress", InternalCategory = TaskStageCategory.InProgress, SortKey = 2048 };
            var done = new TaskWorkflowStage { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, DefinitionId = workflow.Id, Name = "Done", InternalCategory = TaskStageCategory.Done, SortKey = 3072, IsTerminalStage = true };
            var cancelled = new TaskWorkflowStage { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, DefinitionId = workflow.Id, Name = "Cancelled", InternalCategory = TaskStageCategory.Cancelled, SortKey = 4096, IsTerminalStage = true };
            var task = new TaskItem { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, Title = "original", CreatedByUserId = user.Id, WorkflowStageId = todo.Id, Status = TaskItemStatus.NotStarted, VersionNo = 1 };
            var unrelated = new TaskItem { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, Title = "unrelated", CreatedByUserId = user.Id, VersionNo = 1 };
            var file = new FileObject { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, UploadedByUserId = user.Id, OriginalFileName = "source.txt", StorageKey = $"task-concurrency/{suffix}", ContentType = "text/plain", SizeBytes = 1, Status = FileObjectStatus.Active };
            var sourceAttachment = new Attachment { TenantId = tenant.Id, FileObjectId = file.Id, WorkspaceId = workspace.Id, OwnerType = AttachmentOwnerType.Workspace, OwnerId = workspace.Id, OwnerUserId = user.Id, UploadedByUserId = user.Id, FileName = "source.txt", StoredFileName = "source.txt", FilePath = "source.txt", ContentType = "text/plain", Extension = ".txt", SizeBytes = 1, StorageProvider = "test", StorageKey = $"task-concurrency/{suffix}", ScanStatus = FileScanStatus.Clean };
            db.AddRange(workspace, project, otherProject, workflow, todo, inProgress, done, cancelled, task, unrelated,
                file, sourceAttachment,
                new TenantUser { TenantId = tenant.Id, UserId = user.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new TenantUser { TenantId = tenant.Id, UserId = mentionUser.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new TenantUser { TenantId = tenant.Id, UserId = collaboratorUser.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new TenantUser { TenantId = tenant.Id, UserId = reviewerUser.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new TenantUser { TenantId = tenant.Id, UserId = manualWatchUser.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new TenantUser { TenantId = tenant.Id, UserId = optOutUser.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new TenantUser { TenantId = tenant.Id, UserId = sameProjectUnrelatedUser.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = mentionUser.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = collaboratorUser.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = reviewerUser.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = manualWatchUser.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = optOutUser.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = sameProjectUnrelatedUser.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow },
                new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = user.Id, Role = ProjectRole.Owner, JoinedAt = DateTimeOffset.UtcNow },
                new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = mentionUser.Id, Role = ProjectRole.Contributor, JoinedAt = DateTimeOffset.UtcNow },
                new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = collaboratorUser.Id, Role = ProjectRole.Contributor, JoinedAt = DateTimeOffset.UtcNow },
                new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = reviewerUser.Id, Role = ProjectRole.Contributor, JoinedAt = DateTimeOffset.UtcNow },
                new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = manualWatchUser.Id, Role = ProjectRole.Contributor, JoinedAt = DateTimeOffset.UtcNow },
                new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = optOutUser.Id, Role = ProjectRole.Contributor, JoinedAt = DateTimeOffset.UtcNow },
                new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = sameProjectUnrelatedUser.Id, Role = ProjectRole.Contributor, JoinedAt = DateTimeOffset.UtcNow },
                new NotificationUserState { TenantId = tenant.Id, UserId = mentionUser.Id, Version = 0, UpdatedAt = DateTimeOffset.UtcNow },
                TaskWatchStateInitializer.ForCreator(task, user.Id, new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();
            return new Graph(tenant, otherTenant, workspace, project, otherProject, user, mentionUser, collaboratorUser, reviewerUser, manualWatchUser, optOutUser, sameProjectUnrelatedUser, otherWorkspaceUser, otherTenantUser, task, unrelated, sourceAttachment, todo, inProgress, done, cancelled);
        }

        private static User UserFor(string role, string suffix) => new()
        {
            DisplayName = $"Task {role}",
            Email = $"task-concurrency-{role}-{suffix}@example.test",
            NormalizedEmail = $"TASK-CONCURRENCY-{role}-{suffix}@EXAMPLE.TEST",
            PasswordHash = "hash",
            Status = UserStatus.Active
        };

        private static AppDbContext CreatePlatformContext(string connectionString)
        {
            var tenant = new CurrentTenantService();
            tenant.SetPlatformScope();
            return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, tenant);
        }
    }

    private sealed record Graph(
        Tenant Tenant,
        Tenant OtherTenant,
        Workspace Workspace,
        Project Project,
        Project OtherProject,
        User User,
        User MentionUser,
        User CollaboratorUser,
        User ReviewerUser,
        User ManualWatchUser,
        User OptOutUser,
        User SameProjectUnrelatedUser,
        User OtherWorkspaceUser,
        User OtherTenantUser,
        TaskItem Task,
        TaskItem UnrelatedTask,
        Attachment SourceAttachment,
        TaskWorkflowStage TodoStage,
        TaskWorkflowStage InProgressStage,
        TaskWorkflowStage DoneStage,
        TaskWorkflowStage CancelledStage);

    private sealed record RequestScope(AsyncServiceScope Scope, AppDbContext Db, ITaskCommandService Commands, ITaskSubresourceService Subresources, IProjectService Compatibility, RequestSaveOutcomeRecorder SaveRecorder) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        private Guid userId;
        public void SetUser(Guid value) => userId = value;
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string? Email => "task-concurrency@example.test";
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => true;
    }

    private sealed class UtcTimeZoneResolver : ITaskWorkspaceTimeZoneResolver
    {
        public Task<TimeZoneInfo> ResolveAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(TimeZoneInfo.Utc);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid recipientUserId, string title, string? body, string sourceType, Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EnabledTaskNotificationFeatureFlags : IFeatureFlagService
    {
        public Task<bool> IsEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(
                FeatureKeys.Normalize(featureKey),
                FeatureKeys.TasksNotificationsV1,
                StringComparison.Ordinal));

        public async Task<Coglatas.Application.Common.Result> RequireEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default) =>
            await IsEnabledAsync(featureKey, cancellationToken)
                ? Coglatas.Application.Common.Result.Success()
                : Coglatas.Application.Common.Result.Failure($"Feature '{featureKey}' is disabled.");

        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([FeatureKeys.TasksNotificationsV1]);
    }

    private sealed class NoopAuthorizationStateChangePublisher : IAuthorizationStateChangePublisher
    {
        public Task PublishAsync(Guid tenantId, Guid affectedUserId, string scopeType, Guid? scopeId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SaveRaceCoordinator : IDisposable
    {
        private readonly object gate = new();
        private TaskCompletionSource? release;
        private TaskCompletionSource? singleWriterArrival;
        private TaskCompletionSource? singleWriterRelease;
        private bool singleWriterHoldArmed;
        private int remaining;
        private int saveCallCount;

        public int SaveCallCount => Volatile.Read(ref saveCallCount);

        public void Arm()
        {
            lock (gate)
            {
                if (remaining != 0)
                    throw new InvalidOperationException("The previous save race has not completed.");

                remaining = 2;
                saveCallCount = 0;
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ArmSingleWriterHold()
        {
            lock (gate)
            {
                if (remaining != 0 || singleWriterHoldArmed || singleWriterRelease is not null)
                    throw new InvalidOperationException("The previous save coordination has not completed.");

                saveCallCount = 0;
                singleWriterHoldArmed = true;
                singleWriterArrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                singleWriterRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public async Task WaitForSingleWriterArrivalAsync(CancellationToken cancellationToken = default)
        {
            Task arrival;
            lock (gate)
            {
                arrival = singleWriterArrival?.Task
                    ?? throw new InvalidOperationException("A single-writer hold has not been armed.");
            }

            await arrival.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        public void ReleaseSingleWriter()
        {
            lock (gate)
            {
                if (singleWriterRelease is null)
                    throw new InvalidOperationException("A single-writer hold is not awaiting release.");

                singleWriterRelease.TrySetResult();
                singleWriterRelease = null;
                singleWriterArrival = null;
            }
        }

        public async Task WaitBeforeSaveAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref saveCallCount);
            Task? wait = null;
            lock (gate)
            {
                if (singleWriterHoldArmed)
                {
                    singleWriterHoldArmed = false;
                    singleWriterArrival!.TrySetResult();
                    wait = singleWriterRelease!.Task;
                }
                else
                {
                    if (remaining == 0)
                        return;

                    remaining--;
                    if (remaining == 0)
                    {
                        release!.TrySetResult();
                        release = null;
                        return;
                    }

                    wait = release!.Task;
                }
            }

            await wait.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        public void Dispose()
        {
            lock (gate)
            {
                release?.TrySetCanceled();
                singleWriterArrival?.TrySetCanceled();
                singleWriterRelease?.TrySetCanceled();
                release = null;
                singleWriterArrival = null;
                singleWriterRelease = null;
                singleWriterHoldArmed = false;
                remaining = 0;
            }
        }
    }

    private sealed class RequestSaveOutcomeRecorder
    {
        public int SaveTaskCommandCallCount { get; private set; }
        public TaskCommandSaveOutcome? LastSaveOutcome { get; private set; }
        public int ClearTrackingCallCount { get; private set; }
        public bool RecoveryPathEntered => LastSaveOutcome is { Result: not TaskCommandSaveResult.Saved } || ClearTrackingCallCount > 0;

        public void RecordSave(TaskCommandSaveOutcome outcome)
        {
            SaveTaskCommandCallCount++;
            LastSaveOutcome = outcome;
        }

        public void RecordClear() => ClearTrackingCallCount++;
    }

    private sealed class RecordingTaskCommandUnitOfWork(EfUnitOfWork inner, SaveRaceCoordinator coordinator, RequestSaveOutcomeRecorder recorder) : ITaskCommandUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => inner.SaveChangesAsync(cancellationToken);

        public void ClearTaskCommandTracking()
        {
            recorder.RecordClear();
            inner.ClearTaskCommandTracking();
        }

        public async Task<TaskCommandSaveOutcome> SaveTaskCommandAsync(CancellationToken cancellationToken = default)
        {
            await coordinator.WaitBeforeSaveAsync(cancellationToken);
            var outcome = await inner.SaveTaskCommandAsync(cancellationToken);
            recorder.RecordSave(outcome);
            return outcome;
        }
    }
}
