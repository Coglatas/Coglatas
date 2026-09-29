using Coglatas.Application.Audit;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Admin;

public sealed record AdminUserListItemResponse(
    Guid Id,
    string DisplayName,
    string Email,
    SystemRole SystemRole,
    UserStatus Status,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeletedAt);

public sealed record AdminUserDetailResponse(
    Guid Id,
    string DisplayName,
    string Email,
    SystemRole SystemRole,
    UserStatus Status,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeletedAt);

public sealed record UpdateAdminUserRequest(
    string? DisplayName,
    string? Email,
    UserStatus? Status);

public sealed record ChangeSystemRoleRequest(SystemRole SystemRole);

public sealed record RestartTaskDeadlineDigestRequest(string Reason);

public sealed record CreateInviteRequest(
    Guid WorkspaceId,
    string Email,
    WorkspaceRole Role,
    DateTimeOffset? ExpiresAt);

public sealed record BulkCreateInviteRequest(
    Guid WorkspaceId,
    IReadOnlyList<string> Emails,
    WorkspaceRole Role,
    DateTimeOffset? ExpiresAt);

public sealed record AdminInviteResponse(
    Guid Id,
    Guid WorkspaceId,
    string Email,
    WorkspaceRole Role,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt,
    Guid InvitedByUserId,
    DateTimeOffset CreatedAt,
    string? InviteToken = null);

public sealed record SystemSettingResponse(
    Guid Id,
    string Key,
    string? Value,
    string ValueType,
    string? Description,
    bool IsSensitive,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByUserId);

public sealed record UpdateSystemSettingRequest(
    string? Value,
    string? ValueType,
    string? Description,
    bool? IsSensitive);

public sealed record AdminDashboardResponse(
    int UserCount,
    int ActiveUserCount,
    int WorkspaceCount,
    int GroupCount,
    int ProjectCount,
    int OpenTaskCount,
    int OverdueTaskCount,
    long StorageUsageEstimateBytes,
    IReadOnlyList<AuditLogListItemResponse> RecentAuditLogs,
    IReadOnlyList<SecurityEventListItemResponse> RecentSecurityEvents);

public sealed record AdminDashboardSnapshot(
    int UserCount,
    int ActiveUserCount,
    int WorkspaceCount,
    int GroupCount,
    int ProjectCount,
    int OpenTaskCount,
    int OverdueTaskCount,
    long StorageUsageEstimateBytes,
    IReadOnlyList<AuditLogListItemResponse> RecentAuditLogs,
    IReadOnlyList<SecurityEventListItemResponse> RecentSecurityEvents);
