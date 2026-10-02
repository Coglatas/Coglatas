using Coglatas.Domain.Common;
using Coglatas.Domain.Enums;

namespace Coglatas.Domain.Entities;

public sealed class ExportJob : AuditableEntity, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public ExportJobStatus Status { get; set; } = ExportJobStatus.Queued;
    public TenantExportType ExportType { get; set; } = TenantExportType.Metadata;
    public Guid? FileObjectId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public User? RequestedByUser { get; set; }
    public FileObject? FileObject { get; set; }
}

public sealed class ExportPackageGrant : AuditableEntity, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid StudentRecordId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string ExportType { get; set; } = "StudentRecordRestricted";
    public string IncludedClassifications { get; set; } = nameof(DataClassification.StudentRecordRestricted);
    public string RequestedScopeType { get; set; } = "StudentRecord";
    public Guid RequestedScopeId { get; set; }
    public bool ReasonRequired { get; set; } = true;
    public DataClassification Classification { get; set; } = DataClassification.StudentRecordRestricted;
    public string RequestedFields { get; set; } = string.Empty;
    public string AuthorizedFields { get; set; } = string.Empty;
    public string PolicyStamp { get; set; } = string.Empty;
    public string BuildAuthorizationState { get; set; } = string.Empty;
    public string DownloadAuthorizationState { get; set; } = string.Empty;
    public DateTimeOffset ReauthorizedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? BuiltAt { get; set; }
    public DateTimeOffset? DownloadedAt { get; set; }
}

public sealed class IntegrationAccount : SoftDeletableEntity, ITenantEntity
{
    public Guid TenantId { get; set; }
    public IntegrationProvider Provider { get; set; } = IntegrationProvider.Other;
    public string DisplayName { get; set; } = string.Empty;
    public IntegrationAccountStatus Status { get; set; } = IntegrationAccountStatus.Draft;
    public string SettingsJson { get; set; } = "{}";
    public Guid CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }
}

public sealed class WebhookEndpoint : SoftDeletableEntity, ITenantEntity
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? SecretHash { get; set; }
    public string EnabledEventsJson { get; set; } = "[]";
    public WebhookEndpointStatus Status { get; set; } = WebhookEndpointStatus.Active;
    public Guid CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }
}

public sealed class ApiToken : AuditableEntity, ITenantEntity
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public User? CreatedByUser { get; set; }
}
