using Avalonia;
using Avalonia.Controls;
using EQBuddy.Core;

namespace EQBuddy.Avalonia;

/// <summary>Avalonia adapter for <see cref="WindowPlacement"/>: checks saved positions
/// against the union of all screens. When the platform reports no screens (headless
/// tests), a saved position is trusted as-is — the guard only exists to catch
/// monitor-layout changes, and there is no layout to check against.</summary>
internal static class ScreenGuard
{
    public static bool OnScreen(WindowBase window, double left, double top,
        double width = double.NaN, double height = double.NaN)
    {
        var screens = window.Screens?.All;
        if (screens is null || screens.Count == 0)
            return !double.IsNaN(left) && !double.IsNaN(top);
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var s in screens)
        {
            l = Math.Min(l, s.Bounds.X);
            t = Math.Min(t, s.Bounds.Y);
            r = Math.Max(r, s.Bounds.Right);
            b = Math.Max(b, s.Bounds.Bottom);
        }
        return WindowPlacement.IsReachable(left, top, l, t, r - l, b - t, width, height);
    }

    /// <summary>Where a chip stack goes when it has no saved position: immediately right of
    /// the widget, stacked down by <paramref name="slot"/>. The old default was a corner of
    /// the work area, which on a multi-monitor desktop could be a different screen from the
    /// widget entirely — "it should start near the main window" (reported from play).
    /// Clamped so a widget parked at the right edge doesn't push the stack off-screen.</summary>
    public static PixelPoint NextToOwner(Window owner, int slot, double width = 200, double height = 90)
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        // Position is in PHYSICAL pixels but Width is device-independent, so on a scaled
        // display the two cannot be added directly — the stack landed 78px INSIDE the
        // widget on a 1.25x monitor before this. Same WorkingArea/Scaling pairing
        // MainWindow.UpdateWindowHeightLimit uses.
        var scale = screen?.Scaling ?? 1.0;
        // Width is NaN until the owner has been laid out; fall back to its rendered bounds.
        var ownerWidth = double.IsNaN(owner.Width) ? owner.Bounds.Width : owner.Width;
        double left = owner.Position.X + Math.Max(0, ownerWidth) * scale + Gap * scale;
        double top = owner.Position.Y + slot * SlotHeight * scale;
        // Off the right edge (widget parked right): fall back to the widget's left side.
        if (left + width * scale > work.Right) left = owner.Position.X - (width + Gap) * scale;
        return new PixelPoint(
            (int)Math.Clamp(left, work.X, Math.Max(work.X, work.Right - width)),
            (int)Math.Clamp(top, work.Y, Math.Max(work.Y, work.Bottom - height)));
    }

    private const int Gap = 8;
    /// <summary>Vertical spacing between stacks. Three stacks defaulting to the same pixel
    /// read as one broken window until they're dragged apart.</summary>
    private const int SlotHeight = 110;
}
