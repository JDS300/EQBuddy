using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Avalonia;

/// <summary>
/// The spawn-countdown chicklet stack (SPAWN-005, David's design): while timers run,
/// each shows as a small movable chip — "⏳ Asaka L`Rei 3:12" — and the full zone list
/// only exists behind a double-click. At zero a chip flips to a warn-colored DUE for
/// one minute, then clears itself — click it away sooner if you're watching. The stack shows every
/// running timer on the server regardless of zone: a Befallen camp timer keeps counting
/// while you bank in West Commonlands, because a timer you set is a timer you care
/// about. MainWindow's shared tick drives refresh and decides visibility — the stack
/// exists exactly while timers do, staying up alongside the full zone window too
/// (dropping it while the browser was open lost the countdowns you cared about).
/// Sibling of <see cref="MezChipsWindow"/>: same chicklet language, deliberately its own
/// window and its own saved position (spawn chips are ambient, mez chips are combat).
/// </summary>
internal sealed class SpawnChipsWindow : Window
{
    private readonly MainWindow _owner;
    private readonly SpawnsViewModel _vm;
    private readonly AppSettings _settings;
    private readonly LayoutTransformControl _scaleRoot = new();
    private readonly StackPanel _chipsPanel = new();
    private readonly List<TextBlock> _countdowns = [];
    private List<SpawnChip> _chips = [];
    private string _signature = "";

    // The last position seen while the stack was actually on screen. A window that is
    // hidden or already torn down reports a Position the backend has reset (X11 collapses
    // it to 0,0), so a save that reads Position at the wrong moment loses the drag. The
    // Closing hook below runs while the platform window still exists and is the normal
    // path; this snapshot is what a Closed-time or hidden-window save falls back to.
    private PixelPoint _lastVisiblePosition;
    private bool _haveVisiblePosition;

    public SpawnChipsWindow(MainWindow owner, SpawnsViewModel vm)
    {
        _owner = owner;
        _vm = vm;
        _settings = owner.Settings;

        Title = "EQBuddy Spawn Chips";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // A topmost window that activates on show yanks keyboard input out of EverQuest
        // on X11 — the same gotcha AlertWindow.cs:34 guards against. This stack appears
        // unattended the moment a named dies, which is precisely mid-fight, so it must
        // never take focus.
        ShowActivated = false;
        CanResize = false;
        Opacity = _settings.Opacity;

        _scaleRoot.Child = _chipsPanel;
        Content = _scaleRoot;

        // Position is saved on close as well as on demand: MainWindow tears the stack down
        // when the last timer expires, and a drag you did thirty seconds earlier should
        // still be where you left it next time.
        Closing += (_, _) => SavePosition();

        // Capture moves only while the stack is visibly on screen. The live settings object
        // is updated at the same time, so a stack recreated later in the same session comes
        // back where you left it even before Closing has flushed the file.
        PositionChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _lastVisiblePosition = Position;
            _haveVisiblePosition = true;
            _settings.SpawnChipsLeft = Position.X;
            _settings.SpawnChipsTop = Position.Y;
        };

        PositionFromSettings();
    }

    /// <summary>Called from MainWindow's 1 s tick while the stack is visible.</summary>
    public void RefreshChips(DateTime now)
    {
        _chips = _vm.Chips(now);
        // Icon is part of the signature: a chip whose glyph changes without its identity
        // changing (⏳ to the due marker) still has to be redrawn, and U+0001 joins the
        // parts because no zone or mob name can contain it.
        var signature = string.Join("\u0001",
            _chips.Select(c => $"{c.Zone}|{c.Name}|{c.IsDue}|{c.Icon}"));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild();
        }
        else
        {
            for (var i = 0; i < _chips.Count && i < _countdowns.Count; i++)
                _countdowns[i].Text = _chips[i].IsDue ? "DUE" : _chips[i].CountdownText;
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
                Text = chip.IsDue ? "DUE" : chip.CountdownText,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
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
                // A chip is clickable (dismiss when due) and draggable, so it says so.
                Cursor = new Cursor(StandardCursorType.Hand),
                // The handler reads the chip back off the border, exactly as WPF did —
                // one handler for the whole stack, no closure per row to leak.
                Tag = chip,
            };
            ToolTip.SetTip(border, chip.Detail);
            border.PointerPressed += OnChipPressed;
            _chipsPanel.Children.Add(border);
        }
    }

    private void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: SpawnChip chip }) return;
        if (e.ClickCount == 2)
        {
            // The full zone list, opened on the chip's zone. MainWindow's tick decides
            // what happens to the stack from there.
            _owner.ShowSpawnsWindow(chip.Zone);
            e.Handled = true;
            return;
        }
        if (chip.IsDue)
        {
            // A due chip has said its piece — click acknowledges and clears the timer.
            _vm.ClearTimer(chip.Zone, chip.Name);
            _signature = "";
            RefreshChips(DateTime.Now);
            e.Handled = true;
            return;
        }
        // Left button only: a right-click on a chip must not start a window drag.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>Matches MainWindow.ApplyUiScale's LayoutTransformControl idiom exactly, so
    /// the spawn stack scales in lockstep with the rest of the UI.</summary>
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
        // While the platform window is still there Position is the truth — that is why the
        // save hangs off Closing rather than Closed. Once it is gone, fall back to the last
        // position seen while the stack was on screen instead of the backend's reset value.
        var saved = TryGetPlatformHandle() is not null ? Position
            : _haveVisiblePosition ? _lastVisiblePosition : Position;
        _settings.SpawnChipsLeft = saved.X;
        _settings.SpawnChipsTop = saved.Y;
        _settings.Save();
    }

    private void PositionFromSettings()
    {
        var left = _settings.SpawnChipsLeft;
        var top = _settings.SpawnChipsTop;
        if (!ScreenGuard.OnScreen(this, left, top, Width, Height))
        {
            // First use, or the saved monitor is gone: beside the widget, below the mez and
            // HoT stacks. Spawn chips are the ambient one, so they sit furthest down.
            Position = ScreenGuard.NextToOwner(_owner, slot: 2);
            return;
        }

        Position = new PixelPoint((int)left, (int)top);
    }
}
