using System.ComponentModel.DataAnnotations;
using Coglatas.Application.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Route("api/invites")]
public sealed class InvitesController(IAuthService authService) : ControllerBase
{
    [HttpGet("validate")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Validate(
        [FromQuery, Required, RegularExpression("^[a-f0-9]{64}$")] string token,
        CancellationToken cancellationToken)
    {
        var result = await authService.ValidateInviteAsync(token, cancellationToken);
        return result.IsSuccess
            ? Ok(result.Value)
            : InviteProblem(result.Error, "Invite validation failed.");
    }

    [HttpPost("accept")]
    [EnableRateLimiting("invite")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoginResponse>> Accept(AcceptInviteRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.AcceptInviteAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return InviteProblem(result.Error, "Invite acceptance failed.");
        }

        await AuthenticationCookieSignIn.SignInAsync(HttpContext, result.Value);
        return Ok(result.Value);
    }

    private ObjectResult InviteProblem(string? detail, string title)
    {
        return Problem(
            title: title,
            detail: string.IsNullOrWhiteSpace(detail) ? "Invite is invalid." : detail,
            statusCode: StatusCodes.Status404NotFound);
    }
}
