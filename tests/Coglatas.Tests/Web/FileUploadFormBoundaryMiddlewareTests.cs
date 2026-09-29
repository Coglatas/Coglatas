using Coglatas.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Coglatas.Tests.Web;

public sealed class FileUploadFormBoundaryMiddlewareTests
{
    [Fact]
    public async Task ValidCanonicalMultipartShapeContinuesPipeline()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            new Dictionary<string, StringValues>
            {
                ["OwnerType"] = "Workspace",
                ["OwnerId"] = Guid.NewGuid().ToString()
            },
            ("File", "sample.txt", "text/plain", "payload"u8.ToArray()));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task UnexpectedScalarFieldIsRejectedBeforeMvcBinding()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            new Dictionary<string, StringValues>
            {
                ["OwnerType"] = "Workspace",
                ["OwnerId"] = Guid.NewGuid().ToString(),
                ["unexpected"] = "value"
            },
            ("File", "sample.txt", "text/plain", "payload"u8.ToArray()));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task TextFieldNamedFileIsRejectedAsAmbiguous()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            new Dictionary<string, StringValues>
            {
                ["OwnerType"] = "Workspace",
                ["OwnerId"] = Guid.NewGuid().ToString(),
                ["File"] = "not-a-file-part"
            },
            ("file", "sample.txt", "text/plain", "payload"u8.ToArray()));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task MultipleFilePartsAreRejected()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            new Dictionary<string, StringValues>
            {
                ["OwnerType"] = "Workspace",
                ["OwnerId"] = Guid.NewGuid().ToString()
            },
            ("File", "one.txt", "text/plain", "one"u8.ToArray()),
            ("File", "two.txt", "text/plain", "two"u8.ToArray()));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(
        Dictionary<string, StringValues> fields,
        params (string Name, string FileName, string ContentType, byte[] Content)[] files)
    {
        var fileCollection = new FormFileCollection();
        foreach (var file in files)
        {
            var stream = new MemoryStream(file.Content);
            fileCollection.Add(new FormFile(stream, 0, stream.Length, file.Name, file.FileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = file.ContentType
            });
        }

        var form = new FormCollection(fields, fileCollection);
        var context = new DefaultHttpContext
        {
            Request =
            {
                Method = HttpMethods.Post,
                Path = "/api/files",
                ContentType = "multipart/form-data; boundary=test"
            }
        };
        context.Features.Set<IFormFeature>(new FormFeature(form));
        context.Response.Body = new MemoryStream();
        return context;
    }
}
