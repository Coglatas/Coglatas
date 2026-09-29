using Coglatas.Application.Communication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class CommunicationPollingController(ICommunicationPollingService polling) : ControllerBase
{
    [HttpGet("api/communication/poll/unread-counts")]
    public async Task<IActionResult> UnreadCounts([FromQuery] CommunicationPollingQuery query, CancellationToken cancellationToken)
    {
        return ToActionResult(await polling.GetUnreadCountsAsync(query, cancellationToken));
    }

    [HttpGet("api/communication/poll/notifications")]
    public async Task<IActionResult> Notifications([FromQuery] CommunicationPollingQuery query, CancellationToken cancellationToken)
    {
        return ToActionResult(await polling.GetNotificationsAsync(query, cancellationToken));
    }

    [HttpGet("api/communication/poll/updates")]
    public async Task<IActionResult> Updates([FromQuery] CommunicationUpdatesPollingQuery query, CancellationToken cancellationToken)
    {
        return ToActionResult(await polling.GetUpdatesAsync(query, cancellationToken));
    }

    private IActionResult ToActionResult<T>(Coglatas.Application.Common.Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return string.Equals(result.Error, "Polling rate limit exceeded.", StringComparison.Ordinal)
            ? StatusCode(StatusCodes.Status429TooManyRequests, new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }
}
