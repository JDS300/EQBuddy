using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Avalonia;

/// <summary>
/// The Spawns window (SPAWN-004): named-mob respawn countdowns for one zone at a time,
/// fed by <see cref="SpawnsViewModel"/>. While "Track spawns" is armed the window stays
/// HIDDEN until a countdown exists — it pops when a named (or placeholder) death starts
/// one, including timers recovered from the log at startup — and MainWindow closes it
/// again when the last timer runs out. ✕ only hides it; the next kill brings it back.
/// David's call (2026-08-02): a tracker parked on screen all session is noise, a tracker
/// that appears because something died is information. Alerts fire from MainWindow's
/// shared tick, not here, so they don't depend on this window's lifetime.
/// Unlike the chip stacks this one takes focus on open — you type in it.
/// </summary>
internal sealed class SpawnsWindow : Window
{
    private readonly MainWindow _main;
    private readonly SpawnsViewModel _vm;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick;

    private readonly TextBlock _titleText;
    private readonly ComboBox _zoneCombo;
    private readonly CheckBox _followCheck;
    private readonly ScrollViewer _bodyScroll;
    private readonly StackPanel _rowsPanel;
    private readonly TextBox _addNameBox;
    private readonly TextBox _addDurationBox;

    // Rebuilds are keyed on a signature of everything except the countdown text, so a
    // ticking clock updates labels in place and never yanks focus out of an edit box.
    private string _signature = "";
    private readonly List<TextBlock> _countdowns = [];
    private List<SpawnRow> _rows = [];
    private bool _syncingZone;
    private Border _chrome = null!;
    private bool _kicking;
    private bool _forceRebuild;

    /// <summary>The zone Follow last snapped to. Following reacts to zone CHANGES, not to
    /// every tick — so browsing another zone's list mid-camp survives until you actually
    /// zone, instead of the dropdown yanking itself back every second.</summary>
    private string? _lastFollowedZone;

    /// <summary><paramref name="initialZone"/>: the zone whose kill popped the window,
    /// so it opens showing the timer that summoned it.</summary>
    public SpawnsWindow(MainWindow main, SpawnsViewModel vm, string? initialZone = null)
    {
        _main = main;
        _vm = vm;
        _settings = main.Settings;

        Title = "EQBuddy Spawns";
        // Rows can carry start, bell, sound, clear, and delete actions, and which of those
        // exist changes as timers start and stop. Upstream widened the window and gave the
        // action lane a fixed width so that starting a timer (which adds ✕) can no longer
        // reflow the duration and "died ago" boxes out from under the cursor.
        Width = 740;
        SizeToContent = SizeToContent.Height;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;

        _titleText = new TextBlock
        {
            Text = "🕒 Spawns",
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Foreground = AppTheme.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _zoneCombo = new ComboBox { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(_zoneCombo, "Which zone's named to show");
        // Plain string content, not a TextBlock: the label has to stay readable as the
        // CheckBox's own Content for anything inspecting it, so the foreground is set on
        // the control instead of on a nested block.
        _followCheck = new CheckBox
        {
            Content = "Follow",
            FontSize = 11,
            Foreground = AppTheme.TextBrush,
            Margin = new Thickness(10, 2, 0, 0),
        };
        ToolTip.SetTip(_followCheck, "Switch to whatever zone the log says you are in");
        _rowsPanel = new StackPanel { Margin = new Thickness(16, 0, 16, 4) };
        _bodyScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rowsPanel,
        };
        _addNameBox = DarkBox("", "Add a named the catalog doesn't know (this zone)");
        _addDurationBox = DarkBox("", "Respawn time: 22 (minutes), 90s, 12h, 3d, 6:40");

        Content = BuildChrome();

        // Height auto-fits but is clamped to the work area; the body scrolls, the title
        // row stays reachable. The owner's screen answers before this window exists, and
        // Opened re-asks once we know which monitor we actually landed on.
        ApplyHeightLimit(main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary);
        Opened += (_, _) => ApplyHeightLimit(Screens.ScreenFromWindow(this) ?? Screens.Primary);

        PositionFromSettings();

        _vm.RefreshZoneList();
        foreach (var z in _vm.ZoneNames) _zoneCombo.Items.Add(z);
        _followCheck.IsChecked = _settings.SpawnFollowZone;

        SelectZone(initialZone
            ?? (_settings.SpawnFollowZone ? _vm.CurrentZoneName : null)
            ?? FirstNonEmpty(_settings.SpawnZone, _vm.ZoneNames.FirstOrDefault() ?? ""));
        _lastFollowedZone = _vm.CurrentZoneName;

        _zoneCombo.SelectionChanged += OnZonePicked;
        _followCheck.IsCheckedChanged += OnFollowChanged;

        // Its own tick, not MainWindow's: this window's refresh is a per-second countdown
        // redraw that only matters while it is open, and routing it through the widget's
        // shared tick would make the widget responsible for a window it doesn't own.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => RefreshRows();
        _tick.Start();
        RefreshRows();

        Closing += (_, _) =>
        {
            _tick.Stop();
            _settings.SpawnLeft = Position.X;
            _settings.SpawnTop = Position.Y;
            _settings.Save();
        };
    }

    private Control BuildChrome()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var titleRow = new Grid { Margin = new Thickness(16, 16, 16, 6) };
        titleRow.Children.Add(_titleText);
        // ✕ hides the window; tracking stays armed and the next kill re-opens it. Turning
        // the feature off lives in the menu and Options, deliberately elsewhere.
        var close = AppTheme.IconButton("✕",
            "Hide — the window returns when the next named dies (turn tracking off in Options or the right-click menu)");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        titleRow.Children.Add(close);
        grid.Children.Add(titleRow);

        var zoneRow = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        zoneRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        zoneRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        zoneRow.Children.Add(_zoneCombo);
        Grid.SetColumn(_followCheck, 1);
        zoneRow.Children.Add(_followCheck);
        Grid.SetRow(zoneRow, 1);
        grid.Children.Add(zoneRow);

        Grid.SetRow(_bodyScroll, 2);
        grid.Children.Add(_bodyScroll);

        var footer = new StackPanel { Margin = new Thickness(16, 4, 16, 14) };
        footer.Children.Add(new TextBlock
        {
            Text = "Kill a named (or its placeholder) and its countdown starts from the log. ▶ starts one by hand — type how long ago it died first (5m, 90s) or leave empty for now. We captured the respawn times we could from community sources; if what you see in game disagrees, just type over the duration — your number wins and survives updates.",
            FontSize = 10,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.TextBrush,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var addRow = new Grid();
        addRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        addRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(70)));
        addRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        addRow.Children.Add(_addNameBox);
        _addDurationBox.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_addDurationBox, 1);
        addRow.Children.Add(_addDurationBox);
        var add = AppTheme.IconButton("+", "Add");
        add.Margin = new Thickness(6, 0, 0, 0);
        add.Padding = new Thickness(8, 0);
        add.Click += (_, _) => OnAddCustom();
        Grid.SetColumn(add, 2);
        addRow.Children.Add(add);
        footer.Children.Add(addRow);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        // Same custom chrome as Options: no OS title bar, so the frame itself is the
        // drag handle. Focusable because Kick() parks keyboard focus here — see there.
        _chrome = new Border
        {
            Background = AppTheme.BgBrush,
            CornerRadius = new CornerRadius(10),
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            Focusable = true,
            Child = grid,
        };
        _chrome.PointerPressed += OnDrag;
        return _chrome;
    }

    private static string FirstNonEmpty(string a, string b) => a.Length > 0 ? a : b;

    private string SelectedZone => _zoneCombo.SelectedItem as string ?? "";

    private void SelectZone(string zone)
    {
        _syncingZone = true;
        _zoneCombo.SelectedItem = _vm.ZoneNames.FirstOrDefault(z =>
            string.Equals(z, zone, StringComparison.OrdinalIgnoreCase)) ?? _zoneCombo.SelectedItem;
        _syncingZone = false;
    }

    private void RefreshRows()
    {
        // Follow the player: the log's zone lines drive the dropdown while Follow is on.
        if (_settings.SpawnFollowZone && _vm.CurrentZoneName is { } here && here != _lastFollowedZone)
        {
            _lastFollowedZone = here;
            if (here != SelectedZone) SelectZone(here);
        }

        var zone = SelectedZone;
        _titleText.Text = zone.Length > 0 ? $"🕒 Spawns - {zone}" : "🕒 Spawns";
        if (zone.Length == 0) return;

        var now = DateTime.Now;
        _rows = _vm.RowsFor(zone, now);
        // U+0001 rather than an empty joiner: a separator no mob name can contain, so
        // two different row lists cannot collapse to the same signature and skip a rebuild.
        var signature = zone + "\u0001" + string.Join("\u0001",
            _rows.Select(r => $"{r.DisplayName}|{r.HasActiveTimer}|{r.IsDue}|{r.DurationText}|{r.Alert}|{r.SoundName}|{r.IsCustom}"));
        if (signature != _signature)
        {
            // Never rebuild under someone's cursor — committing the edit refreshes anyway.
            // Kick() overrides: it fires from a click the player just made, and the button
            // they clicked still holds focus, so the guard would swallow their own edit.
            if (!_forceRebuild && _rowsPanel.IsKeyboardFocusWithin) return;
            _signature = signature;
            Rebuild();
        }
        else
        {
            for (var i = 0; i < _rows.Count && i < _countdowns.Count; i++)
                _countdowns[i].Text = _rows[i].CountdownText;
        }
    }

    private void Rebuild()
    {
        _rowsPanel.Children.Clear();
        _countdowns.Clear();

        if (_rows.Count == 0)
        {
            _rowsPanel.Children.Add(new TextBlock
            {
                Text = "No named catalogued for this zone yet — add one below.",
                FontSize = 11,
                Opacity = 0.65,
                Foreground = AppTheme.TextBrush,
                Margin = new Thickness(0, 4, 0, 4),
            });
            return;
        }

        foreach (var row in _rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(72)));    // countdown
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(70)));    // duration
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(60)));    // ago
            // Fixed, not Auto: the buttons a row carries change as timers start and stop,
            // and an Auto lane would resize the whole row underneath the boxes when it did.
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(232)));   // buttons

            var name = new TextBlock
            {
                Text = row.DisplayName,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = AppTheme.TextBrush,
            };
            if (row.Detail.Length > 0) ToolTip.SetTip(name, row.Detail);
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var countdown = new TextBlock
            {
                Text = row.CountdownText,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = row.IsDue ? AppTheme.WarnBrush : AppTheme.AccentBrush,
            };
            Grid.SetColumn(countdown, 1);
            grid.Children.Add(countdown);
            _countdowns.Add(countdown);

            var duration = DarkBox(row.DurationText,
                "Respawn time: 22 (minutes), 90s, 12h, 3d, 6:40 — edits persist as yours");
            duration.Margin = new Thickness(0, 0, 6, 0);
            duration.LostFocus += (_, _) => CommitDuration(duration, row);
            duration.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitDuration(duration, row); };
            Grid.SetColumn(duration, 2);
            grid.Children.Add(duration);

            var ago = DarkBox("", "Died how long ago? (5m, 90s) Empty = just now");
            ago.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(ago, 3);
            grid.Children.Add(ago);

            // Left-aligned inside the fixed lane, so a row with fewer buttons keeps the
            // ones it has in the same place as every other row.
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            buttons.Children.Add(RowButton("▶", "Start the countdown from a kill you saw yourself",
                () => { _vm.StartNow(row.Zone, row.Name, ago.Text ?? ""); Kick(); }));
            var bell = RowButton(row.Alert ? "🔔" : "🔕",
                "Sound when this one comes due — off by default, like watch-rule sounds (the chicklet shows DUE either way)",
                () => { _vm.ToggleAlert(row.Zone, row.Name); Kick(); });
            bell.Opacity = row.Alert ? 1.0 : 0.45;
            buttons.Children.Add(bell);
            buttons.Children.Add(BuildSoundPicker(row));
            if (row.HasActiveTimer)
                buttons.Children.Add(RowButton("✕", "Forget this countdown",
                    () => { _vm.ClearTimer(row.Zone, row.Name); Kick(); }));
            if (row.IsCustom)
                buttons.Children.Add(RowButton("🗑", "Remove this named (you added it)",
                    () => { _vm.RemoveCustom(row.Zone, row.Name); Kick(); }));
            Grid.SetColumn(buttons, 4);
            grid.Children.Add(buttons);

            _rowsPanel.Children.Add(grid);
        }
    }

    /// <summary>This named's own due sound — the watch-rule scheme: Default follows the
    /// shared choice at the bottom, Off silences just this one, Custom… takes a file.
    /// Different camps with different sounds is how the ear knows which one popped.</summary>
    private ComboBox BuildSoundPicker(SpawnRow row)
    {
        var combo = new ComboBox
        {
            FontSize = 10,
            Width = 78,
            Margin = new Thickness(4, 0, 0, 0),
        };
        foreach (var item in (string[])["Default", "Off", .. AlertSoundCatalog.Names, "Custom…"])
            combo.Items.Add(item);
        var isCustomFile = row.SoundName.Length > 0
            && !string.Equals(row.SoundName, "Off", StringComparison.OrdinalIgnoreCase)
            && !AlertSoundCatalog.Names.Contains(row.SoundName, StringComparer.OrdinalIgnoreCase);
        combo.SelectedItem = row.SoundName.Length == 0 ? "Default"
            : isCustomFile ? "Custom…"
            : combo.Items.Cast<string>().First(i => string.Equals(i, row.SoundName, StringComparison.OrdinalIgnoreCase));
        ToolTip.SetTip(combo, isCustomFile
            ? $"Custom: {row.SoundName}"
            : "Sound for this named — Default is Alarm");

        var ready = false;
        combo.SelectionChanged += async (_, _) =>
        {
            if (!ready || combo.SelectedItem is not string choice) return;
            switch (choice)
            {
                case "Default":
                    _vm.SetSound(row.Zone, row.Name, "");
                    break;
                case "Off":
                    _vm.SetSound(row.Zone, row.Name, "Off");
                    break;
                case "Custom…":
                    // Avalonia has no modal Win32 dialog: the storage provider picker is
                    // async, so Kick() below lands after the await. Cancelling changes
                    // nothing, and Kick's forced rebuild snaps the combo back to what the
                    // named actually has — the WPF behaviour, arrived at differently.
                    var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = $"Choose a sound for \"{row.Name}\"",
                        AllowMultiple = false,
                        FileTypeFilter =
                        [
                            new FilePickerFileType("Sound files") { Patterns = ["*.wav", "*.mp3", "*.ogg"] },
                            FilePickerFileTypes.All,
                        ],
                    });
                    if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
                    {
                        _vm.SetSound(row.Zone, row.Name, path);
                        _main.PlayAlertSound(path);
                    }
                    break;
                default:
                    _vm.SetSound(row.Zone, row.Name, choice);
                    _main.PlayAlertSound(choice);   // hear it as you pick it
                    break;
            }
            Kick();
        };
        ready = true;
        return combo;
    }

    private void CommitDuration(TextBox box, SpawnRow row)
    {
        var before = row.DurationText;
        if ((box.Text ?? "").Trim() == before) return;
        _vm.SetDuration(row.Zone, row.Name, box.Text ?? "");
        Kick();
    }

    /// <summary>Rebuild right now, even though focus sits in the panel.</summary>
    private void Kick()
    {
        // Re-entrancy: the rebuild tears down the very TextBox that may be focused, whose
        // LostFocus commits and calls back in here. WPF got away with it via
        // Keyboard.ClearFocus() ordering; Avalonia needs it said out loud.
        if (_kicking) return;
        _kicking = true;
        try
        {
            _signature = "";
            // Avalonia's IFocusManager exposes no ClearFocus(); parking focus on the frame
            // is the equivalent and gets it out of the row about to be replaced.
            _chrome.Focus();
            _forceRebuild = true;
            RefreshRows();
        }
        finally
        {
            _forceRebuild = false;
            _kicking = false;
        }
    }

    private static TextBox DarkBox(string text, string tooltip)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = 11,
            Padding = new Thickness(3, 1, 3, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = AppTheme.ComboBoxBrush,
            Foreground = AppTheme.TextBrush,
            BorderBrush = AppTheme.BorderBrush,
        };
        ToolTip.SetTip(box, tooltip);
        return box;
    }

    private static Button RowButton(string glyph, string tooltip, Action onClick)
    {
        var b = AppTheme.IconButton(glyph, tooltip);
        b.FontSize = 11;
        b.Margin = new Thickness(4, 0, 0, 0);
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---- chrome ----

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        // Anything you can click or type into keeps its own click; the rest of the frame
        // drags the window (same test Options uses).
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(IsInteractiveControl))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // Deliberately NOT ScrollViewer: WPF only spared TextBoxes, so a press on the list's
    // empty space drags the window. Dropping that would cost the only large grab area
    // this frame has.
    private static bool IsInteractiveControl(Visual visual) => visual is
        Button or TextBox or ComboBox or CheckBox or ToggleButton or ScrollBar;

    private void OnZonePicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingZone) return;
        // A null/empty selection can arrive from teardown paths, not from the user —
        // 1.20.0 let one of those persist state and silently killed zone-following.
        if (SelectedZone.Length == 0) return;
        _settings.SpawnZone = SelectedZone;
        _settings.Save();
        _signature = "";
        RefreshRows();
    }

    private void OnFollowChanged(object? sender, EventArgs e)
    {
        if (_syncingZone) return;
        _settings.SpawnFollowZone = _followCheck.IsChecked == true;
        _settings.Save();
        RefreshRows();
    }

    private void OnAddCustom()
    {
        if (_vm.AddCustom(SelectedZone, _addNameBox.Text ?? "", _addDurationBox.Text ?? ""))
        {
            _addNameBox.Text = "";
            _addDurationBox.Text = "";
            Kick();
        }
    }

    private void ApplyHeightLimit(Screen? screen)
    {
        if (screen is null) return;
        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        MaxHeight = Math.Max(240, workingHeight - 40);
        _bodyScroll.MaxHeight = Math.Max(160, workingHeight - 220);
    }

    private void PositionFromSettings()
    {
        var left = _settings.SpawnLeft;
        var top = _settings.SpawnTop;
        if (!ScreenGuard.OnScreen(this, left, top, Width, Height))
        {
            // First use, or the saved monitor is gone: near the top-left of the work area
            // (WPF parity — left+40, top 80).
            var work = _main.Screens.ScreenFromWindow(_main)?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            left = work.X + 40;
            top = 80;
        }

        Position = new PixelPoint((int)left, (int)top);
    }
}
