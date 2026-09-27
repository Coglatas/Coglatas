using Coglatas.Application;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure;
using Coglatas.Infrastructure.Files;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Web;
using Coglatas.Web.Configuration;
using Coglatas.Web.Extensions;
using Coglatas.Web.Middleware;
using Coglatas.Web.Security;
using Coglatas.Web.Realtime;
using Coglatas.Web.Notifications;
using Coglatas.Application.Notifications;
using Coglatas.Web.Testing;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var avMigInspection = builder.Configuration.GetValue<bool>("AvMigContractVerify");
if (avMigInspection && !builder.Environment.IsEnvironment("Test"))
{
    throw new InvalidOperationException("AV-MIG contract inspection is Test-only.");
}
var browserSmokeSeedEnabled = BrowserSmokeTestBoundary.IsEnabled(
    builder.Environment.EnvironmentName,
    builder.Configuration.GetValue<bool>("BrowserSmokeSeed:Enabled") ||
    builder.Configuration.GetValue<bool>("COGLATAS_BROWSER_SMOKE_SEED_ENABLED"));
var demoDatasetSeedEnabled = DemoDatasetBoundary.IsEnabled(
    builder.Environment.EnvironmentName,
    builder.Configuration.GetValue<bool>("DemoDataset:Enabled") ||
    builder.Configuration.GetValue<bool>("COGLATAS_DEMO_DATASET_ENABLED"));
var browserSmokeResponseGateEnabled =
    browserSmokeSeedEnabled &&
    builder.Configuration.GetValue<bool>("COGLATAS_BROWSER_SMOKE_RESPONSE_GATE_ENABLED");

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddWebServices(builder.Configuration);
if (browserSmokeResponseGateEnabled)
{
    builder.Services.AddSingleton<BrowserSmokeResponseGateRegistry>();
}

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    var keysDirectory = Path.GetFullPath(dataProtectionKeysPath);
    Directory.CreateDirectory(keysDirectory);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
}

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        var security = builder.Configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
        HttpSecurityPolicy.ConfigureAuthenticationCookie(options, security);
        options.EventsType = typeof(DbSessionCookieAuthenticationEvents);
    });

builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.Configure<RealtimeOptions>(builder.Configuration.GetSection("Realtime"));
builder.Services.Configure<TaskDeadlineDigestWorkerOptions>(builder.Configuration.GetSection("TaskDeadlineDigest"));
builder.Services.Configure<AnnouncementPublisherWorkerOptions>(builder.Configuration.GetSection("AnnouncementPublisher"));
builder.Services.AddSingleton<TaskDeadlineDigestDiagnostics>();
builder.Services.AddScoped<ITaskDeadlineDigestScheduler, TaskDeadlineDigestScheduler>();
builder.Services.AddScoped<ITaskDeadlineDigestGenerator, TaskDeadlineDigestGenerator>();
builder.Services.AddScoped<ITaskDeadlineDigestFailureHandler, TaskDeadlineDigestFailureHandler>();
builder.Services.AddSingleton<HubSubscriptionRegistry>();
builder.Services.AddSingleton<RealtimeDiagnostics>();
builder.Services.AddSingleton<IRealtimeConnectionInvalidator, RealtimeConnectionInvalidator>();
builder.Services.AddScoped<IHubSubscriptionAuthorizer, HubSubscriptionAuthorizer>();
builder.Services.AddScoped<IRealtimeDispatchAuthorizer, RealtimeDispatchAuthorizer>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<TaskDeadlineDigestWorker>();
builder.Services.AddHostedService<AnnouncementPublisherWorker>();
builder.Services.AddRateLimiter(HttpSecurityPolicy.ConfigureRateLimiting);

if (ForwardedHeadersConfiguration.ShouldTrustForwardedHeaders(builder.Configuration))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        ForwardedHeadersConfiguration.Configure(options, builder.Configuration));
}

var app = builder.Build();

if (ForwardedHeadersConfiguration.ShouldTrustForwardedHeaders(app.Configuration))
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<GlobalExceptionHandlingMiddleware>();
app.UseMiddleware<WpcApiContractMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestBodyLimitMiddleware>();

var tenancyOptions = app.Services.GetRequiredService<TenancyOptions>();
var seedAdminEnabled = builder.Configuration.GetValue<bool>("COGLATAS_SEED_ADMIN_ENABLED");
var bootstrapAdminEmail =
    builder.Configuration["COGLATAS_BOOTSTRAP_ADMIN_EMAIL"] ??
    builder.Configuration["BootstrapAdmin:Email"];
if (tenancyOptions.SeedOnStartup ||
    tenancyOptions.AppMode == AppMode.OnPremSingleTenant ||
    builder.Configuration.GetValue<bool>("UiShell:SeedOnStartup") ||
    browserSmokeSeedEnabled ||
    demoDatasetSeedEnabled ||
    seedAdminEnabled ||
    !string.IsNullOrWhiteSpace(bootstrapAdminEmail))
{
    await using var scope = app.Services.CreateAsyncScope();
    var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>();
    currentTenant.SetPlatformScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var defaultTenant = await AppDbContextSeed.SeedDefaultTenantAsync(dbContext, tenancyOptions);
    await AppDbContextSeed.SeedPlansAsync(dbContext);
    if (seedAdminEnabled)
    {
        var seedAdminEmail = builder.Configuration["COGLATAS_SEED_ADMIN_EMAIL"];
        var seedAdminUsername = builder.Configuration["COGLATAS_SEED_ADMIN_USERNAME"];
        var seedAdminPassword = builder.Configuration["COGLATAS_SEED_ADMIN_PASSWORD"];
        if (string.IsNullOrWhiteSpace(seedAdminEmail) ||
            string.IsNullOrWhiteSpace(seedAdminUsername) ||
            string.IsNullOrWhiteSpace(seedAdminPassword))
        {
            throw new InvalidOperationException(
                "Coglatas seed admin is enabled but COGLATAS_SEED_ADMIN_EMAIL, COGLATAS_SEED_ADMIN_USERNAME, or COGLATAS_SEED_ADMIN_PASSWORD is missing.");
        }

        await AppDbContextSeed.SeedLocalAdminAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            defaultTenant.Id,
            seedAdminEmail,
            seedAdminPassword,
            seedAdminUsername);
    }
    else if (!string.IsNullOrWhiteSpace(bootstrapAdminEmail))
    {
        await AppDbContextSeed.EnsureBootstrapAdminAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            defaultTenant.Id,
            bootstrapAdminEmail,
            builder.Configuration["COGLATAS_BOOTSTRAP_ADMIN_PASSWORD"],
            builder.Configuration["COGLATAS_BOOTSTRAP_ADMIN_DISPLAY_NAME"] ?? builder.Configuration["BootstrapAdmin:DisplayName"]);
    }
    else if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("LocalAdmin:SeedOnStartup"))
    {
        var localAdminPassword = builder.Configuration["LocalAdmin:Password"];
        if (string.IsNullOrWhiteSpace(localAdminPassword))
        {
            throw new InvalidOperationException("LocalAdmin:Password is required when LocalAdmin:SeedOnStartup is enabled.");
        }

        await AppDbContextSeed.SeedLocalAdminAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            defaultTenant.Id,
            builder.Configuration["LocalAdmin:Email"] ?? "admin@example.com",
            localAdminPassword,
            builder.Configuration["LocalAdmin:DisplayName"] ?? "Local Admin");
    }

    if (builder.Configuration.GetValue<bool>("UiShell:SeedOnStartup"))
    {
        await AppDbContextSeed.SeedUiShellAsync(dbContext, defaultTenant.Id);
    }

    if (browserSmokeSeedEnabled)
    {
        var smokeEmail =
            builder.Configuration["BrowserSmokeSeed:Email"] ??
            builder.Configuration["COGLATAS_BROWSER_SMOKE_EMAIL"];
        var smokePassword =
            builder.Configuration["BrowserSmokeSeed:Password"] ??
            builder.Configuration["COGLATAS_BROWSER_SMOKE_PASSWORD"];
        if (string.IsNullOrWhiteSpace(smokeEmail) || string.IsNullOrWhiteSpace(smokePassword))
        {
            throw new InvalidOperationException(
                "Browser smoke seed is enabled but BrowserSmokeSeed:Email or BrowserSmokeSeed:Password is missing.");
        }

        await AppDbContextSeed.SeedBrowserSmokeAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<IFileStorageService>(),
            defaultTenant.Id,
            smokeEmail,
            smokePassword);
        await BrowserSmokeNotificationFixtureSeed.EnsureRevocableProjectAccessAsync(
            dbContext,
            defaultTenant.Id);
    }

    if (demoDatasetSeedEnabled)
    {
        var demoEmail =
            builder.Configuration["DemoDataset:Email"] ??
            builder.Configuration["COGLATAS_DEMO_DATASET_EMAIL"] ??
            DemoDatasetSeed.OwnerEmail;
        var demoPassword =
            builder.Configuration["DemoDataset:Password"] ??
            builder.Configuration["COGLATAS_DEMO_DATASET_PASSWORD"];
        if (string.IsNullOrWhiteSpace(demoPassword))
        {
            throw new InvalidOperationException(
                "Demo dataset seed is enabled but DemoDataset:Password is missing.");
        }

        await DemoDatasetSeed.SeedAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<IFileStorageService>(),
            defaultTenant.Id,
            demoEmail,
            demoPassword);
    }
}

var securityOptions = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
if (securityOptions.EnableHsts && !app.Environment.IsDevelopment())
{
    app.UseHsts();
}

if (securityOptions.RequireHttps)
{
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/health"),
        branch => branch.UseHttpsRedirection());
}

var webRootPath = string.IsNullOrEmpty(app.Environment.WebRootPath)
    ? Path.Combine(app.Environment.ContentRootPath, "wwwroot")
    : app.Environment.WebRootPath;

app.Use(async (context, next) =>
{
    if ((AngularSpaFallback.IsAppPath(context.Request.Path) || AngularSpaFallback.IsAngularIndexPath(context.Request.Path)) &&
        !AngularSpaFallback.HasAngularBuild(webRootPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = AngularSpaFallback.AppRequestPath,
    OnPrepareResponse = context =>
        AngularSpaFallback.ApplyStaticFileHeaders(context.Context.Response, context.Context.Request.Path)
});

app.UseRouting();
app.UseCors(HttpSecurityPolicy.CorsPolicyName);
app.UseMiddleware<TenantResolutionMiddleware>();
// SignalR upgrades its same-origin transport to WebSockets. Register the
// WebSocket middleware before authentication and endpoint execution so the
// Hub can establish the upgrade after a successful negotiate request.
app.UseWebSockets();
app.UseAuthentication();
if (securityOptions.EnableRateLimiting)
{
    app.UseRateLimiter();
}
if (securityOptions.EnableCsrfProtection)
{
    app.Services.GetRequiredService<CsrfProtectionState>().MarkMiddlewareActive();
    app.UseMiddleware<CsrfProtectionMiddleware>();
}
app.UseAuthorization();
if (browserSmokeResponseGateEnabled)
{
    app.UseMiddleware<BrowserSmokeResponseGateMiddleware>();
}

app.MapControllers();
if (browserSmokeResponseGateEnabled)
{
    var responseGates = app.MapGroup("/internal/browser-smoke/response-gates")
        .RequireAuthorization();
    responseGates.MapPost("/{gateId}/arm", ArmBrowserSmokeResponseGate);
    responseGates.MapGet("/{gateId}", GetBrowserSmokeResponseGate);
    responseGates.MapPost("/{gateId}/release", ReleaseBrowserSmokeResponseGate);

    // These fixture endpoints stay inside the same explicitly enabled Test
    // boundary as the response gates. They exercise the production
    // notification/outbox pipeline without enabling tasks.notificationsV1.
    var notificationFixture = app.MapGroup("/internal/browser-smoke/notifications")
        .RequireAuthorization();
    notificationFixture.MapPost("/task", StageBrowserSmokeTaskNotificationAsync);
    notificationFixture.MapGet("/events/{eventId:guid}", GetBrowserSmokeOutboxEventAsync);
}

// Runtime flags are loaded as a same-origin external script so the Angular
// bootstrap remains compatible with the production CSP (script-src 'self').
app.MapGet("/api/ui/runtime-config.js", async (
    IFeatureFlagService featureFlags,
    CancellationToken cancellationToken) =>
{
    var flags = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["realtime.signalR"] = await featureFlags.IsEnabledAsync(FeatureKeys.RealtimeSignalR, cancellationToken),
        ["tasks.kanbanV1"] = await featureFlags.IsEnabledAsync(FeatureKeys.KanbanV1, cancellationToken),
        ["tasks.ganttV1"] = await featureFlags.IsEnabledAsync(FeatureKeys.GanttV1, cancellationToken)
    };
    return Results.Text(
        $"window.__AIP_FEATURE_FLAGS__ = {JsonSerializer.Serialize(flags)};",
        "text/javascript; charset=utf-8");
});
app.MapHub<AppHub>("/hubs/app");

app.MapGet("/", () => Results.Redirect($"{AngularSpaFallback.AppRequestPath}/", permanent: false))
    .ExcludeFromDescription();

app.MapGet("/health", () => Results.Redirect("/health/ready", permanent: false))
    .ExcludeFromDescription();

app.MapGet("/health/live", () => Results.Ok(new { status = "OK" }));

app.MapGet("/health/realtime", async (
    Coglatas.Application.Realtime.IOutboxEventRepository outbox,
    ICurrentTenantAccessor currentTenant,
    RealtimeDiagnostics diagnostics,
    IOptions<RealtimeOptions> realtimeOptions,
    CancellationToken cancellationToken) =>
{
    currentTenant.SetPlatformScope();
    var configured = realtimeOptions.Value;
    var state = await outbox.GetDiagnosticsAsync(
        DateTimeOffset.UtcNow.AddSeconds(-Math.Max(1, configured.ProcessingLockSeconds)),
        cancellationToken);
    var counters = diagnostics.Snapshot();
    return Results.Ok(new
    {
        status = "OK",
        backlog = new
        {
            pending = state.PendingCount,
            retryScheduled = state.RetryScheduledCount,
            deadLetter = state.DeadLetterCount,
            oldestPendingAt = state.OldestPendingAt,
            staleProcessing = state.StaleProcessingCount
        },
        dispatcher = new
        {
            dispatchSuccess = counters.DispatchSuccessCount,
            dispatchFailure = counters.DispatchFailureCount,
            failures = counters.DispatcherFailureCount
        },
        subscriptions = new { denials = counters.SubscriptionDenialCount }
    });
});

app.MapGet("/health/task-deadline-digests", async (
    ITaskDeadlineDigestRepository repository,
    ICurrentTenantAccessor currentTenant,
    TaskDeadlineDigestDiagnostics diagnostics,
    CancellationToken cancellationToken) =>
{
    currentTenant.SetPlatformScope();
    var store = await repository.GetDiagnosticsAsync(DateTimeOffset.UtcNow, cancellationToken);
    var counters = diagnostics.Snapshot();
    return Results.Ok(new
    {
        status = "OK",
        ledger = new
        {
            due = store.DueCount,
            claimed = store.ClaimedCount,
            succeeded = store.SucceededCount,
            failed = store.FailedCount,
            oldestDueAt = store.OldestDueAt,
            oldestClaimedAt = store.OldestClaimedAt
        },
        worker = new
        {
            scheduled = counters.Scheduled,
            claimed = counters.Claimed,
            succeeded = counters.Succeeded,
            succeededWithoutCandidates = counters.SucceededWithoutCandidates,
            failures = counters.Failures,
            terminalFailures = counters.TerminalFailures,
            claimLosses = counters.ClaimLosses,
            invalidTimeZones = counters.InvalidTimeZones,
            invalidPreferences = counters.InvalidPreferences,
            operatorRestarts = counters.OperatorRestarts
        }
    });
});

app.MapGet("/favicon.ico", Results.NoContent)
    .ExcludeFromDescription();

app.MapGet("/health/ready", async (
    AppDbContext dbContext,
    IOptions<FileStorageOptions> fileStorageOptions,
    TenancyOptions healthTenancyOptions,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var databaseOk = await IsDatabaseReadyAsync(dbContext, cancellationToken);
    var migrationsOk = databaseOk && await AreMigrationsReadyAsync(dbContext, cancellationToken);
    var storageOk = await IsFileStorageReadyAsync(fileStorageOptions.Value, cancellationToken);
    var dataProtectionOk = IsDataProtectionReady(configuration);
    var defaultTenantOk = databaseOk && await IsDefaultTenantReadyAsync(dbContext, healthTenancyOptions, cancellationToken);
    var ready = databaseOk && migrationsOk && storageOk && dataProtectionOk && defaultTenantOk;

    return ready
        ? Results.Ok(new { status = "OK", checks = new { database = "OK", migrations = "OK", fileStorage = "OK", dataProtection = "OK", defaultTenant = "OK" } })
        : Results.Json(new { status = "Unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

AngularSpaFallback.MapEndpointFallback(app, webRootPath);

if (avMigInspection)
{
    await AvMigRuntimeContract.VerifyAsync(app,
        builder.Configuration["AvMigContractPolicy"] ?? throw new InvalidOperationException("AvMigContractPolicy is required."));
    await app.DisposeAsync();
    return;
}

app.Run();

static IResult ArmBrowserSmokeResponseGate(
    string gateId,
    BrowserSmokeResponseGateArmRequest request,
    BrowserSmokeResponseGateRegistry registry,
    ICurrentUser currentUser)
{
    if (!TryGetSyntheticBrowserSmokeActor(currentUser, out var actorUserId) ||
        !Guid.TryParseExact(gateId, "N", out var parsedGateId))
    {
        return Results.NotFound();
    }

    if (!BrowserSmokeResponseGateRegistry.IsAllowedTarget(request.Method, request.Path))
    {
        return Results.BadRequest(new { error = "Invalid response gate target." });
    }

    return registry.TryArm(parsedGateId, actorUserId, request.Method, request.Path)
        ? Results.Ok(new { state = "armed" })
        : Results.Conflict(new { error = "A response gate is already active." });
}

static IResult GetBrowserSmokeResponseGate(
    string gateId,
    BrowserSmokeResponseGateRegistry registry,
    ICurrentUser currentUser)
{
    if (!TryGetSyntheticBrowserSmokeActor(currentUser, out var actorUserId) ||
        !Guid.TryParseExact(gateId, "N", out var parsedGateId))
    {
        return Results.NotFound();
    }

    var snapshot = registry.GetSnapshot(parsedGateId, actorUserId);
    return snapshot is null
        ? Results.NotFound()
        : Results.Ok(snapshot);
}

static IResult ReleaseBrowserSmokeResponseGate(
    string gateId,
    BrowserSmokeResponseGateRegistry registry,
    ICurrentUser currentUser)
{
    if (!TryGetSyntheticBrowserSmokeActor(currentUser, out var actorUserId) ||
        !Guid.TryParseExact(gateId, "N", out var parsedGateId) ||
        !registry.TryRelease(parsedGateId, actorUserId))
    {
        return Results.NotFound();
    }

    return Results.Ok(new { state = "released" });
}

static async Task<IResult> StageBrowserSmokeTaskNotificationAsync(
    BrowserSmokeTaskNotificationRequest request,
    AppDbContext dbContext,
    INotificationService notifications,
    IUnitOfWork unitOfWork,
    ICurrentTenant currentTenant,
    ICurrentUser currentUser,
    CancellationToken cancellationToken)
{
    if (!TryGetSyntheticBrowserSmokeActor(currentUser, out var actorUserId) || !currentTenant.IsAvailable)
    {
        return Results.NotFound();
    }

    var target = await dbContext.TaskItems
        .Where(task =>
            task.TenantId == currentTenant.TenantId &&
            task.DeletedAt == null &&
            task.Title == BrowserSmokeNotificationFixture.TaskTitle)
        .Join(
            dbContext.Projects.Where(project =>
                project.TenantId == currentTenant.TenantId &&
                project.DeletedAt == null &&
                project.Status == ProjectStatus.Active &&
                project.Slug == BrowserSmokeNotificationFixture.ProjectSlug),
            task => task.ProjectId,
            project => project.Id,
            (task, project) => new
            {
                TaskId = task.Id,
                task.WorkspaceId,
                ProjectId = project.Id,
                project.OwnerUserId
            })
        .SingleOrDefaultAsync(cancellationToken);
    if (target is null || target.OwnerUserId != actorUserId)
    {
        return Results.NotFound();
    }

    var recipient = await dbContext.Users.SingleOrDefaultAsync(user =>
        user.NormalizedEmail == BrowserSmokeNotificationFixture.RecipientEmail.ToUpperInvariant(),
        cancellationToken);
    if (recipient is null || !await dbContext.ProjectMembers.AnyAsync(member =>
            member.TenantId == currentTenant.TenantId &&
            member.ProjectId == target.ProjectId &&
            member.UserId == recipient.Id,
            cancellationToken))
    {
        return Results.NotFound();
    }

    var notificationId = await notifications.StageTaskByLogicalKeyAsync(
        recipient.Id,
        NotificationType.TaskAssigned,
        BrowserSmokeNotificationFixture.NotificationTitle,
        target.TaskId,
        $"browser-smoke:pr07d:{Guid.NewGuid():N}",
        cancellationToken);
    var notification = dbContext.Notifications.Local.SingleOrDefault(item => item.Id == notificationId);
    var eventItem = dbContext.OutboxEvents.Local.SingleOrDefault(item =>
        item is { EventType: "Notifications.NotificationCreated.v1", AggregateType: "Notification" } &&
        item.AggregateId == notificationId);
    if (notification is null || eventItem is null)
    {
        return Results.Problem("The browser-smoke notification fixture could not stage its durable event.");
    }

    var delay = Math.Clamp(
        request.DispatchDelaySeconds ?? 0,
        0,
        BrowserSmokeNotificationFixture.MaximumDispatchDelaySeconds);
    if (delay > 0)
    {
        eventItem.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(delay);
    }

    await unitOfWork.SaveChangesAsync(cancellationToken);
    return Results.Ok(new BrowserSmokeTaskNotificationResponse(
        notificationId,
        eventItem.Id,
        target.WorkspaceId,
        target.ProjectId,
        target.TaskId,
        notification.StateVersion,
        delay));
}

static async Task<IResult> GetBrowserSmokeOutboxEventAsync(
    Guid eventId,
    AppDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentUser currentUser,
    CancellationToken cancellationToken)
{
    if (!TryGetSyntheticBrowserSmokeActor(currentUser, out var actorUserId) || !currentTenant.IsAvailable)
    {
        return Results.NotFound();
    }

    var eventItem = await dbContext.OutboxEvents.AsNoTracking().SingleOrDefaultAsync(item =>
        item.Id == eventId &&
        item.TenantId == currentTenant.TenantId &&
        item.EventType == "Notifications.NotificationCreated.v1" &&
        item.AggregateType == "Notification",
        cancellationToken);
    if (eventItem is null)
    {
        return Results.NotFound();
    }

    var notification = await dbContext.Notifications.AsNoTracking().SingleOrDefaultAsync(item =>
        item.Id == eventItem.AggregateId &&
        item.TenantId == currentTenant.TenantId &&
        item.RelatedEntityType == "TaskItem" &&
        item.RelatedEntityId.HasValue,
        cancellationToken);
    if (notification?.RelatedEntityId is not { } taskId)
    {
        return Results.NotFound();
    }

    var task = await dbContext.TaskItems.AsNoTracking().SingleOrDefaultAsync(item =>
        item.TenantId == currentTenant.TenantId &&
        item.Id == taskId &&
        item.DeletedAt == null &&
        item.Title == BrowserSmokeNotificationFixture.TaskTitle,
        cancellationToken);
    if (task is null || !await dbContext.Projects.AnyAsync(project =>
            project.TenantId == currentTenant.TenantId &&
            project.Id == task.ProjectId &&
            project.OwnerUserId == actorUserId &&
            project.Slug == BrowserSmokeNotificationFixture.ProjectSlug,
            cancellationToken))
    {
        return Results.NotFound();
    }

    return Results.Ok(new BrowserSmokeOutboxEventSnapshot(
        eventItem.Id,
        eventItem.Status.ToString(),
        eventItem.AttemptCount,
        eventItem.LastErrorCode));
}

static bool TryGetSyntheticBrowserSmokeActor(ICurrentUser currentUser, out Guid actorUserId)
{
    actorUserId = currentUser.UserId ?? Guid.Empty;
    return currentUser.IsAuthenticated &&
           actorUserId != Guid.Empty &&
           currentUser.Email?.EndsWith("@example.test", StringComparison.OrdinalIgnoreCase) == true;
}

static async Task<bool> IsDatabaseReadyAsync(AppDbContext dbContext, CancellationToken cancellationToken)
{
    try
    {
        return await dbContext.Database.CanConnectAsync(cancellationToken);
    }
    catch
    {
        return false;
    }
}

static async Task<bool> AreMigrationsReadyAsync(AppDbContext dbContext, CancellationToken cancellationToken)
{
    try
    {
        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        return !pendingMigrations.Any();
    }
    catch
    {
        return false;
    }
}

static async Task<bool> IsFileStorageReadyAsync(FileStorageOptions options, CancellationToken cancellationToken)
{
    try
    {
        return options.Provider switch
        {
            "LocalFileSystem" => await IsLocalFileStorageReadyAsync(options.RootPath, cancellationToken),
            _ => false
        };
    }
    catch
    {
        return false;
    }
}

static async Task<bool> IsLocalFileStorageReadyAsync(string rootPath, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(rootPath))
    {
        return false;
    }

    var fullPath = Path.GetFullPath(rootPath);
    Directory.CreateDirectory(fullPath);
    var probePath = Path.Combine(fullPath, $".health-{Guid.NewGuid():N}");
    try
    {
        await File.WriteAllTextAsync(probePath, "OK", cancellationToken);
        return true;
    }
    finally
    {
        File.Delete(probePath);
    }
}

static bool IsDataProtectionReady(IConfiguration configuration)
{
    var keysPath = configuration["DataProtection:KeysPath"];
    if (string.IsNullOrWhiteSpace(keysPath))
    {
        return true;
    }

    try
    {
        var fullPath = Path.GetFullPath(keysPath);
        Directory.CreateDirectory(fullPath);
        var probePath = Path.Combine(fullPath, $".health-{Guid.NewGuid():N}");
        File.WriteAllText(probePath, "OK");
        File.Delete(probePath);
        return true;
    }
    catch
    {
        return false;
    }
}

static async Task<bool> IsDefaultTenantReadyAsync(AppDbContext dbContext, TenancyOptions tenancyOptions, CancellationToken cancellationToken)
{
    if (tenancyOptions.AppMode != AppMode.OnPremSingleTenant)
    {
        return true;
    }

    if (string.IsNullOrWhiteSpace(tenancyOptions.DefaultTenantSlug))
    {
        return false;
    }

    try
    {
        var slug = tenancyOptions.DefaultTenantSlug.Trim().ToLowerInvariant();
        return await dbContext.Tenants.AnyAsync(tenant => tenant.Slug == slug && tenant.Status == TenantStatus.Active, cancellationToken);
    }
    catch
    {
        return false;
    }
}
