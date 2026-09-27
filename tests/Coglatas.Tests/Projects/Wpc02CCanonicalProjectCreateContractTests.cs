using System.Reflection;
using System.Text.Json;
using Coglatas.Application.Projects;
using Coglatas.Domain.Enums;
using Coglatas.Web.Controllers;
using Coglatas.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Tests.Projects;

[Trait("Scope", "WPC02C")]
public sealed class Wpc02CCanonicalProjectCreateContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RequestRejectsBodyWorkspaceScopeOverride()
    {
        var workspaceId = Guid.NewGuid();
        var payload = $$"""
        {
          "title": "Canonical Project",
          "workspaceId": "{{workspaceId}}"
        }
        """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CanonicalCreateProjectRequest>(payload, WebJson));
    }

    [Fact]
    public void RequestKeepsGroupAndVisibilityOptional()
    {
        const string payload = """
        {
          "title": "Canonical Project"
        }
        """;

        var request = JsonSerializer.Deserialize<CanonicalCreateProjectRequest>(payload, WebJson);

        Assert.NotNull(request);
        Assert.Equal("Canonical Project", request.Title);
        Assert.Null(request.GroupId);
        Assert.Null(request.Visibility);
    }

    [Fact]
    public void WpcEnvelopeClassifierRecognizesOnlyCanonicalWorkspaceScopedProjectCreateShape()
    {
        var workspaceId = Guid.NewGuid();

        Assert.True(ApiEnvelope.IsWorkspaceCreationPath($"/api/workspaces/{workspaceId}/projects"));
        Assert.True(ApiEnvelope.IsWorkspaceCreationPath($"/api/workspaces/{workspaceId}/projects/"));
        Assert.True(ApiEnvelope.IsWorkspaceCreationPath($"/api/workspaces/{workspaceId}/projects/create-options"));
        Assert.True(ApiEnvelope.IsWorkspaceCreationPath($"/api/workspaces/{workspaceId}/projects/create-options/"));
        Assert.False(ApiEnvelope.IsWorkspaceCreationPath("/api/workspaces/not-a-guid/projects"));
        Assert.False(ApiEnvelope.IsWorkspaceCreationPath("/api/workspaces/not-a-guid/projects/create-options"));
        Assert.False(ApiEnvelope.IsWorkspaceCreationPath($"/api/workspaces/{workspaceId}/projects/extra"));
        Assert.False(ApiEnvelope.IsWorkspaceCreationPath($"/api/workspaces/{workspaceId}/project"));
    }

    [Fact]
    public void ControllerPinsCanonicalRouteAuthorizationAndIdempotencyHeader()
    {
        var controllerType = typeof(WorkspaceProjectsController);
        Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>());

        var create = controllerType.GetMethod(nameof(WorkspaceProjectsController.Create));
        Assert.NotNull(create);

        var post = Assert.Single(create.GetCustomAttributes<HttpPostAttribute>());
        Assert.Equal("api/workspaces/{workspaceId:guid}/projects", post.Template);

        var parameters = create.GetParameters();
        Assert.Contains(parameters, parameter => parameter.Name == "workspaceId" && parameter.ParameterType == typeof(Guid));
        var idempotencyParameter = Assert.Single(parameters, parameter => parameter.Name == "idempotencyKey");
        var fromHeader = Assert.Single(idempotencyParameter.GetCustomAttributes<FromHeaderAttribute>());
        Assert.Equal("Idempotency-Key", fromHeader.Name);

        var createOptions = controllerType.GetMethod(nameof(WorkspaceProjectsController.GetCreateOptions));
        Assert.NotNull(createOptions);
        var get = Assert.Single(createOptions.GetCustomAttributes<HttpGetAttribute>());
        Assert.Equal("api/workspaces/{workspaceId:guid}/projects/create-options", get.Template);
    }

    [Fact]
    public void CreateOptionsContractUsesNumericVisibilityAndMinimalGroupShape()
    {
        var workspaceId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var response = new ProjectCreateOptionsResponse(
            workspaceId,
            true,
            [ProjectVisibility.WorkspaceVisible, ProjectVisibility.MembersOnly, ProjectVisibility.Restricted],
            [new ProjectCreateGroupOptionResponse(groupId, "Createable Group")]);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WebJson));
        var root = document.RootElement;
        Assert.Equal(workspaceId, root.GetProperty("workspaceId").GetGuid());
        Assert.True(root.GetProperty("canCreateUngrouped").GetBoolean());
        Assert.Equal(new[] { 0, 1, 2 }, root.GetProperty("allowedVisibilities").EnumerateArray().Select(item => item.GetInt32()));
        var group = Assert.Single(root.GetProperty("groups").EnumerateArray());
        Assert.Equal(groupId, group.GetProperty("id").GetGuid());
        Assert.Equal("Createable Group", group.GetProperty("name").GetString());
        Assert.Equal(2, group.EnumerateObject().Count());
    }
}
