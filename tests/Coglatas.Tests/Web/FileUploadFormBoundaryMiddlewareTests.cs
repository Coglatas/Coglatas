using System.Text;
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

    [Fact]
    public async Task ArtifactVersionAcceptsFileAndChangeNote()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            new Dictionary<string, StringValues> { ["ChangeNote"] = "Revised" },
            "/api/artifacts/596b2ae6-e11c-4d12-a072-a80dc4deab19/versions",
            ("File", "sample.txt", "text/plain", "payload"u8.ToArray()));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ArtifactVersionRejectsUnexpectedFieldsBeforeMvcBinding()
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
                ["ChangeNote"] = "Revised",
                ["unexpected"] = "value"
            },
            "/api/artifacts/596b2ae6-e11c-4d12-a072-a80dc4deab19/versions",
            ("File", "sample.txt", "text/plain", "payload"u8.ToArray()));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task ArtifactVersionRejectsTextFieldNamedFile()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            new Dictionary<string, StringValues> { ["file"] = "not-a-file-part" },
            "/api/artifacts/596b2ae6-e11c-4d12-a072-a80dc4deab19/versions");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task ArtifactVersionRejectsUnexpectedRawMultipartBeforeMvcBinding()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        const string boundary = "security-test-boundary";
        var body = string.Join("\r\n",
            $"--{boundary}",
            "Content-Disposition: form-data; name=\"unexpected\"",
            "",
            "value",
            $"--{boundary}",
            "Content-Disposition: form-data; name=\"file\"",
            "",
            "not-a-file-part",
            $"--{boundary}",
            "Content-Disposition: form-data; name=\"File\"; filename=\"sample.txt\"",
            "Content-Type: text/plain",
            "",
            "payload",
            $"--{boundary}--",
            "");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = $"/api/artifacts/{Guid.NewGuid():D}/versions";
        context.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task ArtifactVersionLeavesUnsupportedFormMediaTypeToMvc()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            new Dictionary<string, StringValues> { ["ChangeNote"] = "Revised" },
            $"/api/artifacts/{Guid.NewGuid():D}/versions");
        context.Request.ContentType = "application/x-www-form-urlencoded";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ArtifactVersionRejectsDuplicateChangeNotes()
    {
        var nextCalled = false;
        var middleware = new FileUploadFormBoundaryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            new Dictionary<string, StringValues> { ["ChangeNote"] = new StringValues(new[] { "one", "two" }) },
            $"/api/artifacts/{Guid.NewGuid():D}/versions",
            ("File", "sample.txt", "text/plain", "payload"u8.ToArray()));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(
        Dictionary<string, StringValues> fields,
        params (string Name, string FileName, string ContentType, byte[] Content)[] files)
        => CreateContext(fields, "/api/files", files);

    private static DefaultHttpContext CreateContext(
        Dictionary<string, StringValues> fields,
        string path,
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
                Path = path,
                ContentType = "multipart/form-data; boundary=test"
            }
        };
        context.Features.Set<IFormFeature>(new FormFeature(form));
        context.Response.Body = new MemoryStream();
        return context;
    }
}
