using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Coglatas.Application.Announcements;
using Coglatas.Application.Messaging;
using Coglatas.Web.OpenApi;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Coglatas.Tests.OpenApi;

public sealed class SecurityOpenApiSchemaTransformerTests
{
    [Theory]
    [InlineData(typeof(CreateAnnouncementRequest))]
    [InlineData(typeof(UpdateAnnouncementRequest))]
    public async Task Announcement_body_uses_persisted_limit_when_schema_has_no_maximum(Type requestType)
    {
        var schema = await Transform(
            requestType,
            new Dictionary<string, OpenApiSchema>
            {
                ["body"] = new() { Type = JsonSchemaType.String }
            });

        var body = Assert.IsType<OpenApiSchema>(schema.Properties!["body"]);
        Assert.Equal(AnnouncementContentContract.MaximumPersistedLength, body.MaxLength);
    }

    [Theory]
    [InlineData(typeof(CreateAnnouncementRequest))]
    [InlineData(typeof(UpdateAnnouncementRequest))]
    public async Task Announcement_body_preserves_existing_schema_maximum(Type requestType)
    {
        const int generatedMaximum = 4_096;
        var schema = await Transform(
            requestType,
            new Dictionary<string, OpenApiSchema>
            {
                ["body"] = new() { Type = JsonSchemaType.String, MaxLength = generatedMaximum }
            });

        var body = Assert.IsType<OpenApiSchema>(schema.Properties!["body"]);
        Assert.Equal(generatedMaximum, body.MaxLength);
    }

    [Fact]
    public async Task Conversation_scope_one_of_rejects_extra_scope_identifiers()
    {
        var schema = await Transform(typeof(CreateConversationRequest));
        var variants = Assert.IsAssignableFrom<IList<IOpenApiSchema>>(schema.OneOf)
            .Select(variant => Assert.IsType<OpenApiSchema>(variant))
            .ToArray();
        Assert.Equal(3, variants.Length);

        var directMessage = FindVariant(variants, "DirectMessage");
        AssertScopeType(directMessage, "workspaceId", JsonSchemaType.String);
        AssertScopeType(directMessage, "projectId", JsonSchemaType.Null);
        AssertScopeType(directMessage, "parentConversationId", JsonSchemaType.Null);

        var projectChannelPayload = new Dictionary<string, object?>
        {
            ["type"] = "ProjectChannel",
            ["workspaceId"] = Guid.NewGuid().ToString(),
            ["projectId"] = Guid.NewGuid().ToString(),
            ["parentConversationId"] = null
        };
        Assert.Single(variants, variant => MatchesVariant(variant, projectChannelPayload));
        Assert.True(MatchesVariant(FindVariant(variants, "ProjectChannel"), projectChannelPayload));

        var invalidDirectMessagePayload = new Dictionary<string, object?>
        {
            ["type"] = "DirectMessage",
            ["workspaceId"] = Guid.NewGuid().ToString(),
            ["projectId"] = Guid.NewGuid().ToString(),
            ["parentConversationId"] = null
        };
        Assert.DoesNotContain(variants, variant => MatchesVariant(variant, invalidDirectMessagePayload));
    }

    private static OpenApiSchema FindVariant(IEnumerable<OpenApiSchema> variants, string type)
        => variants.Single(variant =>
            variant.Properties?.TryGetValue("type", out var property) == true &&
            property is OpenApiSchema typeSchema &&
            typeSchema.Enum?.SingleOrDefault()?.ToString() == type);

    private static void AssertScopeType(OpenApiSchema variant, string propertyName, JsonSchemaType expected)
    {
        Assert.NotNull(variant.Properties);
        var property = Assert.IsType<OpenApiSchema>(variant.Properties[propertyName]);
        Assert.Equal(expected, property.Type);
        if (expected == JsonSchemaType.String)
        {
            Assert.Equal("uuid", property.Format);
        }
    }

    private static bool MatchesVariant(OpenApiSchema variant, IReadOnlyDictionary<string, object?> payload)
    {
        if (variant.Required is not null &&
            variant.Required.Any(name => !payload.TryGetValue(name, out var value) || value is null))
        {
            return false;
        }

        if (variant.Properties is null)
        {
            return true;
        }

        foreach (var (name, property) in variant.Properties)
        {
            if (!payload.TryGetValue(name, out var value) || property is not OpenApiSchema propertySchema)
            {
                continue;
            }

            if (propertySchema.Enum is { Count: > 0 } &&
                !propertySchema.Enum.Any(candidate => candidate?.ToString() == value?.ToString()))
            {
                return false;
            }

            if (value is null)
            {
                if (propertySchema.Type != JsonSchemaType.Null)
                {
                    return false;
                }
            }
            else if (propertySchema.Type == JsonSchemaType.Null)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<OpenApiSchema> Transform(
        Type type,
        IReadOnlyDictionary<string, OpenApiSchema>? properties = null)
    {
        var schema = new OpenApiSchema
        {
            Properties = properties?.ToDictionary(
                pair => pair.Key,
                pair => (IOpenApiSchema)pair.Value)
        };
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var context = new OpenApiSchemaTransformerContext
        {
            DocumentName = "v1",
            ParameterDescription = null,
            JsonPropertyInfo = null,
            JsonTypeInfo = options.GetTypeInfo(type),
            ApplicationServices = EmptyServiceProvider.Instance
        };

        await new SecurityOpenApiSchemaTransformer().TransformAsync(schema, context, default);
        return schema;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
