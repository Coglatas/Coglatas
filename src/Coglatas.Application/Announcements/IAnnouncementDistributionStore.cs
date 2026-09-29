
namespace Coglatas.Application.Announcements;

/// <summary>
/// Durable sidecar for #388. Announcement content remains owned by the existing
/// draft/announcement aggregate while this store owns the selected delivery
/// scopes and the transaction boundary used when a frozen recipient cohort is
/// emitted.
/// </summary>
public interface IAnnouncementDistributionStore
{
    /// <summary>
    /// Called from the create-idempotency transaction after the draft row is
    /// staged. Implementations may flush the tracked row inside that already
    /// active transaction before writing the sidecar target set.
    /// </summary>
    Task StageCreatedDraftTargetsAsync(
        Guid tenantId,
        Guid draftId,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits one optimistic draft edit and its selected target set as one
    /// database transaction. The draft/audit mutations are already tracked in
    /// the current unit of work when this method is called.
    /// </summary>
    Task CommitDraftSaveAsync(
        Guid tenantId,
        Guid draftId,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnnouncementDraftTargetRequest>> GetDraftTargetsAsync(
        Guid tenantId,
        Guid draftId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the immutable selected targets persisted with a published
    /// Announcement. Consumers must reauthorize every returned target before
    /// exposing data derived from the combined delivery cohort.
    /// </summary>
    Task<IReadOnlyList<AnnouncementDraftTargetRequest>> GetAnnouncementTargetsAsync(
        Guid tenantId,
        Guid announcementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the supplied publication mutation inside one transaction and then
    /// stores the immutable target metadata for the newly-created Announcement.
    /// Recipient delivery notifications are staged by the supplied callback so
    /// their logical keys are committed with the Announcement itself.
    /// </summary>
    Task CommitPublicationAsync(
        Guid tenantId,
        Guid announcementId,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        Func<CancellationToken, Task> stagePublication,
        CancellationToken cancellationToken = default);
}

public static class AnnouncementDistributionContract
{
    public const int MaximumTargetCount = 20;
    public const string DeliveryLogicalKeyPrefix = "announcement-delivery:";
    public const string FrozenCohortAuditAction = "AnnouncementDeliveryCohortFrozen";

    public static string DeliveryLogicalKey(Guid announcementId) =>
        $"{DeliveryLogicalKeyPrefix}{announcementId:N}";
}
