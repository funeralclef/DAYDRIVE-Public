# DAYDRIVE

A desktop planner for keeping tasks out of your head, giving them time on the calendar, and recording what actually got done.

On Windows, stacking X11 desktops, and Wayland compositors with layer-shell support, DAYDRIVE stays at the edge of the screen with a quick widget and separate side panels. This includes niri and Hyprland when GTK layer-shell is installed.

Read the [Linux window-manager guide](docs/LINUX-WINDOW-MANAGERS.md) for dependencies, compositor shortcuts, and platform limits. Other desktops can use the resizable single-window fallback.

DAYDRIVE stores your data locally and works without an account. AI and cloud sync are optional.

## See it in action

**Video**

> A short walkthrough goes here: open the planner, book an hour, start a session, and wrap up the day.

<!-- Replace the block above with a video URL or a linked thumbnail. -->

**Screenshots**

> Space reserved for the daily planner, weekly calendar, quick widget, and analytics.

<!-- Add screenshots under docs/media/ and link them here. -->

## What you can do

- **Plan your day.** Keep an unscheduled task list alongside a weekly calendar. Create events by dragging across free time, repeat them, or move several at once.
- **Break work into steps.** Give a task a plan across several days, or turn an event into a checklist for one sitting.
- **Track the work.** Start, pause, and stop sessions. Mark an event done, partly done, or skipped, and leave a note about where to continue.
- **Close the day.** Review unanswered events together, carry work forward, and see planned time beside actual time.
- **Look back.** Review completion, focus time, workload, and timing across weeks and months.
- **Ask for help.** Use a local GGUF model, a compatible API, or MCP tools to work with your schedule. Choose which changes need confirmation.
- **Make it yours.** Customise MARELIAN's colours, corners, typography and shadows in light and dark modes; import and export themes. System and wallpaper accents, custom fonts, reminders, focus mode, and 16 interface languages are available.

## Download

Get the builds from [Releases](https://github.com/funeralclef/DAYDRIVE-Public/releases/latest). Each includes its own .NET runtime.

| Platform | Install | Portable |
| --- | --- | --- |
| Windows 10/11, x64 | `DAYDRIVE-0.1.1-win-x64-setup.exe` | `DAYDRIVE-0.1.1-win-x64-portable.zip` |
| Linux, x64 | `DAYDRIVE-0.1.1-linux-x64.deb` | `DAYDRIVE-0.1.1-linux-x64-portable.tar.gz` |

The `.deb` is for Debian/Ubuntu systems. Other glibc-based distributions can use the portable archive. Linux needs a graphical desktop and its native libraries; see the [installation guide](docs/USER-MANUAL.md#installation). macOS builds are not included in this release.

Portable copies keep their data in a `Data` folder beside the executable. Extract the entire archive to a writable folder before starting it.

## First five minutes

1. Open DAYDRIVE. Left-click the strip on the right edge for the planner or right-click it for the quick widget. In the resizable fallback, use the tabs at the top of the window.
2. Add a task under **Unscheduled**.
3. Create an event when you know when you will work on it. Tasks do not reserve calendar time by themselves.
4. Press **Start** when you begin. At the end, record the result.

The [user manual](docs/USER-MANUAL.md) covers the calendar, steps, sessions, assistant, settings, sync, and backups.

## Build from source

Install the .NET SDK selected by [global.json](global.json), then run:

```sh
dotnet restore IntelligentScheduler.slnx
dotnet run --project src/IntelligentScheduler.Desktop
```

Run the tests with `dotnet test --solution IntelligentScheduler.slnx`. Packaging instructions are in [docs/RELEASING.md](docs/RELEASING.md); dependency notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
