using System.Globalization;

namespace IntelligentScheduler.Application;

public static class SupportedLanguageResolver
{
    public static string Resolve(
        string? savedLanguageCode,
        CultureInfo systemCulture,
        IEnumerable<string> supportedLanguageCodes,
        string fallback = "en")
    {
        var supported = supportedLanguageCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(savedLanguageCode))
        {
            var saved = Normalize(savedLanguageCode);
            return supported.Contains(saved) ? saved : fallback;
        }

        var system = Normalize(systemCulture.Name);
        return supported.Contains(system) ? system : fallback;
    }

    private static string Normalize(string code)
    {
        var normalized = code.Split('-', '_')[0].ToLowerInvariant();
        return normalized == "no" ? "nb" : normalized;
    }
}
