using System.Collections;
using System.Globalization;
using System.Resources;
using Avalonia;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Localization;

public sealed record LanguageOption(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

public sealed class LocalizationService
{
    private readonly ResourceManager _resources = new(
        "IntelligentScheduler.Desktop.Localization.Strings",
        typeof(LocalizationService).Assembly);

    // Sort the names users actually see, independently of the selected interface culture.
    public IReadOnlyList<LanguageOption> SupportedLanguages { get; } = new LanguageOption[]
    {
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("en", "English"),
        new("nb", "Norsk bokmål"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("zh", "简体中文"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("fr", "Français"),
        new("pt", "Português"),
        new("pl", "Polski"),
        new("hi", "हिन्दी"),
        new("it", "Italiano"),
        new("id", "Bahasa Indonesia"),
        new("tr", "Türkçe")
    }.OrderBy(language => language.NativeName, StringComparer.InvariantCultureIgnoreCase).ToArray();

    public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public string ResolveLanguage(string? savedLanguageCode, CultureInfo? systemCulture = null)
        => SupportedLanguageResolver.Resolve(
            savedLanguageCode,
            systemCulture ?? CultureInfo.InstalledUICulture,
            SupportedLanguages.Select(language => language.Code));

    public void Apply(string? languageCode)
    {
        var code = ResolveLanguage(languageCode);
        CurrentCulture = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentCulture = CurrentCulture;
        CultureInfo.CurrentUICulture = CurrentCulture;
        CultureInfo.DefaultThreadCurrentCulture = CurrentCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;

        if (Avalonia.Application.Current is not { } application) return;
        var resourceSet = _resources.GetResourceSet(CurrentCulture, true, true);
        if (resourceSet is null) return;
        foreach (DictionaryEntry entry in resourceSet)
            if (entry.Key is string key && entry.Value is string value)
                application.Resources[$"L.{key}"] = value;
    }

    public string Text(string key)
        => _resources.GetString(key, CurrentCulture) ?? _resources.GetString(key, CultureInfo.InvariantCulture) ?? key;

    public string Format(string key, params object[] values)
        => string.Format(CurrentCulture, Text(key), values);
}
