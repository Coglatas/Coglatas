using Coglatas.Application.Auth;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Files;
using Coglatas.Web.Configuration;
using Coglatas.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Coglatas.Tests.Tenancy;

public sealed class TenancyFoundationTests
{
    [Fact]
    public async Task GlobalQueryFilterHidesOtherTenantData()
    {
        var currentTenant = new CurrentTenantService();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        currentTenant.SetPlatformScope();
        dbContext.Workspaces.AddRange(
            new Workspace { TenantId = tenantA, Name = "Tenant A", Slug = "tenant-a", CreatedByUserId = Guid.NewGuid() },
            new Workspace { TenantId = tenantB, Name = "Tenant B", Slug = "tenant-b", CreatedByUserId = Guid.NewGuid() });
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(tenantA, "tenant-a");
        var visibleWorkspaces = await dbContext.Workspaces.AsNoTracking().ToListAsync();

        Assert.Single(visibleWorkspaces);
        Assert.Equal(tenantA, visibleWorkspaces[0].TenantId);
    }

    [Fact]
    public async Task CreatingTenantEntityAutoSetsTenantId()
    {
        var currentTenant = new CurrentTenantService();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenantId = Guid.NewGuid();
        currentTenant.SetPlatformScope();
        dbContext.Tenants.Add(new Tenant(tenantId) { Name = "Tenant", Slug = "tenant", DisplayName = "Tenant" });
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(tenantId, "tenant");

        var workspace = new Workspace
        {
            Name = "Workspace",
            Slug = "workspace",
            CreatedByUserId = Guid.NewGuid()
        };

        dbContext.Workspaces.Add(workspace);
        await dbContext.SaveChangesAsync();

        Assert.Equal(tenantId, workspace.TenantId);
    }

    [Fact]
    public async Task MismatchedTenantIdCreationIsRejected()
    {
        var currentTenant = new CurrentTenantService();
        await using var dbContext = CreateDbContext(currentTenant);
        currentTenant.SetTenant(Guid.NewGuid(), "tenant");

        dbContext.Workspaces.Add(new Workspace
        {
            TenantId = Guid.NewGuid(),
            Name = "Wrong Tenant",
            Slug = "wrong-tenant",
            CreatedByUserId = Guid.NewGuid()
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task TenantAdminCannotListPlatformTenants()
    {
        var currentTenant = new CurrentTenantService();
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = new Tenant { Name = "Tenant", Slug = "tenant", DisplayName = "Tenant" };
        var user = NewUser(SystemRole.User);

        currentTenant.SetPlatformScope();
        dbContext.Tenants.Add(tenant);
        dbContext.Users.Add(user);
        dbContext.TenantUsers.Add(new TenantUser
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            Role = TenantUserRole.Admin,
            Status = TenantUserStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var service = CreateTenantService(dbContext, currentTenant, user, new TenancyOptions());

        var result = await service.ListPlatformTenantsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("PlatformAdmin access is required.", result.Error);
    }

    [Fact]
    public async Task PlatformAdminCanListTenants()
    {
        var currentTenant = new CurrentTenantService();
        await using var dbContext = CreateDbContext(currentTenant);
        var user = NewUser(SystemRole.PlatformAdmin);

        currentTenant.SetPlatformScope();
        dbContext.Users.Add(user);
        dbContext.Tenants.Add(new Tenant { Name = "Tenant", Slug = "tenant", DisplayName = "Tenant" });
        await dbContext.SaveChangesAsync();

        var service = CreateTenantService(dbContext, currentTenant, user, new TenancyOptions());

        var result = await service.ListPlatformTenantsAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task OnPremSingleTenantResolverUsesConfiguredDefaultTenant()
    {
        var currentTenant = new CurrentTenantService();
        await using var dbContext = CreateDbContext(currentTenant);
        currentTenant.SetPlatformScope();
        dbContext.Tenants.Add(new Tenant { Name = "Default", Slug = "default", DisplayName = "Default" });
        await dbContext.SaveChangesAsync();

        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        httpContextAccessor.HttpContext.Request.Host = new HostString("ignored.example");

        var resolver = new HttpTenantResolver(
            httpContextAccessor,
            new FakeWebHostEnvironment(Environments.Production),
            Options.Create(new TenancyOptions
            {
                AppMode = AppMode.OnPremSingleTenant,
                DefaultTenantSlug = "default",
                TenantResolutionStrategy = TenantResolutionStrategy.Host
            }),
            dbContext);

        var result = await resolver.ResolveAsync();

        Assert.True(result.IsResolved);
        Assert.Equal("default", result.TenantSlug);
    }

    [Fact]
    public async Task DevelopmentHeaderTenantResolutionRequiresExplicitEnablement()
    {
        var currentTenant = new CurrentTenantService();
        await using var dbContext = CreateDbContext(currentTenant);
        currentTenant.SetPlatformScope();
        dbContext.Tenants.Add(new Tenant { Name = "Default", Slug = "default", DisplayName = "Default" });
        await dbContext.SaveChangesAsync();

        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        httpContextAccessor.HttpContext.Request.Headers["X-Tenant-Slug"] = "default";

        var resolver = new HttpTenantResolver(
            httpContextAccessor,
            new FakeWebHostEnvironment(Environments.Development),
            Options.Create(new TenancyOptions
            {
                AppMode = AppMode.SaaS,
                DefaultTenantSlug = "default",
                TenantResolutionStrategy = TenantResolutionStrategy.HeaderForDevelopmentOnly,
                AllowDevelopmentHeaderTenantResolution = false
            }),
            dbContext);

        var result = await resolver.ResolveAsync();

        Assert.False(result.IsResolved);
    }

    [Fact]
    public async Task OnPremSingleTenantDisablesTenantSwitching()
    {
        var currentTenant = new CurrentTenantService();
        await using var dbContext = CreateDbContext(currentTenant);
        var user = NewUser(SystemRole.User);
        var tenant = new Tenant { Name = "Tenant", Slug = "tenant", DisplayName = "Tenant" };

        currentTenant.SetPlatformScope();
        dbContext.Users.Add(user);
        dbContext.Tenants.Add(tenant);
        dbContext.TenantUsers.Add(new TenantUser
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            Role = TenantUserRole.Owner,
            Status = TenantUserStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var service = CreateTenantService(dbContext, currentTenant, user, new TenancyOptions
        {
            AppMode = AppMode.OnPremSingleTenant,
            AllowTenantSwitching = true
        });

        var result = await service.SwitchTenantAsync(tenant.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("Tenant switching is disabled.", result.Error);
    }

    [Fact]
    public async Task StartupValidationRejectsUnsafeProductionCookieConfig()
    {
        var validator = new StartupConfigurationValidator(
            Options.Create(new TenancyOptions
            {
                AppMode = AppMode.SaaS,
                DefaultTenantSlug = "default",
                TenantResolutionStrategy = TenantResolutionStrategy.Host
            }),
            Options.Create(new FileStorageOptions
            {
                Provider = "LocalFileSystem",
                RootPath = Path.Combine(Path.GetTempPath(), "coglatas-validation-tests", Guid.NewGuid().ToString("N")),
                MaxFileSizeBytes = 1024,
                AllowedExtensions = [".txt"],
                AllowedContentTypes = ["text/plain"]
            }),
            Options.Create(new SecurityOptions
            {
                CookieSecurePolicy = CookieSecurePolicy.SameAsRequest,
                RequireHttps = true,
                EnableHsts = true,
                LoginLockoutEnabled = true,
                MaxFailedLoginAttempts = 5
            }),
            Options.Create(new PlatformOptions()),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Host=db;Port=5432;Database=aip;Username=aip;Password=StrongPasswordValue123!",
                    ["DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), "coglatas-dp-validation-tests", Guid.NewGuid().ToString("N"))
                })
                .Build(),
            new ServiceCollection().AddAntiforgery().BuildServiceProvider(),
            new CsrfProtectionState(),
            new FakeWebHostEnvironment(Environments.Production),
            NullLogger<StartupConfigurationValidator>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));
    }

    private static AppDbContext CreateDbContext(ICurrentTenant currentTenant)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options, currentTenant);
    }

    private static TenantService CreateTenantService(
        AppDbContext dbContext,
        ICurrentTenant currentTenant,
        User currentUser,
        TenancyOptions options)
    {
        var repository = new TenantRepository(dbContext);
        var authorization = new TenantAuthorizationService(repository);
        return new TenantService(
            repository,
            authorization,
            currentTenant,
            new FakeCurrentUser(currentUser),
            new FakeAuditLogger(),
            new EfUnitOfWork(dbContext),
            new FakeUserSessionService(),
            options);
    }

    private static User NewUser(SystemRole role)
    {
        return new User
        {
            DisplayName = "User",
            Email = $"{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"{Guid.NewGuid():N}@EXAMPLE.COM",
            PasswordHash = "hash",
            Status = UserStatus.Active,
            SystemRole = role
        };
    }

    private sealed class FakeCurrentUser(User user) : ICurrentUser
    {
        public Guid? UserId => user.Id;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => user.Email;
        public SystemRole? SystemRole => user.SystemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeUserSessionService : IUserSessionService
    {
        public Task<SessionValidationResult> ValidateSessionAsync(Guid userId, Guid sessionId, Guid? tenantId, bool requireActiveTenantMembership, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SessionValidationResult.Success());
        }

        public Task<Result> RevokeSessionAsync(Guid sessionId, Guid? actorUserId, string reason, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<int>> RevokeUserSessionsAsync(Guid userId, Guid? actorUserId, string reason, Guid? exceptSessionId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<int>.Success(0));
        }
    }

    private sealed class FakeWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Coglatas.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
