DAYDRIVE 0.1.2.

This update refines the quick widget and event reminders, fixes assistant navigation, and improves native Linux file dialogs.

## Changes

- The complete quick widget stays centred beside the edge petal as its blocks appear or disappear. Its opening animation grows from the petal.
- The event card shows the planned start/end times and a compact timer. The separate timer card, current-event heading, and plan/actual/paused captions have been removed. Progress uses a thin line below the timer.
- The edge timer capsule is slightly wider and less rounded. All three pairs of digits use equal bold tabular figures, matching row sizes, and pixel-aligned placement.
- Event reminders show the notification without a timer. A compact symmetric petal beside the card dismisses it without opening the widget or changing focus.
- Event-card footers follow custom corner radii, fixing the lower border edges.
- Text is vertically centred in input fields. The minute selector steps by five minutes.
- A new assistant chat can be created from Wrap up the day. AI permission options have a shared card layout and shorter explanations.
- Native Wayland panels use the system GTK/portal file chooser for theme import/export and other file operations. File dialogs stay above the application panels and support spaces and non-Latin paths. Theme exports suggest `MARELIAN.daydrive-theme.json` independently of the interface language.

## Downloads

All builds include the .NET runtime. Local AI models are downloaded separately.

| System | Install | Portable |
| --- | --- | --- |
| Windows 10/11 x64 | `DAYDRIVE-0.1.2-win-x64-setup.exe` | `DAYDRIVE-0.1.2-win-x64-portable.zip` |
| Linux x64 | `DAYDRIVE-0.1.2-linux-x64.deb` | `DAYDRIVE-0.1.2-linux-x64-portable.tar.gz` |

Extract portable archives completely into a writable folder. Keep `daydrive.portable` beside the executable; data is stored in the adjacent `Data` folder. Installed packages keep data in the account's application-data directory. `SHA256SUMS.txt` contains hashes for all four downloads.

See the [user manual](https://github.com/funeralclef/DAYDRIVE-Public/blob/DAYDRIVE-PUBLIC-RELEASE/docs/USER-MANUAL.md) for installation and everyday use, and the [Linux window-manager guide](https://github.com/funeralclef/DAYDRIVE-Public/blob/DAYDRIVE-PUBLIC-RELEASE/docs/LINUX-WINDOW-MANAGERS.md) for compositor setup.

## Platform notes

- Native Wayland edge panels require GTK 3, gtk-layer-shell, and layer-shell v4 support. The Debian package declares the native dependencies. Desktops without this support use the resizable fallback.
- Linux portable builds target glibc distributions. Desktop services on Wayland still need Xwayland.
- Windows global shortcuts use the desktop hotkey service; Linux global shortcuts use compositor bindings.
- On Wayland, focus mode is controlled manually; automatic focus detection remains available on Windows and X11.
- The Windows installer is unsigned. macOS packages are not included.
