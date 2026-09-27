using Coglatas.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class MessageFollowUpsController(IMessageFollowUpService followUps) : ApiResultControllerBase
{
    [HttpGet("api/me/message-follow-ups")]
    public async Task<IActionResult> List(
        [FromQuery] MessageFollowUpListQuery query,
        CancellationToken cancellationToken) =>
        ToActionResult(await followUps.ListAsync(query, cancellationToken));

    [HttpPut("api/me/message-follow-ups/{messageId:guid}")]
    public async Task<IActionResult> Save(Guid messageId, CancellationToken cancellationToken) =>
        ToActionResult(await followUps.SaveAsync(messageId, cancellationToken));

    [HttpDelete("api/me/message-follow-ups/{messageId:guid}")]
    public async Task<IActionResult> Remove(Guid messageId, CancellationToken cancellationToken) =>
        ToActionResult(await followUps.RemoveAsync(messageId, cancellationToken));

}
