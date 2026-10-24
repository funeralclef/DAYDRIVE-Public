using System.Security.Cryptography;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using IntelligentScheduler.Domain;
using SkiaSharp;

namespace IntelligentScheduler.Desktop.Platform;

public sealed record AppFontDescriptor(string Value, string DisplayName, bool IsCustom);

/// <summary>Owns bundled UI fonts and a persistent, app-local collection of user supplied fonts.</summary>
public sealed class FontCatalogService
{
    public const string InterId = "builtin:Inter";
    public const string SystemId = "builtin:System";
    private const string CustomPrefix = "custom:";
    private static readonly Uri UserCollectionKey = new("fonts:DAYDRIVE.User", UriKind.Absolute);
    private readonly string _fontDirectory;
    private UserFontCollection? _userCollection;

    public FontCatalogService(string dataDirectory)
    {
        _fontDirectory = Path.Combine(dataDirectory, "Fonts");
        Directory.CreateDirectory(_fontDirectory);
    }

    public IReadOnlyList<AppFontDescriptor> GetAvailableFonts()
    {
        RefreshUserCollection();
        var result = new List<AppFontDescriptor>
        {
            new(InterId, "Inter", false),
            new("builtin:Literata", "Literata", false),
            new("builtin:ShantellSans", "Shantell Sans", false),
            new(SystemId, "System", false)
        };
        result.AddRange(ReadCustomFonts());
        return result;
    }

    public AppFontDescriptor Import(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only TTF and OTF fonts are supported.");

        using var typeface = SKTypeface.FromFile(sourcePath)
            ?? throw new InvalidDataException("The selected file is not a readable font.");
        if (string.IsNullOrWhiteSpace(typeface.FamilyName))
            throw new InvalidDataException("The selected font has no family name.");

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)))[..10];
        var safeFamily = string.Concat(typeface.FamilyName.Select(character =>
            char.IsLetterOrDigit(character) ? character : '-')).Trim('-');
        var fileName = $"{safeFamily}-{hash}{extension.ToLowerInvariant()}";
        var destination = Path.Combine(_fontDirectory, fileName);
        if (!File.Exists(destination)) File.Copy(sourcePath, destination);

        RefreshUserCollection();
        return new AppFontDescriptor(CustomPrefix + fileName, typeface.FamilyName, true);
    }

    public FontFamily Resolve(string? value, ShellTheme theme)
    {
        return value switch
        {
            "builtin:Literata" => FontFamily.Parse("avares://DAYDRIVE/Assets/Fonts/Literata#Literata"),
            "builtin:ShantellSans" => FontFamily.Parse("avares://DAYDRIVE/Assets/Fonts/ShantellSans#Shantell Sans"),
            SystemId => new FontFamily(FontManager.Current.DefaultFontFamily.Name),
            { } custom when custom.StartsWith(CustomPrefix, StringComparison.Ordinal) => ResolveCustom(custom),
            _ => FontFamily.Parse("fonts:Inter#Inter")
        };
    }

    private FontFamily ResolveCustom(string value)
    {
        RefreshUserCollection();
        var fileName = value[CustomPrefix.Length..];
        var descriptor = ReadCustomFonts().FirstOrDefault(item =>
            item.Value.Equals(CustomPrefix + fileName, StringComparison.OrdinalIgnoreCase));
        return descriptor is null
            ? FontFamily.Parse("fonts:Inter#Inter")
            : FontFamily.Parse($"{UserCollectionKey}#{descriptor.DisplayName}");
    }

    private IReadOnlyList<AppFontDescriptor> ReadCustomFonts()
    {
        var result = new List<AppFontDescriptor>();
        foreach (var path in EnumerateFontFiles())
        {
            try
            {
                using var typeface = SKTypeface.FromFile(path);
                if (typeface is null || string.IsNullOrWhiteSpace(typeface.FamilyName)) continue;
                result.Add(new AppFontDescriptor(CustomPrefix + Path.GetFileName(path), typeface.FamilyName, true));
            }
            catch { }
        }
        return result.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void RefreshUserCollection()
    {
        var collection = new UserFontCollection(UserCollectionKey);
        foreach (var path in EnumerateFontFiles())
            collection.TryAddFontSource(new Uri(path, UriKind.Absolute));
        FontManager.Current.AddFontCollection(collection);
        _userCollection = collection;
    }

    private IEnumerable<string> EnumerateFontFiles()
        => Directory.EnumerateFiles(_fontDirectory)
            .Where(path => Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase));

    private sealed class UserFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key { get; } = key;
    }
}
