using Coglatas.Application.Common;
using Coglatas.Application.Planning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class PlanningController(IPlanningService planning) : ApiResultControllerBase
{
    [AllowAnonymous]
    [HttpGet("api/projects/{projectId:guid}/gantt")]
    public async Task<IActionResult> Gantt(Guid projectId, CancellationToken cancellationToken) =>
        ToGanttActionResult(await planning.GetGanttAsync(projectId, cancellationToken));

    [HttpGet("api/projects/{projectId:guid}/dashboard")]
    public async Task<IActionResult> Dashboard(Guid projectId, CancellationToken cancellationToken) => ToActionResult(await planning.GetDashboardAsync(projectId, cancellationToken));

    [HttpGet("api/me/tasks")]
    public async Task<IActionResult> MyTasks(
        [FromQuery] MyTasksQuery query,
        CancellationToken cancellationToken = default)
    {
        return ToMyTasksActionResult(await planning.ListMyTasksAsync(query, cancellationToken));
    }

    [HttpGet("api/me/tasks/counts")]
    public async Task<IActionResult> MyTaskCounts([FromQuery] MyTasksQuery query, CancellationToken cancellationToken = default) =>
        ToMyTasksActionResult(await planning.GetMyTaskCountsAsync(query, cancellationToken));

    [HttpGet("api/projects/{projectId:guid}/workload")]
    public async Task<IActionResult> Workload(Guid projectId, CancellationToken cancellationToken) => ToActionResult(await planning.GetWorkloadAsync(projectId, cancellationToken));

    private IActionResult ToGanttActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var detail = result.ErrorDetail;
        var code = detail?.Code ?? "GANTT_REQUEST_FAILED";
        var message = detail?.Message ?? "The request could not be completed.";
        var status = code switch
        {
            "GANTT_AUTHENTICATION_REQUIRED" => StatusCodes.Status401Unauthorized,
            "GANTT_FORBIDDEN" => StatusCodes.Status403Forbidden,
            "GANTT_PROJECT_NOT_FOUND" => StatusCodes.Status404NotFound,
            "GANTT_STALE_VERSION" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        var redactionApplied = code == "GANTT_PROJECT_NOT_FOUND";
        return StatusCode(status, new
        {
            requestId = HttpContext.TraceIdentifier,
            error = new
            {
                code,
                message,
                target = code.Contains("LIMIT", StringComparison.Ordinal) ? "projectId" : null,
                details = Array.Empty<object>(),
                redactionApplied
            }
        });
    }

    private IActionResult ToMyTasksActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var detail = result.ErrorDetail;
        var code = detail?.Code ?? "MY_TASKS_REQUEST_FAILED";
        var message = detail?.Message ?? "The request could not be completed.";
        var status = code switch
        {
            "MY_TASKS_AUTHENTICATION_REQUIRED" => StatusCodes.Status401Unauthorized,
            "MY_TASKS_WORKSPACE_FORBIDDEN" => StatusCodes.Status403Forbidden,
            "MY_TASKS_PROJECT_NOT_FOUND" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(status, new
        {
            requestId = HttpContext.TraceIdentifier,
            error = new
            {
                code,
                message,
                target = (string?)null,
                details = Array.Empty<object>(),
                redactionApplied = false
            }
        });
    }
}
