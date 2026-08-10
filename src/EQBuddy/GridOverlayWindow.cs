using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EQBuddy.Core;

namespace EQBuddy;

/// <summary>
/// Full-desktop click-through alignment grid (discussion #34 — "a bit OCD with my UI
/// and would love a way to overlay a grid so I can align UI elements"). Covers the
/// whole virtual screen so it works on whichever monitor the game lives on, never
/// takes focus, never eats a click — the right-click menu toggle (or the Options
/// checkbox) is the only way in or out, exactly like the alert tile's click-through.
///
/// The grid is two tiled DrawingBrushes (minor lines at the chosen spacing, stronger
/// lines every fourth) rather than thousands of Line elements — spacing changes are
/// a brush swap, and rendering cost stays flat no matter the desk size.
/// </summary>
public sealed class GridOverlayWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Border _minor = new();
    private readonly Border _major = new();

    public GridOverlayWindow(AppSettings settings)
    {
        _settings = settings;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);   // 1px lines stay 1px
        _minor.Child = _major;
        Content = _minor;
        SourceInitialized += (_, _) => ApplyClickThrough();
        ApplySpacing();
    }

    /// <summary>(Re)build the brushes from the current settings — called on open and
    /// live from the Options slider, so dragging it tunes the grid in place.</summary>
    public void ApplySpacing()
    {
        var spacing = Math.Clamp(_settings.GridSpacing, 8, 256);
        var color = (TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.Silver;
        _minor.Background = GridBrush(spacing, color, 0.18);
        _major.Background = GridBrush(spacing * 4, color, 0.40);
    }

    private static DrawingBrush GridBrush(double spacing, Color color, double opacity)
    {
        var pen = new Pen(new SolidColorBrush(color) { Opacity = opacity }, 1);
        pen.Freeze();
        var lines = new GeometryGroup
        {
            Children =
            {
                new LineGeometry(new Point(0, 0), new Point(spacing, 0)),
                new LineGeometry(new Point(0, 0), new Point(0, spacing)),
            },
        };
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, lines))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, spacing, spacing),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    // Same Win32 recipe as AlertWindow: transparent to the mouse, never activated,
    // invisible to alt-tab.
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowLong(hwnd, GwlExStyle,
            GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }
}
