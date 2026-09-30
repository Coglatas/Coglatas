using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Coglatas.Application;
using Coglatas.Application.Auth;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Files;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Security;
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

namespace Coglatas.Tests.Auth;

public sealed class AuthSecurityHttpTests
{
    [Fact]
    public async Task UnsafeRequestWithoutCsrfTokenIsRejected()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();

        var response = await app.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(app.Email, "Password123"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnsafeRequestWithValidCsrfTokenSucceeds()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();

        var response = await app.LoginAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetRequestDoesNotRequireCsrfToken()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CsrfTokenRequestSucceedsBehindTrustedForwardedHttps()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync(
            cookieSecurePolicy: CookieSecurePolicy.Always,
            trustForwardedHeaders: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/security/csrf-token");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.42");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "portal.example.com");

        var response = await app.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        Assert.False(string.IsNullOrWhiteSpace(payload?.Token));
        Assert.Equal(SecurityOptions.CsrfHeaderName, payload.HeaderName);
        Assert.True(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(cookie => cookie.Contains(".Coglatas.Csrf=", StringComparison.Ordinal) &&
                                  cookie.Contains("secure", StringComparison.OrdinalIgnoreCase)),
            response.Headers.ToString());
    }


    [Theory]
    [Trait("Scope", "Issue357")]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("PATCH", "/api/admin/users/00000000-0000-0000-0000-000000000000")]
    [InlineData("DELETE", "/api/files/00000000-0000-0000-0000-000000000000")]
    [InlineData("POST", "/api/auth/register-by-invite")]
    [InlineData("POST", "/api/invites/accept")]
    [InlineData("POST", "/api/workspaces")]
    [InlineData("PUT", "/api/files/00000000-0000-0000-0000-000000000000/sharing")]
    [InlineData("POST", "/api/files/00000000-0000-0000-0000-000000000000/sharing/recipients")]
    [InlineData("DELETE", "/api/files/00000000-0000-0000-0000-000000000000/sharing/recipients/00000000-0000-0000-0000-000000000000")]
    [InlineData("PUT", "/api/projects/00000000-0000-0000-0000-000000000000/kanban/config")]
    [InlineData("PUT", "/api/projects/00000000-0000-0000-0000-000000000000/execution-scope")]
    [InlineData("POST", "/api/tasks/00000000-0000-0000-0000-000000000000/kanban-move")]
    public async Task UnsafeCookieAuthFlowsWithoutCsrfTokenAreRejected(string method, string path)
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();
        await app.LoginAndReadAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Content = JsonContent.Create(new { });
        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task UnsafeMethodsWithValidCsrfTokenReachEndpointRouting(string method)
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();
        var token = await app.GetCsrfTokenAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/not-found-for-csrf-test");
        request.Content = JsonContent.Create(new { });
        request.Headers.TryAddWithoutValidation(SecurityOptions.CsrfHeaderName, token);
        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LoginResponseDoesNotExposeSessionId()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();

        var response = await app.LoginAsync();
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(payload.RootElement.TryGetProperty("sessionId", out _));
    }

    [Fact]
    public async Task AuthenticatedLogoutReturnsSuccessContractClearsCookieAndRevokesAccess()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();
        await app.LoginAndReadAsync();
        var csrfToken = await app.GetCsrfTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Content = JsonContent.Create(new { });
        request.Headers.TryAddWithoutValidation(SecurityOptions.CsrfHeaderName, csrfToken);

        using var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OK", payload.RootElement.GetProperty("status").GetString());
        Assert.True(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(cookie => cookie.Contains(".Coglatas.Auth.Test=", StringComparison.Ordinal) &&
                                  cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)),
            response.Headers.ToString());

        using var currentUser = await app.Client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, currentUser.StatusCode);
        Assert.Equal("application/json", currentUser.Content.Headers.ContentType?.MediaType);
        using var unauthorized = JsonDocument.Parse(await currentUser.Content.ReadAsStringAsync());
        Assert.Equal(
            "AuthenticationRequired",
            unauthorized.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RevokedSessionCannotAccessAuthenticatedEndpoint()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();
        await app.LoginAndReadAsync();

        await app.UpdateCurrentSessionAsync(session => session.RevokedAt = DateTimeOffset.UtcNow);
        var response = await app.Client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredSessionCannotAccessAuthenticatedEndpoint()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();
        await app.LoginAndReadAsync();

        await app.UpdateCurrentSessionAsync(session => session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        var response = await app.Client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DisabledUserCannotContinueWithOldCookie()
    {
        await using var app = await AuthSecurityTestApp.CreateAsync();
        await app.LoginAndReadAsync();

        await app.UpdateUserAsync(user => user.Status = UserStatus.Suspended);
        var response = await app.Client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class AuthSecurityTestApp : IAsyncDisposable
    {
        private AuthSecurityTestApp(WebApplication app, HttpClient client, Guid userId, string email, string dataProtectionKeysPath)
        {
            App = app;
            Client = client;
            UserId = userId;
            Email = email;
            DataProtectionKeysPath = dataProtectionKeysPath;
        }

        private WebApplication App { get; }
        private string DataProtectionKeysPath { get; }
        public HttpClient Client { get; }
        private Guid UserId { get; }
        public string Email { get; }

        public static async Task<AuthSecurityTestApp> CreateAsync(
            CookieSecurePolicy cookieSecurePolicy = CookieSecurePolicy.SameAsRequest,
            bool trustForwardedHeaders = false)
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
                ["Tenancy:TenantResolutionStrategy"] = "ConfigDefault",
                ["Tenancy:DefaultTenantSlug"] = "default",
                ["Tenancy:AllowTenantSwitching"] = "true",
                ["Security:CookieSecurePolicy"] = cookieSecurePolicy.ToString(),
                ["Security:RequireHttps"] = "false",
                ["Security:EnableHsts"] = "false",
                ["Security:EnableCsrfProtection"] = "true",
                ["Security:EnableRateLimiting"] = "false",
                ["Security:LoginLockoutEnabled"] = "true",
                ["Security:MaxFailedLoginAttempts"] = "5",
                ["Security:LoginLockoutDurationMinutes"] = "15",
                [ForwardedHeadersConfiguration.TrustForwardedHeadersKey] = trustForwardedHeaders.ToString(),
                ["ReverseProxy:TrustedProxies:0"] = "127.0.0.1",
                ["FileStorage:Provider"] = "LocalFileSystem",
                ["FileStorage:RootPath"] = Path.Combine(Path.GetTempPath(), "coglatas-auth-security-tests", Guid.NewGuid().ToString("N")),
                ["FileStorage:MaxFileSizeBytes"] = "10485760",
                ["FileStorage:AllowedExtensions:0"] = ".txt",
                ["FileStorage:AllowedContentTypes:0"] = "text/plain"
            });

            var dataProtectionKeysPath = Path.Combine(
                Path.GetTempPath(),
                "coglatas-auth-security-tests",
                "data-protection",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataProtectionKeysPath);
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

            builder.Services
                .AddApplication()
                .AddWebServices(builder.Configuration);
            if (ForwardedHeadersConfiguration.ShouldTrustForwardedHeaders(builder.Configuration))
            {
                builder.Services.Configure<ForwardedHeadersOptions>(options =>
                    ForwardedHeadersConfiguration.Configure(options, builder.Configuration));
            }
            builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            builder.Services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = ".Coglatas.Auth.Test";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = cookieSecurePolicy;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = true;
                    options.EventsType = typeof(DbSessionCookieAuthenticationEvents);
                });
            builder.Services.AddAuthorization();
            var databaseName = Guid.NewGuid().ToString("N");
            builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
            AddInfrastructureLikeServices(builder.Services, builder.Configuration);
            builder.Services.AddScoped<IStudentRecordRepository, StudentRecordRepository>();

            var app = builder.Build();
            if (ForwardedHeadersConfiguration.ShouldTrustForwardedHeaders(app.Configuration))
            {
                app.UseForwardedHeaders();
            }
            app.UseMiddleware<TenantResolutionMiddleware>();
            app.UseAuthentication();
            app.Services.GetRequiredService<CsrfProtectionState>().MarkMiddlewareActive();
            app.UseMiddleware<CsrfProtectionMiddleware>();
            app.UseAuthorization();
            app.MapControllers();

            await app.StartAsync();

            var userId = await SeedUserAsync(app.Services);
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Test server address was not available.");
            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            return new AuthSecurityTestApp(
                app,
                new HttpClient(handler) { BaseAddress = new Uri(address) },
                userId,
                "student@example.com",
                dataProtectionKeysPath);
        }

        public async Task<HttpResponseMessage> LoginAsync()
        {
            var token = await GetCsrfTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
            request.Content = JsonContent.Create(new LoginRequest(Email, "Password123"));
            request.Headers.TryAddWithoutValidation(SecurityOptions.CsrfHeaderName, token);
            return await Client.SendAsync(request);
        }

        public async Task LoginAndReadAsync()
        {
            var response = await LoginAsync();
            response.EnsureSuccessStatusCode();
            _ = await response.Content.ReadFromJsonAsync<LoginResponse>()
                ?? throw new InvalidOperationException("Login response was empty.");
        }

        public async Task UpdateCurrentSessionAsync(Action<Session> update)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await dbContext.Sessions.FirstAsync(item => item.UserId == UserId);
            update(session);
            await dbContext.SaveChangesAsync();
        }

        public async Task UpdateUserAsync(Action<User> update)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await dbContext.Users.FirstAsync(item => item.Id == UserId);
            update(user);
            await dbContext.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            TryDeleteDirectory(DataProtectionKeysPath);
        }

        public async Task<string> GetCsrfTokenAsync()
        {
            var response = await Client.GetAsync("/api/security/csrf-token");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"CSRF token request failed with {(int)response.StatusCode} {response.StatusCode}: {body}");
            }

            var tokenResponse = JsonSerializer.Deserialize<CsrfTokenResponse>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return tokenResponse?.Token ?? throw new InvalidOperationException("CSRF token response was empty.");
        }

        private static async Task<Guid> SeedUserAsync(IServiceProvider services)
        {
            await using var scope = services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var user = new User
            {
                DisplayName = "Student",
                Email = "student@example.com",
                NormalizedEmail = "STUDENT@EXAMPLE.COM",
                PasswordHash = passwordHasher.HashPassword("Password123"),
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            };
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();
            return user.Id;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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
            services.AddScoped<IFileStorageService, LocalFileStorageService>();
            services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
            services.AddScoped<ITokenHasher, Sha256TokenHasher>();
            services.AddScoped<IAuditLogger, DbAuditLogger>();
            services.AddScoped<INotificationService, DbNotificationService>();
            services.AddScoped<CurrentAuthorizationTargetResolver>();
            services.AddScoped<INotificationTargetResolver>(provider => provider.GetRequiredService<CurrentAuthorizationTargetResolver>());
            services.AddScoped<INotificationOpenService, NotificationOpenService>();
            services.AddScoped<Application.Search.ISearchService, DbSearchService>();
            services.AddScoped<Coglatas.Application.Audit.IAuditQueryService, DbAuditQueryService>();
            services.AddSingleton<IClock, SystemClock>();
        }
    }
}
