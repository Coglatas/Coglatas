using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

[Collection("PostgreSqlTaskV1")]
[Trait("Category", "PostgreSQLIntegration")]
[Trait("Scope", "WPC02F")]
public sealed class Wpc02FNotificationNavigationPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 3, 40, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task ArtifactAndMessageOpenUseCurrentAuthorizedCanonicalNavigationAndUnavailableTargetStaysUnread()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var suffix = Guid.NewGuid().ToString("N");
            var tenant = new Tenant
            {
                Name = $"WPC-02F tenant {suffix}",
                DisplayName = "WPC-02F tenant",
                Slug = $"wpc02f-{suffix}",
                Status = TenantStatus.Active
            };
            var recipient = new User
            {
                DisplayName = "WPC-02F recipient",
                Email = $"wpc02f-{suffix}@example.invalid",
                NormalizedEmail = $"WPC02F-{suffix}@EXAMPLE.INVALID",
                PasswordHash = "hash",
                Status = UserStatus.Active
            };

            await using (var platform = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database))
            {
                platform.AddRange(tenant, recipient);
                await platform.SaveChangesAsync();
            }

            var workspace = new Workspace
            {
                TenantId = tenant.Id,
                Name = "Navigation workspace",
                Slug = $"navigation-{suffix}",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = recipient.Id
            };
            var project = new Project
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                Name = "Navigation project",
                Slug = $"navigation-project-{suffix}",
                Status = ProjectStatus.Active,
                OwnerUserId = recipient.Id,
                CreatedByUserId = recipient.Id
            };
            var artifact = new Artifact
            {
                TenantId = tenant.Id,
                ProjectId = project.Id,
                Name = "Authorized artifact",
                CreatedByUserId = recipient.Id
            };
            var artifactNotification = new Notification
            {
                TenantId = tenant.Id,
                UserId = recipient.Id,
                NotificationType = NotificationType.ArtifactUploaded,
                Title = "Artifact uploaded",
                RelatedEntityType = "Artifact",
                RelatedEntityId = artifact.Id,
                CreatedAt = Now,
                StateVersion = 4
            };
            var conversation = new Conversation
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Type = ConversationType.ProjectChannel,
                Title = "Authorized conversation",
                CreatedByUserId = recipient.Id
            };
            var message = new Message
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ConversationId = conversation.Id,
                AuthorUserId = recipient.Id,
                Body = "Authorized message"
            };
            var messageNotification = new Notification
            {
                TenantId = tenant.Id,
                UserId = recipient.Id,
                NotificationType = NotificationType.DirectMessage,
                Title = "New message",
                RelatedEntityType = "Message",
                RelatedEntityId = message.Id,
                CreatedAt = Now,
                StateVersion = 4
            };
            var deletedArtifact = new Artifact
            {
                TenantId = tenant.Id,
                ProjectId = project.Id,
                Name = "Deleted artifact",
                CreatedByUserId = recipient.Id
            };
            deletedArtifact.MarkDeleted(Now);
            var deletedArtifactNotification = new Notification
            {
                TenantId = tenant.Id,
                UserId = recipient.Id,
                NotificationType = NotificationType.ArtifactUploaded,
                Title = "Deleted artifact notification",
                RelatedEntityType = "Artifact",
                RelatedEntityId = deletedArtifact.Id,
                CreatedAt = Now,
                StateVersion = 4
            };

            await using (var seed = CreateTenantContext(database, tenant))
            {
                seed.AddRange(
                    new TenantUser
                    {
                        TenantId = tenant.Id,
                        UserId = recipient.Id,
                        Status = TenantUserStatus.Active,
                        JoinedAt = Now
                    },
                    workspace,
                    new WorkspaceMember
                    {
                        TenantId = tenant.Id,
                        WorkspaceId = workspace.Id,
                        UserId = recipient.Id,
                        Status = MembershipStatus.Active,
                        Role = WorkspaceRole.Member
                    },
                    project,
                    artifact,
                    artifactNotification,
                    conversation,
                    new ConversationMember
                    {
                        TenantId = tenant.Id,
                        ConversationId = conversation.Id,
                        UserId = recipient.Id,
                        JoinedAt = Now
                    },
                    message,
                    messageNotification,
                    deletedArtifact,
                    deletedArtifactNotification,
                    new NotificationUserState
                    {
                        TenantId = tenant.Id,
                        UserId = recipient.Id,
                        Version = 4,
                        UpdatedAt = Now
                    });
                await seed.SaveChangesAsync();
            }

            var currentTenant = TenantScope(tenant);
            await using (var openContext = CreateTenantContext(database, tenant))
            {
                var currentAuthorization = new CurrentAuthorizationTargetResolver(
                    openContext,
                    currentTenant,
                    new MessagingRepository(openContext));
                var navigation = new NotificationNavigationTargetResolver(openContext, currentAuthorization);
                var outbox = new TransactionalOutbox(
                    new OutboxEventRepository(openContext),
                    currentTenant,
                    new FixedClock());
                var notifications = new DbNotificationService(
                    openContext,
                    new FixedClock(),
                    currentTenant,
                    targets: navigation);
                var service = new NotificationOpenService(
                    openContext,
                    currentTenant,
                    new FixedClock(),
                    outbox,
                    navigation,
                    notifications);

                var artifactResult = await service.OpenAsync(
                    tenant.Id,
                    recipient.Id,
                    artifactNotification.Id);
                Assert.True(artifactResult.IsOwned);
                Assert.True(artifactResult.IsAvailable);
                Assert.Equal($"/artifacts/{artifact.Id}", artifactResult.Route);
                Assert.Equal(workspace.Id, artifactResult.WorkspaceId);

                var messageResult = await service.OpenAsync(
                    tenant.Id,
                    recipient.Id,
                    messageNotification.Id);
                Assert.True(messageResult.IsOwned);
                Assert.True(messageResult.IsAvailable);
                Assert.Equal(
                    $"/conversations/{conversation.Id}?messageId={message.Id}",
                    messageResult.Route);
                Assert.Equal(workspace.Id, messageResult.WorkspaceId);

                var unavailableResult = await service.OpenAsync(
                    tenant.Id,
                    recipient.Id,
                    deletedArtifactNotification.Id);
                Assert.True(unavailableResult.IsOwned);
                Assert.False(unavailableResult.IsAvailable);
                Assert.Null(unavailableResult.Route);
                Assert.Null(unavailableResult.WorkspaceId);
            }

            await using var verify = CreateTenantContext(database, tenant);
            Assert.True((await verify.Notifications.SingleAsync(item => item.Id == artifactNotification.Id)).IsRead);
            Assert.True((await verify.Notifications.SingleAsync(item => item.Id == messageNotification.Id)).IsRead);
            Assert.False((await verify.Notifications.SingleAsync(item => item.Id == deletedArtifactNotification.Id)).IsRead);
            Assert.Equal(
                2,
                await verify.OutboxEvents.CountAsync(item => item.EventType == "Notifications.NotificationReadStateChanged.v1"));
        });
    }

    private static AppDbContext CreateTenantContext(string connectionString, Tenant tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, TenantScope(tenant));

    private static CurrentTenantService TenantScope(Tenant tenant)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        return currentTenant;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
