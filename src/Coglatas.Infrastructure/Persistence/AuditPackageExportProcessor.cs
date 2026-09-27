using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Coglatas.Application.Artifacts;
using Coglatas.Application.Audit;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class AuditPackageExportProcessor(
    AppDbContext dbContext,
    IArtifactRepository artifacts,
    IArtifactEvidenceRepository evidenceRepository,
    IArtifactAuthorizationService artifactAuthorization,
    IFileRepository files,
    IFileAuthorizationService fileAuthorization,
    ITenantAuthorizationService tenantAuthorization,
    ICapabilityGrantEvaluator capabilityGrants,
    IFileStorageService storage,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork,
    IClock clock) : IAuditPackageExportProcessor
{
    private readonly AuditClaimsEvidenceProjectionBuilder _claimsEvidenceProjection =
        new(dbContext, artifacts, evidenceRepository, artifactAuthorization, files, fileAuthorization);

    private const int MaxPackageBytes = 25 * 1024 * 1024;

    public async Task<IReadOnlyList<Guid>> ListQueuedTenantIdsAsync(
        int take,
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(take, 1, 100);
        return await dbContext.ExportJobs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(job =>
                job.ExportType == TenantExportType.AuditPackage &&
                (job.Status == ExportJobStatus.Queued ||
                 (job.Status == ExportJobStatus.Running &&
                  (!job.UpdatedAt.HasValue || job.UpdatedAt.Value <= staleBefore))))
            .OrderBy(job => job.CreatedAt)
            .Select(job => job.TenantId)
            .Distinct()
            .Take(bounded)
            .ToListAsync(cancellationToken);
    }

    public Task<int> RecoverStaleRunningAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return dbContext.ExportJobs
            .Where(job =>
                job.ExportType == TenantExportType.AuditPackage &&
                job.Status == ExportJobStatus.Running &&
                (!job.UpdatedAt.HasValue || job.UpdatedAt.Value <= staleBefore))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, ExportJobStatus.Failed)
                .SetProperty(job => job.ErrorMessage, "WorkerInterrupted")
                .SetProperty(job => job.CompletedAt, now)
                .SetProperty(job => job.UpdatedAt, now),
                cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListQueuedJobIdsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(take, 1, 25);
        return await dbContext.ExportJobs
            .AsNoTracking()
            .Where(job =>
                job.ExportType == TenantExportType.AuditPackage &&
                job.Status == ExportJobStatus.Queued)
            .OrderBy(job => job.CreatedAt)
            .Select(job => job.Id)
            .Take(bounded)
            .ToListAsync(cancellationToken);
    }

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var claimed = await dbContext.ExportJobs
            .Where(job =>
                job.Id == jobId &&
                job.ExportType == TenantExportType.AuditPackage &&
                job.Status == ExportJobStatus.Queued)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, ExportJobStatus.Running)
                .SetProperty(job => job.ErrorMessage, (string?)null)
                .SetProperty(job => job.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(job => job.UpdatedAt, now),
                cancellationToken);
        if (claimed == 0)
        {
            return;
        }

        var job = await dbContext.ExportJobs
            .Include(item => item.FileObject)
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job?.FileObject is null ||
            job.ExportType != TenantExportType.AuditPackage ||
            !AuditPackageStorageKey.TryParse(
                job.FileObject.StorageKey,
                out var tenantId,
                out var parsedJobId,
                out var artifactVersionId) ||
            tenantId != job.TenantId ||
            parsedJobId != job.Id)
        {
            if (job is not null)
            {
                await FailAsync(job, "ExportJobCorrupt", cancellationToken);
            }
            return;
        }

        try
        {
            var authorization = await AuthorizeActorAsync(
                job.RequestedByUserId,
                job.TenantId,
                cancellationToken);
            if (!authorization.CanBuild)
            {
                await FailAsync(job, "AuthorizationChanged", cancellationToken);
                return;
            }

            var projection = await _claimsEvidenceProjection.BuildAsync(
                job.RequestedByUserId,
                artifactVersionId,
                cancellationToken);
            if (projection is null)
            {
                await FailAsync(job, "ArtifactVersionUnavailable", cancellationToken);
                return;
            }

            var claimIds = projection.Claims.Select(claim => claim.ClaimId).ToArray();
            IReadOnlyList<ArtifactFinding> findings = claimIds.Length == 0
                ? Array.Empty<ArtifactFinding>()
                : await dbContext.Set<ArtifactFinding>()
                    .AsNoTracking()
                    .Where(finding => Enumerable.Contains(claimIds, finding.ArtifactClaimId))
                    .OrderBy(finding => finding.CreatedAt)
                    .ThenBy(finding => finding.Id)
                    .ToListAsync(cancellationToken);

            var findingIds = findings.Select(finding => finding.Id).ToArray();
            IReadOnlyList<AuditFindingDecision> decisions = findingIds.Length == 0
                ? Array.Empty<AuditFindingDecision>()
                : await dbContext.Set<AuditFindingDecision>()
                    .AsNoTracking()
                    .Where(decision => Enumerable.Contains(findingIds, decision.ArtifactFindingId))
                    .OrderBy(decision => decision.CreatedAt)
                    .ThenBy(decision => decision.Id)
                    .ToListAsync(cancellationToken);

            var generatedAt = clock.UtcNow;
            var packageBytes = BuildPackage(
                job,
                projection,
                findings,
                decisions,
                authorization.CanViewSensitiveMetadata,
                generatedAt);
            if (packageBytes.Length > MaxPackageBytes)
            {
                await FailAsync(job, "PackageTooLarge", cancellationToken);
                return;
            }

            var deliveryAuthorization = await AuthorizeActorAsync(
                job.RequestedByUserId,
                job.TenantId,
                cancellationToken);
            if (!deliveryAuthorization.CanBuild ||
                (authorization.CanViewSensitiveMetadata && !deliveryAuthorization.CanViewSensitiveMetadata))
            {
                await FailAsync(job, "AuthorizationChanged", cancellationToken);
                return;
            }

            await using var packageStream = new MemoryStream(packageBytes, writable: false);
            var saved = await storage.SaveAsync(
                job.FileObject.StorageKey,
                packageStream,
                "application/zip",
                cancellationToken);
            if (!saved.IsSuccess)
            {
                await FailAsync(job, "StorageWriteFailed", cancellationToken);
                return;
            }

            job.FileObject.ContentType = "application/zip";
            job.FileObject.SizeBytes = packageBytes.LongLength;
            job.FileObject.HashSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            job.Status = ExportJobStatus.Completed;
            job.CompletedAt = generatedAt;
            job.ErrorMessage = null;
            await auditLogger.LogAsync(new AuditLogEntry(
                job.RequestedByUserId,
                "AuditPackageExportCompleted",
                "ExportJob",
                job.Id,
                "Audit package export completed.",
                TenantId: job.TenantId,
                Metadata: new Dictionary<string, object?>
                {
                    ["artifactVersionId"] = artifactVersionId,
                    ["packageHash"] = job.FileObject.HashSha256
                }), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            try
            {
                if (await storage.ExistsAsync(job.FileObject.StorageKey, cancellationToken))
                {
                    await storage.DeleteAsync(job.FileObject.StorageKey, cancellationToken);
                }
            }
            catch
            {
                // The durable job remains Failed even when orphan cleanup cannot complete.
            }

            await FailAsync(job, "PackageBuildFailed", cancellationToken);
        }
    }

    private async Task<(bool CanBuild, bool CanViewSensitiveMetadata)> AuthorizeActorAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty)
        {
            return (false, false);
        }

        if (await tenantAuthorization.IsPlatformAdminAsync(userId, cancellationToken))
        {
            return (true, true);
        }

        if (!await tenantAuthorization.CanAccessTenantAsync(userId, tenantId, cancellationToken))
        {
            return (false, false);
        }

        var tenantAdmin = await tenantAuthorization.CanManageTenantAsync(userId, tenantId, cancellationToken);
        var canView = tenantAdmin || await capabilityGrants.HasActiveGrantAsync(
            userId,
            tenantId,
            CapabilityKeys.AuditView,
            CapabilityScopeType.Tenant,
            tenantId,
            cancellationToken);
        var canExport = await capabilityGrants.HasActiveGrantAsync(
            userId,
            tenantId,
            CapabilityKeys.AuditExport,
            CapabilityScopeType.Tenant,
            tenantId,
            cancellationToken);
        var sensitive = await capabilityGrants.HasActiveGrantAsync(
            userId,
            tenantId,
            CapabilityKeys.AuditSensitiveMetadataView,
            CapabilityScopeType.Tenant,
            tenantId,
            cancellationToken);
        return (canView && canExport, sensitive);
    }

    private static byte[] BuildPackage(
        ExportJob job,
        AuditClaimsEvidenceResponse projection,
        IReadOnlyList<ArtifactFinding> findings,
        IReadOnlyList<AuditFindingDecision> decisions,
        bool includeSensitiveMetadata,
        DateTimeOffset generatedAt)
    {
        var decisionsByFinding = decisions
            .GroupBy(decision => decision.ArtifactFindingId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var sourceManifest = projection.Claims
            .SelectMany(claim => claim.Evidence.Select(evidence => new { claim, evidence }))
            .GroupBy(item => item.evidence.SourceId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First().evidence;
                return new
                {
                    sourceId = group.Key,
                    sourceKind = first.SourceKind,
                    sourceReference = first.SourceReference,
                    sourceTitle = first.SourceTitle,
                    sourcePublisher = first.SourcePublisher,
                    sourceType = first.SourceType,
                    sourceClassification = first.SourceClassification,
                    publishedAt = first.PublishedAt,
                    retrievedAt = first.RetrievedAt,
                    contentHash = first.ContentHash,
                    sourceVersion = first.SourceVersion,
                    verificationStatus = first.VerificationStatus,
                    references = group
                        .OrderBy(item => item.claim.Ordinal)
                        .ThenBy(item => item.evidence.Ordinal)
                        .Select(item => new
                        {
                            claimId = item.claim.ClaimId,
                            claimOrdinal = item.claim.Ordinal,
                            evidenceId = item.evidence.EvidenceId,
                            evidenceOrdinal = item.evidence.Ordinal,
                            location = item.evidence.Location
                        })
                        .ToArray()
                };
            })
            .ToArray();

        var riskDecisions = findings.Select(finding =>
        {
            var revisions = decisionsByFinding.TryGetValue(finding.Id, out var found)
                ? found
                : new List<AuditFindingDecision>();
            return new
            {
                findingId = finding.Id,
                claimId = finding.ArtifactClaimId,
                severity = finding.Severity.ToString(),
                confidencePercent = finding.ConfidencePercent,
                detectorKey = finding.DetectorKey,
                policyVersion = finding.PolicyVersion,
                triageStatus = finding.Status.ToString(),
                workflowStatus = finding.WorkflowStatus.ToString(),
                ownerUserId = includeSensitiveMetadata ? finding.OwnerUserId : null,
                dueDate = finding.DueDate,
                resolutionReason = finding.ResolutionReason,
                decisions = revisions.Select(decision => new
                {
                    decisionId = decision.Id,
                    decision = decision.Decision.ToString(),
                    previousDecision = decision.PreviousDecision?.ToString(),
                    rationale = decision.Rationale,
                    reviewerUserId = includeSensitiveMetadata ? decision.ReviewerUserId : (Guid?)null,
                    reviewerDisplayName = includeSensitiveMetadata ? decision.ReviewerDisplayName : null,
                    timestamp = decision.CreatedAt
                }).ToArray()
            };
        }).ToArray();

        var policyVersions = findings
            .Select(finding => finding.PolicyVersion)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var report = new
        {
            schemaVersion = 1,
            artifactId = projection.ArtifactId,
            artifactVersionId = projection.ArtifactVersionId,
            artifactVersionNumber = projection.ArtifactVersionNumber,
            artifactTitle = projection.ArtifactTitle,
            generatedAt,
            summary = new
            {
                claims = projection.Claims.Count,
                evidence = projection.Claims.Sum(claim => claim.Evidence.Count),
                sources = sourceManifest.Length,
                findings = findings.Count,
                decisionRevisions = decisions.Count,
                openFindings = findings.Count(finding => finding.Status == AuditFindingTriageStatus.Open)
            }
        };

        var metadata = new
        {
            schemaVersion = 1,
            exportJobId = job.Id,
            generatedAt,
            scope = new
            {
                type = "ArtifactVersion",
                artifactId = projection.ArtifactId,
                artifactVersionId = projection.ArtifactVersionId,
                artifactVersionNumber = projection.ArtifactVersionNumber
            },
            filters = new
            {
                kind = "none",
                description = "Full authorized Audit scope for the selected Artifact version."
            },
            policyVersions,
            sensitiveMetadataIncluded = includeSensitiveMetadata,
            sections = new[]
            {
                "audit-report",
                "claim-evidence",
                "source-manifest",
                "risk-decisions",
                "run-metadata"
            }
        };

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJsonEntry(archive, "audit-report.json", report);
            WriteJsonEntry(archive, "claim-evidence.json", projection);
            WriteJsonEntry(archive, "source-manifest.json", sourceManifest);
            WriteJsonEntry(archive, "risk-decisions.json", riskDecisions);
            WriteJsonEntry(archive, "run-metadata.json", metadata);
        }

        return output.ToArray();
    }

    private static void WriteJsonEntry<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);
        writer.Write(JsonSerializer.Serialize(value, AuditPackageJson.Options));
    }

    private async Task FailAsync(
        ExportJob job,
        string errorCode,
        CancellationToken cancellationToken)
    {
        job.Status = ExportJobStatus.Failed;
        job.ErrorMessage = errorCode;
        job.CompletedAt = clock.UtcNow;
        await auditLogger.LogAsync(new AuditLogEntry(
            job.RequestedByUserId,
            "AuditPackageExportFailed",
            "ExportJob",
            job.Id,
            "Audit package export failed.",
            TenantId: job.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["errorCode"] = errorCode
            }), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
