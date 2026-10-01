using System.Data;
using System.Security.Cryptography;
using System.Text;
using Coglatas.Application.Announcements;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Privacy-minimized engagement persistence for Announcement analytics. The
/// PostgreSQL implementation stores only a deterministic recipient token plus
/// the action in a private sidecar table; it deliberately does not use AuditLog
/// or retain per-recipient timing/content metadata. Non-PostgreSQL providers use
/// an in-memory equivalent for tests.
/// </summary>
public sealed class AnnouncementEngagementStore(AppDbContext dbContext) : IAnnouncementEngagementStore
{
    private readonly HashSet<EngagementEventKey> inMemoryEvents = [];

    public async Task RecordOnceAsync(
        Guid tenantId,
        Guid announcementId,
        Guid userId,
        string action,
        CancellationToken cancellationToken = default)
    {
        ValidateAction(action);
        var recipientToken = CreateRecipientToken(tenantId, announcementId, userId);

        if (!UsesPostgreSql())
        {
            inMemoryEvents.Add(new EngagementEventKey(
                tenantId,
                announcementId,
                recipientToken,
                action));
            return;
        }

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                INSERT INTO announcement_engagement_events
                    ("TenantId", "AnnouncementId", "RecipientToken", "Action")
                VALUES
                    (@tenantId, @announcementId, @recipientToken, @action)
                ON CONFLICT ("TenantId", "AnnouncementId", "RecipientToken", "Action") DO NOTHING
                """;
            AddParameter(command, "tenantId", tenantId);
            AddParameter(command, "announcementId", announcementId);
            AddParameter(command, "recipientToken", recipientToken);
            AddParameter(command, "action", action);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<AnnouncementEngagementAggregate> GetAggregateAsync(
        Guid tenantId,
        Guid announcementId,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        var hasFrozenDeliveryCohort = await dbContext.AuditLogs
            .AsNoTracking()
            .AnyAsync(log =>
                log.TenantId == tenantId &&
                log.Action == AnnouncementDistributionContract.FrozenCohortAuditAction &&
                log.EntityType == "Announcement" &&
                log.EntityId == announcementId,
                cancellationToken);

        Guid[] recipientIds;
        if (hasFrozenDeliveryCohort)
        {
            var deliveryLogicalKey = AnnouncementDistributionContract.DeliveryLogicalKey(announcementId);
            recipientIds = await dbContext.Notifications
                .AsNoTracking()
                .Where(notification =>
                    notification.TenantId == tenantId &&
                    notification.NotificationType == NotificationType.Announcement &&
                    notification.RelatedEntityType == "Announcement" &&
                    notification.RelatedEntityId == announcementId &&
                    notification.LogicalKey == deliveryLogicalKey)
                .Select(notification => notification.UserId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            recipientIds = recipientUserIds.Distinct().ToArray();
        }

        if (recipientIds.Length == 0)
        {
            return new AnnouncementEngagementAggregate(
                hasFrozenDeliveryCohort,
                0,
                0,
                0,
                0,
                []);
        }

        var readTimes = await dbContext.AnnouncementReads
            .AsNoTracking()
            .Where(read =>
                read.TenantId == tenantId &&
                read.AnnouncementId == announcementId &&
                Enumerable.Contains(recipientIds, read.UserId))
            .Select(read => read.ReadAt)
            .ToListAsync(cancellationToken);

        var recipientTokens = recipientIds
            .Select(userId => CreateRecipientToken(tenantId, announcementId, userId))
            .ToHashSet(StringComparer.Ordinal);
        var engagementEvents = UsesPostgreSql()
            ? await ReadEventsAsync(tenantId, announcementId, cancellationToken)
            : inMemoryEvents
                .Where(item => item.TenantId == tenantId && item.AnnouncementId == announcementId)
                .Select(item => new EngagementEvent(item.RecipientToken, item.Action))
                .ToArray();

        var acknowledgedCount = engagementEvents.Count(item =>
            item.Action == AnnouncementEngagementActions.Acknowledged &&
            recipientTokens.Contains(item.RecipientToken));
        var ctaClickedCount = engagementEvents.Count(item =>
            item.Action == AnnouncementEngagementActions.CtaClicked &&
            recipientTokens.Contains(item.RecipientToken));

        return new AnnouncementEngagementAggregate(
            hasFrozenDeliveryCohort,
            recipientIds.Length,
            readTimes.Count,
            acknowledgedCount,
            ctaClickedCount,
            readTimes);
    }

    private async Task<IReadOnlyList<EngagementEvent>> ReadEventsAsync(
        Guid tenantId,
        Guid announcementId,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                SELECT "RecipientToken", "Action"
                FROM announcement_engagement_events
                WHERE "TenantId" = @tenantId
                  AND "AnnouncementId" = @announcementId
                """;
            AddParameter(command, "tenantId", tenantId);
            AddParameter(command, "announcementId", announcementId);

            var events = new List<EngagementEvent>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new EngagementEvent(reader.GetString(0), reader.GetString(1)));
            }
            return events;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private bool UsesPostgreSql() =>
        dbContext.Database.IsRelational() &&
        string.Equals(dbContext.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);

    private static string CreateRecipientToken(Guid tenantId, Guid announcementId, Guid userId)
    {
        var material = Encoding.UTF8.GetBytes($"{tenantId:N}:{announcementId:N}:{userId:N}");
        return Convert.ToHexString(SHA256.HashData(material));
    }

    private static void ValidateAction(string action)
    {
        if (action is not AnnouncementEngagementActions.Acknowledged and not AnnouncementEngagementActions.CtaClicked)
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported Announcement engagement action.");
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record EngagementEvent(string RecipientToken, string Action);

    private sealed record EngagementEventKey(
        Guid TenantId,
        Guid AnnouncementId,
        string RecipientToken,
        string Action);
}
