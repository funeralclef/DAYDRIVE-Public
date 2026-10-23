using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class LocalizationTests
{
    private static readonly string[] LanguageCodes =
    [
        "ru", "uk", "de", "nb", "es", "zh", "ja", "ko", "fr", "pt", "pl", "hi", "it", "id", "tr"
    ];

    [Fact]
    public void AllLocalizationFiles_ContainAllBaseKeys()
    {
        var locDir = Path.Combine(RepositoryRoot(), "src", "IntelligentScheduler.Desktop", "Localization");
        Assert.True(Directory.Exists(locDir), $"Localization folder not found: {locDir}");

        var basePath = Path.Combine(locDir, "Strings.resx");
        Assert.True(File.Exists(basePath), $"Base file not found: {basePath}");

        var baseDoc = XDocument.Load(basePath);
        var baseKeyList = baseDoc.Root!.Elements("data").Select(e => e.Attribute("name")!.Value).ToList();
        var baseKeys = baseKeyList.ToHashSet();
        Assert.Equal(baseKeyList.Count, baseKeys.Count);

        foreach (var code in LanguageCodes)
        {
            var path = Path.Combine(locDir, $"Strings.{code}.resx");
            Assert.True(File.Exists(path), $"Localization file not found: {path}");

            var doc = XDocument.Load(path);
            var keyList = doc.Root!.Elements("data").Select(e => e.Attribute("name")!.Value).ToList();
            var keys = keyList.ToHashSet();
            Assert.Equal(keyList.Count, keys.Count);

            var missing = baseKeys.Except(keys).ToList();
            Assert.True(missing.Count == 0, $"Language '{code}' is missing {missing.Count} keys: {string.Join(", ", missing)}");
            var unexpected = keys.Except(baseKeys).ToList();
            Assert.True(unexpected.Count == 0,
                $"Language '{code}' contains {unexpected.Count} unexpected keys: {string.Join(", ", unexpected)}");
        }
    }

    [Fact]
    public void AnalyticsHours_AreRoundedInEveryLanguage()
    {
        var locDir = Path.Combine(RepositoryRoot(), "src", "IntelligentScheduler.Desktop", "Localization");
        foreach (var path in Directory.GetFiles(locDir, "Strings*.resx"))
        {
            var document = XDocument.Load(path);
            var template = document.Root!.Elements("data")
                .Single(element => element.Attribute("name")?.Value == "MetricHours")
                .Element("value")!.Value;

            Assert.Contains("{0:0.#}", template);
        }
    }

    [Fact]
    public void AllLocalizationFiles_PreserveFormatItems()
    {
        var locDir = Path.Combine(RepositoryRoot(), "src", "IntelligentScheduler.Desktop", "Localization");
        var baseEntries = XDocument.Load(Path.Combine(locDir, "Strings.resx")).Root!.Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => FormatItems(element.Element("value")!.Value));

        foreach (var code in LanguageCodes)
        {
            var entries = XDocument.Load(Path.Combine(locDir, $"Strings.{code}.resx")).Root!.Elements("data");
            foreach (var entry in entries)
            {
                var key = entry.Attribute("name")!.Value;
                Assert.Equal(baseEntries[key], FormatItems(entry.Element("value")!.Value));
            }
        }
    }

    [Fact]
    public void AllLocalizationFiles_PreserveProductName()
    {
        var locDir = Path.Combine(RepositoryRoot(), "src", "IntelligentScheduler.Desktop", "Localization");
        var productKeys = XDocument.Load(Path.Combine(locDir, "Strings.resx")).Root!.Elements("data")
            .Where(element => element.Element("value")!.Value.Contains("DAYDRIVE", StringComparison.Ordinal))
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet();

        foreach (var code in LanguageCodes)
        {
            var entries = XDocument.Load(Path.Combine(locDir, $"Strings.{code}.resx")).Root!.Elements("data")
                .Where(element => productKeys.Contains(element.Attribute("name")!.Value));
            Assert.All(entries,
                entry => Assert.Contains("DAYDRIVE", entry.Element("value")!.Value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void FindAllCyrillicInSourceFiles()
    {
        var srcDir = Path.Combine(RepositoryRoot(), "src");
        Assert.True(Directory.Exists(srcDir), $"Source folder not found: {srcDir}");
        string[] textSourceExtensions = [".cs", ".axaml", ".json", ".xml", ".csproj", ".props", ".targets"];
        var files = Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories)
            .Where(f => textSourceExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                     && !f.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith("LocalizationService.cs", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith("NaturalLanguageTaskParser.cs", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Any(c => c >= '\u0400' && c <= '\u04FF'))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"Found unlocalized Cyrillic in source files:\n{string.Join("\n", offenders)}");
    }

    /// <summary>
    /// Locates the repository from this file's own path. Walking up from the build output
    /// breaks as soon as the output is redirected, which these checks should not care about.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string[] FormatItems(string value)
        => System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+(?::[^}]+)?\}")
            .Select(match => match.Value)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
}
