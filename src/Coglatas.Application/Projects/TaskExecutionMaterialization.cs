using System.Security.Cryptography;
using System.Text;

namespace Coglatas.Application.Projects;

/// <summary>
/// Canonical bounded text policy for FirstPartyProjectFilesRuntimeV1. It does
/// no source discovery and accepts no browser identity; callers provide a
/// server-authorized stream only after current authorization has been checked.
/// </summary>
public static class FirstPartyProjectFilesMaterializationV1
{
    public const int MaxSourceCount = 16;
    public const int MaxSourceBytes = 256 * 1024;
    public const int MaxTotalBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string? NormalizeSupportedMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator >= 0 ? contentType[..separator] : contentType)
            .Trim()
            .ToLowerInvariant();

        return mediaType is "text/plain" or "text/markdown" ? mediaType : null;
    }

    public static async Task<TaskExecutionMaterializedText?> ReadUtf8Async(
        Stream stream,
        string? contentType,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var mediaType = NormalizeSupportedMediaType(contentType);
        if (mediaType is null || maximumBytes < 0)
        {
            return null;
        }

        var boundedMaximum = Math.Min(maximumBytes, MaxSourceBytes);
        if (stream.CanSeek && stream.Length > boundedMaximum)
        {
            return null;
        }

        using var buffer = new MemoryStream(Math.Min(boundedMaximum, 81920));
        var chunk = new byte[Math.Min(81920, Math.Max(1, boundedMaximum + 1))];
        var total = 0;

        while (true)
        {
            var remainingWithSentinel = boundedMaximum + 1 - total;
            if (remainingWithSentinel <= 0)
            {
                return null;
            }

            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remainingWithSentinel)),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > boundedMaximum)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        var bytes = buffer.ToArray();
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new TaskExecutionMaterializedText(mediaType, hash, bytes.LongLength, text);
    }
}

/// <summary>
/// Ephemeral authorized content passed inside the runtime only. The Text value
/// is never persisted by the materialization boundary.
/// </summary>
public sealed record TaskExecutionMaterializedText(
    string MediaType,
    string ContentSha256,
    long ByteCount,
    string Text);
