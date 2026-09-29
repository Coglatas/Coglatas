using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Notifications;
using Coglatas.Application.Realtime;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Notifications;

[Trait("Scope", "TaskV1PR07D")]
public sealed class TaskV1Pr07DAuthorizedDeliveryTests
{
    [Fact]
    public async Task NotificationCreatedDispatchReauthorizesTaskAccess()
    {
        await using var fixture = await Fixture.CreateAsync();

        var allowed = await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.NotificationCreatedEnvelope());

        Assert.True(allowed);
    }

    [Fact]
    public async Task NotificationCreatedDispatchIsSuppressedAfterMembershipRevocation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Member.Status = MembershipStatus.Suspended;
        await fixture.Db.SaveChangesAsync();

        var allowed = await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.NotificationCreatedEnvelope());

        Assert.False(allowed);
    }

    [Fact]
    public async Task NotificationCreatedDispatchIsSuppressedAfterWorkspaceArchive()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Workspace.Status = WorkspaceStatus.Archived;
        await fixture.Db.SaveChangesAsync();

        var allowed = await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.NotificationCreatedEnvelope());

        Assert.False(allowed);
    }

    [Fact]
    public async Task NotificationCreatedUsesRecipientOnlyUserRouting()
    {
        await using var fixture = await Fixture.CreateAsync();

        var allowedForOwner = await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.NotificationCreatedEnvelope());
        var allowedForOtherUser = await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            Guid.NewGuid(),
            fixture.NotificationCreatedEnvelope());

        Assert.True(allowedForOwner);
        Assert.False(allowedForOtherUser);
    }

    [Fact]
    public async Task NotificationCreatedDispatchRejectsWidenedReferencePayload()
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.NotificationCreatedEnvelope() with
        {
            Payload = JsonSerializer.SerializeToElement(new
            {
                notificationId = fixture.Notification.Id,
                stateVersion = fixture.Notification.StateVersion,
                requiresRefetch = true,
                title = "Restricted task title"
            })
        };

        Assert.False(await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            envelope));
    }

    [Fact]
    public async Task GenericRecipientNotificationCreatedRetainsLegacyRecipientDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var notification = new Notification
        {
            TenantId = fixture.TenantId,
            UserId = fixture.UserId,
            NotificationType = NotificationType.System,
            Title = "General recipient notification",
            CreatedAt = FixedClock.Instance.UtcNow,
            StateVersion = 6
        };
        fixture.Db.Notifications.Add(notification);
        await fixture.Db.SaveChangesAsync();

        var envelope = new DurableEventEnvelope(
            Guid.NewGuid(),
            "Notifications.NotificationCreated.v1",
            RealtimeEventCatalog.PayloadSchemaVersion1,
            FixedClock.Instance.UtcNow,
            fixture.TenantId,
            "Notification",
            notification.Id,
            notification.StateVersion,
            RealtimeActor.System(),
            null,
            null,
            JsonSerializer.SerializeToElement(new
            {
                notification = new { id = notification.Id, title = notification.Title, version = notification.StateVersion },
                unreadCount = 1,
                stateVersion = notification.StateVersion
            }));

        Assert.True(await fixture.Resolver.CanDeliverCreatedAsync(fixture.TenantId, fixture.UserId, envelope));
    }

    [Fact]
    public async Task TaskNotificationCreatedPayloadIsReferenceOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var document = JsonDocument.Parse($$"""
            {
              "requiresRefetch": true,
              "stateVersion": {{fixture.Notification.StateVersion}},
              "notificationId": "{{fixture.Notification.Id}}"
            }
            """);
        var envelope = fixture.NotificationCreatedEnvelope() with { Payload = document.RootElement.Clone() };

        Assert.True(await fixture.Resolver.CanDeliverCreatedAsync(fixture.TenantId, fixture.UserId, envelope));
    }

    [Fact]
    public async Task DigestNotificationCreatedPayloadIsReferenceOnly()
    {
        await using var fixture = await Fixture.CreateAsync(digest: true);
        using var document = JsonDocument.Parse($$"""
            {
              "stateVersion": {{fixture.Notification.StateVersion}},
              "notificationId": "{{fixture.Notification.Id}}",
              "requiresRefetch": true
            }
            """);
        var envelope = fixture.NotificationCreatedEnvelope() with { Payload = document.RootElement.Clone() };

        Assert.True(await fixture.Resolver.CanDeliverCreatedAsync(fixture.TenantId, fixture.UserId, envelope));
    }

    [Fact]
    public async Task NotificationReadStateUsesRecipientOnlyUserRouting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.ReadStateEnvelope("read", fixture.Notification.Id);

        Assert.True(await fixture.Resolver.CanDeliverReadStateAsync(fixture.TenantId, fixture.UserId, envelope));
        Assert.False(await fixture.Resolver.CanDeliverReadStateAsync(fixture.TenantId, Guid.NewGuid(), envelope));
        Assert.True(await fixture.Resolver.CanDeliverReadStateAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.ReadStateEnvelope("allRead", null)));
    }

    [Fact]
    public async Task GenericRecipientNotificationReadStateRetainsLegacyRecipientDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var notification = new Notification
        {
            TenantId = fixture.TenantId,
            UserId = fixture.UserId,
            NotificationType = NotificationType.System,
            Title = "General recipient notification",
            CreatedAt = FixedClock.Instance.UtcNow,
            StateVersion = 6
        };
        fixture.Db.Notifications.Add(notification);
        await fixture.Db.SaveChangesAsync();
        var envelope = new DurableEventEnvelope(
            Guid.NewGuid(),
            "Notifications.NotificationReadStateChanged.v1",
            RealtimeEventCatalog.PayloadSchemaVersion1,
            FixedClock.Instance.UtcNow,
            fixture.TenantId,
            "Notification",
            notification.Id,
            notification.StateVersion,
            RealtimeActor.System(),
            null,
            null,
            JsonSerializer.SerializeToElement(new
            {
                notificationId = notification.Id,
                change = "read",
                unreadCount = 0,
                stateVersion = notification.StateVersion,
                updatedAt = FixedClock.Instance.UtcNow
            }));

        Assert.True(await fixture.Resolver.CanDeliverReadStateAsync(fixture.TenantId, fixture.UserId, envelope));
    }

    [Fact]
    public async Task NotificationReadStateDispatchIsSuppressedAfterMembershipRevocation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Member.Status = MembershipStatus.Suspended;
        await fixture.Db.SaveChangesAsync();

        Assert.False(await fixture.Resolver.CanDeliverReadStateAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.ReadStateEnvelope("read", fixture.Notification.Id)));
    }

    [Fact]
    public async Task ReadAndDeleteSignalsUseResultingUnreadCountAndCurrentTargetAuthorization()
    {
        await using var fixture = await Fixture.CreateAsync();
        var notifications = new DbNotificationService(
            fixture.Db,
            FixedClock.Instance,
            fixture.Tenant,
            fixture.Outbox,
            fixture.Resolver);

        Assert.True(await notifications.MarkAsReadAsync(fixture.UserId, fixture.Notification.Id));
        await fixture.Db.SaveChangesAsync();
        var readEnvelope = Assert.Single(fixture.Outbox.Items).Envelope;
        Assert.Equal(0, readEnvelope.Payload.GetProperty("unreadCount").GetInt32());
        Assert.True(await fixture.Resolver.CanDeliverReadStateAsync(
            fixture.TenantId,
            fixture.UserId,
            readEnvelope));

        fixture.Outbox.Items.Clear();
        Assert.True(await notifications.DeleteAsync(
            fixture.UserId,
            fixture.Notification.Id,
            FixedClock.Instance.UtcNow));
        await fixture.Db.SaveChangesAsync();
        var deletedEnvelope = Assert.Single(fixture.Outbox.Items).Envelope;
        Assert.Equal("deleted", deletedEnvelope.Payload.GetProperty("change").GetString());
        Assert.Equal(0, deletedEnvelope.Payload.GetProperty("unreadCount").GetInt32());
        Assert.True(await fixture.Resolver.CanDeliverReadStateAsync(
            fixture.TenantId,
            fixture.UserId,
            deletedEnvelope));
    }

    [Fact]
    public async Task AuthorizationScanUsesStableOrderingAcrossTiedTimestampBatches()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tied = Enumerable.Range(0, 125)
            .Select(index => new Notification
            {
                TenantId = fixture.TenantId,
                UserId = fixture.UserId,
                NotificationType = NotificationType.System,
                Title = $"Tied notification {index}",
                CreatedAt = FixedClock.Instance.UtcNow,
                StateVersion = 5
            })
            .ToArray();
        fixture.Db.Notifications.AddRange(tied);
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Notifications.ListAsync(fixture.UserId, 1, 100);
        var second = await fixture.Notifications.ListAsync(fixture.UserId, 2, 100);
        var ids = first.Items.Concat(second.Items).Select(item => item.Id).ToArray();
        var expectedIds = tied
            .Select(notification => notification.Id)
            .Append(fixture.Notification.Id)
            .OrderByDescending(id => id)
            .ToArray();

        Assert.Equal(126, first.TotalCount);
        Assert.Equal(126, second.TotalCount);
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(26, second.Items.Count);
        Assert.Equal(expectedIds, ids);
        Assert.Equal(126, await fixture.Notifications.GetUnreadCountAsync(fixture.UserId));
    }

    [Fact]
    public async Task RevokedTaskNotificationIsHiddenAndCannotBeMutatedThroughTheLegacyEndpoints()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Member.Status = MembershipStatus.Suspended;
        await fixture.Db.SaveChangesAsync();
        var notifications = new DbNotificationService(
            fixture.Db,
            FixedClock.Instance,
            fixture.Tenant,
            targets: fixture.Resolver);

        var page = await notifications.ListAsync(fixture.UserId, 1, 20);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, await notifications.GetUnreadCountAsync(fixture.UserId));
        Assert.False(await notifications.MarkAsReadAsync(fixture.UserId, fixture.Notification.Id));
        Assert.False(await notifications.DeleteAsync(fixture.UserId, fixture.Notification.Id, FixedClock.Instance.UtcNow));
        Assert.False((await fixture.Db.Notifications.SingleAsync()).IsRead);
        Assert.Null((await fixture.Db.Notifications.SingleAsync()).DeletedAt);
    }

    [Fact]
    public async Task ArtifactNotificationReauthorizesListOpenMutationAndDelayedDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (artifact, notification) = await fixture.AddArtifactNotificationAsync();

        var visible = await fixture.Notifications.ListAsync(fixture.UserId, 1, 20);
        Assert.Contains(visible.Items, item =>
            item.Id == notification.Id &&
            item.Body == "Protected artifact name");
        var available = await fixture.Resolver.ResolveAsync(fixture.TenantId, fixture.UserId, notification.Id);
        Assert.True(available.IsAvailable);
        Assert.Equal($"/artifacts/{artifact.Id}", available.Route);
        Assert.Equal(fixture.Workspace.Id, available.WorkspaceId);
        Assert.True(await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.EmbeddedNotificationCreatedEnvelope(notification)));

        fixture.Member.Status = MembershipStatus.Suspended;
        await fixture.Db.SaveChangesAsync();

        var hidden = await fixture.Notifications.ListAsync(fixture.UserId, 1, 20);
        Assert.DoesNotContain(hidden.Items, item => item.Id == notification.Id);
        Assert.Equal(0, hidden.TotalCount);
        Assert.Equal(0, await fixture.Notifications.GetUnreadCountAsync(fixture.UserId));
        Assert.False(await fixture.Notifications.MarkAsReadAsync(fixture.UserId, notification.Id));
        Assert.False(await fixture.Notifications.DeleteAsync(
            fixture.UserId,
            notification.Id,
            FixedClock.Instance.UtcNow));
        var open = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, notification.Id);
        Assert.True(open.IsOwned);
        Assert.False(open.IsAvailable);
        Assert.False(await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.EmbeddedNotificationCreatedEnvelope(notification)));
        Assert.False(await fixture.Resolver.CanDeliverReadStateAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.ReadStateEnvelope("read", notification.Id)));
        var persisted = await fixture.Db.Notifications.SingleAsync(item => item.Id == notification.Id);
        Assert.False(persisted.IsRead);
        Assert.Null(persisted.DeletedAt);
    }

    [Fact]
    public async Task ProjectMessageNotificationReauthorizesConversationAndProjectAccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (message, notification) = await fixture.AddMessageNotificationAsync();

        var available = await fixture.Resolver.ResolveAsync(fixture.TenantId, fixture.UserId, notification.Id);
        Assert.True(available.IsAvailable);
        Assert.Equal($"/messages/{message.Id}", available.Route);
        Assert.Equal(fixture.Workspace.Id, available.WorkspaceId);
        Assert.True(await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.EmbeddedNotificationCreatedEnvelope(notification)));

        var conversation = await fixture.Db.Conversations.SingleAsync(item => item.Id == message.ConversationId);
        var conversationMember = await fixture.Db.ConversationMembers
            .SingleAsync(item => item.ConversationId == conversation.RootConversationId && item.UserId == fixture.UserId);
        conversationMember.RemovedAt = FixedClock.Instance.UtcNow;
        await fixture.Db.SaveChangesAsync();

        var hidden = await fixture.Notifications.ListAsync(fixture.UserId, 1, 20);
        Assert.DoesNotContain(hidden.Items, item => item.Id == notification.Id);
        Assert.Equal(1, hidden.TotalCount);
        Assert.Equal(fixture.Notification.Id, Assert.Single(hidden.Items).Id);
        Assert.Equal(1, await fixture.Notifications.GetUnreadCountAsync(fixture.UserId));
        Assert.False(await fixture.Notifications.MarkAsReadAsync(fixture.UserId, notification.Id));
        Assert.False(await fixture.Notifications.DeleteAsync(
            fixture.UserId,
            notification.Id,
            FixedClock.Instance.UtcNow));
        var open = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, notification.Id);
        Assert.True(open.IsOwned);
        Assert.False(open.IsAvailable);
        Assert.False(await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.EmbeddedNotificationCreatedEnvelope(notification)));
        Assert.False(await fixture.Resolver.CanDeliverReadStateAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.ReadStateEnvelope("read", notification.Id)));

        conversationMember.RemovedAt = null;
        fixture.Project.Status = ProjectStatus.Archived;
        await fixture.Db.SaveChangesAsync();
        Assert.False((await fixture.Resolver.ResolveAsync(
            fixture.TenantId,
            fixture.UserId,
            notification.Id)).IsAvailable);
        Assert.False(await fixture.Resolver.CanDeliverCreatedAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.EmbeddedNotificationCreatedEnvelope(notification)));
    }

    [Fact]
    public async Task OpenTaskNotificationReadStateEventUsesCurrentVisibleUnreadCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var unavailableProject = new Project
        {
            TenantId = fixture.TenantId,
            WorkspaceId = fixture.Workspace.Id,
            OwnerUserId = fixture.UserId,
            CreatedByUserId = fixture.UserId,
            Name = "Unavailable project",
            Slug = "unavailable-project",
            Status = ProjectStatus.Archived
        };
        var unavailableTask = new TaskItem
        {
            TenantId = fixture.TenantId,
            WorkspaceId = fixture.Workspace.Id,
            ProjectId = unavailableProject.Id,
            CreatedByUserId = fixture.UserId,
            Title = "Unavailable task",
            VersionNo = 1
        };
        var unavailableNotification = new Notification
        {
            TenantId = fixture.TenantId,
            UserId = fixture.UserId,
            NotificationType = NotificationType.TaskDueSoon,
            Title = "Unavailable notification",
            RelatedEntityType = "TaskItem",
            RelatedEntityId = unavailableTask.Id,
            CreatedAt = FixedClock.Instance.UtcNow,
            StateVersion = 5
        };
        fixture.Db.AddRange(unavailableProject, unavailableTask, unavailableNotification);
        await fixture.Db.SaveChangesAsync();
        var notifications = new DbNotificationService(
            fixture.Db,
            FixedClock.Instance,
            fixture.Tenant,
            targets: fixture.Resolver);

        Assert.Equal(1, await notifications.GetUnreadCountAsync(fixture.UserId));

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsAvailable);
        var eventPayload = Assert.Single(fixture.Outbox.Items).Envelope.Payload;
        Assert.Equal(0, eventPayload.GetProperty("unreadCount").GetInt32());
        Assert.Equal(0, await notifications.GetUnreadCountAsync(fixture.UserId));
        Assert.False((await fixture.Db.Notifications.SingleAsync(item => item.Id == unavailableNotification.Id)).IsRead);
        var page = await notifications.ListAsync(fixture.UserId, 1, 20);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(fixture.Notification.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task TaskInvalidationDispatchReauthorizesCurrentTaskAccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.TaskChangedEnvelope();

        Assert.True(await fixture.Resolver.CanReceiveTaskEventAsync(
            fixture.TenantId,
            fixture.UserId,
            RealtimeSubscriptionType.Project,
            fixture.Project.Id,
            envelope));

        fixture.Task.MarkDeleted(FixedClock.Instance.UtcNow);
        await fixture.Db.SaveChangesAsync();

        Assert.False(await fixture.Resolver.CanReceiveTaskEventAsync(
            fixture.TenantId,
            fixture.UserId,
            RealtimeSubscriptionType.Project,
            fixture.Project.Id,
            envelope));
    }

    [Fact]
    public async Task TaskInvalidationDoesNotLeakThroughBroadWorkspaceRoute()
    {
        await using var fixture = await Fixture.CreateAsync();

        var allowed = await fixture.Resolver.CanReceiveTaskEventAsync(
            fixture.TenantId,
            fixture.UserId,
            RealtimeSubscriptionType.Workspace,
            Guid.NewGuid(),
            fixture.TaskChangedEnvelope());

        Assert.False(allowed);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task PlanningTaskRealtimeAndNotificationTargetsRequireExplicitProjectMembership()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Project.Status = ProjectStatus.Planning;
        await fixture.Db.SaveChangesAsync();

        Assert.False(await fixture.Resolver.CanReceiveTaskEventAsync(
            fixture.TenantId,
            fixture.UserId,
            RealtimeSubscriptionType.Project,
            fixture.Project.Id,
            fixture.TaskChangedEnvelope()));
        var unavailable = await fixture.Open.OpenAsync(
            fixture.TenantId,
            fixture.UserId,
            fixture.Notification.Id);
        Assert.False(unavailable.IsAvailable);

        fixture.Db.ProjectMembers.Add(new ProjectMember
        {
            TenantId = fixture.TenantId,
            ProjectId = fixture.Project.Id,
            UserId = fixture.UserId,
            Role = ProjectRole.Contributor,
            JoinedAt = FixedClock.Instance.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Resolver.CanReceiveTaskEventAsync(
            fixture.TenantId,
            fixture.UserId,
            RealtimeSubscriptionType.Project,
            fixture.Project.Id,
            fixture.TaskChangedEnvelope()));
    }

    [Fact]
    public async Task OpenTaskNotificationReturnsCurrentProjectTaskRoute()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.True(result.IsAvailable);
        Assert.Equal($"/projects/{fixture.Project.Id}/tasks/{fixture.Task.Id}", result.Route);
        Assert.Equal(fixture.Workspace.Id, result.WorkspaceId);
        Assert.Equal(6, result.StateVersion);
        var persisted = await fixture.Db.Notifications.SingleAsync();
        Assert.True(persisted.IsRead);
        Assert.Equal(6, persisted.StateVersion);
        var outbox = Assert.Single(fixture.Outbox.Items);
        Assert.Equal("Notifications.NotificationReadStateChanged.v1", outbox.Envelope.EventType);
        Assert.Equal(RealtimeSubscriptionType.User, Assert.Single(outbox.Targets).SubscriptionType);
        Assert.Equal(fixture.UserId, Assert.Single(outbox.Targets).ResourceId);
        Assert.DoesNotContain("title", outbox.Envelope.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("route", outbox.Envelope.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenTaskNotificationMarksReadAfterAuthorizedResolution()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.True(result.IsAvailable);
        var persisted = await fixture.Db.Notifications.SingleAsync();
        Assert.True(persisted.IsRead);
        Assert.Equal(result.StateVersion, persisted.StateVersion);
        Assert.Equal(result.StateVersion, (await fixture.Db.NotificationUserStates.SingleAsync()).Version);
        Assert.Single(fixture.Outbox.Items);
    }

    [Fact]
    public async Task OpenTaskNotificationRejectsAnotherRecipient()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, Guid.NewGuid(), fixture.Notification.Id);

        Assert.False(result.IsOwned);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Route);
        Assert.False((await fixture.Db.Notifications.SingleAsync()).IsRead);
    }

    [Fact]
    public async Task OpenTaskNotificationReturnsUnavailableAfterMembershipRevocationWithoutMarkingRead()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Member.Status = MembershipStatus.Suspended;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Route);
        Assert.False((await fixture.Db.Notifications.SingleAsync()).IsRead);
        Assert.Equal(5, (await fixture.Db.NotificationUserStates.SingleAsync()).Version);
        Assert.Empty(fixture.Outbox.Items);
    }

    [Fact]
    public async Task OpenTaskNotificationDoesNotMarkReadWhenUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Project.Status = ProjectStatus.Archived;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.False(result.IsAvailable);
        Assert.False((await fixture.Db.Notifications.SingleAsync()).IsRead);
        Assert.Empty(fixture.Outbox.Items);
    }

    [Fact]
    public async Task OpenTaskNotificationReturnsUnavailableAfterProjectArchive()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Project.Status = ProjectStatus.Archived;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Route);
    }

    [Fact]
    public async Task OpenTaskNotificationReturnsUnavailableAfterTaskDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Task.MarkDeleted(FixedClock.Instance.UtcNow);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Route);
        Assert.False((await fixture.Db.Notifications.SingleAsync()).IsRead);
        Assert.Empty(fixture.Outbox.Items);
    }

    [Fact]
    public async Task OpenTaskNotificationDoesNotExposeUnavailableReason()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Project.Status = ProjectStatus.Archived;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Route);
        Assert.Equal(5, result.StateVersion);
        Assert.Null(result.WorkspaceId);
    }

    [Fact]
    public async Task RepeatedOpenDoesNotAdvanceReadStateTwice()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);
        var second = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.Equal(first.StateVersion, second.StateVersion);
        Assert.Equal(6, (await fixture.Db.NotificationUserStates.SingleAsync()).Version);
        Assert.Single(fixture.Outbox.Items);
    }

    [Fact]
    public async Task OpenDigestNotificationReturnsWorkspaceMyTasksTarget()
    {
        await using var fixture = await Fixture.CreateAsync(digest: true);

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.True(result.IsAvailable);
        Assert.Equal("/tasks", result.Route);
        Assert.Equal(fixture.Workspace.Id, result.WorkspaceId);
    }

    [Fact]
    public async Task OpenDigestNotificationReturnsUnavailableAfterWorkspaceRevocation()
    {
        await using var fixture = await Fixture.CreateAsync(digest: true);
        fixture.Member.Status = MembershipStatus.Suspended;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Route);
    }

    [Fact]
    public async Task UnknownNotificationTargetReturnsUniformUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Notification.RelatedEntityType = "UnknownRestrictedTarget";
        fixture.Notification.RelatedEntityId = Guid.NewGuid();
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Open.OpenAsync(fixture.TenantId, fixture.UserId, fixture.Notification.Id);

        Assert.True(result.IsOwned);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Route);
        Assert.Equal(5, result.StateVersion);
        Assert.False((await fixture.Db.Notifications.SingleAsync()).IsRead);
    }

    [Fact]
    public async Task WorkspaceArchivePublishesAuthorizationInvalidationForAffectedUsers()
    {
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var firstMemberId = Guid.NewGuid();
        var secondMemberId = Guid.NewGuid();
        var workspace = new Workspace
        {
            TenantId = tenantId,
            Name = "Workspace",
            Slug = "workspace",
            Status = WorkspaceStatus.Active,
            CreatedByUserId = actorId
        };
        var repository = new ArchiveWorkspaceRepository(workspace,
        [
            new WorkspaceMember { TenantId = tenantId, WorkspaceId = workspace.Id, UserId = firstMemberId, Status = MembershipStatus.Active },
            new WorkspaceMember { TenantId = tenantId, WorkspaceId = workspace.Id, UserId = secondMemberId, Status = MembershipStatus.Active },
            new WorkspaceMember { TenantId = tenantId, WorkspaceId = workspace.Id, UserId = Guid.NewGuid(), Status = MembershipStatus.Suspended }
        ]);
        var publisher = new RecordingAuthorizationChanges();
        var tenant = new CurrentTenantService();
        tenant.SetTenant(tenantId, "tenant");
        var service = new WorkspaceService(
            repository,
            null!,
            new AllowWorkspaceManagement(),
            new TestCurrentUser(actorId),
            FixedClock.Instance,
            new NoopAuditLogger(),
            new ArchiveUnitOfWork(publisher),
            tenant,
            publisher);

        var result = await service.ArchiveAsync(workspace.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceStatus.Archived, workspace.Status);
        Assert.Equal(
            new[] { firstMemberId, secondMemberId }.OrderBy(id => id).ToArray(),
            publisher.Committed.Select(item => item.AffectedUserId).OrderBy(id => id).ToArray());
        Assert.All(publisher.Committed, item =>
        {
            Assert.Equal(tenantId, item.TenantId);
            Assert.Equal("workspace", item.ScopeType);
            Assert.Equal(workspace.Id, item.ScopeId);
            Assert.Equal("archived", item.Change);
        });
    }

    [Fact]
    public async Task WorkspaceArchiveRollbackPublishesNoAuthorizationInvalidation()
    {
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var workspace = new Workspace
        {
            TenantId = tenantId,
            Name = "Workspace",
            Slug = "workspace",
            Status = WorkspaceStatus.Active,
            CreatedByUserId = actorId
        };
        var repository = new ArchiveWorkspaceRepository(workspace,
        [new WorkspaceMember { TenantId = tenantId, WorkspaceId = workspace.Id, UserId = actorId, Status = MembershipStatus.Active }]);
        var publisher = new RecordingAuthorizationChanges();
        var tenant = new CurrentTenantService();
        tenant.SetTenant(tenantId, "tenant");
        var service = new WorkspaceService(
            repository,
            null!,
            new AllowWorkspaceManagement(),
            new TestCurrentUser(actorId),
            FixedClock.Instance,
            new NoopAuditLogger(),
            new ArchiveUnitOfWork(publisher, failSave: true),
            tenant,
            publisher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ArchiveAsync(workspace.Id));

        Assert.Empty(publisher.Committed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext db,
            CurrentTenantService tenant,
            CurrentAuthorizationTargetResolver resolver,
            DbNotificationService notifications,
            NotificationOpenService open,
            RecordingOutbox outbox,
            Guid tenantId,
            Guid userId,
            Workspace workspace,
            WorkspaceMember member,
            Project project,
            TaskItem task,
            Notification notification)
        {
            Db = db;
            Tenant = tenant;
            Resolver = resolver;
            Notifications = notifications;
            Open = open;
            Outbox = outbox;
            TenantId = tenantId;
            UserId = userId;
            Workspace = workspace;
            Member = member;
            Project = project;
            Task = task;
            Notification = notification;
        }

        public AppDbContext Db { get; }
        public CurrentTenantService Tenant { get; }
        public CurrentAuthorizationTargetResolver Resolver { get; }
        public DbNotificationService Notifications { get; }
        public NotificationOpenService Open { get; }
        public RecordingOutbox Outbox { get; }
        public Guid TenantId { get; }
        public Guid UserId { get; }
        public Workspace Workspace { get; }
        public WorkspaceMember Member { get; }
        public Project Project { get; }
        public TaskItem Task { get; }
        public Notification Notification { get; }

        public static async Task<Fixture> CreateAsync(bool digest = false)
        {
            var tenantId = Guid.NewGuid();
            var tenant = new CurrentTenantService();
            tenant.SetPlatformScope();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options,
                tenant);
            db.Tenants.Add(new Tenant(tenantId)
            {
                Name = "Tenant",
                DisplayName = "Tenant",
                Slug = $"tenant-{tenantId:N}",
                Status = TenantStatus.Active
            });
            await db.SaveChangesAsync();
            tenant.SetTenant(tenantId, $"tenant-{tenantId:N}");

            var user = new User { DisplayName = "Recipient", Email = "recipient@example.invalid", NormalizedEmail = "RECIPIENT@EXAMPLE.INVALID", Status = UserStatus.Active };
            var userId = user.Id;
            var workspace = new Workspace { TenantId = tenantId, Name = "Workspace", Slug = "workspace", Status = WorkspaceStatus.Active, CreatedByUserId = userId };
            var member = new WorkspaceMember { TenantId = tenantId, WorkspaceId = workspace.Id, UserId = userId, Status = MembershipStatus.Active, Role = WorkspaceRole.Member };
            var project = new Project { TenantId = tenantId, WorkspaceId = workspace.Id, OwnerUserId = userId, CreatedByUserId = userId, Name = "Project", Slug = "project", Status = ProjectStatus.Active };
            var task = new TaskItem { TenantId = tenantId, WorkspaceId = workspace.Id, ProjectId = project.Id, CreatedByUserId = userId, Title = "Restricted task", VersionNo = 9 };
            var digestJob = digest
                ? new TaskDeadlineDigestJob
                {
                    TenantId = tenantId,
                    WorkspaceId = workspace.Id,
                    UserId = userId,
                    LocalDate = new DateOnly(2026, 8, 6),
                    ScheduledForUtc = FixedClock.Instance.UtcNow
                }
                : null;
            var notification = new Notification
            {
                TenantId = tenantId,
                UserId = userId,
                NotificationType = NotificationType.TaskDueSoon,
                Title = "Restricted notification",
                RelatedEntityType = digest ? TaskDeadlineDigestPolicy.RelatedEntityType : "TaskItem",
                RelatedEntityId = digestJob?.Id ?? task.Id,
                CreatedAt = FixedClock.Instance.UtcNow,
                StateVersion = 5
            };
            db.AddRange(
                user,
                new TenantUser { TenantId = tenantId, UserId = userId, Status = TenantUserStatus.Active, JoinedAt = FixedClock.Instance.UtcNow },
                workspace,
                member,
                project,
                task,
                notification,
                new NotificationUserState { TenantId = tenantId, UserId = userId, Version = 5, UpdatedAt = FixedClock.Instance.UtcNow });
            if (digestJob is not null)
            {
                digestJob.NotificationId = notification.Id;
                db.TaskDeadlineDigestJobs.Add(digestJob);
            }
            await db.SaveChangesAsync();

            var outbox = new RecordingOutbox();
            var resolver = new CurrentAuthorizationTargetResolver(db, tenant, new MessagingRepository(db));
            var notifications = new DbNotificationService(db, FixedClock.Instance, tenant, targets: resolver);
            var open = new NotificationOpenService(db, tenant, FixedClock.Instance, outbox, resolver, notifications);
            return new Fixture(db, tenant, resolver, notifications, open, outbox, tenantId, userId, workspace, member, project, task, notification);
        }

        public async Task<(Artifact Artifact, Notification Notification)> AddArtifactNotificationAsync()
        {
            var artifact = new Artifact
            {
                TenantId = TenantId,
                ProjectId = Project.Id,
                Name = "Protected artifact name",
                CreatedByUserId = UserId
            };
            var notification = new Notification
            {
                TenantId = TenantId,
                UserId = UserId,
                NotificationType = NotificationType.ArtifactUploaded,
                Title = "Artifact uploaded",
                Body = artifact.Name,
                RelatedEntityType = "Artifact",
                RelatedEntityId = artifact.Id,
                CreatedAt = FixedClock.Instance.UtcNow,
                StateVersion = 5
            };
            Db.AddRange(artifact, notification);
            await Db.SaveChangesAsync();
            return (artifact, notification);
        }

        public async Task<(Message Message, Notification Notification)> AddMessageNotificationAsync()
        {
            var conversation = new Conversation
            {
                TenantId = TenantId,
                WorkspaceId = Workspace.Id,
                ProjectId = Project.Id,
                Type = ConversationType.ProjectChannel,
                Title = "Protected project conversation",
                CreatedByUserId = UserId
            };
            var thread = new Conversation
            {
                TenantId = TenantId,
                WorkspaceId = Workspace.Id,
                ProjectId = Project.Id,
                Type = ConversationType.Thread,
                Title = "Protected project thread",
                ParentConversationId = conversation.Id,
                RootConversationId = conversation.Id,
                CreatedByUserId = UserId
            };
            var message = new Message
            {
                TenantId = TenantId,
                WorkspaceId = Workspace.Id,
                ConversationId = thread.Id,
                AuthorUserId = UserId,
                Body = "Protected message body"
            };
            var notification = new Notification
            {
                TenantId = TenantId,
                UserId = UserId,
                NotificationType = NotificationType.DirectMessage,
                Title = "New message",
                Body = message.Body,
                RelatedEntityType = "Message",
                RelatedEntityId = message.Id,
                CreatedAt = FixedClock.Instance.UtcNow,
                StateVersion = 5
            };
            Db.AddRange(
                conversation,
                new ConversationMember
                {
                    TenantId = TenantId,
                    ConversationId = conversation.Id,
                    UserId = UserId,
                    JoinedAt = FixedClock.Instance.UtcNow
                },
                thread,
                new ConversationMember
                {
                    TenantId = TenantId,
                    ConversationId = thread.Id,
                    UserId = UserId,
                    JoinedAt = FixedClock.Instance.UtcNow
                },
                message,
                notification);
            await Db.SaveChangesAsync();
            return (message, notification);
        }

        public DurableEventEnvelope NotificationCreatedEnvelope() => new(
            Guid.NewGuid(),
            "Notifications.NotificationCreated.v1",
            RealtimeEventCatalog.PayloadSchemaVersion1,
            FixedClock.Instance.UtcNow,
            TenantId,
            "Notification",
            Notification.Id,
            Notification.StateVersion,
            RealtimeActor.System(),
            null,
            null,
            JsonSerializer.SerializeToElement(new { notificationId = Notification.Id, stateVersion = Notification.StateVersion, requiresRefetch = true }));

        public DurableEventEnvelope EmbeddedNotificationCreatedEnvelope(Notification notification) => new(
            Guid.NewGuid(),
            "Notifications.NotificationCreated.v1",
            RealtimeEventCatalog.PayloadSchemaVersion1,
            FixedClock.Instance.UtcNow,
            TenantId,
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
                unreadCount = 2,
                stateVersion = notification.StateVersion
            }));

        public DurableEventEnvelope ReadStateEnvelope(string change, Guid? notificationId) => new(
            Guid.NewGuid(),
            "Notifications.NotificationReadStateChanged.v1",
            RealtimeEventCatalog.PayloadSchemaVersion1,
            FixedClock.Instance.UtcNow,
            TenantId,
            "Notification",
            notificationId ?? UserId,
            5,
            RealtimeActor.System(),
            null,
            null,
            JsonSerializer.SerializeToElement(new { notificationId, change, unreadCount = 0, stateVersion = 5, updatedAt = FixedClock.Instance.UtcNow }));

        public DurableEventEnvelope TaskChangedEnvelope() => new(
            Guid.NewGuid(),
            "Projects.TaskChanged.v1",
            RealtimeEventCatalog.PayloadSchemaVersion1,
            FixedClock.Instance.UtcNow,
            TenantId,
            "Task",
            Task.Id,
            Task.VersionNo,
            RealtimeActor.System(),
            null,
            null,
            JsonSerializer.SerializeToElement(new { projectId = Project.Id, taskId = Task.Id, taskVersion = Task.VersionNo, change = "updated", requiresRefetch = true }));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class RecordingOutbox : ITransactionalOutbox
    {
        public List<(DurableEventEnvelope Envelope, IReadOnlyCollection<RealtimeRoutingTarget> Targets)> Items { get; } = [];

        public Task<Result<Guid>> EnqueueAsync(
            DurableEventEnvelope envelope,
            IReadOnlyCollection<RealtimeRoutingTarget> routingTargets,
            CancellationToken cancellationToken = default)
        {
            Items.Add((envelope, routingTargets));
            return Task.FromResult(Result<Guid>.Success(envelope.EventId));
        }
    }

    private sealed class FixedClock : IClock
    {
        public static FixedClock Instance { get; } = new();
        public DateTimeOffset UtcNow => new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ArchiveWorkspaceRepository(Workspace workspace, IReadOnlyList<WorkspaceMember> members) : IWorkspaceRepository
    {
        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workspace>>([workspace]);

        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(workspace.Id == workspaceId ? workspace : null);

        public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(members.SingleOrDefault(item => item.WorkspaceId == workspaceId && item.UserId == userId));

        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceMember>>(members.Where(item => item.WorkspaceId == workspaceId).ToArray());

        public Task AddAsync(Workspace value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowWorkspaceManagement : IWorkspaceAuthorizationService
    {
        public Task<bool> CanViewWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanContributeWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanManageWorkspace(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanCreateWorkspace(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => "actor@example.invalid";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class NoopAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ArchiveUnitOfWork(RecordingAuthorizationChanges publisher, bool failSave = false) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (failSave)
            {
                throw new InvalidOperationException("Simulated archive rollback.");
            }

            publisher.Commit();
            return Task.FromResult(1);
        }
    }

    private sealed class RecordingAuthorizationChanges : IAuthorizationStateChangePublisher
    {
        private readonly List<AuthorizationChange> _pending = [];
        public List<AuthorizationChange> Committed { get; } = [];

        public Task PublishAsync(
            Guid tenantId,
            Guid affectedUserId,
            string scopeType,
            Guid? scopeId,
            string change,
            CancellationToken cancellationToken = default)
        {
            _pending.Add(new AuthorizationChange(tenantId, affectedUserId, scopeType, scopeId, change));
            return Task.CompletedTask;
        }

        public void Commit()
        {
            Committed.AddRange(_pending);
            _pending.Clear();
        }
    }

    private sealed record AuthorizationChange(Guid TenantId, Guid AffectedUserId, string ScopeType, Guid? ScopeId, string Change);
}
