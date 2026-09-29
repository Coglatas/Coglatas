using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Files;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

[Trait("Scope", "WPCFinal03")]
public sealed class WpcFinal03SecurityPostgreSqlTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task CrossTenantUserCannotBePersistedThroughWorkspaceGeneralAdmission()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);
            var currentTenant = new CurrentTenantService();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnectionString)
                .AddInterceptors(new ProjectGovernanceSaveChangesInterceptor())
                .Options;

            await using var dbContext = new AppDbContext(options, currentTenant);
            var tenantA = NewTenant("tenant-a");
            var tenantB = NewTenant("tenant-b");
            var owner = NewUser("owner");
            var foreignUser = NewUser("foreign");

            currentTenant.SetPlatformScope();
            dbContext.Tenants.AddRange(tenantA, tenantB);
            dbContext.Users.AddRange(owner, foreignUser);
            await dbContext.SaveChangesAsync();

            dbContext.TenantUsers.AddRange(
                NewTenantUser(tenantA.Id, owner.Id, TenantUserRole.Owner),
                NewTenantUser(tenantB.Id, foreignUser.Id, TenantUserRole.Member));
            await dbContext.SaveChangesAsync();

            currentTenant.SetTenant(tenantA.Id, tenantA.Slug);
            var workspace = new Workspace
            {
                TenantId = tenantA.Id,
                Name = "Tenant A Workspace",
                Slug = $"tenant-a-workspace-{Guid.NewGuid():N}",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = owner.Id
            };
            dbContext.Workspaces.Add(workspace);
            dbContext.WorkspaceMembers.Add(new WorkspaceMember
            {
                TenantId = tenantA.Id,
                WorkspaceId = workspace.Id,
                UserId = owner.Id,
                Role = WorkspaceRole.Owner,
                Status = MembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();

            var invalidMembership = new WorkspaceMember
            {
                TenantId = tenantA.Id,
                WorkspaceId = workspace.Id,
                UserId = foreignUser.Id,
                Role = WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            };
            dbContext.WorkspaceMembers.Add(invalidMembership);
            var synchronizer = new WorkspaceGeneralMembershipSynchronizer(
                null!,
                currentTenant,
                null!,
                null!,
                new TenantRepository(dbContext));

            var result = await synchronizer.StageAsync(invalidMembership, owner.Id);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                "WorkspaceGeneral membership requires an active Tenant membership.",
                result.Error);

            dbContext.ChangeTracker.Clear();
            Assert.False(await dbContext.WorkspaceMembers
                .IgnoreQueryFilters()
                .AnyAsync(member =>
                    member.WorkspaceId == workspace.Id &&
                    member.UserId == foreignUser.Id));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task WorkspaceVisibleReadDoesNotBecomeProjectFileOrTaskMutationAuthority()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);
            var currentTenant = new CurrentTenantService();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnectionString)
                .AddInterceptors(new ProjectGovernanceSaveChangesInterceptor())
                .Options;

            await using var dbContext = new AppDbContext(options, currentTenant);
            var tenant = NewTenant("mutation-boundary");
            var owner = NewUser("project-owner");
            var reader = NewUser("workspace-reader");

            currentTenant.SetPlatformScope();
            dbContext.Tenants.Add(tenant);
            dbContext.Users.AddRange(owner, reader);
            await dbContext.SaveChangesAsync();

            currentTenant.SetTenant(tenant.Id, tenant.Slug);
            dbContext.TenantUsers.AddRange(
                NewTenantUser(tenant.Id, owner.Id, TenantUserRole.Owner),
                NewTenantUser(tenant.Id, reader.Id, TenantUserRole.Member));

            var workspace = new Workspace
            {
                TenantId = tenant.Id,
                Name = "Mutation Boundary Workspace",
                Slug = $"mutation-boundary-{Guid.NewGuid():N}",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = owner.Id
            };
            var ownerWorkspaceMember = new WorkspaceMember
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                UserId = owner.Id,
                Role = WorkspaceRole.Owner,
                Status = MembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            };
            var readerWorkspaceMember = new WorkspaceMember
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                UserId = reader.Id,
                Role = WorkspaceRole.ReadOnly,
                Status = MembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            };
            var project = new Project
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                OwnerUserId = owner.Id,
                CreatedByUserId = owner.Id,
                Name = "Workspace Visible Project",
                Slug = $"workspace-visible-{Guid.NewGuid():N}",
                Status = ProjectStatus.Active,
                Visibility = ProjectVisibility.WorkspaceVisible,
                ActivationState = ProjectActivationState.Activated,
                ActivatedAtUtc = DateTimeOffset.UtcNow,
                ActivationVersion = 1,
                VersionNo = 1
            };
            var ownerProjectMember = new ProjectMember
            {
                TenantId = tenant.Id,
                ProjectId = project.Id,
                UserId = owner.Id,
                Role = ProjectRole.Owner,
                JoinedAt = DateTimeOffset.UtcNow
            };
            var task = new TaskItem
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Title = "Mutation target",
                CreatedByUserId = owner.Id,
                VersionNo = 1
            };

            dbContext.Workspaces.Add(workspace);
            dbContext.WorkspaceMembers.AddRange(ownerWorkspaceMember, readerWorkspaceMember);
            dbContext.Projects.Add(project);
            dbContext.ProjectMembers.Add(ownerProjectMember);
            dbContext.TaskItems.Add(task);
            await dbContext.SaveChangesAsync();

            var workspaceRepository = new WorkspaceRepository(dbContext);
            var workspaceAuthorization = new WorkspaceAuthorizationService(
                new UserRepository(dbContext),
                workspaceRepository,
                new TenantAuthorizationService(new TenantRepository(dbContext)));
            var groupRepository = new GroupRepository(dbContext);
            var projectAuthorization = new ProjectAuthorizationService(
                new ProjectRepository(dbContext),
                workspaceAuthorization,
                new GroupAuthorizationService(
                    groupRepository,
                    workspaceRepository,
                    workspaceAuthorization),
                groupRepository);
            var fileAuthorization = new FileAuthorizationService(
                new FileRepository(dbContext),
                projectAuthorization,
                null!,
                null!,
                workspaceAuthorization);

            Assert.True(await projectAuthorization.CanViewProject(reader.Id, project.Id));
            Assert.False(await projectAuthorization.CanCreateTask(reader.Id, project.Id));
            Assert.False(await projectAuthorization.CanCommentOnTarget(
                reader.Id,
                CommentTargetType.Project,
                project.Id));
            Assert.False(await fileAuthorization.CanUploadAttachment(
                reader.Id,
                AttachmentOwnerType.TaskItem,
                task.Id));

            readerWorkspaceMember.Role = WorkspaceRole.Member;
            var readerProjectMember = new ProjectMember
            {
                TenantId = tenant.Id,
                ProjectId = project.Id,
                UserId = reader.Id,
                Role = ProjectRole.Viewer,
                JoinedAt = DateTimeOffset.UtcNow
            };
            // Preserve the database invariant that reviewer and primary assignee
            // must be distinct while still proving that creator/reviewer history
            // cannot elevate a current Project Viewer into mutation authority.
            task.CreatedByUserId = reader.Id;
            task.PrimaryAssigneeUserId = null;
            task.ReviewerUserId = reader.Id;
            dbContext.ProjectMembers.Add(readerProjectMember);
            await dbContext.SaveChangesAsync();

            Assert.True(await projectAuthorization.CanViewProject(reader.Id, project.Id));
            Assert.False(await projectAuthorization.CanCreateTask(reader.Id, project.Id));
            Assert.False(await projectAuthorization.CanUpdateTask(reader.Id, task.Id));
            Assert.False(await projectAuthorization.CanReviewTask(reader.Id, task.Id));
            Assert.False(await projectAuthorization.CanCommentOnTarget(
                reader.Id,
                CommentTargetType.TaskItem,
                task.Id));
            Assert.False(await fileAuthorization.CanUploadAttachment(
                reader.Id,
                AttachmentOwnerType.TaskItem,
                task.Id));

            readerProjectMember.Role = ProjectRole.Contributor;
            await dbContext.SaveChangesAsync();

            Assert.True(await projectAuthorization.CanCreateTask(reader.Id, project.Id));
            Assert.True(await projectAuthorization.CanUpdateTask(reader.Id, task.Id));
            Assert.True(await projectAuthorization.CanReviewTask(reader.Id, task.Id));
            Assert.True(await projectAuthorization.CanCommentOnTarget(
                reader.Id,
                CommentTargetType.TaskItem,
                task.Id));
            Assert.True(await fileAuthorization.CanUploadAttachment(
                reader.Id,
                AttachmentOwnerType.TaskItem,
                task.Id));

            project.Status = ProjectStatus.Completed;
            await dbContext.SaveChangesAsync();

            Assert.True(await projectAuthorization.CanViewProject(reader.Id, project.Id));
            Assert.False(await projectAuthorization.CanCreateTask(reader.Id, project.Id));
            Assert.False(await projectAuthorization.CanUpdateTask(reader.Id, task.Id));
            Assert.False(await projectAuthorization.CanCommentOnTarget(
                reader.Id,
                CommentTargetType.Project,
                project.Id));
            Assert.False(await fileAuthorization.CanUploadAttachment(
                reader.Id,
                AttachmentOwnerType.TaskItem,
                task.Id));

            task.ProgressPercent = 50;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());
            Assert.Contains(
                "activated Project in Active or Review status",
                exception.Message,
                StringComparison.Ordinal);
        });
    }

    private static Tenant NewTenant(string suffix) => new()
    {
        Name = $"WPC Final03 {suffix}",
        DisplayName = $"WPC Final03 {suffix}",
        Slug = $"wpc-final03-{suffix}-{Guid.NewGuid():N}",
        Status = TenantStatus.Active
    };

    private static User NewUser(string suffix) => new()
    {
        DisplayName = $"WPC Final03 {suffix}",
        Email = $"wpc-final03-{suffix}-{Guid.NewGuid():N}@example.test",
        NormalizedEmail = $"WPC-FINAL03-{suffix}-{Guid.NewGuid():N}@EXAMPLE.TEST",
        Status = UserStatus.Active
    };

    private static TenantUser NewTenantUser(
        Guid tenantId,
        Guid userId,
        TenantUserRole role) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = TenantUserStatus.Active,
        JoinedAt = DateTimeOffset.UtcNow
    };
}