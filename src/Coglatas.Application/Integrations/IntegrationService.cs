using System.Security.Cryptography;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Integrations;

public sealed class IntegrationService(
    IIntegrationRepository integrations,
    ITenantAuthorizationService tenantAuthorization,
    IFeatureFlagService features,
    ICurrentTenant currentTenant,
    ICurrentUser currentUser,
    IClock clock,
    ITokenHasher tokenHasher,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork) : IIntegrationService, IApiTokenValidator
{
    private static readonly string[] SensitiveSettingsKeys = ["password", "secret", "token", "apiKey", "clientSecret", "privateKey"];

    public async Task<Result<IReadOnlyList<IntegrationAccountResponse>>> ListIntegrationsAsync(CancellationToken cancellationToken = default)
    {
        var auth = await RequireTenantAdminAsync(cancellationToken);
        if (!auth.IsSuccess)
        {
            return Result<IReadOnlyList<IntegrationAccountResponse>>.Failure(auth.Error!);
        }

        var items = await integrations.ListIntegrationAccountsAsync(cancellationToken);
        return Result<IReadOnlyList<IntegrationAccountResponse>>.Success(items
            .Where(item => item is { DeletedAt: null, Status: not IntegrationAccountStatus.Deleted })
            .Select(ToIntegrationResponse)
            .ToList());
    }

    public async Task<Result<IntegrationAccountResponse>> CreateIntegrationAsync(CreateIntegrationAccountRequest request, CancellationToken cancellationToken = default)
    {
        var auth = await RequireTenantAdminAsync(cancellationToken);
        if (!auth.IsSuccess || !TryCurrentUser(out var userId))
        {
            return Result<IntegrationAccountResponse>.Failure(auth.Error ?? "Authentication is required.");
        }

        var validation = ValidateIntegrationRequest(request.DisplayName, request.SettingsJson);
        if (!validation.IsSuccess)
        {
            return Result<IntegrationAccountResponse>.Failure(validation.Error!);
        }

        var integration = new IntegrationAccount
        {
            Provider = request.Provider,
            DisplayName = request.DisplayName.Trim(),
            SettingsJson = NormalizeJson(request.SettingsJson, "{}"),
            Status = request.Status,
            CreatedByUserId = userId
        };

        await integrations.AddIntegrationAccountAsync(integration, cancellationToken);
        await auditLogger.LogUserActionAsync(userId, "IntegrationCreated", "IntegrationAccount", integration.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<IntegrationAccountResponse>.Success(ToIntegrationResponse(integration));
    }

    public async Task<Result<IntegrationAccountResponse>> GetIntegrationAsync(Guid integrationId, CancellationToken cancellationToken = default)
    {
        var auth = await RequireTenantAdminAsync(cancellationToken);
        if (!auth.IsSuccess)
        {
            return Result<IntegrationAccountResponse>.Failure("Integration not found.");
        }

        var integration = await integrations.GetIntegrationAccountAsync(integrationId, cancellationToken);
        return integration is null or { DeletedAt: not null } || integration.Status == IntegrationAccountStatus.Deleted
            ? Result<IntegrationAccountResponse>.Failure("Integration not found.")
            : Result<IntegrationAccountResponse>.Success(ToIntegrationResponse(integration));
    }

    public async Task<Result<IntegrationAccountResponse>> UpdateIntegrationAsync(Guid integrationId, UpdateIntegrationAccountRequest request, CancellationToken cancellationToken = default)
    {
        var auth = await RequireTenantAdminAsync(cancellationToken);
        if (!auth.IsSuccess || !TryCurrentUser(out var userId))
        {
            return Result<IntegrationAccountResponse>.Failure(auth.Error ?? "Authentication is required.");
        }

        var integration = await integrations.GetIntegrationAccountAsync(integrationId, cancellationToken);
        if (integration is null or { DeletedAt: not null } || integration.Status == IntegrationAccountStatus.Deleted)
        {
            return Result<IntegrationAccountResponse>.Failure("Integration not found.");
        }

        var validation = ValidateIntegrationRequest(request.DisplayName ?? integration.DisplayName, request.SettingsJson ?? integration.SettingsJson);
        if (!validation.IsSuccess)
        {
            return Result<IntegrationAccountResponse>.Failure(validation.Error!);
        }

        integration.DisplayName = request.DisplayName?.Trim() ?? integration.DisplayName;
        integration.SettingsJson = request.SettingsJson is null ? integration.SettingsJson : NormalizeJson(request.SettingsJson, "{}");
        integration.Status = request.Status ?? integration.Status;
        await auditLogger.LogUserActionAsync(userId, "IntegrationUpdated", "IntegrationAccount", integration.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<IntegrationAccountResponse>.Success(ToIntegrationResponse(integration));
    }

    public async Task<Result> DeleteIntegrationAsync(Guid integrationId, CancellationToken cancellationToken = default)
    {
        var auth = await RequireTenantAdminAsync(cancellationToken);
        if (!auth.IsSuccess || !TryCurrentUser(out var userId))
        {
            return Result.Failure(auth.Error ?? "Authentication is required.");
        }

        var integration = await integrations.GetIntegrationAccountAsync(integrationId, cancellationToken);
        if (integration is null or { DeletedAt: not null })
        {
            return Result.Failure("Integration not found.");
        }

        integration.Status = IntegrationAccountStatus.Deleted;
        integration.MarkDeleted(clock.UtcNow, userId, "Deleted by tenant admin.");
        await auditLogger.LogUserActionAsync(userId, "IntegrationDeleted", "IntegrationAccount", integration.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<WebhookEndpointResponse>>> ListWebhooksAsync(CancellationToken cancellationToken = default)
    {
        var auth = await RequireFeatureAndTenantAdminAsync(FeatureKeys.WebhookIntegration, cancellationToken);
        if (!auth.IsSuccess)
        {
            return Result<IReadOnlyList<WebhookEndpointResponse>>.Failure(auth.Error!);
        }

        var items = await integrations.ListWebhookEndpointsAsync(cancellationToken);
        return Result<IReadOnlyList<WebhookEndpointResponse>>.Success(items
            .Where(item => item is { DeletedAt: null, Status: not WebhookEndpointStatus.Deleted })
            .Select(ToWebhookResponse)
            .ToList());
    }

    public async Task<Result<WebhookEndpointResponse>> CreateWebhookAsync(CreateWebhookEndpointRequest request, CancellationToken cancellationToken = default)
    {
        var auth = await RequireFeatureAndTenantAdminAsync(FeatureKeys.WebhookIntegration, cancellationToken);
        if (!auth.IsSuccess || !TryCurrentUser(out var userId))
        {
            return Result<WebhookEndpointResponse>.Failure(auth.Error ?? "Authentication is required.");
        }

        var validation = ValidateWebhookRequest(request.Name, request.Url, request.EnabledEventsJson);
        if (!validation.IsSuccess)
        {
            return Result<WebhookEndpointResponse>.Failure(validation.Error!);
        }

        var webhook = new WebhookEndpoint
        {
            Name = request.Name.Trim(),
            Url = request.Url.Trim(),
            SecretHash = string.IsNullOrWhiteSpace(request.Secret) ? null : tokenHasher.HashToken(request.Secret),
            EnabledEventsJson = NormalizeJson(request.EnabledEventsJson, "[]"),
            Status = request.Status,
            CreatedByUserId = userId
        };

        await integrations.AddWebhookEndpointAsync(webhook, cancellationToken);
        await auditLogger.LogUserActionAsync(userId, "WebhookCreated", "WebhookEndpoint", webhook.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<WebhookEndpointResponse>.Success(ToWebhookResponse(webhook));
    }

    public async Task<Result<WebhookEndpointResponse>> GetWebhookAsync(Guid webhookId, CancellationToken cancellationToken = default)
    {
        var auth = await RequireFeatureAndTenantAdminAsync(FeatureKeys.WebhookIntegration, cancellationToken);
        if (!auth.IsSuccess)
        {
            return Result<WebhookEndpointResponse>.Failure("Webhook not found.");
        }

        var webhook = await integrations.GetWebhookEndpointAsync(webhookId, cancellationToken);
        return webhook is null or { DeletedAt: not null } || webhook.Status == WebhookEndpointStatus.Deleted
            ? Result<WebhookEndpointResponse>.Failure("Webhook not found.")
            : Result<WebhookEndpointResponse>.Success(ToWebhookResponse(webhook));
    }

    public async Task<Result<WebhookEndpointResponse>> UpdateWebhookAsync(Guid webhookId, UpdateWebhookEndpointRequest request, CancellationToken cancellationToken = default)
    {
        var auth = await RequireFeatureAndTenantAdminAsync(FeatureKeys.WebhookIntegration, cancellationToken);
        if (!auth.IsSuccess || !TryCurrentUser(out var userId))
        {
            return Result<WebhookEndpointResponse>.Failure(auth.Error ?? "Authentication is required.");
        }

        var webhook = await integrations.GetWebhookEndpointAsync(webhookId, cancellationToken);
        if (webhook is null or { DeletedAt: not null } || webhook.Status == WebhookEndpointStatus.Deleted)
        {
            return Result<WebhookEndpointResponse>.Failure("Webhook not found.");
        }

        var validation = ValidateWebhookRequest(request.Name ?? webhook.Name, request.Url ?? webhook.Url, request.EnabledEventsJson ?? webhook.EnabledEventsJson);
        if (!validation.IsSuccess)
        {
            return Result<WebhookEndpointResponse>.Failure(validation.Error!);
        }

        webhook.Name = request.Name?.Trim() ?? webhook.Name;
        webhook.Url = request.Url?.Trim() ?? webhook.Url;
        webhook.EnabledEventsJson = request.EnabledEventsJson is null ? webhook.EnabledEventsJson : NormalizeJson(request.EnabledEventsJson, "[]");
        webhook.Status = request.Status ?? webhook.Status;
        if (!string.IsNullOrWhiteSpace(request.Secret))
        {
            webhook.SecretHash = tokenHasher.HashToken(request.Secret);
        }

        await auditLogger.LogUserActionAsync(userId, "WebhookUpdated", "WebhookEndpoint", webhook.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<WebhookEndpointResponse>.Success(ToWebhookResponse(webhook));
    }

    public async Task<Result> DeleteWebhookAsync(Guid webhookId, CancellationToken cancellationToken = default)
    {
        var auth = await RequireFeatureAndTenantAdminAsync(FeatureKeys.WebhookIntegration, cancellationToken);
        if (!auth.IsSuccess || !TryCurrentUser(out var userId))
        {
            return Result.Failure(auth.Error ?? "Authentication is required.");
        }

        var webhook = await integrations.GetWebhookEndpointAsync(webhookId, cancellationToken);
        if (webhook is null or { DeletedAt: not null })
        {
            return Result.Failure("Webhook not found.");
        }

        webhook.Status = WebhookEndpointStatus.Deleted;
        webhook.MarkDeleted(clock.UtcNow, userId, "Deleted by tenant admin.");
        await auditLogger.LogUserActionAsync(userId, "WebhookDeleted", "WebhookEndpoint", webhook.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<WebhookEndpointResponse>> ValidateWebhookAsync(Guid webhookId, CancellationToken cancellationToken = default)
    {
        var result = await GetWebhookAsync(webhookId, cancellationToken);
        if (!result.IsSuccess || !TryCurrentUser(out var userId))
        {
            return result;
        }

        await auditLogger.LogUserActionAsync(userId, "WebhookTestValidated", "WebhookEndpoint", webhookId, "Webhook URL and configuration validated; no outbound request was sent.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<Result<IReadOnlyList<ApiTokenResponse>>> ListApiTokensAsync(CancellationToken cancellationToken = default)
    {
        var auth = await RequireFeatureAndTenantAdminAsync(FeatureKeys.ApiAccess, cancellationToken);
        if (!auth.IsSuccess)
        {
            return Result<IReadOnlyList<ApiTokenResponse>>.Failure(auth.Error!);
        }

        var tokens = await integrations.ListApiTokensAsync(cancellationToken);
        return Result<IReadOnlyList<ApiTokenResponse>>.Success(tokens.Select(ToTokenResponse).ToList());
    }

    public async Task<Result<CreateApiTokenResponse>> CreateApiTokenAsync(CreateApiTokenRequest request, CancellationToken cancellationToken = default)
    {
        var auth = await RequireFeatureAndTenantAdminAsync(FeatureKeys.ApiAccess, cancellationToken);
        if (!auth.IsSuccess || !TryCurrentUser(out var userId))
        {
            return Result<CreateApiTokenResponse>.Failure(auth.Error ?? "Authentication is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160)
        {
            return Result<CreateApiTokenResponse>.Failure("API token name must be 1 to 160 characters.");
        }

        string scopesJson;
        try
        {
            scopesJson = NormalizeJson(request.ScopesJson, "[]");
            using var document = JsonDocument.Parse(scopesJson);
            if (scopesJson.Length > 4000 || document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                return Result<CreateApiTokenResponse>.Failure("API token scopes must be a JSON array of strings (at most 4000 characters).");
            }
        }
        catch (JsonException)
        {
            return Result<CreateApiTokenResponse>.Failure("API token scopes must be a JSON array of strings (at most 4000 characters).");
        }
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= clock.UtcNow)
        {
            return Result<CreateApiTokenResponse>.Failure("API token expiry must be in the future.");
        }

        var rawToken = GenerateRawToken();
        var token = new ApiToken
        {
            Name = request.Name.Trim(),
            TokenHash = tokenHasher.HashToken(rawToken),
            ScopesJson = scopesJson,
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            CreatedByUserId = userId
        };

        await integrations.AddApiTokenAsync(token, cancellationToken);
        await auditLogger.LogUserActionAsync(userId, "ApiTokenCreated", "ApiToken", token.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<CreateApiTokenResponse>.Success(new CreateApiTokenResponse(ToTokenResponse(token), rawToken));
    }

    public async Task<Result> RevokeApiTokenAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var auth = await RequireFeatureAndTenantAdminAsync(FeatureKeys.ApiAccess, cancellationToken);
        if (!auth.IsSuccess || !TryCurrentUser(out var userId))
        {
            return Result.Failure(auth.Error ?? "Authentication is required.");
        }

        var token = await integrations.GetApiTokenAsync(tokenId, cancellationToken);
        if (token is null)
        {
            return Result.Failure("API token not found.");
        }

        token.RevokedAt ??= clock.UtcNow;
        await auditLogger.LogUserActionAsync(userId, "ApiTokenRevoked", "ApiToken", token.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<ApiTokenValidationResult> ValidateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            await AuditTokenFailureAsync("Missing API token.", cancellationToken);
            return new ApiTokenValidationResult(false, FailureReason: "Missing API token.");
        }

        var hash = tokenHasher.HashToken(rawToken);
        var token = await integrations.GetApiTokenByHashAsync(hash, cancellationToken);
        if (token is null)
        {
            await AuditTokenFailureAsync("API token hash was not found.", cancellationToken);
            return new ApiTokenValidationResult(false, FailureReason: "Invalid API token.");
        }

        if (token.RevokedAt.HasValue)
        {
            await AuditTokenFailureAsync("Revoked API token used.", cancellationToken);
            return new ApiTokenValidationResult(false, token.TenantId, token.CreatedByUserId, token.Id, FailureReason: "API token is revoked.");
        }

        if (token.ExpiresAt.HasValue && token.ExpiresAt.Value <= clock.UtcNow)
        {
            await AuditTokenFailureAsync("Expired API token used.", cancellationToken);
            return new ApiTokenValidationResult(false, token.TenantId, token.CreatedByUserId, token.Id, FailureReason: "API token is expired.");
        }

        token.LastUsedAt = clock.UtcNow;
        await auditLogger.LogUserActionAsync(token.CreatedByUserId, "ApiTokenUsed", "ApiToken", token.Id, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ApiTokenValidationResult(true, token.TenantId, token.CreatedByUserId, token.Id, ParseStringArray(token.ScopesJson));
    }

    private async Task<Result> RequireFeatureAndTenantAdminAsync(string featureKey, CancellationToken cancellationToken)
    {
        var feature = await features.RequireEnabledAsync(featureKey, cancellationToken);
        if (!feature.IsSuccess)
        {
            return feature;
        }

        return await RequireTenantAdminAsync(cancellationToken);
    }

    private async Task<Result> RequireTenantAdminAsync(CancellationToken cancellationToken)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Result.Failure("Authentication is required.");
        }

        if (!currentTenant.IsAvailable)
        {
            return Result.Failure("Tenant scope is required.");
        }

        if (await tenantAuthorization.IsPlatformAdminAsync(userId, cancellationToken))
        {
            return Result.Success();
        }

        return await tenantAuthorization.CanManageTenantAsync(userId, currentTenant.TenantId, cancellationToken)
            ? Result.Success()
            : Result.Failure("Tenant admin permission is required.");
    }

    private Result ValidateIntegrationRequest(string displayName, string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Result.Failure("Integration display name is required.");
        }

        try
        {
            var json = NormalizeJson(settingsJson, "{}");
            using var document = JsonDocument.Parse(json);
            if (ContainsSensitiveKey(document.RootElement))
            {
                return Result.Failure("Integration settings must not contain raw secrets, tokens, passwords, or API keys.");
            }
        }
        catch (JsonException)
        {
            return Result.Failure("Integration settings must contain valid JSON.");
        }

        return Result.Success();
    }

    private static Result ValidateWebhookRequest(string name, string url, string? enabledEventsJson)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure("Webhook name is required.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Result.Failure("Webhook URL must be absolute.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("Webhook URL must use HTTPS.");
        }

        try
        {
            _ = NormalizeJson(enabledEventsJson, "[]");
        }
        catch (JsonException)
        {
            return Result.Failure("Webhook events must contain valid JSON.");
        }

        return Result.Success();
    }

    private static string NormalizeJson(string? json, string defaultJson)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return defaultJson;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetRawText();
    }

    private static bool ContainsSensitiveKey(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (SensitiveSettingsKeys.Any(key => property.Name.Contains(key, StringComparison.OrdinalIgnoreCase)) ||
                    ContainsSensitiveKey(property.Value))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsSensitiveKey(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task AuditTokenFailureAsync(string summary, CancellationToken cancellationToken)
    {
        await auditLogger.LogSecurityAsync("AccessDenied", summary, cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private bool TryCurrentUser(out Guid userId)
    {
        userId = currentUser.UserId ?? Guid.Empty;
        return currentUser is { IsAuthenticated: true, UserId: not null };
    }

    private static string GenerateRawToken()
    {
        return $"coglatas_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "-").Replace("/", "_").TrimEnd('=')}";
    }

    private static string[] ParseStringArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IntegrationAccountResponse ToIntegrationResponse(IntegrationAccount integration)
    {
        return new IntegrationAccountResponse(
            integration.Id,
            integration.Provider,
            integration.DisplayName,
            integration.Status,
            integration.SettingsJson,
            integration.CreatedByUserId,
            integration.CreatedAt,
            integration.UpdatedAt);
    }

    private static WebhookEndpointResponse ToWebhookResponse(WebhookEndpoint webhook)
    {
        return new WebhookEndpointResponse(
            webhook.Id,
            webhook.Name,
            webhook.Url,
            webhook.EnabledEventsJson,
            webhook.Status,
            webhook.CreatedByUserId,
            webhook.CreatedAt,
            webhook.UpdatedAt);
    }

    private static ApiTokenResponse ToTokenResponse(ApiToken token)
    {
        return new ApiTokenResponse(
            token.Id,
            token.Name,
            token.ScopesJson,
            token.ExpiresAt,
            token.CreatedByUserId,
            token.CreatedAt,
            token.LastUsedAt,
            token.RevokedAt);
    }
}
