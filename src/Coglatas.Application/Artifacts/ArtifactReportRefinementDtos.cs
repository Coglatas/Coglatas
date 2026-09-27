using System.Text.Json.Serialization;
using Coglatas.Application.Common;

namespace Coglatas.Application.Artifacts;

// API response properties are consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactReportRefinementTargetKind
{
    Section = 0,
    Claim = 1
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefineArtifactReportRequest(
    ArtifactReportRefinementTargetKind TargetKind,
    Guid TargetLogicalId,
    string? Feedback,
    [param: System.ComponentModel.DataAnnotations.Range(typeof(long), "1", "9223372036854775807")] long ConfirmedProjectScopeVersion,
    long? ConfirmedTaskOverrideVersion,
    Guid? ConfirmedResearchPlanRevisionId,
    long? ConfirmedResearchPlanRevisionNo) : System.ComponentModel.DataAnnotations.IValidatableObject
{
    public IEnumerable<System.ComponentModel.DataAnnotations.ValidationResult> Validate(
        System.ComponentModel.DataAnnotations.ValidationContext validationContext)
    {
        if (ConfirmedResearchPlanRevisionId.HasValue != ConfirmedResearchPlanRevisionNo.HasValue)
        {
            yield return new System.ComponentModel.DataAnnotations.ValidationResult(
                "Research Plan revision id and revision number must be supplied together.",
                [nameof(ConfirmedResearchPlanRevisionId), nameof(ConfirmedResearchPlanRevisionNo)]);
        }
    }
}

public sealed record ArtifactReportRefinementScopeResponse(
    string Origin,
    long ProjectScopeVersion,
    long? TaskOverrideVersion,
    bool WebEnabled,
    bool ProjectFilesEnabled,
    int SourcePolicySchemaVersion,
    Guid? ResearchPlanRevisionId,
    long? ResearchPlanRevisionNo,
    string Provider);

public sealed record ArtifactReportRefinementPreflightResponse(
    Guid ProjectId,
    Guid TaskItemId,
    Guid ArtifactId,
    Guid BaseArtifactVersionId,
    int BaseVersionNumber,
    ArtifactReportRefinementTargetKind TargetKind,
    Guid TargetLogicalId,
    string TargetLabel,
    ArtifactReportRefinementScopeResponse Scope,
    bool CanRefine,
    string? RestrictionCode,
    string ChangesApplyTo);

public sealed record ArtifactReportRefinementResponse(
    Guid ProjectId,
    Guid TaskItemId,
    Guid ArtifactId,
    Guid BaseArtifactVersionId,
    Guid ArtifactVersionId,
    int VersionNumber,
    ArtifactReportRefinementTargetKind TargetKind,
    Guid TargetLogicalId,
    int RefreshedClaimCount,
    int EvidenceAdded);

public interface IArtifactReportRefinementService
{
    Task<Result<ArtifactReportRefinementPreflightResponse>> PreflightAsync(
        Guid projectId,
        Guid baseArtifactVersionId,
        ArtifactReportRefinementTargetKind targetKind,
        Guid targetLogicalId,
        CancellationToken cancellationToken = default);

    Task<Result<ArtifactReportRefinementResponse>> RefineAsync(
        Guid projectId,
        Guid baseArtifactVersionId,
        RefineArtifactReportRequest request,
        CancellationToken cancellationToken = default);
}
