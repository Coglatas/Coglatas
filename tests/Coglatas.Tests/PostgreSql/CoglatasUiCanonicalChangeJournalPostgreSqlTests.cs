using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Coglatas.Ui.Core.Interaction;

namespace Coglatas.Tests.PostgreSql;

public sealed class CoglatasUiCanonicalChangeJournalPostgreSqlTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "CoglatasUiCanonicalJournal")]
    public async Task JournalAllowsIrrelevantRaceButRejectsRelevantRace()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(
            connectionString,
            async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);

                var scope = NewScope();
                await using var context =
                    PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
                var coordinator = new CoglatasUiCanonicalChangeJournalCoordinator(context);

                var first = await coordinator.ExecuteAuthoritativeMutationAsync(
                    scope,
                    CanonicalDependencyDomain.WorkflowMetadata,
                    static (_, _, _) => Task.CompletedTask);
                var second = await coordinator.ExecuteAuthoritativeMutationAsync(
                    scope,
                    CanonicalDependencyDomain.Auxiliary,
                    static (_, _, _) => Task.CompletedTask);

                Assert.Equal(1, first);
                Assert.Equal(2, second);

                var permit = new ExecutionPrecondition(
                    CoreActionContracts.MutateCanonical,
                    scope,
                    ExpectedAuthorityGeneration: 4,
                    ObservedCanonicalRevision: 1,
                    RequiredCanonicalDomains: CanonicalDependencyDomain.SelectedEntity);

                var allowed = await coordinator.ExecutePermittedMutationAsync(
                    permit,
                    (_, _) => Task.FromResult(
                        new AuthoritySnapshot(
                            scope,
                            AuthorityStatus.Allowed,
                            Generation: 4)),
                    CanonicalDependencyDomain.SelectedEntity,
                    static (_, _, _) => Task.CompletedTask);

                Assert.True(allowed.IsCommitted);
                Assert.Equal(CommitDecisionCode.Allowed, allowed.Decision.Code);
                Assert.Equal(3, allowed.CommittedRevision);

                var stalePermit = permit with
                {
                    ObservedCanonicalRevision = 1
                };

                var rejected = await coordinator.ExecutePermittedMutationAsync(
                    stalePermit,
                    (_, _) => Task.FromResult(
                        new AuthoritySnapshot(
                            scope,
                            AuthorityStatus.Allowed,
                            Generation: 4)),
                    CanonicalDependencyDomain.SelectedEntity,
                    static (_, _, _) => Task.CompletedTask);

                Assert.False(rejected.IsCommitted);
                Assert.Equal(CommitDecisionCode.StaleCanonical, rejected.Decision.Code);
                Assert.Null(rejected.CommittedRevision);

                var head = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT "Revision"
                    FROM coglatas_ui_canonical_revision_heads
                    WHERE "ScopeKey" = @scopeKey
                    """,
                    ("scopeKey", CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(scope)));

                var journalCount = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT COUNT(*)
                    FROM coglatas_ui_canonical_change_journal
                    WHERE "ScopeKey" = @scopeKey
                    """,
                    ("scopeKey", CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(scope)));

                Assert.Equal(3, head);
                Assert.Equal(3, journalCount);
            });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "CoglatasUiCanonicalJournal")]
    public async Task JournalRollsBackHeadAndHistoryWhenMutationFails()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(
            connectionString,
            async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);

                await PostgreSqlMigrationTestDatabase.ExecuteAsync(
                    database,
                    """
                    CREATE TABLE coglatas_ui_atomic_payload_probe (
                        "Id" integer PRIMARY KEY,
                        "Revision" bigint NOT NULL
                    )
                    """);

                var scope = NewScope();
                await using var context =
                    PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
                var coordinator = new CoglatasUiCanonicalChangeJournalCoordinator(context);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    coordinator.ExecuteAuthoritativeMutationAsync(
                        scope,
                        CanonicalDependencyDomain.SelectedEntity,
                        async (transactionContext, revision, cancellationToken) =>
                        {
                            await transactionContext.Database.ExecuteSqlInterpolatedAsync(
                                $"INSERT INTO coglatas_ui_atomic_payload_probe (\"Id\", \"Revision\") VALUES ({1}, {revision})",
                                cancellationToken);
                            throw new InvalidOperationException("synthetic failure after staged database mutation");
                        }));

                var payloadCount = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT COUNT(*)
                    FROM coglatas_ui_atomic_payload_probe
                    """);

                var headCount = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT COUNT(*)
                    FROM coglatas_ui_canonical_revision_heads
                    WHERE "ScopeKey" = @scopeKey
                    """,
                    ("scopeKey", CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(scope)));

                var journalCount = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT COUNT(*)
                    FROM coglatas_ui_canonical_change_journal
                    WHERE "ScopeKey" = @scopeKey
                    """,
                    ("scopeKey", CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(scope)));

                Assert.Equal(0, payloadCount);
                Assert.Equal(0, headCount);
                Assert.Equal(0, journalCount);
            });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "CoglatasUiCanonicalJournal")]
    public async Task JournalRejectsAuthorityRaceWithoutAdvancingRevisionOrStagingMutation()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(
            connectionString,
            async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);

                var scope = NewScope();
                await using var context =
                    PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
                var coordinator = new CoglatasUiCanonicalChangeJournalCoordinator(context);

                var initialRevision = await coordinator.ExecuteAuthoritativeMutationAsync(
                    scope,
                    CanonicalDependencyDomain.WorkflowMetadata,
                    static (_, _, _) => Task.CompletedTask);
                Assert.Equal(1, initialRevision);

                var permit = new ExecutionPrecondition(
                    CoreActionContracts.MutateCanonical,
                    scope,
                    ExpectedAuthorityGeneration: 4,
                    ObservedCanonicalRevision: 1,
                    RequiredCanonicalDomains: CanonicalDependencyDomain.SelectedEntity);

                var stagedAfterGenerationRace = false;
                var staleAuthority = await coordinator.ExecutePermittedMutationAsync(
                    permit,
                    (_, _) => Task.FromResult(
                        new AuthoritySnapshot(
                            scope,
                            AuthorityStatus.Allowed,
                            Generation: 5)),
                    CanonicalDependencyDomain.SelectedEntity,
                    (_, _, _) =>
                    {
                        stagedAfterGenerationRace = true;
                        return Task.CompletedTask;
                    });

                Assert.False(staleAuthority.IsCommitted);
                Assert.Equal(CommitDecisionCode.StaleAuthority, staleAuthority.Decision.Code);
                Assert.False(stagedAfterGenerationRace);

                var stagedAfterRevocation = false;
                var revoked = await coordinator.ExecutePermittedMutationAsync(
                    permit,
                    (_, _) => Task.FromResult(
                        new AuthoritySnapshot(
                            scope,
                            AuthorityStatus.Revoked,
                            Generation: 4)),
                    CanonicalDependencyDomain.SelectedEntity,
                    (_, _, _) =>
                    {
                        stagedAfterRevocation = true;
                        return Task.CompletedTask;
                    });

                Assert.False(revoked.IsCommitted);
                Assert.Equal(CommitDecisionCode.AuthorityDenied, revoked.Decision.Code);
                Assert.False(stagedAfterRevocation);

                var head = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT "Revision"
                    FROM coglatas_ui_canonical_revision_heads
                    WHERE "ScopeKey" = @scopeKey
                    """,
                    ("scopeKey", CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(scope)));

                var journalCount = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT COUNT(*)
                    FROM coglatas_ui_canonical_change_journal
                    WHERE "ScopeKey" = @scopeKey
                    """,
                    ("scopeKey", CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(scope)));

                Assert.Equal(1, head);
                Assert.Equal(1, journalCount);
            });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "CoglatasUiCanonicalJournal")]
    public async Task ConcurrentWritersReceiveDistinctContiguousRevisions()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(
            connectionString,
            async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
                var scope = NewScope();

                async Task<long> WriteAsync(CanonicalDependencyDomain domain)
                {
                    await using var context =
                        PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
                    var coordinator =
                        new CoglatasUiCanonicalChangeJournalCoordinator(context);
                    return await coordinator.ExecuteAuthoritativeMutationAsync(
                        scope,
                        domain,
                        static (_, _, _) => Task.CompletedTask);
                }

                var revisions = await Task.WhenAll(
                    WriteAsync(CanonicalDependencyDomain.WorkflowMetadata),
                    WriteAsync(CanonicalDependencyDomain.SelectedEntity));

                Assert.Equal(new long[] { 1, 2 }, revisions.Order().ToArray());

                var rows = await PostgreSqlMigrationTestDatabase.QueryAsync(
                    database,
                    """
                    SELECT "Revision", "ChangedDomains"
                    FROM coglatas_ui_canonical_change_journal
                    WHERE "ScopeKey" = @scopeKey
                    ORDER BY "Revision"
                    """,
                    reader => (
                        Revision: reader.GetInt64(0),
                        Domains: reader.GetInt32(1)),
                    ("scopeKey", CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(scope)));

                Assert.Equal(2, rows.Count);
                Assert.Equal(1, rows[0].Revision);
                Assert.Equal(2, rows[1].Revision);
            });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "CoglatasUiCanonicalJournal")]
    public async Task DatabaseGuardsContiguousHeadAndAppendOnlyJournal()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(
            connectionString,
            async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);

                var scope = NewScope();
                await using var context =
                    PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
                var coordinator = new CoglatasUiCanonicalChangeJournalCoordinator(context);
                var revision = await coordinator.ExecuteAuthoritativeMutationAsync(
                    scope,
                    CanonicalDependencyDomain.SelectedEntity,
                    static (_, _, _) => Task.CompletedTask);
                Assert.Equal(1, revision);

                var scopeKey = CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(scope);

                var invalidScope = NewScope();
                var invalidScopeKey =
                    CoglatasUiCanonicalChangeJournalCoordinator.CreateScopeKey(invalidScope);
                var invalidInitialHead = await Assert.ThrowsAsync<PostgresException>(() =>
                    PostgreSqlMigrationTestDatabase.ExecuteAsync(
                        database,
                        """
                        INSERT INTO coglatas_ui_canonical_revision_heads
                            ("ScopeKey", "TenantId", "WorkspaceId", "ProjectId", "Revision", "UpdatedAtUtc")
                        VALUES
                            (@scopeKey, @tenantId, @workspaceId, @projectId, 4, CURRENT_TIMESTAMP)
                        """,
                        ("scopeKey", invalidScopeKey),
                        ("tenantId", invalidScope.TenantId),
                        ("workspaceId", invalidScope.WorkspaceId),
                        ("projectId", invalidScope.ProjectId)));
                Assert.Equal(PostgresErrorCodes.CheckViolation, invalidInitialHead.SqlState);

                var identityRewrite = await Assert.ThrowsAsync<PostgresException>(() =>
                    PostgreSqlMigrationTestDatabase.ExecuteAsync(
                        database,
                        """
                        UPDATE coglatas_ui_canonical_revision_heads
                        SET "TenantId" = 'tampered-tenant',
                            "Revision" = 2
                        WHERE "ScopeKey" = @scopeKey
                        """,
                        ("scopeKey", scopeKey)));
                Assert.Equal(PostgresErrorCodes.CheckViolation, identityRewrite.SqlState);

                var skippedHead = await Assert.ThrowsAsync<PostgresException>(() =>
                    PostgreSqlMigrationTestDatabase.ExecuteAsync(
                        database,
                        """
                        UPDATE coglatas_ui_canonical_revision_heads
                        SET "Revision" = 3
                        WHERE "ScopeKey" = @scopeKey
                        """,
                        ("scopeKey", scopeKey)));
                Assert.Equal(PostgresErrorCodes.CheckViolation, skippedHead.SqlState);

                var mismatchedInsert = await Assert.ThrowsAsync<PostgresException>(() =>
                    PostgreSqlMigrationTestDatabase.ExecuteAsync(
                        database,
                        """
                        INSERT INTO coglatas_ui_canonical_change_journal
                            ("ScopeKey", "Revision", "ChangedDomains", "OccurredAtUtc")
                        VALUES
                            (@scopeKey, 2, 2, CURRENT_TIMESTAMP)
                        """,
                        ("scopeKey", scopeKey)));
                Assert.Equal(PostgresErrorCodes.CheckViolation, mismatchedInsert.SqlState);

                var rewritten = await Assert.ThrowsAsync<PostgresException>(() =>
                    PostgreSqlMigrationTestDatabase.ExecuteAsync(
                        database,
                        """
                        UPDATE coglatas_ui_canonical_change_journal
                        SET "ChangedDomains" = 8
                        WHERE "ScopeKey" = @scopeKey
                          AND "Revision" = 1
                        """,
                        ("scopeKey", scopeKey)));
                Assert.Equal(PostgresErrorCodes.CheckViolation, rewritten.SqlState);

                var deleted = await Assert.ThrowsAsync<PostgresException>(() =>
                    PostgreSqlMigrationTestDatabase.ExecuteAsync(
                        database,
                        """
                        DELETE FROM coglatas_ui_canonical_change_journal
                        WHERE "ScopeKey" = @scopeKey
                          AND "Revision" = 1
                        """,
                        ("scopeKey", scopeKey)));
                Assert.Equal(PostgresErrorCodes.CheckViolation, deleted.SqlState);

                var head = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT "Revision"
                    FROM coglatas_ui_canonical_revision_heads
                    WHERE "ScopeKey" = @scopeKey
                    """,
                    ("scopeKey", scopeKey));

                var journalCount = await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    """
                    SELECT COUNT(*)
                    FROM coglatas_ui_canonical_change_journal
                    WHERE "ScopeKey" = @scopeKey
                    """,
                    ("scopeKey", scopeKey));

                Assert.Equal(1, head);
                Assert.Equal(1, journalCount);
            });
    }

    private static ContextScope NewScope() =>
        new(
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"));
}
