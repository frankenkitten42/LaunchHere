# LaunchHere

> **⚠️ Beta release (v0.1.3-beta).** LaunchHere works in day-to-day use but is still being tested. Expect rough edges and some apps that don't behave (see [Known limitations](#known-limitations)). Bug reports are welcome. Turn on **Detailed log** (Settings > Advanced) and include the relevant lines from `launch-here.log`.

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

The released `LaunchHere.exe` is self-contained (about 75 MB), so .NET does not need to be installed.

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
  - **Exit**

Only one copy runs at a time.

**Start with Windows** (in Settings) adds LaunchHere to your user's startup apps (no admin needed). It starts the exe from wherever it is right now. If you move `LaunchHere.exe` to another folder, turn the option on again from the new location. You can also switch it off in Task Manager > Startup apps.

### Admin (elevated) apps

Windows does not let a normal program move windows that belong to an app running as administrator. To have those moved too, run LaunchHere as administrator.

## Settings

Open **Settings...** from the tray menu. Changes apply as soon as you click **OK** or **Apply**.

- **Apps**: move every app except the ones listed, or only the ones listed. Add apps by picking from the ones running
  now, browsing for an `.exe`, or typing an exe name. An exe name matches every copy of that app; a full path
  matches only that copy.
- **Behavior**: which launches to watch, bringing open windows to the clicked taskbar, and Start with Windows.
- **Updates**: **Check for updates** looks for a newer release on GitHub. **Update now** downloads it, checks it
  against the release's SHA-256 checksum, replaces `LaunchHere.exe` and restarts. If the new version fails to start,
  the current one is put back. Your settings and Start with Windows are kept.
- **Advanced**: timings, how windows that are too wide get shrunk, and the detailed log.

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
  "DebugLog": true,
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
| `DebugLog` | Write detailed entries to `launch-here.log`. |

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

This produces a single standalone `dist\LaunchHere.exe` (about 50 MB, as the .NET runtime is bundled).

To check that window moves work on your monitors, run the self-test (needs two or more monitors). It writes results to `launch-here.log` and exits with code 0 if everything passed:

```powershell
dist\LaunchHere.exe --selftest
```

## How it works

A low-level mouse hook watches for clicks on the taskbars (`Shell_TrayWnd` / `Shell_SecondaryTrayWnd`), jump lists, and desktop icons, and records which monitor each click was on. A WinEvent hook then watches for a new top-level window to appear. It tracks windows rather than processes, so single-process apps such as browsers work. Within the timeout, the first suitable window is moved to the clicked monitor. Its size is scaled to the new monitor's work area and DPI, and maximized windows stay maximized.

## License

[PolyForm Noncommercial 1.0.0](LICENSE.md). Copyright (c) 2026 David Smiley.

You may use, modify and share LaunchHere for **non-commercial** purposes only. Any copy or modified version must keep the copyright notice and license. Selling it or using it commercially is not permitted.
