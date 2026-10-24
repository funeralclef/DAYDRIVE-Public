namespace IntelligentScheduler.Application;

public enum DesktopShellMode { Edge, Windowed }

public static class DesktopShellPolicy
{
    public static DesktopShellMode Resolve(IEnumerable<string> arguments, bool isLinux,
        string? configuredMode, string? sessionType, string? waylandDisplay, string? desktop,
        bool supportsLayerShell = false)
    {
        var option = arguments.FirstOrDefault(value => value.StartsWith("--shell-mode=", StringComparison.OrdinalIgnoreCase));
        var requested = (option is null ? configuredMode : option["--shell-mode=".Length..])?.ToLowerInvariant();
        if (requested is "windowed") return DesktopShellMode.Windowed;
        if (requested is "edge") return DesktopShellMode.Edge;
        if (requested is not (null or "" or "auto"))
            throw new ArgumentException("Shell mode must be auto, windowed, or edge.");
        if (!isLinux) return DesktopShellMode.Edge;
        if (supportsLayerShell) return DesktopShellMode.Edge;
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(waylandDisplay)) return DesktopShellMode.Windowed;

        var tilingDesktops = new[] { "niri", "sway", "hyprland", "i3", "bspwm", "awesome", "qtile", "dwm", "herbstluftwm", "spectrwm", "leftwm", "river", "scroll", "mango" };
        var names = (desktop ?? "").Split([':', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return names.Any(name => tilingDesktops.Contains(name, StringComparer.OrdinalIgnoreCase))
            ? DesktopShellMode.Windowed : DesktopShellMode.Edge;
    }
}
