using System.Runtime.InteropServices;
using System.Windows;

namespace EQBuddy;

/// <summary>
/// The work area of the monitor a window is ACTUALLY on, in DIPs.
/// SystemParameters.WorkArea is the PRIMARY monitor only — sizing against it caps a
/// widget on a secondary portrait screen at roughly half its height (discussion #31,
/// togreglove), the same primary-only bug class as the old alert-tile clamp.
/// </summary>
internal static class MonitorMetrics
{
    /// <summary>Work-area size of the window's current monitor, DIP-scaled; null
    /// before the window handle exists (callers keep their previous cap).</summary>
    public static (double Width, double Height)? WorkAreaFor(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        var m = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        var (sx, sy) = m is { } t ? (t.M11, t.M22) : (1.0, 1.0);
        return ((info.rcWork.right - info.rcWork.left) * sx,
                (info.rcWork.bottom - info.rcWork.top) * sy);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
}
