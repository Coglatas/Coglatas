using Coglatas.Application.Common;
using Coglatas.Application.Projects;
using Coglatas.Application.Security.Redaction;
using Coglatas.Domain.Enums;
using Coglatas.Web.Models;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class ProjectsController(IProjectService projects, ITaskCommandService taskCommands, ITaskSubresourceService taskSubresources) : ControllerBase
{
    [HttpGet("api/projects")]
    public async Task<IActionResult> List([FromQuery] ProjectListQuery query, CancellationToken cancellationToken)
    {
        var result = await projects.ListAsync(query, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.UiList,
                "Projects",
                RedactionAuthorizationState.Allowed))
            : ToProjectReadError(result, "ProjectListFailed");
    }

    [HttpPost("api/projects")]
    public async Task<IActionResult> Create(CreateProjectRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.CreateAsync(request, cancellationToken));

    [HttpGet("api/projects/{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await projects.GetAsync(projectId, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.UiDetail,
                "Projects",
                RedactionAuthorizationState.Allowed))
            : ToProjectReadError(result, "ProjectReadFailed");
    }

    [HttpPatch("api/projects/{projectId:guid}")]
    public async Task<IActionResult> Update(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.UpdateAsync(projectId, request, cancellationToken));

    [HttpDelete("api/projects/{projectId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, CancellationToken cancellationToken) => OkOrBad(await projects.ArchiveAsync(projectId, cancellationToken));

    [HttpPost("api/projects/{projectId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid projectId, CancellationToken cancellationToken) => OkOrBad(await projects.ArchiveAsync(projectId, cancellationToken));

    [HttpPost("api/projects/{projectId:guid}/restore")]
    public async Task<IActionResult> Restore(Guid projectId, CancellationToken cancellationToken) => OkOrBad(await projects.RestoreAsync(projectId, cancellationToken));

    [HttpGet("api/projects/{projectId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid projectId, CancellationToken cancellationToken) => ToActionResult(await projects.ListMembersAsync(projectId, cancellationToken));

    [HttpPost("api/projects/{projectId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid projectId, AddProjectMemberRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.AddMemberAsync(projectId, request, cancellationToken));

    [HttpPatch("api/projects/{projectId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> UpdateMember(Guid projectId, Guid userId, UpdateProjectMemberRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.UpdateMemberAsync(projectId, userId, request, cancellationToken));

    [HttpDelete("api/projects/{projectId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid projectId, Guid userId, CancellationToken cancellationToken) => OkOrBad(await projects.RemoveMemberAsync(projectId, userId, cancellationToken));

    [HttpGet("api/projects/{projectId:guid}/milestones")]
    public async Task<IActionResult> ListMilestones(Guid projectId, [FromQuery] ProjectChildListQuery query, CancellationToken cancellationToken) => ToActionResult(await projects.ListMilestonesAsync(projectId, query, cancellationToken));

    [HttpPost("api/projects/{projectId:guid}/milestones")]
    public async Task<IActionResult> CreateMilestone(Guid projectId, CreateMilestoneRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.CreateMilestoneAsync(projectId, request, cancellationToken));

    [HttpGet("api/milestones/{milestoneId:guid}")]
    public async Task<IActionResult> GetMilestone(Guid milestoneId, CancellationToken cancellationToken) => ToActionResult(await projects.GetMilestoneAsync(milestoneId, cancellationToken));

    [HttpPatch("api/milestones/{milestoneId:guid}")]
    public async Task<IActionResult> UpdateMilestone(Guid milestoneId, UpdateMilestoneRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.UpdateMilestoneAsync(milestoneId, request, cancellationToken));

    [HttpDelete("api/milestones/{milestoneId:guid}")]
    public async Task<IActionResult> DeleteMilestone(Guid milestoneId, CancellationToken cancellationToken) => OkOrBad(await projects.DeleteMilestoneAsync(milestoneId, cancellationToken));

    [HttpGet("api/projects/{projectId:guid}/tasks")]
    public async Task<IActionResult> ListTasks(Guid projectId, [FromQuery] TaskListQuery query, CancellationToken cancellationToken) => ToActionResult(await projects.ListTasksAsync(projectId, query, cancellationToken));

    [HttpPost("api/projects/{projectId:guid}/tasks")]
    public async Task<IActionResult> CreateTask(Guid projectId, CreateTaskItemRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.CreateTaskAsync(projectId, request, cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}")]
    public async Task<IActionResult> GetTask(Guid taskItemId, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.GetDetailAsync(taskItemId, cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}/activity")]
    public async Task<IActionResult> ListTaskActivity(Guid taskItemId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        ToTaskActionResult(await taskSubresources.ListActivityAsync(taskItemId, page, pageSize, cancellationToken));

    [HttpPatch("api/tasks/{taskItemId:guid}")]
    public async Task<IActionResult> UpdateTask(Guid taskItemId, TaskUpdateDetailsRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.UpdateDetailsAsync(taskItemId, request, cancellationToken));

    [AllowAnonymous]
    [HttpPatch("api/tasks/{taskItemId:guid}/schedule")]
    public async Task<IActionResult> UpdateTaskSchedule(Guid taskItemId, TaskScheduleUpdateRequest request, CancellationToken cancellationToken) =>
        ToGanttCommandActionResult(await taskCommands.UpdateScheduleAsync(taskItemId, request, cancellationToken));

    [AllowAnonymous]
    [HttpPatch("api/tasks/{taskItemId:guid}/progress")]
    public async Task<IActionResult> UpdateTaskProgress(Guid taskItemId, TaskProgressUpdateRequest request, CancellationToken cancellationToken) =>
        ToGanttCommandActionResult(await taskCommands.UpdateProgressAsync(taskItemId, request, cancellationToken));

    [HttpDelete("api/tasks/{taskItemId:guid}")]
    public async Task<IActionResult> DeleteTask(Guid taskItemId, [FromQuery] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.DeleteAsync(taskItemId, new TaskDeleteRequest(expectedVersion), cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/restore")]
    public async Task<IActionResult> RestoreTask(Guid taskItemId, TaskRestoreRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.RestoreAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/transition")]
    public async Task<IActionResult> TransitionTask(Guid taskItemId, TaskTransitionRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.TransitionAsync(taskItemId, request, cancellationToken));

    [HttpPut("api/tasks/{taskItemId:guid}/blocked-state")]
    public async Task<IActionResult> SetBlockedState(Guid taskItemId, TaskBlockedStateRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.SetBlockedStateAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/cancel")]
    public async Task<IActionResult> CancelTask(Guid taskItemId, TaskReviewRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.CancelAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/reopen")]
    public async Task<IActionResult> ReopenTask(Guid taskItemId, TaskReviewRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.ReopenAsync(taskItemId, request, cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}/relationships")]
    public async Task<IActionResult> GetRelationships(Guid taskItemId, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.GetRelationshipsAsync(taskItemId, cancellationToken));

    [HttpPut("api/tasks/{taskItemId:guid}/assignee")]
    public async Task<IActionResult> SetAssignee(Guid taskItemId, TaskRelationshipUserRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.SetAssigneeAsync(taskItemId, request, cancellationToken));

    [HttpPut("api/tasks/{taskItemId:guid}/target-group")]
    public async Task<IActionResult> SetTargetGroup(Guid taskItemId, TaskTargetGroupRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.SetTargetGroupAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/collaborators")]
    public async Task<IActionResult> AddCollaborator(Guid taskItemId, TaskCollaboratorRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.AddCollaboratorAsync(taskItemId, request, cancellationToken));

    [HttpDelete("api/tasks/{taskItemId:guid}/collaborators/{userId:guid}")]
    public async Task<IActionResult> RemoveCollaborator(Guid taskItemId, Guid userId, [FromQuery] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.RemoveCollaboratorAsync(taskItemId, userId, expectedVersion, cancellationToken));

    [HttpPut("api/tasks/{taskItemId:guid}/reviewer")]
    public async Task<IActionResult> SetReviewer(Guid taskItemId, TaskRelationshipUserRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.SetReviewerAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/review/submit")]
    public async Task<IActionResult> SubmitReview(Guid taskItemId, TaskReviewRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.SubmitReviewAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/review/accept")]
    public async Task<IActionResult> AcceptReview(Guid taskItemId, TaskReviewRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.AcceptReviewAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/review/return")]
    public async Task<IActionResult> ReturnReview(Guid taskItemId, TaskReviewRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.ReturnReviewAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/review/override-complete")]
    public async Task<IActionResult> OverrideComplete(Guid taskItemId, TaskReviewRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.OverrideCompleteAsync(taskItemId, request, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/claim")]
    public async Task<IActionResult> Claim(Guid taskItemId, TaskClaimRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.ClaimAsync(taskItemId, request, cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}/watch-state")]
    public async Task<IActionResult> GetWatchState(Guid taskItemId, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.GetWatchStateAsync(taskItemId, cancellationToken));

    [HttpPut("api/tasks/{taskItemId:guid}/watch")]
    public async Task<IActionResult> Watch(Guid taskItemId, TaskWatchRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.WatchAsync(taskItemId, request, cancellationToken));

    [HttpDelete("api/tasks/{taskItemId:guid}/watch")]
    public async Task<IActionResult> Unwatch(Guid taskItemId, [FromQuery, System.ComponentModel.DataAnnotations.Range(typeof(long), "0", "9223372036854775807")] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskCommands.UnwatchAsync(taskItemId, new TaskWatchRequest(expectedVersion), cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}/subtasks")]
    public async Task<IActionResult> ListSubtasks(Guid taskItemId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) => ToTaskActionResult(await taskSubresources.ListSubtasksAsync(taskItemId, page, pageSize, cancellationToken));
    [HttpPost("api/tasks/{taskItemId:guid}/subtasks")]
    public async Task<IActionResult> CreateSubtask(Guid taskItemId, CreateTaskSubtaskRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.CreateSubtaskAsync(taskItemId, request, cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}/files")]
    public async Task<IActionResult> ListTaskFiles(Guid taskItemId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) => ToTaskActionResult(await taskSubresources.ListFilesAsync(taskItemId, page, pageSize, cancellationToken));
    [HttpPost("api/tasks/{taskItemId:guid}/files")]
    public async Task<IActionResult> AssociateTaskFile(Guid taskItemId, CreateTaskFileAssociationRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.AssociateFileAsync(taskItemId, request, cancellationToken));
    [HttpDelete("api/tasks/{taskItemId:guid}/files/{associationId:guid}")]
    public async Task<IActionResult> RemoveTaskFile(Guid taskItemId, Guid associationId, [FromQuery] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.RemoveFileAsync(taskItemId, associationId, expectedVersion, cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}/checklist")]
    public async Task<IActionResult> ListChecklist(Guid taskItemId, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.ListChecklistAsync(taskItemId, cancellationToken));
    [HttpPost("api/tasks/{taskItemId:guid}/checklist")]
    public async Task<IActionResult> CreateChecklist(Guid taskItemId, CreateTaskChecklistRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.CreateChecklistAsync(taskItemId, request, cancellationToken));
    [HttpPatch("api/tasks/{taskItemId:guid}/checklist/{itemId:guid}")]
    public async Task<IActionResult> UpdateChecklist(Guid taskItemId, Guid itemId, UpdateTaskChecklistRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.UpdateChecklistAsync(taskItemId, itemId, request, cancellationToken));
    [HttpDelete("api/tasks/{taskItemId:guid}/checklist/{itemId:guid}")]
    public async Task<IActionResult> DeleteChecklist(Guid taskItemId, Guid itemId, [FromQuery] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.DeleteChecklistAsync(taskItemId, itemId, expectedVersion, cancellationToken));
    [HttpPut("api/tasks/{taskItemId:guid}/checklist/order")]
    public async Task<IActionResult> ReorderChecklist(Guid taskItemId, ReorderTaskChecklistRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.ReorderChecklistAsync(taskItemId, request, cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}/comments")]
    public async Task<IActionResult> ListTaskComments(Guid taskItemId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) => ToTaskActionResult(await taskSubresources.ListCommentsAsync(taskItemId, page, pageSize, cancellationToken));
    [HttpPost("api/tasks/{taskItemId:guid}/comments")]
    public async Task<IActionResult> CreateTaskComment(Guid taskItemId, CreateTaskCommentRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.CreateCommentAsync(taskItemId, request, cancellationToken));
    [HttpPatch("api/task-comments/{commentId:guid}")]
    public async Task<IActionResult> UpdateTaskComment(Guid commentId, UpdateTaskCommentRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.UpdateCommentAsync(commentId, request, cancellationToken));
    [HttpDelete("api/task-comments/{commentId:guid}")]
    public async Task<IActionResult> DeleteTaskComment(Guid commentId, [FromQuery] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.DeleteCommentAsync(commentId, expectedVersion, cancellationToken));
    [HttpGet("api/tasks/{taskItemId:guid}/mention-candidates")]
    public async Task<IActionResult> SearchMentionCandidates(Guid taskItemId, [FromQuery] string? query, [FromQuery] int limit = 10, CancellationToken cancellationToken = default) => ToTaskActionResult(await taskSubresources.SearchMentionCandidatesAsync(taskItemId, query, limit, cancellationToken));

    [HttpGet("api/projects/{projectId:guid}/task-labels")]
    public async Task<IActionResult> ListTaskLabels(Guid projectId, [FromQuery] bool includeArchived, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.ListLabelsAsync(projectId, includeArchived, cancellationToken));
    [HttpPost("api/projects/{projectId:guid}/task-labels")]
    public async Task<IActionResult> CreateTaskLabel(Guid projectId, CreateProjectTaskLabelRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.CreateLabelAsync(projectId, request, cancellationToken));
    [HttpPatch("api/projects/{projectId:guid}/task-labels/{labelId:guid}")]
    public async Task<IActionResult> UpdateTaskLabel(Guid projectId, Guid labelId, UpdateProjectTaskLabelRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.UpdateLabelAsync(projectId, labelId, request, cancellationToken));
    [HttpPost("api/projects/{projectId:guid}/task-labels/{labelId:guid}/archive")]
    public async Task<IActionResult> ArchiveTaskLabel(Guid projectId, Guid labelId, [FromQuery] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.SetLabelArchiveAsync(projectId, labelId, expectedVersion, true, cancellationToken));
    [HttpPost("api/projects/{projectId:guid}/task-labels/{labelId:guid}/restore")]
    public async Task<IActionResult> RestoreTaskLabel(Guid projectId, Guid labelId, [FromQuery] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.SetLabelArchiveAsync(projectId, labelId, expectedVersion, false, cancellationToken));
    [HttpPut("api/tasks/{taskItemId:guid}/labels/{labelId:guid}")]
    public async Task<IActionResult> ApplyTaskLabel(Guid taskItemId, Guid labelId, TaskLabelAssociationRequest request, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.ApplyLabelAsync(taskItemId, labelId, request, cancellationToken));
    [HttpDelete("api/tasks/{taskItemId:guid}/labels/{labelId:guid}")]
    public async Task<IActionResult> RemoveTaskLabel(Guid taskItemId, Guid labelId, [FromQuery] long expectedVersion, CancellationToken cancellationToken) => ToTaskActionResult(await taskSubresources.RemoveLabelAsync(taskItemId, labelId, expectedVersion, cancellationToken));

    [HttpGet("api/tasks/{taskItemId:guid}/assignments")]
    public async Task<IActionResult> ListAssignments(Guid taskItemId, CancellationToken cancellationToken) => ToActionResult(await projects.ListAssignmentsAsync(taskItemId, cancellationToken));

    [HttpPost("api/tasks/{taskItemId:guid}/assignments")]
    public async Task<IActionResult> AddAssignment(Guid taskItemId, AddTaskAssignmentRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.AddAssignmentAsync(taskItemId, request, cancellationToken));

    [HttpPatch("api/tasks/{taskItemId:guid}/assignments/{assignmentId:guid}")]
    public async Task<IActionResult> UpdateAssignment(Guid assignmentId, UpdateTaskAssignmentRequest request, CancellationToken cancellationToken) => ToActionResult(await projects.UpdateAssignmentAsync(assignmentId, request, cancellationToken));

    [HttpDelete("api/tasks/{taskItemId:guid}/assignments/{assignmentId:guid}")]
    public async Task<IActionResult> DeleteAssignment(Guid assignmentId, CancellationToken cancellationToken) => OkOrBad(await projects.DeleteAssignmentAsync(assignmentId, cancellationToken));

    [AllowAnonymous]
    [HttpGet("api/tasks/{taskItemId:guid}/dependencies")]
    public async Task<IActionResult> ListDependencies(Guid taskItemId, CancellationToken cancellationToken) =>
        ToDependencyActionResult(await projects.ListDependenciesAsync(taskItemId, cancellationToken));

    [AllowAnonymous]
    [HttpPost("api/tasks/{taskItemId:guid}/dependencies")]
    public async Task<IActionResult> AddDependency(Guid taskItemId, AddTaskDependencyRequest request, CancellationToken cancellationToken) =>
        ToDependencyActionResult(await projects.AddDependencyAsync(taskItemId, request, cancellationToken));

    [AllowAnonymous]
    [HttpDelete("api/tasks/{taskItemId:guid}/dependencies/{dependencyId:guid}")]
    public async Task<IActionResult> DeleteDependency(
        Guid taskItemId,
        Guid dependencyId,
        [FromQuery] long expectedVersion,
        CancellationToken cancellationToken) =>
        ToDependencyActionResult(await projects.DeleteDependencyAsync(taskItemId, dependencyId, expectedVersion, cancellationToken));

    [HttpGet("api/comments")]
    public async Task<IActionResult> ListComments([FromQuery] CommentTargetType targetType, [FromQuery] Guid targetId, [FromQuery] ProjectChildListQuery query, CancellationToken cancellationToken)
    {
        if (targetType != CommentTargetType.TaskItem) return ToActionResult(await projects.ListCommentsAsync(targetType, targetId, query, cancellationToken));
        var result = await taskSubresources.ListCommentsAsync(targetId, query.SafePage, query.SafePageSize, cancellationToken);
        if (!result.IsSuccess) return ToTaskActionResult(result);
        var page = result.Value!;
        var items = page.Items.Where(item => item.BodyPlainText is not null).Select(ToLegacyComment).ToList();
        return Ok(new PagedResponse<CommentResponse>(items, page.Page, page.PageSize, page.TotalCount));
    }

    [HttpPost("api/comments")]
    public async Task<IActionResult> AddComment(CreateCommentRequest request, CancellationToken cancellationToken)
    {
        if (request.TargetType != CommentTargetType.TaskItem) return ToActionResult(await projects.AddCommentAsync(request, cancellationToken));
        var result = await taskSubresources.CreateCommentAsync(request.TargetId, new CreateTaskCommentRequest(request.Body), cancellationToken);
        return result.IsSuccess ? Ok(ToLegacyComment(result.Value!)) : ToTaskActionResult(result);
    }

    [HttpPatch("api/comments/{commentId:guid}")]
    public async Task<IActionResult> UpdateComment(Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken)
    {
        var compatibility = await taskSubresources.GetCommentForCompatibilityAsync(commentId, cancellationToken);
        if (!compatibility.IsSuccess) return ToTaskActionResult(compatibility);
        if (compatibility.Value is null) return ToActionResult(await projects.UpdateCommentAsync(commentId, request, cancellationToken));
        var result = await taskSubresources.UpdateCommentAsync(commentId, new UpdateTaskCommentRequest(request.Body, null, request.ExpectedVersion ?? compatibility.Value.Version), cancellationToken);
        return result.IsSuccess ? Ok(ToLegacyComment(result.Value!)) : ToTaskActionResult(result);
    }

    [HttpDelete("api/comments/{commentId:guid}")]
    public async Task<IActionResult> DeleteComment(Guid commentId, [FromQuery] long? expectedVersion, CancellationToken cancellationToken)
    {
        var compatibility = await taskSubresources.GetCommentForCompatibilityAsync(commentId, cancellationToken);
        if (!compatibility.IsSuccess) return ToTaskActionResult(compatibility);
        if (compatibility.Value is null) return OkOrBad(await projects.DeleteCommentAsync(commentId, cancellationToken));
        return ToTaskActionResult(await taskSubresources.DeleteCommentAsync(commentId, expectedVersion ?? compatibility.Value.Version, cancellationToken));
    }

    private IActionResult OkOrBad(Result result) =>
        result.IsSuccess
            ? Ok(new { status = "OK" })
            : ProjectMutationFailure(result.ErrorDetail, result.Error);

    private IActionResult ToProjectReadError<T>(
        Result<T> result,
        string fallbackCode)
    {
        var status = result.ErrorDetail?.Code switch
        {
            "PROJECT_CONFLICT" or "InvalidStateTransition" or
            "MILESTONE_STALE_VERSION" or "MILESTONE_CONFLICT" =>
                StatusCodes.Status409Conflict,
            "NotFound" => StatusCodes.Status404NotFound,
            "DependencyUnavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(status, CanonicalErrorEnvelope.FromSensitiveResult(
            HttpContext,
            status,
            result.ErrorDetail,
            result.Error,
            fallbackCode));
    }

    private IActionResult ToActionResult<T>(Result<T> result) =>
        result.IsSuccess
            ? Ok(result.Value)
            : ProjectMutationFailure(result.ErrorDetail, result.Error);

    private IActionResult ProjectMutationFailure(
        ApplicationErrorDetail? detail,
        string? error)
    {
        if (detail?.Code is "PROJECT_CONFLICT" or "InvalidStateTransition")
            return ProjectConflict(detail);
        if (detail?.Code is "MILESTONE_STALE_VERSION" or "MILESTONE_CONFLICT")
            return MilestoneConflict(detail);
        if (detail?.Code == "NotFound")
            return StatusCode(StatusCodes.Status404NotFound, ApiEnvelope.Error(
                HttpContext,
                StatusCodes.Status404NotFound,
                "NotFound",
                "The requested resource was not found.",
                redactionApplied: true));
        if (detail?.Code == "DependencyUnavailable")
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiEnvelope.Error(
                HttpContext,
                StatusCodes.Status503ServiceUnavailable,
                "DependencyUnavailable",
                "Project creation is temporarily unavailable."));
        if (detail?.Code == "TASK_BRIEF_FIELD_TOO_LONG")
            return TaskBriefValidationError(detail);
        return BadRequest(ToErrorResponse(error));
    }

    private IActionResult ProjectConflict(
        ApplicationErrorDetail detail) =>
        StatusCode(
            StatusCodes.Status409Conflict,
            ApiEnvelope.Error(
                HttpContext,
                StatusCodes.Status409Conflict,
                detail.Code,
                detail.Message,
                detail.Target ?? (detail.Code == "InvalidStateTransition" ? "body.status" : "project")));

    private IActionResult MilestoneConflict(
        ApplicationErrorDetail detail) =>
        StatusCode(StatusCodes.Status409Conflict, new
        {
            requestId = HttpContext.TraceIdentifier,
            error = new
            {
                code = detail.Code,
                message = detail.Message,
                target = "milestone",
                details = Array.Empty<object>(),
                redactionApplied = false
            }
        });

    private IActionResult ToTaskActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);
        var parts = (result.Error ?? "TASK_TRANSITION_GUARD_FAILED|The request could not be completed.").Split('|', 2);
        var code = result.ErrorDetail?.Code ?? parts[0];
        var message = result.ErrorDetail?.Message ?? (parts.Length == 2 ? parts[1] : "The request could not be completed.");
        var status = code switch
        {
            "TASK_NOT_FOUND" or "TASK_CHECKLIST_ITEM_NOT_FOUND" or "TASK_FILE_ASSOCIATION_NOT_FOUND" or "TASK_LABEL_NOT_FOUND" => StatusCodes.Status404NotFound,
            "TASK_FORBIDDEN" or "TASK_CLAIM_GROUP_MEMBERSHIP_REQUIRED" or "TASK_COMMENT_FORBIDDEN" or "TASK_LABEL_FORBIDDEN" or "TASK_FILE_ASSOCIATION_FORBIDDEN" => StatusCodes.Status403Forbidden,
            "TASK_STALE_VERSION" or "TASK_ALREADY_ASSIGNED" or "TASK_CONFLICT" => StatusCodes.Status409Conflict,
            "TASK_COMMENT_RATE_LIMITED" => StatusCodes.Status429TooManyRequests,
            "TASK_TRANSITION_GUARD_FAILED" or "TASK_ASSIGNEE_REQUIRED" or "TASK_REVIEW_REQUIRED" or "TASK_BLOCK_REASON_REQUIRED" or "TASK_CANCEL_REASON_REQUIRED" => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest
        };
        if (code == "TASK_COMMENT_RATE_LIMITED")
        {
            var retryAfterSeconds = Math.Max(1, result.ErrorDetail?.RetryAfterSeconds ?? 1);
            Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var problem = new ProblemDetails
            {
                Status = status,
                Title = "Comment submission is temporarily limited.",
                Detail = message,
                Type = code,
                Instance = HttpContext.Request.Path,
                Extensions =
                {
                    ["code"] = code,
                    ["retryAfterSeconds"] = retryAfterSeconds,
                    ["requestId"] = HttpContext.TraceIdentifier
                }
            };
            return StatusCode(status, problem);
        }
        return StatusCode(status, new { requestId = HttpContext.TraceIdentifier, error = new { code, message, target = result.ErrorDetail?.Target, details = Array.Empty<object>(), redactionApplied = false } });
    }

    private IActionResult TaskBriefValidationError(ApplicationErrorDetail detail) =>
        StatusCode(StatusCodes.Status400BadRequest, new
        {
            requestId = HttpContext.TraceIdentifier,
            error = new
            {
                code = detail.Code,
                message = detail.Message,
                target = detail.Target,
                details = Array.Empty<object>(),
                redactionApplied = false
            }
        });
    private IActionResult ToTaskActionResult(Result result)
    {
        if (result.IsSuccess) return Ok(new { status = "OK" });
        var parts = (result.Error ?? "TASK_TRANSITION_GUARD_FAILED|The request could not be completed.").Split('|', 2);
        var code = parts[0];
        var status = code is "TASK_STALE_VERSION" or "TASK_CONFLICT" ? StatusCodes.Status409Conflict : code is "TASK_COMMENT_RATE_LIMITED" ? StatusCodes.Status429TooManyRequests : code is "TASK_NOT_FOUND" or "TASK_CHECKLIST_ITEM_NOT_FOUND" or "TASK_FILE_ASSOCIATION_NOT_FOUND" or "TASK_LABEL_NOT_FOUND" ? StatusCodes.Status404NotFound : code is "TASK_FORBIDDEN" or "TASK_LABEL_FORBIDDEN" or "TASK_COMMENT_FORBIDDEN" or "TASK_FILE_ASSOCIATION_FORBIDDEN" ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
        if (code == "TASK_COMMENT_RATE_LIMITED")
        {
            var retryAfterSeconds = Math.Max(1, result.ErrorDetail?.RetryAfterSeconds ?? 1);
            Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var problem = new ProblemDetails
            {
                Status = status,
                Title = "Comment submission is temporarily limited.",
                Detail = parts.Length == 2 ? parts[1] : "The request could not be completed.",
                Type = code,
                Instance = HttpContext.Request.Path,
                Extensions =
                {
                    ["code"] = code,
                    ["retryAfterSeconds"] = retryAfterSeconds,
                    ["requestId"] = HttpContext.TraceIdentifier
                }
            };
            return StatusCode(status, problem);
        }
        return StatusCode(status, new { requestId = HttpContext.TraceIdentifier, error = new { code, message = parts.Length == 2 ? parts[1] : "The request could not be completed.", target = (string?)null, details = Array.Empty<object>(), redactionApplied = false } });
    }

    private IActionResult ToGanttCommandActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var parts = (result.Error ?? "GANTT_REQUEST_FAILED|The request could not be completed.").Split('|', 2);
        var code = result.ErrorDetail?.Code ?? parts[0];
        var message = result.ErrorDetail?.Message ?? (parts.Length == 2 ? parts[1] : "The request could not be completed.");
        var status = code switch
        {
            "GANTT_AUTHENTICATION_REQUIRED" => StatusCodes.Status401Unauthorized,
            "GANTT_FORBIDDEN" => StatusCodes.Status403Forbidden,
            "GANTT_WORK_ITEM_NOT_FOUND" => StatusCodes.Status404NotFound,
            "GANTT_STALE_VERSION" or "GANTT_CONFLICT" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return StructuredCommandError(
            status,
            code,
            message,
            code switch
            {
                "GANTT_INVALID_DATE_RANGE" => "plannedEndDate",
                "GANTT_INVALID_PROGRESS" => "progressPercent",
                "MILESTONE_DATE_REQUIRED" => "milestoneDate",
                _ => null
            },
            code == "GANTT_WORK_ITEM_NOT_FOUND");
    }

    private IActionResult ToDependencyActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
            return Ok(result.Value);
        return DependencyError(result.Error, result.ErrorDetail);
    }

    private IActionResult ToDependencyActionResult(Result result)
    {
        if (result.IsSuccess)
            return Ok(new { status = "OK" });
        return DependencyError(result.Error, result.ErrorDetail);
    }

    private IActionResult DependencyError(
        string? rawError,
        ApplicationErrorDetail? detail)
    {
        var parts = (rawError ?? "TASK_DEPENDENCY_REQUEST_FAILED|The request could not be completed.").Split('|', 2);
        var code = detail?.Code ?? parts[0];
        var message = detail?.Message ?? (parts.Length == 2 ? parts[1] : "The request could not be completed.");
        var status = code switch
        {
            "TASK_DEPENDENCY_AUTHENTICATION_REQUIRED" => StatusCodes.Status401Unauthorized,
            "TASK_DEPENDENCY_FORBIDDEN" => StatusCodes.Status403Forbidden,
            "TASK_DEPENDENCY_NOT_FOUND" => StatusCodes.Status404NotFound,
            "TASK_STALE_VERSION" or "TASK_DEPENDENCY_CONFLICT" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return StructuredCommandError(
            status,
            code,
            message,
            code == "TASK_DEPENDENCY_INVALID_EXPECTED_VERSION" ? "expectedVersion" : "dependency",
            code == "TASK_DEPENDENCY_NOT_FOUND");
    }

    private IActionResult StructuredCommandError(
        int status,
        string code,
        string message,
        string? target,
        bool redactionApplied) =>
        StatusCode(status, new
        {
            requestId = HttpContext.TraceIdentifier,
            error = new
            {
                code,
                message,
                target,
                details = Array.Empty<object>(),
                redactionApplied
            }
        });

    private ErrorResponse ToErrorResponse(string? message) => new("BadRequest", message ?? "The request could not be completed.", HttpContext.TraceIdentifier);
    private static CommentResponse ToLegacyComment(TaskCommentResponse comment) => new(comment.Id, CommentTargetType.TaskItem, comment.TaskId, comment.Author?.UserId ?? Guid.Empty, comment.BodyPlainText ?? string.Empty, comment.CreatedAt, comment.UpdatedAt);
}
