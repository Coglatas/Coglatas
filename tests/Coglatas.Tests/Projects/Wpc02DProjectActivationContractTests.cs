using System.Text.Json;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Web.Models;

namespace Coglatas.Tests.Projects;

[Trait("Scope", "WPC02D")]
public sealed class Wpc02DProjectActivationContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ActivationRequestRequiresExpectedVersionAndRejectsUnknownFields()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ActivateProjectRequest>("{}", WebJson));

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ActivateProjectRequest>(
                """{"expectedVersion":1,"workspaceId":"00000000-0000-0000-0000-000000000001"}""",
                WebJson));

        var request = JsonSerializer.Deserialize<ActivateProjectRequest>(
            """{"expectedVersion":7}""",
            WebJson);
        Assert.NotNull(request);
        Assert.Equal(7, request.ExpectedVersion);
    }

    [Fact]
    public void WpcEnvelopeClassifierRecognizesOnlyCanonicalProjectActivationShape()
    {
        var projectId = Guid.NewGuid();

        Assert.True(ApiEnvelope.IsWorkspaceCreationPath($"/api/projects/{projectId}/activate"));
        Assert.True(ApiEnvelope.IsWorkspaceCreationPath($"/api/projects/{projectId}/activate/"));
        Assert.False(ApiEnvelope.IsWorkspaceCreationPath("/api/projects/not-a-guid/activate"));
        Assert.False(ApiEnvelope.IsWorkspaceCreationPath($"/api/projects/{projectId}/activate/extra"));
        Assert.False(ApiEnvelope.IsWorkspaceCreationPath($"/api/projects/{projectId}/active"));
    }

    [Fact]
    public async Task ResolverUsesWorkspaceThenTenantThenCanonicalFallbackPrecedence()
    {
        var project = NewProject();
        var workspace = Workflow("workspace-template", "Workspace Template");
        var tenant = Workflow("tenant-default", "Tenant Default");

        var workspaceResult = await new ProjectTaskWorkflowResolver(
            new ConfiguredSource(workspace, tenant)).ResolveAsync(project);
        Assert.True(workspaceResult.IsSuccess, workspaceResult.Error);
        Assert.Equal("workspace-template", workspaceResult.Value!.SourceIdentity);

        var tenantResult = await new ProjectTaskWorkflowResolver(
            new ConfiguredSource(null, tenant)).ResolveAsync(project);
        Assert.True(tenantResult.IsSuccess, tenantResult.Error);
        Assert.Equal("tenant-default", tenantResult.Value!.SourceIdentity);

        var fallbackResult = await new ProjectTaskWorkflowResolver(
            new NoConfiguredProjectTaskWorkflowSource()).ResolveAsync(project);
        Assert.True(fallbackResult.IsSuccess, fallbackResult.Error);
        Assert.Equal(ProjectTaskWorkflowResolver.CanonicalFallbackIdentity, fallbackResult.Value!.SourceIdentity);
        Assert.Equal(6, fallbackResult.Value.Stages.Count);
        Assert.Single(fallbackResult.Value.Stages, stage => stage.IsInitialStage);
        Assert.Equal(2, fallbackResult.Value.Stages.Count(stage => stage.IsTerminalStage));
    }

    [Fact]
    public async Task ResolverFailsClosedOnInvalidConfiguredWorkflow()
    {
        var invalid = new ProjectActivationTaskWorkflow(
            "workspace-template",
            "Workspace Template",
            true,
            [
                new("Duplicate", TaskStageCategory.Todo, 1000, null, true, false),
                new("Duplicate", TaskStageCategory.Done, 1000, null, false, true)
            ]);

        var result = await new ProjectTaskWorkflowResolver(
            new ConfiguredSource(invalid, null)).ResolveAsync(NewProject());

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidTaskWorkflow", result.ErrorDetail?.Code);
    }

    private static Project NewProject() => new()
    {
        TenantId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        OwnerUserId = Guid.NewGuid(),
        Name = "Activation contract",
        Slug = "activation-contract",
        Status = ProjectStatus.Planning,
        Visibility = ProjectVisibility.MembersOnly,
        ActivationState = ProjectActivationState.NeverActivated,
        VersionNo = 1,
        CreatedByUserId = Guid.NewGuid()
    };

    private static ProjectActivationTaskWorkflow Workflow(string source, string name) => new(
        source,
        name,
        true,
        [
            new("Todo", TaskStageCategory.Todo, 1000, null, true, false),
            new("Done", TaskStageCategory.Done, 2000, null, false, true)
        ]);

    private sealed class ConfiguredSource(
        ProjectActivationTaskWorkflow? workspace,
        ProjectActivationTaskWorkflow? tenant) : IConfiguredProjectTaskWorkflowSource
    {
        public Task<ProjectActivationTaskWorkflow?> FindWorkspaceTemplateAsync(
            Project project,
            CancellationToken cancellationToken = default) => Task.FromResult(workspace);

        public Task<ProjectActivationTaskWorkflow?> FindTenantDefaultAsync(
            Project project,
            CancellationToken cancellationToken = default) => Task.FromResult(tenant);
    }
}
