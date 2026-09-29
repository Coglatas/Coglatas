using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Projects;

public sealed class TaskNotificationRecipientPolicyTests
{
    public enum ActorSuppressionCategory
    {
        AssignmentAdded,
        AssignmentRemoved,
        ReviewerAssigned,
        DirectMention,
        ReviewSubmitted,
        ReviewReturned,
        BecameBlocked,
        MajorDeadlineChanged,
        ImportantComment
    }

    public static TheoryData<ActorSuppressionCategory> ActorSuppressionCategories =>
        new()
        {
            ActorSuppressionCategory.AssignmentAdded,
            ActorSuppressionCategory.AssignmentRemoved,
            ActorSuppressionCategory.ReviewerAssigned,
            ActorSuppressionCategory.DirectMention,
            ActorSuppressionCategory.ReviewSubmitted,
            ActorSuppressionCategory.ReviewReturned,
            ActorSuppressionCategory.BecameBlocked,
            ActorSuppressionCategory.MajorDeadlineChanged,
            ActorSuppressionCategory.ImportantComment
        };

    [Fact]
    public async Task AssigneeAddedNotifiesExactlyTheNewPrimaryAssignee()
    {
        var fixture = new Fixture();
        var assignee = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = assignee;

        var result = await fixture.ResolveAsync(
            TaskNotificationEventKind.PrimaryAssigneeChanged,
            newPrimaryAssigneeUserId: assignee);

        AssertRecipients(result, [assignee]);
    }

    [Fact]
    public async Task AssigneeRemovedUsesThePreMutationPrimaryAssigneeSnapshot()
    {
        var fixture = new Fixture();
        var previousAssignee = fixture.AddActiveUser();
        var unrelatedPersistedAssignee = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = unrelatedPersistedAssignee;

        var result = await fixture.ResolveAsync(
            TaskNotificationEventKind.PrimaryAssigneeChanged,
            previousPrimaryAssigneeUserId: previousAssignee);

        AssertRecipients(result, [previousAssignee]);
    }

    [Fact]
    public async Task AssigneeReplacementIncludesOldAndNewAndDeduplicatesSameRelationship()
    {
        var fixture = new Fixture();
        var previousAssignee = fixture.AddActiveUser();
        var newAssignee = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = newAssignee;

        var replacement = await fixture.ResolveAsync(
            TaskNotificationEventKind.PrimaryAssigneeChanged,
            previousPrimaryAssigneeUserId: previousAssignee,
            newPrimaryAssigneeUserId: newAssignee);
        var duplicate = await fixture.ResolveAsync(
            TaskNotificationEventKind.PrimaryAssigneeChanged,
            previousPrimaryAssigneeUserId: newAssignee,
            newPrimaryAssigneeUserId: newAssignee);

        AssertRecipients(replacement, [previousAssignee, newAssignee]);
        AssertRecipients(duplicate, [newAssignee]);
    }

    [Fact]
    public async Task ReviewerAssignedNotifiesExactlyTheNewReviewer()
    {
        var fixture = new Fixture();
        var reviewer = fixture.AddActiveUser();
        fixture.Task.ReviewerUserId = reviewer;

        var result = await fixture.ResolveAsync(
            TaskNotificationEventKind.ReviewerAssigned,
            newReviewerUserId: reviewer);

        AssertRecipients(result, [reviewer]);
    }

    [Fact]
    public async Task DirectMentionUsesOnlyActiveCurrentlyAuthorizedUsersAndSuppressesActor()
    {
        var fixture = new Fixture();
        var actor = fixture.AddActiveUser();
        var recipient = fixture.AddActiveUser();
        var inactive = fixture.AddUser(UserStatus.Suspended);
        var unauthorized = fixture.AddActiveUser();
        var missing = Guid.NewGuid();
        fixture.Authorization.DeniedUserIds.Add(unauthorized);

        var result = await fixture.ResolveAsync(
            TaskNotificationEventKind.TaskCommentSignificant,
            actorUserId: actor,
            validDirectMentionUserIds: [actor, recipient, recipient, inactive, unauthorized, missing, Guid.Empty]);

        AssertRecipients(result, [recipient]);
    }

    [Fact]
    public async Task ReviewSubmittedNotifiesExactlyTheCurrentReviewer()
    {
        var fixture = new Fixture();
        var primary = fixture.AddActiveUser();
        var reviewer = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = primary;
        fixture.Task.ReviewerUserId = reviewer;

        var result = await fixture.ResolveAsync(TaskNotificationEventKind.ReviewSubmitted);

        AssertRecipients(result, [reviewer]);
    }

    [Fact]
    public async Task ReviewReturnedNotifiesExactlyTheCurrentPrimaryAssignee()
    {
        var fixture = new Fixture();
        var primary = fixture.AddActiveUser();
        var reviewer = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = primary;
        fixture.Task.ReviewerUserId = reviewer;

        var result = await fixture.ResolveAsync(TaskNotificationEventKind.ReviewReturned);

        AssertRecipients(result, [primary]);
    }

    [Fact]
    public async Task BecameBlockedNotifiesPrimaryAssigneeAndReviewer()
    {
        var fixture = new Fixture();
        var primary = fixture.AddActiveUser();
        var reviewer = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = primary;
        fixture.Task.ReviewerUserId = reviewer;

        var result = await fixture.ResolveAsync(TaskNotificationEventKind.BecameBlocked);

        AssertRecipients(result, [primary, reviewer]);
    }

    [Fact]
    public async Task MajorDeadlineChangeNotifiesPrimaryAssigneeAndReviewer()
    {
        var fixture = new Fixture();
        var primary = fixture.AddActiveUser();
        var reviewer = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = primary;
        fixture.Task.ReviewerUserId = reviewer;

        var result = await fixture.ResolveAsync(
            TaskNotificationEventKind.MajorDeadlineChanged,
            deadlineChangeClassification: TaskDeadlineChangeClassification.CrossedUrgencyBoundary);

        AssertRecipients(result, [primary, reviewer]);
    }

    [Fact]
    public async Task ImportantCommentUnionsPrimaryReviewerCollaboratorsAndMentionsOnce()
    {
        var fixture = new Fixture();
        var primary = fixture.AddActiveUser();
        var reviewer = fixture.AddActiveUser();
        var collaborator = fixture.AddActiveUser();
        var mentioned = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = primary;
        fixture.Task.ReviewerUserId = reviewer;
        fixture.Projects.Collaborators.AddRange([
            new WorkItemCollaborator { TaskItemId = fixture.Task.Id, UserId = collaborator },
            new WorkItemCollaborator { TaskItemId = fixture.Task.Id, UserId = primary },
            new WorkItemCollaborator { TaskItemId = fixture.Task.Id, UserId = collaborator }
        ]);

        var result = await fixture.ResolveAsync(
            TaskNotificationEventKind.TaskCommentSignificant,
            validDirectMentionUserIds: [mentioned, collaborator],
            isImportantComment: true);

        AssertRecipients(result, [primary, reviewer, collaborator, mentioned]);
    }

    [Fact]
    public async Task OrdinaryCommentCannotNotifyAllOrUseTheWatchLayer()
    {
        var fixture = new Fixture();
        var watcher = fixture.AddActiveUser();
        fixture.Projects.Watches.Add(new WorkItemWatchState
        {
            TaskItemId = fixture.Task.Id,
            UserId = watcher,
            IsWatching = true
        });

        var result = await fixture.ResolveAsync(TaskNotificationEventKind.OrdinaryComment);

        AssertRecipients(result, []);
        Assert.Equal(0, fixture.Projects.ListWatchStatesCallCount);
    }

    [Fact]
    public async Task ImportantCommentStillUsesAuthorizedOptionalWatchersWhenMandatorySetIsEmpty()
    {
        var fixture = new Fixture();
        var watcher = fixture.AddActiveUser();
        fixture.Projects.Watches.Add(new WorkItemWatchState
        {
            TaskItemId = fixture.Task.Id,
            UserId = watcher,
            IsWatching = true
        });

        var result = await fixture.ResolveAsync(
            TaskNotificationEventKind.TaskCommentSignificant,
            isImportantComment: true);

        Assert.Empty(result.MandatoryRecipientUserIds);
        Assert.Equal([watcher], result.WatchDerivedRecipientUserIds);
        Assert.Equal([watcher], result.RecipientUserIds);
    }

    [Fact]
    public async Task EmptySignificantCommentAndNoneDeadlineCannotReachWatchers()
    {
        var fixture = new Fixture();
        var watcher = fixture.AddActiveUser();
        fixture.Projects.Watches.Add(new WorkItemWatchState { TaskItemId = fixture.Task.Id, UserId = watcher, IsWatching = true });

        var emptyComment = await fixture.ResolveAsync(TaskNotificationEventKind.TaskCommentSignificant);
        var unchangedDeadline = await fixture.ResolveAsync(
            TaskNotificationEventKind.MajorDeadlineChanged,
            deadlineChangeClassification: TaskDeadlineChangeClassification.None);

        AssertRecipients(emptyComment, []);
        AssertRecipients(unchangedDeadline, []);
        Assert.Equal(0, fixture.Projects.ListWatchStatesCallCount);
    }

    [Fact]
    public async Task ActorSuppressionCoversSelfAssignmentAndSelfMention()
    {
        var fixture = new Fixture();
        var actor = fixture.AddActiveUser();
        fixture.Task.PrimaryAssigneeUserId = actor;

        var selfAssignment = await fixture.ResolveAsync(
            TaskNotificationEventKind.PrimaryAssigneeChanged,
            actorUserId: actor,
            newPrimaryAssigneeUserId: actor);
        var selfMention = await fixture.ResolveAsync(
            TaskNotificationEventKind.TaskCommentSignificant,
            actorUserId: actor,
            validDirectMentionUserIds: [actor]);

        AssertRecipients(selfAssignment, []);
        AssertRecipients(selfMention, []);
    }

    [Theory]
    [MemberData(nameof(ActorSuppressionCategories))]
    public async Task ActorIsExcludedWhenTheyAreTheOnlyMandatoryRecipientForEveryCategory(
        ActorSuppressionCategory category)
    {
        var fixture = new Fixture();
        var actor = fixture.AddActiveUser();
        fixture.Projects.Watches.Add(new WorkItemWatchState
        {
            TaskItemId = fixture.Task.Id,
            UserId = actor,
            IsWatching = true
        });

        var result = await ResolveActorSuppressionCategoryAsync(fixture, category, actor);

        AssertRecipients(result, []);
    }

    [Theory]
    [MemberData(nameof(ActorSuppressionCategories))]
    public async Task ActorIsExcludedFromMandatoryAndWatchUnionWithoutSuppressingOtherRecipients(
        ActorSuppressionCategory category)
    {
        var fixture = new Fixture();
        var actor = fixture.AddActiveUser();
        var otherMandatoryRecipient = fixture.AddActiveUser();
        var otherWatcher = fixture.AddActiveUser();
        fixture.Projects.Watches.AddRange([
            new WorkItemWatchState { TaskItemId = fixture.Task.Id, UserId = actor, IsWatching = true },
            new WorkItemWatchState { TaskItemId = fixture.Task.Id, UserId = otherWatcher, IsWatching = true }
        ]);

        var result = await ResolveActorSuppressionCategoryAsync(
            fixture,
            category,
            actor,
            otherMandatoryRecipient);

        var expectedMandatory = CategorySupportsMultipleMandatoryRecipients(category)
            ? new[] { otherMandatoryRecipient }
            : [];
        AssertSetEqual(expectedMandatory, result.MandatoryRecipientUserIds);
        Assert.Equal([otherWatcher], result.WatchDerivedRecipientUserIds);
        AssertSetEqual(expectedMandatory.Append(otherWatcher).ToArray(), result.RecipientUserIds);
        Assert.DoesNotContain(actor, result.RecipientUserIds);
    }

    [Fact]
    public async Task WatchOptOutOnlyAffectsOptionalRecipients()
    {
        var fixture = new Fixture();
        var mandatoryOptedOut = fixture.AddActiveUser();
        var optionalWatcher = fixture.AddActiveUser();
        var optionalOptedOut = fixture.AddActiveUser();
        var staleNotWatching = fixture.AddActiveUser();
        fixture.Task.ReviewerUserId = mandatoryOptedOut;
        fixture.Projects.Watches.AddRange([
            new WorkItemWatchState { TaskItemId = fixture.Task.Id, UserId = mandatoryOptedOut, IsWatching = false, IsExplicitOptOut = true },
            new WorkItemWatchState { TaskItemId = fixture.Task.Id, UserId = optionalWatcher, IsWatching = true },
            new WorkItemWatchState { TaskItemId = fixture.Task.Id, UserId = optionalOptedOut, IsWatching = true, IsExplicitOptOut = true },
            new WorkItemWatchState { TaskItemId = fixture.Task.Id, UserId = staleNotWatching, IsWatching = false }
        ]);

        var result = await fixture.ResolveAsync(TaskNotificationEventKind.ReviewSubmitted);

        Assert.Equal([mandatoryOptedOut], result.MandatoryRecipientUserIds);
        Assert.Equal([optionalWatcher], result.WatchDerivedRecipientUserIds);
        AssertSetEqual([mandatoryOptedOut, optionalWatcher], result.RecipientUserIds);
    }

    private static void AssertRecipients(TaskNotificationRecipientResult actual, IReadOnlyCollection<Guid> expected)
    {
        AssertSetEqual(expected, actual.MandatoryRecipientUserIds);
        Assert.Empty(actual.WatchDerivedRecipientUserIds);
        AssertSetEqual(expected, actual.RecipientUserIds);
    }

    private static void AssertSetEqual(IReadOnlyCollection<Guid> expected, IReadOnlyCollection<Guid> actual) =>
        Assert.Equal(expected.OrderBy(id => id), actual.OrderBy(id => id));

    private static Task<TaskNotificationRecipientResult> ResolveActorSuppressionCategoryAsync(
        Fixture fixture,
        ActorSuppressionCategory category,
        Guid actor,
        Guid? otherMandatoryRecipient = null)
    {
        switch (category)
        {
            case ActorSuppressionCategory.AssignmentAdded:
                return fixture.ResolveAsync(
                    TaskNotificationEventKind.PrimaryAssigneeChanged,
                    actorUserId: actor,
                    newPrimaryAssigneeUserId: actor);

            case ActorSuppressionCategory.AssignmentRemoved:
                return fixture.ResolveAsync(
                    TaskNotificationEventKind.PrimaryAssigneeChanged,
                    actorUserId: actor,
                    previousPrimaryAssigneeUserId: actor);

            case ActorSuppressionCategory.ReviewerAssigned:
                return fixture.ResolveAsync(
                    TaskNotificationEventKind.ReviewerAssigned,
                    actorUserId: actor,
                    newReviewerUserId: actor);

            case ActorSuppressionCategory.DirectMention:
                return fixture.ResolveAsync(
                    TaskNotificationEventKind.TaskCommentSignificant,
                    actorUserId: actor,
                    validDirectMentionUserIds: otherMandatoryRecipient.HasValue
                        ? [actor, otherMandatoryRecipient.Value]
                        : [actor]);

            case ActorSuppressionCategory.ReviewSubmitted:
                fixture.Task.ReviewerUserId = actor;
                return fixture.ResolveAsync(TaskNotificationEventKind.ReviewSubmitted, actorUserId: actor);

            case ActorSuppressionCategory.ReviewReturned:
                fixture.Task.PrimaryAssigneeUserId = actor;
                return fixture.ResolveAsync(TaskNotificationEventKind.ReviewReturned, actorUserId: actor);

            case ActorSuppressionCategory.BecameBlocked:
                fixture.Task.PrimaryAssigneeUserId = actor;
                fixture.Task.ReviewerUserId = otherMandatoryRecipient;
                return fixture.ResolveAsync(TaskNotificationEventKind.BecameBlocked, actorUserId: actor);

            case ActorSuppressionCategory.MajorDeadlineChanged:
                fixture.Task.PrimaryAssigneeUserId = actor;
                fixture.Task.ReviewerUserId = otherMandatoryRecipient;
                return fixture.ResolveAsync(
                    TaskNotificationEventKind.MajorDeadlineChanged,
                    actorUserId: actor,
                    deadlineChangeClassification: TaskDeadlineChangeClassification.ShiftAtLeast24Hours);

            case ActorSuppressionCategory.ImportantComment:
                fixture.Task.PrimaryAssigneeUserId = actor;
                fixture.Task.ReviewerUserId = otherMandatoryRecipient;
                return fixture.ResolveAsync(
                    TaskNotificationEventKind.TaskCommentSignificant,
                    actorUserId: actor,
                    isImportantComment: true);

            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }
    }

    private static bool CategorySupportsMultipleMandatoryRecipients(ActorSuppressionCategory category) =>
        category is ActorSuppressionCategory.DirectMention
            or ActorSuppressionCategory.BecameBlocked
            or ActorSuppressionCategory.MajorDeadlineChanged
            or ActorSuppressionCategory.ImportantComment;

    private sealed class Fixture
    {
        public Fixture()
        {
            Task = new TaskItem
            {
                TenantId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                Title = "Task"
            };
            Policy = new TaskNotificationRecipientPolicy(Projects, Users, Authorization);
        }

        public TaskItem Task { get; }
        public FakeProjects Projects { get; } = new();
        private FakeUsers Users { get; } = new();
        public FakeProjectAuthorization Authorization { get; } = new();
        private TaskNotificationRecipientPolicy Policy { get; }

        public Guid AddActiveUser() => AddUser(UserStatus.Active);

        public Guid AddUser(UserStatus status)
        {
            var user = new User { DisplayName = "Recipient", Status = status };
            Users.Items[user.Id] = user;
            return user.Id;
        }

        public Task<TaskNotificationRecipientResult> ResolveAsync(
            TaskNotificationEventKind eventKind,
            Guid? actorUserId = null,
            Guid? previousPrimaryAssigneeUserId = null,
            Guid? newPrimaryAssigneeUserId = null,
            Guid? newReviewerUserId = null,
            IReadOnlyCollection<Guid>? validDirectMentionUserIds = null,
            bool isImportantComment = false,
            TaskDeadlineChangeClassification deadlineChangeClassification = TaskDeadlineChangeClassification.None) =>
            Policy.ResolveAsync(new TaskNotificationRecipientRequest(
                Task,
                eventKind,
                actorUserId,
                previousPrimaryAssigneeUserId,
                newPrimaryAssigneeUserId,
                newReviewerUserId,
                validDirectMentionUserIds,
                isImportantComment,
                deadlineChangeClassification));
    }

    private sealed class FakeUsers : IUserRepository
    {
        public Dictionary<Guid, User> Items { get; } = [];

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.GetValueOrDefault(id));

        public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult<User?>(null);

        public Task<IReadOnlyList<User>> GetActiveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<User>>(Items.Values
                .Where(user => ids.Contains(user.Id) && user.Status == UserStatus.Active && !user.DeletedAt.HasValue)
                .ToArray());

        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            Items[user.Id] = user;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjectAuthorization : IProjectAuthorizationService
    {
        public HashSet<Guid> DeniedUserIds { get; } = [];

        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(!DeniedUserIds.Contains(userId));

        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeProjects : IProjectRepository
    {
        public List<WorkItemCollaborator> Collaborators { get; } = [];
        public List<WorkItemWatchState> Watches { get; } = [];
        public int ListWatchStatesCallCount { get; private set; }

        public Task<IReadOnlyList<WorkItemCollaborator>> ListCollaboratorsAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCollaborator>>(Collaborators.Where(item => item.TaskItemId == taskItemId).ToArray());

        public Task<IReadOnlyList<WorkItemWatchState>> ListWatchStatesAsync(Guid taskItemId, CancellationToken cancellationToken = default)
        {
            ListWatchStatesCallCount++;
            return Task.FromResult<IReadOnlyList<WorkItemWatchState>>(Watches.Where(item => item.TaskItemId == taskItemId).ToArray());
        }

        public Task<IReadOnlyList<Project>> ListVisibleAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Project>>([]);
        public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
        public Task<ProjectMember?> GetMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<ProjectMember?>(null);
        public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectMember>>([]);
        public Task<IReadOnlyList<Milestone>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Milestone>>([]);
        public Task<Milestone?> GetMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default) => Task.FromResult<Milestone?>(null);
        public Task<IReadOnlyList<TaskItem>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskItem>>([]);
        public Task<TaskItem?> GetTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<TaskItem?>(null);
        public Task<IReadOnlyList<TaskAssignment>> ListAssignmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskAssignment>>([]);
        public Task<TaskAssignment?> GetAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default) => Task.FromResult<TaskAssignment?>(null);
        public Task<IReadOnlyList<TaskDependency>> ListDependenciesAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>([]);
        public Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>([]);
        public Task<TaskDependency?> GetDependencyAsync(Guid dependencyId, CancellationToken cancellationToken = default) => Task.FromResult<TaskDependency?>(null);
        public Task<bool> DependencyExistsAsync(Guid predecessorTaskId, Guid successorTaskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
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
}
