using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Coglatas.Application;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Notifications;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Files;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Security;
using Coglatas.Web.Controllers;
using Coglatas.Web.Extensions;
using Coglatas.Web.Configuration;
using Coglatas.Web.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Coglatas.Tests.Tenancy;

public sealed class HttpTenantIsolationTests
{
    private sealed record PlanningProjectGraph(
        Project Project,
        TaskItem Task,
        Artifact Artifact,
        ActivityLog ActivityLog,
        Comment Comment,
        Conversation Conversation,
        Message Message);

    [Fact]
    [Trait("Scope", "WPC01")]
    [Trait("Scope", "Issue409")]
    public async Task WorkspaceCreateCoordinatorHttpSeamBindsCapabilityAuthorizationAndIdempotency()
    {
        // This explicit no-op initialization seam isolates the HTTP and
        // idempotency coordinator contract. It is not evidence that canonical
        // general-channel provisioning exists in production.
        await using var app = await HttpTenantIsolationTestApp.CreateAsync(workspaceInitializationAvailable: true);
        var data = app.Data;

        Assert.Contains("GET api/workspaces/capabilities", app.GetHttpRoutes());
        Assert.Contains("POST api/workspaces", app.GetHttpRoutes());
        Assert.Contains("POST api/workspaces/{workspaceId:guid}/projects", app.GetHttpRoutes());
        Assert.Contains("GET api/workspaces/{workspaceId:guid}/projects/create-options", app.GetHttpRoutes());
        Assert.Contains("POST api/projects/{projectId:guid}/activate", app.GetHttpRoutes());

        using (var ownerOptions = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   $"/api/workspaces/{data.WorkspaceA.Id}/projects/create-options"))
        using (var document = JsonDocument.Parse(await ownerOptions.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, ownerOptions.StatusCode);
            var options = document.RootElement.GetProperty("data");
            Assert.Equal(data.WorkspaceA.Id, options.GetProperty("workspaceId").GetGuid());
            Assert.True(options.GetProperty("canCreateUngrouped").GetBoolean());
            Assert.Equal(
                new[] { 0, 1, 2 },
                options.GetProperty("allowedVisibilities").EnumerateArray().Select(item => item.GetInt32()));
            var group = Assert.Single(options.GetProperty("groups").EnumerateArray());
            Assert.Equal(data.GroupA.Id, group.GetProperty("id").GetGuid());
            Assert.Equal(data.GroupA.Name, group.GetProperty("name").GetString());
        }

        using (var memberOptions = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/workspaces/{data.WorkspaceA.Id}/projects/create-options"))
        using (var document = JsonDocument.Parse(await memberOptions.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, memberOptions.StatusCode);
            var options = document.RootElement.GetProperty("data");
            Assert.False(options.GetProperty("canCreateUngrouped").GetBoolean());
            Assert.Empty(options.GetProperty("allowedVisibilities").EnumerateArray());
            Assert.Empty(options.GetProperty("groups").EnumerateArray());
        }

        using (var crossTenantOptions = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/workspaces/{data.WorkspaceA.Id}/projects/create-options"))
        {
            var body = await crossTenantOptions.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, crossTenantOptions.StatusCode);
            Assert.DoesNotContain(data.GroupA.Name, body, StringComparison.Ordinal);
        }

        using (var ownerCapability = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/workspaces/capabilities"))
        using (var document = JsonDocument.Parse(await ownerCapability.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, ownerCapability.StatusCode);
            Assert.True(document.RootElement.GetProperty("data").GetProperty("canCreate").GetBoolean());
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("warnings").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("requestId").GetString()));
        }

        using (var memberCapability = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, "/api/workspaces/capabilities"))
        using (var document = JsonDocument.Parse(await memberCapability.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, memberCapability.StatusCode);
            Assert.False(document.RootElement.GetProperty("data").GetProperty("canCreate").GetBoolean());
        }

        using (var missingIdentity = await SendWorkspaceCreateAsync(
                   app,
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   null,
                   "Missing identity"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingIdentity.StatusCode);
            using var document = JsonDocument.Parse(await missingIdentity.Content.ReadAsStringAsync());
            Assert.Equal("MissingIdempotencyKey", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("header.Idempotency-Key", document.RootElement.GetProperty("error").GetProperty("target").GetString());
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("error").GetProperty("details").ValueKind);
            AssertCompleteErrorEnvelope(document.RootElement, 400, "MissingIdempotencyKey", "header.Idempotency-Key");
        }

        using (var denied = await SendWorkspaceCreateAsync(
                   app,
                   data.TenantAMember,
                   data.TenantA.Slug,
                   "wpc01-member-denied",
                   "Denied member workspace"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var document = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
            Assert.Equal("CapabilityDenied", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("workspace", document.RootElement.GetProperty("error").GetProperty("target").GetString());
            AssertCompleteErrorEnvelope(document.RootElement, 403, "CapabilityDenied", "workspace");
        }

        using (var invalidIdentity = await SendWorkspaceCreateAsync(
                   app,
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "short",
                   "Invalid identity"))
        using (var document = JsonDocument.Parse(await invalidIdentity.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidIdentity.StatusCode);
            Assert.Equal("InvalidIdempotencyKey", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("header.Idempotency-Key", document.RootElement.GetProperty("error").GetProperty("target").GetString());
            Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
            AssertCompleteErrorEnvelope(document.RootElement, 400, "InvalidIdempotencyKey", "header.Idempotency-Key");
        }

        Guid createdId;
        using (var created = await SendWorkspaceCreateAsync(
                   app,
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "wpc01-http-create",
                   "HTTP Created Workspace"))
        using (var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var dataElement = document.RootElement.GetProperty("data");
            createdId = dataElement.GetProperty("id").GetGuid();
            Assert.Null(dataElement.GetProperty("description").GetString());
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("warnings").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("requestId").GetString()));
            Assert.NotNull(created.Headers.Location);
            Assert.EndsWith($"/api/workspaces/{createdId:D}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        using (var replay = await SendWorkspaceCreateAsync(
                   app,
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "wpc01-http-create",
                   "HTTP Created Workspace"))
        using (var document = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(createdId, document.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        }

        using (var mismatch = await SendWorkspaceCreateAsync(
                   app,
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "wpc01-http-create",
                   "Different request"))
        using (var document = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
            Assert.Equal(
                "IdempotencyConflict",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(
                "header.Idempotency-Key",
                document.RootElement.GetProperty("error").GetProperty("target").GetString());
            AssertCompleteErrorEnvelope(document.RootElement, 409, "IdempotencyConflict", "header.Idempotency-Key");
        }

        Assert.Equal(
            1,
            await app.CountWorkspacesAsync(data.TenantA.Id, data.TenantA.Slug, "HTTP Created Workspace"));
    }

    [Fact]
    [Trait("Scope", "Issue409")]
    public async Task ProjectCreateOptionsFailClosedAfterMembershipOrWorkspaceDeactivation()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var path = $"/api/workspaces/{data.WorkspaceA.Id:D}/projects/create-options";

        await app.SetWorkspaceMembershipStatusAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.TenantAOwner.Id,
            MembershipStatus.Suspended);
        using (var revoked = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, path))
        using (var document = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
            AssertCompleteErrorEnvelope(
                document.RootElement,
                404,
                "NotFound",
                null,
                expectedRedactionApplied: true);
        }

        await app.SetWorkspaceMembershipStatusAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.TenantAOwner.Id,
            MembershipStatus.Active);
        await app.SetWorkspaceAvailabilityAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            WorkspaceStatus.Archived,
            softDeleted: false);
        using (var archived = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, path))
        using (var document = JsonDocument.Parse(await archived.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.Conflict, archived.StatusCode);
            AssertCompleteErrorEnvelope(
                document.RootElement,
                409,
                "InvalidStateTransition",
                null,
                expectedRedactionApplied: true);
        }
    }

    [Fact]
    [Trait("Scope", "Issue410")]
    public async Task CanonicalTaskCreateRoutesResolveThroughTheInProcessHostAndPreserveSafeTenantBoundaries()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var optionsPath = $"/api/projects/{data.ProjectA.Id}/tasks/create-options";
        var createPath = $"/api/projects/{data.ProjectA.Id}/tasks/create";

        Assert.Contains("GET api/projects/{projectId:guid}/tasks/create-options", app.GetHttpRoutes());
        Assert.Contains("POST api/projects/{projectId:guid}/tasks/create", app.GetHttpRoutes());

        using (var options = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, optionsPath))
        using (var document = JsonDocument.Parse(await options.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
            var payload = document.RootElement.GetProperty("data");
            Assert.Equal(data.ProjectA.Id, payload.GetProperty("projectId").GetGuid());
            Assert.Equal(data.WorkspaceA.Id, payload.GetProperty("workspaceId").GetGuid());
            Assert.True(payload.GetProperty("canCreateTask").GetBoolean());
            Assert.False(payload.GetProperty("canManageProject").GetBoolean());
            Assert.Empty(payload.GetProperty("assignees").EnumerateArray());
            Assert.False(payload.GetProperty("projectScope").GetProperty("canSetTaskOverride").GetBoolean());
        }

        using (var missingKey = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   createPath,
                   HttpMethod.Post,
                   JsonContent("""{"title":"Task through host","sourceScopeMode":"Inherit"}""")))
        using (var document = JsonDocument.Parse(await missingKey.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
            AssertCompleteErrorEnvelope(document.RootElement, 400, "MissingIdempotencyKey", "header.Idempotency-Key");
        }

        using var crossTenant = await app.SendAsync(data.TenantBOwner, data.TenantB.Slug, optionsPath);
        var crossTenantBody = await crossTenant.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.DoesNotContain(data.ProjectA.Name, crossTenantBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantA.Slug, crossTenantBody, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task WorkspaceCreateFailsClosedThroughCanonicalEnvelopeWhenGeneralIsUnavailable()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync(workspaceInitializationAvailable: false);
        var data = app.Data;
        var before = await app.GetWorkspaceCreateCountsAsync(data.TenantA.Id, data.TenantA.Slug);

        using (var capability = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "/api/workspaces/capabilities"))
        using (var document = JsonDocument.Parse(await capability.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
            Assert.False(document.RootElement.GetProperty("data").GetProperty("canCreate").GetBoolean());
        }

        using (var failure = await SendWorkspaceCreateAsync(
                   app,
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "wpc01-general-unavailable",
                   "Must not persist"))
        using (var document = JsonDocument.Parse(await failure.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
            var root = document.RootElement;
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("requestId").GetString()));
            Assert.Equal("DependencyUnavailable", root.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("error").GetProperty("target").ValueKind);
            Assert.Equal(0, root.GetProperty("error").GetProperty("details").GetArrayLength());
            Assert.False(root.GetProperty("error").GetProperty("redactionApplied").GetBoolean());
            AssertCompleteErrorEnvelope(root, 503, "DependencyUnavailable", null);
        }

        Assert.Equal(before, await app.GetWorkspaceCreateCountsAsync(data.TenantA.Id, data.TenantA.Slug));
        Assert.Equal(0, await app.CountWorkspacesAsync(data.TenantA.Id, data.TenantA.Slug, "Must not persist"));
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task WorkspacePreControllerFailuresUseCompleteCanonicalEnvelopes()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using (var unauthenticatedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/workspaces/capabilities"))
        {
            unauthenticatedRequest.Headers.TryAddWithoutValidation("X-Tenant-Slug", data.TenantA.Slug);
            using var response = await app.Client.SendAsync(unauthenticatedRequest);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            AssertCompleteErrorEnvelope(document.RootElement, 401, "AuthenticationRequired", null);
        }

        using (var malformedBody = new StringContent("{", Encoding.UTF8, "application/json"))
        using (var response = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "/api/workspaces",
                   HttpMethod.Post,
                   malformedBody))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertCompleteErrorEnvelope(document.RootElement, 400, "MalformedJson", "body");
        }

        using (var invalidFieldTypeBody = new StringContent("""{"name":123}""", Encoding.UTF8, "application/json"))
        using (var response = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "/api/workspaces",
                   HttpMethod.Post,
                   invalidFieldTypeBody))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertCompleteErrorEnvelope(document.RootElement, 400, "ValidationFailed", "body");
        }

        using (var invalidRootTypeBody = new StringContent("123", Encoding.UTF8, "application/json"))
        using (var response = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "/api/workspaces",
                   HttpMethod.Post,
                   invalidRootTypeBody))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertCompleteErrorEnvelope(document.RootElement, 400, "ValidationFailed", "body");
        }

        using (var unsupportedBody = new StringContent("name=unsupported", Encoding.UTF8, "text/plain"))
        using (var response = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "/api/workspaces",
                   HttpMethod.Post,
                   unsupportedBody))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            AssertCompleteErrorEnvelope(
                document.RootElement,
                415,
                "UnsupportedMediaType",
                "header.Content-Type");
        }
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task WorkspaceCreateCsrfFailureUsesCompleteCanonicalEnvelope()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync(enableCsrfProtection: true);
        var data = app.Data;

        using var response = await SendWorkspaceCreateAsync(
            app,
            data.TenantAOwner,
            data.TenantA.Slug,
            "wpc01-csrf-rejected",
            "CSRF rejected");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertCompleteErrorEnvelope(document.RootElement, 403, "CsrfRejected", null);
    }

    [Theory]
    [InlineData(ProjectStatus.Planning, ProjectStatus.Review)]
    [InlineData(ProjectStatus.Active, ProjectStatus.Planning)]
    [InlineData(ProjectStatus.Completed, ProjectStatus.Review)]
    [InlineData(ProjectStatus.Suspended, ProjectStatus.Planning)]
    [Trait("Scope", "WPC01")]
    public async Task InvalidProjectLifecycleTransitionsUseCanonicalHttp409Envelope(
        ProjectStatus previousStatus,
        ProjectStatus nextStatus)
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var graph = await app.AddPlanningProjectGraphAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.GroupA.Id,
            data.TenantAOwner.Id,
            data.TenantAAdmin.Id,
            [],
            previousStatus);

        using var response = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            $"/api/projects/{graph.Project.Id:D}",
            HttpMethod.Patch,
            JsonContent($$"""
                {"title":"Must not persist","description":"Must not persist","status":{{(int)nextStatus}},"startDate":"2026-02-01","endDate":"2026-11-30"}
                """));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertCompleteErrorEnvelope(
            document.RootElement,
            StatusCodes.Status409Conflict,
            "InvalidStateTransition",
            "body.status");
        var lifecycle = await app.GetProjectLifecycleAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            graph.Project.Id);
        Assert.Equal(previousStatus, lifecycle.Status);
        Assert.Null(lifecycle.DeletedAt);
    }

    [Fact]
    [Trait("Scope", "Issue367")]
    public async Task SearchModelBindingFailuresUseFixedNonReflectiveEnvelope()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var invalidRequests = new[]
        {
            ("/api/search?type=Message&authorUserId=invalid-author-secret-marker", "invalid-author-secret-marker"),
            ("/api/search?type=Message&messageRead=invalid-read-secret-marker", "invalid-read-secret-marker"),
            ("/api/search?type=Message&toDateExclusive=invalid-date-secret-marker", "invalid-date-secret-marker"),
            ("/api/search/message-authors?selectedUserId=invalid-selected-secret-marker", "invalid-selected-secret-marker")
        };

        foreach (var (path, marker) in invalidRequests)
        {
            using var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, path);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
            AssertCompleteErrorEnvelope(
                document.RootElement,
                StatusCodes.Status400BadRequest,
                "SearchRequestInvalid",
                "query");
        }
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task PlanningProjectAndSubresourcesAreNotDisclosedBeyondProjectMembership()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var graph = await app.AddPlanningProjectGraphAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.GroupA.Id,
            data.TenantAOwner.Id,
            data.TenantAAdmin.Id,
            [data.TenantAMember.Id, data.TenantAAdmin.Id, data.PlatformAdmin.Id]);

        await AssertDraftGraphHiddenAsync(app, data, graph);

        using (var suspend = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   $"/api/projects/{graph.Project.Id:D}",
                   HttpMethod.Patch,
                   JsonContent("""{"status":4}""")))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        }
        await AssertDraftGraphHiddenAsync(app, data, graph);

        using (var archive = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   $"/api/projects/{graph.Project.Id:D}/archive",
                   HttpMethod.Post))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        }
        using (var archivedList = await app.SendAsync(
                   data.TenantAAdmin,
                   data.TenantA.Slug,
                   "/api/projects?archived=true"))
        {
            Assert.DoesNotContain(graph.Project.Name, await archivedList.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        using (var deniedRestore = await app.SendAsync(
                   data.TenantAAdmin,
                   data.TenantA.Slug,
                   $"/api/projects/{graph.Project.Id:D}/restore",
                   HttpMethod.Post))
        {
            Assert.Equal(HttpStatusCode.BadRequest, deniedRestore.StatusCode);
        }
        using (var ownerRestore = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   $"/api/projects/{graph.Project.Id:D}/restore",
                   HttpMethod.Post))
        using (var restoreDocument = JsonDocument.Parse(await ownerRestore.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.Conflict, ownerRestore.StatusCode);
            AssertCompleteErrorEnvelope(
                restoreDocument.RootElement,
                409,
                "InvalidStateTransition",
                "project");
        }

        var lifecycle = await app.GetProjectLifecycleAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            graph.Project.Id);
        Assert.Equal(ProjectStatus.Archived, lifecycle.Status);
        Assert.Null(lifecycle.DeletedAt);
    }

    private static async Task AssertDraftGraphHiddenAsync(
        HttpTenantIsolationTestApp app,
        TenantIsolationTestData data,
        PlanningProjectGraph graph)
    {
        foreach (var deniedActor in new[] { data.TenantAMember, data.TenantAAdmin, data.PlatformAdmin })
        {
            using var detail = await app.SendAsync(
                deniedActor,
                data.TenantA.Slug,
                $"/api/projects/{graph.Project.Id:D}");
            var detailBody = await detail.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
            Assert.DoesNotContain(graph.Project.Name, detailBody, StringComparison.Ordinal);
            using (var detailDocument = JsonDocument.Parse(detailBody))
            {
                AssertCompleteErrorEnvelope(detailDocument.RootElement, 404, "NotFound", null, expectedRedactionApplied: true);
            }

            Assert.DoesNotContain(
                graph.Project.Id,
                await SearchIdsAsync(app, deniedActor, data.TenantA.Slug, "Project", graph.Project.Id, data.WorkspaceA.Id));
            Assert.DoesNotContain(
                graph.Task.Id,
                await SearchIdsAsync(app, deniedActor, data.TenantA.Slug, "Task", graph.Project.Id, data.WorkspaceA.Id));
            Assert.DoesNotContain(
                graph.Artifact.Id,
                await SearchIdsAsync(app, deniedActor, data.TenantA.Slug, "Artifact", graph.Project.Id, data.WorkspaceA.Id));
            Assert.DoesNotContain(
                graph.ActivityLog.Id,
                await SearchIdsAsync(app, deniedActor, data.TenantA.Slug, "ActivityLog", graph.Project.Id, data.WorkspaceA.Id));
            Assert.DoesNotContain(
                graph.Comment.Id,
                await SearchIdsAsync(app, deniedActor, data.TenantA.Slug, "Comment", graph.Project.Id, data.WorkspaceA.Id));

            using (var conversationList = await app.SendAsync(
                       deniedActor,
                       data.TenantA.Slug,
                       "/api/conversations"))
            {
                var body = await conversationList.Content.ReadAsStringAsync();
                Assert.DoesNotContain(graph.Conversation.Title!, body, StringComparison.Ordinal);
                Assert.DoesNotContain(graph.Message.Body, body, StringComparison.Ordinal);
            }
            using (var messages = await app.SendAsync(
                       deniedActor,
                       data.TenantA.Slug,
                       $"/api/conversations/{graph.Conversation.Id:D}/messages"))
            {
                var body = await messages.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.BadRequest, messages.StatusCode);
                Assert.DoesNotContain(graph.Message.Body, body, StringComparison.Ordinal);
            }
            Assert.DoesNotContain(
                graph.Message.Id,
                await SearchIdsAsync(app, deniedActor, data.TenantA.Slug, "Message", graph.Project.Id, data.WorkspaceA.Id));

            using var createChannel = await app.SendAsync(
                deniedActor,
                data.TenantA.Slug,
                "/api/conversations",
                HttpMethod.Post,
                JsonContent($$"""
                    {"type":"ProjectChannel","workspaceId":"{{data.WorkspaceA.Id:D}}","projectId":"{{graph.Project.Id:D}}","title":"unauthorized draft channel","memberUserIds":[]}
                    """));
            Assert.Equal(HttpStatusCode.BadRequest, createChannel.StatusCode);
        }

        using (var myTasks = await app.SendAsync(
                   data.TenantAAdmin,
                   data.TenantA.Slug,
                   $"/api/me/tasks?scope=CurrentWorkspace&workspaceId={data.WorkspaceA.Id:D}&projectId={graph.Project.Id:D}&view=Assigned"))
        {
            var body = await myTasks.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, myTasks.StatusCode);
            Assert.DoesNotContain(graph.Task.Title, body, StringComparison.Ordinal);
        }

        using (var detail = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   $"/api/projects/{graph.Project.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        }
        Assert.Contains(
            graph.Project.Id,
            await SearchIdsAsync(app, data.TenantAOwner, data.TenantA.Slug, "Project", graph.Project.Id, data.WorkspaceA.Id));
        Assert.Contains(
            graph.Task.Id,
            await SearchIdsAsync(app, data.TenantAOwner, data.TenantA.Slug, "Task", graph.Project.Id, data.WorkspaceA.Id));
        Assert.Contains(
            graph.Artifact.Id,
            await SearchIdsAsync(app, data.TenantAOwner, data.TenantA.Slug, "Artifact", graph.Project.Id, data.WorkspaceA.Id));
        Assert.Contains(
            graph.ActivityLog.Id,
            await SearchIdsAsync(app, data.TenantAOwner, data.TenantA.Slug, "ActivityLog", graph.Project.Id, data.WorkspaceA.Id));
        Assert.Contains(
            graph.Comment.Id,
            await SearchIdsAsync(app, data.TenantAOwner, data.TenantA.Slug, "Comment", graph.Project.Id, data.WorkspaceA.Id));
        Assert.Contains(
            graph.Message.Id,
            await SearchIdsAsync(app, data.TenantAOwner, data.TenantA.Slug, "Message", graph.Project.Id, data.WorkspaceA.Id));
        using (var conversationList = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   "/api/conversations"))
        {
            var body = await conversationList.Content.ReadAsStringAsync();
            Assert.Contains(graph.Conversation.Title!, body, StringComparison.Ordinal);
            Assert.Contains(graph.Message.Body, body, StringComparison.Ordinal);
        }
    }

    private static async Task<IReadOnlySet<Guid>> SearchIdsAsync(
        HttpTenantIsolationTestApp app,
        User actor,
        string tenantSlug,
        string type,
        Guid projectId,
        Guid workspaceId)
    {
        var scope = type is "Comment" or "Message"
            ? $"workspaceId={workspaceId:D}"
            : $"projectId={projectId:D}";
        using var response = await app.SendAsync(
            actor,
            tenantSlug,
            $"/api/search?type={type}&{scope}&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToHashSet();
    }

    private static void AssertCompleteErrorEnvelope(
        JsonElement root,
        int expectedStatus,
        string expectedCode,
        string? expectedTarget,
        bool expectedRedactionApplied = false)
    {
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("requestId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.Equal(expectedStatus, root.GetProperty("status").GetInt32());
        var error = root.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        if (expectedTarget is null)
            Assert.Equal(JsonValueKind.Null, error.GetProperty("target").ValueKind);
        else
            Assert.Equal(expectedTarget, error.GetProperty("target").GetString());
        Assert.Equal(0, error.GetProperty("details").GetArrayLength());
        Assert.Equal(expectedRedactionApplied, error.GetProperty("redactionApplied").GetBoolean());
    }

    [Fact]
    [Trait("Scope", "Issue409")]
    public async Task AuthenticatedHttpRequestsStayTenantScopedAcrossCoreWorkflows()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, "/api/tenants/current", data.TenantA.Slug, data.TenantB.Slug);
        await AssertWorkspaceProjectionUnavailableOnNonPostgreSqlAsync(
            app,
            data.CrossTenantUser,
            data.TenantA.Slug,
            "WorkspaceA",
            "WorkspaceB");
        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/workspaces/{data.WorkspaceA.Id}", "WorkspaceA", "WorkspaceB");
        await AssertStatusAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/workspaces/{data.WorkspaceB.Id}", HttpStatusCode.NotFound);

        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/workspaces/{data.WorkspaceA.Id}/groups", "GroupA", "GroupB");
        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/groups/{data.GroupA.Id}", "GroupA", "GroupB");
        await AssertBadRequestAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/groups/{data.GroupB.Id}");

        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, "/api/projects?archived=false", "ProjectA", "ProjectB");
        using (var filteredProjects = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/projects?workspaceId={data.WorkspaceA.Id:D}"))
        using (var document = JsonDocument.Parse(await filteredProjects.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, filteredProjects.StatusCode);
            var project = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(data.ProjectA.Id, project.GetProperty("id").GetGuid());
            Assert.False(project.GetProperty("uiPermissions").GetProperty("canActivate").GetBoolean());
        }
        using (var crossWorkspaceFilter = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/projects?workspaceId={data.WorkspaceB.Id:D}"))
        using (var document = JsonDocument.Parse(await crossWorkspaceFilter.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, crossWorkspaceFilter.StatusCode);
            Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        }
        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/projects/{data.ProjectA.Id}", "ProjectA", "ProjectB");
        await AssertStatusAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/projects/{data.ProjectB.Id}", HttpStatusCode.NotFound);

        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/projects/{data.ProjectA.Id}/tasks", "TaskA", "TaskB");
        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id}", "TaskA", "TaskB");
        // Task commands use the canonical safe-not-found envelope so guessed cross-tenant IDs
        // do not reveal a resource outside the active tenant.
        await AssertStatusAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/tasks/{data.TaskB.Id}", HttpStatusCode.NotFound);

        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, "/api/conversations", "ConversationA", "ConversationB");
        await AssertOkContainsOnlyAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}", "ConversationA", "ConversationB");
        await AssertBadRequestAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationB.Id}");

        using (var fileMetadata = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/files/{data.FileA.Id}"))
        {
            var fileBody = await fileMetadata.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, fileMetadata.StatusCode);
            Assert.Contains("[redacted:file]", fileBody, StringComparison.Ordinal);
            Assert.DoesNotContain(data.FileA.OriginalFileName, fileBody, StringComparison.Ordinal);
            Assert.DoesNotContain(data.FileB.OriginalFileName, fileBody, StringComparison.Ordinal);
        }
        await AssertStatusAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/files/{data.FileA.Id}/download", HttpStatusCode.OK);
        await AssertBadRequestAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/files/{data.FileB.Id}");
        await AssertBadRequestAsync(app, data.CrossTenantUser, data.TenantA.Slug, $"/api/files/{data.FileB.Id}/download");
    }

    [Fact]
    public async Task TaskListHttpContractUsesCanonicalStringStateAndBooleanArtifactSignal()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var artifactName = $"private-artifact-{Guid.NewGuid():N}";
        await app.BlockTaskAndAddArtifactAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.TaskA.Id,
            data.TenantAOwner.Id,
            artifactName);

        using var response = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/projects/{data.ProjectA.Id}/tasks");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(artifactName, body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var task = Assert.Single(
            document.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == data.TaskA.Id);
        Assert.Equal(JsonValueKind.String, task.GetProperty("stageCategory").ValueKind);
        Assert.Contains(
            task.GetProperty("stageCategory").GetString(),
            new[] { "Backlog", "Todo", "InProgress", "Review", "Done", "Cancelled" });
        Assert.Contains(
            task.GetProperty("workflowStageId").ValueKind,
            new[] { JsonValueKind.Null, JsonValueKind.String });
        Assert.False(string.IsNullOrWhiteSpace(task.GetProperty("workflowStageName").GetString()));
        Assert.True(task.GetProperty("isBlocked").GetBoolean());
        Assert.True(task.GetProperty("hasArtifact").GetBoolean());
        Assert.Equal(JsonValueKind.String, task.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, task.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task FileMetadataAndDeniedResponsesDoNotExposeStorageIdentifiers()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        var allowedMetadata = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/files/{data.FileA.Id}");
        var allowedBody = await allowedMetadata.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, allowedMetadata.StatusCode);
        Assert.Contains("[redacted:file]", allowedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.FileA.OriginalFileName, allowedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("storageKey", allowedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storedFileName", allowedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.FileA.StorageKey, allowedBody, StringComparison.Ordinal);

        var deniedMetadata = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/files/{data.FileB.Id}");
        var deniedMetadataBody = await deniedMetadata.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedMetadata.StatusCode);
        Assert.DoesNotContain(data.FileB.OriginalFileName, deniedMetadataBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.FileB.StorageKey, deniedMetadataBody, StringComparison.Ordinal);

        var deniedDownload = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/files/{data.FileB.Id}/download");
        var deniedDownloadBody = await deniedDownload.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedDownload.StatusCode);
        Assert.DoesNotContain(data.FileB.OriginalFileName, deniedDownloadBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.FileB.StorageKey, deniedDownloadBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileDownloadResponsesUsePrivateCacheHeaders()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        var response = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/files/{data.FileA.Id}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(response.Headers.Pragma, value => string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UploadSanitizesOriginalFileNameAndDoesNotReturnStorageIdentifiers()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var content = new MultipartFormDataContent
        {
            { new StringContent(AttachmentOwnerType.TaskItem.ToString()), "OwnerType" },
            { new StringContent(data.TaskB.Id.ToString("D")), "OwnerId" }
        };
        var file = new ByteArrayContent("hello"u8.ToArray());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(file, "File", @"..\secret.txt");

        var response = await app.SendAsync(data.TenantBMember, data.TenantB.Slug, "/api/files", HttpMethod.Post, content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("[redacted:file]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", body, StringComparison.Ordinal);
        Assert.DoesNotContain("..", body, StringComparison.Ordinal);
        Assert.DoesNotContain("storageKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storedFileName", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"tenants/{data.TenantB.Id:D}", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scope", "Issue345")]
    public async Task WorkspaceFileDeleteCapabilityAndDirectMutationRemainOwnerScoped()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var privateName = $"owner-only-{Guid.NewGuid():N}.txt";
        Guid fileObjectId;

        using (var upload = new MultipartFormDataContent
               {
                   { new StringContent(AttachmentOwnerType.Workspace.ToString()), "OwnerType" },
                   { new StringContent(data.WorkspaceB.Id.ToString("D")), "OwnerId" }
               })
        {
            var file = new ByteArrayContent("file"u8.ToArray());
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            upload.Add(file, "File", privateName);
            using var response = await app.SendAsync(
                data.TenantBOwner,
                data.TenantB.Slug,
                "/api/files",
                HttpMethod.Post,
                upload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            fileObjectId = document.RootElement.GetProperty("fileObjectId").GetGuid();
        }

        using (var ownerList = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files?workspaceId={data.WorkspaceB.Id:D}&page=1&pageSize=20"))
        using (var document = JsonDocument.Parse(await ownerList.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
            var item = Assert.Single(
                document.RootElement.GetProperty("items").EnumerateArray(),
                candidate => candidate.GetProperty("fileObjectId").GetGuid() == fileObjectId);
            Assert.True(item.GetProperty("canDelete").GetBoolean());
            Assert.Equal("[redacted:file]", item.GetProperty("originalFileName").GetString());
            Assert.DoesNotContain(privateName, document.RootElement.GetRawText(), StringComparison.Ordinal);
        }

        // New direct Workspace uploads are intentionally Private. This
        // deletion-capability regression needs a visible shared file, so make
        // the owner-authorized policy transition explicit rather than relying
        // on the old implicit Workspace default.
        using (var shareWithWorkspace = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}/sharing",
                   HttpMethod.Put,
                   System.Net.Http.Json.JsonContent.Create(new { shareWithWorkspace = true, expectedSharingVersion = 1 })))
        using (var document = JsonDocument.Parse(await shareWithWorkspace.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, shareWithWorkspace.StatusCode);
            Assert.Equal("Workspace", document.RootElement.GetProperty("accessState").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("sharingVersion").GetInt64());
        }

        using (var otherContributorList = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantB.Slug,
                   $"/api/files?workspaceId={data.WorkspaceB.Id:D}&page=1&pageSize=20"))
        using (var document = JsonDocument.Parse(await otherContributorList.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, otherContributorList.StatusCode);
            var item = Assert.Single(
                document.RootElement.GetProperty("items").EnumerateArray(),
                candidate => candidate.GetProperty("fileObjectId").GetGuid() == fileObjectId);
            Assert.False(item.GetProperty("canDelete").GetBoolean());
            Assert.Equal("[redacted:file]", item.GetProperty("originalFileName").GetString());
            Assert.DoesNotContain(privateName, document.RootElement.GetRawText(), StringComparison.Ordinal);
        }

        using (var denied = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}",
                   HttpMethod.Delete))
        {
            var body = await denied.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.DoesNotContain(privateName, body, StringComparison.Ordinal);
        }

        using (var crossWorkspace = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/{data.FileA.Id:D}",
                   HttpMethod.Delete))
        {
            var body = await crossWorkspace.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, crossWorkspace.StatusCode);
            Assert.DoesNotContain(data.FileA.OriginalFileName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(data.FileA.StorageKey, body, StringComparison.Ordinal);
        }

        using (var stillAvailable = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, stillAvailable.StatusCode);
        }

        using (var deleted = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}",
                   HttpMethod.Delete))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        using (var deletedRead = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, deletedRead.StatusCode);
        }
    }

    [Fact]
    [Trait("Scope", "Issue348")]
    public async Task FileSearchSelectionSnapshotIsActorBoundConsumedOnceAndReauthorizesBatchDeletion()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        Guid fileObjectId;

        using (var upload = new MultipartFormDataContent
               {
                   { new StringContent(AttachmentOwnerType.Workspace.ToString()), "OwnerType" },
                   { new StringContent(data.WorkspaceB.Id.ToString("D")), "OwnerId" },
               })
        {
            var file = new ByteArrayContent("batch-selection"u8.ToArray());
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            upload.Add(file, "File", "batch-selection.txt");

            using var response = await app.SendAsync(
                data.TenantBOwner,
                data.TenantB.Slug,
                "/api/files",
                HttpMethod.Post,
                upload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            fileObjectId = document.RootElement.GetProperty("fileObjectId").GetGuid();
        }

        Guid selectionSnapshotId;
        using (var capture = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/selection-snapshots?workspaceId={data.WorkspaceB.Id:D}&onlyMyUploads=true",
                   HttpMethod.Post))
        {
            Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
            using var document = JsonDocument.Parse(await capture.Content.ReadAsStringAsync());
            Assert.Equal("Captured", document.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("selectedCount").GetInt32());
            selectionSnapshotId = document.RootElement.GetProperty("selectionSnapshotId").GetGuid();
        }

        using (var deleted = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/selection-snapshots/{selectionSnapshotId:D}/delete",
                   HttpMethod.Post))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            using var document = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
            Assert.Equal(1, document.RootElement.GetProperty("attemptedCount").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("succeededCount").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("failedCount").GetInt32());
        }

        using (var secondAttempt = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/selection-snapshots/{selectionSnapshotId:D}/delete",
                   HttpMethod.Post))
        {
            Assert.Equal(HttpStatusCode.BadRequest, secondAttempt.StatusCode);
        }

        using var deletedRead = await app.SendAsync(
            data.TenantBOwner,
            data.TenantB.Slug,
            $"/api/files/{fileObjectId:D}");
        Assert.Equal(HttpStatusCode.BadRequest, deletedRead.StatusCode);
    }

    [Fact]
    [Trait("Scope", "Issue360")]
    public async Task PrivateWorkspaceSharingReauthorizesApiReadsAndDoesNotLeakProtectedSharingMetadata()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var privateName = $"sharing-private-{Guid.NewGuid():N}.txt";
        Guid fileObjectId;
        Guid grantId;

        using (var upload = new MultipartFormDataContent
               {
                   { new StringContent(AttachmentOwnerType.Workspace.ToString()), "OwnerType" },
                   { new StringContent(data.WorkspaceB.Id.ToString("D")), "OwnerId" },
               })
        {
            var file = new ByteArrayContent("sharing boundary"u8.ToArray());
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            upload.Add(file, "File", privateName);
            using var response = await app.SendAsync(
                data.TenantBOwner,
                data.TenantB.Slug,
                "/api/files",
                HttpMethod.Post,
                upload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            fileObjectId = document.RootElement.GetProperty("fileObjectId").GetGuid();
        }

        using (var list = await app.SendAsync(
                   data.TenantBMember,
                   data.TenantB.Slug,
                   $"/api/files?workspaceId={data.WorkspaceB.Id:D}&page=1&pageSize=20"))
        using (var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.DoesNotContain(
                document.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("fileObjectId").GetGuid() == fileObjectId);
            Assert.DoesNotContain(privateName, document.RootElement.GetRawText(), StringComparison.Ordinal);
        }

        using (var deniedRead = await app.SendAsync(
                   data.TenantBMember,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}/sharing"))
        {
            var body = await deniedRead.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, deniedRead.StatusCode);
            Assert.DoesNotContain(privateName, body, StringComparison.Ordinal);
            Assert.DoesNotContain("externalRecipientCount", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("recipients", body, StringComparison.OrdinalIgnoreCase);
        }

        using (var deniedMutation = await app.SendAsync(
                   data.TenantBMember,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}/sharing",
                   HttpMethod.Put,
                   System.Net.Http.Json.JsonContent.Create(new { shareWithWorkspace = true, expectedSharingVersion = 1 })))
        {
            var body = await deniedMutation.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, deniedMutation.StatusCode);
            Assert.DoesNotContain(privateName, body, StringComparison.Ordinal);
            Assert.DoesNotContain("recipients", body, StringComparison.OrdinalIgnoreCase);
        }

        using (var ownerProjection = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}/sharing"))
        using (var document = JsonDocument.Parse(await ownerProjection.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, ownerProjection.StatusCode);
            Assert.Equal("Private", document.RootElement.GetProperty("accessState").GetString());
            Assert.True(document.RootElement.GetProperty("canManageSharing").GetBoolean());
            Assert.True(document.RootElement.GetProperty("canInspectSharing").GetBoolean());
        }

        using (var grant = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}/sharing/recipients",
                   HttpMethod.Post,
                   System.Net.Http.Json.JsonContent.Create(new { recipientUserId = data.TenantBMember.Id, expectedSharingVersion = 1 })))
        using (var document = JsonDocument.Parse(await grant.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
            Assert.Equal("Private", document.RootElement.GetProperty("accessState").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("sharingVersion").GetInt64());
            grantId = Assert.Single(document.RootElement.GetProperty("recipients").EnumerateArray())
                .GetProperty("grantId").GetGuid();
        }

        using (var grantedList = await app.SendAsync(
                   data.TenantBMember,
                   data.TenantB.Slug,
                   $"/api/files?workspaceId={data.WorkspaceB.Id:D}&page=1&pageSize=20"))
        using (var document = JsonDocument.Parse(await grantedList.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, grantedList.StatusCode);
            var item = Assert.Single(
                document.RootElement.GetProperty("items").EnumerateArray(),
                value => value.GetProperty("fileObjectId").GetGuid() == fileObjectId);
            Assert.Equal("Private", item.GetProperty("accessState").GetString());
            Assert.False(item.GetProperty("canManageSharing").GetBoolean());
            Assert.True(
                !item.TryGetProperty("externalRecipientCount", out var count) ||
                count.ValueKind == JsonValueKind.Null);
        }

        using (var readerProjection = await app.SendAsync(
                   data.TenantBMember,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}/sharing"))
        using (var document = JsonDocument.Parse(await readerProjection.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, readerProjection.StatusCode);
            Assert.Equal("Private", document.RootElement.GetProperty("accessState").GetString());
            Assert.False(document.RootElement.GetProperty("canManageSharing").GetBoolean());
            Assert.False(document.RootElement.GetProperty("canInspectSharing").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("recipients").EnumerateArray());
            Assert.Empty(document.RootElement.GetProperty("availableRecipients").EnumerateArray());
            Assert.True(
                !document.RootElement.TryGetProperty("externalRecipientCount", out var count) ||
                count.ValueKind == JsonValueKind.Null);
        }

        using (var revoke = await app.SendAsync(
                   data.TenantBOwner,
                   data.TenantB.Slug,
                   $"/api/files/{fileObjectId:D}/sharing/recipients/{grantId:D}?expectedSharingVersion=2",
                   HttpMethod.Delete))
        using (var document = JsonDocument.Parse(await revoke.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            Assert.Equal("Private", document.RootElement.GetProperty("accessState").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("sharingVersion").GetInt64());
        }

        using var postRevokeRead = await app.SendAsync(
            data.TenantBMember,
            data.TenantB.Slug,
            $"/api/files/{fileObjectId:D}/sharing");
        var postRevokeBody = await postRevokeRead.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, postRevokeRead.StatusCode);
        Assert.DoesNotContain(privateName, postRevokeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("recipients", postRevokeBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticatedHttpNotificationsStayUserAndTenantScoped()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        await AssertOkContainsOnlyAsync(app, data.TenantAMember, data.TenantA.Slug, "/api/notifications?page=1&pageSize=20", "TenantA notification", "TenantB notification");
        await AssertOkContainsOnlyAsync(app, data.TenantBMember, data.TenantB.Slug, "/api/notifications?page=1&pageSize=20", "TenantB notification", "TenantA notification");
        await AssertBadRequestAsync(app, data.TenantAMember, data.TenantA.Slug, $"/api/notifications/{data.NotificationB.Id}/read", HttpMethod.Patch);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task TaskNotificationPreferencesUseCanonicalRoutesAndAcceptExactQuarterHours()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var path = $"/api/me/workspaces/{data.WorkspaceA.Id:D}/task-notification-preferences";

        var routes = app.GetHttpRoutes();
        Assert.Contains("GET api/me/workspaces/{workspaceId:guid}/task-notification-preferences", routes);
        Assert.Contains("PATCH api/me/workspaces/{workspaceId:guid}/task-notification-preferences", routes);

        using (var initial = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path))
        using (var document = JsonDocument.Parse(await initial.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            Assert.Equal("\"1\"", initial.Headers.ETag?.Tag);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("deadlineDigestLocalTime").ValueKind);
            Assert.Equal("08:00", document.RootElement.GetProperty("effectiveDeadlineDigestLocalTime").GetString());
            Assert.Equal("UTC", document.RootElement.GetProperty("workspaceTimeZoneId").GetString());
            Assert.Equal(1L, document.RootElement.GetProperty("version").GetInt64());
        }

        var expectedVersion = 1L;
        foreach (var localTime in new[] { "00:00", "00:15", "23:45" })
        {
            using var content = JsonContent($$"""{"deadlineDigestLocalTime":"{{localTime}}","expectedVersion":{{expectedVersion}}}""");
            using var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, content);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            expectedVersion++;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal($"\"{expectedVersion}\"", response.Headers.ETag?.Tag);
            Assert.Equal(localTime, document.RootElement.GetProperty("deadlineDigestLocalTime").GetString());
            Assert.Equal(localTime, document.RootElement.GetProperty("effectiveDeadlineDigestLocalTime").GetString());
            Assert.Equal(expectedVersion, document.RootElement.GetProperty("version").GetInt64());
        }

        using (var inherit = JsonContent($$"""{"deadlineDigestLocalTime":null,"expectedVersion":{{expectedVersion}}}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, inherit))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("deadlineDigestLocalTime").ValueKind);
            Assert.Equal("08:00", document.RootElement.GetProperty("effectiveDeadlineDigestLocalTime").GetString());
            Assert.Equal(expectedVersion + 1, document.RootElement.GetProperty("version").GetInt64());
        }
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task TaskNotificationPreferencesRejectInvalidTimesAndProvideSafeConcurrencyRetryMetadata()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var path = $"/api/me/workspaces/{data.WorkspaceA.Id:D}/task-notification-preferences";

        foreach (var invalid in new[] { "00:01", "23:59", "24:00" })
        {
            using var content = JsonContent($$"""{"deadlineDigestLocalTime":"{{invalid}}","expectedVersion":1}""");
            using var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, content);
            await AssertTaskNotificationPreferenceErrorAsync(
                response,
                HttpStatusCode.BadRequest,
                "TASK_NOTIFICATION_PREFERENCE_INVALID_LOCAL_TIME");
        }

        using (var missingVersion = JsonContent("""{"deadlineDigestLocalTime":"00:00"}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, missingVersion))
        {
            await AssertTaskNotificationPreferenceErrorAsync(
                response,
                HttpStatusCode.Conflict,
                "TASK_NOTIFICATION_PREFERENCE_VERSION_CONFLICT",
                currentVersion: 1);
        }

        using (var winner = JsonContent("""{"deadlineDigestLocalTime":"00:15","expectedVersion":1}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, winner))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        }

        using (var stale = JsonContent("""{"deadlineDigestLocalTime":"23:45","expectedVersion":1}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, stale))
        {
            await AssertTaskNotificationPreferenceErrorAsync(
                response,
                HttpStatusCode.Conflict,
                "TASK_NOTIFICATION_PREFERENCE_VERSION_CONFLICT",
                currentVersion: 2);
        }

        using (var retry = JsonContent("""{"deadlineDigestLocalTime":"23:45","expectedVersion":2}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, retry))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("23:45", document.RootElement.GetProperty("deadlineDigestLocalTime").GetString());
            Assert.Equal(3L, document.RootElement.GetProperty("version").GetInt64());
        }
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task TaskNotificationPreferencesClassifyNumericVersionConflictsAndMalformedJsonSafelyWithoutMutation()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var path = $"/api/me/workspaces/{data.WorkspaceA.Id:D}/task-notification-preferences";

        foreach (var requestJson in new[]
                 {
                     """{"deadlineDigestLocalTime":"00:00"}""",
                     """{"deadlineDigestLocalTime":"00:00","expectedVersion":0}""",
                     """{"deadlineDigestLocalTime":"00:00","expectedVersion":-1}"""
                 })
        {
            using var content = JsonContent(requestJson);
            using var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, content);
            await AssertTaskNotificationPreferenceErrorAsync(
                response,
                HttpStatusCode.Conflict,
                "TASK_NOTIFICATION_PREFERENCE_VERSION_CONFLICT",
                currentVersion: 1);
            await AssertTaskNotificationPreferenceStateAsync(app, data, path, null, 1);
        }

        using (var winner = JsonContent("""{"deadlineDigestLocalTime":"00:00","expectedVersion":1}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, winner))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var stale = JsonContent("""{"deadlineDigestLocalTime":"23:45","expectedVersion":1}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, stale))
        {
            await AssertTaskNotificationPreferenceErrorAsync(
                response,
                HttpStatusCode.Conflict,
                "TASK_NOTIFICATION_PREFERENCE_VERSION_CONFLICT",
                currentVersion: 2);
            await AssertTaskNotificationPreferenceStateAsync(app, data, path, "00:00", 2);
        }

        using (var incompatibleType = JsonContent("""{"deadlineDigestLocalTime":"23:45","expectedVersion":"abc"}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path, HttpMethod.Patch, incompatibleType))
        {
            await AssertSafeModelBindingErrorAsync(response, HttpStatusCode.BadRequest);
            await AssertTaskNotificationPreferenceStateAsync(app, data, path, "00:00", 2);
        }
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task TaskNotificationPreferencesArePrivateTenantScopedAndFailClosedForRevokedMembership()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var workspaceAPath = $"/api/me/workspaces/{data.WorkspaceA.Id:D}/task-notification-preferences";
        var workspaceBPath = $"/api/me/workspaces/{data.WorkspaceB.Id:D}/task-notification-preferences";

        using (var update = JsonContent("""{"deadlineDigestLocalTime":"23:45","expectedVersion":1}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, workspaceAPath, HttpMethod.Patch, update))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var anotherMember = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, workspaceAPath))
        using (var document = JsonDocument.Parse(await anotherMember.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, anotherMember.StatusCode);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("deadlineDigestLocalTime").ValueKind);
            Assert.Equal(1L, document.RootElement.GetProperty("version").GetInt64());
        }

        using (var wrongWorkspace = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, workspaceBPath))
        {
            await AssertTaskNotificationPreferenceErrorAsync(
                wrongWorkspace,
                HttpStatusCode.NotFound,
                "TASK_NOTIFICATION_PREFERENCE_NOT_FOUND",
                redacted: true);
        }

        using (var wrongTenant = await app.SendAsync(data.TenantAMember, data.TenantB.Slug, workspaceBPath))
        {
            await AssertTaskNotificationPreferenceErrorAsync(
                wrongTenant,
                HttpStatusCode.NotFound,
                "TASK_NOTIFICATION_PREFERENCE_NOT_FOUND",
                redacted: true);
        }

        await app.SetWorkspaceAvailabilityAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            WorkspaceStatus.Archived,
            softDeleted: true);
        using (var inactiveWorkspace = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, workspaceAPath))
        {
            await AssertTaskNotificationPreferenceErrorAsync(
                inactiveWorkspace,
                HttpStatusCode.NotFound,
                "TASK_NOTIFICATION_PREFERENCE_NOT_FOUND",
                redacted: true);
        }
        await app.SetWorkspaceAvailabilityAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            WorkspaceStatus.Active,
            softDeleted: false);

        await app.SetWorkspaceMembershipStatusAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.TenantAMember.Id,
            MembershipStatus.Suspended);

        using (var revokedGet = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, workspaceAPath))
        {
            await AssertTaskNotificationPreferenceErrorAsync(
                revokedGet,
                HttpStatusCode.NotFound,
                "TASK_NOTIFICATION_PREFERENCE_NOT_FOUND",
                redacted: true);
        }

        using (var revokedPatch = JsonContent("""{"deadlineDigestLocalTime":"00:00","expectedVersion":2}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, workspaceAPath, HttpMethod.Patch, revokedPatch))
        {
            await AssertTaskNotificationPreferenceErrorAsync(
                response,
                HttpStatusCode.NotFound,
                "TASK_NOTIFICATION_PREFERENCE_NOT_FOUND",
                redacted: true);
        }
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task GeneralWorkspaceDtosDoNotDisclosePrivateTaskNotificationPreferences()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var members = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            $"/api/workspaces/{data.WorkspaceA.Id:D}/members");
        var membersBody = await members.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        Assert.DoesNotContain("taskDeadlineDigestLocalTime", membersBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskNotificationPreferenceVersion", membersBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(WorkspaceMemberResponse).GetProperties(),
            property => string.Equals(property.Name, "TaskDeadlineDigestLocalTime", StringComparison.Ordinal) ||
                        string.Equals(property.Name, "TaskNotificationPreferenceVersion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommunicationBodiesStayParticipantScopedAndDeniedResponsesAreGeneric()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        var allowedMessages = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages");
        var allowedBody = await allowedMessages.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, allowedMessages.StatusCode);
        Assert.Contains(data.MessageA.Body, allowedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.MessageB.Body, allowedBody, StringComparison.Ordinal);

        var deniedOutsider = await app.SendAsync(data.Outsider, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages");
        var deniedOutsiderBody = await deniedOutsider.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedOutsider.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedOutsiderBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantAMember.Email, deniedOutsiderBody, StringComparison.OrdinalIgnoreCase);

        var deniedCrossTenant = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationB.Id}/messages");
        var deniedCrossTenantBody = await deniedCrossTenant.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedCrossTenant.StatusCode);
        Assert.DoesNotContain(data.MessageB.Body, deniedCrossTenantBody, StringComparison.Ordinal);

        var auditLogs = await app.ListAuditLogsAsync(data.TenantA.Id, data.TenantA.Slug);
        var denialLogs = auditLogs.Where(log => log.Action == "ConversationAccessDenied").ToList();

        Assert.Contains(denialLogs, log => log.EntityId == data.ConversationA.Id);
        Assert.Contains(denialLogs, log => log.EntityId == data.ConversationB.Id);
        Assert.All(denialLogs, log =>
        {
            Assert.Equal("Conversation access denied.", log.Summary);
            Assert.DoesNotContain(data.MessageA.Body, log.MetadataJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(data.MessageB.Body, log.MetadataJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(data.TenantAMember.Email, log.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(data.FileA.StorageKey, log.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        });
    }

    [Fact]
    [Trait("Scope", "Issue362")]
    public async Task MessageThreadRoutesKeepRepliesOutOfMainTimelineAndPublishBoundedMetadata()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var clientRequestId = Guid.NewGuid();
        const string replyBody = "Issue 362 private reply";

        Assert.Contains("GET api/messages/{messageId:guid}/thread", app.GetHttpRoutes());
        Assert.Contains("POST api/messages/{messageId:guid}/thread/messages", app.GetHttpRoutes());

        using var createContent = JsonContent(JsonSerializer.Serialize(new { body = replyBody, clientRequestId }));
        using var createResponse = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/messages/{data.MessageA.Id:D}/thread/messages",
            HttpMethod.Post,
            createContent);
        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createdMessage = createDocument.RootElement.GetProperty("message");
        var replyId = createdMessage.GetProperty("id").GetGuid();
        Assert.Equal(data.MessageA.Id, createdMessage.GetProperty("threadRootMessageId").GetGuid());
        Assert.Equal(1, createDocument.RootElement.GetProperty("summary").GetProperty("replyCount").GetInt32());
        Assert.Contains(
            data.CrossTenantUser.DisplayName,
            createDocument.RootElement.GetProperty("summary").GetProperty("participantDisplayNames")
                .EnumerateArray().Select(value => value.GetString()));

        using var replayContent = JsonContent(JsonSerializer.Serialize(new { body = replyBody, clientRequestId }));
        using var replayResponse = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/messages/{data.MessageA.Id:D}/thread/messages",
            HttpMethod.Post,
            replayContent);
        using var replayDocument = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(replyId, replayDocument.RootElement.GetProperty("message").GetProperty("id").GetGuid());
        Assert.Equal(1, replayDocument.RootElement.GetProperty("summary").GetProperty("replyCount").GetInt32());

        using var threadResponse = await app.SendAsync(
            data.TenantAMember,
            data.TenantA.Slug,
            $"/api/messages/{data.MessageA.Id:D}/thread");
        using var threadDocument = JsonDocument.Parse(await threadResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, threadResponse.StatusCode);
        Assert.Equal(data.MessageA.Body, threadDocument.RootElement.GetProperty("rootMessage").GetProperty("body").GetString());
        Assert.Equal(replyBody, Assert.Single(threadDocument.RootElement.GetProperty("replies").EnumerateArray()).GetProperty("body").GetString());
        Assert.Equal(1, threadDocument.RootElement.GetProperty("summary").GetProperty("replyCount").GetInt32());
        Assert.False(threadDocument.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(100, threadDocument.RootElement.GetProperty("maximumReplies").GetInt32());

        using var timelineResponse = await app.SendAsync(
            data.TenantAMember,
            data.TenantA.Slug,
            $"/api/conversations/{data.ConversationA.Id:D}/messages");
        var timelineBody = await timelineResponse.Content.ReadAsStringAsync();
        using var timelineDocument = JsonDocument.Parse(timelineBody);
        Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
        Assert.Contains(data.MessageA.Body, timelineBody, StringComparison.Ordinal);
        Assert.DoesNotContain(replyBody, timelineBody, StringComparison.Ordinal);
        var rootItem = Assert.Single(timelineDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, rootItem.GetProperty("thread").GetProperty("replyCount").GetInt32());

        using var targetIsolationContent = JsonContent(JsonSerializer.Serialize(new
        {
            body = "must not change targets",
            clientRequestId
        }));
        using var targetIsolationResponse = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/conversations/{data.ConversationA.Id:D}/messages",
            HttpMethod.Post,
            targetIsolationContent);
        Assert.Equal(HttpStatusCode.BadRequest, targetIsolationResponse.StatusCode);

        var outbox = await app.ListOutboxEventsAsync(data.TenantA.Id, data.TenantA.Slug);
        var messageCreated = Assert.Single(outbox, item =>
            item.EventType == "Messaging.MessageCreated.v1" && item.AggregateId == replyId);
        Assert.Contains(data.MessageA.Id.ToString("D"), messageCreated.PayloadJson, StringComparison.OrdinalIgnoreCase);
        var threadChanged = Assert.Single(outbox, item =>
            item.EventType == "Messaging.ThreadChanged.v1" && item.AggregateId == data.MessageA.Id);
        Assert.Null(threadChanged.AggregateVersion);
        Assert.Contains("\"replyCount\":1", threadChanged.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"requiresRefetch\":true", threadChanged.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(replyBody, threadChanged.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("participantDisplayNames", threadChanged.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("displayName", threadChanged.PayloadJson, StringComparison.OrdinalIgnoreCase);

        var audits = await app.ListAuditLogsAsync(data.TenantA.Id, data.TenantA.Slug);
        var threadAudit = Assert.Single(audits, item =>
            item.Action == "communication.thread_reply_posted" && item.EntityId == replyId);
        Assert.DoesNotContain(replyBody, threadAudit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scope", "Issue362")]
    public async Task MessageThreadAuthorityRequiresReadPostAndCreateThreadWithoutLeakingSummary()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        await app.UpdateConversationMemberAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.ConversationA.Id,
            data.TenantAMember.Id,
            member =>
            {
                member.CanPost = true;
                member.CanCreateThread = false;
            });

        using (var deniedFirstContent = JsonContent("""{"body":"cannot create first reply"}"""))
        using (var deniedFirst = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread/messages",
                   HttpMethod.Post,
                   deniedFirstContent))
        {
            Assert.Equal(HttpStatusCode.BadRequest, deniedFirst.StatusCode);
        }

        using (var firstContent = JsonContent("""{"body":"authorized first reply"}"""))
        using (var first = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread/messages",
                   HttpMethod.Post,
                   firstContent))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var laterContent = JsonContent("""{"body":"posting authority is enough later"}"""))
        using (var later = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread/messages",
                   HttpMethod.Post,
                   laterContent))
        {
            Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        }

        await app.UpdateConversationMemberAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.ConversationA.Id,
            data.TenantAMember.Id,
            member => member.CanPost = false);
        using (var readOnlyContent = JsonContent("""{"body":"read only denial"}"""))
        using (var readOnlyPost = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread/messages",
                   HttpMethod.Post,
                   readOnlyContent))
        {
            Assert.Equal(HttpStatusCode.BadRequest, readOnlyPost.StatusCode);
        }
        using (var readOnlyGet = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread"))
        {
            Assert.Equal(HttpStatusCode.OK, readOnlyGet.StatusCode);
        }

        foreach (var deniedUser in new[] { data.Outsider, data.TenantAAdmin })
        {
            using var denied = await app.SendAsync(
                deniedUser,
                data.TenantA.Slug,
                $"/api/messages/{data.MessageA.Id:D}/thread");
            var deniedBody = await denied.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.DoesNotContain("authorized first reply", deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", deniedBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(data.CrossTenantUser.DisplayName, deniedBody, StringComparison.Ordinal);
        }

        using (var crossTenant = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageB.Id:D}/thread"))
        {
            var body = await crossTenant.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, crossTenant.StatusCode);
            Assert.DoesNotContain(data.MessageB.Body, body, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", body, StringComparison.OrdinalIgnoreCase);
        }

        await app.UpdateConversationMemberAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.ConversationA.Id,
            data.TenantAMember.Id,
            member =>
            {
                member.LeftAt = DateTimeOffset.UtcNow;
                member.RemovedAt = member.LeftAt;
                member.RemovedByUserId = data.CrossTenantUser.Id;
            });
        using var removed = await app.SendAsync(
            data.TenantAMember,
            data.TenantA.Slug,
            $"/api/messages/{data.MessageA.Id:D}/thread");
        var removedBody = await removed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);
        Assert.DoesNotContain("authorized first reply", removedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("replyCount", removedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.CrossTenantUser.DisplayName, removedBody, StringComparison.Ordinal);

        var otherWorkspaceId = await app.AddWorkspaceAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.TenantAOwner.Id);
        var workspaceScoped = await app.AddScopedConversationWithRootAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            otherWorkspaceId,
            projectId: null,
            ConversationType.WorkspaceChannel,
            data.TenantAOwner.Id,
            [data.CrossTenantUser.Id],
            "workspace-scoped root must not leak");

        using (var workspaceReadDenied = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{workspaceScoped.Message.Id:D}/thread"))
        {
            var body = await workspaceReadDenied.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, workspaceReadDenied.StatusCode);
            Assert.DoesNotContain(workspaceScoped.Message.Body, body, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(data.TenantAOwner.DisplayName, body, StringComparison.Ordinal);
        }

        using (var workspacePostContent = JsonContent("""{"body":"workspace denial"}"""))
        using (var workspacePostDenied = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{workspaceScoped.Message.Id:D}/thread/messages",
                   HttpMethod.Post,
                   workspacePostContent))
        {
            var body = await workspacePostDenied.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, workspacePostDenied.StatusCode);
            Assert.DoesNotContain(workspaceScoped.Message.Body, body, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", body, StringComparison.OrdinalIgnoreCase);
        }

        var projectScoped = await app.AddScopedConversationWithRootAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.ProjectA.Id,
            ConversationType.DirectMessage,
            data.CrossTenantUser.Id,
            [data.CrossTenantUser.Id, data.TenantAStaff.Id],
            "project-scoped root must not leak");

        using (var authorizedProjectPostContent = JsonContent("""{"body":"authorized project reply"}"""))
        using (var authorizedProjectPost = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{projectScoped.Message.Id:D}/thread/messages",
                   HttpMethod.Post,
                   authorizedProjectPostContent))
        {
            Assert.Equal(HttpStatusCode.OK, authorizedProjectPost.StatusCode);
        }

        using (var projectReadDenied = await app.SendAsync(
                   data.TenantAStaff,
                   data.TenantA.Slug,
                   $"/api/messages/{projectScoped.Message.Id:D}/thread"))
        {
            var body = await projectReadDenied.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, projectReadDenied.StatusCode);
            Assert.DoesNotContain(projectScoped.Message.Body, body, StringComparison.Ordinal);
            Assert.DoesNotContain("authorized project reply", body, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(data.CrossTenantUser.DisplayName, body, StringComparison.Ordinal);
        }

        using (var projectPostContent = JsonContent("""{"body":"project denial"}"""))
        using (var projectPostDenied = await app.SendAsync(
                   data.TenantAStaff,
                   data.TenantA.Slug,
                   $"/api/messages/{projectScoped.Message.Id:D}/thread/messages",
                   HttpMethod.Post,
                   projectPostContent))
        {
            var body = await projectPostDenied.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, projectPostDenied.StatusCode);
            Assert.DoesNotContain(projectScoped.Message.Body, body, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", body, StringComparison.OrdinalIgnoreCase);
        }

        await app.SetWorkspaceMembershipStatusAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.CrossTenantUser.Id,
            MembershipStatus.Suspended);

        using (var revokedRead = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{projectScoped.Message.Id:D}/thread"))
        {
            var body = await revokedRead.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, revokedRead.StatusCode);
            Assert.DoesNotContain(projectScoped.Message.Body, body, StringComparison.Ordinal);
            Assert.DoesNotContain("authorized project reply", body, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(data.CrossTenantUser.DisplayName, body, StringComparison.Ordinal);
        }

        using (var revokedPostContent = JsonContent("""{"body":"revoked denial"}"""))
        using (var revokedPost = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{projectScoped.Message.Id:D}/thread/messages",
                   HttpMethod.Post,
                   revokedPostContent))
        {
            var body = await revokedPost.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, revokedPost.StatusCode);
            Assert.DoesNotContain(projectScoped.Message.Body, body, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Scope", "Issue362")]
    public async Task DeletedThreadMessagesRemainBodylessStableTombstonesAndDeletedRootRejectsPosts()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var createContent = JsonContent("""{"body":"reply that becomes a tombstone"}""");
        using var create = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/messages/{data.MessageA.Id:D}/thread/messages",
            HttpMethod.Post,
            createContent);
        using var createDocument = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var replyId = createDocument.RootElement.GetProperty("message").GetProperty("id").GetGuid();

        using (var deleteReply = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{replyId:D}",
                   HttpMethod.Delete))
        {
            Assert.Equal(HttpStatusCode.OK, deleteReply.StatusCode);
        }

        using (var afterReplyDelete = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread"))
        using (var document = JsonDocument.Parse(await afterReplyDelete.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, afterReplyDelete.StatusCode);
            var reply = Assert.Single(document.RootElement.GetProperty("replies").EnumerateArray());
            Assert.Equal(replyId, reply.GetProperty("id").GetGuid());
            Assert.True(reply.GetProperty("isDeleted").GetBoolean());
            Assert.Equal(string.Empty, reply.GetProperty("body").GetString());
            Assert.Empty(reply.GetProperty("attachments").EnumerateArray());
            Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("replyCount").GetInt32());
        }

        using (var deleteRoot = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}",
                   HttpMethod.Delete))
        {
            Assert.Equal(HttpStatusCode.OK, deleteRoot.StatusCode);
        }

        using (var tombstoneThread = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread"))
        using (var document = JsonDocument.Parse(await tombstoneThread.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, tombstoneThread.StatusCode);
            var root = document.RootElement.GetProperty("rootMessage");
            Assert.True(root.GetProperty("isDeleted").GetBoolean());
            Assert.Equal(string.Empty, root.GetProperty("body").GetString());
            Assert.Empty(root.GetProperty("attachments").EnumerateArray());
            Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("replyCount").GetInt32());
        }

        using (var tombstoneTimeline = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/conversations/{data.ConversationA.Id:D}/messages"))
        using (var document = JsonDocument.Parse(await tombstoneTimeline.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, tombstoneTimeline.StatusCode);
            var root = Assert.Single(
                document.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetGuid() == data.MessageA.Id);
            Assert.True(root.GetProperty("isDeleted").GetBoolean());
            Assert.Equal(string.Empty, root.GetProperty("body").GetString());
            Assert.Empty(root.GetProperty("attachments").EnumerateArray());
            Assert.Equal(1, root.GetProperty("thread").GetProperty("replyCount").GetInt32());
            Assert.DoesNotContain(
                document.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetGuid() == replyId);
        }

        using var deniedContent = JsonContent("""{"body":"cannot reply to deleted root"}""");
        using var denied = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/messages/{data.MessageA.Id:D}/thread/messages",
            HttpMethod.Post,
            deniedContent);
        var deniedBody = await denied.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.DoesNotContain("replyCount", deniedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.TenantAMember.DisplayName, deniedBody, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scope", "Issue362")]
    public async Task ThreadReadIsTruthfullyBoundedAndRejectsCorruptCrossConversationOrTenantLinks()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var seeded = await app.SeedBoundedMessageThreadAsync(data);

        using var response = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/messages/{data.MessageA.Id:D}/thread");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(101, document.RootElement.GetProperty("summary").GetProperty("replyCount").GetInt32());
        Assert.Equal(100, document.RootElement.GetProperty("replies").GetArrayLength());
        Assert.True(document.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(100, document.RootElement.GetProperty("maximumReplies").GetInt32());
        Assert.DoesNotContain(seeded.CrossConversationBody, body, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.CrossTenantBody, body, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantAStaff.DisplayName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantBMember.DisplayName, body, StringComparison.Ordinal);
        var tombstone = Assert.Single(
            document.RootElement.GetProperty("replies").EnumerateArray(),
            reply => reply.GetProperty("id").GetGuid() == seeded.TombstoneReplyId);
        Assert.True(tombstone.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(string.Empty, tombstone.GetProperty("body").GetString());

        using var replyAsRoot = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/messages/{seeded.TombstoneReplyId:D}/thread");
        Assert.Equal(HttpStatusCode.BadRequest, replyAsRoot.StatusCode);
    }

    [Fact]
    [Trait("Scope", "Issue368")]
    public async Task ThreadReplyAnchorIncludesAnOlderExactReplyAndRejectsDeletedOrMismatchedAnchorsWithoutDisclosure()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var seeded = await app.SeedBoundedMessageThreadAsync(data);

        string readCursorBefore;
        int unreadBefore;
        using (var stateBefore = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/conversations/{data.ConversationA.Id:D}/state"))
        using (var stateDocument = JsonDocument.Parse(await stateBefore.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, stateBefore.StatusCode);
            readCursorBefore = stateDocument.RootElement.GetProperty("lastReadMessageId").GetRawText();
            unreadBefore = stateDocument.RootElement.GetProperty("unreadCount").GetInt32();
        }

        Guid[] ordinaryReplyIds;
        using (var ordinary = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread"))
        using (var ordinaryDocument = JsonDocument.Parse(await ordinary.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
            ordinaryReplyIds = ordinaryDocument.RootElement
                .GetProperty("replies")
                .EnumerateArray()
                .Select(reply => reply.GetProperty("id").GetGuid())
                .ToArray();
            Assert.Equal(100, ordinaryReplyIds.Length);
            Assert.DoesNotContain(seeded.OldestReplyId, ordinaryReplyIds);
        }

        using (var response = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/messages/{data.MessageA.Id:D}/thread?anchorReplyMessageId={seeded.OldestReplyId:D}"))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var replies = document.RootElement.GetProperty("replies").EnumerateArray().ToArray();
            Assert.Equal(100, replies.Length);
            Assert.Equal(101, document.RootElement.GetProperty("summary").GetProperty("replyCount").GetInt32());
            Assert.True(document.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal(100, document.RootElement.GetProperty("maximumReplies").GetInt32());
            var anchor = Assert.Single(
                replies,
                reply => reply.GetProperty("id").GetGuid() == seeded.OldestReplyId);
            Assert.Equal("bounded authorized reply 0", anchor.GetProperty("body").GetString());
            var anchoredReplyIds = replies.Select(reply => reply.GetProperty("id").GetGuid()).ToArray();
            Assert.Equal(99, ordinaryReplyIds.Intersect(anchoredReplyIds).Count());
            Assert.Single(ordinaryReplyIds.Except(anchoredReplyIds));
        }

        foreach (var deniedAnchorId in new[]
                 {
                     seeded.TombstoneReplyId,
                     data.MessageA.Id,
                     seeded.WrongRootReplyId,
                     seeded.CrossConversationReplyId,
                     seeded.CrossTenantReplyId,
                     Guid.NewGuid()
                 })
        {
            using var denied = await app.SendAsync(
                data.CrossTenantUser,
                data.TenantA.Slug,
                $"/api/messages/{data.MessageA.Id:D}/thread?anchorReplyMessageId={deniedAnchorId:D}");
            var deniedBody = await denied.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Contains("Message thread not found.", deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("replyCount", deniedBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bounded authorized reply", deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(seeded.CrossConversationBody, deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(seeded.CrossTenantBody, deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(seeded.WrongRootBody, deniedBody, StringComparison.Ordinal);
        }

        using (var stateAfter = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/conversations/{data.ConversationA.Id:D}/state"))
        using (var stateDocument = JsonDocument.Parse(await stateAfter.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, stateAfter.StatusCode);
            Assert.Equal(readCursorBefore, stateDocument.RootElement.GetProperty("lastReadMessageId").GetRawText());
            Assert.Equal(unreadBefore, stateDocument.RootElement.GetProperty("unreadCount").GetInt32());
        }
    }

    [Fact]
    public async Task DmBodyAndMessageListStayParticipantOnlyEvenForElevatedTenantUsers()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        var deniedAdminMessages = await app.SendAsync(data.TenantAAdmin, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages");
        var deniedAdminMessagesBody = await deniedAdminMessages.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedAdminMessages.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedAdminMessagesBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantAMember.Email, deniedAdminMessagesBody, StringComparison.OrdinalIgnoreCase);

        var deniedAdminDetail = await app.SendAsync(data.TenantAAdmin, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}");
        var deniedAdminDetailBody = await deniedAdminDetail.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedAdminDetail.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedAdminDetailBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantAMember.Email, deniedAdminDetailBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadOnlyAndRemovedParticipantsCannotPostOrCreateThreads()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        await app.UpdateConversationMemberAsync(data.TenantA.Id, data.TenantA.Slug, data.ConversationA.Id, data.TenantAMember.Id, member =>
        {
            member.Role = ConversationMemberRole.ReadOnly;
            member.CanPost = false;
            member.CanCreateThread = false;
        });

        using var postContent = JsonContent("""{"body":"B-07 readonly post body","attachments":[]}""");
        var postResponse = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, postContent);
        var postBody = await postResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, postResponse.StatusCode);
        Assert.DoesNotContain("B-07 readonly post body", postBody, StringComparison.Ordinal);

        using var threadContent = JsonContent($$"""
            {"type":"Thread","workspaceId":"{{data.WorkspaceA.Id:D}}","parentConversationId":"{{data.ConversationA.Id:D}}","title":"B-07 readonly thread","memberUserIds":[]}
            """);
        var threadResponse = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, "/api/conversations", HttpMethod.Post, threadContent);
        var threadBody = await threadResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, threadResponse.StatusCode);
        Assert.DoesNotContain("B-07 readonly thread", threadBody, StringComparison.Ordinal);

        await app.UpdateConversationMemberAsync(data.TenantA.Id, data.TenantA.Slug, data.ConversationA.Id, data.TenantAMember.Id, member =>
        {
            member.Role = ConversationMemberRole.Member;
            member.CanPost = true;
            member.CanCreateThread = true;
            member.LeftAt = DateTimeOffset.UtcNow;
            member.RemovedAt = DateTimeOffset.UtcNow;
            member.RemovedByUserId = data.CrossTenantUser.Id;
        });

        using var removedPostContent = JsonContent("""{"body":"B-07 removed post body","attachments":[]}""");
        var removedPostResponse = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, removedPostContent);
        var removedPostBody = await removedPostResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, removedPostResponse.StatusCode);
        Assert.DoesNotContain("B-07 removed post body", removedPostBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreadAccessRequiresParentConversationParticipantBoundary()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var threadContent = JsonContent($$"""
            {"type":"Thread","workspaceId":"{{data.WorkspaceA.Id:D}}","parentConversationId":"{{data.ConversationA.Id:D}}","title":"B-07 parent scoped thread","memberUserIds":[]}
            """);
        var threadResponse = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, "/api/conversations", HttpMethod.Post, threadContent);
        var threadBody = await threadResponse.Content.ReadAsStringAsync();
        var threadId = ReadResponseId(threadBody);

        Assert.Equal(HttpStatusCode.OK, threadResponse.StatusCode);

        using var threadMessageContent = JsonContent("""{"body":"B-07 thread private body","attachments":[]}""");
        var sendThreadMessage = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{threadId}/messages", HttpMethod.Post, threadMessageContent);
        Assert.Equal(HttpStatusCode.OK, sendThreadMessage.StatusCode);

        await app.UpdateConversationMemberAsync(data.TenantA.Id, data.TenantA.Slug, data.ConversationA.Id, data.CrossTenantUser.Id, member =>
        {
            member.LeftAt = DateTimeOffset.UtcNow;
            member.RemovedAt = DateTimeOffset.UtcNow;
            member.RemovedByUserId = data.TenantAMember.Id;
        });

        var deniedThreadRead = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{threadId}/messages");
        var deniedThreadReadBody = await deniedThreadRead.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedThreadRead.StatusCode);
        Assert.DoesNotContain("B-07 thread private body", deniedThreadReadBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.MessageA.Body, deniedThreadReadBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectChannelBodyStillRequiresConversationParticipantMembership()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var projectContent = JsonContent($$"""
            {"type":"ProjectChannel","workspaceId":"{{data.WorkspaceA.Id:D}}","projectId":"{{data.ProjectA.Id:D}}","title":"B-07 Project Channel","memberUserIds":[]}
            """);
        var projectResponse = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, "/api/conversations", HttpMethod.Post, projectContent);
        var projectBody = await projectResponse.Content.ReadAsStringAsync();
        var projectChannelId = ReadResponseId(projectBody);

        Assert.Equal(HttpStatusCode.OK, projectResponse.StatusCode);

        using var messageContent = JsonContent("""{"body":"B-07 project channel private body","attachments":[]}""");
        var sendResponse = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{projectChannelId}/messages", HttpMethod.Post, messageContent);
        Assert.Equal(HttpStatusCode.OK, sendResponse.StatusCode);

        var deniedProjectMember = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/conversations/{projectChannelId}/messages");
        var deniedProjectMemberBody = await deniedProjectMember.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedProjectMember.StatusCode);
        Assert.DoesNotContain("B-07 project channel private body", deniedProjectMemberBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationCreateSupportsOnlyMvpTypesAndKeepsScopeFields()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var directContent = JsonContent($$"""
            {"type":"DirectMessage","workspaceId":"{{data.WorkspaceA.Id:D}}","title":"direct title must not be stored","memberUserIds":["{{data.TenantAMember.Id:D}}"]}
            """);
        var directResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/conversations", HttpMethod.Post, directContent);
        var directBody = await directResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, directResponse.StatusCode);
        Assert.Contains("DirectMessage", directBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectChannel", directBody, StringComparison.Ordinal);
        Assert.DoesNotContain("direct title must not be stored", directBody, StringComparison.Ordinal);

        using var projectContent = JsonContent($$"""
            {"type":"ProjectChannel","workspaceId":"{{data.WorkspaceA.Id:D}}","projectId":"{{data.ProjectA.Id:D}}","title":"Project Alpha","memberUserIds":[]}
            """);
        var projectResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/conversations", HttpMethod.Post, projectContent);
        var projectBody = await projectResponse.Content.ReadAsStringAsync();
        var projectConversationId = ReadResponseId(projectBody);

        Assert.Equal(HttpStatusCode.OK, projectResponse.StatusCode);
        Assert.Contains("ProjectChannel", projectBody, StringComparison.Ordinal);
        Assert.Contains(data.ProjectA.Id.ToString("D"), projectBody, StringComparison.OrdinalIgnoreCase);

        var storedProjectChannel = await app.GetConversationAsync(data.TenantA.Id, data.TenantA.Slug, projectConversationId);
        Assert.NotNull(storedProjectChannel);
        Assert.Equal(ConversationType.ProjectChannel, storedProjectChannel.Type);
        Assert.Equal(data.WorkspaceA.Id, storedProjectChannel.WorkspaceId);
        Assert.Equal(data.ProjectA.Id, storedProjectChannel.ProjectId);
    }

    [Fact]
    public async Task DirectConversationMvpCanSearchCreateReuseSendAndPersistWithinTenant()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        var recipients = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/conversations/recipients?query=Staff");
        var recipientsBody = await recipients.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, recipients.StatusCode);
        Assert.Contains(data.TenantAStaff.DisplayName, recipientsBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantAStaff.Email, recipientsBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.TenantBMember.DisplayName, recipientsBody, StringComparison.Ordinal);

        using var createContent = JsonContent($$"""{"recipientUserId":"{{data.TenantAStaff.Id:D}}"}""");
        var createResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/conversations/direct", HttpMethod.Post, createContent);
        var createBody = await createResponse.Content.ReadAsStringAsync();
        var conversationId = ReadResponseId(createBody);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.Contains(data.TenantAStaff.DisplayName, createBody, StringComparison.Ordinal);

        var storedConversation = await app.GetConversationAsync(data.TenantA.Id, data.TenantA.Slug, conversationId);
        Assert.NotNull(storedConversation);
        Assert.Equal(ConversationType.DirectMessage, storedConversation.Type);
        Assert.Equal(data.WorkspaceA.Id, storedConversation.WorkspaceId);
        Assert.Equal(
            new[] { data.TenantAOwner.Id, data.TenantAStaff.Id }.Order().ToArray(),
            storedConversation.Members.Select(member => member.UserId).Order().ToArray());

        using var duplicateContent = JsonContent($$"""{"recipientUserId":"{{data.TenantAStaff.Id:D}}"}""");
        var duplicateResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/conversations/direct", HttpMethod.Post, duplicateContent);
        var duplicateBody = await duplicateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        Assert.Equal(conversationId, ReadResponseId(duplicateBody));

        using var messageContent = JsonContent("""{"body":"MVP direct message persisted","attachments":[]}""");
        var messageResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/conversations/{conversationId}/messages", HttpMethod.Post, messageContent);
        var messageBody = await messageResponse.Content.ReadAsStringAsync();
        var messageId = ReadResponseId(messageBody);

        Assert.Equal(HttpStatusCode.OK, messageResponse.StatusCode);
        Assert.Contains("MVP direct message persisted", messageBody, StringComparison.Ordinal);

        var storedMessage = await app.GetMessageAsync(data.TenantA.Id, data.TenantA.Slug, messageId);
        Assert.NotNull(storedMessage);
        Assert.Equal(conversationId, storedMessage.ConversationId);
        Assert.Equal(data.TenantAOwner.Id, storedMessage.AuthorUserId);
        Assert.Equal("MVP direct message persisted", storedMessage.Body);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task UnscopedDirectCreateDoesNotReuseProjectBoundConversationForDeniedActor()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var protectedConversation = await app.AddProjectBoundDirectConversationAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.ProjectA.Id,
            data.TenantAStaff.Id,
            data.TenantAOwner.Id);

        using (var deniedDetail = await app.SendAsync(
                   data.TenantAStaff,
                   data.TenantA.Slug,
                   $"/api/conversations/{protectedConversation.Id:D}"))
        {
            var deniedBody = await deniedDetail.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, deniedDetail.StatusCode);
            Assert.DoesNotContain(protectedConversation.Title!, deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(data.ProjectA.Id.ToString("D"), deniedBody, StringComparison.OrdinalIgnoreCase);
        }

        using var createContent = JsonContent($$"""{"recipientUserId":"{{data.TenantAOwner.Id:D}}"}""");
        using var createResponse = await app.SendAsync(
            data.TenantAStaff,
            data.TenantA.Slug,
            "/api/conversations/direct",
            HttpMethod.Post,
            createContent);
        var createBody = await createResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.DoesNotContain(protectedConversation.Title!, createBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.ProjectA.Id.ToString("D"), createBody, StringComparison.OrdinalIgnoreCase);
        var unscopedConversationId = ReadResponseId(createBody);
        Assert.NotEqual(protectedConversation.Id, unscopedConversationId);

        var unscopedConversation = await app.GetConversationAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            unscopedConversationId);
        Assert.NotNull(unscopedConversation);
        Assert.Null(unscopedConversation.ProjectId);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task GenericDirectCreateReusesOnlyAnExactlyMatchingProjectScope()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var projectBound = await app.AddProjectBoundDirectConversationAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.ProjectA.Id,
            data.TenantAMember.Id,
            data.TenantAOwner.Id);

        using var sameProject = JsonContent($$"""
            {"type":"DirectMessage","workspaceId":"{{data.WorkspaceA.Id:D}}","projectId":"{{data.ProjectA.Id:D}}","memberUserIds":["{{data.TenantAMember.Id:D}}"]}
            """);
        using var sameProjectResponse = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            "/api/conversations",
            HttpMethod.Post,
            sameProject);
        Assert.Equal(HttpStatusCode.OK, sameProjectResponse.StatusCode);
        Assert.Equal(
            projectBound.Id,
            ReadResponseId(await sameProjectResponse.Content.ReadAsStringAsync()));

        using var unscoped = JsonContent($$"""
            {"type":"DirectMessage","workspaceId":"{{data.WorkspaceA.Id:D}}","memberUserIds":["{{data.TenantAMember.Id:D}}"]}
            """);
        using var unscopedResponse = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            "/api/conversations",
            HttpMethod.Post,
            unscoped);
        Assert.Equal(HttpStatusCode.OK, unscopedResponse.StatusCode);
        var unscopedId = ReadResponseId(await unscopedResponse.Content.ReadAsStringAsync());
        Assert.NotEqual(projectBound.Id, unscopedId);
        Assert.Null((await app.GetConversationAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            unscopedId))!.ProjectId);
    }

    [Fact]
    public async Task DirectConversationMvpRejectsSelfAndCrossTenantRecipients()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var selfContent = JsonContent($$"""{"recipientUserId":"{{data.TenantAOwner.Id:D}}"}""");
        var selfResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/conversations/direct", HttpMethod.Post, selfContent);
        Assert.Equal(HttpStatusCode.BadRequest, selfResponse.StatusCode);

        using var crossTenantContent = JsonContent($$"""{"recipientUserId":"{{data.TenantBMember.Id:D}}"}""");
        var crossTenantResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/conversations/direct", HttpMethod.Post, crossTenantContent);
        var crossTenantBody = await crossTenantResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, crossTenantResponse.StatusCode);
        Assert.DoesNotContain(data.TenantBMember.DisplayName, crossTenantBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantBMember.Email, crossTenantBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConversationThreadCreationInheritsParentScopeAndMembers()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var content = JsonContent($$"""
            {"type":"Thread","workspaceId":"{{data.WorkspaceA.Id:D}}","parentConversationId":"{{data.ConversationA.Id:D}}","title":"Thread A","memberUserIds":[]}
            """);
        var response = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, "/api/conversations", HttpMethod.Post, content);
        var body = await response.Content.ReadAsStringAsync();
        var threadId = ReadResponseId(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Thread", body, StringComparison.Ordinal);
        Assert.Contains(data.ConversationA.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);

        var thread = await app.GetConversationAsync(data.TenantA.Id, data.TenantA.Slug, threadId);
        Assert.NotNull(thread);
        Assert.Equal(ConversationType.Thread, thread.Type);
        Assert.Equal(data.WorkspaceA.Id, thread.WorkspaceId);
        Assert.Null(thread.ProjectId);
        Assert.Equal(data.ConversationA.Id, thread.ParentConversationId);
        Assert.Equal(data.ConversationA.Id, thread.RootConversationId);
        Assert.Equal(
            new[] { data.CrossTenantUser.Id, data.TenantAMember.Id }.Order().ToArray(),
            thread.Members.Select(member => member.UserId).Order().ToArray());
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task ConversationThreadCreationRejectsChildBeyondReadableDepthWithoutSuccessMutation()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var deepestReadableThreadId = await app.AddConversationThreadChainAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.ConversationA.Id,
            data.WorkspaceA.Id,
            data.CrossTenantUser.Id,
            depth: 32);
        var before = await app.GetConversationThreadCreationCountsAsync(
            data.TenantA.Id,
            data.TenantA.Slug);

        using var content = JsonContent($$"""
            {"type":"Thread","workspaceId":"{{data.WorkspaceA.Id:D}}","parentConversationId":"{{deepestReadableThreadId:D}}","title":"unreadable level 33","memberUserIds":[]}
            """);
        using var response = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            "/api/conversations",
            HttpMethod.Post,
            content);
        var responseBody = await response.Content.ReadAsStringAsync();
        var after = await app.GetConversationThreadCreationCountsAsync(
            data.TenantA.Id,
            data.TenantA.Slug);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("unreadable level 33", responseBody, StringComparison.Ordinal);
        Assert.Equal(before.Conversations, after.Conversations);
        Assert.Equal(before.SuccessAudits, after.SuccessAudits);
    }

    [Fact]
    public async Task ConversationCreateRejectsDisabledTypesAndInvalidThreadBoundariesWithoutBodyLeak()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var otherWorkspaceId = await app.AddWorkspaceAsync(data.TenantA.Id, data.TenantA.Slug, data.TenantAOwner.Id);

        foreach (var disabledType in new[] { "CommitteeChannel", "AnnouncementThread", "ExternalSharedChannel", "LegalHoldConversation" })
        {
            using var content = JsonContent($$"""
                {"type":"{{disabledType}}","workspaceId":"{{data.WorkspaceA.Id:D}}","projectId":"{{data.ProjectA.Id:D}}","title":"secret disabled title","memberUserIds":["{{data.TenantAMember.Id:D}}"]}
                """);
            var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/conversations", HttpMethod.Post, content);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("secret disabled title", body, StringComparison.Ordinal);
        }

        await AssertConversationCreateRejectedWithoutLeakAsync(
            app,
            data,
            $$"""{"type":"Thread","workspaceId":"{{data.WorkspaceA.Id:D}}","title":"secret missing parent","memberUserIds":[]}""",
            "secret missing parent");
        await AssertConversationCreateRejectedWithoutLeakAsync(
            app,
            data,
            $$"""{"type":"Thread","workspaceId":"{{data.WorkspaceA.Id:D}}","parentConversationId":"{{Guid.NewGuid():D}}","title":"secret missing parent row","memberUserIds":[]}""",
            "secret missing parent row");
        await AssertConversationCreateRejectedWithoutLeakAsync(
            app,
            data,
            $$"""{"type":"Thread","workspaceId":"{{otherWorkspaceId:D}}","parentConversationId":"{{data.ConversationA.Id:D}}","title":"secret workspace mismatch","memberUserIds":[]}""",
            "secret workspace mismatch");
        await AssertConversationCreateRejectedWithoutLeakAsync(
            app,
            data,
            $$"""{"type":"Thread","workspaceId":"{{data.WorkspaceA.Id:D}}","projectId":"{{data.ProjectA.Id:D}}","parentConversationId":"{{data.ConversationA.Id:D}}","title":"secret project expansion","memberUserIds":[]}""",
            "secret project expansion");
        await AssertConversationCreateRejectedWithoutLeakAsync(
            app,
            data,
            $$"""{"type":"Thread","workspaceId":"{{data.WorkspaceA.Id:D}}","parentConversationId":"{{data.ConversationA.Id:D}}","title":"secret member expansion","memberUserIds":["{{data.Outsider.Id:D}}"]}""",
            "secret member expansion");
    }

    [Fact]
    public async Task RemovedConversationParticipantCannotReadEditOrDeleteExistingMessage()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        await AssertStatusAsync(app, data.TenantAMember, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/leave", HttpStatusCode.OK, HttpMethod.Post);

        var deniedRead = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages");
        var deniedReadBody = await deniedRead.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedRead.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedReadBody, StringComparison.Ordinal);

        using var editContent = JsonContent("""{"body":"A-08 edited body should not be accepted"}""");
        var deniedEdit = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/messages/{data.MessageA.Id}", HttpMethod.Patch, editContent);
        var deniedEditBody = await deniedEdit.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedEdit.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedEditBody, StringComparison.Ordinal);
        Assert.DoesNotContain("A-08 edited body should not be accepted", deniedEditBody, StringComparison.Ordinal);

        var deniedDelete = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/messages/{data.MessageA.Id}", HttpMethod.Delete);

        Assert.Equal(HttpStatusCode.BadRequest, deniedDelete.StatusCode);
    }

    [Fact]
    public async Task CommunicationSafetyBaselineEnforcesPostValidationSpamRateLimitAndClosedStates()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var activeContent = JsonContent("""{"body":"B-10 active participant post","attachments":[]}""");
        var activePost = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, activeContent);
        Assert.Equal(HttpStatusCode.OK, activePost.StatusCode);

        using var emptyContent = JsonContent("""{"body":"   ","attachments":[]}""");
        var emptyPost = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, emptyContent);
        Assert.Equal(HttpStatusCode.BadRequest, emptyPost.StatusCode);

        using var oversizedContent = JsonContent($$"""{"body":"{{new string('x', 121)}}","attachments":[]}""");
        var oversizedPost = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, oversizedContent);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedPost.StatusCode);

        await using var duplicateApp = await HttpTenantIsolationTestApp.CreateAsync();
        var duplicateData = duplicateApp.Data;
        using var duplicateOne = JsonContent("""{"body":"B-10 duplicate body","attachments":[]}""");
        var firstDuplicate = await duplicateApp.SendAsync(duplicateData.CrossTenantUser, duplicateData.TenantA.Slug, $"/api/conversations/{duplicateData.ConversationA.Id}/messages", HttpMethod.Post, duplicateOne);
        Assert.Equal(HttpStatusCode.OK, firstDuplicate.StatusCode);
        using var duplicateTwo = JsonContent("""{"body":"B-10 duplicate body","attachments":[]}""");
        var secondDuplicate = await duplicateApp.SendAsync(duplicateData.CrossTenantUser, duplicateData.TenantA.Slug, $"/api/conversations/{duplicateData.ConversationA.Id}/messages", HttpMethod.Post, duplicateTwo);
        var secondDuplicateBody = await secondDuplicate.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, secondDuplicate.StatusCode);
        Assert.DoesNotContain("B-10 duplicate body", secondDuplicateBody, StringComparison.Ordinal);

        await using var rateApp = await HttpTenantIsolationTestApp.CreateAsync();
        var rateData = rateApp.Data;
        for (var index = 0; index < 3; index++)
        {
            using var rateContent = JsonContent($$"""{"body":"B-10 rate {{index}}","attachments":[]}""");
            var response = await rateApp.SendAsync(rateData.CrossTenantUser, rateData.TenantA.Slug, $"/api/conversations/{rateData.ConversationA.Id}/messages", HttpMethod.Post, rateContent);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var limitedContent = JsonContent("""{"body":"B-10 rate limited body","attachments":[]}""");
        var limited = await rateApp.SendAsync(rateData.CrossTenantUser, rateData.TenantA.Slug, $"/api/conversations/{rateData.ConversationA.Id}/messages", HttpMethod.Post, limitedContent);
        var limitedBody = await limited.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, limited.StatusCode);
        Assert.DoesNotContain("B-10 rate limited body", limitedBody, StringComparison.Ordinal);

        await app.UpdateConversationAsync(data.TenantA.Id, data.TenantA.Slug, data.ConversationA.Id, conversation => conversation.IsLocked = true);
        using var lockedContent = JsonContent("""{"body":"B-10 locked body","attachments":[]}""");
        var lockedPost = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, lockedContent);
        var lockedBody = await lockedPost.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, lockedPost.StatusCode);
        Assert.DoesNotContain("B-10 locked body", lockedBody, StringComparison.Ordinal);

        await app.UpdateConversationAsync(data.TenantA.Id, data.TenantA.Slug, data.ConversationA.Id, conversation =>
        {
            conversation.IsLocked = false;
            conversation.IsArchived = true;
        });
        using var archivedContent = JsonContent("""{"body":"B-10 archived body","attachments":[]}""");
        var archivedPost = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, archivedContent);
        Assert.Equal(HttpStatusCode.BadRequest, archivedPost.StatusCode);

        await app.UpdateConversationAsync(data.TenantA.Id, data.TenantA.Slug, data.ConversationA.Id, conversation =>
        {
            conversation.IsArchived = false;
            conversation.Type = ConversationType.ExternalSharedChannel;
        });
        using var unsupportedContent = JsonContent("""{"body":"B-10 unsupported type body","attachments":[]}""");
        var unsupportedPost = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, unsupportedContent);
        var unsupportedBody = await unsupportedPost.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedPost.StatusCode);
        Assert.DoesNotContain("B-10 unsupported type body", unsupportedBody, StringComparison.Ordinal);

        var auditLogs = await app.ListAuditLogsAsync(data.TenantA.Id, data.TenantA.Slug);
        var auditJson = JsonSerializer.Serialize(auditLogs.Select(log => new { log.Action, log.Summary, log.MetadataJson }));
        Assert.Contains("communication.message_post_denied", auditJson, StringComparison.Ordinal);
        Assert.Contains("communication.rate_limited", JsonSerializer.Serialize(await rateApp.ListAuditLogsAsync(rateData.TenantA.Id, rateData.TenantA.Slug)), StringComparison.Ordinal);
        Assert.Contains("communication.spam_guard_triggered", JsonSerializer.Serialize(await duplicateApp.ListAuditLogsAsync(duplicateData.TenantA.Id, duplicateData.TenantA.Slug)), StringComparison.Ordinal);
        Assert.DoesNotContain("B-10 locked body", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("B-10 unsupported type body", auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommunicationEditDeleteReportAndLockStayParticipantBoundedAndMetadataOnly()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var editContent = JsonContent("""{"body":"B-10 edited body"}""");
        var edit = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/messages/{data.MessageA.Id}", HttpMethod.Patch, editContent);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using (var editDocument = JsonDocument.Parse(await edit.Content.ReadAsStringAsync()))
        {
            Assert.Equal(data.MessageA.Id, editDocument.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("B-10 edited body", editDocument.RootElement.GetProperty("body").GetString());
            Assert.True(editDocument.RootElement.GetProperty("editedAt").GetDateTimeOffset() > DateTimeOffset.MinValue);
            Assert.True(editDocument.RootElement.GetProperty("version").GetInt64() > 0);
        }

        using var deniedEditContent = JsonContent("""{"body":"B-10 non-author edit body"}""");
        var deniedEdit = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/messages/{data.MessageA.Id}", HttpMethod.Patch, deniedEditContent);
        var deniedEditBody = await deniedEdit.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, deniedEdit.StatusCode);
        Assert.DoesNotContain("B-10 edited body", deniedEditBody, StringComparison.Ordinal);
        Assert.DoesNotContain("B-10 non-author edit body", deniedEditBody, StringComparison.Ordinal);

        using var reportContent = JsonContent("""{"reasonCode":"abuse","reason":"raw report text token storage/path DM body"}""");
        var report = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/messages/{data.MessageA.Id}/report", HttpMethod.Post, reportContent);
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        using (var reportDocument = JsonDocument.Parse(await report.Content.ReadAsStringAsync()))
        {
            Assert.Equal("OK", reportDocument.RootElement.GetProperty("status").GetString());
        }

        using var deniedReportContent = JsonContent("""{"reasonCode":"abuse","reason":"raw report text token storage/path DM body"}""");
        var deniedReport = await app.SendAsync(data.TenantAAdmin, data.TenantA.Slug, $"/api/messages/{data.MessageA.Id}/report", HttpMethod.Post, deniedReportContent);
        var deniedReportBody = await deniedReport.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, deniedReport.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedReportBody, StringComparison.Ordinal);

        var deniedAdminLock = await app.SendAsync(data.TenantAAdmin, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/lock", HttpMethod.Post, JsonContent("""{"reasonCode":"admin-non-participant"}"""));
        var deniedAdminLockBody = await deniedAdminLock.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, deniedAdminLock.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedAdminLockBody, StringComparison.Ordinal);

        await app.UpdateConversationMemberAsync(data.TenantA.Id, data.TenantA.Slug, data.ConversationA.Id, data.CrossTenantUser.Id, member =>
        {
            member.Role = ConversationMemberRole.Admin;
            member.CanManageMembers = true;
        });

        var lockResponse = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/lock", HttpMethod.Post, JsonContent("""{"reasonCode":"moderation_lock"}"""));
        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);

        using var lockedEditContent = JsonContent("""{"body":"B-10 locked edit body"}""");
        var lockedEdit = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/messages/{data.MessageA.Id}", HttpMethod.Patch, lockedEditContent);
        Assert.Equal(HttpStatusCode.BadRequest, lockedEdit.StatusCode);

        var unlockResponse = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/unlock", HttpMethod.Post, JsonContent("""{"reasonCode":"moderation_unlock"}"""));
        Assert.Equal(HttpStatusCode.OK, unlockResponse.StatusCode);

        var deleteResponse = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/messages/{data.MessageA.Id}", HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        using (var deleteDocument = JsonDocument.Parse(await deleteResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("OK", deleteDocument.RootElement.GetProperty("status").GetString());
        }

        var deletedRead = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages");
        var deletedReadBody = await deletedRead.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, deletedRead.StatusCode);
        Assert.DoesNotContain("B-10 edited body", deletedReadBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.MessageA.Body, deletedReadBody, StringComparison.Ordinal);

        var storedMessage = await app.GetMessageAsync(data.TenantA.Id, data.TenantA.Slug, data.MessageA.Id);
        Assert.NotNull(storedMessage);
        Assert.NotNull(storedMessage.DeletedAt);
        Assert.Equal(data.CrossTenantUser.Id, storedMessage.DeletedByUserId);
        Assert.Equal(string.Empty, storedMessage.Body);

        var auditLogs = await app.ListAuditLogsAsync(data.TenantA.Id, data.TenantA.Slug);
        var auditJson = JsonSerializer.Serialize(auditLogs.Select(log => new { log.Action, log.Summary, log.MetadataJson }));
        Assert.Contains("communication.message_edited", auditJson, StringComparison.Ordinal);
        Assert.Contains("communication.message_edit_denied", auditJson, StringComparison.Ordinal);
        Assert.Contains("communication.message_reported", auditJson, StringComparison.Ordinal);
        Assert.Contains("communication.message_report_denied", auditJson, StringComparison.Ordinal);
        Assert.Contains("communication.conversation_locked", auditJson, StringComparison.Ordinal);
        Assert.Contains("communication.message_deleted", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("B-10 edited body", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("B-10 non-author edit body", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("raw report text", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(data.FileA.StorageKey, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("StudentRecordRestricted", auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationReadCursorMustReferenceMessageInSameConversation()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using var content = JsonContent($$"""{"lastReadMessageId":"{{data.MessageB.Id:D}}"}""");
        var response = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/read", HttpMethod.Post, content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(data.MessageB.Body, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParticipantStateIsOwnStateOnlyAndDoesNotExposeOtherParticipantMetadata()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        var ownState = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/state");
        var ownStateBody = await ownState.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, ownState.StatusCode);
        Assert.Contains($@"""userId"":""{data.CrossTenantUser.Id:D}""", ownStateBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.TenantAMember.Email, ownStateBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.MessageA.Body, ownStateBody, StringComparison.Ordinal);

        using var updateContent = JsonContent($$"""
            {"lastReadMessageId":"{{data.MessageA.Id:D}}","unreadCursorMessageId":"{{data.MessageA.Id:D}}","isMuted":true,"isArchived":true}
            """);
        var updateState = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/state", HttpMethod.Patch, updateContent);
        var updateStateBody = await updateState.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, updateState.StatusCode);
        Assert.Contains($@"""lastReadMessageId"":""{data.MessageA.Id:D}""", updateStateBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""isMuted"":true", updateStateBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""isArchived"":true", updateStateBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.TenantAMember.Email, updateStateBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.MessageA.Body, updateStateBody, StringComparison.Ordinal);

        var list = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, "/api/conversations");
        var listBody = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(@"""isMuted"":true", listBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""isArchived"":true", listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(data.TenantAMember.Email, listBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Scope", "Issue355")]
    public async Task InboxViewsUseCurrentReadableConversationsAndKeepLaterIndependentFromReadState()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using (var mentionContent = JsonContent($$"""
            {"body":"Issue 355 mention","mentionedUserIds":["{{data.CrossTenantUser.Id:D}}"]}
            """))
        using (var mentionResponse = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   $"/api/conversations/{data.ConversationA.Id:D}/messages",
                   HttpMethod.Post,
                   mentionContent))
        {
            Assert.Equal(HttpStatusCode.OK, mentionResponse.StatusCode);
        }

        using (var allResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   "/api/conversations?view=All"))
        using (var allDocument = JsonDocument.Parse(await allResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);
            var root = allDocument.RootElement;
            Assert.Equal(1, root.GetProperty("counts").GetProperty("all").GetInt32());
            Assert.Equal(1, root.GetProperty("counts").GetProperty("unread").GetInt32());
            Assert.Equal(1, root.GetProperty("counts").GetProperty("mentions").GetInt32());
            Assert.Equal(0, root.GetProperty("counts").GetProperty("later").GetInt32());
            Assert.Equal(data.ConversationA.Id, Assert.Single(root.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        }

        int unreadBeforeLater;
        using (var unreadResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   "/api/conversations?view=Unread"))
        using (var unreadDocument = JsonDocument.Parse(await unreadResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, unreadResponse.StatusCode);
            unreadBeforeLater = Assert.Single(unreadDocument.RootElement.GetProperty("items").EnumerateArray())
                .GetProperty("unreadCount")
                .GetInt32();
            Assert.True(unreadBeforeLater > 0);
        }

        using (var laterContent = JsonContent("""{"isLater":true}"""))
        using (var laterResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/conversations/{data.ConversationA.Id:D}/state",
                   HttpMethod.Patch,
                   laterContent))
        using (var laterState = JsonDocument.Parse(await laterResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, laterResponse.StatusCode);
            Assert.True(laterState.RootElement.GetProperty("isLater").GetBoolean());
            Assert.Equal(unreadBeforeLater, laterState.RootElement.GetProperty("unreadCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, laterState.RootElement.GetProperty("lastReadMessageId").ValueKind);
        }

        using (var laterListResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   "/api/conversations?view=Later"))
        using (var laterList = JsonDocument.Parse(await laterListResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, laterListResponse.StatusCode);
            Assert.Equal(1, laterList.RootElement.GetProperty("counts").GetProperty("later").GetInt32());
            var item = Assert.Single(laterList.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(data.ConversationA.Id, item.GetProperty("id").GetGuid());
            Assert.True(item.GetProperty("isLater").GetBoolean());
            Assert.Equal(unreadBeforeLater, item.GetProperty("unreadCount").GetInt32());
        }

        using (var deniedListResponse = await app.SendAsync(
                   data.TenantAAdmin,
                   data.TenantA.Slug,
                   "/api/conversations?view=All"))
        {
            var deniedBody = await deniedListResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, deniedListResponse.StatusCode);
            Assert.Contains("\"all\":0", deniedBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(data.ConversationA.Title!, deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Issue 355 mention", deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(data.ConversationB.Title!, deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(data.MessageB.Body, deniedBody, StringComparison.Ordinal);
        }

        using (var invalidViewResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   "/api/conversations?view=99"))
        {
            var invalidViewBody = await invalidViewResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, invalidViewResponse.StatusCode);
            Assert.DoesNotContain(data.ConversationA.Title!, invalidViewBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Issue 355 mention", invalidViewBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scope", "Issue368")]
    public async Task MessageFollowUpsArePrivateIdempotentReauthorizedAndDoNotMutateReadState()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using (var readContent = JsonContent($$"""{"lastReadMessageId":"{{data.MessageA.Id:D}}"}"""))
        using (var readResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/conversations/{data.ConversationA.Id:D}/read",
                   HttpMethod.Post,
                   readContent))
        {
            Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        }

        using var stateBeforeResponse = await app.SendAsync(
            data.CrossTenantUser,
            data.TenantA.Slug,
            $"/api/conversations/{data.ConversationA.Id:D}/state");
        using var stateBefore = JsonDocument.Parse(await stateBeforeResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, stateBeforeResponse.StatusCode);
        var readMessageBefore = stateBefore.RootElement.GetProperty("lastReadMessageId").GetGuid();
        var unreadBefore = stateBefore.RootElement.GetProperty("unreadCount").GetInt32();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var saveResponse = await app.SendAsync(
                data.CrossTenantUser,
                data.TenantA.Slug,
                $"/api/me/message-follow-ups/{data.MessageA.Id:D}",
                HttpMethod.Put,
                JsonContent("{}"));
            using var saved = JsonDocument.Parse(await saveResponse.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
            Assert.Equal(data.MessageA.Id, saved.RootElement.GetProperty("messageId").GetGuid());
            Assert.True(saved.RootElement.GetProperty("isSaved").GetBoolean());
        }

        using (var listResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   "/api/me/message-follow-ups?page=1&pageSize=20"))
        using (var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            Assert.Equal(1, list.RootElement.GetProperty("totalCount").GetInt32());
            var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(data.MessageA.Id, item.GetProperty("messageId").GetGuid());
            Assert.Equal(data.ConversationA.Id, item.GetProperty("conversationId").GetGuid());
            Assert.Equal(data.MessageA.Body, item.GetProperty("body").GetString());
        }

        using (var otherParticipantList = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   "/api/me/message-follow-ups?page=1&pageSize=20"))
        using (var otherParticipantDocument = JsonDocument.Parse(await otherParticipantList.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, otherParticipantList.StatusCode);
            Assert.Equal(0, otherParticipantDocument.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Empty(otherParticipantDocument.RootElement.GetProperty("items").EnumerateArray());
        }

        using (var anchorResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/conversations/{data.ConversationA.Id:D}/messages?anchorMessageId={data.MessageA.Id:D}"))
        using (var anchorDocument = JsonDocument.Parse(await anchorResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, anchorResponse.StatusCode);
            Assert.Contains(
                anchorDocument.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetGuid() == data.MessageA.Id);
        }

        using (var stateAfterSaveResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/conversations/{data.ConversationA.Id:D}/state"))
        using (var stateAfterSave = JsonDocument.Parse(await stateAfterSaveResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(readMessageBefore, stateAfterSave.RootElement.GetProperty("lastReadMessageId").GetGuid());
            Assert.Equal(unreadBefore, stateAfterSave.RootElement.GetProperty("unreadCount").GetInt32());
        }

        using (var crossTenantSave = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/me/message-follow-ups/{data.MessageB.Id:D}",
                   HttpMethod.Put,
                   JsonContent("{}")))
        {
            var deniedBody = await crossTenantSave.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, crossTenantSave.StatusCode);
            Assert.DoesNotContain(data.MessageB.Body, deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(data.ConversationB.Title!, deniedBody, StringComparison.Ordinal);
        }

        using (var nonParticipantSave = await app.SendAsync(
                   data.TenantAAdmin,
                   data.TenantA.Slug,
                   $"/api/me/message-follow-ups/{data.MessageA.Id:D}",
                   HttpMethod.Put,
                   JsonContent("{}")))
        {
            var deniedBody = await nonParticipantSave.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, nonParticipantSave.StatusCode);
            Assert.DoesNotContain(data.MessageA.Body, deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(data.ConversationA.Title!, deniedBody, StringComparison.Ordinal);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var removeResponse = await app.SendAsync(
                data.CrossTenantUser,
                data.TenantA.Slug,
                $"/api/me/message-follow-ups/{data.MessageA.Id:D}",
                HttpMethod.Delete);
            using var removed = JsonDocument.Parse(await removeResponse.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
            Assert.False(removed.RootElement.GetProperty("isSaved").GetBoolean());
        }

        using (var resaveResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/me/message-follow-ups/{data.MessageA.Id:D}",
                   HttpMethod.Put,
                   JsonContent("{}")))
        {
            Assert.Equal(HttpStatusCode.OK, resaveResponse.StatusCode);
        }
        await app.UpdateConversationMemberAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.ConversationA.Id,
            data.CrossTenantUser.Id,
            member =>
            {
                member.CanRead = false;
                member.RemovedAt = DateTimeOffset.UtcNow;
            });

        using (var revokedListResponse = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   "/api/me/message-follow-ups?page=1&pageSize=20"))
        using (var revokedList = JsonDocument.Parse(await revokedListResponse.Content.ReadAsStringAsync()))
        {
            var revokedBody = await revokedListResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, revokedListResponse.StatusCode);
            Assert.Equal(0, revokedList.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Empty(revokedList.RootElement.GetProperty("items").EnumerateArray());
            Assert.DoesNotContain(data.MessageA.Body, revokedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(data.ConversationA.Title!, revokedBody, StringComparison.Ordinal);
        }

        using (var revokedRemove = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/me/message-follow-ups/{data.MessageA.Id:D}",
                   HttpMethod.Delete))
        {
            var deniedBody = await revokedRemove.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, revokedRemove.StatusCode);
            Assert.DoesNotContain(data.MessageA.Body, deniedBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ParticipantStateDeniesNonParticipantsRemovedParticipantsAndCrossConversationCursors()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        var deniedAdminState = await app.SendAsync(data.TenantAAdmin, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/state");
        var deniedAdminStateBody = await deniedAdminState.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedAdminState.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedAdminStateBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.TenantAMember.Email, deniedAdminStateBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isMuted", deniedAdminStateBody, StringComparison.OrdinalIgnoreCase);

        using var deniedAdminUpdateContent = JsonContent("""{"isMuted":true,"isArchived":true}""");
        var deniedAdminUpdate = await app.SendAsync(data.TenantAAdmin, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/state", HttpMethod.Patch, deniedAdminUpdateContent);
        var deniedAdminUpdateBody = await deniedAdminUpdate.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedAdminUpdate.StatusCode);
        Assert.DoesNotContain("isMuted", deniedAdminUpdateBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isArchived", deniedAdminUpdateBody, StringComparison.OrdinalIgnoreCase);

        using var cursorMismatchContent = JsonContent($$"""{"unreadCursorMessageId":"{{data.MessageB.Id:D}}"}""");
        var cursorMismatch = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/state", HttpMethod.Patch, cursorMismatchContent);
        var cursorMismatchBody = await cursorMismatch.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, cursorMismatch.StatusCode);
        Assert.DoesNotContain(data.MessageB.Body, cursorMismatchBody, StringComparison.Ordinal);

        await app.UpdateConversationMemberAsync(data.TenantA.Id, data.TenantA.Slug, data.ConversationA.Id, data.CrossTenantUser.Id, member =>
        {
            member.LeftAt = DateTimeOffset.UtcNow;
            member.RemovedAt = DateTimeOffset.UtcNow;
            member.RemovedByUserId = data.TenantAMember.Id;
        });

        var deniedRemovedState = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/state");
        var deniedRemovedStateBody = await deniedRemovedState.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, deniedRemovedState.StatusCode);
        Assert.DoesNotContain(data.MessageA.Body, deniedRemovedStateBody, StringComparison.Ordinal);
        Assert.DoesNotContain("lastReadMessageId", deniedRemovedStateBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MessageNotificationDoesNotEmbedPrivateMessageBody()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        const string privateMessageBody = "A-08 synthetic private notification body";

        using var sendContent = JsonContent($$"""{"body":"{{privateMessageBody}}","attachments":[]}""");
        var sendResponse = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}/messages", HttpMethod.Post, sendContent);

        Assert.Equal(HttpStatusCode.OK, sendResponse.StatusCode);

        var notifications = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, "/api/notifications?page=1&pageSize=20");
        var body = await notifications.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, notifications.StatusCode);
        Assert.Contains("New direct message", body, StringComparison.Ordinal);
        Assert.Contains("You have a new message.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(privateMessageBody, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenantHeaderDoesNotGrantAccessWithoutResourceMembership()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        await AssertWorkspaceProjectionUnavailableOnNonPostgreSqlAsync(
            app,
            data.Outsider,
            data.TenantA.Slug,
            "WorkspaceA",
            "WorkspaceB");
        await AssertStatusAsync(app, data.Outsider, data.TenantA.Slug, $"/api/workspaces/{data.WorkspaceA.Id}", HttpStatusCode.NotFound);
        await AssertStatusAsync(app, data.Outsider, data.TenantA.Slug, $"/api/projects/{data.ProjectA.Id}", HttpStatusCode.NotFound);
        await AssertBadRequestAsync(app, data.Outsider, data.TenantA.Slug, $"/api/conversations/{data.ConversationA.Id}");
        await AssertBadRequestAsync(app, data.Outsider, data.TenantA.Slug, $"/api/files/{data.FileA.Id}/download");
    }

    [Fact]
    public async Task MissingAuthenticationIsRejectedBeforeTenantDataIsReturned()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/workspaces");
        request.Headers.TryAddWithoutValidation("X-Tenant-Slug", app.Data.TenantA.Slug);
        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR04")]
    public async Task MyTasksHttpContractUsesExplicitWorkspaceScopeSafeErrorsAndRevocation()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;

        using (var unauthenticated = new HttpRequestMessage(HttpMethod.Get, "/api/me/tasks?view=Created"))
        {
            unauthenticated.Headers.TryAddWithoutValidation("X-Tenant-Slug", data.TenantA.Slug);
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.SendAsync(unauthenticated)).StatusCode);
        }

        var sole = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/me/tasks?view=Created");
        Assert.Equal(HttpStatusCode.OK, sole.StatusCode);
        using (var document = JsonDocument.Parse(await sole.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, document.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(data.WorkspaceA.Id, document.RootElement.GetProperty("workspaceId").GetGuid());
            Assert.Equal("Created", document.RootElement.GetProperty("view").GetString());
            Assert.Equal("CurrentWorkspace", document.RootElement.GetProperty("scope").GetString());
            var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(data.TaskA.Id, item.GetProperty("taskId").GetGuid());
            Assert.Equal(JsonValueKind.String, item.GetProperty("kind").ValueKind);
            Assert.Equal(JsonValueKind.String, item.GetProperty("stageCategory").ValueKind);
            Assert.Equal(JsonValueKind.String, item.GetProperty("priority").ValueKind);
        }

        var soleCounts = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/me/tasks/counts?view=Created");
        Assert.Equal(HttpStatusCode.OK, soleCounts.StatusCode);
        using (var document = JsonDocument.Parse(await soleCounts.Content.ReadAsStringAsync()))
        {
            Assert.Equal(data.WorkspaceA.Id, document.RootElement.GetProperty("workspaceId").GetGuid());
            Assert.Equal(
                1,
                document.RootElement.GetProperty("views").EnumerateArray()
                    .Single(item => item.GetProperty("view").GetString() == "Created")
                    .GetProperty("count").GetInt32());
        }

        await AssertSafeModelBindingErrorAsync(
            await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/me/tasks?view=999"),
            HttpStatusCode.BadRequest);
        await AssertMyTasksErrorAsync(
            await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/me/tasks?view=Created&projectId={data.ProjectB.Id:D}"),
            HttpStatusCode.NotFound,
            "MY_TASKS_PROJECT_NOT_FOUND");
        await AssertMyTasksErrorAsync(
            await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/me/tasks?view=Created&workspaceId={data.WorkspaceB.Id:D}"),
            HttpStatusCode.Forbidden,
            "MY_TASKS_WORKSPACE_FORBIDDEN");

        var projectScopedPath = $"/api/me/tasks?view=Assigned&workspaceId={data.WorkspaceA.Id:D}&projectId={data.ProjectA.Id:D}";
        foreach (var visibleUser in new[] { data.TenantAOwner, data.TenantAAdmin, data.TenantAMember })
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await app.SendAsync(visibleUser, data.TenantA.Slug, projectScopedPath)).StatusCode);
        }
        await AssertMyTasksErrorAsync(
            await app.SendAsync(data.TenantAStaff, data.TenantA.Slug, projectScopedPath),
            HttpStatusCode.NotFound,
            "MY_TASKS_PROJECT_NOT_FOUND");

        await app.AddGroupMemberAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.GroupA.Id,
            data.TenantAGuest.Id);
        Assert.Equal(
            HttpStatusCode.OK,
            (await app.SendAsync(data.TenantAGuest, data.TenantA.Slug, projectScopedPath)).StatusCode);

        await app.AddWorkspaceAsync(data.TenantA.Id, data.TenantA.Slug, data.TenantAOwner.Id);
        await AssertMyTasksErrorAsync(
            await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, "/api/me/tasks?view=Created"),
            HttpStatusCode.BadRequest,
            "MY_TASKS_INVALID_WORKSPACE_SCOPE");

        var explicitWorkspace = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            $"/api/me/tasks?view=Created&workspaceId={data.WorkspaceA.Id:D}");
        Assert.Equal(HttpStatusCode.OK, explicitWorkspace.StatusCode);

        var allWorkspaces = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            "/api/me/tasks?view=Created&scope=AllWorkspaces");
        Assert.Equal(HttpStatusCode.OK, allWorkspaces.StatusCode);
        using (var document = JsonDocument.Parse(await allWorkspaces.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, document.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("availableWorkspaceCount").GetInt32());
        }

        await app.SetWorkspaceMembershipStatusAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.TenantAOwner.Id,
            MembershipStatus.Suspended);

        var afterRevocation = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            "/api/me/tasks?view=Created&scope=AllWorkspaces");
        Assert.Equal(HttpStatusCode.OK, afterRevocation.StatusCode);
        using (var document = JsonDocument.Parse(await afterRevocation.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        }

        var countsAfterRevocation = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            "/api/me/tasks/counts?view=Created&scope=AllWorkspaces");
        Assert.Equal(HttpStatusCode.OK, countsAfterRevocation.StatusCode);
        using (var document = JsonDocument.Parse(await countsAfterRevocation.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                0,
                document.RootElement.GetProperty("views").EnumerateArray()
                    .Single(item => item.GetProperty("view").GetString() == "Created")
                    .GetProperty("count").GetInt32());
        }

        await AssertMyTasksErrorAsync(
            await app.SendAsync(
                data.TenantAOwner,
                data.TenantA.Slug,
                $"/api/me/tasks?view=Created&workspaceId={data.WorkspaceA.Id:D}"),
            HttpStatusCode.Forbidden,
            "MY_TASKS_WORKSPACE_FORBIDDEN");
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task TaskDeadlinePatchIsServerAuthoritativeAndSeparateFromPlannedSchedule()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var taskPath = $"/api/tasks/{data.TaskA.Id:D}";

        using var initialResponse = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            taskPath);
        using var initialDocument = JsonDocument.Parse(
            await initialResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var version = initialDocument.RootElement
            .GetProperty("task")
            .GetProperty("version")
            .GetInt64();

        using (var reviewer = JsonContent(
                   $$"""{"userId":"{{data.TenantAMember.Id:D}}","expectedVersion":{{version}}}"""))
        using (var reviewerResponse = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   $"{taskPath}/reviewer",
                   HttpMethod.Put,
                   reviewer))
        using (var reviewerDocument = JsonDocument.Parse(
                   await reviewerResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, reviewerResponse.StatusCode);
            version = reviewerDocument.RootElement
                .GetProperty("task")
                .GetProperty("version")
                .GetInt64();
        }

        const string deadlineText = "2026-08-03T00:15:00+09:00";
        var expectedDeadline = new DateTimeOffset(
            2026,
            8,
            3,
            0,
            15,
            0,
            TimeSpan.FromHours(9));
        using (var validDeadline = JsonContent(
                   $$"""{"title":"TaskA","description":null,"priority":1,"plannedStartDate":null,"plannedEndDate":null,"progressPercent":0,"expectedVersion":{{version}},"deadlineAt":"{{deadlineText}}"}"""))
        using (var validResponse = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   taskPath,
                   HttpMethod.Patch,
                   validDeadline))
        using (var validDocument = JsonDocument.Parse(
                   await validResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
            var responseDeadline = validDocument.RootElement
                .GetProperty("deadlineAt")
                .GetDateTimeOffset();
            Assert.Equal(expectedDeadline.ToUniversalTime(), responseDeadline);
            Assert.Equal(TimeSpan.Zero, responseDeadline.Offset);
            version = validDocument.RootElement.GetProperty("version").GetInt64();
        }

        var afterValidDeadline = await app.GetTaskMutationStateAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.TaskA.Id);
        Assert.Equal(expectedDeadline.ToUniversalTime(), afterValidDeadline.DeadlineAt);
        Assert.Equal(TimeSpan.Zero, afterValidDeadline.DeadlineAt!.Value.Offset);
        Assert.Equal(version, afterValidDeadline.Version);
        // tasks.notificationsV1 remains disabled in this application's seeded tenant.
        Assert.Equal(0, afterValidDeadline.TaskNotificationCount);

        foreach (var clientHint in new[]
                 {
                     "\"isMajorDeadlineChange\":true",
                     "\"deadlineChangeClassification\":\"None\"",
                     "\"suppressDeadlineNotification\":true"
                 })
        {
            using var untrustedHint = JsonContent(
                $$"""{"title":"TaskA","description":null,"priority":1,"plannedStartDate":null,"plannedEndDate":null,"progressPercent":0,"expectedVersion":{{version}},"deadlineAt":"2026-08-05T00:15:00+09:00",{{clientHint}}}""");
            using var response = await app.SendAsync(
                data.TenantAOwner,
                data.TenantA.Slug,
                taskPath,
                HttpMethod.Patch,
                untrustedHint);

            await AssertSafeRejectedJsonContractAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(
                afterValidDeadline,
                await app.GetTaskMutationStateAsync(
                    data.TenantA.Id,
                    data.TenantA.Slug,
                    data.TaskA.Id));
        }

        var plannedEnd = new DateOnly(2026, 8, 6);
        using (var plannedEndOnly = JsonContent(
                   $$"""{"plannedStartDate":null,"plannedEndDate":"{{plannedEnd:yyyy-MM-dd}}","milestoneDate":null,"expectedVersion":{{version}}}"""))
        using (var scheduleResponse = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   $"{taskPath}/schedule",
                   HttpMethod.Patch,
                   plannedEndOnly))
        using (var scheduleDocument = JsonDocument.Parse(
                   await scheduleResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, scheduleResponse.StatusCode);
            Assert.Equal(
                plannedEnd,
                DateOnly.FromDateTime(
                    scheduleDocument.RootElement.GetProperty("plannedEndDate").GetDateTime()));
            Assert.False(scheduleDocument.RootElement.TryGetProperty("deadlineAt", out _));
            version = scheduleDocument.RootElement.GetProperty("version").GetInt64();
        }

        var afterPlannedEnd = await app.GetTaskMutationStateAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.TaskA.Id);
        Assert.Equal(expectedDeadline, afterPlannedEnd.DeadlineAt);
        Assert.Equal(plannedEnd, afterPlannedEnd.PlannedEndDate);
        Assert.Equal(version, afterPlannedEnd.Version);
        Assert.Equal(
            afterValidDeadline.TaskNotificationCount,
            afterPlannedEnd.TaskNotificationCount);

        using var deadlineOnSchedule = JsonContent(
            $$"""{"plannedStartDate":null,"plannedEndDate":"2026-08-07","milestoneDate":null,"expectedVersion":{{version}},"deadlineAt":"2026-08-07T00:15:00+09:00"}""");
        using var rejectedSchedule = await app.SendAsync(
            data.TenantAOwner,
            data.TenantA.Slug,
            $"{taskPath}/schedule",
            HttpMethod.Patch,
            deadlineOnSchedule);
        await AssertSafeRejectedJsonContractAsync(
            rejectedSchedule,
            HttpStatusCode.BadRequest);
        Assert.Equal(
            afterPlannedEnd,
            await app.GetTaskMutationStateAsync(
                data.TenantA.Id,
                data.TenantA.Slug,
                data.TaskA.Id));
    }

    [Fact]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task TaskDetailHttpContractUsesCanonicalRoutesSafeErrorsAndBoundedAggregate()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var expectedRoutes = new[]
        {
            "GET api/tasks/{taskItemId:guid}", "PATCH api/tasks/{taskItemId:guid}",
            "GET api/tasks/{taskItemId:guid}/subtasks", "POST api/tasks/{taskItemId:guid}/subtasks",
            "GET api/tasks/{taskItemId:guid}/checklist", "POST api/tasks/{taskItemId:guid}/checklist",
            "PATCH api/tasks/{taskItemId:guid}/checklist/{itemId:guid}", "DELETE api/tasks/{taskItemId:guid}/checklist/{itemId:guid}", "PUT api/tasks/{taskItemId:guid}/checklist/order",
            "GET api/tasks/{taskItemId:guid}/comments", "POST api/tasks/{taskItemId:guid}/comments",
            "PATCH api/task-comments/{commentId:guid}", "DELETE api/task-comments/{commentId:guid}",
            "GET api/tasks/{taskItemId:guid}/mention-candidates",
            "GET api/projects/{projectId:guid}/task-labels", "POST api/projects/{projectId:guid}/task-labels", "PATCH api/projects/{projectId:guid}/task-labels/{labelId:guid}",
            "POST api/projects/{projectId:guid}/task-labels/{labelId:guid}/archive", "POST api/projects/{projectId:guid}/task-labels/{labelId:guid}/restore",
            "PUT api/tasks/{taskItemId:guid}/labels/{labelId:guid}", "DELETE api/tasks/{taskItemId:guid}/labels/{labelId:guid}",
            "GET api/tasks/{taskItemId:guid}/watch-state", "PUT api/tasks/{taskItemId:guid}/watch", "DELETE api/tasks/{taskItemId:guid}/watch",
            "GET api/tasks/{taskItemId:guid}/activity",
            "GET api/tasks/{taskItemId:guid}/files", "POST api/tasks/{taskItemId:guid}/files", "DELETE api/tasks/{taskItemId:guid}/files/{associationId:guid}",
            "POST api/attachments/{attachmentId:guid}/download-grants", "POST api/attachment-download-grants/{fileDownloadGrantId:guid}/download"
        };
        var routes = app.GetHttpRoutes();
        Assert.All(expectedRoutes, route => Assert.Contains(route, routes));

        using (var unauthenticated = new HttpRequestMessage(HttpMethod.Get, $"/api/tasks/{data.TaskA.Id:D}"))
        {
            unauthenticated.Headers.TryAddWithoutValidation("X-Tenant-Slug", data.TenantA.Slug);
            var response = await app.Client.SendAsync(unauthenticated);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var detailResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}");
        var detailJson = await detailResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.DoesNotContain("storageKey", detailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filePath", detailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", detailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signedUrl", detailJson, StringComparison.OrdinalIgnoreCase);

        using var detail = JsonDocument.Parse(detailJson);
        var root = detail.RootElement;
        AssertDetailAggregateShape(root, data);
        var taskVersion = root.GetProperty("task").GetProperty("version").GetInt64();
        Assert.True(taskVersion > 0);

        using (var invalidUpdate = JsonContent("""{"title":"TaskA","description":null,"priority":1,"plannedStartDate":null,"plannedEndDate":null,"progressPercent":0}"""))
        {
            var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}", HttpMethod.Patch, invalidUpdate);
            await AssertTaskErrorAsync(response, HttpStatusCode.BadRequest, "TASK_INVALID_EXPECTED_VERSION");
        }

        using (var invalidChecklistOrder = JsonContent("""{"orderedItemIds":[],"expectedTaskVersion":0}"""))
        {
            var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/checklist/order", HttpMethod.Put, invalidChecklistOrder);
            await AssertTaskErrorAsync(response, HttpStatusCode.BadRequest, "TASK_INVALID_EXPECTED_VERSION");
        }

        using (var invalidFileAssociation = JsonContent("""{"attachmentId":"00000000-0000-0000-0000-000000000001","expectedVersion":-1}"""))
        {
            var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/files", HttpMethod.Post, invalidFileAssociation);
            await AssertTaskErrorAsync(response, HttpStatusCode.BadRequest, "TASK_INVALID_EXPECTED_VERSION");
        }

        using (var staleUpdate = JsonContent("""{"title":"TaskA","description":null,"priority":1,"plannedStartDate":null,"plannedEndDate":null,"progressPercent":0,"expectedVersion":99999}"""))
        {
            var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}", HttpMethod.Patch, staleUpdate);
            await AssertTaskErrorAsync(response, HttpStatusCode.Conflict, "TASK_STALE_VERSION");
        }

        using (var deniedUpdate = JsonContent($$"""{"title":"TaskA","description":null,"priority":1,"plannedStartDate":null,"plannedEndDate":null,"progressPercent":0,"expectedVersion":{{taskVersion}}}"""))
        {
            var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}", HttpMethod.Patch, deniedUpdate);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain(data.TaskA.Title, body, StringComparison.Ordinal);
        }

        var crossTenant = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, $"/api/tasks/{data.TaskB.Id:D}");
        var crossTenantBody = await crossTenant.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.DoesNotContain(data.TaskB.Title, crossTenantBody, StringComparison.Ordinal);
        Assert.DoesNotContain(data.FileB.StorageKey, crossTenantBody, StringComparison.Ordinal);

        for (var index = 0; index < 3; index++)
        {
            using var comment = JsonContent($$"""{"bodyPlainText":"contract-rate-{{index}}","isImportant":false}""");
            var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/comments", HttpMethod.Post, comment);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var rateLimitedComment = JsonContent("""{"bodyPlainText":"contract-rate-limited","isImportant":false}"""))
        {
            var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/comments", HttpMethod.Post, rateLimitedComment);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal((HttpStatusCode)429, response.StatusCode);
            Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfter));
            Assert.True(int.TryParse(Assert.Single(retryAfter), out var retryAfterSeconds) && retryAfterSeconds > 0);
            using var problem = JsonDocument.Parse(body);
            Assert.Equal("TASK_COMMENT_RATE_LIMITED", problem.RootElement.GetProperty("code").GetString());
            Assert.True(problem.RootElement.GetProperty("retryAfterSeconds").GetInt32() > 0);
            Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task TaskExecutionScopeHttpContractUsesStrictJsonAndTheManagerOnlySafeBoundary()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var projectPath = $"/api/projects/{data.ProjectA.Id:D}/execution-scope";
        var taskPath = $"/api/tasks/{data.TaskA.Id:D}/execution-scope";

        var expectedRoutes = new[]
        {
            "GET api/projects/{projectId:guid}/execution-scope",
            "PUT api/projects/{projectId:guid}/execution-scope",
            "GET api/tasks/{taskItemId:guid}/execution-scope",
            "PUT api/tasks/{taskItemId:guid}/execution-scope-override",
            "DELETE api/tasks/{taskItemId:guid}/execution-scope-override",
            "POST api/tasks/{taskItemId:guid}/execution-runs"
        };
        Assert.All(expectedRoutes, route => Assert.Contains(route, app.GetHttpRoutes()));

        using (var anonymous = new HttpRequestMessage(HttpMethod.Get, taskPath))
        {
            anonymous.Headers.TryAddWithoutValidation("X-Tenant-Slug", data.TenantA.Slug);
            using var response = await app.Client.SendAsync(anonymous);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var denied = await app.SendAsync(
                   data.TenantAMember,
                   data.TenantA.Slug,
                   projectPath,
                   HttpMethod.Put,
                   JsonContent("""{"webEnabled":true,"projectFilesEnabled":true,"expectedVersion":1}""")))
        {
            var body = await denied.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Contains("TASK_EXECUTION_NOT_FOUND", body, StringComparison.Ordinal);
            Assert.Contains("\"redactionApplied\":true", body, StringComparison.Ordinal);
            Assert.DoesNotContain(data.TaskA.Title, body, StringComparison.Ordinal);
        }

        using (var crossTenant = await app.SendAsync(
                   data.CrossTenantUser,
                   data.TenantA.Slug,
                   $"/api/tasks/{data.TaskB.Id:D}/execution-scope"))
        {
            var body = await crossTenant.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
            Assert.DoesNotContain(data.TaskB.Title, body, StringComparison.Ordinal);
            Assert.DoesNotContain(data.FileB.StorageKey, body, StringComparison.Ordinal);
        }

        using (var unknownMember = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   projectPath,
                   HttpMethod.Put,
                   JsonContent("""{"webEnabled":true,"projectFilesEnabled":false,"expectedVersion":1,"unapprovedSourceSelector":"never"}""")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownMember.StatusCode);
        }

        using (var managerUpdate = await app.SendAsync(
                   data.TenantAOwner,
                   data.TenantA.Slug,
                   projectPath,
                   HttpMethod.Put,
                   JsonContent("""{"webEnabled":true,"projectFilesEnabled":false,"expectedVersion":1}""")))
        using (var document = JsonDocument.Parse(await managerUpdate.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, managerUpdate.StatusCode);
            Assert.True(document.RootElement.GetProperty("policy").GetProperty("webEnabled").GetBoolean());
            Assert.False(document.RootElement.GetProperty("policy").GetProperty("projectFilesEnabled").GetBoolean());
            Assert.Equal(2, document.RootElement.GetProperty("version").GetInt64());
            Assert.True(document.RootElement.GetProperty("canManage").GetBoolean());
        }

        using (var inherited = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, taskPath))
        {
            var body = await inherited.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal(HttpStatusCode.OK, inherited.StatusCode);
            Assert.Equal("ProjectDefault", document.RootElement.GetProperty("origin").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("projectDefaultVersion").GetInt64());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("taskOverrideVersion").ValueKind);
            Assert.True(document.RootElement.GetProperty("effectivePolicy").GetProperty("webEnabled").GetBoolean());
            Assert.False(document.RootElement.GetProperty("effectivePolicy").GetProperty("projectFilesEnabled").GetBoolean());
            Assert.Equal("nextRun", document.RootElement.GetProperty("changesApplyTo").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("latestRun").ValueKind);
            Assert.DoesNotContain("storageKey", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("signedUrl", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Scope", "Issue369")]
    public async Task TaskActivityHttpContractIsIndependentBoundedStableAndFailClosed()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var occurredAt = new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero);
        var firstId = Guid.Parse("36900000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("36900000-0000-0000-0000-000000000002");
        var thirdId = Guid.Parse("36900000-0000-0000-0000-000000000003");
        await app.AddTaskActivityAsync(data.TenantA.Id, data.TenantA.Slug, data.TaskA, data.TenantAOwner.Id, firstId, ActivityLogType.Note, "first activity", occurredAt);
        await app.AddTaskActivityAsync(data.TenantA.Id, data.TenantA.Slug, data.TaskA, data.TenantAOwner.Id, secondId, ActivityLogType.Issue, "needs attention", occurredAt);
        await app.AddTaskActivityAsync(data.TenantA.Id, data.TenantA.Slug, data.TaskA, data.TenantAOwner.Id, thirdId, ActivityLogType.StatusUpdate, "status update", occurredAt);
        await app.AddTaskActivityAsync(data.TenantB.Id, data.TenantB.Slug, data.TaskB, data.CrossTenantUser.Id, Guid.NewGuid(), ActivityLogType.Decision, "tenant B secret", occurredAt.AddMinutes(1));

        using (var detailResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
            Assert.False(detail.RootElement.TryGetProperty("activity", out _));
            Assert.True(detail.RootElement.GetProperty("task").TryGetProperty("workflowStageName", out _));
            Assert.True(detail.RootElement.GetProperty("task").TryGetProperty("stageCategory", out _));
        }

        using (var pageOneResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/activity?page=1&pageSize=2"))
        {
            Assert.Equal(HttpStatusCode.OK, pageOneResponse.StatusCode);
            using var pageOne = JsonDocument.Parse(await pageOneResponse.Content.ReadAsStringAsync());
            var root = pageOne.RootElement;
            Assert.Equal(1, root.GetProperty("page").GetInt32());
            Assert.Equal(2, root.GetProperty("pageSize").GetInt32());
            Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
            Assert.True(root.GetProperty("hasMore").GetBoolean());
            var items = root.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal([thirdId, secondId], items.Select(item => item.GetProperty("id").GetGuid()));
            Assert.Equal("StatusUpdate", items[0].GetProperty("activityType").GetString());
            Assert.Equal(data.TenantAOwner.DisplayName, items[0].GetProperty("author").GetProperty("displayName").GetString());
        }

        using (var pageTwoResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/activity?page=2&pageSize=2"))
        {
            Assert.Equal(HttpStatusCode.OK, pageTwoResponse.StatusCode);
            using var pageTwo = JsonDocument.Parse(await pageTwoResponse.Content.ReadAsStringAsync());
            Assert.Equal(firstId, Assert.Single(pageTwo.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
            Assert.False(pageTwo.RootElement.GetProperty("hasMore").GetBoolean());
        }

        using (var unauthenticated = new HttpRequestMessage(HttpMethod.Get, $"/api/tasks/{data.TaskA.Id:D}/activity"))
        {
            unauthenticated.Headers.TryAddWithoutValidation("X-Tenant-Slug", data.TenantA.Slug);
            using var response = await app.Client.SendAsync(unauthenticated);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var crossTenantResponse = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskB.Id:D}/activity"))
        {
            var body = await crossTenantResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, crossTenantResponse.StatusCode);
            Assert.DoesNotContain("tenant B secret", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedTaskCommentAuthorReceivesSafeForbiddenForCanonicalUpdateAndDelete()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        Guid commentId;
        long commentVersion;

        using (var create = JsonContent("""{"bodyPlainText":"author comment","isImportant":false}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/comments", HttpMethod.Post, create))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            commentId = created.RootElement.GetProperty("id").GetGuid();
            commentVersion = created.RootElement.GetProperty("version").GetInt64();
        }

        await app.SetWorkspaceMembershipStatusAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.TenantAMember.Id,
            MembershipStatus.Suspended);

        using (var update = JsonContent($$"""{"bodyPlainText":"must not change","isImportant":true,"expectedVersion":{{commentVersion}}}"""))
        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/task-comments/{commentId:D}", HttpMethod.Patch, update))
        {
            await AssertTaskErrorAsync(response, HttpStatusCode.Forbidden, "TASK_COMMENT_FORBIDDEN");
        }

        using (var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, $"/api/task-comments/{commentId:D}?expectedVersion={commentVersion}", HttpMethod.Delete))
        {
            await AssertTaskErrorAsync(response, HttpStatusCode.Forbidden, "TASK_COMMENT_FORBIDDEN");
        }
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ImportantOnlyTaskCommentPatchIsRateLimitedWithRetryAfterAndNoPersistenceDelta()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        var comments = new List<TaskComment>();
        for (var index = 0; index < 4; index++)
        {
            comments.Add(await app.AddTaskCommentAsync(
                data.TenantA.Id,
                data.TenantA.Slug,
                data.TaskA,
                data.TenantAOwner.Id,
                $"important-only-{index}"));
        }

        foreach (var comment in comments.Take(3))
        {
            using var update = JsonContent($$"""{"bodyPlainText":null,"isImportant":true,"expectedVersion":{{comment.VersionNo}}}""");
            using var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/task-comments/{comment.Id:D}", HttpMethod.Patch, update);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var beforeTask = await app.GetTaskMutationStateAsync(data.TenantA.Id, data.TenantA.Slug, data.TaskA.Id);
        var beforeComment = await app.GetTaskCommentStateAsync(data.TenantA.Id, data.TenantA.Slug, comments[3].Id);
        using (var update = JsonContent($$"""{"bodyPlainText":null,"isImportant":true,"expectedVersion":{{comments[3].VersionNo}}}"""))
        using (var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/task-comments/{comments[3].Id:D}", HttpMethod.Patch, update))
        {
            Assert.Equal((HttpStatusCode)429, response.StatusCode);
            Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfter));
            Assert.True(int.TryParse(Assert.Single(retryAfter), out var retryAfterSeconds) && retryAfterSeconds >= 1);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("TASK_COMMENT_RATE_LIMITED", problem.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(beforeTask, await app.GetTaskMutationStateAsync(data.TenantA.Id, data.TenantA.Slug, data.TaskA.Id));
        Assert.Equal(beforeComment, await app.GetTaskCommentStateAsync(data.TenantA.Id, data.TenantA.Slug, comments[3].Id));
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedMemberIsExcludedFromMentionCandidatesAndDirectMentions()
    {
        await using var app = await HttpTenantIsolationTestApp.CreateAsync();
        var data = app.Data;
        Guid commentId;
        long commentVersion;

        using (var create = JsonContent("""{"bodyPlainText":"update target","isImportant":false}"""))
        using (var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/comments", HttpMethod.Post, create))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            commentId = created.RootElement.GetProperty("id").GetGuid();
            commentVersion = created.RootElement.GetProperty("version").GetInt64();
        }

        await app.SetWorkspaceMembershipStatusAsync(
            data.TenantA.Id,
            data.TenantA.Slug,
            data.WorkspaceA.Id,
            data.TenantAMember.Id,
            MembershipStatus.Suspended);

        using (var candidates = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/mention-candidates?query=TenantAMember"))
        {
            Assert.Equal(HttpStatusCode.OK, candidates.StatusCode);
            var body = await candidates.Content.ReadAsStringAsync();
            Assert.DoesNotContain(data.TenantAMember.DisplayName, body, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(0, json.RootElement.GetArrayLength());
        }

        var mentionBody = $"@{{{data.TenantAMember.Id:D}}}";
        using (var create = JsonContent($$"""{"bodyPlainText":"{{mentionBody}}","isImportant":false}"""))
        using (var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/tasks/{data.TaskA.Id:D}/comments", HttpMethod.Post, create))
        {
            await AssertTaskErrorAsync(response, HttpStatusCode.BadRequest, "TASK_MENTION_NOT_ELIGIBLE");
        }

        using (var update = JsonContent($$"""{"bodyPlainText":"please review {{mentionBody}}","isImportant":false,"expectedVersion":{{commentVersion}}}"""))
        using (var response = await app.SendAsync(data.TenantAOwner, data.TenantA.Slug, $"/api/task-comments/{commentId:D}", HttpMethod.Patch, update))
        {
            await AssertTaskErrorAsync(response, HttpStatusCode.BadRequest, "TASK_MENTION_NOT_ELIGIBLE");
        }
    }

    private static async Task AssertOkContainsOnlyAsync(
        HttpTenantIsolationTestApp app,
        User user,
        string tenantSlug,
        string path,
        string expected,
        string unexpected)
    {
        var response = await app.SendAsync(user, tenantSlug, path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected OK from {path}, got {response.StatusCode}: {body}");
        if (!string.IsNullOrEmpty(expected))
        {
            Assert.Contains(expected, body, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(unexpected, body, StringComparison.Ordinal);
    }

    private static async Task AssertWorkspaceProjectionUnavailableOnNonPostgreSqlAsync(
        HttpTenantIsolationTestApp app,
        User user,
        string tenantSlug,
        params string[] hiddenWorkspaceNames)
    {
        using var response = await app.SendAsync(user, tenantSlug, "/api/workspaces");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertCompleteErrorEnvelope(
            document.RootElement,
            StatusCodes.Status503ServiceUnavailable,
            "DependencyUnavailable",
            expectedTarget: null);
        Assert.All(hiddenWorkspaceNames, workspaceName =>
            Assert.DoesNotContain(workspaceName, body, StringComparison.Ordinal));
    }

    private static Task AssertBadRequestAsync(
        HttpTenantIsolationTestApp app,
        User user,
        string tenantSlug,
        string path,
        HttpMethod? method = null)
    {
        return AssertStatusAsync(app, user, tenantSlug, path, HttpStatusCode.BadRequest, method);
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> SendWorkspaceCreateAsync(
        HttpTenantIsolationTestApp app,
        User user,
        string tenantSlug,
        string? idempotencyKey,
        string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspaces");
        request.Headers.TryAddWithoutValidation("X-Test-User-Id", user.Id.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Test-Email", user.Email);
        request.Headers.TryAddWithoutValidation("X-Test-System-Role", user.SystemRole.ToString());
        request.Headers.TryAddWithoutValidation("X-Tenant-Slug", tenantSlug);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }
        request.Content = JsonContent(JsonSerializer.Serialize(new
        {
            name,
            description = (string?)null,
            icon = (string?)null
        }));
        return await app.Client.SendAsync(request);
    }

    private static void AssertDetailAggregateShape(JsonElement root, TenantIsolationTestData data)
    {
        var task = root.GetProperty("task");
        foreach (var field in new[] { "id", "tenantId", "workspaceId", "projectId", "kind", "parentTaskId", "title", "description", "workflowStageId", "workflowStageName", "stageCategory", "priority", "plannedStartDate", "plannedEndDate", "deadlineAt", "progressPercent", "progressIsDerived", "version", "reviewStatus", "subresources" })
            Assert.True(task.TryGetProperty(field, out _), $"Missing canonical task field '{field}'.");
        Assert.Equal(data.TaskA.Id, task.GetProperty("id").GetGuid());
        Assert.Equal(data.TenantA.Id, task.GetProperty("tenantId").GetGuid());
        Assert.Equal(data.ProjectA.Id, task.GetProperty("projectId").GetGuid());
        Assert.Equal(JsonValueKind.String, task.GetProperty("priority").ValueKind);
        Assert.Equal(JsonValueKind.Number, task.GetProperty("stageCategory").ValueKind);
        Assert.Equal(JsonValueKind.Number, task.GetProperty("reviewStatus").ValueKind);

        foreach (var field in new[] { "relationships", "permissions", "checklist", "labels", "watchState", "subtasks", "comments", "files" })
            Assert.True(root.TryGetProperty(field, out _), $"Missing task detail aggregate field '{field}'.");
        foreach (var field in new[] { "canCreateSubtask", "canCreateChecklistItem", "canUpdateChecklistItems", "canDeleteChecklistItems", "canReorderChecklist", "canCreateComment", "canMarkCommentImportant", "canApplyLabels", "canManageLabelDefinitions", "canAssociateFiles", "canRemoveFiles", "canChangeWatch" })
            Assert.True(root.GetProperty("permissions").TryGetProperty(field, out _), $"Missing task detail permission '{field}'.");
        foreach (var pageName in new[] { "subtasks", "comments", "files" })
        {
            var page = root.GetProperty(pageName);
            foreach (var field in new[] { "items", "page", "pageSize", "totalCount", "hasMore" })
                Assert.True(page.TryGetProperty(field, out _), $"Missing {pageName} page field '{field}'.");
        }
        Assert.Equal(50, root.GetProperty("subtasks").GetProperty("pageSize").GetInt32());
        Assert.Equal(20, root.GetProperty("comments").GetProperty("pageSize").GetInt32());
        Assert.Equal(20, root.GetProperty("files").GetProperty("pageSize").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("watchState").GetProperty("automaticSources").ValueKind);
    }

    private static async Task AssertTaskErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(document.RootElement.TryGetProperty("requestId", out _));
    }

    private static async Task AssertTaskNotificationPreferenceErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        long? currentVersion = null,
        bool redacted = false)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(code, root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(redacted, root.GetProperty("error").GetProperty("redactionApplied").GetBoolean());
        Assert.True(root.TryGetProperty("requestId", out _));
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(root.TryGetProperty("deadlineDigestLocalTime", out _));
        Assert.False(root.TryGetProperty("effectiveDeadlineDigestLocalTime", out _));

        if (currentVersion.HasValue)
        {
            Assert.Equal(currentVersion.Value, root.GetProperty("currentVersion").GetInt64());
            Assert.Equal($"\"{currentVersion.Value}\"", response.Headers.ETag?.Tag);
        }
    }

    private static async Task AssertTaskNotificationPreferenceStateAsync(
        HttpTenantIsolationTestApp app,
        TenantIsolationTestData data,
        string path,
        string? expectedLocalTime,
        long expectedVersion)
    {
        using var response = await app.SendAsync(data.TenantAMember, data.TenantA.Slug, path);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{expectedVersion}\"", response.Headers.ETag?.Tag);
        Assert.Equal(expectedVersion, document.RootElement.GetProperty("version").GetInt64());
        if (expectedLocalTime is null)
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("deadlineDigestLocalTime").ValueKind);
        }
        else
        {
            Assert.Equal(expectedLocalTime, document.RootElement.GetProperty("deadlineDigestLocalTime").GetString());
        }
    }

    private static async Task AssertMyTasksErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(document.RootElement.TryGetProperty("requestId", out _));
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertSafeModelBindingErrorAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currentVersion", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertSafeRejectedJsonContractAsync(
        HttpResponseMessage response,
        HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("status", out var responseStatus))
        {
            Assert.Equal((int)status, responseStatus.GetInt32());
        }
        Assert.True(
            document.RootElement.TryGetProperty("traceId", out _) ||
            document.RootElement.TryGetProperty("requestId", out _));
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currentVersion", body, StringComparison.OrdinalIgnoreCase);
    }

    private static Guid ReadResponseId(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task AssertConversationCreateRejectedWithoutLeakAsync(
        HttpTenantIsolationTestApp app,
        TenantIsolationTestData data,
        string json,
        string leakedValue)
    {
        using var content = JsonContent(json);
        var response = await app.SendAsync(data.CrossTenantUser, data.TenantA.Slug, "/api/conversations", HttpMethod.Post, content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(leakedValue, body, StringComparison.Ordinal);
        Assert.DoesNotContain(data.MessageA.Body, body, StringComparison.Ordinal);
        Assert.DoesNotContain(data.MessageB.Body, body, StringComparison.Ordinal);
    }

    private static async Task AssertStatusAsync(
        HttpTenantIsolationTestApp app,
        User user,
        string tenantSlug,
        string path,
        HttpStatusCode expectedStatus,
        HttpMethod? method = null)
    {
        var response = await app.SendAsync(user, tenantSlug, path, method);
        Assert.Equal(expectedStatus, response.StatusCode);
    }

    private sealed class HttpTenantIsolationTestApp : IAsyncDisposable
    {
        private HttpTenantIsolationTestApp(WebApplication app, HttpClient client, TenantIsolationTestData data)
        {
            App = app;
            Client = client;
            Data = data;
        }

        private WebApplication App { get; }
        public HttpClient Client { get; }
        public TenantIsolationTestData Data { get; }

        public static async Task<HttpTenantIsolationTestApp> CreateAsync(
            bool workspaceInitializationAvailable = false,
            bool enableCsrfProtection = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenancy:AppMode"] = "SaaS",
                ["Tenancy:TenantResolutionStrategy"] = "HeaderForDevelopmentOnly",
                ["Tenancy:AllowDevelopmentHeaderTenantResolution"] = "true",
                ["Tenancy:AllowDevelopmentHeaderInProduction"] = "false",
                ["Tenancy:DevelopmentTenantHeaderName"] = "X-Tenant-Slug",
                ["Security:CookieSecurePolicy"] = "SameAsRequest",
                ["Security:RequireHttps"] = "false",
                ["Security:EnableHsts"] = "false",
                ["Security:EnableCsrfProtection"] = enableCsrfProtection.ToString(),
                ["Security:EnableRateLimiting"] = "false",
                ["CommunicationSafety:MaxMessageLength"] = "120",
                ["CommunicationSafety:MaxAttachmentsPerMessage"] = "2",
                ["CommunicationSafety:MaxPostsPerMinutePerUser"] = "3",
                ["CommunicationSafety:MaxPostsPerMinutePerConversation"] = "30",
                ["CommunicationSafety:MaxThreadCreatesPerMinutePerUser"] = "3",
                ["CommunicationSafety:MaxReportsPerHourPerUser"] = "3",
                ["CommunicationSafety:DuplicatePostWindowSeconds"] = "60",
                ["FileStorage:Provider"] = "LocalFileSystem",
                ["FileStorage:RootPath"] = Path.Combine(Path.GetTempPath(), "coglatas-http-tenant-tests", Guid.NewGuid().ToString("N")),
                ["FileStorage:MaxFileSizeBytes"] = "10485760",
                ["FileStorage:AllowedExtensions:0"] = ".txt",
                ["FileStorage:AllowedContentTypes:0"] = "text/plain"
            });

            builder.Services
                .AddApplication()
                .AddWebServices(builder.Configuration);
            builder.Services.AddControllers().AddApplicationPart(typeof(WorkspacesController).Assembly);
            builder.Services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization();
            var databaseName = Guid.NewGuid().ToString("N");
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            AddInfrastructureLikeServices(builder.Services, builder.Configuration);
            if (workspaceInitializationAvailable)
            {
                builder.Services.AddScoped<IWorkspaceRequiredInitialization, NoopWorkspaceInitializationForCoordinatorTests>();
            }

            var app = builder.Build();
            app.UseMiddleware<GlobalExceptionHandlingMiddleware>();
            app.UseMiddleware<WpcApiContractMiddleware>();
            app.UseMiddleware<TenantResolutionMiddleware>();
            app.UseAuthentication();
            if (enableCsrfProtection)
            {
                app.Services.GetRequiredService<CsrfProtectionState>().MarkMiddlewareActive();
                app.UseMiddleware<CsrfProtectionMiddleware>();
            }
            app.UseAuthorization();
            app.MapControllers();

            await app.StartAsync();

            TenantIsolationTestData data;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
                data = await TenantIsolationTestData.SeedAsync(dbContext, currentTenant);
            }

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.Single() ?? throw new InvalidOperationException("Test server address was not available.");
            return new HttpTenantIsolationTestApp(app, new HttpClient { BaseAddress = new Uri(address) }, data);
        }

        public Task<HttpResponseMessage> SendAsync(User user, string tenantSlug, string path, HttpMethod? method = null, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("X-Test-User-Id", user.Id.ToString("D"));
            request.Headers.TryAddWithoutValidation("X-Test-Email", user.Email);
            request.Headers.TryAddWithoutValidation("X-Test-System-Role", user.SystemRole.ToString());
            request.Headers.TryAddWithoutValidation("X-Tenant-Slug", tenantSlug);
            request.Content = content;
            return Client.SendAsync(request);
        }

        public IReadOnlySet<string> GetHttpRoutes() => App.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public async Task<IReadOnlyList<AuditLog>> ListAuditLogsAsync(Guid tenantId, string tenantSlug)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.AuditLogs.AsNoTracking().ToListAsync();
        }

        public async Task<int> CountWorkspacesAsync(Guid tenantId, string tenantSlug, string name)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.Workspaces.CountAsync(item => item.Name == name);
        }

        public async Task<Guid> AddConversationThreadChainAsync(
            Guid tenantId,
            string tenantSlug,
            Guid rootConversationId,
            Guid workspaceId,
            Guid userId,
            int depth)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var parentId = rootConversationId;

            for (var level = 1; level <= depth; level++)
            {
                var conversation = new Conversation
                {
                    TenantId = tenantId,
                    WorkspaceId = workspaceId,
                    Type = ConversationType.Thread,
                    Title = $"Depth {level}",
                    ParentConversationId = parentId,
                    RootConversationId = rootConversationId,
                    CreatedByUserId = userId
                };
                dbContext.Conversations.Add(conversation);
                dbContext.ConversationMembers.Add(new ConversationMember
                {
                    TenantId = tenantId,
                    ConversationId = conversation.Id,
                    UserId = userId,
                    Role = ConversationMemberRole.Member,
                    CanRead = true,
                    CanPost = true,
                    CanCreateThread = true,
                    JoinedAt = DateTimeOffset.UtcNow
                });
                parentId = conversation.Id;
            }

            await dbContext.SaveChangesAsync();
            return parentId;
        }

        public async Task<(int Conversations, int SuccessAudits)> GetConversationThreadCreationCountsAsync(
            Guid tenantId,
            string tenantSlug)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (
                await dbContext.Conversations.CountAsync(),
                await dbContext.AuditLogs.CountAsync(log => log.Action == "ConversationThreadCreated"));
        }

        public async Task<(int Workspaces, int WorkspaceMembers, int Conversations, int ConversationMembers, int Audits, int Outbox, int Idempotency)>
            GetWorkspaceCreateCountsAsync(Guid tenantId, string tenantSlug)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (
                await dbContext.Workspaces.CountAsync(),
                await dbContext.WorkspaceMembers.CountAsync(),
                await dbContext.Conversations.CountAsync(),
                await dbContext.ConversationMembers.CountAsync(),
                await dbContext.AuditLogs.CountAsync(),
                await dbContext.OutboxEvents.CountAsync(),
                await dbContext.IdempotencyRecords.CountAsync());
        }

        public async Task BlockTaskAndAddArtifactAsync(
            Guid tenantId,
            string tenantSlug,
            Guid taskId,
            Guid actorUserId,
            string artifactName)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var task = await dbContext.TaskItems.SingleAsync(item => item.Id == taskId);
            task.IsBlocked = true;
            dbContext.Artifacts.Add(new Artifact
            {
                TenantId = tenantId,
                ProjectId = task.ProjectId,
                TaskItemId = task.Id,
                Name = artifactName,
                CreatedByUserId = actorUserId
            });
            await dbContext.SaveChangesAsync();
        }

        public async Task<PlanningProjectGraph> AddPlanningProjectGraphAsync(
                Guid tenantId,
                string tenantSlug,
                Guid workspaceId,
                Guid groupId,
                Guid ownerUserId,
                Guid relationshipUserId,
                IReadOnlyCollection<Guid> staleConversationMemberUserIds,
                ProjectStatus status = ProjectStatus.Planning)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = new Project
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                GroupId = groupId,
                OwnerUserId = ownerUserId,
                CreatedByUserId = ownerUserId,
                Name = $"WPC draft {Guid.NewGuid():N}",
                Slug = $"wpc-draft-{Guid.NewGuid():N}",
                Description = "WPC planning project",
                Status = status
            };
            var task = new TaskItem
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = project.Id,
                CreatedByUserId = ownerUserId,
                Title = "WPC draft task",
                PrimaryAssigneeUserId = relationshipUserId
            };
            var artifact = new Artifact
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                TaskItemId = task.Id,
                CreatedByUserId = ownerUserId,
                Name = "WPC draft artifact"
            };
            var activityLog = new ActivityLog
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                TaskItemId = task.Id,
                AuthorUserId = ownerUserId,
                ActivityType = ActivityLogType.Note,
                Body = "WPC draft activity",
                OccurredAt = DateTimeOffset.UtcNow
            };
            var comment = new Comment
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                AuthorUserId = ownerUserId,
                TargetType = CommentTargetType.Project,
                TargetId = project.Id,
                Body = "WPC draft comment"
            };
            var conversation = new Conversation
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = project.Id,
                Type = ConversationType.ProjectChannel,
                Title = "WPC protected draft conversation",
                CreatedByUserId = ownerUserId
            };
            var message = new Message
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ConversationId = conversation.Id,
                AuthorUserId = ownerUserId,
                Body = "WPC protected draft message"
            };
            dbContext.Projects.Add(project);
            dbContext.ProjectMembers.Add(new ProjectMember
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                UserId = ownerUserId,
                Role = ProjectRole.Owner,
                JoinedAt = DateTimeOffset.UtcNow
            });
            dbContext.TaskItems.Add(task);
            dbContext.Artifacts.Add(artifact);
            dbContext.ActivityLogs.Add(activityLog);
            dbContext.Comments.Add(comment);
            dbContext.Conversations.Add(conversation);
            foreach (var memberUserId in staleConversationMemberUserIds.Append(ownerUserId).Distinct())
            {
                dbContext.ConversationMembers.Add(new ConversationMember
                {
                    TenantId = tenantId,
                    ConversationId = conversation.Id,
                    UserId = memberUserId,
                    Role = memberUserId == ownerUserId
                        ? ConversationMemberRole.Admin
                        : ConversationMemberRole.Member,
                    CanRead = true,
                    CanPost = true,
                    CanCreateThread = true,
                    JoinedAt = DateTimeOffset.UtcNow
                });
            }
            dbContext.Messages.Add(message);
            await dbContext.SaveChangesAsync();
            return new PlanningProjectGraph(project, task, artifact, activityLog, comment, conversation, message);
        }

        public async Task<(ProjectStatus Status, DateTimeOffset? DeletedAt)> GetProjectLifecycleAsync(
            Guid tenantId,
            string tenantSlug,
            Guid projectId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = await dbContext.Projects
                .AsNoTracking()
                .SingleAsync(item => item.Id == projectId);
            return (project.Status, project.DeletedAt);
        }

        public async Task<Conversation> AddProjectBoundDirectConversationAsync(
            Guid tenantId,
            string tenantSlug,
            Guid workspaceId,
            Guid projectId,
            Guid userAId,
            Guid userBId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversation = new Conversation
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Type = ConversationType.DirectMessage,
                Title = "WPC protected Project-bound direct conversation",
                CreatedByUserId = userBId
            };
            dbContext.Conversations.Add(conversation);
            dbContext.ConversationMembers.AddRange(
                new ConversationMember
                {
                    TenantId = tenantId,
                    ConversationId = conversation.Id,
                    UserId = userAId,
                    Role = ConversationMemberRole.Member,
                    CanRead = true,
                    CanPost = true,
                    JoinedAt = DateTimeOffset.UtcNow
                },
                new ConversationMember
                {
                    TenantId = tenantId,
                    ConversationId = conversation.Id,
                    UserId = userBId,
                    Role = ConversationMemberRole.Admin,
                    CanRead = true,
                    CanPost = true,
                    JoinedAt = DateTimeOffset.UtcNow
                });
            await dbContext.SaveChangesAsync();
            return conversation;
        }

        public async Task<(Conversation Conversation, Message Message)> AddScopedConversationWithRootAsync(
            Guid tenantId,
            string tenantSlug,
            Guid workspaceId,
            Guid? projectId,
            ConversationType type,
            Guid createdByUserId,
            IReadOnlyCollection<Guid> participantUserIds,
            string body)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversation = new Conversation
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Type = type,
                Title = "Issue 362 scoped authorization fixture",
                CreatedByUserId = createdByUserId
            };
            var message = new Message
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                ConversationId = conversation.Id,
                AuthorUserId = createdByUserId,
                Body = body
            };
            dbContext.Conversations.Add(conversation);
            foreach (var participantUserId in participantUserIds.Distinct())
            {
                dbContext.ConversationMembers.Add(new ConversationMember
                {
                    TenantId = tenantId,
                    ConversationId = conversation.Id,
                    UserId = participantUserId,
                    Role = ConversationMemberRole.Member,
                    CanRead = true,
                    CanPost = true,
                    CanCreateThread = true,
                    JoinedAt = DateTimeOffset.UtcNow
                });
            }

            dbContext.Messages.Add(message);
            await dbContext.SaveChangesAsync();
            return (conversation, message);
        }

        public async Task<Conversation?> GetConversationAsync(Guid tenantId, string tenantSlug, Guid conversationId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.Conversations
                .AsNoTracking()
                .Include(conversation => conversation.Members)
                .FirstOrDefaultAsync(conversation => conversation.Id == conversationId);
        }

        public async Task<Message?> GetMessageAsync(Guid tenantId, string tenantSlug, Guid messageId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.Messages
                .AsNoTracking()
                .FirstOrDefaultAsync(message => message.Id == messageId);
        }

        public async Task<IReadOnlyList<OutboxEvent>> ListOutboxEventsAsync(Guid tenantId, string tenantSlug)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.OutboxEvents
                .AsNoTracking()
                .OrderBy(item => item.CreatedAt)
                .ToListAsync();
        }

        public async Task<(
            Guid OldestReplyId,
            Guid TombstoneReplyId,
            Guid WrongRootReplyId,
            Guid CrossConversationReplyId,
            Guid CrossTenantReplyId,
            string WrongRootBody,
            string CrossConversationBody,
            string CrossTenantBody)>
            SeedBoundedMessageThreadAsync(TenantIsolationTestData data)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetPlatformScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var baseTime = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
            var replies = Enumerable.Range(0, 101)
                .Select(index => new Message
                {
                    TenantId = data.TenantA.Id,
                    WorkspaceId = data.WorkspaceA.Id,
                    ConversationId = data.ConversationA.Id,
                    ThreadRootMessageId = data.MessageA.Id,
                    AuthorUserId = index % 2 == 0 ? data.TenantAMember.Id : data.CrossTenantUser.Id,
                    Body = $"bounded authorized reply {index}",
                    CreatedAt = baseTime.AddSeconds(index),
                    Version = 1
                })
                .ToList();
            var tombstone = replies[^1];
            tombstone.MarkDeleted(baseTime.AddMinutes(3), data.CrossTenantUser.Id, "author_delete");
            tombstone.Body = string.Empty;

            var otherConversation = new Conversation
            {
                TenantId = data.TenantA.Id,
                WorkspaceId = data.WorkspaceA.Id,
                Type = ConversationType.DirectMessage,
                Title = "Corrupt-link isolation fixture",
                CreatedByUserId = data.TenantAStaff.Id
            };
            const string crossConversationBody = "cross-conversation corrupt reply must never project";
            const string crossTenantBody = "cross-tenant corrupt reply must never project";
            const string wrongRootBody = "same-conversation wrong-root reply must never project";
            var otherRoot = new Message
            {
                TenantId = data.TenantA.Id,
                WorkspaceId = data.WorkspaceA.Id,
                ConversationId = data.ConversationA.Id,
                AuthorUserId = data.TenantAMember.Id,
                Body = "other bounded thread root",
                CreatedAt = baseTime.AddMinutes(8),
                Version = 1
            };
            var wrongRootReply = new Message
            {
                TenantId = data.TenantA.Id,
                WorkspaceId = data.WorkspaceA.Id,
                ConversationId = data.ConversationA.Id,
                ThreadRootMessageId = otherRoot.Id,
                AuthorUserId = data.TenantAMember.Id,
                Body = wrongRootBody,
                CreatedAt = baseTime.AddMinutes(9),
                Version = 1
            };
            var crossConversationReply = new Message
            {
                TenantId = data.TenantA.Id,
                WorkspaceId = data.WorkspaceA.Id,
                ConversationId = otherConversation.Id,
                ThreadRootMessageId = data.MessageA.Id,
                AuthorUserId = data.TenantAStaff.Id,
                Body = crossConversationBody,
                CreatedAt = baseTime.AddMinutes(10),
                Version = 1
            };
            var crossTenantReply = new Message
            {
                TenantId = data.TenantB.Id,
                WorkspaceId = data.WorkspaceB.Id,
                ConversationId = data.ConversationB.Id,
                ThreadRootMessageId = data.MessageA.Id,
                AuthorUserId = data.TenantBMember.Id,
                Body = crossTenantBody,
                CreatedAt = baseTime.AddMinutes(11),
                Version = 1
            };

            dbContext.Conversations.Add(otherConversation);
            dbContext.Messages.AddRange(replies);
            dbContext.Messages.AddRange(otherRoot, wrongRootReply, crossConversationReply, crossTenantReply);
            await dbContext.SaveChangesAsync();
            for (var index = 0; index < replies.Count; index++)
            {
                // AppDbContext stamps newly added audited rows with one current
                // time. Reapply the fixture ordering after insertion so the
                // oldest reply is deterministically outside the latest 100.
                replies[index].CreatedAt = baseTime.AddSeconds(index);
            }
            crossConversationReply.CreatedAt = baseTime.AddMinutes(10);
            crossTenantReply.CreatedAt = baseTime.AddMinutes(11);
            otherRoot.CreatedAt = baseTime.AddMinutes(8);
            wrongRootReply.CreatedAt = baseTime.AddMinutes(9);
            await dbContext.SaveChangesAsync();
            return (
                replies[0].Id,
                tombstone.Id,
                wrongRootReply.Id,
                crossConversationReply.Id,
                crossTenantReply.Id,
                wrongRootBody,
                crossConversationBody,
                crossTenantBody);
        }

        public async Task<(
            DateTimeOffset? DeadlineAt,
            DateOnly? PlannedEndDate,
            long Version,
            int TaskNotificationCount,
            int TaskOutboxCount)> GetTaskMutationStateAsync(
                Guid tenantId,
                string tenantSlug,
                Guid taskId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var task = await dbContext.TaskItems
                .AsNoTracking()
                .SingleAsync(item => item.Id == taskId);
            var notificationCount = await dbContext.Notifications
                .AsNoTracking()
                .CountAsync(item =>
                    item.RelatedEntityType == "TaskItem" &&
                    item.RelatedEntityId == taskId);
            var outboxCount = await dbContext.OutboxEvents
                .AsNoTracking()
                .CountAsync(item =>
                    item.AggregateType == "Task" &&
                    item.AggregateId == taskId);
            return (
                task.DeadlineAt,
                task.PlannedEndDate,
                task.VersionNo,
                notificationCount,
                outboxCount);
        }

        public async Task<TaskComment> AddTaskCommentAsync(
            Guid tenantId,
            string tenantSlug,
            TaskItem task,
            Guid authorUserId,
            string body)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comment = new TaskComment
            {
                TenantId = tenantId,
                WorkspaceId = task.WorkspaceId,
                ProjectId = task.ProjectId,
                TaskItemId = task.Id,
                AuthorUserId = authorUserId,
                BodyPlainText = body,
                CreatedAt = DateTimeOffset.UtcNow,
                VersionNo = 1
            };
            dbContext.TaskComments.Add(comment);
            await dbContext.SaveChangesAsync();
            return comment;
        }

        public async Task AddTaskActivityAsync(
            Guid tenantId,
            string tenantSlug,
            TaskItem task,
            Guid authorUserId,
            Guid activityId,
            ActivityLogType activityType,
            string body,
            DateTimeOffset occurredAt)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.ActivityLogs.Add(new ActivityLog
            {
                Id = activityId,
                TenantId = tenantId,
                ProjectId = task.ProjectId,
                TaskItemId = task.Id,
                AuthorUserId = authorUserId,
                ActivityType = activityType,
                Body = body,
                OccurredAt = occurredAt
            });
            await dbContext.SaveChangesAsync();
        }

        public async Task<(string Body, bool IsImportant, long Version, DateTimeOffset? UpdatedAt)> GetTaskCommentStateAsync(
            Guid tenantId,
            string tenantSlug,
            Guid commentId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.TaskComments.AsNoTracking()
                .Where(comment => comment.Id == commentId)
                .Select(comment => new ValueTuple<string, bool, long, DateTimeOffset?>(
                    comment.BodyPlainText,
                    comment.IsImportant,
                    comment.VersionNo,
                    comment.UpdatedAt))
                .SingleAsync();
        }

        public async Task<Guid> AddWorkspaceAsync(Guid tenantId, string tenantSlug, Guid userId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var workspace = new Workspace
            {
                TenantId = tenantId,
                Name = "WorkspaceA-ThreadMismatch",
                Slug = $"workspace-a-thread-mismatch-{Guid.NewGuid():N}",
                CreatedByUserId = userId,
                Status = WorkspaceStatus.Active
            };
            dbContext.Workspaces.Add(workspace);
            dbContext.WorkspaceMembers.Add(new WorkspaceMember
            {
                TenantId = tenantId,
                WorkspaceId = workspace.Id,
                UserId = userId,
                Role = WorkspaceRole.Owner,
                Status = MembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
            return workspace.Id;
        }

        public async Task SetWorkspaceMembershipStatusAsync(
            Guid tenantId,
            string tenantSlug,
            Guid workspaceId,
            Guid userId,
            MembershipStatus status)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var member = await dbContext.WorkspaceMembers
                .FirstAsync(item => item.WorkspaceId == workspaceId && item.UserId == userId);
            member.Status = status;
            await dbContext.SaveChangesAsync();
        }

        public async Task SetWorkspaceAvailabilityAsync(
            Guid tenantId,
            string tenantSlug,
            Guid workspaceId,
            WorkspaceStatus status,
            bool softDeleted)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var workspace = await dbContext.Workspaces.FirstAsync(item => item.Id == workspaceId);
            workspace.Status = status;
            if (softDeleted)
            {
                workspace.MarkDeleted(DateTimeOffset.UtcNow);
            }
            else
            {
                workspace.Restore();
            }

            await dbContext.SaveChangesAsync();
        }

        public async Task AddGroupMemberAsync(Guid tenantId, string tenantSlug, Guid groupId, Guid userId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await dbContext.GroupMembers.AnyAsync(item => item.GroupId == groupId && item.UserId == userId))
            {
                dbContext.GroupMembers.Add(new GroupMember
                {
                    TenantId = tenantId,
                    GroupId = groupId,
                    UserId = userId,
                    Role = GroupRole.Member,
                    JoinedAt = DateTimeOffset.UtcNow
                });
                await dbContext.SaveChangesAsync();
            }
        }

        public async Task UpdateConversationMemberAsync(Guid tenantId, string tenantSlug, Guid conversationId, Guid userId, Action<ConversationMember> update)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var member = await dbContext.ConversationMembers.FirstAsync(item => item.ConversationId == conversationId && item.UserId == userId);
            update(member);
            await dbContext.SaveChangesAsync();
        }

        public async Task UpdateConversationAsync(Guid tenantId, string tenantSlug, Guid conversationId, Action<Conversation> update)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var currentTenant = scope.ServiceProvider.GetRequiredService<CurrentTenantService>();
            currentTenant.SetTenant(tenantId, tenantSlug);
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversation = await dbContext.Conversations.FirstAsync(item => item.Id == conversationId);
            update(conversation);
            await dbContext.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }

        private static void AddInfrastructureLikeServices(IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<FileStorageOptions>(configuration.GetSection("FileStorage"));
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<ITenantRepository, TenantRepository>();
            services.AddScoped<ITenantExportRepository, TenantExportRepository>();
            services.AddScoped<IIntegrationRepository, IntegrationRepository>();
            services.AddScoped<Coglatas.Application.Admin.IAdminRepository, AdminRepository>();
            services.AddScoped<IInviteRepository, InviteRepository>();
            services.AddScoped<ISessionRepository, SessionRepository>();
            services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
            services.AddScoped<ITaskNotificationPreferenceRepository, TaskNotificationPreferenceRepository>();
            services.AddScoped<IGroupRepository, GroupRepository>();
            services.AddScoped<IChannelRepository, ChannelRepository>();
            services.AddScoped<IMessagingRepository, MessagingRepository>();
            services.AddScoped<IMessageFollowUpRepository, MessageFollowUpRepository>();
            services.AddScoped<IProjectRepository, ProjectRepository>();
            services.AddScoped<ITaskExecutionScopeRepository, TaskExecutionScopeRepository>();
            services.AddScoped<IEventRepository, EventRepository>();
            services.AddScoped<IFormRepository, FormRepository>();
            services.AddScoped<IFileRepository, FileRepository>();
            services.AddScoped<Coglatas.Application.Files.IFileAccessGrantRepository, FileAccessGrantRepository>();
            services.AddScoped<IFileDownloadGrantRepository, FileDownloadGrantRepository>();
            services.AddScoped<Coglatas.Application.Files.IFileSelectionSnapshotService, FileSelectionSnapshotService>();
            services.AddScoped<IStudentRecordExportGrantRepository, StudentRecordExportGrantRepository>();
            services.AddScoped<ITenantPlanRepository, TenantPlanRepository>();
            services.AddScoped<IArtifactRepository, ArtifactRepository>();
            services.AddScoped<IPlanningRepository, PlanningRepository>();
            services.AddScoped<IUiShellRepository, UiShellRepository>();
            services.AddScoped<IAnnouncementRepository, AnnouncementRepository>();
            services.AddScoped<Coglatas.Application.Realtime.IOutboxEventRepository, OutboxEventRepository>();
            services.AddScoped<Coglatas.Application.Realtime.ITransactionalOutbox, Coglatas.Application.Realtime.TransactionalOutbox>();
            services.AddScoped<IUnitOfWork, EfUnitOfWork>();
            services.AddScoped<ICreateIdempotencyCoordinator, EfCreateIdempotencyCoordinator>();
            services.AddScoped<IFileUploadPolicy, ConfiguredFileUploadPolicy>();
            services.AddScoped<IFileStorageService, InMemoryFileStorageService>();
            services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
            services.AddScoped<ITokenHasher, Sha256TokenHasher>();
            services.AddScoped<IAuditLogger, DbAuditLogger>();
            // The hosted fixture uses EF InMemory, while the production store is
            // intentionally a PostgreSQL sidecar and has dedicated PostgreSQL
            // coverage. Keep delivery enabled here so the real preference-aware
            // wrapper and pre-save Message path are still exercised end to end.
            services.AddScoped<IMessageNotificationPreferenceStore, EnabledMessageNotificationPreferenceStore>();
            services.AddScoped<DbNotificationService>();
            services.AddScoped<INotificationService, PreferenceAwareNotificationService>();
            services.AddScoped<CurrentAuthorizationTargetResolver>();
            services.AddScoped<INotificationTargetResolver>(provider => provider.GetRequiredService<CurrentAuthorizationTargetResolver>());
            services.AddScoped<INotificationOpenService, NotificationOpenService>();
            services.AddScoped<Coglatas.Application.Search.ISearchService, DbSearchService>();
            services.AddScoped<Coglatas.Application.Audit.IAuditQueryService, DbAuditQueryService>();
            services.AddSingleton<IClock, Coglatas.Infrastructure.Security.SystemClock>();
            services.AddScoped<IStudentRecordRepository, StudentRecordRepository>();
        }
    }

    private sealed class EnabledMessageNotificationPreferenceStore : IMessageNotificationPreferenceStore
    {
        public Task<bool?> GetEnabledAsync(
            Guid tenantId,
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<bool?>(true);

        public Task<bool> SetEnabledAsync(
            Guid tenantId,
            Guid userId,
            bool value,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class NoopWorkspaceInitializationForCoordinatorTests : IWorkspaceRequiredInitialization
    {
        public bool IsAvailable => true;

        public Task<Result> StageAsync(
            Workspace workspace,
            Guid creatorUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User-Id", out var userId) ||
                !Guid.TryParse(userId.ToString(), out var parsedUserId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var email = Request.Headers.TryGetValue("X-Test-Email", out var emailHeader)
                ? emailHeader.ToString()
                : "test@example.test";
            var systemRole = Request.Headers.TryGetValue("X-Test-System-Role", out var roleHeader)
                ? roleHeader.ToString()
                : SystemRole.User.ToString();

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, parsedUserId.ToString("D")),
                new(ClaimTypes.Email, email),
                new("system_role", systemRole),
                new(ClaimTypes.Role, systemRole)
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class InMemoryFileStorageService : IFileStorageService
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

        public async Task<Result> SaveAsync(string storageKey, Stream stream, string contentType, CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            files[storageKey] = memory.ToArray();
            return Result.Success();
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            files.TryGetValue(storageKey, out var bytes);
            return Task.FromResult<Stream>(new MemoryStream(bytes ?? "test file"u8.ToArray()));
        }

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            files.Remove(storageKey);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string?> CreateSignedReadUrlAsync(string storageKey, TimeSpan expiresIn, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
