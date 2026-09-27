using Coglatas.Domain.Enums;

namespace Coglatas.Application.Integrations;

public sealed record IntegrationAccountResponse(
    Guid Id,
    IntegrationProvider Provider,
    string DisplayName,
    IntegrationAccountStatus Status,
    string SettingsJson,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CreateIntegrationAccountRequest(
    [property: System.Text.Json.Serialization.JsonRequired] IntegrationProvider Provider,
    string DisplayName,
    [property: System.Text.Json.Serialization.JsonRequired] string? SettingsJson,
    IntegrationAccountStatus Status = IntegrationAccountStatus.Draft);

public sealed record UpdateIntegrationAccountRequest(
    string? DisplayName,
    string? SettingsJson,
    IntegrationAccountStatus? Status);

public sealed record WebhookEndpointResponse(
    Guid Id,
    string Name,
    string Url,
    string EnabledEventsJson,
    WebhookEndpointStatus Status,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CreateWebhookEndpointRequest(
    string Name,
    string Url,
    string? Secret,
    string? EnabledEventsJson,
    WebhookEndpointStatus Status = WebhookEndpointStatus.Active);

public sealed record UpdateWebhookEndpointRequest(
    string? Name,
    string? Url,
    string? Secret,
    string? EnabledEventsJson,
    WebhookEndpointStatus? Status);

public sealed record ApiTokenResponse(
    Guid Id,
    string Name,
    string ScopesJson,
    DateTimeOffset? ExpiresAt,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record CreateApiTokenRequest(string Name, string? ScopesJson, DateTimeOffset? ExpiresAt);

public sealed record CreateApiTokenResponse(ApiTokenResponse Token, string RawToken);

public sealed record ApiTokenValidationResult(
    bool IsValid,
    Guid? TenantId = null,
    Guid? CreatedByUserId = null,
    Guid? TokenId = null,
    string[]? Scopes = null,
    string? FailureReason = null);
