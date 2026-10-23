using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace IntelligentScheduler.Desktop.Platform;

internal sealed class LinuxSystemAppearance(LinuxAppearanceReader? reader = null) : IDisposable
{
    internal sealed record Colors(Color? Accent = null, Color? Wallpaper = null);
    private readonly LinuxAppearanceReader _reader = reader ?? LinuxAppearanceReader.ForCurrentDesktop();
    private readonly object _gate = new();
    private Timer? _timer;
    private Action? _changed;
    private bool _wallpaper;
    private bool _disposed;
    private int _reading;
    private Colors _colors = new();
    private (string Path, DateTime Modified, long Length)? _imageKey;
    private Color? _imageColor;
    public Colors Current => Volatile.Read(ref _colors);

    public void Watch(bool enabled, bool wallpaper, Action changed)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var differentSource = _wallpaper != wallpaper;
            _wallpaper = wallpaper;
            _changed = enabled ? changed : null;
            if (!enabled) { _timer?.Dispose(); _timer = null; return; }
            // File palettes, portal values and wallpaper daemons are all refreshed off-thread.
            // Re-read images only when the path, modification time or length changes.
            if (_timer is null) _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
            else if (differentSource) _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(3));
        }
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) return;
        try
        {
            bool wallpaper;
            lock (_gate)
            {
                if (_disposed || _changed is null) return;
                wallpaper = _wallpaper;
            }
            var accent = _reader.SystemAccent();
            Color? image = null;
            if (wallpaper)
            {
                var path = _reader.WallpaperPath();
                if (path is not null)
                {
                    var file = new FileInfo(path);
                    var key = (path, file.LastWriteTimeUtc, file.Length);
                    if (_imageKey != key)
                    {
                        _imageColor = ShellThemeService.SampleWallpaper(path);
                        _imageKey = _imageColor is not null ? key : null;
                    }
                    image = _imageColor;
                }
            }
            var next = new Colors(accent, image);
            Action? changed = null;
            lock (_gate)
            {
                if (!_disposed && _changed is not null && wallpaper == _wallpaper && _colors != next)
                {
                    Volatile.Write(ref _colors, next);
                    changed = _changed;
                }
            }
            changed?.Invoke();
        }
        // A wallpaper/palette can be atomically replaced or removed during a read.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        finally { Volatile.Write(ref _reading, 0); }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _changed = null; _timer?.Dispose(); _timer = null; }
    }
}

/// <summary>Sources with no shared Linux wallpaper API, with read-only and bounded fallbacks.</summary>
internal sealed class LinuxAppearanceReader(
    string configHome, string cacheHome, string desktop,
    Func<string, string[], string?> run,
    Func<string, string, string?> setting,
    Func<Color?> portal,
    string? wallpaperOverride = null,
    string? noctaliaConfig = null, string? noctaliaCache = null)
{
    private readonly string _noctaliaConfig = noctaliaConfig ?? Path.Combine(configHome, "noctalia");
    private readonly string _noctaliaCache = noctaliaCache ?? Path.Combine(cacheHome, "noctalia");
    private bool Is(string name) => desktop.Contains(name, StringComparison.OrdinalIgnoreCase);
    private bool GeneratedPalette => !Is("GNOME") && !Is("KDE") && !Is("Plasma");

    public static LinuxAppearanceReader ForCurrentDesktop()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
        return new(Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"),
            Env("XDG_CACHE_HOME") ?? Path.Combine(home, ".cache"),
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "",
            Run, LinuxAppearanceSettings.Read, LinuxAppearanceSettings.PortalAccent,
            Env("DAYDRIVE_WALLPAPER"), Env("NOCTALIA_CONFIG_DIR"), Env("NOCTALIA_CACHE_DIR"));
    }

    public Color? SystemAccent()
    {
        // Tiling shells often publish an exact wallpaper-derived accent while the portal
        // exposes only a stale GNOME named colour. Prefer their generated palette there.
        if (GeneratedPalette && PaletteAccent() is { } generated) return generated;
        return portal() ?? (Is("GNOME") ? GnomeAccent() ?? GtkAccent() : KdeAccent() ?? GtkAccent() ?? GnomeAccent())
            ?? (GeneratedPalette ? null : PaletteAccent());
    }

    private Color? PaletteAccent()
    {
        using var noctalia = ReadJson(Path.Combine(_noctaliaConfig, "colors.json"));
        if (Property(noctalia?.RootElement, "mPrimary") is { } primary && Color.TryParse(primary, out var color)) return color;
        using var wal = ReadJson(Path.Combine(cacheHome, "wal", "colors.json"));
        var colors = Child(wal?.RootElement, "colors");
        return Property(colors, "color4") is { } blue && Color.TryParse(blue, out color) ? color : null;
    }

    private Color? KdeAccent()
    {
        var raw = IniValue(ReadText(Path.Combine(configHome, "kdeglobals")), "General", "AccentColor");
        if (raw is null) return null;
        var parts = raw.Split(',');
        return parts.Length == 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g)
            && byte.TryParse(parts[2], out var b) ? Color.FromRgb(r, g, b) : null;
    }

    private Color? GtkAccent()
    {
        foreach (var version in new[] { "gtk-4.0", "gtk-3.0" })
        {
            var css = ReadText(Path.Combine(configHome, version, "gtk.css"));
            var match = Regex.Match(css, @"@define-color\s+(?:accent_bg_color|theme_selected_bg_color)\s+(#[\da-fA-F]{6}|#[\da-fA-F]{3})\s*;",
                RegexOptions.CultureInvariant);
            if (match.Success && Color.TryParse(match.Groups[1].Value, out var color)) return color;
        }
        return null;
    }

    private Color? GnomeAccent() => setting("org.gnome.desktop.interface", "accent-color") switch
    {
        "blue" => Color.Parse("#3584E4"), "teal" => Color.Parse("#2190A4"),
        "green" => Color.Parse("#3A944A"), "yellow" => Color.Parse("#C88800"),
        "orange" => Color.Parse("#ED5B00"), "red" => Color.Parse("#E62D42"),
        "pink" => Color.Parse("#D56199"), "purple" => Color.Parse("#9141AC"),
        "slate" => Color.Parse("#6F8396"), _ => null
    };

    public string? WallpaperPath()
    {
        if (ExistingPath(wallpaperOverride) is { } explicitPath) return explicitPath;
        // GNOME's background schema may be installed but unused under niri/Hyprland.
        if (GeneratedPalette && NoctaliaWallpaper() is { } noctalia) return noctalia;
        if (GeneratedPalette)
        {
            foreach (var program in new[] { "awww", "swww" })
                foreach (var line in (run(program, ["query"]) ?? "").Split('\n'))
                {
                    var marker = line.IndexOf("image: ", StringComparison.Ordinal);
                    if (marker >= 0 && ExistingPath(line[(marker + 7)..]) is { } path) return path;
                }
            if (Is("Hyprland"))
                foreach (var line in (run("hyprctl", ["hyprpaper", "listactive"]) ?? "").Split('\n'))
                {
                    var marker = line.IndexOf(" = ", StringComparison.Ordinal);
                    if (marker >= 0 && ExistingPath(line[(marker + 3)..]) is { } path) return path;
                }
            var waypaper = IniValue(ReadText(Path.Combine(configHome, "waypaper", "config.ini")), "Settings", "wallpaper");
            if (ExistingPath(waypaper) is { } selected) return selected;
            using var wal = ReadJson(Path.Combine(cacheHome, "wal", "colors.json"));
            if (ExistingPath(Property(wal?.RootElement, "wallpaper")) is { } walPath) return walPath;
        }
        if (Is("KDE") || Is("Plasma"))
            foreach (var line in ReadText(Path.Combine(configHome, "plasma-org.kde.plasma.desktop-appletsrc")).Split('\n'))
                if (line.StartsWith("Image=", StringComparison.Ordinal) && ExistingPath(line[6..]) is { } image) return image;
        var dark = setting("org.gnome.desktop.interface", "color-scheme") == "prefer-dark";
        return ExistingPath(setting("org.gnome.desktop.background", dark ? "picture-uri-dark" : "picture-uri"))
            ?? ExistingPath(setting("org.gnome.desktop.background", dark ? "picture-uri" : "picture-uri-dark"));
    }

    private string? NoctaliaWallpaper()
    {
        using var settings = ReadJson(Path.Combine(_noctaliaConfig, "settings.json"));
        var schemes = Child(settings?.RootElement, "colorSchemes");
        var dark = Child(schemes, "darkMode")?.ValueKind != JsonValueKind.False;
        var monitor = Property(schemes, "monitorForColors");
        using var cache = ReadJson(Path.Combine(_noctaliaCache, "wallpapers.json"));
        var wallpapers = Child(cache?.RootElement, "wallpapers");
        if (monitor is not null && FromEntry(Child(wallpapers, monitor)) is { } selected) return selected;
        if (wallpapers is { ValueKind: JsonValueKind.Object } map)
            foreach (var entry in map.EnumerateObject())
                if (FromEntry(entry.Value) is { } path) return path;
        return ExistingPath(Property(cache?.RootElement, "defaultWallpaper"));

        string? FromEntry(JsonElement? entry) => entry is { ValueKind: JsonValueKind.String } text
            ? ExistingPath(text.GetString()) : ExistingPath(Property(entry, dark ? "dark" : "light"))
                ?? ExistingPath(Property(entry, dark ? "light" : "dark"));
    }

    internal static string? ExistingPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim().Trim('\'', '"');
        if (path.StartsWith("~/", StringComparison.Ordinal)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }

    internal static string? IniValue(string text, string group, string key)
    {
        var active = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { active = line == $"[{group}]"; continue; }
            if (!active || line.StartsWith('#') || line.StartsWith(';')) continue;
            var equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim() == key) return line[(equals + 1)..].Trim();
        }
        return null;
    }

    private static JsonElement? Child(JsonElement? value, string key)
        => value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var item) ? item : null;
    private static string? Property(JsonElement? value, string key)
        => Child(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
    private static JsonDocument? ReadJson(string path)
    {
        try { var text = ReadText(path); return text.Length > 0 ? JsonDocument.Parse(text) : null; }
        catch (JsonException) { return null; }
    }

    private static string? Run(string program, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(700)) { process.Kill(entireProcessTree: true); process.WaitForExit(700); return null; }
            return process.ExitCode == 0 && output.Wait(700) && error.Wait(700) ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}
