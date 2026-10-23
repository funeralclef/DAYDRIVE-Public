using System.Globalization;

namespace IntelligentScheduler.Application;

/// <summary>
/// Formats file and memory sizes consistently across operating-system locales.
/// </summary>
public static class BinarySizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 MB";

        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1024d
            ? $"{(megabytes / 1024d).ToString("0.00", CultureInfo.InvariantCulture)} GB"
            : $"{megabytes.ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }
}
