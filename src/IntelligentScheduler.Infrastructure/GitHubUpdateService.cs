using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// Reads the newest release from the public GitHub API.
///
/// Deliberately quiet: one anonymous GET per day at most, no telemetry sent, and a failure is
/// swallowed into a message rather than surfaced as an error dialog. A user who never opens
/// settings should not be able to tell that the check happens at all.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    private readonly HttpClient _http;
    private readonly ISettingsRepository _settings;
    private readonly string _owner;
    private readonly string _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateCheckResult? _cached;

    /// <summary>Anonymous GitHub calls are rate limited, so one request a day is plenty.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public GitHubUpdateService(ISettingsRepository settings, string currentVersion,
        string owner = "funeralclef", string repository = "DAYDRIVE-Public")
    {
        _settings = settings;
        _owner = owner;
        _repository = repository;
        CurrentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DAYDRIVE", CurrentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string CurrentVersion { get; }

    public string ReleasesUrl => $"https://github.com/{_owner}/{_repository}/releases";

    public async Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAppSettingsAsync(cancellationToken);
        if (!force)
        {
            if (!settings.UpdateCheckEnabled) return UpdateCheckResult.UpToDate(string.Empty);
            if (settings.UpdateLastCheckedUtc is { } last && DateTimeOffset.UtcNow - last < CheckInterval)
                return _cached ?? UpdateCheckResult.UpToDate(string.Empty);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var release = await FetchLatestAsync(cancellationToken);
            await _settings.PatchAppSettingsAsync(stored => stored.UpdateLastCheckedUtc = DateTimeOffset.UtcNow,
                cancellationToken);
            settings = await _settings.GetAppSettingsAsync(cancellationToken);

            if (release is null)
                return _cached = UpdateCheckResult.UpToDate("No published release was found.");

            if (IUpdateService.CompareVersions(release.Version, CurrentVersion) <= 0)
                return _cached = UpdateCheckResult.UpToDate($"DAYDRIVE {CurrentVersion} is the latest version.");

            // A skipped version stays silent until the user asks directly; a later one is announced.
            var wasSkipped = !string.IsNullOrWhiteSpace(settings.UpdateSkippedVersion) &&
                             IUpdateService.CompareVersions(release.Version, settings.UpdateSkippedVersion) <= 0;

            return _cached = new UpdateCheckResult(!wasSkipped || force, release,
                $"Version {release.Version} is available.");
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException exception)
        {
            return UpdateCheckResult.Failed(Describe(exception));
        }
        catch (Exception exception)
        {
            return UpdateCheckResult.Failed(exception.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task SkipAsync(string version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        await _settings.PatchAppSettingsAsync(stored => stored.UpdateSkippedVersion = version.Trim(), cancellationToken);
        _cached = UpdateCheckResult.UpToDate(string.Empty);
    }

    /// <summary>
    /// Prefers /releases/latest, then falls back to the list. A repository whose newest release
    /// is marked as a pre-release returns 404 from the first endpoint, which is not an error.
    /// </summary>
    private async Task<AppRelease?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        var baseUrl = $"https://api.github.com/repos/{_owner}/{_repository}/releases";
        using (var response = await _http.GetAsync($"{baseUrl}/latest", cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return ReadRelease(document.RootElement);
            }
            if (response.StatusCode != HttpStatusCode.NotFound)
                throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var listResponse = await _http.GetAsync($"{baseUrl}?per_page=10", cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)listResponse.StatusCode} {listResponse.ReasonPhrase}.");

        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cancellationToken));
        if (list.RootElement.ValueKind != JsonValueKind.Array) return null;

        AppRelease? best = null;
        foreach (var element in list.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var candidate = ReadRelease(element);
            if (candidate is null) continue;
            if (best is null || IUpdateService.CompareVersions(candidate.Version, best.Version) > 0) best = candidate;
        }
        return best;
    }

    private static AppRelease? ReadRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var tag = Text(element, "tag_name");
        var name = Text(element, "name");
        var version = string.IsNullOrWhiteSpace(tag) ? name : tag;
        if (string.IsNullOrWhiteSpace(version)) return null;
        version = version.Trim().TrimStart('v', 'V');

        string? downloadUrl = null;
        var downloadBytes = 0L;
        if (element.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = Text(asset, "name");
                // Prefer a Windows archive; anything else is only used when nothing better exists.
                var isPreferred = assetName.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                                  assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                if (downloadUrl is not null && !isPreferred) continue;
                var url = Text(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                downloadUrl = url;
                downloadBytes = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0;
                if (isPreferred) break;
            }
        }

        var published = element.TryGetProperty("published_at", out var publishedAt) &&
                        publishedAt.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(publishedAt.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        return new AppRelease(
            version,
            string.IsNullOrWhiteSpace(name) ? $"DAYDRIVE {version}" : name.Trim(),
            Text(element, "body").Trim(),
            Text(element, "html_url"),
            downloadUrl,
            downloadBytes,
            published);
    }

    private static string Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Describe(HttpRequestException exception)
        => exception.InnerException is System.Net.Sockets.SocketException
            ? "GitHub is unreachable right now."
            : exception.Message;

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
