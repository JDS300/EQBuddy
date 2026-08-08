using System.Windows;
using System.Windows.Media;

namespace EQBuddy;

/// <summary>
/// Applies AppSettings.ChipScale to a small floating window (spawn chips, mez chips,
/// alert banner) as a LayoutTransform on its content — the same mechanism UiScale uses
/// on the widget, so SizeToContent re-measures and the window grows to fit. One shared
/// scale for the whole chicklet family: they're read at the same glance, so they should
/// agree on a size (discussion #47, jlewisnj at 4K).
/// </summary>
internal static class ChipScale
{
    public static void Apply(Window window, double scale)
    {
        if (window.Content is not FrameworkElement root) return;
        root.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
            ? null
            : new ScaleTransform(scale, scale);
    }
}
