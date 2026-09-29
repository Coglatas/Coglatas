using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Messaging;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Projects;

public sealed class TaskSubresourceServiceTests
{
    [Fact]
    [Trait("Scope", "Issue369")]
    public async Task ActivityReadUsesTheAuthorizedTaskProjectAndBoundedPaging()
    {
        var fixture = new Fixture();
        var authorId = Guid.NewGuid();
        fixture.Projects.ActivityLogs =
        [
            new TaskActivityLogReadModel(
                Guid.NewGuid(),
                ActivityLogType.StatusUpdate,
                "Implementation is ready for review.",
                new DateTimeOffset(2026, 8, 24, 1, 2, 3, TimeSpan.Zero),
                authorId,
                "Activity author")
        ];
        fixture.Projects.ActivityTotalCount = 51;

        var result = await fixture.Service.ListActivityAsync(fixture.Task.Id, page: 0, pageSize: 500);

        Assert.True(result.IsSuccess, result.Error);
        var query = Assert.Single(fixture.Projects.ActivityQueries);
        Assert.Equal((fixture.Task.ProjectId, fixture.Task.Id, 1, 50), query);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(ActivityLogType.StatusUpdate, item.ActivityType);
        Assert.Equal("Activity author", item.Author.DisplayName);
        Assert.True(result.Value.HasMore);
    }

    [Fact]
    [Trait("Scope", "Issue369")]
    public async Task ActivityReadDoesNotQueryRowsAfterProjectAuthorizationIsDenied()
    {
        var fixture = new Fixture();
        fixture.ProjectAuthorization.ViewAllowed = false;

        var result = await fixture.Service.ListActivityAsync(fixture.Task.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_NOT_FOUND|Task not found.", result.Error);
        Assert.Empty(fixture.Projects.ActivityQueries);
    }

    [Fact]
    [Trait("Scope", "Issue369")]
    public async Task ActivityReadDoesNotQueryRowsForADeletedTask()
    {
        var fixture = new Fixture();
        fixture.Task.MarkDeleted(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));

        var result = await fixture.Service.ListActivityAsync(fixture.Task.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_NOT_FOUND|Task not found.", result.Error);
        Assert.Empty(fixture.Projects.ActivityQueries);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task OrdinaryCommentStagesBothInvalidationsWithoutNotificationIntent()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.CreateCommentAsync(
            fixture.Task.Id,
            new CreateTaskCommentRequest("ordinary comment"));

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Notifications.Requests);
        Assert.Single(fixture.Invalidations.CommentChanges, change => change.Change == "created");
        Assert.Single(fixture.Invalidations.TaskChanges, change => change.Change == "commentChanged");
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task MalformedLongMentionLikeTextIsStoredAsOrdinaryComment()
    {
        var fixture = new Fixture();
        var malformedBody = "@" + new string('{', 4096) + "not-a-mention";

        var result = await fixture.Service.CreateCommentAsync(
            fixture.Task.Id,
            new CreateTaskCommentRequest(malformedBody));

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Notifications.Requests);
        Assert.Equal(malformedBody, Assert.Single(fixture.Projects.Comments.Values).BodyPlainText);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task DirectMentionAndImportantMarkerUseOneSignificantRequestWithDeduplicatedMentions()
    {
        var fixture = new Fixture();
        var mentionedUserId = fixture.AddEligibleMentionUser();
        var body = $"Please review @{{{mentionedUserId:D}}} and again @{{{mentionedUserId:D}}}.";

        var result = await fixture.Service.CreateCommentAsync(
            fixture.Task.Id,
            new CreateTaskCommentRequest(body, IsImportant: true));

        Assert.True(result.IsSuccess);
        var request = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.TaskCommentSignificant, request.EventKind);
        Assert.Equal([mentionedUserId], request.ValidDirectMentionUserIds);
        Assert.True(request.IsImportantComment);
        Assert.Equal(fixture.ActorUserId, request.ActorUserId);
        Assert.Equal(2, request.Task.VersionNo);
        Assert.Equal("created", Assert.Single(fixture.Invalidations.CommentChanges).Change);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task UpdatedCommentRevalidatesMentionsAndUsesOneSignificantRequest()
    {
        var fixture = new Fixture();
        var mentionedUserId = fixture.AddEligibleMentionUser();
        var comment = fixture.AddComment("ordinary comment");

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest($"Now important @{{{mentionedUserId:D}}}", true, comment.VersionNo));

        Assert.True(result.IsSuccess);
        var request = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.TaskCommentSignificant, request.EventKind);
        Assert.Equal([mentionedUserId], request.ValidDirectMentionUserIds);
        Assert.True(request.IsImportantComment);
        var semantic = Assert.Single(fixture.Invalidations.CommentChanges);
        Assert.Equal("updated", semantic.Change);
        Assert.Equal(2, semantic.CommentVersion);
        Assert.Equal(2, fixture.Task.VersionNo);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ImportanceOnlyUpdateDoesNotReplayMentionsFromThePersistedBody()
    {
        var fixture = new Fixture();
        var previouslyMentionedUserId = Guid.NewGuid();
        var comment = fixture.AddComment($"Existing @{{{previouslyMentionedUserId:D}}}");

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, true, comment.VersionNo));

        Assert.True(result.IsSuccess);
        var request = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.TaskCommentSignificant, request.EventKind);
        Assert.Empty(request.ValidDirectMentionUserIds ?? []);
        Assert.True(request.IsImportantComment);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task AlreadyImportantCommentWithEmptyPatchDoesNotMutateOrNotify()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("important", important: true);
        var before = fixture.Snapshot(comment);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, null, comment.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_COMMENT_UPDATE_REQUIRED|At least one comment field must be supplied.", result.Error);
        fixture.AssertUnchanged(comment, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task AlreadyImportantCommentWithTrueAgainDoesNotMutateOrNotify()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("important", important: true);
        var before = fixture.Snapshot(comment);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, true, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        fixture.AssertUnchanged(comment, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task SameBodyPatchDoesNotMutateOrNotify()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("same body", important: true);
        var before = fixture.Snapshot(comment);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest("same body", null, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        fixture.AssertUnchanged(comment, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task FalseToTrueImportantPatchCreatesOneSignificantIntent()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("ordinary");

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, true, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        var request = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.TaskCommentSignificant, request.EventKind);
        Assert.True(request.IsImportantComment);
        Assert.Empty(request.ValidDirectMentionUserIds ?? []);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task TrueToFalseImportantPatchDoesNotNotify()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("important", important: true);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, false, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(comment.IsImportant);
        Assert.Empty(fixture.Notifications.Requests);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task BodyChangeOnExistingImportantWithoutMentionDoesNotNotifyImportantRecipients()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("important", important: true);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest("changed body", null, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(fixture.Notifications.Requests);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task BodyChangeOnExistingImportantWithMentionNotifiesOnlyMentionTargets()
    {
        var fixture = new Fixture();
        var mentionUserId = fixture.AddEligibleMentionUser();
        var comment = fixture.AddComment("important", important: true);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest($"changed @{{{mentionUserId:D}}}", null, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        var request = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal([mentionUserId], request.ValidDirectMentionUserIds);
        Assert.False(request.IsImportantComment);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task BodyChangeAndFalseToTrueUsesOneRecipientUnion()
    {
        var fixture = new Fixture();
        var mentionUserId = fixture.AddEligibleMentionUser();
        var comment = fixture.AddComment("ordinary");

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest($"changed @{{{mentionUserId:D}}}", true, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        var request = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.TaskCommentSignificant, request.EventKind);
        Assert.Equal([mentionUserId], request.ValidDirectMentionUserIds);
        Assert.True(request.IsImportantComment);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task IneligibleMentionReturnsSafeErrorAndStagesNothing()
    {
        var fixture = new Fixture();
        var unavailableUserId = Guid.NewGuid();

        var result = await fixture.Service.CreateCommentAsync(
            fixture.Task.Id,
            new CreateTaskCommentRequest($"private @{{{unavailableUserId:D}}}"));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_MENTION_NOT_ELIGIBLE|One or more mentions are not available for this task.", result.Error);
        Assert.Empty(fixture.Projects.Comments);
        Assert.Empty(fixture.Notifications.Requests);
        Assert.Empty(fixture.Invalidations.CommentChanges);
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Equal(1, fixture.Task.VersionNo);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task DeleteStagesSemanticAndGenericInvalidationsWithoutNotificationIntent()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("delete me");

        var result = await fixture.Service.DeleteCommentAsync(comment.Id, comment.VersionNo);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Notifications.Requests);
        Assert.Equal("deleted", Assert.Single(fixture.Invalidations.CommentChanges).Change);
        Assert.Equal("commentChanged", Assert.Single(fixture.Invalidations.TaskChanges).Change);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedCommentAuthorCannotUpdateComment()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("original");
        var before = fixture.Snapshot(comment);
        fixture.ProjectAuthorization.DeniedViewUserIds.Add(fixture.ActorUserId);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest("changed", true, comment.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_COMMENT_FORBIDDEN", result.Error?.Split('|', 2)[0]);
        fixture.AssertUnchanged(comment, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedCommentAuthorCannotDeleteComment()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("original");
        var before = fixture.Snapshot(comment);
        fixture.ProjectAuthorization.DeniedViewUserIds.Add(fixture.ActorUserId);

        var result = await fixture.Service.DeleteCommentAsync(comment.Id, comment.VersionNo);

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_COMMENT_FORBIDDEN", result.Error?.Split('|', 2)[0]);
        fixture.AssertUnchanged(comment, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CommentAuthorWithoutCurrentProjectVisibilityIsDenied()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("original");
        var before = fixture.Snapshot(comment);
        fixture.ProjectAuthorization.DeniedViewUserIds.Add(fixture.ActorUserId);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, true, comment.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_COMMENT_FORBIDDEN", result.Error?.Split('|', 2)[0]);
        fixture.AssertUnchanged(comment, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ArchivedProjectCommentCannotBeUpdated()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("original");
        var before = fixture.Snapshot(comment);
        fixture.ProjectAuthorization.ViewAllowed = false;

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest("changed", null, comment.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_COMMENT_FORBIDDEN", result.Error?.Split('|', 2)[0]);
        fixture.AssertUnchanged(comment, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task DeletedTaskCommentCannotBeUpdated()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("original");
        var before = fixture.Snapshot(comment);
        fixture.Task.MarkDeleted(new DateTimeOffset(2026, 8, 2, 1, 0, 0, TimeSpan.Zero));

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest("changed", null, comment.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_COMMENT_FORBIDDEN", result.Error?.Split('|', 2)[0]);
        fixture.AssertUnchanged(comment, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CurrentAuthorWithProjectAccessCanStillUpdate()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("original");

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest("changed", null, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("changed", comment.BodyPlainText);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CurrentManagerWithProjectAccessCanStillUpdate()
    {
        var fixture = new Fixture();
        var comment = fixture.AddComment("original", authorUserId: Guid.NewGuid());

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest("manager update", null, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("manager update", comment.BodyPlainText);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ImportantOnlyUpdateInvokesSignificanceSafetyCheck()
    {
        var safety = new RecordingCommunicationSafetyGuard();
        var fixture = new Fixture(safety);
        var comment = fixture.AddComment("ordinary");

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, true, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(0, safety.MessagePostCalls);
        Assert.Equal(1, safety.SignificanceCalls);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task BodyAndImportantUpdateInvokesSafetyCheckOnce()
    {
        var safety = new RecordingCommunicationSafetyGuard();
        var fixture = new Fixture(safety);
        var comment = fixture.AddComment("ordinary");

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest("changed", true, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, safety.MessagePostCalls);
        Assert.Equal(0, safety.SignificanceCalls);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RateLimitedImportantOnlyUpdateMutatesNothing()
    {
        var fixture = new Fixture(new InMemoryCommunicationSafetyGuard(new CommunicationSafetyOptions
        {
            MaxPostsPerMinutePerUser = 1,
            MaxPostsPerMinutePerConversation = 10
        }));
        var first = fixture.AddComment("first");
        var second = fixture.AddComment("second");

        var firstResult = await fixture.Service.UpdateCommentAsync(
            first.Id,
            new UpdateTaskCommentRequest(null, true, first.VersionNo));
        Assert.True(firstResult.IsSuccess, firstResult.Error);

        var before = fixture.Snapshot(second);
        var result = await fixture.Service.UpdateCommentAsync(
            second.Id,
            new UpdateTaskCommentRequest(null, true, second.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_COMMENT_RATE_LIMITED", result.ErrorDetail?.Code);
        Assert.True(result.ErrorDetail?.RetryAfterSeconds >= 1);
        fixture.AssertUnchanged(second, before);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ImportantFalseToFalseDoesNotInvokeSafetyCheck()
    {
        var safety = new RecordingCommunicationSafetyGuard();
        var fixture = new Fixture(safety);
        var comment = fixture.AddComment("ordinary");

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, false, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(0, safety.MessagePostCalls);
        Assert.Equal(0, safety.SignificanceCalls);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ImportantTrueToTrueNoOpDoesNotInvokeSafetyCheck()
    {
        var safety = new RecordingCommunicationSafetyGuard();
        var fixture = new Fixture(safety);
        var comment = fixture.AddComment("important", important: true);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest(null, true, comment.VersionNo));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(0, safety.MessagePostCalls);
        Assert.Equal(0, safety.SignificanceCalls);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedWorkspaceMemberIsNotMentionCandidate()
    {
        var fixture = new Fixture();
        var revokedUserId = fixture.AddMentionCandidateUser();
        fixture.ProjectAuthorization.DeniedViewUserIds.Add(revokedUserId);

        var result = await fixture.Service.SearchMentionCandidatesAsync(fixture.Task.Id, "Mention");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value!);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task StaleProjectMemberWithoutWorkspaceAccessIsNotMentionCandidate()
    {
        var fixture = new Fixture();
        var staleUserId = fixture.AddMentionCandidateUser();
        fixture.ProjectAuthorization.DeniedViewUserIds.Add(staleUserId);

        var result = await fixture.Service.SearchMentionCandidatesAsync(fixture.Task.Id, "Mention");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value!);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task StaleGroupMemberWithoutWorkspaceAccessIsNotMentionCandidate()
    {
        var fixture = new Fixture();
        var staleUserId = fixture.AddMentionCandidateUser();
        fixture.ProjectAuthorization.DeniedViewUserIds.Add(staleUserId);

        var result = await fixture.Service.SearchMentionCandidatesAsync(fixture.Task.Id, "Mention");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value!);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task AuthorizedProjectMemberRemainsMentionCandidate()
    {
        var fixture = new Fixture();
        var userId = fixture.AddMentionCandidateUser();

        var result = await fixture.Service.SearchMentionCandidatesAsync(fixture.Task.Id, "Mention");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(userId, Assert.Single(result.Value!).UserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task UnauthorizedDirectMentionReturnsGenericError()
    {
        var fixture = new Fixture();
        var revokedUserId = fixture.AddEligibleMentionUser();
        fixture.ProjectAuthorization.DeniedViewUserIds.Add(revokedUserId);

        var result = await fixture.Service.CreateCommentAsync(
            fixture.Task.Id,
            new CreateTaskCommentRequest($"@{{{revokedUserId:D}}}"));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_MENTION_NOT_ELIGIBLE|One or more mentions are not available for this task.", result.Error);
        Assert.Empty(fixture.Projects.Comments);
        Assert.Empty(fixture.Notifications.Requests);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task MixedAuthorizedAndUnauthorizedMentionsRejectWholeMutation()
    {
        var fixture = new Fixture();
        var authorizedUserId = fixture.AddEligibleMentionUser();
        var revokedUserId = fixture.AddEligibleMentionUser();
        fixture.ProjectAuthorization.DeniedViewUserIds.Add(revokedUserId);
        var comment = fixture.AddComment("original");
        var before = fixture.Snapshot(comment);

        var result = await fixture.Service.UpdateCommentAsync(
            comment.Id,
            new UpdateTaskCommentRequest($"@{{{authorizedUserId:D}}} @{{{revokedUserId:D}}}", null, comment.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_MENTION_NOT_ELIGIBLE|One or more mentions are not available for this task.", result.Error);
        fixture.AssertUnchanged(comment, before);
    }

    private sealed class Fixture
    {
        public Fixture(ICommunicationSafetyGuard? safetyGuard = null)
        {
            ActorUserId = Guid.NewGuid();
            Task = new TaskItem
            {
                TenantId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                CreatedByUserId = ActorUserId,
                Title = "Task",
                VersionNo = 1
            };
            Projects.Tasks[Task.Id] = Task;
            Service = new TaskSubresourceService(
                Projects,
                null!,
                ProjectAuthorization,
                null!,
                CommentAuthorization,
                null!,
                null!,
                null!,
                safetyGuard ?? new AllowingCommunicationSafetyGuard(),
                new FakeCurrentUser(ActorUserId),
                new FixedClock(),
                Audit,
                Invalidations,
                UnitOfWork,
                null!,
                Notifications);
        }

        public Guid ActorUserId { get; }
        public TaskItem Task { get; }
        public FakeProjectRepository Projects { get; } = new();
        public FakeAuditLogger Audit { get; } = new();
        public FakeInvalidationPublisher Invalidations { get; } = new();
        public FakeTaskUnitOfWork UnitOfWork { get; } = new();
        public FakeTaskNotificationProducer Notifications { get; } = new();
        public ControllableProjectAuthorization ProjectAuthorization { get; } = new();
        private ControllableCommentAuthorization CommentAuthorization { get; } = new();
        public TaskSubresourceService Service { get; }

        public Guid AddEligibleMentionUser()
        {
            var emailKey = Guid.NewGuid().ToString("N");
            var user = new User
            {
                Email = $"{emailKey}@example.test",
                NormalizedEmail = $"{emailKey}@EXAMPLE.TEST".ToUpperInvariant(),
                DisplayName = "Mention target"
            };
            Projects.EligibleMentionUsers[user.Id] = user;
            return user.Id;
        }

        public Guid AddMentionCandidateUser()
        {
            var userId = AddEligibleMentionUser();
            Projects.MentionCandidates[userId] = Projects.EligibleMentionUsers[userId];
            return userId;
        }

        public TaskComment AddComment(string body, bool important = false, Guid? authorUserId = null)
        {
            var comment = new TaskComment
            {
                TenantId = Task.TenantId,
                WorkspaceId = Task.WorkspaceId,
                ProjectId = Task.ProjectId,
                TaskItemId = Task.Id,
                TaskItem = Task,
                AuthorUserId = authorUserId ?? ActorUserId,
                BodyPlainText = body,
                IsImportant = important,
                CreatedAt = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                VersionNo = 1
            };
            Projects.Comments[comment.Id] = comment;
            return comment;
        }

        public CommentMutationSnapshot Snapshot(TaskComment comment) => new(
            comment.BodyPlainText,
            comment.IsImportant,
            comment.VersionNo,
            Task.VersionNo,
            comment.UpdatedAt,
            Audit.Entries.Count,
            Invalidations.TaskChanges.Count,
            Invalidations.CommentChanges.Count,
            Notifications.Requests.Count,
            UnitOfWork.SaveCount);

        public void AssertUnchanged(TaskComment comment, CommentMutationSnapshot before)
        {
            Assert.Equal(before.Body, comment.BodyPlainText);
            Assert.Equal(before.IsImportant, comment.IsImportant);
            Assert.Equal(before.CommentVersion, comment.VersionNo);
            Assert.Equal(before.TaskVersion, Task.VersionNo);
            Assert.Equal(before.UpdatedAt, comment.UpdatedAt);
            Assert.Equal(before.AuditCount, Audit.Entries.Count);
            Assert.Equal(before.TaskInvalidationCount, Invalidations.TaskChanges.Count);
            Assert.Equal(before.CommentInvalidationCount, Invalidations.CommentChanges.Count);
            Assert.Equal(before.NotificationIntentCount, Notifications.Requests.Count);
            Assert.Equal(before.SaveCount, UnitOfWork.SaveCount);
        }
    }

    private sealed record CommentMutationSnapshot(
        string Body,
        bool IsImportant,
        long CommentVersion,
        long TaskVersion,
        DateTimeOffset? UpdatedAt,
        int AuditCount,
        int TaskInvalidationCount,
        int CommentInvalidationCount,
        int NotificationIntentCount,
        int SaveCount);

    private sealed class FakeProjectRepository : IProjectRepository
    {
        public Dictionary<Guid, TaskItem> Tasks { get; } = [];
        public Dictionary<Guid, TaskComment> Comments { get; } = [];
        public Dictionary<Guid, User> EligibleMentionUsers { get; } = [];
        public Dictionary<Guid, User> MentionCandidates { get; } = [];
        public IReadOnlyList<TaskActivityLogReadModel> ActivityLogs { get; set; } = [];
        public int ActivityTotalCount { get; set; }
        public List<(Guid ProjectId, Guid TaskItemId, int Page, int PageSize)> ActivityQueries { get; } = [];

        public Task<TaskItem?> GetTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Tasks.GetValueOrDefault(taskItemId));

        public Task<PagedResponse<TaskActivityLogReadModel>> ListTaskActivityLogsPageAsync(Guid projectId, Guid taskItemId, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            ActivityQueries.Add((projectId, taskItemId, page, pageSize));
            return Task.FromResult(new PagedResponse<TaskActivityLogReadModel>(ActivityLogs, page, pageSize, ActivityTotalCount));
        }

        public Task<TaskComment?> GetTaskCommentAsync(Guid commentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Comments.GetValueOrDefault(commentId));

        public Task<IReadOnlyList<User>> GetEligibleMentionUsersAsync(Guid projectId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<User>>(userIds.Where(EligibleMentionUsers.ContainsKey).Select(userId => EligibleMentionUsers[userId]).ToArray());

        public Task<IReadOnlyList<User>> SearchMentionCandidatesAsync(Guid projectId, string query, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<User>>(MentionCandidates.Values.Take(take).ToArray());

        public Task AddTaskCommentAsync(TaskComment comment, CancellationToken cancellationToken = default)
        {
            comment.TaskItem = Tasks.GetValueOrDefault(comment.TaskItemId);
            Comments[comment.Id] = comment;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Project>> ListVisibleAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Project>>([]);
        public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
        public Task<ProjectMember?> GetMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<ProjectMember?>(null);
        public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectMember>>([]);
        public Task<IReadOnlyList<Milestone>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Milestone>>([]);
        public Task<Milestone?> GetMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default) => Task.FromResult<Milestone?>(null);
        public Task<IReadOnlyList<TaskItem>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskItem>>(Tasks.Values.Where(task => task.ProjectId == projectId).ToArray());
        public Task<IReadOnlyList<TaskAssignment>> ListAssignmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskAssignment>>([]);
        public Task<TaskAssignment?> GetAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default) => Task.FromResult<TaskAssignment?>(null);
        public Task<IReadOnlyList<TaskDependency>> ListDependenciesAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>([]);
        public Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>([]);
        public Task<TaskDependency?> GetDependencyAsync(Guid dependencyId, CancellationToken cancellationToken = default) => Task.FromResult<TaskDependency?>(null);
        public Task<bool> DependencyExistsAsync(Guid predecessorTaskId, Guid successorTaskItemId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Comment>> ListCommentsAsync(CommentTargetType targetType, Guid targetId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Comment>>([]);
        public Task<Comment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken = default) => Task.FromResult<Comment?>(null);
        public Task AddProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddMemberAsync(ProjectMember member, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddMilestoneAsync(Milestone milestone, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddTaskAsync(TaskItem task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddAssignmentAsync(TaskAssignment assignment, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddDependencyAsync(TaskDependency dependency, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddCommentAsync(Comment comment, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RemoveMember(ProjectMember member) { }
        public void RemoveAssignment(TaskAssignment assignment) { }
        public void RemoveDependency(TaskDependency dependency) { }
    }

    private sealed class ControllableProjectAuthorization : IProjectAuthorizationService
    {
        public bool ViewAllowed { get; set; } = true;
        private bool ManageAllowed { get; } = true;
        public HashSet<Guid> DeniedViewUserIds { get; } = [];

        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(ViewAllowed && !DeniedViewUserIds.Contains(userId));
        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(ManageAllowed && ViewAllowed && !DeniedViewUserIds.Contains(userId));
        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class ControllableCommentAuthorization : ICommentAuthorizationService
    {
        private bool IsAllowed { get; } = true;
        public Task<bool> CanCommentOnTarget(Guid userId, CommentTargetType targetType, Guid targetId, CancellationToken cancellationToken = default) => Task.FromResult(IsAllowed);
    }

    private sealed class AllowingCommunicationSafetyGuard : ICommunicationSafetyGuard
    {
        public CommunicationSafetyDecision CheckMessagePost(CommunicationSafetyScope scope, string normalizedBody, DateTimeOffset now) => CommunicationSafetyDecision.Allow();
        public CommunicationSafetyDecision CheckThreadCreate(CommunicationSafetyScope scope, DateTimeOffset now) => CommunicationSafetyDecision.Allow();
        public CommunicationSafetyDecision CheckReport(CommunicationSafetyScope scope, DateTimeOffset now) => CommunicationSafetyDecision.Allow();
    }

    private sealed class RecordingCommunicationSafetyGuard : ICommunicationSafetyGuard
    {
        public int MessagePostCalls { get; private set; }
        public int SignificanceCalls { get; private set; }

        public CommunicationSafetyDecision CheckMessagePost(CommunicationSafetyScope scope, string normalizedBody, DateTimeOffset now)
        {
            MessagePostCalls++;
            return CommunicationSafetyDecision.Allow();
        }

        public CommunicationSafetyDecision CheckTaskCommentSignificance(CommunicationSafetyScope scope, DateTimeOffset now)
        {
            SignificanceCalls++;
            return CommunicationSafetyDecision.Allow();
        }

        public CommunicationSafetyDecision CheckThreadCreate(CommunicationSafetyScope scope, DateTimeOffset now) => CommunicationSafetyDecision.Allow();
        public CommunicationSafetyDecision CheckReport(CommunicationSafetyScope scope, DateTimeOffset now) => CommunicationSafetyDecision.Allow();
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInvalidationPublisher : IBusinessInvalidationPublisher
    {
        public List<(Guid TaskId, string Change)> TaskChanges { get; } = [];
        public List<(Guid TaskId, Guid CommentId, long CommentVersion, string Change)> CommentChanges { get; } = [];

        public Task TaskChangedAsync(TaskItem task, Guid actorUserId, string change, IEnumerable<string>? changedFields = null, IEnumerable<Guid>? affectedUserIds = null, CancellationToken cancellationToken = default)
        {
            TaskChanges.Add((task.Id, change));
            return Task.CompletedTask;
        }

        public Task TaskCommentChangedAsync(TaskItem task, TaskComment comment, Guid actorUserId, string change, CancellationToken cancellationToken = default)
        {
            CommentChanges.Add((task.Id, comment.Id, comment.VersionNo, change));
            return Task.CompletedTask;
        }

        public Task ProjectChangedAsync(Project project, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AnnouncementChangedAsync(Announcement announcement, Guid actorUserId, string change, IEnumerable<Guid> audienceUserIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FileChangedAsync(FileObject fileObject, Attachment attachment, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTaskNotificationProducer : ITaskNotificationProducer
    {
        public List<TaskNotificationRecipientRequest> Requests { get; } = [];
        public Task ProduceAsync(TaskNotificationRecipientRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTaskUnitOfWork : ITaskCommandUnitOfWork
    {
        public int SaveCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<TaskCommandSaveOutcome> SaveTaskCommandAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(new TaskCommandSaveOutcome(TaskCommandSaveResult.Saved));
        }
    }
}
