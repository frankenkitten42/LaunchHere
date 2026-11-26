namespace LaunchHere;

/// <summary>Plain-text log next to the exe. Info lines only when verbose logging is on; Always lines
/// (start, settings, updates, exit) and errors are written every time.</summary>
public sealed class Logger
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "launch-here.log");

    readonly object _lock = new();
    public bool Verbose { get; set; }

    public Logger()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
        }
        catch { /* not fatal */ }
    }

    public void Info(string msg) { if (Verbose) Write(msg); }
    public void Always(string msg) => Write(msg);
    public void Error(string msg) => Write("ERROR " + msg);

    void Write(string msg)
    {
        lock (_lock)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
            catch { /* logging must never crash the app */ }
        }
    }

    /// <summary>The last <paramref name="count"/> lines of the log (empty if there is none).</summary>
    public string[] Tail(int count)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return Array.Empty<string>();
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var q = new Queue<string>(count + 1);
                while (sr.ReadLine() is { } line)
                {
                    q.Enqueue(line);
                    if (q.Count > count) q.Dequeue();
                }
                return q.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }
    }
}
