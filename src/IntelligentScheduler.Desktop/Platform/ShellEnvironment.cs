using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

internal static class ShellEnvironment
{
    public static DesktopShellMode Mode => DesktopShellPolicy.Resolve(Environment.GetCommandLineArgs(),
        OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable("DAYDRIVE_SHELL_MODE"),
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
        string.Join(':', new[] { "XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP", "DESKTOP_SESSION" }
            .Select(Environment.GetEnvironmentVariable).Where(value => !string.IsNullOrWhiteSpace(value))),
        LinuxLayerShell.IsSupported);
}
