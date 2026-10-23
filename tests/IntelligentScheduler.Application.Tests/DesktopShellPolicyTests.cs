using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class DesktopShellPolicyTests
{
    [Theory]
    [InlineData("niri")]
    [InlineData("Hyprland")]
    [InlineData("sway")]
    [InlineData("KDE")]
    public void LayerShellPreservesTheOriginalDesktopLayout(string desktop)
        => Assert.Equal(DesktopShellMode.Edge, DesktopShellPolicy.Resolve([], true, null, "wayland", "wayland-0", desktop, true));

    [Fact]
    public void WindowedOverrideStillWinsWhenNativeEdgeIsAvailable()
        => Assert.Equal(DesktopShellMode.Windowed, DesktopShellPolicy.Resolve(["--shell-mode=windowed"], true,
            "edge", "wayland", "wayland-0", "niri", true));

    [Theory]
    [InlineData("wayland", "", "GNOME")]
    [InlineData("x11", "wayland-0", "KDE")]
    [InlineData(null, "", "niri")]
    [InlineData("x11", "", "i3")]
    [InlineData("x11", "", "ubuntu:Hyprland")]
    public void CompositorManagedDesktopsUseOneWindow(string? session, string display, string desktop)
        => Assert.Equal(DesktopShellMode.Windowed, DesktopShellPolicy.Resolve([], true, null, session, display, desktop));

    [Theory]
    [InlineData("GNOME")]
    [InlineData("KDE")]
    [InlineData("XFCE")]
    public void StackingX11KeepsTheEdgeShell(string desktop)
        => Assert.Equal(DesktopShellMode.Edge, DesktopShellPolicy.Resolve([], true, null, "x11", "", desktop));

    [Fact]
    public void WindowsKeepsEdgeEvenWithInheritedLinuxVariables()
        => Assert.Equal(DesktopShellMode.Edge, DesktopShellPolicy.Resolve([], false, null, "wayland", "wayland-0", "niri"));

    [Fact]
    public void ExplicitModeOverridesDetectionAndEnvironment()
        => Assert.Equal(DesktopShellMode.Edge, DesktopShellPolicy.Resolve(["--shell-mode=edge"], true, "windowed", "wayland", "wayland-0", "niri"));

    [Fact]
    public void UnknownWmCanUseWindowedModeWithoutChangingConfiguration()
        => Assert.Equal(DesktopShellMode.Windowed, DesktopShellPolicy.Resolve([], true, "windowed", "x11", "", "custom"));

    [Fact]
    public void AutoArgumentRestoresDetectionOverEnvironmentOverride()
        => Assert.Equal(DesktopShellMode.Windowed, DesktopShellPolicy.Resolve(["--shell-mode=auto"], true, "edge", "wayland", "wayland-0", "niri"));
}
