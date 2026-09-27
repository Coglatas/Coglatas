using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Coglatas.Application.Announcements;
using Coglatas.Application.Auth;
using Coglatas.Web.OpenApi;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Coglatas.Tests.Announcements;

public sealed class AnnouncementOpenApiSchemaTests
{
    [Fact]
    public async Task Invite_schema_preserves_property_validation_metadata()
    {
        var schema = await Transform(typeof(RegisterByInviteRequest), "DisplayName", "Email", "Password", "InviteToken");
        Assert.NotNull(schema.Properties);

        var displayNamePattern = schema.Properties["DisplayName"].Pattern;
        Assert.NotNull(displayNamePattern);
        Assert.DoesNotMatch(displayNamePattern, " ");
        Assert.Equal("email", schema.Properties["Email"].Format);
        Assert.Equal(8, schema.Properties["Password"].MinLength);

        var inviteTokenPattern = schema.Properties["InviteToken"].Pattern;
        Assert.NotNull(inviteTokenPattern);
        Assert.DoesNotMatch(inviteTokenPattern, "");
    }

    [Theory]
    [InlineData(typeof(CreateAnnouncementRequest))]
    [InlineData(typeof(UpdateAnnouncementRequest))]
    public async Task Content_schema_rejects_blank_title_and_body(Type requestType)
    {
        var schema = await Transform(requestType, "title", "body");
        Assert.NotNull(schema.Properties);

        foreach (var field in new[] { "title", "body" })
        {
            var property = Assert.IsType<OpenApiSchema>(schema.Properties[field]);
            Assert.Equal(1, property.MinLength);

            var pattern = property.Pattern;
            Assert.NotNull(pattern);
            Assert.DoesNotMatch(pattern, " \t\n");
            Assert.Matches(pattern, "Announcement");
        }
    }

    [Theory]
    [InlineData("https://?foo", false)]
    [InlineData("https://#fragment", false)]
    [InlineData("https://:", false)]
    [InlineData("https://user@example.com", false)]
    [InlineData("https://example.com:bad", false)]
    [InlineData("/", true)]
    [InlineData("/announcements", true)]
    [InlineData("https://example.com?foo", true)]
    [InlineData("HTTPS://example.com/path", true)]
    [InlineData("https://[::1]/path", true)]
    public async Task Action_url_schema_matches_targeted_runtime_contract(string url, bool expected)
    {
        var schema = await Transform(typeof(AnnouncementActionLink), "label", "url");
        Assert.NotNull(schema.Properties);
        var property = Assert.IsType<OpenApiSchema>(schema.Properties["url"]);
        var pattern = property.Pattern;
        Assert.NotNull(pattern);

        Assert.Equal(expected, AnnouncementContentContract.IsSafeUrl(url));
        Assert.Equal(expected, Regex.IsMatch(url, pattern));
    }

    private static async Task<OpenApiSchema> Transform(Type type, params string[] properties)
    {
        var schema = new OpenApiSchema
        {
            Properties = properties.ToDictionary(name => name,
                _ => (IOpenApiSchema)new OpenApiSchema { Type = JsonSchemaType.String })
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
