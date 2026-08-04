using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EQBuddy.Core;
using SpawnChip = EQBuddy.UI.Shared.SpawnChip;

namespace EQBuddy.Avalonia;

/// <summary>
/// The mez-target stack: one chip per believed-active mez, counting down to wake-up.
/// Same chicklet language as the spawn stack but a separate window with its own saved
/// position — mez chips get parked next to the fight, spawn chips are ambient. Chips
/// only drag; there is nothing to click through to and nothing to clear (breaks clear
/// themselves via the log).
/// </summary>
internal sealed class MezChipsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainWindow _owner;
    private readonly Func<DateTime, List<SpawnChip>> _source;
    private readonly LayoutTransformControl _scaleRoot = new();
    private readonly StackPanel _chipsPanel = new();
    private readonly List<TextBlock> _countdowns = [];
    private List<SpawnChip> _chips = [];
    private string _signature = "";

    public MezChipsWindow(MainWindow owner, Func<DateTime, List<SpawnChip>> source)
    {
        _owner = owner;
        _settings = owner.Settings;
        _source = source;

        Title = "EQBuddy Mez Chips";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // A topmost window that steals focus mid-fight yanks keyboard input away from
        // EverQuest — same X11 gotcha AlertWindow.cs:34 guards against. This chip stack
        // can pop up unattended while the player is actively fighting, so it must never
        // activate itself.
        ShowActivated = false;
        CanResize = false;
        Opacity = _settings.Opacity;

        _scaleRoot.Child = _chipsPanel;
        Content = _scaleRoot;

        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Closing += (_, _) => SavePosition();

        PositionFromSettings();
    }

    /// <summary>Called from MainWindow's 1 s tick while the stack is visible.</summary>
    public void RefreshChips(DateTime now)
    {
        _chips = _source(now);
        var signature = string.Join("", _chips.Select(c => $"{c.Name}|{c.IsDue}"));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild();
        }
        else
        {
            for (var i = 0; i < _chips.Count && i < _countdowns.Count; i++)
                _countdowns[i].Text = _chips[i].CountdownText;
        }
    }

    private void Rebuild()
    {
        _chipsPanel.Children.Clear();
        _countdowns.Clear();
        foreach (var chip in _chips)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var name = new TextBlock
            {
                Text = $"{chip.Icon} {chip.Name}",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180,
                Foreground = AppTheme.TextBrush,
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var countdown = new TextBlock
            {
                Text = chip.CountdownText,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                // Warning tint inside the last tick — the wake-up is the urgent moment.
                Foreground = chip.IsDue ? AppTheme.WarnBrush : AppTheme.AccentBrush,
            };
            Grid.SetColumn(countdown, 1);
            row.Children.Add(countdown);
            _countdowns.Add(countdown);

            var border = new Border
            {
                Child = row,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 0, 3),
                BorderThickness = new Thickness(1),
                Background = AppTheme.BgBrush,
                BorderBrush = chip.IsDue ? AppTheme.WarnBrush : AppTheme.BorderBrush,
            };
            ToolTip.SetTip(border, chip.Detail);
            _chipsPanel.Children.Add(border);
        }
    }

    /// <summary>Matches MainWindow.ApplyUiScale's LayoutTransformControl idiom exactly, so
    /// the mez stack scales in lockstep with the rest of the UI.</summary>
    public void ApplyScale(double scale)
    {
        _scaleRoot.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? null : new ScaleTransform(scale, scale);
        _scaleRoot.InvalidateMeasure();
        InvalidateMeasure();
    }

    public void ApplyClickThrough(bool enabled)
    {
        if (TryGetPlatformHandle() is null) return;
        X11ClickThrough.Set(this, enabled);
    }

    public void SavePosition()
    {
        _settings.MezChipsLeft = Position.X;
        _settings.MezChipsTop = Position.Y;
        _settings.Save();
    }

    private void PositionFromSettings()
    {
        var left = _settings.MezChipsLeft;
        var top = _settings.MezChipsTop;
        if (!ScreenGuard.OnScreen(this, left, top, Width, Height))
        {
            // First use, or the saved monitor is gone: parked near the owner widget
            // (WPF parity — work-area left+40, top+120).
            var work = _owner.Screens.ScreenFromWindow(_owner)?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            left = work.X + 40;
            top = work.Y + 120;
        }

        Position = new PixelPoint((int)left, (int)top);
    }
}
