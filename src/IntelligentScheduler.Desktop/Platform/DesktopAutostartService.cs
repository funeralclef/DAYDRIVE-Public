using IntelligentScheduler.Application;
using Microsoft.Win32;

namespace IntelligentScheduler.Desktop.Platform;

public sealed class DesktopAutostartService(string executablePath) : IAutostartService
{
    private const string AppName = "DAYDRIVE";
    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return Task.FromResult(key?.GetValue(AppName) is string);
        }
        if (OperatingSystem.IsLinux()) return Task.FromResult(File.Exists(LinuxDesktopEntryPath));
        return Task.FromResult(false);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (enabled) key.SetValue(AppName, $"\"{executablePath}\" --background");
            else key.DeleteValue(AppName, false);
            return Task.CompletedTask;
        }
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopEntryPath)!);
            if (enabled)
            {
                File.WriteAllText(LinuxDesktopEntryPath, $"""
                    [Desktop Entry]
                    Type=Application
                    Name=DAYDRIVE
                    Exec="{executablePath}" --background
                    Terminal=false
                    X-GNOME-Autostart-enabled=true
                    """);
            }
            else if (File.Exists(LinuxDesktopEntryPath)) File.Delete(LinuxDesktopEntryPath);
        }
        return Task.CompletedTask;
    }

    private static string LinuxDesktopEntryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "daydrive.desktop");
}
