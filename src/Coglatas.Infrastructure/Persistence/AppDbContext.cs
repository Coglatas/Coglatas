using Coglatas.Domain.Common;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Coglatas.Infrastructure.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ICurrentTenant currentTenant) : DbContext(options)
{
    private static readonly HashSet<string> ImmutableTaskExecutionRunSnapshotProperties = new(StringComparer.Ordinal)
    {
        nameof(TaskExecutionRun.TenantId),
        nameof(TaskExecutionRun.WorkspaceId),
        nameof(TaskExecutionRun.ProjectId),
        nameof(TaskExecutionRun.TaskItemId),
        nameof(TaskExecutionRun.RequestedByUserId),
        nameof(TaskExecutionRun.RequestedAtUtc),
        nameof(TaskExecutionRun.RuntimeProvider),
        nameof(TaskExecutionRun.RuntimeContractVersion),
        nameof(TaskExecutionRun.SnapshotSchemaVersion),
        nameof(TaskExecutionRun.SnapshotScopeOrigin),
        nameof(TaskExecutionRun.SnapshotProjectScopeVersion),
        nameof(TaskExecutionRun.SnapshotTaskOverrideVersion),
        nameof(TaskExecutionRun.SnapshotWebEnabled),
        nameof(TaskExecutionRun.SnapshotProjectFilesEnabled),
        nameof(TaskExecutionRun.SnapshotResearchPlanRevisionId),
        nameof(TaskExecutionRun.SnapshotResearchPlanRevisionNo)
    };

    internal Guid? ActiveTenantId =>
        currentTenant.IsAvailable && !currentTenant.IsPlatformScope
            ? currentTenant.TenantId
            : null;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();
    public DbSet<ExportPackageGrant> ExportPackageGrants => Set<ExportPackageGrant>();
    public DbSet<IntegrationAccount> IntegrationAccounts => Set<IntegrationAccount>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelMember> ChannelMembers => Set<ChannelMember>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostThread> PostThreads => Set<PostThread>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<MessageFollowUp> MessageFollowUps => Set<MessageFollowUp>();
    public DbSet<ReadState> ReadStates => Set<ReadState>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationUserState> NotificationUserStates => Set<NotificationUserState>();
    public DbSet<TaskDeadlineDigestJob> TaskDeadlineDigestJobs => Set<TaskDeadlineDigestJob>();
    public DbSet<TaskDeadlineDigestAttempt> TaskDeadlineDigestAttempts => Set<TaskDeadlineDigestAttempt>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<AnnouncementRead> AnnouncementReads => Set<AnnouncementRead>();
    public DbSet<AnnouncementDraft> AnnouncementDrafts => Set<AnnouncementDraft>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<EventAttendance> EventAttendances => Set<EventAttendance>();
    public DbSet<StudentRecord> StudentRecords => Set<StudentRecord>();
    public DbSet<InternalForm> InternalForms => Set<InternalForm>();
    public DbSet<FormQuestion> FormQuestions => Set<FormQuestion>();
    public DbSet<FormResponse> FormResponses => Set<FormResponse>();
    public DbSet<FormAnswer> FormAnswers => Set<FormAnswer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectExecutionScope> ProjectExecutionScopes => Set<ProjectExecutionScope>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<TaskExecutionScopeOverride> TaskExecutionScopeOverrides => Set<TaskExecutionScopeOverride>();
    public DbSet<TaskExecutionRun> TaskExecutionRuns => Set<TaskExecutionRun>();
    public DbSet<ResearchPlan> ResearchPlans => Set<ResearchPlan>();
    public DbSet<ResearchPlanRevision> ResearchPlanRevisions => Set<ResearchPlanRevision>();
    public DbSet<ResearchPlanStep> ResearchPlanSteps => Set<ResearchPlanStep>();
    public DbSet<TaskWorkflowDefinition> TaskWorkflowDefinitions => Set<TaskWorkflowDefinition>();
    public DbSet<TaskWorkflowStage> TaskWorkflowStages => Set<TaskWorkflowStage>();
    public DbSet<WorkItemCollaborator> WorkItemCollaborators => Set<WorkItemCollaborator>();
    public DbSet<TaskMigrationInventory> TaskMigrationInventories => Set<TaskMigrationInventory>();
    public DbSet<TaskChecklistItem> TaskChecklistItems => Set<TaskChecklistItem>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<WorkItemWatchState> WorkItemWatchStates => Set<WorkItemWatchState>();
    public DbSet<ProjectTaskLabel> ProjectTaskLabels => Set<ProjectTaskLabel>();
    public DbSet<WorkItemLabel> WorkItemLabels => Set<WorkItemLabel>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<TaskAssignment> TaskAssignments => Set<TaskAssignment>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ArtifactVersion> ArtifactVersions => Set<ArtifactVersion>();
    public DbSet<ArtifactReportDocument> ArtifactReportDocuments => Set<ArtifactReportDocument>();
    public DbSet<ArtifactReportSection> ArtifactReportSections => Set<ArtifactReportSection>();
    public DbSet<ArtifactReportCitation> ArtifactReportCitations => Set<ArtifactReportCitation>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<FileObject> FileObjects => Set<FileObject>();
    public DbSet<FileAccessGrant> FileAccessGrants => Set<FileAccessGrant>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<FileDownloadGrant> FileDownloadGrants => Set<FileDownloadGrant>();
    public DbSet<FileSelectionSnapshot> FileSelectionSnapshots => Set<FileSelectionSnapshot>();
    public DbSet<FileSelectionSnapshotItem> FileSelectionSnapshotItems => Set<FileSelectionSnapshotItem>();
    public DbSet<FileScanResult> FileScanResults => Set<FileScanResult>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<FeatureModule> FeatureModules => Set<FeatureModule>();
    public DbSet<PanelDefinition> PanelDefinitions => Set<PanelDefinition>();
    public DbSet<UserLayout> UserLayouts => Set<UserLayout>();
    public DbSet<CommandDefinition> CommandDefinitions => Set<CommandDefinition>();
    public DbSet<RadialMenuProfile> RadialMenuProfiles => Set<RadialMenuProfile>();
    public DbSet<RadialMenuItem> RadialMenuItems => Set<RadialMenuItem>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureProjectExecutionScopeDefaults();
        EnsureTaskExecutionRunSnapshotImmutability();
        EnsureResearchPlanSnapshotImmutability();
        StampAuditableEntities();
        var hasNormalTenantWrite = ApplyTenantRules();
        EnsureLegacyUnclassifiedOperationalTaskWorkflowCompatibility();
        IncrementProjectAggregateVersions();
        IncrementTaskAggregateVersions();
        if (hasNormalTenantWrite)
        {
            await EnsureCurrentTenantCanWriteAsync(cancellationToken);
        }

        if (!RequiresTaskDeadlineDigestMutationFence() || !Database.IsNpgsql())
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var ownsTransaction = Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        await AcquireTaskDeadlineDigestMutationFenceAsync(cancellationToken);
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureProjectExecutionScopeDefaults();
        EnsureTaskExecutionRunSnapshotImmutability();
        EnsureResearchPlanSnapshotImmutability();
        StampAuditableEntities();
        var hasNormalTenantWrite = ApplyTenantRules();
        EnsureLegacyUnclassifiedOperationalTaskWorkflowCompatibility();
        IncrementProjectAggregateVersions();
        IncrementTaskAggregateVersions();
        if (hasNormalTenantWrite)
        {
            EnsureCurrentTenantCanWrite();
        }

        if (!RequiresTaskDeadlineDigestMutationFence() || !Database.IsNpgsql())
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        var ownsTransaction = Database.CurrentTransaction is null;
        using var transaction = ownsTransaction
            ? Database.BeginTransaction()
            : null;
        AcquireTaskDeadlineDigestMutationFence();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        transaction?.Commit();
        return result;
    }

    /// <summary>
    /// Protects digest predicates that depend on an optional relationship row.
    /// PostgreSQL row locks protect existing rows only, so a digest's shared
    /// read lock takes a stable parent and every relationship writer takes the
    /// same parent exclusively before inserting, changing, or deleting the
    /// child. This hook is intentionally at the DbContext boundary so legacy
    /// commands, direct EF writers, and test mutations share the contract.
    /// </summary>
    private bool RequiresTaskDeadlineDigestMutationFence() =>
        HasMutation<TenantSettings>() ||
        HasMutation<Subscription>() ||
        HasMutation<WorkspaceMember>() ||
        HasMutation<ProjectMember>() ||
        HasMutation<GroupMember>() ||
        HasMutation<WorkItemWatchState>() ||
        HasMutation<WorkItemCollaborator>();

    private bool HasMutation<TEntity>() where TEntity : class =>
        ChangeTracker.Entries<TEntity>().Any(entry => IsMutation(entry.State));

    private static bool IsMutation(EntityState state) =>
        state is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    private async Task AcquireTaskDeadlineDigestMutationFenceAsync(CancellationToken cancellationToken)
    {
        var pivots = CollectTaskDeadlineDigestMutationPivots();
        if (pivots.TenantIds.Length > 0)
        {
            await Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM tenants
                WHERE "Id" = ANY({pivots.TenantIds})
                ORDER BY "Id"
                FOR UPDATE
                """, cancellationToken);
        }
        if (pivots.WorkspaceIds.Length > 0)
        {
            await Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM workspaces
                WHERE "Id" = ANY({pivots.WorkspaceIds})
                ORDER BY "Id"
                FOR UPDATE
                """, cancellationToken);
        }
        if (pivots.ProjectIds.Length > 0)
        {
            await Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM projects
                WHERE "Id" = ANY({pivots.ProjectIds})
                ORDER BY "Id"
                FOR UPDATE
                """, cancellationToken);
        }
        if (pivots.GroupIds.Length > 0)
        {
            await Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM groups
                WHERE "Id" = ANY({pivots.GroupIds})
                ORDER BY "Id"
                FOR UPDATE
                """, cancellationToken);
        }
        if (pivots.TaskIds.Length > 0)
        {
            await Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM task_items
                WHERE "Id" = ANY({pivots.TaskIds})
                ORDER BY "Id"
                FOR UPDATE
                """, cancellationToken);
        }
    }

    private void AcquireTaskDeadlineDigestMutationFence()
    {
        var pivots = CollectTaskDeadlineDigestMutationPivots();
        if (pivots.TenantIds.Length > 0)
        {
            Database.ExecuteSqlInterpolated($"""
                SELECT 1 FROM tenants
                WHERE "Id" = ANY({pivots.TenantIds})
                ORDER BY "Id"
                FOR UPDATE
                """);
        }
        if (pivots.WorkspaceIds.Length > 0)
        {
            Database.ExecuteSqlInterpolated($"""
                SELECT 1 FROM workspaces
                WHERE "Id" = ANY({pivots.WorkspaceIds})
                ORDER BY "Id"
                FOR UPDATE
                """);
        }
        if (pivots.ProjectIds.Length > 0)
        {
            Database.ExecuteSqlInterpolated($"""
                SELECT 1 FROM projects
                WHERE "Id" = ANY({pivots.ProjectIds})
                ORDER BY "Id"
                FOR UPDATE
                """);
        }
        if (pivots.GroupIds.Length > 0)
        {
            Database.ExecuteSqlInterpolated($"""
                SELECT 1 FROM groups
                WHERE "Id" = ANY({pivots.GroupIds})
                ORDER BY "Id"
                FOR UPDATE
                """);
        }
        if (pivots.TaskIds.Length > 0)
        {
            Database.ExecuteSqlInterpolated($"""
                SELECT 1 FROM task_items
                WHERE "Id" = ANY({pivots.TaskIds})
                ORDER BY "Id"
                FOR UPDATE
                """);
        }
    }

    private TaskDeadlineDigestMutationPivots CollectTaskDeadlineDigestMutationPivots()
    {
        var addedTenantIds = AddedEntityIds<Tenant>();
        var addedWorkspaceIds = AddedEntityIds<Workspace>();
        var addedProjectIds = AddedEntityIds<Project>();
        var addedGroupIds = AddedEntityIds<Group>();
        var addedTaskIds = AddedEntityIds<TaskItem>();
        var tenantIds = MutationParentIds<TenantSettings>(nameof(global::Coglatas.Domain.Entities.TenantSettings.TenantId))
            .Concat(MutationParentIds<Subscription>(nameof(Subscription.TenantId)))
            .Where(id => !addedTenantIds.Contains(id))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var workspaceIds = MutationParentIds<WorkspaceMember>(nameof(WorkspaceMember.WorkspaceId))
            .Where(id => !addedWorkspaceIds.Contains(id))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var projectIds = MutationParentIds<ProjectMember>(nameof(ProjectMember.ProjectId))
            .Where(id => !addedProjectIds.Contains(id))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var groupIds = MutationParentIds<GroupMember>(nameof(GroupMember.GroupId))
            .Where(id => !addedGroupIds.Contains(id))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var taskIds = MutationParentIds<WorkItemWatchState>(nameof(WorkItemWatchState.TaskItemId))
            .Concat(MutationParentIds<WorkItemCollaborator>(nameof(WorkItemCollaborator.TaskItemId)))
            .Where(id => !addedTaskIds.Contains(id))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        return new TaskDeadlineDigestMutationPivots(
            tenantIds,
            workspaceIds,
            projectIds,
            groupIds,
            taskIds);
    }

    private HashSet<Guid> AddedEntityIds<TEntity>() where TEntity : Entity =>
        ChangeTracker.Entries<TEntity>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.Id != Guid.Empty)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

    private IEnumerable<Guid> MutationParentIds<TEntity>(string propertyName) where TEntity : class
    {
        foreach (var entry in ChangeTracker.Entries<TEntity>().Where(entry => IsMutation(entry.State)))
        {
            var property = entry.Property(propertyName);
            if (property.OriginalValue is Guid original && original != Guid.Empty)
            {
                yield return original;
            }
            if (property.CurrentValue is Guid current && current != Guid.Empty)
            {
                yield return current;
            }
        }
    }

    private sealed record TaskDeadlineDigestMutationPivots(
        Guid[] TenantIds,
        Guid[] WorkspaceIds,
        Guid[] ProjectIds,
        Guid[] GroupIds,
        Guid[] TaskIds);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplyTenantEntityConfiguration(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    private void StampAuditableEntities()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }

    /// <summary>
    /// Every persisted Project owns an explicit, fail-closed execution-source
    /// policy. This lives at the DbContext boundary so canonical creates,
    /// seeds, and any narrow direct persistence path cannot accidentally leave
    /// a Project without its default scope row. Application commands remain
    /// responsible for authorization before they alter that policy.
    /// </summary>
    private void EnsureProjectExecutionScopeDefaults()
    {
        if (ChangeTracker.Entries<ProjectExecutionScope>()
            .Any(entry => entry.State == EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Project execution scopes are persistent defaults and cannot be deleted.");
        }

        if (ChangeTracker.Entries<TaskExecutionRun>()
            .Any(entry => entry.State == EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Task execution runs are append-only foundation records and cannot be deleted.");
        }

        var trackedScopes = ChangeTracker.Entries<ProjectExecutionScope>()
            .Where(entry => entry.State is not EntityState.Detached and not EntityState.Deleted)
            .Select(entry => entry.Entity)
            .Where(scope => scope.ProjectId != Guid.Empty)
            .ToDictionary(scope => scope.ProjectId);

        foreach (var projectEntry in ChangeTracker.Entries<Project>()
                     .Where(entry => entry.State == EntityState.Added)
                     .ToArray())
        {
            var project = projectEntry.Entity;
            if (project.Id == Guid.Empty)
            {
                throw new InvalidOperationException("New Projects require an identifier before execution policy initialization.");
            }

            var scope = project.ExecutionScope;
            if (scope is null && trackedScopes.TryGetValue(project.Id, out var trackedScope))
            {
                scope = trackedScope;
                project.ExecutionScope = scope;
            }

            if (scope is null)
            {
                var actor = project.CreatedByUserId != Guid.Empty
                    ? project.CreatedByUserId
                    : project.OwnerUserId;
                if (actor == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "New Projects require a creator or owner before execution policy initialization.");
                }

                scope = new ProjectExecutionScope
                {
                    TenantId = project.TenantId,
                    WorkspaceId = project.WorkspaceId,
                    ProjectId = project.Id,
                    WebEnabled = false,
                    ProjectFilesEnabled = false,
                    VersionNo = 1,
                    UpdatedByUserId = actor,
                    Project = project
                };
                project.ExecutionScope = scope;
                ProjectExecutionScopes.Add(scope);
                trackedScopes.Add(project.Id, scope);
                continue;
            }

            if (scope.ProjectId == Guid.Empty)
            {
                scope.ProjectId = project.Id;
            }

            if (scope.ProjectId != project.Id ||
                (scope.TenantId != Guid.Empty && scope.TenantId != project.TenantId) ||
                (scope.WorkspaceId != Guid.Empty && scope.WorkspaceId != project.WorkspaceId))
            {
                throw new InvalidOperationException("Project execution scope must match its Project tenant, Workspace, and identifier.");
            }

            scope.TenantId = project.TenantId;
            scope.WorkspaceId = project.WorkspaceId;
            if (scope.UpdatedByUserId == Guid.Empty)
            {
                scope.UpdatedByUserId = project.CreatedByUserId != Guid.Empty
                    ? project.CreatedByUserId
                    : project.OwnerUserId;
            }

            if (scope.UpdatedByUserId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "Project execution scopes require a creator or owner as their updater.");
            }
        }
    }

    /// <summary>
    /// Research Plan revisions are historical snapshots. Plan edits append a
    /// new revision and move the aggregate pointer; they never alter or remove
    /// a prior revision or step in the normal persistence path.
    /// </summary>
    private void EnsureResearchPlanSnapshotImmutability()
    {
        if (ChangeTracker.Entries<ResearchPlanRevision>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Research Plan revisions are immutable and cannot be changed or deleted.");
        }

        if (ChangeTracker.Entries<ResearchPlanStep>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Research Plan steps are immutable and cannot be changed or deleted.");
        }

        if (ChangeTracker.Entries<ResearchPlan>()
            .Any(entry => entry.State == EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Research Plans are historical Task records and cannot be deleted.");
        }
    }

    /// <summary>
    /// Lifecycle fields on an accepted run may change as server work proceeds,
    /// but its server-owned provenance and source/plan snapshots are fixed at
    /// acceptance. This keeps a later dispatcher or worker from rewriting the
    /// execution-start authority.
    /// </summary>
    private void EnsureTaskExecutionRunSnapshotImmutability()
    {
        if (ChangeTracker.Entries<TaskExecutionRun>()
            .Where(entry => entry.State == EntityState.Modified)
            .Any(entry => entry.Properties.Any(property =>
                property.IsModified && ImmutableTaskExecutionRunSnapshotProperties.Contains(property.Metadata.Name))))
        {
            throw new InvalidOperationException(
                "Task execution run snapshots are immutable after acceptance.");
        }
    }

    private bool ApplyTenantRules()
    {
        var hasNormalTenantWrite = false;

        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged)
            {
                continue;
            }

            if (currentTenant.IsPlatformScope)
            {
                if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
                {
                    throw new InvalidOperationException("Platform-scope tenant entities must set TenantId explicitly.");
                }

                continue;
            }

            hasNormalTenantWrite = true;

            if (!currentTenant.IsAvailable)
            {
                throw new InvalidOperationException("A tenant scope is required to save tenant-owned data.");
            }

            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = currentTenant.TenantId;
                continue;
            }

            if (entry.Entity.TenantId != currentTenant.TenantId)
            {
                throw new InvalidOperationException("TenantId does not match the current tenant context.");
            }
        }

        return hasNormalTenantWrite;
    }

    /// <summary>
    /// Temporary compatibility for legacy operational graphs that are inserted
    /// directly with no canonical Visibility classification. Canonical WPC create
    /// always writes a classified Planning Project, so this path cannot provision
    /// workflow state for canonical Draft creation and is never used by activation.
    /// </summary>
    private void EnsureLegacyUnclassifiedOperationalTaskWorkflowCompatibility()
    {
        foreach (var project in ChangeTracker.Entries<Project>()
                     .Where(entry =>
                         entry.State == EntityState.Added &&
                         entry.Entity.Visibility is null &&
                         entry.Entity.Status is ProjectStatus.Active or ProjectStatus.Review or ProjectStatus.Completed)
                     .Select(entry => entry.Entity)
                     .ToList())
        {
            if (ChangeTracker.Entries<TaskWorkflowDefinition>().Any(entry =>
                    entry.State != EntityState.Deleted && entry.Entity.ProjectId == project.Id))
            {
                continue;
            }

            var definition = new TaskWorkflowDefinition
            {
                TenantId = project.TenantId,
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Name = "Default",
                ReviewEnforcementEnabled = true
            };
            TaskWorkflowDefinitions.Add(definition);

            var stages = new (string Name, TaskStageCategory Category, bool Initial, bool Terminal)[]
            {
                ("Backlog", TaskStageCategory.Backlog, true, false),
                ("Todo", TaskStageCategory.Todo, false, false),
                ("In Progress", TaskStageCategory.InProgress, false, false),
                ("Review", TaskStageCategory.Review, false, false),
                ("Done", TaskStageCategory.Done, false, true),
                ("Cancelled", TaskStageCategory.Cancelled, false, true)
            };

            for (var index = 0; index < stages.Length; index++)
            {
                var stage = stages[index];
                TaskWorkflowStages.Add(new TaskWorkflowStage
                {
                    TenantId = project.TenantId,
                    WorkspaceId = project.WorkspaceId,
                    ProjectId = project.Id,
                    DefinitionId = definition.Id,
                    Name = stage.Name,
                    InternalCategory = stage.Category,
                    SortKey = (index + 1) * 1000L,
                    IsInitialStage = stage.Initial,
                    IsTerminalStage = stage.Terminal
                });
            }
        }
    }

    private void IncrementTaskAggregateVersions()
    {
        foreach (var entry in ChangeTracker.Entries<TaskItem>())
        {
            if (entry.State == EntityState.Added && entry.Entity.VersionNo <= 0)
            {
                entry.Entity.VersionNo = 1;
            }
            else if (entry.State == EntityState.Modified && !entry.Property(task => task.VersionNo).IsModified)
            {
                entry.Entity.VersionNo = entry.OriginalValues.GetValue<long>(nameof(TaskItem.VersionNo)) + 1;
            }
        }
    }

    private void IncrementProjectAggregateVersions()
    {
        foreach (var entry in ChangeTracker.Entries<Project>())
        {
            if (entry.State == EntityState.Added && entry.Entity.VersionNo <= 0)
            {
                entry.Entity.VersionNo = 1;
            }
            else if (entry.State == EntityState.Modified &&
                     !entry.Property(project => project.VersionNo).IsModified)
            {
                entry.Entity.VersionNo =
                    entry.OriginalValues.GetValue<long>(nameof(Project.VersionNo)) + 1;
            }
        }
    }

    private async Task EnsureCurrentTenantCanWriteAsync(CancellationToken cancellationToken)
    {
        var isActive = await Tenants
            .AsNoTracking()
            .AnyAsync(tenant => tenant.Id == currentTenant.TenantId && tenant.Status == TenantStatus.Active, cancellationToken);

        if (!isActive)
        {
            throw new InvalidOperationException("Current tenant is not active and cannot save tenant-owned data.");
        }
    }

    private void EnsureCurrentTenantCanWrite()
    {
        var isActive = Tenants
            .AsNoTracking()
            .Any(tenant => tenant.Id == currentTenant.TenantId && tenant.Status == TenantStatus.Active);

        if (!isActive)
        {
            throw new InvalidOperationException("Current tenant is not active and cannot save tenant-owned data.");
        }
    }

    private void ApplyTenantEntityConfiguration(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(entityType => typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType)))
        {
            ConfigureTenantEntity(modelBuilder, entityType);
        }
    }

    private void ConfigureTenantEntity(ModelBuilder modelBuilder, IMutableEntityType entityType)
    {
        var builder = modelBuilder.Entity(entityType.ClrType);
        builder.Property<Guid>(nameof(ITenantEntity.TenantId)).IsRequired();
        builder.HasIndex(nameof(ITenantEntity.TenantId));

        var method = typeof(AppDbContext)
            .GetMethod(nameof(ApplyTenantQueryFilter), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(entityType.ClrType);
        method.Invoke(this, [modelBuilder]);
    }

    private void ApplyTenantQueryFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantEntity
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity =>
            currentTenant.IsPlatformScope ||
            (currentTenant.IsAvailable && entity.TenantId == currentTenant.TenantId));
    }
}
