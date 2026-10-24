using System.Diagnostics;
using System.Reflection;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// The handful of facts the About panel, the update check and the crash log all need to agree
/// on. Read once from the assembly so the csproj stays the single place a version is bumped.
/// </summary>
public static class AppInfo
{
    public const string Name = "DAYDRIVE";
    public const string Author = "funeralclef";
    public const string AuthorProfileUrl = "https://github.com/funeralclef";
    public const string RepositoryUrl = "https://github.com/funeralclef/DAYDRIVE-Public";
    public const string IssuesUrl = "https://github.com/funeralclef/DAYDRIVE-Public/issues";
    public const string ReleasesUrl = "https://github.com/funeralclef/DAYDRIVE-Public/releases";

    /// <summary>Dotted version of the running build, without a "v" prefix or build metadata.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends "+<commit>" to the informational version; the shell shows neither.
            var cut = informational.IndexOf('+');
            return (cut > 0 ? informational[..cut] : informational).Trim();
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>Hands a URL to the system browser. Failure is reported, never thrown at the UI.</summary>
    public static bool TryOpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        // Only web links ever reach here; a file: or custom scheme would be a way to launch anything.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a fresh copy of the application with the same arguments and tells it to wait for
    /// this one to exit. The caller shuts down straight after; the new copy then finds the
    /// database free, which is what a staged cloud download needs to be swapped in.
    /// </summary>
    public static bool TryRestart()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
            {
                // Standing arguments such as the data folder come along; one-off instructions do not.
                if (argument.StartsWith("--wait-for-exit=", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith("--section=", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("--show", StringComparison.OrdinalIgnoreCase)) continue;
                start.ArgumentList.Add(argument);
            }
            start.ArgumentList.Add($"--wait-for-exit={Environment.ProcessId}");
            start.ArgumentList.Add("--show");
            return Process.Start(start) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Opens a local folder in the system file manager.</summary>
    public static bool TryOpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
