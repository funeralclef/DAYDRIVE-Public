namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// Where this installation keeps its files. Resolved once during service construction so the
/// About panel, the backup service and the cloud sync all name the same directory.
/// </summary>
public sealed record AppPaths(string DataDirectory, string DatabasePath);
