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
        // Windows 11 Fluent look; follows the system light/dark setting and accent colour.
        new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            ThemeMode = ThemeMode.System,
        };
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
