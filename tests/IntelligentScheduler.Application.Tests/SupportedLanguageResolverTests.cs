using System.Globalization;
using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class SupportedLanguageResolverTests
{
    private static readonly string[] Supported =
    [
        "ru", "uk", "en", "nb", "de", "es", "zh", "ja", "ko", "fr", "pt", "pl", "hi", "it", "id", "tr"
    ];

    [Theory]
    [InlineData("uk-UA", "uk")]
    [InlineData("de-DE", "de")]
    [InlineData("nb-NO", "nb")]
    [InlineData("no-NO", "nb")]
    [InlineData("es-MX", "es")]
    [InlineData("zh-CN", "zh")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("fr-CA", "fr")]
    [InlineData("pt-BR", "pt")]
    [InlineData("pl-PL", "pl")]
    [InlineData("hi-IN", "hi")]
    [InlineData("it-IT", "it")]
    [InlineData("id-ID", "id")]
    [InlineData("tr-TR", "tr")]
    public void NewProfile_UsesSupportedSystemLanguage(string culture, string expected)
        => Assert.Equal(expected, SupportedLanguageResolver.Resolve(null, CultureInfo.GetCultureInfo(culture), Supported));

    [Fact]
    public void NewProfile_FallsBackToEnglishForUnsupportedSystemLanguage()
        => Assert.Equal("en", SupportedLanguageResolver.Resolve(string.Empty, CultureInfo.GetCultureInfo("ar-SA"), Supported));

    [Fact]
    public void ExistingPreference_WinsOverSystemLanguage()
        => Assert.Equal("ru", SupportedLanguageResolver.Resolve("ru-RU", CultureInfo.GetCultureInfo("de-DE"), Supported));
}
