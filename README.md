# LaunchHere

> **⚠️ Beta release (v0.2.1-beta).** LaunchHere works in day-to-day use but is still being tested. Expect rough edges and some apps that don't behave (see [Known limitations](#known-limitations)). Bug reports are welcome: use **Report a problem...** in the tray menu (see [Reporting a problem](#reporting-a-problem)).

A small Windows tray app for multi-monitor setups: **apps open on the monitor where you clicked to launch them.**

Windows normally opens a new app window on whatever monitor the app remembers (usually your primary one), even if you clicked its icon on the taskbar of your second monitor. LaunchHere notices which monitor the click happened on and moves the new window there, typically within a few milliseconds of it appearing.

**[⬇️ Download LaunchHere.exe](https://github.com/frankenkitten42/LaunchHere/releases)** from the Releases page (under **Assets** of the newest release).

## What it handles

- **Taskbar clicks** on the main taskbar and on the taskbars of other monitors
- **Jump lists** (right-click a taskbar icon, then pick an item)
- **Desktop shortcuts** (double-click an icon on any monitor's desktop)
- **Maximized windows** stay maximized on the new monitor
- **Mixed DPI / different resolutions**: windows too big for the target monitor are shrunk to fit
- **Apps that restore their own position** (e.g. Chrome) are checked again briefly and put back
- **Splash screens**: the app's main window that follows a splash screen is moved too
- **Already-open windows**: click an app's taskbar icon, or one of its window previews, on another monitor's taskbar and that window comes over to that monitor. Maximized and full-screen windows stay that way, and minimized windows are restored straight onto the monitor you clicked. You can turn this off in Settings.
- **Screen-sized windows** (Chrome opens new windows like this) are maximized on the new monitor instead of being shrunk a little

For already-open windows, set Windows' **Settings > Personalization > Taskbar > Taskbar behaviors > "Show my taskbar apps on"** to **All taskbars**, so every app's icon appears on every monitor's taskbar.

## Requirements

- Windows 10 or 11, x64
- Two or more monitors (it does nothing useful with one)

The released `LaunchHere.exe` is self-contained (about 165 MB), so .NET does not need to be installed. It is a big file because it carries its own copy of .NET, stored uncompressed so that it uses less memory while running (about 12 MB when idle).

## Usage

1. [Download `LaunchHere.exe`](https://github.com/frankenkitten42/LaunchHere/releases) and put it in a folder of its own (it writes `config.json` and `launch-here.log` next to itself).
2. Run it. A small icon with two monitors appears in the notification area (system tray).
3. Launch apps from any monitor's taskbar or desktop as usual.

Tray icon:

- **Left-click**: pause / resume (the icon turns grey when paused)
- **Right-click** for a menu with:
  - **Enabled**: pause / resume
  - **Settings...**: the settings window (see below), including **Start with Windows**
  - **Reload config.json**: apply changes made by hand to `config.json`
  - **Open log**
  - **Report a problem...**: see [Reporting a problem](#reporting-a-problem)
  - **Exit**

Only one copy runs at a time.

**Start with Windows** (in Settings) adds LaunchHere to your user's startup apps (no admin needed). It starts the exe from wherever it is right now. If you move `LaunchHere.exe` to another folder, turn the option on again from the new location. You can also switch it off in Task Manager > Startup apps.

### Reporting a problem

**Report a problem...** (tray menu, or Settings > Troubleshooting) opens a window with a report you can check and edit: your LaunchHere and Windows versions, monitor layout, settings and the last 50 lines of `launch-here.log`. Window titles are hidden unless you untick **Hide window titles**, and your Windows user name is taken out of file paths.

- **Open GitHub issue** opens a new issue on GitHub in your browser with the report filled in. Nothing is sent until you press **Create** there (you need a free GitHub account). Issues are public. If the report is too long to fill in automatically, the details are copied to your clipboard so you can paste them in.
- **Copy to clipboard** copies the report instead.

With verbose logging off, the log only has the basics. For a more useful report, press **Turn on verbose logging**, make the problem happen again, then press **Refresh**.

### Admin (elevated) apps

Windows does not let a normal program move windows that belong to an app running as administrator. To have those moved too, run LaunchHere as administrator.

## Settings

Open **Settings...** from the tray menu. Changes apply as soon as you click **OK** or **Apply**.

- **Apps**: move every app except the ones listed, or only the ones listed. Add apps by picking from the ones running
  now, browsing for an `.exe`, or typing an exe name. An exe name matches every copy of that app; a full path
  matches only that copy.
- **Behavior**: which launches to watch, bringing open windows to the clicked taskbar, and Start with Windows.
- **Advanced**: timings, and how windows that are too wide get shrunk.
- **Troubleshooting**: **Verbose logging** (off by default) writes every window LaunchHere looks at to
  `launch-here.log`. Start, settings changes, updates and errors are always logged. **Report a problem...** is here too.
- **Updates**: **Check for updates** looks for a newer release on GitHub. **Update now** downloads it, checks it
  against the release's SHA-256 checksum and the developer's signature (an update that isn't signed with the LaunchHere
  release key is refused), replaces `LaunchHere.exe` and restarts. If the new version fails to start,
  the current one is put back. Your settings and Start with Windows are kept.

The settings window follows Windows' light/dark mode and accent colour.

Everything is stored in `config.json` next to the exe, which you can also edit by hand (then use **Reload config.json**).
It is created with defaults on first run:

```json
{
  "Mode": "Blacklist",
  "Apps": [],
  "WatchTaskbar": true,
  "WatchDesktop": true,
  "MoveOpenWindows": true,
  "ArmTimeoutMs": 8000,
  "SameAppGraceMs": 3000,
  "ActivationCancelMs": 1500,
  "RecheckDelaysMs": [50, 150, 400, 1000, 2000],
  "VerboseLog": false,
  "VerticalShrinkShare": 0.2
}
```

| Setting | Meaning |
|---|---|
| `Mode` | `Blacklist`: move every app **except** those in `Apps`. `Whitelist`: move **only** apps in `Apps`. |
| `Apps` | Exe names (`"chrome.exe"`) or full paths (`"C:\\Games\\foo.exe"`, with doubled backslashes). Not case-sensitive. |
| `WatchTaskbar` | React to taskbar and jump list clicks. |
| `WatchDesktop` | React to desktop shortcut double-clicks. |
| `MoveOpenWindows` | Clicking the taskbar icon or a window preview of an app that is open on another monitor brings that window to the clicked monitor. |
| `ArmTimeoutMs` | How long to wait for a new window after a click (covers slow-starting apps). |
| `SameAppGraceMs` | After the first window is moved, further new windows from the same app are moved too for this long (splash screen, then main window). |
| `ActivationCancelMs` | If an already-open window comes to the front within this time, the click was a switch, not a launch: stop waiting for a new window (and, with `MoveOpenWindows`, bring that window over). |
| `RecheckDelaysMs` | When (ms after the move) to check again for apps that move themselves back. |
| `VerticalShrinkShare` | When a window is too wide for the target monitor, how much of the width reduction is also applied to the height (0 = keep height, 1 = proportional). |
| `VerboseLog` | Write every window LaunchHere looks at to `launch-here.log` (replaces `DebugLog` from 0.1.3 and earlier). |

Use **Reload config.json** in the tray menu after editing the file by hand.

## Known limitations

- **Discord** (and some other apps that hide to the tray instead of closing) can behave inconsistently, because showing a hidden window can look like a new launch.
- **Start menu** launches work when you click the Start button and pick the app within 8 seconds (`ArmTimeoutMs`). Opening Start with the Windows key, or taking longer than that, isn't covered yet.
- Apps launched some other way (Run box, File Explorer, scripts) are not moved.
- Pinned taskbar icons are shared across all monitors in Windows; per-monitor pinned bars are a possible future feature.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet publish -c Release
```

This produces a single standalone `dist\LaunchHere.exe` (about 165 MB, as the .NET runtime is bundled, uncompressed to save memory).

To check that window moves work on your monitors, run the self-test (needs two or more monitors). It writes results to `launch-here.log` and exits with code 0 if everything passed:

```powershell
dist\LaunchHere.exe --selftest
```

## How it works

A low-level mouse hook watches for clicks on the taskbars (`Shell_TrayWnd` / `Shell_SecondaryTrayWnd`), jump lists, and desktop icons, and records which monitor each click was on. A WinEvent hook then watches for a new top-level window to appear. It tracks windows rather than processes, so single-process apps such as browsers work. Within the timeout, the first suitable window is moved to the clicked monitor. Its size is scaled to the new monitor's work area and DPI, and maximized windows stay maximized.

## License

[PolyForm Noncommercial 1.0.0](LICENSE.md). Copyright (c) 2026 David Smiley.

You may use, modify and share LaunchHere for **non-commercial** purposes only. Any copy or modified version must keep the copyright notice and license. Selling it or using it commercially is not permitted.
