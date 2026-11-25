using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaunchHere;

public enum ListMode { Blacklist, Whitelist }

/// <summary>User settings, stored as config.json next to the exe.</summary>
public sealed class Config
{
    /// <summary>Blacklist: move every app except those in Apps. Whitelist: move only apps in Apps.</summary>
    public ListMode Mode { get; set; } = ListMode.Blacklist;

    /// <summary>Exe names ("chrome.exe") or full paths ("C:\\Games\\foo.exe").</summary>
    public List<string> Apps { get; set; } = new();

    public bool WatchTaskbar { get; set; } = true;
    public bool WatchDesktop { get; set; } = true;

    /// <summary>How long after a click we wait for the app's first window (covers slow cold starts).</summary>
    public int ArmTimeoutMs { get; set; } = 8000;

    /// <summary>After the first window is moved, further new windows of the same process
    /// (splash screen -> main window) are moved too, for this long.</summary>
    public int SameAppGraceMs { get; set; } = 3000;

    /// <summary>If, within this long after the click, an already-open window comes to the front,
    /// the click just switched to a running app - stop waiting for a new window.</summary>
    public int ActivationCancelMs { get; set; } = 1500;

    /// <summary>Clicking a taskbar icon (or its thumbnail) of a window that is open on another monitor
    /// moves that window to the clicked taskbar's monitor, keeping it maximized / full screen.</summary>
    public bool MoveOpenWindows { get; set; } = true;

    /// <summary>Times (ms after the first move) to re-check apps that restore their own position.</summary>
    public List<int> RecheckDelaysMs { get; set; } = new() { 50, 150, 400, 1000, 2000 };

    /// <summary>When a window is too wide for the target monitor its width is shrunk to fit;
    /// this share of that shrink is applied to the height too (0 = keep height, 1 = proportional).</summary>
    public double VerticalShrinkShare { get; set; } = 0.2;

    public bool DebugLog { get; set; } = true;

    public bool Allows(string exePath)
    {
        string name = Path.GetFileName(exePath);
        bool listed = Apps.Any(a =>
            a.Contains('\\') || a.Contains('/')
                ? string.Equals(Path.GetFullPath(a), exePath, StringComparison.OrdinalIgnoreCase)
                : string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return Mode == ListMode.Whitelist ? listed : !listed;
    }

    // ---- persistence ----
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Deep copy, for editing in the settings window without touching the live config.</summary>
    public Config Clone() => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this, Json), Json)!;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static Config Load(Logger log)
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Json) ?? new Config();
        }
        catch (Exception ex)
        {
            log.Error($"config.json unreadable, using defaults: {ex.Message}");
            return new Config();
        }
        var cfg = new Config();
        cfg.Save(log);
        return cfg;
    }

    public void Save(Logger log)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { log.Error($"could not write config.json: {ex.Message}"); }
    }
}
