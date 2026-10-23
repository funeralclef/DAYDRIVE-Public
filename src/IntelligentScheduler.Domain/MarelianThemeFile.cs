using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntelligentScheduler.Domain;

/// <summary>Appearance only: no account, schedule, credentials or machine paths are exported.</summary>
public sealed record MarelianThemeFile
{
    public string Format { get; init; } = "daydrive-theme";
    public int Version { get; init; } = 1;
    public MarelianThemeProfile Dark { get; init; } = new();
    public MarelianThemeProfile Light { get; init; } = new();
    public ShellThemeMode Mode { get; init; }
    public ThemeColorSource AccentSource { get; init; }
    public VisualPalette CategoryPalette { get; init; } = VisualPalette.Vibrant;
    public string Font { get; init; } = "builtin:Inter";
    public bool Transparency { get; init; } = true;
    public double TransparencyIntensity { get; init; } = 10;
    public bool ReduceMotion { get; init; }
    public bool ShowMascot { get; init; } = true;
    public string? MascotColor { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static MarelianThemeFile Parse(string content)
    {
        if (content.Length > 256 * 1024) throw new InvalidDataException("Theme file is too large.");
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(nameof(Format), out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "daydrive-theme"
            || !document.RootElement.TryGetProperty(nameof(Version), out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            throw new InvalidDataException("Unsupported theme format.");
        var theme = JsonSerializer.Deserialize<MarelianThemeFile>(content, Options)
            ?? throw new InvalidDataException("Empty theme.");
        if (theme.Format != "daydrive-theme" || theme.Version != 1 || theme.Dark is null || theme.Light is null
            || !Enum.IsDefined(theme.Mode) || !Enum.IsDefined(theme.AccentSource) || !Enum.IsDefined(theme.CategoryPalette)
            || !double.IsFinite(theme.TransparencyIntensity) || theme.TransparencyIntensity is < 0 or > 80)
            throw new InvalidDataException("Unsupported theme format.");
        foreach (var profile in new[] { theme.Dark, theme.Light })
        {
            foreach (var role in Enum.GetValues<ThemeColorRole>())
                if (profile.Color(role) is { } value && MarelianAppearance.Hex(value) is null)
                    throw new InvalidDataException("Invalid theme colour.");
            var normalized = profile.Normalize();
            if (normalized.WindowRadius != profile.WindowRadius || normalized.CardRadius != profile.CardRadius
                || normalized.ItemRadius != profile.ItemRadius || normalized.ControlRadius != profile.ControlRadius
                || normalized.TextScale != profile.TextScale || normalized.ShadowStrength != profile.ShadowStrength)
                throw new InvalidDataException("Invalid theme dimensions.");
        }
        if (theme.MascotColor is { Length: > 0 } mascot && MarelianAppearance.Hex(mascot) is null)
            throw new InvalidDataException("Invalid mascot colour.");
        // Font files belong to the receiving machine; unavailable custom fonts fall back to Inter.
        return theme with { Dark = theme.Dark.Normalize(), Light = theme.Light.Normalize() };
    }
}
