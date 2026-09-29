using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.UiShell;

public sealed class UiShellService(
    IUiShellRepository uiShell,
    IProjectRepository projects,
    IArtifactRepository artifacts,
    IProjectAuthorizationService projectAuthorization,
    IWorkspaceAuthorizationService workspaceAuthorization,
    IFeatureFlagService featureFlags,
    ICurrentUser currentUser,
    IClock clock,
    IUnitOfWork unitOfWork) : IUiShellService
{
    public async Task<Result<IReadOnlyList<FeatureModuleResponse>>> ListModulesAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out _, out var role))
        {
            return Result<IReadOnlyList<FeatureModuleResponse>>.Failure("Authentication is required.");
        }

        var modules = await uiShell.ListModulesAsync(cancellationToken);
        var enabledFeatures = await GetEnabledFeatureSetAsync(cancellationToken);
        return Result<IReadOnlyList<FeatureModuleResponse>>.Success(modules
            .Where(module => module.IsEnabled && HasRole(role, module.RequiredRole) && IsFeatureVisible(module.Key, enabledFeatures))
            .OrderBy(module => module.SortOrder)
            .Select(ToModule)
            .ToList());
    }

    public async Task<Result<IReadOnlyList<PanelDefinitionResponse>>> ListPanelsAsync(string? moduleKey, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out _, out var role))
        {
            return Result<IReadOnlyList<PanelDefinitionResponse>>.Failure("Authentication is required.");
        }

        var panels = await uiShell.ListPanelsAsync(moduleKey, cancellationToken);
        var enabledFeatures = await GetEnabledFeatureSetAsync(cancellationToken);
        return Result<IReadOnlyList<PanelDefinitionResponse>>.Success(panels
            .Where(panel => panel.IsEnabled && panel.FeatureModule?.IsEnabled == true && HasRole(role, panel.FeatureModule.RequiredRole) && IsFeatureVisible(panel.FeatureModule.Key, enabledFeatures) && enabledFeatures.Contains(FeatureKeys.DockingLayout))
            .OrderBy(panel => panel.SortOrder)
            .Select(ToPanel)
            .ToList());
    }

    public async Task<Result<IReadOnlyList<UserLayoutResponse>>> ListLayoutsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId, out _))
        {
            return Result<IReadOnlyList<UserLayoutResponse>>.Failure("Authentication is required.");
        }

        return Result<IReadOnlyList<UserLayoutResponse>>.Success((await uiShell.ListLayoutsAsync(userId, cancellationToken)).Select(ToLayout).ToList());
    }

    public async Task<Result<UserLayoutResponse>> GetCurrentLayoutAsync(LayoutScopeType scopeType, Guid? scopeId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId, out _))
        {
            return Result<UserLayoutResponse>.Failure("Authentication is required.");
        }

        var layout = await uiShell.GetCurrentLayoutAsync(userId, scopeType, scopeId, cancellationToken);
        return layout is null
            ? Result<UserLayoutResponse>.Failure("Layout not found.")
            : Result<UserLayoutResponse>.Success(ToLayout(layout));
    }

    public async Task<Result<UserLayoutResponse>> CreateLayoutAsync(SaveUserLayoutRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId, out _))
        {
            return Result<UserLayoutResponse>.Failure("Authentication is required.");
        }

        var validation = await ValidateLayoutAsync(request, cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<UserLayoutResponse>.Failure(validation.Error!);
        }

        var now = clock.UtcNow;
        var layout = new UserLayout
        {
            UserId = userId,
            WorkspaceId = request.ScopeType == LayoutScopeType.Workspace ? request.ScopeId : null,
            ScopeType = request.ScopeType,
            ScopeId = request.ScopeId,
            Name = request.LayoutName.Trim(),
            LayoutJson = request.LayoutJson,
            CreatedAt = now,
            UpdatedAt = now
        };

        await uiShell.AddLayoutAsync(layout, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<UserLayoutResponse>.Success(ToLayout(layout));
    }

    public async Task<Result<UserLayoutResponse>> UpdateLayoutAsync(Guid layoutId, SaveUserLayoutRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId, out _))
        {
            return Result<UserLayoutResponse>.Failure("Authentication is required.");
        }

        var layout = await uiShell.GetLayoutAsync(layoutId, cancellationToken);
        if (layout is null || layout.UserId != userId)
        {
            return Result<UserLayoutResponse>.Failure("Layout not found.");
        }

        var validation = await ValidateLayoutAsync(request, cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<UserLayoutResponse>.Failure(validation.Error!);
        }

        layout.WorkspaceId = request.ScopeType == LayoutScopeType.Workspace ? request.ScopeId : null;
        layout.ScopeType = request.ScopeType;
        layout.ScopeId = request.ScopeId;
        layout.Name = request.LayoutName.Trim();
        layout.LayoutJson = request.LayoutJson;
        layout.UpdatedAt = clock.UtcNow;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<UserLayoutResponse>.Success(ToLayout(layout));
    }

    public async Task<Result> DeleteLayoutAsync(Guid layoutId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId, out _))
        {
            return Result.Failure("Authentication is required.");
        }

        var layout = await uiShell.GetLayoutAsync(layoutId, cancellationToken);
        if (layout is null || layout.UserId != userId)
        {
            return Result.Failure("Layout not found.");
        }

        uiShell.RemoveLayout(layout);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<CommandDefinitionResponse>>> ListCommandsAsync(CommandContextType? contextType, Guid? contextId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId, out var role) || !await CanAccessContextAsync(userId, contextType, contextId, cancellationToken))
        {
            return Result<IReadOnlyList<CommandDefinitionResponse>>.Failure("Context not found.");
        }

        var commands = await uiShell.ListCommandsAsync(contextType, cancellationToken);
        var enabledFeatures = await GetEnabledFeatureSetAsync(cancellationToken);
        return Result<IReadOnlyList<CommandDefinitionResponse>>.Success(commands
            .Where(command => command.IsEnabled &&
                (command.FeatureModule is null || (HasRole(role, command.FeatureModule.RequiredRole) && IsFeatureVisible(command.FeatureModule.Key, enabledFeatures))))
            .OrderBy(command => command.SortOrder)
            .Select(ToCommand)
            .ToList());
    }

    public async Task<Result<RadialMenuResponse>> GetRadialMenuAsync(CommandContextType contextType, Guid? contextId, CancellationToken cancellationToken = default)
    {
        var commands = await ListCommandsAsync(contextType, contextId, cancellationToken);
        if (!commands.IsSuccess || !TryCurrentUser(out _, out _))
        {
            return Result<RadialMenuResponse>.Failure(commands.Error ?? "Context not found.");
        }

        var profile = await uiShell.GetDefaultRadialMenuAsync(contextType, cancellationToken);
        if (profile is null || !await featureFlags.IsEnabledAsync(FeatureKeys.RadialMenu, cancellationToken))
        {
            return Result<RadialMenuResponse>.Failure("Radial menu not found.");
        }

        var commandKeys = commands.Value!.Select(command => command.CommandKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = profile.Items
            .OrderBy(item => item.SortOrder)
            .Where(item => commandKeys.Contains(item.CommandKey))
            .Select(item => new RadialMenuItemResponse(item.Direction, item.CommandKey, item.Label, item.Icon, true, item.SortOrder))
            .ToList();

        return Result<RadialMenuResponse>.Success(new RadialMenuResponse(profile.ProfileKey, profile.Name, profile.ContextType, items));
    }

    private async Task<Result> ValidateLayoutAsync(SaveUserLayoutRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.LayoutName))
        {
            return Result.Failure("Layout name is required.");
        }

        using var document = TryParseJson(request.LayoutJson, out var parseError);
        if (document is null)
        {
            return Result.Failure(parseError!);
        }

        var panelKeys = await uiShell.GetPanelKeysAsync(cancellationToken);
        foreach (var panelKey in EnumeratePanelKeys(document.RootElement))
        {
            if (!panelKeys.Contains(panelKey))
            {
                return Result.Failure($"Unknown panel key '{panelKey}'.");
            }
        }

        if (request.ScopeType == LayoutScopeType.Workspace && request.ScopeId.HasValue && TryCurrentUser(out var userId, out _) &&
            !await workspaceAuthorization.CanViewWorkspace(userId, request.ScopeId.Value, cancellationToken))
        {
            return Result.Failure("Workspace not found.");
        }

        return Result.Success();
    }

    private static JsonDocument? TryParseJson(string json, out string? error)
    {
        try
        {
            error = null;
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            error = $"LayoutJson must be valid JSON: {exception.Message}";
            return null;
        }
    }

    private static IEnumerable<string> EnumeratePanelKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("panelKey") && property.Value.ValueKind == JsonValueKind.String)
                {
                    yield return property.Value.GetString()!;
                }

                foreach (var nested in EnumeratePanelKeys(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePanelKeys(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private async Task<bool> CanAccessContextAsync(Guid userId, CommandContextType? contextType, Guid? contextId, CancellationToken cancellationToken)
    {
        if (contextType is null or CommandContextType.Global)
        {
            return true;
        }

        if (!contextId.HasValue)
        {
            return false;
        }

        if (contextType == CommandContextType.Workspace)
        {
            return await workspaceAuthorization.CanViewWorkspace(userId, contextId.Value, cancellationToken);
        }

        if (contextType == CommandContextType.Project)
        {
            return await projectAuthorization.CanViewProject(userId, contextId.Value, cancellationToken);
        }

        if (contextType == CommandContextType.TaskItem)
        {
            var task = await projects.GetTaskAsync(contextId.Value, cancellationToken);
            return task is not null && await projectAuthorization.CanViewProject(userId, task.ProjectId, cancellationToken);
        }

        if (contextType == CommandContextType.Artifact)
        {
            var artifact = await artifacts.GetArtifactAsync(contextId.Value, cancellationToken);
            return artifact is not null && await projectAuthorization.CanViewProject(userId, artifact.ProjectId, cancellationToken);
        }

        return true;
    }

    private static bool HasRole(SystemRole? actual, SystemRole? required)
    {
        return required is null || (actual.HasValue && actual.Value >= required.Value);
    }

    private async Task<HashSet<string>> GetEnabledFeatureSetAsync(CancellationToken cancellationToken)
    {
        var enabled = FeatureKeys.All.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (currentUser.IsAuthenticated)
        {
            foreach (var feature in FeatureKeys.All)
            {
                if (!await featureFlags.IsEnabledAsync(feature, cancellationToken))
                {
                    enabled.Remove(feature);
                }
            }
        }

        return enabled;
    }

    private static bool IsFeatureVisible(string featureKey, HashSet<string> enabledFeatures)
    {
        return !FeatureKeys.All.Contains(featureKey, StringComparer.OrdinalIgnoreCase) || enabledFeatures.Contains(featureKey);
    }

    private bool TryCurrentUser(out Guid userId, out SystemRole? role)
    {
        userId = currentUser.UserId ?? Guid.Empty;
        role = currentUser.SystemRole;
        return currentUser.IsAuthenticated && currentUser.UserId.HasValue;
    }

    private static FeatureModuleResponse ToModule(FeatureModule module)
    {
        return new FeatureModuleResponse(module.Id, module.Key, module.Name, module.Description, module.DefaultRoute, module.Icon, module.SortOrder);
    }

    private static PanelDefinitionResponse ToPanel(PanelDefinition panel)
    {
        return new PanelDefinitionResponse(panel.Id, panel.Key, panel.Name, panel.FeatureModule?.Key ?? string.Empty, panel.DefaultWidth, panel.DefaultHeight, panel.DefaultPosition, panel.IsDockable, panel.IsClosable, panel.SortOrder);
    }

    private static UserLayoutResponse ToLayout(UserLayout layout)
    {
        return new UserLayoutResponse(layout.Id, layout.Name, layout.ScopeType, layout.ScopeId, layout.LayoutJson, layout.CreatedAt, layout.UpdatedAt);
    }

    private static CommandDefinitionResponse ToCommand(CommandDefinition command)
    {
        return new CommandDefinitionResponse(command.Id, command.Key, command.Name, command.Icon, command.FeatureModule?.Key, command.ActionType, command.Route, command.ContextType, command.SortOrder);
    }
}
