using System.Text.Json;
using Avalonia.Media;
using IntelligentScheduler.Desktop.Platform;
using SkiaSharp;
using Xunit;

namespace IntelligentScheduler.Desktop.Tests;

public sealed class LinuxAppearanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daydrive appearance " + Guid.NewGuid());
    private string Config => Path.Combine(_root, "config");
    private string Cache => Path.Combine(_root, "cache");
    private readonly Dictionary<string, string> _settings = [];
    private readonly Dictionary<string, string> _commands = [];
    private Color? _portal;

    private LinuxAppearanceReader Reader(string desktop = "niri", string? wallpaper = null)
        => new(Config, Cache, desktop, (program, args) => _commands.GetValueOrDefault(program + " " + string.Join(' ', args)),
            (schema, key) => _settings.GetValueOrDefault(schema + "/" + key), () => _portal, wallpaper);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void NoctaliaFollowsPublishedPaletteInsteadOfStaleNamedPortalColor()
    {
        _portal = Colors.Blue;
        Write("config/noctalia/colors.json", "{\"mPrimary\":\"#A9C7FF\"}");
        var reader = Reader();
        Assert.Equal(Color.Parse("#A9C7FF"), reader.SystemAccent());
        Write("config/noctalia/colors.json", "{\"mPrimary\":\"#FF9A80\"}");
        Assert.Equal(Color.Parse("#FF9A80"), reader.SystemAccent());
        Assert.Equal(Colors.Blue, Reader("GNOME").SystemAccent());
    }

    [Fact]
    public void IncompletePaletteFallsBackToPortalWithoutThrowing()
    {
        Write("config/noctalia/colors.json", "{\"mPrimary\":");
        _portal = Colors.Teal;
        Assert.Equal(Colors.Teal, Reader().SystemAccent());
        _portal = null;
        Assert.Null(Reader().SystemAccent());
    }

    [Theory]
    [InlineData("config/kdeglobals", "[General]\nAccentColor=120,90,210\n", "#785AD2")]
    [InlineData("config/gtk-4.0/gtk.css", "@define-color accent_bg_color #82abdc;", "#82ABDC")]
    [InlineData("cache/wal/colors.json", "{\"colors\":{\"color4\":\"#C090D0\"}}", "#C090D0")]
    public void DesktopAndGeneratedAccentFallbacks(string file, string contents, string expected)
    {
        Write(file, contents);
        Assert.Equal(Color.Parse(expected), Reader().SystemAccent());
    }

    [Theory]
    [InlineData(0.1, 0.5, 1.0, "#1A80FF")]
    [InlineData(-1, 0.5, 1.0, null)]
    [InlineData(0.5, 1.1, 1.0, null)]
    [InlineData(double.NaN, 0.5, 1.0, null)]
    public void PortalRejectsUnsetAndInvalidRgb(double r, double g, double b, string? expected)
        => Assert.Equal(expected is null ? (Color?)null : Color.Parse(expected), LinuxAppearanceSettings.FromRgb(r, g, b));

    [Fact]
    public void NoctaliaUsesDesktopAppearanceAndConfiguredMonitorForWallpaper()
    {
        var light = Write("light image.png", "test");
        var dark = Write("dark image.png", "test");
        Write("cache/noctalia/wallpapers.json", JsonSerializer.Serialize(new
        {
            wallpapers = new Dictionary<string, object>
            {
                ["DP-1"] = new { light, dark = light }, ["eDP-1"] = new { light, dark }
            }
        }));
        Write("config/noctalia/settings.json", "{\"colorSchemes\":{\"darkMode\":true,\"monitorForColors\":\"eDP-1\"}}");
        Assert.Equal(dark, Reader().WallpaperPath());
        Write("config/noctalia/settings.json", "{\"colorSchemes\":{\"darkMode\":false,\"monitorForColors\":\"eDP-1\"}}");
        Assert.Equal(light, Reader().WallpaperPath());
    }

    [Theory]
    [InlineData("swww query", "winit: 1600x1000, scale: 1, currently displaying: image: ")]
    [InlineData("awww query", "DP-1: 2560x1440, scale: 1, currently displaying: image: ")]
    [InlineData("hyprctl hyprpaper listactive", "DP-1 = ")]
    public void LiveWallpaperDaemonsTakePriorityOverUnusedGnomeSettings(string command, string prefix)
    {
        var actual = Write("live wallpaper #1.png", "test");
        var unused = Write("unused.png", "test");
        _settings["org.gnome.desktop.background/picture-uri"] = new Uri(unused).AbsoluteUri;
        _commands[command] = prefix + actual + "\n";
        Assert.Equal(actual, Reader("Hyprland").WallpaperPath());
    }

    [Fact]
    public void GnomeDarkWallpaperDecodesEscapedFileUrisAndFallsBackWhenMissing()
    {
        var light = Write("light wallpaper #1.png", "test");
        var dark = Write("dark wallpaper é.png", "test");
        _settings["org.gnome.desktop.interface/color-scheme"] = "prefer-dark";
        _settings["org.gnome.desktop.background/picture-uri-dark"] = new Uri(dark).AbsoluteUri;
        _settings["org.gnome.desktop.background/picture-uri"] = new Uri(light).AbsoluteUri;
        Assert.Equal(dark, Reader("GNOME").WallpaperPath());
        File.Delete(dark);
        Assert.Equal(light, Reader("GNOME").WallpaperPath());
    }

    [Fact]
    public void WallpaperOverridesAndWaypaperSupportPathsContainingSpaces()
    {
        var path = Write("wallpaper with spaces.png", "test");
        Write("config/waypaper/config.ini", "[Settings]\nwallpaper = " + path);
        Assert.Equal(path, Reader().WallpaperPath());
        var other = Write("explicit.png", "test");
        Assert.Equal(other, Reader(wallpaper: other).WallpaperPath());
    }

    [Fact]
    public void WallpaperSamplerUsesTheImageAndIgnoresMissingOrCorruptFiles()
    {
        var file = Write("wallpaper.png", "invalid");
        Assert.Null(ShellThemeService.SampleWallpaper(file));
        using var bitmap = new SKBitmap(80, 80);
        bitmap.Erase(new SKColor(32, 160, 220));
        using (var output = File.Create(file)) bitmap.Encode(output, SKEncodedImageFormat.Png, 100);
        var color = Assert.IsType<Color>(ShellThemeService.SampleWallpaper(file));
        Assert.InRange(color.R, 31, 32);
        Assert.InRange(color.G, 159, 160);
        Assert.InRange(color.B, 219, 220);
        Assert.Null(ShellThemeService.SampleWallpaper(file + ".missing"));
    }

    [Fact]
    public async Task PaletteAndWallpaperChangesArePublishedWhileRunning()
    {
        var image = Write("live image.png", "");
        SaveImage(30, 140, 220);
        Write("cache/noctalia/wallpapers.json", JsonSerializer.Serialize(new { wallpapers = new { winit = image } }));
        Write("config/noctalia/colors.json", "{\"mPrimary\":\"#A9C7FF\"}");
        using var appearance = new LinuxSystemAppearance(Reader());
        var updates = System.Threading.Channels.Channel.CreateUnbounded<LinuxSystemAppearance.Colors>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        appearance.Watch(true, true, () => updates.Writer.TryWrite(appearance.Current));
        var first = await updates.Reader.ReadAsync(timeout.Token);
        Assert.Equal(Color.Parse("#A9C7FF"), first.Accent);
        Assert.True(first.Wallpaper?.B > first.Wallpaper?.R);
        Write("config/noctalia/colors.json", "{\"mPrimary\":\"#FF9A80\"}");
        SaveImage(210, 50, 20);
        var second = await updates.Reader.ReadAsync(timeout.Token);
        Assert.Equal(Color.Parse("#FF9A80"), second.Accent);
        Assert.True(second.Wallpaper?.R > second.Wallpaper?.B);
        appearance.Watch(false, false, () => throw new InvalidOperationException("A custom palette must stop watching"));

        void SaveImage(byte r, byte g, byte b)
        {
            using var bitmap = new SKBitmap(32, 32);
            bitmap.Erase(new SKColor(r, g, b));
            using var stream = File.Create(image);
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        }
    }

    [Fact]
    public void MissingGsettingsSchemaAndKeyAreSafe()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Null(LinuxAppearanceSettings.Read("org.daydrive.nonexistent", "wallpaper"));
        Assert.Null(LinuxAppearanceSettings.Read("org.gnome.desktop.interface", "daydrive-nonexistent"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
