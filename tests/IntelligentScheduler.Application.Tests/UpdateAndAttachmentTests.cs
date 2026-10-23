using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

/// <summary>
/// Version comparison decides whether a user is told about a release at all, and attachment
/// classification decides whether a file can be picked in the first place. Both are pure and
/// both are the kind of thing that silently regresses.
/// </summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("1.3.0", "1.2.0", 1)]
    [InlineData("1.2.0", "1.3.0", -1)]
    [InlineData("1.2.0", "1.2.0", 0)]
    [InlineData("v1.3.0", "1.2.0", 1)]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0", "1.99.99", 1)]
    [InlineData("1.2.0.1", "1.2.0", 1)]
    public void ComparesDottedVersionsNumerically(string left, string right, int expected)
        => Assert.Equal(expected, Math.Sign(IUpdateService.CompareVersions(left, right)));

    [Fact]
    public void APreReleaseNeverOutranksItsOwnRelease()
        => Assert.Equal(0, IUpdateService.CompareVersions("1.3.0-beta.2", "1.3.0"));

    [Fact]
    public void APreReleaseStillBeatsTheOlderRelease()
        => Assert.True(IUpdateService.CompareVersions("1.3.0-beta.1", "1.2.0") > 0);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void UnreadableVersionsCountAsZeroRatherThanThrowing(string? value)
        => Assert.True(IUpdateService.CompareVersions("0.0.1", value) > 0);
}

public sealed class AttachmentSupportTests
{
    [Theory]
    [InlineData("photo.PNG")]
    [InlineData("scan.jpeg")]
    [InlineData("shot.webp")]
    [InlineData("old.bmp")]
    [InlineData("loop.gif")]
    [InlineData("phone.jfif")]
    public void RecognisesEveryImageFormatTheAppReEncodes(string name)
        => Assert.Equal(AttachmentKind.Image, AttachmentSupport.Classify(name));

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("notes.DOCX")]
    [InlineData("plan.md")]
    [InlineData("rows.csv")]
    [InlineData("feed.ics")]
    public void RecognisesTheDocumentFormatsTheAppExtractsTextFrom(string name)
        => Assert.Equal(AttachmentKind.Document, AttachmentSupport.Classify(name));

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("movie.mp4")]
    [InlineData("binary")]
    [InlineData("")]
    public void RefusesAnythingItCannotRead(string name)
        => Assert.Equal(AttachmentKind.Unsupported, AttachmentSupport.Classify(name));

    [Fact]
    public void PickerPatternsCoverExactlyTheSupportedExtensions()
    {
        Assert.Equal(AttachmentSupport.ImageExtensions.Length, AttachmentSupport.ImagePatterns.Length);
        Assert.All(AttachmentSupport.ImagePatterns, pattern => Assert.StartsWith("*.", pattern));
        Assert.Equal(AttachmentSupport.DocumentExtensions.Length, AttachmentSupport.DocumentPatterns.Length);
        Assert.All(AttachmentSupport.DocumentPatterns, pattern => Assert.StartsWith("*.", pattern));
    }
}
