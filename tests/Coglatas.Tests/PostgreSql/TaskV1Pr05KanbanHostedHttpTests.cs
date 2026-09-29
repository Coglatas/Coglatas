using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Coglatas.Application;
using Coglatas.Application.Auth;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Web.Configuration;
using Coglatas.Web.Controllers;
using Coglatas.Web.Extensions;
using Coglatas.Web.Middleware;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coglatas.Tests.PostgreSql;

[Collection("PostgreSqlTaskV1")]
public sealed class TaskV1Pr05KanbanHostedHttpTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR05")]
    public async Task Snapshot_HostedPostgreSqlPipelineEnforcesAuthVisibilityDoneWindowAndMembershipRevocation()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            await using var app = await KanbanHostedTestApp.CreateAsync(database);

            using (var unauthenticated = await app.SendAnonymousGetAsync(
                       app.Graph.TenantA,
                       $"/api/projects/{app.Graph.Project.Id:D}/kanban"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            }

            var manager = await app.LoginAsync(app.Graph.Manager, app.Graph.TenantA);
            using var response = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/kanban");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var snapshot = await ReadJsonAsync<ProjectKanbanSnapshot>(response);

            Assert.Equal(app.Graph.Project.Id, snapshot.Board.ProjectId);
            Assert.Equal(
                app.Graph.MainStages.AllIds.OrderBy(id => id),
                snapshot.Columns.Select(column => column.WorkflowStageId).OrderBy(id => id));
            Assert.Contains(snapshot.Cards, card => card.TaskId == app.Graph.VisibleTask.Id);
            Assert.Contains(snapshot.Cards, card => card.TaskId == app.Graph.MoveTask.Id);
            Assert.Contains(snapshot.Cards, card => card.TaskId == app.Graph.RecentDoneTask.Id);
            Assert.DoesNotContain(snapshot.Cards, card => card.TaskId == app.Graph.DeletedTask.Id);
            Assert.DoesNotContain(snapshot.Cards, card => card.TaskId == app.Graph.OldDoneTask.Id);

            using (var olderResponse = await manager.GetAsync(
                       $"/api/projects/{app.Graph.Project.Id:D}/kanban?includeOlderCompleted=true"))
            {
                Assert.Equal(HttpStatusCode.OK, olderResponse.StatusCode);
                var withOlderCompleted = await ReadJsonAsync<ProjectKanbanSnapshot>(olderResponse);
                Assert.Contains(withOlderCompleted.Cards, card => card.TaskId == app.Graph.OldDoneTask.Id);
            }

            KanbanError unauthorizedError;
            using (var unauthorizedResponse = await manager.GetAsync(
                       $"/api/projects/{app.Graph.UnauthorizedProject.Id:D}/kanban"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unauthorizedResponse.StatusCode);
                unauthorizedError = await ReadKanbanErrorAsync(unauthorizedResponse);
            }

            using (var crossTenantResponse = await manager.GetAsync(
                       $"/api/projects/{app.Graph.CrossTenantProject.Id:D}/kanban"))
            {
                Assert.Equal(HttpStatusCode.NotFound, crossTenantResponse.StatusCode);
                var crossTenantError = await ReadKanbanErrorAsync(crossTenantResponse);
                Assert.Equal(unauthorizedError.Code, crossTenantError.Code);
                Assert.Equal(unauthorizedError.Message, crossTenantError.Message);
                var body = await crossTenantResponse.Content.ReadAsStringAsync();
                Assert.DoesNotContain(app.Graph.CrossTenantProject.Name, body, StringComparison.Ordinal);
                Assert.DoesNotContain(app.Graph.CrossTenantProject.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            }

            await app.SetWorkspaceMembershipStatusAsync(
                app.Graph.TenantA,
                app.Graph.Workspace,
                app.Graph.Manager,
                MembershipStatus.Suspended);
            using (var revokedWorkspaceResponse = await manager.GetAsync(
                       $"/api/projects/{app.Graph.Project.Id:D}/kanban"))
            {
                Assert.Equal(HttpStatusCode.NotFound, revokedWorkspaceResponse.StatusCode);
                Assert.Equal("KANBAN_NOT_FOUND", (await ReadKanbanErrorAsync(revokedWorkspaceResponse)).Code);
            }

            await app.SetWorkspaceMembershipStatusAsync(
                app.Graph.TenantA,
                app.Graph.Workspace,
                app.Graph.Manager,
                MembershipStatus.Active);
            using (var restoredResponse = await manager.GetAsync(
                       $"/api/projects/{app.Graph.Project.Id:D}/kanban"))
            {
                Assert.Equal(HttpStatusCode.OK, restoredResponse.StatusCode);
            }

            await app.SetTenantMembershipStatusAsync(
                app.Graph.TenantA,
                app.Graph.Manager,
                TenantUserStatus.Suspended);
            using var revokedTenantResponse = await manager.GetAsync(
                $"/api/projects/{app.Graph.Project.Id:D}/kanban");
            Assert.Equal(HttpStatusCode.Unauthorized, revokedTenantResponse.StatusCode);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR05")]
    public async Task Config_HostedPostgreSqlPipelineEnforcesCsrfAuthorizationConcurrencyPersistenceAndAtomicSideEffects()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            await using var app = await KanbanHostedTestApp.CreateAsync(database);
            var manager = await app.LoginAsync(app.Graph.Manager, app.Graph.TenantA);
            var member = await app.LoginAsync(app.Graph.Member, app.Graph.TenantA);

            using var initialResponse = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/kanban");
            initialResponse.EnsureSuccessStatusCode();
            var initial = await ReadJsonAsync<ProjectKanbanSnapshot>(initialResponse);
            var requestedColumns = initial.Columns
                .Reverse()
                .Select((column, index) => new ProjectKanbanStageConfig(
                    column.WorkflowStageId,
                    index,
                    column.Category == TaskStageCategory.Todo ? 2 : null))
                .ToList();
            var request = new UpdateProjectKanbanConfigRequest(
                initial.Board.Version,
                ProjectKanbanSwimlane.Priority,
                requestedColumns);

            var baseline = await app.ReadConfigStateAsync();
            using (var noCsrf = await manager.SendJsonAsync(
                       HttpMethod.Put,
                       $"/api/projects/{app.Graph.Project.Id:D}/kanban/config",
                       request,
                       includeCsrf: false))
            {
                Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
            }
            AssertConfigStateUnchanged(baseline, await app.ReadConfigStateAsync());

            HttpResponseMessage forcedFailure;
            await app.InstallOutboxFailureConstraintAsync();
            try
            {
                forcedFailure = await manager.SendJsonAsync(
                    HttpMethod.Put,
                    $"/api/projects/{app.Graph.Project.Id:D}/kanban/config",
                    request);
            }
            finally
            {
                await app.RemoveOutboxFailureConstraintAsync();
            }

            using (forcedFailure)
            {
                Assert.Equal(HttpStatusCode.InternalServerError, forcedFailure.StatusCode);
            }
            AssertConfigStateUnchanged(baseline, await app.ReadConfigStateAsync());

            using var successResponse = await manager.SendJsonAsync(
                HttpMethod.Put,
                $"/api/projects/{app.Graph.Project.Id:D}/kanban/config",
                request);
            Assert.Equal(HttpStatusCode.OK, successResponse.StatusCode);
            var command = await ReadJsonAsync<ProjectKanbanCommandResponse>(successResponse);
            Assert.True(command.Snapshot.Board.Version > initial.Board.Version);

            using var reloadResponse = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/kanban");
            reloadResponse.EnsureSuccessStatusCode();
            var reload = await ReadJsonAsync<ProjectKanbanSnapshot>(reloadResponse);
            Assert.Equal(ProjectKanbanSwimlane.Priority, reload.Board.DefaultSwimlane);
            Assert.Equal(
                requestedColumns.OrderBy(column => column.DisplayOrder).Select(column => column.WorkflowStageId),
                reload.Columns.Select(column => column.WorkflowStageId));
            Assert.Equal(
                2,
                reload.Columns.Single(column => column.Category == TaskStageCategory.Todo).WipWarningLimit);

            var persisted = await app.ReadConfigStateAsync();
            Assert.True(persisted.BoardVersion > baseline.BoardVersion);
            Assert.Equal(ProjectKanbanSwimlane.Priority, persisted.DefaultSwimlane);
            Assert.Equal(
                requestedColumns.OrderBy(column => column.DisplayOrder).Select(column => column.WorkflowStageId),
                persisted.Stages.OrderBy(stage => stage.SortKey).ThenBy(stage => stage.Id).Select(stage => stage.Id));
            Assert.Equal(
                2,
                persisted.Stages.Single(stage => stage.Id == app.Graph.MainStages.Todo).WipWarningLimit);
            Assert.Equal(1, persisted.KanbanAuditCount);
            Assert.Equal(1, persisted.ProjectOutboxCount);

            var audit = Assert.Single(await app.ReadAuditLogsAsync("ProjectKanbanConfigured"));
            Assert.NotNull(audit.MetadataJson);
            using (var metadata = JsonDocument.Parse(audit.MetadataJson!))
            {
                Assert.Equal(baseline.BoardVersion, metadata.RootElement.GetProperty("versionBefore").GetInt64());
                Assert.Equal(persisted.BoardVersion, metadata.RootElement.GetProperty("versionAfter").GetInt64());
                Assert.Equal("Priority", metadata.RootElement.GetProperty("defaultSwimlane").GetString());
                Assert.True(metadata.RootElement.GetProperty("changedStageCount").GetInt32() > 0);
            }

            var projectOutbox = Assert.Single(
                await app.ReadOutboxEventsAsync(),
                item =>
                    item.EventType == "Projects.ProjectChanged.v1" &&
                    item.AggregateId == app.Graph.Project.Id);
            using (var envelope = JsonDocument.Parse(projectOutbox.PayloadJson))
            {
                Assert.Equal(
                    "kanbanConfigurationChanged",
                    envelope.RootElement.GetProperty("payload").GetProperty("change").GetString());
                Assert.True(envelope.RootElement.GetProperty("payload").GetProperty("requiresRefetch").GetBoolean());
            }

            var afterSuccess = await app.ReadConfigStateAsync();
            var staleRequest = request with { ExpectedBoardVersion = initial.Board.Version };
            using (var staleResponse = await manager.SendJsonAsync(
                       HttpMethod.Put,
                       $"/api/projects/{app.Graph.Project.Id:D}/kanban/config",
                       staleRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
                Assert.Equal("KANBAN_STALE_BOARD", (await ReadKanbanErrorAsync(staleResponse)).Code);
            }
            AssertConfigStateUnchanged(afterSuccess, await app.ReadConfigStateAsync());

            var memberRequest = request with
            {
                ExpectedBoardVersion = reload.Board.Version,
                DefaultSwimlane = ProjectKanbanSwimlane.ParentTask
            };
            using (var forbiddenResponse = await member.SendJsonAsync(
                       HttpMethod.Put,
                       $"/api/projects/{app.Graph.Project.Id:D}/kanban/config",
                       memberRequest))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
                Assert.Equal("KANBAN_FORBIDDEN", (await ReadKanbanErrorAsync(forbiddenResponse)).Code);
            }
            AssertConfigStateUnchanged(afterSuccess, await app.ReadConfigStateAsync());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR05")]
    public async Task Move_HostedPostgreSqlPipelinePersistsCanonicalOrderVersionsCancellationAndAtomicSideEffects()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            await using var app = await KanbanHostedTestApp.CreateAsync(database);
            var manager = await app.LoginAsync(app.Graph.Manager, app.Graph.TenantA);
            var member = await app.LoginAsync(app.Graph.Member, app.Graph.TenantA);

            using var initialResponse = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/kanban");
            initialResponse.EnsureSuccessStatusCode();
            var initial = await ReadJsonAsync<ProjectKanbanSnapshot>(initialResponse);
            var movingCard = initial.Cards.Single(card => card.TaskId == app.Graph.MoveTask.Id);
            var moveBetween = new MoveTaskOnKanbanRequest(
                app.Graph.MainStages.InProgress,
                app.Graph.TargetLastTask.Id,
                app.Graph.TargetFirstTask.Id,
                movingCard.Version,
                initial.Board.Version);

            var baseline = await app.ReadMoveStateAsync(app.Graph.MainStages.Todo);
            using (var noCsrf = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                       moveBetween,
                       includeCsrf: false))
            {
                Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
            }
            AssertMoveStateUnchanged(baseline, await app.ReadMoveStateAsync(app.Graph.MainStages.Todo));

            HttpResponseMessage forcedFailure;
            await app.InstallOutboxFailureConstraintAsync();
            try
            {
                forcedFailure = await manager.SendJsonAsync(
                    HttpMethod.Post,
                    $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                    moveBetween);
            }
            finally
            {
                await app.RemoveOutboxFailureConstraintAsync();
            }

            using (forcedFailure)
            {
                Assert.Equal(HttpStatusCode.InternalServerError, forcedFailure.StatusCode);
            }
            AssertMoveStateUnchanged(baseline, await app.ReadMoveStateAsync(app.Graph.MainStages.Todo));

            using var moveResponse = await manager.SendJsonAsync(
                HttpMethod.Post,
                $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                moveBetween);
            Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);
            var moveCommand = await ReadJsonAsync<ProjectKanbanCommandResponse>(moveResponse);
            var movedCard = moveCommand.Snapshot.Cards.Single(card => card.TaskId == app.Graph.MoveTask.Id);
            Assert.Equal(app.Graph.MainStages.InProgress, movedCard.WorkflowStageId);
            Assert.True(movedCard.Version > movingCard.Version);
            Assert.True(moveCommand.Snapshot.Board.Version > initial.Board.Version);

            using var moveReloadResponse = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/kanban");
            moveReloadResponse.EnsureSuccessStatusCode();
            var moveReload = await ReadJsonAsync<ProjectKanbanSnapshot>(moveReloadResponse);
            var reloadedMovedCard = moveReload.Cards.Single(card => card.TaskId == app.Graph.MoveTask.Id);
            var firstTarget = moveReload.Cards.Single(card => card.TaskId == app.Graph.TargetFirstTask.Id);
            var lastTarget = moveReload.Cards.Single(card => card.TaskId == app.Graph.TargetLastTask.Id);
            Assert.Equal(app.Graph.MainStages.InProgress, reloadedMovedCard.WorkflowStageId);
            Assert.InRange(reloadedMovedCard.BoardOrder, firstTarget.BoardOrder + 1, lastTarget.BoardOrder - 1);

            var persistedMove = await app.ReadMoveStateAsync(app.Graph.MainStages.InProgress);
            Assert.Equal(app.Graph.MainStages.InProgress, persistedMove.WorkflowStageId);
            Assert.Equal(reloadedMovedCard.BoardOrder, persistedMove.SortKey);
            Assert.Equal(reloadedMovedCard.Version, persistedMove.TaskVersion);
            Assert.Equal(moveReload.Board.Version, persistedMove.BoardVersion);
            Assert.Equal(
                [
                    app.Graph.TargetFirstTask.Id,
                    app.Graph.MoveTask.Id,
                    app.Graph.TargetLastTask.Id
                ],
                persistedMove.TargetStageOrder);

            var beforeEndTaskVersion = persistedMove.TaskVersion;
            var moveToCanonicalEnd = new MoveTaskOnKanbanRequest(
                app.Graph.MainStages.InProgress,
                null,
                null,
                persistedMove.TaskVersion,
                persistedMove.BoardVersion);
            using var endResponse = await manager.SendJsonAsync(
                HttpMethod.Post,
                $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                moveToCanonicalEnd);
            Assert.Equal(HttpStatusCode.OK, endResponse.StatusCode);

            using var endReloadResponse = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/kanban");
            endReloadResponse.EnsureSuccessStatusCode();
            var endReload = await ReadJsonAsync<ProjectKanbanSnapshot>(endReloadResponse);
            Assert.Equal(
                app.Graph.MoveTask.Id,
                endReload.Cards
                    .Where(card => card.WorkflowStageId == app.Graph.MainStages.InProgress)
                    .OrderBy(card => card.BoardOrder)
                    .ThenBy(card => card.TaskId)
                    .Last()
                    .TaskId);

            var atEnd = await app.ReadMoveStateAsync(app.Graph.MainStages.InProgress);
            Assert.Equal(app.Graph.MoveTask.Id, atEnd.TargetStageOrder.Last());
            Assert.True(atEnd.TaskVersion > beforeEndTaskVersion);

            var rejectionBaseline = atEnd;
            var staleTask = moveToCanonicalEnd with
            {
                ExpectedTaskVersion = beforeEndTaskVersion,
                ExpectedBoardVersion = atEnd.BoardVersion
            };
            using (var staleTaskResponse = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                       staleTask))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleTaskResponse.StatusCode);
                Assert.Equal("TASK_STALE_VERSION", (await ReadKanbanErrorAsync(staleTaskResponse)).Code);
            }

            var staleBoard = moveToCanonicalEnd with
            {
                ExpectedTaskVersion = atEnd.TaskVersion,
                ExpectedBoardVersion = initial.Board.Version
            };
            using (var staleBoardResponse = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                       staleBoard))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleBoardResponse.StatusCode);
                Assert.Equal("KANBAN_STALE_BOARD", (await ReadKanbanErrorAsync(staleBoardResponse)).Code);
            }

            var crossProjectPosition = moveToCanonicalEnd with
            {
                TargetBeforeTaskId = app.Graph.CrossProjectNeighborTask.Id,
                ExpectedTaskVersion = atEnd.TaskVersion,
                ExpectedBoardVersion = atEnd.BoardVersion
            };
            KanbanError crossProjectError;
            using (var crossProjectResponse = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                       crossProjectPosition))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, crossProjectResponse.StatusCode);
                crossProjectError = await ReadKanbanErrorAsync(crossProjectResponse);
                var body = await crossProjectResponse.Content.ReadAsStringAsync();
                Assert.DoesNotContain(app.Graph.CrossProjectNeighborTask.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(app.Graph.CrossProjectNeighborTask.Title, body, StringComparison.Ordinal);
            }

            var unknownPosition = crossProjectPosition with { TargetBeforeTaskId = Guid.NewGuid() };
            using (var unknownResponse = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                       unknownPosition))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownResponse.StatusCode);
                var unknownError = await ReadKanbanErrorAsync(unknownResponse);
                Assert.Equal(crossProjectError.Code, unknownError.Code);
                Assert.Equal(crossProjectError.Message, unknownError.Message);
            }

            var deniedMove = moveToCanonicalEnd with
            {
                ExpectedTaskVersion = atEnd.TaskVersion,
                ExpectedBoardVersion = atEnd.BoardVersion
            };
            using (var deniedResponse = await member.SendJsonAsync(
                       HttpMethod.Post,
                       $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                       deniedMove))
            {
                Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
                Assert.Equal("KANBAN_FORBIDDEN", (await ReadKanbanErrorAsync(deniedResponse)).Code);
            }

            var cancelWithoutReason = new MoveTaskOnKanbanRequest(
                app.Graph.MainStages.Cancelled,
                null,
                null,
                atEnd.TaskVersion,
                atEnd.BoardVersion);
            using (var missingReasonResponse = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                       cancelWithoutReason))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, missingReasonResponse.StatusCode);
                Assert.Equal(
                    "TASK_CANCEL_REASON_REQUIRED",
                    (await ReadKanbanErrorAsync(missingReasonResponse)).Code);
            }
            AssertMoveStateUnchanged(rejectionBaseline, await app.ReadMoveStateAsync(app.Graph.MainStages.InProgress));

            const string cancellationReason = "Acceptance cancellation reason";
            var cancel = new MoveTaskOnKanbanRequest(
                app.Graph.MainStages.Cancelled,
                null,
                null,
                atEnd.TaskVersion,
                atEnd.BoardVersion,
                $"  {cancellationReason}  ");
            using var cancelResponse = await manager.SendJsonAsync(
                HttpMethod.Post,
                $"/api/tasks/{app.Graph.MoveTask.Id:D}/kanban-move",
                cancel);
            Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

            using var cancelReloadResponse = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/kanban");
            cancelReloadResponse.EnsureSuccessStatusCode();
            var cancelReload = await ReadJsonAsync<ProjectKanbanSnapshot>(cancelReloadResponse);
            var cancelledCard = cancelReload.Cards.Single(card => card.TaskId == app.Graph.MoveTask.Id);
            Assert.Equal(app.Graph.MainStages.Cancelled, cancelledCard.WorkflowStageId);

            var cancelled = await app.ReadMoveStateAsync(app.Graph.MainStages.Cancelled);
            Assert.Equal(app.Graph.MainStages.Cancelled, cancelled.WorkflowStageId);
            Assert.Equal(cancellationReason, cancelled.CancellationReason);
            Assert.NotNull(cancelled.CancelledAt);
            Assert.Equal(TaskItemStatus.Cancelled, cancelled.Status);
            Assert.Equal(cancelledCard.Version, cancelled.TaskVersion);
            Assert.Equal(cancelReload.Board.Version, cancelled.BoardVersion);

            var moveAudits = await app.ReadAuditLogsAsync("TaskKanbanMoved");
            var cancellationAudit = Assert.Single(
                moveAudits,
                item => MetadataGuidEquals(
                    item.MetadataJson,
                    "targetWorkflowStageId",
                    app.Graph.MainStages.Cancelled));
            using (var metadata = JsonDocument.Parse(cancellationAudit.MetadataJson!))
            {
                Assert.Equal(
                    app.Graph.MainStages.InProgress,
                    metadata.RootElement.GetProperty("sourceWorkflowStageId").GetGuid());
                Assert.Equal(
                    app.Graph.MainStages.Cancelled,
                    metadata.RootElement.GetProperty("targetWorkflowStageId").GetGuid());
                Assert.True(
                    metadata.RootElement.GetProperty("boardVersionAfter").GetInt64() >
                    metadata.RootElement.GetProperty("boardVersionBefore").GetInt64());
            }

            var outbox = await app.ReadOutboxEventsAsync();
            var finalTaskEvent = Assert.Single(
                outbox,
                item =>
                    item.EventType == "Projects.TaskChanged.v1" &&
                    item.AggregateId == app.Graph.MoveTask.Id &&
                    item.AggregateVersion == cancelled.TaskVersion);
            using (var envelope = JsonDocument.Parse(finalTaskEvent.PayloadJson))
            {
                var payload = envelope.RootElement.GetProperty("payload");
                Assert.Equal("kanbanMoved", payload.GetProperty("change").GetString());
                Assert.Equal(cancelled.TaskVersion, payload.GetProperty("taskVersion").GetInt64());
                Assert.True(payload.GetProperty("requiresRefetch").GetBoolean());
            }
            Assert.Contains(outbox, item =>
                item.EventType == "Projects.ProjectChanged.v1" &&
                item.AggregateId == app.Graph.Project.Id);
        });
    }

    private static void AssertConfigStateUnchanged(ConfigState expected, ConfigState actual)
    {
        Assert.Equal(expected.BoardVersion, actual.BoardVersion);
        Assert.Equal(expected.DefaultSwimlane, actual.DefaultSwimlane);
        Assert.Equal(expected.KanbanAuditCount, actual.KanbanAuditCount);
        Assert.Equal(expected.ProjectOutboxCount, actual.ProjectOutboxCount);
        Assert.Equal(expected.Stages, actual.Stages);
    }

    private static void AssertMoveStateUnchanged(MoveState expected, MoveState actual)
    {
        Assert.Equal(expected.WorkflowStageId, actual.WorkflowStageId);
        Assert.Equal(expected.SortKey, actual.SortKey);
        Assert.Equal(expected.TaskVersion, actual.TaskVersion);
        Assert.Equal(expected.BoardVersion, actual.BoardVersion);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.CancellationReason, actual.CancellationReason);
        Assert.Equal(expected.CancelledAt, actual.CancelledAt);
        Assert.Equal(expected.KanbanAuditCount, actual.KanbanAuditCount);
        Assert.Equal(expected.ProjectOutboxCount, actual.ProjectOutboxCount);
        Assert.Equal(expected.TargetStageOrder, actual.TargetStageOrder);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>()
        ?? throw new InvalidOperationException($"HTTP response did not contain {typeof(T).Name}.");

    private static async Task<KanbanError> ReadKanbanErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        return new KanbanError(
            error.GetProperty("code").GetString() ?? string.Empty,
            error.GetProperty("message").GetString() ?? string.Empty);
    }

    private static bool MetadataGuidEquals(string? metadataJson, string propertyName, Guid expected)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return false;
        using var document = JsonDocument.Parse(metadataJson);
        return document.RootElement.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               property.TryGetGuid(out var value) &&
               value == expected;
    }

    private sealed class KanbanHostedTestApp : IAsyncDisposable
    {
        private const string Password = "KanbanAcceptance!234";
        private const string TenantHeader = "X-Tenant-Slug";
        private const string OutboxFailureConstraint = "CK_TaskV1Pr05_ForceOutboxFailure";

        private readonly WebApplication _app;
        private readonly Uri _baseAddress;
        private readonly string _connectionString;
        private readonly string _fileStoragePath;
        private readonly string _dataProtectionPath;
        private readonly List<ActorHttpClient> _actorClients = [];
        private readonly List<HttpClient> _anonymousClients = [];

        private KanbanHostedTestApp(
            WebApplication app,
            Uri baseAddress,
            string connectionString,
            string fileStoragePath,
            string dataProtectionPath,
            HostedGraph graph)
        {
            _app = app;
            _baseAddress = baseAddress;
            _connectionString = connectionString;
            _fileStoragePath = fileStoragePath;
            _dataProtectionPath = dataProtectionPath;
            Graph = graph;
        }

        public HostedGraph Graph { get; }

        public static async Task<KanbanHostedTestApp> CreateAsync(string connectionString)
        {
            var runId = Guid.NewGuid().ToString("N");
            var fileStoragePath = Path.Combine(Path.GetTempPath(), "coglatas-pr05-hosted-tests", runId, "files");
            var dataProtectionPath = Path.Combine(Path.GetTempPath(), "coglatas-pr05-hosted-tests", runId, "keys");
            Directory.CreateDirectory(dataProtectionPath);
            var now = DateTimeOffset.UtcNow;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["Tenancy:AppMode"] = "SaaS",
                ["Tenancy:TenantResolutionStrategy"] = "HeaderForDevelopmentOnly",
                ["Tenancy:AllowDevelopmentHeaderTenantResolution"] = "true",
                ["Tenancy:AllowDevelopmentHeaderInProduction"] = "false",
                ["Tenancy:DevelopmentTenantHeaderName"] = TenantHeader,
                ["Security:CookieSecurePolicy"] = "SameAsRequest",
                ["Security:RequireHttps"] = "false",
                ["Security:EnableHsts"] = "false",
                ["Security:EnableCsrfProtection"] = "true",
                ["Security:EnableRateLimiting"] = "false",
                ["Security:LoginLockoutEnabled"] = "false",
                ["Security:MaxFailedLoginAttempts"] = "10",
                ["Security:LoginLockoutDurationMinutes"] = "15",
                ["FileStorage:Provider"] = "LocalFileSystem",
                ["FileStorage:RootPath"] = fileStoragePath,
                ["FileStorage:MaxFileSizeBytes"] = "10485760",
                ["FileStorage:AllowedExtensions:0"] = ".txt",
                ["FileStorage:AllowedContentTypes:0"] = "text/plain"
            });

            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
            builder.Services
                .AddApplication()
                .AddInfrastructure(builder.Configuration)
                .AddWebServices(builder.Configuration);
            builder.Services.AddSingleton<IClock>(new FixedClock(now));
            builder.Services.AddControllers().AddApplicationPart(typeof(ProjectKanbanController).Assembly);
            builder.Services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = $".Coglatas.Auth.PR05.{runId}";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = true;
                    options.EventsType = typeof(DbSessionCookieAuthenticationEvents);
                });
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseMiddleware<GlobalExceptionHandlingMiddleware>();
            app.UseMiddleware<SecurityHeadersMiddleware>();
            app.UseMiddleware<TenantResolutionMiddleware>();
            app.UseAuthentication();
            app.Services.GetRequiredService<CsrfProtectionState>().MarkMiddlewareActive();
            app.UseMiddleware<CsrfProtectionMiddleware>();
            app.UseAuthorization();
            app.MapControllers();

            var graph = await SeedAsync(app.Services, now);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Test server address was not available.");
            return new KanbanHostedTestApp(
                app,
                new Uri(address),
                connectionString,
                fileStoragePath,
                dataProtectionPath,
                graph);
        }

        public async Task<ActorHttpClient> LoginAsync(User actor, Tenant tenant)
        {
            var client = new HttpClient(
                new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = new CookieContainer()
                })
            {
                BaseAddress = _baseAddress
            };
            var actorClient = new ActorHttpClient(client, tenant.Slug);
            _actorClients.Add(actorClient);
            await actorClient.LoginAsync(actor.Email, Password);
            return actorClient;
        }

        public async Task<HttpResponseMessage> SendAnonymousGetAsync(Tenant tenant, string path)
        {
            var client = new HttpClient(
                new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = new CookieContainer()
                })
            {
                BaseAddress = _baseAddress
            };
            _anonymousClients.Add(client);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation(TenantHeader, tenant.Slug);
            return await client.SendAsync(request);
        }

        public async Task SetWorkspaceMembershipStatusAsync(
            Tenant tenant,
            Workspace workspace,
            User user,
            MembershipStatus status)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, tenant);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = await dbContext.WorkspaceMembers
                .SingleAsync(item => item.WorkspaceId == workspace.Id && item.UserId == user.Id);
            membership.Status = status;
            await dbContext.SaveChangesAsync();
        }

        public async Task SetTenantMembershipStatusAsync(
            Tenant tenant,
            User user,
            TenantUserStatus status)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, tenant);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = await dbContext.TenantUsers.SingleAsync(item => item.UserId == user.Id);
            membership.Status = status;
            await dbContext.SaveChangesAsync();
        }

        public async Task<ConfigState> ReadConfigStateAsync()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var definition = await dbContext.TaskWorkflowDefinitions
                .AsNoTracking()
                .SingleAsync(item => item.ProjectId == Graph.Project.Id);
            var stages = await dbContext.TaskWorkflowStages
                .AsNoTracking()
                .Where(item => item.ProjectId == Graph.Project.Id)
                .OrderBy(item => item.Id)
                .Select(item => new ConfigStageState(item.Id, item.SortKey, item.WipWarningLimit, item.VersionNo))
                .ToListAsync();
            return new ConfigState(
                definition.VersionNo,
                definition.KanbanDefaultSwimlane,
                stages,
                await dbContext.AuditLogs.CountAsync(item =>
                    item.ProjectId == Graph.Project.Id &&
                    item.Action == "ProjectKanbanConfigured"),
                await dbContext.OutboxEvents.CountAsync(item =>
                    item.AggregateId == Graph.Project.Id &&
                    item.EventType == "Projects.ProjectChanged.v1"));
        }

        public async Task<MoveState> ReadMoveStateAsync(Guid targetStageId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var task = await dbContext.TaskItems
                .AsNoTracking()
                .SingleAsync(item => item.Id == Graph.MoveTask.Id);
            var boardVersion = await dbContext.TaskWorkflowDefinitions
                .AsNoTracking()
                .Where(item => item.ProjectId == Graph.Project.Id)
                .Select(item => item.VersionNo)
                .SingleAsync();
            var targetStageOrder = await dbContext.TaskItems
                .AsNoTracking()
                .Where(item =>
                    item.ProjectId == Graph.Project.Id &&
                    item.WorkflowStageId == targetStageId &&
                    !item.DeletedAt.HasValue)
                .OrderBy(item => item.SortKey)
                .ThenBy(item => item.Id)
                .Select(item => item.Id)
                .ToListAsync();
            return new MoveState(
                task.WorkflowStageId,
                task.SortKey,
                task.VersionNo,
                boardVersion,
                task.Status,
                task.CancellationReason,
                task.CancelledAt,
                await dbContext.AuditLogs.CountAsync(item =>
                    item.ProjectId == Graph.Project.Id &&
                    item.Action == "TaskKanbanMoved"),
                await dbContext.OutboxEvents.CountAsync(item =>
                    item.AggregateId == Graph.Project.Id ||
                    item.AggregateId == Graph.MoveTask.Id),
                targetStageOrder);
        }

        public async Task<IReadOnlyList<AuditLog>> ReadAuditLogsAsync(string action)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.AuditLogs
                .AsNoTracking()
                .Where(item => item.ProjectId == Graph.Project.Id && item.Action == action)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<OutboxEvent>> ReadOutboxEventsAsync()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.OutboxEvents
                .AsNoTracking()
                .Where(item =>
                    item.AggregateId == Graph.Project.Id ||
                    item.AggregateId == Graph.MoveTask.Id)
                .ToListAsync();
        }

        public Task InstallOutboxFailureConstraintAsync() =>
            PostgreSqlMigrationTestDatabase.ExecuteAsync(
                _connectionString,
                $"""
                 ALTER TABLE outbox_events
                 ADD CONSTRAINT "{OutboxFailureConstraint}"
                 CHECK (FALSE) NOT VALID;
                 """);

        public Task RemoveOutboxFailureConstraintAsync() =>
            PostgreSqlMigrationTestDatabase.ExecuteAsync(
                _connectionString,
                $"""
                 ALTER TABLE outbox_events
                 DROP CONSTRAINT IF EXISTS "{OutboxFailureConstraint}";
                 """);

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _actorClients)
                client.Dispose();
            foreach (var client in _anonymousClients)
                client.Dispose();
            await _app.DisposeAsync();
            TryDeleteDirectory(Path.GetDirectoryName(_fileStoragePath)!);
            TryDeleteDirectory(Path.GetDirectoryName(_dataProtectionPath)!);
        }

        private static async Task<HostedGraph> SeedAsync(IServiceProvider services, DateTimeOffset now)
        {
            await using var scope = services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetPlatformScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var suffix = Guid.NewGuid().ToString("N");

            var tenantA = new Tenant
            {
                Name = $"PR05 hosted A {suffix}",
                DisplayName = "PR05 hosted A",
                Slug = $"pr05-hosted-a-{suffix}",
                Status = TenantStatus.Active
            };
            var tenantB = new Tenant
            {
                Name = $"PR05 hosted B {suffix}",
                DisplayName = "PR05 hosted B",
                Slug = $"pr05-hosted-b-{suffix}",
                Status = TenantStatus.Active
            };
            var owner = User("owner", suffix, passwordHasher);
            var manager = User("manager", suffix, passwordHasher);
            var member = User("member", suffix, passwordHasher);
            var outsider = User("outsider", suffix, passwordHasher);
            var tenantBManager = User("tenant-b-manager", suffix, passwordHasher);
            dbContext.AddRange(tenantA, tenantB, owner, manager, member, outsider, tenantBManager);
            await dbContext.SaveChangesAsync();

            var workspace = new Workspace
            {
                TenantId = tenantA.Id,
                Name = "PR05 Kanban workspace",
                Slug = $"pr05-kanban-workspace-{suffix}",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = owner.Id,
                TimeZone = "UTC"
            };
            var unauthorizedWorkspace = new Workspace
            {
                TenantId = tenantA.Id,
                Name = "PR05 unauthorized workspace",
                Slug = $"pr05-unauthorized-workspace-{suffix}",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = outsider.Id,
                TimeZone = "UTC"
            };
            var crossTenantWorkspace = new Workspace
            {
                TenantId = tenantB.Id,
                Name = "PR05 cross tenant workspace",
                Slug = $"pr05-cross-tenant-workspace-{suffix}",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = tenantBManager.Id,
                TimeZone = "UTC"
            };
            var project = Project(
                tenantA,
                workspace,
                owner,
                "PR05 canonical board",
                $"pr05-board-{suffix}");
            var siblingProject = Project(
                tenantA,
                workspace,
                owner,
                "PR05 sibling board",
                $"pr05-sibling-{suffix}");
            var unauthorizedProject = Project(
                tenantA,
                unauthorizedWorkspace,
                outsider,
                "PR05 unauthorized board",
                $"pr05-unauthorized-{suffix}");
            var crossTenantProject = Project(
                tenantB,
                crossTenantWorkspace,
                tenantBManager,
                "PR05 cross tenant secret board",
                $"pr05-cross-tenant-{suffix}");

            dbContext.AddRange(
                new TenantUser
                {
                    TenantId = tenantA.Id,
                    UserId = owner.Id,
                    Role = TenantUserRole.Owner,
                    Status = TenantUserStatus.Active,
                    JoinedAt = now
                },
                new TenantUser
                {
                    TenantId = tenantA.Id,
                    UserId = manager.Id,
                    Role = TenantUserRole.Member,
                    Status = TenantUserStatus.Active,
                    JoinedAt = now
                },
                new TenantUser
                {
                    TenantId = tenantA.Id,
                    UserId = member.Id,
                    Role = TenantUserRole.Member,
                    Status = TenantUserStatus.Active,
                    JoinedAt = now
                },
                new TenantUser
                {
                    TenantId = tenantA.Id,
                    UserId = outsider.Id,
                    Role = TenantUserRole.Member,
                    Status = TenantUserStatus.Active,
                    JoinedAt = now
                },
                new TenantUser
                {
                    TenantId = tenantB.Id,
                    UserId = tenantBManager.Id,
                    Role = TenantUserRole.Member,
                    Status = TenantUserStatus.Active,
                    JoinedAt = now
                },
                workspace,
                unauthorizedWorkspace,
                crossTenantWorkspace,
                new WorkspaceMember
                {
                    TenantId = tenantA.Id,
                    WorkspaceId = workspace.Id,
                    UserId = owner.Id,
                    Role = WorkspaceRole.Owner,
                    Status = MembershipStatus.Active,
                    JoinedAt = now
                },
                new WorkspaceMember
                {
                    TenantId = tenantA.Id,
                    WorkspaceId = workspace.Id,
                    UserId = manager.Id,
                    Role = WorkspaceRole.Member,
                    Status = MembershipStatus.Active,
                    JoinedAt = now
                },
                new WorkspaceMember
                {
                    TenantId = tenantA.Id,
                    WorkspaceId = workspace.Id,
                    UserId = member.Id,
                    Role = WorkspaceRole.Member,
                    Status = MembershipStatus.Active,
                    JoinedAt = now
                },
                new WorkspaceMember
                {
                    TenantId = tenantA.Id,
                    WorkspaceId = unauthorizedWorkspace.Id,
                    UserId = outsider.Id,
                    Role = WorkspaceRole.Owner,
                    Status = MembershipStatus.Active,
                    JoinedAt = now
                },
                new WorkspaceMember
                {
                    TenantId = tenantB.Id,
                    WorkspaceId = crossTenantWorkspace.Id,
                    UserId = tenantBManager.Id,
                    Role = WorkspaceRole.Owner,
                    Status = MembershipStatus.Active,
                    JoinedAt = now
                },
                project,
                siblingProject,
                unauthorizedProject,
                crossTenantProject,
                new ProjectMember
                {
                    TenantId = tenantA.Id,
                    ProjectId = project.Id,
                    UserId = owner.Id,
                    Role = ProjectRole.Owner,
                    JoinedAt = now
                },
                new ProjectMember
                {
                    TenantId = tenantA.Id,
                    ProjectId = project.Id,
                    UserId = manager.Id,
                    Role = ProjectRole.Manager,
                    JoinedAt = now
                },
                new ProjectMember
                {
                    TenantId = tenantA.Id,
                    ProjectId = project.Id,
                    UserId = member.Id,
                    Role = ProjectRole.Contributor,
                    JoinedAt = now
                },
                new ProjectMember
                {
                    TenantId = tenantA.Id,
                    ProjectId = siblingProject.Id,
                    UserId = owner.Id,
                    Role = ProjectRole.Owner,
                    JoinedAt = now
                },
                new ProjectMember
                {
                    TenantId = tenantA.Id,
                    ProjectId = unauthorizedProject.Id,
                    UserId = outsider.Id,
                    Role = ProjectRole.Owner,
                    JoinedAt = now
                },
                new ProjectMember
                {
                    TenantId = tenantB.Id,
                    ProjectId = crossTenantProject.Id,
                    UserId = tenantBManager.Id,
                    Role = ProjectRole.Owner,
                    JoinedAt = now
                });
            await dbContext.SaveChangesAsync();

            var mainStages = await StageIdsAsync(dbContext, project.Id);
            var siblingStages = await StageIdsAsync(dbContext, siblingProject.Id);
            var visibleTask = Task(
                tenantA,
                workspace,
                project,
                mainStages.Todo,
                manager,
                "Visible PostgreSQL Task",
                1000);
            var moveTask = Task(
                tenantA,
                workspace,
                project,
                mainStages.Todo,
                manager,
                "Hosted move Task",
                2000);
            var deletedTask = Task(
                tenantA,
                workspace,
                project,
                mainStages.Todo,
                manager,
                "Deleted PostgreSQL Task",
                3000);
            deletedTask.MarkDeleted(now.AddDays(-1), manager.Id, "PR05 hosted acceptance");
            var recentDoneTask = Task(
                tenantA,
                workspace,
                project,
                mainStages.Done,
                manager,
                "Recent Done Task",
                1000,
                TaskItemStatus.Completed);
            recentDoneTask.CompletedAt = now.AddDays(-5);
            recentDoneTask.ProgressPercent = 100;
            var oldDoneTask = Task(
                tenantA,
                workspace,
                project,
                mainStages.Done,
                manager,
                "Old Done Task",
                2000,
                TaskItemStatus.Completed);
            oldDoneTask.CompletedAt = now.AddDays(-31);
            oldDoneTask.ProgressPercent = 100;
            var targetFirstTask = Task(
                tenantA,
                workspace,
                project,
                mainStages.InProgress,
                manager,
                "In Progress First",
                1000,
                TaskItemStatus.InProgress);
            targetFirstTask.ActualStartAt = now.AddDays(-2);
            var targetLastTask = Task(
                tenantA,
                workspace,
                project,
                mainStages.InProgress,
                manager,
                "In Progress Last",
                2000,
                TaskItemStatus.InProgress);
            targetLastTask.ActualStartAt = now.AddDays(-1);
            var crossProjectNeighborTask = Task(
                tenantA,
                workspace,
                siblingProject,
                siblingStages.Todo,
                manager,
                "Cross Project Neighbor Secret",
                1000);
            dbContext.AddRange(
                visibleTask,
                moveTask,
                deletedTask,
                recentDoneTask,
                oldDoneTask,
                targetFirstTask,
                targetLastTask,
                crossProjectNeighborTask);
            await dbContext.SaveChangesAsync();

            return new HostedGraph(
                tenantA,
                tenantB,
                manager,
                member,
                outsider,
                tenantBManager,
                workspace,
                unauthorizedWorkspace,
                crossTenantWorkspace,
                project,
                siblingProject,
                unauthorizedProject,
                crossTenantProject,
                mainStages,
                visibleTask,
                moveTask,
                deletedTask,
                recentDoneTask,
                oldDoneTask,
                targetFirstTask,
                targetLastTask,
                crossProjectNeighborTask);
        }

        private static User User(string role, string suffix, IPasswordHasher passwordHasher)
        {
            var email = $"pr05-hosted-{role}-{suffix}@example.test";
            return new User
            {
                DisplayName = $"PR05 {role}",
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                PasswordHash = passwordHasher.HashPassword(Password),
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            };
        }

        private static Project Project(
            Tenant tenant,
            Workspace workspace,
            User owner,
            string name,
            string slug) => new()
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            OwnerUserId = owner.Id,
            CreatedByUserId = owner.Id,
            Name = name,
            Slug = slug,
            Status = ProjectStatus.Active,
            ActivationState = ProjectActivationState.Activated,
            ActivatedAtUtc = DateTimeOffset.UtcNow,
            ActivationVersion = 1
        };

        private static TaskItem Task(
            Tenant tenant,
            Workspace workspace,
            Project project,
            Guid workflowStageId,
            User actor,
            string title,
            long sortKey,
            TaskItemStatus status = TaskItemStatus.NotStarted) => new()
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            WorkflowStageId = workflowStageId,
            Title = title,
            SortKey = sortKey,
            CreatedByUserId = actor.Id,
            PrimaryAssigneeUserId = actor.Id,
            Priority = TaskPriority.Medium,
            Status = status,
            VersionNo = 1
        };

        private static async Task<WorkflowStageIds> StageIdsAsync(AppDbContext dbContext, Guid projectId)
        {
            var stages = await dbContext.TaskWorkflowStages
                .AsNoTracking()
                .Where(item => item.ProjectId == projectId)
                .ToDictionaryAsync(item => item.InternalCategory, item => item.Id);
            return new WorkflowStageIds(
                stages[TaskStageCategory.Backlog],
                stages[TaskStageCategory.Todo],
                stages[TaskStageCategory.InProgress],
                stages[TaskStageCategory.Review],
                stages[TaskStageCategory.Done],
                stages[TaskStageCategory.Cancelled]);
        }

        private static void SetTenant(IServiceProvider services, Tenant tenant) =>
            services.GetRequiredService<CurrentTenantService>().SetTenant(tenant.Id, tenant.Slug);

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ActorHttpClient(HttpClient client, string tenantSlug) : IDisposable
    {
        public async Task LoginAsync(string email, string password)
        {
            var token = await GetCsrfTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
            request.Content = JsonContent.Create(new LoginRequest(email, password));
            request.Headers.TryAddWithoutValidation("X-Tenant-Slug", tenantSlug);
            request.Headers.TryAddWithoutValidation(SecurityOptions.CsrfHeaderName, token);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Hosted PR05 login failed with {(int)response.StatusCode} {response.StatusCode}: {body}");
            }
        }

        public Task<HttpResponseMessage> GetAsync(string path) =>
            SendAsync(HttpMethod.Get, path, content: null, includeCsrf: false);

        public Task<HttpResponseMessage> SendJsonAsync<T>(
            HttpMethod method,
            string path,
            T body,
            bool includeCsrf = true) =>
            SendAsync(method, path, JsonContent.Create(body), includeCsrf);

        public void Dispose() => client.Dispose();

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            HttpContent? content,
            bool includeCsrf)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Content = content;
            request.Headers.TryAddWithoutValidation("X-Tenant-Slug", tenantSlug);
            if (includeCsrf)
            {
                request.Headers.TryAddWithoutValidation(
                    SecurityOptions.CsrfHeaderName,
                    await GetCsrfTokenAsync());
            }

            return await client.SendAsync(request);
        }

        private async Task<string> GetCsrfTokenAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/security/csrf-token");
            request.Headers.TryAddWithoutValidation("X-Tenant-Slug", tenantSlug);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Hosted PR05 CSRF token request failed with {(int)response.StatusCode} {response.StatusCode}: {body}");
            }

            var payload = JsonSerializer.Deserialize<CsrfTokenResponse>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return payload?.Token
                   ?? throw new InvalidOperationException("Hosted PR05 CSRF token response was empty.");
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed record HostedGraph(
        Tenant TenantA,
        Tenant TenantB,
        User Manager,
        User Member,
        User Outsider,
        User TenantBManager,
        Workspace Workspace,
        Workspace UnauthorizedWorkspace,
        Workspace CrossTenantWorkspace,
        Project Project,
        Project SiblingProject,
        Project UnauthorizedProject,
        Project CrossTenantProject,
        WorkflowStageIds MainStages,
        TaskItem VisibleTask,
        TaskItem MoveTask,
        TaskItem DeletedTask,
        TaskItem RecentDoneTask,
        TaskItem OldDoneTask,
        TaskItem TargetFirstTask,
        TaskItem TargetLastTask,
        TaskItem CrossProjectNeighborTask);

    private sealed record WorkflowStageIds(
        Guid Backlog,
        Guid Todo,
        Guid InProgress,
        Guid Review,
        Guid Done,
        Guid Cancelled)
    {
        public IReadOnlyList<Guid> AllIds => [Backlog, Todo, InProgress, Review, Done, Cancelled];
    }

    private sealed record ConfigStageState(Guid Id, long SortKey, int? WipWarningLimit, long VersionNo);

    private sealed record ConfigState(
        long BoardVersion,
        ProjectKanbanSwimlane DefaultSwimlane,
        IReadOnlyList<ConfigStageState> Stages,
        int KanbanAuditCount,
        int ProjectOutboxCount);

    private sealed record MoveState(
        Guid? WorkflowStageId,
        long SortKey,
        long TaskVersion,
        long BoardVersion,
        TaskItemStatus Status,
        string? CancellationReason,
        DateTimeOffset? CancelledAt,
        int KanbanAuditCount,
        int ProjectOutboxCount,
        IReadOnlyList<Guid> TargetStageOrder);

    private sealed record KanbanError(string Code, string Message);
}
