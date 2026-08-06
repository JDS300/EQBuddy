using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Avalonia;

public sealed class OptionsWindow : Window
{
    private readonly MainWindow _main;
    private readonly TextBlock _scaleLabel = LabelValue();
    private readonly TextBlock _bgOpacityLabel = LabelValue();
    private readonly TextBlock _opacityLabel = LabelValue();
    private readonly Slider _scaleSlider = Slider(0.8, 1.6, 0.05);
    private readonly Slider _bgOpacitySlider = Slider(0.15, 1.0, 0.05);
    private readonly Slider _opacitySlider = Slider(0.5, 1.0, 0.02);
    private readonly CheckBox _truncateCheck = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _tutorialCheck = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _pinChipsCheck = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _trackSpawnsCheck = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _selfHotCheck = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly ComboBox _themeCombo = new() { Width = 130, FontSize = 12 };
    private readonly ComboBox _windowCombo = new() { Width = 90, FontSize = 12 };
    private readonly ComboBox _soundCombo = new() { Width = 120, FontSize = 12 };
    private readonly TextBlock _soundFileNote = AppTheme.DimText("");
    private readonly StackPanel _rulesPanel = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _guideToggle = AppTheme.IconButton("> Show examples", "Worked examples for every rule kind");
    private readonly Border _guidePanel = new()
    {
        IsVisible = false,
        Margin = new Thickness(0, 4, 0, 2),
        Background = AppTheme.PanelBrush,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 6),
    };
    private readonly StackPanel _cardsPanel = new();
    // Only the body scrolls — the title row and its close button live outside this and
    // stay reachable no matter how tall the watch-rules section grows. See ApplyHeightLimit.
    private readonly ScrollViewer _bodyScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private bool _ready;

    private static readonly string[] SoundNames = Array.ConvertAll(MainWindow.AlertSounds, x => x.Name);

    /// <summary>Width of the "Watch" column. 92 clipped the widest kind, "Spell fade", to
    /// "Spell fad" at the real desktop font — headless measured it a hair narrower, so no
    /// test saw it.</summary>
    private const double KindColumnWidth = 106;

    /// <summary>Width of the settings column. Driven by the watch-rule row, the widest
    /// thing in this window and the one whose columns are unforgiving: 106 (kind) + 115
    /// (name) + the match cell + 236 for the five auto columns that follow it (P, B, sound,
    /// delay, delete). At the old 520 the match cell was left 77px — narrower than the
    /// class combo's own 104px minimum — so a Spell fade row spilled its combo and its
    /// match box sideways out of the cell, where the toggles and the sound picker painted
    /// over them and swallowed every click: the box was visible but impossible to type in.
    /// This leaves the cell ~223px: the combo, plus a match box wide enough to read a real
    /// spell name in ("Clarity", "Color Shift") rather than one that technically accepts
    /// typing. Anything added to a rule row has to come back through this number.</summary>
    private const double BodyWidth = 680;

    public OptionsWindow(MainWindow main)
    {
        _main = main;
        Title = "EQBuddy Options";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new Border
        {
            Background = AppTheme.BgBrush,
            CornerRadius = new CornerRadius(10),
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            Child = BuildChrome(),
        };
        PointerPressed += OnDrag;
        _scaleSlider.Value = main.UiScale;
        _opacitySlider.Value = main.WidgetOpacity;
        _bgOpacitySlider.Value = main.BackgroundOpacityValue;
        Subscribe(_scaleSlider, () => _main.SetUiScale(_scaleSlider.Value));
        Subscribe(_bgOpacitySlider, () => _main.SetBackgroundOpacity(_bgOpacitySlider.Value));
        Subscribe(_opacitySlider, () => _main.SetWindowOpacity(_opacitySlider.Value));

        _truncateCheck.Content = new TextBlock
        {
            Text = "Auto-empty finished-session logs",
            FontSize = 12,
            Foreground = AppTheme.TextBrush,
        };
        _truncateCheck.IsChecked = main.TruncateLogsValue;
        _truncateCheck.IsCheckedChanged += (_, _) =>
        {
            if (_ready) _main.SetTruncateLogs(_truncateCheck.IsChecked == true);
        };

        _tutorialCheck.Content = new TextBlock
        {
            Text = "Show quick tutorial at launch",
            FontSize = 12,
            Foreground = AppTheme.TextBrush,
        };
        _tutorialCheck.IsChecked = main.Settings.ShowTutorial;
        _tutorialCheck.IsCheckedChanged += (_, _) =>
        {
            if (!_ready) return;
            _main.Settings.ShowTutorial = _tutorialCheck.IsChecked == true;
            _main.PersistSettings();
        };

        _trackSpawnsCheck.Content = new TextBlock
        {
            Text = "🕒 Track spawns (named respawn timers)",
            FontSize = 12,
            Foreground = AppTheme.TextBrush,
        };
        _trackSpawnsCheck.IsChecked = main.Settings.TrackSpawns;
        _trackSpawnsCheck.IsCheckedChanged += (_, _) =>
        {
            // Routed through MainWindow, not the settings object: the setting, the
            // right-click menu check, and the windows themselves all move together.
            if (_ready) _main.SetTrackSpawns(_trackSpawnsCheck.IsChecked == true);
        };

        _selfHotCheck.Content = new TextBlock
        {
            Text = "🌿 Show heal-over-time chips for myself",
            FontSize = 12,
            Foreground = AppTheme.TextBrush,
        };
        _selfHotCheck.IsChecked = main.Settings.ShowSelfHotChips;
        _selfHotCheck.IsCheckedChanged += (_, _) =>
        {
            // Straight to settings: nothing else mirrors this one, and the next 1 s tick
            // rebuilds the stack from HotChips, so unticking it drops the self chip within
            // a second (and closes the stack outright if yours was the only HoT running).
            if (!_ready) return;
            _main.Settings.ShowSelfHotChips = _selfHotCheck.IsChecked == true;
            _main.PersistSettings();
        };

        _pinChipsCheck.Content = new TextBlock
        {
            Text = "📌 Show watch chips in the mini dashboard",
            FontSize = 12,
            Foreground = AppTheme.TextBrush,
        };
        _pinChipsCheck.IsChecked = main.Settings.PinWatchChips;
        _pinChipsCheck.IsCheckedChanged += (_, _) =>
        {
            if (!_ready) return;
            _main.Settings.PinWatchChips = _pinChipsCheck.IsChecked == true;
            _main.PersistSettings();
        };

        foreach (var label in OptionsViewModel.ThemeLabels) _themeCombo.Items.Add(label);
        _themeCombo.SelectedIndex = ThemeCatalog.IndexOf(main.Settings.Theme);
        _themeCombo.SelectionChanged += OnThemeChanged;

        foreach (var m in (int[])[5, 15, 30]) _windowCombo.Items.Add($"{m} min");
        _windowCombo.SelectedIndex = main.Settings.RecentWindowMinutes switch { 5 => 0, 30 => 2, _ => 1 };
        _windowCombo.SelectionChanged += (_, _) =>
        {
            if (!_ready) return;
            _main.Settings.RecentWindowMinutes = _windowCombo.SelectedIndex switch { 0 => 5, 2 => 30, _ => 15 };
            _main.PersistSettings();
        };

        foreach (var name in SoundNames) _soundCombo.Items.Add($"{name}{(name == "Ding" ? " (default)" : "")}");
        _soundCombo.Items.Add("Custom file...");
        var current = main.Settings.AlertSound switch
        {
            "Asterisk" or "" => "Ding",
            "Beep" => "Chord",
            "Hand" => "Chimes",
            "Question" => "Notify",
            { } other => other,
        };
        var idx = Array.IndexOf(SoundNames, current);
        _soundCombo.SelectedIndex = idx >= 0 ? idx : SoundNames.Length;
        _soundCombo.SelectionChanged += OnSoundChanged;
        UpdateSoundFileNote();

        BuildRulesEditor();
        BuildCardsEditor();
        // Restore before _ready so this doesn't count as the user changing it.
        ToggleGuide(main.Settings.ShowWatchGuide, persist: false);
        UpdateLabels();

        // Ceiling, not a fixed height — SizeToContent still shrinks the window for short
        // content. Applied now against the owner's screen (this window has no compositor
        // surface of its own until it's shown) and again on Opened once we know which
        // monitor it actually landed on — same two-step as SpawnsWindow.
        ApplyHeightLimit(main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary);
        Opened += (_, _) => ApplyHeightLimit(Screens.ScreenFromWindow(this) ?? Screens.Primary);

        _ready = true;
    }

    /// <summary>Title row (fixed) over a scrolling body — the split that keeps the close
    /// button reachable no matter how far the watch-rules section grows underneath it.</summary>
    private Control BuildChrome()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        grid.Children.Add(BuildTitleRow());

        _bodyScroll.Content = BuildBody();
        Grid.SetRow(_bodyScroll, 1);
        grid.Children.Add(_bodyScroll);

        return grid;
    }

    private Control BuildTitleRow()
    {
        var title = new Grid { Margin = new Thickness(16, 16, 16, 10) };
        title.Children.Add(new TextBlock
        {
            Text = "Options",
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Foreground = AppTheme.AccentBrush,
        });
        var close = AppTheme.IconButton("x", "Close");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        title.Children.Add(close);
        return title;
    }

    private Control BuildBody()
    {
        var panel = new StackPanel { Margin = new Thickness(16, 0, 16, 16), Width = BodyWidth };

        panel.Children.Add(Row("Theme", _themeCombo, new Thickness(0, 0, 0, 12)));

        AddSlider(panel, "Widget size", _scaleLabel, _scaleSlider);
        AddSlider(panel, "Background see-through", _bgOpacityLabel, _bgOpacitySlider,
            "Only the dark panel fades; text stays sharp.");
        AddSlider(panel, "Whole-widget opacity", _opacityLabel, _opacitySlider,
            "Fades everything, text included.");
        panel.Children.Add(_truncateCheck);
        panel.Children.Add(AppTheme.DimText(
            "Turn off if you upload your log files elsewhere - they will grow forever, so clean them up yourself occasionally.",
            new Thickness(20, 2, 0, 0)));
        panel.Children.Add(_tutorialCheck);

        panel.Children.Add(_trackSpawnsCheck);
        panel.Children.Add(AppTheme.DimText(
            "Kill a named - or its placeholder - and a small countdown chicklet appears (⏳ Asaka L`Rei 3:12). Chicklets stack, drag anywhere as one, show every timer you have running in any zone, and flip to DUE for a minute (click to dismiss sooner). Double-click one (or right-click → Spawn timers...) for the full zone list, which follows you zone to zone. We captured the respawn times we could from community sources - if you notice a discrepancy in game, type over the duration: your number wins and survives updates.",
            new Thickness(20, 2, 0, 0)));

        panel.Children.Add(_selfHotCheck);
        panel.Children.Add(AppTheme.DimText(
            "Cast a heal-over-time and a small countdown chicklet appears (🌿 Daggo 0:18), so you know when it stops ticking and can recast in time. The chips stack, drag anywhere as one, and switch to the warning colour for the last few seconds. Your own buff bar already shows your HoT on yourself, so turn this off if that chip is just in the way - chips on everyone else stay. Either way, the chip on you is tinted with the healing colour, to tell it apart at a glance from the ones on people whose buff bar you cannot see.",
            new Thickness(20, 2, 0, 0)));

        panel.Children.Add(Row("Recent-rate window", _windowCombo, new Thickness(0, 12, 0, 0)));
        panel.Children.Add(AppTheme.DimText("The Last Xm figures on Combat, Kills, Money, and Progress."));

        panel.Children.Add(Heading("Watch rules", new Thickness(0, 14, 0, 2)));
        panel.Children.Add(AppTheme.DimText("Watch loot, kills, skill-ups, deaths, milestones, your spells wearing off, or any text in the log. Match is a case-insensitive substring, e.g. 'mote'; when empty, the display name is used. Spell fade rules can pick a whole class (Any crowd control, Charm, Mez, Root, Lull, Stun, HoT) instead of a named spell, needing no match text. Delay holds the alert back that many seconds so it lands as a cue. B shows a banner and S plays a sound."));

        // Collapsed by default — the examples answer the questions people actually ask, and
        // are noise for anyone who already knows the answers.
        _guideToggle.Click += (_, _) => ToggleGuide(!_guidePanel.IsVisible, persist: true);
        panel.Children.Add(_guideToggle);
        panel.Children.Add(_guidePanel);

        panel.Children.Add(_rulesPanel);
        var add = AppTheme.IconButton("+ Add watch rule", "Add watch rule");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.FontSize = 12;
        add.Click += (_, _) =>
        {
            _main.Settings.TrackedRules.Add(new TrackedRule { Name = "", Pattern = "" });
            _main.PersistSettings();
            BuildRulesEditor();
            ReclampHeight();
        };
        panel.Children.Add(add);
        panel.Children.Add(_pinChipsCheck);

        var soundRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        soundRow.Children.Add(_soundCombo);
        var test = AppTheme.IconButton(">", "Play the alert sound");
        test.Margin = new Thickness(4, 0, 0, 0);
        test.Click += (_, _) => _main.PlayAlertSound();
        soundRow.Children.Add(test);
        panel.Children.Add(Row("Alert sound", soundRow, new Thickness(0, 8, 0, 0)));
        panel.Children.Add(_soundFileNote);
        panel.Children.Add(AppTheme.DimText(
            "While Options is open, the ★ alert banner tile is visible — drag it to where alerts should appear. During play it's click-through and never steals focus.",
            new Thickness(0, 4, 0, 0)));
        // The tile is draggable ONLY while Options is open and click-through the rest of the
        // time, so one parked on a monitor you no longer use has nothing to grab. Without
        // this button the only way back is hand-editing settings.json.
        var recall = AppTheme.IconButton("Bring the alert tile back next to the widget",
            "Move the ★ tile beside the widget — use when it's on a monitor you can't reach");
        recall.HorizontalAlignment = HorizontalAlignment.Left;
        recall.Margin = new Thickness(0, 6, 0, 0);
        recall.Click += (_, _) => _main.AlertTile.ResetPosition();
        panel.Children.Add(recall);

        panel.Children.Add(Heading("Overlay cards", new Thickness(0, 14, 0, 2)));
        panel.Children.Add(_cardsPanel);
        panel.Children.Add(AppTheme.DimText(
            $"Hotkeys (global, editable in settings.json):\n{_main.Settings.HotkeyToggleOverlay} show/hide - {_main.Settings.HotkeyClickThrough} click-through - {_main.Settings.HotkeyMiniMode} mini - {_main.Settings.HotkeyCampMarker} camp marker",
            new Thickness(0, 14, 0, 0)));
        panel.Children.Add(AppTheme.DimText("Size also scales all text. Changes apply instantly and are saved.",
            new Thickness(0, 8, 0, 0)));
        return panel;
    }

    /// <summary>Called back by MainWindow.SetTrackSpawns so toggling the right-click menu
    /// (or the feature turning itself off) updates this checkbox while Options sits open.
    /// The _ready flag is dropped around the write so the sync doesn't read as the user
    /// clicking and bounce straight back into MainWindow.</summary>
    internal void SyncTrackSpawns(bool on)
    {
        var wasReady = _ready;
        _ready = false;
        _trackSpawnsCheck.IsChecked = on;
        _ready = wasReady;
    }

    /// <summary>Show or hide the worked examples, remembering the choice. Built on first
    /// expand rather than up front — most people never open it.</summary>
    private void ToggleGuide(bool open, bool persist)
    {
        _guidePanel.IsVisible = open;
        _guideToggle.Content = open ? "v Hide examples" : "> Show examples";
        if (open && _guidePanel.Child is null) _guidePanel.Child = BuildGuide();
        if (persist && _ready)
        {
            _main.Settings.ShowWatchGuide = open;
            _main.PersistSettings();
        }
        // The guide is exactly the content that used to push the close button off-screen —
        // re-pin the ceiling now that it just changed size. (Window.MaxHeight already holds
        // once set, so this is belt-and-braces against layout quirks rather than strictly
        // load-bearing — cheap, and it's the case the bug report was actually about.)
        // Gated on _ready for the same reason persist is: the constructor's own restore
        // call runs before this window has a platform surface, and the real ApplyHeightLimit
        // call that follows it (against the owner's screen) already covers that case.
        if (_ready) ReclampHeight();
    }

    private static Control BuildGuide()
    {
        var panel = new StackPanel();
        TextBlock Line(string text, IBrush brush, double top, bool bold = false) => new()
        {
            Text = text, FontSize = 11, Foreground = brush, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 0),
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        };

        panel.Children.Add(Line("How matching works", AppTheme.AccentBrush, 0, bold: true));
        foreach (var basic in WatchGuide.Basics)
            panel.Children.Add(Line("- " + basic, AppTheme.DimBrush, 2));

        panel.Children.Add(Line("Examples", AppTheme.AccentBrush, 8, bold: true));
        foreach (var ex in WatchGuide.Examples)
        {
            var match = ex.Match.Length > 0 ? $"Match \"{ex.Match}\"" : "no match text";
            var delay = ex.Delay.Length > 0 ? $" - Delay {ex.Delay}" : "";
            panel.Children.Add(Line(
                $"{OptionsViewModel.KindNames[(int)ex.Kind]} - \"{ex.Name}\" - {match}{delay}",
                AppTheme.TextBrush, 8));
            panel.Children.Add(Line(ex.What, AppTheme.DimBrush, 1));
        }
        return panel;
    }

    private void BuildRulesEditor()
    {
        _rulesPanel.Children.Clear();
        foreach (var rule in _main.Settings.TrackedRules)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(KindColumnWidth)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(115)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var i = 0; i < 5; i++)   // pin, banner, sound, delay, delete
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Wiring SelectionChanged is deferred past the matchArea block below: the handler
            // calls SyncMatchArea(), a local function that closes over spellFilter and
            // pattern, and the compiler demands those be definitely assigned at the point the
            // closure is written — even though it only actually runs later, on selection.
            var kind = new ComboBox { FontSize = 11, Margin = new Thickness(0, 0, 4, 0) };
            foreach (var k in OptionsViewModel.KindNames) kind.Items.Add(k);
            kind.SelectedIndex = (int)rule.Kind;
            ToolTip.SetTip(kind, "What this rule watches");
            row.Children.Add(kind);

            var name = DarkBox(rule.Name, "Display name (also used as match text when the optional filter is empty)");
            name.PlaceholderText = "Display name";
            name.Margin = new Thickness(0, 0, 4, 0);
            name.LostFocus += (_, _) => { rule.Name = (name.Text ?? "").Trim(); _main.PersistSettings(); };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            // Column 2 holds the match text, preceded (for Spell fade rules) by a class
            // picker: one named spell, or a whole class that keeps working as the
            // character levels into new spells and ranks. Nested in its own Grid, matching
            // the WPF layout, so the combo can claim the whole cell when the text box hides.
            var matchArea = new Grid();
            matchArea.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            matchArea.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(matchArea, 2);
            row.Children.Add(matchArea);

            var spellFilter = new ComboBox
            {
                FontSize = 11,
                MinWidth = 104,
                Margin = new Thickness(0, 0, 4, 0),
            };
            foreach (var f in OptionsViewModel.SpellFilterNames) spellFilter.Items.Add(f);
            spellFilter.SelectedIndex = (int)rule.SpellFilter;
            ToolTip.SetTip(spellFilter, "Watch one named spell, or a whole class of spells");
            matchArea.Children.Add(spellFilter);

            var pattern = DarkBox(rule.Pattern, "Optional case-insensitive match text; uses the display name when empty, and may be empty for Death or Milestone");
            pattern.PlaceholderText = "Match text (optional)";
            pattern.Margin = new Thickness(0, 0, 4, 0);
            pattern.LostFocus += (_, _) => { rule.Pattern = (pattern.Text ?? "").Trim(); _main.PersistSettings(); };
            Grid.SetColumn(pattern, 1);
            matchArea.Children.Add(pattern);

            // A class filter needs no match text, so the box goes away rather than sitting
            // there inviting input that would be ignored.
            void SyncMatchArea()
            {
                var isFade = rule.Kind == WatchKind.SpellFade;
                var byName = rule.SpellFilter == SpellFilter.ByName;
                spellFilter.IsVisible = isFade;
                pattern.IsVisible = !(isFade && !byName);
                // With no match box beside it the combo takes the whole cell, so its text
                // and drop arrow stay inside the row instead of running under the toggles.
                Grid.SetColumnSpan(spellFilter, isFade && !byName ? 2 : 1);
            }
            SyncMatchArea();

            kind.SelectionChanged += (_, _) =>
            {
                if (!_ready || kind.SelectedIndex < 0) return;
                rule.Kind = (WatchKind)kind.SelectedIndex;
                SyncMatchArea();
                _main.PersistSettings();
            };
            spellFilter.SelectionChanged += (_, _) =>
            {
                if (!_ready || spellFilter.SelectedIndex < 0) return;
                rule.SpellFilter = (SpellFilter)spellFilter.SelectedIndex;
                SyncMatchArea();
                _main.PersistSettings();
            };

            row.Children.Add(RuleToggle("P", "Show this rule as a chip in the mini dashboard", 3,
                rule.Pinned, v => rule.Pinned = v));
            row.Children.Add(RuleToggle("B", "Banner alert on match", 4, rule.AlertBanner, v => rule.AlertBanner = v));

            // Per-rule sound, replacing the old on/off toggle. Telling rules apart by ear is
            // the entire point — and it matters most for delayed alerts, where the usual
            // setup is two rules on one match ("heard it" now, "cast now" later) that are
            // indistinguishable if they share a sound.
            var sound = new ComboBox
            {
                FontSize = 11,
                MinWidth = 86,
                Margin = new Thickness(0, 0, 4, 0),
            };
            foreach (var choice in AlertSoundCatalog.RuleChoices) sound.Items.Add(choice);
            sound.SelectedIndex = AlertSoundCatalog.RuleChoiceIndex(rule);
            ToolTip.SetTip(sound, AlertSoundCatalog.IsCustom(rule.AlertSoundName) && rule.AlertSoundName.Length > 0
                ? $"Custom: {rule.AlertSoundName}"
                : "Sound for this rule - pick a different one per rule to tell them apart by ear");
            sound.SelectionChanged += async (_, _) =>
            {
                if (!_ready || sound.SelectedIndex < 0) return;
                if (AlertSoundCatalog.ApplyRuleChoice(rule, sound.SelectedIndex))
                {
                    var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = $"Choose a sound for \"{(rule.Name.Length > 0 ? rule.Name : rule.Pattern)}\"",
                        AllowMultiple = false,
                        FileTypeFilter = [new FilePickerFileType("Sound files") { Patterns = ["*.wav", "*.mp3", "*.ogg"] }],
                    });
                    if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
                    {
                        rule.AlertSoundName = path;
                        ToolTip.SetTip(sound, $"Custom: {path}");
                    }
                    else
                    {
                        // Cancelled — snap back to what the rule already had.
                        _ready = false;
                        sound.SelectedIndex = AlertSoundCatalog.RuleChoiceIndex(rule);
                        _ready = true;
                        return;
                    }
                }
                _main.PersistSettings();
                // Play it straight away, so picking a sound is a decision you can hear.
                if (AlertSoundCatalog.Resolve(rule, _main.Settings.AlertSound) is { } preview)
                    _main.PlayAlertSound(preview);
            };
            Grid.SetColumn(sound, 5);
            row.Children.Add(sound);

            // Seconds to hold the alert back — 0 (or empty) is the immediate behaviour.
            // Turns a rule into a cue: sound 2.5 s after a heal-chain call to say "cast
            // now", or 25 s after a mez to say "recast before it breaks".
            var delay = DarkBox(DelayText.Format(rule.AlertDelaySeconds),
                "Wait this long before alerting (empty = at once, up to 30 minutes). " +
                "Seconds by default; add m for minutes - 2.5, 25, 8m, 1:30. " +
                "The count updates immediately either way - only the alert waits.");
            delay.PlaceholderText = "0s";
            delay.Width = 48;
            delay.Margin = new Thickness(0, 0, 4, 0);
            delay.TextAlignment = global::Avalonia.Media.TextAlignment.Right;
            delay.LostFocus += (_, _) =>
            {
                rule.AlertDelaySeconds = DelayText.Parse(delay.Text);
                delay.Text = DelayText.Format(rule.AlertDelaySeconds);
                _main.PersistSettings();
            };
            Grid.SetColumn(delay, 6);
            row.Children.Add(delay);

            var del = AppTheme.IconButton("x", "Delete rule");
            del.Click += (_, _) =>
            {
                _main.Settings.TrackedRules.Remove(rule);
                _main.PersistSettings();
                BuildRulesEditor();
                ReclampHeight();
            };
            Grid.SetColumn(del, 7);
            row.Children.Add(del);
            _rulesPanel.Children.Add(row);
        }
    }

    private ToggleButton RuleToggle(string glyph, string tip, int column, bool initial, Action<bool> apply)
    {
        var t = AppTheme.IconToggle(glyph, tip);
        t.IsChecked = initial;
        t.IsCheckedChanged += (_, _) =>
        {
            apply(t.IsChecked == true);
            _main.PersistSettings();
        };
        Grid.SetColumn(t, column);
        return t;
    }

    private void BuildCardsEditor()
    {
        _cardsPanel.Children.Clear();
        var order = _main.Settings.SectionOrder.ToList();
        foreach (var (key, _) in MainWindow.SectionCatalog)
            if (!order.Contains(key)) order.Add(key);
        _main.Settings.SectionOrder = order;

        foreach (var key in order)
        {
            var title = MainWindow.SectionCatalog.First(c => c.Key == key).Title;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var i = 0; i < 3; i++) row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var hidden = _main.Settings.HiddenSections.Contains(key);
            row.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = hidden ? AppTheme.DimBrush : AppTheme.TextBrush,
            });
            row.Children.Add(CardButton("^", "Move up", 1, () => MoveCard(key, -1), compact: true));
            row.Children.Add(CardButton("v", "Move down", 2, () => MoveCard(key, 1), compact: true));
            row.Children.Add(CardButton(hidden ? "Show" : "Hide", hidden ? "Show card" : "Hide card", 3, () =>
            {
                if (!_main.Settings.HiddenSections.Remove(key))
                    _main.Settings.HiddenSections.Add(key);
                ApplyCards();
            }));
            _cardsPanel.Children.Add(row);
        }
    }

    private Button CardButton(string text, string tip, int column, Action action, bool compact = false)
    {
        var b = AppTheme.IconButton(text, tip);
        b.FontSize = 12;
        b.Width = compact ? 28 : 48;
        b.MinWidth = b.Width;
        b.Height = 26;
        b.MinHeight = 26;
        b.Padding = new Thickness(0);
        b.Margin = new Thickness(column == 1 ? 0 : 4, 0, 0, 0);
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        b.VerticalContentAlignment = VerticalAlignment.Center;
        b.VerticalAlignment = VerticalAlignment.Center;
        b.Click += (_, _) => action();
        Grid.SetColumn(b, column);
        return b;
    }

    private void MoveCard(string key, int delta)
    {
        var order = _main.Settings.SectionOrder;
        var i = order.IndexOf(key);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        ApplyCards();
    }

    private void ApplyCards()
    {
        _main.PersistSettings();
        _main.ApplySectionLayout();
        BuildCardsEditor();
    }

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _themeCombo.SelectedIndex < 0) return;
        _main.Settings.Theme = ThemeCatalog.Themes[_themeCombo.SelectedIndex].Key;
        _main.PersistSettings();
        AppTheme.Apply(_main.Settings);
        _main.RefreshTheme();
    }

    private async void OnSoundChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (_soundCombo.SelectedIndex < SoundNames.Length)
        {
            _main.Settings.AlertSound = SoundNames[_soundCombo.SelectedIndex];
        }
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose an alert sound",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Sound files") { Patterns = ["*.wav", "*.mp3", "*.ogg"] },
                    FilePickerFileTypes.All,
                ],
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is not null)
                _main.Settings.AlertSound = path;
        }
        _main.PersistSettings();
        UpdateSoundFileNote();
        _main.PlayAlertSound();
    }

    private void UpdateSoundFileNote()
    {
        var custom = Array.IndexOf(SoundNames, _main.Settings.AlertSound) < 0;
        _soundFileNote.Text = custom ? $"Custom: {_main.Settings.AlertSound}" : "";
        _soundFileNote.IsVisible = custom;
    }

    private static TextBox DarkBox(string text, string tip)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = 12,
            Background = AppTheme.ComboBoxBrush,
            Foreground = AppTheme.TextBrush,
            BorderBrush = AppTheme.BorderBrush,
            Padding = new Thickness(4, 2),
        };
        ToolTip.SetTip(box, tip);
        return box;
    }

    private static TextBlock Heading(string text, Thickness margin) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = AppTheme.AccentBrush,
        Margin = margin,
    };

    private static Control Row(string label, Control control, Thickness? margin = null)
    {
        var row = new Grid { Margin = margin ?? default };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = AppTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static void AddSlider(Panel panel, string label, TextBlock value, Slider slider, string? hint = null)
    {
        panel.Children.Add(Row(label, value));
        if (hint is not null) panel.Children.Add(AppTheme.DimText(hint));
        slider.Margin = new Thickness(0, 4, 0, 12);
        panel.Children.Add(slider);
    }

    private void Subscribe(Slider slider, Action apply)
    {
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property != RangeBase.ValueProperty || !_ready) return;
            apply();
            UpdateLabels();
        };
    }

    private void UpdateLabels()
    {
        _scaleLabel.Text = $"{_scaleSlider.Value:P0}";
        _opacityLabel.Text = $"{_opacitySlider.Value:P0}";
        _bgOpacityLabel.Text = $"{_bgOpacitySlider.Value:P0}";
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(IsInteractiveControl))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // Deliberately NOT ScrollViewer, same call SpawnsWindow makes: this undecorated window
    // has no OS title bar, and once the body scrolls, the empty space inside the
    // ScrollViewer is the only large grab area left — the title row itself is mostly text
    // and a close button. Excluding ScrollViewer here would leave almost nowhere to drag
    // from. A press on an actual control (ScrollBar included) still gets its own click.
    private static bool IsInteractiveControl(Visual visual) => visual is
        Button or TextBox or ComboBox or global::Avalonia.Controls.Slider or CheckBox or ToggleButton or ScrollBar;

    /// <summary>Ceiling, not a fixed height — SizeToContent still shrinks the window for
    /// short content. Two numbers, same shape as SpawnsWindow.ApplyHeightLimit: the
    /// window-level cap is generous (just shy of the full work area, covers window chrome
    /// and OS panels), the body ScrollViewer's own cap is the one that actually bites,
    /// sized to leave room for the fixed title row that sits above it and must stay
    /// reachable. WorkingArea.Height is divided by Scaling because WorkingArea is in
    /// physical pixels while Avalonia's Height/MaxHeight are logical (DIP) units — skip
    /// that on a HiDPI screen and the clamp fires at the wrong point.</summary>
    private void ApplyHeightLimit(Screen? screen)
    {
        if (screen is null) return;
        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        MaxHeight = Math.Max(280, workingHeight - 40);
        _bodyScroll.MaxHeight = Math.Max(200, workingHeight - 110);
    }

    /// <summary>Re-asks the screen the window actually landed on and re-applies the
    /// clamp. Called after anything that can grow the body — expanding the watch guide,
    /// adding or deleting a watch rule — since that's exactly the content growth that used
    /// to carry the close button off-screen with it.</summary>
    private void ReclampHeight() => ApplyHeightLimit(Screens.ScreenFromWindow(this) ?? Screens.Primary);

    private static TextBlock LabelValue() => new()
    {
        FontSize = 12,
        Foreground = AppTheme.AccentBrush,
    };

    private static Slider Slider(double min, double max, double tick) => new()
    {
        Minimum = min,
        Maximum = max,
        TickFrequency = tick,
        IsSnapToTickEnabled = true,
    };
}
