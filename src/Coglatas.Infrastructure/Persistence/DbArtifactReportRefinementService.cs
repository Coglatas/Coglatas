using System.Text;
using Coglatas.Application.Artifacts;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Issue #375 copy-on-write refinement for structured reports. V1 intentionally
/// refines evidence only: it re-reads the currently authorized Task Project
/// Files for the selected Claim (or the Claims cited by the selected Section),
/// then writes a complete new immutable ArtifactVersion/Report snapshot.
/// Untargeted report text, logical identities and citations are copied exactly.
/// </summary>
public sealed class DbArtifactReportRefinementService(
    AppDbContext db,
    IArtifactRepository artifacts,
    IArtifactAuthorizationService artifactAuthorization,
    IProjectAuthorizationService projectAuthorization,
    ITaskExecutionScopeService executionScopes,
    IResearchPlanRepository researchPlans,
    IFileRepository files,
    IFileAuthorizationService fileAuthorization,
    IFileStorageService storage,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork) : IArtifactReportRefinementService
{
    private const string Provider = "LocalizedProjectFilesEvidenceRefineV1";
    private const int MaxFeedbackLength = 1_000;
    private const int MaxEvidencePerClaim = 20;
    private const int MaxNewEvidencePerClaim = 3;
    private const int MaxPassageLength = 1_500;
    private const int MaxQueryTokens = 24;

    public async Task<Result<ArtifactReportRefinementPreflightResponse>> PreflightAsync(
        Guid projectId,
        Guid baseArtifactVersionId,
        ArtifactReportRefinementTargetKind targetKind,
        Guid targetLogicalId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
            return Failure<ArtifactReportRefinementPreflightResponse>("AuthenticationRequired", "Authentication is required.");

        var context = await LoadContextAsync(projectId, baseArtifactVersionId, userId, cancellationToken);
        if (context is null)
            return NotFound<ArtifactReportRefinementPreflightResponse>();

        var target = ResolveTarget(context, targetKind, targetLogicalId);
        if (target is null)
            return Failure<ArtifactReportRefinementPreflightResponse>("ReportRefinementTargetNotFound", "The selected report target is not available.");

        var snapshot = await ReadScopeSnapshotAsync(context.Task.Id, cancellationToken);
        if (snapshot is null)
            return Failure<ArtifactReportRefinementPreflightResponse>("ReportRefinementUnavailable", "The report refinement scope is unavailable.");

        var (canRefine, restrictionCode) = EvaluatePolicy(snapshot.Policy);
        return Result<ArtifactReportRefinementPreflightResponse>.Success(new(
            projectId,
            context.Task.Id,
            context.Version.ArtifactId,
            context.Version.Id,
            context.Version.VersionNumber,
            targetKind,
            targetLogicalId,
            target.Label,
            ToScopeResponse(snapshot),
            canRefine,
            restrictionCode,
            targetKind == ArtifactReportRefinementTargetKind.Claim
                ? "Only evidence for the selected Claim is refreshed. Every other Claim and Section is copied unchanged."
                : "Only evidence for Claims cited by the selected Section is refreshed. Report text and every other Section are copied unchanged."));
    }

    public async Task<Result<ArtifactReportRefinementResponse>> RefineAsync(
        Guid projectId,
        Guid baseArtifactVersionId,
        RefineArtifactReportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
            return Failure<ArtifactReportRefinementResponse>("AuthenticationRequired", "Authentication is required.");
        if (request is null || request.TargetLogicalId == Guid.Empty)
            return Validation<ArtifactReportRefinementResponse>();

        var feedback = request.Feedback?.Trim();
        if (feedback?.Length > MaxFeedbackLength)
            return Validation<ArtifactReportRefinementResponse>();
        var context = await LoadContextAsync(projectId, baseArtifactVersionId, userId, cancellationToken);
        if (context is null)
            return NotFound<ArtifactReportRefinementResponse>();
        if (context.Version.Artifact?.CurrentVersionId != context.Version.Id)
            return Conflict<ArtifactReportRefinementResponse>("ReportRefinementStaleVersion", "A newer report version already exists. Review that version before refining again.");

        var target = ResolveTarget(context, request.TargetKind, request.TargetLogicalId);
        if (target is null)
            return Failure<ArtifactReportRefinementResponse>("ReportRefinementTargetNotFound", "The selected report target is not available.");
        if (target.ClaimIds.Count == 0)
            return Failure<ArtifactReportRefinementResponse>("ReportRefinementTargetHasNoClaims", "The selected Section has no cited Claims to refresh.");

        var snapshot = await ReadScopeSnapshotAsync(context.Task.Id, cancellationToken);
        if (snapshot is null)
            return Failure<ArtifactReportRefinementResponse>("ReportRefinementUnavailable", "The report refinement scope is unavailable.");
        if (!MatchesConfirmation(snapshot, request))
            return Conflict<ArtifactReportRefinementResponse>("ReportRefinementScopeChanged", "The source scope or Research Plan changed after confirmation. Review the current scope and confirm again.");

        var (canRefine, restrictionCode) = EvaluatePolicy(snapshot.Policy);
        if (!canRefine)
            return Failure<ArtifactReportRefinementResponse>(restrictionCode!, "The current source scope cannot be executed by the localized refinement provider.");

        var sources = await MaterializeAuthorizedSourcesAsync(context, snapshot.Policy, userId, cancellationToken);
        if (sources.Count == 0)
            return Failure<ArtifactReportRefinementResponse>("ReportRefinementNoAuthorizedSources", "No authorized text Project Files are available for localized refinement.");

        var additions = new Dictionary<Guid, List<RefreshedEvidence>>();
        foreach (var claimId in target.ClaimIds)
        {
            if (!context.Claims.TryGetValue(claimId, out var claim))
                return NotFound<ArtifactReportRefinementResponse>();

            var newEvidence = FindEvidence(claim, feedback, sources);
            if (newEvidence.Count > 0)
                additions[claim.Id] = newEvidence;
        }

        var evidenceAdded = additions.Values.Sum(items => items.Count);
        if (evidenceAdded == 0)
            return Failure<ArtifactReportRefinementResponse>("ReportRefinementNoNewEvidence", "No new matching evidence was found in the confirmed Project Files scope.");

        // Re-read the mutable scope/plan after source I/O so a confirmation does
        // not silently survive a change made while refinement was running.
        var finalSnapshot = await ReadScopeSnapshotAsync(context.Task.Id, cancellationToken);
        if (finalSnapshot is null || !MatchesConfirmation(finalSnapshot, request))
            return Conflict<ArtifactReportRefinementResponse>("ReportRefinementScopeChanged", "The source scope or Research Plan changed while refinement was running. Review and confirm again.");

        if (context.Version.Attachment is not { DeletedAt: null } baseAttachment ||
            context.Version.FileObject is not { DeletedAt: null, Status: FileObjectStatus.Active } fileObject)
            return Failure<ArtifactReportRefinementResponse>("ReportRefinementUnavailable", "The base Artifact version cannot be copied safely.");

        var nextVersionNumber = await artifacts.GetNextVersionNumberAsync(context.Version.ArtifactId, cancellationToken);
        var newVersion = new ArtifactVersion
        {
            TenantId = context.Version.TenantId,
            ArtifactId = context.Version.ArtifactId,
            Artifact = context.Version.Artifact,
            VersionNumber = nextVersionNumber,
            FileObjectId = fileObject.Id,
            FileObject = fileObject,
            Notes = $"Localized report refinement from v{context.Version.VersionNumber}: {request.TargetKind} {request.TargetLogicalId:D}",
            CreatedByUserId = userId
        };
        var newAttachment = CloneAttachment(baseAttachment, newVersion.Id, fileObject);
        newVersion.AttachmentId = newAttachment.Id;
        newVersion.Attachment = newAttachment;

        var claimMap = CloneClaims(context, newVersion, additions, finalSnapshot, clock.UtcNow);
        if (context.Document.Sections.SelectMany(section => section.Citations).Any(citation => !claimMap.ContainsKey(citation.ArtifactClaimId)))
            return NotFound<ArtifactReportRefinementResponse>();

        var document = CloneReport(context.Document, newVersion, claimMap);
        await files.AddAttachmentAsync(newAttachment, cancellationToken);
        await artifacts.AddVersionAsync(newVersion, cancellationToken);
        db.Set<ArtifactClaim>().AddRange(claimMap.Values);
        db.Set<ArtifactReportDocument>().Add(document);

        context.Version.Artifact!.CurrentVersionId = newVersion.Id;
        var occurredAt = clock.UtcNow;
        db.ActivityLogs.Add(new ActivityLog
        {
            TenantId = context.Version.TenantId,
            ProjectId = projectId,
            TaskItemId = context.Task.Id,
            AuthorUserId = userId,
            ActivityType = ActivityLogType.Decision,
            Body = $"Report refinement created version {nextVersionNumber} from version {context.Version.VersionNumber}; target {request.TargetKind} {request.TargetLogicalId:D}; {evidenceAdded} evidence item(s) added.",
            OccurredAt = occurredAt
        });
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "ArtifactReportRefined",
            "ArtifactVersion",
            newVersion.Id,
            "A localized report evidence refinement created a new immutable Artifact version.",
            ProjectId: projectId,
            TenantId: context.Version.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["baseArtifactVersionId"] = context.Version.Id,
                ["targetKind"] = request.TargetKind.ToString(),
                ["targetLogicalId"] = request.TargetLogicalId,
                ["refreshedClaimCount"] = additions.Count,
                ["evidenceAdded"] = evidenceAdded,
                ["provider"] = Provider,
                ["projectScopeVersion"] = finalSnapshot.ProjectScopeVersion,
                ["taskOverrideVersion"] = finalSnapshot.TaskOverrideVersion,
                ["researchPlanRevisionId"] = finalSnapshot.ResearchPlanRevisionId,
                ["researchPlanRevisionNo"] = finalSnapshot.ResearchPlanRevisionNo
            }), cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict<ArtifactReportRefinementResponse>("ReportRefinementConcurrentUpdate", "Another report refinement won the version update. Reload the current report before retrying.");
        }

        return Result<ArtifactReportRefinementResponse>.Success(new(
            projectId,
            context.Task.Id,
            context.Version.ArtifactId,
            context.Version.Id,
            newVersion.Id,
            nextVersionNumber,
            request.TargetKind,
            request.TargetLogicalId,
            additions.Count,
            evidenceAdded));
    }

    private async Task<ReportContext?> LoadContextAsync(
        Guid projectId,
        Guid versionId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty || versionId == Guid.Empty ||
            !await projectAuthorization.CanViewProject(userId, projectId, cancellationToken))
            return null;

        var version = await artifacts.GetVersionAsync(versionId, cancellationToken);
        if (version?.Artifact is null || version.DeletedAt.HasValue || version.Artifact.DeletedAt.HasValue ||
            version.Artifact.ProjectId != projectId ||
            !await artifactAuthorization.CanViewArtifact(userId, version.ArtifactId, cancellationToken) ||
            !await artifactAuthorization.CanUpdateArtifact(userId, version.ArtifactId, cancellationToken) ||
            version.Artifact.TaskItemId is not { } taskId)
            return null;

        var task = await db.TaskItems.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == taskId &&
            item.TenantId == version.TenantId &&
            item.ProjectId == projectId &&
            !item.DeletedAt.HasValue,
            cancellationToken);
        if (task is null)
            return null;

        var document = await db.Set<ArtifactReportDocument>()
            .AsNoTracking()
            .Include(item => item.Sections)
            .ThenInclude(section => section.Citations)
            .SingleOrDefaultAsync(item => item.ArtifactVersionId == versionId, cancellationToken);
        if (document is null || document.SchemaVersion != 1)
            return null;

        var claims = await db.Set<ArtifactClaim>()
            .AsNoTracking()
            .Include(claim => claim.Evidence)
            .Where(claim => claim.ArtifactVersionId == versionId)
            .OrderBy(claim => claim.Ordinal)
            .ToListAsync(cancellationToken);
        if (claims.Count == 0)
            return null;

        return new ReportContext(version, task, document, claims.ToDictionary(claim => claim.Id));
    }

    private async Task<ScopeSnapshot?> ReadScopeSnapshotAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var scopeResult = await executionScopes.GetTaskScopeAsync(taskId, cancellationToken);
        if (!scopeResult.IsSuccess || scopeResult.Value is not { } scope)
            return null;

        var plan = await researchPlans.GetCurrentExecutionSnapshotForTaskAsync(taskId, cancellationToken);
        var policy = scope.EffectivePolicy.PolicyV2 ?? TaskExecutionSourcePolicyV2.FromLegacy(
            scope.EffectivePolicy.WebEnabled,
            scope.EffectivePolicy.ProjectFilesEnabled);
        return new ScopeSnapshot(
            scope.Origin.ToString(),
            scope.ProjectDefaultVersion,
            scope.TaskOverrideVersion,
            policy,
            plan?.RevisionId,
            plan?.RevisionNo);
    }

    private static (bool CanRefine, string? RestrictionCode) EvaluatePolicy(TaskExecutionSourcePolicyV2 policy)
    {
        if (!policy.ProjectFilesEnabled)
            return (false, "ReportRefinementProjectFilesRequired");
        if (policy.HasUnsupportedExecutableSources)
            return (false, "ReportRefinementUnsupportedSources");
        return (true, null);
    }

    private static bool MatchesConfirmation(ScopeSnapshot snapshot, RefineArtifactReportRequest request) =>
        snapshot.ProjectScopeVersion == request.ConfirmedProjectScopeVersion &&
        snapshot.TaskOverrideVersion == request.ConfirmedTaskOverrideVersion &&
        snapshot.ResearchPlanRevisionId == request.ConfirmedResearchPlanRevisionId &&
        snapshot.ResearchPlanRevisionNo == request.ConfirmedResearchPlanRevisionNo;

    private static ArtifactReportRefinementScopeResponse ToScopeResponse(ScopeSnapshot snapshot) => new(
        snapshot.Origin,
        snapshot.ProjectScopeVersion,
        snapshot.TaskOverrideVersion,
        snapshot.Policy.WebEnabled,
        snapshot.Policy.ProjectFilesEnabled,
        snapshot.Policy.SchemaVersion,
        snapshot.ResearchPlanRevisionId,
        snapshot.ResearchPlanRevisionNo,
        Provider);

    private async Task<IReadOnlyList<MaterializedRefinementSource>> MaterializeAuthorizedSourcesAsync(
        ReportContext context,
        TaskExecutionSourcePolicyV2 policy,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var attachments = await files.ListTaskAttachmentsAsync(context.Task.Id, cancellationToken);
        var candidates = new List<(Attachment Attachment, TaskExecutionSourceState State)>();
        foreach (var attachment in attachments)
        {
            if (attachment.FileObject is not { } fileObject ||
                attachment.TenantId != context.Version.TenantId ||
                attachment.WorkspaceId != context.Task.WorkspaceId ||
                attachment.OwnerType != AttachmentOwnerType.TaskItem ||
                attachment.OwnerId != context.Task.Id ||
                attachment.DeletedAt.HasValue ||
                attachment.ScanStatus != FileScanStatus.Clean ||
                fileObject.TenantId != context.Version.TenantId ||
                fileObject.WorkspaceId != context.Task.WorkspaceId ||
                fileObject.ProjectId != context.Task.ProjectId ||
                fileObject.DeletedAt.HasValue ||
                fileObject.Status != FileObjectStatus.Active ||
                FirstPartyProjectFilesMaterializationV1.NormalizeSupportedMediaType(fileObject.ContentType) is null)
                continue;

            var state = policy.Resolve(
                TaskExecutionSourceKind.ProjectFile,
                TaskExecutionSourcePolicyV2.ProjectFileSourceId(fileObject.Id));
            if (state == TaskExecutionSourceState.Exclude ||
                !await fileAuthorization.CanViewAttachment(userId, attachment, cancellationToken))
                continue;
            candidates.Add((attachment, state));
        }

        var results = new List<MaterializedRefinementSource>();
        var remaining = FirstPartyProjectFilesMaterializationV1.MaxTotalBytes;
        foreach (var candidate in candidates
                     .OrderBy(item => item.State == TaskExecutionSourceState.Prioritize ? 0 : 1)
                     .ThenBy(item => item.Attachment.CreatedAt)
                     .ThenBy(item => item.Attachment.Id)
                     .Take(FirstPartyProjectFilesMaterializationV1.MaxSourceCount))
        {
            if (remaining <= 0 || candidate.Attachment.FileObject is not { } fileObject)
                break;
            var limit = Math.Min(FirstPartyProjectFilesMaterializationV1.MaxSourceBytes, remaining);
            if (fileObject.SizeBytes < 0 || fileObject.SizeBytes > limit)
                continue;

            TaskExecutionMaterializedText? materialized;
            try
            {
                await using var stream = await storage.OpenReadAsync(fileObject.StorageKey, cancellationToken);
                materialized = await FirstPartyProjectFilesMaterializationV1.ReadUtf8Async(
                    stream,
                    fileObject.ContentType,
                    limit,
                    cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (materialized is null || materialized.ByteCount != fileObject.SizeBytes ||
                (!string.IsNullOrWhiteSpace(fileObject.HashSha256) &&
                 !string.Equals(fileObject.HashSha256.Trim(), materialized.ContentSha256, StringComparison.OrdinalIgnoreCase)))
                continue;

            var current = await files.GetAttachmentAsync(candidate.Attachment.Id, cancellationToken);
            if (current?.FileObject is not { } currentFile ||
                current.DeletedAt.HasValue || current.ScanStatus != FileScanStatus.Clean ||
                current.FileObjectId != fileObject.Id || currentFile.DeletedAt.HasValue || currentFile.Status != FileObjectStatus.Active ||
                !string.Equals(currentFile.StorageKey, fileObject.StorageKey, StringComparison.Ordinal) ||
                currentFile.SizeBytes != fileObject.SizeBytes ||
                !string.Equals(currentFile.ContentType, fileObject.ContentType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(currentFile.HashSha256, fileObject.HashSha256, StringComparison.OrdinalIgnoreCase) ||
                !await fileAuthorization.CanViewAttachment(userId, current, cancellationToken))
                continue;

            results.Add(new MaterializedRefinementSource(
                current.Id,
                currentFile.Id,
                current.FileName,
                materialized.MediaType,
                materialized.ContentSha256,
                materialized.ByteCount,
                materialized.Text,
                candidate.State));
            remaining -= checked((int)materialized.ByteCount);
        }

        return results;
    }

    private static List<RefreshedEvidence> FindEvidence(
        ArtifactClaim claim,
        string? feedback,
        IReadOnlyList<MaterializedRefinementSource> sources)
    {
        var slots = Math.Max(0, MaxEvidencePerClaim - claim.Evidence.Count);
        if (slots == 0)
            return [];

        var tokens = BuildQueryTokens(claim.Text, feedback);
        if (tokens.Count == 0)
            return [];

        var existing = claim.Evidence
            .Select(item => (item.SourceReference, item.ContentHashSnapshot, item.PassageSnapshot))
            .ToHashSet();
        var matches = new List<(RefreshedEvidence Evidence, int Score, int Priority)>();
        foreach (var source in sources)
        {
            var passage = FindBestPassage(source.Text, tokens, out var score);
            if (passage is null || score <= 0)
                continue;
            var sourceReference = source.AttachmentId.ToString("D");
            if (existing.Contains((sourceReference, source.ContentSha256, passage)))
                continue;

            matches.Add((new RefreshedEvidence(
                source.AttachmentId,
                source.FileObjectId,
                source.FileName,
                source.MediaType,
                source.ContentSha256,
                passage,
                source.ByteCount),
                score,
                source.State == TaskExecutionSourceState.Prioritize ? 0 : 1));
        }

        return matches
            .OrderBy(item => item.Priority)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Evidence.AttachmentId)
            .Take(Math.Min(slots, MaxNewEvidencePerClaim))
            .Select(item => item.Evidence)
            .ToList();
    }

    private static IReadOnlyList<string> BuildQueryTokens(string claimText, string? feedback)
    {
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTokens(claimText, tokens, seen);
        if (!string.IsNullOrWhiteSpace(feedback) && tokens.Count < MaxQueryTokens)
            AddTokens(feedback, tokens, seen);
        return tokens;
    }

    private static void AddTokens(string text, List<string> tokens, HashSet<string> seen)
    {
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length == 0 || tokens.Count >= MaxQueryTokens)
            {
                current.Clear();
                return;
            }
            var value = current.ToString();
            current.Clear();
            var nonAscii = value.Any(character => character > 127);
            if ((!nonAscii && value.Length < 4) || (nonAscii && value.Length < 2))
                return;

            if (value.Length <= 48 && seen.Add(value))
                tokens.Add(value);

            if (nonAscii && value.Length > 4)
            {
                for (var index = 0; index + 2 <= value.Length && tokens.Count < MaxQueryTokens; index += 2)
                {
                    var shingle = value.Substring(index, Math.Min(3, value.Length - index));
                    if (seen.Add(shingle)) tokens.Add(shingle);
                }
            }
        }

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
                current.Append(character);
            else
                Flush();
            if (tokens.Count >= MaxQueryTokens) break;
        }
        Flush();
    }

    private static string? FindBestPassage(string text, IReadOnlyList<string> tokens, out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var first = -1;
        foreach (var token in tokens)
        {
            var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            score++;
            if (first < 0 || index < first) first = index;
        }
        if (score == 0 || first < 0)
            return null;

        var start = Math.Max(0, first - MaxPassageLength / 3);
        var length = Math.Min(MaxPassageLength, text.Length - start);
        var passage = text.Substring(start, length).Trim();
        return passage.Length == 0 ? null : passage;
    }

    private static Dictionary<Guid, ArtifactClaim> CloneClaims(
        ReportContext context,
        ArtifactVersion newVersion,
        IReadOnlyDictionary<Guid, List<RefreshedEvidence>> additions,
        ScopeSnapshot snapshot,
        DateTimeOffset retrievedAt)
    {
        var map = new Dictionary<Guid, ArtifactClaim>(context.Claims.Count);
        foreach (var oldClaim in context.Claims.Values.OrderBy(item => item.Ordinal))
        {
            var claim = new ArtifactClaim
            {
                TenantId = oldClaim.TenantId,
                ArtifactVersionId = newVersion.Id,
                ArtifactVersion = newVersion,
                LogicalClaimId = oldClaim.LogicalClaimId,
                Ordinal = oldClaim.Ordinal,
                Text = oldClaim.Text,
                CitationPresent = oldClaim.CitationPresent,
                SupportStatus = oldClaim.SupportStatus,
                ReviewStatus = oldClaim.ReviewStatus
            };
            foreach (var oldEvidence in oldClaim.Evidence.OrderBy(item => item.Ordinal))
                claim.Evidence.Add(CloneEvidence(oldEvidence, claim));

            var ordinal = claim.Evidence.Count == 0 ? 1 : claim.Evidence.Max(item => item.Ordinal) + 1;
            if (additions.TryGetValue(oldClaim.Id, out var items))
            {
                foreach (var item in items)
                {
                    claim.Evidence.Add(new ArtifactEvidence
                    {
                        TenantId = oldClaim.TenantId,
                        ArtifactClaimId = claim.Id,
                        ArtifactClaim = claim,
                        Ordinal = ordinal++,
                        SourceKind = ArtifactEvidenceSourceKind.FileAttachment,
                        SourceReference = item.AttachmentId.ToString("D"),
                        SourceTitleSnapshot = item.FileName,
                        SourceTypeSnapshot = item.MediaType,
                        SourceClassification = ArtifactEvidenceSourceClassification.Primary,
                        RetrievedAtSnapshot = retrievedAt,
                        ContentHashSnapshot = item.ContentSha256,
                        SourceVersionSnapshot = item.FileObjectId.ToString("D"),
                        VerificationStatus = ArtifactEvidenceVerificationStatus.Verified,
                        PassageSnapshot = item.Passage,
                        LocationSnapshot = $"{Provider}; project-scope-v{snapshot.ProjectScopeVersion}"
                    });
                }
            }
            map[oldClaim.Id] = claim;
        }
        return map;
    }

    private static ArtifactEvidence CloneEvidence(ArtifactEvidence source, ArtifactClaim claim) => new()
    {
        TenantId = source.TenantId,
        ArtifactClaimId = claim.Id,
        ArtifactClaim = claim,
        Ordinal = source.Ordinal,
        SourceKind = source.SourceKind,
        SourceReference = source.SourceReference,
        SourceTitleSnapshot = source.SourceTitleSnapshot,
        SourcePublisherSnapshot = source.SourcePublisherSnapshot,
        SourceTypeSnapshot = source.SourceTypeSnapshot,
        SourceClassification = source.SourceClassification,
        PublishedAtSnapshot = source.PublishedAtSnapshot,
        RetrievedAtSnapshot = source.RetrievedAtSnapshot,
        ContentHashSnapshot = source.ContentHashSnapshot,
        SourceVersionSnapshot = source.SourceVersionSnapshot,
        VerificationStatus = source.VerificationStatus,
        PassageSnapshot = source.PassageSnapshot,
        LocationSnapshot = source.LocationSnapshot,
        SourceEventAuditId = source.SourceEventAuditId
    };

    private static ArtifactReportDocument CloneReport(
        ArtifactReportDocument source,
        ArtifactVersion newVersion,
        IReadOnlyDictionary<Guid, ArtifactClaim> claimMap)
    {
        var document = new ArtifactReportDocument
        {
            TenantId = source.TenantId,
            ArtifactVersionId = newVersion.Id,
            ArtifactVersion = newVersion,
            SchemaVersion = source.SchemaVersion,
            Title = source.Title
        };
        foreach (var oldSection in source.Sections.OrderBy(item => item.Ordinal))
        {
            var section = new ArtifactReportSection
            {
                TenantId = oldSection.TenantId,
                ArtifactReportDocumentId = document.Id,
                Document = document,
                LogicalSectionId = oldSection.LogicalSectionId,
                Ordinal = oldSection.Ordinal,
                Heading = oldSection.Heading,
                BodyText = oldSection.BodyText
            };
            foreach (var oldCitation in oldSection.Citations.OrderBy(item => item.Ordinal))
            {
                var claim = claimMap[oldCitation.ArtifactClaimId];
                section.Citations.Add(new ArtifactReportCitation
                {
                    TenantId = oldCitation.TenantId,
                    ArtifactReportSectionId = section.Id,
                    Section = section,
                    Ordinal = oldCitation.Ordinal,
                    AnchorStartUtf16 = oldCitation.AnchorStartUtf16,
                    AnchorLengthUtf16 = oldCitation.AnchorLengthUtf16,
                    ArtifactClaimId = claim.Id,
                    Claim = claim
                });
            }
            document.Sections.Add(section);
        }
        return document;
    }

    private static Attachment CloneAttachment(Attachment source, Guid newVersionId, FileObject fileObject) => new()
    {
        TenantId = source.TenantId,
        FileObjectId = source.FileObjectId,
        FileObject = fileObject,
        WorkspaceId = source.WorkspaceId,
        OwnerType = AttachmentOwnerType.ArtifactVersion,
        OwnerId = newVersionId,
        OwnerUserId = source.OwnerUserId,
        UploadedByUserId = source.UploadedByUserId,
        FileName = source.FileName,
        StoredFileName = source.StoredFileName,
        FilePath = source.FilePath,
        ContentType = source.ContentType,
        Extension = source.Extension,
        SizeBytes = source.SizeBytes,
        StorageProvider = source.StorageProvider,
        StorageKey = source.StorageKey,
        ScanStatus = source.ScanStatus
    };

    private static RefinementTarget? ResolveTarget(
        ReportContext context,
        ArtifactReportRefinementTargetKind targetKind,
        Guid targetLogicalId)
    {
        if (targetLogicalId == Guid.Empty)
            return null;
        if (targetKind == ArtifactReportRefinementTargetKind.Section)
        {
            var section = context.Document.Sections.SingleOrDefault(item => item.LogicalSectionId == targetLogicalId);
            return section is null
                ? null
                : new RefinementTarget(
                    Truncate(section.Heading, 180),
                    section.Citations.Select(item => item.ArtifactClaimId).Distinct().ToArray());
        }

        var claim = context.Claims.Values.SingleOrDefault(item => item.LogicalClaimId == targetLogicalId);
        if (claim is null || context.Document.Sections.SelectMany(item => item.Citations).All(item => item.ArtifactClaimId != claim.Id))
            return null;
        return new RefinementTarget(Truncate(claim.Text, 180), [claim.Id]);
    }

    private bool TryCurrentUser(out Guid userId)
    {
        userId = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated && userId != Guid.Empty;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private static Result<T> NotFound<T>() =>
        Failure<T>("ReportNotFound", "The report is not available.");
    private static Result<T> Validation<T>() =>
        Failure<T>("ValidationFailed", "The report refinement request is invalid.");
    private static Result<T> Conflict<T>(string code, string message) => Failure<T>(code, message);
    private static Result<T> Failure<T>(string code, string message) =>
        Result<T>.Failure(new ApplicationErrorDetail(code, message));

    private sealed record ReportContext(
        ArtifactVersion Version,
        TaskItem Task,
        ArtifactReportDocument Document,
        IReadOnlyDictionary<Guid, ArtifactClaim> Claims);

    private sealed record RefinementTarget(string Label, IReadOnlyList<Guid> ClaimIds);

    private sealed record ScopeSnapshot(
        string Origin,
        long ProjectScopeVersion,
        long? TaskOverrideVersion,
        TaskExecutionSourcePolicyV2 Policy,
        Guid? ResearchPlanRevisionId,
        long? ResearchPlanRevisionNo);

    private sealed record MaterializedRefinementSource(
        Guid AttachmentId,
        Guid FileObjectId,
        string FileName,
        string MediaType,
        string ContentSha256,
        long ByteCount,
        string Text,
        TaskExecutionSourceState State);

    private sealed record RefreshedEvidence(
        Guid AttachmentId,
        Guid FileObjectId,
        string FileName,
        string MediaType,
        string ContentSha256,
        string Passage,
        long ByteCount);
}
