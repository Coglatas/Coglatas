using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Synthetic, deterministic fixture builder for the explicitly opted-in PERF-02 Test stack.
/// The database is dedicated to Performance CI, so each startup truncates all application
/// tables (while retaining EF migration history) before rebuilding the selected PERF-01 profile.
/// </summary>
public static class PerformanceCiFixtureSeed
{
    public const int FixtureVersion = 1;
    public const int DbFixtureVersion = 2;
    public const string DatabaseName = "coglatas_performance";
    public const string TenantSlugPrefix = "perf-";
    private const int BatchSize = 2_000;
    private static readonly DateTimeOffset StableEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> AllowedDatabaseDataSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "postgres",
        "localhost",
        "127.0.0.1",
        "::1"
    };

    public static async Task SeedAsync(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        string manifestPath,
        string profileName,
        string password,
        string evidencePath,
        bool dbScenarioFixture = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);

        if (!dbContext.Database.IsNpgsql())
        {
            throw new InvalidOperationException("PERF-02 fixture requires real PostgreSQL.");
        }

        var connection = dbContext.Database.GetDbConnection();
        var configuredHost = new NpgsqlConnectionStringBuilder(connection.ConnectionString).Host;
        if (!string.Equals(connection.Database, DatabaseName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(configuredHost) ||
            !AllowedDatabaseDataSources.Contains(configuredHost))
        {
            throw new InvalidOperationException(
                $"PERF-02 refuses database target '{connection.DataSource}/{connection.Database}' " +
                $"(configured host '{configuredHost}'). Expected an isolated local/Compose {DatabaseName} database.");
        }

        var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (pendingMigrations.Length != 0)
        {
            throw new InvalidOperationException(
                $"PERF-02 database is not at migration head. Pending: {string.Join(", ", pendingMigrations)}");
        }

        var profile = await LoadProfileAsync(manifestPath, profileName, dbScenarioFixture ? DbFixtureVersion : FixtureVersion, cancellationToken);
        ValidateProfile(profile);

        await ResetDedicatedDatabaseAsync(dbContext, cancellationToken);
        var plan = FixturePlan.Create(profile);

        await SeedIdentityAndWorkspaceAsync(dbContext, passwordHasher, password, profile, plan, cancellationToken);
        await SeedProjectsAndWorkflowAsync(dbContext, profile, plan, cancellationToken);
        await SeedMilestonesAsync(dbContext, plan, cancellationToken);
        await SeedTasksAsync(dbContext, profile, plan, cancellationToken);
        await SeedDependenciesAsync(dbContext, profile, plan, cancellationToken);
        await SeedConversationsAndMessagesAsync(dbContext, profile, plan, cancellationToken);
        if (dbScenarioFixture) await StampMessageCursorKeysAsync(dbContext, plan.TenantId, cancellationToken);
        await SeedNotificationsAsync(dbContext, profile, plan, cancellationToken);
        await SeedAnnouncementsAsync(dbContext, profile, plan, cancellationToken);
        await SeedFilesAsync(dbContext, profile, plan, cancellationToken);

        await VerifyFixtureAsync(dbContext, profile, plan, cancellationToken);
        await WriteEvidenceAsync(profile, plan, evidencePath, cancellationToken);
    }

    private static async Task ResetDedicatedDatabaseAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DO $$
            DECLARE
                table_list text;
            BEGIN
                SELECT string_agg(format('%I.%I', schemaname, tablename), ', ')
                INTO table_list
                FROM pg_tables
                WHERE schemaname = 'public'
                  AND tablename <> '__EFMigrationsHistory';

                IF table_list IS NOT NULL THEN
                    EXECUTE 'TRUNCATE TABLE ' || table_list || ' RESTART IDENTITY CASCADE';
                END IF;
            END $$;
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private static async Task SeedIdentityAndWorkspaceAsync(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        string password,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        dbContext.Tenants.Add(new Tenant(plan.TenantId)
        {
            Name = $"PERF-02 {profile.Name} synthetic tenant",
            DisplayName = $"PERF-02 {profile.Name} synthetic tenant",
            Slug = plan.TenantSlug,
            Status = TenantStatus.Active
        });
        await SaveAndClearAsync(dbContext, cancellationToken);

        var sharedPasswordHash = passwordHasher.HashPassword(password);
        for (var index = 0; index < plan.UserIds.Length; index++)
        {
            var email = index == 0
                ? plan.OperatorEmail
                : $"perf-{profile.Name}-member-{index:D4}@example.test";
            AddWithId(dbContext, new User
            {
                DisplayName = index == 0
                    ? $"PERF-02 {profile.Name} operator"
                    : $"PERF-02 {profile.Name} member {index:D4}",
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                PasswordHash = sharedPasswordHash,
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            }, plan.UserIds[index]);
            await FlushIfNeededAsync(dbContext, index + 1, cancellationToken);
        }
        await SaveAndClearAsync(dbContext, cancellationToken);

        for (var index = 0; index < plan.UserIds.Length; index++)
        {
            AddWithId(dbContext, new TenantUser
            {
                TenantId = plan.TenantId,
                UserId = plan.UserIds[index],
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = StableEpoch.AddSeconds(index)
            }, StableGuid(profile.Seed, "tenant-user", index));
        }
        await SaveAndClearAsync(dbContext, cancellationToken);

        for (var index = 0; index < plan.WorkspaceIds.Length; index++)
        {
            AddWithId(dbContext, new Workspace
            {
                TenantId = plan.TenantId,
                Name = $"PERF workspace {index:D3}",
                Slug = $"perf-{profile.Name}-workspace-{index:D3}",
                Description = "Synthetic PERF-02 workspace.",
                TimeZone = "UTC",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = plan.UserIds[0]
            }, plan.WorkspaceIds[index]);
        }
        await SaveAndClearAsync(dbContext, cancellationToken);

        // PERF-01 "members" is represented by exactly this many WorkspaceMember rows.
        // All benchmark actors are placed in the focus workspace; other workspaces are
        // intentionally not visible to the operator so project-list cardinality is stable.
        for (var index = 0; index < plan.UserIds.Length; index++)
        {
            AddWithId(dbContext, new WorkspaceMember
            {
                TenantId = plan.TenantId,
                WorkspaceId = plan.FocusWorkspaceId,
                UserId = plan.UserIds[index],
                Role = index == 0 ? WorkspaceRole.Owner : WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = StableEpoch.AddSeconds(index)
            }, StableGuid(profile.Seed, "workspace-member", index));
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
    }

    private static async Task SeedProjectsAndWorkflowAsync(
        AppDbContext dbContext,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < plan.ProjectIds.Length; index++)
        {
            var workspaceId = plan.ProjectWorkspaceIds[index];
            var isFocusProject = index < profile.Focus["workspaceProjects"];
            var projectOwnerUserId = isFocusProject
                ? plan.UserIds[0]
                : plan.UserIds[1 + (index % (plan.UserIds.Length - 1))];

            AddWithId(dbContext, new Project
            {
                TenantId = plan.TenantId,
                WorkspaceId = workspaceId,
                OwnerUserId = projectOwnerUserId,
                CreatedByUserId = projectOwnerUserId,
                Name = $"PERF project {index:D4}",
                Slug = $"perf-{profile.Name}-project-{index:D4}",
                Description = "Synthetic PERF-02 project.",
                Status = ProjectStatus.Active,
                Visibility = ProjectVisibility.MembersOnly,
                ActivationState = ProjectActivationState.Activated,
                ActivatedAtUtc = StableEpoch,
                ActivationVersion = 1,
                StartDate = new DateOnly(2026, 1, 1),
                DueDate = new DateOnly(2026, 12, 31),
                VersionNo = 1
            }, plan.ProjectIds[index]);

            AddWithId(dbContext, new ProjectMember
            {
                TenantId = plan.TenantId,
                ProjectId = plan.ProjectIds[index],
                UserId = projectOwnerUserId,
                Role = ProjectRole.Owner,
                JoinedAt = StableEpoch
            }, StableGuid(profile.Seed, "project-member", index));
        }
        await SaveAndClearAsync(dbContext, cancellationToken);

        var categories = new[]
        {
            TaskStageCategory.Backlog,
            TaskStageCategory.Todo,
            TaskStageCategory.InProgress,
            TaskStageCategory.Review,
            TaskStageCategory.Done
        };
        var names = new[] { "Backlog", "Todo", "In Progress", "Review", "Done" };

        for (var projectIndex = 0; projectIndex < plan.ProjectIds.Length; projectIndex++)
        {
            var definitionId = StableGuid(profile.Seed, "workflow", projectIndex);
            AddWithId(dbContext, new TaskWorkflowDefinition
            {
                TenantId = plan.TenantId,
                WorkspaceId = plan.ProjectWorkspaceIds[projectIndex],
                ProjectId = plan.ProjectIds[projectIndex],
                Name = "PERF deterministic workflow",
                ReviewEnforcementEnabled = false,
                VersionNo = 1
            }, definitionId);

            for (var stageIndex = 0; stageIndex < categories.Length; stageIndex++)
            {
                var stageId = plan.StageIds[projectIndex][stageIndex];
                AddWithId(dbContext, new TaskWorkflowStage
                {
                    TenantId = plan.TenantId,
                    WorkspaceId = plan.ProjectWorkspaceIds[projectIndex],
                    ProjectId = plan.ProjectIds[projectIndex],
                    DefinitionId = definitionId,
                    Name = names[stageIndex],
                    InternalCategory = categories[stageIndex],
                    SortKey = (stageIndex + 1) * 1024L,
                    IsInitialStage = stageIndex == 0,
                    IsTerminalStage = stageIndex == categories.Length - 1,
                    VersionNo = 1
                }, stageId);
            }
            await FlushIfNeededAsync(dbContext, projectIndex + 1, cancellationToken);
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
    }

    private static async Task SeedMilestonesAsync(
        AppDbContext dbContext,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        var globalIndex = 0;
        for (var projectIndex = 0; projectIndex < plan.ProjectIds.Length; projectIndex++)
        {
            for (var localIndex = 0; localIndex < plan.MilestoneIds[projectIndex].Length; localIndex++)
            {
                await EnsureProjectTrackedAsync(
                    dbContext,
                    plan.ProjectIds[projectIndex],
                    cancellationToken);
                AddWithId(dbContext, new Milestone
                {
                    TenantId = plan.TenantId,
                    ProjectId = plan.ProjectIds[projectIndex],
                    Name = $"PERF milestone {projectIndex:D4}-{localIndex:D4}",
                    Description = "Synthetic PERF-02 milestone.",
                    DueDate = new DateOnly(2026, 12, 31),
                    Status = MilestoneStatus.NotStarted,
                    SortOrder = localIndex,
                    VersionNo = 1
                }, plan.MilestoneIds[projectIndex][localIndex]);
                globalIndex++;
                await FlushIfNeededAsync(dbContext, globalIndex, cancellationToken);
            }
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
    }

    private static async Task SeedTasksAsync(
        AppDbContext dbContext,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        var globalIndex = 0;
        var myTasksRemaining = profile.Focus["userMyTasks"];
        for (var projectIndex = 0; projectIndex < plan.ProjectIds.Length; projectIndex++)
        {
            var milestoneIds = plan.MilestoneIds[projectIndex];
            var tasks = plan.TaskIds[projectIndex];
            for (var localIndex = 0; localIndex < tasks.Length; localIndex++)
            {
                await EnsureProjectTrackedAsync(
                    dbContext,
                    plan.ProjectIds[projectIndex],
                    cancellationToken);
                var stageIndex = localIndex % 4;
                var assignedToOperator = projectIndex == 0 && myTasksRemaining > 0;
                if (assignedToOperator)
                {
                    myTasksRemaining--;
                }

                AddWithId(dbContext, new TaskItem
                {
                    TenantId = plan.TenantId,
                    WorkspaceId = plan.ProjectWorkspaceIds[projectIndex],
                    ProjectId = plan.ProjectIds[projectIndex],
                    MilestoneId = milestoneIds.Length == 0 ? null : milestoneIds[localIndex % milestoneIds.Length],
                    WorkflowStageId = plan.StageIds[projectIndex][stageIndex],
                    Kind = WorkItemKind.Task,
                    Title = $"PERF task {projectIndex:D4}-{localIndex:D6}",
                    Description = "Synthetic PERF-02 task.",
                    Status = stageIndex >= 2 ? TaskItemStatus.InProgress : TaskItemStatus.NotStarted,
                    Priority = (localIndex % 3) switch
                    {
                        0 => TaskPriority.Low,
                        1 => TaskPriority.Medium,
                        _ => TaskPriority.High
                    },
                    PrimaryAssigneeUserId = assignedToOperator
                        ? plan.UserIds[0]
                        : plan.UserIds[1 + (globalIndex % (plan.UserIds.Length - 1))],
                    PlannedStartDate = new DateOnly(2026, 1, 1).AddDays(localIndex % 180),
                    PlannedEndDate = new DateOnly(2026, 1, 2).AddDays(localIndex % 180),
                    SortKey = (localIndex + 1) * 1024L,
                    SortOrder = localIndex,
                    VersionNo = 1,
                    ProgressPercent = stageIndex >= 2 ? 50 : 0,
                    CreatedByUserId = plan.UserIds[0]
                }, tasks[localIndex]);

                globalIndex++;
                await FlushIfNeededAsync(dbContext, globalIndex, cancellationToken);
            }
        }

        if (myTasksRemaining != 0)
        {
            throw new InvalidOperationException("PERF-02 profile cannot satisfy userMyTasks from the focus project.");
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
    }

    private static async Task SeedDependenciesAsync(
        AppDbContext dbContext,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        var globalIndex = 0;
        for (var projectIndex = 0; projectIndex < plan.ProjectIds.Length; projectIndex++)
        {
            var taskIds = plan.TaskIds[projectIndex];
            var required = plan.DependencyCounts[projectIndex];
            var emitted = 0;
            for (var gap = 1; gap < taskIds.Length && emitted < required; gap++)
            {
                for (var predecessor = 0;
                     predecessor + gap < taskIds.Length && emitted < required;
                     predecessor++)
                {
                    AddWithId(dbContext, new TaskDependency
                    {
                        TenantId = plan.TenantId,
                        ProjectId = plan.ProjectIds[projectIndex],
                        PredecessorTaskItemId = taskIds[predecessor],
                        SuccessorTaskItemId = taskIds[predecessor + gap],
                        DependencyType = TaskDependencyType.FinishToStart
                    }, StableGuid(profile.Seed, $"dependency-{projectIndex}", emitted));
                    emitted++;
                    globalIndex++;
                    await FlushIfNeededAsync(dbContext, globalIndex, cancellationToken);
                }
            }

            if (emitted != required)
            {
                throw new InvalidOperationException(
                    $"PERF-02 project {projectIndex} cannot satisfy {required} acyclic dependencies.");
            }
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
    }

    private static async Task SeedConversationsAndMessagesAsync(
        AppDbContext dbContext,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < plan.ConversationIds.Length; index++)
        {
            AddWithId(dbContext, new Conversation
            {
                TenantId = plan.TenantId,
                WorkspaceId = plan.FocusWorkspaceId,
                ProjectId = plan.TaskListProjectId,
                Type = ConversationType.DirectMessage,
                Title = $"PERF conversation {index:D5}",
                CreatedByUserId = plan.UserIds[0]
            }, plan.ConversationIds[index]);

            AddWithId(dbContext, new ConversationMember
            {
                TenantId = plan.TenantId,
                ConversationId = plan.ConversationIds[index],
                UserId = plan.UserIds[0],
                Role = ConversationMemberRole.Admin,
                CanRead = true,
                CanPost = true,
                CanManageMembers = true,
                JoinedAt = StableEpoch
            }, StableGuid(profile.Seed, "conversation-member", index));
        }
        await SaveAndClearAsync(dbContext, cancellationToken);

        var messageCounts = AllocateWithFirst(
            profile.Counts["messages"],
            profile.Focus["conversationMessages"],
            plan.ConversationIds.Length);
        var globalIndex = 0;
        for (var conversationIndex = 0; conversationIndex < plan.ConversationIds.Length; conversationIndex++)
        {
            for (var localIndex = 0; localIndex < messageCounts[conversationIndex]; localIndex++)
            {
                AddWithId(dbContext, new Message
                {
                    TenantId = plan.TenantId,
                    WorkspaceId = plan.FocusWorkspaceId,
                    ConversationId = plan.ConversationIds[conversationIndex],
                    AuthorUserId = plan.UserIds[globalIndex % plan.UserIds.Length],
                    ClientRequestId = StableGuid(profile.Seed, "message-request", globalIndex),
                    Version = 1,
                    Body = $"PERF message {globalIndex:D8}"
                }, StableGuid(profile.Seed, "message", globalIndex));
                globalIndex++;
                await FlushIfNeededAsync(dbContext, globalIndex, cancellationToken);
            }
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
    }

    private static async Task StampMessageCursorKeysAsync(
        AppDbContext dbContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // SaveChanges stamps all new rows with a batch timestamp. Cursor probes
        // need deterministic, distinct keys, applied after that stamping.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE messages AS m SET "CreatedAt" = {StableEpoch} + ranked.ordinal * interval '1 millisecond'
            FROM (SELECT "Id", row_number() OVER (ORDER BY "Id") AS ordinal
                  FROM messages WHERE "TenantId" = {tenantId}) AS ranked
            WHERE m."Id" = ranked."Id"
            """, cancellationToken);
    }

    private static async Task SeedNotificationsAsync(
        AppDbContext dbContext,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        var operatorCount = profile.Focus["userNotifications"];
        for (var index = 0; index < profile.Counts["notifications"]; index++)
        {
            var userId = index < operatorCount
                ? plan.UserIds[0]
                : plan.UserIds[1 + ((index - operatorCount) % (plan.UserIds.Length - 1))];
            AddWithId(dbContext, new Notification
            {
                TenantId = plan.TenantId,
                UserId = userId,
                LogicalKey = $"perf-{profile.Name}-{index:D8}",
                NotificationType = NotificationType.System,
                Title = $"PERF notification {index:D8}",
                Body = "Synthetic PERF-02 notification.",
                IsRead = false,
                CreatedAt = StableEpoch.AddSeconds(index),
                StateVersion = 1
            }, StableGuid(profile.Seed, "notification", index));
            await FlushIfNeededAsync(dbContext, index + 1, cancellationToken);
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
    }

    private static async Task SeedAnnouncementsAsync(
        AppDbContext dbContext,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        var visible = profile.Focus["visibleAnnouncements"];
        for (var index = 0; index < profile.Counts["announcements"]; index++)
        {
            var isVisible = index < visible;
            var workspaceId = isVisible || plan.WorkspaceIds.Length == 1
                ? plan.FocusWorkspaceId
                : plan.WorkspaceIds[1 + ((index - visible) % (plan.WorkspaceIds.Length - 1))];
            AddWithId(dbContext, new Announcement
            {
                TenantId = plan.TenantId,
                WorkspaceId = workspaceId,
                AuthorUserId = plan.UserIds[0],
                Title = $"PERF announcement {index:D7}",
                Body = "Synthetic PERF-02 announcement.",
                Priority = AnnouncementPriority.Normal,
                IsPinned = index % 10 == 0,
                RequiresReadConfirmation = false,
                PublishedAt = StableEpoch.AddSeconds(index),
                ExpiresAt = isVisible || plan.WorkspaceIds.Length > 1
                    ? null
                    : DateTimeOffset.UnixEpoch
            }, StableGuid(profile.Seed, "announcement", index));
            await FlushIfNeededAsync(dbContext, index + 1, cancellationToken);
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
    }

    private static async Task SeedFilesAsync(
        AppDbContext dbContext,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        var focusFiles = profile.Focus["workspaceFiles"];
        for (var index = 0; index < profile.Counts["files"]; index++)
        {
            var inFocusWorkspace = index < focusFiles;
            var fileId = StableGuid(profile.Seed, "file", index);
            AddWithId(dbContext, new FileObject
            {
                TenantId = plan.TenantId,
                WorkspaceId = inFocusWorkspace ? plan.FocusWorkspaceId : null,
                ProjectId = inFocusWorkspace ? plan.TaskListProjectId : null,
                UploadedByUserId = plan.UserIds[index % plan.UserIds.Length],
                OriginalFileName = $"perf-file-{index:D8}.bin",
                StorageKey = $"performance/{profile.Name}/{index:D8}.bin",
                ContentType = "application/octet-stream",
                SizeBytes = 1024 + (index % 4096),
                HashSha256 = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes($"perf-file:{profile.Seed}:{index}")))
                    .ToLowerInvariant(),
                SharingPolicy = FileSharingPolicy.Private,
                SharingVersion = 1,
                Status = FileObjectStatus.Active
            }, fileId);
            if (inFocusWorkspace && profile.FixtureVersion == DbFixtureVersion)
            {
                AddWithId(dbContext, new Attachment
                {
                    TenantId = plan.TenantId,
                    FileObjectId = fileId,
                    WorkspaceId = plan.FocusWorkspaceId,
                    OwnerType = AttachmentOwnerType.Workspace,
                    OwnerId = plan.FocusWorkspaceId,
                    OwnerUserId = plan.UserIds[0],
                    UploadedByUserId = plan.UserIds[0],
                    FileName = $"perf-file-{index:D8}.bin",
                    StoredFileName = fileId.ToString("N"),
                    FilePath = $"performance/{profile.Name}/{index:D8}.bin",
                    ContentType = "application/octet-stream",
                    Extension = ".bin",
                    SizeBytes = 1024 + (index % 4096),
                    StorageProvider = "PerformanceFixture",
                    StorageKey = $"performance/{profile.Name}/{index:D8}.bin",
                    ScanStatus = FileScanStatus.Skipped
                }, StableGuid(profile.Seed, "workspace-attachment", index));
            }
            await FlushIfNeededAsync(dbContext, index + 1, cancellationToken);
        }
        await SaveAndClearAsync(dbContext, cancellationToken);
        if (profile.FixtureVersion == DbFixtureVersion)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE file_objects AS f SET "CreatedAt" = {StableEpoch} + ranked.ordinal * interval '1 millisecond'
            FROM (SELECT "Id", row_number() OVER (ORDER BY "Id") AS ordinal
                  FROM file_objects WHERE "TenantId" = {plan.TenantId}) AS ranked
            WHERE f."Id" = ranked."Id"
            """, cancellationToken);
        }
    }

    private static async Task VerifyFixtureAsync(
        AppDbContext dbContext,
        DatasetProfile profile,
        FixturePlan plan,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["tenants"] = await dbContext.Tenants.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["workspaces"] = await dbContext.Workspaces.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["projects"] = await dbContext.Projects.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["tasks"] = await dbContext.TaskItems.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["workItems"] = await dbContext.TaskItems.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["milestones"] = await dbContext.Milestones.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["dependencies"] = await dbContext.TaskDependencies.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["members"] = await dbContext.WorkspaceMembers.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["messages"] = await dbContext.Messages.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["notifications"] = await dbContext.Notifications.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["announcements"] = await dbContext.Announcements.IgnoreQueryFilters().CountAsync(cancellationToken),
            ["files"] = await dbContext.FileObjects.IgnoreQueryFilters().CountAsync(cancellationToken)
        };

        foreach (var (key, expected) in profile.Counts)
        {
            if (!counts.TryGetValue(key, out var actual) || actual != expected)
            {
                throw new InvalidOperationException(
                    $"PERF-02 fixture cardinality mismatch for {key}: expected {expected}, got {actual}.");
            }
        }

        await AssertCountAsync(
            "workspaceProjects",
            profile.Focus["workspaceProjects"],
            dbContext.Projects.IgnoreQueryFilters().CountAsync(
                project => project.WorkspaceId == plan.FocusWorkspaceId, cancellationToken));
        await AssertCountAsync(
            "projectTasks",
            profile.Focus["projectTasks"],
            dbContext.TaskItems.IgnoreQueryFilters().CountAsync(
                task => task.ProjectId == plan.TaskListProjectId, cancellationToken));
        await AssertCountAsync(
            "projectMilestones",
            profile.Focus["projectMilestones"],
            dbContext.Milestones.IgnoreQueryFilters().CountAsync(
                milestone => milestone.ProjectId == plan.GanttProjectId, cancellationToken));
        await AssertCountAsync(
            "projectDependencies",
            profile.Focus["projectDependencies"],
            dbContext.TaskDependencies.IgnoreQueryFilters().CountAsync(
                dependency => dependency.ProjectId == plan.GanttProjectId, cancellationToken));
        await AssertCountAsync(
            "userMyTasks",
            profile.Focus["userMyTasks"],
            dbContext.TaskItems.IgnoreQueryFilters().CountAsync(
                task => task.PrimaryAssigneeUserId == plan.UserIds[0], cancellationToken));
        await AssertCountAsync(
            "workspaceFiles",
            profile.Focus["workspaceFiles"],
            dbContext.FileObjects.IgnoreQueryFilters().CountAsync(
                file => file.WorkspaceId == plan.FocusWorkspaceId, cancellationToken));
        if (profile.FixtureVersion == DbFixtureVersion)
        {
            await AssertCountAsync(
            "workspaceAttachments",
            profile.Focus["workspaceFiles"],
            dbContext.Attachments.IgnoreQueryFilters().CountAsync(
                attachment => attachment.WorkspaceId == plan.FocusWorkspaceId &&
                              attachment.OwnerType == AttachmentOwnerType.Workspace &&
                              attachment.OwnerId == plan.FocusWorkspaceId, cancellationToken));
        }
        await AssertCountAsync(
            "conversations",
            profile.Focus["conversations"],
            dbContext.Conversations.IgnoreQueryFilters().CountAsync(
                conversation => conversation.WorkspaceId == plan.FocusWorkspaceId, cancellationToken));
        await AssertCountAsync(
            "conversationMessages",
            profile.Focus["conversationMessages"],
            dbContext.Messages.IgnoreQueryFilters().CountAsync(
                message => message.ConversationId == plan.FocusConversationId, cancellationToken));
        if (profile.FixtureVersion == DbFixtureVersion)
        {
            await AssertCountAsync(
            "distinctMessageCursorKeys",
            profile.Counts["messages"],
            dbContext.Messages.IgnoreQueryFilters().Select(message => message.CreatedAt).Distinct().CountAsync(cancellationToken));
        }
        await AssertCountAsync(
            "userNotifications",
            profile.Focus["userNotifications"],
            dbContext.Notifications.IgnoreQueryFilters().CountAsync(
                notification => notification.UserId == plan.UserIds[0], cancellationToken));

        var visibleAnnouncements = await dbContext.Announcements
            .IgnoreQueryFilters()
            .CountAsync(
                announcement =>
                    announcement.WorkspaceId == plan.FocusWorkspaceId &&
                    (announcement.ExpiresAt == null || announcement.ExpiresAt > DateTimeOffset.UtcNow),
                cancellationToken);
        if (visibleAnnouncements != profile.Focus["visibleAnnouncements"])
        {
            throw new InvalidOperationException(
                $"PERF-02 fixture focus mismatch for visibleAnnouncements: expected " +
                $"{profile.Focus["visibleAnnouncements"]}, got {visibleAnnouncements}.");
        }

        var kanbanCapacity = await dbContext.TaskItems.IgnoreQueryFilters().CountAsync(
            task => task.ProjectId == plan.KanbanProjectId, cancellationToken);
        if (kanbanCapacity < profile.Focus["kanbanAuthorizedCards"])
        {
            throw new InvalidOperationException(
                $"PERF-02 kanban focus has only {kanbanCapacity} tasks; " +
                $"{profile.Focus["kanbanAuthorizedCards"]} are required.");
        }
    }

    private static async Task AssertCountAsync(string name, int expected, Task<int> actualTask)
    {
        var actual = await actualTask;
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"PERF-02 fixture focus mismatch for {name}: expected {expected}, got {actual}.");
        }
    }

    private static async Task WriteEvidenceAsync(
        DatasetProfile profile,
        FixturePlan plan,
        string evidencePath,
        CancellationToken cancellationToken)
    {
        var evidence = new
        {
            schemaVersion = 1,
            fixtureVersion = profile.FixtureVersion,
            seedManifestVersion = profile.SeedManifestVersion,
            profile = profile.Name,
            seed = profile.Seed,
            fixtureHash = profile.FixtureHash,
            migrationStatus = "current",
            complete = true,
            cardinalities = profile.Counts,
            focus = profile.Focus,
            identities = new
            {
                tenantSlug = plan.TenantSlug,
                operatorEmail = plan.OperatorEmail,
                workspaceId = plan.FocusWorkspaceId.ToString("D"),
                taskListProjectId = plan.TaskListProjectId.ToString("D"),
                ganttProjectId = plan.GanttProjectId.ToString("D"),
                kanbanProjectId = plan.KanbanProjectId.ToString("D"),
                conversationId = plan.FocusConversationId.ToString("D"),
                taskId = plan.TaskIds[0][0].ToString("D")
            }
        };

        var fullPath = Path.GetFullPath(evidencePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("PERF-02 evidence path requires a parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".fixture-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                Encoding.UTF8,
                cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<DatasetProfile> LoadProfileAsync(
        string manifestPath,
        string profileName,
        int fixtureVersion,
        CancellationToken cancellationToken)
    {
        if (profileName is not ("small" or "medium" or "large"))
        {
            throw new InvalidOperationException("PERF-02 profile must be small, medium, or large.");
        }

        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        var seedManifestVersion = root.GetProperty("seedManifestVersion").GetInt32();
        if (schemaVersion != 1 || seedManifestVersion != 1)
        {
            throw new InvalidOperationException("PERF-02 supports only PERF-01 dataset schema/seed manifest version 1.");
        }

        var profiles = root.GetProperty("profiles");
        if (!profiles.TryGetProperty(profileName, out var profileElement))
        {
            throw new InvalidOperationException($"PERF-02 manifest does not contain profile '{profileName}'.");
        }

        var seed = profileElement.GetProperty("seed").GetInt32();
        var counts = ReadIntMap(profileElement.GetProperty("counts"));
        var focus = ReadIntMap(profileElement.GetProperty("focus"));
        var manifestSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var canonical = string.Concat(
            $"fixtureVersion={fixtureVersion}\n",
            $"manifestSha256={manifestSha}\n",
            $"profile={profileName}\n",
            $"seed={seed}\n");
        var fixtureHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        return new DatasetProfile(
            profileName,
            seed,
            fixtureVersion,
            seedManifestVersion,
            fixtureHash,
            counts,
            focus);
    }

    private static Dictionary<string, int> ReadIntMap(JsonElement element)
    {
        return element.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetInt32(), StringComparer.Ordinal);
    }

    private static void ValidateProfile(DatasetProfile profile)
    {
        string[] countKeys =
        [
            "tenants", "workspaces", "projects", "tasks", "workItems", "milestones",
            "dependencies", "members", "messages", "notifications", "announcements", "files"
        ];
        string[] focusKeys =
        [
            "workspaceProjects", "projectTasks", "projectMilestones", "projectDependencies",
            "userMyTasks", "kanbanAuthorizedCards", "workspaceFiles", "conversations",
            "conversationMessages", "userNotifications", "visibleAnnouncements"
        ];

        foreach (var key in countKeys)
        {
            if (!profile.Counts.TryGetValue(key, out var value) || value < 0)
            {
                throw new InvalidOperationException($"PERF-02 manifest missing non-negative count '{key}'.");
            }
        }
        foreach (var key in focusKeys)
        {
            if (!profile.Focus.TryGetValue(key, out var value) || value < 0)
            {
                throw new InvalidOperationException($"PERF-02 manifest missing non-negative focus '{key}'.");
            }
        }

        if (profile.Counts["tenants"] != 1 ||
            profile.Counts["tasks"] != profile.Counts["workItems"] ||
            profile.Counts["members"] < 2 ||
            profile.Counts["workspaces"] < 1 ||
            profile.Counts["projects"] < profile.Focus["workspaceProjects"] ||
            profile.Counts["tasks"] < profile.Focus["projectTasks"] ||
            profile.Counts["milestones"] < profile.Focus["projectMilestones"] ||
            profile.Counts["dependencies"] < profile.Focus["projectDependencies"] ||
            profile.Counts["messages"] < profile.Focus["conversationMessages"] ||
            profile.Counts["notifications"] < profile.Focus["userNotifications"] ||
            profile.Counts["announcements"] < profile.Focus["visibleAnnouncements"] ||
            profile.Counts["files"] < profile.Focus["workspaceFiles"] ||
            profile.Focus["conversations"] < 1)
        {
            throw new InvalidOperationException("PERF-02 manifest cardinalities are internally inconsistent.");
        }
    }

    private static async Task EnsureProjectTrackedAsync(
        AppDbContext dbContext,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (dbContext.Projects.Local.Any(project => project.Id == projectId))
        {
            return;
        }

        var project = await dbContext.Projects
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"PERF-02 Project {projectId:D} disappeared during fixture construction.");
        if (project.IsDeleted ||
            project.Status is not (ProjectStatus.Active or ProjectStatus.Review) ||
            project.ActivationState != ProjectActivationState.Activated ||
            !project.ActivatedAtUtc.HasValue ||
            project.ActivationVersion is not > 0)
        {
            throw new InvalidOperationException(
                $"PERF-02 Project {projectId:D} is not an activated writable Project.");
        }
    }

    private static void AddWithId<T>(AppDbContext dbContext, T entity, Guid id)
        where T : class
    {
        dbContext.Add(entity);
        dbContext.Entry(entity).Property("Id").CurrentValue = id;
    }

    private static async Task FlushIfNeededAsync(
        AppDbContext dbContext,
        int emitted,
        CancellationToken cancellationToken)
    {
        if (emitted % BatchSize == 0)
        {
            await SaveAndClearAsync(dbContext, cancellationToken);
        }
    }

    private static async Task SaveAndClearAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        dbContext.ChangeTracker.Clear();
    }

    private static Guid StableGuid(int seed, string kind, int index)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"perf02:{seed}:{kind}:{index}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static int[] AllocateWithFirst(int total, int first, int buckets)
    {
        if (buckets <= 0 || first > total)
        {
            throw new InvalidOperationException("PERF-02 allocation is impossible.");
        }
        var result = new int[buckets];
        result[0] = first;
        var remaining = total - first;
        var start = buckets == 1 ? 0 : 1;
        var width = buckets == 1 ? 1 : buckets - 1;
        for (var index = 0; index < remaining; index++)
        {
            result[start + (index % width)]++;
        }
        return result;
    }

    private sealed record DatasetProfile(
        string Name,
        int Seed,
        int FixtureVersion,
        int SeedManifestVersion,
        string FixtureHash,
        Dictionary<string, int> Counts,
        Dictionary<string, int> Focus);

    private sealed class FixturePlan
    {
        private FixturePlan(
            Guid tenantId,
            string tenantSlug,
            string operatorEmail,
            Guid[] userIds,
            Guid[] workspaceIds,
            Guid[] projectIds,
            Guid[] projectWorkspaceIds,
            Guid[][] stageIds,
            Guid[][] milestoneIds,
            Guid[][] taskIds,
            int[] dependencyCounts,
            Guid[] conversationIds,
            Guid kanbanProjectId)
        {
            TenantId = tenantId;
            TenantSlug = tenantSlug;
            OperatorEmail = operatorEmail;
            UserIds = userIds;
            WorkspaceIds = workspaceIds;
            ProjectIds = projectIds;
            ProjectWorkspaceIds = projectWorkspaceIds;
            StageIds = stageIds;
            MilestoneIds = milestoneIds;
            TaskIds = taskIds;
            DependencyCounts = dependencyCounts;
            ConversationIds = conversationIds;
            KanbanProjectId = kanbanProjectId;
        }

        public Guid TenantId { get; }
        public string TenantSlug { get; }
        public string OperatorEmail { get; }
        public Guid[] UserIds { get; }
        public Guid[] WorkspaceIds { get; }
        public Guid[] ProjectIds { get; }
        public Guid[] ProjectWorkspaceIds { get; }
        public Guid[][] StageIds { get; }
        public Guid[][] MilestoneIds { get; }
        public Guid[][] TaskIds { get; }
        public int[] DependencyCounts { get; }
        public Guid[] ConversationIds { get; }
        public Guid FocusWorkspaceId => WorkspaceIds[0];
        public Guid TaskListProjectId => ProjectIds[0];
        public Guid GanttProjectId => ProjectIds[0];
        public Guid KanbanProjectId { get; }
        public Guid FocusConversationId => ConversationIds[0];

        public static FixturePlan Create(DatasetProfile profile)
        {
            var users = Enumerable.Range(0, profile.Counts["members"])
                .Select(index => StableGuid(profile.Seed, "user", index))
                .ToArray();
            var workspaces = Enumerable.Range(0, profile.Counts["workspaces"])
                .Select(index => StableGuid(profile.Seed, "workspace", index))
                .ToArray();
            var projects = Enumerable.Range(0, profile.Counts["projects"])
                .Select(index => StableGuid(profile.Seed, "project", index))
                .ToArray();

            var projectWorkspaceIds = new Guid[projects.Length];
            for (var index = 0; index < projects.Length; index++)
            {
                if (index < profile.Focus["workspaceProjects"] || workspaces.Length == 1)
                {
                    projectWorkspaceIds[index] = workspaces[0];
                }
                else
                {
                    projectWorkspaceIds[index] = workspaces[
                        1 + ((index - profile.Focus["workspaceProjects"]) % (workspaces.Length - 1))];
                }
            }

            var taskCounts = AllocateTaskCounts(profile, projects.Length, out var kanbanProjectIndex);

            var milestoneCounts = AllocateWithFirst(
                profile.Counts["milestones"],
                profile.Focus["projectMilestones"],
                projects.Length);
            var dependencyCounts = AllocateDependencies(
                profile.Counts["dependencies"],
                profile.Focus["projectDependencies"],
                taskCounts);

            var stageIds = new Guid[projects.Length][];
            var milestoneIds = new Guid[projects.Length][];
            var taskIds = new Guid[projects.Length][];
            for (var projectIndex = 0; projectIndex < projects.Length; projectIndex++)
            {
                stageIds[projectIndex] = Enumerable.Range(0, 5)
                    .Select(stageIndex => StableGuid(profile.Seed, $"stage-{projectIndex}", stageIndex))
                    .ToArray();
                milestoneIds[projectIndex] = Enumerable.Range(0, milestoneCounts[projectIndex])
                    .Select(index => StableGuid(profile.Seed, $"milestone-{projectIndex}", index))
                    .ToArray();
                taskIds[projectIndex] = Enumerable.Range(0, taskCounts[projectIndex])
                    .Select(index => StableGuid(profile.Seed, $"task-{projectIndex}", index))
                    .ToArray();
            }

            var conversations = Enumerable.Range(0, profile.Focus["conversations"])
                .Select(index => StableGuid(profile.Seed, "conversation", index))
                .ToArray();

            return new FixturePlan(
                StableGuid(profile.Seed, "tenant", 0),
                $"{TenantSlugPrefix}{profile.Name}",
                $"perf-{profile.Name}-operator@example.test",
                users,
                workspaces,
                projects,
                projectWorkspaceIds,
                stageIds,
                milestoneIds,
                taskIds,
                dependencyCounts,
                conversations,
                projects[kanbanProjectIndex]);
        }

        private static int[] AllocateTaskCounts(
            DatasetProfile profile,
            int projectCount,
            out int kanbanProjectIndex)
        {
            if (projectCount <= 0)
            {
                throw new InvalidOperationException("PERF-02 requires at least one project.");
            }

            var total = profile.Counts["tasks"];
            var projectFocus = profile.Focus["projectTasks"];
            var kanbanFocus = profile.Focus["kanbanAuthorizedCards"];
            if (projectFocus > total)
            {
                throw new InvalidOperationException("PERF-02 project task focus exceeds total task count.");
            }

            var result = new int[projectCount];
            result[0] = projectFocus;
            kanbanProjectIndex = 0;
            var nextProject = 1;

            if (kanbanFocus > projectFocus)
            {
                if (projectCount < 2 || projectFocus + kanbanFocus > total)
                {
                    throw new InvalidOperationException("PERF-02 profile cannot allocate a separate kanban focus project.");
                }
                result[1] = kanbanFocus;
                kanbanProjectIndex = 1;
                nextProject = 2;
            }

            var remaining = total - result.Sum();
            if (remaining == 0)
            {
                return result;
            }

            if (nextProject >= projectCount)
            {
                // Keep project 0 exact for projectTasks/Gantt assertions. A separate
                // Kanban project may absorb additional non-focus rows if no other
                // project remains, while still satisfying its minimum cardinality.
                if (kanbanProjectIndex == 0)
                {
                    throw new InvalidOperationException("PERF-02 cannot preserve the focus project task cardinality.");
                }
                result[kanbanProjectIndex] += remaining;
                return result;
            }

            var width = projectCount - nextProject;
            for (var index = 0; index < remaining; index++)
            {
                result[nextProject + (index % width)]++;
            }
            return result;
        }

        private static int[] AllocateDependencies(int total, int first, int[] taskCounts)
        {
            var result = new int[taskCounts.Length];
            result[0] = first;
            var remaining = total - first;
            while (remaining > 0)
            {
                var progressed = false;
                for (var projectIndex = 1; projectIndex < taskCounts.Length && remaining > 0; projectIndex++)
                {
                    var capacity = taskCounts[projectIndex] * (taskCounts[projectIndex] - 1) / 2;
                    if (result[projectIndex] >= capacity)
                    {
                        continue;
                    }
                    result[projectIndex]++;
                    remaining--;
                    progressed = true;
                }
                if (!progressed)
                {
                    throw new InvalidOperationException("PERF-02 profile dependency count exceeds acyclic task capacity.");
                }
            }

            var firstCapacity = taskCounts[0] * (taskCounts[0] - 1) / 2;
            if (result[0] > firstCapacity)
            {
                throw new InvalidOperationException("PERF-02 focus dependency count exceeds acyclic task capacity.");
            }
            return result;
        }
    }
}
