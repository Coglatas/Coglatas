using Coglatas.Application.Files;

namespace Coglatas.Tests.Files;

[Trait("Portability", "CrossPlatform")]
public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData("..\\secret.txt", "secret.txt")]
    [InlineData("../secret.txt", "secret.txt")]
    [InlineData("folder/secret.txt", "secret.txt")]
    [InlineData("folder\\secret.txt", "secret.txt")]
    [InlineData(" \t ", "upload")]
    [InlineData("safe\u0001name.txt", "safe_name.txt")]
    [InlineData("C:\\temp\\secret.txt", "secret.txt")]
    public void SanitizeOriginalFileNameReturnsSafeDisplayName(string originalFileName, string expected)
    {
        var sanitized = FileNameSanitizer.SanitizeOriginalFileName(originalFileName);

        Assert.Equal(expected, sanitized);
        Assert.DoesNotContain("..", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("/", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(sanitized, char.IsControl);
    }
}
