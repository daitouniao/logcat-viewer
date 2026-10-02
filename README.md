# logcat viewer

[![Version: V0.0.9](https://img.shields.io/badge/version-V0.0.9-green)](logcat.csproj)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows-lightgrey)](#requirements)

A Windows desktop viewer for Android logs (`logcat`). Built with WinForms on .NET 10, it opens **tens of millions of log lines instantly** using memory-mapped files and a columnar index — and it also drives your device over ADB: live capture, screenshots, screen recording, two-pane file transfer, and APK install/uninstall.

**English** | [简体中文](README.zh-CN.md)

> **Note:** the application UI is currently **Chinese-only**. The feature list below describes what the app does; the UI strings themselves are not yet translated.

<!-- TODO: replace with a real screenshot / GIF before release -->
<!-- ![logcat viewer](docs/screenshot.png) -->

## Why this one

Most logcat viewers choke on large captures. This one keeps the text on disk (mmap) and only the fixed-width columns in memory, so a multi-GB log file opens without loading it into RAM and scrolls at a stable frame rate.

| | |
|---|---|
| **Instant open on huge files** | Memory-mapped + parallel block indexing, with a cancellable progress bar |
| **Virtual list** | Only visible rows are rendered, so scrolling stays smooth at 10M+ rows |
| **Six formats auto-detected** | `threadtime` / `time` / `long` / `brief` / `tag` / `ymd`, via a byte-level fast path with regex fallback |
| **Multi-line joining** | Stack traces and wrapped messages fold back into their parent record (toggleable) |
| **Level colours** | V/D/I/W/E/F/A colour-coded |
| **Full record view** | Double-click a row (`Enter`) to see the untruncated original text |

## Features

### Filtering

- **Level**: freely toggle V/D/I/W/E/F/A, with select-all / clear-all
- **Tag / Message**: `or` / `and` combination, regex, case-sensitivity, exclude mode
- **PID / TID**: multiple values space-separated, excludable
- **Minute**: filter by the timestamp's minute value, e.g. `05 20`
- **Marked rows only**
- **Auto-apply**: refreshes 400 ms after you stop typing; `Ctrl+Enter` applies manually
- **Detached filter window**: the filter panel lives in a non-modal window that floats above the main window, so the entire main area is left to the log list. Closing it only collapses it — your filter conditions survive. Reopen via `Ctrl+F`
- **Saved filters**: separate favourites for Tag and Message, up to 60 each, persisted

### Marking and export

- Press `M` (or double-click the line-number column) to mark/unmark; `F2` / `Shift+F2` jump between marks
- Right-click to filter directly by that tag / PID / TID / minute
- Export the current result, or only marked rows; optional "single-line" mode (newlines become `\n`) for spreadsheets
- Copy as raw text or as tab-separated table rows

### ADB device features

| Feature | Description |
| --- | --- |
| Refresh devices | Enumerates online devices; hot-plug is monitored automatically |
| Start / stop capture | Streams `logcat` to a temp file and syncs incrementally into the list, following the tail. Capture and filtering/export are mutually excluded on the document (serialised access), and pure tail appends only re-filter the new rows — per-second cost drops from O(total) to O(added) |
| Save log | Saves the current capture as a `.log` file |
| Device operations | One drop-down entry point (screenshot / screen record / file browser / install-uninstall APK / command window), all merged into a single tabbed "Device operations" window |

### Device operations window

Screenshots, screen recording, file browsing, APK install/uninstall and the command window no longer each spawn their own dialog. They are merged into one non-modal, singleton window with tabs:

- **Screenshot / Screen record / File browser** — one page with three sub-tabs. *Screenshot & record*: preview and save, or pull the recording to your PC. *File browser*: a two-pane device ↔ PC file manager with transfer, refresh, directory favourites, and rename; restricted directories such as `/data/data` need root (the toolbar's run-as button acts as a mode drop-down); transfers tolerate files being in use, drag-and-drop preserves relative structure, and `sync` is issued after upload so data survives an unplug
- **Install / uninstall APK** — see below
- **Command window** — see below

Tabs are created lazily on first open and keep their state; switching devices rebuilds device-bound tabs automatically; closing the window cleans up (stops recording, cancels command tasks, writes options back).

### Install / uninstall APK

- **Two install channels**
  - `adb install` with optional `-r` (keep data, on by default), `-d` (allow downgrade), `-g` (grant all permissions), `-t` (allow test packages)
  - `pm install` as a fallback when `adb install` is disabled — pushes the APK to a device temp directory (default `/data/local/tmp`) and cleans up afterwards
- **Two uninstall channels**: `adb uninstall` or `pm uninstall`, with optional `-k` (keep data and cache); the `pm` channel can run as root via `su -c`, which uninstalling system apps usually requires
- **Installed app list**: pulls third-party apps on connect (name / package / APK path), double-click or `Enter` to uninstall
- **Favourites** for local APK paths and package names; options are remembered between sessions

### Command window

Collect the commands you keep retyping, organise them into categories, and re-run them on the selected device (`Ctrl+Shift+C`).

- **Two execution channels**
  - *Device shell* — via `adb shell`; when root is ticked it is wrapped as `su -c '...'` with POSIX single-quote escaping
  - *Local adb* — calls `adb.exe` directly for subcommands like `install` / `reboot` / `push`, automatically appending `-s <serial>`, and reports the exit code
- **Categories**: 112 built-in commands across 5 categories (`shell`, `dumpsys`, apps & packages, logs & exceptions, `adb`); create / rename / delete your own
- **Favourites and history**: up to 300 favourites deduplicated by command text + channel; execution history keeps the last 120 with usage counts
- **Placeholder parameters**: `{name}` in a command is a parameter prompted for before execution, with the last value remembered — `{pkg}`, `{pid}`, `{apk}`, `{ip}`, `{activity}`, `{url}`, `{tag}`, `{file}`
- **Streaming output**: line-by-line echo with line count and elapsed time; long-running commands like `logcat` can be stopped or time out (default 30 s, 0 = unlimited)
- **No accidental runs**: double-click or `Enter` only fills the input box; you still have to press Execute

### Window position memory (multi-monitor)

The main window's position, size and state are saved on close and restored on next launch — including which monitor it was on, for both horizontally and vertically arranged displays. If that monitor is no longer connected, the saved geometry fails validation and the app falls back to the default layout instead of restoring off-screen. This is verifiable: `%LOCALAPPDATA%\logcat\startup.log` records the screen topology, the coordinates read, the validation result, the actual landing position, and the coordinates written on close.

### High-DPI support

All 14 hand-written forms declare `AutoScaleMode.Dpi` and scale with the system DPI, so button text is no longer clipped on high-scaling displays. The default font is Microsoft YaHei UI, which fixes the missing-CJK-glyph problem that made Chinese text render as clipped glyphs under GDI font fallback at high DPI.

## Requirements

- **Windows**
- **.NET 10 SDK** (`net10.0-windows`, WinForms) — required to build; not needed to run a published build
- **`adb`** on your `PATH` for the ADB features (the app will try to start the ADB server itself)

## Build and run

```powershell
dotnet build                # build
dotnet run                  # run
dotnet publish -c Release   # publish
```

You can also open `logcat.slnx` / `logcat.csproj` directly in Visual Studio.

## Tests

Log parsing, the columnar index (including incremental append and line-boundary alignment), the filter
engine and the persistence stores are covered by xUnit tests. The UI and device/system-integration layers
are intentionally excluded from the coverage scope.

```powershell
# run the suite
dotnet test tests/logcat.Tests/logcat.Tests.csproj

# run with coverage (coverlet -> cobertura), then render a readable report
dotnet test tests/logcat.Tests/logcat.Tests.csproj --collect:"XPlat Code Coverage" --settings tests/logcat.Tests/coverlet.runsettings
python tests/coverage-report.py     # writes tests/coverage-report.html
```

Current status: **340 tests, all passing**; **98.55%** line coverage (3,406 / 3,456 lines).

The scope is defined in `tests/logcat.Tests/coverlet.runsettings` and excludes two groups:

- **UI layer** — `Forms` / `Controls` / `frmMain` / `Program`, plus `DpiFix` / `DpiDiag` (high-DPI layout fallback and diagnostics): WinForms construction and layout depend on a message pump and STA threads, so unit-testing them is costly and low-value.
- **Device / system integration layer** — `AdbManager` (needs a real adb server and device), `LogcatStream` (device stream), `ClipboardHelper` (Windows clipboard), `StartupLog` (writes into the user profile).

> In a restricted sandbox (some IDE-managed terminals) the test host may be denied writes to `%TEMP%`, which
> fails many tests. Point the temp directory into the workspace instead:
> `TMP=<dir inside the repo> TEMP=<same> dotnet test ...` — see [`docs/PUBLISHING.md`](docs/PUBLISHING.md).

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+O` | Open a log file |
| `F5` | Reload (incremental; skipped if the file is unchanged) |
| `Ctrl+E` | Export current result |
| `Ctrl+Enter` | Apply filter |
| `Ctrl+F` | Open the filter window and focus the Message box |
| `Ctrl+C` | Copy selected rows |
| `Ctrl+Shift+C` | Open the command window |
| `F2` / `Shift+F2` | Previous / next mark |
| `F3` / `Shift+F3` | Find next / previous in results (non-regex mode) |
| `M` | Mark the current row |
| `Enter` | View the full record for the current row |
| `Esc` | Stop the current task and clear the selection |

You can also drag a log file straight onto the window.

## Filter syntax

- Separate multiple keywords with spaces: `crash anr`
- Wrap phrases in double quotes: `"null pointer"`
- The `or` / `and` drop-down decides how multiple terms combine (Tag defaults to `or`, Message to `and`)
- Tick *regex* to match with .NET regular expressions
- Tick *exclude* to drop matches

## License

[Apache License 2.0](LICENSE) — free to use, modify and distribute, including commercially. Third-party components and their licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); the full disclaimer is in [DISCLAIMER.md](DISCLAIMER.md).

The source was produced with AI assistance and is not attributed to a named individual author; copyright is declared collectively as `logcat viewer contributors`.

## Disclaimer

See [DISCLAIMER.md](DISCLAIMER.md) for the full terms. In short:

- **Device operations are at your own risk** — screenshots, screen recording, file transfer, APK install/uninstall and `adb shell` / root commands act directly on your device
- **root and restricted directories** — accessing `/data/data` and similar requires root or run-as, and may damage app data or affect your warranty
- **Lawful use only** — only on devices and data you are authorised to access
- **Logs contain sensitive data** — logcat output often includes credentials, tokens and location data; sanitise before sharing
- **Not an official tool** — not affiliated with Google, Android or the ADB team
