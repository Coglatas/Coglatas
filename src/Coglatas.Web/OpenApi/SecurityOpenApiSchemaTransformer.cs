using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Nodes;
using Coglatas.Application.Announcements;
using Coglatas.Application.Events;
using Coglatas.Application.Integrations;
using Coglatas.Application.Messaging;
using Coglatas.Application.Projects;
using Coglatas.Application.TenantAdministration;
using Coglatas.Application.Workspaces;
using Coglatas.Web.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Coglatas.Web.OpenApi;

/// <summary>
/// Describes custom JSON converter-backed PATCH sentinel types by their actual
/// wire representation so generated OpenAPI remains useful to fuzzers/scanners.
/// </summary>
public sealed class SecurityOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    private static readonly string[] ConversationScopeIdentifiers =
        ["workspaceId", "projectId", "parentConversationId"];

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ApplyPropertyValidationMetadata(schema, context);
        ConfigureRequestShape(schema, context.JsonTypeInfo.Type);
        ConfigureWireSchema(schema, context.JsonTypeInfo.Type);
        return Task.CompletedTask;
    }

    private static void ApplyPropertyValidationMetadata(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context)
    {
        // Preserve validation metadata on record properties as well as
        // constructor parameters. Required strings must not generate blanks.
        foreach (var property in context.JsonTypeInfo.Type.GetProperties())
        {
            var name = context.JsonTypeInfo.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
            if (schema.Properties?.TryGetValue(name, out var value) != true || value is not OpenApiSchema field)
            {
                continue;
            }

            ApplyPropertyValidation(schema, name, property, field);
        }
    }

    private static void ApplyPropertyValidation(
        OpenApiSchema schema,
        string name,
        PropertyInfo property,
        OpenApiSchema field)
    {
        if (property.PropertyType == typeof(string) &&
            property.GetCustomAttribute<RequiredAttribute>() is { AllowEmptyStrings: false })
        {
            ConfigureNonBlankString(schema, name, field.MaxLength);
        }
        if (property.GetCustomAttribute<RegularExpressionAttribute>() is { } pattern)
        {
            field.Pattern = pattern.Pattern;
        }
        if (property.GetCustomAttribute<MinLengthAttribute>() is { } minimum)
        {
            field.MinLength = Math.Max(field.MinLength ?? 0, minimum.Length);
        }
        if (property.GetCustomAttribute<EmailAddressAttribute>() is not null)
        {
            field.Format = "email";
        }
    }

    private static void ConfigureRequestShape(OpenApiSchema schema, Type requestType)
    {
        if (requestType == typeof(UploadAttachmentForm) ||
            requestType == typeof(UploadArtifactVersionForm))
        {
            // Upload forms accept only their declared scalar fields and the File part.
            // Closing the schema keeps positive fuzz cases aligned with the actual
            // multipart contract; negative fuzzing still exercises extra fields.
            schema.AdditionalPropertiesAllowed = false;
            return;
        }

        if (requestType == typeof(UpdateTenantSettingsRequest))
        {
            // PATCH semantics permit every field to be omitted. The generated
            // constructor-based schema otherwise marks nullable parameters as
            // required, which contradicts the service's merge behavior.
            schema.Required?.Clear();
            return;
        }

        if (requestType == typeof(CreateWorkspaceRequest))
        {
            // Workspace creation normalizes omitted description and icon to null.
            // Keep the generated contract aligned with that existing runtime behavior.
            schema.Required?.Remove("description");
            schema.Required?.Remove("icon");
            return;
        }

        if (requestType == typeof(CreateApiTokenRequest))
        {
            // Scopes and expiry are optional at runtime. IntegrationService
            // normalizes an omitted ScopesJson to [] and permits no expiry.
            // Constructor-based OpenAPI generation otherwise marks nullable
            // positional-record parameters as required.
            schema.Required?.Remove("scopesJson");
            schema.Required?.Remove("expiresAt");
            return;
        }

        if (requestType == typeof(CreateConversationRequest))
        {
            schema.OneOf =
            [
                ConversationScope("DirectMessage", "workspaceId"),
                ConversationScope("ProjectChannel", "workspaceId", "projectId"),
                ConversationScope("Thread", "parentConversationId")
            ];
            return;
        }

        if (requestType == typeof(CreateEventRequest))
        {
            ConfigureNonBlankString(schema, "title");
            var scopes = new[] { "workspaceId", "groupId", "projectId" };
            schema.OneOf = scopes.Select(selected => (IOpenApiSchema)new OpenApiSchema
            {
                Required = new HashSet<string> { selected },
                Properties = scopes.ToDictionary(name => name, name => (IOpenApiSchema)(name == selected
                    ? new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                    : new OpenApiSchema { Type = JsonSchemaType.Null }))
            }).ToList();
        }
    }

    private static void ConfigureWireSchema(OpenApiSchema schema, Type requestType)
    {
        if (requestType == typeof(IFormFile))
        {
            schema.MinLength = 1;
            return;
        }
        if (requestType == typeof(OptionalDateTimeOffset))
        {
            schema.Type = JsonSchemaType.String | JsonSchemaType.Null;
            schema.Format = "date-time";
            return;
        }
        if (requestType == typeof(OptionalString))
        {
            schema.Type = JsonSchemaType.String | JsonSchemaType.Null;
            schema.Format = null;
            return;
        }
        if (requestType == typeof(CreateAnnouncementRequest) ||
            requestType == typeof(UpdateAnnouncementRequest))
        {
            // AnnouncementService and AnnouncementContentContract trim before
            // rejecting blank values; advertise that constraint to API scanners.
            ConfigureNonBlankString(schema, "title");
            var bodyMaximumLength =
                schema.Properties?.TryGetValue("body", out var bodyProperty) == true &&
                bodyProperty is OpenApiSchema bodySchema
                    ? bodySchema.MaxLength ?? AnnouncementContentContract.MaximumPersistedLength
                    : AnnouncementContentContract.MaximumPersistedLength;
            ConfigureNonBlankString(schema, "body", bodyMaximumLength);
            return;
        }
        if (requestType == typeof(AnnouncementActionLink))
        {
            // A present action must be complete and safe; null remains the
            // representation for an omitted CTA or attachment.
            ConfigureNonBlankString(schema, "label", AnnouncementContentContract.MaximumLabelLength);
            ConfigureSafeActionUrl(schema);
        }
    }

    private static OpenApiSchema ConversationScope(string type, params string[] requiredIds)
    {
        var selectedIds = requiredIds.ToHashSet(StringComparer.Ordinal);
        var properties = ConversationScopeIdentifiers.ToDictionary(name => name, name => (IOpenApiSchema)(
            selectedIds.Contains(name)
                ? new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                    Not = new OpenApiSchema { Enum = [JsonValue.Create(Guid.Empty.ToString())] }
                }
                : new OpenApiSchema { Type = JsonSchemaType.Null }));
        properties["type"] = new OpenApiSchema { Enum = [JsonValue.Create(type)] };
        return new OpenApiSchema
        {
            Required = requiredIds.Append("type").ToHashSet(),
            Properties = properties
        };
    }

    private static void ConfigureNonBlankString(OpenApiSchema schema, string propertyName, int? maximumLength = null)
    {
        if (schema.Properties?.TryGetValue(propertyName, out var property) != true ||
            property is not OpenApiSchema stringSchema)
        {
            return;
        }

        stringSchema.MinLength = 1;
        stringSchema.Pattern = "[\\s\\S]*\\S[\\s\\S]*";
        if (maximumLength.HasValue)
        {
            stringSchema.MaxLength = maximumLength;
        }
    }

    private static void ConfigureSafeActionUrl(OpenApiSchema schema)
    {
        if (schema.Properties?.TryGetValue("url", out var property) != true ||
            property is not OpenApiSchema urlSchema)
        {
            return;
        }

        urlSchema.MinLength = 1;
        urlSchema.MaxLength = AnnouncementContentContract.MaximumUrlLength;
        urlSchema.Pattern = "^(?:/(?!/)(?!\\.\\.(?:/|$))(?!.*?/\\.\\.(?:/|$))[^\\s\\\\]*|[hH][tT][tT][pP][sS]://(?:\\[[0-9A-Fa-f:.]+\\]|[^\\s/:?#@\\\\][^\\s/:?#@\\\\]*)(?::[0-9]{1,5})?(?:[/?#][^\\s\\\\]*)?)$";
    }
}