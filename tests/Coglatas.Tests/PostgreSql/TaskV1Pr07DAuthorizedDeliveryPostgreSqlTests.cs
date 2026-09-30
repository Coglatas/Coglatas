using System.Text.Json;
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
[Trait("Scope", "TaskV1PR07D")]
public sealed class TaskV1Pr07DAuthorizedDeliveryPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task OpenTaskNotificationCommitsReadStateAndRecipientOnlyOutboxAtomically()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var suffix = Guid.NewGuid().ToString("N");
            var tenant = new Tenant
            {
                Name = $"PR07-D tenant {suffix}",
                DisplayName = "PR07-D tenant",
                Slug = $"pr07d-{suffix}",
                Status = TenantStatus.Active
            };
            var recipient = new User
            {
                DisplayName = "PR07-D recipient",
                Email = $"pr07d-{suffix}@example.invalid",
                NormalizedEmail = $"PR07D-{suffix}@EXAMPLE.INVALID",
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
                Name = "Workspace",
                Slug = $"workspace-{suffix}",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = recipient.Id
            };
            var project = new Project
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                Name = "Project",
                Slug = $"project-{suffix}",
                Status = ProjectStatus.Active,
                OwnerUserId = recipient.Id,
                CreatedByUserId = recipient.Id
            };
            var task = new TaskItem
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = recipient.Id,
                Title = "Restricted task",
                VersionNo = 4
            };
            var notification = new Notification
            {
                TenantId = tenant.Id,
                UserId = recipient.Id,
                NotificationType = NotificationType.TaskDueSoon,
                Title = "Task deadline changed",
                RelatedEntityType = "TaskItem",
                RelatedEntityId = task.Id,
                CreatedAt = Now,
                StateVersion = 4
            };
            var artifact = new Artifact
            {
                TenantId = tenant.Id,
                ProjectId = project.Id,
                Name = "Protected PostgreSQL artifact",
                CreatedByUserId = recipient.Id
            };
            var artifactNotification = new Notification
            {
                TenantId = tenant.Id,
                UserId = recipient.Id,
                NotificationType = NotificationType.ArtifactUploaded,
                Title = "Artifact uploaded",
                Body = artifact.Name,
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
                Title = "Protected PostgreSQL conversation",
                CreatedByUserId = recipient.Id
            };
            var message = new Message
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ConversationId = conversation.Id,
                AuthorUserId = recipient.Id,
                Body = "Protected PostgreSQL message"
            };
            var messageNotification = new Notification
            {
                TenantId = tenant.Id,
                UserId = recipient.Id,
                NotificationType = NotificationType.DirectMessage,
                Title = "New project message",
                Body = message.Body,
                RelatedEntityType = "Message",
                RelatedEntityId = message.Id,
                CreatedAt = Now,
                StateVersion = 4
            };
            var unavailableProject = new Project
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                Name = "Unavailable project",
                Slug = $"unavailable-project-{suffix}",
                Status = ProjectStatus.Archived,
                OwnerUserId = recipient.Id,
                CreatedByUserId = recipient.Id
            };
            var unavailableTask = new TaskItem
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = unavailableProject.Id,
                CreatedByUserId = recipient.Id,
                Title = "Unavailable task",
                VersionNo = 1
            };
            var unavailableNotification = new Notification
            {
                TenantId = tenant.Id,
                UserId = recipient.Id,
                NotificationType = NotificationType.TaskDueSoon,
                Title = "Unavailable task notification",
                RelatedEntityType = "TaskItem",
                RelatedEntityId = unavailableTask.Id,
                CreatedAt = Now,
                StateVersion = 4
            };

            await using (var seed = CreateTenantContext(database, tenant))
            {
                seed.AddRange(
                    new TenantUser { TenantId = tenant.Id, UserId = recipient.Id, Status = TenantUserStatus.Active, JoinedAt = Now },
                    workspace,
                    new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = recipient.Id, Status = MembershipStatus.Active, Role = WorkspaceRole.Member },
                    project,
                    task,
                    notification,
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
                    unavailableProject,
                    unavailableTask,
                    unavailableNotification,
                    new NotificationUserState { TenantId = tenant.Id, UserId = recipient.Id, Version = 4, UpdatedAt = Now });
                await seed.SaveChangesAsync();
            }

            var currentTenant = TenantScope(tenant);
            await using (var openContext = CreateTenantContext(database, tenant))
            {
                var resolver = new CurrentAuthorizationTargetResolver(
                    openContext,
                    currentTenant,
                    new MessagingRepository(openContext));
                var outbox = new TransactionalOutbox(new OutboxEventRepository(openContext), currentTenant, new FixedClock());
                var notifications = new DbNotificationService(openContext, new FixedClock(), currentTenant, targets: resolver);
                var service = new NotificationOpenService(openContext, currentTenant, new FixedClock(), outbox, resolver, notifications);

                Assert.Equal(3, await notifications.GetUnreadCountAsync(recipient.Id));
                var initialPage = await notifications.ListAsync(recipient.Id, 1, 20);
                Assert.Equal(3, initialPage.TotalCount);
                Assert.Contains(initialPage.Items, item => item.Id == artifactNotification.Id && item.Body == artifact.Name);
                Assert.Contains(initialPage.Items, item => item.Id == messageNotification.Id && item.Body == message.Body);
                Assert.True((await resolver.ResolveAsync(tenant.Id, recipient.Id, artifactNotification.Id)).IsAvailable);
                Assert.True((await resolver.ResolveAsync(tenant.Id, recipient.Id, messageNotification.Id)).IsAvailable);
                Assert.True(await resolver.CanDeliverCreatedAsync(
                    tenant.Id,
                    recipient.Id,
                    EmbeddedCreatedEnvelope(tenant.Id, artifactNotification)));
                Assert.True(await resolver.CanDeliverCreatedAsync(
                    tenant.Id,
                    recipient.Id,
                    EmbeddedCreatedEnvelope(tenant.Id, messageNotification)));

                var result = await service.OpenAsync(tenant.Id, recipient.Id, notification.Id);

                Assert.True(result.IsOwned);
                Assert.True(result.IsAvailable);
                Assert.Equal($"/projects/{project.Id}/tasks/{task.Id}", result.Route);
                Assert.Equal(5, result.StateVersion);
                Assert.Equal(2, await notifications.GetUnreadCountAsync(recipient.Id));

                var mutableProject = await openContext.Projects.SingleAsync(item => item.Id == project.Id);
                mutableProject.Status = ProjectStatus.Archived;
                await openContext.SaveChangesAsync();
                Assert.Equal(0, await notifications.GetUnreadCountAsync(recipient.Id));
                Assert.False(await resolver.CanDeliverCreatedAsync(
                    tenant.Id,
                    recipient.Id,
                    EmbeddedCreatedEnvelope(tenant.Id, artifactNotification)));
                Assert.False(await resolver.CanDeliverCreatedAsync(
                    tenant.Id,
                    recipient.Id,
                    EmbeddedCreatedEnvelope(tenant.Id, messageNotification)));
            }

            await using var verify = CreateTenantContext(database, tenant);
            var persisted = await verify.Notifications.SingleAsync(item => item.Id == notification.Id);
            Assert.True(persisted.IsRead);
            Assert.Equal(5, persisted.StateVersion);
            Assert.Equal(5, (await verify.NotificationUserStates.SingleAsync()).Version);
            var signal = Assert.Single(await verify.OutboxEvents.Where(item => item.EventType == "Notifications.NotificationReadStateChanged.v1").ToListAsync());
            using var envelope = JsonDocument.Parse(signal.PayloadJson);
            var payload = envelope.RootElement.GetProperty("payload");
            Assert.Equal(
                ["change", "notificationId", "stateVersion", "unreadCount", "updatedAt"],
                payload.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(notification.Id, payload.GetProperty("notificationId").GetGuid());
            Assert.Equal(2, payload.GetProperty("unreadCount").GetInt32());
            Assert.DoesNotContain("Restricted task", signal.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("route", signal.PayloadJson, StringComparison.OrdinalIgnoreCase);
            var routes = JsonSerializer.Deserialize<List<RealtimeRoutingTarget>>(signal.RoutingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var route = Assert.Single(routes!);
            Assert.Equal(RealtimeSubscriptionType.User, route.SubscriptionType);
            Assert.Equal(recipient.Id, route.ResourceId);
            var verificationTenant = TenantScope(tenant);
            var verificationResolver = new CurrentAuthorizationTargetResolver(
                verify,
                verificationTenant,
                new MessagingRepository(verify));
            var verificationNotifications = new DbNotificationService(verify, new FixedClock(), verificationTenant, targets: verificationResolver);
            Assert.Equal(0, await verificationNotifications.GetUnreadCountAsync(recipient.Id));
            Assert.False((await verify.Notifications.SingleAsync(item => item.Id == unavailableNotification.Id)).IsRead);
            var page = await verificationNotifications.ListAsync(recipient.Id, 1, 20);
            Assert.Equal(0, page.TotalCount);
            Assert.Empty(page.Items);
        });
    }

    private static DurableEventEnvelope EmbeddedCreatedEnvelope(Guid tenantId, Notification notification) => new(
        Guid.NewGuid(),
        "Notifications.NotificationCreated.v1",
        RealtimeEventCatalog.PayloadSchemaVersion1,
        Now,
        tenantId,
        "Notification",
        notification.Id,
        notification.StateVersion,
        RealtimeActor.System(),
        null,
        null,
        JsonSerializer.SerializeToElement(new
        {
            notification = new
            {
                id = notification.Id,
                title = notification.Title,
                body = notification.Body,
                target = new
                {
                    targetType = notification.RelatedEntityType,
                    targetId = notification.RelatedEntityId
                },
                version = notification.StateVersion
            },
            unreadCount = 3,
            stateVersion = notification.StateVersion
        }));

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
