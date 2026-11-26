using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using static LaunchHere.Native;

namespace LaunchHere;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var log = new Logger();

        if (args.Contains("--selftest")) return SelfTest.Run(log);
#if TEST_HOOKS
        if (args.Length >= 4 && args[0] == "--arm-test") return SelfTest.ArmTest(log, args);
#endif
        if (args.Contains("--startup-test")) return SelfTest.StartupTest(log);
        if (args.Length >= 2 && args[0] == "--settings-screenshot") return SelfTest.SettingsScreenshot(log, args[1]);

        // After an update the old version is still shutting down: wait for it to let go.
        bool afterUpdate = args.Contains("--after-update");
        if (!TakeInstanceLock(afterUpdate ? TimeSpan.FromSeconds(15) : TimeSpan.Zero)) return 0;

#if TEST_HOOKS
        bool updateNow = args.Contains("--update-now");   // headless update, for the updater end-to-end test
        bool settingsCycle = args.Contains("--settings-cycle");   // open Settings, close it (memory measurements)
#else
        const bool updateNow = false, settingsCycle = false;
#endif
        Application.Run(new TrayContext(log, afterUpdate, updateNow, settingsCycle));
        ReleaseInstanceLock();
        return 0;
    }

    /// <summary>Prefix for named objects (single-instance mutex, update event). The environment variable
    /// LAUNCHHERE_TEST_INSTANCE gives a test copy its own names, so it can run next to the real one.</summary>
    public static string InstanceName =>
        @"Local\LaunchHere" + (Environment.GetEnvironmentVariable("LAUNCHHERE_TEST_INSTANCE") is { Length: > 0 } t ? "." + t : "");

    static Mutex? _instanceLock;

    /// <summary>Only one copy runs at a time. Same name as 0.1.x, so old and new versions see each other.</summary>
    public static bool TakeInstanceLock(TimeSpan wait)
    {
        _instanceLock ??= new Mutex(false, InstanceName + ".SingleInstance");
        try { return _instanceLock.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }   // previous owner died without releasing it
    }

    /// <summary>Give up the lock (before starting an updated copy). Must run on the thread that took it.</summary>
    public static void ReleaseInstanceLock()
    {
        try { _instanceLock?.ReleaseMutex(); } catch { }
    }
}

/// <summary>Tray icon + menu; owns the Tracker.</summary>
internal sealed class TrayContext : ApplicationContext
{
    readonly Logger _log;
    readonly Tracker _tracker;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _enabledItem;
    Config _cfg;

    public TrayContext(Logger log, bool afterUpdate = false, bool updateNow = false, bool settingsCycle = false)
    {
        _log = log;
        _cfg = Config.Load(log);
        _log.Verbose = _cfg.VerboseLog;
        WindowMover.VerticalShrinkShare = _cfg.VerticalShrinkShare;
        _log.Always($"started {Updater.CurrentVersion}; mode={_cfg.Mode}, apps=[{string.Join(", ", _cfg.Apps)}]");
        foreach (var s in Screen.AllScreens)
            _log.Always($"  monitor {s.DeviceName} {s.Bounds} primary={s.Primary}");

        _tracker = new Tracker(_cfg, log);

        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => SetEnabled(!_tracker.Enabled)) { Checked = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        var settingsItem = new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings());
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
        menu.Items.Add(settingsItem);
        menu.Items.Add("Reload config.json", null, (_, _) => Reload());
        menu.Items.Add("Open log", null, (_, _) => Open(Logger.FilePath));
        menu.Items.Add("Report a problem...", null, (_, _) => ShowReport());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(true),
            Text = "LaunchHere - opens apps on the monitor you clicked",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) SetEnabled(!_tracker.Enabled); };

        if (afterUpdate) Updater.ConfirmStarted(_log);
        if (updateNow) StartHeadlessUpdate();
        if (settingsCycle) StartSettingsCycle();
    }

    /// <summary>`--settings-cycle` (test builds): open Settings after 3 s and close it 5 s later.</summary>
    void StartSettingsCycle()
    {
        int step = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) =>
        {
            if (step++ == 0) { ShowSettings(); timer.Interval = 5000; }
            else { _settings?.Close(); timer.Dispose(); }
        };
        timer.Start();
    }

    /// <summary>`--update-now` (testing): check, download and install without the settings window.</summary>
    void StartHeadlessUpdate()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 300 };
        timer.Tick += async (_, _) =>
        {
            timer.Dispose();
            try
            {
                var info = await Updater.CheckAsync();
                _log.Info($"update-now: {(info == null ? "no update" : "found " + info.Version)}");
                if (info == null) { ExitThread(); return; }
                string file = await Updater.DownloadAsync(info, null);
                string? err = await InstallUpdateAsync(file);
                if (err != null) { _log.Error($"update-now: {err}"); ExitThread(); }
            }
            catch (Exception ex) { _log.Error($"update-now: {ex.Message}"); ExitThread(); }
        };
        timer.Start();
    }

    /// <summary>Swap in a downloaded, verified update and restart into it. On success this process
    /// exits; on failure it carries on as before and the error is returned.</summary>
    public async Task<string?> InstallUpdateAsync(string downloaded)
    {
        _log.Always("update: installing");
        _settings?.Close();
        _report?.Close();
        _tracker.Enabled = false;
        _tray.Visible = false;
        Program.ReleaseInstanceLock();

        string? err = await Task.Run(() => Updater.SwapAndStart(downloaded, _log));
        if (err == null)
        {
            ExitThread();
            return null;
        }
        _log.Error($"update: {err}");
        Program.TakeInstanceLock(TimeSpan.FromSeconds(5));
        _tray.Visible = true;
        _tracker.Enabled = _enabledItem.Checked;
        return err;
    }

    void SetEnabled(bool on)
    {
        _tracker.Enabled = on;
        _enabledItem.Checked = on;
        _tray.Icon = MakeIcon(on);
        _tray.Text = on ? "LaunchHere - on" : "LaunchHere - paused";
        _log.Always(on ? "enabled" : "paused");
    }

    void Reload()
    {
        Use(Config.Load(_log));
        _log.Always($"settings reloaded; mode={_cfg.Mode}, apps=[{string.Join(", ", _cfg.Apps)}]");
    }

    void Use(Config cfg)
    {
        _cfg = cfg;
        _log.Verbose = _cfg.VerboseLog;
        WindowMover.VerticalShrinkShare = _cfg.VerticalShrinkShare;
        _tracker.UpdateConfig(_cfg);
    }

    SettingsWindow? _settings;

    void ShowSettings()
    {
        if (_settings != null)
        {
            if (_settings.WindowState == System.Windows.WindowState.Minimized) _settings.WindowState = System.Windows.WindowState.Normal;
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_cfg, _log, cfg =>
        {
            cfg.Save(_log);
            Use(cfg);
            _log.Always($"settings saved; mode={_cfg.Mode}, apps=[{string.Join(", ", _cfg.Apps)}]");
        }, InstallUpdateAsync, ShowReport);
        _settings.Closed += (_, _) => { _settings = null; Wpf.TrimWhenAllClosed(); };
        _settings.Show();
        _settings.Activate();
    }

    ReportWindow? _report;

    void ShowReport()
    {
        if (_report != null)
        {
            if (_report.WindowState == System.Windows.WindowState.Minimized) _report.WindowState = System.Windows.WindowState.Normal;
            _report.Activate();
            return;
        }
        _report = new ReportWindow(() => _cfg, _log, EnableVerboseLog);
        _report.Closed += (_, _) => { _report = null; Wpf.TrimWhenAllClosed(); };
        _report.Show();
        _report.Activate();
    }

    /// <summary>"Turn on verbose logging" in the report window: save it straight away.</summary>
    void EnableVerboseLog()
    {
        var c = _cfg.Clone();
        c.VerboseLog = true;
        c.Save(_log);
        Use(c);
        _log.Always("verbose logging turned on (Report a problem)");
        _settings?.ShowVerboseOn();
    }

    static void Open(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Two little monitors with an arrow; grey when paused.</summary>
    internal static Icon MakeIcon(bool on)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = on ? Color.FromArgb(0, 120, 215) : Color.Gray;
            using var pen = new Pen(color, 2.5f);
            using var brush = new SolidBrush(color);
            g.DrawRectangle(pen, 1, 6, 12, 10);
            g.DrawRectangle(pen, 18, 6, 12, 10);
            g.FillRectangle(brush, 5, 18, 4, 2);
            g.FillRectangle(brush, 22, 18, 4, 2);
            g.FillPolygon(brush, new[] { new Point(9, 24), new Point(20, 24), new Point(20, 21), new Point(26, 26), new Point(20, 31), new Point(20, 28), new Point(9, 28) });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _tracker.Dispose();
        _log.Always("exited");
        base.ExitThreadCore();
    }
}

/// <summary>`LaunchHere.exe --selftest`: moves a test window between monitors (normal and
/// maximized) and writes the results to the log. Exit code 0 = all passed.</summary>
internal static class SelfTest
{
#if TEST_HOOKS
    /// <summary>`--arm-test X Y program [args...]` (test builds only): pretend a taskbar click happened at
    /// X,Y, launch the program, trace what happens for 10 s, then exit.</summary>
    public static int ArmTest(Logger log, string[] args)
    {
        log.Verbose = true;
        var cfg = Config.Load(log);
        WindowMover.VerticalShrinkShare = cfg.VerticalShrinkShare;
        using var tracker = new Tracker(cfg, log);
        tracker.TestArm(new POINT { X = int.Parse(args[1]), Y = int.Parse(args[2]) });
        var psi = new ProcessStartInfo(args[3]) { UseShellExecute = true };
        foreach (var a in args.Skip(4)) psi.ArgumentList.Add(a);
        if (psi.ArgumentList.Count > 0) psi.UseShellExecute = false;
        log.Info($"arm-test: launching {args[3]} {string.Join(' ', args.Skip(4))}");
        Process.Start(psi);
        var timer = new System.Windows.Forms.Timer { Interval = 10000 };
        timer.Tick += (_, _) => Application.ExitThread();
        timer.Start();
        Application.Run();
        log.Info("arm-test: done");
        return 0;
    }
#endif

    /// <summary>`--startup-test`: turn "Start with Windows" on and off, check each, then restore
    /// whatever was there before. Exit code 0 = passed.</summary>
    public static int StartupTest(Logger log)
    {
        bool before = Startup.IsEnabled;
        Startup.Set(true);
        bool on = Startup.IsEnabled;
        Startup.Set(false);
        bool off = !Startup.IsEnabled;
        Startup.Set(before);
        bool restored = Startup.IsEnabled == before;
        bool ok = on && off && restored;
        log.Info($"startup-test: {(ok ? "PASS" : "FAIL")} on={on} off={off} restored({before})={restored}");
        return ok ? 0 : 1;
    }

    /// <summary>`--settings-screenshot DIR`: render the settings window and the running-apps picker
    /// to PNGs (layout check; changes nothing).</summary>
    public static int SettingsScreenshot(Logger log, string dir)
    {
        Directory.CreateDirectory(dir);
        void Pump(int ms)
        {
            for (int i = 0; i < ms / 15; i++) { Application.DoEvents(); Thread.Sleep(15); }
        }
        void Shot(System.Windows.Window w, string name, Action<System.Windows.Window>? setup = null)
        {
            w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            w.Left = 60; w.Top = 60;
            w.Topmost = true;
            w.Show();
            setup?.Invoke(w);
            Pump(700);
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            GetWindowRect(h, out RECT r);
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
            bmp.Save(Path.Combine(dir, name));
            log.Info($"settings screenshot {name}: {r}");
            w.Close();
            Pump(100);
        }
        var cfg = Config.Load(log);
        cfg.Apps = new() { "chrome.exe", @"C:\Windows\notepad.exe", "Discord.exe" };
        Func<string, Task<string?>> noUpdate = _ => Task.FromResult<string?>(null);
        Action noReport = () => { };
        Shot(new SettingsWindow(cfg, log, _ => { }, noUpdate, noReport), "settings-light.png", w => w.ThemeMode = System.Windows.ThemeMode.Light);
        Shot(new SettingsWindow(cfg, log, _ => { }, noUpdate, noReport), "settings-dark.png", w => w.ThemeMode = System.Windows.ThemeMode.Dark);
        Shot(new SettingsWindow(cfg, log, _ => { }, noUpdate, noReport), "settings-advanced.png", w =>
        {
            var s = (SettingsWindow)w;
            s.AdvancedExpander.IsExpanded = true;
            Pump(200);
            s.ContentScroller.ScrollToEnd();
        });
        Shot(new RunningAppsWindow(cfg.Apps), "running-apps.png");
        var quiet = cfg.Clone();
        quiet.VerboseLog = false;
        Shot(new ReportWindow(() => quiet, log, noReport), "report.png");
        Shot(new ReportWindow(() => quiet, log, noReport), "report-dark.png", w => w.ThemeMode = System.Windows.ThemeMode.Dark);
        return 0;
    }

    public static int Run(Logger log)
    {
        log.Verbose = true;
        var screens = Screen.AllScreens;
        if (screens.Length < 2) { log.Error("selftest needs 2+ monitors"); return 2; }

        var form = new Form { Text = "LaunchHere selftest", StartPosition = FormStartPosition.Manual, Width = 900, Height = 600 };
        form.Location = new Point(screens[0].WorkingArea.Left + 100, screens[0].WorkingArea.Top + 100);
        form.Show();
        Application.DoEvents();
        IntPtr h = form.Handle;
        int failures = 0;

        // Put the test window at an exact physical rect (twice: the first call may trigger a DPI rescale).
        void SetRect(int x, int y, int w, int hh)
        {
            for (int i = 0; i < 2; i++)
            {
                SetWindowPos(h, IntPtr.Zero, x, y, w, hh, SWP_NOZORDER | SWP_NOACTIVATE);
                Application.DoEvents();
            }
        }

        void Step(string name, Screen target, bool maximize)
        {
            if (maximize) { form.WindowState = FormWindowState.Maximized; Application.DoEvents(); }
            var mon = MonitorFromPoint(new POINT { X = target.Bounds.Left + 10, Y = target.Bounds.Top + 10 }, MONITOR_DEFAULTTONEAREST);
            var sw = Stopwatch.StartNew();
            var result = WindowMover.Place(h, mon, out string detail);
            Application.DoEvents();
            WindowMover.ClampToWorkArea(h, mon);
            Application.DoEvents();
            bool onTarget = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == mon;
            bool stillMax = !maximize || IsZoomed(h);
            GetWindowRect(h, out RECT r);
            bool ok = result == PlaceResult.Moved && onTarget && stillMax;
            if (!ok) failures++;
            log.Info($"selftest {name}: {(ok ? "PASS" : "FAIL")} result={result} {detail} rect={r} zoomed={IsZoomed(h)} ({sw.Elapsed.TotalMilliseconds:F1} ms)");
        }

        Step("normal  0->1", screens[1], false);
        Step("normal  1->0", screens[0], false);
        // Wide window that won't fit the narrow monitor: width should fit exactly, height shrink a little.
        foreach (var (ww, wh) in new[] { (2090, 1476), (3000, 1600) })
        {
            SetRect(screens[0].WorkingArea.Left + 200, screens[0].WorkingArea.Top + 150, ww, wh);
            Step($"wide {ww}x{wh} 0->1", screens[1], false);
            GetWindowRect(h, out RECT wr);
            var twa = screens[1].WorkingArea;
            bool inside = wr.Left >= twa.Left && wr.Right <= twa.Right && wr.Top >= twa.Top && wr.Bottom <= twa.Bottom;
            if (!inside) failures++;
            log.Info($"selftest wide {ww}x{wh} inside {twa}: {(inside ? "PASS" : "FAIL")} rect={wr}");
        }
        // Edge-style: a normal (not maximized) window covering the whole work area.
        var w0 = screens[0].WorkingArea;
        SetRect(w0.Left, w0.Top, w0.Width, w0.Height);
        Step("full-size normal 0->1", screens[1], false);
        GetWindowRect(h, out RECT fr);
        bool fullMax = IsZoomed(h);
        if (!fullMax) failures++;
        log.Info($"selftest full-size normal ends up maximized: {(fullMax ? "PASS" : "FAIL")} rect={fr}");
        // ...and restore-down gives a sensible size, not a screen-sized one.
        form.WindowState = FormWindowState.Normal; Application.DoEvents();
        GetWindowRect(h, out RECT rd0);
        var wa = screens[1].WorkingArea;
        bool smaller = rd0.Width < wa.Width * 0.9 && rd0.Height < wa.Height * 0.9;
        if (!smaller) failures++;
        log.Info($"selftest full-size restore-down is smaller: {(smaller ? "PASS" : "FAIL")} rect={rd0}");
        // Same window, minimized on monitor 0 while screen-sized, restored onto monitor 1: maximized.
        SetRect(w0.Left, w0.Top, w0.Width, w0.Height);
        ShowWindow(h, SW_MINIMIZE); Application.DoEvents();
        var m1 = MonitorFromPoint(new POINT { X = screens[1].Bounds.Left + 10, Y = screens[1].Bounds.Top + 10 }, MONITOR_DEFAULTTONEAREST);
        WindowMover.RestoreOnto(h, m1, out _); Application.DoEvents();
        bool minFullOk = IsZoomed(h) && MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == m1;
        if (!minFullOk) failures++;
        log.Info($"selftest minimized full-size 0->1 restores maximized: {(minFullOk ? "PASS" : "FAIL")} zoomed={IsZoomed(h)}");
        form.WindowState = FormWindowState.Normal; Application.DoEvents();
        SetRect(w0.Left + 100, w0.Top + 100, 900, 600);
        Step("maximized 0->1", screens[1], true);
        Step("maximized 1->0", screens[0], true);

        IntPtr MonOf(Screen s) => MonitorFromPoint(new POINT { X = s.Bounds.Left + 10, Y = s.Bounds.Top + 10 }, MONITOR_DEFAULTTONEAREST);

        // Minimized on one monitor, restored straight onto the other (taskbar click on the other
        // monitor's taskbar). Normal and maximized.
        foreach (bool max in new[] { false, true })
        {
            form.WindowState = FormWindowState.Normal; Application.DoEvents();
            SetRect(w0.Left + 100, w0.Top + 100, 900, 600);
            if (max) { form.WindowState = FormWindowState.Maximized; Application.DoEvents(); }
            ShowWindow(h, SW_MINIMIZE); Application.DoEvents();
            var mon1 = MonOf(screens[1]);
            bool restored = WindowMover.RestoreOnto(h, mon1, out string rd);
            Application.DoEvents();
            WindowMover.Place(h, mon1, out _);
            Application.DoEvents();
            GetWindowRect(h, out RECT rr);
            // Same on-screen size as a normal move: 900x600 at 200% -> about 562x375 at 125%.
            bool sized = max || (rr.Width > 500 && rr.Height > 300);
            bool ok = restored && sized && !IsIconic(h) && MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == mon1 && IsZoomed(h) == max;
            if (!ok) failures++;
            log.Info($"selftest minimized{(max ? " maximized" : "")} 0->1 restore: {(ok ? "PASS" : "FAIL")} {rd} rect={rr} zoomed={IsZoomed(h)}");
        }
        form.Close();

        // Full screen (borderless, covering the whole monitor, like F11 in a browser).
        var fs = new Form { Text = "LaunchHere selftest full screen", FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual };
        fs.Show();
        Application.DoEvents();
        IntPtr fh = fs.Handle;
        var b0 = screens[0].Bounds;
        for (int i = 0; i < 2; i++) { SetWindowPos(fh, IntPtr.Zero, b0.Left, b0.Top, b0.Width, b0.Height, SWP_NOZORDER | SWP_NOACTIVATE); Application.DoEvents(); }
        foreach (var (name, target) in new[] { ("0->1", screens[1]), ("1->0", screens[0]) })
        {
            var mon = MonOf(target);
            var result = WindowMover.Place(fh, mon, out string detail);
            Application.DoEvents();
            bool clamped = WindowMover.ClampToWorkArea(fh, mon);   // must leave full screen alone
            GetWindowRect(fh, out RECT r);
            var tb = target.Bounds;
            bool ok = result == PlaceResult.Moved && !clamped &&
                      Math.Abs(r.Left - tb.Left) <= 1 && Math.Abs(r.Top - tb.Top) <= 1 &&
                      Math.Abs(r.Width - tb.Width) <= 1 && Math.Abs(r.Height - tb.Height) <= 1;
            if (!ok) failures++;
            log.Info($"selftest full screen {name}: {(ok ? "PASS" : "FAIL")} result={result} {detail} rect={r} clamped={clamped}");
        }
        fs.Close();
        log.Info($"selftest done: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
