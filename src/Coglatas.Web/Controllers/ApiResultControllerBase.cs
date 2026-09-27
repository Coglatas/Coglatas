using Coglatas.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

public abstract class ApiResultControllerBase : ControllerBase
{
    protected IActionResult OkOrBad(Result result) =>
        result.IsSuccess
            ? Ok(new { status = "OK" })
            : BadRequest(new { error = result.Error });

    protected IActionResult ToActionResult<T>(Result<T> result) =>
        result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
}
