using Coglatas.Application.Artifacts;
using Coglatas.Application.Audit;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

internal sealed class AuditClaimsEvidenceProjectionBuilder(
    AppDbContext dbContext,
    IArtifactRepository artifacts,
    IArtifactEvidenceRepository evidenceRepository,
    IArtifactAuthorizationService artifactAuthorization,
    IFileRepository files,
    IFileAuthorizationService fileAuthorization)
{
    private const int MaxEvidencePerClaim = 20;

    internal async Task<AuditClaimsEvidenceResponse?> BuildAsync(
        Guid actorUserId,
        Guid artifactVersionId,
        CancellationToken cancellationToken)
    {
        var version = await artifacts.GetVersionAsync(artifactVersionId, cancellationToken);
        if (version?.Artifact is null ||
            version.DeletedAt.HasValue ||
            version.Artifact.DeletedAt.HasValue ||
            !await artifactAuthorization.CanViewArtifact(actorUserId, version.ArtifactId, cancellationToken))
        {
            return null;
        }

        var claims = await evidenceRepository.ListClaimsAsync(version.Id, cancellationToken);
        var sourceAuthorization = new Dictionary<(ArtifactEvidenceSourceKind Kind, string Reference), bool>();
        var authorizedByClaim = new Dictionary<Guid, List<ArtifactEvidence>>();
        var candidateEventIds = new HashSet<Guid>();

        foreach (var claim in claims)
        {
            var authorized = new List<ArtifactEvidence>();
            foreach (var evidence in claim.Evidence.OrderBy(item => item.Ordinal).Take(MaxEvidencePerClaim))
            {
                var key = (evidence.SourceKind, evidence.SourceReference);
                if (!sourceAuthorization.TryGetValue(key, out var allowed))
                {
                    allowed = await CanViewSourceAsync(
                        actorUserId,
                        evidence.SourceKind,
                        evidence.SourceReference,
                        cancellationToken);
                    sourceAuthorization[key] = allowed;
                }

                if (!allowed)
                {
                    continue;
                }

                authorized.Add(evidence);
                if (evidence.SourceEventAuditId.HasValue)
                {
                    candidateEventIds.Add(evidence.SourceEventAuditId.Value);
                }
            }

            authorizedByClaim[claim.Id] = authorized;
        }

        var authorizedEventIds = candidateEventIds.Count == 0
            ? new HashSet<Guid>()
            : (await dbContext.AuditLogs
                .AsNoTracking()
                .Where(log =>
                    log.TenantId == version.TenantId &&
                    candidateEventIds.Contains(log.Id))
                .Select(log => log.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();

        var projectedClaims = claims
            .OrderBy(claim => claim.Ordinal)
            .Select(claim => new AuditClaimEvidenceResponse(
                claim.Id,
                claim.Ordinal,
                claim.Text,
                claim.CitationPresent,
                claim.SupportStatus.ToString(),
                claim.ReviewStatus.ToString(),
                authorizedByClaim[claim.Id]
                    .Select(evidence => new AuditEvidenceResponse(
                        evidence.Id,
                        evidence.Ordinal,
                        evidence.SourceKind.ToString(),
                        evidence.SourceReference,
                        evidence.SourceTitleSnapshot,
                        evidence.PassageSnapshot,
                        evidence.LocationSnapshot,
                        evidence.SourceEventAuditId.HasValue &&
                        authorizedEventIds.Contains(evidence.SourceEventAuditId.Value)
                            ? evidence.SourceEventAuditId
                            : null,
                        AuditSourceIdentity.Create(evidence.SourceKind, evidence.SourceReference),
                        evidence.SourcePublisherSnapshot,
                        evidence.SourceTypeSnapshot,
                        evidence.SourceClassification.ToString(),
                        evidence.PublishedAtSnapshot,
                        evidence.RetrievedAtSnapshot,
                        evidence.ContentHashSnapshot,
                        evidence.SourceVersionSnapshot,
                        evidence.VerificationStatus.ToString()))
                    .ToList()))
            .ToList();

        return new AuditClaimsEvidenceResponse(
            version.Artifact.Id,
            version.Id,
            version.VersionNumber,
            version.Artifact.Name,
            projectedClaims);
    }

    private async Task<bool> CanViewSourceAsync(
        Guid userId,
        ArtifactEvidenceSourceKind sourceKind,
        string sourceReference,
        CancellationToken cancellationToken)
    {
        if (sourceKind == ArtifactEvidenceSourceKind.WebSnapshot)
        {
            return true;
        }

        if (!Guid.TryParse(sourceReference, out var sourceId) || sourceId == Guid.Empty)
        {
            return false;
        }

        if (sourceKind == ArtifactEvidenceSourceKind.ArtifactVersion)
        {
            return await artifactAuthorization.CanDownloadArtifactVersion(userId, sourceId, cancellationToken);
        }

        if (sourceKind != ArtifactEvidenceSourceKind.FileAttachment)
        {
            return false;
        }

        var attachment = await files.GetAttachmentAsync(sourceId, cancellationToken);
        return attachment is not null &&
            !attachment.DeletedAt.HasValue &&
            await fileAuthorization.CanViewAttachment(userId, attachment, cancellationToken);
    }
}
