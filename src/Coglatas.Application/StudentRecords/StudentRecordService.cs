using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coglatas.Application.StudentRecords;

public sealed class StudentRecordService(
    IStudentRecordRepository studentRecords,
    IStudentRecordExportGrantRepository exportGrants,
    IStudentRecordAuthorizationService authorization,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    IClock clock,
    IUnitOfWork unitOfWork,
    IAuditLogger auditLogger) : IStudentRecordService
{
    private const int MinimumExportReasonLength = 10;
    private static readonly TimeSpan ExportGrantLifetime = TimeSpan.FromMinutes(15);

    public async Task<Result<StudentRecordPublicResponse>> GetPublicAsync(Guid studentRecordId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId) || !currentTenant.IsAvailable)
        {
            return Result<StudentRecordPublicResponse>.Failure("Student record not found.");
        }

        var record = await studentRecords.GetByIdAsync(studentRecordId, cancellationToken);
        if (record is null ||
            record.TenantId != currentTenant.TenantId ||
            !await authorization.CanViewPublicStudentRecordAsync(userId, record.WorkspaceId, cancellationToken))
        {
            return Result<StudentRecordPublicResponse>.Failure("Student record not found.");
        }

        return Result<StudentRecordPublicResponse>.Success(StudentRecordDataPolicy.ToPublicResponse(record));
    }

    public async Task<Result<StudentRecordRestrictedResponse>> GetRestrictedAsync(
        Guid studentRecordId,
        StudentRecordRestrictedRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId) || !currentTenant.IsAvailable)
        {
            return Result<StudentRecordRestrictedResponse>.Failure("Student record not found.");
        }

        var record = await studentRecords.GetByIdAsync(studentRecordId, cancellationToken);
        if (record is null || record.TenantId != currentTenant.TenantId)
        {
            return Result<StudentRecordRestrictedResponse>.Failure("Student record not found.");
        }

        var requestedFields = NormalizeRequestedFields(request.Fields);
        var access = await authorization.AuthorizeRestrictedStudentRecordAsync(userId, record, requestedFields, cancellationToken);
        if (!access.IsAuthorizedForRecord)
        {
            await AuditRestrictedDenialAsync(record, requestedFields, access, cancellationToken);
            return Result<StudentRecordRestrictedResponse>.Failure("Student record not found.");
        }

        var publicResponse = request.IncludePublic
            ? StudentRecordDataPolicy.ToPublicResponse(record)
            : null;
        var restrictedFields = StudentRecordDataPolicy.ProjectRestrictedFields(record, access.AllowedFields);
        if (restrictedFields.Count > 0)
        {
            await AuditRestrictedViewAsync(userId, record, restrictedFields.Keys, access, cancellationToken);
        }

        return Result<StudentRecordRestrictedResponse>.Success(new StudentRecordRestrictedResponse(
            record.Id,
            record.WorkspaceId,
            publicResponse,
            restrictedFields));
    }

    public async Task<Result<StudentRecordExportGrantResponse>> RequestRestrictedExportAsync(
        Guid studentRecordId,
        StudentRecordExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId) || !currentTenant.IsAvailable)
        {
            return Result<StudentRecordExportGrantResponse>.Failure("Student record export not found.");
        }

        var record = await studentRecords.GetByIdAsync(studentRecordId, cancellationToken);
        if (record is null || record.TenantId != currentTenant.TenantId)
        {
            return Result<StudentRecordExportGrantResponse>.Failure("Student record export not found.");
        }

        var requestedFields = NormalizeRequestedFields(request.Fields);
        if (!IsValidExportReason(request.Reason))
        {
            await AuditExportDenialAsync(userId, record, null, "issue", ReasonDenialCode(request.Reason), requestedFields, null, cancellationToken);
            return Result<StudentRecordExportGrantResponse>.Failure("A valid export reason is required.");
        }

        var decision = await ReauthorizeExportAsync(userId, record, requestedFields, null, "issue", cancellationToken);
        if (!decision.IsAllowed)
        {
            await AuditExportDenialAsync(userId, record, null, "issue", decision.DenialReason, requestedFields, decision.Access, cancellationToken);
            return Result<StudentRecordExportGrantResponse>.Failure("You are not allowed to export this student record.");
        }

        var now = clock.UtcNow;
        var grant = new ExportPackageGrant
        {
            TenantId = record.TenantId,
            RequestedByUserId = userId,
            StudentRecordId = record.Id,
            WorkspaceId = record.WorkspaceId,
            ExportType = "StudentRecordRestricted",
            IncludedClassifications = DataClassification.StudentRecordRestricted.ToString(),
            RequestedScopeType = "StudentRecord",
            RequestedScopeId = record.Id,
            ReasonRequired = true,
            Classification = DataClassification.StudentRecordRestricted,
            RequestedFields = JoinFields(decision.RequestedRestrictedFields),
            AuthorizedFields = JoinFields(decision.AuthorizedFields),
            PolicyStamp = decision.PolicyStamp,
            BuildAuthorizationState = "pending",
            DownloadAuthorizationState = "pending",
            ReauthorizedAt = now,
            ExpiresAt = now.Add(ExportGrantLifetime)
        };

        await exportGrants.AddAsync(grant, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await AuditExportAllowAsync(userId, record, grant, "issue", decision, cancellationToken);

        return Result<StudentRecordExportGrantResponse>.Success(ToGrantResponse(grant));
    }

    public async Task<Result<StudentRecordExportPackageResponse>> BuildRestrictedExportAsync(
        Guid exportPackageGrantId,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateExportGrantAsync(exportPackageGrantId, "build", requireBuilt: false, cancellationToken);
        if (!validation.Result.IsSuccess)
        {
            return Result<StudentRecordExportPackageResponse>.Failure(validation.Result.Error!);
        }

        var now = clock.UtcNow;
        validation.Grant!.BuiltAt = now;
        validation.Grant.ReauthorizedAt = now;
        validation.Grant.BuildAuthorizationState = "authorized";
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await AuditExportAllowAsync(validation.UserId, validation.Record!, validation.Grant, "build", validation.Decision!, cancellationToken);
        return Result<StudentRecordExportPackageResponse>.Success(CreateExportPackage(validation.Record!, validation.Grant));
    }

    public async Task<Result<StudentRecordExportPackageResponse>> DownloadRestrictedExportAsync(
        Guid exportPackageGrantId,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateExportGrantAsync(exportPackageGrantId, "download", requireBuilt: true, cancellationToken);
        if (!validation.Result.IsSuccess)
        {
            return Result<StudentRecordExportPackageResponse>.Failure(validation.Result.Error!);
        }

        var now = clock.UtcNow;
        validation.Grant!.DownloadedAt = now;
        validation.Grant.ReauthorizedAt = now;
        validation.Grant.DownloadAuthorizationState = "authorized";
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await AuditExportAllowAsync(validation.UserId, validation.Record!, validation.Grant, "download", validation.Decision!, cancellationToken);
        return Result<StudentRecordExportPackageResponse>.Success(CreateExportPackage(validation.Record!, validation.Grant));
    }

    private static IReadOnlyList<string> NormalizeRequestedFields(IReadOnlyCollection<string> fields)
    {
        return fields
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => field.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task AuditRestrictedDenialAsync(
        StudentRecord record,
        IReadOnlyCollection<string> requestedFields,
        StudentRecordRestrictedAccess access,
        CancellationToken cancellationToken)
    {
        var knownRestrictedFields = requestedFields
            .Where(StudentRecordDataPolicy.IsKnownRestrictedField)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unknownFieldCount = requestedFields.Count - knownRestrictedFields.Length;

        await auditLogger.LogSecurityAsync(
            "AccessDenied",
            "StudentRecordRestricted access denied.",
            new Dictionary<string, object?>
            {
                ["classification"] = DataClassification.StudentRecordRestricted.ToString(),
                ["studentRecordId"] = record.Id,
                ["workspaceId"] = record.WorkspaceId,
                ["schoolRole"] = access.Role?.ToString(),
                ["decisionReason"] = access.Reason,
                ["requestedRestrictedFields"] = string.Join(",", knownRestrictedFields),
                ["unknownFieldCount"] = unknownFieldCount
            },
            SecurityEventSeverity.Warning,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private Task AuditRestrictedViewAsync(
        Guid userId,
        StudentRecord record,
        IEnumerable<string> accessedFields,
        StudentRecordRestrictedAccess access,
        CancellationToken cancellationToken)
    {
        return auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "student_record.view_sensitive",
            "StudentRecord",
            record.Id,
            "Student record restricted fields viewed.",
            WorkspaceId: record.WorkspaceId,
            Metadata: new Dictionary<string, object?>
            {
                ["classification"] = DataClassification.StudentRecordRestricted.ToString(),
                ["studentRecordId"] = record.Id,
                ["workspaceId"] = record.WorkspaceId,
                ["accessedFields"] = string.Join(",", accessedFields.Order(StringComparer.OrdinalIgnoreCase)),
                ["schoolRole"] = access.Role?.ToString(),
                ["decision"] = "allow",
                ["decisionReason"] = access.Reason
            },
            TenantId: record.TenantId), cancellationToken);
    }

    private async Task<ExportGrantValidation> ValidateExportGrantAsync(
        Guid exportPackageGrantId,
        string stage,
        bool requireBuilt,
        CancellationToken cancellationToken)
    {
        if (!TryCurrentUser(out var userId) || !currentTenant.IsAvailable)
        {
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        var grant = await exportGrants.GetAsync(exportPackageGrantId, cancellationToken);
        if (grant is null)
        {
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        if (grant.TenantId != currentTenant.TenantId)
        {
            await AuditExportGrantBoundaryDenialAsync(userId, grant, stage, "tenant_mismatch", cancellationToken);
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        if (grant.RequestedByUserId != userId)
        {
            await AuditExportGrantBoundaryDenialAsync(userId, grant, stage, "actor_mismatch", cancellationToken);
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        if (grant.Classification != DataClassification.StudentRecordRestricted ||
            !string.Equals(grant.ExportType, "StudentRecordRestricted", StringComparison.Ordinal) ||
            !string.Equals(grant.IncludedClassifications, DataClassification.StudentRecordRestricted.ToString(), StringComparison.Ordinal) ||
            !string.Equals(grant.RequestedScopeType, "StudentRecord", StringComparison.Ordinal) ||
            grant.RequestedScopeId != grant.StudentRecordId ||
            !grant.ReasonRequired)
        {
            await AuditExportGrantBoundaryDenialAsync(userId, grant, stage, "scope_mismatch", cancellationToken);
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        var record = await studentRecords.GetByIdAsync(grant.StudentRecordId, cancellationToken);
        if (record is null ||
            record.TenantId != grant.TenantId ||
            record.WorkspaceId != grant.WorkspaceId ||
            record.Id != grant.RequestedScopeId)
        {
            await AuditExportDenialAsync(userId, record, grant, stage, "scope_mismatch", SplitFields(grant.RequestedFields), null, cancellationToken);
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        if (grant.RevokedAt.HasValue)
        {
            await AuditExportDenialAsync(userId, record, grant, stage, "grant_revoked", SplitFields(grant.RequestedFields), null, cancellationToken);
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        if (clock.UtcNow >= grant.ExpiresAt)
        {
            await AuditExportDenialAsync(userId, record, grant, stage, "grant_expired", SplitFields(grant.RequestedFields), null, cancellationToken);
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        if (requireBuilt && !grant.BuiltAt.HasValue)
        {
            await AuditExportDenialAsync(userId, record, grant, stage, "export_package_not_built", SplitFields(grant.RequestedFields), null, cancellationToken);
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        var decision = await ReauthorizeExportAsync(userId, record, SplitFields(grant.RequestedFields), grant, stage, cancellationToken);
        if (!decision.IsAllowed)
        {
            await AuditExportDenialAsync(userId, record, grant, stage, decision.DenialReason, SplitFields(grant.RequestedFields), decision.Access, cancellationToken);
            return ExportGrantValidation.Failure("Student record export not found.");
        }

        return ExportGrantValidation.Success(userId, grant, record, decision);
    }

    private async Task AuditExportGrantBoundaryDenialAsync(
        Guid userId,
        ExportPackageGrant grant,
        string stage,
        string decisionReason,
        CancellationToken cancellationToken)
    {
        await auditLogger.LogSecurityAsync(
            "AccessDenied",
            "Export package grant denied.",
            new Dictionary<string, object?>
            {
                ["classification"] = grant.Classification.ToString(),
                ["actorUserId"] = userId,
                ["grantActorUserId"] = grant.RequestedByUserId,
                ["tenantId"] = grant.TenantId,
                ["studentRecordId"] = grant.StudentRecordId,
                ["workspaceId"] = grant.WorkspaceId,
                ["exportPackageGrantId"] = grant.Id,
                ["grantId"] = grant.Id,
                ["exportType"] = grant.ExportType,
                ["includedClassifications"] = grant.IncludedClassifications,
                ["requestedScopeType"] = grant.RequestedScopeType,
                ["requestedScopeId"] = grant.RequestedScopeId,
                ["operationType"] = stage,
                ["stage"] = stage,
                ["decision"] = "deny",
                ["decisionReason"] = decisionReason,
                ["policyVersion"] = grant.PolicyStamp,
                ["accessStamp"] = grant.PolicyStamp,
                ["expiresAt"] = grant.ExpiresAt
            },
            SecurityEventSeverity.Warning,
            cancellationToken);
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "export_package.reauthorization_failed",
            "ExportPackageGrant",
            grant.Id,
            "Export package grant reauthorization failed.",
            WorkspaceId: grant.WorkspaceId,
            Metadata: ExportGrantBoundaryMetadata(userId, grant, stage, "deny", decisionReason),
            TenantId: grant.TenantId), cancellationToken);
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            ExportDenialAction(stage),
            "ExportPackageGrant",
            grant.Id,
            "Export package grant denied.",
            WorkspaceId: grant.WorkspaceId,
            Metadata: ExportGrantBoundaryMetadata(userId, grant, stage, "deny", decisionReason),
            TenantId: grant.TenantId), cancellationToken);
        if (stage.Equals("download", StringComparison.OrdinalIgnoreCase) &&
            decisionReason is "scope_mismatch" or "tenant_mismatch")
        {
            await auditLogger.LogAsync(new AuditLogEntry(
                userId,
                "export_package.download_denied_scope_changed",
                "ExportPackageGrant",
                grant.Id,
                "Export package download denied because scope changed.",
                WorkspaceId: grant.WorkspaceId,
                Metadata: ExportGrantBoundaryMetadata(userId, grant, stage, "deny", decisionReason),
                TenantId: grant.TenantId), cancellationToken);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<StudentRecordExportAuthorization> ReauthorizeExportAsync(
        Guid userId,
        StudentRecord record,
        IReadOnlyCollection<string> requestedFields,
        ExportPackageGrant? existingGrant,
        string stage,
        CancellationToken cancellationToken)
    {
        var requestedRestrictedFields = requestedFields
            .Where(StudentRecordDataPolicy.IsKnownRestrictedField)
            .Select(StudentRecordDataPolicy.CanonicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unknownFieldCount = requestedFields.Count - requestedRestrictedFields.Length;

        if (unknownFieldCount > 0)
        {
            return StudentRecordExportAuthorization.Deny(
                "unknown_sensitive_classification",
                requestedRestrictedFields,
                [],
                unknownFieldCount,
                null,
                string.Empty);
        }

        if (requestedRestrictedFields.Length == 0)
        {
            return StudentRecordExportAuthorization.Deny(
                "missing_classification",
                requestedRestrictedFields,
                [],
                unknownFieldCount,
                null,
                string.Empty);
        }

        var access = await authorization.AuthorizeRestrictedStudentRecordAsync(userId, record, requestedRestrictedFields, cancellationToken);
        if (!access.IsAuthorizedForRecord)
        {
            return StudentRecordExportAuthorization.Deny(
                MapExportAccessDenialReason(access),
                requestedRestrictedFields,
                [],
                unknownFieldCount,
                access,
                string.Empty);
        }

        var authorizedFields = access.AllowedFields
            .Select(StudentRecordDataPolicy.CanonicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!requestedRestrictedFields.All(field => authorizedFields.Contains(field, StringComparer.OrdinalIgnoreCase)))
        {
            var reason = existingGrant is not null
                ? stage.Equals("download", StringComparison.OrdinalIgnoreCase)
                    ? "policy_changed"
                    : "policy_changed"
                : "field_access_denied";
            return StudentRecordExportAuthorization.Deny(
                reason,
                requestedRestrictedFields,
                authorizedFields,
                unknownFieldCount,
                access,
                ComputePolicyStamp(userId, record, access, authorizedFields));
        }

        var policyStamp = ComputePolicyStamp(userId, record, access, authorizedFields);
        if (existingGrant is not null &&
            (!string.Equals(existingGrant.PolicyStamp, policyStamp, StringComparison.Ordinal) ||
             !FieldsEqual(SplitFields(existingGrant.AuthorizedFields), authorizedFields)))
        {
            var reason = stage.Equals("download", StringComparison.OrdinalIgnoreCase)
                ? "policy_changed"
                : "policy_changed";
            return StudentRecordExportAuthorization.Deny(
                reason,
                requestedRestrictedFields,
                authorizedFields,
                unknownFieldCount,
                access,
                policyStamp);
        }

        return StudentRecordExportAuthorization.Allow(
            requestedRestrictedFields,
            authorizedFields,
            unknownFieldCount,
            access,
            policyStamp);
    }

    private async Task AuditExportAllowAsync(
        Guid userId,
        StudentRecord record,
        ExportPackageGrant grant,
        string stage,
        StudentRecordExportAuthorization decision,
        CancellationToken cancellationToken)
    {
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "export_package.reauthorization_passed",
            "ExportPackageGrant",
            grant.Id,
            $"Export package grant {stage} reauthorization passed.",
            WorkspaceId: record.WorkspaceId,
            Metadata: ExportAuditMetadata(userId, record, grant, stage, "allow", "reauthorization_passed", decision),
            TenantId: record.TenantId), cancellationToken);
        var action = stage.Equals("issue", StringComparison.OrdinalIgnoreCase)
            ? "export_package.grant_issued"
            : stage.Equals("download", StringComparison.OrdinalIgnoreCase)
                ? "export_package.grant_used"
                : null;
        if (action is not null)
        {
            await auditLogger.LogAsync(new AuditLogEntry(
                userId,
                action,
                "ExportPackageGrant",
                grant.Id,
                $"Export package grant {stage} recorded.",
                WorkspaceId: record.WorkspaceId,
                Metadata: ExportAuditMetadata(userId, record, grant, stage, "allow", action, decision),
                TenantId: record.TenantId), cancellationToken);
        }
    }

    private async Task AuditExportDenialAsync(
        Guid userId,
        StudentRecord? record,
        ExportPackageGrant? grant,
        string stage,
        string decisionReason,
        IReadOnlyCollection<string> requestedFields,
        StudentRecordRestrictedAccess? access,
        CancellationToken cancellationToken)
    {
        var knownRestrictedFields = requestedFields
            .Where(StudentRecordDataPolicy.IsKnownRestrictedField)
            .Select(StudentRecordDataPolicy.CanonicalName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unknownFieldCount = requestedFields.Count - knownRestrictedFields.Length;

        var metadata = ExportDenialMetadata(userId, record, grant, stage, decisionReason, knownRestrictedFields, unknownFieldCount, access);
        await auditLogger.LogSecurityAsync(
            "AccessDenied",
            "Student record restricted export denied.",
            metadata,
            SecurityEventSeverity.Warning,
            cancellationToken);
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "export_package.reauthorization_failed",
            "ExportPackageGrant",
            grant?.Id ?? record?.Id,
            "Export package grant reauthorization failed.",
            WorkspaceId: record?.WorkspaceId ?? grant?.WorkspaceId,
            Metadata: metadata,
            TenantId: record?.TenantId ?? grant?.TenantId), cancellationToken);
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            ExportDenialAction(stage),
            grant is null ? "StudentRecord" : "ExportPackageGrant",
            grant?.Id ?? record?.Id,
            "Export package grant denied.",
            WorkspaceId: record?.WorkspaceId ?? grant?.WorkspaceId,
            Metadata: metadata,
            TenantId: record?.TenantId ?? grant?.TenantId), cancellationToken);
        foreach (var lifecycleAction in ExportLifecycleDenialActions(stage, decisionReason))
        {
            await auditLogger.LogAsync(new AuditLogEntry(
                userId,
                lifecycleAction,
                "ExportPackageGrant",
                grant?.Id,
                "Export package grant lifecycle denial.",
                WorkspaceId: record?.WorkspaceId ?? grant?.WorkspaceId,
                Metadata: metadata,
                TenantId: record?.TenantId ?? grant?.TenantId), cancellationToken);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyDictionary<string, object?> ExportAuditMetadata(
        Guid actorUserId,
        StudentRecord record,
        ExportPackageGrant grant,
        string stage,
        string decision,
        string decisionReason,
        StudentRecordExportAuthorization authorization)
    {
        return new Dictionary<string, object?>
        {
            ["classification"] = DataClassification.StudentRecordRestricted.ToString(),
            ["actorUserId"] = actorUserId,
            ["tenantId"] = grant.TenantId,
            ["studentRecordId"] = record.Id,
            ["workspaceId"] = record.WorkspaceId,
            ["exportPackageGrantId"] = grant.Id,
            ["grantId"] = grant.Id,
            ["exportType"] = grant.ExportType,
            ["includedClassifications"] = grant.IncludedClassifications,
            ["requestedScopeType"] = grant.RequestedScopeType,
            ["requestedScopeId"] = grant.RequestedScopeId,
            ["operationType"] = stage,
            ["stage"] = stage,
            ["decision"] = decision,
            ["decisionReason"] = decisionReason,
            ["schoolRole"] = authorization.Access?.Role?.ToString(),
            ["requestedRestrictedFields"] = string.Join(",", authorization.RequestedRestrictedFields),
            ["authorizedRestrictedFields"] = string.Join(",", authorization.AuthorizedFields),
            ["reasonProvided"] = true,
            ["reasonRequired"] = grant.ReasonRequired,
            ["buildAuthorizationState"] = grant.BuildAuthorizationState,
            ["downloadAuthorizationState"] = grant.DownloadAuthorizationState,
            ["policyVersion"] = grant.PolicyStamp,
            ["exportPolicyVersion"] = grant.PolicyStamp,
            ["accessStamp"] = grant.PolicyStamp,
            ["grantExpiresAt"] = grant.ExpiresAt
        };
    }

    private static StudentRecordExportPackageResponse CreateExportPackage(StudentRecord record, ExportPackageGrant grant)
    {
        var fields = SplitFields(grant.AuthorizedFields);
        var payload = new
        {
            manifest = new
            {
                exportVersion = 1,
                classification = DataClassification.StudentRecordRestricted.ToString(),
                studentRecordId = record.Id,
                workspaceId = record.WorkspaceId,
                grantId = grant.Id,
                fields
            },
            studentRecord = new
            {
                record.Id,
                record.WorkspaceId,
                restrictedFields = StudentRecordDataPolicy.ProjectRestrictedFields(record, fields)
            }
        };

        return new StudentRecordExportPackageResponse(
            ToGrantResponse(grant),
            $"student-record-{record.Id:N}-restricted-export.json",
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static StudentRecordExportGrantResponse ToGrantResponse(ExportPackageGrant grant)
    {
        return new StudentRecordExportGrantResponse(
            grant.Id,
            grant.StudentRecordId,
            grant.WorkspaceId,
            SplitFields(grant.RequestedFields),
            SplitFields(grant.AuthorizedFields),
            grant.Classification.ToString(),
            grant.ReauthorizedAt,
            grant.ExpiresAt,
            grant.BuiltAt,
            grant.DownloadedAt);
    }

    private static bool IsValidExportReason(string? reason)
    {
        return !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length >= MinimumExportReasonLength;
    }

    private static string ReasonDenialCode(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "export_reason_missing" : "invalid_export_reason";
    }

    private static string MapExportAccessDenialReason(StudentRecordRestrictedAccess access)
    {
        if (access.Reason.Equals("missing-school-role", StringComparison.OrdinalIgnoreCase))
        {
            return "missing_policy";
        }

        if (access.Reason.Equals("missing-relationship-or-scope", StringComparison.OrdinalIgnoreCase))
        {
            return access.Role switch
            {
                SchoolRole.Guardian => "guardian_relationship_removed",
                SchoolRole.Teacher or SchoolRole.HomeroomTeacher or SchoolRole.GradeTeacher => "teacher_scope_removed",
                SchoolRole.StudentAdmin or SchoolRole.SchoolAdmin => "scope_mismatch",
                SchoolRole.Student => "student_scope_removed",
                _ => "export_reauthorization_failed"
            };
        }

        if (access.Reason.Equals("missing-field-access-policy", StringComparison.OrdinalIgnoreCase))
        {
            return "field_access_denied";
        }

        return "export_reauthorization_failed";
    }

    private static string ExportDenialAction(string stage)
    {
        return stage.Equals("issue", StringComparison.OrdinalIgnoreCase)
            ? "export_package.grant_issue_denied"
            : "export_package.grant_use_denied";
    }

    private static IEnumerable<string> ExportLifecycleDenialActions(string stage, string decisionReason)
    {
        if (decisionReason == "grant_expired")
        {
            yield return "export_package.grant_expired";
        }

        if (decisionReason == "grant_revoked")
        {
            yield return "export_package.grant_revoked";
        }

        if (stage.Equals("download", StringComparison.OrdinalIgnoreCase) && decisionReason == "policy_changed")
        {
            yield return "export_package.download_denied_policy_changed";
        }

        if (stage.Equals("download", StringComparison.OrdinalIgnoreCase) &&
            decisionReason is "scope_mismatch" or "tenant_mismatch" or "guardian_relationship_removed" or "teacher_scope_removed" or "student_scope_removed")
        {
            yield return "export_package.download_denied_scope_changed";
        }
    }

    private static IReadOnlyDictionary<string, object?> ExportGrantBoundaryMetadata(
        Guid actorUserId,
        ExportPackageGrant grant,
        string stage,
        string decision,
        string decisionReason)
    {
        return new Dictionary<string, object?>
        {
            ["classification"] = grant.Classification.ToString(),
            ["actorUserId"] = actorUserId,
            ["grantActorUserId"] = grant.RequestedByUserId,
            ["tenantId"] = grant.TenantId,
            ["workspaceId"] = grant.WorkspaceId,
            ["studentRecordId"] = grant.StudentRecordId,
            ["exportPackageGrantId"] = grant.Id,
            ["grantId"] = grant.Id,
            ["exportType"] = grant.ExportType,
            ["includedClassifications"] = grant.IncludedClassifications,
            ["requestedScopeType"] = grant.RequestedScopeType,
            ["requestedScopeId"] = grant.RequestedScopeId,
            ["operationType"] = stage,
            ["stage"] = stage,
            ["decision"] = decision,
            ["decisionReason"] = decisionReason,
            ["policyVersion"] = grant.PolicyStamp,
            ["exportPolicyVersion"] = grant.PolicyStamp,
            ["accessStamp"] = grant.PolicyStamp,
            ["grantExpiresAt"] = grant.ExpiresAt
        };
    }

    private static IReadOnlyDictionary<string, object?> ExportDenialMetadata(
        Guid actorUserId,
        StudentRecord? record,
        ExportPackageGrant? grant,
        string stage,
        string decisionReason,
        IReadOnlyCollection<string> knownRestrictedFields,
        int unknownFieldCount,
        StudentRecordRestrictedAccess? access)
    {
        return new Dictionary<string, object?>
        {
            ["classification"] = DataClassification.StudentRecordRestricted.ToString(),
            ["actorUserId"] = actorUserId,
            ["tenantId"] = record?.TenantId ?? grant?.TenantId,
            ["studentRecordId"] = record?.Id ?? grant?.StudentRecordId,
            ["workspaceId"] = record?.WorkspaceId ?? grant?.WorkspaceId,
            ["exportPackageGrantId"] = grant?.Id,
            ["grantId"] = grant?.Id,
            ["exportType"] = grant?.ExportType ?? "StudentRecordRestricted",
            ["includedClassifications"] = grant?.IncludedClassifications ?? DataClassification.StudentRecordRestricted.ToString(),
            ["requestedScopeType"] = grant?.RequestedScopeType ?? "StudentRecord",
            ["requestedScopeId"] = grant?.RequestedScopeId ?? record?.Id,
            ["operationType"] = stage,
            ["stage"] = stage,
            ["decision"] = "deny",
            ["decisionReason"] = decisionReason,
            ["schoolRole"] = access?.Role?.ToString(),
            ["requestedRestrictedFields"] = string.Join(",", knownRestrictedFields),
            ["unknownFieldCount"] = unknownFieldCount,
            ["policyVersion"] = grant?.PolicyStamp,
            ["exportPolicyVersion"] = grant?.PolicyStamp,
            ["accessStamp"] = grant?.PolicyStamp,
            ["grantExpiresAt"] = grant?.ExpiresAt
        };
    }

    private static string ComputePolicyStamp(
        Guid userId,
        StudentRecord record,
        StudentRecordRestrictedAccess access,
        IReadOnlyCollection<string> authorizedFields)
    {
        var basis = string.Join("|",
            userId,
            record.TenantId,
            record.WorkspaceId,
            record.Id,
            DataClassification.StudentRecordRestricted,
            access.Role?.ToString() ?? "none",
            access.Reason,
            JoinFields(authorizedFields));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(bytes);
    }

    private static bool FieldsEqual(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right)
    {
        return left.Count == right.Count && left.All(item => right.Contains(item, StringComparer.OrdinalIgnoreCase));
    }

    private static string JoinFields(IEnumerable<string> fields)
    {
        return string.Join(",", fields
            .Select(StudentRecordDataPolicy.CanonicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitFields(string fields)
    {
        return fields
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StudentRecordDataPolicy.CanonicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryCurrentUser(out Guid userId)
    {
        userId = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated && currentUser.UserId.HasValue;
    }

    private sealed record StudentRecordExportAuthorization(
        bool IsAllowed,
        string DenialReason,
        IReadOnlyCollection<string> RequestedRestrictedFields,
        IReadOnlyCollection<string> AuthorizedFields,
        int UnknownFieldCount,
        StudentRecordRestrictedAccess? Access,
        string PolicyStamp)
    {
        public static StudentRecordExportAuthorization Allow(
            IReadOnlyCollection<string> requestedRestrictedFields,
            IReadOnlyCollection<string> authorizedFields,
            int unknownFieldCount,
            StudentRecordRestrictedAccess access,
            string policyStamp)
        {
            return new StudentRecordExportAuthorization(true, string.Empty, requestedRestrictedFields, authorizedFields, unknownFieldCount, access, policyStamp);
        }

        public static StudentRecordExportAuthorization Deny(
            string reason,
            IReadOnlyCollection<string> requestedRestrictedFields,
            IReadOnlyCollection<string> authorizedFields,
            int unknownFieldCount,
            StudentRecordRestrictedAccess? access,
            string policyStamp)
        {
            return new StudentRecordExportAuthorization(false, reason, requestedRestrictedFields, authorizedFields, unknownFieldCount, access, policyStamp);
        }
    }

    private sealed record ExportGrantValidation(
        Result Result,
        Guid UserId,
        ExportPackageGrant? Grant,
        StudentRecord? Record,
        StudentRecordExportAuthorization? Decision)
    {
        public static ExportGrantValidation Success(
            Guid userId,
            ExportPackageGrant grant,
            StudentRecord record,
            StudentRecordExportAuthorization decision)
        {
            return new ExportGrantValidation(Result.Success(), userId, grant, record, decision);
        }

        public static ExportGrantValidation Failure(string error)
        {
            return new ExportGrantValidation(Result.Failure(error), Guid.Empty, null, null, null);
        }
    }
}
