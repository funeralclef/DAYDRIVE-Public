# DAYDRIVE on Linux window managers

Linux builds from this source branch host the original edge shell on **native Wayland layer surfaces** when the compositor supports layer-shell v4. The strip, quick widget, planner, calendar, assistant, analytics, settings, and pickers use the same Avalonia views and animations as Windows. They overlay the desktop directly and do not become tiles. No nested desktop or floating rules are needed for this path.

## Desktop libraries

The Debian package declares GTK 3 and gtk-layer-shell as dependencies. For a portable build on Debian/Ubuntu:

```sh
sudo apt install libgtk-layer-shell0 xwayland
```

On Arch Linux:

```sh
sudo pacman -S gtk3 gtk-layer-shell xorg-xwayland
```

GTK layer-shell 0.6 or newer and a compositor advertising protocol v4 or newer are required. The application checks the actual protocol, not a list of compositor names. Niri, Hyprland, Sway, and compatible KDE Plasma sessions provide this protocol; GNOME Wayland does not. See the [GTK layer-shell documentation](https://wmww.github.io/gtk-layer-shell/).

The panels use a GTK Wayland connection. Avalonia 12.1.1 still uses X11 for its remaining desktop services, so a working `DISPLAY` is also required. In niri, keep xwayland-satellite enabled as described in [niri's Xwayland guide](https://niri-wm.github.io/niri/Xwayland.html). Other compositors normally start Xwayland themselves. Do not set `DISPLAY=:0` manually.

## The original layout

```sh
daydrive --show
```

For a portable copy, use the path to its `DAYDRIVE` executable. Automatic mode selects the edge shell after probing layer-shell. Existing preferences for themes, fonts, transparency, hidden startup, and widget behaviour remain in use.

Left-click the strip to show or hide the planner. Right-click it for the quick widget. Its target includes the visual gap up to the screen edge. The planner's outer tabs open one side section at a time to its left. The assistant can also unfold over the planner. Gaps between tabs and adjoining panels belong to DAYDRIVE's input area. Clicking the gap beside the strip preserves the open editor's keyboard focus. The strip and quick widget never claim keyboard focus; editors acquire it when used. Menus and tooltips render inside their host panel. File and folder selection uses Avalonia's managed chooser.

The surfaces use the **Top** layer and reserve no desktop space. This keeps other applications' tile sizes unchanged and allows fullscreen applications to cover DAYDRIVE. Niri documents this ordering in its [layer-shell notes](https://niri-wm.github.io/niri/Layer%E2%80%90Shell-Components.html).

Allow at least **1340 × 748 logical pixels** for the planner and full weekly calendar together; 1600 × 900 gives more room. Logical space decreases when display scaling increases. For smaller screens, use the resizable layout below.

## Desktop shortcuts

Linux uses compositor bindings for desktop-wide shortcuts. A second launch sends a command to the running copy for the same data directory instead of opening another database. These commands invoke the same actions used by Windows shortcuts:

| Command | Action |
| --- | --- |
| `daydrive --action=ToggleShell` | Show or hide the planner |
| `daydrive --action=ToggleWidget` | Show or hide the quick widget |
| `daydrive --action=ToggleAssistant` | Open or close the assistant |
| `daydrive --action=StartOrPauseSession` | Start or pause the current session |
| `daydrive --action=StopSession` | Stop the session and ask for its outcome |
| `daydrive --action=ToggleDoNotDisturb` | Toggle quiet mode |

Add the bindings you want to your own configuration. For niri:

```kdl
binds {
    Ctrl+Alt+Space { spawn "daydrive" "--action=ToggleShell"; }
    Ctrl+Alt+W { spawn "daydrive" "--action=ToggleWidget"; }
    Ctrl+Alt+A { spawn "daydrive" "--action=ToggleAssistant"; }
    Ctrl+Alt+S { spawn "daydrive" "--action=StartOrPauseSession"; }
    Ctrl+Alt+E { spawn "daydrive" "--action=StopSession"; }
    Ctrl+Alt+Q { spawn "daydrive" "--action=ToggleDoNotDisturb"; }
}
```

For Hyprland:

```ini
bind = CTRL ALT, SPACE, exec, daydrive --action=ToggleShell
bind = CTRL ALT, W, exec, daydrive --action=ToggleWidget
bind = CTRL ALT, A, exec, daydrive --action=ToggleAssistant
bind = CTRL ALT, S, exec, daydrive --action=StartOrPauseSession
bind = CTRL ALT, E, exec, daydrive --action=StopSession
bind = CTRL ALT, Q, exec, daydrive --action=ToggleDoNotDisturb
```

Use an absolute executable path for portable copies. If you specify `--data-dir` or `INTELLIGENT_SCHEDULER_DATA_DIR` at startup, use the same directory in your bindings. Rebinding keys inside DAYDRIVE changes its in-app shortcuts; compositor bindings are configured separately. Focus mode's “hold shortcuts” preference also suppresses incoming action commands.

`daydrive --section=calendar`, `--section=settings`, `--section=analytics`, and `--section=assistant` reveal those sections in the existing instance. `daydrive --exit` saves settings and exits; it is useful on desktops without a tray. Closing an edge panel only hides it, as on Windows.

## System and wallpaper colours

In **Settings → Appearance → Colour source**, select **System accent** or **Wallpaper**. The accent colours buttons, selected items, icons and highlights while the chosen design and light/dark mode remain in use. Custom colours and preset palettes remain under your control.

System accent reads the [desktop Settings portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Settings.html), with KDE, GTK and GNOME settings as fallbacks. In tiling sessions it first checks generated Noctalia (`mPrimary` in `colors.json`) and pywal palettes, which can contain a more precise colour than the portal's named accent.

Wallpaper mode samples the current image from Noctalia's wallpaper cache, `awww`/`swww query`, `hyprctl hyprpaper listactive`, Waypaper, pywal, KDE or GNOME. Noctalia's selected colour monitor and desktop light/dark wallpaper slot are respected. For another wallpaper tool, pass the image explicitly, for example `DAYDRIVE_WALLPAPER="/path/to/wallpaper.png" daydrive --show`. With several wallpapers and no selected colour monitor, the first available image is used.

Changes are checked every three seconds in the background. Images are sampled again only when their path, modification time or size changes. Missing sources fall back to the system accent and then the selected DAYDRIVE palette. The application reads these desktop files and settings without changing them.

## Fallbacks

Use one ordinary resizable window explicitly:

```sh
daydrive --shell-mode=windowed
```

This is also the automatic fallback if GTK/layer-shell is unavailable, or if the compositor does not implement the required protocol. Tabs and embedded pickers provide access to every section. The desktop controls placement and resizing; closing this window exits the application.

`DAYDRIVE_SHELL_MODE=windowed|edge|auto` provides the same selection through the environment. A command-line option takes precedence. For troubleshooting the native host, set `DAYDRIVE_LAYER_SHELL=0`; automatic mode then selects the fallback on Wayland. Forcing `--shell-mode=edge` with the native host disabled uses the old X11 placement path, which niri's satellite cannot reproduce reliably.

Stacking X11 sessions keep the original X11 edge implementation. On X11 tiling WMs, use a floating rule for all DAYDRIVE windows and launch with `--shell-mode=edge`. For i3:

```i3
for_window [class="^DAYDRIVE$"] floating enable
```

## GPU detection

GPU hardware detection does not require a downloaded model or a display connection. It reads Vulkan devices through the system's optional `libvulkan.so.1`, DRM sysfs information, and NVIDIA's `nvidia-smi` when available. Missing Vulkan or vendor tools leave the other detection sources available. The model engine still needs a working graphics driver to use an identified card.

## Platform limits

- Transparency and rounded surfaces are drawn by the application. Backdrop blur and Mica are desktop effects; their appearance depends on the compositor. Native layer surfaces use software Skia rendering, with buffers scaled for the selected monitor.
- Layer surfaces use a fixed compositor layer. The Windows “always on top” preference cannot give them ordinary application stacking semantics.
- Automatic detection of other applications' fullscreen state remains unavailable on Wayland. Use manual focus mode to hold reminders. Fullscreen windows still cover the Top layer.
- A tray icon requires a status-notifier host such as a desktop panel. The edge strip and `--exit` remain available without one.
- X11 tiling WMs and compositors without layer-shell cannot all provide the same placement rules. The resizable layout remains available for these environments.

## Verification and reports

`bash scripts/verify-linux-edge.sh` runs the real application inside an isolated niri/Xvfb session. It checks compositor screenshots, separate panel placement, transparent corners, wheel scrolling, live system/wallpaper colour changes, pointer input, keyboard editing and clipboard, screen-edge activation, passive widget focus, repeated launches, and clean exit. An input recorder beneath DAYDRIVE detects clicks or keyboard focus leaking through gaps between petals and panels. It saves logs and screenshots in the reported directory. It requires niri, xwayland-satellite, Xvfb, xauth, D-Bus, GTK layer-shell, libXtst, Python with Pillow, a C compiler, pkg-config and GTK/layer-shell development headers. `bash scripts/verify-niri.sh` separately checks the resizable fallback.

The native host was checked with niri 26.04 at 100% and 125% scale, and Hyprland 0.56.2 in an isolated labwc 0.20.2 test host. Hyprland checks assert its own layer geometry, including a 32-pixel bar reserving screen space. This nested desktop is only a test fixture; the application itself connects directly to Hyprland. Sway and KDE's protocol compatibility has not been exercised in this environment.

Include your distribution, compositor/version, executable version, installed/portable build, launch command, and any shell override when reporting a problem. Include `daydrive-crash.log` from the data directory if present. The following describes the session without changing it:

```sh
printf 'session=%s\ndesktop=%s\nDISPLAY=%s\nWAYLAND_DISPLAY=%s\n' \
  "$XDG_SESSION_TYPE" "$XDG_CURRENT_DESKTOP" "$DISPLAY" "$WAYLAND_DISPLAY"
```
