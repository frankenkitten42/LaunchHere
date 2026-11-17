namespace LaunchHere;

/// <summary>Plain-text log next to the exe. Info lines only when DebugLog is on; errors always.</summary>
public sealed class Logger
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "launch-here.log");

    readonly object _lock = new();
    public bool Debug { get; set; } = true;

    public Logger()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
        }
        catch { /* not fatal */ }
    }

    public void Info(string msg) { if (Debug) Write(msg); }
    public void Error(string msg) => Write("ERROR " + msg);

    void Write(string msg)
    {
        lock (_lock)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
            catch { /* logging must never crash the app */ }
        }
    }
}
