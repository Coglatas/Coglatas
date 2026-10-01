using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Announcements;

/// <summary>
/// Durable announcement publication workflow. #388 extends the #378 single
/// target contract with one independently-persisted target set while retaining
/// one content aggregate. Target memberships remain live until dispatch; the
/// worker then de-duplicates by UserId and commits one logical Announcement
/// notification per recipient in the publication transaction.
/// </summary>
public sealed class AnnouncementDraftService(
    IAnnouncementDraftRepository drafts,
    IAnnouncementRepository announcements,
    IAnnouncementAudienceService audiences,
    IAnnouncementScheduleTimeZoneResolver scheduleTimeZones,
    ICreateIdempotencyCoordinator idempotency,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    IClock clock,
    IAuditLogger audit,
    IBusinessInvalidationPublisher invalidations,
    IUnitOfWork unitOfWork,
    INotificationService? notifications = null,
    IAnnouncementDistributionStore? distributionStore = null) : IAnnouncementDraftService, IAnnouncementPublicationProcessor
{
    private const string CreateOperation = "AnnouncementDraft.Create.v1";
    private const string PublishOperation = "AnnouncementDraft.Publish.v1";
    private const string ScheduleOperation = "AnnouncementDraft.Schedule.v1";
    private const string ResourceType = "AnnouncementDraft";
    private const string AudienceNoLongerAuthorized = "AudienceNoLongerAuthorized";
    private const string PublicationWorkerRetry = "PublicationWorkerRetry";

    public async Task<Result<AnnouncementDraftResponse>> CreateAsync(
        CreateAnnouncementDraftRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentActor(out var actorUserId, out var actorError))
        {
            return Failure<AnnouncementDraftResponse>("ANNOUNCEMENT_DRAFT_AUTHENTICATION_REQUIRED", actorError!);
        }
        if (!IsValidIdempotencyKey(idempotencyKey))
        {
            return IdempotencyFailure<AnnouncementDraftResponse>(idempotencyKey);
        }

        var targetsResult = NormalizeTargets(request.Content);
        if (!targetsResult.IsSuccess)
        {
            return Result<AnnouncementDraftResponse>.Failure(targetsResult.ErrorDetail!);
        }
        var targets = targetsResult.Value!;
        var contentValidation = await ValidateContentAsync(actorUserId, request.Content, targets, clock.UtcNow, cancellationToken);
        if (!contentValidation.IsSuccess)
        {
            return Result<AnnouncementDraftResponse>.Failure(contentValidation.ErrorDetail!);
        }

        var draft = new AnnouncementDraft
        {
            TenantId = currentTenant.TenantId,
            AuthorUserId = actorUserId,
            VersionNo = 1
        };

        try
        {
            var result = await idempotency.ExecuteAsync(
                new CreateIdempotencyContext(
                    currentTenant.TenantId,
                    actorUserId,
                    CreateOperation,
                    idempotencyKey.Trim(),
                    Fingerprint(request),
                    ResourceType,
                    draft.Id),
                async token =>
                {
                    ApplyContent(draft, request.Content, targets);
                    await drafts.AddAsync(draft, token);
                    await audit.LogUserActionAsync(
                        actorUserId,
                        "AnnouncementDraftCreated",
                        "AnnouncementDraft",
                        draft.Id,
                        "Announcement draft saved.",
                        new Dictionary<string, object?>
                        {
                            ["version"] = draft.VersionNo,
                            ["targetCount"] = targets.Count
                        },
                        token);
                    if (distributionStore is not null)
                    {
                        await distributionStore.StageCreatedDraftTargetsAsync(
                            currentTenant.TenantId,
                            draft.Id,
                            targets,
                            token);
                    }
                    return draft;
                },
                async (draftId, token) => await drafts.GetAsync(draftId, token),
                cancellationToken);

            if (result.Disposition is IdempotentCreateDisposition.Created or IdempotentCreateDisposition.Replayed &&
                result.Value is not null &&
                await CanAccessDraftAsync(actorUserId, result.Value, cancellationToken))
            {
                return Result<AnnouncementDraftResponse>.Success(
                    await ToResponseAsync(result.Value, cancellationToken));
            }
            return result.Disposition == IdempotentCreateDisposition.RequestMismatch
                ? IdempotencyConflict<AnnouncementDraftResponse>()
                : ReplayUnavailable<AnnouncementDraftResponse>();
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            return Stale<AnnouncementDraftResponse>();
        }
        catch (InvalidOperationException)
        {
            return Failure<AnnouncementDraftResponse>(
                "ANNOUNCEMENT_DRAFT_PERSISTENCE_UNAVAILABLE",
                "The announcement draft could not be saved.");
        }
    }

    public async Task<Result<IReadOnlyList<AnnouncementDraftListItemResponse>>> ListMineAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentActor(out var actorUserId, out var actorError))
        {
            return Failure<IReadOnlyList<AnnouncementDraftListItemResponse>>(
                "ANNOUNCEMENT_DRAFT_AUTHENTICATION_REQUIRED",
                actorError!);
        }

        var candidates = await drafts.ListForAuthorAsync(actorUserId, 50, cancellationToken);
        var items = new List<AnnouncementDraftListItemResponse>(candidates.Count);
        foreach (var draft in candidates)
        {
            if (await CanAccessDraftAsync(actorUserId, draft, cancellationToken))
            {
                items.Add(ToListItem(draft));
            }
        }

        return Result<IReadOnlyList<AnnouncementDraftListItemResponse>>.Success(items);
    }

    public async Task<Result<AnnouncementDraftResponse>> GetAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentActor(out var actorUserId, out _))
        {
            return NotFound<AnnouncementDraftResponse>();
        }

        var draft = await drafts.GetAsync(draftId, cancellationToken);
        return draft is not null && await CanAccessDraftAsync(actorUserId, draft, cancellationToken)
            ? Result<AnnouncementDraftResponse>.Success(await ToResponseAsync(draft, cancellationToken))
            : NotFound<AnnouncementDraftResponse>();
    }

    public async Task<Result<AnnouncementDraftResponse>> SaveAsync(
        Guid draftId,
        SaveAnnouncementDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentActor(out var actorUserId, out _))
        {
            return NotFound<AnnouncementDraftResponse>();
        }

        var draft = await drafts.GetAsync(draftId, cancellationToken);
        if (draft is null || !await CanAccessDraftAsync(actorUserId, draft, cancellationToken))
        {
            return NotFound<AnnouncementDraftResponse>();
        }
        if (draft.Status != AnnouncementDraftStatus.Draft)
        {
            return Failure<AnnouncementDraftResponse>(
                "ANNOUNCEMENT_DRAFT_NOT_EDITABLE",
                "Only a draft can be edited.");
        }
        if (request.ExpectedVersion != draft.VersionNo)
        {
            return Stale<AnnouncementDraftResponse>();
        }

        var targetsResult = NormalizeTargets(request.Content);
        if (!targetsResult.IsSuccess)
        {
            return Result<AnnouncementDraftResponse>.Failure(targetsResult.ErrorDetail!);
        }
        var targets = targetsResult.Value!;
        var validation = await ValidateContentAsync(actorUserId, request.Content, targets, clock.UtcNow, cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<AnnouncementDraftResponse>.Failure(validation.ErrorDetail!);
        }

        ApplyContent(draft, request.Content, targets);
        draft.VersionNo = checked(draft.VersionNo + 1);
        await audit.LogUserActionAsync(
            actorUserId,
            "AnnouncementDraftSaved",
            "AnnouncementDraft",
            draft.Id,
            "Announcement draft updated.",
            new Dictionary<string, object?>
            {
                ["version"] = draft.VersionNo,
                ["targetCount"] = targets.Count
            },
            cancellationToken);

        try
        {
            if (distributionStore is not null)
            {
                await distributionStore.CommitDraftSaveAsync(
                    currentTenant.TenantId,
                    draft.Id,
                    targets,
                    cancellationToken);
            }
            else
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            return Result<AnnouncementDraftResponse>.Success(await ToResponseAsync(draft, cancellationToken));
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            return Stale<AnnouncementDraftResponse>();
        }
        catch (InvalidOperationException)
        {
            return Failure<AnnouncementDraftResponse>(
                "ANNOUNCEMENT_DRAFT_PERSISTENCE_UNAVAILABLE",
                "The announcement draft could not be saved.");
        }
    }

    public Task<Result<AnnouncementDraftResponse>> PublishNowAsync(
        Guid draftId,
        PublishAnnouncementDraftRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            draftId,
            request.ExpectedVersion,
            idempotencyKey,
            PublishOperation,
            Fingerprint(request),
            async (draft, actorUserId, _, token) =>
            {
                var dueAtUtc = clock.UtcNow;
                if (draft.ExpiresAt.HasValue && draft.ExpiresAt.Value <= dueAtUtc)
                {
                    throw new AnnouncementTransitionValidationException(
                        "Announcement expiration must be after publication.");
                }

                draft.Status = AnnouncementDraftStatus.Scheduled;
                draft.ScheduledForUtc = dueAtUtc;
                draft.ScheduleTimeZoneId = "UTC";
                draft.ScheduleLocalDateTime = DateTime.SpecifyKind(
                    dueAtUtc.UtcDateTime,
                    DateTimeKind.Unspecified);
                draft.ScheduleUtcOffsetMinutes = null;
                draft.NextPublicationAttemptAtUtc = dueAtUtc;
                draft.LastPublicationFailureCode = null;
                ClearClaim(draft);
                draft.VersionNo = checked(draft.VersionNo + 1);
                await audit.LogUserActionAsync(
                    actorUserId,
                    "AnnouncementDraftQueuedForImmediatePublication",
                    "AnnouncementDraft",
                    draft.Id,
                    "Announcement publication queued for immediate delivery.",
                    new Dictionary<string, object?>
                    {
                        ["version"] = draft.VersionNo,
                        ["scheduledForUtc"] = dueAtUtc
                    },
                    token);
            },
            cancellationToken);

    public async Task<Result<AnnouncementDraftResponse>> ScheduleAsync(
        Guid draftId,
        ScheduleAnnouncementDraftRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var resolution = ResolveSchedule(request);
        if (!resolution.IsSuccess)
        {
            return Result<AnnouncementDraftResponse>.Failure(resolution.ErrorDetail!);
        }

        return await TransitionAsync(
            draftId,
            request.ExpectedVersion,
            idempotencyKey,
            ScheduleOperation,
            Fingerprint(request),
            async (draft, actorUserId, targets, token) =>
            {
                var accepted = resolution.Value!;
                foreach (var workspaceId in targets.Select(target => target.WorkspaceId).Distinct())
                {
                    var authoritativeZone = await scheduleTimeZones.ResolveAsync(
                        currentTenant.TenantId,
                        workspaceId,
                        token);
                    if (!string.Equals(authoritativeZone.Id, accepted.TimeZoneId, StringComparison.Ordinal))
                    {
                        throw new AnnouncementTransitionValidationException(
                            targets.Count > 1
                                ? "Selected audiences use different organizational time zones. Publish now or select audiences that share one time zone."
                                : "The organizational time zone changed. Review the displayed schedule and try again.");
                    }
                }
                if (accepted.DueAtUtc <= clock.UtcNow)
                {
                    throw new AnnouncementTransitionValidationException(
                        "Scheduled publication must be in the future.");
                }
                if (draft.ExpiresAt.HasValue && draft.ExpiresAt.Value <= accepted.DueAtUtc)
                {
                    throw new AnnouncementTransitionValidationException(
                        "Announcement expiration must be after scheduled publication.");
                }

                draft.Status = AnnouncementDraftStatus.Scheduled;
                draft.ScheduledForUtc = accepted.DueAtUtc;
                draft.ScheduleTimeZoneId = accepted.TimeZoneId;
                draft.ScheduleLocalDateTime = accepted.LocalDateTime;
                draft.ScheduleUtcOffsetMinutes = accepted.AmbiguousTimeOffsetMinutes;
                draft.NextPublicationAttemptAtUtc = accepted.DueAtUtc;
                draft.LastPublicationFailureCode = null;
                ClearClaim(draft);
                draft.VersionNo = checked(draft.VersionNo + 1);
                await audit.LogUserActionAsync(
                    actorUserId,
                    "AnnouncementDraftScheduled",
                    "AnnouncementDraft",
                    draft.Id,
                    "Announcement publication scheduled.",
                    new Dictionary<string, object?>
                    {
                        ["version"] = draft.VersionNo,
                        ["scheduledForUtc"] = accepted.DueAtUtc,
                        ["timeZoneId"] = accepted.TimeZoneId,
                        ["targetCount"] = targets.Count
                    },
                    token);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        drafts.ListActiveTenantIdsAsync(page, pageSize, cancellationToken);

    public Task<IReadOnlyList<AnnouncementPublicationClaim>> ClaimDueAsync(
        string claimOwner,
        DateTimeOffset now,
        int batchSize,
        TimeSpan claimTimeout,
        CancellationToken cancellationToken = default) =>
        drafts.ClaimDueAsync(claimOwner, now, batchSize, claimTimeout, cancellationToken);

    public async Task ProcessAsync(
        AnnouncementPublicationClaim claim,
        DateTimeOffset now,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        var draft = await drafts.GetClaimedAsync(claim.DraftId, claim.ClaimToken, cancellationToken);
        if (draft is null)
        {
            return;
        }

        var targets = await GetDraftTargetsAsync(draft, cancellationToken);
        var validation = await ValidateContentAsync(
            draft.AuthorUserId,
            ToContent(draft, targets),
            targets,
            now,
            cancellationToken);
        if (!validation.IsSuccess)
        {
            await DeferClaimAsync(draft, "DraftValidationFailed", now, retryDelay, cancellationToken);
            return;
        }

        // ValidateContentAsync is the first current-authorization check. Keep a
        // second explicit dispatch check so a scope revoked between validation
        // and recipient resolution fails closed before any delivery ledger row
        // is created.
        foreach (var target in targets)
        {
            var authorized = await audiences.IsAuthorizedForActorAsync(
                draft.AuthorUserId,
                target.WorkspaceId,
                target.GroupId,
                target.ChannelId,
                cancellationToken);
            if (!authorized.IsSuccess)
            {
                await DeferClaimAsync(draft, PublicationWorkerRetry, now, retryDelay, cancellationToken);
                return;
            }
            if (authorized.Value != true)
            {
                await DeferClaimAsync(draft, AudienceNoLongerAuthorized, now, retryDelay, cancellationToken);
                return;
            }
        }

        if (draft.ExpiresAt.HasValue && draft.ExpiresAt.Value <= now)
        {
            await DeferClaimAsync(draft, "AnnouncementExpiredBeforePublication", now, retryDelay, cancellationToken);
            return;
        }

        var recipients = await ResolveRecipientsAsync(draft, targets, cancellationToken);
        await PublishDraftAsync(draft, targets, recipients, now, cancellationToken);
    }

    public async Task RecordFailureAsync(
        AnnouncementPublicationClaim claim,
        DateTimeOffset now,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        var draft = await drafts.GetClaimedAsync(claim.DraftId, claim.ClaimToken, cancellationToken);
        if (draft is not null)
        {
            await DeferClaimAsync(draft, PublicationWorkerRetry, now, retryDelay, cancellationToken);
        }
    }

    private async Task<Result<AnnouncementDraftResponse>> TransitionAsync(
        Guid draftId,
        long expectedVersion,
        string idempotencyKey,
        string operation,
        string fingerprint,
        Func<AnnouncementDraft, Guid, IReadOnlyList<AnnouncementDraftTargetRequest>, CancellationToken, Task> stageTransition,
        CancellationToken cancellationToken)
    {
        if (!TryCurrentActor(out var actorUserId, out _))
        {
            return NotFound<AnnouncementDraftResponse>();
        }
        if (!IsValidIdempotencyKey(idempotencyKey))
        {
            return IdempotencyFailure<AnnouncementDraftResponse>(idempotencyKey);
        }

        var draft = await drafts.GetAsync(draftId, cancellationToken);
        if (draft is null || !await CanAccessDraftAsync(actorUserId, draft, cancellationToken))
        {
            return NotFound<AnnouncementDraftResponse>();
        }
        try
        {
            var result = await idempotency.ExecuteAsync(
                new CreateIdempotencyContext(
                    currentTenant.TenantId,
                    actorUserId,
                    operation,
                    idempotencyKey.Trim(),
                    fingerprint,
                    ResourceType,
                    draft.Id),
                async token =>
                {
                    if (!await CanAccessDraftAsync(actorUserId, draft, token))
                    {
                        throw new AnnouncementTransitionValidationException("The selected audience is no longer authorized.");
                    }
                    if (draft.Status != AnnouncementDraftStatus.Draft)
                    {
                        throw new AnnouncementTransitionNotReadyException();
                    }
                    if (draft.VersionNo != expectedVersion)
                    {
                        throw new AnnouncementTransitionConflictException();
                    }

                    var targets = await GetDraftTargetsAsync(draft, token);
                    var validation = await ValidateContentAsync(
                        actorUserId,
                        ToContent(draft, targets),
                        targets,
                        clock.UtcNow,
                        token);
                    if (!validation.IsSuccess)
                    {
                        throw new AnnouncementTransitionValidationException(
                            validation.ErrorDetail?.Message ?? "Announcement draft is invalid.");
                    }
                    await stageTransition(draft, actorUserId, targets, token);
                    return draft;
                },
                async (existingDraftId, token) => await drafts.GetAsync(existingDraftId, token),
                cancellationToken);

            if (result.Disposition is IdempotentCreateDisposition.Created or IdempotentCreateDisposition.Replayed &&
                result.Value is not null &&
                await CanAccessDraftAsync(actorUserId, result.Value, cancellationToken))
            {
                return Result<AnnouncementDraftResponse>.Success(
                    await ToResponseAsync(result.Value, cancellationToken));
            }
            return result.Disposition == IdempotentCreateDisposition.RequestMismatch
                ? IdempotencyConflict<AnnouncementDraftResponse>()
                : ReplayUnavailable<AnnouncementDraftResponse>();
        }
        catch (AnnouncementTransitionConflictException)
        {
            return Stale<AnnouncementDraftResponse>();
        }
        catch (AnnouncementTransitionNotReadyException)
        {
            return Failure<AnnouncementDraftResponse>(
                "ANNOUNCEMENT_DRAFT_NOT_READY",
                "Only a draft can be published or scheduled.");
        }
        catch (AnnouncementTransitionValidationException exception)
        {
            return Failure<AnnouncementDraftResponse>("ANNOUNCEMENT_DRAFT_TRANSITION_INVALID", exception.Message);
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            return Stale<AnnouncementDraftResponse>();
        }
        catch (InvalidOperationException)
        {
            return Failure<AnnouncementDraftResponse>(
                "ANNOUNCEMENT_DRAFT_PERSISTENCE_UNAVAILABLE",
                "The announcement publication could not be recorded.");
        }
    }

    private async Task<IReadOnlyList<AnnouncementTargetUser>> ResolveRecipientsAsync(
        AnnouncementDraft draft,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        CancellationToken cancellationToken)
    {
        var recipients = new Dictionary<Guid, AnnouncementTargetUser>();
        foreach (var target in targets)
        {
            var prototype = new Announcement
            {
                TenantId = draft.TenantId,
                WorkspaceId = target.WorkspaceId,
                GroupId = target.GroupId,
                ChannelId = target.ChannelId,
                AuthorUserId = draft.AuthorUserId,
                Title = draft.Title,
                Body = draft.Body,
                Priority = draft.Priority,
                PublishedAt = clock.UtcNow
            };
            foreach (var recipient in await announcements.ListTargetUsersAsync(prototype, cancellationToken))
            {
                recipients.TryAdd(recipient.UserId, recipient);
            }
        }

        return recipients.Values.OrderBy(recipient => recipient.UserId).ToArray();
    }

    private async Task PublishDraftAsync(
        AnnouncementDraft draft,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        IReadOnlyList<AnnouncementTargetUser> recipients,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken)
    {
        var primary = targets[0];
        var announcement = new Announcement
        {
            TenantId = draft.TenantId,
            WorkspaceId = primary.WorkspaceId,
            GroupId = primary.GroupId,
            ChannelId = primary.ChannelId,
            AuthorUserId = draft.AuthorUserId,
            Title = draft.Title,
            Body = draft.Body,
            Priority = draft.Priority,
            IsPinned = draft.IsPinned,
            RequiresReadConfirmation = draft.RequiresReadConfirmation,
            PublishedAt = publishedAtUtc,
            ExpiresAt = draft.ExpiresAt
        };

        async Task StagePublication(CancellationToken token)
        {
            await announcements.AddAsync(announcement, token);

            draft.Status = AnnouncementDraftStatus.Published;
            draft.PublishedAnnouncementId = announcement.Id;
            draft.PublishedAtUtc = publishedAtUtc;
            draft.NextPublicationAttemptAtUtc = null;
            draft.LastPublicationFailureCode = null;
            ClearClaim(draft);
            draft.VersionNo = checked(draft.VersionNo + 1);

            await audit.LogUserActionAsync(
                draft.AuthorUserId,
                "AnnouncementPublished",
                "Announcement",
                announcement.Id,
                "Announcement published from durable draft.",
                new Dictionary<string, object?>
                {
                    ["draftId"] = draft.Id,
                    ["draftVersion"] = draft.VersionNo,
                    ["targetCount"] = targets.Count,
                    ["recipientCount"] = recipients.Count
                },
                token);
            await invalidations.AnnouncementChangedAsync(
                announcement,
                draft.AuthorUserId,
                "created",
                recipients.Select(recipient => recipient.UserId),
                token);

            if (notifications is null)
            {
                return;
            }

            var decodedBody = AnnouncementContentContract.Decode(draft.Body).Body;
            var notificationBody = decodedBody.Length > 500 ? decodedBody[..500] : decodedBody;
            var logicalKey = AnnouncementDistributionContract.DeliveryLogicalKey(announcement.Id);
            foreach (var recipient in recipients)
            {
                await notifications.CreateOrGetByLogicalKeyAsync(
                    recipient.UserId,
                    NotificationType.Announcement,
                    announcement.Title,
                    notificationBody,
                    "Announcement",
                    announcement.Id,
                    logicalKey,
                    token);
            }
        }

        if (distributionStore is not null)
        {
            await distributionStore.CommitPublicationAsync(
                draft.TenantId,
                announcement.Id,
                targets,
                StagePublication,
                cancellationToken);
        }
        else
        {
            await StagePublication(cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task DeferClaimAsync(
        AnnouncementDraft draft,
        string failureCode,
        DateTimeOffset now,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        draft.LastPublicationFailureCode = failureCode;
        draft.NextPublicationAttemptAtUtc = now + (retryDelay <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : retryDelay);
        ClearClaim(draft);
        draft.VersionNo = checked(draft.VersionNo + 1);
        await audit.LogUserActionAsync(
            draft.AuthorUserId,
            "AnnouncementPublicationDeferred",
            "AnnouncementDraft",
            draft.Id,
            "Scheduled announcement publication was deferred.",
            new Dictionary<string, object?> { ["failureCode"] = failureCode },
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> CanAccessDraftAsync(
        Guid actorUserId,
        AnnouncementDraft draft,
        CancellationToken cancellationToken)
    {
        if (actorUserId != draft.AuthorUserId || draft.TenantId != currentTenant.TenantId)
        {
            return false;
        }

        foreach (var target in await GetDraftTargetsAsync(draft, cancellationToken))
        {
            var authorization = await audiences.IsAuthorizedForActorAsync(
                actorUserId,
                target.WorkspaceId,
                target.GroupId,
                target.ChannelId,
                cancellationToken);
            if (!authorization.IsSuccess || authorization.Value != true)
            {
                return false;
            }
        }
        return true;
    }

    private async Task<IReadOnlyList<AnnouncementDraftTargetRequest>> GetDraftTargetsAsync(
        AnnouncementDraft draft,
        CancellationToken cancellationToken)
    {
        if (distributionStore is not null)
        {
            var stored = await distributionStore.GetDraftTargetsAsync(
                draft.TenantId,
                draft.Id,
                cancellationToken);
            if (stored.Count > 0)
            {
                return stored;
            }
        }

        return [new AnnouncementDraftTargetRequest(draft.WorkspaceId, draft.GroupId, draft.ChannelId)];
    }

    private async Task<Result> ValidateContentAsync(
        Guid actorUserId,
        AnnouncementDraftContentRequest content,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        DateTimeOffset publicationTime,
        CancellationToken cancellationToken)
    {
        if (content is null || content.Target is null || targets.Count == 0)
        {
            return Failure("ANNOUNCEMENT_DRAFT_VALIDATION_FAILED", "At least one announcement audience is required.");
        }
        if (string.IsNullOrWhiteSpace(content.Title) || content.Title.Trim().Length > 200)
        {
            return Failure("ANNOUNCEMENT_DRAFT_VALIDATION_FAILED", "Announcement title is required and must be 200 characters or fewer.");
        }
        if (string.IsNullOrWhiteSpace(content.Body) || content.Body.Trim().Length > 20000)
        {
            return Failure("ANNOUNCEMENT_DRAFT_VALIDATION_FAILED", "Announcement body is required and must be 20,000 characters or fewer.");
        }
        if (!Enum.IsDefined(content.Priority) || targets.Any(target => !HasValidTargetShape(target)))
        {
            return Failure("ANNOUNCEMENT_DRAFT_VALIDATION_FAILED", "Announcement content is invalid.");
        }
        if (content.ExpiresAt.HasValue && content.ExpiresAt.Value <= publicationTime)
        {
            return Failure("ANNOUNCEMENT_DRAFT_VALIDATION_FAILED", "Announcement expiration must be in the future.");
        }

        foreach (var target in targets)
        {
            var authorized = await audiences.IsAuthorizedForActorAsync(
                actorUserId,
                target.WorkspaceId,
                target.GroupId,
                target.ChannelId,
                cancellationToken);
            if (!authorized.IsSuccess || authorized.Value != true)
            {
                return Failure(
                    "ANNOUNCEMENT_DRAFT_AUDIENCE_DENIED",
                    "One or more selected announcement audiences are not authorized.");
            }
        }
        return Result.Success();
    }

    private static Result<IReadOnlyList<AnnouncementDraftTargetRequest>> NormalizeTargets(
        AnnouncementDraftContentRequest content)
    {
        if (content is null || content.Target is null)
        {
            return Failure<IReadOnlyList<AnnouncementDraftTargetRequest>>(
                "ANNOUNCEMENT_DRAFT_VALIDATION_FAILED",
                "At least one announcement audience is required.");
        }

        var requested = content.Targets is { Count: > 0 }
            ? content.Targets
            : [content.Target];
        if (requested.Count is < 1 or > AnnouncementDistributionContract.MaximumTargetCount)
        {
            return Failure<IReadOnlyList<AnnouncementDraftTargetRequest>>(
                "ANNOUNCEMENT_DRAFT_VALIDATION_FAILED",
                $"Select between 1 and {AnnouncementDistributionContract.MaximumTargetCount} announcement audiences.");
        }
        if (requested.Any(target => target is null || !HasValidTargetShape(target)))
        {
            return Failure<IReadOnlyList<AnnouncementDraftTargetRequest>>(
                "ANNOUNCEMENT_DRAFT_VALIDATION_FAILED",
                "One or more announcement audiences are invalid.");
        }
        if (requested.Count > 1 && requested.Any(target => !target.GroupId.HasValue && !target.ChannelId.HasValue))
        {
            return Failure<IReadOnlyList<AnnouncementDraftTargetRequest>>(
                "ANNOUNCEMENT_DRAFT_VALIDATION_FAILED",
                "Multiple-audience delivery supports Group and Channel targets only.");
        }

        var normalized = requested
            .Select(target => new AnnouncementDraftTargetRequest(target.WorkspaceId, target.GroupId, target.ChannelId))
            .ToArray();
        var distinctCount = normalized
            .Select(target => (target.WorkspaceId, target.GroupId, target.ChannelId))
            .Distinct()
            .Count();
        if (distinctCount != normalized.Length)
        {
            return Failure<IReadOnlyList<AnnouncementDraftTargetRequest>>(
                "ANNOUNCEMENT_DRAFT_VALIDATION_FAILED",
                "The same announcement audience cannot be selected more than once.");
        }

        return Result<IReadOnlyList<AnnouncementDraftTargetRequest>>.Success(normalized);
    }

    private static Result<ScheduleResolution> ResolveSchedule(ScheduleAnnouncementDraftRequest request)
    {
        if (request.LocalDateTime.Kind != DateTimeKind.Unspecified ||
            string.IsNullOrWhiteSpace(request.TimeZoneId) ||
            request.TimeZoneId.Trim().Length > 80 ||
            (!string.Equals(request.TimeZoneId.Trim(), "UTC", StringComparison.Ordinal) && !request.TimeZoneId.Contains('/', StringComparison.Ordinal)))
        {
            return Failure<ScheduleResolution>(
                "ANNOUNCEMENT_DRAFT_SCHEDULE_INVALID",
                "A local date and an IANA time zone are required.");
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return Failure<ScheduleResolution>("ANNOUNCEMENT_DRAFT_SCHEDULE_INVALID", "The selected time zone is not supported.");
        }
        catch (InvalidTimeZoneException)
        {
            return Failure<ScheduleResolution>("ANNOUNCEMENT_DRAFT_SCHEDULE_INVALID", "The selected time zone is not supported.");
        }

        if (zone.IsInvalidTime(request.LocalDateTime))
        {
            return Failure<ScheduleResolution>("ANNOUNCEMENT_DRAFT_SCHEDULE_INVALID", "The selected local time does not exist in that time zone.");
        }

        DateTimeOffset dueAtUtc;
        if (zone.IsAmbiguousTime(request.LocalDateTime))
        {
            if (!request.AmbiguousTimeOffsetMinutes.HasValue)
            {
                return Failure<ScheduleResolution>("ANNOUNCEMENT_DRAFT_SCHEDULE_AMBIGUOUS", "The selected local time is ambiguous. Choose a different time or offset.");
            }

            var suppliedOffset = TimeSpan.FromMinutes(request.AmbiguousTimeOffsetMinutes.Value);
            if (!zone.GetAmbiguousTimeOffsets(request.LocalDateTime).Contains(suppliedOffset))
            {
                return Failure<ScheduleResolution>("ANNOUNCEMENT_DRAFT_SCHEDULE_INVALID", "The selected time-zone offset is invalid.");
            }
            dueAtUtc = new DateTimeOffset(request.LocalDateTime, suppliedOffset).ToUniversalTime();
        }
        else
        {
            if (request.AmbiguousTimeOffsetMinutes.HasValue)
            {
                return Failure<ScheduleResolution>("ANNOUNCEMENT_DRAFT_SCHEDULE_INVALID", "A time-zone offset may be supplied only for an ambiguous local time.");
            }
            dueAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(request.LocalDateTime, zone), TimeSpan.Zero);
        }

        return Result<ScheduleResolution>.Success(new ScheduleResolution(
            dueAtUtc,
            request.TimeZoneId.Trim(),
            request.LocalDateTime,
            request.AmbiguousTimeOffsetMinutes));
    }

    private static void ApplyContent(
        AnnouncementDraft draft,
        AnnouncementDraftContentRequest content,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets)
    {
        var primary = targets[0];
        draft.WorkspaceId = primary.WorkspaceId;
        draft.GroupId = primary.GroupId;
        draft.ChannelId = primary.ChannelId;
        draft.Title = content.Title.Trim();
        draft.Body = content.Body.Trim();
        draft.Priority = content.Priority;
        draft.IsPinned = content.IsPinned;
        draft.RequiresReadConfirmation = content.RequiresReadConfirmation;
        draft.ExpiresAt = content.ExpiresAt;
    }

    private static AnnouncementDraftContentRequest ToContent(
        AnnouncementDraft draft,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets) => new(
        targets[0],
        draft.Title,
        draft.Body,
        draft.Priority,
        draft.IsPinned,
        draft.RequiresReadConfirmation,
        draft.ExpiresAt,
        Targets: targets);

    private static bool HasValidTargetShape(AnnouncementDraftTargetRequest target) =>
        target.ChannelId.HasValue
            ? target.GroupId.HasValue && target.WorkspaceId.HasValue
            : !target.GroupId.HasValue || target.WorkspaceId.HasValue;

    private bool TryCurrentActor(out Guid actorUserId, out string? error)
    {
        actorUserId = Guid.Empty;
        error = null;
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            error = "Authentication is required.";
            return false;
        }
        if (!currentTenant.IsAvailable || currentTenant.IsPlatformScope)
        {
            error = "A tenant context is required.";
            return false;
        }
        actorUserId = currentUser.UserId.Value;
        return true;
    }

    private static bool IsValidIdempotencyKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length is >= 8 and <= 128 &&
        value.Trim().All(character => character is >= '!' and <= '~');

    private static string Fingerprint<T>(T request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));

    private static void ClearClaim(AnnouncementDraft draft)
    {
        draft.PublicationClaimOwner = null;
        draft.PublicationClaimToken = null;
        draft.PublicationClaimExpiresAtUtc = null;
    }

    private async Task<AnnouncementDraftResponse> ToResponseAsync(
        AnnouncementDraft draft,
        CancellationToken cancellationToken)
    {
        var targets = await GetDraftTargetsAsync(draft, cancellationToken);
        return new AnnouncementDraftResponse(
            draft.Id,
            draft.VersionNo,
            draft.Status,
            draft.WorkspaceId,
            draft.GroupId,
            draft.ChannelId,
            draft.Title,
            draft.Body,
            draft.Priority,
            draft.IsPinned,
            draft.RequiresReadConfirmation,
            draft.ExpiresAt,
            draft.ScheduledForUtc,
            draft.ScheduleTimeZoneId,
            draft.ScheduleLocalDateTime,
            draft.ScheduleUtcOffsetMinutes,
            draft.PublishedAnnouncementId,
            draft.PublishedAtUtc,
            draft.LastPublicationFailureCode,
            draft.CreatedAt,
            draft.UpdatedAt,
            targets);
    }

    private static AnnouncementDraftListItemResponse ToListItem(AnnouncementDraft draft) => new(
        draft.Id,
        draft.VersionNo,
        draft.Status,
        draft.Title,
        draft.ScheduledForUtc,
        draft.ScheduleTimeZoneId,
        draft.PublishedAnnouncementId,
        draft.PublishedAtUtc,
        draft.CreatedAt,
        draft.UpdatedAt);

    private static Result<T> NotFound<T>() => Failure<T>("ANNOUNCEMENT_DRAFT_NOT_FOUND", "Announcement draft not found.");

    private static Result<T> Stale<T>() => Failure<T>("ANNOUNCEMENT_DRAFT_STALE", "Announcement draft has changed. Reload it before retrying.");

    private static Result<T> IdempotencyFailure<T>(string? key) => Failure<T>(
        string.IsNullOrWhiteSpace(key) ? "ANNOUNCEMENT_DRAFT_MISSING_IDEMPOTENCY_KEY" : "ANNOUNCEMENT_DRAFT_INVALID_IDEMPOTENCY_KEY",
        string.IsNullOrWhiteSpace(key) ? "An Idempotency-Key header is required." : "The Idempotency-Key header is invalid.",
        "header.Idempotency-Key");

    private static Result<T> IdempotencyConflict<T>() => Failure<T>(
        "ANNOUNCEMENT_DRAFT_IDEMPOTENCY_CONFLICT",
        "The Idempotency-Key was already used for a different announcement transition.",
        "header.Idempotency-Key");

    private static Result<T> ReplayUnavailable<T>() => Failure<T>(
        "ANNOUNCEMENT_DRAFT_REPLAY_UNAVAILABLE",
        "The announcement transition could not be reconciled. Retry with a new Idempotency-Key.");

    private static Result Failure(string code, string message, string? target = null) =>
        Result.Failure(new ApplicationErrorDetail(code, message, Target: target));

    private static Result<T> Failure<T>(string code, string message, string? target = null) =>
        Result<T>.Failure(new ApplicationErrorDetail(code, message, Target: target));

    private static bool IsConcurrencyConflict(Exception exception) =>
        exception.GetType().Name == "DbUpdateConcurrencyException" ||
        exception.InnerException is not null && IsConcurrencyConflict(exception.InnerException);

    private sealed record ScheduleResolution(
        DateTimeOffset DueAtUtc,
        string TimeZoneId,
        DateTime LocalDateTime,
        int? AmbiguousTimeOffsetMinutes);

    private sealed class AnnouncementTransitionConflictException : Exception;
    private sealed class AnnouncementTransitionNotReadyException : Exception;
    private sealed class AnnouncementTransitionValidationException(string message) : Exception(message);
}
