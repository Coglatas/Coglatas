using System.Security.Cryptography;
using System.Text;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Workspaces;

public sealed class WorkspaceService(
    IWorkspaceRepository workspaces,
    IUserRepository users,
    IWorkspaceAuthorizationService authorization,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork,
    ICurrentTenant? currentTenant = null,
    IAuthorizationStateChangePublisher? authorizationChanges = null,
    ICreateIdempotencyCoordinator? createIdempotency = null,
    IWorkspaceRequiredInitialization? requiredInitialization = null,
    IWorkspaceGeneralMembershipSynchronizer? generalMemberships = null,
    IWorkspaceDashboardQuery? dashboardQuery = null) : IWorkspaceService
{
    private const string WorkspaceCreateOperation = "Workspace.Create.v1";

    public async Task<Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>.Failure(AuthenticationRequiredError());
        }

        if (currentTenant is not { IsAvailable: true, IsPlatformScope: false })
        {
            return Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>.Failure(new ApplicationErrorDetail(
                "TenantMembershipRequired",
                "An active Tenant membership is required."));
        }

        if (dashboardQuery is not { IsAvailable: true })
        {
            return Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>.Failure(new ApplicationErrorDetail(
                "DependencyUnavailable",
                "Workspace dashboard projection is temporarily unavailable."));
        }

        var items = await dashboardQuery.ListAsync(userId, cancellationToken);
        return Result<IReadOnlyList<WorkspaceDashboardListItemResponse>>.Success(items);
    }

    public async Task<Result<IReadOnlyList<WorkspaceListItemResponse>>> ListArchivedAsync(CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<IReadOnlyList<WorkspaceListItemResponse>>.Failure(new ApplicationErrorDetail(
                "AuthenticationRequired",
                "Authentication is required."));
        }

        // Archived history never uses the SystemAdmin include-all shortcut.
        // The repository's normal user scope requires a current active
        // Workspace membership, which is the canonical historical boundary.
        var items = await workspaces.ListForUserAsync(userId, includeAll: false, cancellationToken);
        return Result<IReadOnlyList<WorkspaceListItemResponse>>.Success(items
            .Where(workspace => !workspace.DeletedAt.HasValue && workspace.Status == WorkspaceStatus.Archived)
            .Select(ToListItem)
            .ToList());
    }

    public async Task<Result<WorkspaceCapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<WorkspaceCapabilitiesResponse>.Failure(new ApplicationErrorDetail(
                "AuthenticationRequired",
                "Authentication is required."));
        }

        if (currentTenant is not { IsAvailable: true, IsPlatformScope: false })
        {
            return Result<WorkspaceCapabilitiesResponse>.Failure(new ApplicationErrorDetail(
                "TenantMembershipRequired",
                "An active Tenant membership is required."));
        }

        var canCreate = requiredInitialization is { IsAvailable: true } &&
                        await authorization.CanCreateWorkspace(userId, currentTenant.TenantId, cancellationToken);
        return Result<WorkspaceCapabilitiesResponse>.Success(new WorkspaceCapabilitiesResponse(canCreate));
    }

    public async Task<Result<WorkspaceDetailResponse>> CreateAsync(
        CreateWorkspaceRequest request,
        string? clientRequestIdentity,
        CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "AuthenticationRequired",
                "Authentication is required."));
        }

        if (currentTenant is not { IsAvailable: true, IsPlatformScope: false })
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "TenantMembershipRequired",
                "An active Tenant membership is required."));
        }

        if (!await authorization.CanCreateWorkspace(userId, currentTenant.TenantId, cancellationToken))
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "CapabilityDenied",
                "You are not allowed to create workspaces."));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationFailure("Workspace name is required.", "body.name");
        }

        var name = request.Name.Trim();
        var description = NormalizeOptional(request.Description);
        var icon = NormalizeOptional(request.Icon);
        if (name.Length > 160)
        {
            return ValidationFailure("Workspace name must not exceed 160 characters.", "body.name");
        }
        if (description?.Length > 2000)
        {
            return ValidationFailure("Workspace description must not exceed 2000 characters.", "body.description");
        }
        if (icon?.Length > 120)
        {
            return ValidationFailure("Workspace icon must not exceed 120 characters.", "body.icon");
        }
        if (clientRequestIdentity is null)
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "MissingIdempotencyKey",
                "An Idempotency-Key header is required.",
                Target: "header.Idempotency-Key"));
        }
        if (!IsValidClientRequestIdentity(clientRequestIdentity))
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "InvalidIdempotencyKey",
                "The Idempotency-Key header is invalid.",
                Target: "header.Idempotency-Key"));
        }
        if (createIdempotency is null)
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "DependencyUnavailable",
                "Workspace creation is temporarily unavailable."));
        }
        if (authorizationChanges is null)
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "DependencyUnavailable",
                "Workspace creation is temporarily unavailable."));
        }
        if (requiredInitialization is not { IsAvailable: true })
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "DependencyUnavailable",
                "Workspace creation is temporarily unavailable."));
        }

        var workspace = new Workspace
        {
            Name = name,
            Description = description,
            Icon = icon,
            Status = WorkspaceStatus.Active,
            CreatedByUserId = userId
        };
        workspace.Slug = CreateWorkspaceSlug(name, workspace.Id);

        IdempotentCreateResult<Workspace> idempotency;
        try
        {
            idempotency = await createIdempotency.ExecuteAsync(
                new CreateIdempotencyContext(
                    currentTenant.TenantId,
                    userId,
                    WorkspaceCreateOperation,
                    clientRequestIdentity,
                    CreateRequestFingerprint(name, description, icon),
                    "Workspace",
                    workspace.Id),
                async token =>
                {
                    await workspaces.AddAsync(workspace, token);
                    await workspaces.AddMemberAsync(new WorkspaceMember
                    {
                        WorkspaceId = workspace.Id,
                        UserId = userId,
                        Role = WorkspaceRole.Owner,
                        Status = MembershipStatus.Active,
                        JoinedAt = clock.UtcNow
                    }, token);
                    var initialization = await requiredInitialization.StageAsync(workspace, userId, token);
                    if (!initialization.IsSuccess)
                    {
                        throw new WorkspaceRequiredInitializationException();
                    }
                    await AuditAsync(userId, "WorkspaceCreated", workspace.Id, token);
                    await PublishAuthorizationChangeAsync(userId, workspace.Id, "granted", token);
                    return workspace;
                },
                async (resourceId, token) =>
                {
                    // A create replay must re-establish the creator's current
                    // record-level access. The broader Workspace viewer policy
                    // may include an active-workspace SystemAdmin shortcut, but
                    // a platform role alone never recovers create metadata.
                    var membership = await workspaces.GetMemberAsync(resourceId, userId, token);
                    if (membership is not { Status: MembershipStatus.Active })
                    {
                        return null;
                    }

                    return await workspaces.GetByIdAsync(resourceId, token);
                },
                cancellationToken);
        }
        catch (WorkspaceRequiredInitializationException)
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "DependencyUnavailable",
                "Workspace creation is temporarily unavailable."));
        }
        catch (RequiredOutboxStagingException)
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "DependencyUnavailable",
                "Workspace creation is temporarily unavailable."));
        }

        return idempotency.Disposition switch
        {
            IdempotentCreateDisposition.Created or IdempotentCreateDisposition.Replayed when idempotency.Value is not null =>
                Result<WorkspaceDetailResponse>.Success(ToDetail(idempotency.Value)),
            IdempotentCreateDisposition.RequestMismatch =>
                Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                    "IdempotencyConflict",
                    "The Idempotency-Key was already used with a different Workspace request.",
                    Target: "header.Idempotency-Key")),
            _ => Result<WorkspaceDetailResponse>.Failure(NotFoundError())
        };
    }

    public async Task<Result<WorkspaceDetailResponse>> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanViewWorkspace(userId, workspaceId, cancellationToken))
        {
            return Result<WorkspaceDetailResponse>.Failure(NotFoundError());
        }

        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        return workspace is null
            ? Result<WorkspaceDetailResponse>.Failure(NotFoundError())
            : Result<WorkspaceDetailResponse>.Success(ToDetail(workspace));
    }

    public async Task<Result<WorkspaceDetailResponse>> UpdateAsync(Guid workspaceId, UpdateWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<WorkspaceDetailResponse>.Failure(AuthenticationRequiredError());
        }

        if (!await authorization.CanViewWorkspace(userId, workspaceId, cancellationToken))
        {
            return Result<WorkspaceDetailResponse>.Failure(NotFoundError());
        }

        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace is null)
        {
            return Result<WorkspaceDetailResponse>.Failure(NotFoundError());
        }

        if (workspace.Status == WorkspaceStatus.Archived)
        {
            return Result<WorkspaceDetailResponse>.Failure(ArchivedReadOnlyError());
        }

        if (!await authorization.CanManageWorkspace(userId, workspaceId, cancellationToken))
        {
            return Result<WorkspaceDetailResponse>.Failure(CapabilityDeniedError("You are not allowed to manage this workspace."));
        }

        if (request.Status.HasValue && request.Status.Value != workspace.Status)
        {
            return Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
                "InvalidStateTransition",
                "Workspace lifecycle changes must use the archive or restore command.",
                Target: "body.status"));
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Result<WorkspaceDetailResponse>.Failure("Workspace name is required.");
            }

            workspace.Name = request.Name.Trim();
            workspace.Slug = CreateWorkspaceSlug(workspace.Name, workspace.Id);
        }

        workspace.Description = request.Description?.Trim() ?? workspace.Description;
        workspace.Icon = request.Icon?.Trim() ?? workspace.Icon;
        await AuditAsync(userId, "WorkspaceUpdated", workspace.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<WorkspaceDetailResponse>.Success(ToDetail(workspace));
    }

    public async Task<Result> ArchiveAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result.Failure(AuthenticationRequiredError());
        }

        if (!await authorization.CanViewWorkspace(userId, workspaceId, cancellationToken))
        {
            return Result.Failure(NotFoundError());
        }

        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace is null)
        {
            return Result.Failure(NotFoundError());
        }

        if (workspace.Status == WorkspaceStatus.Archived)
        {
            return Result.Failure(new ApplicationErrorDetail(
                "InvalidStateTransition",
                "The Workspace is already archived.",
                Target: "workspace.status"));
        }

        if (!await authorization.CanManageWorkspace(userId, workspaceId, cancellationToken))
        {
            return Result.Failure(CapabilityDeniedError("You are not allowed to manage this workspace."));
        }

        // Determine the affected recipients before changing the lifecycle and
        // stage metadata-only invalidations in this same business unit of
        // work. The Outbox row is therefore absent on rollback.
        var affectedMembers = (await workspaces.ListMembersAsync(workspaceId, cancellationToken))
            .Where(member => member.Status == MembershipStatus.Active)
            .Select(member => member.UserId)
            .Distinct()
            .ToArray();
        workspace.Status = WorkspaceStatus.Archived;
        await AuditAsync(userId, "WorkspaceArchived", workspace.Id, cancellationToken);
        foreach (var affectedUserId in affectedMembers)
        {
            await PublishAuthorizationChangeAsync(affectedUserId, workspace.Id, "archived", cancellationToken);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> RestoreAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result.Failure(AuthenticationRequiredError());
        }

        // This check intentionally precedes restore authority. SystemAdmin
        // without a current Workspace membership must not learn archived state.
        if (!await authorization.CanViewWorkspace(userId, workspaceId, cancellationToken))
        {
            return Result.Failure(NotFoundError());
        }

        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace is null)
        {
            return Result.Failure(NotFoundError());
        }

        if (workspace.Status != WorkspaceStatus.Archived)
        {
            return Result.Failure(new ApplicationErrorDetail(
                "InvalidStateTransition",
                "Only an archived Workspace can be restored.",
                Target: "workspace.status"));
        }

        if (!await authorization.CanRestoreWorkspace(userId, workspaceId, cancellationToken))
        {
            return Result.Failure(CapabilityDeniedError("Only a current Workspace Owner can restore an archived Workspace."));
        }

        var affectedMembers = (await workspaces.ListMembersAsync(workspaceId, cancellationToken))
            .Where(member => member.Status == MembershipStatus.Active)
            .Select(member => member.UserId)
            .Distinct()
            .ToArray();
        workspace.Status = WorkspaceStatus.Active;
        await AuditAsync(userId, "WorkspaceRestored", workspace.Id, cancellationToken);
        foreach (var affectedUserId in affectedMembers)
        {
            await PublishAuthorizationChangeAsync(affectedUserId, workspace.Id, "restored", cancellationToken);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<WorkspaceMemberResponse>>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanViewWorkspace(userId, workspaceId, cancellationToken))
        {
            return Result<IReadOnlyList<WorkspaceMemberResponse>>.Failure(NotFoundError());
        }

        var members = await workspaces.ListMembersAsync(workspaceId, cancellationToken);
        return Result<IReadOnlyList<WorkspaceMemberResponse>>.Success(members.Select(ToMember).ToList());
    }

    public async Task<Result<WorkspaceMemberResponse>> AddMemberAsync(Guid workspaceId, AddWorkspaceMemberRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var actorUserId))
        {
            return Result<WorkspaceMemberResponse>.Failure(AuthenticationRequiredError());
        }

        if (!await authorization.CanViewWorkspace(actorUserId, workspaceId, cancellationToken))
        {
            return Result<WorkspaceMemberResponse>.Failure(NotFoundError());
        }

        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace is null)
        {
            return Result<WorkspaceMemberResponse>.Failure(NotFoundError());
        }
        if (workspace.Status == WorkspaceStatus.Archived)
        {
            return Result<WorkspaceMemberResponse>.Failure(ArchivedReadOnlyError());
        }
        if (!await authorization.CanManageWorkspace(actorUserId, workspaceId, cancellationToken))
        {
            return Result<WorkspaceMemberResponse>.Failure(CapabilityDeniedError("You are not allowed to manage members."));
        }

        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result<WorkspaceMemberResponse>.Failure("User not found.");
        }

        if (await workspaces.GetMemberAsync(workspaceId, request.UserId, cancellationToken) is not null)
        {
            return Result<WorkspaceMemberResponse>.Failure("User is already a workspace member.");
        }

        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = request.UserId,
            User = user,
            Role = request.Role,
            Status = MembershipStatus.Active,
            JoinedAt = clock.UtcNow
        };

        await workspaces.AddMemberAsync(member, cancellationToken);
        var generalSync = await StageWorkspaceGeneralMembershipAsync(member, actorUserId, cancellationToken);
        if (!generalSync.IsSuccess)
        {
            return Result<WorkspaceMemberResponse>.Failure(WorkspaceGeneralDependencyError());
        }
        await AuditAsync(actorUserId, "WorkspaceMemberAdded", workspaceId, cancellationToken);
        await PublishAuthorizationChangeAsync(member.UserId, workspaceId, "granted", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<WorkspaceMemberResponse>.Success(ToMember(member));
    }

    public async Task<Result<WorkspaceMemberResponse>> UpdateMemberAsync(Guid workspaceId, Guid userId, UpdateWorkspaceMemberRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var actorUserId))
        {
            return Result<WorkspaceMemberResponse>.Failure(AuthenticationRequiredError());
        }

        if (!await authorization.CanViewWorkspace(actorUserId, workspaceId, cancellationToken))
        {
            return Result<WorkspaceMemberResponse>.Failure(NotFoundError());
        }

        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace is null)
        {
            return Result<WorkspaceMemberResponse>.Failure(NotFoundError());
        }
        if (workspace.Status == WorkspaceStatus.Archived)
        {
            return Result<WorkspaceMemberResponse>.Failure(ArchivedReadOnlyError());
        }
        if (!await authorization.CanManageWorkspace(actorUserId, workspaceId, cancellationToken))
        {
            return Result<WorkspaceMemberResponse>.Failure(CapabilityDeniedError("You are not allowed to manage members."));
        }

        var member = await workspaces.GetMemberAsync(workspaceId, userId, cancellationToken);
        if (member is null)
        {
            return Result<WorkspaceMemberResponse>.Failure("Workspace member not found.");
        }

        member.Role = request.Role;
        member.Status = request.Status ?? member.Status;
        var generalSync = await StageWorkspaceGeneralMembershipAsync(member, actorUserId, cancellationToken);
        if (!generalSync.IsSuccess)
        {
            return Result<WorkspaceMemberResponse>.Failure(WorkspaceGeneralDependencyError());
        }
        await AuditAsync(actorUserId, "WorkspaceMemberRoleChanged", workspaceId, cancellationToken);
        await PublishAuthorizationChangeAsync(member.UserId, workspaceId, member.Status == MembershipStatus.Active ? "membershipChanged" : "suspended", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<WorkspaceMemberResponse>.Success(ToMember(member));
    }

    public async Task<Result> RemoveMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var actorUserId))
        {
            return Result.Failure(AuthenticationRequiredError());
        }

        if (!await authorization.CanViewWorkspace(actorUserId, workspaceId, cancellationToken))
        {
            return Result.Failure(NotFoundError());
        }

        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace is null)
        {
            return Result.Failure(NotFoundError());
        }
        if (workspace.Status == WorkspaceStatus.Archived)
        {
            return Result.Failure(ArchivedReadOnlyError());
        }
        if (!await authorization.CanManageWorkspace(actorUserId, workspaceId, cancellationToken))
        {
            return Result.Failure(CapabilityDeniedError("You are not allowed to manage members."));
        }

        var member = await workspaces.GetMemberAsync(workspaceId, userId, cancellationToken);
        if (member is null)
        {
            return Result.Failure("Workspace member not found.");
        }

        member.Status = MembershipStatus.Suspended;
        var generalSync = await StageWorkspaceGeneralMembershipAsync(member, actorUserId, cancellationToken);
        if (!generalSync.IsSuccess)
        {
            return Result.Failure(WorkspaceGeneralDependencyError());
        }
        await AuditAsync(actorUserId, "WorkspaceMemberRemoved", workspaceId, cancellationToken);
        await PublishAuthorizationChangeAsync(userId, workspaceId, "revoked", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private Task<Result> StageWorkspaceGeneralMembershipAsync(
        WorkspaceMember member,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        return generalMemberships?.StageAsync(member, actorUserId, cancellationToken)
               ?? Task.FromResult(Result.Failure(
                   "Canonical Workspace general membership synchronization is unavailable."));
    }

    private Task AuditAsync(Guid actorUserId, string action, Guid targetId, CancellationToken cancellationToken)
    {
        return auditLogger.LogAsync(new AuditLogEntry(actorUserId, action, "Workspace", targetId), cancellationToken);
    }

    private Task PublishAuthorizationChangeAsync(Guid userId, Guid workspaceId, string change, CancellationToken cancellationToken)
    {
        if (currentTenant is null || !currentTenant.IsAvailable || authorizationChanges is null)
        {
            return Task.CompletedTask;
        }

        return authorizationChanges.PublishAsync(
            currentTenant.TenantId,
            userId,
            "workspace",
            workspaceId,
            change,
            cancellationToken);
    }

    private static ApplicationErrorDetail AuthenticationRequiredError() =>
        new("AuthenticationRequired", "Authentication is required.");

    private static ApplicationErrorDetail NotFoundError() =>
        new("NotFound", "The requested resource was not found.");

    private static ApplicationErrorDetail CapabilityDeniedError(string message) =>
        new("CapabilityDenied", message, Target: "workspace");

    private static ApplicationErrorDetail WorkspaceGeneralDependencyError() =>
        new(
            "DependencyUnavailable",
            "Canonical Workspace general membership could not be synchronized.",
            Target: "workspace.general");

    private static ApplicationErrorDetail ArchivedReadOnlyError() =>
        new(
            "InvalidStateTransition",
            "Archived Workspaces are read-only. Restore the Workspace before modifying it.",
            Target: "workspace.status");

    private static Result<WorkspaceDetailResponse> ValidationFailure(string message, string target) =>
        Result<WorkspaceDetailResponse>.Failure(new ApplicationErrorDetail(
            "ValidationFailed",
            message,
            Target: target));

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static bool IsValidClientRequestIdentity(string value) =>
        value.Length is >= 8 and <= 128 &&
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => character is >= ' ' and <= '~');

    private static string CreateRequestFingerprint(string name, string? description, string? icon)
    {
        var canonical = string.Concat(
            EncodeFingerprintPart(name),
            EncodeFingerprintPart(description),
            EncodeFingerprintPart(icon));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string EncodeFingerprintPart(string? value) =>
        value is null ? "-1:" : $"{Encoding.UTF8.GetByteCount(value)}:{value}";

    private static string CreateWorkspaceSlug(string name, Guid workspaceId)
    {
        const int maximumLength = 120;
        var suffix = $"-{workspaceId:N}";
        var stemBuilder = new StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                stemBuilder.Append(character);
            }
            else if (stemBuilder.Length > 0 && stemBuilder[^1] != '-')
            {
                stemBuilder.Append('-');
            }
        }

        var stem = stemBuilder.ToString().Trim('-');
        if (string.IsNullOrEmpty(stem))
        {
            stem = "workspace";
        }

        var maximumStemLength = maximumLength - suffix.Length;
        if (stem.Length > maximumStemLength)
        {
            stem = stem[..maximumStemLength].TrimEnd('-');
        }

        return string.Concat(stem, suffix);
    }

    private static WorkspaceListItemResponse ToListItem(Workspace workspace)
    {
        return new WorkspaceListItemResponse(workspace.Id, workspace.Name, workspace.Description, workspace.Icon, workspace.Status, workspace.CreatedAt, workspace.UpdatedAt);
    }

    private static WorkspaceDetailResponse ToDetail(Workspace workspace)
    {
        return new WorkspaceDetailResponse(workspace.Id, workspace.Name, workspace.Description, workspace.Icon, workspace.Status, workspace.CreatedByUserId, workspace.CreatedAt, workspace.UpdatedAt);
    }

    private static WorkspaceMemberResponse ToMember(WorkspaceMember member)
    {
        return new WorkspaceMemberResponse(member.UserId, member.User?.DisplayName ?? string.Empty, member.User?.Email ?? string.Empty, member.Role, member.Status, member.JoinedAt);
    }

    private sealed class WorkspaceRequiredInitializationException : Exception;
}
