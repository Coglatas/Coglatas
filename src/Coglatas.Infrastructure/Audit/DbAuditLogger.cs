using System.Diagnostics;
using System.Text.Json;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;

namespace Coglatas.Infrastructure.Audit;

public sealed class DbAuditLogger(AppDbContext dbContext, IClock clock, ICurrentUser currentUser, ICurrentTenant currentTenant) : IAuditLogger
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "token",
        "grantToken",
        "secret",
        "signedUrl",
        "storageKey",
        "objectStoragePath",
        "rawFilePath",
        "filePath",
        "messageBody",
        "commentBody",
        "bodyPlainText",
        "body",
        "reviewReason",
        "reviewReturnReason",
        "blockedReason",
        "watchState",
        "watchStates",
        "taskNotificationPreference",
        "preferenceValue",
        "deadlineDigestLocalTime",
        "effectiveDeadlineDigestLocalTime",
        "taskTitle",
        "restrictedTitle",
        "displayName",
        "taskDisplayName",
        "attachmentContent",
        "license",
        "licenseKey",
        "licenseMaterial",
        "cookie",
        "connectionString",
        "environmentVariable"
    };

    public async Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = entry.TenantId ?? (currentTenant.IsAvailable ? currentTenant.TenantId : null);
            if (!tenantId.HasValue)
            {
                return;
            }

            await dbContext.AuditLogs.AddAsync(new AuditLog
            {
                TenantId = tenantId.Value,
                ActorUserId = entry.ActorUserId ?? currentUser.UserId,
                Action = entry.Action,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                WorkspaceId = entry.WorkspaceId,
                GroupId = entry.GroupId,
                ProjectId = entry.ProjectId,
                IpAddress = entry.IpAddress,
                UserAgent = entry.UserAgent,
                Summary = entry.Summary,
                MetadataJson = SerializeMetadata(entry.Metadata),
                CorrelationId = entry.CorrelationId ?? Activity.Current?.TraceId.ToString(),
                CreatedAt = clock.UtcNow
            }, cancellationToken);
        }
        catch
        {
            if (entry.Action.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
                entry.EntityType.Equals("SecurityEvent", StringComparison.OrdinalIgnoreCase) ||
                entry.Action is "WorkspaceCreated" or
                    "ProjectCreated" or
                    "ProjectActivated" or
                    "ProjectVisibilityChanged" or
                    "ProjectMemberAdded" or
                    "ProjectMemberUpdated" or
                    "ProjectMemberRemoved" or
                    "ProjectExecutionScopeChanged" or
                    "TaskExecutionScopeOverrideSet" or
                    "TaskExecutionScopeOverrideCleared" or
                    "TaskExecutionRunRequested" or
                    "TaskExecutionRunQueued" or
                    "TaskExecutionRunStarted" or
                    "TaskExecutionRunSucceeded" or
                    "TaskExecutionRunFailed" or
                    "TaskExecutionRunStopped" or
                    "TaskExecutionRunRedirected")
            {
                throw;
            }
        }
    }

    public Task LogAsync(
        string action,
        string entityType,
        Guid? entityId,
        string? summary = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(new AuditLogEntry(currentUser.UserId, action, entityType, entityId, summary, Metadata: metadata), cancellationToken);
    }

    public async Task LogSecurityAsync(
        string action,
        string summary,
        IReadOnlyDictionary<string, object?>? metadata = null,
        SecurityEventSeverity severity = SecurityEventSeverity.Info,
        CancellationToken cancellationToken = default)
    {
        var tenantId = currentTenant.IsAvailable ? currentTenant.TenantId : (Guid?)null;
        if (!tenantId.HasValue)
        {
            return;
        }

        var parsed = Enum.TryParse<SecurityEventType>(action, ignoreCase: true, out var eventType)
            ? eventType
            : SecurityEventType.AccessDenied;

        await dbContext.SecurityEvents.AddAsync(new SecurityEvent
        {
            TenantId = tenantId.Value,
            EventType = parsed,
            UserId = currentUser.UserId,
            Email = currentUser.Email,
            Severity = severity,
            Summary = summary,
            MetadataJson = SerializeMetadata(metadata),
            CreatedAt = clock.UtcNow
        }, cancellationToken);

        await LogAsync(new AuditLogEntry(currentUser.UserId, action, "SecurityEvent", null, summary, Metadata: metadata), cancellationToken);
    }

    public Task LogUserActionAsync(
        Guid actorUserId,
        string action,
        string entityType,
        Guid? entityId,
        string? summary = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(new AuditLogEntry(actorUserId, action, entityType, entityId, summary, Metadata: metadata), cancellationToken);
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var sanitized = metadata
            .Where(pair => !SensitiveKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return sanitized.Count == 0 ? null : JsonSerializer.Serialize(sanitized);
    }
}
