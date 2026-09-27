using System.Text.Json;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

internal static class AuthorizationTargetShared
{
    internal static async Task<bool> HasCurrentTenantUserAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Users.AsNoTracking().AnyAsync(user =>
            user.Id == userId &&
            user.DeletedAt == null &&
            user.Status == UserStatus.Active,
            cancellationToken) &&
            await dbContext.Tenants.AsNoTracking().AnyAsync(tenant =>
                tenant.Id == tenantId &&
                tenant.DeletedAt == null &&
                tenant.Status == TenantStatus.Active,
                cancellationToken) &&
            await dbContext.TenantUsers.AsNoTracking().AnyAsync(member =>
                member.TenantId == tenantId &&
                member.UserId == userId &&
                member.Status == TenantUserStatus.Active,
                cancellationToken);
    }

    /// <summary>
    /// Notification-created signals for protected targets carry identity and
    /// ordering metadata only. Reject widened payloads so replay cannot expose
    /// target titles, routes, or relationship state.
    /// </summary>
    internal static bool IsReferenceOnlyNotificationCreatedPayload(
        JsonElement payload,
        Guid notificationId,
        long? aggregateVersion)
    {
        if (payload.ValueKind != JsonValueKind.Object || aggregateVersion is not > 0)
        {
            return false;
        }

        var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "notificationId",
            "stateVersion",
            "requiresRefetch"
        };
        var propertyCount = 0;
        foreach (var property in payload.EnumerateObject())
        {
            if (!expectedProperties.Contains(property.Name))
            {
                return false;
            }

            propertyCount++;
        }

        return propertyCount == expectedProperties.Count &&
            TryGetGuid(payload, "notificationId", out var payloadNotificationId) &&
            payloadNotificationId == notificationId &&
            TryGetLong(payload, "stateVersion", out var stateVersion) &&
            stateVersion == aggregateVersion &&
            payload.TryGetProperty("requiresRefetch", out var requiresRefetch) &&
            requiresRefetch.ValueKind == JsonValueKind.True;
    }

    internal static bool TryGetGuid(JsonElement payload, string name, out Guid value)
    {
        value = Guid.Empty;
        return payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out value);
    }

    private static bool TryGetLong(JsonElement payload, string name, out long value)
    {
        value = 0;
        return payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value);
    }

    internal static bool TryGetNullableGuid(JsonElement payload, string name, out Guid? value)
    {
        value = null;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(property.GetString(), out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    internal static string? GetString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
