using System.Diagnostics;
using static LaunchHere.Native;

namespace LaunchHere;

/// <summary>Apps that have a normal top-level window open.</summary>
internal static class RunningApps
{
    public static List<(string Name, string Path, string Title)> List()
    {
        var found = new Dictionary<string, (string Name, string Path, List<string> Titles)>(StringComparer.OrdinalIgnoreCase);
        int own = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((ExStyle(h) & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0 || IsCloaked(h)) return true;
            string title = TitleOf(h);
            if (title.Length == 0) return true;
            string cls = ClassOf(h);
            if (Tracker.ShellClasses.Contains(cls)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if ((int)pid == own) return true;
            string path = ProcessPath(pid);
            string name = path != "" ? System.IO.Path.GetFileName(path) : NameOf(pid);
            if (name == "" || Tracker.ShellProcesses.Contains(name)) return true;
            if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && cls != "CabinetWClass") return true;
            if (!found.TryGetValue(name, out var e)) found[name] = e = (name, path, new List<string>());
            e.Titles.Add(title);
            return true;
        }, IntPtr.Zero);

        return found.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => (a.Name, a.Path, Describe(a.Name, a.Path, a.Titles)))
            .ToList();
    }

    static string Describe(string name, string path, List<string> titles)
    {
        string t = titles[0] + (titles.Count > 1 ? $" (+{titles.Count - 1} more)" : "");
        if (name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return $"Store apps: {string.Join(", ", titles)} - they all share this entry";
        if (path == "") t += " (running as admin)";
        return t;
    }

    static string NameOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName + ".exe"; }
        catch { return ""; }
    }

    /// <summary>Path of a running copy of this exe name, for its icon ("" if none is running).</summary>
    public static string FindPath(string exeName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exeName)))
            {
                using (p)
                {
                    string path = ProcessPath((uint)p.Id);
                    if (path != "") return path;
                }
            }
        }
        catch { }
        return "";
    }
}
