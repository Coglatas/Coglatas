using System.Reflection;
using Coglatas.Application.Common;
using Coglatas.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Tests.Projects;

public sealed class ProjectKanbanControllerTests
{
    [Theory]
    [InlineData("KANBAN_NOT_FOUND", StatusCodes.Status404NotFound)]
    [InlineData("KANBAN_FORBIDDEN", StatusCodes.Status403Forbidden)]
    [InlineData("KANBAN_STALE_BOARD", StatusCodes.Status409Conflict)]
    [InlineData("KANBAN_CONFLICT", StatusCodes.Status409Conflict)]
    [InlineData("KANBAN_INVALID_POSITION", StatusCodes.Status422UnprocessableEntity)]
    [InlineData("TASK_BLOCK_REASON_REQUIRED", StatusCodes.Status422UnprocessableEntity)]
    public void ErrorsUseTheCanonicalPrivacyAndConflictStatus(string code, int expectedStatus)
    {
        var controller = new ProjectKanbanController(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var method = typeof(ProjectKanbanController)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate is { Name: "ToActionResult", IsGenericMethod: true })
            .MakeGenericMethod(typeof(string));

        var action = Assert.IsAssignableFrom<IActionResult>(
            method.Invoke(controller, [Result<string>.Failure($"{code}|Kanban request failed.")]));

        Assert.Equal(expectedStatus, Assert.IsType<ObjectResult>(action).StatusCode);
    }
}
