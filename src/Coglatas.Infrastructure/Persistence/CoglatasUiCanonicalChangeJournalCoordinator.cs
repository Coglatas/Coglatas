using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Coglatas.Ui.Core.Interaction;

namespace Coglatas.Infrastructure.Persistence;

public sealed record CoglatasUiCanonicalJournalCommitResult(
    CommitDecision Decision,
    long? CommittedRevision)
{
    public bool IsCommitted => Decision.IsAllowed && CommittedRevision.HasValue;
}

/// <summary>
/// PostgreSQL-backed implementation of the ordered Canonical Change Journal.
///
/// Every canonical mutation for one interaction scope is serialized through one
/// locked revision-head row. The head increment, journal row, and staged domain
/// mutation commit in the same database transaction, so a committed revision can
/// never exist without its corresponding changed-domain record.
///
/// Authority loading and staged mutation callbacks are deliberately supplied by
/// the caller because those records are use-case specific. Both callbacks receive
/// this coordinator's transaction-bound AppDbContext so they can lock/read/write
/// through the same database transaction.
/// </summary>
public sealed class CoglatasUiCanonicalChangeJournalCoordinator(AppDbContext dbContext)
{
    private const string HeadTable = "coglatas_ui_canonical_revision_heads";
    private const string JournalTable = "coglatas_ui_canonical_change_journal";

    public async Task<long> ExecuteAuthoritativeMutationAsync(
        ContextScope scope,
        CanonicalDependencyDomain changedDomains,
        Func<AppDbContext, long, CancellationToken, Task> stageMutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(stageMutation);
        ValidateChangedDomains(changedDomains);
        EnsurePostgreSql();
        EnsureNoAmbientTransaction();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var scopeKey = CreateScopeKey(scope);
            var currentRevision = await EnsureAndLockHeadAsync(
                scopeKey,
                scope,
                cancellationToken);
            var nextRevision = checked(currentRevision + 1);

            await stageMutation(dbContext, nextRevision, cancellationToken);
            await AdvanceHeadAsync(
                scopeKey,
                currentRevision,
                nextRevision,
                cancellationToken);
            await InsertJournalEntryAsync(
                scopeKey,
                nextRevision,
                changedDomains,
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return nextRevision;
        }
        catch
        {
            await RollbackAndClearAsync(transaction);
            throw;
        }
    }

    public async Task<CoglatasUiCanonicalJournalCommitResult> ExecutePermittedMutationAsync(
        ExecutionPrecondition precondition,
        Func<AppDbContext, CancellationToken, Task<AuthoritySnapshot>> loadAndLockCurrentAuthority,
        CanonicalDependencyDomain changedDomains,
        Func<AppDbContext, long, CancellationToken, Task> stageMutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentNullException.ThrowIfNull(loadAndLockCurrentAuthority);
        ArgumentNullException.ThrowIfNull(stageMutation);
        ValidateChangedDomains(changedDomains);
        EnsurePostgreSql();
        EnsureNoAmbientTransaction();

        if (precondition.ObservedCanonicalRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(precondition),
                "ObservedCanonicalRevision cannot be negative.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var scopeKey = CreateScopeKey(precondition.Scope);
            var currentRevision = await EnsureAndLockHeadAsync(
                scopeKey,
                precondition.Scope,
                cancellationToken);
            var changes = await LoadChangesSinceAsync(
                scopeKey,
                precondition.ObservedCanonicalRevision,
                currentRevision,
                cancellationToken);
            var authority = await loadAndLockCurrentAuthority(dbContext, cancellationToken);

            var descriptorDecision = ValidateExecutionDescriptor(precondition, changedDomains);
            if (descriptorDecision is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                dbContext.ChangeTracker.Clear();
                return new CoglatasUiCanonicalJournalCommitResult(descriptorDecision, null);
            }

            var decision = AtomicCommitPreconditionEvaluator.Evaluate(
                precondition,
                new AuthoritativeCommitSnapshot(
                    authority.Scope,
                    authority.Status,
                    authority.Generation,
                    currentRevision,
                    changes));

            if (!decision.IsAllowed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                dbContext.ChangeTracker.Clear();
                return new CoglatasUiCanonicalJournalCommitResult(decision, null);
            }

            var nextRevision = checked(currentRevision + 1);
            await stageMutation(dbContext, nextRevision, cancellationToken);
            await AdvanceHeadAsync(
                scopeKey,
                currentRevision,
                nextRevision,
                cancellationToken);
            await InsertJournalEntryAsync(
                scopeKey,
                nextRevision,
                changedDomains,
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new CoglatasUiCanonicalJournalCommitResult(
                CommitDecision.Allow(),
                nextRevision);
        }
        catch
        {
            await RollbackAndClearAsync(transaction);
            throw;
        }
    }

    private static CommitDecision? ValidateExecutionDescriptor(
        ExecutionPrecondition precondition,
        CanonicalDependencyDomain changedDomains)
    {
        if (!CoreActionContracts.TryResolve(precondition.ActionContract, out var semantics) ||
            semantics.Kind == InteractionActionKind.Read)
        {
            return new CommitDecision(
                CommitDecisionCode.InvalidExecutionPrecondition,
                false,
                "The execution permit does not identify a known non-read action contract.");
        }

        if (precondition.RequiredCanonicalDomains == CanonicalDependencyDomain.None ||
            (precondition.RequiredCanonicalDomains & ~CanonicalDependencyDomain.All) != CanonicalDependencyDomain.None)
        {
            return new CommitDecision(
                CommitDecisionCode.InvalidExecutionPrecondition,
                false,
                "The execution permit contains an invalid canonical dependency mask.");
        }

        if ((changedDomains & precondition.RequiredCanonicalDomains) != changedDomains)
        {
            return new CommitDecision(
                CommitDecisionCode.InvalidExecutionPrecondition,
                false,
                "The execution permit does not cover every canonical domain mutated by this commit.");
        }

        return null;
    }

    private async Task<long> EnsureAndLockHeadAsync(
        string scopeKey,
        ContextScope scope,
        CancellationToken cancellationToken)
    {
        await using (var insert = CreateCommand($"""
            INSERT INTO {HeadTable}
                ("ScopeKey", "TenantId", "WorkspaceId", "ProjectId", "Revision", "UpdatedAtUtc")
            VALUES
                (@scopeKey, @tenantId, @workspaceId, @projectId, 0, CURRENT_TIMESTAMP)
            ON CONFLICT ("ScopeKey") DO NOTHING
            """))
        {
            AddParameter(insert, "scopeKey", scopeKey, DbType.String);
            AddParameter(insert, "tenantId", scope.TenantId, DbType.String);
            AddParameter(insert, "workspaceId", scope.WorkspaceId, DbType.String);
            AddParameter(insert, "projectId", scope.ProjectId, DbType.String);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = CreateCommand($"""
            SELECT "TenantId", "WorkspaceId", "ProjectId", "Revision"
            FROM {HeadTable}
            WHERE "ScopeKey" = @scopeKey
            FOR UPDATE
            """);
        AddParameter(select, "scopeKey", scopeKey, DbType.String);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Canonical revision head disappeared while acquiring its transaction lock.");
        }

        var tenantId = reader.GetString(0);
        var workspaceId = reader.GetString(1);
        var projectId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var revision = reader.GetInt64(3);

        if (!string.Equals(tenantId, scope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(workspaceId, scope.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(projectId, scope.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Canonical scope-key collision or corrupted revision-head identity detected.");
        }

        return revision;
    }

    private async Task<IReadOnlyList<CanonicalChangeRecord>> LoadChangesSinceAsync(
        string scopeKey,
        long observedRevision,
        long currentRevision,
        CancellationToken cancellationToken)
    {
        if (currentRevision <= observedRevision)
        {
            return [];
        }

        await using var command = CreateCommand($"""
            SELECT "Revision", "ChangedDomains"
            FROM {JournalTable}
            WHERE "ScopeKey" = @scopeKey
              AND "Revision" > @observedRevision
              AND "Revision" <= @currentRevision
            ORDER BY "Revision"
            """);
        AddParameter(command, "scopeKey", scopeKey, DbType.String);
        AddParameter(command, "observedRevision", observedRevision, DbType.Int64);
        AddParameter(command, "currentRevision", currentRevision, DbType.Int64);

        var changes = new List<CanonicalChangeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawDomains = reader.GetInt32(1);
            var domains = (CanonicalDependencyDomain)rawDomains;
            ValidateStoredDomains(domains);
            changes.Add(new CanonicalChangeRecord(reader.GetInt64(0), domains));
        }

        return changes;
    }

    private async Task AdvanceHeadAsync(
        string scopeKey,
        long expectedRevision,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand($"""
            UPDATE {HeadTable}
            SET "Revision" = @nextRevision,
                "UpdatedAtUtc" = CURRENT_TIMESTAMP
            WHERE "ScopeKey" = @scopeKey
              AND "Revision" = @expectedRevision
            """);
        AddParameter(command, "scopeKey", scopeKey, DbType.String);
        AddParameter(command, "expectedRevision", expectedRevision, DbType.Int64);
        AddParameter(command, "nextRevision", nextRevision, DbType.Int64);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "Canonical revision head changed despite the held transaction lock.");
        }
    }

    private async Task InsertJournalEntryAsync(
        string scopeKey,
        long revision,
        CanonicalDependencyDomain changedDomains,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand($"""
            INSERT INTO {JournalTable}
                ("ScopeKey", "Revision", "ChangedDomains", "OccurredAtUtc")
            VALUES
                (@scopeKey, @revision, @changedDomains, CURRENT_TIMESTAMP)
            """);
        AddParameter(command, "scopeKey", scopeKey, DbType.String);
        AddParameter(command, "revision", revision, DbType.Int64);
        AddParameter(command, "changedDomains", (int)changedDomains, DbType.Int32);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(string commandText)
    {
        var transaction = dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Canonical journal access requires an active coordinator transaction.");

        var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = commandText;
        return command;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value,
        DbType dbType)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private void EnsurePostgreSql()
    {
        if (!string.Equals(
                dbContext.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "The canonical change journal requires PostgreSQL transactional row locking.");
        }
    }

    private void EnsureNoAmbientTransaction()
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "The canonical journal coordinator owns the transaction; nested execution is not allowed.");
        }
    }

    private static void ValidateChangedDomains(CanonicalDependencyDomain changedDomains)
    {
        if (changedDomains == CanonicalDependencyDomain.None ||
            (changedDomains & ~CanonicalDependencyDomain.All) != CanonicalDependencyDomain.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changedDomains),
                "A canonical mutation must identify one or more known changed domains.");
        }
    }

    private static void ValidateStoredDomains(CanonicalDependencyDomain changedDomains)
    {
        if (changedDomains == CanonicalDependencyDomain.None ||
            (changedDomains & ~CanonicalDependencyDomain.All) != CanonicalDependencyDomain.None)
        {
            throw new InvalidOperationException(
                "Canonical journal contains an invalid changed-domain mask.");
        }
    }

    public static string CreateScopeKey(ContextScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        static string Encode(string? value) =>
            value is null
                ? "-1:"
                : $"{Encoding.UTF8.GetByteCount(value)}:{value}";

        var canonical =
            Encode(scope.TenantId) +
            Encode(scope.WorkspaceId) +
            Encode(scope.ProjectId);

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task RollbackAndClearAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the original failure.
        }

        dbContext.ChangeTracker.Clear();
    }
}
