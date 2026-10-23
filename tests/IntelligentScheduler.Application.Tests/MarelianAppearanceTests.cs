using System.Text.Json;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class MarelianAppearanceTests
{
    [Theory]
    [InlineData(ShellTheme.Noctalia)]
    [InlineData(ShellTheme.Celestia)]
    [InlineData(ShellTheme.Windows11)]
    [InlineData(ShellTheme.Windows10)]
    [InlineData(ShellTheme.Marelian)]
    public void LegacyColoursMigrateToMarelianWithoutLosingEitherMode(ShellTheme old)
    {
        var settings = new AppSettings
        {
            ShellTheme = old, ShellThemeMode = ShellThemeMode.Light, ThemeColorSource = ThemeColorSource.Custom,
            ThemeColorOverridesJson = JsonSerializer.Serialize(new Dictionary<string, MarelianThemeProfile>
            {
                [$"{old}:Light"] = new() { Accent = "#abc123", Background = "#fedcba" },
                [$"{old}:Dark"] = new() { Accent = "#123abc", Background = "#121212" }
            })
        };
        MarelianAppearance.NormalizeSettings(settings);
        Assert.Equal(ShellTheme.Marelian, settings.ShellTheme);
        var profiles = MarelianAppearance.Read(settings.ThemeColorOverridesJson);
        Assert.Equal(2, profiles.Count);
        Assert.Equal("#ABC123", profiles["Marelian:Light"].Accent);
        Assert.Equal("#FEDCBA", profiles["Marelian:Light"].Background);
        Assert.Equal("#123ABC", profiles["Marelian:Dark"].Accent);
        var once = settings.ThemeColorOverridesJson;
        MarelianAppearance.NormalizeSettings(settings);
        Assert.Equal(once, settings.ThemeColorOverridesJson);
    }

    [Fact]
    public void LegacyCustomPairSurvivesMissingOrInvalidJson()
    {
        var settings = new AppSettings
        {
            ShellTheme = ShellTheme.Celestia, ThemeColorSource = ThemeColorSource.Custom,
            CustomAccent = "#abcdef", CustomBackground = "#123456", ThemeColorOverridesJson = "{broken"
        };
        MarelianAppearance.NormalizeSettings(settings);
        var profile = MarelianAppearance.Profile(settings);
        Assert.Equal("#ABCDEF", profile.Accent);
        Assert.Equal("#123456", profile.Background);
        Assert.Equal(15, profile.WindowRadius);
    }

    [Fact]
    public void StoredProfilesClampInvalidValuesAndKeepValidColourRoles()
    {
        var profile = (new MarelianThemeProfile
        {
            Accent = "red", Background = " #aa123456 ", Surface = "#ABCDEF", TextScale = 20,
            WindowRadius = -1, CardRadius = double.NaN, ShadowStrength = double.PositiveInfinity
        }).Normalize();
        Assert.Null(profile.Accent);
        Assert.Equal("#AA123456", profile.Background);
        Assert.Equal("#ABCDEF", profile.Surface);
        Assert.Equal(1.25, profile.TextScale);
        Assert.Equal(0, profile.WindowRadius);
        Assert.Equal(10, profile.CardRadius);
        Assert.Equal(1, profile.ShadowStrength);
    }

    [Fact]
    public void ThemeFileRoundTripsBothModesAndSharedPreferences()
    {
        var theme = new MarelianThemeFile
        {
            Dark = new() { Surface = "#121212", Text = "#FFFFFF", WindowRadius = 30, TextScale = 1.1, ShadowStrength = 0 },
            Light = new() { Background = "#FFFFFF", Accent = "#123456", ControlRadius = 0 },
            Mode = ShellThemeMode.Light, AccentSource = ThemeColorSource.Wallpaper,
            Font = "builtin:Geologica", CategoryPalette = VisualPalette.ColorBlind,
            Transparency = false, TransparencyIntensity = 60, ReduceMotion = true, MascotColor = "#AA336699"
        };
        Assert.Equal(theme, MarelianThemeFile.Parse(theme.Serialize()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Format\":\"other\"}")]
    [InlineData("{\"Version\":2}")]
    [InlineData("{\"Dark\":null}")]
    [InlineData("{\"Dark\":{\"Accent\":\"oops\"}}")]
    [InlineData("{\"Dark\":{\"TextScale\":2}}")]
    [InlineData("{\"Dark\":{\"ControlRadius\":-1}}")]
    [InlineData("{\"AccentSource\":\"Unknown\"}")]
    [InlineData("{\"AccentSource\":99}")]
    [InlineData("{\"TransparencyIntensity\":81}")]
    public void ImportedFilesAreValidatedBeforeUse(string json)
    {
        if (json.StartsWith('{') && json != "{}" && !json.Contains("Format") && !json.Contains("Version"))
            json = "{\"Format\":\"daydrive-theme\",\"Version\":1," + json[1..];
        Assert.ThrowsAny<Exception>(() => MarelianThemeFile.Parse(json));
    }

    [Fact]
    public void OversizedThemeFileIsRejected()
        => Assert.Throws<InvalidDataException>(() => MarelianThemeFile.Parse(new string(' ', 256 * 1024 + 1)));
}
