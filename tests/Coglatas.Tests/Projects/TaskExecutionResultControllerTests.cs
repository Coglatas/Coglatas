using System.Reflection;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Projects;
using Coglatas.Domain.Enums;
using Coglatas.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Tests.Projects;

public sealed class TaskExecutionResultControllerTests
{
    [Fact]
    [Trait("Scope", "Issue463")]
    public async Task LatestResultEndpointReturnsTheAuthorizedDurableReport()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var resultService = new StubResultService
        {
            Latest = Result<TaskExecutionResultResponse>.Success(new TaskExecutionResultResponse(
                runId,
                TaskExecutionRunStatus.Succeeded,
                null,
                new DateTimeOffset(2026, 8, 30, 22, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 30, 22, 0, 1, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 30, 22, 0, 2, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 30, 22, 0, 3, TimeSpan.Zero),
                new TaskExecutionReportResponse(
                    Guid.NewGuid(),
                    1,
                    "Project Files Analysis Report",
                    "# Authorized report\n",
                    new string('a', 64),
                    new DateTimeOffset(2026, 8, 30, 22, 0, 3, TimeSpan.Zero))))
        };
        var controller = Controller(resultService);

        var action = await controller.GetLatestResult(taskId, CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(action);
        var payload = Assert.IsType<TaskExecutionResultResponse>(response.Value);
        Assert.Equal(runId, payload.RunId);
        Assert.NotNull(payload.Report);
        Assert.Equal("Project Files Analysis Report", payload.Report!.Title);
        Assert.Equal(taskId, resultService.LastTaskId);
    }

    [Fact]
    [Trait("Scope", "Issue463")]
    public async Task RevokedResultMapsToTheCanonicalRedactedNotFoundEnvelope()
    {
        var resultService = new StubResultService
        {
            Latest = Result<TaskExecutionResultResponse>.Failure(new ApplicationErrorDetail(
                "TASK_EXECUTION_RESULT_NOT_FOUND",
                "The execution result was not found."))
        };
        var controller = Controller(resultService);

        var action = await controller.GetLatestResult(Guid.NewGuid(), CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        var json = JsonSerializer.Serialize(response.Value);
        Assert.Contains("TASK_EXECUTION_RESULT_NOT_FOUND", json, StringComparison.Ordinal);
        Assert.Contains("\"redactionApplied\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("source", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Scope", "Issue463")]
    public void ResultRoutesUseTaskAndRunServerIdentitiesOnly()
    {
        var latest = typeof(TaskExecutionController)
            .GetMethod(nameof(TaskExecutionController.GetLatestResult))!
            .GetCustomAttribute<HttpGetAttribute>();
        var specific = typeof(TaskExecutionController)
            .GetMethod(nameof(TaskExecutionController.GetResult))!
            .GetCustomAttribute<HttpGetAttribute>();

        Assert.Equal("api/tasks/{taskItemId:guid}/execution-result", latest!.Template);
        Assert.Equal("api/tasks/{taskItemId:guid}/execution-runs/{runId:guid}/result", specific!.Template);
    }

    private static TaskExecutionController Controller(ITaskExecutionResultService results) =>
        new(new StubScopeService(), executionResults: results)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    TraceIdentifier = "issue463-controller-test"
                }
            }
        };

    private sealed class StubResultService : ITaskExecutionResultService
    {
        public Result<TaskExecutionResultResponse> Latest { get; set; } =
            Result<TaskExecutionResultResponse>.Failure(new ApplicationErrorDetail(
                "TASK_EXECUTION_RESULT_NOT_FOUND",
                "The execution result was not found."));

        public Guid LastTaskId { get; private set; }

        public Task<Result<TaskExecutionResultResponse>> GetLatestAsync(
            Guid taskItemId,
            CancellationToken cancellationToken = default)
        {
            LastTaskId = taskItemId;
            return Task.FromResult(Latest);
        }

        public Task<Result<TaskExecutionResultResponse>> GetAsync(
            Guid taskItemId,
            Guid runId,
            CancellationToken cancellationToken = default)
        {
            LastTaskId = taskItemId;
            return Task.FromResult(Latest);
        }
    }

    private sealed class StubScopeService : ITaskExecutionScopeService
    {
        private static readonly ApplicationErrorDetail Missing = new(
            "TASK_EXECUTION_NOT_FOUND",
            "The execution request was not found.");

        public Task<Result<ProjectExecutionScopeResponse>> GetProjectScopeAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ProjectExecutionScopeResponse>.Failure(Missing));

        public Task<Result<ProjectExecutionScopeResponse>> UpdateProjectScopeAsync(Guid projectId, UpdateProjectExecutionScopeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ProjectExecutionScopeResponse>.Failure(Missing));

        public Task<Result<TaskExecutionScopeResponse>> GetTaskScopeAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<TaskExecutionScopeResponse>.Failure(Missing));

        public Task<Result<TaskExecutionScopeResponse>> UpdateTaskOverrideAsync(Guid taskItemId, UpdateTaskExecutionScopeOverrideRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<TaskExecutionScopeResponse>.Failure(Missing));

        public Task<Result<TaskExecutionScopeResponse>> ClearTaskOverrideAsync(Guid taskItemId, ClearTaskExecutionScopeOverrideRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<TaskExecutionScopeResponse>.Failure(Missing));

        public Task<Result<TaskExecutionRunResponse>> RequestRunAsync(Guid taskItemId, string? idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<TaskExecutionRunResponse>.Failure(Missing));
    }
}
