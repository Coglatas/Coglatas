using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Auth;
using Coglatas.Application.Messaging;
using Coglatas.Application.Notifications;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Web.Audit;
using Coglatas.Web.Configuration;
using Coglatas.Web.OpenApi;
using Coglatas.Web.Security;
using Coglatas.Web.Services;
using Coglatas.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Coglatas.Web.Extensions;

public static class DependencyInjection
{
    // Returning IServiceCollection is intentional for standard DI fluent composition,
    // even though the current composition root does not consume the return value.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IServiceCollection AddWebServices(this IServiceCollection services, IConfiguration configuration)
    {
        var security = configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
        services.Configure<TenancyOptions>(configuration.GetSection("Tenancy"));
        services.Configure<SecurityOptions>(configuration.GetSection("Security"));
        services.Configure<AuditPackageExportWorkerOptions>(configuration.GetSection("AuditPackageExport"));
        services.AddSingleton(configuration.GetSection("CommunicationSafety").Get<CommunicationSafetyOptions>() ?? new CommunicationSafetyOptions());
        services.AddSingleton(configuration.GetSection("Security").Get<AuthSecurityOptions>() ?? new AuthSecurityOptions());
        services.Configure<PlatformOptions>(configuration.GetSection("Platform"));
        services.Configure<FeatureOptions>(configuration.GetSection("Features"));
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TenancyOptions>>().Value);
        services.AddSingleton<CsrfProtectionState>();
        services.AddCors(options => HttpSecurityPolicy.ConfigureCors(options, security));
        services.AddHsts(HttpSecurityPolicy.ConfigureHsts);
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = security.MaxMultipartBodySizeBytes;
        });
        services.AddAntiforgery(options => HttpSecurityPolicy.ConfigureAntiforgery(options, security));
        services.AddHostedService<StartupConfigurationValidator>();
        services.AddHostedService<HttpSecurityConfigurationValidator>();
        services.AddHostedService<AuditPackageExportWorker>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUserService>();
        services.AddScoped<DbSessionCookieAuthenticationEvents>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, WpcAuthorizationMiddlewareResultHandler>();
        services.AddScoped<ITenantResolver, HttpTenantResolver>();

        // Message notification settings are tenant/user scoped. Replace the
        // generic persistence service at the Web composition root so all
        // Message notification creation passes through the preference policy.
        services.AddScoped<IMessageNotificationPreferenceStore, MessageNotificationPreferenceStore>();
        services.AddScoped<IMessageNotificationPreferenceService, MessageNotificationPreferenceService>();
        services.AddScoped<DbNotificationService>();
        services.Replace(ServiceDescriptor.Scoped<INotificationService, PreferenceAwareNotificationService>());

        // Register the canonical API description without mapping a runtime
        // documentation endpoint. SEC-01 emits this document at build time for
        // security tooling while keeping production OpenAPI exposure disabled.
        services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer<SecurityOpenApiSchemaTransformer>();
            options.AddOperationTransformer<SecurityOpenApiOperationTransformer>();
        });

        services.AddControllers(options =>
            {
                options.Filters.Add<CanonicalProjectsResponseProjectionFilter>();
                options.Filters.Add<StrictQueryParameterFilter>();
            })
            .ConfigureApiBehaviorOptions(options =>
            {
                var defaultFactory = options.InvalidModelStateResponseFactory;
                options.InvalidModelStateResponseFactory = context =>
                {
                    var path = context.HttpContext.Request.Path.Value;
                    if (IsSearchPath(path))
                    {
                        // Query conversion errors can otherwise echo raw Guid,
                        // enum, or date input through ValidationProblemDetails.
                        return new BadRequestObjectResult(ApiEnvelope.Error(
                            context.HttpContext,
                            StatusCodes.Status400BadRequest,
                            "SearchRequestInvalid",
                            "The search parameters are invalid.",
                            "query"));
                    }

                    if (IsCommunicationPollingPath(path))
                    {
                        // Model-binding conversion failures can embed the raw
                        // attempted query value in ValidationProblemDetails.
                        // The SEC-06 active scanner can then make its own test
                        // payload look like server-side PII. Preserve field
                        // ownership while removing attacker-controlled text.
                        return new BadRequestObjectResult(
                            new ValidationProblemDetails(CreateSanitizedModelState(context.ModelState)));
                    }

                    if (IsWpcCreatePath(path, context.HttpContext.Request.Method))
                    {
                        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
                        {
                            return new UnauthorizedObjectResult(ApiEnvelope.Error(
                                context.HttpContext,
                                StatusCodes.Status401Unauthorized,
                                "AuthenticationRequired",
                                "Authentication is required."));
                        }

                        var malformedJson = context.ModelState.Any(entry =>
                            string.Equals(entry.Key, "$", StringComparison.Ordinal) &&
                            entry.Value?.Errors.Any(error =>
                                !error.ErrorMessage.Contains("could not be converted", StringComparison.OrdinalIgnoreCase)) == true);
                        var unsupportedMediaType = context.ModelState.Values
                            .SelectMany(value => value.Errors)
                            .Any(error => string.Equals(
                                error.Exception?.GetType().Name,
                                "UnsupportedContentTypeException",
                                StringComparison.Ordinal));
                        if (unsupportedMediaType)
                        {
                            return new ObjectResult(ApiEnvelope.Error(
                                context.HttpContext,
                                StatusCodes.Status415UnsupportedMediaType,
                                "UnsupportedMediaType",
                                "The request Content-Type is not supported.",
                                "header.Content-Type"))
                            {
                                StatusCode = StatusCodes.Status415UnsupportedMediaType
                            };
                        }
                        return new BadRequestObjectResult(ApiEnvelope.Error(
                            context.HttpContext,
                            StatusCodes.Status400BadRequest,
                            malformedJson ? "MalformedJson" : "ValidationFailed",
                            malformedJson
                                ? "The request body is not valid JSON."
                                : "The request body or parameters are invalid.",
                            "body"));
                    }
                    if (!IsPr06CommandPath(path))
                    {
                        // MVC supplies its default factory through a later
                        // options configurator.  Capturing it here can therefore
                        // legitimately yield null, which used to turn ordinary
                        // data-annotation failures into 500 responses.
                        // InvalidModelStateResponseFactory is annotated non-null, but this
                        // configurator can observe it before MVC's later default configurator.
                        // Keep the runtime fallback that prevents validation failures becoming 500s.
                        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
                        return defaultFactory?.Invoke(context) ??
                            new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState));
                    }

                    var dependency = path?.Contains("/dependencies", StringComparison.OrdinalIgnoreCase) == true;
                    if (context.HttpContext.User.Identity?.IsAuthenticated != true)
                    {
                        return new UnauthorizedObjectResult(new
                        {
                            requestId = context.HttpContext.TraceIdentifier,
                            error = new
                            {
                                code = dependency
                                    ? "TASK_DEPENDENCY_AUTHENTICATION_REQUIRED"
                                    : "GANTT_AUTHENTICATION_REQUIRED",
                                message = "Authentication is required.",
                                target = (string?)null,
                                details = Array.Empty<object>(),
                                redactionApplied = false
                            }
                        });
                    }

                    return new BadRequestObjectResult(new
                    {
                        requestId = context.HttpContext.TraceIdentifier,
                        error = new
                        {
                            code = dependency ? "TASK_DEPENDENCY_INVALID_REQUEST" : "GANTT_INVALID_REQUEST",
                            message = "The request body or parameters are invalid.",
                            target = (string?)null,
                            details = Array.Empty<object>(),
                            redactionApplied = false
                        }
                    });
                };
            });
        return services;
    }

    private static bool IsPr06CommandPath(string? path) =>
        NormalizePath(path).StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase) &&
        (NormalizePath(path).EndsWith("/schedule", StringComparison.OrdinalIgnoreCase) ||
         NormalizePath(path).EndsWith("/progress", StringComparison.OrdinalIgnoreCase) ||
         NormalizePath(path).Contains("/dependencies", StringComparison.OrdinalIgnoreCase));

    private static bool IsSearchPath(string? path)
    {
        var normalized = NormalizePath(path);
        return normalized.Equals("/api/search", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("/api/search/message-authors", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommunicationPollingPath(string? path) =>
        NormalizePath(path).StartsWith("/api/communication/poll/", StringComparison.OrdinalIgnoreCase);

    private static ModelStateDictionary CreateSanitizedModelState(ModelStateDictionary source)
    {
        var sanitized = new ModelStateDictionary();
        foreach (var entry in source)
        {
            var errorCount = entry.Value?.Errors.Count ?? 0;
            for (var index = 0; index < errorCount; index++)
            {
                sanitized.AddModelError(entry.Key, "The supplied value is invalid.");
            }
        }

        if (sanitized.ErrorCount == 0)
        {
            sanitized.AddModelError(string.Empty, "The request parameters are invalid.");
        }

        return sanitized;
    }

    private static bool IsWpcCreatePath(string? path, string method) =>
        HttpMethods.IsPost(method) && ApiEnvelope.IsCanonicalCreatePath(path);

    private static string NormalizePath(string? path) =>
        path?.TrimEnd('/') ?? string.Empty;
}
