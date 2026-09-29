using System.Text.Json;
using Coglatas.Application;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Messaging;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.PostgreSql;

[Trait("Scope", "Issue528")]
public sealed class MessageCanonicalAttachmentPostgreSqlTests
{
    [Fact]
    public void SendMessageRequestRejectsClientSuppliedStorageMetadata()
    {
        var attachmentId = Guid.NewGuid();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var canonical = JsonSerializer.Deserialize<SendMessageRequest>(
            $$"""
            {
              "body": "canonical",
              "attachments": [
                { "attachmentId": "{{attachmentId}}" }
              ]
            }
            """,
            options);

        Assert.NotNull(canonical);
        Assert.Equal(attachmentId, Assert.Single(canonical.Attachments!).AttachmentId);

        var malicious = $$"""
            {
              "body": "malicious",
              "attachments": [
                {
                  "attachmentId": "{{attachmentId}}",
                  "storedFileName": "browser-owned-name",
                  "filePath": "../../outside-root",
                  "storageKey": "tenant-b/secret"
                }
              ]
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SendMessageRequest>(malicious, options));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task AuthorizedCanonicalAttachmentPersistsServerMetadataAndReplayCreatesOneRelation()
    {
        var baseConnectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(baseConnectionString, async connectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(connectionString);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(connectionString, $"issue528-{Guid.NewGuid():N}");
            await using var fixture = CreateFixture(connectionString, graph);

            var conversation = await fixture.SeedConversationAsync(graph.WorkspaceId, graph.UserId);
            var sourceMessage = await fixture.SeedMessageAsync(conversation, graph.UserId, "canonical source");
            var fileObject = await fixture.SeedFileObjectAsync(
                graph.WorkspaceId,
                graph.UserId,
                FileObjectStatus.Active,
                "canonical-report.txt",
                "tenant/server/canonical-report.txt");
            var firstSource = await fixture.SeedMessageAttachmentAsync(
                sourceMessage,
                fileObject,
                graph.UserId,
                FileScanStatus.Clean,
                compatibilityFileName: "legacy-browser-name.bin",
                compatibilityContentType: "application/x-client-metadata",
                compatibilitySizeBytes: 1,
                compatibilityStorageKey: "legacy/client/key");
            var duplicateSource = await fixture.SeedMessageAttachmentAsync(
                sourceMessage,
                fileObject,
                graph.UserId,
                FileScanStatus.Clean,
                compatibilityFileName: "second-browser-name.bin",
                compatibilityContentType: "application/x-second-client-value",
                compatibilitySizeBytes: 2,
                compatibilityStorageKey: "legacy/client/key-2");
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();

            var clientRequestId = Guid.NewGuid();
            var request = new SendMessageRequest(
                "send canonical attachment",
                [
                    new MessageAttachmentReferenceRequest(firstSource.Id),
                    new MessageAttachmentReferenceRequest(duplicateSource.Id)
                ],
                clientRequestId);

            var first = await fixture.Service.SendMessageAsync(conversation.Id, request);
            Assert.True(first.IsSuccess, first.Error);

            var replay = await fixture.Service.SendMessageAsync(conversation.Id, request);
            Assert.True(replay.IsSuccess, replay.Error);
            Assert.Equal(first.Value!.Id, replay.Value!.Id);

            fixture.Db.ChangeTracker.Clear();
            var persisted = await fixture.Db.Messages
                .AsNoTracking()
                .Include(message => message.Attachments)
                .ThenInclude(link => link.Attachment)
                .SingleAsync(message =>
                    message.ConversationId == conversation.Id &&
                    message.AuthorUserId == graph.UserId &&
                    message.ClientRequestId == clientRequestId);
            var relation = Assert.Single(persisted.Attachments);
            Assert.NotNull(relation.Attachment);
            var persistedAttachment = relation.Attachment!;

            Assert.Equal(graph.TenantId, relation.TenantId);
            Assert.Equal(graph.TenantId, persistedAttachment.TenantId);
            Assert.Equal(graph.WorkspaceId, persistedAttachment.WorkspaceId);
            Assert.Equal(fileObject.Id, persistedAttachment.FileObjectId);
            Assert.Equal(AttachmentOwnerType.Message, persistedAttachment.OwnerType);
            Assert.Equal(persisted.Id, persistedAttachment.OwnerId);
            Assert.Equal(fileObject.OriginalFileName, persistedAttachment.FileName);
            Assert.Equal(fileObject.ContentType, persistedAttachment.ContentType);
            Assert.Equal(fileObject.SizeBytes, persistedAttachment.SizeBytes);
            Assert.Equal(fileObject.StorageKey, persistedAttachment.StorageKey);
            Assert.Equal(fileObject.StorageKey, persistedAttachment.FilePath);
            Assert.Equal(fileObject.Id.ToString("N"), persistedAttachment.StoredFileName);
            Assert.Equal(1, await fixture.Db.MessageAttachments.AsNoTracking().CountAsync(link => link.MessageId == persisted.Id));
            Assert.Equal(1, await fixture.Db.Attachments.AsNoTracking().CountAsync(attachment =>
                attachment.OwnerType == AttachmentOwnerType.Message &&
                attachment.OwnerId == persisted.Id &&
                attachment.FileObjectId == fileObject.Id));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task MissingDeletedQuarantinedAndWrongConversationAttachmentsFailClosedWithoutMetadataLeak()
    {
        var baseConnectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(baseConnectionString, async connectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(connectionString);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(connectionString, $"issue528-negative-{Guid.NewGuid():N}");
            await using var fixture = CreateFixture(connectionString, graph);

            var targetConversation = await fixture.SeedConversationAsync(graph.WorkspaceId, graph.UserId);
            var otherConversation = await fixture.SeedConversationAsync(graph.WorkspaceId, graph.UserId);
            var targetSourceMessage = await fixture.SeedMessageAsync(targetConversation, graph.UserId, "target source");
            var otherSourceMessage = await fixture.SeedMessageAsync(otherConversation, graph.UserId, "other source");

            var deletedFile = await fixture.SeedFileObjectAsync(
                graph.WorkspaceId,
                graph.UserId,
                FileObjectStatus.Deleted,
                "deleted-secret.txt",
                "tenant/server/deleted-secret.txt");
            var deletedSource = await fixture.SeedMessageAttachmentAsync(
                targetSourceMessage,
                deletedFile,
                graph.UserId,
                FileScanStatus.Clean);

            var quarantinedFile = await fixture.SeedFileObjectAsync(
                graph.WorkspaceId,
                graph.UserId,
                FileObjectStatus.Quarantined,
                "quarantined-secret.txt",
                "tenant/server/quarantined-secret.txt");
            var quarantinedSource = await fixture.SeedMessageAttachmentAsync(
                targetSourceMessage,
                quarantinedFile,
                graph.UserId,
                FileScanStatus.Pending);

            var wrongConversationFile = await fixture.SeedFileObjectAsync(
                graph.WorkspaceId,
                graph.UserId,
                FileObjectStatus.Active,
                "other-conversation-secret.txt",
                "tenant/server/other-conversation-secret.txt");
            var wrongConversationSource = await fixture.SeedMessageAttachmentAsync(
                otherSourceMessage,
                wrongConversationFile,
                graph.UserId,
                FileScanStatus.Clean);
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();

            await AssertDeniedAsync(fixture, targetConversation.Id, Guid.NewGuid(), Guid.NewGuid(), "missing");
            await AssertDeniedAsync(fixture, targetConversation.Id, deletedSource.Id, Guid.NewGuid(), "deleted-secret.txt", deletedFile.StorageKey);
            await AssertDeniedAsync(fixture, targetConversation.Id, quarantinedSource.Id, Guid.NewGuid(), "quarantined-secret.txt", quarantinedFile.StorageKey);
            await AssertDeniedAsync(fixture, targetConversation.Id, wrongConversationSource.Id, Guid.NewGuid(), "other-conversation-secret.txt", wrongConversationFile.StorageKey);

            fixture.Db.ChangeTracker.Clear();
            Assert.Equal(0, await fixture.Db.Messages.AsNoTracking().CountAsync(message => message.Body.StartsWith("issue528 denied")));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task CrossTenantAndWrongWorkspaceAttachmentsAreDenied()
    {
        var baseConnectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(baseConnectionString, async connectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(connectionString);
            var graphA = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(connectionString, $"issue528-a-{Guid.NewGuid():N}");
            var graphB = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(connectionString, $"issue528-b-{Guid.NewGuid():N}");
            await using var fixture = CreateFixture(connectionString, graphA);

            var targetConversation = await fixture.SeedConversationAsync(graphA.WorkspaceId, graphA.UserId);

            var wrongWorkspace = await fixture.SeedWorkspaceAsync(graphA.TenantId, graphA.UserId, "wrong-workspace");
            var wrongWorkspaceConversation = await fixture.SeedConversationAsync(wrongWorkspace.Id, graphA.UserId);
            var wrongWorkspaceMessage = await fixture.SeedMessageAsync(wrongWorkspaceConversation, graphA.UserId, "wrong workspace source");
            var wrongWorkspaceFile = await fixture.SeedFileObjectAsync(
                wrongWorkspace.Id,
                graphA.UserId,
                FileObjectStatus.Active,
                "wrong-workspace-secret.txt",
                "tenant/server/wrong-workspace-secret.txt");
            var wrongWorkspaceSource = await fixture.SeedMessageAttachmentAsync(
                wrongWorkspaceMessage,
                wrongWorkspaceFile,
                graphA.UserId,
                FileScanStatus.Clean);
            await fixture.Db.SaveChangesAsync();

            fixture.Tenant.SetTenant(graphB.TenantId, "issue528-b");
            fixture.Db.ChangeTracker.Clear();
            var crossTenantConversation = await fixture.SeedConversationAsync(graphB.WorkspaceId, graphB.UserId);
            var crossTenantMessage = await fixture.SeedMessageAsync(crossTenantConversation, graphB.UserId, "cross tenant source");
            var crossTenantFile = await fixture.SeedFileObjectAsync(
                graphB.WorkspaceId,
                graphB.UserId,
                FileObjectStatus.Active,
                "cross-tenant-secret.txt",
                "tenant-b/server/cross-tenant-secret.txt");
            var crossTenantSource = await fixture.SeedMessageAttachmentAsync(
                crossTenantMessage,
                crossTenantFile,
                graphB.UserId,
                FileScanStatus.Clean);
            await fixture.Db.SaveChangesAsync();

            fixture.Tenant.SetTenant(graphA.TenantId, "issue528-a");
            fixture.Db.ChangeTracker.Clear();

            await AssertDeniedAsync(fixture, targetConversation.Id, wrongWorkspaceSource.Id, Guid.NewGuid(), "wrong-workspace-secret.txt", wrongWorkspaceFile.StorageKey);
            await AssertDeniedAsync(fixture, targetConversation.Id, crossTenantSource.Id, Guid.NewGuid(), "cross-tenant-secret.txt", crossTenantFile.StorageKey);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task SkippedScanAttachmentIsDeniedToPreserveMessageAttachmentSearchContract()
    {
        var baseConnectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(baseConnectionString, async connectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(connectionString);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(connectionString, $"issue528-skipped-{Guid.NewGuid():N}");
            await using var fixture = CreateFixture(connectionString, graph);

            var conversation = await fixture.SeedConversationAsync(graph.WorkspaceId, graph.UserId);
            var sourceMessage = await fixture.SeedMessageAsync(conversation, graph.UserId, "skipped scan source");
            var fileObject = await fixture.SeedFileObjectAsync(
                graph.WorkspaceId,
                graph.UserId,
                FileObjectStatus.Active,
                "skipped-scan-secret.txt",
                "tenant/server/skipped-scan-secret.txt");
            var source = await fixture.SeedMessageAttachmentAsync(
                sourceMessage,
                fileObject,
                graph.UserId,
                FileScanStatus.Skipped);
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();

            await AssertDeniedAsync(
                fixture,
                conversation.Id,
                source.Id,
                Guid.NewGuid(),
                fileObject.OriginalFileName,
                fileObject.StorageKey);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ProjectScopedFileObjectIsDeniedToPreserveMessageAttachmentSearchContract()
    {
        var baseConnectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(baseConnectionString, async connectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(connectionString);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(connectionString, $"issue528-project-{Guid.NewGuid():N}");
            await using var fixture = CreateFixture(connectionString, graph);

            // The raw migration graph creates the Project row but intentionally has
            // no Workspace/Project memberships. Seed the canonical read boundary so
            // this regression reaches Message attachment validation instead of being
            // rejected earlier by ProjectChannel authorization.
            fixture.Db.WorkspaceMembers.Add(new WorkspaceMember
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                UserId = graph.UserId,
                Role = WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            });
            fixture.Db.ProjectMembers.Add(new ProjectMember
            {
                TenantId = graph.TenantId,
                ProjectId = graph.ProjectId,
                UserId = graph.UserId,
                Role = ProjectRole.Owner,
                JoinedAt = DateTimeOffset.UtcNow
            });
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();

            var conversation = await fixture.SeedConversationAsync(graph.WorkspaceId, graph.UserId, graph.ProjectId);
            var sourceMessage = await fixture.SeedMessageAsync(conversation, graph.UserId, "project source");
            var projectFile = await fixture.SeedFileObjectAsync(
                graph.WorkspaceId,
                graph.UserId,
                FileObjectStatus.Active,
                "project-scoped-secret.txt",
                "tenant/server/project-scoped-secret.txt",
                graph.ProjectId);
            var source = await fixture.SeedMessageAttachmentAsync(
                sourceMessage,
                projectFile,
                graph.UserId,
                FileScanStatus.Clean);
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();

            await AssertDeniedAsync(
                fixture,
                conversation.Id,
                source.Id,
                Guid.NewGuid(),
                projectFile.OriginalFileName,
                projectFile.StorageKey);
        });
    }

    private static async Task AssertDeniedAsync(
        Fixture fixture,
        Guid conversationId,
        Guid attachmentId,
        Guid clientRequestId,
        params string[] forbiddenMetadata)
    {
        var result = await fixture.Service.SendMessageAsync(
            conversationId,
            new SendMessageRequest(
                $"issue528 denied {clientRequestId:N}",
                [new MessageAttachmentReferenceRequest(attachmentId)],
                clientRequestId));

        Assert.False(result.IsSuccess);
        Assert.Equal("Attachment is unavailable or not authorized.", result.Error);
        foreach (var forbidden in forbiddenMetadata)
        {
            Assert.DoesNotContain(forbidden, result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Fixture CreateFixture(string connectionString, TaskV1MigrationRawSqlSeed.Graph graph)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["FileStorage:Provider"] = "LocalFileSystem",
                ["FileStorage:RootPath"] = Path.Combine(Path.GetTempPath(), $"coglatas-issue528-{Guid.NewGuid():N}")
            })
            .Build();
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(configuration);
        services.AddScoped<ICurrentUser>(_ => new TestCurrentUser(graph.UserId));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
        tenant.SetTenant(graph.TenantId, "issue528");
        return new Fixture(provider, scope, tenant);
    }

    private sealed class Fixture(
        ServiceProvider provider,
        AsyncServiceScope scope,
        CurrentTenantService tenant) : IAsyncDisposable
    {
        public CurrentTenantService Tenant { get; } = tenant;
        public AppDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        public IConversationService Service { get; } = scope.ServiceProvider.GetRequiredService<IConversationService>();

        public async Task<Workspace> SeedWorkspaceAsync(Guid tenantId, Guid userId, string suffix)
        {
            var workspace = new Workspace
            {
                TenantId = tenantId,
                Name = $"Issue 528 {suffix}",
                Slug = $"issue-528-{suffix}-{Guid.NewGuid():N}",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = userId
            };
            await Db.Workspaces.AddAsync(workspace);
            await Db.WorkspaceMembers.AddAsync(new WorkspaceMember
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                UserId = userId,
                Role = WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            });
            return workspace;
        }

        public async Task<Conversation> SeedConversationAsync(Guid workspaceId, Guid userId, Guid? projectId = null)
        {
            var conversation = new Conversation
            {
                TenantId = Tenant.TenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Type = projectId.HasValue ? ConversationType.ProjectChannel : ConversationType.DirectMessage,
                CreatedByUserId = userId
            };
            await Db.Conversations.AddAsync(conversation);
            await Db.ConversationMembers.AddAsync(new ConversationMember
            {
                TenantId = Tenant.TenantId,
                ConversationId = conversation.Id,
                UserId = userId,
                Role = ConversationMemberRole.Admin,
                CanRead = true,
                CanPost = true,
                CanManageMembers = true,
                CanCreateThread = true,
                JoinedAt = DateTimeOffset.UtcNow
            });
            return conversation;
        }

        public async Task<Message> SeedMessageAsync(Conversation conversation, Guid userId, string body)
        {
            var message = new Message
            {
                TenantId = Tenant.TenantId,
                WorkspaceId = conversation.WorkspaceId,
                ConversationId = conversation.Id,
                AuthorUserId = userId,
                Body = body,
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await Db.Messages.AddAsync(message);
            return message;
        }

        public async Task<FileObject> SeedFileObjectAsync(
            Guid workspaceId,
            Guid userId,
            FileObjectStatus status,
            string fileName,
            string storageKey,
            Guid? projectId = null)
        {
            var file = new FileObject
            {
                TenantId = Tenant.TenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                UploadedByUserId = userId,
                OriginalFileName = fileName,
                StorageKey = storageKey,
                ContentType = "text/plain",
                SizeBytes = 128,
                HashSha256 = new string('a', 64),
                Classification = DataClassification.Internal,
                SharingPolicy = FileSharingPolicy.Private,
                SharingVersion = 1,
                Status = status
            };
            await Db.FileObjects.AddAsync(file);
            return file;
        }

        public async Task<Attachment> SeedMessageAttachmentAsync(
            Message owner,
            FileObject fileObject,
            Guid userId,
            FileScanStatus scanStatus,
            string? compatibilityFileName = null,
            string? compatibilityContentType = null,
            long? compatibilitySizeBytes = null,
            string? compatibilityStorageKey = null)
        {
            var attachment = new Attachment
            {
                TenantId = Tenant.TenantId,
                FileObjectId = fileObject.Id,
                WorkspaceId = owner.WorkspaceId,
                OwnerType = AttachmentOwnerType.Message,
                OwnerId = owner.Id,
                OwnerUserId = userId,
                UploadedByUserId = userId,
                FileName = compatibilityFileName ?? fileObject.OriginalFileName,
                StoredFileName = "compatibility-only-name",
                FilePath = compatibilityStorageKey ?? fileObject.StorageKey,
                ContentType = compatibilityContentType ?? fileObject.ContentType,
                Extension = Path.GetExtension(fileObject.OriginalFileName),
                SizeBytes = compatibilitySizeBytes ?? fileObject.SizeBytes,
                StorageProvider = "LocalFileSystem",
                StorageKey = compatibilityStorageKey ?? fileObject.StorageKey,
                ScanStatus = scanStatus,
                FileObject = fileObject
            };
            await Db.Attachments.AddAsync(attachment);
            await Db.MessageAttachments.AddAsync(new MessageAttachment
            {
                TenantId = Tenant.TenantId,
                MessageId = owner.Id,
                AttachmentId = attachment.Id
            });
            return attachment;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string Email => "issue528@example.test";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }
}