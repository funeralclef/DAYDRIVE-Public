using Avalonia.Media;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>Shared typography and shadow tokens used by every shell view, picker and hint.</summary>
internal static class ThemeVisualTokens
{
    internal static readonly IReadOnlyDictionary<string, double> Fonts = new Dictionary<string, double>
    {
        ["MarelianFont8"] = 8d,
        ["MarelianFont8_5"] = 8.5d,
        ["MarelianFont8_8"] = 8.8d,
        ["MarelianFont9"] = 9d,
        ["MarelianFont9_5"] = 9.5d,
        ["MarelianFont10"] = 10d,
        ["MarelianFont10_5"] = 10.5d,
        ["MarelianFont11"] = 11d,
        ["MarelianFont11_5"] = 11.5d,
        ["MarelianFont12"] = 12d,
        ["MarelianFont12_5"] = 12.5d,
        ["MarelianFont13"] = 13d,
        ["MarelianFont13_5"] = 13.5d,
        ["MarelianFont14"] = 14d,
        ["MarelianFont15"] = 15d,
        ["MarelianFont16"] = 16d,
        ["MarelianFont17"] = 17d,
        ["MarelianFont18"] = 18d,
        ["MarelianFont20"] = 20d,
        ["MarelianFont21"] = 21d,
        ["MarelianFont22"] = 22d,
        ["MarelianFont24"] = 24d,
        ["MarelianFont29"] = 29d,
    };
    internal static readonly IReadOnlyDictionary<string, string> Shadows = new Dictionary<string, string>
    {
        ["MarelianShadow0"] = "0 0 0 0 Transparent",
        ["MarelianShadow1"] = "0 1 5 0 #30000000",
        ["MarelianShadow2"] = "0 10 30 0 #6B000000",
        ["MarelianShadow3"] = "0 10 32 0 #75000000",
        ["MarelianShadow4"] = "0 12 30 0 #99000000",
        ["MarelianShadow5"] = "0 14 34 -12 #7A000000",
        ["MarelianShadow6"] = "0 18 42 0 #72000000",
        ["MarelianShadow7"] = "0 18 48 0 #80000000",
        ["MarelianShadow8"] = "0 2 6 0 #60000000",
        ["MarelianShadow9"] = "0 22 54 0 #B0000000",
        ["MarelianShadow10"] = "0 3 10 0 #28000000",
        ["MarelianShadow11"] = "0 3 10 0 #33000000",
        ["MarelianShadow12"] = "0 4 12 0 #4A000000",
        ["MarelianShadow13"] = "0 6 18 0 #2E000000",
        ["MarelianShadow14"] = "0 6 18 0 #4A000000",
        ["MarelianShadow15"] = "0 8 24 0 #22000000",
        ["MarelianShadow16"] = "0 8 26 0 #26000000",
    };

    public static void Apply(Avalonia.Application app, MarelianThemeProfile profile)
    {
        foreach (var (key, size) in Fonts) app.Resources[key] = size * profile.TextScale;
        foreach (var (key, value) in Shadows)
        {
            var shadows = BoxShadows.Parse(value);
            if (shadows.Count == 0) { app.Resources[key] = shadows; continue; }
            var shadow = shadows[0];
            var color = profile.Shadow is { } hex && Color.TryParse(hex, out var custom) ? custom : shadow.Color;
            shadow.Color = Color.FromArgb((byte)Math.Clamp(Math.Round(shadow.Color.A * profile.ShadowStrength * (profile.Shadow is null ? 1 : color.A / 255d)), 0, 255), color.R, color.G, color.B);
            app.Resources[key] = new BoxShadows(shadow);
        }
    }
}
