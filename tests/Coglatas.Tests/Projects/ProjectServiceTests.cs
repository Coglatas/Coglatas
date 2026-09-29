using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Projects;

public sealed class ProjectServiceTests
{
    [Fact]
    public async Task NonMemberCannotViewProject()
    {
        var fixture = ProjectFixture.Create();

        var canView = await fixture.ProjectAuthorization.CanViewProject(Guid.NewGuid(), fixture.Project.Id);

        Assert.False(canView);
    }

    [Fact]
    public async Task ProjectMemberCanViewTasks()
    {
        var fixture = ProjectFixture.Create();
        var member = fixture.AddUser();
        fixture.Current.UserIdValue = member.Id;
        fixture.AddProjectMember(member.Id, ProjectRole.Viewer);
        fixture.AddTask("Storyboard");

        var result = await fixture.Service.ListTasksAsync(fixture.Project.Id, new TaskListQuery());

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Items);
    }

    [Theory]
    [InlineData(TaskStageCategory.Backlog)]
    [InlineData(TaskStageCategory.Todo)]
    [InlineData(TaskStageCategory.InProgress)]
    [InlineData(TaskStageCategory.Review)]
    [InlineData(TaskStageCategory.Done)]
    [InlineData(TaskStageCategory.Cancelled)]
    public async Task TaskListProjectsCanonicalStageBlockedTimestampAndArtifactState(
        TaskStageCategory category)
    {
        var fixture = ProjectFixture.Create();
        var viewer = fixture.AddUser();
        fixture.Current.UserIdValue = viewer.Id;
        fixture.AddProjectMember(viewer.Id, ProjectRole.Viewer);
        var stage = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            WorkspaceId = fixture.Workspace.Id,
            Name = $"Configured {category}",
            InternalCategory = category,
            SortKey = 2000
        };
        fixture.Projects.Stages[stage.Id] = stage;
        var createdAt = new DateTimeOffset(2026, 8, 20, 1, 2, 3, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(4);
        var task = fixture.AddTask($"{category} task");
        task.WorkflowStageId = stage.Id;
        task.WorkflowStage = stage;
        task.IsBlocked = true;
        task.CreatedAt = createdAt;
        task.UpdatedAt = updatedAt;
        fixture.Projects.TaskIdsWithArtifacts.Add(task.Id);

        var result = await fixture.Service.ListTasksAsync(
            fixture.Project.Id,
            new TaskListQuery());

        Assert.True(result.IsSuccess, result.Error);
        var response = Assert.Single(result.Value!.Items);
        Assert.Equal(stage.Id, response.WorkflowStageId);
        Assert.Equal(stage.Name, response.WorkflowStageName);
        Assert.Equal(category, response.StageCategory);
        Assert.True(response.IsBlocked);
        Assert.True(response.HasArtifact);
        Assert.Equal(createdAt, response.CreatedAt);
        Assert.Equal(updatedAt, response.UpdatedAt);
        Assert.Equal(1, fixture.Projects.ListTaskIdsWithArtifactsCallCount);

        var json = System.Text.Json.JsonSerializer.Serialize(
            response,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(category.ToString(), document.RootElement.GetProperty("stageCategory").GetString());
    }

    [Fact]
    public async Task TaskListDoesNotReadArtifactStateBeforeProjectAuthorization()
    {
        var fixture = ProjectFixture.Create();
        var outsider = fixture.AddUser(addWorkspaceMember: false);
        fixture.Current.UserIdValue = outsider.Id;
        fixture.AddTask("Private task");

        var result = await fixture.Service.ListTasksAsync(
            fixture.Project.Id,
            new TaskListQuery());

        Assert.False(result.IsSuccess);
        Assert.Equal("Project not found.", result.Error);
        Assert.Equal(0, fixture.Projects.ListTaskIdsWithArtifactsCallCount);
    }

    [Fact]
    public async Task LegacyTaskListUsesReadableCanonicalStageFallback()
    {
        var fixture = ProjectFixture.Create();
        var viewer = fixture.AddUser();
        fixture.Current.UserIdValue = viewer.Id;
        fixture.AddProjectMember(viewer.Id, ProjectRole.Viewer);
        var task = fixture.AddTask("Legacy active task");
        task.Status = TaskItemStatus.InProgress;
        task.WorkflowStageId = null;
        task.WorkflowStage = null;

        var result = await fixture.Service.ListTasksAsync(
            fixture.Project.Id,
            new TaskListQuery());

        Assert.True(result.IsSuccess, result.Error);
        var response = Assert.Single(result.Value!.Items);
        Assert.Null(response.WorkflowStageId);
        Assert.Equal("In progress", response.WorkflowStageName);
        Assert.Equal(TaskStageCategory.InProgress, response.StageCategory);
    }

    [Fact]
    public async Task UserOutsideProjectCannotBeAssigned()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        var outsider = fixture.AddUser(addWorkspaceMember: false);
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var task = fixture.AddTask("Layout");

        var result = await fixture.Service.AddAssignmentAsync(task.Id, new AddTaskAssignmentRequest(outsider.Id, TaskAssignmentRole.Assignee, 2));

        Assert.False(result.IsSuccess);
    }


    [Fact]
    public async Task ProjectParticipantCanCreateAndUpdateTaskFields()
    {
        var fixture = ProjectFixture.Create();
        var contributor = fixture.AddUser();
        fixture.Current.UserIdValue = contributor.Id;
        fixture.AddProjectMember(contributor.Id, ProjectRole.Contributor);
        var milestone = fixture.AddMilestone("Build", 1);
        var start = new DateOnly(2026, 6, 10);
        var due = new DateOnly(2026, 6, 30);

        var created = await fixture.Service.CreateTaskAsync(fixture.Project.Id, new CreateTaskItemRequest(milestone.Id, "Prep assets", "Collect references", TaskPriority.High, start, due));

        Assert.True(created.IsSuccess);
        Assert.Equal(milestone.Id, created.Value!.MilestoneId);
        Assert.Equal(TaskPriority.High, created.Value.Priority);
        Assert.Equal(TaskItemStatus.NotStarted, created.Value.Status);
        Assert.Equal(0, created.Value.ProgressPercent);
        var createdEntity = fixture.Projects.Tasks[created.Value.Id];
        Assert.Equal(fixture.Projects.Stages.Values.Single().Id, createdEntity.WorkflowStageId);
        Assert.Equal(1000, createdEntity.SortKey);

        var membership = fixture.Projects.Members.Single(member => member.ProjectId == fixture.Project.Id && member.UserId == contributor.Id);
        membership.Role = ProjectRole.Manager;
        var updated = await fixture.Service.UpdateTaskAsync(created.Value.Id, new UpdateTaskItemRequest(null, null, null, TaskItemStatus.InProgress, TaskPriority.Critical, null, null, 35));

        Assert.True(updated.IsSuccess);
        Assert.Equal(TaskItemStatus.InProgress, updated.Value!.Status);
        Assert.Equal(TaskPriority.Critical, updated.Value.Priority);
        Assert.Equal(35, updated.Value.ProgressPercent);
        Assert.Equal(2, fixture.Projects.Tasks[created.Value.Id].VersionNo);
    }

    [Fact]
    public async Task TaskCreationPersistsStructuredBriefWithoutReinterpretingLegacyDescriptionOrProjectContext()
    {
        var fixture = ProjectFixture.Create();
        var contributor = fixture.AddUser();
        fixture.Current.UserIdValue = contributor.Id;
        fixture.AddProjectMember(contributor.Id, ProjectRole.Contributor);
        fixture.EnsureProjectOperational();
        fixture.Project.Description = "Project context must remain separately labelled.";

        var created = await fixture.Service.CreateTaskAsync(
            fixture.Project.Id,
            new CreateTaskItemRequest(
                null,
                "Structured brief",
                "  Legacy free-form notes stay here.  ",
                TaskPriority.Medium,
                null,
                null,
                "  Reach the review-ready state.  ",
                "  A signed-off package.  ",
                "  Keep the public URL stable.  "));

        Assert.True(created.IsSuccess);
        var task = fixture.Projects.Tasks[created.Value!.Id];
        Assert.Equal("Legacy free-form notes stay here.", task.Description);
        Assert.Equal("Reach the review-ready state.", task.BriefGoal);
        Assert.Equal("A signed-off package.", task.BriefDeliverable);
        Assert.Equal("Keep the public URL stable.", task.BriefConstraints);
        Assert.NotEqual(fixture.Project.Description, task.BriefGoal);
    }

    [Fact]
    public async Task LegacyDescriptionOnlyTaskCreationLeavesStructuredBriefUnset()
    {
        var fixture = ProjectFixture.Create();
        var contributor = fixture.AddUser();
        fixture.Current.UserIdValue = contributor.Id;
        fixture.AddProjectMember(contributor.Id, ProjectRole.Contributor);
        fixture.EnsureProjectOperational();
        fixture.Project.Description = "Project context is not a Task Brief default.";

        var created = await fixture.Service.CreateTaskAsync(
            fixture.Project.Id,
            new CreateTaskItemRequest(null, "Legacy task", "Free-form input", TaskPriority.Low, null, null));

        Assert.True(created.IsSuccess);
        var task = fixture.Projects.Tasks[created.Value!.Id];
        Assert.Equal("Free-form input", task.Description);
        Assert.Null(task.BriefGoal);
        Assert.Null(task.BriefDeliverable);
        Assert.Null(task.BriefConstraints);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("deliverable")]
    [InlineData("constraints")]
    public async Task TaskCreationRejectsEachOversizedStructuredBriefFieldWithoutSideEffects(string field)
    {
        var fixture = ProjectFixture.Create();
        var contributor = fixture.AddUser();
        fixture.Current.UserIdValue = contributor.Id;
        fixture.AddProjectMember(contributor.Id, ProjectRole.Contributor);
        fixture.EnsureProjectOperational();
        var oversized = new string('x', TaskBriefText.MaximumFieldLength + 1);

        var result = await fixture.Service.CreateTaskAsync(
            fixture.Project.Id,
            new CreateTaskItemRequest(
                null,
                "Rejected brief",
                "Free-form input",
                TaskPriority.Medium,
                null,
                null,
                field == "goal" ? oversized : null,
                field == "deliverable" ? oversized : null,
                field == "constraints" ? oversized : null));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_BRIEF_FIELD_TOO_LONG", result.ErrorDetail?.Code);
        Assert.Equal(field, result.ErrorDetail?.Target);
        Assert.Contains($"Task brief {char.ToUpperInvariant(field[0])}{field[1..]}", result.ErrorDetail?.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Projects.Tasks);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task RevokedWorkspaceMemberCannotCreateTaskThroughRetainedProjectMembership()
    {
        var fixture = ProjectFixture.Create();
        var contributor = fixture.AddUser();
        fixture.Current.UserIdValue = contributor.Id;
        fixture.AddProjectMember(contributor.Id, ProjectRole.Contributor);
        fixture.Workspaces.Members.Single(member => member.UserId == contributor.Id).Status =
            MembershipStatus.Suspended;

        var result = await fixture.Service.CreateTaskAsync(
            fixture.Project.Id,
            new CreateTaskItemRequest(null, "Denied task", null, TaskPriority.Medium, null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("You are not allowed to create tasks.", result.Error);
        Assert.Empty(fixture.Projects.Tasks);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task ArchivedProjectMemberCannotCreateTask()
    {
        var fixture = ProjectFixture.Create();
        var contributor = fixture.AddUser();
        fixture.Current.UserIdValue = contributor.Id;
        fixture.AddProjectMember(contributor.Id, ProjectRole.Contributor);
        fixture.Project.Status = ProjectStatus.Archived;

        var result = await fixture.Service.CreateTaskAsync(
            fixture.Project.Id,
            new CreateTaskItemRequest(null, "Denied task", null, TaskPriority.Medium, null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("You are not allowed to create tasks.", result.Error);
        Assert.Empty(fixture.Projects.Tasks);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    public async Task TaskResponseProjectsMutationPermissionsAndRejectsUnauthorizedUpdate()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        var viewer = fixture.AddUser();
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.AddProjectMember(viewer.Id, ProjectRole.Viewer);
        var task = fixture.AddTask("Permissioned task");

        fixture.Current.UserIdValue = manager.Id;
        var managerDetail = await fixture.Service.GetTaskAsync(task.Id);

        Assert.True(managerDetail.IsSuccess);
        Assert.True(managerDetail.Value!.UiPermissions.CanEdit);
        Assert.True(managerDetail.Value.UiPermissions.CanAssign);
        Assert.True(managerDetail.Value.UiPermissions.CanDelete);
        Assert.False(managerDetail.Value.UiPermissions.CanChangeStatus);
        Assert.Empty(managerDetail.Value.UiPermissions.AllowedTransitions);

        fixture.Current.UserIdValue = viewer.Id;
        var viewerDetail = await fixture.Service.GetTaskAsync(task.Id);
        var visibleProjects = await fixture.Service.ListAsync(new ProjectListQuery());
        var denied = await fixture.Service.UpdateTaskAsync(task.Id, new UpdateTaskItemRequest(null, "Rejected", null, null, null, null, null, null));

        Assert.True(viewerDetail.IsSuccess);
        Assert.False(viewerDetail.Value!.UiPermissions.CanEdit);
        Assert.False(visibleProjects.Value!.Items.Single().UiPermissions.CanCreateTask);
        Assert.False(denied.IsSuccess);
        Assert.Equal("You are not allowed to update this task.", denied.Error);
        Assert.Equal("Permissioned task", task.Title);
    }

    [Fact]
    public async Task TaskListSupportsPagingSearchAndFilters()
    {
        var fixture = ProjectFixture.Create();
        var viewer = fixture.AddUser();
        fixture.Current.UserIdValue = viewer.Id;
        fixture.AddProjectMember(viewer.Id, ProjectRole.Viewer);
        var milestone = fixture.AddMilestone("Delivery", 1);
        fixture.AddTask("Draft brief").Status = TaskItemStatus.Blocked;
        var target = fixture.AddTask("Review cut");
        target.MilestoneId = milestone.Id;
        target.Status = TaskItemStatus.WaitingReview;
        target.Priority = TaskPriority.Critical;

        var result = await fixture.Service.ListTasksAsync(fixture.Project.Id, new TaskListQuery(Search: "review", Status: TaskItemStatus.WaitingReview, Priority: TaskPriority.Critical, MilestoneId: milestone.Id, Page: 1, PageSize: 1));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.TotalCount);
        Assert.Equal(target.Id, Assert.Single(result.Value.Items).Id);
    }

    [Fact]
    public async Task AssignmentUpdateAuditsCanonicalCompatibilityChanges()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        var assignee = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.AddProjectMember(assignee.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Composite");
        task.PrimaryAssigneeUserId = assignee.Id;
        var assignment = fixture.AddLegacyAssignment(task, assignee, TaskAssignmentRole.Assignee, manager.Id);

        var updated = await fixture.Service.UpdateAssignmentAsync(
            assignment.Id,
            new UpdateTaskAssignmentRequest(TaskAssignmentRole.Assignee, 3, 1));

        Assert.True(updated.IsSuccess);
        Assert.Equal(TaskAssignmentRole.Assignee, updated.Value!.Role);
        Assert.Equal(3, updated.Value.EstimatedHours);
        Assert.Equal(1, updated.Value.ActualHours);
        Assert.Equal(2, task.VersionNo);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "TaskAssignmentUpdated" && entry.EntityId == task.Id);
    }

    [Theory]
    [Trait("Scope", "TaskV1PR07B")]
    [InlineData(TaskAssignmentRole.Assignee, "assigneeChanged", WorkItemWatchAutomaticSource.PrimaryAssignee)]
    [InlineData(TaskAssignmentRole.Reviewer, "reviewerChanged", WorkItemWatchAutomaticSource.Reviewer)]
    [InlineData(TaskAssignmentRole.Support, "collaboratorChanged", WorkItemWatchAutomaticSource.Collaborator)]
    public async Task CompatibilitySupportedAssignmentAddMapsCanonicalRelationshipAtomically(
        TaskAssignmentRole role,
        string semanticChange,
        WorkItemWatchAutomaticSource watchSource)
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(recipient.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Compatibility add");

        var result = await fixture.Service.AddAssignmentAsync(
            task.Id,
            new AddTaskAssignmentRequest(recipient.Id, role, 1));

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Projects.Assignments);
        Assert.Equal(role == TaskAssignmentRole.Assignee ? recipient.Id : null, task.PrimaryAssigneeUserId);
        Assert.Equal(role == TaskAssignmentRole.Reviewer ? recipient.Id : null, task.ReviewerUserId);
        Assert.Equal(role == TaskAssignmentRole.Support, fixture.Projects.Collaborators.Any(item => item.UserId == recipient.Id));
        Assert.Equal(role == TaskAssignmentRole.Support ? 0 : 1, fixture.TaskNotifications.Requests.Count);
        Assert.Equal(1, fixture.Invalidations.TaskChangedCount);
        Assert.Equal([semanticChange], fixture.Invalidations.TaskAssignmentChanges);
        var watch = Assert.Single(fixture.Projects.Watches, state => state.UserId == recipient.Id);
        Assert.Equal(watchSource, watch.AutomaticSources);
        Assert.True(watch.IsWatching);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(2, task.VersionNo);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityChildRelationshipChangeAdvancesParentInSameSave()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(recipient.Id, ProjectRole.Contributor);
        var parent = fixture.AddTask("Parent");
        var child = fixture.AddTask("Child");
        child.ParentTaskItemId = parent.Id;

        var result = await fixture.Service.AddAssignmentAsync(
            child.Id,
            new AddTaskAssignmentRequest(recipient.Id, TaskAssignmentRole.Assignee, 1));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, child.VersionNo);
        Assert.Equal(2, parent.VersionNo);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(2, fixture.Invalidations.TaskChangedCount);
        Assert.Contains(
            fixture.Audit.Entries,
            entry => entry.Action == "TaskSubtasksChanged" && entry.EntityId == parent.Id);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityOwnerCreationFailsClosedWithoutMutation()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(recipient.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Historical owner");

        var result = await fixture.Service.AddAssignmentAsync(
            task.Id,
            new AddTaskAssignmentRequest(recipient.Id, TaskAssignmentRole.Owner, 1));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_ASSIGNMENT_ROLE_UNSUPPORTED|", result.Error);
        Assert.Empty(fixture.Projects.Assignments);
        Assert.Equal(1, task.VersionNo);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.Invalidations.TaskChangedCount);
    }

    [Theory]
    [Trait("Scope", "TaskV1PR07B")]
    [InlineData(TaskAssignmentRole.Assignee)]
    [InlineData(TaskAssignmentRole.Reviewer)]
    [InlineData(TaskAssignmentRole.Support)]
    public async Task HistoricalOwnerCanBeMigratedToSupportedCanonicalRole(TaskAssignmentRole newRole)
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(recipient.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Migrate owner");
        var assignment = fixture.AddLegacyAssignment(task, recipient, TaskAssignmentRole.Owner, actor.Id);

        var result = await fixture.Service.UpdateAssignmentAsync(
            assignment.Id,
            new UpdateTaskAssignmentRequest(newRole, 2, 1));

        Assert.True(result.IsSuccess);
        Assert.Equal(newRole, assignment.Role);
        Assert.Equal(newRole == TaskAssignmentRole.Assignee ? recipient.Id : null, task.PrimaryAssigneeUserId);
        Assert.Equal(newRole == TaskAssignmentRole.Reviewer ? recipient.Id : null, task.ReviewerUserId);
        Assert.Equal(newRole == TaskAssignmentRole.Support, fixture.Projects.Collaborators.Any(item => item.UserId == recipient.Id));
        Assert.Equal(newRole == TaskAssignmentRole.Support ? 0 : 1, fixture.TaskNotifications.Requests.Count);
        Assert.Equal(1, fixture.Invalidations.TaskAssignmentChangedCount);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task HistoricalOwnerHoursRemainEditableWithoutCreatingCanonicalOwnership()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        var task = fixture.AddTask("Historical owner hours");
        var assignment = fixture.AddLegacyAssignment(task, recipient, TaskAssignmentRole.Owner, actor.Id);

        var result = await fixture.Service.UpdateAssignmentAsync(
            assignment.Id,
            new UpdateTaskAssignmentRequest(TaskAssignmentRole.Owner, 3, 1));

        Assert.True(result.IsSuccess);
        Assert.Null(task.PrimaryAssigneeUserId);
        Assert.Null(task.ReviewerUserId);
        Assert.Empty(fixture.TaskNotifications.Requests);
        Assert.Equal(0, fixture.Invalidations.TaskAssignmentChangedCount);
        Assert.Equal(1, fixture.Invalidations.TaskChangedCount);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
    }

    [Theory]
    [Trait("Scope", "TaskV1PR07B")]
    [InlineData(TaskAssignmentRole.Assignee)]
    [InlineData(TaskAssignmentRole.Reviewer)]
    public async Task CanonicalRelationshipThenCompatibilityRowDoesNotDuplicateIntentOrSemanticEvent(TaskAssignmentRole role)
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(recipient.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Canonical then compatibility");

        var canonical = role == TaskAssignmentRole.Assignee
            ? await fixture.Commands.SetAssigneeAsync(task.Id, new TaskRelationshipUserRequest(recipient.Id, task.VersionNo))
            : await fixture.Commands.SetReviewerAsync(task.Id, new TaskRelationshipUserRequest(recipient.Id, task.VersionNo));
        var compatibility = await fixture.Service.AddAssignmentAsync(
            task.Id,
            new AddTaskAssignmentRequest(recipient.Id, role, 1));

        Assert.True(canonical.IsSuccess);
        Assert.True(compatibility.IsSuccess);
        Assert.Single(fixture.TaskNotifications.Requests);
        Assert.Equal(1, fixture.Invalidations.TaskChangedCount);
        Assert.Equal(0, fixture.Invalidations.TaskAssignmentChangedCount);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
    }

    [Theory]
    [Trait("Scope", "TaskV1PR07B")]
    [InlineData(TaskAssignmentRole.Assignee)]
    [InlineData(TaskAssignmentRole.Reviewer)]
    public async Task CompatibilityRowThenCanonicalRelationshipDoesNotDuplicateIntentOrVersion(TaskAssignmentRole role)
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(recipient.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Compatibility then canonical");

        var compatibility = await fixture.Service.AddAssignmentAsync(
            task.Id,
            new AddTaskAssignmentRequest(recipient.Id, role, 1));
        var committedVersion = task.VersionNo;
        var canonical = role == TaskAssignmentRole.Assignee
            ? await fixture.Commands.SetAssigneeAsync(task.Id, new TaskRelationshipUserRequest(recipient.Id, task.VersionNo))
            : await fixture.Commands.SetReviewerAsync(task.Id, new TaskRelationshipUserRequest(recipient.Id, task.VersionNo));

        Assert.True(compatibility.IsSuccess);
        Assert.True(canonical.IsSuccess);
        Assert.Single(fixture.TaskNotifications.Requests);
        Assert.Equal(committedVersion, task.VersionNo);
        Assert.Equal(1, fixture.Invalidations.TaskChangedCount);
        Assert.Equal(1, fixture.Invalidations.TaskAssignmentChangedCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompositeAssigneeToReviewerRoleChangeUsesOneSaveAndOneSemanticEvent()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(recipient.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Composite role change");
        task.PrimaryAssigneeUserId = recipient.Id;
        var assignment = fixture.AddLegacyAssignment(task, recipient, TaskAssignmentRole.Assignee, actor.Id);

        var result = await fixture.Service.UpdateAssignmentAsync(
            assignment.Id,
            new UpdateTaskAssignmentRequest(TaskAssignmentRole.Reviewer, 1, 0));

        Assert.True(result.IsSuccess);
        Assert.Null(task.PrimaryAssigneeUserId);
        Assert.Equal(recipient.Id, task.ReviewerUserId);
        Assert.Collection(
            fixture.TaskNotifications.Requests,
            request => Assert.Equal(TaskNotificationEventKind.PrimaryAssigneeChanged, request.EventKind),
            request => Assert.Equal(TaskNotificationEventKind.ReviewerAssigned, request.EventKind));
        Assert.Equal(["reviewerChanged"], fixture.Invalidations.TaskAssignmentChanges);
        Assert.Equal(1, fixture.Invalidations.TaskChangedCount);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        var watch = Assert.Single(fixture.Projects.Watches, state => state.UserId == recipient.Id);
        Assert.Equal(WorkItemWatchAutomaticSource.Reviewer, watch.AutomaticSources);
    }

    [Theory]
    [Trait("Scope", "TaskV1PR07B")]
    [InlineData(TaskAssignmentRole.Assignee)]
    [InlineData(TaskAssignmentRole.Reviewer)]
    [InlineData(TaskAssignmentRole.Support)]
    public async Task CompatibilityDeleteRemovesCanonicalMappingAndReconcilesWatch(TaskAssignmentRole role)
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        var task = fixture.AddTask("Compatibility delete");
        var assignment = fixture.AddLegacyAssignment(task, recipient, role, actor.Id);
        var source = role switch
        {
            TaskAssignmentRole.Assignee => WorkItemWatchAutomaticSource.PrimaryAssignee,
            TaskAssignmentRole.Reviewer => WorkItemWatchAutomaticSource.Reviewer,
            _ => WorkItemWatchAutomaticSource.Collaborator
        };
        if (role == TaskAssignmentRole.Assignee) task.PrimaryAssigneeUserId = recipient.Id;
        if (role == TaskAssignmentRole.Reviewer) task.ReviewerUserId = recipient.Id;
        if (role == TaskAssignmentRole.Support)
        {
            fixture.Projects.Collaborators.Add(new WorkItemCollaborator
            {
                TaskItemId = task.Id,
                UserId = recipient.Id,
                AddedByUserId = actor.Id,
                AddedAt = fixture.Clock.UtcNow
            });
        }
        fixture.Projects.Watches.Add(new WorkItemWatchState
        {
            TaskItemId = task.Id,
            UserId = recipient.Id,
            AutomaticSources = source,
            IsWatching = true,
            UpdatedAt = fixture.Clock.UtcNow
        });

        var result = await fixture.Service.DeleteAssignmentAsync(assignment.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(task.PrimaryAssigneeUserId);
        Assert.Null(task.ReviewerUserId);
        Assert.DoesNotContain(fixture.Projects.Collaborators, item => item.UserId == recipient.Id);
        Assert.Equal(role == TaskAssignmentRole.Assignee ? 1 : 0, fixture.TaskNotifications.Requests.Count);
        Assert.Equal(1, fixture.Invalidations.TaskChangedCount);
        Assert.Equal(1, fixture.Invalidations.TaskAssignmentChangedCount);
        var watch = Assert.Single(fixture.Projects.Watches, state => state.UserId == recipient.Id);
        Assert.Equal(WorkItemWatchAutomaticSource.None, watch.AutomaticSources);
        Assert.False(watch.IsWatching);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ActivePrimaryAssigneeCannotBeClearedThroughCompatibilityDelete()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var recipient = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        var task = fixture.AddTask("Active assignment");
        task.Status = TaskItemStatus.InProgress;
        task.PrimaryAssigneeUserId = recipient.Id;
        var assignment = fixture.AddLegacyAssignment(task, recipient, TaskAssignmentRole.Assignee, actor.Id);

        var result = await fixture.Service.DeleteAssignmentAsync(assignment.Id);

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_ASSIGNEE_REQUIRED|", result.Error);
        Assert.Equal(recipient.Id, task.PrimaryAssigneeUserId);
        Assert.Contains(assignment, fixture.Projects.Assignments);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.Empty(fixture.TaskNotifications.Requests);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task AmbiguousCompatibilitySingletonFailsBeforeMutation()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var first = fixture.AddUser();
        var second = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(first.Id, ProjectRole.Contributor);
        fixture.AddProjectMember(second.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Ambiguous compatibility rows");
        task.PrimaryAssigneeUserId = first.Id;
        fixture.AddLegacyAssignment(task, first, TaskAssignmentRole.Assignee, actor.Id);

        var result = await fixture.Service.AddAssignmentAsync(
            task.Id,
            new AddTaskAssignmentRequest(second.Id, TaskAssignmentRole.Assignee, 1));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_ASSIGNMENT_AMBIGUOUS|", result.Error);
        Assert.Equal(first.Id, task.PrimaryAssigneeUserId);
        Assert.Single(fixture.Projects.Assignments);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(0, fixture.Invalidations.TaskChangedCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityReviewerCannotEqualCanonicalPrimaryAssignee()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        var primaryAssignee = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        fixture.AddProjectMember(primaryAssignee.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Distinct reviewer");
        task.PrimaryAssigneeUserId = primaryAssignee.Id;

        var result = await fixture.Service.AddAssignmentAsync(
            task.Id,
            new AddTaskAssignmentRequest(primaryAssignee.Id, TaskAssignmentRole.Reviewer, 1));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_REVIEWER_MUST_DIFFER|", result.Error);
        Assert.Null(task.ReviewerUserId);
        Assert.Empty(fixture.Projects.Assignments);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
    }

    [Theory]
    [InlineData("IX_task_assignments_TenantId_TaskItemId_UserId_Role", "TASK_ALREADY_ASSIGNED")]
    [InlineData("IX_notification_user_states_TenantId_UserId", "TASK_CONFLICT")]
    public async Task AssignmentUniqueConflictUsesOnlyTheAssignmentIdentityConstraint(string constraintName, string expectedCode)
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        var assignee = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.AddProjectMember(assignee.Id, ProjectRole.Contributor);
        fixture.CommandUnitOfWork.Outcome = new TaskCommandSaveOutcome(TaskCommandSaveResult.UniqueConflict, constraintName);

        var result = await fixture.Service.AddAssignmentAsync(
            fixture.AddTask("constraint classification").Id,
            new AddTaskAssignmentRequest(assignee.Id, TaskAssignmentRole.Assignee, 1));

        Assert.False(result.IsSuccess);
        Assert.StartsWith(expectedCode + "|", result.Error);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task TaskCannotDependOnItself()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var task = fixture.AddTask("Blocking");

        var result = await fixture.Service.AddDependencyAsync(task.Id, new AddTaskDependencyRequest(task.Id, TaskDependencyType.FinishToStart, task.VersionNo));

        Assert.StartsWith("TASK_DEPENDENCY_SELF|", result.Error);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task DuplicateDependencyIsRejected()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var predecessor = fixture.AddTask("Concept");
        var successor = fixture.AddTask("Final");
        fixture.Dependencies.Add(new TaskDependency
        {
            ProjectId = fixture.Project.Id,
            PredecessorTaskItemId = predecessor.Id,
            SuccessorTaskItemId = successor.Id
        });

        var result = await fixture.Service.AddDependencyAsync(successor.Id, new AddTaskDependencyRequest(predecessor.Id, TaskDependencyType.FinishToStart, successor.VersionNo));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task DependencyCycleIsRejectedWithoutMovingOrPersistingTasks()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var predecessor = fixture.AddTask("Predecessor");
        var successor = fixture.AddTask("Successor");
        fixture.Dependencies.Add(new TaskDependency
        {
            ProjectId = fixture.Project.Id,
            PredecessorTaskItemId = successor.Id,
            SuccessorTaskItemId = predecessor.Id,
            DependencyType = TaskDependencyType.FinishToStart
        });

        var result = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(
                predecessor.Id,
                TaskDependencyType.FinishToStart,
                successor.VersionNo));

        Assert.StartsWith("TASK_DEPENDENCY_CYCLE|", result.Error);
        Assert.Single(fixture.Dependencies);
        Assert.Equal(1, predecessor.VersionNo);
        Assert.Equal(1, successor.VersionNo);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task DependencyAtCanonicalLimitRejectsTheNextEdgeWithoutMutation()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var tasks = Enumerable.Range(0, 64)
            .Select(index => fixture.AddTask($"Task {index:D2}"))
            .ToArray();
        var predecessor = tasks[0];
        var successor = tasks[^1];

        for (var predecessorIndex = 0;
             predecessorIndex < tasks.Length && fixture.Dependencies.Count < 2_000;
             predecessorIndex++)
        {
            for (var successorIndex = predecessorIndex + 1;
                 successorIndex < tasks.Length && fixture.Dependencies.Count < 2_000;
                 successorIndex++)
            {
                if (predecessorIndex == 0 && successorIndex == tasks.Length - 1)
                {
                    continue;
                }

                fixture.Dependencies.Add(new TaskDependency
                {
                    ProjectId = fixture.Project.Id,
                    PredecessorTaskItemId = tasks[predecessorIndex].Id,
                    SuccessorTaskItemId = tasks[successorIndex].Id,
                    DependencyType = TaskDependencyType.FinishToStart
                });
            }
        }

        Assert.Equal(2_000, fixture.Dependencies.Count);
        var projectVersion = fixture.Project.VersionNo;
        var result = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(
                predecessor.Id,
                TaskDependencyType.FinishToStart,
                successor.VersionNo));

        Assert.StartsWith("TASK_DEPENDENCY_LIMIT_EXCEEDED|", result.Error);
        Assert.Equal(2_000, fixture.Dependencies.Count);
        Assert.Equal(1, successor.VersionNo);
        Assert.Equal(projectVersion, fixture.Project.VersionNo);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.Contains(
            fixture.Audit.Entries,
            entry => entry.Action == "TaskDependencyMutationRejected" &&
                Equals(entry.Metadata?["reasonCode"], "TASK_DEPENDENCY_LIMIT_EXCEEDED"));
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task UnknownAndDeletedDependencyNeighborsShareTheSafeNotFoundOutcome()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var successor = fixture.AddTask("Successor");
        var deleted = fixture.AddTask("Deleted predecessor");
        deleted.MarkDeleted(fixture.Clock.UtcNow, manager.Id, "test");
        var legacyKind = fixture.AddTask("Legacy Milestone-kind predecessor");
        legacyKind.Kind = WorkItemKind.Milestone;
        var legacySuccessor = fixture.AddTask("Legacy Milestone-kind successor");
        legacySuccessor.Kind = WorkItemKind.Milestone;

        var unknown = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(
                Guid.NewGuid(),
                TaskDependencyType.FinishToStart,
                successor.VersionNo));
        var deletedResult = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(
                deleted.Id,
                TaskDependencyType.FinishToStart,
                successor.VersionNo));
        var legacyKindResult = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(
                legacyKind.Id,
                TaskDependencyType.FinishToStart,
                successor.VersionNo));
        var legacySuccessorResult = await fixture.Service.AddDependencyAsync(
            legacySuccessor.Id,
            new AddTaskDependencyRequest(
                successor.Id,
                TaskDependencyType.FinishToStart,
                legacySuccessor.VersionNo));

        Assert.StartsWith("TASK_DEPENDENCY_NOT_FOUND|", unknown.Error);
        Assert.StartsWith("TASK_DEPENDENCY_NOT_FOUND|", deletedResult.Error);
        Assert.StartsWith("TASK_DEPENDENCY_NOT_FOUND|", legacyKindResult.Error);
        Assert.StartsWith("TASK_DEPENDENCY_NOT_FOUND|", legacySuccessorResult.Error);
        Assert.Equal(unknown.Error, deletedResult.Error);
        Assert.Equal(unknown.Error, legacyKindResult.Error);
        Assert.Equal(unknown.Error, legacySuccessorResult.Error);
        Assert.Empty(fixture.Dependencies);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task ProjectRevisionConcurrencyIsMappedWithoutThrowing()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.CommandUnitOfWork.Outcome =
            new TaskCommandSaveOutcome(TaskCommandSaveResult.ConcurrencyConflict);

        var result = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest("Concurrent rename", null, null, null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("PROJECT_CONFLICT", result.ErrorDetail?.Code);
        Assert.Equal(1, fixture.CommandUnitOfWork.ClearCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task CanonicalFinishToStartDependencyAdvancesOnlySuccessorVersionWithoutMovingDates()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var predecessor = fixture.AddTask("Predecessor");
        predecessor.PlannedStartDate = new DateOnly(2026, 8, 1);
        predecessor.PlannedEndDate = new DateOnly(2026, 8, 10);
        var successor = fixture.AddTask("Successor");
        successor.PlannedStartDate = new DateOnly(2026, 8, 5);
        successor.PlannedEndDate = new DateOnly(2026, 8, 8);
        var predecessorDates = (predecessor.PlannedStartDate, predecessor.PlannedEndDate);
        var successorDates = (successor.PlannedStartDate, successor.PlannedEndDate);

        var result = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(predecessor.Id, TaskDependencyType.FinishToStart, successor.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Dependencies);
        Assert.Equal(1, predecessor.VersionNo);
        Assert.Equal(2, successor.VersionNo);
        Assert.Equal(predecessorDates, (predecessor.PlannedStartDate, predecessor.PlannedEndDate));
        Assert.Equal(successorDates, (successor.PlannedStartDate, successor.PlannedEndDate));
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "TaskDependencyAdded");
        Assert.Equal(successor.VersionNo, result.Value!.Version);
        Assert.Contains(result.Value.Warnings, warning => warning.Code == "DEPENDENCY_VIOLATION" && !warning.Blocking);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task DependencyAuthoringRedactsCrossProjectNeighborAndRejectsLegacyType()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var successor = fixture.AddTask("Successor");
        var otherProject = new Project
        {
            WorkspaceId = fixture.Workspace.Id,
            Name = "Other",
            Slug = "other",
            OwnerUserId = manager.Id,
            CreatedByUserId = manager.Id,
            Status = ProjectStatus.Active
        };
        fixture.Projects.ProjectItems[otherProject.Id] = otherProject;
        var hiddenNeighbor = new TaskItem
        {
            ProjectId = otherProject.Id,
            WorkspaceId = fixture.Workspace.Id,
            Title = "Hidden",
            CreatedByUserId = manager.Id,
            VersionNo = 1
        };
        fixture.Projects.Tasks[hiddenNeighbor.Id] = hiddenNeighbor;

        var crossProject = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(hiddenNeighbor.Id, TaskDependencyType.FinishToStart, successor.VersionNo));
        var legacy = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(Guid.NewGuid(), TaskDependencyType.StartToStart, successor.VersionNo));

        Assert.StartsWith("TASK_DEPENDENCY_NOT_FOUND|", crossProject.Error);
        Assert.DoesNotContain("Other", crossProject.Error, StringComparison.Ordinal);
        Assert.StartsWith("TASK_DEPENDENCY_TYPE_DEFERRED|", legacy.Error);
        Assert.Empty(fixture.Dependencies);
        var rejections = fixture.Audit.Entries
            .Where(entry => entry.Action == "TaskDependencyMutationRejected")
            .ToArray();
        Assert.Equal(2, rejections.Length);
        Assert.Contains(
            rejections,
            entry => Equals(entry.Metadata!["reasonCode"], "TASK_DEPENDENCY_NOT_FOUND"));
        Assert.Contains(
            rejections,
            entry => Equals(entry.Metadata!["reasonCode"], "TASK_DEPENDENCY_TYPE_DEFERRED"));
        Assert.All(rejections, entry =>
        {
            Assert.Null(entry.EntityId);
            Assert.DoesNotContain(entry.Metadata!.Keys, key =>
                key.Contains("predecessor", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("neighbor", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("title", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task DependencyDeleteRequiresSuccessorVersionAndKeepsLegacyRowsReadOnly()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var predecessor = fixture.AddTask("Predecessor");
        var successor = fixture.AddTask("Successor");
        var legacy = new TaskDependency
        {
            ProjectId = fixture.Project.Id,
            PredecessorTaskItemId = predecessor.Id,
            SuccessorTaskItemId = successor.Id,
            DependencyType = TaskDependencyType.StartToStart
        };
        fixture.Dependencies.Add(legacy);

        var stale = await fixture.Service.DeleteDependencyAsync(successor.Id, legacy.Id, 99);
        var readOnly = await fixture.Service.DeleteDependencyAsync(successor.Id, legacy.Id, successor.VersionNo);

        Assert.StartsWith("TASK_STALE_VERSION|", stale.Error);
        Assert.StartsWith("TASK_DEPENDENCY_LEGACY_READ_ONLY|", readOnly.Error);
        Assert.Contains(legacy, fixture.Dependencies);
        Assert.Equal(1, successor.VersionNo);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task WorkspaceReadOnlyManagerCannotManageDependencies()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser(workspaceRole: WorkspaceRole.ReadOnly);
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var predecessor = fixture.AddTask("Predecessor");
        var successor = fixture.AddTask("Successor");

        var result = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(predecessor.Id, TaskDependencyType.FinishToStart, successor.VersionNo));

        Assert.StartsWith("TASK_DEPENDENCY_FORBIDDEN|", result.Error);
        Assert.Empty(fixture.Dependencies);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task DependencyAuthoringRejectsCombinedTaskAndMilestoneOverflow()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var predecessor = fixture.AddTask("Predecessor");
        var successor = fixture.AddTask("Successor");
        for (var index = 2; index < 500; index++)
            fixture.AddTask($"Task {index}");
        fixture.AddMilestone("Milestone", 1);

        var result = await fixture.Service.AddDependencyAsync(
            successor.Id,
            new AddTaskDependencyRequest(
                predecessor.Id,
                TaskDependencyType.FinishToStart,
                successor.VersionNo));

        Assert.StartsWith("GANTT_ITEM_LIMIT_EXCEEDED|", result.Error);
        Assert.Empty(fixture.Dependencies);
        Assert.Equal(1, successor.VersionNo);
    }

    [Fact]
    public async Task CommentTargetAuthorizationWorks()
    {
        var fixture = ProjectFixture.Create();
        var outsider = fixture.AddUser(addWorkspaceMember: false);
        fixture.Current.UserIdValue = outsider.Id;

        var result = await fixture.Service.AddCommentAsync(new CreateCommentRequest(CommentTargetType.Project, fixture.Project.Id, "Looks good"));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityOrdinaryTaskCommentNeverProducesTaskNotification()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Contributor);
        var task = fixture.AddTask("Compatibility comment");

        var result = await fixture.Service.AddCommentAsync(
            new CreateCommentRequest(CommentTargetType.TaskItem, task.Id, "Ordinary comment"));

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Projects.Comments);
        Assert.Empty(fixture.TaskNotifications.Requests);
        Assert.Equal(0, fixture.Invalidations.TaskAssignmentChangedCount);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public void CompatibilityProjectServiceUsesCentralTaskProducerAndNoLegacyNotificationDependency()
    {
        var constructor = Assert.Single(typeof(ProjectService).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(INotificationService));
        Assert.Contains(
            parameters,
            parameter => parameter.ParameterType == typeof(ITaskNotificationProducer));
    }

    [Fact]
    public async Task SoftDeletedProjectIsHiddenFromNormalList()
    {
        var fixture = ProjectFixture.Create();
        var member = fixture.AddUser();
        fixture.Current.UserIdValue = member.Id;
        fixture.AddProjectMember(member.Id, ProjectRole.Viewer);
        fixture.Project.MarkDeleted(fixture.Clock.UtcNow, member.Id, "test");

        var result = await fixture.Service.ListAsync(new ProjectListQuery());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
    }

    [Fact]
    public async Task ArchivedProjectIsVisibleOnlyWithArchiveFilter()
    {
        var fixture = ProjectFixture.Create();
        var member = fixture.AddUser();
        fixture.Current.UserIdValue = member.Id;
        fixture.AddProjectMember(member.Id, ProjectRole.Viewer);
        fixture.Project.Status = ProjectStatus.Archived;

        var normal = await fixture.Service.ListAsync(new ProjectListQuery());
        var archived = await fixture.Service.ListAsync(new ProjectListQuery(Archived: true));

        Assert.True(normal.IsSuccess);
        Assert.Empty(normal.Value!.Items);
        Assert.True(archived.IsSuccess);
        Assert.Single(archived.Value!.Items);
    }

    [Fact]
    public async Task ProjectListSupportsPagingAndSearch()
    {
        var fixture = ProjectFixture.Create();
        var member = fixture.AddUser();
        fixture.Current.UserIdValue = member.Id;
        fixture.AddProjectMember(member.Id, ProjectRole.Viewer);
        var secondProject = new Project
        {
            WorkspaceId = fixture.Workspace.Id,
            OwnerUserId = member.Id,
            CreatedByUserId = member.Id,
            Name = "Marketing Launch",
            Slug = "marketing-launch",
            Description = "Campaign timeline",
            Status = ProjectStatus.Active
        };
        fixture.Projects.ProjectItems[secondProject.Id] = secondProject;
        fixture.Projects.Members.Add(new ProjectMember { ProjectId = secondProject.Id, UserId = member.Id, User = member, Role = ProjectRole.Viewer, JoinedAt = fixture.Clock.UtcNow });

        var result = await fixture.Service.ListAsync(new ProjectListQuery(Search: "marketing", Page: 1, PageSize: 1));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Page);
        Assert.Equal(1, result.Value.PageSize);
        Assert.Equal(1, result.Value.TotalCount);
        Assert.Equal(secondProject.Id, Assert.Single(result.Value.Items).Id);
    }

    [Fact]
    public async Task ProjectListFiltersByWorkspaceAndProjectsServerActivationCapability()
    {
        var fixture = ProjectFixture.Create();
        var member = fixture.AddUser();
        fixture.Current.UserIdValue = member.Id;
        fixture.AddProjectMember(member.Id, ProjectRole.Owner);
        fixture.Project.Status = ProjectStatus.Planning;
        fixture.Project.Visibility = ProjectVisibility.MembersOnly;
        fixture.Project.ActivationState = ProjectActivationState.NeverActivated;
        fixture.Project.ActivatedAtUtc = null;
        fixture.Project.ActivationVersion = null;
        fixture.Project.VersionNo = 1;

        var otherWorkspace = new Workspace
        {
            Name = "Other Workspace",
            Slug = "other-workspace",
            CreatedByUserId = member.Id,
            Status = WorkspaceStatus.Active
        };
        fixture.Workspaces.Items[otherWorkspace.Id] = otherWorkspace;
        fixture.Workspaces.Members.Add(new WorkspaceMember
        {
            WorkspaceId = otherWorkspace.Id,
            UserId = member.Id,
            Role = WorkspaceRole.Member,
            Status = MembershipStatus.Active,
            JoinedAt = fixture.Clock.UtcNow
        });
        var otherProject = new Project
        {
            WorkspaceId = otherWorkspace.Id,
            OwnerUserId = member.Id,
            CreatedByUserId = member.Id,
            Name = "Other Project",
            Slug = "other-project",
            Status = ProjectStatus.Planning
        };
        fixture.Projects.ProjectItems[otherProject.Id] = otherProject;
        fixture.Projects.Members.Add(new ProjectMember
        {
            ProjectId = otherProject.Id,
            UserId = member.Id,
            User = member,
            Role = ProjectRole.Owner,
            JoinedAt = fixture.Clock.UtcNow
        });

        var result = await fixture.Service.ListAsync(new ProjectListQuery(WorkspaceId: fixture.Workspace.Id));

        Assert.True(result.IsSuccess, result.Error);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(fixture.Project.Id, item.Id);
        Assert.True(item.UiPermissions.CanActivate);
        Assert.DoesNotContain(result.Value.Items, project => project.Id == otherProject.Id);
        Assert.Equal(1, fixture.Projects.ListActivatableProjectIdsCallCount);

        var workspaceMembership = Assert.Single(fixture.Workspaces.Members, workspaceMember =>
            workspaceMember.WorkspaceId == fixture.Workspace.Id &&
            workspaceMember.UserId == member.Id);
        workspaceMembership.Status = MembershipStatus.Suspended;
        var afterRevocation = await fixture.Service.ListAsync(new ProjectListQuery(WorkspaceId: fixture.Workspace.Id));
        Assert.False(Assert.Single(afterRevocation.Value!.Items).UiPermissions.CanActivate);
        Assert.Equal(2, fixture.Projects.ListActivatableProjectIdsCallCount);

        workspaceMembership.Status = MembershipStatus.Active;
        fixture.Workspace.Status = WorkspaceStatus.Archived;
        var inactiveWorkspace = await fixture.Service.ListAsync(new ProjectListQuery(WorkspaceId: fixture.Workspace.Id));
        Assert.False(Assert.Single(inactiveWorkspace.Value!.Items).UiPermissions.CanActivate);
        Assert.Equal(3, fixture.Projects.ListActivatableProjectIdsCallCount);
    }

    [Fact]
    public async Task ProjectListBatchesActivationEligibilityOnceForTheBoundedPage()
    {
        var fixture = ProjectFixture.Create();
        var member = fixture.AddUser();
        fixture.Current.UserIdValue = member.Id;
        fixture.AddProjectMember(member.Id, ProjectRole.Owner);
        fixture.Project.Status = ProjectStatus.Planning;
        fixture.Project.Visibility = ProjectVisibility.MembersOnly;
        fixture.Project.ActivationState = ProjectActivationState.NeverActivated;
        fixture.Project.VersionNo = 1;

        var secondProject = new Project
        {
            WorkspaceId = fixture.Workspace.Id,
            OwnerUserId = member.Id,
            CreatedByUserId = member.Id,
            Name = "Second canonical draft",
            Slug = "second-canonical-draft",
            Status = ProjectStatus.Planning,
            Visibility = ProjectVisibility.MembersOnly,
            ActivationState = ProjectActivationState.NeverActivated,
            VersionNo = 1
        };
        fixture.Projects.ProjectItems[secondProject.Id] = secondProject;
        fixture.Projects.Members.Add(new ProjectMember
        {
            ProjectId = secondProject.Id,
            UserId = member.Id,
            User = member,
            Role = ProjectRole.Owner,
            JoinedAt = fixture.Clock.UtcNow
        });

        var result = await fixture.Service.ListAsync(new ProjectListQuery(WorkspaceId: fixture.Workspace.Id));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, result.Value!.Items.Count);
        Assert.All(result.Value.Items, project => Assert.True(project.UiPermissions.CanActivate));
        Assert.Equal(1, fixture.Projects.ListActivatableProjectIdsCallCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR04")]
    public async Task ProjectListSerializesTaskPermissionQueries()
    {
        var fixture = ProjectFixture.Create();
        var member = fixture.AddUser();
        fixture.Current.UserIdValue = member.Id;
        fixture.AddProjectMember(member.Id, ProjectRole.Viewer);
        fixture.EnsureProjectOperational();
        var secondProject = new Project
        {
            WorkspaceId = fixture.Workspace.Id,
            OwnerUserId = member.Id,
            CreatedByUserId = member.Id,
            Name = "Second project",
            Slug = "second-project",
            Status = ProjectStatus.Active,
            ActivationState = ProjectActivationState.Activated,
            ActivatedAtUtc = fixture.Clock.UtcNow,
            ActivationVersion = 1
        };
        fixture.Projects.ProjectItems[secondProject.Id] = secondProject;
        fixture.Projects.Members.Add(new ProjectMember
        {
            ProjectId = secondProject.Id,
            UserId = member.Id,
            User = member,
            Role = ProjectRole.Viewer,
            JoinedAt = fixture.Clock.UtcNow
        });
        fixture.Projects.BlockMemberLookups = true;

        var resultTask = fixture.Service.ListAsync(new ProjectListQuery());
        await fixture.Projects.FirstMemberLookupEntered.WaitAsync(TimeSpan.FromSeconds(1));
        fixture.Projects.ReleaseMemberLookups();
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.TotalCount);
        Assert.Equal(1, fixture.Projects.MaxConcurrentMemberLookups);
    }


    [Fact]
    public async Task DeprecatedBodyScopedCreateFailsClosedWithoutMutation()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser(workspaceRole: WorkspaceRole.Admin);
        fixture.Current.UserIdValue = manager.Id;

        var result = await fixture.Service.CreateAsync(new CreateProjectRequest(fixture.Workspace.Id, Guid.Empty, "New Project", null, null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("DependencyUnavailable", result.ErrorDetail?.Code);
        Assert.Single(fixture.Projects.ProjectItems);
        Assert.Empty(fixture.Projects.Members);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task LegacyGroupManagerCannotBypassCanonicalCreateDependencies()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        var group = fixture.AddGroup(manager.Id, GroupRole.Admin);
        fixture.Current.UserIdValue = manager.Id;
        var start = new DateOnly(2026, 6, 1);
        var expectedEnd = new DateOnly(2026, 7, 1);

        var result = await fixture.Service.CreateAsync(new CreateProjectRequest(fixture.Workspace.Id, group.Id, "New Project", "Scoped", start, expectedEnd));

        Assert.False(result.IsSuccess);
        Assert.Equal("DependencyUnavailable", result.ErrorDetail?.Code);
        Assert.Single(fixture.Projects.ProjectItems);
        Assert.Empty(fixture.Projects.Members);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Theory]
    [InlineData(ProjectStatus.Planning, ProjectStatus.Review)]
    [InlineData(ProjectStatus.Active, ProjectStatus.Planning)]
    [InlineData(ProjectStatus.Completed, ProjectStatus.Review)]
    [InlineData(ProjectStatus.Suspended, ProjectStatus.Planning)]
    [Trait("Scope", "WPC01")]
    public async Task InvalidLifecycleTransitionReturnsTypedConflictWithoutSideEffects(
        ProjectStatus previousStatus,
        ProjectStatus nextStatus)
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = previousStatus;
        fixture.Project.Description = "Original description";
        fixture.Project.StartDate = new DateOnly(2026, 1, 1);
        fixture.Project.DueDate = new DateOnly(2026, 12, 31);

        var result = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(
                "Must not persist",
                "Must not persist",
                nextStatus,
                new DateOnly(2026, 2, 1),
                new DateOnly(2026, 11, 30)));

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidStateTransition", result.ErrorDetail?.Code);
        Assert.Equal(
            "The requested Project lifecycle transition is not available.",
            result.ErrorDetail?.Message);
        Assert.Equal("body.status", result.ErrorDetail?.Target);
        Assert.Equal(previousStatus, fixture.Project.Status);
        Assert.Equal("Launch", fixture.Project.Name);
        Assert.Equal("Original description", fixture.Project.Description);
        Assert.Equal(new DateOnly(2026, 1, 1), fixture.Project.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), fixture.Project.DueDate);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.Invalidations.ProjectChangedCount);
        Assert.Empty(fixture.AuthorizationChanges.Items);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task InvalidLifecycleTransitionPrecedesUnrelatedMetadataValidation()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Completed;

        var result = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(
                " ",
                "Must not persist",
                ProjectStatus.Review,
                new DateOnly(2026, 12, 31),
                new DateOnly(2026, 1, 1)));

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidStateTransition", result.ErrorDetail?.Code);
        Assert.Equal("body.status", result.ErrorDetail?.Target);
        Assert.Equal(ProjectStatus.Completed, fixture.Project.Status);
        Assert.Equal("Launch", fixture.Project.Name);
        Assert.Null(fixture.Project.Description);
        Assert.Null(fixture.Project.StartDate);
        Assert.Null(fixture.Project.DueDate);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.Invalidations.ProjectChangedCount);
        Assert.Empty(fixture.AuthorizationChanges.Items);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task GenericUpdateCannotActivatePlanningProject()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Planning;

        var originalName = fixture.Project.Name;
        var result = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest("Must not persist", null, ProjectStatus.Active, null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidStateTransition", result.ErrorDetail?.Code);
        Assert.Equal(ProjectStatus.Planning, fixture.Project.Status);
        Assert.Equal(originalName, fixture.Project.Name);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.Invalidations.ProjectChangedCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task PlanningProjectArchiveThenRestoreFailsWithoutGuessingPriorState()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Planning;

        var archived = await fixture.Service.ArchiveAsync(fixture.Project.Id);
        var restored = await fixture.Service.RestoreAsync(fixture.Project.Id);
        var activated = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Active, null, null));

        Assert.True(archived.IsSuccess);
        Assert.False(restored.IsSuccess);
        Assert.Equal("InvalidStateTransition", restored.ErrorDetail?.Code);
        Assert.False(activated.IsSuccess);
        Assert.Equal("InvalidStateTransition", activated.ErrorDetail?.Code);
        Assert.Equal(ProjectStatus.Archived, fixture.Project.Status);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "ProjectArchived");
        Assert.DoesNotContain(fixture.Audit.Entries, entry => entry.Action == "ProjectRestored");
        Assert.DoesNotContain(fixture.Audit.Entries, entry => entry.Action == "ProjectActivated");
        Assert.Equal(1, fixture.Invalidations.ProjectChangedCount);
        Assert.Contains(fixture.AuthorizationChanges.Items, item =>
            item.UserId == manager.Id && item.ScopeId == fixture.Project.Id && item.Change == "archived");
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task GenericUpdateCannotActivateSuspendedPlanningProject()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Planning;

        var suspended = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Suspended, null, null));
        var activated = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Active, null, null));

        Assert.True(suspended.IsSuccess);
        Assert.False(activated.IsSuccess);
        Assert.Equal("InvalidStateTransition", activated.ErrorDetail?.Code);
        Assert.Equal(ProjectStatus.Suspended, fixture.Project.Status);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        Assert.Single(fixture.Audit.Entries, entry => entry.Action == "ProjectStatusChanged");
        Assert.Equal(1, fixture.Invalidations.ProjectChangedCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task SuspendedPlanningProjectCannotRecoverOrActivateThroughGenericUpdates()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Planning;

        var suspended = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Suspended, null, null));
        var resumed = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(
                "Must not persist",
                "Must not persist",
                ProjectStatus.Planning,
                new DateOnly(2026, 2, 1),
                new DateOnly(2026, 11, 30)));
        var activated = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Active, null, null));

        Assert.True(suspended.IsSuccess);
        Assert.False(resumed.IsSuccess);
        Assert.Equal("InvalidStateTransition", resumed.ErrorDetail?.Code);
        Assert.Equal("body.status", resumed.ErrorDetail?.Target);
        Assert.False(activated.IsSuccess);
        Assert.Equal("InvalidStateTransition", activated.ErrorDetail?.Code);
        Assert.Equal("body.status", activated.ErrorDetail?.Target);
        Assert.Equal(ProjectStatus.Suspended, fixture.Project.Status);
        Assert.Equal("Launch", fixture.Project.Name);
        Assert.Null(fixture.Project.Description);
        Assert.Null(fixture.Project.StartDate);
        Assert.Null(fixture.Project.DueDate);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        Assert.Single(fixture.Audit.Entries, entry => entry.Action == "ProjectStatusChanged");
        Assert.Equal(1, fixture.Invalidations.ProjectChangedCount);
        Assert.Empty(fixture.AuthorizationChanges.Items);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task SuspendedOperationalProjectCannotRecoverToPlanningOrActiveThroughGenericUpdates()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Active;
        fixture.Projects.CurrentReaderUserIds[fixture.Project.Id] = [manager.Id];

        var suspended = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Suspended, null, null));
        Assert.True(suspended.IsSuccess);

        var saveCount = fixture.CommandUnitOfWork.SaveCount;
        var auditCount = fixture.Audit.Entries.Count;
        var projectChangedCount = fixture.Invalidations.ProjectChangedCount;
        var authorizationChangeCount = fixture.AuthorizationChanges.Items.Count;

        var planning = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(
                "Must not persist",
                "Must not persist",
                ProjectStatus.Planning,
                new DateOnly(2026, 2, 1),
                new DateOnly(2026, 11, 30)));
        var active = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Active, null, null));

        Assert.False(planning.IsSuccess);
        Assert.Equal("InvalidStateTransition", planning.ErrorDetail?.Code);
        Assert.Equal("body.status", planning.ErrorDetail?.Target);
        Assert.False(active.IsSuccess);
        Assert.Equal("InvalidStateTransition", active.ErrorDetail?.Code);
        Assert.Equal("body.status", active.ErrorDetail?.Target);
        Assert.Equal(ProjectStatus.Suspended, fixture.Project.Status);
        Assert.Equal("Launch", fixture.Project.Name);
        Assert.Null(fixture.Project.Description);
        Assert.Null(fixture.Project.StartDate);
        Assert.Null(fixture.Project.DueDate);
        Assert.Equal(saveCount, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(auditCount, fixture.Audit.Entries.Count);
        Assert.Equal(projectChangedCount, fixture.Invalidations.ProjectChangedCount);
        Assert.Equal(authorizationChangeCount, fixture.AuthorizationChanges.Items.Count);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task SuspendedProjectMetadataUpdateMayRetainSuspendedStatus()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Suspended;

        var result = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(
                "Renamed suspended project",
                "Updated while suspended",
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal(ProjectStatus.Suspended, fixture.Project.Status);
        Assert.Equal("Renamed suspended project", fixture.Project.Name);
        Assert.Equal("Updated while suspended", fixture.Project.Description);
        Assert.Single(fixture.Audit.Entries, entry => entry.Action == "ProjectUpdated");
        Assert.Equal(1, fixture.Invalidations.ProjectChangedCount);
        Assert.Empty(fixture.AuthorizationChanges.Items);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task NeverActivatedSuspendedProjectRemainsLimitedToExplicitMembers()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        var workspaceAdmin = fixture.AddUser(workspaceRole: WorkspaceRole.Admin);
        var group = fixture.AddGroup(workspaceAdmin.Id, GroupRole.Admin);
        fixture.Project.GroupId = group.Id;
        fixture.Project.Status = ProjectStatus.Planning;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);

        fixture.Current.UserIdValue = manager.Id;
        var suspended = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Suspended, null, null));
        Assert.True(suspended.IsSuccess);

        fixture.Current.UserIdValue = workspaceAdmin.Id;
        Assert.False(await fixture.ProjectAuthorization.CanViewProject(workspaceAdmin.Id, fixture.Project.Id));
        Assert.False(await fixture.ProjectAuthorization.CanManageProject(workspaceAdmin.Id, fixture.Project.Id));
        var hidden = await fixture.Service.GetAsync(fixture.Project.Id);
        Assert.False(hidden.IsSuccess);
        Assert.Equal("NotFound", hidden.ErrorDetail?.Code);

        Assert.True(await fixture.ProjectAuthorization.CanViewProject(manager.Id, fixture.Project.Id));
        Assert.True(await fixture.ProjectAuthorization.CanManageProject(manager.Id, fixture.Project.Id));
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task NeverActivatedArchivedProjectCannotBeRestoredByNonmemberGovernanceActor()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        var workspaceAdmin = fixture.AddUser(workspaceRole: WorkspaceRole.Admin);
        var group = fixture.AddGroup(workspaceAdmin.Id, GroupRole.Admin);
        fixture.Project.GroupId = group.Id;
        fixture.Project.Status = ProjectStatus.Planning;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);

        fixture.Current.UserIdValue = manager.Id;
        Assert.True((await fixture.Service.ArchiveAsync(fixture.Project.Id)).IsSuccess);

        fixture.Current.UserIdValue = workspaceAdmin.Id;
        Assert.False(await fixture.ProjectAuthorization.CanManageProject(workspaceAdmin.Id, fixture.Project.Id));
        var denied = await fixture.Service.RestoreAsync(fixture.Project.Id);
        Assert.False(denied.IsSuccess);
        Assert.Equal(ProjectStatus.Archived, fixture.Project.Status);

        fixture.Current.UserIdValue = manager.Id;
        var ambiguous = await fixture.Service.RestoreAsync(fixture.Project.Id);
        Assert.False(ambiguous.IsSuccess);
        Assert.Equal("InvalidStateTransition", ambiguous.ErrorDetail?.Code);
        Assert.Equal(ProjectStatus.Archived, fixture.Project.Status);
        Assert.DoesNotContain(fixture.Audit.Entries, entry => entry.Action == "ProjectRestored");
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(1, fixture.Invalidations.ProjectChangedCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task ActiveProjectCanEnterReviewAndReturnToActive()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Active;

        var review = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Review, null, null));
        var active = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Active, null, null));

        Assert.True(review.IsSuccess);
        Assert.True(active.IsSuccess);
        Assert.Equal(ProjectStatus.Active, fixture.Project.Status);
        Assert.Equal(2, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(2, fixture.Audit.Entries.Count(entry => entry.Action == "ProjectStatusChanged"));
        Assert.Equal(2, fixture.Invalidations.ProjectChangedCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task ActiveProjectCanCompleteAsAnOrdinaryPostOperationalTransition()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Active;

        var result = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, ProjectStatus.Completed, null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(ProjectStatus.Completed, fixture.Project.Status);
        Assert.Single(fixture.Audit.Entries, entry => entry.Action == "ProjectStatusChanged");
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(1, fixture.Invalidations.ProjectChangedCount);
    }

    [Theory]
    [InlineData(ProjectStatus.Suspended, "suspended")]
    [InlineData(ProjectStatus.Archived, "archived")]
    [Trait("Scope", "WPC01")]
    public async Task VisibilityReducingGenericTransitionInvalidatesEveryCurrentReader(
        ProjectStatus nextStatus,
        string change)
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        var ordinaryViewer = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Projects.CurrentReaderUserIds[fixture.Project.Id] = [manager.Id, ordinaryViewer.Id];

        var result = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest(null, null, nextStatus, null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(nextStatus, fixture.Project.Status);
        Assert.Equal(
            new[] { manager.Id, ordinaryViewer.Id }.Order().ToArray(),
            fixture.AuthorizationChanges.Items.Select(item => item.UserId).Order().ToArray());
        Assert.All(fixture.AuthorizationChanges.Items, item =>
        {
            Assert.Equal(fixture.Project.Id, item.ScopeId);
            Assert.Equal("project", item.ScopeType);
            Assert.Equal(change, item.Change);
        });
        Assert.Equal(1, fixture.Invalidations.ProjectChangedCount);
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task ActiveProjectMetadataUpdateRetainsActiveStatus()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Active;

        var result = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest("Renamed operational project", "Updated", null, null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(ProjectStatus.Active, fixture.Project.Status);
        Assert.Equal("Renamed operational project", fixture.Project.Name);
        Assert.Single(fixture.Audit.Entries, entry => entry.Action == "ProjectUpdated");
        Assert.Equal(1, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(1, fixture.Invalidations.ProjectChangedCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task RestoreOnActiveProjectFailsWithoutFalseSuccessSideEffects()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Active;

        var result = await fixture.Service.RestoreAsync(fixture.Project.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidStateTransition", result.ErrorDetail?.Code);
        Assert.Equal(ProjectStatus.Active, fixture.Project.Status);
        Assert.Null(fixture.Project.DeletedAt);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(0, fixture.Invalidations.ProjectChangedCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task DeletedProjectRestoreAndArchiveFailWithoutMutationOrSuccessSideEffects()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var deletedAt = fixture.Clock.UtcNow.AddMinutes(-5);
        fixture.Project.Status = ProjectStatus.Deleted;
        fixture.Project.MarkDeleted(deletedAt, manager.Id, "historical deletion");

        var restored = await fixture.Service.RestoreAsync(fixture.Project.Id);
        var archived = await fixture.Service.ArchiveAsync(fixture.Project.Id);

        Assert.False(restored.IsSuccess);
        Assert.Equal("InvalidStateTransition", restored.ErrorDetail?.Code);
        Assert.False(archived.IsSuccess);
        Assert.Equal("InvalidStateTransition", archived.ErrorDetail?.Code);
        Assert.Equal(ProjectStatus.Deleted, fixture.Project.Status);
        Assert.Equal(deletedAt, fixture.Project.DeletedAt);
        Assert.Equal(manager.Id, fixture.Project.DeletedByUserId);
        Assert.Equal("historical deletion", fixture.Project.DeleteReason);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(0, fixture.Invalidations.ProjectChangedCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task ArchivedProjectIsReadOnlyAcrossUpdateAndRepeatedArchive()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        fixture.Project.Status = ProjectStatus.Archived;
        var originalName = fixture.Project.Name;

        var updated = await fixture.Service.UpdateAsync(
            fixture.Project.Id,
            new UpdateProjectRequest("Must not persist", "Must not persist", null, null, null));
        var archivedAgain = await fixture.Service.ArchiveAsync(fixture.Project.Id);

        Assert.False(updated.IsSuccess);
        Assert.Equal("InvalidStateTransition", updated.ErrorDetail?.Code);
        Assert.False(archivedAgain.IsSuccess);
        Assert.Equal("InvalidStateTransition", archivedAgain.ErrorDetail?.Code);
        Assert.Equal(ProjectStatus.Archived, fixture.Project.Status);
        Assert.Equal(originalName, fixture.Project.Name);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.Equal(0, fixture.Invalidations.ProjectChangedCount);
    }

    [Fact]
    public async Task PlanningProjectIsHiddenFromWorkspaceAndGroupAuthoritiesWithoutProjectMembership()
    {
        var fixture = ProjectFixture.Create();
        fixture.Project.Status = ProjectStatus.Planning;
        var workspaceOwner = fixture.AddUser(workspaceRole: WorkspaceRole.Owner);
        var groupManager = fixture.AddUser();
        var group = fixture.AddGroup(groupManager.Id, GroupRole.Admin);
        fixture.Project.GroupId = group.Id;

        Assert.False(await fixture.ProjectAuthorization.CanViewProject(workspaceOwner.Id, fixture.Project.Id));
        Assert.False(await fixture.ProjectAuthorization.CanManageProject(workspaceOwner.Id, fixture.Project.Id));
        Assert.False(await fixture.ProjectAuthorization.CanViewProject(groupManager.Id, fixture.Project.Id));
        Assert.False(await fixture.ProjectAuthorization.CanManageProject(groupManager.Id, fixture.Project.Id));

        fixture.AddProjectMember(workspaceOwner.Id, ProjectRole.Owner);

        Assert.True(await fixture.ProjectAuthorization.CanViewProject(workspaceOwner.Id, fixture.Project.Id));
        Assert.True(await fixture.ProjectAuthorization.CanManageProject(workspaceOwner.Id, fixture.Project.Id));
    }

    [Fact]
    public async Task ProjectResponsePreservesNullGroupAndExposesVersion()
    {
        var fixture = ProjectFixture.Create();
        var member = fixture.AddUser();
        fixture.Current.UserIdValue = member.Id;
        fixture.AddProjectMember(member.Id, ProjectRole.Viewer);
        fixture.Project.GroupId = null;
        fixture.Project.VersionNo = 7;

        var result = await fixture.Service.GetAsync(fixture.Project.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.GroupId);
        Assert.Equal(7, result.Value.VersionNo);
    }

    [Fact]
    public async Task ArchiveCreatesAuditLog()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);

        var result = await fixture.Service.ArchiveAsync(fixture.Project.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProjectStatus.Archived, fixture.Project.Status);
        Assert.False(fixture.Project.DeletedAt.HasValue);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "ProjectArchived" && entry.EntityId == fixture.Project.Id);
        Assert.Contains(fixture.AuthorizationChanges.Items, item =>
            item.UserId == manager.Id && item.ScopeId == fixture.Project.Id && item.Change == "archived");
    }

    [Fact]
    public async Task ProjectManagerCanCreateAndUpdateMilestoneFields()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var dueDate = new DateOnly(2026, 7, 15);

        var created = await fixture.Service.CreateMilestoneAsync(fixture.Project.Id, new CreateMilestoneRequest("Alpha", "Scope", dueDate, 20));

        Assert.True(created.IsSuccess);
        Assert.Equal(fixture.Project.Id, created.Value!.ProjectId);
        Assert.Equal(dueDate, created.Value.DueDate);
        Assert.Equal(MilestoneStatus.NotStarted, created.Value.Status);
        Assert.Equal(20, created.Value.DisplayOrder);

        var updated = await fixture.Service.UpdateMilestoneAsync(
            created.Value.Id,
            new UpdateMilestoneRequest(
                null,
                "Updated",
                new DateOnly(2026, 8, 1),
                MilestoneStatus.InProgress,
                10,
                created.Value.Version));

        Assert.True(updated.IsSuccess);
        Assert.Equal("Updated", updated.Value!.Description);
        Assert.Equal(new DateOnly(2026, 8, 1), updated.Value.DueDate);
        Assert.Equal(MilestoneStatus.InProgress, updated.Value.Status);
        Assert.Equal(10, updated.Value.DisplayOrder);
    }

    [Fact]
    public async Task MilestoneListPreservesDisplayOrder()
    {
        var fixture = ProjectFixture.Create();
        var viewer = fixture.AddUser();
        fixture.Current.UserIdValue = viewer.Id;
        fixture.AddProjectMember(viewer.Id, ProjectRole.Viewer);
        fixture.AddMilestone("Second", 20);
        fixture.AddMilestone("First", 10);

        var result = await fixture.Service.ListMilestonesAsync(fixture.Project.Id, new ProjectChildListQuery());

        Assert.True(result.IsSuccess);
        Assert.Collection(result.Value!.Items,
            milestone => Assert.Equal("First", milestone.Title),
            milestone => Assert.Equal("Second", milestone.Title));
    }

    [Fact]
    public async Task UnauthorizedUserCannotCreateOrViewMilestones()
    {
        var fixture = ProjectFixture.Create();
        var outsider = fixture.AddUser(addWorkspaceMember: false);
        fixture.Current.UserIdValue = outsider.Id;
        var milestone = fixture.AddMilestone("Restricted", 1);

        var list = await fixture.Service.ListMilestonesAsync(fixture.Project.Id, new ProjectChildListQuery());
        var get = await fixture.Service.GetMilestoneAsync(milestone.Id);
        var create = await fixture.Service.CreateMilestoneAsync(fixture.Project.Id, new CreateMilestoneRequest("Blocked", null, null, 2));

        Assert.False(list.IsSuccess);
        Assert.False(get.IsSuccess);
        Assert.False(create.IsSuccess);
    }

    [Fact]
    public async Task UnsupportedMilestoneStatusIsRejected()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var milestone = fixture.AddMilestone("Alpha", 1);

        var result = await fixture.Service.UpdateMilestoneAsync(
            milestone.Id,
            new UpdateMilestoneRequest(
                null,
                null,
                null,
                MilestoneStatus.Cancelled,
                null,
                milestone.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal(MilestoneStatus.NotStarted, milestone.Status);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task MilestoneCompatibilityUpdateRequiresCurrentVersion()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var milestone = fixture.AddMilestone("Alpha", 1);
        milestone.DueDate = new DateOnly(2026, 8, 1);
        milestone.VersionNo = 4;

        var result = await fixture.Service.UpdateMilestoneAsync(
            milestone.Id,
            new UpdateMilestoneRequest(
                "Stale title",
                null,
                new DateOnly(2026, 8, 2),
                MilestoneStatus.Completed,
                null,
                3));

        Assert.False(result.IsSuccess);
        Assert.Equal("MILESTONE_STALE_VERSION", result.ErrorDetail?.Code);
        Assert.Equal("Alpha", milestone.Name);
        Assert.Equal(new DateOnly(2026, 8, 1), milestone.DueDate);
        Assert.Equal(MilestoneStatus.NotStarted, milestone.Status);
        Assert.Equal(4, milestone.VersionNo);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.DoesNotContain(fixture.Audit.Entries, entry => entry.EntityId == milestone.Id);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task MilestoneCompatibilityUpdateCannotActivateOrCompleteWithoutDate()
    {
        var fixture = ProjectFixture.Create();
        var manager = fixture.AddUser();
        fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager);
        var milestone = fixture.AddMilestone("Legacy undated", 1);

        var result = await fixture.Service.UpdateMilestoneAsync(
            milestone.Id,
            new UpdateMilestoneRequest(
                null,
                null,
                null,
                MilestoneStatus.Completed,
                null,
                milestone.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.Equal("MILESTONE_DATE_REQUIRED", result.ErrorDetail?.Code);
        Assert.Null(milestone.DueDate);
        Assert.Equal(MilestoneStatus.NotStarted, milestone.Status);
        Assert.Equal(1, milestone.VersionNo);
        Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
        Assert.DoesNotContain(fixture.Audit.Entries, entry => entry.EntityId == milestone.Id);
    }

    [Fact]
    public async Task ProjectListAndCanonicalTaskDetailAgreeForParentDerivedValues()
    {
        var fixture = ProjectFixture.Create();
        var actor = fixture.AddUser();
        fixture.Current.UserIdValue = actor.Id;
        fixture.AddProjectMember(actor.Id, ProjectRole.Manager);
        var parent = fixture.AddTask("parent");
        parent.WorkspaceId = fixture.Workspace.Id;
        parent.TenantId = Guid.NewGuid();
        parent.VersionNo = 7;
        var active = fixture.AddTask("active");
        active.ParentTaskItemId = parent.Id;
        active.WorkspaceId = parent.WorkspaceId;
        active.TenantId = parent.TenantId;
        active.PlannedStartDate = new DateOnly(2026, 7, 2);
        active.PlannedEndDate = new DateOnly(2026, 7, 4);
        active.ProgressPercent = 20;
        var cancelled = fixture.AddTask("cancelled");
        cancelled.ParentTaskItemId = parent.Id;
        cancelled.WorkspaceId = parent.WorkspaceId;
        cancelled.TenantId = parent.TenantId;
        cancelled.PlannedStartDate = new DateOnly(2026, 7, 1);
        cancelled.PlannedEndDate = new DateOnly(2026, 7, 8);
        cancelled.ProgressPercent = 100;
        cancelled.Status = TaskItemStatus.Cancelled;

        var listed = await fixture.Service.ListTasksAsync(fixture.Project.Id, new TaskListQuery());
        var detail = await fixture.Commands.GetAsync(parent.Id);

        Assert.True(listed.IsSuccess);
        Assert.True(detail.IsSuccess);
        var row = Assert.Single(listed.Value!.Items, item => item.Id == parent.Id);
        var canonical = detail.Value!;
        Assert.Equal(row.PlannedStartDate, canonical.PlannedStartDate);
        Assert.Equal(row.PlannedEndDate, canonical.PlannedEndDate);
        Assert.Equal(row.ProgressPercent, canonical.ProgressPercent);
        Assert.Equal(row.ProgressIsDerived, canonical.ProgressIsDerived);
        Assert.Equal(row.IsOverdue, canonical.IsOverdue);
        Assert.Equal(row.Version, canonical.Version);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityAssigneeAddRejectsRevokedWorkspaceMember()
    {
        var fixture = ProjectFixture.Create(); var (_, target, task) = PrepareCompatibilityTarget(fixture);
        RevokeWorkspaceMember(fixture, target.Id);
        var result = await fixture.Service.AddAssignmentAsync(task.Id, new AddTaskAssignmentRequest(target.Id, TaskAssignmentRole.Assignee, 1));
        AssertCompatibilityRejected(fixture, task, result); Assert.Empty(fixture.Projects.Assignments); Assert.Null(task.PrimaryAssigneeUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityReviewerAddRejectsRevokedWorkspaceMember()
    {
        var fixture = ProjectFixture.Create(); var (_, target, task) = PrepareCompatibilityTarget(fixture);
        RevokeWorkspaceMember(fixture, target.Id);
        var result = await fixture.Service.AddAssignmentAsync(task.Id, new AddTaskAssignmentRequest(target.Id, TaskAssignmentRole.Reviewer, 1));
        AssertCompatibilityRejected(fixture, task, result); Assert.Empty(fixture.Projects.Assignments); Assert.Null(task.ReviewerUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilitySupportAddRejectsRevokedWorkspaceMember()
    {
        var fixture = ProjectFixture.Create(); var (_, target, task) = PrepareCompatibilityTarget(fixture);
        RevokeWorkspaceMember(fixture, target.Id);
        var result = await fixture.Service.AddAssignmentAsync(task.Id, new AddTaskAssignmentRequest(target.Id, TaskAssignmentRole.Support, 1));
        AssertCompatibilityRejected(fixture, task, result); Assert.Empty(fixture.Projects.Assignments); Assert.Empty(fixture.Projects.Collaborators);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityAssigneeAddRejectsSuspendedUser()
    {
        var fixture = ProjectFixture.Create(); var (_, target, task) = PrepareCompatibilityTarget(fixture);
        target.Status = UserStatus.Suspended;
        var result = await fixture.Service.AddAssignmentAsync(task.Id, new AddTaskAssignmentRequest(target.Id, TaskAssignmentRole.Assignee, 1));
        AssertCompatibilityRejected(fixture, task, result); Assert.Empty(fixture.Projects.Assignments);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityRoleChangeRejectsRevokedWorkspaceMember()
    {
        var fixture = ProjectFixture.Create(); var (manager, target, task) = PrepareCompatibilityTarget(fixture);
        task.PrimaryAssigneeUserId = target.Id;
        var assignment = fixture.AddLegacyAssignment(task, target, TaskAssignmentRole.Assignee, manager.Id);
        RevokeWorkspaceMember(fixture, target.Id);
        var result = await fixture.Service.UpdateAssignmentAsync(assignment.Id, new UpdateTaskAssignmentRequest(TaskAssignmentRole.Reviewer, 2, 0));
        AssertCompatibilityRejected(fixture, task, result); Assert.Equal(TaskAssignmentRole.Assignee, assignment.Role); Assert.Equal(target.Id, task.PrimaryAssigneeUserId); Assert.Null(task.ReviewerUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilitySameRoleUpdateRejectsRevokedWorkspaceMember()
    {
        var fixture = ProjectFixture.Create(); var (manager, target, task) = PrepareCompatibilityTarget(fixture);
        task.PrimaryAssigneeUserId = target.Id;
        var assignment = fixture.AddLegacyAssignment(task, target, TaskAssignmentRole.Assignee, manager.Id);
        RevokeWorkspaceMember(fixture, target.Id);
        var result = await fixture.Service.UpdateAssignmentAsync(assignment.Id, new UpdateTaskAssignmentRequest(TaskAssignmentRole.Assignee, 3, 0));
        AssertCompatibilityRejected(fixture, task, result); Assert.Null(assignment.EstimatedHours); Assert.Equal(target.Id, task.PrimaryAssigneeUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task CompatibilityDeleteAllowsRevokedWorkspaceMemberCleanup()
    {
        var fixture = ProjectFixture.Create(); var (manager, target, task) = PrepareCompatibilityTarget(fixture);
        task.PrimaryAssigneeUserId = target.Id;
        var assignment = fixture.AddLegacyAssignment(task, target, TaskAssignmentRole.Assignee, manager.Id);
        RevokeWorkspaceMember(fixture, target.Id);
        var result = await fixture.Service.DeleteAssignmentAsync(assignment.Id);
        Assert.True(result.IsSuccess, result.Error); Assert.Empty(fixture.Projects.Assignments); Assert.Null(task.PrimaryAssigneeUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task HistoricalOwnerCleanupStillWorks()
    {
        var fixture = ProjectFixture.Create(); var (manager, target, task) = PrepareCompatibilityTarget(fixture);
        var assignment = fixture.AddLegacyAssignment(task, target, TaskAssignmentRole.Owner, manager.Id);
        RevokeWorkspaceMember(fixture, target.Id);
        var result = await fixture.Service.DeleteAssignmentAsync(assignment.Id);
        Assert.True(result.IsSuccess, result.Error); Assert.Empty(fixture.Projects.Assignments);
    }

    private static (User Manager, User Target, TaskItem Task) PrepareCompatibilityTarget(ProjectFixture fixture)
    {
        var manager = fixture.AddUser(); var target = fixture.AddUser(); fixture.Current.UserIdValue = manager.Id;
        fixture.AddProjectMember(manager.Id, ProjectRole.Manager); fixture.AddProjectMember(target.Id, ProjectRole.Contributor);
        return (manager, target, fixture.AddTask("compatibility target"));
    }

    private static void RevokeWorkspaceMember(ProjectFixture fixture, Guid userId) =>
        fixture.Workspaces.Members.Single(member => member.UserId == userId).Status = MembershipStatus.Suspended;

    private static void AssertCompatibilityRejected(ProjectFixture fixture, TaskItem task, Result<TaskAssignmentResponse> result)
    {
        Assert.False(result.IsSuccess); Assert.StartsWith("TASK_FORBIDDEN|", result.Error); Assert.Equal(1, task.VersionNo);
        Assert.Empty(fixture.Projects.Watches); Assert.Empty(fixture.TaskNotifications.Requests); Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskAssignmentChanges); Assert.Equal(0, fixture.Invalidations.TaskChangedCount); Assert.Equal(0, fixture.CommandUnitOfWork.SaveCount);
    }

    private sealed class ProjectFixture
    {
        private ProjectFixture()
        {
            Projects.ActivationEligibility = (userId, project) =>
                Workspaces.Items.TryGetValue(project.WorkspaceId, out var workspace) &&
                workspace is { Status: WorkspaceStatus.Active, DeletedAt: null } &&
                Workspaces.Members.Any(member =>
                    member.WorkspaceId == project.WorkspaceId &&
                    member.UserId == userId &&
                    member.Status == MembershipStatus.Active &&
                    member.TenantId == project.TenantId) &&
                Projects.Members.Any(member =>
                    member.ProjectId == project.Id &&
                    member.UserId == userId &&
                    member.Role is ProjectRole.Owner or ProjectRole.Manager);
            WorkspaceAuthorization = new WorkspaceAuthorizationService(Users, Workspaces);
            GroupAuthorization = new GroupAuthorizationService(Groups, Workspaces, WorkspaceAuthorization);
            ProjectAuthorization = new ProjectAuthorizationService(Projects, WorkspaceAuthorization, GroupAuthorization, Groups);
            Service = new ProjectService(
                Projects,
                Workspaces,
                Groups,
                Users,
                ProjectAuthorization,
                ProjectAuthorization,
                ProjectAuthorization,
                Current,
                Clock,
                Audit,
                Invalidations,
                AuthorizationChanges,
                UnitOfWork,
                CommandUnitOfWork,
                taskNotifications: TaskNotifications);
            Commands = new TaskCommandService(
                Projects,
                Groups,
                Users,
                ProjectAuthorization,
                ProjectAuthorization,
                Current,
                Clock,
                Audit,
                new NoopInvalidations(),
                new FakeTaskCommandUnitOfWork(),
                new UtcTimeZoneResolver(),
                taskNotifications: TaskNotifications);
        }

        private FakeUsers Users { get; } = new();
        public FakeWorkspaces Workspaces { get; } = new();
        private FakeGroups Groups { get; } = new();
        public FakeProjects Projects { get; } = new();
        public FakeCurrentUser Current { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeAuditLogger Audit { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeTaskCommandUnitOfWork CommandUnitOfWork { get; } = new();
        public RecordingTaskNotificationProducer TaskNotifications { get; } = new();
        public RecordingInvalidations Invalidations { get; } = new();
        public RecordingAuthorizationChanges AuthorizationChanges { get; } = new();
        private WorkspaceAuthorizationService WorkspaceAuthorization { get; }
        private GroupAuthorizationService GroupAuthorization { get; }
        public ProjectAuthorizationService ProjectAuthorization { get; }
        public ProjectService Service { get; }
        public TaskCommandService Commands { get; }
        public Workspace Workspace { get; } = new() { Name = "Workspace", Slug = "workspace", CreatedByUserId = Guid.NewGuid(), Status = WorkspaceStatus.Active };
        public Project Project { get; } = new() { Name = "Launch", Slug = "launch", WorkspaceId = Guid.Empty, OwnerUserId = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), Status = ProjectStatus.Active };
        public List<TaskDependency> Dependencies => Projects.Dependencies;

        public static ProjectFixture Create()
        {
            var fixture = new ProjectFixture();
            fixture.Project.WorkspaceId = fixture.Workspace.Id;
            fixture.Workspaces.Items[fixture.Workspace.Id] = fixture.Workspace;
            fixture.Projects.ProjectItems[fixture.Project.Id] = fixture.Project;
            var initialStage = new TaskWorkflowStage
            {
                ProjectId = fixture.Project.Id,
                WorkspaceId = fixture.Project.WorkspaceId,
                Name = "Backlog",
                InternalCategory = TaskStageCategory.Backlog,
                IsInitialStage = true,
                SortKey = 1000
            };
            fixture.Projects.Stages[initialStage.Id] = initialStage;
            return fixture;
        }

        public User AddUser(bool addWorkspaceMember = true, WorkspaceRole workspaceRole = WorkspaceRole.Member)
        {
            var user = new User
            {
                DisplayName = $"User {Users.Items.Count + 1}",
                Email = $"user{Users.Items.Count + 1}@example.com",
                NormalizedEmail = $"USER{Users.Items.Count + 1}@EXAMPLE.COM",
                PasswordHash = "hash",
                Status = UserStatus.Active
            };
            Users.Items[user.Id] = user;
            if (addWorkspaceMember)
            {
                Workspaces.Members.Add(new WorkspaceMember
                {
                    WorkspaceId = Workspace.Id,
                    UserId = user.Id,
                    User = user,
                    Role = workspaceRole,
                    Status = MembershipStatus.Active,
                    JoinedAt = Clock.UtcNow
                });
            }

            return user;
        }

        public Group AddGroup(Guid userId, GroupRole role = GroupRole.Member)
        {
            var group = new Group { WorkspaceId = Workspace.Id, Name = "Projects", Slug = "projects", GroupType = GroupType.ProjectGroup, Status = GroupStatus.Active, CreatedByUserId = userId };
            Groups.Items[group.Id] = group;
            Groups.Members.Add(new GroupMember { GroupId = group.Id, UserId = userId, Role = role, JoinedAt = Clock.UtcNow });
            return group;
        }

        public void AddProjectMember(Guid userId, ProjectRole role)
        {
            Projects.Members.Add(new ProjectMember
            {
                ProjectId = Project.Id,
                UserId = userId,
                User = Users.Items[userId],
                Role = role,
                JoinedAt = Clock.UtcNow
            });
        }

        public void EnsureProjectOperational()
        {
            if (Project.Status is not (ProjectStatus.Active or ProjectStatus.Review) ||
                Project.ActivationState == ProjectActivationState.Activated)
                return;

            Project.ActivationState = ProjectActivationState.Activated;
            Project.ActivatedAtUtc = Clock.UtcNow;
            Project.ActivationVersion = 1;
        }

        public TaskItem AddTask(string title)
        {
            EnsureProjectOperational();
            var task = new TaskItem
            {
                ProjectId = Project.Id,
                Title = title,
                CreatedByUserId = Guid.NewGuid()
            };
            Projects.Tasks[task.Id] = task;
            return task;
        }

        public Milestone AddMilestone(string title, int displayOrder)
        {
            EnsureProjectOperational();
            var milestone = new Milestone
            {
                ProjectId = Project.Id,
                Name = title,
                SortOrder = displayOrder
            };
            Projects.Milestones[milestone.Id] = milestone;
            return milestone;
        }

        public TaskAssignment AddLegacyAssignment(
            TaskItem task,
            User user,
            TaskAssignmentRole role,
            Guid actorUserId)
        {
            var assignment = new TaskAssignment
            {
                TaskItemId = task.Id,
                TaskItem = task,
                UserId = user.Id,
                User = user,
                Role = role,
                AssignedByUserId = actorUserId,
                AssignedAt = Clock.UtcNow
            };
            Projects.Assignments.Add(assignment);
            return assignment;
        }
    }

    private sealed class NoopInvalidations : IBusinessInvalidationPublisher
    {
        public Task TaskChangedAsync(TaskItem task, Guid actorUserId, string change, IEnumerable<string>? changedFields = null, IEnumerable<Guid>? affectedUserIds = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProjectChangedAsync(Project project, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AnnouncementChangedAsync(Announcement announcement, Guid actorUserId, string change, IEnumerable<Guid> audienceUserIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FileChangedAsync(FileObject fileObject, Attachment attachment, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingInvalidations : IBusinessInvalidationPublisher
    {
        public int TaskChangedCount { get; private set; }
        public int TaskAssignmentChangedCount { get; private set; }
        public int ProjectChangedCount { get; private set; }
        public List<string> TaskAssignmentChanges { get; } = [];

        public Task TaskChangedAsync(
            TaskItem task,
            Guid actorUserId,
            string change,
            IEnumerable<string>? changedFields = null,
            IEnumerable<Guid>? affectedUserIds = null,
            CancellationToken cancellationToken = default)
        {
            TaskChangedCount++;
            return Task.CompletedTask;
        }

        public Task TaskAssignmentChangedAsync(
            TaskItem task,
            Guid actorUserId,
            string change,
            IEnumerable<Guid>? affectedUserIds = null,
            CancellationToken cancellationToken = default)
        {
            TaskAssignmentChangedCount++;
            TaskAssignmentChanges.Add(change);
            return Task.CompletedTask;
        }

        public Task ProjectChangedAsync(Project project, Guid actorUserId, string change, CancellationToken cancellationToken = default)
        {
            ProjectChangedCount++;
            return Task.CompletedTask;
        }
        public Task AnnouncementChangedAsync(Announcement announcement, Guid actorUserId, string change, IEnumerable<Guid> audienceUserIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FileChangedAsync(FileObject fileObject, Attachment attachment, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingAuthorizationChanges : IAuthorizationStateChangePublisher
    {
        public List<(Guid TenantId, Guid UserId, string ScopeType, Guid? ScopeId, string Change)> Items { get; } = [];

        public Task PublishAsync(Guid tenantId, Guid affectedUserId, string scopeType, Guid? scopeId, string change, CancellationToken cancellationToken = default)
        {
            Items.Add((tenantId, affectedUserId, scopeType, scopeId, change));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTaskCommandUnitOfWork : ITaskCommandUnitOfWork
    {
        public TaskCommandSaveOutcome Outcome { get; set; } = new(TaskCommandSaveResult.Saved);
        public int SaveCount { get; private set; }
        public int ClearCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<TaskCommandSaveOutcome> SaveTaskCommandAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(Outcome);
        }
        public void ClearTaskCommandTracking() => ClearCount++;
    }

    private sealed class UtcTimeZoneResolver : ITaskWorkspaceTimeZoneResolver
    {
        public Task<TimeZoneInfo> ResolveAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(TimeZoneInfo.Utc);
    }

    private sealed class FakeProjects : IProjectRepository
    {
        public Dictionary<Guid, Project> ProjectItems { get; } = [];
        public Dictionary<Guid, IReadOnlyList<Guid>> CurrentReaderUserIds { get; } = [];
        public List<ProjectMember> Members { get; } = [];
        public Dictionary<Guid, Milestone> Milestones { get; } = [];
        public Dictionary<Guid, TaskItem> Tasks { get; } = [];
        public Dictionary<Guid, TaskWorkflowStage> Stages { get; } = [];
        public HashSet<Guid> TaskIdsWithArtifacts { get; } = [];
        public List<TaskAssignment> Assignments { get; } = [];
        public List<TaskDependency> Dependencies { get; } = [];
        public List<Comment> Comments { get; } = [];
        public List<WorkItemCollaborator> Collaborators { get; } = [];
        public List<WorkItemWatchState> Watches { get; } = [];
        private readonly System.Threading.Lock _memberLookupSync = new();
        private readonly TaskCompletionSource _firstMemberLookupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseMemberLookups = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeMemberLookups;
        public bool BlockMemberLookups { get; set; }
        public Task FirstMemberLookupEntered => _firstMemberLookupEntered.Task;
        public int MaxConcurrentMemberLookups { get; private set; }
        public int ListTaskIdsWithArtifactsCallCount { get; private set; }
        public int ListActivatableProjectIdsCallCount { get; private set; }
        public Func<Guid, Project, bool>? ActivationEligibility { get; set; }
        public void ReleaseMemberLookups() => _releaseMemberLookups.TrySetResult();

        public Task<IReadOnlyList<Project>> ListVisibleAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Project>>(ProjectItems.Values.Where(project => Members.Any(member => member.ProjectId == project.Id && member.UserId == userId)).ToList());
        public Task<IReadOnlyList<Guid>> ListActivatableProjectIdsAsync(Guid userId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
        {
            ListActivatableProjectIdsCallCount++;
            return Task.FromResult<IReadOnlyList<Guid>>(ProjectItems.Values
                .Where(project => projectIds.Contains(project.Id) && ActivationEligibility?.Invoke(userId, project) == true)
                .Select(project => project.Id)
                .ToArray());
        }
        public Task<IReadOnlyList<Guid>> ListCurrentReaderUserIdsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentReaderUserIds.TryGetValue(projectId, out var userIds)
                ? userIds
                : Members
                    .Where(member => member.ProjectId == projectId)
                    .Select(member => member.UserId)
                    .Distinct()
                    .ToArray());
        public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(ProjectItems.GetValueOrDefault(projectId));
        public async Task<ProjectMember?> GetMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default)
        {
            if (!BlockMemberLookups)
            {
                return Members.FirstOrDefault(member => member.ProjectId == projectId && member.UserId == userId);
            }

            var active = Interlocked.Increment(ref _activeMemberLookups);
            lock (_memberLookupSync)
            {
                MaxConcurrentMemberLookups = Math.Max(MaxConcurrentMemberLookups, active);
            }

            _firstMemberLookupEntered.TrySetResult();
            try
            {
                await _releaseMemberLookups.Task.WaitAsync(cancellationToken);
                return Members.FirstOrDefault(member => member.ProjectId == projectId && member.UserId == userId);
            }
            finally
            {
                Interlocked.Decrement(ref _activeMemberLookups);
            }
        }
        public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectMember>>(Members.Where(member => member.ProjectId == projectId).ToList());
        public Task<IReadOnlyList<Milestone>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Milestone>>(Milestones.Values.Where(milestone => milestone.ProjectId == projectId).OrderBy(milestone => milestone.SortOrder).ThenBy(milestone => milestone.DueDate).ToList());
        public Task<Milestone?> GetMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default) => Task.FromResult(Milestones.GetValueOrDefault(milestoneId));
        public Task<IReadOnlyList<TaskItem>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskItem>>(Tasks.Values.Where(task => task.ProjectId == projectId).ToList());
        public Task<IReadOnlyList<Guid>> ListTaskIdsWithArtifactsAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            ListTaskIdsWithArtifactsCallCount++;
            return Task.FromResult<IReadOnlyList<Guid>>(TaskIdsWithArtifacts.ToArray());
        }
        public Task<TaskItem?> GetTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult(Tasks.GetValueOrDefault(taskItemId));
        public Task<TaskWorkflowStage?> GetWorkflowStageAsync(Guid workflowStageId, CancellationToken cancellationToken = default) => Task.FromResult(Stages.GetValueOrDefault(workflowStageId));
        public Task<IReadOnlyList<TaskWorkflowStage>> ListWorkflowStagesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskWorkflowStage>>(Stages.Values.Where(stage => stage.ProjectId == projectId).ToList());
        public Task<IReadOnlyList<WorkItemCollaborator>> ListCollaboratorsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkItemCollaborator>>(Collaborators.Where(item => item.TaskItemId == taskItemId).ToList());
        public Task<IReadOnlyList<TaskAssignment>> ListAssignmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskAssignment>>(Assignments.Where(assignment => assignment.TaskItemId == taskItemId).ToList());
        public Task<TaskAssignment?> GetAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default)
        {
            var assignment = Assignments.FirstOrDefault(assignment => assignment.Id == assignmentId);
            if (assignment is not null && Tasks.TryGetValue(assignment.TaskItemId, out var task))
            {
                assignment.TaskItem = task;
            }
            return Task.FromResult(assignment);
        }
        public Task<IReadOnlyList<TaskDependency>> ListDependenciesAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>(Dependencies.Where(dependency => dependency.PredecessorTaskItemId == taskItemId || dependency.SuccessorTaskItemId == taskItemId).ToList());
        public Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>(Dependencies.Where(dependency => dependency.ProjectId == projectId).ToList());
        public Task<TaskDependency?> GetDependencyAsync(Guid dependencyId, CancellationToken cancellationToken = default) => Task.FromResult(Dependencies.FirstOrDefault(dependency => dependency.Id == dependencyId));
        public Task<bool> DependencyExistsAsync(Guid predecessorTaskId, Guid successorTaskId, CancellationToken cancellationToken = default) => Task.FromResult(Dependencies.Any(dependency => dependency.PredecessorTaskItemId == predecessorTaskId && dependency.SuccessorTaskItemId == successorTaskId));
        public Task<IReadOnlyList<Comment>> ListCommentsAsync(CommentTargetType targetType, Guid targetId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Comment>>(Comments.Where(comment => comment.TargetType == targetType && comment.TargetId == targetId).ToList());
        public Task<Comment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken = default) => Task.FromResult(Comments.FirstOrDefault(comment => comment.Id == commentId));
        public Task<IReadOnlyList<WorkItemWatchState>> ListWatchStatesAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkItemWatchState>>(Watches.Where(state => state.TaskItemId == taskItemId).ToList());
        public Task AddProjectAsync(Project project, CancellationToken cancellationToken = default) { ProjectItems[project.Id] = project; return Task.CompletedTask; }
        public Task AddMemberAsync(ProjectMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
        public Task AddMilestoneAsync(Milestone milestone, CancellationToken cancellationToken = default) { Milestones[milestone.Id] = milestone; return Task.CompletedTask; }
        public Task AddTaskAsync(TaskItem task, CancellationToken cancellationToken = default) { Tasks[task.Id] = task; return Task.CompletedTask; }
        public Task AddCollaboratorAsync(WorkItemCollaborator collaborator, CancellationToken cancellationToken = default) { Collaborators.Add(collaborator); return Task.CompletedTask; }
        public Task AddAssignmentAsync(TaskAssignment assignment, CancellationToken cancellationToken = default) { Assignments.Add(assignment); return Task.CompletedTask; }
        public Task AddDependencyAsync(TaskDependency dependency, CancellationToken cancellationToken = default) { Dependencies.Add(dependency); return Task.CompletedTask; }
        public Task AddCommentAsync(Comment comment, CancellationToken cancellationToken = default) { Comments.Add(comment); return Task.CompletedTask; }
        public Task AddWatchStateAsync(WorkItemWatchState watchState, CancellationToken cancellationToken = default) { Watches.Add(watchState); return Task.CompletedTask; }
        public void RemoveMember(ProjectMember member) => Members.Remove(member);
        public void RemoveAssignment(TaskAssignment assignment) => Assignments.Remove(assignment);
        public void RemoveDependency(TaskDependency dependency) => Dependencies.Remove(dependency);
        public void RemoveCollaborator(WorkItemCollaborator collaborator) => Collaborators.Remove(collaborator);
    }

    private sealed class FakeUsers : IUserRepository
    {
        public Dictionary<Guid, User> Items { get; } = [];
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => Task.FromResult(Items.Values.FirstOrDefault(user => user.NormalizedEmail == normalizedEmail));
        public Task<IReadOnlyList<User>> GetActiveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<User>>(Items.Values.Where(user => ids.Contains(user.Id) && user is { Status: UserStatus.Active, DeletedAt: null }).ToArray());
        public Task AddAsync(User user, CancellationToken cancellationToken = default) { Items[user.Id] = user; return Task.CompletedTask; }
    }

    private sealed class FakeWorkspaces : IWorkspaceRepository
    {
        public Dictionary<Guid, Workspace> Items { get; } = [];
        public List<WorkspaceMember> Members { get; } = [];
        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Workspace>>(Items.Values.ToList());
        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(workspaceId));
        public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Members.FirstOrDefault(member => member.WorkspaceId == workspaceId && member.UserId == userId));
        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceMember>>(Members.Where(member => member.WorkspaceId == workspaceId).ToList());
        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default) { Items[workspace.Id] = workspace; return Task.CompletedTask; }
        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
    }

    private sealed class FakeGroups : IGroupRepository
    {
        public Dictionary<Guid, Group> Items { get; } = [];
        public List<GroupMember> Members { get; } = [];
        public Task<IReadOnlyList<Group>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Group>>(Items.Values.Where(group => group.WorkspaceId == workspaceId).ToList());
        public Task<IReadOnlyList<Group>> ListManagedByUserAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Group>>(Items.Values.Where(group => group.WorkspaceId == workspaceId && Members.Any(member => member.GroupId == group.Id && member.UserId == userId && member.Role is GroupRole.Owner or GroupRole.Admin)).ToList());
        public Task<Group?> GetByIdAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(groupId));
        public Task<GroupMember?> GetMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Members.FirstOrDefault(member => member.GroupId == groupId && member.UserId == userId));
        public Task<IReadOnlyList<GroupMember>> ListMembersAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GroupMember>>(Members.Where(member => member.GroupId == groupId).ToList());
        public Task AddAsync(Group group, CancellationToken cancellationToken = default) { Items[group.Id] = group; return Task.CompletedTask; }
        public Task AddMemberAsync(GroupMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserIdValue { get; set; }
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => UserIdValue.HasValue;
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; } = new(2026, 6, 6, 0, 0, 0, TimeSpan.Zero); }
    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
    private sealed class RecordingTaskNotificationProducer : ITaskNotificationProducer
    {
        public List<TaskNotificationRecipientRequest> Requests { get; } = [];

        public Task ProduceAsync(
            TaskNotificationRecipientRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }
}
