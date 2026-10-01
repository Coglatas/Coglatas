using System.Text;

namespace Coglatas.Application.Files;

public static class FileNameSanitizer
{
    private const int MaxFileNameLength = 260;
    private const string FallbackFileName = "upload";
    private static readonly char[] AdditionalInvalidFileNameChars = ['<', '>', ':', '"', '|', '?', '*'];

    public static string SanitizeOriginalFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FallbackFileName;
        }

        var normalized = fileName.Replace('\\', '/');
        var leafName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(leafName))
        {
            return FallbackFileName;
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(leafName.Length);
        foreach (var character in leafName)
        {
            sanitized.Append(IsUnsafeFileNameCharacter(character, invalidFileNameChars) ? '_' : character);
        }

        var result = sanitized
            .ToString()
            .Replace("..", "_", StringComparison.Ordinal)
            .Trim();

        if (string.IsNullOrWhiteSpace(result) || result is "." or "..")
        {
            return FallbackFileName;
        }

        if (result.Length > MaxFileNameLength)
        {
            result = result[..MaxFileNameLength].Trim();
        }

        return string.IsNullOrWhiteSpace(result) ? FallbackFileName : result;
    }

    private static bool IsUnsafeFileNameCharacter(char character, char[] invalidFileNameChars)
    {
        return character is '/' or '\\' ||
            char.IsControl(character) ||
            invalidFileNameChars.Contains(character) ||
            AdditionalInvalidFileNameChars.Contains(character);
    }
}
