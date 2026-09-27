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
