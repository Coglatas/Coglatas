using Coglatas.Application.UiShell;
using Coglatas.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class UiShellController(IUiShellService uiShell) : ApiResultControllerBase
{
    [HttpGet("api/ui/modules")]
    public async Task<IActionResult> Modules(CancellationToken cancellationToken) => ToActionResult(await uiShell.ListModulesAsync(cancellationToken));

    [HttpGet("api/ui/panels")]
    public async Task<IActionResult> Panels([FromQuery] string? moduleKey, CancellationToken cancellationToken) => ToActionResult(await uiShell.ListPanelsAsync(moduleKey, cancellationToken));

    [HttpGet("api/ui/layouts")]
    public async Task<IActionResult> Layouts(CancellationToken cancellationToken) => ToActionResult(await uiShell.ListLayoutsAsync(cancellationToken));

    [HttpGet("api/ui/layouts/current")]
    public async Task<IActionResult> CurrentLayout([FromQuery] LayoutScopeType scopeType, [FromQuery] Guid? scopeId, CancellationToken cancellationToken)
    {
        return ToActionResult(await uiShell.GetCurrentLayoutAsync(scopeType, scopeId, cancellationToken));
    }

    [HttpPost("api/ui/layouts")]
    public async Task<IActionResult> CreateLayout(SaveUserLayoutRequest request, CancellationToken cancellationToken) => ToActionResult(await uiShell.CreateLayoutAsync(request, cancellationToken));

    [HttpPut("api/ui/layouts/{layoutId:guid}")]
    public async Task<IActionResult> UpdateLayout(Guid layoutId, SaveUserLayoutRequest request, CancellationToken cancellationToken) => ToActionResult(await uiShell.UpdateLayoutAsync(layoutId, request, cancellationToken));

    [HttpDelete("api/ui/layouts/{layoutId:guid}")]
    public async Task<IActionResult> DeleteLayout(Guid layoutId, CancellationToken cancellationToken) => OkOrBad(await uiShell.DeleteLayoutAsync(layoutId, cancellationToken));

    [HttpGet("api/ui/commands")]
    public async Task<IActionResult> Commands([FromQuery] CommandContextType? contextType, [FromQuery] Guid? contextId, CancellationToken cancellationToken)
    {
        return ToActionResult(await uiShell.ListCommandsAsync(contextType, contextId, cancellationToken));
    }

    [HttpGet("api/ui/radial-menu")]
    public async Task<IActionResult> RadialMenu([FromQuery] CommandContextType contextType, [FromQuery] Guid? contextId, CancellationToken cancellationToken)
    {
        return ToActionResult(await uiShell.GetRadialMenuAsync(contextType, contextId, cancellationToken));
    }

}
