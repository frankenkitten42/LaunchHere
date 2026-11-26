using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LaunchHere;

/// <summary>Builds the text for "Report a problem": version, Windows, monitors, settings and the end of
/// the log. Nothing here sends anything; the report window shows it for the user to check and edit,
/// then opens a pre-filled GitHub issue in the browser.</summary>
internal static class ProblemReport
{
    public const string NewIssueUrl = "https://github.com/frankenkitten42/LaunchHere/issues/new";
    public const int LogLines = 50;

    /// <summary>Longest URL we hand to the browser. GitHub fails above about 6500 characters when the user is
    /// signed out (the login redirect carries the URL again), and with HTTP 414 above about 8000.</summary>
    public const int MaxUrlLength = 6000;

    public static string Details(Config cfg, Logger log, bool hideTitles)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LaunchHere {Updater.CurrentVersion}");
        sb.AppendLine(WindowsVersion());
        sb.AppendLine($"Running as administrator: {(Environment.IsPrivilegedProcess ? "yes" : "no")}");
        sb.AppendLine($"Taskbar setting \"Show my taskbar apps on\": {TaskbarAppsSetting()}");
        sb.AppendLine("Monitors:");
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            var b = s.Bounds;
            var hmon = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST);
            uint dpi = Native.MonitorDpi(hmon);
            sb.AppendLine($"  {s.DeviceName}{(s.Primary ? " (primary)" : "")}: {b.Width}x{b.Height} at {b.Left},{b.Top}, " +
                          $"scale {dpi * 100 / 96}%, taskbar {TaskbarEdge(s)}");
        }
        sb.AppendLine($"Settings: {cfg.Mode} with {cfg.Apps.Count} app(s) listed, taskbar {OnOff(cfg.WatchTaskbar)}, " +
                      $"desktop {OnOff(cfg.WatchDesktop)}, bring open windows {OnOff(cfg.MoveOpenWindows)},\n" +
                      $"  start with Windows {OnOff(Startup.IsEnabled)}, verbose log {OnOff(cfg.VerboseLog)}");
        sb.AppendLine($"Timings: wait {cfg.ArmTimeoutMs} ms, next windows {cfg.SameAppGraceMs} ms, " +
                      $"switch {cfg.ActivationCancelMs} ms, height shrink {cfg.VerticalShrinkShare:0.##}");
        sb.AppendLine();

        var lines = log.Tail(LogLines);
        sb.AppendLine(lines.Length == 0 ? "(launch-here.log is empty)" : $"Last {lines.Length} lines of launch-here.log:");
        foreach (var line in lines) sb.AppendLine(Clean(line, hideTitles));
        return sb.ToString().TrimEnd();
    }

    /// <summary>The issue body in GitHub Markdown.</summary>
    public static string Body(string whatHappened, string details)
    {
        static string N(string s) => s.Trim().Replace("\r\n", "\n");
        return $"## What happened\n\n{(whatHappened.Trim().Length > 0 ? N(whatHappened) : "_(not filled in)_")}\n\n" +
               $"## Details\n\n````text\n{N(details)}\n````\n";
    }

    public static string IssueUrl(string title, string body) =>
        $"{NewIssueUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    // Window titles are logged in double quotes ("Inbox - Outlook").
    static readonly Regex Quoted = new("\"[^\"\\r\\n]*\"", RegexOptions.Compiled);

    /// <summary>Takes the user's profile folder (which contains their Windows user name) out of a log
    /// line, and optionally the window titles.</summary>
    static string Clean(string line, bool hideTitles)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) line = line.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName;
        if (user.Length > 2) line = Regex.Replace(line, @"\\" + Regex.Escape(user) + @"(?=\\|\b)", @"\%USERNAME%", RegexOptions.IgnoreCase);
        if (hideTitles) line = Quoted.Replace(line, m => m.Length > 2 ? "\"(title hidden)\"" : m.Value);
        return line;
    }

    static string OnOff(bool b) => b ? "on" : "off";

    static string TaskbarEdge(System.Windows.Forms.Screen s)
    {
        var b = s.Bounds;
        var w = s.WorkingArea;
        if (w.Bottom < b.Bottom) return "bottom";
        if (w.Top > b.Top) return "top";
        if (w.Left > b.Left) return "left";
        if (w.Right < b.Right) return "right";
        return "none or auto-hidden";
    }

    static string WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string name = k?.GetValue("ProductName") as string ?? "Windows";
            string build = k?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            // Windows 11 still says "Windows 10" in ProductName.
            if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            string display = k?.GetValue("DisplayVersion") as string ?? "";
            string ubr = k?.GetValue("UBR") is int u ? "." + u : "";
            return $"{name}{(display.Length > 0 ? " " + display : "")} (build {build}{ubr}), {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    /// <summary>Settings > Personalization > Taskbar > Taskbar behaviors > "Show my taskbar apps on".</summary>
    static string TaskbarAppsSetting()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            if (k?.GetValue("MMTaskbarEnabled") is int enabled && enabled == 0) return "taskbar only on the main display";
            return (k?.GetValue("MMTaskbarMode") as int? ?? 0) switch
            {
                0 => "All taskbars",
                1 => "Main taskbar and taskbar where window is open",
                2 => "Taskbar where window is open",
                int m => $"unknown ({m})",
            };
        }
        catch { return "unknown"; }
    }
}
