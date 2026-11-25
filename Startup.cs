using System.Windows.Forms;
using Microsoft.Win32;

namespace LaunchHere;

/// <summary>"Start with Windows": a per-user Run-key entry pointing at this exe (no admin needed).</summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Settings > Apps > Startup / Task Manager record that the user switched an entry off.
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "LaunchHere";

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    static string Command => $"\"{ExePath}\"";

    /// <summary>True when Windows will actually start this exe at sign-in: the Run entry exists,
    /// points at this exe (not an old copy elsewhere), and hasn't been disabled in Task Manager.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string cmd) return false;
            if (!string.Equals(cmd.Trim().Trim('"'), ExePath, StringComparison.OrdinalIgnoreCase)) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // First byte: 0x02 (or even) = enabled, 0x03 (odd) = disabled by the user.
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
    }

    public static void Set(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) run.SetValue(ValueName, Command);
        else run.DeleteValue(ValueName, throwOnMissingValue: false);
        // Clear any "disabled" mark so turning it on here really turns it on.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
