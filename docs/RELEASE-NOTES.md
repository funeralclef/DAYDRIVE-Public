DAYDRIVE 0.1.1 public release.

Plan work in an edge-of-screen calendar, keep an unscheduled task list, break tasks and events into steps, and record work sessions. Review the day together and use analytics to compare the plan with what happened. AI assistance and cloud sync are optional.

The application works without an account. All builds include the .NET runtime; local AI models are downloaded separately.

## Downloads

| System | Install | Portable |
| --- | --- | --- |
| Windows 10/11 x64 | `DAYDRIVE-0.1.1-win-x64-setup.exe` | `DAYDRIVE-0.1.1-win-x64-portable.zip` |
| Linux x64 | `DAYDRIVE-0.1.1-linux-x64.deb` | `DAYDRIVE-0.1.1-linux-x64-portable.tar.gz` |

Extract portable archives completely into a writable folder. Keep `daydrive.portable` beside the executable; your data goes into the adjacent `Data` folder. The `.deb` installs through Debian/Ubuntu's package manager; the portable Linux build targets glibc distributions and needs desktop libraries and XWayland on Wayland sessions.

See the [README](https://github.com/funeralclef/DAYDRIVE-Public#readme) and [user manual](https://github.com/funeralclef/DAYDRIVE-Public/blob/DAYDRIVE-PUBLIC-RELEASE/docs/USER-MANUAL.md) for setup and everyday use. `SHA256SUMS.txt` contains hashes for all four downloads.

## Desktop layouts

On Wayland compositors supporting layer-shell v4, DAYDRIVE uses native GTK layer surfaces to preserve the Windows edge strip, quick widget, and separate panels. Install GTK 3 and gtk-layer-shell for portable Linux copies. The Debian package declares these dependencies. Native surfaces require no floating rules.

Windows and stacking X11 desktops keep the edge panel and quick widget. You can choose a layout explicitly with `--shell-mode=windowed` or `--shell-mode=edge`.

Desktops without layer-shell use the single-window fallback. The [Linux window-manager guide](https://github.com/funeralclef/DAYDRIVE-Public/blob/DAYDRIVE-PUBLIC-RELEASE/docs/LINUX-WINDOW-MANAGERS.md) covers dependencies, compositor bindings, and platform limits. Repeated Linux launches target the existing instance; `--action` supports compositor shortcuts and `--exit` works without a tray.

## Platform notes

- MARELIAN is now the single customisable theme. Both light and dark modes have independent colour, corner, text-size and shadow settings. Themes can be imported, exported and reset; existing custom colours migrate from the removed Noctalia, Celestia and Windows designs. System and wallpaper accents remain available.
- Linux detects dedicated and integrated GPUs before installing a model engine, including hybrid AMD/NVIDIA laptops, and refreshes available VRAM when hardware detection is requested.
- The main edge petal no longer shows hover tooltips. Settings tabs use the planner tabs' narrow width and taller shape.
- The main petal and its session timer use symmetric rounded capsules, inset six pixels from the screen edge. Languages are sorted by their displayed names.
- The petal and timer are slightly wider, with activation extending to the screen edge. Transparent input bridges cover gaps between petals and adjoining panels so clicks stay inside DAYDRIVE.
- Hours, minutes and seconds on the petal timer now share the same bold weight.
- Native Wayland wheel input preserves smooth scrolling and ignores stop/compatibility events that previously undid downward scrolling.
- Linux can follow system accent and wallpaper colours, including Noctalia palettes and wallpaper tools used in tiling sessions. Changes update the running theme in the background.
- Hover hints use the application palette, wrap longer explanations and wait before opening. Redundant labels and hints covering whole cards have been removed.
- No macOS builds in this release.
- Windows shortcuts work across the desktop; Linux uses compositor bindings for desktop shortcuts and also supports in-app bindings.
- Automatic focus detection is available on Windows and Linux/X11. On Wayland, use manual focus mode.
- The Windows installer is unsigned.
