using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Search;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

public sealed class MessageAdvancedSearchPostgreSqlTests
{
    [Fact]
    [Trait("Scope", "Issue367")]
    public async Task AdvancedMessageFilterValidationFailsClosed()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(Guid.NewGuid(), "issue-367-validation");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"issue-367-validation-{Guid.NewGuid():N}")
            .Options;
        await using var dbContext = new AppDbContext(options, currentTenant);
        var service = new DbSearchService(
            dbContext,
            new TestCurrentUser(Guid.NewGuid()),
            new MessagingRepository(dbContext));

        Assert.False((await service.SearchAsync(new SearchRequest(
            Type: (SearchResultType)999,
            MessageRead: MessageReadFilter.Unread))).IsSuccess);
        Assert.False((await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Project,
            MessageRead: MessageReadFilter.Unread))).IsSuccess);
        Assert.False((await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            MessageAttachment: (MessageAttachmentFilter)999))).IsSuccess);
        Assert.False((await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            AuthorUserId: Guid.Empty))).IsSuccess);
        Assert.False((await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            FromDate: DateTimeOffset.UtcNow,
            ToDate: DateTimeOffset.UtcNow.AddDays(-1)))).IsSuccess);
        Assert.False((await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Project,
            ToDateExclusive: DateTimeOffset.UtcNow))).IsSuccess);
        Assert.False((await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            ToDate: DateTimeOffset.UtcNow,
            ToDateExclusive: DateTimeOffset.UtcNow.AddDays(1)))).IsSuccess);
        Assert.False((await service.SearchMessageAuthorsAsync(
            new MessageAuthorOptionsRequest(Q: "x"))).IsSuccess);
        Assert.False((await service.SearchMessageAuthorsAsync(
            new MessageAuthorOptionsRequest(SelectedUserId: Guid.Empty))).IsSuccess);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "Issue367")]
    public async Task AdvancedFiltersAndAuthorOptionsComposeAuthorizationBeforeProjection()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgreSqlTestEnvironment.RequireConnectionString())
            .Options;
        var currentTenant = new CurrentTenantService();
        var runId = Guid.NewGuid().ToString("N");
        var now = new DateTimeOffset(2026, 8, 30, 2, 0, 0, TimeSpan.Zero);

        await using var dbContext = new AppDbContext(options, currentTenant);
        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());

        var tenant = NewTenant($"Advanced Search {runId}", $"advanced-search-{runId}");
        var otherTenant = NewTenant($"Other Search {runId}", $"other-search-{runId}");
        var actor = NewUser($"advanced-actor-{runId}@example.test", "Advanced Actor");
        var sender = NewUser($"advanced-sender-{runId}@example.test", "Authorized Sender");
        var unprovenAuthor = NewUser($"advanced-unproven-{runId}@example.test", "Unproven Sender");
        var restrictedAuthor = NewUser($"advanced-restricted-{runId}@example.test", "Restricted Sender");
        var scopeMismatchAuthor = NewUser($"advanced-scope-mismatch-{runId}@example.test", "Cross Scope Sender");
        var otherTenantAuthor = NewUser($"advanced-other-{runId}@example.test", "Other Tenant Sender");

        currentTenant.SetPlatformScope();
        dbContext.Tenants.AddRange(tenant, otherTenant);
        dbContext.Users.AddRange(actor, sender, unprovenAuthor, restrictedAuthor, scopeMismatchAuthor, otherTenantAuthor);
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(otherTenant.Id, otherTenant.Slug);
        var otherWorkspace = NewWorkspace(otherTenantAuthor.Id, $"other-workspace-{runId}");
        var otherConversation = NewConversation(otherWorkspace.Id, otherTenantAuthor.Id, "Other Tenant Conversation");
        dbContext.TenantUsers.Add(NewTenantUser(otherTenantAuthor.Id, now));
        dbContext.Workspaces.Add(otherWorkspace);
        dbContext.WorkspaceMembers.Add(NewWorkspaceMember(otherWorkspace.Id, otherTenantAuthor.Id, now));
        dbContext.Conversations.Add(otherConversation);
        dbContext.ConversationMembers.Add(NewConversationMember(otherConversation.Id, otherTenantAuthor.Id, now));
        var otherTenantSecret = NewMessage(
            otherWorkspace.Id,
            otherConversation.Id,
            otherTenantAuthor.Id,
            "tenant-b-secret-marker",
            now.AddMinutes(20));
        dbContext.Messages.Add(otherTenantSecret);
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var workspace = NewWorkspace(actor.Id, $"advanced-workspace-{runId}");
        var mismatchedWorkspace = NewWorkspace(actor.Id, $"advanced-mismatched-workspace-{runId}");
        var readableConversation = NewConversation(workspace.Id, actor.Id, "Authorized Conversation");
        var restrictedConversation = NewConversation(workspace.Id, restrictedAuthor.Id, "Restricted Conversation");
        dbContext.TenantUsers.AddRange(
            NewTenantUser(actor.Id, now),
            NewTenantUser(sender.Id, now),
            NewTenantUser(unprovenAuthor.Id, now),
            NewTenantUser(restrictedAuthor.Id, now),
            NewTenantUser(scopeMismatchAuthor.Id, now));
        dbContext.Workspaces.AddRange(workspace, mismatchedWorkspace);
        dbContext.WorkspaceMembers.AddRange(
            NewWorkspaceMember(workspace.Id, actor.Id, now),
            NewWorkspaceMember(workspace.Id, sender.Id, now),
            NewWorkspaceMember(workspace.Id, unprovenAuthor.Id, now),
            NewWorkspaceMember(workspace.Id, restrictedAuthor.Id, now),
            NewWorkspaceMember(workspace.Id, scopeMismatchAuthor.Id, now),
            NewWorkspaceMember(mismatchedWorkspace.Id, scopeMismatchAuthor.Id, now));
        dbContext.Conversations.AddRange(readableConversation, restrictedConversation);
        dbContext.ConversationMembers.AddRange(
            NewConversationMember(readableConversation.Id, actor.Id, now),
            NewConversationMember(readableConversation.Id, sender.Id, now),
            NewConversationMember(readableConversation.Id, scopeMismatchAuthor.Id, now),
            NewConversationMember(restrictedConversation.Id, restrictedAuthor.Id, now));

        var ownMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            actor.Id,
            "own-read-marker",
            now);
        var readMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            sender.Id,
            "sender-read-marker",
            now.AddMinutes(1));
        var unreadMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            sender.Id,
            "sender-unread-marker",
            now.AddMinutes(3));
        var canonicalAttachmentMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            sender.Id,
            "canonical-attachment-marker",
            now.AddMinutes(4));
        var malformedAttachmentMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            sender.Id,
            "malformed-attachment-marker",
            now.AddMinutes(5));
        var corruptAuthorMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            otherTenantAuthor.Id,
            "corrupt-author-marker",
            now.AddMinutes(6));
        var unprovenAuthorMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            unprovenAuthor.Id,
            "unproven-author-marker",
            now.AddMinutes(6).AddSeconds(30));
        var tiedFirstMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            sender.Id,
            "tied-cursor-marker-first",
            now.AddMinutes(7));
        tiedFirstMessage.Id = Guid.ParseExact($"{runId[..30]}01", "N");
        var tiedSecondMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            sender.Id,
            "tied-cursor-marker-second",
            now.AddMinutes(7));
        tiedSecondMessage.Id = Guid.ParseExact($"{runId[..30]}02", "N");
        var dayEndExclusive = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var beforeDayEndMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            sender.Id,
            "date-boundary-marker-before",
            dayEndExclusive.AddTicks(-10));
        var atDayEndMessage = NewMessage(
            workspace.Id,
            readableConversation.Id,
            sender.Id,
            "date-boundary-marker-at",
            dayEndExclusive);
        var crossWorkspaceMessage = NewMessage(
            mismatchedWorkspace.Id,
            readableConversation.Id,
            scopeMismatchAuthor.Id,
            "cross-workspace-secret-marker",
            now.AddMinutes(11));
        var restrictedMessage = NewMessage(
            workspace.Id,
            restrictedConversation.Id,
            restrictedAuthor.Id,
            "restricted-secret-marker",
            now.AddMinutes(10));
        dbContext.Messages.AddRange(
            ownMessage,
            readMessage,
            unreadMessage,
            canonicalAttachmentMessage,
            malformedAttachmentMessage,
            corruptAuthorMessage,
            unprovenAuthorMessage,
            tiedFirstMessage,
            tiedSecondMessage,
            beforeDayEndMessage,
            atDayEndMessage,
            crossWorkspaceMessage,
            restrictedMessage);
        dbContext.ReadStates.Add(new ReadState
        {
            UserId = actor.Id,
            ScopeType = ReadScopeType.Conversation,
            ScopeId = readableConversation.Id,
            ConversationId = readableConversation.Id,
            LastReadMessageId = readMessage.Id,
            LastReadItemId = readMessage.Id,
            LastReadSequence = now.AddMinutes(1).UtcTicks,
            // Deliberately later than every normal Message: action time must
            // never stand in for the server-validated Message cursor.
            LastReadAt = now.AddHours(4)
        });

        var canonicalFile = NewFile(workspace.Id, sender.Id, "canonical.txt");
        var malformedFile = NewFile(workspace.Id, sender.Id, "malformed.txt");
        var pendingFile = NewFile(workspace.Id, sender.Id, "pending.txt");
        var infectedFile = NewFile(workspace.Id, sender.Id, "infected.txt");
        var failedFile = NewFile(workspace.Id, sender.Id, "failed.txt");
        var unclassifiedFile = NewFile(workspace.Id, sender.Id, "unclassified.txt");
        unclassifiedFile.Classification = null;
        var unknownSensitiveFile = NewFile(workspace.Id, sender.Id, "unknown-sensitive.txt");
        unknownSensitiveFile.Classification = DataClassification.UnknownSensitive;
        var canonicalAttachment = NewAttachment(
            workspace.Id,
            sender.Id,
            canonicalFile,
            AttachmentOwnerType.Message,
            canonicalAttachmentMessage.Id);
        var wrongOwnerAttachment = NewAttachment(
            workspace.Id,
            sender.Id,
            malformedFile,
            AttachmentOwnerType.Workspace,
            workspace.Id);
        var pendingAttachment = NewAttachment(
            workspace.Id,
            sender.Id,
            pendingFile,
            AttachmentOwnerType.Message,
            malformedAttachmentMessage.Id);
        pendingAttachment.ScanStatus = FileScanStatus.Pending;
        var infectedAttachment = NewAttachment(
            workspace.Id,
            sender.Id,
            infectedFile,
            AttachmentOwnerType.Message,
            malformedAttachmentMessage.Id);
        infectedAttachment.ScanStatus = FileScanStatus.Infected;
        var failedAttachment = NewAttachment(
            workspace.Id,
            sender.Id,
            failedFile,
            AttachmentOwnerType.Message,
            malformedAttachmentMessage.Id);
        failedAttachment.ScanStatus = FileScanStatus.Failed;
        var unclassifiedAttachment = NewAttachment(
            workspace.Id,
            sender.Id,
            unclassifiedFile,
            AttachmentOwnerType.Message,
            malformedAttachmentMessage.Id);
        var unknownSensitiveAttachment = NewAttachment(
            workspace.Id,
            sender.Id,
            unknownSensitiveFile,
            AttachmentOwnerType.Message,
            malformedAttachmentMessage.Id);
        dbContext.FileObjects.AddRange(
            canonicalFile,
            malformedFile,
            pendingFile,
            infectedFile,
            failedFile,
            unclassifiedFile,
            unknownSensitiveFile);
        dbContext.Attachments.AddRange(
            canonicalAttachment,
            wrongOwnerAttachment,
            pendingAttachment,
            infectedAttachment,
            failedAttachment,
            unclassifiedAttachment,
            unknownSensitiveAttachment);
        dbContext.MessageAttachments.AddRange(
            new MessageAttachment
            {
                MessageId = canonicalAttachmentMessage.Id,
                AttachmentId = canonicalAttachment.Id
            },
            new MessageAttachment
            {
                MessageId = malformedAttachmentMessage.Id,
                AttachmentId = wrongOwnerAttachment.Id
            },
            new MessageAttachment
            {
                MessageId = malformedAttachmentMessage.Id,
                AttachmentId = pendingAttachment.Id
            },
            new MessageAttachment
            {
                MessageId = malformedAttachmentMessage.Id,
                AttachmentId = infectedAttachment.Id
            },
            new MessageAttachment
            {
                MessageId = malformedAttachmentMessage.Id,
                AttachmentId = failedAttachment.Id
            },
            new MessageAttachment
            {
                MessageId = malformedAttachmentMessage.Id,
                AttachmentId = unclassifiedAttachment.Id
            },
            new MessageAttachment
            {
                MessageId = malformedAttachmentMessage.Id,
                AttachmentId = unknownSensitiveAttachment.Id
            });

        await dbContext.SaveChangesAsync();
        // AppDbContext intentionally stamps Added auditable rows with the
        // persistence time. Set the deterministic ordering tokens through
        // provider updates so this test exercises read/date predicates rather
        // than depending on wall-clock timing between SaveChanges calls.
        await SetMessageCreatedAtAsync(dbContext, ownMessage.Id, now);
        await SetMessageCreatedAtAsync(dbContext, readMessage.Id, now.AddMinutes(1));
        await SetMessageCreatedAtAsync(dbContext, unreadMessage.Id, now.AddMinutes(3));
        await SetMessageCreatedAtAsync(dbContext, canonicalAttachmentMessage.Id, now.AddMinutes(4));
        await SetMessageCreatedAtAsync(dbContext, malformedAttachmentMessage.Id, now.AddMinutes(5));
        await SetMessageCreatedAtAsync(dbContext, corruptAuthorMessage.Id, now.AddMinutes(6));
        await SetMessageCreatedAtAsync(dbContext, unprovenAuthorMessage.Id, now.AddMinutes(6).AddSeconds(30));
        await SetMessageCreatedAtAsync(dbContext, tiedFirstMessage.Id, now.AddMinutes(7));
        await SetMessageCreatedAtAsync(dbContext, tiedSecondMessage.Id, now.AddMinutes(7));
        await SetMessageCreatedAtAsync(dbContext, beforeDayEndMessage.Id, dayEndExclusive.AddTicks(-10));
        await SetMessageCreatedAtAsync(dbContext, atDayEndMessage.Id, dayEndExclusive);
        await SetMessageCreatedAtAsync(dbContext, crossWorkspaceMessage.Id, now.AddMinutes(11));
        await SetMessageCreatedAtAsync(dbContext, restrictedMessage.Id, now.AddMinutes(10));
        dbContext.ChangeTracker.Clear();

        var service = new DbSearchService(
            dbContext,
            new TestCurrentUser(actor.Id),
            new MessagingRepository(dbContext));

        var fuzzedUpperBound = DateTimeOffset.Parse(
            "2873-10-04T06:33:40-06:53",
            System.Globalization.CultureInfo.InvariantCulture);
        var offsetSearch = await service.SearchAsync(new SearchRequest(
            WorkspaceId: workspace.Id,
            ToDate: fuzzedUpperBound,
            PageSize: 50));
        var utcSearch = await service.SearchAsync(new SearchRequest(
            WorkspaceId: workspace.Id,
            ToDate: fuzzedUpperBound.ToUniversalTime(),
            PageSize: 50));
        Assert.True(offsetSearch.IsSuccess, offsetSearch.Error);
        Assert.True(utcSearch.IsSuccess, utcSearch.Error);
        Assert.Equal(utcSearch.Value!.TotalCount, offsetSearch.Value!.TotalCount);
        Assert.Equal(
            utcSearch.Value.Items.Select(item => item.Id),
            offsetSearch.Value.Items.Select(item => item.Id));

        var read = await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            PageSize: 50,
            MessageRead: MessageReadFilter.Read));
        Assert.True(read.IsSuccess, read.Error);
        Assert.Equal(
            new[] { readMessage.Id, ownMessage.Id }.OrderBy(id => id),
            read.Value!.Items.Select(item => item.Id).OrderBy(id => id));

        var unread = await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            FromDate: now.AddMinutes(3),
            ToDate: now.AddMinutes(5),
            PageSize: 50,
            MessageRead: MessageReadFilter.Unread));
        Assert.True(unread.IsSuccess, unread.Error);
        Assert.Equal(
            new[] { unreadMessage.Id, canonicalAttachmentMessage.Id, malformedAttachmentMessage.Id }.OrderBy(id => id),
            unread.Value!.Items.Select(item => item.Id).OrderBy(id => id));

        TimeSpan[] offsets = [TimeSpan.FromHours(9), TimeSpan.FromMinutes(-413)];
        foreach (var offset in offsets)
        {
            var offsetUnread = await service.SearchAsync(new SearchRequest(
                Type: SearchResultType.Message,
                WorkspaceId: workspace.Id,
                FromDate: now.AddMinutes(3).ToOffset(offset),
                ToDate: now.AddMinutes(5).ToOffset(offset),
                PageSize: 50,
                MessageRead: MessageReadFilter.Unread));
            Assert.True(offsetUnread.IsSuccess, offsetUnread.Error);
            Assert.Equal(unread.Value!.TotalCount, offsetUnread.Value!.TotalCount);
            Assert.Equal(
                unread.Value.Items.Select(item => item.Id),
                offsetUnread.Value.Items.Select(item => item.Id));
        }

        var allUnread = await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            PageSize: 50,
            MessageRead: MessageReadFilter.Unread));
        Assert.True(allUnread.IsSuccess, allUnread.Error);
        Assert.Equal(
            new[]
            {
                unreadMessage.Id,
                canonicalAttachmentMessage.Id,
                malformedAttachmentMessage.Id,
                corruptAuthorMessage.Id,
                unprovenAuthorMessage.Id,
                tiedFirstMessage.Id,
                tiedSecondMessage.Id,
                beforeDayEndMessage.Id,
                atDayEndMessage.Id
            }.OrderBy(id => id),
            allUnread.Value!.Items.Select(item => item.Id).OrderBy(id => id));

        var allMessages = await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            PageSize: 50));
        Assert.True(allMessages.IsSuccess, allMessages.Error);
        var authorizedIds = new[]
        {
            ownMessage.Id,
            readMessage.Id,
            unreadMessage.Id,
            canonicalAttachmentMessage.Id,
            malformedAttachmentMessage.Id,
            corruptAuthorMessage.Id,
            unprovenAuthorMessage.Id,
            tiedFirstMessage.Id,
            tiedSecondMessage.Id,
            beforeDayEndMessage.Id,
            atDayEndMessage.Id
        };
        Assert.Equal(authorizedIds.OrderBy(id => id), allMessages.Value!.Items.Select(item => item.Id).OrderBy(id => id));
        Assert.Empty(read.Value!.Items.Select(item => item.Id).Intersect(allUnread.Value.Items.Select(item => item.Id)));
        Assert.Equal(
            authorizedIds.OrderBy(id => id),
            read.Value.Items.Concat(allUnread.Value.Items).Select(item => item.Id).OrderBy(id => id));

        var readState = await dbContext.ReadStates.SingleAsync(state =>
            state.UserId == actor.Id && state.ConversationId == readableConversation.Id);
        readState.LastReadMessageId = tiedFirstMessage.Id;
        readState.LastReadItemId = tiedFirstMessage.Id;
        // A cursor Message remains authoritative for migrated rows whose
        // sequence token still has the legacy default.
        readState.LastReadSequence = 0;
        readState.LastReadAt = now.AddHours(5);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var tiedOrder = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            PageSize: 50));
        Assert.True(tiedOrder.IsSuccess, tiedOrder.Error);
        Assert.Equal(
            new[] { tiedFirstMessage.Id, tiedSecondMessage.Id },
            tiedOrder.Value!.Items.Select(item => item.Id));

        var tiedReadAfterFirst = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            MessageRead: MessageReadFilter.Read,
            PageSize: 50));
        Assert.True(tiedReadAfterFirst.IsSuccess, tiedReadAfterFirst.Error);
        Assert.Equal(tiedFirstMessage.Id, Assert.Single(tiedReadAfterFirst.Value!.Items).Id);

        var tiedUnreadAfterFirst = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            MessageRead: MessageReadFilter.Unread,
            PageSize: 50));
        Assert.True(tiedUnreadAfterFirst.IsSuccess, tiedUnreadAfterFirst.Error);
        Assert.Equal(tiedSecondMessage.Id, Assert.Single(tiedUnreadAfterFirst.Value!.Items).Id);
        Assert.Empty(tiedReadAfterFirst.Value.Items.Select(item => item.Id)
            .Intersect(tiedUnreadAfterFirst.Value.Items.Select(item => item.Id)));

        readState = await dbContext.ReadStates.SingleAsync(state =>
            state.UserId == actor.Id && state.ConversationId == readableConversation.Id);
        readState.LastReadMessageId = tiedSecondMessage.Id;
        readState.LastReadItemId = tiedSecondMessage.Id;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var tiedReadAfterSecond = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            MessageRead: MessageReadFilter.Read,
            PageSize: 50));
        Assert.True(tiedReadAfterSecond.IsSuccess, tiedReadAfterSecond.Error);
        Assert.Equal(
            new[] { tiedFirstMessage.Id, tiedSecondMessage.Id },
            tiedReadAfterSecond.Value!.Items.Select(item => item.Id));
        var tiedUnreadAfterSecond = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            MessageRead: MessageReadFilter.Unread,
            PageSize: 50));
        Assert.True(tiedUnreadAfterSecond.IsSuccess, tiedUnreadAfterSecond.Error);
        Assert.Empty(tiedUnreadAfterSecond.Value!.Items);

        readState = await dbContext.ReadStates.SingleAsync(state =>
            state.UserId == actor.Id && state.ConversationId == readableConversation.Id);
        readState.LastReadMessageId = null;
        readState.LastReadItemId = null;
        readState.LastReadSequence = 0;
        readState.LastReadAt = now.AddMinutes(7);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var tiedReadWithLegacyTimestamp = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            MessageRead: MessageReadFilter.Read,
            PageSize: 50));
        var tiedUnreadWithLegacyTimestamp = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            MessageRead: MessageReadFilter.Unread,
            PageSize: 50));
        Assert.True(tiedReadWithLegacyTimestamp.IsSuccess, tiedReadWithLegacyTimestamp.Error);
        Assert.True(tiedUnreadWithLegacyTimestamp.IsSuccess, tiedUnreadWithLegacyTimestamp.Error);
        Assert.Equal(
            new[] { tiedFirstMessage.Id, tiedSecondMessage.Id },
            tiedReadWithLegacyTimestamp.Value!.Items.Select(item => item.Id));
        Assert.Empty(tiedUnreadWithLegacyTimestamp.Value!.Items);

        readState = await dbContext.ReadStates.SingleAsync(state =>
            state.UserId == actor.Id && state.ConversationId == readableConversation.Id);
        // Same ConversationId but a different Workspace must not be accepted
        // as a cursor for the readable Conversation.
        readState.LastReadMessageId = crossWorkspaceMessage.Id;
        readState.LastReadItemId = crossWorkspaceMessage.Id;
        readState.LastReadAt = now.AddHours(8);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var tiedReadWithMismatchedCursor = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            MessageRead: MessageReadFilter.Read,
            PageSize: 50));
        var tiedUnreadWithMismatchedCursor = await service.SearchAsync(new SearchRequest(
            Q: "tied-cursor-marker",
            Type: SearchResultType.Message,
            MessageRead: MessageReadFilter.Unread,
            PageSize: 50));
        Assert.True(tiedReadWithMismatchedCursor.IsSuccess, tiedReadWithMismatchedCursor.Error);
        Assert.True(tiedUnreadWithMismatchedCursor.IsSuccess, tiedUnreadWithMismatchedCursor.Error);
        Assert.Empty(tiedReadWithMismatchedCursor.Value!.Items);
        Assert.Equal(
            new[] { tiedFirstMessage.Id, tiedSecondMessage.Id },
            tiedUnreadWithMismatchedCursor.Value!.Items.Select(item => item.Id));

        var dateBoundary = await service.SearchAsync(new SearchRequest(
            Q: "date-boundary-marker",
            Type: SearchResultType.Message,
            FromDate: new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
            ToDateExclusive: dayEndExclusive,
            PageSize: 50));
        Assert.True(dateBoundary.IsSuccess, dateBoundary.Error);
        Assert.Equal(beforeDayEndMessage.Id, Assert.Single(dateBoundary.Value!.Items).Id);

        foreach (var offset in offsets)
        {
            var offsetBoundary = await service.SearchAsync(new SearchRequest(
                Q: "date-boundary-marker",
                Type: SearchResultType.Message,
                FromDate: new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero).ToOffset(offset),
                ToDateExclusive: dayEndExclusive.ToOffset(offset),
                PageSize: 50));
            Assert.True(offsetBoundary.IsSuccess, offsetBoundary.Error);
            Assert.Equal(beforeDayEndMessage.Id, Assert.Single(offsetBoundary.Value!.Items).Id);
            Assert.Equal(dateBoundary.Value.TotalCount, offsetBoundary.Value.TotalCount);
        }

        var withAttachment = await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            PageSize: 50,
            MessageAttachment: MessageAttachmentFilter.With));
        Assert.True(withAttachment.IsSuccess, withAttachment.Error);
        Assert.Equal(canonicalAttachmentMessage.Id, Assert.Single(withAttachment.Value!.Items).Id);
        Assert.Equal(1, withAttachment.Value.TotalCount);

        var withoutAttachment = await service.SearchAsync(new SearchRequest(
            Q: "attachment-marker",
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            PageSize: 50,
            MessageAttachment: MessageAttachmentFilter.Without));
        Assert.True(withoutAttachment.IsSuccess, withoutAttachment.Error);
        Assert.Equal(malformedAttachmentMessage.Id, Assert.Single(withoutAttachment.Value!.Items).Id);

        var fromSender = await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            AuthorUserId: sender.Id,
            PageSize: 50));
        Assert.True(fromSender.IsSuccess, fromSender.Error);
        Assert.Equal(8, fromSender.Value!.TotalCount);
        Assert.All(fromSender.Value.Items, item => Assert.Equal("Authorized Sender", item.AuthorDisplayName));

        var corruptAuthor = await service.SearchAsync(new SearchRequest(
            Q: "corrupt-author-marker",
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            PageSize: 50));
        Assert.True(corruptAuthor.IsSuccess, corruptAuthor.Error);
        Assert.Null(Assert.Single(corruptAuthor.Value!.Items).AuthorDisplayName);

        var corruptAuthorFilter = await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            AuthorUserId: otherTenantAuthor.Id,
            PageSize: 50));
        Assert.True(corruptAuthorFilter.IsSuccess, corruptAuthorFilter.Error);
        Assert.Empty(corruptAuthorFilter.Value!.Items);
        Assert.Equal(0, corruptAuthorFilter.Value.TotalCount);

        var unprovenAuthorResult = await service.SearchAsync(new SearchRequest(
            Q: "unproven-author-marker",
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            PageSize: 50));
        Assert.True(unprovenAuthorResult.IsSuccess, unprovenAuthorResult.Error);
        Assert.Null(Assert.Single(unprovenAuthorResult.Value!.Items).AuthorDisplayName);

        var unprovenAuthorFilter = await service.SearchAsync(new SearchRequest(
            Type: SearchResultType.Message,
            WorkspaceId: workspace.Id,
            AuthorUserId: unprovenAuthor.Id,
            PageSize: 50));
        Assert.True(unprovenAuthorFilter.IsSuccess, unprovenAuthorFilter.Error);
        Assert.Empty(unprovenAuthorFilter.Value!.Items);
        Assert.Equal(0, unprovenAuthorFilter.Value.TotalCount);

        var scopeMismatch = await service.SearchAsync(new SearchRequest(
            Q: "cross-workspace-secret-marker",
            Type: SearchResultType.Message,
            PageSize: 50));
        Assert.True(scopeMismatch.IsSuccess, scopeMismatch.Error);
        Assert.Empty(scopeMismatch.Value!.Items);
        Assert.Equal(0, scopeMismatch.Value.TotalCount);

        foreach (var secret in new[] { "restricted-secret-marker", "tenant-b-secret-marker" })
        {
            var denied = await service.SearchAsync(new SearchRequest(
                Q: secret,
                Type: SearchResultType.Message,
                PageSize: 50));
            Assert.True(denied.IsSuccess, denied.Error);
            Assert.Empty(denied.Value!.Items);
            Assert.Equal(0, denied.Value.TotalCount);
        }

        var authorOptions = await service.SearchMessageAuthorsAsync(
            new MessageAuthorOptionsRequest(Q: "Authorized"));
        Assert.True(authorOptions.IsSuccess, authorOptions.Error);
        var authorizedOption = Assert.Single(authorOptions.Value!.Items);
        Assert.Equal(sender.Id, authorizedOption.UserId);
        Assert.Equal("Authorized Sender", authorizedOption.DisplayName);

        var restrictedOptions = await service.SearchMessageAuthorsAsync(
            new MessageAuthorOptionsRequest(Q: "Restricted"));
        Assert.True(restrictedOptions.IsSuccess, restrictedOptions.Error);
        Assert.Empty(restrictedOptions.Value!.Items);

        var scopeMismatchOptions = await service.SearchMessageAuthorsAsync(
            new MessageAuthorOptionsRequest(Q: "Cross Scope"));
        Assert.True(scopeMismatchOptions.IsSuccess, scopeMismatchOptions.Error);
        Assert.Empty(scopeMismatchOptions.Value!.Items);

        var unprovenOptions = await service.SearchMessageAuthorsAsync(
            new MessageAuthorOptionsRequest(Q: "Unproven"));
        Assert.True(unprovenOptions.IsSuccess, unprovenOptions.Error);
        Assert.Empty(unprovenOptions.Value!.Items);

        var crossTenantOption = await service.SearchMessageAuthorsAsync(
            new MessageAuthorOptionsRequest(SelectedUserId: otherTenantAuthor.Id));
        Assert.True(crossTenantOption.IsSuccess, crossTenantOption.Error);
        Assert.Empty(crossTenantOption.Value!.Items);

        var selectedSender = await service.SearchMessageAuthorsAsync(
            new MessageAuthorOptionsRequest(SelectedUserId: sender.Id));
        Assert.True(selectedSender.IsSuccess, selectedSender.Error);
        Assert.Equal(sender.Id, Assert.Single(selectedSender.Value!.Items).UserId);
    }

    private static Tenant NewTenant(string name, string slug) => new()
    {
        Name = name,
        DisplayName = name,
        Slug = slug
    };

    private static User NewUser(string email, string displayName) => new()
    {
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = displayName,
        Status = UserStatus.Active
    };

    private static TenantUser NewTenantUser(Guid userId, DateTimeOffset joinedAt) => new()
    {
        UserId = userId,
        Role = TenantUserRole.Member,
        Status = TenantUserStatus.Active,
        JoinedAt = joinedAt
    };

    private static Workspace NewWorkspace(Guid creatorUserId, string slug) => new()
    {
        Name = "Advanced Search Workspace",
        Slug = slug,
        CreatedByUserId = creatorUserId
    };

    private static WorkspaceMember NewWorkspaceMember(
        Guid workspaceId,
        Guid userId,
        DateTimeOffset joinedAt) => new()
    {
        WorkspaceId = workspaceId,
        UserId = userId,
        Role = WorkspaceRole.Member,
        Status = MembershipStatus.Active,
        JoinedAt = joinedAt
    };

    private static Conversation NewConversation(
        Guid workspaceId,
        Guid creatorUserId,
        string title) => new()
    {
        WorkspaceId = workspaceId,
        Type = ConversationType.DirectMessage,
        Title = title,
        CreatedByUserId = creatorUserId
    };

    private static ConversationMember NewConversationMember(
        Guid conversationId,
        Guid userId,
        DateTimeOffset joinedAt) => new()
    {
        ConversationId = conversationId,
        UserId = userId,
        JoinedAt = joinedAt
    };

    private static Message NewMessage(
        Guid workspaceId,
        Guid conversationId,
        Guid authorUserId,
        string body,
        DateTimeOffset createdAt) => new()
    {
        WorkspaceId = workspaceId,
        ConversationId = conversationId,
        AuthorUserId = authorUserId,
        Body = body,
        CreatedAt = createdAt
    };

    private static FileObject NewFile(Guid workspaceId, Guid uploaderUserId, string name) => new()
    {
        WorkspaceId = workspaceId,
        UploadedByUserId = uploaderUserId,
        OriginalFileName = name,
        StorageKey = $"issue-367/{Guid.NewGuid():N}/{name}",
        ContentType = "text/plain",
        SizeBytes = 128,
        Classification = DataClassification.Private,
        Status = FileObjectStatus.Active
    };

    private static Attachment NewAttachment(
        Guid workspaceId,
        Guid userId,
        FileObject file,
        AttachmentOwnerType ownerType,
        Guid ownerId) => new()
    {
        FileObjectId = file.Id,
        WorkspaceId = workspaceId,
        OwnerType = ownerType,
        OwnerId = ownerId,
        OwnerUserId = userId,
        UploadedByUserId = userId,
        FileName = file.OriginalFileName,
        StoredFileName = file.OriginalFileName,
        FilePath = $"/issue-367/{file.Id:D}",
        ContentType = file.ContentType,
        Extension = ".txt",
        SizeBytes = file.SizeBytes,
        StorageProvider = "test",
        StorageKey = file.StorageKey,
        ScanStatus = FileScanStatus.Clean
    };

    private static Task SetMessageCreatedAtAsync(
        AppDbContext dbContext,
        Guid messageId,
        DateTimeOffset createdAt) =>
        dbContext.Messages
            .Where(message => message.Id == messageId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.CreatedAt, createdAt));

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => "issue-367@example.test";
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => true;
    }
}
