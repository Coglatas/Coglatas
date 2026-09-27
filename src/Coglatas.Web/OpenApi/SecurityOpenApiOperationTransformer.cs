using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Coglatas.Web.OpenApi;

/// <summary>
/// Describes cross-cutting authentication, authorization, CSRF, and request
/// validation responses that execute outside controller action return types.
/// </summary>
public sealed class SecurityOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public const string CookieSchemeName = "CookieAuth";
    private const string AuthenticationCookieName = ".Coglatas.Auth";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var document = context.Document ??
            throw new InvalidOperationException("OpenAPI operation transformer requires its document context.");
        EnsureCookieSecurityScheme(document);
        ConfigureAuthorizationResponses(operation, context, document);
        ConfigureValidationResponses(operation, context);
        ConfigureRequestBody(operation, context);
        ConfigureKnownErrorContent(operation);
        return Task.CompletedTask;
    }

    private static void ConfigureAuthorizationResponses(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        OpenApiDocument document)
    {
        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;
        var hasAuthorizationBoundary = endpointMetadata.OfType<IAuthorizeData>().Any();
        var allowsAnonymousTransport = endpointMetadata.OfType<IAllowAnonymous>().Any();
        var requiresAuthorization = hasAuthorizationBoundary && !allowsAnonymousTransport;

        if (requiresAuthorization)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(CookieSchemeName, document)] = []
            });
        }

        if (hasAuthorizationBoundary)
        {
            // A small set of canonical command actions deliberately allows the
            // transport through so the application layer can return its typed
            // 401 envelope. They retain the controller authorization boundary.
            AddResponse(operation, "401", "Authentication is required.");
            AddResponse(operation, "403", "Authentication, authorization, or CSRF validation failed.");
            AddResponse(operation, "404", "The protected resource is absent or not visible to the current actor.");
        }
        else if (RequiresCsrf(context.Description.HttpMethod))
        {
            AddResponse(operation, "403", "CSRF validation failed for an authenticated unsafe request.");
        }
    }

    private static void ConfigureValidationResponses(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context)
    {
        if (context.Description.RelativePath?.Contains('{') == true)
        {
            AddResponse(operation, "404", "The route value is invalid or the addressed resource does not exist.");
        }

        if (context.Description.ParameterDescriptions.Count > 0)
        {
            AddResponse(operation, "400", "The request parameters or body are invalid.");
        }

        // The legacy body-scoped project-create endpoint is deliberately
        // fail-closed until the canonical Workspace-root create contract is
        // available. ProjectService.CreateAsync therefore owns an explicit
        // DependencyUnavailable / 503 result; keep the generated security
        // contract aligned with that intentional application state.
        if (IsLegacyProjectCreate(context))
        {
            AddResponse(operation, "503", "Project creation is temporarily unavailable.");
        }
    }

    private static bool IsLegacyProjectCreate(OpenApiOperationTransformerContext context) =>
        context.Description.HttpMethod is { } method &&
        HttpMethods.IsPost(method) &&
        string.Equals(
            context.Description.RelativePath?.TrimEnd('/'),
            "api/projects",
            StringComparison.OrdinalIgnoreCase);

    private static void ConfigureRequestBody(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context)
    {
        if (operation.RequestBody is null)
        {
            return;
        }

        PreserveMultipartRequiredProperties(operation, context);

        // ApiExplorer includes the legacy text/json formatter media type,
        // but the production request pipeline rejects it with 415. Keep
        // the authoritative security contract aligned with runtime input.
        operation.RequestBody.Content?.Remove("text/json");
        AddResponse(operation, "415", "The request content type is not supported.");
    }

    private static void PreserveMultipartRequiredProperties(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context)
    {
        // ApiExplorer flattens form DTOs and can omit their property-level
        // Required attributes. Preserve those runtime validation rules in
        // the multipart schema used by clients and scanners.
        var content = operation.RequestBody?.Content;
        if (content is null ||
            !content.TryGetValue("multipart/form-data", out var multipart) ||
            multipart?.Schema is not OpenApiSchema formSchema)
        {
            return;
        }

        foreach (var parameter in context.Description.ActionDescriptor.Parameters)
        {
            foreach (var property in RequiredProperties(parameter.ParameterType))
            {
                var propertyName = SerializedPropertyName(property, context);
                if (formSchema.Properties?.ContainsKey(propertyName) != true)
                {
                    continue;
                }

                var required = formSchema.Required ??= new HashSet<string>();
                required.Add(propertyName);
            }
        }
    }

    private static string SerializedPropertyName(
        PropertyInfo property,
        OpenApiOperationTransformerContext context)
    {
        var explicitName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        var jsonOptions = context.ApplicationServices?.GetService(typeof(IOptions<JsonOptions>)) as IOptions<JsonOptions>;
        return jsonOptions?.Value.JsonSerializerOptions.PropertyNamingPolicy?.ConvertName(property.Name)
            ?? property.Name;
    }

    private static IEnumerable<PropertyInfo> RequiredProperties(Type parameterType) =>
        parameterType.GetProperties()
            .Where(property => property.GetCustomAttribute<RequiredAttribute>() is not null);

    private static void ConfigureKnownErrorContent(OpenApiOperation operation)
    {
        foreach (var status in new[] { "400", "401", "403", "404", "415" })
        {
            if (operation.Responses?.ContainsKey(status) == true)
            {
                AddErrorResponseContent(operation, status);
            }
        }
    }

    private static void EnsureCookieSecurityScheme(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        if (!document.Components.SecuritySchemes.ContainsKey(CookieSchemeName))
        {
            document.Components.SecuritySchemes.Add(
                CookieSchemeName,
                new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Cookie,
                    Name = AuthenticationCookieName,
                    Description = "Coglatas Portal authenticated session cookie."
                });
        }
    }

    private static void AddResponse(OpenApiOperation operation, string status, string description)
    {
        operation.Responses ??= new OpenApiResponses();
        if (!operation.Responses.ContainsKey(status))
        {
            operation.Responses.Add(status, new OpenApiResponse { Description = description });
        }

        AddErrorResponseContent(operation, status);
    }

    private static void AddErrorResponseContent(OpenApiOperation operation, string status)
    {
        // Framework model validation and status-code handling emit RFC 9457
        // Problem Details, while authorization and CSRF middleware emit the
        // existing JSON error envelope. Preserve action-specific media types
        // and document both cross-cutting wire formats.
        if (operation.Responses?.TryGetValue(status, out var describedResponse) == true &&
            describedResponse is OpenApiResponse response)
        {
            response.Content ??= new Dictionary<string, OpenApiMediaType>();
            if (!response.Content.ContainsKey("application/json"))
            {
                response.Content.Add("application/json", new OpenApiMediaType());
            }

            if (!response.Content.ContainsKey("application/problem+json"))
            {
                response.Content.Add("application/problem+json", new OpenApiMediaType());
            }
        }
    }

    private static bool RequiresCsrf(string? method) =>
        !string.IsNullOrWhiteSpace(method) &&
        !HttpMethods.IsGet(method) &&
        !HttpMethods.IsHead(method) &&
        !HttpMethods.IsOptions(method) &&
        !HttpMethods.IsTrace(method);
}
