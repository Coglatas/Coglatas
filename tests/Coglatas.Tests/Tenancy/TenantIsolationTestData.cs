using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;

namespace Coglatas.Tests.Tenancy;

internal sealed class TenantIsolationTestData
{
    public Tenant TenantA { get; private init; } = null!;
    public Tenant TenantB { get; private init; } = null!;
    public Tenant SuspendedTenant { get; private init; } = null!;

    public User PlatformAdmin { get; private init; } = null!;
    public User TenantAOwner { get; private init; } = null!;
    public User TenantAAdmin { get; private init; } = null!;
    public User TenantAStaff { get; private init; } = null!;
    public User TenantAMember { get; private init; } = null!;
    public User TenantAGuest { get; private init; } = null!;
    public User TenantBOwner { get; private init; } = null!;
    public User TenantBMember { get; private init; } = null!;
    public User CrossTenantUser { get; private init; } = null!;
    public User Outsider { get; private init; } = null!;
    public User SuspendedTenantUser { get; private init; } = null!;

    public Workspace WorkspaceA { get; private init; } = null!;
    public Workspace WorkspaceB { get; private init; } = null!;
    public Group GroupA { get; private init; } = null!;
    public Group GroupB { get; private init; } = null!;
    public Project ProjectA { get; private init; } = null!;
    public Project ProjectB { get; private init; } = null!;
    public TaskItem TaskA { get; private init; } = null!;
    public TaskItem TaskB { get; private init; } = null!;
    public FileObject FileA { get; private init; } = null!;
    public FileObject FileB { get; private init; } = null!;
    public Conversation ConversationA { get; private init; } = null!;
    public Conversation ConversationB { get; private init; } = null!;
    public Message MessageA { get; private init; } = null!;
    public Message MessageB { get; private init; } = null!;
    public Announcement AnnouncementA { get; private init; } = null!;
    public Announcement AnnouncementB { get; private init; } = null!;
    public Notification NotificationB { get; private init; } = null!;

    public static async Task<TenantIsolationTestData> SeedAsync(AppDbContext dbContext, CurrentTenantService currentTenant)
    {
        currentTenant.SetPlatformScope();
        var now = new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero);

        var tenantA = NewTenant("TenantA", "tenant-a");
        var tenantB = NewTenant("TenantB", "tenant-b");
        var suspendedTenant = NewTenant("SuspendedTenant", "suspended-tenant", TenantStatus.Suspended);

        var platformAdmin = NewUser("PlatformAdmin", SystemRole.PlatformAdmin);
        var tenantAOwner = NewUser("TenantAOwner");
        var tenantAAdmin = NewUser("TenantAAdmin");
        var tenantAStaff = NewUser("TenantAStaff");
        var tenantAMember = NewUser("TenantAMember");
        var tenantAGuest = NewUser("TenantAGuest");
        var tenantBOwner = NewUser("TenantBOwner");
        var tenantBMember = NewUser("TenantBMember");
        var crossTenantUser = NewUser("CrossTenantUser");
        var outsider = NewUser("Outsider");
        var suspendedTenantUser = NewUser("SuspendedTenantUser");

        var workspaceA = NewWorkspace(tenantA.Id, "WorkspaceA", "workspace-a", tenantAOwner.Id);
        var workspaceB = NewWorkspace(tenantB.Id, "WorkspaceB", "workspace-b", tenantBOwner.Id);
        var groupA = NewGroup(tenantA.Id, workspaceA.Id, "GroupA", "group-a", tenantAOwner.Id);
        var groupB = NewGroup(tenantB.Id, workspaceB.Id, "GroupB", "group-b", tenantBOwner.Id);
        var projectA = NewProject(tenantA.Id, workspaceA.Id, groupA.Id, "ProjectA", "project-a", tenantAOwner.Id);
        var projectB = NewProject(tenantB.Id, workspaceB.Id, groupB.Id, "ProjectB", "project-b", tenantBOwner.Id);
        var taskA = NewTask(tenantA.Id, workspaceA.Id, projectA.Id, "TaskA", tenantAOwner.Id);
        var taskB = NewTask(tenantB.Id, workspaceB.Id, projectB.Id, "TaskB", tenantBOwner.Id);
        var fileA = NewFile(tenantA.Id, workspaceA.Id, projectA.Id, tenantAOwner.Id);
        var fileB = NewFile(tenantB.Id, workspaceB.Id, projectB.Id, tenantBOwner.Id);
        var conversationA = NewConversation(tenantA.Id, workspaceA.Id, "ConversationA", tenantAOwner.Id);
        var conversationB = NewConversation(tenantB.Id, workspaceB.Id, "ConversationB", tenantBOwner.Id);
        var messageA = NewMessage(tenantA.Id, workspaceA.Id, conversationA.Id, tenantAMember.Id, "TenantA private message body");
        var messageB = NewMessage(tenantB.Id, workspaceB.Id, conversationB.Id, tenantBMember.Id, "TenantB private message body");
        var announcementA = NewAnnouncement(tenantA.Id, workspaceA.Id, groupA.Id, tenantAOwner.Id, "AnnouncementA", now);
        var announcementB = NewAnnouncement(tenantB.Id, workspaceB.Id, groupB.Id, tenantBOwner.Id, "AnnouncementB", now);
        var notificationA = NewNotification(tenantA.Id, tenantAMember.Id, "TenantA notification", now);
        var notificationB = NewNotification(tenantB.Id, tenantBMember.Id, "TenantB notification", now);

        dbContext.Tenants.AddRange(tenantA, tenantB, suspendedTenant);
        dbContext.Users.AddRange(
            platformAdmin,
            tenantAOwner,
            tenantAAdmin,
            tenantAStaff,
            tenantAMember,
            tenantAGuest,
            tenantBOwner,
            tenantBMember,
            crossTenantUser,
            outsider,
            suspendedTenantUser);
        dbContext.TenantUsers.AddRange(
            NewTenantUser(tenantA.Id, tenantAOwner.Id, TenantUserRole.Owner),
            NewTenantUser(tenantA.Id, tenantAAdmin.Id, TenantUserRole.Admin),
            NewTenantUser(tenantA.Id, tenantAStaff.Id, TenantUserRole.Staff),
            NewTenantUser(tenantA.Id, tenantAMember.Id, TenantUserRole.Member),
            NewTenantUser(tenantA.Id, tenantAGuest.Id, TenantUserRole.Guest),
            NewTenantUser(tenantA.Id, crossTenantUser.Id, TenantUserRole.Member),
            NewTenantUser(tenantB.Id, tenantBOwner.Id, TenantUserRole.Owner),
            NewTenantUser(tenantB.Id, tenantBMember.Id, TenantUserRole.Member),
            NewTenantUser(tenantB.Id, crossTenantUser.Id, TenantUserRole.Member),
            NewTenantUser(suspendedTenant.Id, suspendedTenantUser.Id, TenantUserRole.Member));
        dbContext.Workspaces.AddRange(workspaceA, workspaceB);
        dbContext.WorkspaceMembers.AddRange(
            NewWorkspaceMember(tenantA.Id, workspaceA.Id, tenantAOwner.Id, WorkspaceRole.Owner),
            NewWorkspaceMember(tenantA.Id, workspaceA.Id, tenantAAdmin.Id, WorkspaceRole.Admin),
            NewWorkspaceMember(tenantA.Id, workspaceA.Id, tenantAStaff.Id, WorkspaceRole.Adviser),
            NewWorkspaceMember(tenantA.Id, workspaceA.Id, tenantAMember.Id, WorkspaceRole.Member),
            NewWorkspaceMember(tenantA.Id, workspaceA.Id, tenantAGuest.Id, WorkspaceRole.ReadOnly),
            NewWorkspaceMember(tenantA.Id, workspaceA.Id, crossTenantUser.Id, WorkspaceRole.Member),
            NewWorkspaceMember(tenantB.Id, workspaceB.Id, tenantBOwner.Id, WorkspaceRole.Owner),
            NewWorkspaceMember(tenantB.Id, workspaceB.Id, tenantBMember.Id, WorkspaceRole.Member),
            NewWorkspaceMember(tenantB.Id, workspaceB.Id, crossTenantUser.Id, WorkspaceRole.Member));
        dbContext.Groups.AddRange(groupA, groupB);
        dbContext.GroupMembers.AddRange(
            NewGroupMember(tenantA.Id, groupA.Id, tenantAMember.Id),
            NewGroupMember(tenantA.Id, groupA.Id, crossTenantUser.Id),
            NewGroupMember(tenantB.Id, groupB.Id, tenantBMember.Id),
            NewGroupMember(tenantB.Id, groupB.Id, crossTenantUser.Id));
        dbContext.Projects.AddRange(projectA, projectB);
        dbContext.ProjectMembers.AddRange(
            NewProjectMember(tenantA.Id, projectA.Id, tenantAMember.Id),
            NewProjectMember(tenantA.Id, projectA.Id, crossTenantUser.Id),
            NewProjectMember(tenantB.Id, projectB.Id, tenantBMember.Id),
            NewProjectMember(tenantB.Id, projectB.Id, crossTenantUser.Id));
        dbContext.TaskItems.AddRange(taskA, taskB);
        dbContext.FileObjects.AddRange(fileA, fileB);
        dbContext.Attachments.AddRange(
            NewAttachment(tenantA.Id, workspaceA.Id, fileA.Id, taskA.Id, tenantAOwner.Id, fileA.StorageKey),
            NewAttachment(tenantB.Id, workspaceB.Id, fileB.Id, taskB.Id, tenantBOwner.Id, fileB.StorageKey));
        dbContext.Conversations.AddRange(conversationA, conversationB);
        dbContext.ConversationMembers.AddRange(
            NewConversationMember(tenantA.Id, conversationA.Id, tenantAMember.Id),
            NewConversationMember(tenantA.Id, conversationA.Id, crossTenantUser.Id),
            NewConversationMember(tenantB.Id, conversationB.Id, tenantBMember.Id),
            NewConversationMember(tenantB.Id, conversationB.Id, crossTenantUser.Id));
        dbContext.Messages.AddRange(messageA, messageB);
        dbContext.Announcements.AddRange(announcementA, announcementB);
        dbContext.Notifications.AddRange(notificationA, notificationB);
        dbContext.AuditLogs.AddRange(
            NewAuditLog(tenantA.Id, tenantAAdmin.Id, workspaceA.Id, "TenantA audit", now),
            NewAuditLog(tenantB.Id, tenantBOwner.Id, workspaceB.Id, "TenantB audit", now));
        dbContext.SecurityEvents.AddRange(
            NewSecurityEvent(tenantA.Id, tenantAMember.Id, "TenantA denied", now),
            NewSecurityEvent(tenantB.Id, tenantBMember.Id, "TenantB denied", now));
        dbContext.TenantSettings.AddRange(
            new TenantSettings
            {
                TenantId = tenantA.Id,
                DisplayName = "TenantA",
                StorageQuotaBytes = 100,
                FileUploadLimitBytes = 50,
                ProjectLimit = 1,
                FeatureFlagsJson = """{"ProductionTracking":false,"FileSharing":false}""",
                NotificationSettingsJson = "{}"
            },
            new TenantSettings
            {
                TenantId = tenantB.Id,
                DisplayName = "TenantB",
                StorageQuotaBytes = 10_000,
                FileUploadLimitBytes = 1_000,
                ProjectLimit = 10,
                FeatureFlagsJson = """{"ProductionTracking":true,"FileSharing":true}""",
                NotificationSettingsJson = "{}"
            });
        dbContext.UsageRecords.AddRange(
            new UsageRecord { TenantId = tenantA.Id, Date = DateOnly.FromDateTime(now.UtcDateTime), ProjectCount = 1, StorageUsedBytes = 90, FileCount = 1, CreatedAt = now },
            new UsageRecord { TenantId = tenantB.Id, Date = DateOnly.FromDateTime(now.UtcDateTime), ProjectCount = 1, StorageUsedBytes = 10, FileCount = 1, CreatedAt = now });

        await dbContext.SaveChangesAsync();

        return new TenantIsolationTestData
        {
            TenantA = tenantA,
            TenantB = tenantB,
            SuspendedTenant = suspendedTenant,
            PlatformAdmin = platformAdmin,
            TenantAOwner = tenantAOwner,
            TenantAAdmin = tenantAAdmin,
            TenantAStaff = tenantAStaff,
            TenantAMember = tenantAMember,
            TenantAGuest = tenantAGuest,
            TenantBOwner = tenantBOwner,
            TenantBMember = tenantBMember,
            CrossTenantUser = crossTenantUser,
            Outsider = outsider,
            SuspendedTenantUser = suspendedTenantUser,
            WorkspaceA = workspaceA,
            WorkspaceB = workspaceB,
            GroupA = groupA,
            GroupB = groupB,
            ProjectA = projectA,
            ProjectB = projectB,
            TaskA = taskA,
            TaskB = taskB,
            FileA = fileA,
            FileB = fileB,
            ConversationA = conversationA,
            ConversationB = conversationB,
            MessageA = messageA,
            MessageB = messageB,
            AnnouncementA = announcementA,
            AnnouncementB = announcementB,
            NotificationB = notificationB
        };
    }

    private static Tenant NewTenant(string name, string slug, TenantStatus status = TenantStatus.Active) => new()
    {
        Name = name,
        Slug = slug,
        DisplayName = name,
        Status = status
    };

    private static User NewUser(string name, SystemRole role = SystemRole.User) => new()
    {
        DisplayName = name,
        Email = $"{name.ToLowerInvariant()}@example.test",
        NormalizedEmail = $"{name.ToUpperInvariant()}@EXAMPLE.TEST",
        PasswordHash = "hash",
        SystemRole = role,
        Status = UserStatus.Active
    };

    private static TenantUser NewTenantUser(Guid tenantId, Guid userId, TenantUserRole role) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = TenantUserStatus.Active,
        JoinedAt = DateTimeOffset.UtcNow
    };

    private static Workspace NewWorkspace(Guid tenantId, string name, string slug, Guid createdByUserId) => new()
    {
        TenantId = tenantId,
        Name = name,
        Slug = slug,
        CreatedByUserId = createdByUserId,
        Status = WorkspaceStatus.Active
    };

    private static WorkspaceMember NewWorkspaceMember(Guid tenantId, Guid workspaceId, Guid userId, WorkspaceRole role) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        UserId = userId,
        Role = role,
        Status = MembershipStatus.Active,
        JoinedAt = DateTimeOffset.UtcNow
    };

    private static Group NewGroup(Guid tenantId, Guid workspaceId, string name, string slug, Guid createdByUserId) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        Name = name,
        Slug = slug,
        CreatedByUserId = createdByUserId
    };

    private static GroupMember NewGroupMember(Guid tenantId, Guid groupId, Guid userId) => new()
    {
        TenantId = tenantId,
        GroupId = groupId,
        UserId = userId,
        Role = GroupRole.Member,
        JoinedAt = DateTimeOffset.UtcNow
    };

    private static Project NewProject(Guid tenantId, Guid workspaceId, Guid groupId, string name, string slug, Guid userId) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        GroupId = groupId,
        OwnerUserId = userId,
        CreatedByUserId = userId,
        Name = name,
        Slug = slug,
        Status = ProjectStatus.Active,
        ActivationState = ProjectActivationState.Activated,
        ActivatedAtUtc = new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero),
        ActivationVersion = 1
    };

    private static ProjectMember NewProjectMember(Guid tenantId, Guid projectId, Guid userId) => new()
    {
        TenantId = tenantId,
        ProjectId = projectId,
        UserId = userId,
        Role = ProjectRole.Contributor,
        JoinedAt = DateTimeOffset.UtcNow
    };

    private static TaskItem NewTask(Guid tenantId, Guid workspaceId, Guid projectId, string title, Guid userId) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        ProjectId = projectId,
        Title = title,
        CreatedByUserId = userId,
        Status = TaskItemStatus.NotStarted
    };

    private static FileObject NewFile(Guid tenantId, Guid workspaceId, Guid projectId, Guid userId)
    {
        var file = new FileObject
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            UploadedByUserId = userId,
            OriginalFileName = $"{projectId:N}.txt",
            ContentType = "text/plain",
            SizeBytes = 10,
            Classification = DataClassification.Private,
            Status = FileObjectStatus.Active
        };
        file.StorageKey = $"tenants/{tenantId:D}/projects/{projectId:D}/files/{file.Id:D}";
        return file;
    }

    private static Attachment NewAttachment(Guid tenantId, Guid workspaceId, Guid fileObjectId, Guid ownerId, Guid userId, string storageKey) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        FileObjectId = fileObjectId,
        OwnerType = AttachmentOwnerType.TaskItem,
        OwnerId = ownerId,
        OwnerUserId = userId,
        UploadedByUserId = userId,
        FileName = "file.txt",
        StoredFileName = fileObjectId.ToString("N"),
        FilePath = storageKey,
        StorageKey = storageKey,
        ContentType = "text/plain",
        Extension = ".txt",
        SizeBytes = 10,
        StorageProvider = "Test",
        ScanStatus = FileScanStatus.Clean
    };

    private static Conversation NewConversation(Guid tenantId, Guid workspaceId, string title, Guid userId) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        Title = title,
        Type = ConversationType.DirectMessage,
        CreatedByUserId = userId
    };

    private static ConversationMember NewConversationMember(Guid tenantId, Guid conversationId, Guid userId) => new()
    {
        TenantId = tenantId,
        ConversationId = conversationId,
        UserId = userId,
        JoinedAt = DateTimeOffset.UtcNow
    };

    private static Message NewMessage(Guid tenantId, Guid workspaceId, Guid conversationId, Guid authorUserId, string body) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        ConversationId = conversationId,
        AuthorUserId = authorUserId,
        Body = body
    };

    private static Announcement NewAnnouncement(Guid tenantId, Guid workspaceId, Guid groupId, Guid userId, string title, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        GroupId = groupId,
        AuthorUserId = userId,
        Title = title,
        Body = title,
        PublishedAt = now
    };

    private static Notification NewNotification(Guid tenantId, Guid userId, string title, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        NotificationType = NotificationType.System,
        Title = title,
        CreatedAt = now
    };

    private static AuditLog NewAuditLog(Guid tenantId, Guid userId, Guid workspaceId, string action, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        ActorUserId = userId,
        WorkspaceId = workspaceId,
        Action = action,
        EntityType = "Workspace",
        EntityId = workspaceId,
        CreatedAt = now
    };

    private static SecurityEvent NewSecurityEvent(Guid tenantId, Guid userId, string summary, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        EventType = SecurityEventType.AccessDenied,
        Severity = SecurityEventSeverity.Warning,
        Summary = summary,
        CreatedAt = now
    };
}
