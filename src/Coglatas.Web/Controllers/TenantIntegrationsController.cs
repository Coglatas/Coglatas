using Coglatas.Application.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class TenantIntegrationsController(IIntegrationService integrations) : ApiResultControllerBase
{
    [HttpGet("api/tenant/integrations")]
    public async Task<IActionResult> ListIntegrations(CancellationToken cancellationToken) =>
        ToActionResult(await integrations.ListIntegrationsAsync(cancellationToken));

    [HttpPost("api/tenant/integrations")]
    public async Task<IActionResult> CreateIntegration(CreateIntegrationAccountRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await integrations.CreateIntegrationAsync(request, cancellationToken));

    [HttpGet("api/tenant/integrations/{integrationId:guid}")]
    public async Task<IActionResult> GetIntegration(Guid integrationId, CancellationToken cancellationToken) =>
        ToActionResult(await integrations.GetIntegrationAsync(integrationId, cancellationToken));

    [HttpPatch("api/tenant/integrations/{integrationId:guid}")]
    public async Task<IActionResult> UpdateIntegration(Guid integrationId, UpdateIntegrationAccountRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await integrations.UpdateIntegrationAsync(integrationId, request, cancellationToken));

    [HttpDelete("api/tenant/integrations/{integrationId:guid}")]
    public async Task<IActionResult> DeleteIntegration(Guid integrationId, CancellationToken cancellationToken) =>
        OkOrBad(await integrations.DeleteIntegrationAsync(integrationId, cancellationToken));

    [HttpGet("api/tenant/webhooks")]
    public async Task<IActionResult> ListWebhooks(CancellationToken cancellationToken) =>
        ToActionResult(await integrations.ListWebhooksAsync(cancellationToken));

    [HttpPost("api/tenant/webhooks")]
    public async Task<IActionResult> CreateWebhook(CreateWebhookEndpointRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await integrations.CreateWebhookAsync(request, cancellationToken));

    [HttpGet("api/tenant/webhooks/{webhookId:guid}")]
    public async Task<IActionResult> GetWebhook(Guid webhookId, CancellationToken cancellationToken) =>
        ToActionResult(await integrations.GetWebhookAsync(webhookId, cancellationToken));

    [HttpPatch("api/tenant/webhooks/{webhookId:guid}")]
    public async Task<IActionResult> UpdateWebhook(Guid webhookId, UpdateWebhookEndpointRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await integrations.UpdateWebhookAsync(webhookId, request, cancellationToken));

    [HttpDelete("api/tenant/webhooks/{webhookId:guid}")]
    public async Task<IActionResult> DeleteWebhook(Guid webhookId, CancellationToken cancellationToken) =>
        OkOrBad(await integrations.DeleteWebhookAsync(webhookId, cancellationToken));

    [HttpPost("api/tenant/webhooks/{webhookId:guid}/test")]
    public async Task<IActionResult> TestWebhook(Guid webhookId, CancellationToken cancellationToken) =>
        ToActionResult(await integrations.ValidateWebhookAsync(webhookId, cancellationToken));

    [HttpGet("api/tenant/api-tokens")]
    public async Task<IActionResult> ListApiTokens(CancellationToken cancellationToken) =>
        ToActionResult(await integrations.ListApiTokensAsync(cancellationToken));

    [HttpPost("api/tenant/api-tokens")]
    [EnableRateLimiting("api-token")]
    public async Task<IActionResult> CreateApiToken(CreateApiTokenRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await integrations.CreateApiTokenAsync(request, cancellationToken));

    [HttpPost("api/tenant/api-tokens/{tokenId:guid}/revoke")]
    public async Task<IActionResult> RevokeApiToken(Guid tokenId, CancellationToken cancellationToken) =>
        OkOrBad(await integrations.RevokeApiTokenAsync(tokenId, cancellationToken));

}
