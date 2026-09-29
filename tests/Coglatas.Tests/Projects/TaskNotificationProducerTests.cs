using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Projects;

public sealed class TaskNotificationProducerTests
{
    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task DisabledFlagDoesNotResolvePolicyOrStageNotification()
    {
        var recipient = Guid.NewGuid();
        var policy = new RecordingRecipientPolicy(
            new TaskNotificationRecipientResult([recipient], [], [recipient]));
        var notifications = new RecordingNotifications();
        var producer = new TaskNotificationProducer(
            policy,
            notifications,
            new FixedFeatureFlags(false));

        await producer.ProduceAsync(Request(TaskNotificationEventKind.PrimaryAssigneeChanged));

        Assert.Empty(policy.Requests);
        Assert.Empty(notifications.Staged);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task EnabledProducerStagesExactDeduplicatedRecipientsWithStableLogicalKey()
    {
        var firstRecipient = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var secondRecipient = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var policy = new RecordingRecipientPolicy(
            new TaskNotificationRecipientResult(
                [secondRecipient, firstRecipient],
                [firstRecipient],
                [secondRecipient, firstRecipient, secondRecipient, Guid.Empty]));
        var notifications = new RecordingNotifications();
        var producer = new TaskNotificationProducer(
            policy,
            notifications,
            new FixedFeatureFlags(true));
        var request = Request(TaskNotificationEventKind.PrimaryAssigneeChanged);

        await producer.ProduceAsync(request);
        await producer.ProduceAsync(request);

        Assert.Equal(2, policy.Requests.Count);
        Assert.Equal(4, notifications.Staged.Count);
        Assert.Equal(
            [firstRecipient, secondRecipient, firstRecipient, secondRecipient],
            notifications.Staged.Select(item => item.UserId).ToArray());
        Assert.All(
            notifications.Staged,
            item => Assert.Equal(
                $"task:{request.Task.Id:N}:event:TaskAssignmentChanged:version:{request.Task.VersionNo}",
                item.LogicalKey));
    }

    [Theory]
    [InlineData(TaskNotificationEventKind.PrimaryAssigneeChanged, NotificationType.TaskAssigned, "Task assignment changed", "TaskAssignmentChanged")]
    [InlineData(TaskNotificationEventKind.ReviewerAssigned, NotificationType.TaskAssigned, "Task assignment changed", "TaskAssignmentChanged")]
    [InlineData(TaskNotificationEventKind.TaskCommentSignificant, NotificationType.Mention, "Task comment requires attention", "TaskCommentSignificant")]
    [InlineData(TaskNotificationEventKind.ReviewSubmitted, NotificationType.TaskStatusChanged, "Task review requested", "TaskReviewSubmitted")]
    [InlineData(TaskNotificationEventKind.ReviewReturned, NotificationType.TaskStatusChanged, "Task review returned", "TaskReviewReturned")]
    [InlineData(TaskNotificationEventKind.BecameBlocked, NotificationType.TaskStatusChanged, "Task blocked", "TaskBecameBlocked")]
    [InlineData(TaskNotificationEventKind.MajorDeadlineChanged, NotificationType.TaskDueSoon, "Task deadline changed", "TaskDeadlineChanged")]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task PresentationIsGenericAndNeverCopiesTaskContent(
        TaskNotificationEventKind eventKind,
        NotificationType expectedType,
        string expectedTitle,
        string expectedSourceGroup)
    {
        var recipient = Guid.NewGuid();
        var policy = new RecordingRecipientPolicy(
            new TaskNotificationRecipientResult([recipient], [], [recipient]));
        var notifications = new RecordingNotifications();
        var producer = new TaskNotificationProducer(
            policy,
            notifications,
            new FixedFeatureFlags(true));
        var request = Request(eventKind);

        await producer.ProduceAsync(request);

        var staged = Assert.Single(notifications.Staged);
        Assert.Equal(recipient, staged.UserId);
        Assert.Equal(expectedType, staged.Type);
        Assert.Equal(expectedTitle, staged.Title);
        Assert.Equal(request.Task.Id, staged.TaskId);
        Assert.Contains($":event:{expectedSourceGroup}:", staged.LogicalKey, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Task.Title, staged.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Task.Title, staged.LogicalKey, StringComparison.Ordinal);
        Assert.DoesNotContain("review reason secret", staged.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("review reason secret", staged.LogicalKey, StringComparison.OrdinalIgnoreCase);
    }

    private static TaskNotificationRecipientRequest Request(TaskNotificationEventKind eventKind)
    {
        var task = new TaskItem
        {
            ProjectId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(),
            Title = "Restricted Task Alpha",
            Description = "review reason secret",
            BlockedReason = "review reason secret",
            VersionNo = 17
        };
        return new TaskNotificationRecipientRequest(
            task,
            eventKind,
            ActorUserId: Guid.NewGuid(),
            DeadlineChangeClassification: TaskDeadlineChangeClassification.Added);
    }

    private sealed class RecordingRecipientPolicy(TaskNotificationRecipientResult result)
        : ITaskNotificationRecipientPolicy
    {
        public List<TaskNotificationRecipientRequest> Requests { get; } = [];

        public Task<TaskNotificationRecipientResult> ResolveAsync(
            TaskNotificationRecipientRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<StagedTaskNotification> Staged { get; } = [];

        public Task<Guid> StageTaskByLogicalKeyAsync(
            Guid userId,
            NotificationType type,
            string title,
            Guid taskId,
            string logicalKey,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            Staged.Add(new StagedTaskNotification(id, userId, type, title, taskId, logicalKey));
            return Task.FromResult(id);
        }

        public Task NotifyAsync(
            Guid recipientUserId,
            string title,
            string? body,
            string sourceType,
            Guid sourceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedFeatureFlags(bool enabled) : IFeatureFlagService
    {
        public Task<bool> IsEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(FeatureKeys.TasksNotificationsV1, featureKey);
            return Task.FromResult(enabled);
        }

        public Task<Result> RequireEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(enabled ? Result.Success() : Result.Failure("disabled"));

        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                enabled ? [FeatureKeys.TasksNotificationsV1] : []);
    }

    private sealed record StagedTaskNotification(
        Guid Id,
        Guid UserId,
        NotificationType Type,
        string Title,
        Guid TaskId,
        string LogicalKey);
}
