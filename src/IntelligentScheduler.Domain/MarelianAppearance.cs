using System.Text.Json;

namespace IntelligentScheduler.Domain;

public enum ThemeColorRole
{
    Accent, Background, Surface, SurfaceAlt, Text, Muted, Stroke,
    Secondary, Tertiary, Selection, Success, Warning, Danger, Shadow
}

/// <summary>One MARELIAN light/dark variant. Missing colours follow the base palette.</summary>
public sealed record MarelianThemeProfile
{
    public string? Accent { get; init; }
    public string? Background { get; init; }
    public string? Surface { get; init; }
    public string? SurfaceAlt { get; init; }
    public string? Text { get; init; }
    public string? Muted { get; init; }
    public string? Stroke { get; init; }
    public string? Secondary { get; init; }
    public string? Tertiary { get; init; }
    public string? Selection { get; init; }
    public string? Success { get; init; }
    public string? Warning { get; init; }
    public string? Danger { get; init; }
    public string? Shadow { get; init; }
    public double WindowRadius { get; init; } = 15;
    public double CardRadius { get; init; } = 10;
    public double ItemRadius { get; init; } = 7;
    public double ControlRadius { get; init; } = 6;
    public double TextScale { get; init; } = 1;
    public double ShadowStrength { get; init; } = 1;

    public string? Color(ThemeColorRole role) => role switch
    {
        ThemeColorRole.Accent => Accent, ThemeColorRole.Background => Background,
        ThemeColorRole.Surface => Surface, ThemeColorRole.SurfaceAlt => SurfaceAlt,
        ThemeColorRole.Text => Text, ThemeColorRole.Muted => Muted, ThemeColorRole.Stroke => Stroke,
        ThemeColorRole.Secondary => Secondary, ThemeColorRole.Tertiary => Tertiary,
        ThemeColorRole.Selection => Selection, ThemeColorRole.Success => Success,
        ThemeColorRole.Warning => Warning, ThemeColorRole.Danger => Danger, ThemeColorRole.Shadow => Shadow,
        _ => null
    };

    public MarelianThemeProfile WithColor(ThemeColorRole role, string? value) => role switch
    {
        ThemeColorRole.Accent => this with { Accent = value }, ThemeColorRole.Background => this with { Background = value },
        ThemeColorRole.Surface => this with { Surface = value }, ThemeColorRole.SurfaceAlt => this with { SurfaceAlt = value },
        ThemeColorRole.Text => this with { Text = value }, ThemeColorRole.Muted => this with { Muted = value },
        ThemeColorRole.Stroke => this with { Stroke = value }, ThemeColorRole.Secondary => this with { Secondary = value },
        ThemeColorRole.Tertiary => this with { Tertiary = value }, ThemeColorRole.Selection => this with { Selection = value },
        ThemeColorRole.Success => this with { Success = value }, ThemeColorRole.Warning => this with { Warning = value },
        ThemeColorRole.Danger => this with { Danger = value }, ThemeColorRole.Shadow => this with { Shadow = value },
        _ => this
    };

    public MarelianThemeProfile Normalize()
    {
        var result = this;
        foreach (var role in Enum.GetValues<ThemeColorRole>()) result = result.WithColor(role, MarelianAppearance.Hex(Color(role)));
        return result with
        {
            WindowRadius = Bounded(WindowRadius, 0, 40, 15), CardRadius = Bounded(CardRadius, 0, 24, 10),
            ItemRadius = Bounded(ItemRadius, 0, 20, 7), ControlRadius = Bounded(ControlRadius, 0, 20, 6),
            TextScale = Bounded(TextScale, 0.8, 1.25, 1), ShadowStrength = Bounded(ShadowStrength, 0, 2, 1)
        };
    }

    private static double Bounded(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public static class MarelianAppearance
{
    public static string Key(ShellThemeMode mode) => $"Marelian:{mode}";

    public static string? Hex(string? text)
    {
        var value = text?.Trim();
        if (value is null || value.Length is not (7 or 9) || value[0] != '#' || !value[1..].All(Uri.IsHexDigit)) return null;
        return value.ToUpperInvariant();
    }

    public static Dictionary<string, MarelianThemeProfile> Read(string? json)
    {
        try
        {
            var source = JsonSerializer.Deserialize<Dictionary<string, MarelianThemeProfile>>(json ?? "{}");
            return source?.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Normalize(), StringComparer.OrdinalIgnoreCase)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public static string Write(IReadOnlyDictionary<string, MarelianThemeProfile> profiles)
        => JsonSerializer.Serialize(profiles.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Normalize()));

    public static MarelianThemeProfile Profile(AppSettings settings)
        => Read(settings.ThemeColorOverridesJson).GetValueOrDefault(Key(settings.ShellThemeMode)) ?? new();

    /// <summary>Also used after importing an older database or syncing an older installation.</summary>
    public static void NormalizeSettings(AppSettings settings)
    {
        var source = Read(settings.ThemeColorOverridesJson);
        var profiles = new Dictionary<string, MarelianThemeProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var mode in Enum.GetValues<ShellThemeMode>())
        {
            var key = Key(mode);
            var legacy = $"{settings.ShellTheme}:{mode}";
            var profile = settings.ShellTheme != ShellTheme.Marelian && mode == settings.ShellThemeMode
                ? source.GetValueOrDefault(legacy) ?? source.GetValueOrDefault(key)
                : source.GetValueOrDefault(key) ?? source.GetValueOrDefault(legacy);
            if (profile is not null) profiles[key] = profile;
        }
        var activeKey = Key(settings.ShellThemeMode);
        if (settings.ThemeColorSource == ThemeColorSource.Custom && !profiles.ContainsKey(activeKey))
            profiles[activeKey] = new MarelianThemeProfile { Accent = settings.CustomAccent, Background = settings.CustomBackground }.Normalize();
        settings.ShellTheme = ShellTheme.Marelian;
        settings.ThemeColorOverridesJson = Write(profiles);
    }
}
