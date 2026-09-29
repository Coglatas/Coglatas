using System.Reflection;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Application.Security.Redaction;
using Coglatas.Domain.Enums;
using Coglatas.Web.Controllers;
using Coglatas.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.Projects;

[Trait("Scope", "Issue410")]
public sealed class CanonicalTaskCreateContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RequestRejectsUnknownMembersAndRequiresCompleteOverridePolicy()
    {
        const string unknownMember = """
        {
          "title": "Task",
          "sourceScopeMode": "Inherit",
          "projectId": "d67ce480-1276-4ec5-a18c-a8b2aa3e4c03"
        }
        """;
        const string partialOverride = """
        {
          "title": "Task",
          "sourceScopeMode": "TaskOverride",
          "taskOverridePolicy": { "webEnabled": true }
        }
        """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CanonicalCreateTaskRequest>(unknownMember, WebJson));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CanonicalCreateTaskRequest>(partialOverride, WebJson));
    }

    [Fact]
    public void RoutesAndCanonicalEnvelopeClassifierArePinned()
    {
        var projectId = Guid.NewGuid();
        var controllerType = typeof(ProjectTaskCreateController);
        Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>());

        var options = controllerType.GetMethod(nameof(ProjectTaskCreateController.GetCreateOptions));
        Assert.NotNull(options);
        Assert.Equal(
            "api/projects/{projectId:guid}/tasks/create-options",
            Assert.Single(options.GetCustomAttributes<HttpGetAttribute>()).Template);

        var create = controllerType.GetMethod(nameof(ProjectTaskCreateController.Create));
        Assert.NotNull(create);
        Assert.Equal(
            "api/projects/{projectId:guid}/tasks/create",
            Assert.Single(create.GetCustomAttributes<HttpPostAttribute>()).Template);
        var idempotencyKey = Assert.Single(create.GetParameters(), parameter => parameter.Name == "idempotencyKey");
        Assert.Equal(
            "Idempotency-Key",
            Assert.Single(idempotencyKey.GetCustomAttributes<FromHeaderAttribute>()).Name);

        Assert.True(ApiEnvelope.IsCanonicalTaskCreatePath($"/api/projects/{projectId}/tasks/create"));
        Assert.True(ApiEnvelope.IsCanonicalTaskCreatePath($"/api/projects/{projectId}/tasks/create-options/"));
        Assert.True(ApiEnvelope.IsCanonicalCreatePath($"/api/projects/{projectId}/tasks/create"));
        Assert.False(ApiEnvelope.IsCanonicalTaskCreatePath($"/api/projects/{projectId}/tasks"));
        Assert.False(ApiEnvelope.IsCanonicalTaskCreatePath("/api/projects/not-a-guid/tasks/create"));
    }

    [Fact]
    public async Task ControllerReturnsCanonicalCreatedEnvelopeAndForwardsIdempotencyKey()
    {
        var response = new CanonicalTaskCreateResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            "Created Task",
            TaskPriority.Medium,
            TaskItemStatus.NotStarted,
            Guid.NewGuid(),
            1,
            TaskCreateSourceScopeMode.Inherit,
            null);
        var service = new StubService
        {
            CreateResult = Result<CanonicalTaskCreateResponse>.Success(response)
        };
        var controller = Controller(service);

        var action = await controller.Create(
            response.ProjectId,
            new CanonicalCreateTaskRequest("Created Task", SourceScopeMode: TaskCreateSourceScopeMode.Inherit),
            "task-create-http-001",
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        Assert.Equal($"/api/tasks/{response.TaskId}", created.Location);
        var envelope = Assert.IsType<ApiSuccessEnvelope<object>>(created.Value);
        Assert.Equal("issue410-task-create", envelope.RequestId);
        Assert.Equal(response, Assert.IsType<CanonicalTaskCreateResponse>(envelope.Data));
        Assert.Equal("task-create-http-001", service.IdempotencyKey);
        Assert.Empty(envelope.Warnings);
    }

    [Fact]
    public async Task ControllerMapsCrossScopeNotFoundAndIdempotencyMismatchToSafeCanonicalErrors()
    {
        var request = new CanonicalCreateTaskRequest("Task", SourceScopeMode: TaskCreateSourceScopeMode.Inherit);
        var hiddenService = new StubService
        {
            CreateResult = Failure<CanonicalTaskCreateResponse>("NotFound", "The requested resource was not found.")
        };
        var hidden = await Controller(hiddenService).Create(
            Guid.NewGuid(), request, "task-create-http-002", CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(hidden);
        var notFoundEnvelope = Assert.IsType<ApiErrorEnvelope>(notFound.Value);
        Assert.Equal(StatusCodes.Status404NotFound, notFoundEnvelope.Status);
        Assert.Equal("NotFound", notFoundEnvelope.Error.Code);
        Assert.Null(notFoundEnvelope.Error.Target);
        Assert.True(notFoundEnvelope.Error.RedactionApplied);

        var conflictService = new StubService
        {
            CreateResult = Failure<CanonicalTaskCreateResponse>(
                "IdempotencyConflict",
                "The Idempotency-Key was already used with a different Task request.",
                "header.Idempotency-Key")
        };
        var conflict = await Controller(conflictService).Create(
            Guid.NewGuid(), request, "task-create-http-002", CancellationToken.None);

        var conflictResult = Assert.IsType<ConflictObjectResult>(conflict);
        var conflictEnvelope = Assert.IsType<ApiErrorEnvelope>(conflictResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, conflictEnvelope.Status);
        Assert.Equal("IdempotencyConflict", conflictEnvelope.Error.Code);
        Assert.Equal("header.Idempotency-Key", conflictEnvelope.Error.Target);
    }

    private static ProjectTaskCreateController Controller(ICanonicalTaskCreateService service)
    {
        var tenant = new CurrentTenantService();
        tenant.SetTenant(Guid.NewGuid(), "issue410-task-create");
        var services = new ServiceCollection()
            .AddSingleton<IRedactionService, CanonicalRedactionService>()
            .AddSingleton<ICurrentUser>(new TestCurrentUser(Guid.NewGuid()))
            .AddSingleton<ICurrentTenant>(tenant)
            .BuildServiceProvider();

        return new ProjectTaskCreateController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    TraceIdentifier = "issue410-task-create",
                    RequestServices = services
                }
            }
        };
    }

    private static Result<T> Failure<T>(string code, string message, string? target = null) =>
        Result<T>.Failure(new ApplicationErrorDetail(code, message, Target: target));

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class StubService : ICanonicalTaskCreateService
    {
        private Result<TaskCreateOptionsResponse> OptionsResult { get; set; } = Failure<TaskCreateOptionsResponse>("NotFound", "The requested resource was not found.");
        public Result<CanonicalTaskCreateResponse> CreateResult { get; set; } = Failure<CanonicalTaskCreateResponse>("NotFound", "The requested resource was not found.");
        public string? IdempotencyKey { get; private set; }

        public Task<Result<TaskCreateOptionsResponse>> GetCreateOptionsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OptionsResult);

        public Task<Result<CanonicalTaskCreateResponse>> CreateAsync(
            Guid projectId,
            CanonicalCreateTaskRequest request,
            string? clientRequestIdentity,
            CancellationToken cancellationToken = default)
        {
            IdempotencyKey = clientRequestIdentity;
            return Task.FromResult(CreateResult);
        }
    }
}
