using System.Security.Cryptography;
using System.Text;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Deterministic synthetic data for the explicitly opted-in SEC-02 Test stack.
/// Distinct tenant/resource canaries let later API and DAST checks prove both
/// authorization and non-disclosure boundaries without using real user data.
/// </summary>
public static class SecurityCiFixtureSeed
{
    public const string TenantASlug = "security-alpha";
    public const string TenantBSlug = "security-beta";

    public const string TenantAOwnerEmail = "security-alpha-owner@example.test";
    public const string TenantAMemberEmail = "security-alpha-member@example.test";
    public const string TenantARestrictedEmail = "security-alpha-restricted@example.test";
    public const string TenantBOwnerEmail = "security-beta-owner@example.test";

    public static readonly Guid TenantAOwnerUserId = new("a11fa001-aaaa-4aaa-8aaa-aaaaaaaaa001");
    public static readonly Guid TenantAMemberUserId = new("a11fa002-aaaa-4aaa-8aaa-aaaaaaaaa002");
    public static readonly Guid TenantARestrictedUserId = new("a11fa003-aaaa-4aaa-8aaa-aaaaaaaaa003");
    public static readonly Guid TenantBOwnerUserId = new("be7a0001-bbbb-4bbb-8bbb-bbbbbbbbb001");

    public const string TenantAWorkspaceSlug = "sec02-alpha-workspace";
    public const string TenantBWorkspaceSlug = "sec02-beta-workspace";
    public const string TenantAProjectSlug = "sec02-alpha-project";
    public const string TenantBProjectSlug = "sec02-beta-project";

    public const string TenantATaskTitle = "SEC02 ALPHA PRIVATE TASK CANARY";
    public const string TenantBTaskTitle = "SEC02 BETA PRIVATE TASK CANARY";
    private const string TenantAFileName = "sec02-alpha-private.txt";
    private const string TenantBFileName = "sec02-beta-private.txt";
    private const string TenantAConversationTitle = "SEC02 ALPHA PRIVATE CONVERSATION CANARY";
    private const string TenantBConversationTitle = "SEC02 BETA PRIVATE CONVERSATION CANARY";

    private const string TenantAFileBody = "SEC02_ALPHA_FILE_CANARY_DO_NOT_LEAK\n";
    private const string TenantBFileBody = "SEC02_BETA_FILE_CANARY_DO_NOT_LEAK\n";
    private const string TenantAMessageBody = "SEC02_ALPHA_MESSAGE_CANARY_DO_NOT_LEAK";
    private const string TenantBMessageBody = "SEC02_BETA_MESSAGE_CANARY_DO_NOT_LEAK";

    public static async Task SeedAsync(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        IFileStorageService fileStorage,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(fileStorage);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var now = DateTimeOffset.UtcNow;

        var tenantA = await EnsureTenantAsync(
            dbContext,
            TenantASlug,
            "SEC-02 Security Alpha Tenant",
            cancellationToken);
        var tenantB = await EnsureTenantAsync(
            dbContext,
            TenantBSlug,
            "SEC-02 Security Beta Tenant",
            cancellationToken);

        var alphaOwner = await EnsureUserAsync(
            dbContext,
            passwordHasher,
            TenantAOwnerUserId,
            TenantAOwnerEmail,
            "SEC-02 Alpha Owner",
            password,
            cancellationToken);
        var alphaMember = await EnsureUserAsync(
            dbContext,
            passwordHasher,
            TenantAMemberUserId,
            TenantAMemberEmail,
            "SEC-02 Alpha Member",
            password,
            cancellationToken);
        var alphaRestricted = await EnsureUserAsync(
            dbContext,
            passwordHasher,
            TenantARestrictedUserId,
            TenantARestrictedEmail,
            "SEC-02 Alpha Restricted",
            password,
            cancellationToken);
        var betaOwner = await EnsureUserAsync(
            dbContext,
            passwordHasher,
            TenantBOwnerUserId,
            TenantBOwnerEmail,
            "SEC-02 Beta Owner",
            password,
            cancellationToken);

        // Persist the identities before building the resource graph. This keeps
        // the second, idempotence pass identical across relational and in-memory
        // providers and makes uniqueness failures fail the startup immediately.
        await dbContext.SaveChangesAsync(cancellationToken);

        await EnsureTenantUserAsync(dbContext, tenantA.Id, alphaOwner.Id, TenantUserRole.Owner, now, cancellationToken);
        await EnsureTenantUserAsync(dbContext, tenantA.Id, alphaMember.Id, TenantUserRole.Member, now, cancellationToken);
        await EnsureTenantUserAsync(dbContext, tenantA.Id, alphaRestricted.Id, TenantUserRole.Guest, now, cancellationToken);
        await EnsureTenantUserAsync(dbContext, tenantB.Id, betaOwner.Id, TenantUserRole.Owner, now, cancellationToken);

        await EnsureTenantGraphAsync(
            dbContext,
            fileStorage,
            tenantA,
            new FixtureActor(alphaOwner, WorkspaceRole.Owner, ProjectRole.Owner, ConversationMemberRole.Admin, true),
            [
                new FixtureActor(alphaMember, WorkspaceRole.Member, ProjectRole.Contributor, ConversationMemberRole.Member, true),
                new FixtureActor(alphaRestricted, WorkspaceRole.ReadOnly, ProjectRole.Viewer, ConversationMemberRole.ReadOnly, false)
            ],
            TenantAWorkspaceSlug,
            "SEC-02 Alpha Workspace Canary",
            TenantAProjectSlug,
            "SEC-02 Alpha Private Project Canary",
            TenantATaskTitle,
            TenantAFileName,
            TenantAFileBody,
            TenantAConversationTitle,
            TenantAMessageBody,
            now,
            cancellationToken);

        await EnsureTenantGraphAsync(
            dbContext,
            fileStorage,
            tenantB,
            new FixtureActor(betaOwner, WorkspaceRole.Owner, ProjectRole.Owner, ConversationMemberRole.Admin, true),
            [],
            TenantBWorkspaceSlug,
            "SEC-02 Beta Workspace Canary",
            TenantBProjectSlug,
            "SEC-02 Beta Private Project Canary",
            TenantBTaskTitle,
            TenantBFileName,
            TenantBFileBody,
            TenantBConversationTitle,
            TenantBMessageBody,
            now,
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Tenant> EnsureTenantAsync(
        AppDbContext dbContext,
        string slug,
        string displayName,
        CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Slug == slug, cancellationToken);

        if (tenant is null)
        {
            tenant = new Tenant
            {
                Name = displayName,
                DisplayName = displayName,
                Slug = slug,
                Status = TenantStatus.Active
            };
            await dbContext.Tenants.AddAsync(tenant, cancellationToken);
            return tenant;
        }

        tenant.Name = displayName;
        tenant.DisplayName = displayName;
        tenant.Status = TenantStatus.Active;
        if (tenant.IsDeleted)
        {
            tenant.Restore();
        }

        return tenant;
    }

    private static async Task<User> EnsureUserAsync(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        Guid expectedUserId,
        string email,
        string displayName,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user is null)
        {
            user = new User
            {
                DisplayName = displayName,
                Email = email,
                NormalizedEmail = normalizedEmail,
                PasswordHash = passwordHasher.HashPassword(password),
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            };
            dbContext.Entry(user).Property(candidate => candidate.Id).CurrentValue = expectedUserId;
            await dbContext.Users.AddAsync(user, cancellationToken);
            return user;
        }

        if (user.Id != expectedUserId)
        {
            throw new InvalidOperationException(
                $"SEC-02 fixture user '{email}' drifted from its deterministic identity.");
        }

        user.DisplayName = displayName;
        user.Email = email;
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

        return user;
    }

    private static async Task EnsureTenantUserAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid userId,
        TenantUserRole role,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var membership = await dbContext.TenantUsers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.UserId == userId,
            cancellationToken);

        if (membership is null)
        {
            await dbContext.TenantUsers.AddAsync(new TenantUser
            {
                TenantId = tenantId,
                UserId = userId,
                Role = role,
                Status = TenantUserStatus.Active,
                JoinedAt = now
            }, cancellationToken);
            return;
        }

        membership.Role = role;
        membership.Status = TenantUserStatus.Active;
        if (membership.JoinedAt == default)
        {
            membership.JoinedAt = now;
        }
    }

    private static async Task EnsureTenantGraphAsync(
        AppDbContext dbContext,
        IFileStorageService fileStorage,
        Tenant tenant,
        FixtureActor owner,
        IReadOnlyCollection<FixtureActor> participants,
        string workspaceSlug,
        string workspaceName,
        string projectSlug,
        string projectName,
        string taskTitle,
        string fileName,
        string fileBody,
        string conversationTitle,
        string messageBody,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var workspace = await dbContext.Workspaces
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenant.Id && candidate.Slug == workspaceSlug,
                cancellationToken);

        if (workspace is null)
        {
            workspace = new Workspace
            {
                TenantId = tenant.Id,
                Name = workspaceName,
                Slug = workspaceSlug,
                Description = "Synthetic SEC-02 workspace. Security CI only.",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = owner.User.Id
            };
            await dbContext.Workspaces.AddAsync(workspace, cancellationToken);
        }
        else
        {
            workspace.Name = workspaceName;
            workspace.Description = "Synthetic SEC-02 workspace. Security CI only.";
            workspace.Status = WorkspaceStatus.Active;
            workspace.CreatedByUserId = owner.User.Id;
            if (workspace.IsDeleted)
            {
                workspace.Restore();
            }
        }

        await EnsureWorkspaceMemberAsync(dbContext, tenant.Id, workspace.Id, owner, now, cancellationToken);
        foreach (var participant in participants)
        {
            await EnsureWorkspaceMemberAsync(dbContext, tenant.Id, workspace.Id, participant, now, cancellationToken);
        }

        var project = await dbContext.Projects
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenant.Id &&
                             candidate.WorkspaceId == workspace.Id &&
                             candidate.Slug == projectSlug,
                cancellationToken);

        if (project is null)
        {
            project = new Project
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                OwnerUserId = owner.User.Id,
                CreatedByUserId = owner.User.Id,
                Name = projectName,
                Slug = projectSlug,
                Description = "Synthetic members-only SEC-02 project. Security CI only.",
                Visibility = ProjectVisibility.MembersOnly,
                Status = ProjectStatus.Active,
                ActivationState = ProjectActivationState.Activated,
                ActivatedAtUtc = now,
                ActivationVersion = 1,
                VersionNo = 1,
                StartDate = DateOnly.FromDateTime(now.UtcDateTime.Date),
                DueDate = DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(7))
            };
            await dbContext.Projects.AddAsync(project, cancellationToken);
        }
        else
        {
            if (project.IsDeleted ||
                project.Status != ProjectStatus.Active ||
                project.ActivationState != ProjectActivationState.Activated ||
                !project.ActivatedAtUtc.HasValue ||
                project.ActivationVersion is not > 0)
            {
                throw new InvalidOperationException(
                    $"SEC-02 fixture project '{projectSlug}' drifted from its required activated state.");
            }

            project.OwnerUserId = owner.User.Id;
            project.CreatedByUserId = owner.User.Id;
            project.Name = projectName;
            project.Description = "Synthetic members-only SEC-02 project. Security CI only.";
            project.Visibility = ProjectVisibility.MembersOnly;
            project.VersionNo = Math.Max(1, project.VersionNo);
        }

        await EnsureProjectMemberAsync(dbContext, tenant.Id, project.Id, owner, now, cancellationToken);
        foreach (var participant in participants)
        {
            await EnsureProjectMemberAsync(dbContext, tenant.Id, project.Id, participant, now, cancellationToken);
        }

        var task = await dbContext.TaskItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenant.Id &&
                             candidate.ProjectId == project.Id &&
                             candidate.Title == taskTitle,
                cancellationToken);

        if (task is null)
        {
            task = new TaskItem
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Title = taskTitle,
                Description = $"Synthetic private Task canary for tenant '{tenant.Slug}'.",
                Status = TaskItemStatus.NotStarted,
                Priority = TaskPriority.Medium,
                StartDate = project.StartDate,
                DueDate = project.DueDate,
                PrimaryAssigneeUserId = owner.User.Id,
                CreatedByUserId = owner.User.Id,
                VersionNo = 1
            };
            await dbContext.TaskItems.AddAsync(task, cancellationToken);
        }
        else
        {
            task.WorkspaceId = workspace.Id;
            task.Description = $"Synthetic private Task canary for tenant '{tenant.Slug}'.";
            task.Status = TaskItemStatus.NotStarted;
            task.PrimaryAssigneeUserId = owner.User.Id;
            task.CreatedByUserId = owner.User.Id;
            task.VersionNo = Math.Max(1, task.VersionNo);
            if (task.IsDeleted)
            {
                task.Restore();
            }
        }

        var fileBytes = Encoding.UTF8.GetBytes(fileBody);
        var storageKey = $"tenants/{tenant.Id:D}/security-ci/{fileName}";
        await using (var fileStream = new MemoryStream(fileBytes, writable: false))
        {
            var storageResult = await fileStorage.SaveAsync(storageKey, fileStream, "text/plain", cancellationToken);
            if (!storageResult.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"SEC-02 fixture file storage failed for tenant '{tenant.Slug}': {storageResult.Error}");
            }
        }

        var file = await dbContext.FileObjects
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenant.Id && candidate.StorageKey == storageKey,
                cancellationToken);

        if (file is null)
        {
            file = new FileObject
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                UploadedByUserId = owner.User.Id,
                OriginalFileName = fileName,
                StorageKey = storageKey,
                ContentType = "text/plain",
                SizeBytes = fileBytes.LongLength,
                HashSha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant(),
                Classification = DataClassification.Private,
                SharingPolicy = FileSharingPolicy.Private,
                SharingVersion = 1,
                Status = FileObjectStatus.Active
            };
            await dbContext.FileObjects.AddAsync(file, cancellationToken);
        }
        else
        {
            if (file.DeletedAt.HasValue)
            {
                throw new InvalidOperationException(
                    $"SEC-02 fixture file '{storageKey}' was deleted and cannot be silently restored.");
            }

            file.WorkspaceId = workspace.Id;
            file.ProjectId = project.Id;
            file.UploadedByUserId = owner.User.Id;
            file.OriginalFileName = fileName;
            file.ContentType = "text/plain";
            file.SizeBytes = fileBytes.LongLength;
            file.HashSha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
            file.Classification = DataClassification.Private;
            file.SharingPolicy = FileSharingPolicy.Private;
            file.SharingVersion = Math.Max(1, file.SharingVersion);
            file.Status = FileObjectStatus.Active;
        }

        var attachment = await dbContext.Attachments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenant.Id &&
                             candidate.FileObjectId == file.Id &&
                             candidate.OwnerType == AttachmentOwnerType.TaskItem &&
                             candidate.OwnerId == task.Id,
                cancellationToken);

        if (attachment is null)
        {
            attachment = new Attachment
            {
                TenantId = tenant.Id,
                FileObjectId = file.Id,
                WorkspaceId = workspace.Id,
                OwnerType = AttachmentOwnerType.TaskItem,
                OwnerId = task.Id,
                OwnerUserId = owner.User.Id,
                UploadedByUserId = owner.User.Id,
                FileName = fileName,
                StoredFileName = fileName,
                FilePath = "security-ci/internal/task-file",
                ContentType = "text/plain",
                Extension = ".txt",
                SizeBytes = fileBytes.LongLength,
                StorageProvider = "LocalFileSystem",
                StorageKey = storageKey,
                ScanStatus = FileScanStatus.Clean
            };
            await dbContext.Attachments.AddAsync(attachment, cancellationToken);
        }
        else
        {
            attachment.WorkspaceId = workspace.Id;
            attachment.OwnerUserId = owner.User.Id;
            attachment.UploadedByUserId = owner.User.Id;
            attachment.FileName = fileName;
            attachment.StoredFileName = fileName;
            attachment.FilePath = "security-ci/internal/task-file";
            attachment.ContentType = "text/plain";
            attachment.Extension = ".txt";
            attachment.SizeBytes = fileBytes.LongLength;
            attachment.StorageProvider = "LocalFileSystem";
            attachment.StorageKey = storageKey;
            attachment.ScanStatus = FileScanStatus.Clean;
            if (attachment.IsDeleted)
            {
                attachment.Restore();
            }
        }

        var conversation = await dbContext.Conversations.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenant.Id &&
                         candidate.WorkspaceId == workspace.Id &&
                         candidate.ProjectId == project.Id &&
                         candidate.Title == conversationTitle,
            cancellationToken);

        if (conversation is null)
        {
            conversation = new Conversation
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Type = ConversationType.ProjectLinked,
                Title = conversationTitle,
                IsArchived = false,
                IsLocked = false,
                CreatedByUserId = owner.User.Id
            };
            await dbContext.Conversations.AddAsync(conversation, cancellationToken);
        }
        else
        {
            conversation.Type = ConversationType.ProjectLinked;
            conversation.IsArchived = false;
            conversation.IsLocked = false;
            conversation.CreatedByUserId = owner.User.Id;
        }

        await EnsureConversationMemberAsync(dbContext, tenant.Id, conversation.Id, owner, now, cancellationToken);
        foreach (var participant in participants)
        {
            await EnsureConversationMemberAsync(dbContext, tenant.Id, conversation.Id, participant, now, cancellationToken);
        }

        var message = await dbContext.Messages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenant.Id &&
                             candidate.ConversationId == conversation.Id &&
                             candidate.Body == messageBody,
                cancellationToken);

        if (message is null)
        {
            await dbContext.Messages.AddAsync(new Message
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ConversationId = conversation.Id,
                AuthorUserId = owner.User.Id,
                Body = messageBody,
                Version = 1
            }, cancellationToken);
        }
        else
        {
            message.WorkspaceId = workspace.Id;
            message.AuthorUserId = owner.User.Id;
            message.Version = Math.Max(1, message.Version);
            if (message.IsDeleted)
            {
                message.Restore();
            }
        }
    }

    private static async Task EnsureWorkspaceMemberAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid workspaceId,
        FixtureActor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var membership = await dbContext.WorkspaceMembers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId &&
                         candidate.WorkspaceId == workspaceId &&
                         candidate.UserId == actor.User.Id,
            cancellationToken);

        if (membership is null)
        {
            await dbContext.WorkspaceMembers.AddAsync(new WorkspaceMember
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                UserId = actor.User.Id,
                Role = actor.WorkspaceRole,
                Status = MembershipStatus.Active,
                JoinedAt = now
            }, cancellationToken);
            return;
        }

        membership.Role = actor.WorkspaceRole;
        membership.Status = MembershipStatus.Active;
        membership.JoinedAt ??= now;
    }

    private static async Task EnsureProjectMemberAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid projectId,
        FixtureActor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var membership = await dbContext.ProjectMembers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId &&
                         candidate.ProjectId == projectId &&
                         candidate.UserId == actor.User.Id,
            cancellationToken);

        if (membership is null)
        {
            await dbContext.ProjectMembers.AddAsync(new ProjectMember
            {
                TenantId = tenantId,
                ProjectId = projectId,
                UserId = actor.User.Id,
                Role = actor.ProjectRole,
                JoinedAt = now
            }, cancellationToken);
            return;
        }

        membership.Role = actor.ProjectRole;
        if (membership.JoinedAt == default)
        {
            membership.JoinedAt = now;
        }
    }

    private static async Task EnsureConversationMemberAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid conversationId,
        FixtureActor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var membership = await dbContext.ConversationMembers.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId &&
                         candidate.ConversationId == conversationId &&
                         candidate.UserId == actor.User.Id,
            cancellationToken);

        if (membership is null)
        {
            await dbContext.ConversationMembers.AddAsync(new ConversationMember
            {
                TenantId = tenantId,
                ConversationId = conversationId,
                UserId = actor.User.Id,
                Role = actor.ConversationRole,
                CanRead = true,
                CanPost = actor.CanPost,
                CanManageMembers = actor.ConversationRole == ConversationMemberRole.Admin,
                CanCreateThread = actor.CanPost,
                JoinedAt = now
            }, cancellationToken);
            return;
        }

        membership.Role = actor.ConversationRole;
        membership.CanRead = true;
        membership.CanPost = actor.CanPost;
        membership.CanManageMembers = actor.ConversationRole == ConversationMemberRole.Admin;
        membership.CanCreateThread = actor.CanPost;
        membership.LeftAt = null;
        membership.RemovedAt = null;
        membership.RemovedByUserId = null;
        if (membership.JoinedAt == default)
        {
            membership.JoinedAt = now;
        }
    }

    private sealed record FixtureActor(
        User User,
        WorkspaceRole WorkspaceRole,
        ProjectRole ProjectRole,
        ConversationMemberRole ConversationRole,
        bool CanPost);
}
