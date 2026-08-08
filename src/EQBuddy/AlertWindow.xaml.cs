using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EQBuddy.Core;

namespace EQBuddy;

/// <summary>
/// The floating alert tile: a tiny always-on-top window, independent of the widget,
/// that shows tracked-rule alerts. It is permanently click-through and never takes
/// focus, so it can sit over the game without interfering — except while Options is
/// open (placement mode), when it becomes draggable so the user can position it.
/// </summary>
public partial class AlertWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _hide;
    private bool _placement;

    public AlertWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ChipScale.Apply(this, settings.ChipScale);
        _hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _hide.Tick += (_, _) => { _hide.Stop(); if (!_placement) Hide(); };
        SourceInitialized += (_, _) => ApplyClickThrough(!_placement);
    }

    /// <summary>Show the banner, optionally tinted with a rule's own color (Chaosrah's
    /// idea, 2026-08-06: colors identify the alert when the sound is off or quiet —
    /// "mez purple, heals green, enemy red"). Null/empty keeps the theme accent, and the
    /// tint is applied per call so one rule's color never sticks to the next alert.</summary>
    public void ShowAlert(string text, string? colorHex = null)
    {
        AlertText.Text = text;
        var tile = (System.Windows.Controls.Border)Content;
        if (!string.IsNullOrEmpty(colorHex) &&
            System.Windows.Media.ColorConverter.ConvertFromString(colorHex) is System.Windows.Media.Color c)
        {
            var brush = new System.Windows.Media.SolidColorBrush(c);
            AlertText.Foreground = brush;
            tile.BorderBrush = brush;
        }
        else
        {
            AlertText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentBrush");
            tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "AccentBrush");
        }
        PositionFromSettings();
        Show();
        Topmost = true;
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>Options is open: show the tile as a draggable placement target.</summary>
    public void EnterPlacement()
    {
        _placement = true;
        _hide.Stop();
        AlertText.Text = "★ Alert banner — drag me to where alerts should appear";
        PositionFromSettings();
        Show();
        ApplyClickThrough(false);
        Topmost = true;
    }

    /// <summary>Options closed: persist the position and go back to click-through.</summary>
    public void ExitPlacement()
    {
        if (!_placement) return;
        _placement = false;
        _settings.AlertLeft = Left;
        _settings.AlertTop = Top;
        _settings.Save();
        ApplyClickThrough(true);
        Hide();
    }

    private void PositionFromSettings()
    {
        var wa = SystemParameters.WorkArea;
        double left = _settings.AlertLeft, top = _settings.AlertTop;
        // Checked against the whole virtual screen: the old primary-only clamp yanked
        // an alert tile deliberately parked on a second monitor back every launch.
        if (!ScreenGuard.OnScreen(left, top, 140, 44))
        {
            // First use, or the saved monitor is gone: just above the widget,
            // falling back to the top-right corner.
            left = Math.Clamp(Owner?.Left ?? (wa.Right - 400), wa.Left, wa.Right - 140);
            top = Math.Clamp((Owner?.Top ?? 110) - 64, wa.Top, wa.Bottom - 44);
        }
        Left = left;
        Top = top;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (_placement && e.ChangedButton == MouseButton.Left) DragMove();
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private void ApplyClickThrough(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;   // not created yet — SourceInitialized applies it
        var style = GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow;
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }
}
