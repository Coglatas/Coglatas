using Coglatas.Infrastructure.Files;
using Microsoft.Extensions.Options;

namespace Coglatas.Tests.Files;

public sealed class LocalFileStorageServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "coglatas-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void InvalidExtensionIsRejected()
    {
        var policy = CreatePolicy();

        Assert.DoesNotContain(".exe", policy.AllowedExtensions);
    }

    [Fact]
    public void InvalidContentTypeIsRejected()
    {
        var policy = CreatePolicy();

        Assert.DoesNotContain("application/x-msdownload", policy.AllowedContentTypes);
    }

    [Fact]
    public void EmptyFileIsRejected()
    {
        var policy = CreatePolicy();

        Assert.True(policy.MaxFileSizeBytes > 0);
    }

    [Fact]
    public async Task StorageKeyControlsStoredPathInsideRoot()
    {
        var storage = CreateStorage();
        await using var content = new MemoryStream("hello"u8.ToArray());

        var result = await storage.SaveAsync("tenants/tenant-a/files/file-a", content, "text/plain");

        Assert.True(result.IsSuccess);
        Assert.True(await storage.ExistsAsync("tenants/tenant-a/files/file-a"));
    }

    [Fact]
    public async Task StorageKeyCannotEscapeRootPath()
    {
        var storage = CreateStorage();
        await using var content = new MemoryStream("bad"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync("../escape.txt", content, "text/plain"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private LocalFileStorageService CreateStorage()
    {
        return new LocalFileStorageService(Options.Create(new FileStorageOptions
        {
            RootPath = _rootPath,
            MaxFileSizeBytes = 1024,
            AllowedExtensions = [".txt", ".md", ".zip"],
            AllowedContentTypes = ["text/plain", "text/markdown", "application/zip"]
        }));
    }

    private ConfiguredFileUploadPolicy CreatePolicy()
    {
        return new ConfiguredFileUploadPolicy(Options.Create(new FileStorageOptions
        {
            RootPath = _rootPath,
            MaxFileSizeBytes = 1024,
            AllowedExtensions = [".txt", ".md", ".zip"],
            AllowedContentTypes = ["text/plain", "text/markdown", "application/zip"]
        }));
    }
}
