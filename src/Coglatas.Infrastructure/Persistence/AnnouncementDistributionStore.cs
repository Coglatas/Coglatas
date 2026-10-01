using System.Data;
using System.Text.Json;
using Coglatas.Application.Announcements;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL sidecar for #388. The distribution target JSON deliberately is
/// not mapped into AppDbContext: content and delivery metadata remain separate
/// without widening the legacy Announcement/Draft entity model or creating a
/// second content aggregate. This follows the repository's existing sidecar
/// persistence pattern (for example MessageNotificationPreferenceStore).
/// </summary>
public sealed class AnnouncementDistributionStore(AppDbContext dbContext, IClock clock) : IAnnouncementDistributionStore
{
    private const string UpdateDraftTargetsSql = """
        UPDATE announcement_drafts
        SET "DistributionTargetsJson" = @targets
        WHERE "TenantId" = @tenantId
          AND "Id" = @resourceId
        """;
    private const string UpdateAnnouncementTargetsSql = """
        UPDATE announcements
        SET "DistributionTargetsJson" = @targets
        WHERE "TenantId" = @tenantId
          AND "Id" = @resourceId
        """;
    private const string ReadDraftTargetsSql = """
        SELECT "DistributionTargetsJson"
        FROM announcement_drafts
        WHERE "TenantId" = @tenantId
          AND "Id" = @resourceId
        LIMIT 1
        """;
    private const string ReadAnnouncementTargetsSql = """
        SELECT "DistributionTargetsJson"
        FROM announcements
        WHERE "TenantId" = @tenantId
          AND "Id" = @resourceId
        LIMIT 1
        """;

    private readonly Dictionary<Guid, IReadOnlyList<AnnouncementDraftTargetRequest>> _inMemoryDraftTargets = [];
    private readonly Dictionary<Guid, IReadOnlyList<AnnouncementDraftTargetRequest>> _inMemoryAnnouncementTargets = [];

    private enum SidecarTable
    {
        Draft,
        Announcement
    }

    public async Task StageCreatedDraftTargetsAsync(
        Guid tenantId,
        Guid draftId,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        CancellationToken cancellationToken = default)
    {
        Validate(tenantId, draftId, targets);

        // The create idempotency coordinator already owns the relational
        // transaction. Flush the draft row first so the raw sidecar update is
        // part of that same uncommitted business transaction.
        await dbContext.SaveChangesAsync(cancellationToken);
        if (!UsesPostgreSql())
        {
            _inMemoryDraftTargets[draftId] = Copy(targets);
            return;
        }

        await UpdateTargetJsonAsync(
            SidecarTable.Draft,
            tenantId,
            draftId,
            Serialize(targets),
            cancellationToken);
    }

    public async Task CommitDraftSaveAsync(
        Guid tenantId,
        Guid draftId,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        CancellationToken cancellationToken = default)
    {
        Validate(tenantId, draftId, targets);
        if (!UsesPostgreSql())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            _inMemoryDraftTargets[draftId] = Copy(targets);
            return;
        }

        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await UpdateTargetJsonAsync(
                SidecarTable.Draft,
                tenantId,
                draftId,
                Serialize(targets),
                cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<IReadOnlyList<AnnouncementDraftTargetRequest>> GetDraftTargetsAsync(
        Guid tenantId,
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || draftId == Guid.Empty)
        {
            return [];
        }
        if (!UsesPostgreSql())
        {
            return _inMemoryDraftTargets.TryGetValue(draftId, out var targets)
                ? Copy(targets)
                : [];
        }

        var json = await ReadTargetJsonAsync(
            SidecarTable.Draft,
            tenantId,
            draftId,
            cancellationToken);
        return Deserialize(json);
    }

    public async Task<IReadOnlyList<AnnouncementDraftTargetRequest>> GetAnnouncementTargetsAsync(
        Guid tenantId,
        Guid announcementId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || announcementId == Guid.Empty)
        {
            return [];
        }
        if (!UsesPostgreSql())
        {
            return _inMemoryAnnouncementTargets.TryGetValue(announcementId, out var targets)
                ? Copy(targets)
                : [];
        }

        var json = await ReadTargetJsonAsync(
            SidecarTable.Announcement,
            tenantId,
            announcementId,
            cancellationToken);
        return Deserialize(json);
    }

    public async Task CommitPublicationAsync(
        Guid tenantId,
        Guid announcementId,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets,
        Func<CancellationToken, Task> stagePublication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagePublication);
        Validate(tenantId, announcementId, targets);

        if (!UsesPostgreSql())
        {
            await stagePublication(cancellationToken);
            StageFrozenCohortMarker(tenantId, announcementId);
            await dbContext.SaveChangesAsync(cancellationToken);
            _inMemoryAnnouncementTargets[announcementId] = Copy(targets);
            return;
        }

        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await stagePublication(cancellationToken);
            StageFrozenCohortMarker(tenantId, announcementId);
            // A logical-notification implementation may already have flushed
            // tracked rows while remaining inside this transaction. This final
            // save persists the cohort marker even when dispatch resolved zero
            // recipients, so an empty cohort cannot later drift with membership.
            await dbContext.SaveChangesAsync(cancellationToken);
            await UpdateTargetJsonAsync(
                SidecarTable.Announcement,
                tenantId,
                announcementId,
                Serialize(targets),
                cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private void StageFrozenCohortMarker(Guid tenantId, Guid announcementId)
    {
        if (dbContext.AuditLogs.Local.Any(log =>
                log.TenantId == tenantId &&
                log.EntityId == announcementId &&
                log.Action == AnnouncementDistributionContract.FrozenCohortAuditAction))
        {
            return;
        }

        var announcement = dbContext.Announcements.Local.FirstOrDefault(item =>
            item.Id == announcementId && item.TenantId == tenantId);
        if (announcement is null)
        {
            throw new InvalidOperationException("The announcement must be tracked before its delivery cohort is frozen.");
        }

        dbContext.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = announcement.AuthorUserId,
            Action = AnnouncementDistributionContract.FrozenCohortAuditAction,
            EntityType = "Announcement",
            EntityId = announcementId,
            Summary = "Announcement delivery cohort frozen.",
            CreatedAt = clock.UtcNow
        });
    }

    private bool UsesPostgreSql() =>
        dbContext.Database.IsRelational() &&
        string.Equals(dbContext.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);

    private async Task UpdateTargetJsonAsync(
        SidecarTable table,
        Guid tenantId,
        Guid resourceId,
        string json,
        CancellationToken cancellationToken)
    {
        await ExecuteRequiredUpdateAsync(table switch
        {
            // Keep the SQL source adjacent to the command sink. The table is
            // never derived from request data, and every request-derived value
            // below remains a typed database parameter.
            SidecarTable.Draft => command =>
            {
                command.CommandText = UpdateDraftTargetsSql;
                AddUpdateParameters(command, tenantId, resourceId, json);
            },
            SidecarTable.Announcement => command =>
            {
                command.CommandText = UpdateAnnouncementTargetsSql;
                AddUpdateParameters(command, tenantId, resourceId, json);
            },
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unsupported announcement sidecar table.")
        }, cancellationToken);
    }

    private async Task<string?> ReadTargetJsonAsync(
        SidecarTable table,
        Guid tenantId,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        return await ExecuteScalarAsync(table switch
        {
            // See UpdateTargetJsonAsync: only these compile-time SQL constants
            // may reach CommandText; resource scope is supplied as parameters.
            SidecarTable.Draft => command =>
            {
                command.CommandText = ReadDraftTargetsSql;
                AddReadParameters(command, tenantId, resourceId);
            },
            SidecarTable.Announcement => command =>
            {
                command.CommandText = ReadAnnouncementTargetsSql;
                AddReadParameters(command, tenantId, resourceId);
            },
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unsupported announcement sidecar table.")
        }, cancellationToken);
    }

    private async Task ExecuteRequiredUpdateAsync(
        Action<System.Data.Common.DbCommand> configure,
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
            configure(command);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Announcement distribution target sidecar could not be persisted.");
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<string?> ExecuteScalarAsync(
        Action<System.Data.Common.DbCommand> configure,
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
            configure(command);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddUpdateParameters(
        System.Data.Common.DbCommand command,
        Guid tenantId,
        Guid resourceId,
        string json)
    {
        AddParameter(command, "targets", json);
        AddParameter(command, "tenantId", tenantId);
        AddParameter(command, "resourceId", resourceId);
    }

    private static void AddReadParameters(
        System.Data.Common.DbCommand command,
        Guid tenantId,
        Guid resourceId)
    {
        AddParameter(command, "tenantId", tenantId);
        AddParameter(command, "resourceId", resourceId);
    }

    private static string Serialize(IReadOnlyList<AnnouncementDraftTargetRequest> targets) =>
        JsonSerializer.Serialize(targets);

    private static IReadOnlyList<AnnouncementDraftTargetRequest> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AnnouncementDraftTargetRequest>>(json) is { Count: > 0 } targets
                ? Copy(targets)
                : [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Announcement distribution target sidecar is invalid.", exception);
        }
    }

    private static IReadOnlyList<AnnouncementDraftTargetRequest> Copy(
        IReadOnlyList<AnnouncementDraftTargetRequest> targets) =>
        targets.Select(target => new AnnouncementDraftTargetRequest(
            target.WorkspaceId,
            target.GroupId,
            target.ChannelId)).ToArray();

    private static void Validate(
        Guid tenantId,
        Guid resourceId,
        IReadOnlyList<AnnouncementDraftTargetRequest> targets)
    {
        if (tenantId == Guid.Empty || resourceId == Guid.Empty)
        {
            throw new ArgumentException("Announcement distribution scope identifiers must be non-empty.");
        }
        if (targets.Count is < 1 or > AnnouncementDistributionContract.MaximumTargetCount)
        {
            throw new ArgumentException("Announcement distribution target count is invalid.", nameof(targets));
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
