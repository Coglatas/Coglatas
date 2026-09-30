using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Coglatas.Application;
using Coglatas.Application.Auth;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Planning;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Coglatas.Tests.PostgreSql;

[Collection("PostgreSqlTaskV1")]
public sealed class TaskV1Pr06GanttHostedHttpTests(ITestOutputHelper output)
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task Snapshot_RealPipelineIsCanonicalDuplicateFreeAndSafelyRejectsRevokedArchivedDeletedAndCrossScopeAccess()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            await using var app = await GanttHostedTestApp.CreateAsync(database);

            using (var anonymous = await app.SendAnonymousGetAsync(
                       app.Graph.TenantA,
                       $"/api/projects/{app.Graph.Project.Id:D}/gantt"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                Assert.Equal("GANTT_AUTHENTICATION_REQUIRED", (await ReadSafeErrorAsync(anonymous)).Code);
            }

            var manager = await app.LoginAsync(app.Graph.Manager, app.Graph.TenantA);
            var viewer = await app.LoginAsync(app.Graph.Viewer, app.Graph.TenantA);
            app.BeginQueryCapture();
            using var response = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/gantt");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var snapshot = await ReadJsonAsync<ProjectGanttResponse>(response);
            var authorizedSnapshotCommands = app.EndQueryCapture();
            output.WriteLine(
                $"PR06 authorized HTTP snapshot query count: {authorizedSnapshotCommands.Count}");
            for (var index = 0; index < authorizedSnapshotCommands.Count; index++)
                output.WriteLine(
                    $"PR06 authorized HTTP SQL {index + 1:D2}: {NormalizeSql(authorizedSnapshotCommands[index])}");
            // WPC-02A-R keeps Workspace status authoritative without extra round
            // trips by hydrating the parent Workspace with membership reads.
            Assert.Equal(24, authorizedSnapshotCommands.Count);

            Assert.Equal(app.Graph.Project.Id, snapshot.ProjectId);
            Assert.True(snapshot.ProjectVersion > 0);
            Assert.Equal("UTC", snapshot.Calendar.TimeZone);
            Assert.Contains(snapshot.ScheduledItems, item => item.TaskId == app.Graph.ScheduledTask.Id);
            Assert.Contains(snapshot.ScheduledItems, item =>
                item.TaskId == app.Graph.ParentTask.Id &&
                item.ProgressIsDerived &&
                !item.ScheduleEditPermissions.CanEditSchedule);
            Assert.Contains(snapshot.UnscheduledItems, item =>
                item.TaskId == app.Graph.UnscheduledTask.Id &&
                item.Warnings.Any(warning => warning.Code == "UNSCHEDULED"));
            Assert.Contains(snapshot.Milestones, item =>
                item.TaskId == app.Graph.Milestone.Id &&
                item.Kind == WorkItemKind.Milestone &&
                item.PlannedStartDate is null &&
                item.PlannedEndDate is null);
            var projectedIds = snapshot.ScheduledItems
                .Concat(snapshot.UnscheduledItems)
                .Concat(snapshot.Milestones)
                .Select(item => item.TaskId)
                .ToList();
            Assert.Equal(snapshot.TotalItems, projectedIds.Count);
            Assert.Equal(snapshot.TotalItems, projectedIds.Distinct().Count());
            Assert.True(snapshot.Permissions.CanManageDependencies);

            using (var viewerResponse = await viewer.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/gantt"))
            {
                viewerResponse.EnsureSuccessStatusCode();
                var viewerSnapshot = await ReadJsonAsync<ProjectGanttResponse>(viewerResponse);
                Assert.False(viewerSnapshot.Permissions.CanEditSchedule);
                Assert.False(viewerSnapshot.Permissions.CanEditProgress);
                Assert.False(viewerSnapshot.Permissions.CanManageDependencies);
                Assert.All(
                    viewerSnapshot.ScheduledItems.Concat(viewerSnapshot.UnscheduledItems),
                    item =>
                    {
                        Assert.False(item.ScheduleEditPermissions.CanEditSchedule);
                        Assert.False(item.ScheduleEditPermissions.CanEditProgress);
                    });
            }

            SafeHttpError unauthorizedError;
            using (var unauthorized = await manager.GetAsync(
                       $"/api/projects/{app.Graph.UnauthorizedProject.Id:D}/gantt"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unauthorized.StatusCode);
                unauthorizedError = await ReadSafeErrorAsync(unauthorized);
            }

            using (var crossTenant = await manager.GetAsync(
                       $"/api/projects/{app.Graph.CrossTenantProject.Id:D}/gantt"))
            {
                Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
                var error = await ReadSafeErrorAsync(crossTenant);
                Assert.Equal(unauthorizedError.Code, error.Code);
                Assert.Equal(unauthorizedError.Message, error.Message);
                Assert.True(error.RedactionApplied);
                var body = await crossTenant.Content.ReadAsStringAsync();
                Assert.DoesNotContain(app.Graph.CrossTenantProject.Name, body, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    app.Graph.CrossTenantProject.Id.ToString("D"),
                    body,
                    StringComparison.OrdinalIgnoreCase);
            }

            await app.SetProjectStatusAsync(ProjectStatus.Archived);
            await AssertSafeSnapshotNotFoundAsync(manager, app.Graph.Project);
            await app.SetProjectStatusAsync(ProjectStatus.Deleted);
            await AssertSafeSnapshotNotFoundAsync(manager, app.Graph.Project);
            await app.SetProjectStatusAsync(ProjectStatus.Active);

            await app.SetWorkspaceMembershipStatusAsync(
                app.Graph.Manager,
                MembershipStatus.Suspended);
            await AssertSafeSnapshotNotFoundAsync(manager, app.Graph.Project);
            await app.SetWorkspaceMembershipStatusAsync(
                app.Graph.Manager,
                MembershipStatus.Active);
            using var restored = await manager.GetAsync($"/api/projects/{app.Graph.Project.Id:D}/gantt");
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task Snapshot_RealPipelineHonorsExactCombinedItemBoundariesWithoutPartialDto()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            await using var app = await GanttHostedTestApp.CreateAsync(database);
            var manager = await app.LoginAsync(app.Graph.Manager, app.Graph.TenantA);

            foreach (var itemCount in new[] { 499, 500 })
            {
                var project = await app.SeedBoundaryProjectAsync(itemCount, activeDependencyCount: 0);
                using var response = await manager.GetAsync($"/api/projects/{project.Id:D}/gantt");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var snapshot = await ReadJsonAsync<ProjectGanttResponse>(response);
                Assert.Equal(itemCount, snapshot.TotalItems);
                Assert.Equal(
                    itemCount,
                    snapshot.ScheduledItems.Count + snapshot.UnscheduledItems.Count + snapshot.Milestones.Count);
                Assert.Empty(snapshot.Dependencies);
            }

            var overflowProject = await app.SeedBoundaryProjectAsync(501, activeDependencyCount: 0);
            using var overflow = await manager.GetAsync($"/api/projects/{overflowProject.Id:D}/gantt");
            await AssertLimitFailureWithoutSnapshotAsync(
                overflow,
                overflowProject.Id,
                "GANTT_ITEM_LIMIT_EXCEEDED");
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task Snapshot_RealPipelineHonorsExactActiveDependencyBoundariesWithoutPartialDto()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            await using var app = await GanttHostedTestApp.CreateAsync(database);
            var manager = await app.LoginAsync(app.Graph.Manager, app.Graph.TenantA);

            foreach (var dependencyCount in new[] { 1_999, 2_000 })
            {
                var project = await app.SeedBoundaryProjectAsync(
                    activeItemCount: 65,
                    activeDependencyCount: dependencyCount);
                using var response = await manager.GetAsync($"/api/projects/{project.Id:D}/gantt");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var snapshot = await ReadJsonAsync<ProjectGanttResponse>(response);
                Assert.Equal(65, snapshot.TotalItems);
                Assert.Equal(dependencyCount, snapshot.Dependencies.Count);
            }

            var overflowProject = await app.SeedBoundaryProjectAsync(
                activeItemCount: 65,
                activeDependencyCount: 2_001);
            using var overflow = await manager.GetAsync($"/api/projects/{overflowProject.Id:D}/gantt");
            await AssertLimitFailureWithoutSnapshotAsync(
                overflow,
                overflowProject.Id,
                "GANTT_DEPENDENCY_LIMIT_EXCEEDED");
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task Commands_RealPipelineEnforcesCookieCsrfPermissionsConcurrencyAtomicityAndCanonicalPersistence()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            await using var app = await GanttHostedTestApp.CreateAsync(database);
            var manager = await app.LoginAsync(app.Graph.Manager, app.Graph.TenantA);
            var contributor = await app.LoginAsync(app.Graph.Contributor, app.Graph.TenantA);
            var viewer = await app.LoginAsync(app.Graph.Viewer, app.Graph.TenantA);

            var schedulePath = $"/api/tasks/{app.Graph.ScheduledTask.Id:D}/schedule";
            var baseline = await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id);
            var scheduleRequest = new TaskScheduleUpdateRequest(
                new DateOnly(2026, 8, 10),
                new DateOnly(2026, 8, 12),
                null,
                baseline.Version);

            using (var anonymous = await app.SendAnonymousJsonAsync(
                       app.Graph.TenantA,
                       HttpMethod.Patch,
                       $"{schedulePath}/",
                       scheduleRequest))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                Assert.Equal("GANTT_AUTHENTICATION_REQUIRED", (await ReadSafeErrorAsync(anonymous)).Code);
            }
            using (var anonymousMalformed = await app.SendAnonymousJsonAsync(
                       app.Graph.TenantA,
                       HttpMethod.Patch,
                       $"{schedulePath}/",
                       new { expectedVersion = baseline.Version }))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymousMalformed.StatusCode);
                Assert.Equal(
                    "GANTT_AUTHENTICATION_REQUIRED",
                    (await ReadSafeErrorAsync(anonymousMalformed)).Code);
            }

            using (var noCsrf = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       schedulePath,
                       scheduleRequest,
                       includeCsrf: false))
            {
                Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
                Assert.Equal("GANTT_CSRF_REQUIRED", (await ReadSafeErrorAsync(noCsrf)).Code);
            }
            Assert.Equal(baseline, await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id));

            using (var omittedScheduleFields = await manager.SendRawJsonAsync(
                       HttpMethod.Patch,
                       $"{schedulePath}/",
                       $$"""{"expectedVersion":{{baseline.Version}}}"""))
            {
                Assert.Equal(HttpStatusCode.BadRequest, omittedScheduleFields.StatusCode);
                Assert.Equal("GANTT_INVALID_REQUEST", (await ReadSafeErrorAsync(omittedScheduleFields)).Code);
            }
            Assert.Equal(baseline, await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id));

            using (var malformed = await manager.SendRawJsonAsync(
                       HttpMethod.Patch,
                       $"/api/tasks/{app.Graph.ScheduledTask.Id:D}/progress",
                       $$"""{"progressPercent":12.5,"expectedVersion":{{baseline.Version}}}"""))
            {
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
                var error = await ReadSafeErrorAsync(malformed);
                Assert.Equal("GANTT_INVALID_REQUEST", error.Code);
                var body = await malformed.Content.ReadAsStringAsync();
                Assert.DoesNotContain("System.", body, StringComparison.Ordinal);
                Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
            }

            using (var missingProgress = await manager.SendRawJsonAsync(
                       HttpMethod.Patch,
                       $"/api/tasks/{app.Graph.ScheduledTask.Id:D}/progress",
                       $$"""{"expectedVersion":{{baseline.Version}}}"""))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missingProgress.StatusCode);
                var error = await ReadSafeErrorAsync(missingProgress);
                Assert.Equal("GANTT_INVALID_PROGRESS", error.Code);
                Assert.Equal("progressPercent", error.Target);
            }
            Assert.Equal(baseline, await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id));

            using (var denied = await viewer.SendJsonAsync(
                       HttpMethod.Patch,
                       schedulePath,
                       scheduleRequest))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.Equal("GANTT_FORBIDDEN", (await ReadSafeErrorAsync(denied)).Code);
            }

            HttpResponseMessage forcedScheduleFailure;
            await app.InstallOutboxFailureConstraintAsync();
            try
            {
                forcedScheduleFailure = await manager.SendJsonAsync(
                    HttpMethod.Patch,
                    schedulePath,
                    scheduleRequest);
            }
            finally
            {
                await app.RemoveOutboxFailureConstraintAsync();
            }
            using (forcedScheduleFailure)
            {
                Assert.Equal(HttpStatusCode.InternalServerError, forcedScheduleFailure.StatusCode);
                Assert.Equal(
                    "GANTT_COMMAND_FAILED",
                    (await ReadSafeErrorAsync(forcedScheduleFailure)).Code);
            }
            Assert.Equal(baseline, await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id));

            long initialProjectVersion;
            using (var initialSnapshotResponse = await manager.GetAsync(
                       $"/api/projects/{app.Graph.Project.Id:D}/gantt"))
            {
                initialSnapshotResponse.EnsureSuccessStatusCode();
                initialProjectVersion =
                    (await ReadJsonAsync<ProjectGanttResponse>(initialSnapshotResponse)).ProjectVersion;
            }

            long scheduleVersion;
            using (var scheduleResponse = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       schedulePath,
                       scheduleRequest))
            {
                Assert.Equal(HttpStatusCode.OK, scheduleResponse.StatusCode);
                var command = await ReadJsonAsync<GanttEditCommandResponse>(scheduleResponse);
                scheduleVersion = command.Version;
                Assert.True(scheduleVersion > baseline.Version);
                Assert.Equal(new DateOnly(2026, 8, 10), command.PlannedStartDate);
                Assert.Equal(new DateOnly(2026, 8, 12), command.PlannedEndDate);
            }

            var scheduled = await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id);
            Assert.Equal(new DateOnly(2026, 8, 10), scheduled.PlannedStartDate);
            Assert.Equal(new DateOnly(2026, 8, 12), scheduled.PlannedEndDate);
            Assert.Equal(scheduled.PlannedStartDate, scheduled.LegacyStartDate);
            Assert.Equal(scheduled.PlannedEndDate, scheduled.LegacyDueDate);
            Assert.Equal(baseline.DeadlineAt, scheduled.DeadlineAt);
            Assert.Equal(baseline.ScheduleAuditCount + 1, scheduled.ScheduleAuditCount);
            Assert.True(scheduled.OutboxCount > baseline.OutboxCount);

            using (var reloadedSnapshotResponse = await manager.GetAsync(
                       $"/api/projects/{app.Graph.Project.Id:D}/gantt"))
            {
                reloadedSnapshotResponse.EnsureSuccessStatusCode();
                var reloaded = await ReadJsonAsync<ProjectGanttResponse>(reloadedSnapshotResponse);
                Assert.True(reloaded.ProjectVersion > initialProjectVersion);
                var projectEvent = await app.ReadLatestProjectChangedEventAsync();
                Assert.Equal(reloaded.ProjectVersion, projectEvent.AggregateVersion);
                using var payload = JsonDocument.Parse(projectEvent.PayloadJson);
                Assert.Equal(
                    reloaded.ProjectVersion,
                    payload.RootElement.GetProperty("payload").GetProperty("projectVersion").GetInt64());
            }

            using (var stale = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       schedulePath,
                       scheduleRequest))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                Assert.Equal("GANTT_STALE_VERSION", (await ReadSafeErrorAsync(stale)).Code);
            }
            Assert.Equal(scheduled, await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id));

            using (var progress = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       $"/api/tasks/{app.Graph.ScheduledTask.Id:D}/progress",
                       new TaskProgressUpdateRequest(55, scheduleVersion)))
            {
                progress.EnsureSuccessStatusCode();
                scheduleVersion = (await ReadJsonAsync<GanttEditCommandResponse>(progress)).Version;
            }
            Assert.Equal(55, (await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id)).ProgressPercent);

            using (var clear = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       schedulePath,
                       new TaskScheduleUpdateRequest(null, null, null, scheduleVersion)))
            {
                clear.EnsureSuccessStatusCode();
                var command = await ReadJsonAsync<GanttEditCommandResponse>(clear);
                Assert.Null(command.PlannedStartDate);
                Assert.Null(command.PlannedEndDate);
                Assert.Contains(command.Warnings, warning => warning.Code == "UNSCHEDULED");
            }

            using (var contributorEdit = await contributor.SendJsonAsync(
                       HttpMethod.Patch,
                       $"/api/tasks/{app.Graph.ContributorTask.Id:D}/schedule",
                       new TaskScheduleUpdateRequest(
                           new DateOnly(2026, 8, 21),
                           new DateOnly(2026, 8, 22),
                           null,
                           app.Graph.ContributorTask.VersionNo)))
            {
                Assert.Equal(HttpStatusCode.OK, contributorEdit.StatusCode);
            }

            using (var milestoneSchedule = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       $"/api/tasks/{app.Graph.Milestone.Id:D}/schedule",
                       new TaskScheduleUpdateRequest(
                           null,
                           null,
                           new DateOnly(2026, 9, 1),
                           app.Graph.Milestone.VersionNo)))
            {
                milestoneSchedule.EnsureSuccessStatusCode();
                var command = await ReadJsonAsync<GanttEditCommandResponse>(milestoneSchedule);
                Assert.Equal(WorkItemKind.Milestone, command.Kind);
                Assert.Equal(new DateOnly(2026, 9, 1), command.MilestoneDate);
                Assert.Equal(2, command.Version);
            }
            using (var milestoneProgress = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       $"/api/tasks/{app.Graph.Milestone.Id:D}/progress",
                       new TaskProgressUpdateRequest(100, 2)))
            {
                milestoneProgress.EnsureSuccessStatusCode();
                Assert.Equal(100, (await ReadJsonAsync<GanttEditCommandResponse>(milestoneProgress)).ProgressPercent);
            }

            var dependencyPath = $"/api/tasks/{app.Graph.Successor.Id:D}/dependencies";
            var addDependency = new AddTaskDependencyRequest(
                app.Graph.Predecessor.Id,
                TaskDependencyType.FinishToStart,
                app.Graph.Successor.VersionNo);
            using (var noCsrf = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       dependencyPath,
                       addDependency,
                       includeCsrf: false))
            {
                Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
                Assert.Equal("TASK_DEPENDENCY_CSRF_REQUIRED", (await ReadSafeErrorAsync(noCsrf)).Code);
            }

            var dependencyBaseline = await app.ReadDependencyStateAsync();
            using (var missingDependencyType = await manager.SendRawJsonAsync(
                       HttpMethod.Post,
                       dependencyPath,
                       $$"""
                         {
                           "predecessorTaskId": "{{app.Graph.Predecessor.Id:D}}",
                           "expectedVersion": {{app.Graph.Successor.VersionNo}}
                         }
                         """))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missingDependencyType.StatusCode);
                Assert.Equal(
                    "TASK_DEPENDENCY_INVALID_REQUEST",
                    (await ReadSafeErrorAsync(missingDependencyType)).Code);
            }
            using (var missingPredecessor = await manager.SendRawJsonAsync(
                       HttpMethod.Post,
                       dependencyPath,
                       $$"""
                         {
                           "dependencyType": "FinishToStart",
                           "expectedVersion": {{app.Graph.Successor.VersionNo}}
                         }
                         """))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missingPredecessor.StatusCode);
                Assert.Equal(
                    "TASK_DEPENDENCY_INVALID_REQUEST",
                    (await ReadSafeErrorAsync(missingPredecessor)).Code);
            }
            using (var lagNotSupported = await manager.SendRawJsonAsync(
                       HttpMethod.Post,
                       dependencyPath,
                       $$"""
                         {
                           "predecessorTaskId": "{{app.Graph.Predecessor.Id:D}}",
                           "dependencyType": "FinishToStart",
                           "expectedVersion": {{app.Graph.Successor.VersionNo}},
                           "lag": 1
                         }
                         """))
            {
                Assert.Equal(HttpStatusCode.BadRequest, lagNotSupported.StatusCode);
                Assert.Equal(
                    "TASK_DEPENDENCY_INVALID_REQUEST",
                    (await ReadSafeErrorAsync(lagNotSupported)).Code);
            }
            using (var leadNotSupported = await manager.SendRawJsonAsync(
                       HttpMethod.Post,
                       dependencyPath,
                       $$"""
                         {
                           "predecessorTaskId": "{{app.Graph.Predecessor.Id:D}}",
                           "dependencyType": "FinishToStart",
                           "expectedVersion": {{app.Graph.Successor.VersionNo}},
                           "lead": 1
                         }
                         """))
            {
                Assert.Equal(HttpStatusCode.BadRequest, leadNotSupported.StatusCode);
                Assert.Equal(
                    "TASK_DEPENDENCY_INVALID_REQUEST",
                    (await ReadSafeErrorAsync(leadNotSupported)).Code);
            }
            Assert.Equal(dependencyBaseline, await app.ReadDependencyStateAsync());

            HttpResponseMessage forcedDependencyFailure;
            await app.InstallOutboxFailureConstraintAsync();
            try
            {
                forcedDependencyFailure = await manager.SendJsonAsync(
                    HttpMethod.Post,
                    dependencyPath,
                    addDependency);
            }
            finally
            {
                await app.RemoveOutboxFailureConstraintAsync();
            }
            using (forcedDependencyFailure)
            {
                Assert.Equal(HttpStatusCode.InternalServerError, forcedDependencyFailure.StatusCode);
                Assert.Equal(
                    "TASK_DEPENDENCY_COMMAND_FAILED",
                    (await ReadSafeErrorAsync(forcedDependencyFailure)).Code);
            }
            Assert.Equal(dependencyBaseline, await app.ReadDependencyStateAsync());

            TaskDependencyResponse added;
            using (var addResponse = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       dependencyPath,
                       addDependency))
            {
                Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
                added = await ReadJsonAsync<TaskDependencyResponse>(addResponse);
                Assert.True(added.Editable);
                Assert.Contains(added.Warnings, warning => warning.Code == "DEPENDENCY_VIOLATION");
            }
            var afterAdd = await app.ReadDependencyStateAsync();
            Assert.Equal(1, afterAdd.DependencyCount);
            Assert.Equal(dependencyBaseline.PredecessorPlannedEnd, afterAdd.PredecessorPlannedEnd);
            Assert.Equal(dependencyBaseline.SuccessorPlannedStart, afterAdd.SuccessorPlannedStart);
            Assert.True(afterAdd.SuccessorVersion > dependencyBaseline.SuccessorVersion);
            Assert.Equal(dependencyBaseline.AddAuditCount + 1, afterAdd.AddAuditCount);

            using (var staleDelete = await manager.SendJsonAsync(
                       HttpMethod.Delete,
                       $"{dependencyPath}/{added.Id:D}?expectedVersion={dependencyBaseline.SuccessorVersion}",
                       new { }))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
                Assert.Equal("TASK_STALE_VERSION", (await ReadSafeErrorAsync(staleDelete)).Code);
            }

            using (var crossProject = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       dependencyPath,
                       new AddTaskDependencyRequest(
                           app.Graph.CrossProjectTask.Id,
                           TaskDependencyType.FinishToStart,
                           afterAdd.SuccessorVersion)))
            {
                Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);
                var error = await ReadSafeErrorAsync(crossProject);
                Assert.Equal("TASK_DEPENDENCY_NOT_FOUND", error.Code);
                Assert.True(error.RedactionApplied);
                var body = await crossProject.Content.ReadAsStringAsync();
                Assert.DoesNotContain(app.Graph.CrossProjectTask.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(app.Graph.CrossProjectTask.Title, body, StringComparison.Ordinal);
            }

            using (var nonFs = await manager.SendJsonAsync(
                       HttpMethod.Post,
                       dependencyPath,
                       new AddTaskDependencyRequest(
                           app.Graph.CrossProjectTask.Id,
                           TaskDependencyType.StartToStart,
                           afterAdd.SuccessorVersion)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, nonFs.StatusCode);
                Assert.Equal("TASK_DEPENDENCY_TYPE_DEFERRED", (await ReadSafeErrorAsync(nonFs)).Code);
            }

            using (var remove = await manager.SendJsonAsync(
                       HttpMethod.Delete,
                       $"{dependencyPath}/{added.Id:D}?expectedVersion={afterAdd.SuccessorVersion}",
                       new { }))
            {
                Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
            }
            var afterRemove = await app.ReadDependencyStateAsync();
            Assert.Equal(0, afterRemove.DependencyCount);
            Assert.Equal(afterAdd.RemoveAuditCount + 1, afterRemove.RemoveAuditCount);

            var currentSchedule = await app.ReadTaskStateAsync(app.Graph.ScheduledTask.Id);
            var lifecycleRequest = new TaskScheduleUpdateRequest(
                new DateOnly(2026, 9, 5),
                new DateOnly(2026, 9, 6),
                null,
                currentSchedule.Version);
            await app.SetProjectStatusAsync(ProjectStatus.Archived);
            using (var archived = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       schedulePath,
                       lifecycleRequest))
            {
                Assert.Equal(HttpStatusCode.NotFound, archived.StatusCode);
                var error = await ReadSafeErrorAsync(archived);
                Assert.Equal("GANTT_WORK_ITEM_NOT_FOUND", error.Code);
                Assert.True(error.RedactionApplied);
            }
            await app.SetProjectStatusAsync(ProjectStatus.Active);

            await app.SetWorkspaceMembershipStatusAsync(
                app.Graph.Manager,
                MembershipStatus.Suspended);
            using (var revoked = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       schedulePath,
                       lifecycleRequest))
            {
                Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
                var error = await ReadSafeErrorAsync(revoked);
                Assert.Equal("GANTT_WORK_ITEM_NOT_FOUND", error.Code);
                Assert.True(error.RedactionApplied);
            }
            await app.SetWorkspaceMembershipStatusAsync(
                app.Graph.Manager,
                MembershipStatus.Active);

            using (var hiddenCrossTenant = await manager.SendJsonAsync(
                       HttpMethod.Patch,
                       $"/api/tasks/{app.Graph.CrossTenantTask.Id:D}/schedule",
                       new TaskScheduleUpdateRequest(
                           new DateOnly(2026, 9, 3),
                           new DateOnly(2026, 9, 4),
                           null,
                           app.Graph.CrossTenantTask.VersionNo)))
            {
                Assert.Equal(HttpStatusCode.NotFound, hiddenCrossTenant.StatusCode);
                var error = await ReadSafeErrorAsync(hiddenCrossTenant);
                Assert.True(error.RedactionApplied);
                var body = await hiddenCrossTenant.Content.ReadAsStringAsync();
                Assert.DoesNotContain(app.Graph.CrossTenantTask.Title, body, StringComparison.Ordinal);
                Assert.DoesNotContain(app.Graph.CrossTenantTask.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    private static async Task AssertSafeSnapshotNotFoundAsync(ActorHttpClient client, Project project)
    {
        using var response = await client.GetAsync($"/api/projects/{project.Id:D}/gantt");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await ReadSafeErrorAsync(response);
        Assert.Equal("GANTT_PROJECT_NOT_FOUND", error.Code);
        Assert.True(error.RedactionApplied);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(project.Name, body, StringComparison.Ordinal);
    }

    private static async Task AssertLimitFailureWithoutSnapshotAsync(
        HttpResponseMessage response,
        Guid projectId,
        string expectedCode)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ReadSafeErrorAsync(response);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal("projectId", error.Target);
        Assert.False(error.RedactionApplied);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(projectId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scheduledItems", body, StringComparison.Ordinal);
        Assert.DoesNotContain("unscheduledItems", body, StringComparison.Ordinal);
        Assert.DoesNotContain("milestones", body, StringComparison.Ordinal);
        Assert.DoesNotContain("dependencies", body, StringComparison.Ordinal);
        Assert.DoesNotContain("totalItems", body, StringComparison.Ordinal);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>()
        ?? throw new InvalidOperationException($"HTTP response did not contain {typeof(T).Name}.");

    private static async Task<SafeHttpError> ReadSafeErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var error = root.GetProperty("error");
        return new SafeHttpError(
            root.GetProperty("requestId").GetString() ?? string.Empty,
            error.GetProperty("code").GetString() ?? string.Empty,
            error.GetProperty("message").GetString() ?? string.Empty,
            error.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String
                ? target.GetString()
                : null,
            error.TryGetProperty("redactionApplied", out var redaction) && redaction.GetBoolean());
    }

    private sealed class GanttHostedTestApp : IAsyncDisposable
    {
        private const string Password = "GanttAcceptance!234";
        private const string TenantHeader = "X-Tenant-Slug";
        private const string OutboxFailureConstraint = "CK_TaskV1Pr06_ForceOutboxFailure";

        private readonly WebApplication _app;
        private readonly Uri _baseAddress;
        private readonly string _connectionString;
        private readonly string _fileStoragePath;
        private readonly string _dataProtectionPath;
        private readonly HostedCommandCounter _queryCounter;
        private readonly List<IDisposable> _clients = [];

        private GanttHostedTestApp(
            WebApplication app,
            Uri baseAddress,
            string connectionString,
            string fileStoragePath,
            string dataProtectionPath,
            HostedGraph graph,
            HostedCommandCounter queryCounter)
        {
            _app = app;
            _baseAddress = baseAddress;
            _connectionString = connectionString;
            _fileStoragePath = fileStoragePath;
            _dataProtectionPath = dataProtectionPath;
            _queryCounter = queryCounter;
            Graph = graph;
        }

        public HostedGraph Graph { get; }

        public static async Task<GanttHostedTestApp> CreateAsync(string connectionString)
        {
            var runId = Guid.NewGuid().ToString("N");
            var root = Path.Combine(Path.GetTempPath(), "coglatas-pr06-hosted-tests", runId);
            var fileStoragePath = Path.Combine(root, "files");
            var dataProtectionPath = Path.Combine(root, "keys");
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
            var queryCounter = new HostedCommandCounter();
            builder.Services.AddSingleton(queryCounter);
            builder.Services.AddDbContext<AppDbContext>((services, options) =>
                options.AddInterceptors(services.GetRequiredService<HostedCommandCounter>()));
            builder.Services.AddSingleton<IClock>(new FixedClock(now));
            builder.Services.AddControllers().AddApplicationPart(typeof(PlanningController).Assembly);
            builder.Services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = $".Coglatas.Auth.PR06.{runId}";
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
                ?? throw new InvalidOperationException("PR06 hosted test server address was unavailable.");
            return new GanttHostedTestApp(
                app,
                new Uri(address),
                connectionString,
                fileStoragePath,
                dataProtectionPath,
                graph,
                queryCounter);
        }

        public void BeginQueryCapture() => _queryCounter.Begin();

        public IReadOnlyList<string> EndQueryCapture() => _queryCounter.End();

        public async Task<ActorHttpClient> LoginAsync(User actor, Tenant tenant)
        {
            var client = NewClient();
            var actorClient = new ActorHttpClient(client, tenant.Slug);
            _clients.Add(actorClient);
            await actorClient.LoginAsync(actor.Email, Password);
            return actorClient;
        }

        public async Task<HttpResponseMessage> SendAnonymousGetAsync(Tenant tenant, string path)
        {
            var client = NewClient();
            _clients.Add(client);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation(TenantHeader, tenant.Slug);
            return await client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> SendAnonymousJsonAsync<T>(
            Tenant tenant,
            HttpMethod method,
            string path,
            T body)
        {
            var client = NewClient();
            _clients.Add(client);
            using var request = new HttpRequestMessage(method, path);
            request.Content = JsonContent.Create(body);
            request.Headers.TryAddWithoutValidation(TenantHeader, tenant.Slug);
            return await client.SendAsync(request);
        }

        public async Task SetProjectStatusAsync(ProjectStatus status)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = await db.Projects.SingleAsync(item => item.Id == Graph.Project.Id);
            project.Status = status;
            if (status == ProjectStatus.Deleted)
                project.MarkDeleted(DateTimeOffset.UtcNow, Graph.Manager.Id, "PR06 hosted deleted-state check");
            else
                project.Restore();
            await db.SaveChangesAsync();
        }

        public async Task SetWorkspaceMembershipStatusAsync(User user, MembershipStatus status)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = await db.WorkspaceMembers.SingleAsync(item =>
                item.WorkspaceId == Graph.Workspace.Id &&
                item.UserId == user.Id);
            membership.Status = status;
            await db.SaveChangesAsync();
        }

        public async Task<Project> SeedBoundaryProjectAsync(
            int activeItemCount,
            int activeDependencyCount)
        {
            if (activeItemCount < 1)
                throw new ArgumentOutOfRangeException(nameof(activeItemCount));

            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var suffix = Guid.NewGuid().ToString("N");
            var project = Project(
                Graph.TenantA,
                Graph.Workspace,
                Graph.Manager,
                $"pr06-boundary-{suffix}",
                $"PR06 boundary {activeItemCount}-{activeDependencyCount}");
            db.AddRange(
                project,
                ProjectMember(
                    Graph.TenantA,
                    project,
                    Graph.Manager,
                    ProjectRole.Manager,
                    DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            var stageId = await TodoStageAsync(db, project.Id);
            var activeTaskCount = activeItemCount - 1;
            var tasks = Enumerable.Range(0, activeTaskCount)
                .Select(index => Task(
                    Graph.TenantA,
                    Graph.Workspace,
                    project,
                    stageId,
                    Graph.Manager,
                    $"Boundary Task {index:D3}",
                    index + 1))
                .ToList();
            var milestone = new Milestone
            {
                TenantId = Graph.TenantA.Id,
                ProjectId = project.Id,
                Name = "Boundary Milestone",
                DueDate = new DateOnly(2026, 9, 1),
                Status = MilestoneStatus.NotStarted,
                SortOrder = 1,
                VersionNo = 1
            };
            db.AddRange(tasks);
            db.Add(milestone);
            await db.SaveChangesAsync();

            if (activeDependencyCount == 0)
                return project;

            var maximumAvailableDependencies = activeTaskCount * (activeTaskCount - 1) / 2;
            if (activeDependencyCount > maximumAvailableDependencies)
                throw new ArgumentOutOfRangeException(nameof(activeDependencyCount));

            var dependencies = new List<TaskDependency>(activeDependencyCount + 1);
            for (var predecessorIndex = 0;
                 predecessorIndex < tasks.Count && dependencies.Count < activeDependencyCount;
                 predecessorIndex++)
            {
                for (var successorIndex = predecessorIndex + 1;
                     successorIndex < tasks.Count && dependencies.Count < activeDependencyCount;
                     successorIndex++)
                {
                    dependencies.Add(new TaskDependency
                    {
                        TenantId = Graph.TenantA.Id,
                        ProjectId = project.Id,
                        PredecessorTaskItemId = tasks[predecessorIndex].Id,
                        SuccessorTaskItemId = tasks[successorIndex].Id,
                        DependencyType = TaskDependencyType.FinishToStart
                    });
                }
            }

            var deletedEndpoint = Task(
                Graph.TenantA,
                Graph.Workspace,
                project,
                stageId,
                Graph.Manager,
                "Deleted dependency endpoint",
                activeTaskCount + 1);
            deletedEndpoint.MarkDeleted(
                DateTimeOffset.UtcNow,
                Graph.Manager.Id,
                "PR06 boundary inactive dependency check");
            db.TaskItems.Add(deletedEndpoint);
            dependencies.Add(new TaskDependency
            {
                TenantId = Graph.TenantA.Id,
                ProjectId = project.Id,
                PredecessorTaskItemId = tasks[0].Id,
                SuccessorTaskItemId = deletedEndpoint.Id,
                DependencyType = TaskDependencyType.FinishToStart
            });
            db.TaskDependencies.AddRange(dependencies);
            await db.SaveChangesAsync();
            return project;
        }

        public async Task<TaskState> ReadTaskStateAsync(Guid taskId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var task = await db.TaskItems.AsNoTracking().SingleAsync(item => item.Id == taskId);
            return new TaskState(
                task.PlannedStartDate,
                task.PlannedEndDate,
                task.StartDate,
                task.DueDate,
                task.DeadlineAt,
                task.ProgressPercent,
                task.VersionNo,
                await db.AuditLogs.CountAsync(item => item.EntityId == taskId && item.Action == "TaskScheduleUpdated"),
                await db.AuditLogs.CountAsync(item => item.EntityId == taskId && item.Action == "TaskProgressUpdated"),
                await db.OutboxEvents.CountAsync(item =>
                    item.AggregateId == taskId ||
                    item.AggregateId == task.ProjectId));
        }

        public async Task<DependencyState> ReadDependencyStateAsync()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var predecessor = await db.TaskItems.AsNoTracking()
                .SingleAsync(item => item.Id == Graph.Predecessor.Id);
            var successor = await db.TaskItems.AsNoTracking()
                .SingleAsync(item => item.Id == Graph.Successor.Id);
            return new DependencyState(
                await db.TaskDependencies.CountAsync(item => item.ProjectId == Graph.Project.Id),
                predecessor.PlannedEndDate,
                successor.PlannedStartDate,
                successor.VersionNo,
                await db.AuditLogs.CountAsync(item =>
                    item.ProjectId == Graph.Project.Id &&
                    item.Action == "TaskDependencyAdded"),
                await db.AuditLogs.CountAsync(item =>
                    item.ProjectId == Graph.Project.Id &&
                    item.Action == "TaskDependencyRemoved"),
                await db.OutboxEvents.CountAsync(item =>
                    item.AggregateId == Graph.Successor.Id ||
                    item.AggregateId == Graph.Project.Id));
        }

        public async Task<OutboxEvent> ReadLatestProjectChangedEventAsync()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            SetTenant(scope.ServiceProvider, Graph.TenantA);
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboxEvents
                .AsNoTracking()
                .Where(item =>
                    item.AggregateId == Graph.Project.Id &&
                    item.EventType == "Projects.ProjectChanged.v1")
                .OrderByDescending(item => item.AggregateVersion)
                .ThenByDescending(item => item.OccurredAt)
                .FirstAsync();
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
            foreach (var client in _clients)
                client.Dispose();
            await _app.DisposeAsync();
            TryDeleteDirectory(Path.GetDirectoryName(_fileStoragePath)!);
            TryDeleteDirectory(Path.GetDirectoryName(_dataProtectionPath)!);
        }

        private HttpClient NewClient() =>
            new(
                new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = new CookieContainer()
                })
            {
                BaseAddress = _baseAddress
            };

        private static async Task<HostedGraph> SeedAsync(IServiceProvider services, DateTimeOffset now)
        {
            await using var scope = services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var suffix = Guid.NewGuid().ToString("N");

            var tenantA = Tenant($"pr06-a-{suffix}", "PR06 tenant A");
            var tenantB = Tenant($"pr06-b-{suffix}", "PR06 tenant B");
            var owner = User("owner", suffix, passwordHasher);
            var manager = User("manager", suffix, passwordHasher);
            var contributor = User("contributor", suffix, passwordHasher);
            var viewer = User("viewer", suffix, passwordHasher);
            var outsider = User("outsider", suffix, passwordHasher);
            var tenantBManager = User("tenant-b-manager", suffix, passwordHasher);
            db.AddRange(tenantA, tenantB, owner, manager, contributor, viewer, outsider, tenantBManager);
            await db.SaveChangesAsync();

            var workspace = Workspace(tenantA, owner, $"pr06-workspace-{suffix}", "PR06 Gantt workspace");
            var unauthorizedWorkspace = Workspace(
                tenantA,
                outsider,
                $"pr06-unauthorized-{suffix}",
                "PR06 unauthorized workspace");
            var crossTenantWorkspace = Workspace(
                tenantB,
                tenantBManager,
                $"pr06-cross-{suffix}",
                "PR06 cross-tenant workspace");
            var project = Project(tenantA, workspace, owner, $"pr06-project-{suffix}", "PR06 canonical schedule");
            var sibling = Project(tenantA, workspace, owner, $"pr06-sibling-{suffix}", "PR06 sibling schedule");
            var unauthorizedProject = Project(
                tenantA,
                unauthorizedWorkspace,
                outsider,
                $"pr06-hidden-{suffix}",
                "PR06 hidden schedule");
            var crossTenantProject = Project(
                tenantB,
                crossTenantWorkspace,
                tenantBManager,
                $"pr06-cross-project-{suffix}",
                "PR06 cross-tenant secret schedule");

            db.AddRange(
                TenantUser(tenantA, owner, TenantUserRole.Owner, now),
                TenantUser(tenantA, manager, TenantUserRole.Member, now),
                TenantUser(tenantA, contributor, TenantUserRole.Member, now),
                TenantUser(tenantA, viewer, TenantUserRole.Member, now),
                TenantUser(tenantA, outsider, TenantUserRole.Member, now),
                TenantUser(tenantB, tenantBManager, TenantUserRole.Owner, now),
                workspace,
                unauthorizedWorkspace,
                crossTenantWorkspace,
                WorkspaceMember(tenantA, workspace, owner, WorkspaceRole.Owner, now),
                WorkspaceMember(tenantA, workspace, manager, WorkspaceRole.Member, now),
                WorkspaceMember(tenantA, workspace, contributor, WorkspaceRole.Member, now),
                WorkspaceMember(tenantA, workspace, viewer, WorkspaceRole.Member, now),
                WorkspaceMember(tenantA, unauthorizedWorkspace, outsider, WorkspaceRole.Owner, now),
                WorkspaceMember(tenantB, crossTenantWorkspace, tenantBManager, WorkspaceRole.Owner, now),
                project,
                sibling,
                unauthorizedProject,
                crossTenantProject,
                ProjectMember(tenantA, project, owner, ProjectRole.Owner, now),
                ProjectMember(tenantA, project, manager, ProjectRole.Manager, now),
                ProjectMember(tenantA, project, contributor, ProjectRole.Contributor, now),
                ProjectMember(tenantA, project, viewer, ProjectRole.Viewer, now),
                ProjectMember(tenantA, sibling, owner, ProjectRole.Owner, now),
                ProjectMember(tenantA, unauthorizedProject, outsider, ProjectRole.Owner, now),
                ProjectMember(tenantB, crossTenantProject, tenantBManager, ProjectRole.Owner, now));
            await db.SaveChangesAsync();

            var mainStage = await TodoStageAsync(db, project.Id);
            var siblingStage = await TodoStageAsync(db, sibling.Id);
            var crossTenantStage = await TodoStageAsync(db, crossTenantProject.Id);
            var scheduled = Task(tenantA, workspace, project, mainStage, manager, "Scheduled Task", 1000);
            scheduled.PlannedStartDate = scheduled.StartDate = new DateOnly(2026, 8, 1);
            scheduled.PlannedEndDate = scheduled.DueDate = new DateOnly(2026, 8, 4);
            scheduled.DeadlineAt = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
            scheduled.ProgressPercent = 20;
            var contributorTask = Task(
                tenantA,
                workspace,
                project,
                mainStage,
                contributor,
                "Contributor-owned Task",
                2000);
            var predecessor = Task(tenantA, workspace, project, mainStage, manager, "Predecessor Task", 3000);
            predecessor.PlannedStartDate = predecessor.StartDate = new DateOnly(2026, 8, 2);
            predecessor.PlannedEndDate = predecessor.DueDate = new DateOnly(2026, 8, 10);
            var successor = Task(tenantA, workspace, project, mainStage, manager, "Successor Task", 4000);
            successor.PlannedStartDate = successor.StartDate = new DateOnly(2026, 8, 5);
            successor.PlannedEndDate = successor.DueDate = new DateOnly(2026, 8, 8);
            var unscheduled = Task(tenantA, workspace, project, mainStage, manager, "Unscheduled Task", 5000);
            var parent = Task(tenantA, workspace, project, mainStage, manager, "Derived Parent", 6000);
            var child = Task(tenantA, workspace, project, mainStage, manager, "Derived Child", 6100);
            child.ParentTaskItemId = parent.Id;
            child.PlannedStartDate = child.StartDate = new DateOnly(2026, 8, 6);
            child.PlannedEndDate = child.DueDate = new DateOnly(2026, 8, 9);
            child.ProgressPercent = 40;
            var crossProjectTask = Task(
                tenantA,
                workspace,
                sibling,
                siblingStage,
                manager,
                "Cross-project secret Task",
                1000);
            var crossTenantTask = Task(
                tenantB,
                crossTenantWorkspace,
                crossTenantProject,
                crossTenantStage,
                tenantBManager,
                "Cross-tenant secret Task",
                1000);
            var milestone = new Milestone
            {
                TenantId = tenantA.Id,
                ProjectId = project.Id,
                Name = "Release Milestone",
                DueDate = new DateOnly(2026, 8, 31),
                Status = MilestoneStatus.NotStarted,
                SortOrder = 1,
                VersionNo = 1
            };
            db.AddRange(
                scheduled,
                contributorTask,
                predecessor,
                successor,
                unscheduled,
                parent,
                child,
                crossProjectTask,
                crossTenantTask,
                milestone);
            await db.SaveChangesAsync();

            return new HostedGraph(
                tenantA,
                manager,
                contributor,
                viewer,
                workspace,
                project,
                unauthorizedProject,
                crossTenantProject,
                scheduled,
                contributorTask,
                predecessor,
                successor,
                unscheduled,
                parent,
                crossProjectTask,
                crossTenantTask,
                milestone);
        }

        private static Tenant Tenant(string slug, string name) => new()
        {
            Name = name,
            DisplayName = name,
            Slug = slug,
            Status = TenantStatus.Active
        };

        private static User User(string role, string suffix, IPasswordHasher passwordHasher)
        {
            var email = $"pr06-hosted-{role}-{suffix}@example.test";
            return new User
            {
                DisplayName = $"PR06 {role}",
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                PasswordHash = passwordHasher.HashPassword(Password),
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            };
        }

        private static Workspace Workspace(Tenant tenant, User owner, string slug, string name) => new()
        {
            TenantId = tenant.Id,
            Name = name,
            Slug = slug,
            Status = WorkspaceStatus.Active,
            CreatedByUserId = owner.Id,
            TimeZone = "UTC"
        };

        private static Project Project(Tenant tenant, Workspace workspace, User owner, string slug, string name) => new()
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            OwnerUserId = owner.Id,
            CreatedByUserId = owner.Id,
            Name = name,
            Slug = slug,
            Status = ProjectStatus.Active,
            ActivationState = ProjectActivationState.Activated,
            ActivatedAtUtc = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            ActivationVersion = 1,
            VersionNo = 1
        };

        private static TenantUser TenantUser(
            Tenant tenant,
            User user,
            TenantUserRole role,
            DateTimeOffset now) => new()
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            Role = role,
            Status = TenantUserStatus.Active,
            JoinedAt = now
        };

        private static WorkspaceMember WorkspaceMember(
            Tenant tenant,
            Workspace workspace,
            User user,
            WorkspaceRole role,
            DateTimeOffset now) => new()
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = role,
            Status = MembershipStatus.Active,
            JoinedAt = now
        };

        private static ProjectMember ProjectMember(
            Tenant tenant,
            Project project,
            User user,
            ProjectRole role,
            DateTimeOffset now) => new()
        {
            TenantId = tenant.Id,
            ProjectId = project.Id,
            UserId = user.Id,
            Role = role,
            JoinedAt = now
        };

        private static TaskItem Task(
            Tenant tenant,
            Workspace workspace,
            Project project,
            Guid stageId,
            User actor,
            string title,
            long sortKey) => new()
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            WorkflowStageId = stageId,
            Title = title,
            SortKey = sortKey,
            CreatedByUserId = actor.Id,
            PrimaryAssigneeUserId = actor.Id,
            Priority = TaskPriority.Medium,
            Status = TaskItemStatus.NotStarted,
            VersionNo = 1
        };

        private static async Task<Guid> TodoStageAsync(AppDbContext db, Guid projectId) =>
            await db.TaskWorkflowStages
                .Where(stage =>
                    stage.ProjectId == projectId &&
                    stage.InternalCategory == TaskStageCategory.Todo)
                .Select(stage => stage.Id)
                .SingleAsync();

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
                throw new InvalidOperationException($"PR06 hosted login failed: {(int)response.StatusCode} {body}");
        }

        public Task<HttpResponseMessage> GetAsync(string path) =>
            SendAsync(HttpMethod.Get, path, null, includeCsrf: false);

        public Task<HttpResponseMessage> SendJsonAsync<T>(
            HttpMethod method,
            string path,
            T body,
            bool includeCsrf = true) =>
            SendAsync(method, path, JsonContent.Create(body), includeCsrf);

        public Task<HttpResponseMessage> SendRawJsonAsync(
            HttpMethod method,
            string path,
            string body,
            bool includeCsrf = true) =>
            SendAsync(
                method,
                path,
                new StringContent(body, Encoding.UTF8, "application/json"),
                includeCsrf);

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
            var payload = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>();
            return payload?.Token
                   ?? throw new InvalidOperationException("PR06 hosted CSRF response was empty.");
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed record HostedGraph(
        Tenant TenantA,
        User Manager,
        User Contributor,
        User Viewer,
        Workspace Workspace,
        Project Project,
        Project UnauthorizedProject,
        Project CrossTenantProject,
        TaskItem ScheduledTask,
        TaskItem ContributorTask,
        TaskItem Predecessor,
        TaskItem Successor,
        TaskItem UnscheduledTask,
        TaskItem ParentTask,
        TaskItem CrossProjectTask,
        TaskItem CrossTenantTask,
        Milestone Milestone);

    private sealed record TaskState(
        DateOnly? PlannedStartDate,
        DateOnly? PlannedEndDate,
        DateOnly? LegacyStartDate,
        DateOnly? LegacyDueDate,
        DateTimeOffset? DeadlineAt,
        int ProgressPercent,
        long Version,
        int ScheduleAuditCount,
        int ProgressAuditCount,
        int OutboxCount);

    private sealed record DependencyState(
        int DependencyCount,
        DateOnly? PredecessorPlannedEnd,
        DateOnly? SuccessorPlannedStart,
        long SuccessorVersion,
        int AddAuditCount,
        int RemoveAuditCount,
        int OutboxCount);

    private sealed record SafeHttpError(
        string RequestId,
        string Code,
        string Message,
        string? Target,
        bool RedactionApplied);

    private sealed class HostedCommandCounter : DbCommandInterceptor
    {
        private readonly Lock _sync = new();
        private readonly List<string> _commands = [];
        private bool _active;

        public void Begin()
        {
            lock (_sync)
            {
                _commands.Clear();
                _active = true;
            }
        }

        public IReadOnlyList<string> End()
        {
            lock (_sync)
            {
                _active = false;
                return _commands.ToArray();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            lock (_sync)
            {
                if (_active)
                    _commands.Add(command.CommandText);
            }
        }
    }

    private static string NormalizeSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
