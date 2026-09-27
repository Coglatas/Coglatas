using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Coglatas.Infrastructure.Persistence;

public static class AppDbContextSeed
{
    private static readonly Guid DefaultTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // These values belong only to the explicitly opted-in Test-environment
    // browser-smoke fixture. They are deliberately synthetic and are not a
    // production seed or an execution-history source.
    private const string U22DemoProjectSlug = "u22-synthetic-demo-project";
    private const string U22DemoProjectName = "U-22 Synthetic Demo Project";
    private const string U22DemoTaskTitle = "U-22 Synthetic Demo Task";
    private const string U22DemoTaskGoal = "Demonstrate a secure, repeatable U-22 Task workflow.";
    private const string U22DemoTaskDeliverable = "A concise U-22 walkthrough showing the Task Brief, source policy, and current Task state.";
    private const string U22DemoTaskConstraints = "Synthetic Test fixture only. No outbound Web retrieval, provider, runtime, raw source content, or execution claim.";
    private const string U22DemoActivityBody = "Synthetic U-22 demo note. This seeded Activity record is presentation data only; it is not execution or phase-transition history.";
    // Keep this safely in the past throughout the submission window so the
    // presentation fixture never appears to invent a future Activity event.
    private static readonly DateTimeOffset U22DemoActivityOccurredAt = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    public static async Task<Tenant> SeedDefaultTenantAsync(
        AppDbContext dbContext,
        TenancyOptions options,
        CancellationToken cancellationToken = default)
    {
        var slug = string.IsNullOrWhiteSpace(options.DefaultTenantSlug)
            ? "default"
            : options.DefaultTenantSlug.Trim().ToLowerInvariant();

        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(candidate => candidate.Slug == slug, cancellationToken);
        if (tenant is not null)
        {
            return tenant;
        }

        tenant = await dbContext.Tenants.FirstOrDefaultAsync(cancellationToken);
        if (tenant is not null)
        {
            return tenant;
        }

        tenant = new Tenant(DefaultTenantId)
        {
            Name = "Default Tenant",
            Slug = slug,
            DisplayName = "Default Tenant",
            Status = TenantStatus.Active
        };

        await dbContext.Tenants.AddAsync(tenant, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return tenant;
    }

    public static async Task SeedUiShellAsync(AppDbContext dbContext, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await SeedModulesAsync(dbContext, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var modules = await dbContext.FeatureModules.ToDictionaryAsync(module => module.Key, cancellationToken);
        await SeedPanelsAsync(dbContext, modules, now, cancellationToken);
        await SeedCommandsAsync(dbContext, modules, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var commands = await dbContext.CommandDefinitions.ToDictionaryAsync(command => command.Key, cancellationToken);
        await SeedRadialMenusAsync(dbContext, commands, tenantId, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public static async Task SeedPlansAsync(AppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var features = "[\"ProductionTracking\",\"AdvancedGanttChart\",\"ExternalGuestAccess\",\"FileSharing\",\"Calendar\",\"Attendance\",\"Forms\",\"WebhookIntegration\",\"ApiAccess\",\"CustomBranding\",\"AuditLogViewer\",\"RadialMenu\",\"DockingLayout\"]";
        var plans = new (string Name, string Description, int Users, long Storage, int Projects, PlanStatus Status)[]
        {
            ("InternalPilot", "Internal pilot and development plan.", 100, 10L * 1024 * 1024 * 1024, 100, PlanStatus.InternalOnly),
            ("SchoolPilot", "Small school pilot plan.", 150, 25L * 1024 * 1024 * 1024, 150, PlanStatus.Active),
            ("Standard", "Standard SaaS plan foundation.", 500, 100L * 1024 * 1024 * 1024, 500, PlanStatus.Active),
            ("Enterprise", "Enterprise and on-prem configuration plan.", 5000, 1024L * 1024 * 1024 * 1024, 5000, PlanStatus.Active)
        };

        var existing = await dbContext.Plans.Select(plan => plan.Name).ToListAsync(cancellationToken);
        foreach (var plan in plans.Where(plan => !existing.Contains(plan.Name)))
        {
            await dbContext.Plans.AddAsync(new Plan
            {
                Name = plan.Name,
                Description = plan.Description,
                MaxUsers = plan.Users,
                MaxStorageBytes = plan.Storage,
                MaxProjects = plan.Projects,
                EnabledFeaturesJson = features,
                Status = plan.Status,
                CreatedAt = now
            }, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public static async Task SeedLocalAdminAsync(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        Guid tenantId,
        string email,
        string password,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        await EnsureBootstrapAdminAsync(
            dbContext,
            passwordHasher,
            tenantId,
            email,
            password,
            displayName,
            ensureDefaultWorkspace: true,
            cancellationToken);
    }

    public static async Task SeedBrowserSmokeAsync(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        IFileStorageService fileStorage,
        Guid tenantId,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        const string workspaceSlug = "browser-smoke-workspace";
        const string projectSlug = "browser-smoke-project";
        const string announcementTitle = "Browser smoke announcement";
        const string taskTitle = "Browser smoke task";
        const string recipientEmail = "browser-smoke-recipient@example.test";
        const string recipientDisplayName = "Browser Smoke Recipient";
        const string taskLabelName = "Browser smoke label";
        const string taskFileName = "browser-smoke-task.txt";
        const string taskFileContents = "Synthetic PR03C browser smoke file.\n";
        const string pr05ManagerEmail = "browser-smoke-pr05-manager@example.test";
        var taskFileBytes = Encoding.UTF8.GetBytes(taskFileContents);

        var now = DateTimeOffset.UtcNow;
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, cancellationToken);
        if (user is null)
        {
            user = new User
            {
                DisplayName = "Automated Browser Smoke User",
                Email = email.Trim(),
                NormalizedEmail = normalizedEmail,
                PasswordHash = passwordHasher.HashPassword(password),
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            };
            await dbContext.Users.AddAsync(user, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            user.DisplayName = "Automated Browser Smoke User";
            user.Email = email.Trim();
            user.NormalizedEmail = normalizedEmail;
            user.SystemRole = SystemRole.User;
            user.Status = UserStatus.Active;
            user.FailedLoginAttempts = 0;
            user.LockoutEndAt = null;
            if (user.IsDeleted)
            {
                user.Restore();
            }

            if (!passwordHasher.VerifyPassword(user.PasswordHash, password))
            {
                user.PasswordHash = passwordHasher.HashPassword(password);
            }
        }

        var normalizedRecipientEmail = recipientEmail.ToUpperInvariant();
        var recipient = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedRecipientEmail, cancellationToken);
        if (recipient is null)
        {
            recipient = new User
            {
                DisplayName = recipientDisplayName,
                Email = recipientEmail,
                NormalizedEmail = normalizedRecipientEmail,
                PasswordHash = passwordHasher.HashPassword($"{password}:recipient"),
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            };
            await dbContext.Users.AddAsync(recipient, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            recipient.DisplayName = recipientDisplayName;
            recipient.Email = recipientEmail;
            recipient.NormalizedEmail = normalizedRecipientEmail;
            recipient.SystemRole = SystemRole.User;
            recipient.Status = UserStatus.Active;
            recipient.FailedLoginAttempts = 0;
            recipient.LockoutEndAt = null;
            if (recipient.IsDeleted)
            {
                recipient.Restore();
            }
        }

        var normalizedPr05ManagerEmail = pr05ManagerEmail.ToUpperInvariant();
        var pr05Manager = await dbContext.Users.FirstOrDefaultAsync(
            candidate => candidate.NormalizedEmail == normalizedPr05ManagerEmail,
            cancellationToken);
        if (pr05Manager is null)
        {
            pr05Manager = new User
            {
                DisplayName = "PR05 Browser Manager",
                Email = pr05ManagerEmail,
                NormalizedEmail = normalizedPr05ManagerEmail,
                PasswordHash = passwordHasher.HashPassword(password),
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            };
            await dbContext.Users.AddAsync(pr05Manager, cancellationToken);
        }
        else
        {
            pr05Manager.DisplayName = "PR05 Browser Manager";
            pr05Manager.Email = pr05ManagerEmail;
            pr05Manager.NormalizedEmail = normalizedPr05ManagerEmail;
            pr05Manager.SystemRole = SystemRole.User;
            pr05Manager.Status = UserStatus.Active;
            pr05Manager.FailedLoginAttempts = 0;
            pr05Manager.LockoutEndAt = null;
            if (pr05Manager.IsDeleted)
            {
                pr05Manager.Restore();
            }

            if (!passwordHasher.VerifyPassword(pr05Manager.PasswordHash, password))
            {
                pr05Manager.PasswordHash = passwordHasher.HashPassword(password);
            }
        }

        var tenantUser = await dbContext.TenantUsers
            .FirstOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.UserId == user.Id, cancellationToken);
        if (tenantUser is null)
        {
            tenantUser = new TenantUser
            {
                TenantId = tenantId,
                UserId = user.Id,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = now
            };
            await dbContext.TenantUsers.AddAsync(tenantUser, cancellationToken);
        }
        else
        {
            tenantUser.Role = TenantUserRole.Member;
            tenantUser.Status = TenantUserStatus.Active;
            if (tenantUser.JoinedAt == default)
            {
                tenantUser.JoinedAt = now;
            }
        }

        var workspaceCreateGrant = await dbContext.Set<CapabilityGrant>().FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.SubjectUserId == user.Id &&
                candidate.CapabilityKey == CapabilityKeys.WorkspaceCreate &&
                candidate.ScopeType == CapabilityScopeType.Tenant &&
                candidate.ScopeId == tenantId,
            cancellationToken);
        if (workspaceCreateGrant is null)
        {
            await dbContext.Set<CapabilityGrant>().AddAsync(new CapabilityGrant
            {
                TenantId = tenantId,
                SubjectUserId = user.Id,
                CapabilityKey = CapabilityKeys.WorkspaceCreate,
                ScopeType = CapabilityScopeType.Tenant,
                ScopeId = tenantId,
                GrantedByUserId = user.Id,
                GrantedAt = now,
                VersionNo = 1
            }, cancellationToken);
        }
        else if (workspaceCreateGrant.RevokedAt.HasValue ||
                 workspaceCreateGrant.ExpiresAt.HasValue ||
                 workspaceCreateGrant.GrantedAt > now)
        {
            workspaceCreateGrant.GrantedByUserId = user.Id;
            workspaceCreateGrant.GrantedAt = now;
            workspaceCreateGrant.ExpiresAt = null;
            workspaceCreateGrant.RevokedAt = null;
            workspaceCreateGrant.VersionNo++;
        }

        var recipientTenantUser = await dbContext.TenantUsers
            .FirstOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.UserId == recipient.Id, cancellationToken);
        if (recipientTenantUser is null)
        {
            await dbContext.TenantUsers.AddAsync(new TenantUser
            {
                TenantId = tenantId,
                UserId = recipient.Id,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = now
            }, cancellationToken);
        }
        else
        {
            recipientTenantUser.Role = TenantUserRole.Member;
            recipientTenantUser.Status = TenantUserStatus.Active;
            if (recipientTenantUser.JoinedAt == default)
            {
                recipientTenantUser.JoinedAt = now;
            }
        }

        var pr05ManagerTenantUser = await dbContext.TenantUsers
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.UserId == pr05Manager.Id,
                cancellationToken);
        if (pr05ManagerTenantUser is null)
        {
            await dbContext.TenantUsers.AddAsync(new TenantUser
            {
                TenantId = tenantId,
                UserId = pr05Manager.Id,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = now
            }, cancellationToken);
        }
        else
        {
            pr05ManagerTenantUser.Role = TenantUserRole.Member;
            pr05ManagerTenantUser.Status = TenantUserStatus.Active;
            if (pr05ManagerTenantUser.JoinedAt == default)
            {
                pr05ManagerTenantUser.JoinedAt = now;
            }
        }

        var workspace = await dbContext.Workspaces
            .FirstOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.Slug == workspaceSlug, cancellationToken);
        if (workspace is null)
        {
            workspace = new Workspace
            {
                TenantId = tenantId,
                Name = "Browser Smoke Workspace",
                Slug = workspaceSlug,
                Description = "Synthetic workspace for automated browser smoke tests.",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = user.Id
            };
            await dbContext.Workspaces.AddAsync(workspace, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            workspace.Name = "Browser Smoke Workspace";
            workspace.Description = "Synthetic workspace for automated browser smoke tests.";
            workspace.Status = WorkspaceStatus.Active;
            workspace.CreatedByUserId = user.Id;
            if (workspace.IsDeleted)
            {
                workspace.Restore();
            }
        }

        var workspaceMember = await dbContext.WorkspaceMembers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.WorkspaceId == workspace.Id && candidate.UserId == user.Id,
            cancellationToken);
        if (workspaceMember is null)
        {
            await dbContext.WorkspaceMembers.AddAsync(new WorkspaceMember
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                Status = MembershipStatus.Active,
                JoinedAt = now
            }, cancellationToken);
        }
        else
        {
            workspaceMember.Role = WorkspaceRole.Owner;
            workspaceMember.Status = MembershipStatus.Active;
            workspaceMember.JoinedAt ??= now;
        }

        var recipientWorkspaceMember = await dbContext.WorkspaceMembers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.WorkspaceId == workspace.Id && candidate.UserId == recipient.Id,
            cancellationToken);
        if (recipientWorkspaceMember is null)
        {
            await dbContext.WorkspaceMembers.AddAsync(new WorkspaceMember
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                UserId = recipient.Id,
                Role = WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = now
            }, cancellationToken);
        }
        else
        {
            recipientWorkspaceMember.Role = WorkspaceRole.Member;
            recipientWorkspaceMember.Status = MembershipStatus.Active;
            recipientWorkspaceMember.JoinedAt ??= now;
        }

        var announcement = await dbContext.Announcements.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.WorkspaceId == workspace.Id && candidate.Title == announcementTitle,
            cancellationToken);
        if (announcement is null)
        {
            announcement = new Announcement
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                AuthorUserId = user.Id,
                Title = announcementTitle,
                Body = "Synthetic announcement body for the real-backend browser smoke test.",
                Priority = AnnouncementPriority.Important,
                IsPinned = true,
                RequiresReadConfirmation = true,
                PublishedAt = now.AddMinutes(-5)
            };
            await dbContext.Announcements.AddAsync(announcement, cancellationToken);
        }
        else
        {
            announcement.AuthorUserId = user.Id;
            announcement.Body = "Synthetic announcement body for the real-backend browser smoke test.";
            announcement.Priority = AnnouncementPriority.Important;
            announcement.IsPinned = true;
            announcement.RequiresReadConfirmation = true;
            announcement.PublishedAt = now.AddMinutes(-5);
            announcement.ExpiresAt = null;
            if (announcement.IsDeleted)
            {
                announcement.Restore();
            }
        }

        var smokeUserAnnouncementReads = await dbContext.AnnouncementReads
            .Where(candidate =>
                candidate.TenantId == tenantId &&
                candidate.AnnouncementId == announcement.Id &&
                candidate.UserId == user.Id)
            .ToListAsync(cancellationToken);
        dbContext.AnnouncementReads.RemoveRange(smokeUserAnnouncementReads);

        var project = await EnsureSeedProjectAsync(
            dbContext,
            tenantId,
            workspace.Id,
            projectSlug,
            () => new Project
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                OwnerUserId = user.Id,
                CreatedByUserId = user.Id,
                Name = "Browser Smoke Project",
                Slug = projectSlug,
                Description = "Synthetic project for the real-backend browser smoke test.",
                Status = ProjectStatus.Active,
                StartDate = DateOnly.FromDateTime(now.UtcDateTime.Date),
                DueDate = DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(14))
            },
            existing =>
            {
                existing.OwnerUserId = user.Id;
                existing.CreatedByUserId = user.Id;
                existing.Name = "Browser Smoke Project";
                existing.Description = "Synthetic project for the real-backend browser smoke test.";
                existing.StartDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
                existing.DueDate = DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(14));
            },
            true,
            cancellationToken);

        await EnsureSeedProjectMemberAsync(
            dbContext,
            tenantId,
            project.Id,
            user.Id,
            ProjectRole.Owner,
            now,
            cancellationToken);
        await EnsureSeedProjectMemberAsync(
            dbContext,
            tenantId,
            project.Id,
            recipient.Id,
            ProjectRole.Contributor,
            now,
            cancellationToken);

        var task = await dbContext.TaskItems.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.ProjectId == project.Id && candidate.Title == taskTitle,
            cancellationToken);
        if (task is null)
        {
            task = new TaskItem
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Title = taskTitle,
                Description = "Synthetic task detail for the real-backend browser smoke test.",
                Status = TaskItemStatus.NotStarted,
                Priority = TaskPriority.Medium,
                StartDate = project.StartDate,
                DueDate = project.DueDate,
                ProgressPercent = 10,
                SortOrder = 1,
                PrimaryAssigneeUserId = user.Id,
                CreatedByUserId = user.Id
            };
            await dbContext.TaskItems.AddAsync(task, cancellationToken);
        }
        else
        {
            task.WorkspaceId = workspace.Id;
            task.Description = "Synthetic task detail for the real-backend browser smoke test.";
            task.Status = TaskItemStatus.NotStarted;
            task.Priority = TaskPriority.Medium;
            task.StartDate = project.StartDate;
            task.DueDate = project.DueDate;
            task.ProgressPercent = 10;
            task.SortOrder = 1;
            task.PrimaryAssigneeUserId = user.Id;
            task.CreatedByUserId = user.Id;
            if (task.IsDeleted)
            {
                task.Restore();
            }
        }
        task.IsBlocked = false;
        task.BlockedReason = null;

        var taskLabel = await dbContext.ProjectTaskLabels.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.ProjectId == project.Id && candidate.Name == taskLabelName,
            cancellationToken);
        if (taskLabel is null)
        {
            taskLabel = new ProjectTaskLabel
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Name = taskLabelName,
                Description = "Synthetic label for the real-backend task detail acceptance.",
                SortKey = 1024,
                VersionNo = 1
            };
            await dbContext.ProjectTaskLabels.AddAsync(taskLabel, cancellationToken);
        }
        else
        {
            taskLabel.WorkspaceId = workspace.Id;
            taskLabel.Description = "Synthetic label for the real-backend task detail acceptance.";
            taskLabel.IsArchived = false;
        }

        var taskWorkItemLabel = await dbContext.WorkItemLabels.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.TaskItemId == task.Id && candidate.LabelId == taskLabel.Id,
            cancellationToken);
        if (taskWorkItemLabel is null)
        {
            await dbContext.WorkItemLabels.AddAsync(new WorkItemLabel
            {
                TenantId = tenantId,
                TaskItemId = task.Id,
                LabelId = taskLabel.Id,
                AddedAt = now,
                AddedByUserId = user.Id
            }, cancellationToken);
        }

        var taskFile = await dbContext.FileObjects.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.WorkspaceId == workspace.Id && candidate.ProjectId == project.Id && candidate.OriginalFileName == taskFileName,
            cancellationToken);
        if (taskFile is null)
        {
            taskFile = new FileObject
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                UploadedByUserId = user.Id,
                OriginalFileName = taskFileName,
                StorageKey = $"browser-smoke/{tenantId:D}/{project.Id:D}/{taskFileName}",
                ContentType = "text/plain",
                SizeBytes = taskFileBytes.LongLength,
                Classification = DataClassification.Private,
                Status = FileObjectStatus.Active
            };
            await dbContext.FileObjects.AddAsync(taskFile, cancellationToken);
        }
        else
        {
            taskFile.Status = FileObjectStatus.Active;
            taskFile.ProjectId = project.Id;
            taskFile.WorkspaceId = workspace.Id;
            taskFile.UploadedByUserId = user.Id;
            taskFile.ContentType = "text/plain";
            taskFile.SizeBytes = taskFileBytes.LongLength;
        }

        var taskAttachment = await dbContext.Attachments.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.OwnerType == AttachmentOwnerType.TaskItem && candidate.OwnerId == task.Id && candidate.FileObjectId == taskFile.Id,
            cancellationToken);
        if (taskAttachment is null)
        {
            await dbContext.Attachments.AddAsync(new Attachment
            {
                TenantId = tenantId,
                FileObjectId = taskFile.Id,
                WorkspaceId = workspace.Id,
                OwnerType = AttachmentOwnerType.TaskItem,
                OwnerId = task.Id,
                OwnerUserId = user.Id,
                UploadedByUserId = user.Id,
                FileName = taskFileName,
                StoredFileName = taskFileName,
                FilePath = "browser-smoke/internal/task-file",
                ContentType = "text/plain",
                Extension = ".txt",
                SizeBytes = taskFileBytes.LongLength,
                StorageProvider = "browser-smoke",
                StorageKey = taskFile.StorageKey,
                ScanStatus = FileScanStatus.Clean
            }, cancellationToken);
        }
        else
        {
            taskAttachment.ScanStatus = FileScanStatus.Clean;
            taskAttachment.OwnerType = AttachmentOwnerType.TaskItem;
            taskAttachment.OwnerId = task.Id;
            taskAttachment.WorkspaceId = workspace.Id;
            taskAttachment.FileName = taskFileName;
            taskAttachment.StoredFileName = taskFileName;
            taskAttachment.FilePath = "browser-smoke/internal/task-file";
            taskAttachment.ContentType = "text/plain";
            taskAttachment.Extension = ".txt";
            taskAttachment.SizeBytes = taskFileBytes.LongLength;
            taskAttachment.StorageProvider = "browser-smoke";
            taskAttachment.StorageKey = taskFile.StorageKey;
            if (taskAttachment.IsDeleted)
            {
                taskAttachment.Restore();
            }
        }

        var taskAssignment = await dbContext.TaskAssignments.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.TaskItemId == task.Id &&
                candidate.UserId == user.Id &&
                candidate.Role == TaskAssignmentRole.Assignee,
            cancellationToken);
        if (taskAssignment is null)
        {
            await dbContext.TaskAssignments.AddAsync(new TaskAssignment
            {
                TenantId = tenantId,
                TaskItemId = task.Id,
                UserId = user.Id,
                Role = TaskAssignmentRole.Assignee,
                AssignedByUserId = user.Id,
                AssignedAt = now
            }, cancellationToken);
        }
        else
        {
            taskAssignment.AssignedByUserId = user.Id;
            taskAssignment.AssignedAt = taskAssignment.AssignedAt == default ? now : taskAssignment.AssignedAt;
        }

        const string taskArtifactName = "Browser Smoke Task Artifact";
        var taskArtifact = await dbContext.Artifacts.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.ProjectId == project.Id &&
                candidate.TaskItemId == task.Id &&
                candidate.Name == taskArtifactName,
            cancellationToken);
        if (taskArtifact is null)
        {
            await dbContext.Artifacts.AddAsync(new Artifact
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                TaskItemId = task.Id,
                Name = taskArtifactName,
                Description = "Synthetic Task-linked Artifact for the real-backend Task state-list acceptance.",
                ArtifactType = ArtifactType.Document,
                Status = ArtifactStatus.Approved,
                CreatedByUserId = user.Id
            }, cancellationToken);
        }
        else
        {
            taskArtifact.TaskItemId = task.Id;
            taskArtifact.Description = "Synthetic Task-linked Artifact for the real-backend Task state-list acceptance.";
            taskArtifact.ArtifactType = ArtifactType.Document;
            taskArtifact.Status = ArtifactStatus.Approved;
            taskArtifact.CreatedByUserId = user.Id;
            if (taskArtifact.IsDeleted)
            {
                taskArtifact.Restore();
            }
        }

        await SeedBrowserSmokeMyTasksPr04Async(
            dbContext,
            tenantId,
            user,
            recipient,
            workspace,
            project,
            task,
            now,
            cancellationToken);

        await SeedBrowserSmokeU22DemoAsync(
            dbContext,
            tenantId,
            user,
            workspace,
            cancellationToken);

        await SeedBrowserSmokeKanbanPr05Async(
            dbContext,
            tenantId,
            user,
            pr05Manager,
            recipient,
            workspace,
            now,
            cancellationToken);

        await SeedBrowserSmokeGanttPr06Async(
            dbContext,
            tenantId,
            user,
            pr05Manager,
            recipient,
            workspace,
            now,
            cancellationToken);

        await SeedBrowserSmokeNotificationsPr07Async(
            dbContext,
            tenantId,
            user,
            recipient,
            workspace,
            now,
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        await using var taskFileStream = new MemoryStream(taskFileBytes, writable: false);
        var storageResult = await fileStorage.SaveAsync(
            taskFile.StorageKey,
            taskFileStream,
            taskFile.ContentType,
            cancellationToken);
        if (!storageResult.IsSuccess)
        {
            throw new InvalidOperationException("Browser smoke synthetic file could not be stored.");
        }
    }

    private static async Task SeedBrowserSmokeNotificationsPr07Async(
        AppDbContext dbContext,
        Guid tenantId,
        User owner,
        User recipient,
        Workspace workspace,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string projectSlug = "browser-smoke-pr07-notifications";
        const string projectTitle = "PR07 Browser Smoke Notifications Project";
        const string taskTitle = "PR07 authorized notification task";
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);

        var project = await EnsureSeedProjectAsync(
            dbContext,
            tenantId,
            workspace.Id,
            projectSlug,
            () => new Project
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                OwnerUserId = owner.Id,
                CreatedByUserId = owner.Id,
                Name = projectTitle,
                Slug = projectSlug,
                Description = "Synthetic isolated Project for PR07-D notification delivery acceptance.",
                Status = ProjectStatus.Active,
                StartDate = today,
                DueDate = today.AddDays(7)
            },
            existing =>
            {
                existing.OwnerUserId = owner.Id;
                existing.CreatedByUserId = owner.Id;
                existing.Name = projectTitle;
                existing.Description = "Synthetic isolated Project for PR07-D notification delivery acceptance.";
                existing.StartDate = today;
                existing.DueDate = today.AddDays(7);
            },
            false,
            cancellationToken);

        await EnsureSeedProjectMemberAsync(dbContext, tenantId, project.Id, owner.Id, ProjectRole.Owner, now, cancellationToken);
        await EnsureSeedProjectMemberAsync(dbContext, tenantId, project.Id, recipient.Id, ProjectRole.Contributor, now, cancellationToken);

        var task = await dbContext.TaskItems.FirstOrDefaultAsync(candidate =>
            candidate.TenantId == tenantId &&
            candidate.ProjectId == project.Id &&
            candidate.Title == taskTitle,
            cancellationToken);
        if (task is null)
        {
            task = new TaskItem
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Title = taskTitle,
                Description = "Synthetic isolated Task for PR07-D notification delivery acceptance.",
                Status = TaskItemStatus.NotStarted,
                Priority = TaskPriority.Medium,
                StartDate = today,
                DueDate = today.AddDays(7),
                SortKey = 1,
                SortOrder = 1,
                VersionNo = 1,
                CreatedByUserId = owner.Id
            };
            await dbContext.TaskItems.AddAsync(task, cancellationToken);
        }
        else
        {
            task.WorkspaceId = workspace.Id;
            task.Status = TaskItemStatus.NotStarted;
            task.Priority = TaskPriority.Medium;
            task.StartDate = today;
            task.DueDate = today.AddDays(7);
            task.SortKey = 1;
            task.SortOrder = 1;
            task.VersionNo = 1;
            task.CreatedByUserId = owner.Id;
            if (task.IsDeleted)
            {
                task.Restore();
            }
        }
    }

    private static async Task SeedBrowserSmokeMyTasksPr04Async(
        AppDbContext dbContext,
        Guid tenantId,
        User user,
        User recipient,
        Workspace workspace,
        Project project,
        TaskItem assignedTask,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string groupSlug = "browser-smoke-pr04-queue";
        var group = await dbContext.Groups.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.WorkspaceId == workspace.Id && candidate.Slug == groupSlug,
            cancellationToken);
        if (group is null)
        {
            group = new Group
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                Name = "Browser Smoke PR04 Queue",
                Slug = groupSlug,
                CreatedByUserId = user.Id,
                Status = GroupStatus.Active
            };
            await dbContext.Groups.AddAsync(group, cancellationToken);
        }
        else
        {
            group.Status = GroupStatus.Active;
            if (group.IsDeleted)
            {
                group.Restore();
            }
        }

        var groupMember = await dbContext.GroupMembers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.GroupId == group.Id && candidate.UserId == user.Id,
            cancellationToken);
        if (groupMember is null)
        {
            await dbContext.GroupMembers.AddAsync(new GroupMember
            {
                TenantId = tenantId,
                GroupId = group.Id,
                UserId = user.Id,
                Role = GroupRole.Member,
                JoinedAt = now
            }, cancellationToken);
        }

        var workflow = await EnsureSeedWorkflowAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            "Browser Smoke PR04 Workflow",
            false,
            null,
            false,
            false,
            null,
            null,
            cancellationToken);

        var todo = await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "Todo",
            TaskStageCategory.Todo,
            1024,
            true,
            false,
            null,
            false,
            false,
            null,
            null,
            cancellationToken);
        var done = await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "Done",
            TaskStageCategory.Done,
            2048,
            false,
            true,
            null,
            false,
            false,
            null,
            null,
            cancellationToken);
        assignedTask.WorkflowStageId = todo.Id;
        assignedTask.PlannedEndDate = assignedTask.DueDate;

        async Task<TaskItem> TaskAsync(string title)
        {
            var task = await EnsureSeedTaskAsync(
                dbContext,
                tenantId,
                workspace.Id,
                project.Id,
                title,
                recipient.Id,
                cancellationToken);
            task.WorkflowStageId = todo.Id;
            task.Status = TaskItemStatus.NotStarted;
            task.Priority = TaskPriority.Medium;
            task.IsBlocked = false;
            task.PrimaryAssigneeUserId = recipient.Id;
            task.ReviewerUserId = null;
            task.TargetGroupId = null;
            task.CompletedAt = null;
            task.ProgressPercent = 0;
            task.DeadlineAt = null;
            task.PlannedEndDate = null;
            task.DueDate = null;
            return task;
        }

        var participating = await TaskAsync("PR04 participating task");
        var collaborator = await dbContext.WorkItemCollaborators.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.TaskItemId == participating.Id && candidate.UserId == user.Id,
            cancellationToken);
        if (collaborator is null)
        {
            await dbContext.WorkItemCollaborators.AddAsync(new WorkItemCollaborator
            {
                TenantId = tenantId,
                TaskItemId = participating.Id,
                UserId = user.Id,
                AddedByUserId = user.Id,
                AddedAt = now
            }, cancellationToken);
        }

        var review = await TaskAsync("PR04 review task");
        review.ReviewerUserId = user.Id;

        var created = await TaskAsync("PR04 created task");
        created.CreatedByUserId = user.Id;

        var watching = await TaskAsync("PR04 watching task");
        var watch = await dbContext.WorkItemWatchStates.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.TaskItemId == watching.Id && candidate.UserId == user.Id,
            cancellationToken);
        if (watch is null)
        {
            watch = new WorkItemWatchState
            {
                TenantId = tenantId,
                TaskItemId = watching.Id,
                UserId = user.Id
            };
            await dbContext.WorkItemWatchStates.AddAsync(watch, cancellationToken);
        }
        watch.AutomaticSources = WorkItemWatchAutomaticSource.Creator;
        watch.IsManualWatch = false;
        watch.IsExplicitOptOut = false;
        watch.IsWatching = false;
        watch.UpdatedAt = now;

        var queue = await TaskAsync("PR04 team queue task");
        queue.PrimaryAssigneeUserId = null;
        queue.TargetGroupId = group.Id;

        var completed = await TaskAsync("PR04 completed task");
        completed.PrimaryAssigneeUserId = user.Id;
        completed.WorkflowStageId = done.Id;
        completed.Status = TaskItemStatus.Completed;
        completed.ProgressPercent = 100;
        completed.CompletedAt = now.AddMinutes(-1);

        var filtered = await TaskAsync("PR04 critical blocked match");
        filtered.PrimaryAssigneeUserId = user.Id;
        filtered.Priority = TaskPriority.Critical;
        filtered.IsBlocked = true;
        filtered.PlannedEndDate = DateOnly.FromDateTime(now.UtcDateTime);
        filtered.DueDate = filtered.PlannedEndDate;

        for (var index = 1; index <= 12; index++)
        {
            var paged = await TaskAsync($"PR04 paging assigned {index:00}");
            paged.PrimaryAssigneeUserId = user.Id;
            paged.Priority = TaskPriority.Low;
        }

        const string secondWorkspaceSlug = "browser-smoke-workspace-secondary";
        var secondWorkspace = await dbContext.Workspaces.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Slug == secondWorkspaceSlug,
            cancellationToken);
        if (secondWorkspace is null)
        {
            secondWorkspace = new Workspace
            {
                TenantId = tenantId,
                Name = "Browser Smoke Workspace Two",
                Slug = secondWorkspaceSlug,
                Description = "Synthetic second Workspace for PR04 acceptance.",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = user.Id
            };
            await dbContext.Workspaces.AddAsync(secondWorkspace, cancellationToken);
        }
        else
        {
            secondWorkspace.Status = WorkspaceStatus.Active;
            if (secondWorkspace.IsDeleted)
            {
                secondWorkspace.Restore();
            }
        }

        var secondMember = await dbContext.WorkspaceMembers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.WorkspaceId == secondWorkspace.Id && candidate.UserId == user.Id,
            cancellationToken);
        if (secondMember is null)
        {
            await dbContext.WorkspaceMembers.AddAsync(new WorkspaceMember
            {
                TenantId = tenantId,
                WorkspaceId = secondWorkspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                Status = MembershipStatus.Active,
                JoinedAt = now
            }, cancellationToken);
        }
        else
        {
            secondMember.Role = WorkspaceRole.Owner;
            secondMember.Status = MembershipStatus.Active;
        }

        const string secondProjectSlug = "browser-smoke-pr04-second-project";
        var secondProject = await dbContext.Projects.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Slug == secondProjectSlug,
            cancellationToken);
        if (secondProject is null)
        {
            secondProject = new Project
            {
                TenantId = tenantId,
                WorkspaceId = secondWorkspace.Id,
                OwnerUserId = user.Id,
                CreatedByUserId = user.Id,
                Name = "Browser Smoke PR04 Second Project",
                Slug = secondProjectSlug,
                Status = ProjectStatus.Active
            };
            await dbContext.Projects.AddAsync(secondProject, cancellationToken);
        }

        var secondProjectMember = await dbContext.ProjectMembers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.ProjectId == secondProject.Id && candidate.UserId == user.Id,
            cancellationToken);
        if (secondProjectMember is null)
        {
            await dbContext.ProjectMembers.AddAsync(new ProjectMember
            {
                TenantId = tenantId,
                ProjectId = secondProject.Id,
                UserId = user.Id,
                Role = ProjectRole.Owner,
                JoinedAt = now
            }, cancellationToken);
        }

        const string secondTaskTitle = "PR04 second workspace assigned";
        var secondTask = await dbContext.TaskItems.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.ProjectId == secondProject.Id && candidate.Title == secondTaskTitle,
            cancellationToken);
        if (secondTask is null)
        {
            secondTask = new TaskItem
            {
                TenantId = tenantId,
                WorkspaceId = secondWorkspace.Id,
                ProjectId = secondProject.Id,
                Title = secondTaskTitle,
                CreatedByUserId = user.Id
            };
            await dbContext.TaskItems.AddAsync(secondTask, cancellationToken);
        }
        secondTask.PrimaryAssigneeUserId = user.Id;
        secondTask.Status = TaskItemStatus.NotStarted;
        secondTask.Priority = TaskPriority.Medium;
    }

    private static async Task SeedBrowserSmokeU22DemoAsync(
        AppDbContext dbContext,
        Guid tenantId,
        User user,
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        var project = await dbContext.Projects.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.WorkspaceId == workspace.Id &&
                candidate.Slug == U22DemoProjectSlug,
            cancellationToken);
        ProjectExecutionScope? projectScope = null;
        if (project is null)
        {
            project = new Project
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                OwnerUserId = user.Id,
                CreatedByUserId = user.Id,
                Name = U22DemoProjectName,
                Slug = U22DemoProjectSlug,
                Description = "Synthetic Test-only Project for the U-22 demo. It is not production data.",
                Status = ProjectStatus.Active,
                Visibility = ProjectVisibility.WorkspaceVisible,
                ActivationState = ProjectActivationState.Activated,
                ActivatedAtUtc = U22DemoActivityOccurredAt,
                ActivationVersion = 1,
                VersionNo = 1
            };
            projectScope = new ProjectExecutionScope
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                WebEnabled = false,
                ProjectFilesEnabled = true,
                VersionNo = 1,
                UpdatedByUserId = user.Id
            };
            project.ExecutionScope = projectScope;
            await dbContext.Projects.AddAsync(project, cancellationToken);
        }
        else
        {
            project.OwnerUserId = user.Id;
            project.CreatedByUserId = user.Id;
            project.Name = U22DemoProjectName;
            project.Description = "Synthetic Test-only Project for the U-22 demo. It is not production data.";
            project.Status = ProjectStatus.Active;
            project.Visibility = ProjectVisibility.WorkspaceVisible;
            project.ActivationState = ProjectActivationState.Activated;
            project.ActivatedAtUtc = U22DemoActivityOccurredAt;
            project.ActivationVersion = 1;
            project.SuspendedFromStatus = null;
            project.ArchivedFromStatus = null;
            project.VersionNo = 1;
            if (project.IsDeleted)
            {
                project.Restore();
            }
        }

        projectScope ??= project.ExecutionScope;
        projectScope ??= dbContext.ProjectExecutionScopes.Local.FirstOrDefault(
            candidate => candidate.ProjectId == project.Id);
        projectScope ??= await dbContext.ProjectExecutionScopes.FirstOrDefaultAsync(
            candidate => candidate.ProjectId == project.Id,
            cancellationToken);
        if (projectScope is null)
        {
            projectScope = new ProjectExecutionScope
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                VersionNo = 1,
                UpdatedByUserId = user.Id
            };
            project.ExecutionScope = projectScope;
            await dbContext.ProjectExecutionScopes.AddAsync(projectScope, cancellationToken);
        }

        // The Project default and Task override intentionally differ so a
        // demo can show that the override replaces, rather than merges with,
        // the default. These are policy flags only: no source is retrieved.
        projectScope.TenantId = tenantId;
        projectScope.WorkspaceId = workspace.Id;
        projectScope.ProjectId = project.Id;
        projectScope.WebEnabled = false;
        projectScope.ProjectFilesEnabled = true;
        projectScope.VersionNo = 1;
        projectScope.UpdatedByUserId = user.Id;

        await EnsureSeedProjectMemberAsync(
            dbContext,
            tenantId,
            project.Id,
            user.Id,
            ProjectRole.Owner,
            U22DemoActivityOccurredAt,
            cancellationToken);

        var workflow = await EnsureSeedWorkflowAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            "U-22 Synthetic Demo Workflow",
            false,
            ProjectKanbanSwimlane.None,
            true,
            true,
            1,
            1,
            cancellationToken);

        await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "Todo",
            TaskStageCategory.Todo,
            1024,
            true,
            false,
            null,
            true,
            false,
            1,
            1,
            cancellationToken);
        var inProgress = await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "In progress",
            TaskStageCategory.InProgress,
            2048,
            false,
            false,
            null,
            true,
            false,
            1,
            1,
            cancellationToken);

        var task = await EnsureSeedTaskAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            U22DemoTaskTitle,
            user.Id,
            cancellationToken);

        // This is one current workflow state, not a fabricated execution
        // history. Keep the legacy percentage at zero so the fixture never
        // implies measured execution progress.
        task.WorkspaceId = workspace.Id;
        task.Kind = WorkItemKind.Task;
        task.Description = "Synthetic Test-only U-22 Task. It demonstrates policy configuration and current state, not runtime execution.";
        task.BriefGoal = U22DemoTaskGoal;
        task.BriefDeliverable = U22DemoTaskDeliverable;
        task.BriefConstraints = U22DemoTaskConstraints;
        task.WorkflowStageId = inProgress.Id;
        task.Status = TaskItemStatus.InProgress;
        task.Priority = TaskPriority.High;
        task.IsBlocked = false;
        task.BlockedReason = null;
        task.PrimaryAssigneeUserId = user.Id;
        task.ReviewerUserId = null;
        task.TargetGroupId = null;
        task.ProgressPercent = 0;
        task.ActualStartAt = null;
        task.CompletedAt = null;
        task.CancelledAt = null;
        task.CancellationReason = null;
        task.ReviewStatus = TaskReviewStatus.None;
        task.ReviewSubmittedAt = null;
        task.ReviewResolvedAt = null;
        task.ReviewResolvedByUserId = null;
        task.SortKey = 1024;
        task.SortOrder = 1;
        task.CreatedByUserId = user.Id;

        var assignment = await dbContext.TaskAssignments.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.TaskItemId == task.Id &&
                candidate.UserId == user.Id &&
                candidate.Role == TaskAssignmentRole.Assignee,
            cancellationToken);
        if (assignment is null)
        {
            await dbContext.TaskAssignments.AddAsync(new TaskAssignment
            {
                TenantId = tenantId,
                TaskItemId = task.Id,
                UserId = user.Id,
                Role = TaskAssignmentRole.Assignee,
                AssignedByUserId = user.Id,
                AssignedAt = U22DemoActivityOccurredAt
            }, cancellationToken);
        }
        else
        {
            assignment.AssignedByUserId = user.Id;
            assignment.AssignedAt = U22DemoActivityOccurredAt;
        }

        var taskOverride = await dbContext.TaskExecutionScopeOverrides.FirstOrDefaultAsync(
            candidate => candidate.TaskItemId == task.Id,
            cancellationToken);
        if (taskOverride is null)
        {
            taskOverride = new TaskExecutionScopeOverride
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                TaskItemId = task.Id,
                VersionNo = 1,
                UpdatedByUserId = user.Id
            };
            await dbContext.TaskExecutionScopeOverrides.AddAsync(taskOverride, cancellationToken);
        }

        taskOverride.TenantId = tenantId;
        taskOverride.WorkspaceId = workspace.Id;
        taskOverride.ProjectId = project.Id;
        taskOverride.TaskItemId = task.Id;
        taskOverride.WebEnabled = true;
        taskOverride.ProjectFilesEnabled = false;
        taskOverride.VersionNo = 1;
        taskOverride.UpdatedByUserId = user.Id;

        var activity = await dbContext.ActivityLogs.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.ProjectId == project.Id &&
                candidate.TaskItemId == task.Id &&
                candidate.Body == U22DemoActivityBody,
            cancellationToken);
        if (activity is null)
        {
            activity = new ActivityLog
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                TaskItemId = task.Id,
                AuthorUserId = user.Id,
                ActivityType = ActivityLogType.Note,
                Body = U22DemoActivityBody,
                OccurredAt = U22DemoActivityOccurredAt
            };
            await dbContext.ActivityLogs.AddAsync(activity, cancellationToken);
        }
        else
        {
            activity.AuthorUserId = user.Id;
            activity.ActivityType = ActivityLogType.Note;
            activity.OccurredAt = U22DemoActivityOccurredAt;
        }
    }

    private static async Task SeedBrowserSmokeKanbanPr05Async(
        AppDbContext dbContext,
        Guid tenantId,
        User owner,
        User manager,
        User recipient,
        Workspace workspace,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string groupSlug = "browser-smoke-pr04-queue";
        const string projectSlug = "browser-smoke-pr05-kanban";

        var group = dbContext.Groups.Local.FirstOrDefault(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.WorkspaceId == workspace.Id &&
                candidate.Slug == groupSlug)
            ?? await dbContext.Groups.SingleAsync(
                candidate =>
                    candidate.TenantId == tenantId &&
                    candidate.WorkspaceId == workspace.Id &&
                    candidate.Slug == groupSlug,
                cancellationToken);

        var managerGroupMember = dbContext.GroupMembers.Local.FirstOrDefault(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.GroupId == group.Id &&
                candidate.UserId == manager.Id)
            ?? await dbContext.GroupMembers.FirstOrDefaultAsync(
                candidate =>
                    candidate.TenantId == tenantId &&
                    candidate.GroupId == group.Id &&
                    candidate.UserId == manager.Id,
                cancellationToken);
        if (managerGroupMember is not null)
        {
            dbContext.GroupMembers.Remove(managerGroupMember);
        }

        await EnsureSeedWorkspaceMemberAsync(
            dbContext,
            tenantId,
            workspace.Id,
            manager.Id,
            WorkspaceRole.Member,
            MembershipStatus.Active,
            now,
            false,
            cancellationToken);

        var project = await EnsureSeedProjectAsync(
            dbContext,
            tenantId,
            workspace.Id,
            projectSlug,
            () => new Project
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                GroupId = group.Id,
                OwnerUserId = owner.Id,
                CreatedByUserId = owner.Id,
                Name = "PR05 Browser Acceptance Project",
                Slug = projectSlug,
                Description = "Synthetic Project Kanban data for PR05 real-backend browser acceptance.",
                Status = ProjectStatus.Active,
                StartDate = DateOnly.FromDateTime(now.UtcDateTime.Date),
                DueDate = DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(14))
            },
            existing =>
            {
                existing.GroupId = group.Id;
                existing.OwnerUserId = owner.Id;
                existing.CreatedByUserId = owner.Id;
                existing.Name = "PR05 Browser Acceptance Project";
                existing.Description = "Synthetic Project Kanban data for PR05 real-backend browser acceptance.";
                existing.StartDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
                existing.DueDate = DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(14));
            },
            false,
            cancellationToken);

        await EnsureSeedProjectMemberAsync(dbContext, tenantId, project.Id, owner.Id, ProjectRole.Owner, now, cancellationToken);
        await EnsureSeedProjectMemberAsync(dbContext, tenantId, project.Id, manager.Id, ProjectRole.Manager, now, cancellationToken);
        await EnsureSeedProjectMemberAsync(dbContext, tenantId, project.Id, recipient.Id, ProjectRole.Contributor, now, cancellationToken);

        var workflow = await EnsureSeedWorkflowAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            "PR05 Browser Acceptance Workflow",
            false,
            ProjectKanbanSwimlane.None,
            true,
            true,
            1,
            1,
            cancellationToken);

        var todo = await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "Todo",
            TaskStageCategory.Todo,
            1000,
            true,
            false,
            4,
            true,
            true,
            1,
            1,
            cancellationToken);
        await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "Done",
            TaskStageCategory.Done,
            2000,
            false,
            true,
            null,
            true,
            true,
            1,
            1,
            cancellationToken);
        await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "Cancelled",
            TaskStageCategory.Cancelled,
            3000,
            false,
            true,
            null,
            true,
            true,
            1,
            1,
            cancellationToken);

        async Task TaskAsync(string title, long sortKey)
        {
            var task = await EnsureSeedTaskAsync(
                dbContext,
                tenantId,
                workspace.Id,
                project.Id,
                title,
                manager.Id,
                cancellationToken);
            task.WorkflowStageId = todo.Id;
            task.Kind = WorkItemKind.Task;
            task.Description = $"Synthetic {title} for PR05 real-backend browser acceptance.";
            task.Status = TaskItemStatus.NotStarted;
            task.Priority = TaskPriority.Medium;
            task.IsBlocked = false;
            task.BlockedReason = null;
            task.TargetGroupId = null;
            task.PrimaryAssigneeUserId = manager.Id;
            task.ReviewerUserId = null;
            task.StartDate = project.StartDate;
            task.DueDate = project.DueDate;
            task.PlannedStartDate = null;
            task.PlannedEndDate = null;
            task.DeadlineAt = null;
            task.ActualStartAt = null;
            task.CompletedAt = null;
            task.CancelledAt = null;
            task.CancellationReason = null;
            task.ReviewStatus = TaskReviewStatus.None;
            task.ReviewSubmittedAt = null;
            task.ReviewResolvedAt = null;
            task.ReviewResolvedByUserId = null;
            task.ReviewReturnReason = null;
            task.SortKey = sortKey;
            task.VersionNo = 1;
            task.ProgressPercent = 0;
            task.SortOrder = checked((int)(sortKey / 1000));
            task.CreatedByUserId = manager.Id;
        }

        await TaskAsync("PR05 real move card", 1000);
        await TaskAsync("PR05 stable reorder card", 2000);
        await TaskAsync("PR05 stable neighbor card", 3000);
        await TaskAsync("PR05 cancellation card", 4000);
        await TaskAsync("PR05 stale conflict card", 5000);
    }

    private static async Task SeedBrowserSmokeGanttPr06Async(
        AppDbContext dbContext,
        Guid tenantId,
        User owner,
        User manager,
        User viewer,
        Workspace workspace,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string groupSlug = "browser-smoke-pr04-queue";
        const string projectSlug = "browser-smoke-pr06-gantt";
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);

        var group = dbContext.Groups.Local.FirstOrDefault(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.WorkspaceId == workspace.Id &&
                candidate.Slug == groupSlug)
            ?? await dbContext.Groups.SingleAsync(
                candidate =>
                    candidate.TenantId == tenantId &&
                    candidate.WorkspaceId == workspace.Id &&
                    candidate.Slug == groupSlug,
                cancellationToken);

        await EnsureSeedWorkspaceMemberAsync(
            dbContext,
            tenantId,
            workspace.Id,
            manager.Id,
            WorkspaceRole.Member,
            MembershipStatus.Active,
            now,
            true,
            cancellationToken);

        var project = await EnsureSeedProjectAsync(
            dbContext,
            tenantId,
            workspace.Id,
            projectSlug,
            () => new Project
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                GroupId = group.Id,
                OwnerUserId = owner.Id,
                CreatedByUserId = owner.Id,
                Name = "PR06 Browser Acceptance Project",
                Slug = projectSlug,
                Description = "Synthetic canonical Gantt data for PR06 real-backend browser acceptance.",
                Status = ProjectStatus.Active,
                StartDate = today,
                DueDate = today.AddDays(45),
                VersionNo = 1
            },
            existing =>
            {
                existing.GroupId = group.Id;
                existing.OwnerUserId = owner.Id;
                existing.CreatedByUserId = owner.Id;
                existing.Name = "PR06 Browser Acceptance Project";
                existing.Description = "Synthetic canonical Gantt data for PR06 real-backend browser acceptance.";
                existing.StartDate = today;
                existing.DueDate = today.AddDays(45);
            },
            false,
            cancellationToken);

        await EnsureSeedProjectMemberAsync(dbContext, tenantId, project.Id, owner.Id, ProjectRole.Owner, now, cancellationToken);
        await EnsureSeedProjectMemberAsync(dbContext, tenantId, project.Id, manager.Id, ProjectRole.Manager, now, cancellationToken);
        await EnsureSeedProjectMemberAsync(dbContext, tenantId, project.Id, viewer.Id, ProjectRole.Viewer, now, cancellationToken);

        var workflow = await EnsureSeedWorkflowAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            "PR06 Browser Acceptance Workflow",
            false,
            ProjectKanbanSwimlane.None,
            true,
            true,
            1,
            null,
            cancellationToken);

        var todo = await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "Todo",
            TaskStageCategory.Todo,
            1000,
            true,
            false,
            null,
            true,
            false,
            1,
            null,
            cancellationToken);
        var inProgress = await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "In Progress",
            TaskStageCategory.InProgress,
            2000,
            false,
            false,
            null,
            true,
            false,
            1,
            null,
            cancellationToken);
        await EnsureSeedWorkflowStageAsync(
            dbContext,
            tenantId,
            workspace.Id,
            project.Id,
            workflow.Id,
            "Done",
            TaskStageCategory.Done,
            3000,
            false,
            true,
            null,
            true,
            false,
            1,
            null,
            cancellationToken);

        async Task<TaskItem> TaskAsync(
            string title,
            long sortKey,
            TaskWorkflowStage stage,
            DateOnly? plannedStartDate,
            DateOnly? plannedEndDate,
            int progressPercent,
            bool blocked = false)
        {
            var task = await EnsureSeedTaskAsync(
                dbContext,
                tenantId,
                workspace.Id,
                project.Id,
                title,
                manager.Id,
                cancellationToken);
            task.WorkflowStageId = stage.Id;
            task.Kind = WorkItemKind.Task;
            task.MilestoneId = null;
            task.ParentTaskItemId = null;
            task.Description = $"Synthetic {title} for PR06 real-backend browser acceptance.";
            task.Status = stage.InternalCategory == TaskStageCategory.InProgress
                ? TaskItemStatus.InProgress
                : TaskItemStatus.NotStarted;
            task.Priority = blocked ? TaskPriority.Critical : TaskPriority.High;
            task.IsBlocked = blocked;
            task.BlockedReason = blocked ? "Synthetic blocked-state acceptance." : null;
            task.TargetGroupId = null;
            task.PrimaryAssigneeUserId = manager.Id;
            task.ReviewerUserId = null;
            task.StartDate = null;
            task.DueDate = null;
            task.PlannedStartDate = plannedStartDate;
            task.PlannedEndDate = plannedEndDate;
            task.DeadlineAt = now.AddDays(60);
            task.ActualStartAt = null;
            task.CompletedAt = null;
            task.CancelledAt = null;
            task.CancellationReason = null;
            task.ReviewStatus = TaskReviewStatus.None;
            task.ReviewSubmittedAt = null;
            task.ReviewResolvedAt = null;
            task.ReviewResolvedByUserId = null;
            task.ReviewReturnReason = null;
            task.SortKey = sortKey;
            task.ProgressPercent = progressPercent;
            task.SortOrder = checked((int)(sortKey / 1000));
            task.CreatedByUserId = manager.Id;
            return task;
        }

        var parent = await TaskAsync(
            "PR06 derived parent",
            1000,
            inProgress,
            null,
            null,
            0);
        var scheduleTask = await TaskAsync(
            "PR06 schedule task",
            2000,
            inProgress,
            today.AddDays(2),
            today.AddDays(5),
            25,
            blocked: true);
        var predecessor = await TaskAsync(
            "PR06 predecessor task",
            3000,
            inProgress,
            today,
            today.AddDays(8),
            20);
        var unscheduled = await TaskAsync(
            "PR06 unscheduled task",
            4000,
            todo,
            null,
            null,
            0);
        var conflict = await TaskAsync(
            "PR06 conflict task",
            5000,
            todo,
            today.AddDays(10),
            today.AddDays(12),
            0);
        var dependencySuccessor = await TaskAsync(
            "PR06 dependency successor",
            6000,
            todo,
            today.AddDays(16),
            today.AddDays(20),
            0);

        scheduleTask.ParentTaskItemId = parent.Id;
        predecessor.ParentTaskItemId = parent.Id;

        var milestone = await dbContext.Milestones.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.ProjectId == project.Id &&
                candidate.Name == "PR06 release milestone",
            cancellationToken);
        if (milestone is null)
        {
            milestone = new Milestone
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                Name = "PR06 release milestone",
                Description = "Synthetic zero-duration PR06 Milestone.",
                DueDate = today.AddDays(30),
                Status = MilestoneStatus.NotStarted,
                SortOrder = 1,
                VersionNo = 1
            };
            await dbContext.Milestones.AddAsync(milestone, cancellationToken);
        }
        else
        {
            milestone.Description = "Synthetic zero-duration PR06 Milestone.";
            milestone.DueDate = today.AddDays(30);
            milestone.Status = MilestoneStatus.NotStarted;
            milestone.SortOrder = 1;
            if (milestone.IsDeleted)
            {
                milestone.Restore();
            }
        }

        var existingDependencies = await dbContext.TaskDependencies
            .Where(candidate => candidate.TenantId == tenantId && candidate.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        dbContext.TaskDependencies.RemoveRange(existingDependencies);
        await dbContext.TaskDependencies.AddRangeAsync(
            new TaskDependency
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                PredecessorTaskItemId = predecessor.Id,
                SuccessorTaskItemId = scheduleTask.Id,
                DependencyType = TaskDependencyType.FinishToStart
            },
            new TaskDependency
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                PredecessorTaskItemId = predecessor.Id,
                SuccessorTaskItemId = conflict.Id,
                DependencyType = TaskDependencyType.StartToStart
            });

        _ = unscheduled;
        _ = dependencySuccessor;
    }

    public static async Task EnsureBootstrapAdminAsync(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        Guid tenantId,
        string email,
        string? password = null,
        string? displayName = null,
        bool ensureDefaultWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, cancellationToken);
        if (user is null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            user = new User
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Local Admin" : displayName.Trim(),
                Email = email.Trim(),
                NormalizedEmail = normalizedEmail,
                PasswordHash = passwordHasher.HashPassword(password),
                SystemRole = SystemRole.SystemAdmin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await dbContext.Users.AddAsync(user, cancellationToken);
        }
        else
        {
            var trimmedDisplayName = string.IsNullOrWhiteSpace(displayName) ? user.DisplayName : displayName.Trim();
            if (user.DisplayName != trimmedDisplayName)
            {
                user.DisplayName = trimmedDisplayName;
            }

            var trimmedEmail = email.Trim();
            if (user.Email != trimmedEmail)
            {
                user.Email = trimmedEmail;
            }

            if (!string.IsNullOrWhiteSpace(password) && !passwordHasher.VerifyPassword(user.PasswordHash, password))
            {
                user.PasswordHash = passwordHasher.HashPassword(password);
            }

            if (user.SystemRole != SystemRole.SystemAdmin)
            {
                user.SystemRole = SystemRole.SystemAdmin;
            }

            if (user.Status != UserStatus.Active)
            {
                user.Status = UserStatus.Active;
            }

            if (user.FailedLoginAttempts != 0)
            {
                user.FailedLoginAttempts = 0;
            }

            if (user.LockoutEndAt.HasValue)
            {
                user.LockoutEndAt = null;
            }

            if (user.IsDeleted)
            {
                user.Restore();
            }
        }

        var tenantUser = await dbContext.TenantUsers
            .FirstOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.UserId == user.Id, cancellationToken);
        if (tenantUser is null)
        {
            tenantUser = new TenantUser
            {
                TenantId = tenantId,
                UserId = user.Id,
                Role = TenantUserRole.Owner,
                Status = TenantUserStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await dbContext.TenantUsers.AddAsync(tenantUser, cancellationToken);
        }
        else
        {
            if (tenantUser.Role != TenantUserRole.Owner)
            {
                tenantUser.Role = TenantUserRole.Owner;
            }

            if (tenantUser.Status != TenantUserStatus.Active)
            {
                tenantUser.Status = TenantUserStatus.Active;
            }

            if (tenantUser.JoinedAt == default)
            {
                tenantUser.JoinedAt = DateTimeOffset.UtcNow;
            }
        }

        if (ensureDefaultWorkspace)
        {
            await EnsureDefaultWorkspaceOwnerAsync(dbContext, tenantId, user, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureDefaultWorkspaceOwnerAsync(
        AppDbContext dbContext,
        Guid tenantId,
        User user,
        CancellationToken cancellationToken)
    {
        const string workspaceSlug = "default-workspace";

        var workspace = await dbContext.Workspaces
            .FirstOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.Slug == workspaceSlug, cancellationToken);
        if (workspace is null)
        {
            workspace = new Workspace
            {
                TenantId = tenantId,
                Name = "Default Workspace",
                Slug = workspaceSlug,
                Description = "Bootstrap workspace for initial admin operations.",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = user.Id
            };
            await dbContext.Workspaces.AddAsync(workspace, cancellationToken);
        }
        else
        {
            if (workspace.Status != WorkspaceStatus.Active)
            {
                workspace.Status = WorkspaceStatus.Active;
            }

            if (workspace.IsDeleted)
            {
                workspace.Restore();
            }
        }

        var member = await dbContext.WorkspaceMembers
            .FirstOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.WorkspaceId == workspace.Id && candidate.UserId == user.Id, cancellationToken);
        if (member is null)
        {
            member = new WorkspaceMember
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                Status = MembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            };
            await dbContext.WorkspaceMembers.AddAsync(member, cancellationToken);
        }
        else
        {
            if (member.Role != WorkspaceRole.Owner)
            {
                member.Role = WorkspaceRole.Owner;
            }

            if (member.Status != MembershipStatus.Active)
            {
                member.Status = MembershipStatus.Active;
            }

            member.JoinedAt ??= DateTimeOffset.UtcNow;
        }
    }

    private static async Task SeedModulesAsync(AppDbContext dbContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var modules = new (string Key, string Name, string Route, string Icon, int Sort)[]
        {
            ("Dashboard", "Dashboard", "/dashboard", "layout-dashboard", 10),
            ("Workspaces", "Workspaces", "/workspaces", "building", 20),
            ("Groups", "Groups", "/groups", "users", 30),
            ("Channels", "Channels", "/channels", "messages-square", 40),
            ("Messaging", "Messaging", "/messages", "message-circle", 50),
            ("Announcements", "Announcements", "/announcements", "megaphone", 60),
            ("Notifications", "Notifications", "/notifications", "bell", 70),
            ("Files", "Files", "/files", "paperclip", 80),
            ("Projects", "Projects", "/projects", "folder-kanban", 90),
            ("ProductionTracking", "Production Tracking", "/production", "gantt-chart", 100),
            ("Feedback", "Feedback", "/feedback", "message-square-text", 110),
            ("Search", "Search", "/search", "search", 120),
            ("Admin", "Admin", "/admin", "shield", 130)
        };

        var existing = await dbContext.FeatureModules.ToDictionaryAsync(module => module.Key, cancellationToken);
        foreach (var module in modules.Where(module => !existing.ContainsKey(module.Key)))
        {
            await dbContext.FeatureModules.AddAsync(new FeatureModule
            {
                Key = module.Key,
                Name = module.Name,
                DefaultRoute = module.Route,
                Icon = module.Icon,
                RequiredRole = module.Key == "Admin" ? SystemRole.Admin : null,
                SortOrder = module.Sort,
                CreatedAt = now
            }, cancellationToken);
        }

        if (existing.TryGetValue("Admin", out var adminModule) && adminModule.RequiredRole != SystemRole.Admin)
        {
            adminModule.RequiredRole = SystemRole.Admin;
        }
    }

    private static async Task SeedPanelsAsync(AppDbContext dbContext, IReadOnlyDictionary<string, FeatureModule> modules, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var panels = new (string Key, string Title, string Module, int Sort)[]
        {
            ("dashboard.overview", "Overview", "Dashboard", 10),
            ("project.summary", "Project Summary", "Projects", 20),
            ("project.taskList", "Task List", "Projects", 30),
            ("project.gantt", "Gantt", "ProductionTracking", 40),
            ("project.members", "Members", "Projects", 50),
            ("project.artifacts", "Artifacts", "Files", 60),
            ("project.comments", "Comments", "Projects", 70),
            ("project.activityLogs", "Activity Logs", "Projects", 80),
            ("messaging.conversationList", "Conversations", "Messaging", 90),
            ("messaging.conversationDetail", "Conversation Detail", "Messaging", 100),
            ("notifications.list", "Notifications", "Notifications", 110)
        };

        var existing = await dbContext.PanelDefinitions.Select(panel => panel.Key).ToListAsync(cancellationToken);
        foreach (var panel in panels.Where(panel => !existing.Contains(panel.Key)))
        {
            await dbContext.PanelDefinitions.AddAsync(new PanelDefinition
            {
                FeatureModuleId = modules[panel.Module].Id,
                Key = panel.Key,
                Name = panel.Title,
                Route = "/" + panel.Key.Replace('.', '/'),
                DefaultPosition = "Center",
                MinWidth = 280,
                MinHeight = 180,
                DefaultWidth = 640,
                DefaultHeight = 420,
                SortOrder = panel.Sort,
                CreatedAt = now
            }, cancellationToken);
        }
    }

    private static async Task SeedCommandsAsync(AppDbContext dbContext, IReadOnlyDictionary<string, FeatureModule> modules, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var commands = new (string Key, string Label, string Module, CommandContextType Context, string? Route, int Sort)[]
        {
            ("project.open", "Open Project", "Projects", CommandContextType.Project, "/projects/{contextId}", 10),
            ("project.create", "Create Project", "Projects", CommandContextType.Global, "/projects/new", 20),
            ("project.members", "Project Members", "Projects", CommandContextType.Project, "/projects/{contextId}/members", 30),
            ("task.create", "Create Task", "Projects", CommandContextType.Project, "/projects/{contextId}/tasks/new", 40),
            ("task.changeStatus", "Change Status", "Projects", CommandContextType.TaskItem, null, 50),
            ("task.assignUser", "Assign User", "Projects", CommandContextType.TaskItem, null, 60),
            ("task.addComment", "Add Comment", "Projects", CommandContextType.TaskItem, null, 70),
            ("task.complete", "Complete Task", "Projects", CommandContextType.TaskItem, null, 80),
            ("artifact.upload", "Upload Artifact", "Files", CommandContextType.Project, null, 90),
            ("activityLog.create", "Create Activity Log", "Projects", CommandContextType.Project, null, 100),
            ("dm.open", "Open DM", "Messaging", CommandContextType.Global, "/messages", 110),
            ("notification.open", "Open Notification", "Notifications", CommandContextType.Global, "/notifications", 120),
            ("gantt.open", "Open Gantt", "ProductionTracking", CommandContextType.Project, "/projects/{contextId}/gantt", 130)
        };

        var existing = await dbContext.CommandDefinitions.Select(command => command.Key).ToListAsync(cancellationToken);
        foreach (var command in commands.Where(command => !existing.Contains(command.Key)))
        {
            await dbContext.CommandDefinitions.AddAsync(new CommandDefinition
            {
                FeatureModuleId = modules[command.Module].Id,
                Key = command.Key,
                Name = command.Label,
                ActionType = command.Route is null ? CommandActionType.ClientAction : CommandActionType.Navigate,
                Route = command.Route,
                ContextType = command.Context,
                SortOrder = command.Sort,
                CreatedAt = now
            }, cancellationToken);
        }
    }

    private static async Task EnsureSeedWorkspaceMemberAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid workspaceId,
        Guid userId,
        WorkspaceRole role,
        MembershipStatus status,
        DateTimeOffset joinedAt,
        bool checkLocalFirst,
        CancellationToken cancellationToken)
    {
        var member = checkLocalFirst
            ? dbContext.WorkspaceMembers.Local.FirstOrDefault(candidate =>
                candidate.TenantId == tenantId &&
                candidate.WorkspaceId == workspaceId &&
                candidate.UserId == userId)
            : null;
        member ??= await dbContext.WorkspaceMembers.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.WorkspaceId == workspaceId &&
                candidate.UserId == userId,
            cancellationToken);

        if (member is null)
        {
            await dbContext.WorkspaceMembers.AddAsync(new WorkspaceMember
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = role,
                Status = status,
                JoinedAt = joinedAt
            }, cancellationToken);
            return;
        }

        member.Role = role;
        member.Status = status;
        member.JoinedAt ??= joinedAt;
    }

    private static async Task<Project> EnsureSeedProjectAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid workspaceId,
        string slug,
        Func<Project> create,
        Action<Project> refresh,
        bool saveAfterCreate,
        CancellationToken cancellationToken)
    {
        var project = await dbContext.Projects.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.WorkspaceId == workspaceId &&
                candidate.Slug == slug,
            cancellationToken);
        if (project is null)
        {
            project = create();
            await dbContext.Projects.AddAsync(project, cancellationToken);
            if (saveAfterCreate)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return project;
        }

        refresh(project);
        if (project.IsDeleted)
        {
            project.Restore();
        }

        return project;
    }

    private static async Task EnsureSeedProjectMemberAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid projectId,
        Guid userId,
        ProjectRole role,
        DateTimeOffset joinedAt,
        CancellationToken cancellationToken)
    {
        var member = await dbContext.ProjectMembers.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.ProjectId == projectId &&
                candidate.UserId == userId,
            cancellationToken);
        if (member is null)
        {
            await dbContext.ProjectMembers.AddAsync(new ProjectMember
            {
                TenantId = tenantId,
                ProjectId = projectId,
                UserId = userId,
                Role = role,
                JoinedAt = joinedAt
            }, cancellationToken);
            return;
        }

        member.Role = role;
        if (member.JoinedAt == default)
        {
            member.JoinedAt = joinedAt;
        }
    }

    private static async Task<TaskItem> EnsureSeedTaskAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid workspaceId,
        Guid projectId,
        string title,
        Guid createdByUserId,
        CancellationToken cancellationToken)
    {
        var task = await dbContext.TaskItems.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.ProjectId == projectId &&
                candidate.Title == title,
            cancellationToken);
        if (task is null)
        {
            task = new TaskItem
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Title = title,
                CreatedByUserId = createdByUserId
            };
            await dbContext.TaskItems.AddAsync(task, cancellationToken);
        }
        else if (task.IsDeleted)
        {
            task.Restore();
        }

        task.WorkspaceId = workspaceId;
        return task;
    }

    private static async Task<TaskWorkflowDefinition> EnsureSeedWorkflowAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid workspaceId,
        Guid projectId,
        string name,
        bool reviewEnforcementEnabled,
        ProjectKanbanSwimlane? kanbanDefaultSwimlane,
        bool refreshExisting,
        bool updateKanbanDefaultSwimlane,
        long? createdVersionNo,
        long? existingVersionNo,
        CancellationToken cancellationToken)
    {
        var workflow = await dbContext.TaskWorkflowDefinitions.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.ProjectId == projectId,
            cancellationToken);
        if (workflow is null)
        {
            workflow = new TaskWorkflowDefinition
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Name = name,
                ReviewEnforcementEnabled = reviewEnforcementEnabled
            };
            if (kanbanDefaultSwimlane.HasValue)
            {
                workflow.KanbanDefaultSwimlane = kanbanDefaultSwimlane.Value;
            }
            if (createdVersionNo.HasValue)
            {
                workflow.VersionNo = createdVersionNo.Value;
            }

            await dbContext.TaskWorkflowDefinitions.AddAsync(workflow, cancellationToken);
            return workflow;
        }

        if (!refreshExisting)
        {
            return workflow;
        }

        workflow.WorkspaceId = workspaceId;
        workflow.Name = name;
        workflow.ReviewEnforcementEnabled = reviewEnforcementEnabled;
        if (updateKanbanDefaultSwimlane && kanbanDefaultSwimlane.HasValue)
        {
            workflow.KanbanDefaultSwimlane = kanbanDefaultSwimlane.Value;
        }
        if (existingVersionNo.HasValue)
        {
            workflow.VersionNo = existingVersionNo.Value;
        }

        return workflow;
    }

    private static async Task<TaskWorkflowStage> EnsureSeedWorkflowStageAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid workspaceId,
        Guid projectId,
        Guid definitionId,
        string name,
        TaskStageCategory category,
        long sortKey,
        bool isInitial,
        bool isTerminal,
        int? wipWarningLimit,
        bool refreshExisting,
        bool updateWipWarningLimit,
        long? createdVersionNo,
        long? existingVersionNo,
        CancellationToken cancellationToken)
    {
        var stage = await dbContext.TaskWorkflowStages.FirstOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantId &&
                candidate.ProjectId == projectId &&
                candidate.InternalCategory == category,
            cancellationToken);
        if (stage is null)
        {
            stage = new TaskWorkflowStage
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                DefinitionId = definitionId,
                Name = name,
                InternalCategory = category,
                SortKey = sortKey,
                WipWarningLimit = wipWarningLimit,
                IsInitialStage = isInitial,
                IsTerminalStage = isTerminal
            };
            if (createdVersionNo.HasValue)
            {
                stage.VersionNo = createdVersionNo.Value;
            }

            await dbContext.TaskWorkflowStages.AddAsync(stage, cancellationToken);
            return stage;
        }

        if (!refreshExisting)
        {
            return stage;
        }

        stage.WorkspaceId = workspaceId;
        stage.DefinitionId = definitionId;
        stage.Name = name;
        stage.SortKey = sortKey;
        stage.IsInitialStage = isInitial;
        stage.IsTerminalStage = isTerminal;
        if (updateWipWarningLimit)
        {
            stage.WipWarningLimit = wipWarningLimit;
        }
        if (existingVersionNo.HasValue)
        {
            stage.VersionNo = existingVersionNo.Value;
        }

        return stage;
    }

    private static async Task SeedRadialMenusAsync(
        AppDbContext dbContext,
        IReadOnlyDictionary<string, CommandDefinition> commands,
        Guid tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await SeedProfileAsync(dbContext, commands, "default.project", "Default Project", CommandContextType.Project,
        [
            (RadialMenuDirection.Up, "project.open"),
            (RadialMenuDirection.UpRight, "project.members"),
            (RadialMenuDirection.Right, "dm.open"),
            (RadialMenuDirection.DownRight, "task.addComment"),
            (RadialMenuDirection.Down, "task.create"),
            (RadialMenuDirection.DownLeft, "activityLog.create"),
            (RadialMenuDirection.Left, "artifact.upload"),
            (RadialMenuDirection.UpLeft, "gantt.open")
        ], tenantId, now, cancellationToken);

        await SeedProfileAsync(dbContext, commands, "default.task", "Default Task", CommandContextType.TaskItem,
        [
            (RadialMenuDirection.Up, "task.changeStatus"),
            (RadialMenuDirection.UpRight, "task.assignUser"),
            (RadialMenuDirection.Right, "task.addComment"),
            (RadialMenuDirection.DownRight, "artifact.upload"),
            (RadialMenuDirection.Down, "task.complete"),
            (RadialMenuDirection.DownLeft, "activityLog.create"),
            (RadialMenuDirection.Left, "project.open"),
            (RadialMenuDirection.UpLeft, "gantt.open")
        ], tenantId, now, cancellationToken);
    }

    private static async Task SeedProfileAsync(
        AppDbContext dbContext,
        IReadOnlyDictionary<string, CommandDefinition> commands,
        string key,
        string name,
        CommandContextType context,
        IReadOnlyList<(RadialMenuDirection Direction, string CommandKey)> items,
        Guid tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await dbContext.RadialMenuProfiles.AnyAsync(profile => profile.TenantId == tenantId && profile.ProfileKey == key, cancellationToken))
        {
            return;
        }

        var profile = new RadialMenuProfile
        {
            TenantId = tenantId,
            ProfileKey = key,
            Name = name,
            ContextType = context,
            Scope = context == CommandContextType.Project ? RadialMenuScope.Project : RadialMenuScope.Global,
            IsDefault = true,
            CreatedAt = now
        };

        await dbContext.RadialMenuProfiles.AddAsync(profile, cancellationToken);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            await dbContext.RadialMenuItems.AddAsync(new RadialMenuItem
            {
                TenantId = tenantId,
                RadialMenuProfileId = profile.Id,
                CommandDefinitionId = commands[item.CommandKey].Id,
                CommandKey = item.CommandKey,
                Direction = item.Direction,
                Label = commands[item.CommandKey].Name,
                Icon = commands[item.CommandKey].Icon,
                SortOrder = i
            }, cancellationToken);
        }
    }
}
