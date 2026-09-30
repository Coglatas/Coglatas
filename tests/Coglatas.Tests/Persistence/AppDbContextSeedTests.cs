using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Persistence;

public sealed class AppDbContextSeedTests
{
    [Fact]
    public async Task DefaultTenantAndPlansAreIdempotent()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var options = new TenancyOptions { DefaultTenantSlug = "default" };

        var firstTenant = await AppDbContextSeed.SeedDefaultTenantAsync(dbContext, options);
        await AppDbContextSeed.SeedPlansAsync(dbContext);
        var secondTenant = await AppDbContextSeed.SeedDefaultTenantAsync(dbContext, options);
        await AppDbContextSeed.SeedPlansAsync(dbContext);

        Assert.Equal(firstTenant.Id, secondTenant.Id);
        Assert.Equal(1, await dbContext.Tenants.CountAsync());
        Assert.Equal(4, await dbContext.Plans.CountAsync());
        Assert.Equal(4, await dbContext.Plans.Select(plan => plan.Name).Distinct().CountAsync());
    }

    [Fact]
    public async Task LocalAdminSeedIsIdempotentAndUpdatesExistingAdmin()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = await AppDbContextSeed.SeedDefaultTenantAsync(dbContext, new TenancyOptions { DefaultTenantSlug = "default" });
        var passwordHasher = new Pbkdf2PasswordHasher();

        await AppDbContextSeed.SeedLocalAdminAsync(
            dbContext,
            passwordHasher,
            tenant.Id,
            "admin@example.com",
            "first-local-password",
            "Local Admin");

        await AppDbContextSeed.SeedLocalAdminAsync(
            dbContext,
            passwordHasher,
            tenant.Id,
            "admin@example.com",
            "second-local-password",
            "Updated Admin");

        var user = await dbContext.Users.SingleAsync();
        var passwordHashAfterPasswordChange = user.PasswordHash;
        var tenantUser = await dbContext.TenantUsers.SingleAsync();
        var workspace = await dbContext.Workspaces.SingleAsync();
        var workspaceMember = await dbContext.WorkspaceMembers.SingleAsync();

        Assert.Equal("Updated Admin", user.DisplayName);
        Assert.True(passwordHasher.VerifyPassword(user.PasswordHash, "second-local-password"));
        Assert.Equal(SystemRole.SystemAdmin, user.SystemRole);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(tenant.Id, tenantUser.TenantId);
        Assert.Equal(user.Id, tenantUser.UserId);
        Assert.Equal(TenantUserRole.Owner, tenantUser.Role);
        Assert.Equal(TenantUserStatus.Active, tenantUser.Status);
        Assert.Equal(tenant.Id, workspace.TenantId);
        Assert.Equal("default-workspace", workspace.Slug);
        Assert.Equal(WorkspaceStatus.Active, workspace.Status);
        Assert.Equal(tenant.Id, workspaceMember.TenantId);
        Assert.Equal(workspace.Id, workspaceMember.WorkspaceId);
        Assert.Equal(user.Id, workspaceMember.UserId);
        Assert.Equal(WorkspaceRole.Owner, workspaceMember.Role);
        Assert.Equal(MembershipStatus.Active, workspaceMember.Status);

        await AppDbContextSeed.SeedLocalAdminAsync(
            dbContext,
            passwordHasher,
            tenant.Id,
            "admin@example.com",
            "second-local-password",
            "Updated Admin");

        Assert.Equal(passwordHashAfterPasswordChange, user.PasswordHash);
    }

    [Fact]
    public async Task LocalAdminSeedPromotesExistingUserAndReusesTenantMembership()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = await AppDbContextSeed.SeedDefaultTenantAsync(dbContext, new TenancyOptions { DefaultTenantSlug = "default" });
        var passwordHasher = new Pbkdf2PasswordHasher();

        var existingUser = new User
        {
            DisplayName = "Existing User",
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            PasswordHash = passwordHasher.HashPassword("old-password"),
            SystemRole = SystemRole.User,
            Status = UserStatus.Suspended,
            FailedLoginAttempts = 3,
            LockoutEndAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow
        };
        existingUser.MarkDeleted(DateTimeOffset.UtcNow);

        await dbContext.Users.AddAsync(existingUser);
        await dbContext.TenantUsers.AddAsync(new TenantUser
        {
            TenantId = tenant.Id,
            UserId = existingUser.Id,
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Invited,
            JoinedAt = default,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        await AppDbContextSeed.SeedLocalAdminAsync(
            dbContext,
            passwordHasher,
            tenant.Id,
            "admin@example.com",
            "new-password",
            "Seeded Admin");

        var user = await dbContext.Users.SingleAsync();
        var tenantUser = await dbContext.TenantUsers.SingleAsync();
        var workspaceMember = await dbContext.WorkspaceMembers.SingleAsync();

        Assert.Equal(existingUser.Id, user.Id);
        Assert.Equal("Seeded Admin", user.DisplayName);
        Assert.True(passwordHasher.VerifyPassword(user.PasswordHash, "new-password"));
        Assert.Equal(SystemRole.SystemAdmin, user.SystemRole);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockoutEndAt);
        Assert.False(user.IsDeleted);
        Assert.Equal(TenantUserRole.Owner, tenantUser.Role);
        Assert.Equal(TenantUserStatus.Active, tenantUser.Status);
        Assert.NotEqual(default, tenantUser.JoinedAt);
        Assert.Equal(user.Id, workspaceMember.UserId);
        Assert.Equal(WorkspaceRole.Owner, workspaceMember.Role);
        Assert.Equal(MembershipStatus.Active, workspaceMember.Status);
    }

    [Fact]
    public async Task BootstrapAdminPromotesExistingUserWithoutChangingPassword()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = await AppDbContextSeed.SeedDefaultTenantAsync(dbContext, new TenancyOptions { DefaultTenantSlug = "default" });
        var passwordHasher = new Pbkdf2PasswordHasher();
        var existingHash = passwordHasher.HashPassword("existing-password");

        await dbContext.Users.AddAsync(new User
        {
            DisplayName = "Existing User",
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            PasswordHash = existingHash,
            SystemRole = SystemRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        await AppDbContextSeed.EnsureBootstrapAdminAsync(
            dbContext,
            passwordHasher,
            tenant.Id,
            "admin@example.com");

        var user = await dbContext.Users.SingleAsync();
        var tenantUser = await dbContext.TenantUsers.SingleAsync();
        var workspaceMember = await dbContext.WorkspaceMembers.SingleAsync();

        Assert.Equal(existingHash, user.PasswordHash);
        Assert.Equal(SystemRole.SystemAdmin, user.SystemRole);
        Assert.Equal(TenantUserRole.Owner, tenantUser.Role);
        Assert.Equal(WorkspaceRole.Owner, workspaceMember.Role);
    }

    [Fact]
    public async Task BrowserSmokeSeedDelegatesWorkspaceCreateToTenantMemberIdempotently()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = await AppDbContextSeed.SeedDefaultTenantAsync(
            dbContext,
            new TenancyOptions { DefaultTenantSlug = "default" });
        var passwordHasher = new Pbkdf2PasswordHasher();
        var storage = new MemoryFileStorage();

        await AppDbContextSeed.SeedBrowserSmokeAsync(
            dbContext,
            passwordHasher,
            storage,
            tenant.Id,
            "browser-smoke@example.test",
            "Browser-smoke-password!234");

        var actor = await dbContext.Users.SingleAsync(
            user => user.Email == "browser-smoke@example.test");
        var recipient = await dbContext.Users.SingleAsync(
            user => user.Email == "browser-smoke-recipient@example.test");
        var smokeAnnouncement = await dbContext.Announcements.SingleAsync(
            announcement =>
                announcement.TenantId == tenant.Id &&
                announcement.Title == "Browser smoke announcement");
        var firstGrant = await dbContext.Set<CapabilityGrant>().SingleAsync(
            grant =>
                grant.TenantId == tenant.Id &&
                grant.SubjectUserId == actor.Id &&
                grant.CapabilityKey == CapabilityKeys.WorkspaceCreate);
        var firstGrantId = firstGrant.Id;
        var firstGrantedAt = firstGrant.GrantedAt;
        var firstTaskArtifact = await dbContext.Artifacts.SingleAsync(
            artifact => artifact.Name == "Browser Smoke Task Artifact");
        var firstTaskArtifactId = firstTaskArtifact.Id;
        var seededTask = await dbContext.TaskItems.SingleAsync(
            task => task.Title == "Browser smoke task");
        seededTask.IsBlocked = true;
        seededTask.BlockedReason = "Stale state from a prior browser run.";
        dbContext.AnnouncementReads.AddRange(
            new AnnouncementRead
            {
                TenantId = tenant.Id,
                AnnouncementId = smokeAnnouncement.Id,
                UserId = actor.Id,
                ReadAt = DateTimeOffset.UtcNow
            },
            new AnnouncementRead
            {
                TenantId = tenant.Id,
                AnnouncementId = smokeAnnouncement.Id,
                UserId = recipient.Id,
                ReadAt = DateTimeOffset.UtcNow
            });
        await dbContext.SaveChangesAsync();

        await AppDbContextSeed.SeedBrowserSmokeAsync(
            dbContext,
            passwordHasher,
            storage,
            tenant.Id,
            "browser-smoke@example.test",
            "Browser-smoke-password!234");

        var tenantMembership = await dbContext.TenantUsers.SingleAsync(
            membership => membership.TenantId == tenant.Id && membership.UserId == actor.Id);
        var grant = Assert.Single(await dbContext.Set<CapabilityGrant>()
            .Where(candidate =>
                candidate.TenantId == tenant.Id &&
                candidate.SubjectUserId == actor.Id &&
                candidate.CapabilityKey == CapabilityKeys.WorkspaceCreate)
            .ToListAsync());

        Assert.Equal(SystemRole.User, actor.SystemRole);
        Assert.Equal(TenantUserRole.Member, tenantMembership.Role);
        Assert.Equal(TenantUserStatus.Active, tenantMembership.Status);
        Assert.Equal(firstGrantId, grant.Id);
        Assert.Equal(firstGrantedAt, grant.GrantedAt);
        Assert.Equal(CapabilityScopeType.Tenant, grant.ScopeType);
        Assert.Equal(tenant.Id, grant.ScopeId);
        Assert.Equal(actor.Id, grant.GrantedByUserId);
        Assert.Null(grant.ExpiresAt);
        Assert.Null(grant.RevokedAt);

        var taskArtifact = Assert.Single(await dbContext.Artifacts
            .Where(artifact => artifact.Name == "Browser Smoke Task Artifact")
            .ToListAsync());
        Assert.Equal(firstTaskArtifactId, taskArtifact.Id);
        Assert.Equal(tenant.Id, taskArtifact.TenantId);
        Assert.NotNull(taskArtifact.TaskItemId);
        Assert.Equal(ArtifactType.Document, taskArtifact.ArtifactType);
        Assert.Equal(ArtifactStatus.Approved, taskArtifact.Status);
        Assert.False(taskArtifact.IsDeleted);
        var artifactTask = await dbContext.TaskItems.SingleAsync(task => task.Id == taskArtifact.TaskItemId);
        Assert.Equal(taskArtifact.ProjectId, artifactTask.ProjectId);
        Assert.False(artifactTask.IsBlocked);
        Assert.Null(artifactTask.BlockedReason);

        var reseededAnnouncement = await dbContext.Announcements.SingleAsync(
            announcement => announcement.Id == smokeAnnouncement.Id);
        Assert.True(reseededAnnouncement.RequiresReadConfirmation);
        var remainingAnnouncementReads = await dbContext.AnnouncementReads
            .Where(read =>
                read.TenantId == tenant.Id &&
                read.AnnouncementId == smokeAnnouncement.Id)
            .ToListAsync();
        Assert.DoesNotContain(remainingAnnouncementReads, read => read.UserId == actor.Id);
        Assert.Contains(remainingAnnouncementReads, read => read.UserId == recipient.Id);

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var tenants = new TenantRepository(dbContext);
        var workspaces = new WorkspaceRepository(dbContext);
        var evaluator = new CapabilityGrantEvaluator(
            new CapabilityGrantRepository(dbContext),
            tenants,
            workspaces,
            currentTenant,
            new SystemClock());
        var authorization = new WorkspaceAuthorizationService(
            new UserRepository(dbContext),
            workspaces,
            new TenantAuthorizationService(tenants),
            evaluator);

        Assert.True(await authorization.CanCreateWorkspace(actor.Id, tenant.Id));
    }

    [Fact]
    public async Task BrowserSmokeSeedProvidesIdempotentU22SyntheticDemoDataWithoutAnExecutionRun()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = await AppDbContextSeed.SeedDefaultTenantAsync(
            dbContext,
            new TenancyOptions { DefaultTenantSlug = "default" });
        var passwordHasher = new Pbkdf2PasswordHasher();
        var storage = new MemoryFileStorage();

        await AppDbContextSeed.SeedBrowserSmokeAsync(
            dbContext,
            passwordHasher,
            storage,
            tenant.Id,
            "browser-smoke@example.test",
            "Browser-smoke-password!234");
        await AppDbContextSeed.SeedBrowserSmokeAsync(
            dbContext,
            passwordHasher,
            storage,
            tenant.Id,
            "browser-smoke@example.test",
            "Browser-smoke-password!234");

        var project = await dbContext.Projects.SingleAsync(candidate =>
            candidate.TenantId == tenant.Id &&
            candidate.Slug == "u22-synthetic-demo-project");
        Assert.Equal("U-22 Synthetic Demo Project", project.Name);
        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Equal(ProjectVisibility.WorkspaceVisible, project.Visibility);
        Assert.Equal(ProjectActivationState.Activated, project.ActivationState);
        Assert.Equal(1, project.ActivationVersion);

        var projectScope = Assert.Single(await dbContext.ProjectExecutionScopes
            .Where(candidate => candidate.ProjectId == project.Id)
            .ToListAsync());
        Assert.False(projectScope.WebEnabled);
        Assert.True(projectScope.ProjectFilesEnabled);
        Assert.Equal(1, projectScope.VersionNo);

        var task = await dbContext.TaskItems.SingleAsync(candidate =>
            candidate.TenantId == tenant.Id &&
            candidate.ProjectId == project.Id &&
            candidate.Title == "U-22 Synthetic Demo Task");
        Assert.Equal("Demonstrate a secure, repeatable U-22 Task workflow.", task.BriefGoal);
        Assert.Equal(
            "A concise U-22 walkthrough showing the Task Brief, source policy, and current Task state.",
            task.BriefDeliverable);
        Assert.Equal(
            "Synthetic Test fixture only. No outbound Web retrieval, provider, runtime, raw source content, or execution claim.",
            task.BriefConstraints);
        Assert.Equal(TaskItemStatus.InProgress, task.Status);
        Assert.False(task.IsBlocked);
        Assert.Equal(0, task.ProgressPercent);
        Assert.NotNull(task.WorkflowStageId);

        var stage = await dbContext.TaskWorkflowStages.SingleAsync(
            candidate => candidate.Id == task.WorkflowStageId!.Value);
        Assert.Equal("In progress", stage.Name);
        Assert.Equal(TaskStageCategory.InProgress, stage.InternalCategory);

        var taskOverride = Assert.Single(await dbContext.TaskExecutionScopeOverrides
            .Where(candidate => candidate.TaskItemId == task.Id)
            .ToListAsync());
        Assert.True(taskOverride.WebEnabled);
        Assert.False(taskOverride.ProjectFilesEnabled);
        Assert.Equal(1, taskOverride.VersionNo);

        var activity = Assert.Single(await dbContext.ActivityLogs
            .Where(candidate => candidate.TaskItemId == task.Id)
            .ToListAsync());
        Assert.Equal(ActivityLogType.Note, activity.ActivityType);
        Assert.Equal(
            "Synthetic U-22 demo note. This seeded Activity record is presentation data only; it is not execution or phase-transition history.",
            activity.Body);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero), activity.OccurredAt);

        Assert.Empty(await dbContext.TaskExecutionRuns
            .Where(candidate => candidate.TaskItemId == task.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task DemoDatasetSeedCreatesAnIdempotentSyntheticExecutionReadyFixture()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = await AppDbContextSeed.SeedDefaultTenantAsync(
            dbContext,
            new TenancyOptions { DefaultTenantSlug = "default" });
        var passwordHasher = new Pbkdf2PasswordHasher();
        var storage = new MemoryFileStorage();
        var password = $"test-only-{Guid.NewGuid():N}";

        await DemoDatasetSeed.SeedAsync(
            dbContext,
            passwordHasher,
            storage,
            tenant.Id,
            DemoDatasetSeed.OwnerEmail,
            password);
        await DemoDatasetSeed.SeedAsync(
            dbContext,
            passwordHasher,
            storage,
            tenant.Id,
            DemoDatasetSeed.OwnerEmail,
            password);

        var owner = await dbContext.Users.SingleAsync(user => user.Email == DemoDatasetSeed.OwnerEmail);
        var observer = await dbContext.Users.SingleAsync(user => user.Email == DemoDatasetSeed.ObserverEmail);
        var workspace = await dbContext.Workspaces.SingleAsync(workspace =>
            workspace.TenantId == tenant.Id && workspace.Slug == DemoDatasetSeed.WorkspaceSlug);
        var project = await dbContext.Projects.SingleAsync(project =>
            project.TenantId == tenant.Id && project.Slug == DemoDatasetSeed.ProjectSlug);
        var scope = await dbContext.ProjectExecutionScopes.SingleAsync(scope => scope.ProjectId == project.Id);
        var executionTask = await dbContext.TaskItems.SingleAsync(task =>
            task.ProjectId == project.Id && task.Title == DemoDatasetSeed.ExecutionTaskTitle);

        Assert.Equal(WorkspaceRole.Owner, (await dbContext.WorkspaceMembers.SingleAsync(member =>
            member.WorkspaceId == workspace.Id && member.UserId == owner.Id)).Role);
        Assert.Equal(WorkspaceRole.ReadOnly, (await dbContext.WorkspaceMembers.SingleAsync(member =>
            member.WorkspaceId == workspace.Id && member.UserId == observer.Id)).Role);
        Assert.False(await dbContext.ProjectMembers.AnyAsync(member =>
            member.ProjectId == project.Id && member.UserId == observer.Id));

        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Equal(ProjectVisibility.MembersOnly, project.Visibility);
        Assert.Equal(ProjectActivationState.Activated, project.ActivationState);
        Assert.False(scope.WebEnabled);
        Assert.True(scope.ProjectFilesEnabled);
        Assert.Equal(TaskItemStatus.InProgress, executionTask.Status);
        Assert.Equal("Demonstrate an authorized, reproducible Task execution path.", executionTask.BriefGoal);
        Assert.Contains(await dbContext.TaskItems.Where(task => task.ProjectId == project.Id).Select(task => task.Status).ToListAsync(),
            status => status == TaskItemStatus.WaitingReview);
        Assert.Contains(await dbContext.TaskItems.Where(task => task.ProjectId == project.Id).Select(task => task.Status).ToListAsync(),
            status => status == TaskItemStatus.Completed);

        var plan = await dbContext.ResearchPlans.SingleAsync(plan => plan.TaskItemId == executionTask.Id);
        Assert.NotNull(plan.CurrentRevisionId);
        Assert.Single(await dbContext.ResearchPlanSteps
            .Where(step => step.ResearchPlanId == plan.Id)
            .ToListAsync());

        var attachment = await dbContext.Attachments.SingleAsync(candidate =>
            candidate.OwnerType == AttachmentOwnerType.TaskItem && candidate.OwnerId == executionTask.Id);
        Assert.Equal(FileScanStatus.Clean, attachment.ScanStatus);
        var source = await dbContext.FileObjects.SingleAsync(candidate => candidate.Id == attachment.FileObjectId);
        Assert.Equal("text/plain", source.ContentType);
        Assert.Equal(FileObjectStatus.Active, source.Status);

        Assert.Single(await dbContext.Conversations.Where(conversation =>
            conversation.WorkspaceId == workspace.Id && conversation.Title == "Issue #483 Demo Conversation").ToListAsync());
        Assert.Single(await dbContext.Messages.Where(message =>
            message.Body == "[issue-483-demo] Synthetic conversation message for the demo.").ToListAsync());
        Assert.Single(await dbContext.Announcements.Where(announcement =>
            announcement.WorkspaceId == workspace.Id && announcement.Title == "Issue #483 Demo: published announcement").ToListAsync());
        Assert.Single(await dbContext.AnnouncementDrafts.Where(draft =>
            draft.WorkspaceId == workspace.Id && draft.Status == AnnouncementDraftStatus.Draft).ToListAsync());
        Assert.Single(await dbContext.AnnouncementDrafts.Where(draft =>
            draft.WorkspaceId == workspace.Id && draft.Status == AnnouncementDraftStatus.Scheduled).ToListAsync());
        Assert.Single(await dbContext.AuditLogs.Where(log =>
            log.ProjectId == project.Id && log.Action == "DemoDatasetProvisioned").ToListAsync());
    }

    private static AppDbContext CreateDbContext(CurrentTenantService currentTenant)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options, currentTenant);
    }

    private sealed class MemoryFileStorage : IFileStorageService
    {
        public async Task<Result> SaveAsync(
            string storageKey,
            Stream stream,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            await stream.CopyToAsync(Stream.Null, cancellationToken);
            return Result.Success();
        }

        public Task<Stream> OpenReadAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Stream.Null);

        public Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<string?> CreateSignedReadUrlAsync(
            string storageKey,
            TimeSpan expiresIn,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
