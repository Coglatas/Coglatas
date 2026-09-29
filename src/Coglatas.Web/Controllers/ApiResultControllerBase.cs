using Coglatas.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

public abstract class ApiResultControllerBase : ControllerBase
{
    protected IActionResult OkOrBad(Result result) =>
        result.IsSuccess
            ? Ok(new { status = "OK" })
            : ToFailureActionResult(result.Error, result.ErrorDetail);

    protected IActionResult ToActionResult<T>(Result<T> result) =>
        result.IsSuccess
            ? Ok(result.Value)
            : ToFailureActionResult(result.Error, result.ErrorDetail);

    private IActionResult ToFailureActionResult(string? error, ApplicationErrorDetail? detail)
    {
        var statusCode = detail?.Code switch
        {
            "AuthenticationRequired" => StatusCodes.Status401Unauthorized,
            "Forbidden" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new
        {
            error = string.IsNullOrWhiteSpace(error)
                ? detail?.Message ?? "Request failed."
                : error
        });
    }
}
