using Avalonia.Media;
using IntelligentScheduler.Desktop.Platform;
using IntelligentScheduler.Desktop.ViewModels;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Desktop.Tests;

public sealed class MarelianThemeTests
{
    [Fact]
    public void PaletteUsesEveryCustomColourIndependently()
    {
        using var service = new ShellThemeService(new FontCatalogService(Path.GetTempPath()));
        var profile = new MarelianThemeProfile();
        foreach (var role in Enum.GetValues<ThemeColorRole>()) profile = profile.WithColor(role, $"#{(int)role + 1:X2}3344");
        var settings = new AppSettings { ThemeColorSource = ThemeColorSource.Custom };
        var palette = service.ResolvePalette(settings, profile);
        foreach (var role in Enum.GetValues<ThemeColorRole>().Where(role => role != ThemeColorRole.Shadow))
            Assert.Equal(Color.Parse(profile.Color(role)!), palette.Color(role));
    }

    [Theory]
    [InlineData(ThemeColorSource.Preset)]
    [InlineData(ThemeColorSource.SystemAccent)]
    [InlineData(ThemeColorSource.Wallpaper)]
    public void AutomaticAccentDoesNotDiscardCustomSurfaces(ThemeColorSource source)
    {
        using var service = new ShellThemeService(new FontCatalogService(Path.GetTempPath()));
        var settings = new AppSettings { ThemeColorSource = source };
        var palette = service.ResolvePalette(settings, new() { Background = "#121212", Surface = "#343434", Text = "#FEDCBA" });
        Assert.Equal(Color.Parse("#121212"), palette.Background);
        Assert.Equal(Color.Parse("#343434"), palette.Surface);
        Assert.Equal(Color.Parse("#FEDCBA"), palette.Text);
    }

    [Theory]
    [InlineData(ShellTheme.Noctalia)]
    [InlineData(ShellTheme.Celestia)]
    [InlineData(ShellTheme.Windows10)]
    [InlineData(ShellTheme.Windows11)]
    public void LegacyThemeNamesRenderMarelian(ShellTheme old)
    {
        using var service = new ShellThemeService(new FontCatalogService(Path.GetTempPath()));
        var palette = service.ResolvePalette(new() { ShellTheme = old });
        Assert.Equal(ShellThemeService.Marelian(ShellThemeMode.Dark).Background, palette.Background);
        Assert.Equal(ShellThemeService.Marelian(ShellThemeMode.Dark).Accent, palette.Accent);
    }

    [Fact]
    public void IncompleteColourEditSurvivesRefreshWithoutApplyingInvalidColour()
    {
        var changes = new List<string?>();
        var editor = new ThemeColorEditorViewModel(ThemeColorRole.Text, (_, value) => changes.Add(value), _ => { });
        editor.Load(null, Colors.White);
        editor.Value = "#12";
        editor.Update(null, Colors.Black);
        Assert.True(editor.IsInvalid);
        Assert.Equal("#12", editor.Value);
        Assert.Empty(changes);
        editor.Value = "#123456";
        Assert.False(editor.IsInvalid);
        Assert.Equal("#123456", Assert.Single(changes));
        editor.SelectedColor = Colors.Red;
        Assert.Equal("#FF0000", changes.Last());
        editor.ResetCommand.Execute(null);
        Assert.Null(changes.Last());
        Assert.False(editor.IsInvalid);
    }
}
