# LaunchHere

> **⚠️ Beta release (v0.1.1-beta).** LaunchHere works in day-to-day use but is still being tested. Expect rough edges and some apps that don't behave (see [Known limitations](#known-limitations)). Bug reports are welcome. Turn on `DebugLog` and include the relevant lines from `launch-here.log`.

A small Windows tray app for multi-monitor setups: **apps open on the monitor where you clicked to launch them.**

Windows normally opens a new app window on whatever monitor the app remembers (usually your primary one), even if you clicked its icon on the taskbar of your second monitor. LaunchHere notices which monitor the click happened on and moves the new window there, typically within a few milliseconds of it appearing.

## What it handles

- **Taskbar clicks** on the main taskbar and on the taskbars of other monitors
- **Jump lists** (right-click a taskbar icon, then pick an item)
- **Desktop shortcuts** (double-click an icon on any monitor's desktop)
- **Maximized windows** stay maximized on the new monitor
- **Mixed DPI / different resolutions**: windows too big for the target monitor are shrunk to fit
- **Apps that restore their own position** (e.g. Chrome) are checked again briefly and put back
- **Splash screens**: the app's main window that follows a splash screen is moved too

It deliberately leaves things alone when you click the taskbar button of an app that's **already open**. That click just switches to the existing window, which is not moved.

## Requirements

- Windows 10 or 11, x64
- Two or more monitors (it does nothing useful with one)

The released `LaunchHere.exe` is self-contained, so .NET does not need to be installed.

## Usage

1. Put `LaunchHere.exe` in a folder of its own (it writes `config.json` and `launch-here.log` next to itself).
2. Run it. A small icon with two monitors appears in the notification area (system tray).
3. Launch apps from any monitor's taskbar or desktop as usual.

Tray icon:

- **Left-click**: pause / resume (the icon turns grey when paused)
- **Right-click** for a menu with:
  - **Enabled**: pause / resume
  - **Start with Windows**: run LaunchHere automatically when you sign in
  - **Edit settings (config.json)**
  - **Reload settings**: apply changes without restarting
  - **Open log**
  - **Exit**

Only one copy runs at a time.

**Start with Windows** adds LaunchHere to your user's startup apps (no admin needed). It starts the exe from wherever it is right now. If you move `LaunchHere.exe` to another folder, turn the option on again from the new location. You can also switch it off in Task Manager > Startup apps.

### Admin (elevated) apps

Windows does not let a normal program move windows that belong to an app running as administrator. To have those moved too, run LaunchHere as administrator.

## Settings

`config.json` is created with defaults on first run:

```json
{
  "Mode": "Blacklist",
  "Apps": [],
  "WatchTaskbar": true,
  "WatchDesktop": true,
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
| `ArmTimeoutMs` | How long to wait for a new window after a click (covers slow-starting apps). |
| `SameAppGraceMs` | After the first window is moved, further new windows from the same app are moved too for this long (splash screen, then main window). |
| `ActivationCancelMs` | If an already-open window comes to the front within this time, the click was a switch, not a launch, so stop waiting. |
| `RecheckDelaysMs` | When (ms after the move) to check again for apps that move themselves back. |
| `VerticalShrinkShare` | When a window is too wide for the target monitor, how much of the width reduction is also applied to the height (0 = keep height, 1 = proportional). |
| `DebugLog` | Write detailed entries to `launch-here.log`. |

Use **Reload settings** in the tray menu after editing.

## Known limitations

- **Discord** (and some other apps that hide to the tray instead of closing) can behave inconsistently, because showing a hidden window can look like a new launch.
- Apps launched some other way (Start menu, Run box, File Explorer, scripts) are not moved; only taskbar and desktop launches are.
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
