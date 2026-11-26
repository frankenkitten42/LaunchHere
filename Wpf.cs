using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LaunchHere;

/// <summary>WPF plumbing for the settings windows. The app's message loop is WinForms (the tray icon);
/// WPF windows run on it fine once a WPF Application object exists (for resources and the theme).</summary>
internal static class Wpf
{
    public static void EnsureInitialized()
    {
        if (System.Windows.Application.Current != null) return;
        // Draw on the CPU. The settings windows are simple, and GPU rendering makes WPF create a Direct3D
        // device that keeps ~40 MB of video memory for as long as the app runs, even after they close.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // Windows 11 Fluent look; follows the system light/dark setting and accent colour.
        new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            ThemeMode = ThemeMode.System,
        };
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    static System.Windows.Forms.Timer? _trimTimer;

    /// <summary>Call when a top-level window closes. Once none are left open, give the memory the windows
    /// used back to Windows (the tray app itself needs little). Waits 2 s, so reopening is not slowed.</summary>
    public static void TrimWhenAllClosed()
    {
        _trimTimer?.Dispose();
        _trimTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer?.Dispose();
            _trimTimer = null;
            if (System.Windows.Application.Current?.Windows.Count > 0) return;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        };
        _trimTimer.Start();
    }

    /// <summary>Keyboard input (Tab, typing) for a WPF window shown from the WinForms message loop.</summary>
    public static void EnableKeyboard(Window w) =>
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(w);

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon an exe shows in Explorer, as a WPF image (null if it can't be read).</summary>
    public static ImageSource? IconOf(string exePath)
    {
        if (exePath == "") return null;
        if (IconCache.TryGetValue(exePath, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (ico != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
            }
        }
        catch { }
        IconCache[exePath] = img;
        return img;
    }

    public static ImageSource ToImageSource(System.Drawing.Icon icon)
    {
        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
