using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy;

/// <summary>
/// Universal per-window text zoom: Ctrl + mouse wheel over any EQBuddy window scales its
/// content, persisted per window kind (discussion #59; David 2026-08-07: one permanent
/// mechanism instead of a slider per window). SizeToContent windows grow to fit; fixed
/// windows let their ScrollViewers absorb the overflow. Windows with a dedicated scale
/// (the widget's UiScale, the chips' ChipScale) route their wheel to that setting
/// instead, so there's exactly one number per surface.
/// </summary>
internal static class WindowZoom
{
    public static void Attach(Window window, string key, AppSettings settings)
    {
        if (window.Content is not FrameworkElement root) return;
        Apply(root, settings.WindowZooms.TryGetValue(key, out var saved) ? saved : 1.0);
        window.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            var current = settings.WindowZooms.TryGetValue(key, out var c) ? c : 1.0;
            var next = WindowZoomMath.Step(current, e.Delta);
            settings.WindowZooms[key] = next;
            Apply(root, next);
            settings.Save();
        };
    }

    /// <summary>For windows whose scale already lives in a named setting: wheel just
    /// drives that setter (which applies, clamps, and persists on its own).</summary>
    public static void Route(Window window, Func<double> get, Action<double> set)
    {
        window.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            set(WindowZoomMath.Step(get(), e.Delta));
        };
    }

    private static void Apply(FrameworkElement root, double zoom) =>
        root.LayoutTransform = Math.Abs(zoom - 1.0) < 0.001
            ? null
            : new ScaleTransform(zoom, zoom);
}
