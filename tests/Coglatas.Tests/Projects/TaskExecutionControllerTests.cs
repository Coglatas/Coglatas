using System.Reflection;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Tests.Projects;

public sealed class TaskExecutionControllerTests
{
    [Fact]
    [Trait("Scope", "Issue461")]
    public async Task RequestRunReturnsCreatedForTheDurablyAcceptedRuntimeContract()
    {
        var run = new TaskExecutionRunResponse(
            Guid.NewGuid(),
            TaskExecutionRunStatus.Accepted,
            null,
            new DateTimeOffset(2026, 8, 25, 8, 30, 0, TimeSpan.Zero),
            null,
            TaskExecutionRun.SnapshotSchemaVersion1,
            TaskExecutionScopeOrigin.ProjectDefault,
            1,
            null,
            false,
            false);
        var service = new StubTaskExecutionScopeService
        {
            RunResult = Result<TaskExecutionRunResponse>.Success(run)
        };
        var controller = Controller(service);

        var action = await controller.RequestRun(
            Guid.NewGuid(),
            new RequestTaskExecutionRunRequest(),
            "controller-run-0001",
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status201Created, response.StatusCode);
        var body = Assert.IsType<TaskExecutionRunResponse>(response.Value);
        Assert.Equal(run.Id, body.Id);
        Assert.Equal(TaskExecutionRunStatus.Accepted, body.Status);
        Assert.Equal(TaskExecutionMajorState.Accepted, body.MajorState);
        Assert.Null(body.FailureCode);
        Assert.Equal(TaskExecutionProvider.FirstPartyProjectFilesRuntimeV1, body.RuntimeProvider);
        Assert.Equal(TaskExecutionRun.RuntimeContractVersion1, body.RuntimeContractVersion);
    }

    [Theory]
    [Trait("Scope", "Issue461")]
    [InlineData(TaskExecutionRunStatus.Accepted, TaskExecutionMajorState.Accepted)]
    [InlineData(TaskExecutionRunStatus.Queued, TaskExecutionMajorState.Queued)]
    [InlineData(TaskExecutionRunStatus.Running, TaskExecutionMajorState.Running)]
    [InlineData(TaskExecutionRunStatus.Failed, TaskExecutionMajorState.Failed)]
    [InlineData(TaskExecutionRunStatus.Succeeded, TaskExecutionMajorState.Succeeded)]
    public void ExecutionRunProjectsStableMajorState(
        TaskExecutionRunStatus status,
        TaskExecutionMajorState expected)
    {
        var run = new TaskExecutionRunResponse(
            Guid.NewGuid(),
            status,
            status is TaskExecutionRunStatus.Failed ? "EXECUTION_FAILED" : null,
            new DateTimeOffset(2026, 8, 29, 8, 30, 0, TimeSpan.Zero),
            status is TaskExecutionRunStatus.Succeeded or TaskExecutionRunStatus.Failed
                ? new DateTimeOffset(2026, 8, 29, 8, 31, 0, TimeSpan.Zero)
                : null,
            TaskExecutionRun.SnapshotSchemaVersion1,
            TaskExecutionScopeOrigin.ProjectDefault,
            1,
            null,
            false,
            false);

        Assert.Equal(expected, run.MajorState);
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task RequestRunMapsUnavailableResourcesToTheSafeRedactedNotFoundEnvelope()
    {
        var service = new StubTaskExecutionScopeService
        {
            RunResult = Failure<TaskExecutionRunResponse>("TASK_EXECUTION_NOT_FOUND")
        };
        var controller = Controller(service);

        var action = await controller.RequestRun(
            Guid.NewGuid(),
            new RequestTaskExecutionRunRequest(),
            "controller-run-0002",
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        var payload = JsonSerializer.Serialize(response.Value);
        Assert.Contains("TASK_EXECUTION_NOT_FOUND", payload, StringComparison.Ordinal);
        Assert.Contains("\"redactionApplied\":true", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("taskItemId", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Scope", "Issue361")]
    public async Task ProjectScopeFailsClosedWhenProjectFileRuleAuthorizationCannotBeRevalidated()
    {
        var fileId = Guid.NewGuid();
        var policy = new TaskExecutionSourcePolicyV2(
            TaskExecutionSourcePolicyV2.CurrentSchemaVersion,
            TaskExecutionSourceState.Exclude,
            TaskExecutionSourceState.Exclude,
            TaskExecutionSourceState.Exclude,
            TaskExecutionSourceState.Exclude,
            [new TaskExecutionSourceRule(
                TaskExecutionSourceKind.ProjectFile,
                TaskExecutionSourcePolicyV2.ProjectFileSourceId(fileId),
                TaskExecutionSourceState.Allow)]);
        var service = new StubTaskExecutionScopeService
        {
            ProjectResult = Result<ProjectExecutionScopeResponse>.Success(new ProjectExecutionScopeResponse(
                new TaskExecutionSourcePolicyResponse(
                    WebEnabled: false,
                    ProjectFilesEnabled: true,
                    PolicyV2: policy),
                Version: 3,
                CanManage: true))
        };
        var controller = Controller(service);

        var action = await controller.GetProjectScope(Guid.NewGuid(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var body = Assert.IsType<ProjectExecutionScopeResponse>(ok.Value);
        Assert.False(body.CanManage);
        Assert.NotNull(body.Policy.PolicyV2);
        Assert.Empty(body.Policy.PolicyV2!.Items);
        Assert.False(body.Policy.ProjectFilesEnabled);
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task TaskOverrideConflictMapsToConflictAndTheEndpointRequiresAuthentication()
    {
        var service = new StubTaskExecutionScopeService
        {
            UpdateOverrideResult = Failure<TaskExecutionScopeResponse>("TASK_EXECUTION_STALE_VERSION")
        };
        var controller = Controller(service);
        var action = await controller.UpdateTaskOverride(
            Guid.NewGuid(),
            new UpdateTaskExecutionScopeOverrideRequest(true, false, ExpectedVersion: 1),
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        var authorize = typeof(TaskExecutionController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        var route = typeof(TaskExecutionController)
            .GetMethod(nameof(TaskExecutionController.RequestRun))!
            .GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(route);
        Assert.Equal("api/tasks/{taskItemId:guid}/execution-runs", route.Template);
    }

    private static TaskExecutionController Controller(ITaskExecutionScopeService service) => new(service)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                TraceIdentifier = "issue357-controller-test"
            }
        }
    };

    private static Result<T> Failure<T>(string code) =>
        Result<T>.Failure(new ApplicationErrorDetail(code, "The request could not be completed."));

    private sealed class StubTaskExecutionScopeService : ITaskExecutionScopeService
    {
        public Result<ProjectExecutionScopeResponse> ProjectResult { get; init; } = Failure<ProjectExecutionScopeResponse>("TASK_EXECUTION_NOT_FOUND");
        private Result<TaskExecutionScopeResponse> TaskResult { get; } = Failure<TaskExecutionScopeResponse>("TASK_EXECUTION_NOT_FOUND");
        public Result<TaskExecutionRunResponse> RunResult { get; init; } = Failure<TaskExecutionRunResponse>("TASK_EXECUTION_NOT_FOUND");
        public Result<TaskExecutionScopeResponse> UpdateOverrideResult { get; init; } = Failure<TaskExecutionScopeResponse>("TASK_EXECUTION_NOT_FOUND");

        public Task<Result<ProjectExecutionScopeResponse>> GetProjectScopeAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectResult);

        public Task<Result<ProjectExecutionScopeResponse>> UpdateProjectScopeAsync(Guid projectId, UpdateProjectExecutionScopeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectResult);

        public Task<Result<TaskExecutionScopeResponse>> GetTaskScopeAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TaskResult);

        public Task<Result<TaskExecutionScopeResponse>> UpdateTaskOverrideAsync(Guid taskItemId, UpdateTaskExecutionScopeOverrideRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(UpdateOverrideResult);

        public Task<Result<TaskExecutionScopeResponse>> ClearTaskOverrideAsync(Guid taskItemId, ClearTaskExecutionScopeOverrideRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(TaskResult);

        public Task<Result<TaskExecutionRunResponse>> RequestRunAsync(Guid taskItemId, string? idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(RunResult);
    }
}