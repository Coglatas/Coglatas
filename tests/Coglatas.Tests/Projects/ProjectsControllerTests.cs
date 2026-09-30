using System.Reflection;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Tests.Projects;

public sealed class ProjectsControllerTests
{
    [Fact]
    public void GenericTaskConflictMapsToHttp409()
    {
        var action = InvokeGenericTaskResult(Result<string>.Failure("TASK_CONFLICT|Conflict"));

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(action).StatusCode);
    }

    [Fact]
    public void NonGenericTaskConflictMapsToHttp409()
    {
        var action = InvokeTaskResult(Result.Failure("TASK_CONFLICT|Conflict"));

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(action).StatusCode);
    }

    [Theory]
    [InlineData("goal", "Goal")]
    [InlineData("deliverable", "Deliverable")]
    [InlineData("constraints", "Constraints")]
    public void TaskBriefValidationMapsToFieldSpecificCanonicalTargets(string target, string label)
    {
        var detail = new ApplicationErrorDetail(
            "TASK_BRIEF_FIELD_TOO_LONG",
            $"Task brief {label} must be 4000 characters or fewer.",
            Target: target);

        var taskAction = Assert.IsType<ObjectResult>(InvokeGenericTaskResult(Result<string>.Failure(detail)));
        var controller = Controller();
        var createMethod = typeof(ProjectsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate is { Name: "ToActionResult", IsGenericMethod: true })
            .MakeGenericMethod(typeof(string));
        var createAction = Assert.IsType<ObjectResult>(createMethod.Invoke(controller, [Result<string>.Failure(detail)]));

        foreach (var action in new[] { taskAction, createAction })
        {
            Assert.Equal(StatusCodes.Status400BadRequest, action.StatusCode);
            using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(action.Value));
            Assert.Equal("TASK_BRIEF_FIELD_TOO_LONG", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(target, envelope.RootElement.GetProperty("error").GetProperty("target").GetString());
            Assert.Contains(label, envelope.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public void ProjectRevisionConflictMapsToSafeHttp409Envelope()
    {
        var detail = new ApplicationErrorDetail(
            "PROJECT_CONFLICT",
            "Project state has changed. Refetch and retry.");
        var controller = Controller();
        var genericMethod = typeof(ProjectsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate is { Name: "ToActionResult", IsGenericMethod: true })
            .MakeGenericMethod(typeof(string));
        var genericAction = Assert.IsType<ObjectResult>(
            genericMethod.Invoke(controller, [Result<string>.Failure(detail)]));
        var nonGenericMethod = typeof(ProjectsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "OkOrBad");
        var nonGenericAction = Assert.IsType<ObjectResult>(
            nonGenericMethod.Invoke(controller, [Result.Failure(detail)]));

        Assert.Equal(StatusCodes.Status409Conflict, genericAction.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, nonGenericAction.StatusCode);
        using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(genericAction.Value));
        Assert.Equal(
            "PROJECT_CONFLICT",
            envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "project",
            envelope.RootElement.GetProperty("error").GetProperty("target").GetString());
    }

    [Fact]
    public void ExplicitActivationRequirementMapsToSafeHttp409Envelope()
    {
        var detail = new ApplicationErrorDetail(
            "InvalidStateTransition",
            "A Planning Project must use the explicit activation command before it can become Active.");
        var controller = Controller();
        var genericMethod = typeof(ProjectsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate is { Name: "ToActionResult", IsGenericMethod: true })
            .MakeGenericMethod(typeof(string));

        var action = Assert.IsType<ObjectResult>(
            genericMethod.Invoke(controller, [Result<string>.Failure(detail)]));

        Assert.Equal(StatusCodes.Status409Conflict, action.StatusCode);
        using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(action.Value));
        Assert.Equal(
            "InvalidStateTransition",
            envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "body.status",
            envelope.RootElement.GetProperty("error").GetProperty("target").GetString());
        Assert.Equal(StatusCodes.Status409Conflict, envelope.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(envelope.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public void AmbiguousRestoreMapsToSafeProjectTargetedHttp409Envelope()
    {
        var detail = new ApplicationErrorDetail(
            "InvalidStateTransition",
            "The Project cannot be restored because its prior lifecycle state is unavailable.",
            Target: "project");
        var controller = Controller();
        var method = typeof(ProjectsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "OkOrBad");

        var action = Assert.IsType<ObjectResult>(method.Invoke(controller, [Result.Failure(detail)]));

        Assert.Equal(StatusCodes.Status409Conflict, action.StatusCode);
        using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(action.Value));
        Assert.Equal("InvalidStateTransition", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("project", envelope.RootElement.GetProperty("error").GetProperty("target").GetString());
        Assert.Equal(StatusCodes.Status409Conflict, envelope.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public void HiddenProjectMapsToRedactedNotFoundEnvelope()
    {
        var controller = Controller();
        var genericMethod = typeof(ProjectsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate is { Name: "ToActionResult", IsGenericMethod: true })
            .MakeGenericMethod(typeof(string));
        var action = Assert.IsType<ObjectResult>(genericMethod.Invoke(
            controller,
            [Result<string>.Failure(new ApplicationErrorDetail("NotFound", "The requested resource was not found."))]));

        Assert.Equal(StatusCodes.Status404NotFound, action.StatusCode);
        using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(action.Value));
        Assert.Equal("NotFound", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(envelope.RootElement.GetProperty("error").GetProperty("redactionApplied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("error").GetProperty("target").ValueKind);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public void MilestoneRevisionConflictMapsToSafeHttp409Envelope()
    {
        var detail = new ApplicationErrorDetail(
            "MILESTONE_STALE_VERSION",
            "Milestone has changed. Refetch and retry.");
        var controller = Controller();
        var genericMethod = typeof(ProjectsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate is { Name: "ToActionResult", IsGenericMethod: true })
            .MakeGenericMethod(typeof(string));

        var action = Assert.IsType<ObjectResult>(
            genericMethod.Invoke(controller, [Result<string>.Failure(detail)]));

        Assert.Equal(StatusCodes.Status409Conflict, action.StatusCode);
        using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(action.Value));
        Assert.Equal(
            "MILESTONE_STALE_VERSION",
            envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "milestone",
            envelope.RootElement.GetProperty("error").GetProperty("target").GetString());
        Assert.False(
            envelope.RootElement.GetProperty("error").GetProperty("redactionApplied").GetBoolean());
    }

    [Theory]
    [InlineData(false, false, WorkItemWatchAutomaticSource.None, false)]
    [InlineData(false, false, WorkItemWatchAutomaticSource.Collaborator, true)]
    [InlineData(false, true, WorkItemWatchAutomaticSource.Collaborator, false)]
    [InlineData(true, true, WorkItemWatchAutomaticSource.None, true)]
    public void WatchStateNormalizationUsesManualIntentAndOptOut(bool manual, bool optOut, WorkItemWatchAutomaticSource sources, bool expected)
    {
        Assert.Equal(expected, TaskWatchStateRules.IsWatching(manual, optOut, sources));
    }

    private static IActionResult InvokeTaskResult(Result result)
    {
        var controller = Controller();
        var method = typeof(ProjectsController).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate is { Name: "ToTaskActionResult", IsGenericMethod: false } && candidate.GetParameters()[0].ParameterType == typeof(Result));
        return Assert.IsAssignableFrom<IActionResult>(method.Invoke(controller, [result]));
    }

    private static IActionResult InvokeGenericTaskResult(Result<string> result)
    {
        var controller = Controller();
        var method = typeof(ProjectsController).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate is { Name: "ToTaskActionResult", IsGenericMethod: true })
            .MakeGenericMethod(typeof(string));
        return Assert.IsAssignableFrom<IActionResult>(method.Invoke(controller, [result]));
    }

    private static ProjectsController Controller() => new(null!, null!, null!)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };
}
