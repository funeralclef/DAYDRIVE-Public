namespace IntelligentScheduler.Application;

/// <summary>A published release as GitHub describes it, reduced to what the shell shows.</summary>
public sealed record AppRelease(
    string Version,
    string Name,
    string Notes,
    string HtmlUrl,
    string? DownloadUrl,
    long DownloadBytes,
    DateTimeOffset PublishedAtUtc);

/// <summary>
/// Outcome of one check. <see cref="IsUpdateAvailable"/> is only true for a release that is
/// both newer than the running build and not one the user chose to skip.
/// </summary>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    AppRelease? Release,
    string Message,
    bool IsError = false)
{
    public static UpdateCheckResult UpToDate(string message) => new(false, null, message);
    public static UpdateCheckResult Failed(string message) => new(false, null, message, IsError: true);
}

public interface IUpdateService
{
    /// <summary>Version of the running build, without a leading "v".</summary>
    string CurrentVersion { get; }

    /// <summary>Page a user is sent to when they want the release itself.</summary>
    string ReleasesUrl { get; }

    /// <summary>
    /// Asks GitHub for the newest release. <paramref name="force"/> bypasses both the daily
    /// interval and the "checks are off" preference, which is what the manual button needs.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken cancellationToken = default);

    /// <summary>Never announce this version again.</summary>
    Task SkipAsync(string version, CancellationToken cancellationToken = default);

    /// <summary>Compares two dotted versions, tolerating "v" prefixes and pre-release suffixes.</summary>
    static int CompareVersions(string? left, string? right)
    {
        var a = ParseVersion(left);
        var b = ParseVersion(right);
        for (var index = 0; index < 4; index++)
        {
            var comparison = a[index].CompareTo(b[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    /// <summary>Reads up to four numeric components; anything unparsable counts as zero.</summary>
    static int[] ParseVersion(string? value)
    {
        var parts = new int[4];
        if (string.IsNullOrWhiteSpace(value)) return parts;
        var trimmed = value.Trim().TrimStart('v', 'V');
        // "1.3.0-beta.2" compares as 1.3.0; a pre-release never outranks its own release.
        var cut = trimmed.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) trimmed = trimmed[..cut];
        var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length && index < segments.Length; index++)
            parts[index] = int.TryParse(segments[index], out var parsed) && parsed >= 0 ? parsed : 0;
        return parts;
    }
}
