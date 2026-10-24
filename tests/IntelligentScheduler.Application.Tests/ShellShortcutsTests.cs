using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class ShellShortcutsTests
{
    [Theory]
    [InlineData("ctrl+alt+space", "Ctrl+Alt+Space")]
    [InlineData("Alt + Ctrl + w", "Ctrl+Alt+W")]
    [InlineData("super+shift+f5", "Shift+Win+F5")]
    [InlineData("CMD+K", "Win+K")]
    [InlineData("ctrl+alt+pageup", "Ctrl+Alt+PageUp")]
    [InlineData("ctrl+7", "Ctrl+7")]
    public void Normalize_RewritesToTheStoredSpelling(string gesture, string expected)
    {
        Assert.True(ShellShortcuts.TryNormalize(gesture, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]                 // no modifier: this would swallow ordinary typing
    [InlineData("Shift+A")]           // shift alone is how a capital A is typed
    [InlineData("Ctrl+Alt")]          // modifiers with nothing to press
    [InlineData("Ctrl+Alt+A+B")]      // two keys is not a gesture
    [InlineData("Ctrl+Alt+Squiggle")] // not a key this application knows
    [InlineData("Ctrl+F25")]
    public void Normalize_RefusesWhatWouldMakeABadShortcut(string gesture)
        => Assert.False(ShellShortcuts.TryNormalize(gesture, out _));

    [Fact]
    public void Read_FallsBackToTheDefaultsWhenNothingIsStored()
    {
        var map = ShellShortcuts.Read(null);
        Assert.Equal(ShellShortcuts.All.Count, map.Count);
        foreach (var action in ShellShortcuts.All)
            Assert.Equal(ShellShortcuts.Default(action), map[action]);
    }

    [Fact]
    public void Read_KeepsTheSingleHotkeyThisApplicationUsedToHave()
    {
        var map = ShellShortcuts.Read(null, "ctrl+shift+d");
        Assert.Equal("Ctrl+Shift+D", map[ShellShortcutAction.ToggleShell]);
        // Every other action still gets its default: the old setting only ever meant one thing.
        Assert.Equal(ShellShortcuts.Default(ShellShortcutAction.ToggleWidget),
            map[ShellShortcutAction.ToggleWidget]);
    }

    [Fact]
    public void Read_TreatsAnEmptyEntryAsSwitchedOffAndGarbageAsUnset()
    {
        var json = """
            {"ToggleWidget":"","ToggleAssistant":"nonsense","StopSession":"alt+f9"}
            """;
        var map = ShellShortcuts.Read(json);
        Assert.Equal(string.Empty, map[ShellShortcutAction.ToggleWidget]);
        Assert.Equal(ShellShortcuts.Default(ShellShortcutAction.ToggleAssistant),
            map[ShellShortcutAction.ToggleAssistant]);
        Assert.Equal("Alt+F9", map[ShellShortcutAction.StopSession]);
    }

    [Fact]
    public void Read_SurvivesAMangledFile()
    {
        var map = ShellShortcuts.Read("{ not json at all");
        Assert.Equal(ShellShortcuts.Default(ShellShortcutAction.ToggleShell), map[ShellShortcutAction.ToggleShell]);
    }

    [Fact]
    public void Write_RoundTrips()
    {
        var written = ShellShortcuts.Write(new Dictionary<ShellShortcutAction, string>
        {
            [ShellShortcutAction.ToggleShell] = "Ctrl+Alt+J",
            [ShellShortcutAction.ToggleWidget] = string.Empty
        });
        var map = ShellShortcuts.Read(written);
        Assert.Equal("Ctrl+Alt+J", map[ShellShortcutAction.ToggleShell]);
        Assert.Equal(string.Empty, map[ShellShortcutAction.ToggleWidget]);
    }

    [Fact]
    public void Clashes_NamesBothSidesAndIgnoresTheOnesSwitchedOff()
    {
        var map = new Dictionary<ShellShortcutAction, string>
        {
            [ShellShortcutAction.ToggleShell] = "Ctrl+Alt+J",
            [ShellShortcutAction.ToggleWidget] = "Ctrl+Alt+J",
            [ShellShortcutAction.StopSession] = string.Empty,
            [ShellShortcutAction.ToggleDoNotDisturb] = string.Empty
        };
        var clashes = ShellShortcuts.Clashes(map);
        Assert.Equal(2, clashes.Count);
        Assert.Contains(ShellShortcutAction.ToggleShell, clashes);
        Assert.Contains(ShellShortcutAction.ToggleWidget, clashes);
    }

    /// <summary>
    /// The defaults have to be usable as they ship: distinct from each other, and none of them
    /// hanging off the key every one of the four target desktops reserves for itself.
    /// </summary>
    [Fact]
    public void Defaults_AreDistinctAndAvoidTheWindowManagersOwnModifier()
    {
        var map = ShellShortcuts.All.ToDictionary(action => action, ShellShortcuts.Default);
        Assert.Empty(ShellShortcuts.Clashes(map));
        foreach (var (action, gesture) in map)
        {
            Assert.True(ShellShortcuts.TryNormalize(gesture, out var normalized), $"{action}: {gesture}");
            Assert.Equal(gesture, normalized);
            // Windows keeps Win, macOS keeps Cmd, and niri and Hyprland hang almost every
            // default binding off Super. All three are the same key.
            Assert.DoesNotContain("Win+", gesture, StringComparison.Ordinal);
        }
    }
}
