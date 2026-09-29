using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Coglatas.Application.Artifacts;
using Coglatas.Application.Common;
using Coglatas.Application.Files;
using Coglatas.Web.Controllers;
using Coglatas.Web.OpenApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Coglatas.Tests.Artifacts;

public sealed class ArtifactUploadContractTests
{
    [Theory]
    [InlineData(typeof(UploadArtifactVersionForm))]
    [InlineData(typeof(UploadAttachmentForm))]
    public async Task Multipart_schema_preserves_required_file(Type formType)
    {
        var schema = new OpenApiSchema
        {
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                ["changeNote"] = new OpenApiSchema { Type = JsonSchemaType.String }
            }
        };
        var operation = new OpenApiOperation
        {
            RequestBody = new OpenApiRequestBody
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new() { Schema = schema }
                }
            }
        };
        var services = new ServiceCollection();
        services.Configure<JsonOptions>(options =>
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        await using var serviceProvider = services.BuildServiceProvider();
        var context = new OpenApiOperationTransformerContext
        {
            DocumentName = "v1",
            Document = new OpenApiDocument(),
            ApplicationServices = serviceProvider,
            Description = new ApiDescription
            {
                HttpMethod = "POST",
                ActionDescriptor = new ActionDescriptor
                {
                    EndpointMetadata = [],
                    Parameters = [new ParameterDescriptor { Name = "form", ParameterType = formType }]
                }
            }
        };

        await new SecurityOpenApiOperationTransformer().TransformAsync(operation, context, CancellationToken.None);

        Assert.NotNull(schema.Required);
        Assert.Contains("file", schema.Required);
        Assert.DoesNotContain("changeNote", schema.Required);
    }

    [Fact]
    public async Task Request_body_keeps_supported_json_media_types_and_documents_415()
    {
        var operation = new OpenApiOperation
        {
            RequestBody = new OpenApiRequestBody
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new(),
                    ["application/*+json"] = new(),
                    ["text/json"] = new()
                }
            }
        };
        var context = new OpenApiOperationTransformerContext
        {
            DocumentName = "v1",
            Document = new OpenApiDocument(),
            ApplicationServices = null!,
            Description = new ApiDescription
            {
                HttpMethod = "POST",
                ActionDescriptor = new ActionDescriptor
                {
                    EndpointMetadata = [],
                    Parameters = []
                }
            }
        };

        await new SecurityOpenApiOperationTransformer().TransformAsync(operation, context, CancellationToken.None);

        Assert.NotNull(operation.RequestBody.Content);
        Assert.Contains("application/json", operation.RequestBody.Content.Keys);
        Assert.Contains("application/*+json", operation.RequestBody.Content.Keys);
        Assert.DoesNotContain("text/json", operation.RequestBody.Content.Keys);
        Assert.NotNull(operation.Responses);
        Assert.Contains("415", operation.Responses.Keys);
    }

    [Fact]
    public void Upload_forms_require_a_file()
    {
        foreach (var form in new object[] { new UploadArtifactVersionForm(), new UploadAttachmentForm() })
        {
            var errors = new List<ValidationResult>();
            Assert.False(Validator.TryValidateObject(form, new ValidationContext(form), errors, true));
            Assert.Contains(errors, error => error.MemberNames.Contains("File"));
        }
    }

    [Fact]
    public async Task Missing_file_is_rejected_before_calling_service()
    {
        var controller = new ArtifactsController(null!);
        Assert.IsType<BadRequestObjectResult>(await controller.UploadVersion(Guid.NewGuid(), new(), CancellationToken.None));
    }

    [Fact]
    public async Task Attachment_missing_file_is_rejected_before_calling_service()
    {
        var controller = new AttachmentsController(null!);
        Assert.IsType<BadRequestObjectResult>(await controller.Upload(new(), CancellationToken.None));
    }

    [Theory]
    [InlineData("Artifact not found.", StatusCodes.Status404NotFound)]
    [InlineData("Empty files are not allowed.", StatusCodes.Status400BadRequest)]
    public async Task Upload_preserves_missing_resource_and_validation_distinction(string error, int status)
    {
        using var stream = new MemoryStream([1]);
        var controller = new ArtifactsController(new ArtifactStub(error));
        var form = new UploadArtifactVersionForm
        {
            File = new FormFile(stream, 0, 1, "File", "report.txt")
            {
                Headers = new HeaderDictionary(),
                ContentType = "text/plain"
            }
        };

        var result = Assert.IsType<ObjectResult>(await controller.UploadVersion(Guid.NewGuid(), form, CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
    }

    private sealed class ArtifactStub(string error) : IArtifactService
    {
        public Task<Result<ArtifactVersionResponse>> UploadVersionAsync(Guid artifactId, UploadArtifactVersionInput input, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<ArtifactVersionResponse>.Failure(error));

        public Task<Result<IReadOnlyList<ArtifactListItemResponse>>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(ListAsync));

        public Task<Result<ArtifactDetailResponse>> CreateAsync(Guid projectId, CreateArtifactRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(CreateAsync));

        public Task<Result<ArtifactDetailResponse>> GetAsync(Guid artifactId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(GetAsync));

        public Task<Result<ArtifactDetailResponse>> UpdateAsync(Guid artifactId, UpdateArtifactRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(UpdateAsync));

        public Task<Result> DeleteAsync(Guid artifactId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(DeleteAsync));

        public Task<Result<IReadOnlyList<ArtifactVersionResponse>>> ListVersionsAsync(Guid artifactId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(ListVersionsAsync));

        public Task<Result<FileDownloadResponse>> DownloadVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(DownloadVersionAsync));

        public Task<Result> DeleteVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(DeleteVersionAsync));
    }
}
