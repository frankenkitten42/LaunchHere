using System.Diagnostics;
using System.Runtime.InteropServices;
using static LaunchHere.Native;

namespace LaunchHere;

/// <summary>
/// The core: watches for launch clicks (taskbar click, desktop double-click), remembers which
/// monitor they happened on, and moves the window the launch produces onto that monitor.
/// All callbacks run on the UI thread's message loop.
/// </summary>
internal sealed class Tracker : IDisposable
{
    sealed class Arm
    {
        public required IntPtr Monitor;
        public required string MonitorName;
        public required string Source;
        public required long StartMs;
        public long ExpiresMs;
        public required bool FromTaskbar;    // a switch to an open window may move it (MoveOpenWindows)
        public required IntPtr PrevForeground; // window in front when the click started
        public int? Pid;              // set once the launched app's first window is claimed
    }

    internal static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging", "MultitaskingViewFrame", "#32768",
    };

    internal static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe", "ShellExperienceHost.exe",
        "ShellHost.exe", "TextInputHost.exe", "LockApp.exe", "PeopleExperienceHost.exe",
    };

    readonly Logger _log;
    Config _cfg;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly int _ownPid = Environment.ProcessId;

    // Delegates must stay referenced or the GC collects them while Windows still calls them.
    readonly LowLevelMouseProc _mouseProc;
    readonly WinEventProc _winEventProc;
    readonly IntPtr _mouseHook, _showHook, _uncloakHook, _foregroundHook;

    Arm? _arm;
    readonly HashSet<IntPtr> _handled = new();     // windows we've already moved for the current arm
    readonly HashSet<IntPtr> _newWindows = new();  // windows that appeared since the current arm
    uint _lastDesktopDownTime;
    POINT _lastDesktopDownPt;
    POINT _jumpListPt;                             // last taskbar right-click
    long _jumpListUntil;                           // jump-list click accepted until this time
    long _clickSerial;                           // bumps on every left click; cancels re-checks
    IntPtr _fgAtDown;                            // foreground window at the last left press

    // Taskbar flyouts: the thumbnail previews shown when an app has several windows.
    static readonly HashSet<string> FlyoutClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "XamlExplorerHostIslandWindow", "TaskListThumbnailWnd",
    };

    public bool Enabled { get; set; } = true;

    public Tracker(Config cfg, Logger log)
    {
        _cfg = cfg;
        _log = log;
        _mouseProc = MouseCallback;
        _winEventProc = WinEventCallback;

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero) _log.Error($"mouse hook failed: {Marshal.GetLastWin32Error()}");

        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _winEventProc, 0, 0, flags);
        _uncloakHook = SetWinEventHook(EVENT_OBJECT_UNCLOAKED, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _winEventProc, 0, 0, flags);
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, flags);
        if (_showHook == IntPtr.Zero || _uncloakHook == IntPtr.Zero || _foregroundHook == IntPtr.Zero)
            _log.Error("window event hook failed");
    }

    public void UpdateConfig(Config cfg) => _cfg = cfg;

    // ------------------------------------------------------------------ clicks

    IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_MBUTTONUP or WM_RBUTTONUP)
            {
                try { OnClick(msg, Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)); }
                catch (Exception ex) { _log.Error($"click handler: {ex}"); }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    void OnClick(int msg, MSLLHOOKSTRUCT info)
    {
        if (msg == WM_LBUTTONDOWN)
        {
            _clickSerial++;
            _fgAtDown = GetForegroundWindow();   // before the click itself changes it
        }

        IntPtr hit = WindowFromPoint(info.pt);
        if (hit == IntPtr.Zero) return;
        IntPtr root = GetAncestor(hit, GA_ROOT);
        string rootClass = ClassOf(root);
        bool onTaskbar = rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

        // Right-click on a taskbar icon opens its jump list ("New window", recent files...).
        if (msg == WM_RBUTTONUP)
        {
            if (_cfg.WatchTaskbar && onTaskbar)
            {
                _jumpListPt = info.pt;
                _jumpListUntil = _clock.ElapsedMilliseconds + 15000;
            }
            return;
        }

        // Taskbar: the launch happens on button release (left click, or middle click = new instance).
        if (msg != WM_LBUTTONDOWN)
        {
            if (!_cfg.WatchTaskbar) return;
            if (onTaskbar)
            {
                _jumpListUntil = 0;
                ArmAt(info.pt, rootClass == "Shell_TrayWnd" ? "main taskbar" : "secondary taskbar");
            }
            else if (msg == WM_LBUTTONUP && _clock.ElapsedMilliseconds < _jumpListUntil)
            {
                // First left-click after a taskbar right-click: if it lands on shell UI it's a
                // jump-list item; use the monitor of the taskbar that was right-clicked.
                _jumpListUntil = 0;
                GetWindowThreadProcessId(root, out uint rpid);
                string proc = Path.GetFileName(ProcessPath(rpid));
                bool shellUi = ShellClasses.Contains(rootClass) || ShellProcesses.Contains(proc)
                    || (proc.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && rootClass != "CabinetWClass");
                _log.Info($"click after taskbar right-click: [{rootClass}] {proc} -> {(shellUi ? "jump list item" : "not a jump list")}");
                if (shellUi) ArmAt(_jumpListPt, "taskbar jump list");
            }
            else if (msg == WM_LBUTTONUP && FlyoutClasses.Contains(rootClass)
                     && (_arm is { FromTaskbar: true } || NearTaskbar(info.pt)))
            {
                // Picked one window from the thumbnail previews of a taskbar icon. The previews also
                // open on hover alone, so the icon need not have been clicked first.
                ArmAt(info.pt, "taskbar thumbnail");
                _ = ThumbnailCheckAsync(_arm!);
            }
            return;
        }

        // Desktop: explorer launches on the second press of a double-click.
        if (!_cfg.WatchDesktop || rootClass is not ("Progman" or "WorkerW"))
        {
            _lastDesktopDownTime = 0;
            return;
        }
        int cx = GetSystemMetrics(SM_CXDOUBLECLK) / 2, cy = GetSystemMetrics(SM_CYDOUBLECLK) / 2;
        bool isDouble = _lastDesktopDownTime != 0
            && info.time - _lastDesktopDownTime <= GetDoubleClickTime()
            && Math.Abs(info.pt.X - _lastDesktopDownPt.X) <= cx
            && Math.Abs(info.pt.Y - _lastDesktopDownPt.Y) <= cy;
        if (isDouble)
        {
            _lastDesktopDownTime = 0;
            ArmAt(info.pt, "desktop");
        }
        else
        {
            _lastDesktopDownTime = info.time;
            _lastDesktopDownPt = info.pt;
        }
    }

    /// <summary>Is the point close to its monitor's taskbar (where the thumbnail previews open)?
    /// Within a quarter of the monitor's size from the taskbar edge.</summary>
    static bool NearTaskbar(POINT pt)
    {
        var m = MonitorInfo(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
        RECT mon = m.rcMonitor, work = m.rcWork;
        int reachY = mon.Height / 4, reachX = mon.Width / 4;
        if (work.Bottom < mon.Bottom) return work.Bottom - pt.Y <= reachY;   // taskbar at the bottom
        if (work.Top > mon.Top) return pt.Y - work.Top <= reachY;            // top
        if (work.Left > mon.Left) return pt.X - work.Left <= reachX;         // left
        if (work.Right < mon.Right) return work.Right - pt.X <= reachX;      // right
        return mon.Bottom - pt.Y <= reachY;                                  // auto-hidden: assume bottom
    }

    void ArmAt(POINT pt, string source)
    {
        IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        long now = _clock.ElapsedMilliseconds;
        _arm = new Arm
        {
            Monitor = mon,
            MonitorName = MonitorInfo(mon).szDevice,
            Source = source,
            StartMs = now,
            ExpiresMs = now + _cfg.ArmTimeoutMs,
            FromTaskbar = source.Contains("taskbar"),
            PrevForeground = _fgAtDown,
        };
        _handled.Clear();
        _newWindows.Clear();
        _log.Info($"armed: {source} click at {pt} -> {_arm.MonitorName}");
    }

    // ------------------------------------------------------------------ windows

    void WinEventCallback(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        var arm = _arm;
        if (arm == null || !Enabled) return;

        long now = _clock.ElapsedMilliseconds;
        if (now > arm.ExpiresMs)
        {
            if (arm.Pid == null) _log.Info($"disarmed: no new window within {_cfg.ArmTimeoutMs} ms");
            _arm = null;
            return;
        }

        try
        {
            if (evt == EVENT_SYSTEM_FOREGROUND) OnForeground(hwnd, arm, now);
            else OnWindowShown(hwnd, arm, now);
        }
        catch (Exception ex) { _log.Error($"window handler: {ex}"); }
    }

    /// <summary>The click brought an already-open window to the front: it was a switch, not a launch.
    /// With MoveOpenWindows, a taskbar switch brings that window over to the clicked monitor.</summary>
    void OnForeground(IntPtr hwnd, Arm arm, long now)
    {
        if (arm.Pid != null || now - arm.StartMs > _cfg.ActivationCancelMs) return;
        if (_newWindows.Contains(hwnd)) return;
        HandleSwitch(hwnd, arm, now);
    }

    /// <summary>Hovering a thumbnail preview can already bring its window to the front, so the
    /// click itself changes nothing and no foreground event comes. Check what is in front.</summary>
    async Task ThumbnailCheckAsync(Arm arm)
    {
        foreach (int delay in new[] { 150, 250 })
        {
            await Task.Delay(delay);
            if (_arm != arm || !Enabled) return;        // handled by a foreground event, or re-armed
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || _newWindows.Contains(fg)) continue;
            if (ShellClasses.Contains(ClassOf(fg))) continue;
            _log.Info($"  thumbnail click: no switch event, using the window in front");
            HandleSwitch(fg, arm, _clock.ElapsedMilliseconds);
            return;
        }
    }

    void HandleSwitch(IntPtr hwnd, Arm arm, long now)
    {
        string exePath, cls = ClassOf(hwnd);
        // A minimized window is still parked off-screen at a tiny size when it comes to the front.
        if (IsIconic(hwnd) ? !IsCandidateMinimized(hwnd, out exePath) : !IsCandidate(hwnd, out _, out exePath, out cls)) return;
        _arm = null;
        string exe = Path.GetFileName(exePath);
        if (!_cfg.MoveOpenWindows || !arm.FromTaskbar)
        {
            _log.Info($"disarmed: click switched to already-open {exe} \"{TitleOf(hwnd)}\"");
            return;
        }
        if (!_cfg.Allows(exePath))
        {
            _log.Info($"switched to already-open {exe}: not allowed by {_cfg.Mode}, left alone");
            return;
        }
        _ = MoveOpenAsync(hwnd, arm, exe, cls, now - arm.StartMs);
    }

    async Task MoveOpenAsync(IntPtr hwnd, Arm arm, string exe, string cls, long at)
    {
        // Restoring from minimized: put it straight onto the clicked monitor so it doesn't
        // pop up on the old one first.
        if (IsIconic(hwnd) && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != arm.Monitor)
        {
            var msw = Stopwatch.StartNew();
            bool ok = WindowMover.RestoreOnto(hwnd, arm.Monitor, out string mdetail);
            var fix = ok ? WindowMover.Place(hwnd, arm.Monitor, out _) : PlaceResult.Failed;
            _log.Info($"+{at} ms  switched to minimized {exe} [{cls}] \"{TitleOf(hwnd)}\": restored here " +
                      $"{(ok ? "ok" : "FAILED")} {mdetail}, then {fix} ({msw.Elapsed.TotalMilliseconds:F1} ms)");
            if (ok) _ = RecheckAsync(hwnd, arm, exe);
            return;
        }

        // Give a restore-from-minimized (or a minimize) a moment to take effect.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(i == 0 ? 30 : 50);
            if (!IsWindow(hwnd)) return;
            // Clicking the icon of the window in front minimizes it, and Windows brings the next
            // window forward. That one wasn't clicked; leave it where it is.
            IntPtr prev = arm.PrevForeground;
            if (prev != IntPtr.Zero && prev != hwnd && IsIconic(prev))
            {
                // If the minimized window lives on another monitor, the user clicked its icon on this
                // taskbar to bring it here: restore it on the clicked monitor instead.
                if (MonitorFromWindow(prev, MONITOR_DEFAULTTONEAREST) != arm.Monitor
                    && IsCandidateMinimized(prev, out string pexe) && _cfg.Allows(pexe))
                {
                    var psw = Stopwatch.StartNew();
                    bool ok = WindowMover.RestoreOnto(prev, arm.Monitor, out string pdetail);
                    var r2 = ok ? WindowMover.Place(prev, arm.Monitor, out string fix) : PlaceResult.Failed;
                    bool front = SetForegroundWindow(prev);
                    _log.Info($"+{at} ms  click minimized open {Path.GetFileName(pexe)} \"{TitleOf(prev)}\" on another monitor: " +
                              $"restored here {(ok ? "ok" : "FAILED")} {pdetail}, then {r2}, foreground={front} ({psw.Elapsed.TotalMilliseconds:F1} ms)");
                    if (ok) _ = RecheckAsync(prev, arm, Path.GetFileName(pexe));
                    return;
                }
                _log.Info($"click minimized \"{TitleOf(prev)}\"; not moving {exe} \"{TitleOf(hwnd)}\"");
                return;
            }
            if (!IsIconic(hwnd)) break;
        }

        var sw = Stopwatch.StartNew();
        var result = WindowMover.Place(hwnd, arm.Monitor, out string detail);
        if (result == PlaceResult.AlreadyThere)
        {
            _log.Info($"disarmed: click switched to already-open {exe} \"{TitleOf(hwnd)}\" (already on {arm.MonitorName})");
            return;
        }
        _log.Info($"+{at} ms  switched to open {exe} [{cls}] \"{TitleOf(hwnd)}\": {result} {detail} ({sw.Elapsed.TotalMilliseconds:F1} ms)");
        if (result == PlaceResult.Moved) _ = RecheckAsync(hwnd, arm, exe);
    }

    void OnWindowShown(IntPtr hwnd, Arm arm, long now)
    {
        _newWindows.Add(hwnd);
        if (_handled.Contains(hwnd)) return;
        if (!IsCandidate(hwnd, out int pid, out string exePath, out string cls, out string why))
        {
            if (why is not ("not top-level" or "invisible"))
                _log.Info($"  ignored window [{cls}] \"{TitleOf(hwnd)}\" {Path.GetFileName(exePath)}: {why}");
            return;
        }
        if (arm.Pid is int claimed && claimed != pid) return;

        _handled.Add(hwnd);
        string exe = Path.GetFileName(exePath);

        if (!_cfg.Allows(exePath))
        {
            _log.Info($"skip: {exe} not allowed by {_cfg.Mode}");
            _arm = null;
            return;
        }

        arm.Pid ??= pid;
        arm.ExpiresMs = Math.Max(arm.ExpiresMs, now + _cfg.SameAppGraceMs);

        var sw = Stopwatch.StartNew();
        var result = WindowMover.Place(hwnd, arm.Monitor, out string detail);
        _log.Info($"+{now - arm.StartMs} ms  {exe} [{cls}] \"{TitleOf(hwnd)}\": {result} {detail} ({sw.Elapsed.TotalMilliseconds:F1} ms)");

        if (result is PlaceResult.Moved or PlaceResult.AlreadyThere)
            _ = RecheckAsync(hwnd, arm, exe);
    }

    /// <summary>Some apps (Chrome, Discord...) restore their saved position right after showing.
    /// Check a few times and move them back, unless the user has started interacting.</summary>
    async Task RecheckAsync(IntPtr hwnd, Arm arm, string exe)
    {
        long serial = _clickSerial;
        int elapsed = 0;
        var tgt = MonitorInfo(arm.Monitor);
        bool fullscreen = WindowMover.IsFullscreen(hwnd, tgt);
        foreach (int at in _cfg.RecheckDelaysMs)
        {
            await Task.Delay(Math.Max(0, at - elapsed));
            elapsed = at;
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return;
            if (_clickSerial != serial || (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) return;

            // A full-screen app may re-size itself after the DPI change; keep it covering the monitor.
            if (fullscreen && !IsZoomed(hwnd) && !WindowMover.IsFullscreen(hwnd, tgt)
                && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == arm.Monitor)
            {
                WindowMover.PinFullscreen(hwnd, tgt.rcMonitor);
                _log.Info($"  re-check +{at} ms: {exe} re-fitted to full screen");
                continue;
            }

            var result = WindowMover.Place(hwnd, arm.Monitor, out string detail);
            if (result == PlaceResult.Moved)
                _log.Info($"  re-check +{at} ms: {exe} moved itself away, moved back {detail}");
            else if (result == PlaceResult.Failed)
                _log.Info($"  re-check +{at} ms: {exe} move failed {detail}");
            if (WindowMover.MaximizeIfScreenSized(hwnd, arm.Monitor))
                _log.Info($"  re-check +{at} ms: {exe} was screen-sized, maximized");
            else if (WindowMover.ClampToWorkArea(hwnd, arm.Monitor))
                _log.Info($"  re-check +{at} ms: {exe} fitted inside work area");
        }
    }

    /// <summary>IsCandidate for a minimized window (its rect is tiny/off-screen while minimized).</summary>
    bool IsCandidateMinimized(IntPtr hwnd, out string exePath)
    {
        exePath = "";
        if (GetAncestor(hwnd, GA_ROOT) != hwnd || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if ((ExStyle(hwnd) & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        if (ShellClasses.Contains(ClassOf(hwnd))) return false;
        GetWindowThreadProcessId(hwnd, out uint upid);
        if ((int)upid == _ownPid) return false;
        exePath = ProcessPath(upid);
        string name = Path.GetFileName(exePath);
        if (ShellProcesses.Contains(name)) return false;
        if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && ClassOf(hwnd) != "CabinetWClass") return false;
        return exePath != "";
    }

    bool IsCandidate(IntPtr hwnd, out int pid, out string exePath, out string cls) =>
        IsCandidate(hwnd, out pid, out exePath, out cls, out _);

    bool IsCandidate(IntPtr hwnd, out int pid, out string exePath, out string cls, out string why)
    {
        pid = 0; exePath = ""; cls = ClassOf(hwnd); why = "";
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) { why = "not top-level"; return false; }
        if (!IsWindowVisible(hwnd)) { why = "invisible"; return false; }
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) { why = "owned (dialog/popup)"; return false; }
        if ((Style(hwnd) & WS_CHILD) != 0) { why = "child style"; return false; }
        if ((ExStyle(hwnd) & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) { why = $"exstyle 0x{ExStyle(hwnd):X}"; return false; }
        if (ShellClasses.Contains(cls)) { why = "shell class"; return false; }

        GetWindowRect(hwnd, out RECT r);
        if (r.Width < 150 || r.Height < 80) { why = $"too small {r}"; return false; }

        GetWindowThreadProcessId(hwnd, out uint upid);
        pid = (int)upid;
        if (pid == _ownPid) { why = "own process"; return false; }

        exePath = ProcessPath(upid);
        string name = Path.GetFileName(exePath);
        if (ShellProcesses.Contains(name)) { why = "shell process"; return false; }
        // explorer.exe owns lots of shell UI; only its File Explorer windows are "apps".
        if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && cls != "CabinetWClass") { why = "explorer shell UI"; return false; }
        return true;
    }

    /// <summary>Test hook: behave as if the user clicked a taskbar at this point.</summary>
    public void TestArm(POINT pt) => ArmAt(pt, "test");

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        foreach (var h in new[] { _showHook, _uncloakHook, _foregroundHook })
            if (h != IntPtr.Zero) UnhookWinEvent(h);
    }
}
