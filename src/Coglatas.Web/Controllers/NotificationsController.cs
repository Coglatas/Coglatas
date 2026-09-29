using Coglatas.Application.Notifications;
using Coglatas.Application.Security.Redaction;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class NotificationsController(INotificationApplicationService notifications) : ControllerBase
{
    [HttpGet("api/notifications")]
    public async Task<IActionResult> List([FromQuery] NotificationListQuery query, CancellationToken cancellationToken)
    {
        return ToActionResult(
            await notifications.ListAsync(query, cancellationToken),
            "Notifications");
    }

    [HttpGet("api/notifications/unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken)
    {
        return ToActionResult(
            await notifications.GetUnreadCountAsync(cancellationToken),
            "NotificationUnreadCount");
    }

    [HttpPatch("api/notifications/{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        var result = await notifications.MarkAsReadAsync(notificationId, cancellationToken);
        return result.IsSuccess
            ? Ok(new { status = "OK" })
            : BadRequest(CanonicalErrorEnvelope.FromSensitiveResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "NotificationUpdateFailed"));
    }

    [HttpPost("api/notifications/{notificationId:guid}/open")]
    public async Task<IActionResult> Open(Guid notificationId, CancellationToken cancellationToken)
    {
        var result = await notifications.OpenAsync(notificationId, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.NotificationPayload,
                "NotificationOpen",
                RedactionAuthorizationState.Allowed));
        }

        // A missing notification and a notification owned by another recipient
        // are deliberately collapsed to the same already-public-safe sentinel.
        // Do not fabricate an Allowed authorization state merely to pass that
        // sentinel through the canonical projection helper.
        return NotFound(new NotificationOpenResponse("Unavailable", null, 0));
    }

    [HttpPatch("api/notifications/read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var result = await notifications.MarkAllAsReadAsync(cancellationToken);
        return result.IsSuccess
            ? Ok(new { status = "OK" })
            : BadRequest(CanonicalErrorEnvelope.FromSensitiveResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "NotificationUpdateFailed"));
    }

    [HttpDelete("api/notifications/{notificationId:guid}")]
    public async Task<IActionResult> Delete(Guid notificationId, CancellationToken cancellationToken)
    {
        var result = await notifications.DeleteAsync(notificationId, cancellationToken);
        return result.IsSuccess
            ? Ok(new { status = "OK" })
            : BadRequest(CanonicalErrorEnvelope.FromSensitiveResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "NotificationUpdateFailed"));
    }

    private IActionResult ToActionResult<T>(
        Coglatas.Application.Common.Result<T> result,
        string moduleKey)
    {
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.NotificationPayload,
                moduleKey,
                RedactionAuthorizationState.Allowed))
            : BadRequest(CanonicalErrorEnvelope.FromSensitiveResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "NotificationQueryFailed"));
    }
}
