using System.Runtime.InteropServices;
using static LaunchHere.Native;

namespace LaunchHere;

public enum PlaceResult { AlreadyThere, Moved, Minimized, Failed }

/// <summary>Moves a top-level window to another monitor, keeping its relative position,
/// and keeping it maximized if it was.</summary>
internal static class WindowMover
{
    public static PlaceResult Place(IntPtr hwnd, IntPtr targetMon, out string detail)
    {
        detail = "";
        IntPtr curMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (curMon == targetMon) return PlaceResult.AlreadyThere;
        if (IsIconic(hwnd)) return PlaceResult.Minimized;

        var src = MonitorInfo(curMon);
        var tgt = MonitorInfo(targetMon);
        double scale = MonitorDpi(targetMon) / (double)MonitorDpi(curMon);

        if (IsFullscreen(hwnd, src))
        {
            // Full screen (F11 in a browser, borderless games/videos): cover the whole target monitor.
            if (!PinFullscreen(hwnd, tgt.rcMonitor))
            {
                detail = $"SetWindowPos error {Marshal.GetLastWin32Error()} (elevated app? run LaunchHere as admin)";
                return PlaceResult.Failed;
            }
            detail = $"full screen {tgt.rcMonitor}";
        }
        else if (IsZoomed(hwnd))
        {
            // Maximized: give it a "normal" rect on the target monitor and re-maximize there.
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hwnd, ref wp);
            RECT normal = WorkspaceToScreen(wp.rcNormalPosition, src);
            RECT mapped = MapRect(normal, src.rcWork, tgt.rcWork, scale);
            wp.rcNormalPosition = ScreenToWorkspace(mapped, tgt);
            wp.showCmd = SW_SHOWMAXIMIZED;
            wp.flags = 0;
            if (!SetWindowPlacement(hwnd, ref wp))
            {
                detail = $"SetWindowPlacement error {Marshal.GetLastWin32Error()}";
                return PlaceResult.Failed;
            }
            if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != targetMon)
            {
                // Direct: keep it maximized and resize it over the target work area, with the same
                // invisible-border overhang it had on the source monitor. Then re-apply the normal
                // rect so "restore" also lands on the target monitor.
                GetWindowRect(hwnd, out RECT cur);
                int bl = src.rcWork.Left - cur.Left, bt = src.rcWork.Top - cur.Top;
                int br = cur.Right - src.rcWork.Right, bb = cur.Bottom - src.rcWork.Bottom;
                int s(int v) => (int)Math.Round(v * scale);
                SetWindowPos(hwnd, IntPtr.Zero,
                    tgt.rcWork.Left - s(bl), tgt.rcWork.Top - s(bt),
                    tgt.rcWork.Width + s(bl) + s(br), tgt.rcWork.Height + s(bt) + s(bb),
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == targetMon && IsZoomed(hwnd))
                {
                    SetWindowPlacement(hwnd, ref wp);
                    detail = "maximized (direct)";
                }
            }
            else detail = "maximized";

            if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != targetMon || !IsZoomed(hwnd))
            {
                // Fallback: restore, move, maximize (may flicker briefly).
                ShowWindow(hwnd, SW_RESTORE);
                SetWindowPos(hwnd, IntPtr.Zero, mapped.Left, mapped.Top, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                ShowWindow(hwnd, SW_SHOWMAXIMIZED);
                detail = "maximized (fallback path)";
            }
        }
        else
        {
            GetWindowRect(hwnd, out RECT r);
            RECT mapped = MapRect(r, src.rcWork, tgt.rcWork, scale);
            const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
            // 1. If it needs to shrink, do it while still on the source monitor (in source pixels):
            //    a window bigger than the target would otherwise still count as on the source.
            int needW = (int)Math.Round(mapped.Width / scale), needH = (int)Math.Round(mapped.Height / scale);
            if (needW < r.Width - 2 || needH < r.Height - 2)
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, needW, needH, 0x0002 /* SWP_NOMOVE */ | flags);
            // 2. Move. Per-monitor-DPI apps rescale themselves on the DPI change (synchronously,
            //    inside this call); DPI-unaware apps are scaled by Windows.
            if (!SetWindowPos(hwnd, IntPtr.Zero, mapped.Left, mapped.Top, 0, 0, SWP_NOSIZE | flags))
            {
                detail = $"SetWindowPos error {Marshal.GetLastWin32Error()} (elevated app? run LaunchHere as admin)";
                return PlaceResult.Failed;
            }
            // 3. Now on the target DPI: pin the exact size/position (apps round differently).
            GetWindowRect(hwnd, out RECT after);
            if (Math.Abs(after.Left - mapped.Left) > 1 || Math.Abs(after.Top - mapped.Top) > 1 ||
                Math.Abs(after.Width - mapped.Width) > 1 || Math.Abs(after.Height - mapped.Height) > 1)
                SetWindowPos(hwnd, IntPtr.Zero, mapped.Left, mapped.Top, mapped.Width, mapped.Height, flags);
            detail = $"{r} -> {mapped}";
            // A normal window as big as the screen (Chrome opens new windows like this) is meant to be
            // maximized: maximize it on the target instead of leaving it a bit smaller.
            if (NearFull(r, src.rcWork) && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == targetMon)
            {
                MaximizeHere(hwnd, tgt);
                detail += IsZoomed(hwnd) ? ", maximized (was screen-sized)" : ", maximize failed";
            }
        }

        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != targetMon)
        {
            detail += " (window did not end up on target - blocked by app or elevation?)";
            return PlaceResult.Failed;
        }
        return PlaceResult.Moved;
    }

    /// <summary>If a normal (non-maximized) window spills outside the target work area, pull it in / shrink it.</summary>
    public static bool ClampToWorkArea(IntPtr hwnd, IntPtr targetMon)
    {
        if (IsIconic(hwnd) || IsZoomed(hwnd)) return false;
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != targetMon) return false;
        if (IsFullscreen(hwnd, MonitorInfo(targetMon))) return false;   // meant to cover the taskbar
        GetWindowRect(hwnd, out RECT r);
        RECT w = MonitorInfo(targetMon).rcWork;
        int width = Math.Min(r.Width, w.Width), height = Math.Min(r.Height, w.Height);
        int left = Math.Clamp(r.Left, w.Left, w.Right - width);
        int top = Math.Clamp(r.Top, w.Top, w.Bottom - height);
        if (left == r.Left && top == r.Top && width == r.Width && height == r.Height) return false;
        return SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Covers (nearly) the whole work area: 95%+ of its width and height.</summary>
    static bool NearFull(RECT r, RECT work) =>
        r.Width >= work.Width * 0.95 && r.Height >= work.Height * 0.95;

    /// <summary>Maximize a window already on this monitor, with a sensible restore-down rect
    /// (75% of the work area, centered) instead of the screen-sized one.</summary>
    static void MaximizeHere(IntPtr hwnd, MONITORINFOEX mon)
    {
        RECT w = mon.rcWork;
        int nw = w.Width * 3 / 4, nh = w.Height * 3 / 4;
        var normal = new RECT(w.Left + (w.Width - nw) / 2, w.Top + (w.Height - nh) / 2,
                              w.Left + (w.Width - nw) / 2 + nw, w.Top + (w.Height - nh) / 2 + nh);
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref wp);
        wp.rcNormalPosition = ScreenToWorkspace(normal, mon);
        wp.showCmd = SW_SHOWMAXIMIZED;
        wp.flags = 0;
        SetWindowPlacement(hwnd, ref wp);
        if (!IsZoomed(hwnd)) ShowWindow(hwnd, SW_SHOWMAXIMIZED);
    }

    /// <summary>A screen-sized normal window that is already on the right monitor: maximize it.</summary>
    public static bool MaximizeIfScreenSized(IntPtr hwnd, IntPtr mon)
    {
        if (IsIconic(hwnd) || IsZoomed(hwnd)) return false;
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != mon) return false;
        var m = MonitorInfo(mon);
        if (IsFullscreen(hwnd, m)) return false;
        GetWindowRect(hwnd, out RECT r);
        if (!NearFull(r, m.rcWork)) return false;
        MaximizeHere(hwnd, m);
        return IsZoomed(hwnd);
    }

    /// <summary>Restore a minimized window straight onto the target monitor (maximized again if it
    /// was), by moving its saved restore rect there first, so it never flashes on the old monitor.</summary>
    public static bool RestoreOnto(IntPtr hwnd, IntPtr targetMon, out string detail)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref wp);
        IntPtr curMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var src = MonitorInfo(curMon);
        var tgt = MonitorInfo(targetMon);
        double scale = MonitorDpi(targetMon) / (double)MonitorDpi(curMon);
        bool max = (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;
        RECT normal = WorkspaceToScreen(wp.rcNormalPosition, src);
        RECT mapped = MapRect(normal, src.rcWork, tgt.rcWork, scale);
        if (!max && NearFull(normal, src.rcWork))
        {
            // Screen-sized normal window: bring it back maximized, with a 75% restore-down size.
            max = true;
            RECT w = tgt.rcWork;
            int nw = w.Width * 3 / 4, nh = w.Height * 3 / 4;
            mapped = new RECT(w.Left + (w.Width - nw) / 2, w.Top + (w.Height - nh) / 2,
                              w.Left + (w.Width - nw) / 2 + nw, w.Top + (w.Height - nh) / 2 + nh);
        }
        wp.rcNormalPosition = ScreenToWorkspace(mapped, tgt);
        wp.showCmd = max ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
        wp.flags = 0;
        bool ok = SetWindowPlacement(hwnd, ref wp);
        if (ok && !max)
        {
            // The DPI change on restore can leave the app at a size of its own choosing; pin it.
            GetWindowRect(hwnd, out RECT after);
            if (!Same(after, mapped))
                SetWindowPos(hwnd, IntPtr.Zero, mapped.Left, mapped.Top, mapped.Width, mapped.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        detail = ok ? (max ? "maximized" : $"{normal} -> {mapped}") : $"SetWindowPlacement error {Marshal.GetLastWin32Error()}";
        return ok && !IsIconic(hwnd);
    }

    /// <summary>Not maximized, but covering the whole monitor (taskbar included).</summary>
    public static bool IsFullscreen(IntPtr hwnd, MONITORINFOEX mon)
    {
        if (IsIconic(hwnd) || IsZoomed(hwnd)) return false;
        GetWindowRect(hwnd, out RECT r);
        return Same(r, mon.rcMonitor);
    }

    /// <summary>Size a full-screen window to exactly cover this monitor rect. Moving to a monitor
    /// with another DPI makes the app propose its own size, so check and set it again.</summary>
    public static bool PinFullscreen(IntPtr hwnd, RECT m)
    {
        const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        for (int i = 0; i < 3; i++)
        {
            GetWindowRect(hwnd, out RECT r);
            if (Same(r, m)) return true;
            if (!SetWindowPos(hwnd, IntPtr.Zero, m.Left, m.Top, m.Width, m.Height, flags)) return false;
        }
        GetWindowRect(hwnd, out RECT final);
        return Same(final, m);
    }

    static bool Same(RECT a, RECT b) =>
        Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 &&
        Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;

    /// <summary>How much of a forced width reduction is also applied to the height (0 = none,
    /// 1 = proportional). Set from config.</summary>
    public static double VerticalShrinkShare { get; set; } = 0.2;

    /// <summary>Target rect: same on-screen (DPI-adjusted) size; if too wide for the target, width
    /// shrinks to fit and height shrinks only by VerticalShrinkShare of that; never taller than the
    /// work area. Center keeps the same relative spot, clamped inside the target work area.</summary>
    static RECT MapRect(RECT r, RECT srcWork, RECT tgtWork, double scale)
    {
        // A window filling (nearly) the whole work area - e.g. Edge restoring a "maximized-sized"
        // normal window - should fill the target work area too.
        if (r.Width >= srcWork.Width * 0.95 && r.Height >= srcWork.Height * 0.95)
            return tgtWork;

        double dw = r.Width * scale, dh = r.Height * scale;
        if (dw > tgtWork.Width)
        {
            // Too wide: fit the width; shrink the height only by a share of that.
            double k = tgtWork.Width / dw;
            dw = tgtWork.Width;
            dh *= 1 - (1 - k) * Math.Clamp(VerticalShrinkShare, 0, 1);
        }
        int w = (int)Math.Round(dw);
        int h = Math.Min((int)Math.Round(dh), tgtWork.Height);
        double fx = (r.Left + r.Width / 2.0 - srcWork.Left) / Math.Max(1, srcWork.Width);
        double fy = (r.Top + r.Height / 2.0 - srcWork.Top) / Math.Max(1, srcWork.Height);
        fx = Math.Clamp(fx, 0, 1); fy = Math.Clamp(fy, 0, 1);
        int left = (int)Math.Round(tgtWork.Left + fx * tgtWork.Width - w / 2.0);
        int top = (int)Math.Round(tgtWork.Top + fy * tgtWork.Height - h / 2.0);
        left = Math.Clamp(left, tgtWork.Left, tgtWork.Right - w);
        top = Math.Clamp(top, tgtWork.Top, tgtWork.Bottom - h);
        return new RECT(left, top, left + w, top + h);
    }

    // WINDOWPLACEMENT uses "workspace" coordinates, offset from screen coordinates by the space
    // the taskbar takes at the top/left of the monitor. Zero when taskbars are at the bottom.
    static RECT WorkspaceToScreen(RECT r, MONITORINFOEX m) => Offset(r, m.rcWork.Left - m.rcMonitor.Left, m.rcWork.Top - m.rcMonitor.Top);
    static RECT ScreenToWorkspace(RECT r, MONITORINFOEX m) => Offset(r, m.rcMonitor.Left - m.rcWork.Left, m.rcMonitor.Top - m.rcWork.Top);
    static RECT Offset(RECT r, int dx, int dy) => new(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
}
