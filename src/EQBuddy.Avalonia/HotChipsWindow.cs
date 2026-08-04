using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EQBuddy.Core;
using SpawnChip = EQBuddy.UI.Shared.SpawnChip;

namespace EQBuddy.Avalonia;

/// <summary>
/// The heal-over-time stack: one chip per HoT of yours still ticking, counting down to the
/// moment it stops healing. Same chicklet language and the same drag-only interaction as
/// the mez and spawn stacks, in a third window with its own saved position — a healer parks
/// "who am I keeping up" somewhere different from either of those.
///
/// One visual rule the other stacks don't have: the chip on YOU is tinted with the "good"
/// brush rather than the accent one. Your own buff bar already shows your HoT, while a
/// groupmate's is invisible to you, so the two kinds of row answer different questions and
/// have to be separable at a glance. See <see cref="EQBuddy.UI.Shared.HotChipPresentation"/>
/// for where Emphasis is set (and <see cref="AppSettings.ShowSelfHotChips"/> for dropping
/// self chips entirely).
/// </summary>
internal sealed class HotChipsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainWindow _owner;
    private readonly Func<DateTime, List<SpawnChip>> _source;
    private readonly LayoutTransformControl _scaleRoot = new();
    private readonly StackPanel _chipsPanel = new();
    private readonly List<TextBlock> _countdowns = [];
    private List<SpawnChip> _chips = [];
    private string _signature = "";

    public HotChipsWindow(MainWindow owner, Func<DateTime, List<SpawnChip>> source)
    {
        _owner = owner;
        _settings = owner.Settings;
        _source = source;

        Title = "EQBuddy HoT Chips";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // A topmost window that steals focus mid-fight yanks keyboard input away from
        // EverQuest — the same X11 gotcha AlertWindow.cs:34 and MezChipsWindow.cs:46 guard
        // against. This stack appears the moment a HoT lands, which is by definition during
        // a fight and never at the player's request, so it must never activate itself.
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
        // Emphasis rides in the signature alongside IsDue because it drives a brush: a HoT
        // recast on someone else while yours lapses can leave the row set identical and the
        // colours wrong. Countdown text is deliberately NOT in here — it changes every tick
        // and is patched in place below, which is the whole point of the diff.
        var signature = string.Join("", _chips.Select(c => $"{c.Name}|{c.IsDue}|{c.Emphasis}"));
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
                // Three-way, in priority order. Due wins outright: one tick from the end is
                // the only urgent moment a HoT has, and "recast now" beats "this one is
                // mine". Otherwise Emphasis (your own HoT) takes the good brush and everyone
                // else takes the accent — the at-a-glance split described on the class.
                Foreground = chip.IsDue ? AppTheme.WarnBrush
                    : chip.Emphasis ? AppTheme.GoodBrush
                    : AppTheme.AccentBrush,
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
                // The frame stays the plain two-state one the other stacks use. Only the
                // countdown carries the self/other distinction: tinting the border too made
                // a group of HoTs read as several kinds of alert rather than one list.
                BorderBrush = chip.IsDue ? AppTheme.WarnBrush : AppTheme.BorderBrush,
            };
            ToolTip.SetTip(border, chip.Detail);
            _chipsPanel.Children.Add(border);
        }
    }

    /// <summary>Matches MainWindow.ApplyUiScale's LayoutTransformControl idiom exactly, so
    /// the HoT stack scales in lockstep with the rest of the UI.</summary>
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
        _settings.HotChipsLeft = Position.X;
        _settings.HotChipsTop = Position.Y;
        _settings.Save();
    }

    private void PositionFromSettings()
    {
        var left = _settings.HotChipsLeft;
        var top = _settings.HotChipsTop;
        if (!ScreenGuard.OnScreen(this, left, top, Width, Height))
        {
            // First use, or the saved monitor is gone. Offset from the mez stack's default
            // (work-area left+40, top+120) rather than sharing it: three stacks that all
            // default to the same pixel look like one broken window until they're dragged
            // apart, and a healer may well have mez and HoT chips up at the same time.
            var work = _owner.Screens.ScreenFromWindow(_owner)?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            left = work.X + 40;
            top = work.Y + 260;
        }

        Position = new PixelPoint((int)left, (int)top);
    }
}
