using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class AuditPackageExportService(
    AppDbContext dbContext,
    IArtifactRepository artifacts,
    IAuditClaimsEvidenceService claimsEvidence,
    IAuditAuthorizationService auditAuthorization,
    ICurrentUser currentUser,
    IFileStorageService storage,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork,
    IClock clock) : IAuditPackageExportService
{
    private const string PackageContentType = "application/zip";

    public async Task<Result<AuditPackageExportPreviewResponse>> PreviewAsync(
        Guid artifactVersionId,
        CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return Failure<AuditPackageExportPreviewResponse>("AuthenticationRequired", "Authentication is required.");
        }

        var projection = await claimsEvidence.GetAsync(artifactVersionId, cancellationToken);
        if (!projection.IsSuccess || projection.Value is null)
        {
            return ForwardFailure<AuditClaimsEvidenceResponse, AuditPackageExportPreviewResponse>(projection);
        }

        var capabilities = await auditAuthorization.GetCapabilitiesAsync(cancellationToken);
        var claimIds = projection.Value.Claims.Select(claim => claim.ClaimId).ToArray();
        var findingCount = claimIds.Length == 0
            ? 0
            : await dbContext.Set<ArtifactFinding>()
                .AsNoTracking()
                .CountAsync(finding => Enumerable.Contains(claimIds, finding.ArtifactClaimId), cancellationToken);
        var sourceCount = projection.Value.Claims
            .SelectMany(claim => claim.Evidence)
            .Select(evidence => evidence.SourceId)
            .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var evidenceCount = projection.Value.Claims.Sum(claim => claim.Evidence.Count);

        return Result<AuditPackageExportPreviewResponse>.Success(new AuditPackageExportPreviewResponse(
            projection.Value.ArtifactId,
            projection.Value.ArtifactVersionId,
            projection.Value.ArtifactVersionNumber,
            projection.Value.ArtifactTitle,
            $"Artifact version {projection.Value.ArtifactVersionNumber} (full authorized Audit scope; no additional filters)",
            capabilities.CanExport,
            capabilities.CanViewSensitiveMetadata,
            [
                new("audit-report", "Audit report", 1),
                new("claim-evidence", "Claim / Evidence table", projection.Value.Claims.Count + evidenceCount),
                new("source-manifest", "Source manifest", sourceCount),
                new("risk-decisions", "Risk decisions", findingCount),
                new("run-metadata", "Run metadata", 1)
            ]));
    }

    public async Task<Result<AuditPackageExportJobResponse>> QueueAsync(
        QueueAuditPackageExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Failure<AuditPackageExportJobResponse>("AuthenticationRequired", "Authentication is required.");
        }

        var exportAuthorization = await auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditExport,
            "audit.package-export.queue",
            cancellationToken);
        if (!exportAuthorization.IsSuccess)
        {
            return AuthorizationFailure<AuditPackageExportJobResponse>(exportAuthorization);
        }

        var preview = await PreviewAsync(request.ArtifactVersionId, cancellationToken);
        if (!preview.IsSuccess || preview.Value is null)
        {
            return ForwardFailure<AuditPackageExportPreviewResponse, AuditPackageExportJobResponse>(preview);
        }

        var version = await artifacts.GetVersionAsync(request.ArtifactVersionId, cancellationToken);
        if (version?.Artifact is null || version.DeletedAt.HasValue || version.Artifact.DeletedAt.HasValue)
        {
            return Failure<AuditPackageExportJobResponse>("ArtifactVersionNotFound", "The artifact version is not available.");
        }

        var job = new ExportJob
        {
            TenantId = version.TenantId,
            RequestedByUserId = userId,
            Status = ExportJobStatus.Queued,
            ExportType = TenantExportType.AuditPackage
        };
        var fileObject = new FileObject
        {
            TenantId = version.TenantId,
            UploadedByUserId = userId,
            OriginalFileName = AuditPackageStorageKey.FileName(
                preview.Value.ArtifactId,
                preview.Value.ArtifactVersionNumber),
            StorageKey = AuditPackageStorageKey.Create(
                version.TenantId,
                job.Id,
                request.ArtifactVersionId),
            ContentType = PackageContentType,
            SizeBytes = 0,
            Classification = DataClassification.UnknownSensitive,
            SharingPolicy = FileSharingPolicy.Private,
            Status = FileObjectStatus.Active
        };
        job.FileObjectId = fileObject.Id;

        dbContext.FileObjects.Add(fileObject);
        dbContext.ExportJobs.Add(job);
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "AuditPackageExportQueued",
            "ExportJob",
            job.Id,
            "Audit package export queued.",
            TenantId: version.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["artifactVersionId"] = request.ArtifactVersionId,
                ["exportType"] = nameof(TenantExportType.AuditPackage)
            }), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<AuditPackageExportJobResponse>.Success(ToJobResponse(job, fileObject));
    }

    public async Task<Result<AuditPackageExportJobResponse>> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Failure<AuditPackageExportJobResponse>("AuthenticationRequired", "Authentication is required.");
        }

        var authorization = await auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditExport,
            "audit.package-export.status",
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return AuthorizationFailure<AuditPackageExportJobResponse>(authorization);
        }

        var loaded = await LoadOwnedJobAsync(jobId, userId, tracking: false, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return ForwardFailure<ExportJob, AuditPackageExportJobResponse>(loaded);
        }

        return Result<AuditPackageExportJobResponse>.Success(ToJobResponse(
            loaded.Value,
            loaded.Value.FileObject!));
    }

    public async Task<Result<AuditPackageExportJobResponse>> RetryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Failure<AuditPackageExportJobResponse>("AuthenticationRequired", "Authentication is required.");
        }

        var authorization = await auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditExport,
            "audit.package-export.retry",
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return AuthorizationFailure<AuditPackageExportJobResponse>(authorization);
        }

        var loaded = await LoadOwnedJobAsync(jobId, userId, tracking: true, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return ForwardFailure<ExportJob, AuditPackageExportJobResponse>(loaded);
        }

        var job = loaded.Value;
        var fileObject = job.FileObject!;
        if (job.Status != ExportJobStatus.Failed)
        {
            return Failure<AuditPackageExportJobResponse>(
                "ExportRetryNotAllowed",
                "Only failed Audit package exports can be retried.");
        }

        if (!AuditPackageStorageKey.TryParse(fileObject.StorageKey, out _, out _, out var artifactVersionId))
        {
            return Failure<AuditPackageExportJobResponse>("ExportJobCorrupt", "The Audit export job is not valid.");
        }

        var projection = await claimsEvidence.GetAsync(artifactVersionId, cancellationToken);
        if (!projection.IsSuccess)
        {
            return Failure<AuditPackageExportJobResponse>(
                "AuthorizationChanged",
                "The Audit package scope is no longer authorized.");
        }

        try
        {
            if (await storage.ExistsAsync(fileObject.StorageKey, cancellationToken))
            {
                await storage.DeleteAsync(fileObject.StorageKey, cancellationToken);
            }
        }
        catch
        {
            return Failure<AuditPackageExportJobResponse>(
                "ExportPackageCleanupFailed",
                "The previous Audit package could not be cleared safely.");
        }

        job.Status = ExportJobStatus.Queued;
        job.ErrorMessage = null;
        job.CompletedAt = null;
        fileObject.SizeBytes = 0;
        fileObject.HashSha256 = null;
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "AuditPackageExportRetried",
            "ExportJob",
            job.Id,
            "Audit package export retry queued.",
            TenantId: job.TenantId), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<AuditPackageExportJobResponse>.Success(ToJobResponse(job, fileObject));
    }

    public async Task<Result<AuditPackageExportDownloadResponse>> DownloadAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Failure<AuditPackageExportDownloadResponse>("AuthenticationRequired", "Authentication is required.");
        }

        var authorization = await auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditExport,
            "audit.package-export.download",
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return AuthorizationFailure<AuditPackageExportDownloadResponse>(authorization);
        }

        var loaded = await LoadOwnedJobAsync(jobId, userId, tracking: false, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return ForwardFailure<ExportJob, AuditPackageExportDownloadResponse>(loaded);
        }

        var job = loaded.Value;
        var fileObject = job.FileObject!;
        if (job.Status != ExportJobStatus.Completed ||
            string.IsNullOrWhiteSpace(fileObject.HashSha256))
        {
            return Failure<AuditPackageExportDownloadResponse>(
                "ExportJobNotReady",
                "The Audit package is not ready for download.");
        }

        if (!AuditPackageStorageKey.TryParse(fileObject.StorageKey, out _, out _, out var artifactVersionId))
        {
            return Failure<AuditPackageExportDownloadResponse>("ExportJobCorrupt", "The Audit export job is not valid.");
        }

        var currentProjection = await claimsEvidence.GetAsync(artifactVersionId, cancellationToken);
        if (!currentProjection.IsSuccess || currentProjection.Value is null)
        {
            return Failure<AuditPackageExportDownloadResponse>(
                "AuthorizationChanged",
                "The Audit package scope is no longer authorized.");
        }
        var capabilities = await auditAuthorization.GetCapabilitiesAsync(cancellationToken);

        try
        {
            if (!await storage.ExistsAsync(fileObject.StorageKey, cancellationToken) ||
                !await ValidatePackageAsync(
                    fileObject,
                    currentProjection.Value,
                    capabilities.CanViewSensitiveMetadata,
                    cancellationToken))
            {
                return Failure<AuditPackageExportDownloadResponse>(
                    "AuthorizationChanged",
                    "The Audit package can no longer be delivered safely.");
            }

            var stream = await storage.OpenReadAsync(fileObject.StorageKey, cancellationToken);
            await auditLogger.LogAsync(new AuditLogEntry(
                userId,
                "AuditPackageExportDownloaded",
                "ExportJob",
                job.Id,
                "Audit package export downloaded.",
                TenantId: job.TenantId), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<AuditPackageExportDownloadResponse>.Success(new AuditPackageExportDownloadResponse(
                fileObject.OriginalFileName,
                PackageContentType,
                stream));
        }
        catch
        {
            return Failure<AuditPackageExportDownloadResponse>(
                "ExportPackageUnavailable",
                "The Audit package is not currently available.");
        }
    }

    private async Task<bool> ValidatePackageAsync(
        FileObject fileObject,
        AuditClaimsEvidenceResponse currentProjection,
        bool canViewSensitiveMetadata,
        CancellationToken cancellationToken)
    {
        await using (var hashStream = await storage.OpenReadAsync(fileObject.StorageKey, cancellationToken))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, fileObject.HashSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        await using var packageStream = await storage.OpenReadAsync(fileObject.StorageKey, cancellationToken);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        var claimEntry = archive.GetEntry("claim-evidence.json");
        var metadataEntry = archive.GetEntry("run-metadata.json");
        if (claimEntry is null || metadataEntry is null)
        {
            return false;
        }

        AuditClaimsEvidenceResponse? packageProjection;
        await using (var stream = claimEntry.Open())
        {
            packageProjection = await JsonSerializer.DeserializeAsync<AuditClaimsEvidenceResponse>(
                stream,
                AuditPackageJson.Options,
                cancellationToken);
        }
        if (packageProjection is null ||
            packageProjection.ArtifactId != currentProjection.ArtifactId ||
            packageProjection.ArtifactVersionId != currentProjection.ArtifactVersionId)
        {
            return false;
        }

        var currentClaimIds = currentProjection.Claims.Select(claim => claim.ClaimId).ToHashSet();
        var currentEvidenceIds = currentProjection.Claims
            .SelectMany(claim => claim.Evidence)
            .Select(evidence => evidence.EvidenceId)
            .ToHashSet();
        if (packageProjection.Claims.Any(claim => !currentClaimIds.Contains(claim.ClaimId)) ||
            packageProjection.Claims.SelectMany(claim => claim.Evidence).Any(evidence => !currentEvidenceIds.Contains(evidence.EvidenceId)))
        {
            return false;
        }

        await using var metadataStream = metadataEntry.Open();
        using var metadata = await JsonDocument.ParseAsync(metadataStream, cancellationToken: cancellationToken);
        if (metadata.RootElement.TryGetProperty("sensitiveMetadataIncluded", out var sensitive) &&
            sensitive.ValueKind == JsonValueKind.True &&
            !canViewSensitiveMetadata)
        {
            return false;
        }

        return true;
    }

    private async Task<Result<ExportJob>> LoadOwnedJobAsync(
        Guid jobId,
        Guid userId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
        {
            return Failure<ExportJob>("ExportJobNotFound", "Export job not found.");
        }

        IQueryable<ExportJob> query = dbContext.ExportJobs.Include(job => job.FileObject);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        var job = await query.SingleOrDefaultAsync(item =>
            item.Id == jobId &&
            item.ExportType == TenantExportType.AuditPackage &&
            item.RequestedByUserId == userId,
            cancellationToken);
        if (job?.FileObject is null || job.FileObject.TenantId != job.TenantId)
        {
            return Failure<ExportJob>("ExportJobNotFound", "Export job not found.");
        }

        return Result<ExportJob>.Success(job);
    }

    private static AuditPackageExportJobResponse ToJobResponse(ExportJob job, FileObject fileObject)
    {
        if (!AuditPackageStorageKey.TryParse(fileObject.StorageKey, out _, out _, out var artifactVersionId))
        {
            artifactVersionId = Guid.Empty;
        }

        var state = job.Status switch
        {
            ExportJobStatus.Running => "Processing",
            _ => job.Status.ToString()
        };
        var progress = job.Status switch
        {
            ExportJobStatus.Queued => 10,
            ExportJobStatus.Running => 60,
            ExportJobStatus.Completed => 100,
            ExportJobStatus.Failed => 100,
            _ => 0
        };
        return new AuditPackageExportJobResponse(
            job.Id,
            artifactVersionId,
            fileObject.OriginalFileName,
            state,
            progress,
            job.ErrorMessage,
            job.CreatedAt,
            job.CompletedAt);
    }

    private bool TryCurrentUser(out Guid userId)
    {
        userId = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated && currentUser.UserId.HasValue;
    }

    private static Result<T> AuthorizationFailure<T>(Result denied) =>
        denied.ErrorDetail is not null
            ? Result<T>.Failure(denied.ErrorDetail)
            : Result<T>.Failure(denied.Error ?? "Audit export is not permitted.");

    private static Result<T> Failure<T>(string code, string message) =>
        Result<T>.Failure(new ApplicationErrorDetail(code, message));

    private static Result<TOut> ForwardFailure<TIn, TOut>(Result<TIn> result) =>
        result.ErrorDetail is not null
            ? Result<TOut>.Failure(result.ErrorDetail)
            : Result<TOut>.Failure(result.Error ?? "The Audit package operation failed.");
}

public static class AuditPackageStorageKey
{
    private const string Prefix = "audit-packages";

    public static string Create(Guid tenantId, Guid jobId, Guid artifactVersionId) =>
        $"{Prefix}/{tenantId:N}/{jobId:N}/{artifactVersionId:N}.zip";

    public static string FileName(Guid artifactId, int artifactVersionNumber) =>
        $"audit-{artifactId:N}-v{Math.Max(1, artifactVersionNumber)}.zip";

    public static bool TryParse(
        string storageKey,
        out Guid tenantId,
        out Guid jobId,
        out Guid artifactVersionId)
    {
        tenantId = Guid.Empty;
        jobId = Guid.Empty;
        artifactVersionId = Guid.Empty;
        var parts = storageKey.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var versionPart = parts[3].EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? parts[3][..^4]
            : string.Empty;
        return Guid.TryParseExact(parts[1], "N", out tenantId) &&
               Guid.TryParseExact(parts[2], "N", out jobId) &&
               Guid.TryParseExact(versionPart, "N", out artifactVersionId) &&
               tenantId != Guid.Empty && jobId != Guid.Empty && artifactVersionId != Guid.Empty;
    }
}

internal static class AuditPackageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
