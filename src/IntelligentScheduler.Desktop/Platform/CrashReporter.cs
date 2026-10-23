using System.Text;

namespace IntelligentScheduler.Desktop.Platform;

public sealed class CrashReporter
{
    private readonly string _logPath;
    private readonly object _gate = new();

    public CrashReporter(string dataDirectory) => _logPath = Path.Combine(dataDirectory, "daydrive-crash.log");

    public string LogPath => _logPath;

    public void Record(Exception exception, string context)
    {
        try
        {
            lock (_gate)
            {
                var builder = new StringBuilder()
                    .AppendLine($"[{DateTimeOffset.Now:O}] {context}")
                    .AppendLine(exception.ToString())
                    .AppendLine();
                File.AppendAllText(_logPath, builder.ToString());
                TrimIfNeeded();
            }
        }
        catch { }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    public void Diagnostic(string message)
    {
        try
        {
            lock (_gate) File.AppendAllText(Path.Combine(Path.GetDirectoryName(_logPath)!, "daydrive-debug.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(_logPath);
        if (!info.Exists || info.Length <= 1_000_000) return;
        var lines = File.ReadAllLines(_logPath);
        File.WriteAllLines(_logPath, lines.Skip(lines.Length / 2));
    }
}
