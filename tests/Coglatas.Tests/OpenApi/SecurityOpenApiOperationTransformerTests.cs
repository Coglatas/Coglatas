using Coglatas.Web.OpenApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Coglatas.Tests.OpenApi;

public sealed class SecurityOpenApiOperationTransformerTests
{
    [Fact]
    public async Task Public_operation_without_authorization_metadata_emits_explicit_empty_security()
    {
        var operation = new OpenApiOperation();
        var context = CreateContext();

        await new SecurityOpenApiOperationTransformer().TransformAsync(operation, context, CancellationToken.None);

        Assert.NotNull(operation.Security);
        Assert.Empty(operation.Security);
    }

    [Fact]
    public async Task AllowAnonymous_emits_explicit_empty_operation_security()
    {
        var operation = new OpenApiOperation();
        var context = CreateContext(new AllowAnonymousAttribute());

        await new SecurityOpenApiOperationTransformer().TransformAsync(operation, context, CancellationToken.None);

        Assert.NotNull(operation.Security);
        Assert.Empty(operation.Security);
    }

    [Fact]
    public async Task AllowAnonymous_overrides_authorization_security_requirement()
    {
        var operation = new OpenApiOperation();
        var context = CreateContext(new AuthorizeAttribute(), new AllowAnonymousAttribute());

        await new SecurityOpenApiOperationTransformer().TransformAsync(operation, context, CancellationToken.None);

        Assert.NotNull(operation.Security);
        Assert.Empty(operation.Security);
    }

    [Theory]
    [InlineData("api/me/tasks")]
    [InlineData("api/me/tasks/counts")]
    [InlineData("api/auth/login")]
    public async Task Operations_document_empty_request_uri_too_long_response(string relativePath)
    {
        var operation = new OpenApiOperation();
        var context = CreateContext();
        context.Description.RelativePath = relativePath;

        await new SecurityOpenApiOperationTransformer().TransformAsync(operation, context, CancellationToken.None);

        var response = Assert.IsType<OpenApiResponse>(operation.Responses!["414"]);
        Assert.False(string.IsNullOrWhiteSpace(response.Description));
        Assert.True(response.Content is null || response.Content.Count == 0);
    }

    [Theory]
    [InlineData("api/search")]
    [InlineData("api/search/message-authors")]
    public async Task Search_query_parameters_exclude_postgresql_unsafe_nul(string relativePath)
    {
        var qSchema = new OpenApiSchema { Type = JsonSchemaType.String };
        var operation = new OpenApiOperation
        {
            Parameters =
            [
                new OpenApiParameter
                {
                    Name = "Q",
                    In = ParameterLocation.Query,
                    Schema = qSchema
                }
            ]
        };
        var context = CreateContext();
        context.Description.RelativePath = relativePath;

        await new SecurityOpenApiOperationTransformer().TransformAsync(operation, context, CancellationToken.None);

        Assert.Equal("^[^\\u0000]*$", qSchema.Pattern);
    }

    private static OpenApiOperationTransformerContext CreateContext(params object[] endpointMetadata) =>
        new()
        {
            DocumentName = "v1",
            Document = new OpenApiDocument(),
            ApplicationServices = null!,
            Description = new ApiDescription
            {
                HttpMethod = "GET",
                ActionDescriptor = new ActionDescriptor
                {
                    EndpointMetadata = endpointMetadata.ToList(),
                    Parameters = []
                }
            }
        };
}
