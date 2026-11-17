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

        if (IsZoomed(hwnd))
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
        GetWindowRect(hwnd, out RECT r);
        RECT w = MonitorInfo(targetMon).rcWork;
        int width = Math.Min(r.Width, w.Width), height = Math.Min(r.Height, w.Height);
        int left = Math.Clamp(r.Left, w.Left, w.Right - width);
        int top = Math.Clamp(r.Top, w.Top, w.Bottom - height);
        if (left == r.Left && top == r.Top && width == r.Width && height == r.Height) return false;
        return SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

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
