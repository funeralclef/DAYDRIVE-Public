using Avalonia.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Platform;
using Xunit;

namespace IntelligentScheduler.Desktop.Tests;

public sealed class LinuxDesktopTests
{
    [Theory]
    [InlineData("--action=ToggleShell", ShellShortcutAction.ToggleShell)]
    [InlineData("--action=toggle-widget", ShellShortcutAction.ToggleWidget)]
    [InlineData("--action=toggle-assistant", ShellShortcutAction.ToggleAssistant)]
    [InlineData("--action=start-or-pause-session", ShellShortcutAction.StartOrPauseSession)]
    public void CompositorBindingsResolveToTheWindowsActions(string option, ShellShortcutAction expected)
        => Assert.Equal(expected, LinuxDesktopControl.RequestedAction([option]));

    [Theory]
    [InlineData("--action=6")]
    [InlineData("--action=unknown")]
    [InlineData("--action=")]
    public void InvalidActionsCannotBecomeSilentLaunches(string option)
        => Assert.Throws<ArgumentException>(() => LinuxDesktopControl.RequestedAction([option]));

    [Theory]
    [InlineData(0x61u, 38, Key.A)]
    [InlineData(0x6c6u, 38, Key.A)] // Cyrillic ef on the same physical key.
    [InlineData(0xff0du, 36, Key.Enter)]
    [InlineData(0xff8du, 104, Key.Enter)]
    [InlineData(0xfe20u, 23, Key.Tab)]
    [InlineData(0xff51u, 113, Key.Left)]
    [InlineData(0xffffu, 119, Key.Delete)]
    [InlineData(0xffb3u, 89, Key.NumPad3)]
    public void NativeKeysKeepEditingAndNonLatinShortcuts(uint symbol, ushort hardware, Key expected)
        => Assert.Equal(expected, LinuxLayerKeys.Translate(symbol, hardware));
}
