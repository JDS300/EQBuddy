using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace EQBuddy.Avalonia;

/// <summary>
/// The way out of click-through: while the widget's clicks pass to the game, this tiny
/// normal-hit-test chip parks beside it — one click restores interaction. Mirrors the
/// WPF chip for the same reason it exists there: the global hotkeys are gone (they ate
/// Ctrl+Shift+T from every browser on the machine — Reddit report, 2026-08-06), and a
/// window whose input shape is empty cannot, by definition, be clicked back to life.
/// Amber like the click-through border so the two read as one state (issue #7).
/// </summary>
internal sealed class ClickThroughChip : Window
{
    public ClickThroughChip(Action unlock)
    {
        Title = "EQBuddy click-through";
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = null;

        var text = new TextBlock
        {
            Text = "\U0001F512 click-through",
            FontSize = 11,
            Foreground = AppTheme.WarnBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 2, 7, 3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = AppTheme.BgBrush,
            BorderBrush = AppTheme.WarnBrush,
            Child = text,
        };
        ToolTip.SetTip(chip, "Click to turn click-through off");
        chip.PointerReleased += (_, _) => unlock();
        Content = chip;
    }

    /// <summary>Parks just above the widget's top-left corner, screen-edge guarded.</summary>
    public void ShowNear(Window main)
    {
        Position = new PixelPoint(Math.Max(0, main.Position.X),
                                  Math.Max(0, main.Position.Y - 34));
        Show();
    }
}
