# DAYDRIVE user manual

This guide covers DAYDRIVE 0.1.1 for Windows and Linux. Button names refer to the English interface. You can change the language under **Settings → General**.

## Contents

- [Installation](#installation)
- [Finding your way around](#finding-your-way-around)
- [Tasks and the calendar](#tasks-and-the-calendar)
- [Moving, copying, and repeating events](#moving-copying-and-repeating-events)
- [Breaking work into steps](#breaking-work-into-steps)
- [Work sessions and results](#work-sessions-and-results)
- [Wrapping up the day](#wrapping-up-the-day)
- [Analytics](#analytics)
- [The assistant](#the-assistant)
- [Connecting an AI provider](#connecting-an-ai-provider)
- [Appearance and behaviour](#appearance-and-behaviour)
- [Reminders and focus mode](#reminders-and-focus-mode)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Data, backups, and sync](#data-backups-and-sync)
- [Updates and troubleshooting](#updates-and-troubleshooting)

## Installation

### Windows

Download the x64 setup executable from the release page, run it, and choose an installation folder. The installer works for your Windows account and adds Start menu and desktop shortcuts. It does not require a separate .NET installation.

For a portable copy, download the Windows ZIP and extract the whole folder somewhere you can write, such as `Documents\DAYDRIVE`. Open `DAYDRIVE.exe`. Running it inside the ZIP will not work correctly.

The portable archive contains a file named `daydrive.portable`. Leave it beside the executable: it tells DAYDRIVE to keep its database, settings, and downloaded models in the adjacent `Data` folder. If you move the portable installation, move the whole folder, including `Data`.

### Linux

For Debian or Ubuntu, download the `.deb` and install it from the directory containing the file:

```sh
sudo apt install ./DAYDRIVE-0.1.1-linux-x64.deb
```

Open DAYDRIVE from your application launcher or run `daydrive` in a terminal.

On other glibc-based distributions, extract the portable archive:

```sh
tar -xzf DAYDRIVE-0.1.1-linux-x64-portable.tar.gz
cd DAYDRIVE-0.1.1-linux-x64-portable
./DAYDRIVE
```

The portable build also uses the `daydrive.portable` marker and an adjacent `Data` folder. Keep it in a writable location. It includes .NET but still uses system libraries for the desktop, fonts, and graphics. These include glibc, ICU, OpenSSL, fontconfig, and X11 libraries. Native Wayland panels also need GTK 3 and gtk-layer-shell; remaining desktop services still require Xwayland. Alpine/musl is not a target for these builds.

### Installed or portable?

An installed copy keeps data in your account's application-data directory. A portable copy keeps it in its own folder. They use separate databases unless you explicitly point them at the same data directory. Do not run two copies against the same database at once.

## Finding your way around

DAYDRIVE chooses its layout for the current desktop. On Windows, stacking X11 desktops, and Wayland compositors with layer-shell v4 and GTK layer-shell installed, it uses the original edge strip and separate panels. Niri, Sway, and Hyprland support this native Wayland path. See the [Linux window-manager guide](LINUX-WINDOW-MANAGERS.md) for dependencies and compositor shortcuts.

Other Wayland sessions and recognised X11 tiling desktops use a resizable fallback. Use its tabs to open the calendar, analytics, assistant, or settings. Wide windows can show the planner beside a section; narrower windows give the selected section the available space. Pickers stay inside this window. Closing it saves settings and exits. The strip and separate widget are not used in this layout, and Start hidden does not hide it at startup.

To select a layout explicitly, launch with `--shell-mode=windowed` or `--shell-mode=edge`. You can also set `DAYDRIVE_SHELL_MODE` to `windowed`, `edge`, or `auto`. The command-line option takes precedence. Edge mode requires a desktop that honours application-controlled placement.

The native Wayland edge shell needs no floating rules or nested desktop. If layer-shell is unavailable, forcing edge mode uses X11 placement and can produce overlapping panels. The guide describes the supported fallback and remaining platform differences.

In **edge mode**, the thin strip at the right edge is called the petal. The following controls describe that layout:

| Action | Result |
| --- | --- |
| Left-click the petal | Show or hide the main panel |
| Right-click the petal | Show or hide the quick widget |
| Open the tray icon's menu | Access the planner, quiet hours, focus mode, sync status, and Exit |
| Pin the panel open | Keep it available while working elsewhere |

The main panel and quick widget share the same space. Opening one closes the other. Hovering over the petal reveals session controls when available; it does not open a panel or show a tooltip.

The outer tabs open the calendar, assistant, analytics, and settings. The assistant opens over the planner and can expand into a separate side panel. Only one side section is shown at a time, while the planner stays on the right. Settings are grouped into General, Appearance, AI, Data and sync, and About. Languages are sorted alphabetically by their displayed native names.

The quick widget shows the clock, the current or next event, and controls for a work session when one is available. Its status chip calls attention to overdue tasks, unmarked events, or skipped work. It can stay open, or hide after the delay selected in settings.

In edge mode, closing or hiding a panel leaves DAYDRIVE running. Use **Exit** in the tray menu, or `daydrive --exit` on Linux, to stop the application completely. In windowed mode, closing the main window exits the application.

## Tasks and the calendar

### What belongs where

A **task** is something you intend to do without a reserved time. An **event** has a start and end time and occupies the calendar. A task or event can also have steps; that is how DAYDRIVE represents a project.

Adding a task does not book time. To work on it at a particular hour, create an event. For a task with steps, **set a time** on a step creates its linked calendar event.

### Add and edit a task

1. Open **Unscheduled** and choose **Add task**.
2. Enter a title, estimated duration, and any deadline or priority you need.
3. Save the task.

Use a task row's edit action to change its details. Tick its completion control when it is finished; untick it to bring it back. Delete asks for confirmation. An overdue deadline is highlighted in the list.

For a larger task, use **Break into steps** instead of trying to estimate the whole job in one sitting. The task's duration then comes from its steps.

### Add and edit an event

Choose **Add event**, enter a title, choose the date and start/end times, and save. You can also drag across an empty interval in the weekly calendar to open a prefilled event form.

Events can have a category, priority, and repeat rule. Categories give blocks their colour; priority identifies work that needs more attention. Choose an existing event's edit action to reopen its form.

Times are shown in the computer's local time zone. An end time earlier than the start represents an overnight event. Equal start and end times are rejected. Check the displayed interval before saving.

### Read the day and week

The day list shows each event's time, duration, category, and relative timing. The active event is highlighted and earlier events are dimmed. The weekly calendar shows seven columns, with a line through today's column at the current time. Use the date picker and navigation arrows to browse other days and weeks.

The summary card changes with the date you are viewing. Today shows current or upcoming work; a future day shows its planned load; a past day shows its recorded results. A planned event can finish on the clock and still need a result from you.

## Moving, copying, and repeating events

### Drag an event

Drag a block to another hour or day in the weekly calendar. Normal dragging snaps to five-minute intervals. Hold **Shift** for finer control; while holding it, use the mouse wheel to change the snap interval. The available steps are 1, 2, 3, 5, 10, 15, 20, 30, and 60 minutes.

Review the destination and any overlap warnings before confirming the move.

### Move several events

Open an event's action menu and choose **Select**. Tick the other events you want, or choose **All**. Choose **Move…**, then either an earlier/later offset or a target date. The preview shows where the selected events will land and which intervals overlap. Confirm to apply the move. **Done** leaves selection mode; **Clear** removes the ticks.

The same selection can be deleted together. Check the confirmation carefully: it applies to every selected event.

### Copy or shift a day

An event's copy action can send a copy to tomorrow, next week, or another date. The day's actions can copy its whole schedule, shift it earlier or later, move it to another date, or clear it. Each operation asks for confirmation.

Copying creates additional events. Moving changes their time. A copied event with steps gets a fresh, unticked checklist.

### Repeating events

The event form offers daily, weekday, weekly, and monthly repeats. When you edit a series, choose the scope: **This occurrence**, **This and following**, or **Entire series**. The scope controls how far the change reaches. Use the occurrence option for a one-off exception.

For repeating events with steps, completion belongs to the individual occurrence. Finishing this week's checklist does not finish next week's.

## Breaking work into steps

### A task spread over several days

Choose **Break into steps** in a task form or use the steps action on an existing task. Write a step and press Enter to add another. Text left in the field is included when you save.

Each step has a title, estimated duration, and optional day. Click its title to rename it, its duration to change the estimate, or its day to move it. A day means “I intend to work on this then”; it does not reserve an hour.

Expand the task's progress chip to see steps grouped by day. **Planned for today** brings today's steps to the top of Unscheduled. Unfinished steps from an earlier day also appear there, with their original date. Finished days can be folded away or shown again.

Choose **set a time** on a step when you are ready to reserve an hour. DAYDRIVE opens an event form using the step's title, project colour, and a free interval. Save to link the event and step. Later changes to that event's date and duration are reflected in the step. Marking the linked event done completes the step.

Progress is weighted by estimated time. Finishing a ten-minute step in a project with a two-hour step does not count as half the project. The task's duration is the sum of its steps; its remaining time comes from unfinished ones.

Ticking the parent task finishes its remaining steps together. Unticking it reverses that batch completion. Deleting the parent task deletes its steps, while already booked calendar events remain.

### A checklist inside one event

Use **Break into steps** in an event form or its action menu. Add the pieces of work you want to fit into that sitting, each with a duration. The form shows how the total compares with the event's time slot.

Open the event's progress chip to see its checklist. Tick items as you finish them. These checks contribute to the event's recorded result. You can rename, retime, or delete individual steps without opening a separate projects screen.

If some work remains, carry it to another time to open a new prefilled event, or send it to **Unscheduled** as a task to plan across days. Completed work stays with the original sitting. Removing the final step returns the host to an ordinary task or event.

## Work sessions and results

### Start, pause, resume, stop

Press **Start** on the current event or the quick widget when you actually begin. The stopwatch does not start just because the planned start time arrives.

**Pause** holds the timer; **Resume** continues it. Paused time is excluded from worked time. The petal can display the timer while other panels are hidden, and nearby controls let you manage it without opening the full planner.

The session can run beyond the event's planned end. The widget shows planned versus actual time so you can see an overrun. **Stop** pauses the clock and asks how the work went; it does not silently mark the event complete. The widget stays available until you answer.

### Record the result

Choose **Done**, **Partly**, or **Skipped**. For partial work, record the progress and actual time, then write what remains. A continuation note can contain several lines and appears with the event so you can pick up where you left off.

Use **Undo** to correct an outcome. You can also record results on past events without having run a stopwatch. Actual minutes entered this way contribute to analytics.

An unmarked event means no final result was recorded. It is different from skipped. Marking outcomes is what gives completion and timing reports their evidence.

## Wrapping up the day

Open the wrap-up button in the assistant panel; its badge shows how many finished events still need a result. This feature works without an AI provider and does not send a chat message.

The list includes today's finished events and outstanding ones from the previous three weeks. Earlier backlog is shown in batches of up to 40 rows, while today's items remain visible.

Each row offers **Done**, **Move**, or **Skip**. If a session has recorded activity, DAYDRIVE suggests Done. Otherwise it suggests Move. Nothing is applied merely because it is highlighted. Use **Accept all suggestions** if the suggestions fit, or answer each row separately.

Move carries the event to tomorrow at the same time, together with its continuation note. A resolved row stays visible with a receipt of what changed. If you keep moving the same work, DAYDRIVE asks what is getting in the way and offers a smaller adjustment: shorten the slot, try an earlier start, write a first step, or remove work that no longer matters.

A wrap-up reminder can appear after the day has ended. Ignoring it makes it less frequent; the button and backlog remain available.

## Analytics

Open the analytics tab and select **Week**, **Month**, or **3 months**. These are calendar periods. Weeks run Monday–Sunday. Navigation arrows move to the previous or next period. Comparisons use the immediately preceding period of the same kind.

| Measure | What it tells you |
| --- | --- |
| Completion | The share of recorded work units finished successfully |
| Plan accuracy | How close actual duration was to its estimate |
| On-time start | Starts no more than ten minutes after the planned start |
| Focus time | Recorded actual working time |
| Workload | Occupied time compared with a 16-hour available day |
| Peak activity | The hour with the most occupied time |

Plan accuracy is 100% for an exact match. A 30-minute plan that took 45 minutes scores 50%; one that took 60 minutes scores 0%. The report averages these scores for measured sessions. Events and recorded work crossing midnight contribute time to the appropriate days.

The workload chart uses daily columns for a week or month and weekly columns for the three-month view. Category breakdowns show where time went. A missing measurement usually means there is not enough recorded history yet.

Insights look for patterns such as underestimating duration, frequently moving work, overloaded days, or a strong time of day. They use calculations from your history and show the sample size and confidence. They do not require an AI model and do not change the calendar. The adjacent assistant action lets you discuss an insight and decide what to do about it.

## The assistant

### Conversations

Open the assistant from the planner's summary card or its shortcut. Ask a question, describe work you need to do, or request a calendar change. For example:

> Find a free 45-minute slot tomorrow afternoon for reviewing the draft.

The assistant can read tasks and events, find gaps, review analytics, create or edit work, and move or copy events and days. Step-aware tools can work with projects too. Whether an action is saved immediately depends on your permission setting.

During a request, the status line shows the current step. Use Stop to interrupt. Below a reply, expand its activity list to see what was read, proposed, applied, or failed. A written suggestion alone is not proof that an event was saved; check the action result or calendar.

Conversation history is stored locally. You can create threads, search, pin, rename, and delete them. The last active conversation is restored when the application opens.

### Permissions

Under **Settings → AI → AI permissions**, choose:

| Setting | Behaviour |
| --- | --- |
| Confirm every action | Calendar and task changes wait for your confirmation |
| Auto-apply safe actions | Creates and edits can be applied; deletion and whole-day changes wait |
| Full schedule access | All supported schedule changes can be applied, including deletion |

Permissions are checked for individual actions. Read a proposed change before pressing Confirm. MCP servers may add capabilities outside your calendar; connect servers whose tools you intend the model to use.

### Memory

The Memory panel contains facts kept between conversations: preferences, routines, constraints, or goals. You can add a note with a short key and value, edit or disable it, delete it, or forget everything. The AI settings also let you switch memory off.

Memory is stored in the local database. External providers receive it only when external context sharing is enabled.

### Attachments

Use the attachment button to add an image or document before sending a message. The maximum file size is 25 MiB.

Images: PNG, JPEG/JFIF, WebP, BMP, and GIF. Images are resized to a maximum long edge of 1536 pixels before sending, so the model sees a reduced version. Image input requires a model with vision support.

Documents: PDF, DOCX, TXT, Markdown, CSV, TSV, JSON, XML, HTML, ICS, LOG, YAML, and YML. DAYDRIVE extracts text before sending it. A scanned PDF with no text layer may not produce useful text; document attachment is not an OCR or calendar-import command.

Check the attachment preview in the composer. If a request fails, the attachment returns there so you can retry.

## Connecting an AI provider

You can use the calendar, tasks, sessions, wrap-up, and analytics without setting up AI. Provider controls live under **Settings → AI**. The model button in chat also lets you switch providers.

### Built-in local model

1. Select **Local model (GGUF)**.
2. Choose a catalogue model and press Download.
3. Wait for the model and local engine to finish downloading.
4. Start the model or send a message to start it on demand.

The catalogue offers Qwen3.5 9B (about 7.1 GB), Ministral 3 8B Reasoning (about 6.1 GB), and the advanced Granite 4.1 8B (about 6.3 GB). Catalogue downloads use Q5_K_M precision. Allow extra disk space for the engine and, when used, a vision projector. Working memory needs exceed the weight-file size.

Automatic tuning reads the available CPU, RAM, and GPU and chooses suitable engine settings and response budgets. Read the hardware summary and warnings. Turn automatic tuning off to set context size, threads, GPU use, and generation budgets yourself. A larger context uses more memory.

On Linux, GPU detection works before a model engine is downloaded. It combines Vulkan, DRM device information and, when available, `nvidia-smi`. Integrated graphics are distinguished from dedicated cards; shared RAM is not counted as dedicated VRAM. Use the hardware detection button to refresh the devices and free video memory.

For image input with Qwen or Ministral, enable vision and download the matching projector. Granite's catalogue entry has no vision projector.

You can instead choose a GGUF file already on disk. DAYDRIVE uses that file in place. For vision, select the projector made for that exact model. If you move either file later, update the paths.

**Stop model** releases its memory. Idle sleep can stop it after inactivity. Delete removes downloaded files for the selected catalogue model; settings can also offer to remove retired precision files from older versions. Models are not bundled in the application download.

Before a local model is installed, a limited offline command parser can handle simple task requests. It is not a conversational model.

### Custom API

Select **Custom API**, enter the server's base address and model identifier, and enter an API key if needed. Use **Save and test connection** to store and check the key. Other connection fields save automatically.

The API must support the chat-completions format. A model with tool calling is needed for reliable calendar actions. For an Ollama installation, its compatible API can be reached through a base address such as `http://localhost:11434/v1`; enter the exact name of a model already installed there.

You can supply the key through `INTELLIGENT_SCHEDULER_API_KEY` instead of saving it in settings. Saved keys are encrypted with a key file in the data directory. Protect that whole directory: it contains both your data and what the application needs to decrypt stored credentials.

### MCP tools

To give your selected model extra tools, enable the option to expose an MCP server's tools. Choose the transport and connection:

- **StreamableHttp:** enter the server address.
- **Stdio:** enter the executable and arguments that start the server.

Use **Show available tools** in the advanced AI settings to check the connection. The list includes built-in tools and the connected server's tools, whose names have an `mcp_` prefix.

The separate **MCP** provider card is a compatibility mode for a server that produces replies itself. It calls a server tool that accepts `prompt` and `context`. A general-purpose MCP tool server normally belongs in the extra-tools setup above.

Stdio processes receive a restricted environment. If a server expects credentials from your shell environment, configure its supported authentication method explicitly.

### Request settings

Local and API generation settings are stored separately. Temperature controls variation; top-p narrows token sampling; the response limit caps answer length. Tool steps limit how many rounds of tool use a request can take. History depth controls how much conversation is replayed, and the timeout limits waiting.

Local automatic tuning can manage answer length, history, and timeout. Those controls become editable when automatic tuning is off. Reset returns the selected provider's controls to their defaults. Additional instructions let you set preferences such as “keep mornings free for writing.”

If a provider rejects a request parameter, DAYDRIVE can retry without the unsupported option. Other provider failures appear with their error details. Check the address, model name, credentials, and server's tool support before retrying.

### Context and privacy

The built-in local model receives app context locally. For external APIs and MCP, **Share context with external AI** controls automatic sharing of calendar context, history, and memory. Leave it off if you do not want those included. Your message and any attachment you send still go to the provider you selected.

Cloud sync and update checks are separate features with separate settings. Turning off external AI context does not switch them off.

## Appearance and behaviour

**General** contains language, startup, time format, and keyboard settings. DAYDRIVE supports English, Ukrainian, Russian, Norwegian Bokmål, German, Spanish, Simplified Chinese, Japanese, Korean, French, Portuguese, Polish, Hindi, Italian, Indonesian, and Turkish. Changing language does not require a restart.

Enable launch at startup if you want DAYDRIVE available after signing in. Start hidden keeps the full panel out of the way. Keep on top controls whether the panel stays above other windows; Pin open controls automatic hiding.

**Appearance** customises MARELIAN, the application's single theme, in light and dark modes. Each mode remembers its own background, panels, text, borders, accent, secondary colours, selection, status colours and shadows. Click a swatch to open its colour picker, or enter `#RRGGBB` / `#AARRGGBB`. An empty field follows the automatic palette; the reset icon clears that colour. The accent can come from MARELIAN, the system, wallpaper, or a custom colour. Other colour overrides apply with any accent source.

Expand **Shape and typography** to adjust the corners of windows, cards, list items and controls, text size (80–125%) and shadow strength (0–200%). These settings are also separate for light and dark modes. **Reset this mode** restores the current mode's colours, corners, text size and shadows. **Reset all appearance** restores both modes and the shared appearance preferences.

**Export theme** saves both modes and shared appearance preferences to a `.daydrive-theme.json` file. **Import theme** applies such a file immediately. These files contain only appearance settings. A custom font must be installed in DAYDRIVE on the receiving machine; otherwise the theme uses Inter. Earlier Noctalia, Celestia and Windows theme selections are migrated to MARELIAN, retaining saved custom colours.

On Linux, **System accent** follows desktop settings or a generated Noctalia/pywal palette. **Wallpaper** samples the current image. Both update in the background when the source changes; custom colours stay as chosen. Supported wallpaper tools and an explicit image override are listed in the [Linux guide](LINUX-WINDOW-MANAGERS.md#system-and-wallpaper-colours).

Choose a visual palette for chart, category, and priority colours. The accessible palette provides another option when colours are hard to distinguish. Fonts can come from the bundled set, the system, or a TTF/OTF file you add.

The mascot can be hidden, coloured by state, or assigned a fixed colour. Transparency is adjustable from 0 to 80%; the result depends on your desktop compositor. Reduce motion disables movement transitions.

Widget settings control whether it stays open and how long it waits before hiding. A pending session-result question keeps it available until answered.

## Reminders and focus mode

In edge mode, reminders appear as cards beside the petal before events start or end. In windowed mode, they appear in a banner inside DAYDRIVE, with the optional reminder sound; they do not open another window or steal focus. Enable or disable reminders and their sound in General.

**Quiet hours** suppress reminders for 30 minutes, one hour, two hours, eight hours, or until you turn them back on. Quiet hours survive a restart. You can change them from settings or the tray menu.

**Focus mode** changes how DAYDRIVE behaves while another application has the full screen. Choose whether it releases global shortcuts, holds reminders back, and hides the edge strip. Held reminders arrive together after focus mode ends. The tray icon remains a way back into the planner.

Windows and Linux/X11 can detect full-screen applications automatically. Wayland does not expose the required information, so use **Hold it on** manually there. The manual setting is cleared after restarting DAYDRIVE. The settings page reports whether automatic detection is available on your desktop.

## Keyboard shortcuts

| Default | Action |
| --- | --- |
| Ctrl+Alt+Space | Show or hide the planner |
| Ctrl+Alt+W | Show or hide the quick widget |
| Ctrl+Alt+A | Open the assistant |
| Ctrl+Alt+S | Start or pause the session |
| Ctrl+Alt+E | Stop the session |
| Ctrl+Alt+Q | Toggle quiet hours |

On Windows these can work across the desktop. On Linux they work while a DAYDRIVE window has focus; use compositor bindings and the `--action` commands in the [Linux guide](LINUX-WINDOW-MANAGERS.md#desktop-shortcuts) for desktop-wide shortcuts. In windowed mode, the planner shortcut brings the window forward and the quick-widget shortcut returns to the planner inside that same window.

Rebind a shortcut under **Settings → General → Shortcuts**: click its binding and press the new combination. Escape cancels capture; Backspace disables a binding. A conflict marker means two actions share a binding or another application holds it. Reset one binding or restore all defaults if needed.

## Data, backups, and sync

### Where data lives

Use **Open data folder** in settings to find the directory used by the running copy. Typical installed locations are:

- Windows: `%LOCALAPPDATA%\IntelligentScheduler`
- Linux: `~/.local/share/IntelligentScheduler` (the system's local application-data location)
- Portable: `Data` beside the executable

The database is `intelligent-scheduler.db`. Chats, memory, tasks, events, results, and settings live there. Downloaded AI files are under `local-ai`; the credential key is `.assistant-secrets.key`; backups use `Backups`; crash details are written to `daydrive-crash.log`.

To choose another directory, launch with `--data-dir=/path/to/data` or set `INTELLIGENT_SCHEDULER_DATA_DIR`. An explicit command-line path takes precedence over the environment, followed by portable mode and then the normal account directory.

### Back up, restore, or move a copy

1. Disable launch at startup if you are moving the installation.
2. Exit DAYDRIVE through the tray menu on every copy using that directory.
3. Copy the entire data directory, including hidden files, to a safe location.
4. To restore, keep a copy of the current directory, then replace it with the saved one while DAYDRIVE is closed.
5. Start DAYDRIVE and check the calendar and settings before deleting the previous copy.

Copying only the database omits downloaded models and the key used for saved credentials. Do not replace or copy the live database while DAYDRIVE is running: SQLite may also be using `-wal` and `-shm` files.

For a fresh start, rename the data directory instead of deleting it, then launch DAYDRIVE again. Keep the old folder until you have checked the result.

### Synced folder

Under **Data and sync**, select **Synced folder** and choose a folder already handled by your cloud client, such as Google Drive, OneDrive, or Dropbox. Give each computer a recognisable name. Point the other computer at the same synced folder.

DAYDRIVE writes snapshots there; it does not turn that folder into the live database. Keep the app's data directory separate from the sync destination. Your cloud client's delivery time still matters, so let it finish before switching computers.

### Direct Google Drive connection

The direct connection uses Drive's hidden app-data area. It needs your own Google OAuth desktop client:

1. Create a project in Google Cloud Console and enable Google Drive API.
2. Configure the OAuth consent screen and add your account as a test user if required by your app's publishing state.
3. Create an OAuth client of type **Desktop app**.
4. Paste its client ID and secret into DAYDRIVE, then choose **Connect Google account**.
5. Complete sign-in and grant the requested access in the browser.

The Google setup link is available in settings. The client secret is saved encrypted locally. Disconnecting the account leaves the existing cloud snapshot intact.

### What sync does

Sync transfers your schedule, tasks, steps, progress, chats, and memory using a database snapshot. Settings and credentials remain specific to each computer. The status area shows the last exchange, pending changes, the cloud copy's source, and recent history.

**Sync now** runs an exchange. **Check the cloud** inspects the remote copy. With **Sync on its own** enabled, checks run shortly after launch, every 15 minutes, and on normal exit. Do not rely on an exit upload during operating-system shutdown.

If both copies have changed, DAYDRIVE asks which to keep. **Keep this computer** replaces the cloud snapshot; **Keep the cloud copy** replaces local user data after a restart. It does not merge individual edits. Read the device and timestamp before choosing.

A downloaded snapshot waits until the next launch. Press **Restart DAYDRIVE**, or exit and reopen it. The previous database is saved under `Backups` before replacement.

## Updates and troubleshooting

### Update DAYDRIVE

The About section shows the installed version and release links. Automatic update checks contact GitHub at most once a day. Use Check now to request a fresh check, or Skip to stop reminders about that version. DAYDRIVE opens the release page; installing a new version is a separate step.

For an installed copy, exit and run the newer installer or install the new `.deb`. For portable, extract the new version into a new folder and copy the old `Data` folder into it while both copies are closed. Keep `daydrive.portable` beside the new executable. Do not start both copies against the same data directory.

### The panel disappeared

In windowed mode, find DAYDRIVE in the desktop's window list; Start hidden is ignored so a missing tray cannot leave it inaccessible. In edge mode, try the petal, tray menu, or, on Windows, Ctrl+Alt+Space. If focus mode hid the petal, turn off the manual hold from the tray. If startup is hidden, a full panel is not expected to open by itself. Some Linux desktops need tray/status-notifier support enabled to display the icon.

### Linux will not launch

Run `./DAYDRIVE` from a terminal to read the error. Confirm the archive was fully extracted and the file is executable (`chmod +x DAYDRIVE`). A permission error may mean the folder or drive is mounted with `noexec`.

If panels open in the middle of the screen or overlap under a tiling WM, follow the [Linux window-manager guide](LINUX-WINDOW-MANAGERS.md). Check GTK layer-shell and compositor protocol support, and remove a forced legacy X11 edge override. To use all sections in one ordinary window, start with `--shell-mode=windowed`; Wayland selects this fallback automatically when the native host is unavailable.

Missing shared-library errors name the system dependency to install. Use the `.deb` on Debian/Ubuntu so the package manager can resolve dependencies. A Wayland session needs XWayland for this build. Automatic focus detection and desktop-wide shortcuts have the limitations described above.

### A local model is slow or cannot start

Read the hardware and memory warning in AI settings. Close other memory-heavy programs, reduce context length, or try CPU mode if GPU initialisation fails. Stop another running model before starting this one. Check that a custom GGUF and its projector still exist at the selected paths. Downloads need internet access and free disk space.

### An assistant reply did not change the calendar

Check the action list under the reply. A proposal may be waiting for confirmation. Also check AI permissions, external context sharing, and whether the selected provider supports tools. Use Show available tools to diagnose MCP connections. A provider error should be fixed before retrying the request.

### Sync did not finish

Read the sync status and History. Check that the selected folder still exists and is writable, or reconnect the Google account if access expired. A copy waiting for restart has already downloaded; restart to apply it. A conflict needs a choice between the two copies.

### Report a problem

Open the issue link in About. Include the DAYDRIVE version, operating system, what you expected, and the steps that produced the problem. Relevant entries from `daydrive-crash.log` can help. Review logs and screenshots before posting them so they do not expose private tasks, conversations, or credentials.
