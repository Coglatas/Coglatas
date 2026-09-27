using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Files;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Coglatas.Tests.Tenancy;

public sealed class SecurityCiFixtureSeedTests
{
    [Fact]
    public async Task SeedCreatesIdempotentMultiTenantAuthorizationGraphAndStoredCanaries()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new AppDbContext(options, currentTenant);

        var storageRoot = Path.Combine(Path.GetTempPath(), "coglatas-sec02-fixture", Guid.NewGuid().ToString("N"));
        var storage = new LocalFileStorageService(Options.Create(new FileStorageOptions
        {
            Provider = "LocalFileSystem",
            RootPath = storageRoot
        }));

        try
        {
            const string password = "sec02-test-password";
            var hasher = new Pbkdf2PasswordHasher();

            await SecurityCiFixtureSeed.SeedAsync(dbContext, hasher, storage, password);
            await SecurityCiFixtureSeed.SeedAsync(dbContext, hasher, storage, password);

            Assert.Equal(2, await dbContext.Tenants.CountAsync());
            Assert.Equal(4, await dbContext.Users.CountAsync());
            Assert.Equal(2, await dbContext.Workspaces.CountAsync());
            Assert.Equal(2, await dbContext.Projects.CountAsync());
            Assert.Equal(2, await dbContext.TaskItems.CountAsync());
            Assert.Equal(2, await dbContext.FileObjects.CountAsync());
            Assert.Equal(2, await dbContext.Attachments.CountAsync());
            Assert.Equal(2, await dbContext.Conversations.CountAsync());
            Assert.Equal(2, await dbContext.Messages.CountAsync());

            var alpha = await dbContext.Tenants.SingleAsync(tenant => tenant.Slug == SecurityCiFixtureSeed.TenantASlug);
            var beta = await dbContext.Tenants.SingleAsync(tenant => tenant.Slug == SecurityCiFixtureSeed.TenantBSlug);
            var alphaOwner = await dbContext.Users.SingleAsync(user => user.Email == SecurityCiFixtureSeed.TenantAOwnerEmail);
            var alphaMember = await dbContext.Users.SingleAsync(user => user.Email == SecurityCiFixtureSeed.TenantAMemberEmail);
            var alphaRestricted = await dbContext.Users.SingleAsync(user => user.Email == SecurityCiFixtureSeed.TenantARestrictedEmail);
            var betaOwner = await dbContext.Users.SingleAsync(user => user.Email == SecurityCiFixtureSeed.TenantBOwnerEmail);

            Assert.Equal(SecurityCiFixtureSeed.TenantAOwnerUserId, alphaOwner.Id);
            Assert.Equal(SecurityCiFixtureSeed.TenantAMemberUserId, alphaMember.Id);
            Assert.Equal(SecurityCiFixtureSeed.TenantARestrictedUserId, alphaRestricted.Id);
            Assert.Equal(SecurityCiFixtureSeed.TenantBOwnerUserId, betaOwner.Id);
            Assert.All(
                [alphaOwner.Id, alphaMember.Id, alphaRestricted.Id, betaOwner.Id],
                userId => Assert.DoesNotMatch(@"\d{12,}", userId.ToString("D")));

            Assert.Equal(TenantUserRole.Owner, (await dbContext.TenantUsers.SingleAsync(item => item.TenantId == alpha.Id && item.UserId == alphaOwner.Id)).Role);
            Assert.Equal(TenantUserRole.Member, (await dbContext.TenantUsers.SingleAsync(item => item.TenantId == alpha.Id && item.UserId == alphaMember.Id)).Role);
            Assert.Equal(TenantUserRole.Guest, (await dbContext.TenantUsers.SingleAsync(item => item.TenantId == alpha.Id && item.UserId == alphaRestricted.Id)).Role);
            Assert.Equal(TenantUserRole.Owner, (await dbContext.TenantUsers.SingleAsync(item => item.TenantId == beta.Id && item.UserId == betaOwner.Id)).Role);

            var alphaWorkspace = await dbContext.Workspaces.SingleAsync(item => item.TenantId == alpha.Id);
            var alphaProject = await dbContext.Projects.SingleAsync(item => item.TenantId == alpha.Id);
            Assert.Equal(WorkspaceRole.Owner, (await dbContext.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == alphaWorkspace.Id && item.UserId == alphaOwner.Id)).Role);
            Assert.Equal(WorkspaceRole.Member, (await dbContext.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == alphaWorkspace.Id && item.UserId == alphaMember.Id)).Role);
            Assert.Equal(WorkspaceRole.ReadOnly, (await dbContext.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == alphaWorkspace.Id && item.UserId == alphaRestricted.Id)).Role);
            Assert.Equal(ProjectRole.Owner, (await dbContext.ProjectMembers.SingleAsync(item => item.ProjectId == alphaProject.Id && item.UserId == alphaOwner.Id)).Role);
            Assert.Equal(ProjectRole.Contributor, (await dbContext.ProjectMembers.SingleAsync(item => item.ProjectId == alphaProject.Id && item.UserId == alphaMember.Id)).Role);
            Assert.Equal(ProjectRole.Viewer, (await dbContext.ProjectMembers.SingleAsync(item => item.ProjectId == alphaProject.Id && item.UserId == alphaRestricted.Id)).Role);

            var alphaFile = await dbContext.FileObjects.SingleAsync(item => item.TenantId == alpha.Id);
            var betaFile = await dbContext.FileObjects.SingleAsync(item => item.TenantId == beta.Id);
            Assert.StartsWith($"tenants/{alpha.Id:D}/", alphaFile.StorageKey, StringComparison.Ordinal);
            Assert.StartsWith($"tenants/{beta.Id:D}/", betaFile.StorageKey, StringComparison.Ordinal);
            Assert.True(await storage.ExistsAsync(alphaFile.StorageKey));
            Assert.True(await storage.ExistsAsync(betaFile.StorageKey));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }
}
