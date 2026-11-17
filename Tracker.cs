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
        public int? Pid;               // set once the launched app's first window is claimed
    }

    static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging", "MultitaskingViewFrame", "#32768",
    };

    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
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
        if (msg == WM_LBUTTONDOWN) _clickSerial++;

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

    /// <summary>The click brought an already-open window to the front: it was a switch, not a launch.</summary>
    void OnForeground(IntPtr hwnd, Arm arm, long now)
    {
        if (arm.Pid != null || now - arm.StartMs > _cfg.ActivationCancelMs) return;
        if (_newWindows.Contains(hwnd)) return;
        if (!IsCandidate(hwnd, out _, out string exePath, out _)) return;
        _log.Info($"disarmed: click switched to already-open {Path.GetFileName(exePath)} \"{TitleOf(hwnd)}\"");
        _arm = null;
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
        foreach (int at in _cfg.RecheckDelaysMs)
        {
            await Task.Delay(Math.Max(0, at - elapsed));
            elapsed = at;
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return;
            if (_clickSerial != serial || (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) return;

            var result = WindowMover.Place(hwnd, arm.Monitor, out string detail);
            if (result == PlaceResult.Moved)
                _log.Info($"  re-check +{at} ms: {exe} moved itself away, moved back {detail}");
            else if (result == PlaceResult.Failed)
                _log.Info($"  re-check +{at} ms: {exe} move failed {detail}");
            if (WindowMover.ClampToWorkArea(hwnd, arm.Monitor))
                _log.Info($"  re-check +{at} ms: {exe} fitted inside work area");
        }
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
