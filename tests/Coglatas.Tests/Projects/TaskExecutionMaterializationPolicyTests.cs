using System.Security.Cryptography;
using System.Text;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;

namespace Coglatas.Tests.Projects;

public sealed class TaskExecutionMaterializationPolicyTests
{
    [Theory]
    [Trait("Scope", "Issue462")]
    [InlineData("text/plain", "text/plain")]
    [InlineData("TEXT/MARKDOWN; charset=utf-8", "text/markdown")]
    [InlineData("application/json", null)]
    [InlineData("application/octet-stream", null)]
    public void MediaTypePolicyAllowsOnlyTheApprovedTextBoundary(
        string contentType,
        string? expected) =>
        Assert.Equal(
            expected,
            FirstPartyProjectFilesMaterializationV1.NormalizeSupportedMediaType(contentType));

    [Fact]
    [Trait("Scope", "Issue462")]
    public async Task Utf8MaterializationIsBoundedAndHashesTheConsumedBytes()
    {
        var bytes = "Contest project file\nsecond line"u8.ToArray();
        await using var stream = new MemoryStream(bytes);

        var materialized = await FirstPartyProjectFilesMaterializationV1.ReadUtf8Async(
            stream,
            "text/plain; charset=utf-8",
            FirstPartyProjectFilesMaterializationV1.MaxSourceBytes);

        Assert.NotNull(materialized);
        Assert.Equal("text/plain", materialized.MediaType);
        Assert.Equal(bytes.LongLength, materialized.ByteCount);
        Assert.Equal(Encoding.UTF8.GetString(bytes), materialized.Text);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            materialized.ContentSha256);
    }

    [Fact]
    [Trait("Scope", "Issue462")]
    public async Task Utf8MaterializationRejectsOversizeAndInvalidTextWithoutReturningContent()
    {
        await using var oversize = new MemoryStream(new byte[9]);
        Assert.Null(await FirstPartyProjectFilesMaterializationV1.ReadUtf8Async(
            oversize,
            "text/plain",
            maximumBytes: 8));

        await using var invalidUtf8 = new MemoryStream([0xff, 0xfe, 0xfd]);
        Assert.Null(await FirstPartyProjectFilesMaterializationV1.ReadUtf8Async(
            invalidUtf8,
            "text/markdown",
            FirstPartyProjectFilesMaterializationV1.MaxSourceBytes));
    }

    [Fact]
    [Trait("Scope", "Issue462")]
    public void BrowserRequestAndDurableProvenanceContainNoSourceAuthorityOrRawContent()
    {
        Assert.Empty(typeof(RequestTaskExecutionRunRequest).GetProperties());

        var propertyNames = typeof(TaskExecutionMaterializedSource)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forbidden = new[]
        {
            "FileName",
            "OriginalFileName",
            "StorageKey",
            "FilePath",
            "Url",
            "Text",
            "Content",
            "Bytes",
            "Credentials",
            "ProviderConfiguration"
        };

        Assert.All(forbidden, property => Assert.DoesNotContain(property, propertyNames));
        Assert.Contains(nameof(TaskExecutionMaterializedSource.FileObjectId), propertyNames);
        Assert.Contains(nameof(TaskExecutionMaterializedSource.AttachmentId), propertyNames);
        Assert.Contains(nameof(TaskExecutionMaterializedSource.ContentSha256), propertyNames);
    }
}
