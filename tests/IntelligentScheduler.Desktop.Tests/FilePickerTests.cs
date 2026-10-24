using IntelligentScheduler.Desktop.Platform;
using Xunit;

namespace IntelligentScheduler.Desktop.Tests;

public sealed class FilePickerTests
{
    [Theory]
    [InlineData("/tmp/Theme folder/Моя тема", "json", "/tmp/Theme folder/Моя тема.json")]
    [InlineData("/tmp/theme.daydrive-theme.json", "json", "/tmp/theme.daydrive-theme.json")]
    [InlineData("/tmp/theme.JSON", ".json", "/tmp/theme.JSON")]
    [InlineData("/tmp/theme", null, "/tmp/theme")]
    public void SaveNamesPreserveExistingExtensionsAndUnicode(string path, string? extension, string expected)
        => Assert.Equal(expected, LinuxFilePicker.WithExtension(path, extension));
}
