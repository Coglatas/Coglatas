using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class DbAuditQueryService(
    AppDbContext dbContext,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    ITenantRepository tenantRepository,
    IAuditAuthorizationService? auditAuthorization = null) : IAuditQueryService
{
    private const int MaxPageSize = 100;
    private const int MaxSearchLength = 200;
    private const int MaxActorFilterLength = 200;
    private const int MaxActionLength = 160;
    private const int MaxEntityTypeLength = 80;

    private readonly IAuditAuthorizationService _auditAuthorization =
        auditAuthorization ?? new LegacyAuditAuthorizationService(
            currentUser,
            currentTenant,
            tenantRepository);

    public async Task<Result<PagedResponse<AuditLogListItemResponse>>> ListAuditLogsAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default)
    {
        var (capabilities, viewDenied) = await GetViewCapabilitiesAsync("audit.logs.list", cancellationToken);
        if (viewDenied is not null)
        {
            return AuthorizationFailure<PagedResponse<AuditLogListItemResponse>>(viewDenied);
        }

        if (query.ActorUserId.HasValue && !capabilities.CanViewSensitiveMetadata)
        {
            var denied = await _auditAuthorization.AuthorizeAsync(
                CapabilityKeys.AuditSensitiveMetadataView,
                "audit.logs.filter.actor",
                cancellationToken);
            return AuthorizationFailure<PagedResponse<AuditLogListItemResponse>>(denied);
        }

        var scopeError = ValidateQueryScope<PagedResponse<AuditLogListItemResponse>>();
        if (scopeError is not null)
        {
            return scopeError;
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var source = ScopeToCurrentTenant(dbContext.AuditLogs.AsNoTracking());

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            source = source.Where(log => log.Action == query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            source = source.Where(log => log.EntityType == query.EntityType);
        }

        if (query.ActorUserId.HasValue)
        {
            source = source.Where(log => log.ActorUserId == query.ActorUserId);
        }

        if (query.WorkspaceId.HasValue)
        {
            source = source.Where(log => log.WorkspaceId == query.WorkspaceId);
        }

        if (query.GroupId.HasValue)
        {
            source = source.Where(log => log.GroupId == query.GroupId);
        }

        if (query.ProjectId.HasValue)
        {
            source = source.Where(log => log.ProjectId == query.ProjectId);
        }

        if (query.FromDate.HasValue)
        {
            var fromDateUtc = NormalizePostgresTimestamp(query.FromDate.Value);
            source = source.Where(log => log.CreatedAt >= fromDateUtc);
        }

        if (query.ToDate.HasValue)
        {
            var toDateUtc = NormalizePostgresTimestamp(query.ToDate.Value);
            source = source.Where(log => log.CreatedAt <= toDateUtc);
        }

        var total = await source.CountAsync(cancellationToken);
        var canViewSensitiveMetadata = capabilities.CanViewSensitiveMetadata;
        var items = await source
            .OrderByDescending(log => log.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(log => new AuditLogListItemResponse(
                log.Id,
                canViewSensitiveMetadata ? log.ActorUserId : null,
                canViewSensitiveMetadata
                    ? (log.ActorUser == null ? null : log.ActorUser.DisplayName)
                    : null,
                log.Action,
                log.EntityType,
                log.EntityId,
                log.WorkspaceId,
                log.GroupId,
                log.ProjectId,
                log.Summary,
                canViewSensitiveMetadata ? log.MetadataJson : null,
                canViewSensitiveMetadata ? log.CorrelationId : null,
                log.CreatedAt))
            .ToListAsync(cancellationToken);

        return Result<PagedResponse<AuditLogListItemResponse>>.Success(
            new PagedResponse<AuditLogListItemResponse>(items, page, pageSize, total));
    }

    public async Task<Result<PagedResponse<AuditGridRowResponse>>> ListAuditGridAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default)
    {
        var (capabilities, viewDenied) = await GetViewCapabilitiesAsync("audit.grid.list", cancellationToken);
        if (viewDenied is not null)
        {
            return AuthorizationFailure<PagedResponse<AuditGridRowResponse>>(viewDenied);
        }

        if ((query.ActorUserId.HasValue || !string.IsNullOrWhiteSpace(query.Actor)) &&
            !capabilities.CanViewSensitiveMetadata)
        {
            var denied = await _auditAuthorization.AuthorizeAsync(
                CapabilityKeys.AuditSensitiveMetadataView,
                "audit.grid.filter.actor",
                cancellationToken);
            return AuthorizationFailure<PagedResponse<AuditGridRowResponse>>(denied);
        }

        var scopeError = ValidateQueryScope<PagedResponse<AuditGridRowResponse>>();
        if (scopeError is not null)
        {
            return scopeError;
        }

        var normalizedQuery = NormalizeAuditGridQuery(query);
        if (!normalizedQuery.IsSuccess)
        {
            return Result<PagedResponse<AuditGridRowResponse>>.Failure(
                normalizedQuery.ErrorDetail ?? new ApplicationErrorDetail(
                    "AuditFilterInvalid",
                    "The audit filter is invalid."));
        }
        query = normalizedQuery.Value!;

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var source = ScopeToCurrentTenant(dbContext.AuditLogs.AsNoTracking());

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.ToLowerInvariant();
            source = source.Where(log => log.Action.ToLower() == action);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            var entityType = query.EntityType.ToLowerInvariant();
            source = source.Where(log => log.EntityType.ToLower() == entityType);
        }

        if (query.ActorUserId.HasValue)
        {
            source = source.Where(log => log.ActorUserId == query.ActorUserId);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var actor = query.Actor.ToLowerInvariant();
            source = source.Where(log =>
                log.ActorUser != null && log.ActorUser.DisplayName.ToLower().Contains(actor));
        }

        if (query.WorkspaceId.HasValue)
        {
            source = source.Where(log => log.WorkspaceId == query.WorkspaceId);
        }

        if (query.GroupId.HasValue)
        {
            source = source.Where(log => log.GroupId == query.GroupId);
        }

        if (query.ProjectId.HasValue)
        {
            source = source.Where(log => log.ProjectId == query.ProjectId);
        }

        if (query.FromDate.HasValue)
        {
            var fromDateUtc = NormalizePostgresTimestamp(query.FromDate.Value);
            source = source.Where(log => log.CreatedAt >= fromDateUtc);
        }

        if (query.ToDate.HasValue)
        {
            var toDateUtc = NormalizePostgresTimestamp(query.ToDate.Value);
            source = source.Where(log => log.CreatedAt <= toDateUtc);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var search = query.Q.ToLowerInvariant();
            var canSearchActor = capabilities.CanViewSensitiveMetadata;
            source = source.Where(log =>
                log.Action.ToLower().Contains(search) ||
                log.EntityType.ToLower().Contains(search) ||
                (log.Summary != null && log.Summary.ToLower().Contains(search)) ||
                (log.Workspace != null && log.Workspace.Name.ToLower().Contains(search)) ||
                (canSearchActor && log.ActorUser != null && log.ActorUser.DisplayName.ToLower().Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(query.Result))
        {
            source = ApplyResultFilter(source, query.Result);
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            source = ApplySeverityFilter(source, query.Severity);
        }

        var total = await source.CountAsync(cancellationToken);
        var records = await source
            .OrderByDescending(log => log.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(log => new AuditGridProjection(
                log.Id,
                log.CreatedAt,
                log.Action,
                log.ActorUser == null ? null : log.ActorUser.DisplayName,
                log.EntityType,
                log.Workspace == null ? null : log.Workspace.Name,
                log.WorkspaceId,
                log.Summary,
                log.CorrelationId))
            .ToListAsync(cancellationToken);

        var canViewSensitiveMetadata = capabilities.CanViewSensitiveMetadata;
        var items = records
            .Select(log => ToAuditGridRow(log, canViewSensitiveMetadata))
            .ToList();

        return Result<PagedResponse<AuditGridRowResponse>>.Success(
            new PagedResponse<AuditGridRowResponse>(items, page, pageSize, total));
    }

    public async Task<Result<AuditGridRowResponse>> GetAuditGridRowAsync(
        Guid auditId,
        CancellationToken cancellationToken = default)
    {
        var (capabilities, viewDenied) = await GetViewCapabilitiesAsync("audit.grid.row.read", cancellationToken);
        if (viewDenied is not null)
        {
            return AuthorizationFailure<AuditGridRowResponse>(viewDenied);
        }

        var scopeError = ValidateQueryScope<AuditGridRowResponse>();
        if (scopeError is not null)
        {
            return scopeError;
        }

        // Scope before ID comparison so an ID from another Tenant is
        // indistinguishable from an absent record. Guid.Empty is handled by
        // the same generic result after authorization, which also keeps an
        // invalid URL marker from receiving a different resource signal.
        var record = auditId == Guid.Empty
            ? null
            : await ScopeToCurrentTenant(dbContext.AuditLogs.AsNoTracking())
                .Where(log => log.Id == auditId)
                .Select(log => new AuditGridProjection(
                    log.Id,
                    log.CreatedAt,
                    log.Action,
                    log.ActorUser == null ? null : log.ActorUser.DisplayName,
                    log.EntityType,
                    log.Workspace == null ? null : log.Workspace.Name,
                    log.WorkspaceId,
                    log.Summary,
                    log.CorrelationId))
                .SingleOrDefaultAsync(cancellationToken);

        return record is null
            ? AuditGridRowNotFound()
            : Result<AuditGridRowResponse>.Success(
                ToAuditGridRow(record, capabilities.CanViewSensitiveMetadata));
    }

    public async Task<Result<AuditSensitiveMetadataResponse>> GetAuditSensitiveMetadataAsync(
        Guid auditId,
        CancellationToken cancellationToken = default)
    {
        var (capabilities, viewDenied) = await GetViewCapabilitiesAsync("audit.grid.sensitive-metadata.read", cancellationToken);
        if (viewDenied is not null)
        {
            return AuthorizationFailure<AuditSensitiveMetadataResponse>(viewDenied);
        }

        if (!capabilities.CanViewSensitiveMetadata)
        {
            var denied = await _auditAuthorization.AuthorizeAsync(
                CapabilityKeys.AuditSensitiveMetadataView,
                "audit.grid.sensitive-metadata.read",
                cancellationToken);
            return AuthorizationFailure<AuditSensitiveMetadataResponse>(denied);
        }

        var scopeError = ValidateQueryScope<AuditSensitiveMetadataResponse>();
        if (scopeError is not null)
        {
            return scopeError;
        }

        // Authorize and apply the current Tenant/platform scope before the ID
        // predicate. Missing, malformed, and other-Tenant IDs therefore share
        // one result and cannot be used as an existence oracle.
        var record = auditId == Guid.Empty
            ? null
            : await ScopeToCurrentTenant(dbContext.AuditLogs.AsNoTracking())
                .Where(log => log.Id == auditId)
                .Select(log => new AuditSensitiveMetadataProjection(
                    log.Id,
                    log.MetadataJson))
                .SingleOrDefaultAsync(cancellationToken);

        return record is null
            ? AuditSensitiveMetadataNotFound()
            : Result<AuditSensitiveMetadataResponse>.Success(
                AuditMetadataDisclosurePolicy.Project(record.Id, record.MetadataJson));
    }

    public async Task<Result<PagedResponse<SecurityEventListItemResponse>>> ListSecurityEventsAsync(
        SecurityEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var (capabilities, viewDenied) = await GetViewCapabilitiesAsync("audit.security-events.list", cancellationToken);
        if (viewDenied is not null)
        {
            return AuthorizationFailure<PagedResponse<SecurityEventListItemResponse>>(viewDenied);
        }

        if ((query.UserId.HasValue || !string.IsNullOrWhiteSpace(query.Email)) &&
            !capabilities.CanViewSensitiveMetadata)
        {
            var denied = await _auditAuthorization.AuthorizeAsync(
                CapabilityKeys.AuditSensitiveMetadataView,
                "audit.security-events.filter.identity",
                cancellationToken);
            return AuthorizationFailure<PagedResponse<SecurityEventListItemResponse>>(denied);
        }

        var scopeError = ValidateQueryScope<PagedResponse<SecurityEventListItemResponse>>();
        if (scopeError is not null)
        {
            return scopeError;
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var source = ScopeToCurrentTenant(dbContext.SecurityEvents.AsNoTracking());

        if (query.EventType.HasValue)
        {
            source = source.Where(item => item.EventType == query.EventType);
        }

        if (query.Severity.HasValue)
        {
            source = source.Where(item => item.Severity == query.Severity);
        }

        if (query.UserId.HasValue)
        {
            source = source.Where(item => item.UserId == query.UserId);
        }

        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            source = source.Where(item =>
                item.Email != null && EF.Functions.ILike(item.Email, $"%{query.Email.Trim()}%"));
        }

        if (query.FromDate.HasValue)
        {
            var fromDateUtc = NormalizePostgresTimestamp(query.FromDate.Value);
            source = source.Where(item => item.CreatedAt >= fromDateUtc);
        }

        if (query.ToDate.HasValue)
        {
            var toDateUtc = NormalizePostgresTimestamp(query.ToDate.Value);
            source = source.Where(item => item.CreatedAt <= toDateUtc);
        }

        var total = await source.CountAsync(cancellationToken);
        var canViewSensitiveMetadata = capabilities.CanViewSensitiveMetadata;
        var items = await source
            .OrderByDescending(item => item.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new SecurityEventListItemResponse(
                item.Id,
                item.EventType,
                canViewSensitiveMetadata ? item.UserId : null,
                canViewSensitiveMetadata ? item.Email : null,
                canViewSensitiveMetadata ? item.IpAddress : null,
                canViewSensitiveMetadata ? item.UserAgent : null,
                item.Severity,
                item.Summary,
                canViewSensitiveMetadata ? item.MetadataJson : null,
                item.CreatedAt))
            .ToListAsync(cancellationToken);

        return Result<PagedResponse<SecurityEventListItemResponse>>.Success(
            new PagedResponse<SecurityEventListItemResponse>(items, page, pageSize, total));
    }

    private async Task<(AuditCapabilityResponse Capabilities, Result? Denied)> GetViewCapabilitiesAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        var capabilities = await _auditAuthorization.GetCapabilitiesAsync(cancellationToken);
        if (capabilities.CanView)
        {
            return (capabilities, null);
        }

        var denied = await _auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditView,
            operation,
            cancellationToken);
        return (capabilities, denied);
    }

    private IQueryable<AuditLog> ScopeToCurrentTenant(IQueryable<AuditLog> source)
    {
        return currentTenant is { IsAvailable: true, IsPlatformScope: false }
            ? source.Where(log => log.TenantId == currentTenant.TenantId)
            : source;
    }

    private IQueryable<SecurityEvent> ScopeToCurrentTenant(IQueryable<SecurityEvent> source)
    {
        return currentTenant is { IsAvailable: true, IsPlatformScope: false }
            ? source.Where(item => item.TenantId == currentTenant.TenantId)
            : source;
    }

    private Result<T>? ValidateQueryScope<T>()
    {
        if (currentTenant.IsPlatformScope || currentTenant.IsAvailable)
        {
            return null;
        }

        return Result<T>.Failure(new ApplicationErrorDetail(
            "TenantMembershipRequired",
            "A Tenant or explicit platform Audit scope is required."));
    }

    private static Result<T> AuthorizationFailure<T>(Result denied)
    {
        if (denied.ErrorDetail is not null)
        {
            return Result<T>.Failure(denied.ErrorDetail);
        }

        return Result<T>.Failure(
            denied.Error ?? "The requested Audit operation is not permitted.");
    }

    private static Result<AuditGridRowResponse> AuditGridRowNotFound() =>
        Result<AuditGridRowResponse>.Failure(new ApplicationErrorDetail(
            "AuditEventNotFound",
            "The requested audit event is not available."));

    private static Result<AuditSensitiveMetadataResponse> AuditSensitiveMetadataNotFound() =>
        Result<AuditSensitiveMetadataResponse>.Failure(new ApplicationErrorDetail(
            "AuditEventNotFound",
            "The requested audit event is not available."));

    private static Result<AuditLogQuery> NormalizeAuditGridQuery(AuditLogQuery query)
    {
        var q = NormalizeFilterValue(query.Q);
        var action = NormalizeFilterValue(query.Action);
        var entityType = NormalizeFilterValue(query.EntityType);
        var actor = NormalizeFilterValue(query.Actor);
        var severity = NormalizeFilterValue(query.Severity)?.ToLowerInvariant();
        var result = NormalizeFilterValue(query.Result)?.ToLowerInvariant();

        if (q?.Length > MaxSearchLength || actor?.Length > MaxActorFilterLength ||
            action?.Length > MaxActionLength || entityType?.Length > MaxEntityTypeLength ||
            (severity is not null && severity is not ("info" or "warning" or "critical")) ||
            (result is not null && result is not ("success" or "denied" or "failed")) ||
            (query is { FromDate: { } fromDate, ToDate: { } toDate } && fromDate > toDate))
        {
            return Result<AuditLogQuery>.Failure(new ApplicationErrorDetail(
                "AuditFilterInvalid",
                "The audit filter is invalid."));
        }

        return Result<AuditLogQuery>.Success(query with
        {
            Q = q,
            Action = action,
            EntityType = entityType,
            Actor = actor,
            Severity = severity,
            Result = result,
        });
    }

    private static string? NormalizeFilterValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static DateTimeOffset NormalizePostgresTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime();


    private static IQueryable<AuditLog> ApplyResultFilter(IQueryable<AuditLog> source, string result)
    {
        return result switch
        {
            "denied" => source.Where(log =>
                log.Action.ToLower().Contains("denied") ||
                log.Action.ToLower().Contains("unauthorized") ||
                log.Action.ToLower().Contains("forbidden") ||
                log.Action.ToLower().Contains("rejected")),
            "failed" => source.Where(log =>
                !(log.Action.ToLower().Contains("denied") ||
                  log.Action.ToLower().Contains("unauthorized") ||
                  log.Action.ToLower().Contains("forbidden") ||
                  log.Action.ToLower().Contains("rejected")) &&
                (log.Action.ToLower().Contains("failed") ||
                 log.Action.ToLower().Contains("failure") ||
                 log.Action.ToLower().Contains("error") ||
                 log.Action.ToLower().Contains("exception") ||
                 log.Action.ToLower().Contains("lockout"))),
            _ => source.Where(log =>
                !(log.Action.ToLower().Contains("denied") ||
                  log.Action.ToLower().Contains("unauthorized") ||
                  log.Action.ToLower().Contains("forbidden") ||
                  log.Action.ToLower().Contains("rejected")) &&
                !(log.Action.ToLower().Contains("failed") ||
                  log.Action.ToLower().Contains("failure") ||
                  log.Action.ToLower().Contains("error") ||
                  log.Action.ToLower().Contains("exception") ||
                  log.Action.ToLower().Contains("lockout"))),
        };
    }

    private static IQueryable<AuditLog> ApplySeverityFilter(IQueryable<AuditLog> source, string severity)
    {
        return severity switch
        {
            "critical" => source.Where(log =>
                log.Action.ToLower().Contains("critical") ||
                log.Action.ToLower().Contains("failed") ||
                log.Action.ToLower().Contains("lockout") ||
                log.Action.ToLower().Contains("suspicious") ||
                log.Action.ToLower().Contains("infected") ||
                log.Action.ToLower().Contains("quarantine")),
            "warning" => source.Where(log =>
                !(log.Action.ToLower().Contains("critical") ||
                  log.Action.ToLower().Contains("failed") ||
                  log.Action.ToLower().Contains("lockout") ||
                  log.Action.ToLower().Contains("suspicious") ||
                  log.Action.ToLower().Contains("infected") ||
                  log.Action.ToLower().Contains("quarantine")) &&
                (log.Action.ToLower().Contains("denied") ||
                 log.Action.ToLower().Contains("unauthorized") ||
                 log.Action.ToLower().Contains("forbidden") ||
                 log.Action.ToLower().Contains("rejected") ||
                 log.Action.ToLower().Contains("failure") ||
                 log.Action.ToLower().Contains("warning") ||
                 log.Action.ToLower().Contains("rate") ||
                 log.Action.ToLower().Contains("revoked") ||
                 log.Action.ToLower().Contains("expired") ||
                 log.Action.ToLower().Contains("blocked"))),
            _ => source.Where(log =>
                !(log.Action.ToLower().Contains("critical") ||
                  log.Action.ToLower().Contains("failed") ||
                  log.Action.ToLower().Contains("lockout") ||
                  log.Action.ToLower().Contains("suspicious") ||
                  log.Action.ToLower().Contains("infected") ||
                  log.Action.ToLower().Contains("quarantine")) &&
                !(log.Action.ToLower().Contains("denied") ||
                  log.Action.ToLower().Contains("unauthorized") ||
                  log.Action.ToLower().Contains("forbidden") ||
                  log.Action.ToLower().Contains("rejected") ||
                  log.Action.ToLower().Contains("failure") ||
                  log.Action.ToLower().Contains("warning") ||
                  log.Action.ToLower().Contains("rate") ||
                  log.Action.ToLower().Contains("revoked") ||
                  log.Action.ToLower().Contains("expired") ||
                  log.Action.ToLower().Contains("blocked"))),
        };
    }

    private static AuditGridRowResponse ToAuditGridRow(
        AuditGridProjection log,
        bool canViewSensitiveMetadata)
    {
        var result = ClassifyResult(log.Action);
        return new AuditGridRowResponse(
            log.Id,
            log.CreatedAt,
            log.Action,
            canViewSensitiveMetadata
                ? (string.IsNullOrWhiteSpace(log.ActorDisplayName) ? "Unknown actor" : log.ActorDisplayName)
                : "Redacted actor",
            log.EntityType,
            log.WorkspaceLabel ?? log.WorkspaceId?.ToString("D"),
            ClassifySeverity(log.Action, result),
            result,
            string.IsNullOrWhiteSpace(log.Summary) ? log.Action : log.Summary,
            canViewSensitiveMetadata ? log.CorrelationId : null);
    }

    private sealed record AuditGridProjection(
        Guid Id,
        DateTimeOffset CreatedAt,
        string Action,
        string? ActorDisplayName,
        string EntityType,
        string? WorkspaceLabel,
        Guid? WorkspaceId,
        string? Summary,
        string? CorrelationId);

    private sealed record AuditSensitiveMetadataProjection(
        Guid Id,
        string? MetadataJson);

    private static string ClassifyResult(string action)
    {
        if (ContainsAny(action, "denied", "unauthorized", "forbidden", "rejected"))
        {
            return "denied";
        }

        if (ContainsAny(action, "failed", "failure", "error", "exception", "lockout"))
        {
            return "failed";
        }

        return "success";
    }

    private static string ClassifySeverity(string action, string result)
    {
        if (ContainsAny(action, "critical", "failed", "lockout", "suspicious", "infected", "quarantine"))
        {
            return "critical";
        }

        if (result == "denied" ||
            ContainsAny(action, "failure", "warning", "rejected", "rate", "revoked", "expired", "blocked"))
        {
            return "warning";
        }

        return "info";
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class LegacyAuditAuthorizationService(
        ICurrentUser currentUser,
        ICurrentTenant currentTenant,
        ITenantRepository tenantRepository) : IAuditAuthorizationService
    {
        public async Task<AuditCapabilityResponse> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
            {
                return new AuditCapabilityResponse(false, false, false, false, false);
            }

            if (currentUser.SystemRole == SystemRole.PlatformAdmin)
            {
                return new AuditCapabilityResponse(true, true, true, true, true);
            }

            if (currentTenant is not { IsAvailable: true, IsPlatformScope: false })
            {
                return new AuditCapabilityResponse(false, false, false, false, false);
            }

            var membership = await tenantRepository.GetTenantUserAsync(
                currentTenant.TenantId,
                currentUser.UserId.Value,
                cancellationToken);
            var isTenantAdmin = membership is
            {
                Status: TenantUserStatus.Active,
                Role: TenantUserRole.Owner or TenantUserRole.Admin
            };

            // This fallback exists only for legacy direct construction in tests and
            // utility hosts. Preserve the historical raw-query contract there while
            // production DI always injects AuditAuthorizationService and enforces the
            // new independent sensitive-metadata capability.
            return isTenantAdmin
                ? new AuditCapabilityResponse(true, true, false, false, true)
                : new AuditCapabilityResponse(false, false, false, false, false);
        }

        public async Task<Result> AuthorizeAsync(
            string capabilityKey,
            string operation,
            CancellationToken cancellationToken = default)
        {
            var capabilities = await GetCapabilitiesAsync(cancellationToken);
            if (capabilities.HasCapability(capabilityKey))
            {
                return Result.Success();
            }

            var message = capabilityKey == CapabilityKeys.AuditView
                ? "You are not allowed to view audit logs."
                : "The requested Audit operation is not permitted.";
            return Result.Failure(message);
        }
    }
}
