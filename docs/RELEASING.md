# Building a release

Public source and downloads live at `funeralclef/DAYDRIVE-Public`. Development history stays in the private repository. The public repository starts from a source snapshot, with no development history or superseded releases.

## Local build

Use Linux with the SDK in `global.json`, Python 3.11 or newer, NSIS (`makensis`), and GNU binutils (`ar`). On Ubuntu, the packaging tools are available with `sudo apt install nsis binutils`.

```sh
dotnet restore IntelligentScheduler.slnx
dotnet test --solution IntelligentScheduler.slnx -c Release
python3 scripts/package-release.py
```

The script reads the version from the desktop project, publishes self-contained Windows and Linux x64 builds, and creates four packages plus `SHA256SUMS.txt` under `artifacts/release`:

- A Windows installer for the current user, with shortcuts and an uninstaller.
- A Windows portable ZIP.
- A Debian/Ubuntu package installed under `/opt/daydrive`.
- A Linux portable tarball.

Portable packages include `daydrive.portable`; installed packages do not. No user database, model weights, or saved credentials belong in any package. README, user manual, Linux window-manager guide, and dependency notices ship with the application. macOS is deliberately excluded.

The Windows installer is unsigned. Signing is not configured in this repository.

## Validation and publication

The release workflow tests the solution on Linux and Windows, builds the packages, and checks both installed and portable Windows copies. It also checks the Linux package structure and exercises its database startup. Portable checks use an extracted path containing spaces and verify that `Data` appears beside the executable.

Run the workflow manually to upload build artifacts without creating a release. To publish, tag the reviewed public commit with `v` followed by the project version and push the tag. The workflow validates the tagged commit, then publishes the four packages and their checksums in GitHub Releases. The release title is `DAYDRIVE PUBLIC RELEASE`.

Before tagging, update the version in the desktop project, the download filenames in the README and manual, and `docs/RELEASE-NOTES.md`. Keep the tag version and project version identical. Never push development history or private tags into the public repository.

## Window-manager checks

The desktop UI tests exercise page switching, embedded pickers, step entry, and resizing. To run the same checks and full application startup under an isolated niri/Xwayland session, install niri, xwayland-satellite, Xvfb, xauth, D-Bus, and software graphics support, then run:

```sh
bash scripts/verify-niri.sh
```

The script creates its own runtime directory and compositor inside Xvfb. It does not edit the desktop configuration or control the current niri session. It verifies that page switches keep one top-level window, then disables the optional native host to check automatic fallback, moves and resizes the app through niri, and closes it normally.

For the native Wayland edge shell, also install GTK 3, gtk-layer-shell, their development headers, a C compiler, pkg-config, libXtst, and Python Pillow, then run:

```sh
bash scripts/verify-linux-edge.sh
```

This launches the real application, checks compositor screenshots and native layers, edits and saves a task through pointer/keyboard input and clipboard, exercises strip clicks and widget focus, forwards commands from additional launches, and checks clean exit. Logs and screenshots remain in the reported temporary directory. Set `DAYDRIVE_WM_TEST_OUTPUT` to retain them in a chosen directory. The native host implements Avalonia platform interfaces; run these checks when changing the pinned Avalonia version.

Use `DAYDRIVE_WM_TEST_SCALE=1.25 bash scripts/verify-linux-edge.sh` for fractional scaling. `bash scripts/verify-linux-edge.sh hyprland` additionally requires Hyprland and labwc; labwc supplies the DMA-BUF capabilities required by nested Hyprland. `DAYDRIVE_LABWC` can select its binary, and `DAYDRIVE_WM_TEST_DRM_DEVICE` can select a render node for the isolated host. `DAYDRIVE_WM_TEST_BAR=1` adds an exclusive-zone bar to the Hyprland check and requires a C compiler and GTK/layer-shell development headers. To test a published executable, set `DAYDRIVE_VERIFY_APP` to its absolute path.
