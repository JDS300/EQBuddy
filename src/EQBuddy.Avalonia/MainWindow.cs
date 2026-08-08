using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EQBuddy.Core;
using SpawnChip = EQBuddy.UI.Shared.SpawnChip;

namespace EQBuddy.Avalonia;

public sealed class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SessionStats _stats = new();
    // Attached at construction (not in SessionStats itself) so tests never touch disk.
    private void AttachSpellStore() =>
        _stats.Spells.AttachStore(System.IO.Path.Combine(Core.AppPaths.Dir, "spell-categories.json"));
    private readonly LogWatcher _watcher;
    private readonly SessionRepository _repo = new(SessionRepository.DefaultDbPath);
    private readonly SessionArchiver _archiver;
    private DateTime _lastCheckpoint = DateTime.MinValue;
    private readonly DispatcherTimer _uiTimer;
    private readonly LayoutTransformControl _scaleRoot = new();
    private readonly Border _root = new();
    private readonly Grid _miniRoot = new();
    private readonly StackPanel _miniChips = new() { Orientation = Orientation.Horizontal };
    private readonly Ellipse _miniDot = Dot();
    private readonly StackPanel _normalRoot = new() { Width = 320 };
    private readonly Ellipse _statusDot = Dot();
    private readonly TextBlock _charLabel = AppTheme.DimText("looking for a character...");
    private readonly ScrollViewer _sectionScroll = new();
    private readonly Border _logBanner = Banner(AppTheme.WarnWashBrush);
    private readonly Border _updateBanner = Banner(AppTheme.GoodWashBrush);
    private readonly TextBlock _updateText = new() { FontSize = 12, Foreground = AppTheme.GoodBrush, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _zoneText = AppTheme.DimText("-");
    private readonly TextBlock _sessionText = AppTheme.DimText("session 0:00");
    private readonly TextBlock _combatHeader = AppTheme.StatValue("0 dps");
    private readonly TextBlock _healingHeader = AppTheme.StatValue("0 hps");
    private readonly TextBlock _killsHeader = AppTheme.StatValue("0");
    private readonly TextBlock _lootHeader = AppTheme.StatValue("0 items");
    private readonly TextBlock _trackedHeader = AppTheme.StatValue("0");
    private readonly TextBlock _moneyHeader = AppTheme.StatValue("0c");
    private readonly TextBlock _progressHeader = AppTheme.StatValue("0% xp");
    private readonly TextBlock _factionHeader = AppTheme.StatValue("-");
    private readonly TextBlock _miscHeader = AppTheme.StatValue("0 deaths");
    private readonly TextBlock _combatSummary = AppTheme.DimText("");
    // The fight in front of you, above the session aggregate — see ShowLastFight. The
    // headings are buttons: each subsection collapses on its own and remembers it.
    private readonly Button _combatFightLabel = AppTheme.IconButton("v Last fight", "Show or hide this fight's breakdown");
    private readonly StackPanel _combatFightBody = new();
    private readonly TextBlock _combatFightText = AppTheme.DimText("");
    private readonly ItemsControl _combatFightList = new();
    private readonly TextBlock _combatFightSplit = AppTheme.DimText("");
    private readonly TextBlock _combatFightOutLabel = AppTheme.Heading("Your damage");
    private readonly TextBlock _combatFightInLabel = AppTheme.Heading("Damage you took");
    private readonly ItemsControl _combatFightInList = new();
    private readonly Button _combatSessionLabel = AppTheme.IconButton("v Session so far", "Show or hide the session totals");
    private readonly StackPanel _combatSessionBody = new();
    private readonly Button _healFightLabel = AppTheme.IconButton("v Last fight", "Show or hide this fight's healing");
    private readonly StackPanel _healFightBody = new();
    private readonly TextBlock _healFightText = AppTheme.DimText("");
    private readonly ItemsControl _healFightList = new();
    private readonly Button _healSessionLabel = AppTheme.IconButton("v Session so far", "Show or hide the session totals");
    private readonly StackPanel _healSessionBody = new();
    private readonly TextBlock _healingSummary = AppTheme.DimText("");
    private readonly TextBlock _killsSummary = AppTheme.DimText("");
    private readonly TextBlock _moneySummary = AppTheme.DimText("");
    private readonly TextBlock _progressSummary = AppTheme.DimText("");
    private readonly ItemsControl _damageSourceList = new();
    private readonly TextBlock _petAbilityLabel = AppTheme.Heading("Pet abilities");
    private readonly ItemsControl _petAbilityList = new();
    private readonly ItemsControl _damageTakenList = new();
    private readonly ItemsControl _healSpellList = new();
    private readonly ItemsControl _healerList = new();
    private readonly ItemsControl _killList = new();
    private readonly ItemsControl _partyKillList = new();
    private readonly ItemsControl _lootList = new();
    private readonly StackPanel _trackedPanel = new();
    private readonly ItemsControl _craftedList = new();
    private readonly ItemsControl _soldList = new();
    private readonly ItemsControl _skillList = new();
    private readonly ItemsControl _factionList = new();
    private readonly ItemsControl _deathList = new();
    private readonly ItemsControl _zoneList = new();
    private readonly TextBlock _healSpellsLabel = AppTheme.Heading("Heals cast", AppTheme.GoodBrush);
    private readonly StackPanel _healSortBar = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _healersLabel = AppTheme.Heading("Healed by", AppTheme.GoodBrush);
    private readonly TextBlock _partyKillsLabel = AppTheme.Heading("Group kills");
    private readonly TextBlock _craftedLabel = AppTheme.Heading("Created by merging");
    private readonly TextBlock _soldLabel = AppTheme.Heading("Sold to merchants");
    private readonly TextBlock _recentFightsLabel = AppTheme.Heading("Recent fights");
    private readonly ItemsControl _recentFightsList = new();
    private readonly TextBlock _stanceLabel = AppTheme.Heading("By stance");
    private readonly ItemsControl _stanceList = new();
    private readonly TextBlock _invocationLabel = AppTheme.Heading("By invocation");
    private readonly ItemsControl _invocationList = new();
    private readonly TextBlock _farmingLabel = AppTheme.Heading("Farming (per creature)");
    private readonly ItemsControl _farmingList = new();
    private readonly TextBlock _markersLabel = AppTheme.Heading("Camp markers");
    private readonly ItemsControl _markerList = new();
    private readonly Button _gearBtn = AppTheme.IconButton(AppIcon.Settings, "Settings");
    private readonly MenuItem _trackSpawnsItem = new() { Header = "Track spawns (named respawn timers)" };
    private readonly MenuItem _clickThroughItem = new() { Header = "Click-through (clicks pass to the game)" };
    private readonly Dictionary<string, Button> _stars = new();
    private readonly Dictionary<string, SectionPanel> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _sectionsPanel = new();
    private TextBlock _dmgOutSortTotal = null!;
    private TextBlock? _dmgOutSortDps;
    private TextBlock _dmgOutSortHits = null!;
    private TextBlock _dmgOutSortAvg = null!;
    private TextBlock _dmgInSortTotal = null!;
    private TextBlock _dmgInSortHits = null!;
    private TextBlock _dmgInSortAvg = null!;
    private TextBlock _healSortTotal = null!;
    private TextBlock? _healSortHps;
    private TextBlock _healSortHits = null!;
    private TextBlock _healSortAvg = null!;
    private DateTime _lastCharScan = DateTime.MinValue;
    private DateTime _lastJanitorRun = DateTime.MinValue;
    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private UpdateInfo? _pendingUpdate;
    private DateTime _upToDateNoticeUntil = DateTime.MinValue;
    private bool _installingUpdate;
    private bool _clickThrough;
    private X11HotkeyService? _hotkeys;
    private HistoryWindow? _historyWindow;
    private OptionsWindow? _optionsWindow;
    private AlertWindow? _alertWindow;
    private readonly MezTracker _mezTracker = new();
    private readonly HotTracker _hotTracker = new();
    private readonly SpawnTimers _spawnTimers;
    private readonly EQBuddy.UI.Shared.SpawnsViewModel _spawnsVm;
    // Nullable fields, not WPF's `is not { IsLoaded: true }` guard: a closed Avalonia
    // window doesn't report IsLoaded the way WPF's does, so each window clears its own
    // field from Closed and "is it up?" is simply "is the field null?".
    private MezChipsWindow? _mezWindow;
    private HotChipsWindow? _hotWindow;
    private SpawnChipsWindow? _chipsWindow;
    private SpawnsWindow? _spawnsWindow;
    private StatSort _dmgOutSort = StatSort.Total;
    private StatSort _dmgInSort = StatSort.Total;
    private StatSort _healSort = StatSort.Total;
    private readonly bool _expandForTesting = Environment.GetEnvironmentVariable("EQBUDDY_EXPAND") == "1";

    private static readonly string[] MiniStatOrder = ["kills", "dps", "hps", "loot", "money", "xp", "deaths"];

    private enum StatSort { Total, Hits, Avg, Rate }

    public MainWindow()
    {
        // Before the watcher's startup replay, so already-logged charms classify with
        // everything learned in earlier sessions (issue #29).
        AttachSpellStore();
        _stats.AaStore = new AaLedgerStore(AppPaths.File("aa-ledger.json"));
        _watcher = new LogWatcher(_stats);
        // All three trackers are hung off the watcher for the same reason AttachSpellStore
        // runs above: Select() replays the whole log, and everything any tracker derives keys
        // off log timestamps, so the replay reconstructs them exactly. Wire them after the
        // replay instead and the app starts blind to mezzes, kills, and HoTs already in
        // today's log. It matters most for HoTs: a HoT has a 24-second life and announces
        // neither its landing nor its fade (HotTracker's doc comment), so a tracker wired
        // late doesn't recover — it simply misses every cast until the next one ticks.
        _mezTracker.AttachStore(System.IO.Path.Combine(Core.AppPaths.Dir, "mez-durations.json"));
        _watcher.Mez = _mezTracker;
        _hotTracker.AttachStore(System.IO.Path.Combine(Core.AppPaths.Dir, "hot-durations.json"));
        _watcher.Hot = _hotTracker;
        var spawnCatalog = SpawnCatalog.LoadEmbedded();
        var spawnOverrides = SpawnOverrides.Load(AppPaths.File("spawn-overrides.json"));
        _spawnTimers = new SpawnTimers(spawnCatalog, spawnOverrides, AppPaths.File("spawn-timers.json"));
        // Select() also stamps the character's server onto SpawnTimers.Server, so the
        // assignment has to happen before FollowActiveCharacter picks a log.
        _watcher.Spawns = _spawnTimers;
        _spawnsVm = new EQBuddy.UI.Shared.SpawnsViewModel(spawnCatalog, spawnOverrides, _spawnTimers);
        // Before any tailing: the initial full-log ingest has to know which text rules to
        // watch for, or a Text rule would miss everything already in today's log.
        _stats.RefreshTextPatterns(_settings.TrackedRules);
        _stats.TextMatched += OnTextMatched;
        // An idle gap ended the session: anything still cued belongs to a fight that is
        // long over.
        _stats.SessionRolledOver += () => Dispatcher.UIThread.Post(_delayedAlerts.CancelAll);
        _archiver = new SessionArchiver(_repo);
        _stats.SessionEnding += snap => _archiver.FinalizeActive(snap, "IdleTimeout");
        Title = "EQBuddy";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        CanResize = false;
        Opacity = _settings.Opacity;
        Content = BuildRoot();

        // Migration: any old per-rule pin enables the replacement group pin.
        if (!_settings.PinWatchChips && _settings.TrackedRules.Any(r => r.Pinned))
            _settings.PinWatchChips = true;
        // Chips became per-rule again: someone who had them on was seeing every enabled rule,
        // so pin what they already had rather than silently emptying their mini bar. Once
        // only — gated on a flag so deliberately unpinning every rule isn't undone next launch.
        if (!_settings.WatchPinsMigrated)
        {
            // Not conditioned on "nothing is pinned": AppSettings.Load may already have
            // added the built-in CC-broke rule, which is pinned by default, and that made
            // this pass skip itself and leave the user's own rules invisible.
            if (_settings.PinWatchChips)
                foreach (var rule in _settings.TrackedRules.Where(r => r.Enabled))
                    rule.Pinned = true;
            _settings.WatchPinsMigrated = true;
            _settings.Save();
        }

        if (_settings.LogFolder is { } saved && !Directory.Exists(saved))
            _settings.LogFolder = null;
        _settings.LogFolder ??= LogWatcher.FindDefaultLogFolder();
        RestorePosition();
        ApplyUiScale(_settings.UiScale);
        ApplyBackgroundOpacity(_settings.BackgroundOpacity);
        UpdateStarVisuals();
        ApplySectionLayout();
        SetMode(_settings.Minimized);
        if (_expandForTesting)
            foreach (var section in _sections.Values)
                section.IsExpanded = true;
        FollowActiveCharacter();

        if (_settings.LogFolder is { } lf)
        {
            // Page one of the launch tour is the log-truncation consent question.
            // Leave existing logs untouched until the user has answered it.
            var prune = _settings.TruncateLogs && !_settings.ShowTutorial;
            Task.Run(() =>
            {
                EqConfig.EnsureLoggingEnabled(lf);
                if (prune) EqConfig.TruncateStaleLogs(lf, SessionStats.SessionGap);
            });
        }

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
        Loaded += (_, _) =>
        {
            UpdateWindowHeightLimit();
            RegisterGlobalHotkeys();
            if (_settings.ShowTutorial)
                new TutorialWindow(this).Show(this);
            // Parity with the WPF hook of the same name (CONTRIBUTING's "Testing without
            // the game"). Options is only reachable through the right-click menu, which
            // makes the one window whose layout has to be checked by eye the one window
            // nobody can screenshot from a script.
            if (Environment.GetEnvironmentVariable("EQBUDDY_OPTIONS") == "1")
                OnOptions(this, EventArgs.Empty);
        };
    }

    public double UiScale => _settings.UiScale;
    public double WidgetOpacity => Opacity;
    public double BackgroundOpacityValue => _settings.BackgroundOpacity;
    public bool TruncateLogsValue => _settings.TruncateLogs;
    public AppSettings Settings => _settings;
    public void PersistSettings() => _settings.Save();

    internal static readonly (string Key, string Title)[] SectionCatalog =
    [
        ("combat", "Combat"), ("healing", "Healing"), ("kills", "Kills"), ("loot", "Loot"),
        ("tracked", "Tracked"), ("money", "Money"), ("progress", "Progress"),
        ("faction", "Faction"), ("misc", "Travels & Deaths"),
    ];

    public void ApplySectionLayout()
    {
        var order = _settings.SectionOrder.Where(_sections.ContainsKey).ToList();
        foreach (var (key, _) in SectionCatalog)
            if (!order.Contains(key)) order.Add(key);

        _sectionsPanel.Children.Clear();
        foreach (var key in order)
        {
            var section = _sections[key];
            _sectionsPanel.Children.Add(section);
            if (key != "tracked")
                section.IsVisible = !_settings.HiddenSections.Contains(key);
        }
    }

    public void SetTruncateLogs(bool enabled)
    {
        _settings.TruncateLogs = enabled;
        _settings.Save();
    }

    public void SetUiScale(double scale)
    {
        _settings.UiScale = Math.Clamp(scale, 0.5, 2.0);
        ApplyUiScale(_settings.UiScale);
        _settings.Save();
    }

    public void SetWindowOpacity(double opacity)
    {
        _settings.Opacity = Math.Clamp(opacity, 0.3, 1.0);
        Opacity = _settings.Opacity;
        _settings.Save();
    }

    public void SetBackgroundOpacity(double opacity)
    {
        _settings.BackgroundOpacity = Math.Clamp(opacity, 0.15, 1.0);
        ApplyBackgroundOpacity(_settings.BackgroundOpacity);
        _settings.Save();
    }

    private Control BuildRoot()
    {
        _scaleRoot.Child = _root;
        _root.CornerRadius = new CornerRadius(10);
        _root.BorderBrush = AppTheme.BorderBrush;
        _root.BorderThickness = new Thickness(1);
        _root.ContextMenu = BuildContextMenu();
        _root.PointerPressed += OnDrag;
        _root.Child = new StackPanel
        {
            Margin = new Thickness(10),
            Children =
            {
                BuildMiniRoot(),
                BuildNormalRoot(),
            },
        };
        return _scaleRoot;
    }

    private Control BuildMiniRoot()
    {
        _miniRoot.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _miniRoot.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _miniRoot.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _miniRoot.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _miniDot.Margin = new Thickness(2, 0, 8, 0);
        _miniRoot.Children.Add(_miniDot);
        Grid.SetColumn(_miniChips, 1);
        _miniRoot.Children.Add(_miniChips);
        var restore = AppTheme.IconButton(AppIcon.Expand, "Expand");
        restore.Click += (_, _) => SetMode(false);
        restore.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(restore, 2);
        _miniRoot.Children.Add(restore);
        var close = AppTheme.IconButton(AppIcon.Close, "Close");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 3);
        _miniRoot.Children.Add(close);
        return _miniRoot;
    }

    private Control BuildNormalRoot()
    {
        _normalRoot.Children.Add(BuildTitleBar());
        _logBanner.Child = new TextBlock
        {
            Text = "Logging looks off. Type /log in the game's chat window. EQBuddy enables it automatically for future game launches.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = AppTheme.WarnBrush,
        };
        _logBanner.Margin = new Thickness(0, 8, 0, 0);
        _normalRoot.Children.Add(_logBanner);
        _updateBanner.Child = _updateText;
        _updateBanner.Margin = new Thickness(0, 8, 0, 0);
        _updateBanner.Cursor = new Cursor(StandardCursorType.Hand);
        _updateBanner.PointerPressed += OnUpdateBannerClick;
        _normalRoot.Children.Add(_updateBanner);
        _normalRoot.Children.Add(BuildSessionLine());
        _sectionScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _sectionScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _sectionScroll.Content = BuildSections();
        _normalRoot.Children.Add(_sectionScroll);
        return _normalRoot;
    }

    private Control BuildTitleBar()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _statusDot.Margin = new Thickness(2, 0, 7, 0);
        title.Children.Add(_statusDot);
        title.Children.Add(new TextBlock { Text = "EQBuddy", FontWeight = FontWeight.Bold, FontSize = 14, Foreground = AppTheme.AccentBrush });
        grid.Children.Add(title);
        _charLabel.Margin = new Thickness(10, 0, 6, 0);
        _charLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_charLabel, 1);
        grid.Children.Add(_charLabel);
        _gearBtn.Click += OnGear;
        Grid.SetColumn(_gearBtn, 2);
        grid.Children.Add(_gearBtn);
        var reset = AppTheme.IconButton(AppIcon.Refresh, "Reset session stats");
        reset.Click += (_, _) => _stats.Reset();
        Grid.SetColumn(reset, 3);
        grid.Children.Add(reset);
        var mini = AppTheme.IconButton(AppIcon.Minimize, "Minimize to dashboard");
        mini.Click += (_, _) => SetMode(true);
        Grid.SetColumn(mini, 4);
        grid.Children.Add(mini);
        var close = AppTheme.IconButton(AppIcon.Close, "Close");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 5);
        grid.Children.Add(close);
        return grid;
    }

    private Control BuildSessionLine()
    {
        var grid = new Grid { Margin = new Thickness(2, 8, 2, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Children.Add(_zoneText);
        Grid.SetColumn(_sessionText, 1);
        grid.Children.Add(_sessionText);
        return grid;
    }

    private Control BuildSections()
    {
        AddSection("combat", "dps", "Combat", _combatHeader, BuildCombatSection(), "Show DPS in mini dashboard");
        AddSection("healing", "hps", "Healing", _healingHeader, BuildHealingSection(), "Show HPS in mini dashboard");
        AddSection("kills", "kills", "Kills", _killsHeader, BuildKillsSection(), "Show kills in mini dashboard");
        AddSection("loot", "loot", "Loot", _lootHeader, BuildLootSection(), "Show loot count in mini dashboard");
        _sections["tracked"] = AppTheme.Section(Header("Tracked", _trackedHeader), _trackedPanel);
        AddSection("money", "money", "Money", _moneyHeader, BuildMoneySection(), "Show money in mini dashboard");
        AddSection("progress", "xp", "Progress", _progressHeader, BuildProgressSection(), "Show XP in mini dashboard");
        _sections["faction"] = AppTheme.Section(Header("Faction", _factionHeader), _factionList);
        AddSection("misc", "deaths", "Travels & Deaths", _miscHeader, BuildMiscSection(), "Show deaths in mini dashboard");
        return _sectionsPanel;
    }

    private void AddSection(string sectionKey, string starKey, string title, TextBlock value, Control content, string tip)
    {
        var star = AppTheme.StarButton(starKey, tip);
        star.Click += OnStarChanged;
        _stars[starKey] = star;
        _sections[sectionKey] = AppTheme.Section(Header(title, value, star), content);
    }

    private static Grid Header(string title, TextBlock value, Button? star = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        if (star is not null) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = AppTheme.TextBrush });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        if (star is not null)
        {
            Grid.SetColumn(star, 2);
            grid.Children.Add(star);
        }
        return grid;
    }

    private Control BuildCombatSection()
    {
        var panel = new StackPanel();
        _combatFightText.Margin = new Thickness(0, 1, 0, 2);
        _combatFightBody.Children.Add(_combatFightText);
        _combatFightBody.Children.Add(_combatFightSplit);
        _combatFightBody.Children.Add(_combatFightOutLabel);
        _combatFightBody.Children.Add(_combatFightList);
        _combatFightInLabel.Margin = new Thickness(0, 2, 0, 0);
        _combatFightBody.Children.Add(_combatFightInLabel);
        _combatFightBody.Children.Add(_combatFightInList);
        _combatFightLabel.Click += (_, _) =>
            ToggleSubsection(v => _settings.ShowCombatFight = v, _settings.ShowCombatFight);
        panel.Children.Add(_combatFightLabel);
        panel.Children.Add(_combatFightBody);

        _combatSessionLabel.Click += (_, _) =>
            ToggleSubsection(v => _settings.ShowCombatSession = v, _settings.ShowCombatSession);
        panel.Children.Add(_combatSessionLabel);

        var body = _combatSessionBody;
        _combatSummary.Margin = new Thickness(0, 2, 0, 4);
        body.Children.Add(_combatSummary);
        body.Children.Add(SortHeader("Damage by attack", out _dmgOutSortTotal, out _dmgOutSortHits,
            out _dmgOutSortAvg, out _dmgOutSortDps, OnSortDmgOut, rateText: "dps"));
        body.Children.Add(_damageSourceList);
        body.Children.Add(_petAbilityLabel);
        body.Children.Add(_petAbilityList);
        body.Children.Add(SortHeader("Damage taken from", out _dmgInSortTotal, out _dmgInSortHits,
            out _dmgInSortAvg, out _, OnSortDmgIn));
        body.Children.Add(_damageTakenList);
        _recentFightsLabel.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_recentFightsLabel);
        body.Children.Add(_recentFightsList);
        _stanceLabel.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_stanceLabel);
        body.Children.Add(_stanceList);
        _invocationLabel.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_invocationLabel);
        body.Children.Add(_invocationList);
        panel.Children.Add(body);
        return panel;
    }

    /// <summary>Each subsection remembers its own collapsed state — see AppSettings.</summary>
    private void ToggleSubsection(Action<bool> set, bool current)
    {
        set(!current);
        PersistSettings();
        RefreshUi();
    }

    private void ApplySessionSubsections()
    {
        _combatSessionLabel.Content = (_settings.ShowCombatSession ? "v" : ">") + " Session so far";
        _combatSessionBody.IsVisible = _settings.ShowCombatSession;
        _healSessionLabel.Content = (_settings.ShowHealSession ? "v" : ">") + " Session so far";
        _healSessionBody.IsVisible = _settings.ShowHealSession;
    }

    private Control BuildHealingSection()
    {
        var panel = new StackPanel();
        _healFightText.Margin = new Thickness(0, 1, 0, 2);
        _healFightBody.Children.Add(_healFightText);
        _healFightBody.Children.Add(_healFightList);
        _healFightLabel.Click += (_, _) =>
            ToggleSubsection(v => _settings.ShowHealFight = v, _settings.ShowHealFight);
        panel.Children.Add(_healFightLabel);
        panel.Children.Add(_healFightBody);

        _healSessionLabel.Click += (_, _) =>
            ToggleSubsection(v => _settings.ShowHealSession = v, _settings.ShowHealSession);
        panel.Children.Add(_healSessionLabel);

        var body = _healSessionBody;
        _healingSummary.Margin = new Thickness(0, 2, 0, 4);
        body.Children.Add(_healingSummary);
        var sort = SortHeader("Heals cast", out _healSortTotal, out _healSortHits, out _healSortAvg,
            out _healSortHps, OnSortHeal, _healSpellsLabel, _healSortBar, "hps");
        body.Children.Add(sort);
        body.Children.Add(_healSpellList);
        body.Children.Add(_healersLabel);
        body.Children.Add(_healerList);
        panel.Children.Add(body);
        return panel;
    }

    private Control BuildKillsSection()
    {
        var panel = new StackPanel();
        _killsSummary.Margin = new Thickness(0, 2, 0, 4);
        panel.Children.Add(_killsSummary);
        panel.Children.Add(_killList);
        _farmingLabel.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_farmingLabel);
        panel.Children.Add(_farmingList);
        _partyKillsLabel.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_partyKillsLabel);
        panel.Children.Add(_partyKillList);
        return panel;
    }

    private Control BuildLootSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(_lootList);
        _craftedLabel.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_craftedLabel);
        panel.Children.Add(_craftedList);
        return panel;
    }

    private Control BuildMoneySection()
    {
        var panel = new StackPanel();
        panel.Children.Add(_moneySummary);
        _soldLabel.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_soldLabel);
        panel.Children.Add(_soldList);
        return panel;
    }

    private Control BuildProgressSection()
    {
        var panel = new StackPanel();
        _progressSummary.Margin = new Thickness(0, 2, 0, 4);
        panel.Children.Add(_progressSummary);
        panel.Children.Add(AppTheme.Heading("Skill-ups"));
        panel.Children.Add(_skillList);
        return panel;
    }

    private Control BuildMiscSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(AppTheme.Heading("Deaths", AppTheme.BadBrush));
        panel.Children.Add(_deathList);
        panel.Children.Add(AppTheme.Heading("Zones visited"));
        panel.Children.Add(_zoneList);
        _markersLabel.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_markersLabel);
        panel.Children.Add(_markerList);
        return panel;
    }

    private static Control SortHeader(string title, out TextBlock total, out TextBlock hits, out TextBlock avg,
        out TextBlock? rate, EventHandler<PointerPressedEventArgs> handler, TextBlock? titleBlock = null,
        StackPanel? sortBar = null, string? rateText = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Children.Add(titleBlock ?? AppTheme.Heading(title));
        sortBar ??= new StackPanel { Orientation = Orientation.Horizontal };
        sortBar.HorizontalAlignment = HorizontalAlignment.Right;
        sortBar.Children.Add(AppTheme.DimText("sort:", new Thickness(0, 0, 4, 0)));
        total = SortLink("total", "total", handler, selected: true);
        var rateSubject = title.Contains("Heal", StringComparison.OrdinalIgnoreCase) ? "spell" : "ability";
        rate = rateText is null ? null : SortLink(rateText, "rate", handler,
            tip: $"Per-{rateSubject} {rateText}: that {rateSubject}'s total divided by total time in combat");
        hits = SortLink(title.Contains("Heal", StringComparison.OrdinalIgnoreCase) ? "casts" : "hits", "hits", handler);
        avg = SortLink("avg", "avg", handler);
        sortBar.Children.Add(total);
        if (rate is not null) sortBar.Children.Add(rate);
        sortBar.Children.Add(hits);
        sortBar.Children.Add(avg);
        Grid.SetColumn(sortBar, 1);
        grid.Children.Add(sortBar);
        return grid;
    }

    private static TextBlock SortLink(string text, string tag, EventHandler<PointerPressedEventArgs> handler,
        bool selected = false, string? tip = null)
    {
        var link = new TextBlock
        {
            Text = text,
            Tag = tag,
            FontSize = 10,
            Foreground = selected ? AppTheme.AccentBrush : AppTheme.DimBrush,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(text == "total" ? 0 : 6, 0, 0, 0),
        };
        if (tip is not null) ToolTip.SetTip(link, tip);
        link.PointerPressed += handler;
        return link;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var version = new MenuItem { Header = $"EQBuddy v{UpdateChecker.DisplayVersion}", IsEnabled = false };
        var check = new MenuItem { Header = "Check for updates" };
        check.Click += (_, _) => { _lastUpdateCheck = DateTime.Now; CheckForUpdates(manual: true); };
        var options = new MenuItem { Header = "Options... (size, opacity, watch rules)" };
        options.Click += OnOptions;
        var tutorial = new MenuItem { Header = "Quick tutorial..." };
        tutorial.Click += OnTutorial;
        var marker = new MenuItem { Header = "Drop camp marker" };
        marker.Click += (_, _) => DropCampMarker();
        var history = new MenuItem { Header = "Session history..." };
        history.Click += OnHistory;
        var spawns = new MenuItem { Header = "Spawn timers..." };
        spawns.Click += (_, _) => ShowSpawnsWindow();
        _trackSpawnsItem.ToggleType = MenuItemToggleType.CheckBox;
        _trackSpawnsItem.IsChecked = _settings.TrackSpawns;
        // Avalonia flips IsChecked in MenuItem's class handler before instance Click
        // handlers run, so this reads the value the user just chose (WPF parity).
        _trackSpawnsItem.Click += (_, _) => SetTrackSpawns(_trackSpawnsItem.IsChecked);
        // The hotkey ships unbound, so the menu is click-through's only reliable door —
        // and the only way back out of a window the mouse can no longer touch.
        _clickThroughItem.ToggleType = MenuItemToggleType.CheckBox;
        _clickThroughItem.IsChecked = _clickThrough;
        // Avalonia flips IsChecked before this runs, so re-sync from the field afterwards:
        // ToggleClickThrough bails without changing anything when X11ClickThrough.Set fails,
        // and a tick left standing over an unchanged window would be a lie.
        _clickThroughItem.Click += (_, _) => { ToggleClickThrough(); _clickThroughItem.IsChecked = _clickThrough; };
        var choose = new MenuItem { Header = "Choose log folder..." };
        choose.Click += OnChooseLogFolder;
        var detect = new MenuItem { Header = "Auto-detect log folder" };
        detect.Click += (_, _) =>
        {
            _settings.LogFolder = LogWatcher.FindDefaultLogFolder();
            _settings.Save();
            _lastCharScan = DateTime.MinValue;
            FollowActiveCharacter();
        };
        menu.Items.Add(version);
        menu.Items.Add(check);
        menu.Items.Add(options);
        menu.Items.Add(tutorial);
        menu.Items.Add(marker);
        menu.Items.Add(history);
        menu.Items.Add(spawns);
        menu.Items.Add(_trackSpawnsItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(choose);
        menu.Items.Add(detect);
        return menu;
    }

    private void RestorePosition()
    {
        // A spot saved on a monitor that's since gone would put the widget in the
        // void; keep the default position instead (parity with the WPF guard).
        if (ScreenGuard.OnScreen(this, _settings.WindowLeft, _settings.WindowTop, Width, Height))
            Position = new PixelPoint((int)_settings.WindowLeft, (int)_settings.WindowTop);
    }

    private void ApplyUiScale(double scale)
    {
        _scaleRoot.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? null : new ScaleTransform(scale, scale);
        UpdateWindowHeightLimit();
        _scaleRoot.InvalidateMeasure();
        InvalidateMeasure();
        // The chicklet stacks are the same widget by another name — they scale with it.
        _mezWindow?.ApplyScale(scale);
        _hotWindow?.ApplyScale(scale);
        _chipsWindow?.ApplyScale(scale);
    }

    private void UpdateWindowHeightLimit()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null) return;

        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        MaxHeight = Math.Max(240, workingHeight - 20);

        // The section list sits inside the scaled widget. Reserve room for the title,
        // status/session lines, borders, and a little work-area breathing room.
        var scale = Math.Max(0.5, _settings.UiScale);
        _sectionScroll.MaxHeight = Math.Max(160, (workingHeight - 160) / scale);
    }

    private void ApplyBackgroundOpacity(double opacity) => _root.Background = AppTheme.BgWithOpacity(opacity);

    /// <summary>Re-applies visual state that AppTheme.Apply's brush mutation can't reach
    /// on its own: BgWithOpacity returns a fresh, non-live brush each call, and stat rows
    /// built from AccentBarBrush() bake in a color snapshot rather than a live reference.
    /// Everything else (borders, banners, headings) repaints on its own because it holds
    /// a reference to the same AppTheme brush instance that just got mutated.</summary>
    public void RefreshTheme()
    {
        ApplyBackgroundOpacity(_settings.BackgroundOpacity);
        RefreshUi();
    }

    private async void OnChooseLogFolder(object? sender, EventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick the EverQuest Legends Logs folder",
            AllowMultiple = false,
        });
        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (picked is null) return;
        var logsSub = System.IO.Path.Combine(picked, "Logs");
        if (!Directory.EnumerateFiles(picked, "eqlog_*.txt").Any() && Directory.Exists(logsSub))
            picked = logsSub;
        _settings.LogFolder = picked;
        _settings.Save();
        _lastCharScan = DateTime.MinValue;
        FollowActiveCharacter();
    }

    private void FollowActiveCharacter()
    {
        if (_settings.LogFolder is null)
        {
            _charLabel.Text = "logs not found - right-click, Choose log folder";
            return;
        }
        var active = LogWatcher.MostRecentlyActive(_settings.LogFolder);
        if (active is null)
        {
            _charLabel.Text = "waiting for a character to log in...";
            return;
        }
        if (!string.Equals(active.FilePath, _watcher.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            if (_watcher.CurrentPath is not null)
                _archiver.FinalizeActive(CurrentSnapshot(), "CharacterChanged");
            _watcher.Select(active.FilePath);
            _archiver.SetIdentity(_stats.ServerName, _stats.CharacterName);
            _charLabel.Text = active.Display;
        }
    }

    private StatsSnapshot CurrentSnapshot() =>
        _stats.Snapshot(TimeSpan.FromMinutes(Math.Max(1, _settings.RecentWindowMinutes)),
            _settings.TrackedRules);

    /// <summary>Mez chips for the chip stack; formatting lives in
    /// <see cref="EQBuddy.UI.Shared.MezChipPresentation"/> (shared with the WPF UI) — see
    /// its doc comment for the display rules (numbering, "?" durations, due tint).</summary>
    private List<SpawnChip> MezChips(DateTime now) =>
        EQBuddy.UI.Shared.MezChipPresentation.Chips(_mezTracker.Snapshot(now), now);

    /// <summary>HoT chips for the chip stack; formatting lives in
    /// <see cref="EQBuddy.UI.Shared.HotChipPresentation"/> (shared with the WPF UI) — see
    /// its doc comment for the display rules (countdown, recast warning, which chip is you).
    /// The self name comes from SessionStats, which LogWatcher.Select fills in from the log
    /// file's own name: it is the same identity the archiver is stamped with, so a character
    /// switch moves both together and nothing here has to parse a log line of its own.</summary>
    private List<SpawnChip> HotChips(DateTime now) =>
        EQBuddy.UI.Shared.HotChipPresentation.Chips(
            _hotTracker.Snapshot(now), now, _stats.CharacterName, _settings.ShowSelfHotChips);

    /// <summary>Bring a freshly built chicklet stack up in the state the widget is already
    /// in: owned by the widget (matching AlertWindow.ShowOwned, so it can never outlive it),
    /// at the current UI scale, and click-through if the widget is. The two stacks share no
    /// base type beyond Window, hence the two delegates rather than an interface.</summary>
    private void ShowStack(Window stack, Action<double> applyScale, Action<bool> applyClickThrough)
    {
        applyScale(_settings.UiScale);
        stack.Show(this);
        // After Show: the X11 handle these need doesn't exist until the window is up.
        if (_clickThrough) applyClickThrough(true);
    }

    private void CloseChips()
    {
        if (_chipsWindow is not { } cw) return;
        _chipsWindow = null;   // cleared first so Closed handling can't loop
        cw.SavePosition();     // a hide must never lose a drag from thirty seconds ago
        cw.Close();
    }

    /// <summary>Single switch for the spawn-timer feature: the setting, the menu check,
    /// and the Options checkbox stay in lockstep whichever of them the user touched.
    /// Arming opens nothing — the chicklet stack appears from the next tick if timers
    /// are running; the full window only ever opens on demand.</summary>
    internal void SetTrackSpawns(bool on)
    {
        _settings.TrackSpawns = on;
        _settings.Save();
        _trackSpawnsItem.IsChecked = on;
        if (_optionsWindow is { IsVisible: true } ow) ow.SyncTrackSpawns(on);
        if (!on)
        {
            CloseChips();
            if (_spawnsWindow is { } w)
            {
                _spawnsWindow = null;   // cleared first so Closed handling can't loop
                w.Close();
            }
        }
    }

    /// <summary>The full zone browser, on demand only. <paramref name="zone"/> comes from
    /// a double-clicked chicklet, so it opens showing the timer you clicked.</summary>
    internal void ShowSpawnsWindow(string? zone = null)
    {
        if (_spawnsWindow is { } open)
        {
            open.Activate();
            return;
        }
        var w = new SpawnsWindow(this, _spawnsVm, zone);
        w.Closed += (_, _) => { if (ReferenceEquals(_spawnsWindow, w)) _spawnsWindow = null; };
        _spawnsWindow = w;
        w.Show(this);
    }

    private void RefreshUi()
    {
        // Spawn timers crossing zero, off the shared tick so a hidden window can't
        // silence a camp.
        if (_settings.TrackSpawns)
        {
            // Sound only — no banner. The chip flipping to DUE is the visual, and a
            // banner on top of it was double notification (David's call). Each named
            // can carry its own sound; "Default" maps to Alarm — a camp popping
            // deserves a louder default than a loot ding (also David's call).
            foreach (var due in _spawnsVm.ConsumeDueAlerts(DateTime.Now))
                if (_spawnsVm.SoundFor(due.Zone, due.Name) is { } sound)
                    PlayAlertSound(sound);

            // Chicklets are the ambient face of spawn tracking: the stack exists exactly
            // while timers do — including alongside the full window, which is a browser,
            // not a replacement. No pop-open of the full window, ever (David's design).
            if (_spawnsVm.HasActiveTimers(DateTime.Now))
            {
                var cw = _chipsWindow;
                if (cw is null)
                {
                    cw = new SpawnChipsWindow(this, _spawnsVm);
                    cw.Closed += (_, _) => { if (ReferenceEquals(_chipsWindow, cw)) _chipsWindow = null; };
                    _chipsWindow = cw;
                    ShowStack(cw, cw.ApplyScale, cw.ApplyClickThrough);
                }
                cw.RefreshChips(DateTime.Now);
            }
            else
            {
                CloseChips();
            }
        }
        else
        {
            CloseChips();
        }

        // The mez stack lives its own life, independent of spawn tracking: it exists
        // exactly while a mez is believed active, in its own window (David's call —
        // mez chips park next to the fight, spawn chips are ambient).
        if (_mezTracker.Snapshot(DateTime.Now).Count > 0)
        {
            var mw = _mezWindow;
            if (mw is null)
            {
                mw = new MezChipsWindow(this, MezChips);
                mw.Closed += (_, _) => { if (ReferenceEquals(_mezWindow, mw)) _mezWindow = null; };
                _mezWindow = mw;
                ShowStack(mw, mw.ApplyScale, mw.ApplyClickThrough);
            }
            mw.RefreshChips(DateTime.Now);
        }
        else if (_mezWindow is { } closing)
        {
            _mezWindow = null;      // cleared first so Closed handling can't loop
            closing.SavePosition(); // a hide must never lose the spot
            closing.Close();
        }

        // The HoT stack, same shape again and just as independent: it exists exactly while
        // one of your heal-over-times is still ticking. Gated on the tracker's snapshot, not
        // on the chip list, so switching ShowSelfHotChips off can't leave an empty window
        // standing while a self-only HoT runs — HotChips would return zero rows for it.
        if (_hotTracker.Snapshot(DateTime.Now).Count > 0)
        {
            var hw = _hotWindow;
            if (hw is null)
            {
                hw = new HotChipsWindow(this, HotChips);
                hw.Closed += (_, _) => { if (ReferenceEquals(_hotWindow, hw)) _hotWindow = null; };
                _hotWindow = hw;
                ShowStack(hw, hw.ApplyScale, hw.ApplyClickThrough);
            }
            hw.RefreshChips(DateTime.Now);
        }
        else if (_hotWindow is { } hotClosing)
        {
            _hotWindow = null;         // cleared first so Closed handling can't loop
            hotClosing.SavePosition(); // a hide must never lose the spot
            hotClosing.Close();
        }

        if (DateTime.Now - _lastCharScan > TimeSpan.FromSeconds(5))
        {
            _lastCharScan = DateTime.Now;
            FollowActiveCharacter();
        }
        if (DateTime.Now - _lastUpdateCheck > TimeSpan.FromHours(6))
        {
            _lastUpdateCheck = DateTime.Now;
            CheckForUpdates(manual: false);
        }
        if (_settings.LogFolder is { } folder && DateTime.Now - _lastJanitorRun > TimeSpan.FromMinutes(10))
        {
            _lastJanitorRun = DateTime.Now;
            var prune = _settings.TruncateLogs && !_settings.ShowTutorial;
            Task.Run(() =>
            {
                EqConfig.EnsureLoggingEnabled(folder);
                if (prune) EqConfig.TruncateStaleLogs(folder, SessionStats.SessionGap);
            });
        }

        UpdateLoggingStatus();
        if (_upToDateNoticeUntil != DateTime.MinValue && DateTime.Now > _upToDateNoticeUntil && _pendingUpdate is null && !_installingUpdate)
        {
            _updateBanner.IsVisible = false;
            _upToDateNoticeUntil = DateTime.MinValue;
        }
        if (_watcher.LastError is { } err) App.LogError(err);

        var s = CurrentSnapshot();
        ProcessTrackedAlerts(s);
        if (DateTime.Now - _lastCheckpoint > TimeSpan.FromMinutes(5))
        {
            _lastCheckpoint = DateTime.Now;
            _archiver.Checkpoint(s);
        }
        if (_miniRoot.IsVisible) UpdateMiniChips(s);
        _zoneText.Text = s.CurrentZone.Length > 0 ? s.CurrentZone : "-";
        var active = TimeSpan.FromSeconds(s.ActiveSeconds);
        _sessionText.Text = s.SessionStart is { } start
            ? $"session {(int)s.Elapsed.TotalHours}:{s.Elapsed.Minutes:D2} - active {(int)active.TotalMinutes}m (since {start:h:mm tt})"
            : "waiting for log activity...";
        _combatHeader.Text = s.CurrentDps > 0 ? $"{s.SessionDps:0} dps (now {s.CurrentDps:0})" : $"{s.SessionDps:0} dps";
        _killsHeader.Text = s.PartyKillCount > 0 ? $"{s.YourKillCount} (+{s.PartyKillCount})" : $"{s.YourKillCount}";
        _lootHeader.Text = s.CraftedTotal > 0 ? $"{s.LootTotal} items (+{s.CraftedTotal} made)" : $"{s.LootTotal} item{(s.LootTotal == 1 ? "" : "s")}";
        _moneyHeader.Text = StatsSnapshot.FormatCoin(s.Copper);
        _progressHeader.Text = $"{s.XpPercent:0.0}% xp" + (s.Levels.Count > 0 ? $", +{s.Levels.Count} lvl" : "") + (s.AaGained > 0 ? $", +{s.AaGained} aa" : "");
        _factionHeader.Text = s.Faction.Count > 0 ? $"{s.Faction.Count} factions" : "-";
        _miscHeader.Text = $"{s.Deaths.Count} death{(s.Deaths.Count == 1 ? "" : "s")}";
        ApplySessionSubsections();
        RefreshExpandedSections(s);
    }

    /// <summary>Paint a snapshot into the cards, without the timer-driven housekeeping
    /// RefreshUi also does (character rescan, update check, log janitor). Exists so the
    /// headless render tests can exercise the code path every refresh takes — which is where
    /// a card that mis-formats or dereferences null actually breaks — without a log folder,
    /// a network, or a five-second wait.</summary>
    internal void RenderSnapshotForTest(StatsSnapshot s)
    {
        ApplySessionSubsections();
        RefreshExpandedSections(s);
        RenderTracked(s);
    }

    private void RefreshExpandedSections(StatsSnapshot s)
    {
        RefreshOptionalSectionVisibility(s);

        if (_sections["combat"].IsExpanded)
        {
            var acc = s.HitCount + s.MissCount > 0 ? (double)s.HitCount / (s.HitCount + s.MissCount) * 100 : 0;
            var critRate = s.HitCount > 0 ? (double)s.CritCount / s.HitCount * 100 : 0;
            var incomingSwings = s.AvoidedIncoming + s.MeleeHitsTaken;
            var avoidance = incomingSwings > 0 ? (double)s.AvoidedIncoming / incomingSwings * 100 : 0;
            var combatTime = TimeSpan.FromSeconds(s.CombatSeconds);
            ShowLastFight(s, _combatFightLabel, _combatFightBody, _combatFightText,
                _combatFightList, healing: false, _settings.ShowCombatFight);
            _combatSummary.Text =
                $"Dealt {s.DamageDealt:N0} ({s.MeleeDamage:N0} melee / {s.SpellDamage:N0} spell)\n" +
                $"{s.CritCount} crits ({critRate:0.#}% rate) - {acc:0}% accuracy\n" +
                $"In combat {(int)combatTime.TotalMinutes}m {combatTime.Seconds}s this session\n" +
                (s.Recent is { } rc ? $"Last {(int)rc.Window.TotalMinutes}m: {rc.Dps:0.#} dps{(rc.HasFullWindow ? "" : " (partial window)")}\n" : "") +
                $"Biggest hit: {s.MaxHit:N0} ({s.MaxHitDesc})\n" +
                $"Taken {s.DamageTaken:N0} - avoided {s.AvoidedIncoming} of {incomingSwings} melee attacks ({avoidance:0}%)" +
                (s.SpecialHits.Count > 0 ? "\n" + string.Join(" - ", s.SpecialHits.Select(x => $"{x.Name} {x.Count}")) : "") +
                (s.Fizzles + s.Resists > 0 ? $"\nFizzles {s.Fizzles} - resists {s.Resists}" : "") +
                (s.CurrentStance.Length > 0 ? $"\nStance: {s.CurrentStance}" : "");
            FillBreakdown(_damageSourceList, s.DamageBySource, _dmgOutSort, s.CombatSeconds, "dps");
            // Shares the damage sort bar above it — it's the same rows, one level down.
            _petAbilityLabel.IsVisible = s.PetAbilities.Count > 0;
            FillBreakdown(_petAbilityList, s.PetAbilities, _dmgOutSort, s.CombatSeconds, "dps");
            FillStatList(_damageTakenList, s.DamageByAttacker, _dmgInSort, "hit");
            _recentFightsLabel.IsVisible = s.RecentEncounters.Count > 0;
            var topFightDps = Math.Max(0.1, s.RecentEncounters.Count > 0
                ? s.RecentEncounters.Max(f => f.Dps)
                : 0);
            var fightBrush = AccentBarBrush();
            _recentFightsList.ItemsSource = s.RecentEncounters.Select(f => BarRow(f.Name,
                $"{f.DurationSeconds:0}s - {f.Dps:0.#} dps{(f.Outcome == "Timeout" ? " - ?" : "")}",
                f.Dps / topFightDps, fightBrush,
                $"{f.DamageOut:N0} damage over {f.DurationSeconds:0}s")).ToList();
            _stanceLabel.IsVisible = s.Stances.Count > 0;
            FillList(_stanceList, s.Stances.Select(x =>
                (x.Name, $"{x.Damage:N0} dmg - {(int)x.CombatSeconds}s - {x.Dps:0.#} dps")));
            _invocationLabel.IsVisible = s.Invocations.Count > 0;
            FillList(_invocationList, s.Invocations.Select(x =>
                (x.Name, $"{x.Damage:N0} dmg - {(int)x.CombatSeconds}s - {x.Dps:0.#} dps")));
        }
        _healingHeader.Text = s.Hps > 0 ? $"{s.Hps:0.#} hps" : $"{s.HealingDone:N0} healed";
        if (_sections["healing"].IsExpanded)
        {
            ShowLastFight(s, _healFightLabel, _healFightBody, _healFightText,
                _healFightList, healing: true, _settings.ShowHealFight);
            _healingSummary.Text = $"Done {s.HealingDone:N0} - received {s.HealingReceived:N0}" +
                (s.Recent is { Hps: > 0 } rh ? $"\nLast {(int)rh.Window.TotalMinutes}m: {rh.Hps:0.#} hps" : "") +
                (s.RegenTicks > 0 ? $"\n{s.RegenTicks} regen/hymn ticks (game logs no amounts for these)" : "");
            var showSpells = s.HealsBySpell.Count > 0;
            _healSpellsLabel.IsVisible = showSpells;
            _healSortBar.IsVisible = showSpells;
            FillBreakdown(_healSpellList, s.HealsBySpell, _healSort, s.CombatSeconds, "hps");
            _healersLabel.IsVisible = s.HealsByHealer.Count > 0;
            FillList(_healerList, s.HealsByHealer.Select(h => (h.Name, $"{h.Total:N0} - {h.Hits} heal{(h.Hits == 1 ? "" : "s")}")));
        }
        if (_sections["kills"].IsExpanded)
        {
            _killsSummary.Text = $"{s.KillsPerHour:0.0} kills/hr - {s.KillsPerActiveHour:0.0} active" +
                (s.Recent is { } rk ? $" - last {(int)rk.Window.TotalMinutes}m: {rk.Kills}" : "");
            FillList(_killList, s.YourKills.Select(k => (k.Name, $"x{k.Count}")));
            var farmed = s.Mobs.Where(m => m.Kills > 0).ToList();
            _farmingLabel.IsVisible = farmed.Count > 0;
            var farmRows = new List<(string, string)>();
            foreach (var m in farmed)
            {
                farmRows.Add((m.Name,
                    $"avg {m.AvgFightSeconds:0}s - {StatsSnapshot.FormatCoin(m.Copper)} - {m.XpPercent:0.0}% xp"));
                foreach (var l in m.Loot)
                    farmRows.Add(($"      {l.Item}", l.DropRatePct is { } pct ? $"x{l.Count} - {pct:0}%" : $"x{l.Count}"));
            }
            FillList(_farmingList, farmRows);
            _partyKillsLabel.IsVisible = s.PartyKillsByKiller.Count > 0;
            FillList(_partyKillList, s.PartyKillsByKiller.Select(k => (k.Name, $"x{k.Count}")));
        }
        if (_sections["loot"].IsExpanded)
        {
            FillList(_lootList, s.Loot.Select(l => (l.Item, $"x{l.Count}")));
            _craftedLabel.IsVisible = s.Crafted.Count > 0;
            FillList(_craftedList, s.Crafted.Select(c => (c.Name, $"x{c.Count}")));
        }
        RenderTracked(s);
        if (_sections["money"].IsExpanded)
        {
            _moneySummary.Text = $"Corpses {StatsSnapshot.FormatCoin(s.CorpseCopper)} ({s.CoinDrops} drops, biggest {StatsSnapshot.FormatCoin(s.BiggestDrop)})\n" +
                $"Merchant sales {StatsSnapshot.FormatCoin(s.VendorCopper)} ({s.SalesCount} sales)\n" +
                $"{StatsSnapshot.FormatCoin(s.CopperPerHour)} per hour - {StatsSnapshot.FormatCoin(s.CopperPerActiveHour)} per active hour" +
                (s.Recent is { } rm ? $"\nLast {(int)rm.Window.TotalMinutes}m: {StatsSnapshot.FormatCoin(rm.Copper)}" : "");
            _soldLabel.IsVisible = s.SoldItems.Count > 0;
            FillList(_soldList, s.SoldItems.Select(i => ($"{i.Item}{(i.Count > 1 ? $" x{i.Count}" : "")}", StatsSnapshot.FormatCoin(i.Copper))));
        }
        if (_sections["progress"].IsExpanded)
        {
            _progressSummary.Text = $"{s.XpTicks} xp gains - {s.XpPerHour:0.0}%/hr - {s.XpPerActiveHour:0.0}% active - {s.SkillUpTotal} skill-ups" +
                (s.Recent is { } rx ? $"\nLast {(int)rx.Window.TotalMinutes}m: {rx.XpPerHour:0.0}%/hr" : "") +
                (s.AaGained > 0 ? $"\n{s.AaGained} AA point{(s.AaGained == 1 ? "" : "s")} - {s.AaPerHour:0.0} AA/hr (now {s.AaTotal} unspent)" : "") +
                (s.HoursToLevel is { } eta ? $"\nNext level in {FormatEta(eta)} at this pace" : "") +
                (s.Levels.Count > 0
                    ? "\n" + string.Join(", ", s.Levels.Select((l, i) =>
                    {
                        var from = i == 0 ? s.SessionStart : s.Levels[i - 1].Time;
                        var mins = from is { } f ? (int)(l.Time - f).TotalMinutes : 0;
                        return $"{l.Text} at {l.Time:h:mm tt} ({mins}m)";
                    }))
                    : "");
            FillList(_skillList, s.SkillUps.Select(k => (k.Skill, $"{k.Value} (+{k.Ups})")));
        }
        if (_sections["faction"].IsExpanded)
            FillList(_factionList, s.Faction.Select(f => (f.Faction, EQBuddy.UI.Shared.FactionFormat.Net(f))),
                valueBrush: f => f.StartsWith('-') ? AppTheme.BadBrush : AppTheme.GoodBrush);
        if (_sections["misc"].IsExpanded)
        {
            FillList(_deathList, s.Deaths.Select(d => (d.Text, d.Time.ToString("h:mm tt"))));
            FillList(_zoneList, s.Zones.Select(z => (z.Text, z.Time.ToString("h:mm tt"))));
            _markersLabel.IsVisible = s.Markers.Count > 0;
            FillList(_markerList, s.Markers.Select(m => (m.Text, m.Time.ToString("h:mm tt"))));
        }

        if (_expandForTesting)
        {
            try
            {
                var dump = $"dmgSrc={_damageSourceList.Items.Count} dmgTaken={_damageTakenList.Items.Count} " +
                    $"kills={_killList.Items.Count} party={_partyKillList.Items.Count} loot={_lootList.Items.Count} " +
                    $"crafted={_craftedList.Items.Count} skills={_skillList.Items.Count} faction={_factionList.Items.Count} " +
                    $"zones={_zoneList.Items.Count} deaths={_deathList.Items.Count} " +
                    $"actualH={Bounds.Height:0} actualW={Bounds.Width:0}";
                File.WriteAllText(AppPaths.File("debug.txt"), dump);
            }
            catch { }
        }
    }

    private void RefreshOptionalSectionVisibility(StatsSnapshot s)
    {
        _recentFightsLabel.IsVisible = s.RecentEncounters.Count > 0;
        _petAbilityLabel.IsVisible = s.PetAbilities.Count > 0;
        _stanceLabel.IsVisible = s.Stances.Count > 0;
        _invocationLabel.IsVisible = s.Invocations.Count > 0;
        _farmingLabel.IsVisible = s.Mobs.Any(m => m.Kills > 0);
        _partyKillsLabel.IsVisible = s.PartyKillsByKiller.Count > 0;
        _craftedLabel.IsVisible = s.Crafted.Count > 0;
        _soldLabel.IsVisible = s.SoldItems.Count > 0;
        _healSpellsLabel.IsVisible = s.HealsBySpell.Count > 0;
        _healSortBar.IsVisible = s.HealsBySpell.Count > 0;
        _healersLabel.IsVisible = s.HealsByHealer.Count > 0;
        _markersLabel.IsVisible = s.Markers.Count > 0;
    }

    // Keyed by TrackedRule.Id — a display name can be shared by two rules, and keying
    // on it made same-named rules share baselines and cooldowns.
    private readonly Dictionary<string, int> _ruleBaseline = new(StringComparer.Ordinal);
    private readonly EQBuddy.UI.Shared.AlertCooldowns _ruleCooldowns = new();
    private readonly EQBuddy.UI.Shared.SoundGate _soundGate = new();
    private string? _alertBaselinePath;

    /// <summary>The floating alert tile, created on first use and owned by the widget.</summary>
    internal AlertWindow AlertTile => _alertWindow ??= new AlertWindow(_settings, this);

    private void RenderTracked(StatsSnapshot s)
    {
        var haveRules = _settings.TrackedRules.Count > 0 && !_settings.HiddenSections.Contains("tracked");
        if (_sections.TryGetValue("tracked", out var section))
            section.IsVisible = haveRules;
        if (!haveRules) return;

        _trackedHeader.Text = s.Tracked.Sum(t => t.TotalQuantity).ToString();
        if (!_sections["tracked"].IsExpanded) return;

        _trackedPanel.Children.Clear();
        foreach (var r in s.Tracked)
        {
            var head = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            head.Children.Add(new TextBlock
            {
                Text = r.Name.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppTheme.AccentBrush,
            });
            var rate = AppTheme.DimText($"{r.TotalQuantity} total - {r.PerHour:0.#}/hr - {r.PerActiveHour:0.#}/active hr");
            Grid.SetColumn(rate, 1);
            head.Children.Add(rate);
            _trackedPanel.Children.Add(head);

            foreach (var item in r.Items)
                _trackedPanel.Children.Add(new TextBlock
                {
                    Text = $"{item.Name}   x{item.Count}",
                    FontSize = 12,
                    Foreground = AppTheme.TextBrush,
                    Margin = new Thickness(6, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            _trackedPanel.Children.Add(AppTheme.DimText(
                r.LastMatch is { } lm ? $"last match {FormatAge(DateTime.Now - lm)} ago" : "no matches yet",
                new Thickness(6, 1, 0, 2)));
        }
    }

    private static string FormatAge(TimeSpan age) => age.TotalMinutes < 1
        ? $"{Math.Max(0, (int)age.TotalSeconds)}s"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m" : $"{(int)age.TotalHours}h {age.Minutes}m";

    /// <summary>Per-rule alert cooldown for text rules. Shorter than the 5 s used elsewhere
    /// (ALERT-008): a heal rotation announces every few seconds by design, and swallowing
    /// those repeats would silence exactly the case this rule kind exists for.</summary>
    private static readonly TimeSpan TextAlertCooldown = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A Text watch rule matched, straight off the ingest thread. Alerting here rather than
    /// from the next snapshot removes a whole refresh interval of lag from the one rule
    /// kind that's about reacting in time. Suppressed during initial ingest, like every
    /// other alert, so replaying today's log at startup fires nothing.
    /// </summary>
    private void OnTextMatched(RawLineEvent raw)
    {
        // Immediate alerts stay suppressed during the startup re-read, but a delayed cue
        // whose due time is still ahead is recovered with the time it has left — losing a
        // running respawn timer to an app restart is exactly when you needed it.
        var ingesting = !_watcher.InitialIngestDone;
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var rule in _settings.TrackedRules)
            {
                if (!rule.Enabled || rule.Kind != WatchKind.Text) continue;
                if (!rule.Matches(raw.Line)) continue;
                if (ingesting && rule.AlertDelaySeconds <= 0) continue;

                var name = rule.Name.Length > 0 ? rule.Name : rule.Pattern;
                var line = raw.Line.Length <= 80 ? raw.Line : raw.Line[..79].TrimEnd() + "…";
                AlertOrCue(rule, name, line, TextAlertCooldown, raw.Time);
            }
        });
    }

    private readonly EQBuddy.UI.Shared.DelayedAlerts _delayedAlerts = new();

    /// <summary>
    /// Alert now, or set a cue for later when the rule asks for a delay
    /// (<see cref="TrackedRule.AlertDelaySeconds"/>) — a complete-heal chain wants the sound
    /// a couple of seconds *after* the call, and a mez wants it before the spell breaks.
    ///
    /// One dispatcher timer per cue rather than the periodic refresh, so a 2.5 s cue lands
    /// at 2.5 s. The cooldown applies when the alert fires, not when it was scheduled: with
    /// a delay set, what matters is how long since you last heard something.
    /// </summary>
    private void AlertOrCue(TrackedRule rule, string ruleName, string label, TimeSpan cooldown,
        DateTime? matchTime = null)
    {
        if (rule.AlertDelaySeconds <= 0)
        {
            FireAlert(rule, ruleName, label, cooldown);
            return;
        }
        // Scheduled from when the line was written, not when we read it.
        var from = matchTime ?? DateTime.Now;
        var remaining = from.AddSeconds(rule.AlertDelaySeconds) - DateTime.Now;
        if (remaining <= TimeSpan.Zero) return;
        if (_delayedAlerts.Schedule(rule, ruleName, label, from) is not { } pending) return;

        DispatcherTimer? timer = null;
        timer = new DispatcherTimer { Interval = remaining };
        timer.Tick += (_, _) =>
        {
            timer!.Stop();
            if (_delayedAlerts.Claim(pending))
                FireAlert(pending.Rule, pending.RuleName, pending.Label, cooldown);
        };
        timer.Start();
    }

    private void FireAlert(TrackedRule rule, string ruleName, string label, TimeSpan cooldown)
    {
        if (!_ruleCooldowns.ShouldFire(rule, label, cooldown, DateTime.Now)) return;

        if (rule.AlertBanner)
            AlertTile.ShowAlert($"★ {ruleName}: {label}",
                EQBuddy.UI.Shared.AlertColors.Hex(rule.AlertColor));
        if (EQBuddy.UI.Shared.AlertSoundCatalog.Resolve(rule, _settings.AlertSound) is { } sound)
            PlayAlertSound(sound, coalesce: true);
    }

    /// <summary>Deaths seen last refresh, so a new one can cancel pending cues — a reminder
    /// to recast something is noise once you're dead.</summary>
    private int _knownDeaths;

    /// <summary>The "Last fight" line above a card's session totals, and the "Session so far"
    /// heading that separates the two. Hidden until there's been a fight.</summary>
    private void ShowLastFight(StatsSnapshot s, Button label, StackPanel body, TextBlock text,
        ItemsControl list, bool healing, bool open)
    {
        if (s.LastFight is not { } f)
        {
            label.IsVisible = body.IsVisible = false;
            return;
        }
        label.IsVisible = true;
        body.IsVisible = open;
        label.Content = $"{(open ? "v" : ">")} {(f.InProgress ? "Current fight" : "Last fight")}";
        if (!open) return;

        // Rates within the fight use the fight's own length, not session combat time.
        FillBreakdown(list, healing ? f.HealsBySpell : f.ByAbility,
            healing ? _healSort : _dmgOutSort, f.DurationSeconds, healing ? "hps" : "dps");
        if (!healing)
        {
            // Same treatment as the WPF card: split line, "Your damage", "Damage you took".
            _combatFightSplit.IsVisible = f.Fights.Count > 1;
            if (f.Fights.Count > 1)
                _combatFightSplit.Text = string.Join(" - ",
                    f.Fights.Select(x => $"{x.Name} {x.DamageOut:N0}"));
            _combatFightOutLabel.IsVisible = f.ByAbility.Count > 0;
            _combatFightInLabel.IsVisible = f.ByIncoming.Count > 0;
            FillList(_combatFightInList, f.ByIncoming.Select(x =>
                (x.Name, $"{x.Total:N0} - x{x.Hits} - avg {(double)x.Total / Math.Max(1, x.Hits):0.#}")));
        }
        text.Text = healing
            ? $"{f.Name} - {f.Healed:N0} healed - {f.Hps:0.#} hps over {f.DurationSeconds:0}s"
              + (f.InProgress ? " (fighting)" : "")
            : $"{f.Name} - {f.DamageOut:N0} dmg - {f.Dps:0.#} dps over {f.DurationSeconds:0}s"
              + $" - took {f.DamageIn:N0}"
              + (f.InProgress ? " (fighting)" : f.Outcome == "Killed" ? "" : $" - {f.Outcome}");
    }

    private void ProcessTrackedAlerts(StatsSnapshot s)
    {
        if (!_watcher.InitialIngestDone) return;
        if (_alertBaselinePath != _watcher.CurrentPath)
        {
            // First run isn't a character switch — cancelling here wiped cues recovered from
            // the log seconds earlier, which is the restart case they exist for.
            var switchedCharacter = _alertBaselinePath is not null;
            _alertBaselinePath = _watcher.CurrentPath;
            _ruleBaseline.Clear();
            foreach (var r in s.Tracked) _ruleBaseline[r.Id] = r.TotalQuantity;
            if (switchedCharacter) _delayedAlerts.CancelAll();
            _knownDeaths = s.Deaths.Count;
            return;
        }
        // Combat cues only: a respawn timer doesn't care that you died.
        if (s.Deaths.Count > _knownDeaths) _delayedAlerts.CancelCombatCues();
        _knownDeaths = s.Deaths.Count;

        foreach (var r in s.Tracked)
        {
            var baseline = _ruleBaseline.TryGetValue(r.Id, out var b) ? b : 0;
            if (r.TotalQuantity <= baseline)
            {
                _ruleBaseline[r.Id] = r.TotalQuantity;
                continue;
            }
            var delta = r.TotalQuantity - baseline;
            _ruleBaseline[r.Id] = r.TotalQuantity;
            var rule = _settings.TrackedRules.FirstOrDefault(x => x.Id == r.Id);
            if (rule is null) continue;
            // Text rules already alerted from the ingest thread the moment the line
            // arrived (OnTextMatched). The baseline above still had to move so this rule
            // doesn't look like a fresh burst later.
            if (rule.Kind == WatchKind.Text) continue;
            AlertOrCue(rule, r.Name,
                $"{r.LastItem ?? "match"}{(delta > 1 ? $" ×{delta}" : "")}",
                TimeSpan.FromSeconds(5));
        }
    }

    private void UpdateLoggingStatus()
    {
        DateTime? lastActivity = _watcher.LastGrowth;
        if (lastActivity is null && _watcher.CurrentPath is { } p && File.Exists(p))
            lastActivity = File.GetLastWriteTime(p);
        var age = lastActivity is { } t ? DateTime.Now - t : TimeSpan.MaxValue;
        var brush = age < TimeSpan.FromSeconds(30) ? AppTheme.GoodBrush : age < TimeSpan.FromMinutes(2) ? AppTheme.WarnBrush : AppTheme.BadBrush;
        var tip = lastActivity is { } la ? $"Last log activity: {la:h:mm:ss tt}" : "No log file activity yet";
        _statusDot.Fill = brush;
        _miniDot.Fill = brush;
        ToolTip.SetTip(_statusDot, tip);
        ToolTip.SetTip(_miniDot, tip);
        _logBanner.IsVisible = age > TimeSpan.FromMinutes(2);
    }

    private void SetMode(bool mini)
    {
        _settings.Minimized = mini;
        _miniRoot.IsVisible = mini;
        _normalRoot.IsVisible = !mini;
        Topmost = true;
        _settings.Save();
        if (mini) UpdateMiniChips(CurrentSnapshot());
    }

    private void UpdateMiniChips(StatsSnapshot s)
    {
        _miniChips.Children.Clear();
        var selected = MiniStatOrder.Where(_settings.MiniStats.Contains).ToList();
        foreach (var key in selected)
        {
            var text = key switch
            {
                "kills" => $"Kills {s.YourKillCount}",
                "dps" => s.CurrentDps > 0 ? $"{s.CurrentDps:0} dps" : $"{s.SessionDps:0} dps",
                "hps" => $"{s.Hps:0.#} hps",
                "loot" => $"Loot {s.LootTotal}",
                "money" => StatsSnapshot.FormatCoin(s.Copper),
                "xp" => $"{s.XpPercent:0.0}%" + (s.HoursToLevel is { } eta ? $" - lvl {FormatEta(eta)}" : ""),
                "deaths" => $"Deaths {s.Deaths.Count}",
                _ => "",
            };
            _miniChips.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppTheme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            });
        }
        // Per-rule pins, not every enabled rule: a mini bar with eight chips isn't a mini bar.
        var due = _delayedAlerts.NextDueByRule(DateTime.Now);
        foreach (var rule in _settings.PinWatchChips
                     ? _settings.TrackedRules.Where(r => r.Enabled && r.Pinned)
                     : [])
        {
            var name = rule.Name.Length > 0 ? rule.Name : rule.Pattern;
            var result = s.Tracked.FirstOrDefault(t => t.Id == rule.Id);
            // While a cue is counting down, when it fires is the only thing worth the space.
            var counting = due.TryGetValue(rule.Id, out var at);
            _miniChips.Children.Add(new TextBlock
            {
                Text = counting
                    ? $"{name} {EQBuddy.UI.Shared.Countdown.Format(at - DateTime.Now)}"
                    : $"Target {name} {result?.TotalQuantity ?? 0}",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = counting ? AppTheme.WarnBrush : AppTheme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            });
        }

        // Only when there is genuinely nothing to show — it used to return early when no
        // stats were starred, hiding pinned watch chips behind the hint.
        if (_miniChips.Children.Count == 0)
            _miniChips.Children.Add(AppTheme.DimText("* star stats in full view"));
    }

    private static string FormatEta(double hours) => hours >= 1
        ? $"~{(int)hours}h {(int)((hours - (int)hours) * 60)}m"
        : $"~{Math.Max(1, (int)(hours * 60))}m";

    private void OnOptions(object? sender, EventArgs e)
    {
        if (_optionsWindow is { IsVisible: true })
        {
            _optionsWindow.Activate();
            return;
        }
        _optionsWindow = new OptionsWindow(this);
        _optionsWindow.Closed += (_, _) => _alertWindow?.ExitPlacement();
        _optionsWindow.Show(this);
        AlertTile.EnterPlacement();
    }

    private void OnTutorial(object? sender, EventArgs e) => new TutorialWindow(this).Show(this);

    private void OnHistory(object? sender, EventArgs e)
    {
        _archiver.CheckpointSync(CurrentSnapshot());
        if (_historyWindow is { IsVisible: true })
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow(_repo);
        _historyWindow.Show();
    }

    private void DropCampMarker()
    {
        var s = CurrentSnapshot();
        _stats.AddMarker($"Marker {s.Markers.Count + 1}" +
            (s.CurrentZone.Length > 0 ? $" - {s.CurrentZone}" : ""));
    }

    /// <summary>Upstream deleted global hotkeys in 1.34 because Windows' RegisterHotKey is
    /// system-wide and swallowed Ctrl+Shift+T from every browser on the machine. This fork
    /// keeps them for Linux but ships every binding empty, so nothing is taken until the
    /// user types a combination into Options.</summary>
    private void RegisterGlobalHotkeys()
    {
        if (_hotkeys is not null) return;
        (string Spec, Action Action)[] specs =
        [
            (_settings.HotkeyToggleOverlay, ToggleOverlayVisibility),
            (_settings.HotkeyClickThrough, ToggleClickThrough),
            (_settings.HotkeyMiniMode, () => SetMode(!_settings.Minimized)),
            (_settings.HotkeyCampMarker, DropCampMarker),
        ];
        // Nothing bound means nothing to listen for: skip opening an X display and
        // starting a poll thread with an empty map, and skip the "hotkeys disabled"
        // error a Wayland session would otherwise log for a feature nobody asked for.
        if (specs.All(h => string.IsNullOrWhiteSpace(h.Spec))) return;
        try
        {
            _hotkeys = new X11HotkeyService(specs);
        }
        catch (Exception ex)
        {
            App.LogError($"Global hotkeys disabled: {ex.Message}");
        }
    }

    private void ToggleOverlayVisibility()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            Topmost = true;
            Activate();
        }
    }

    private void ToggleClickThrough()
    {
        var next = !_clickThrough;
        if (!X11ClickThrough.Set(this, next)) return;
        _clickThrough = next;
        // The stacks vanish from the mouse alongside the widget: half a click-through
        // overlay is worse than none, because the part that still catches clicks is the
        // part parked over the game.
        _mezWindow?.ApplyClickThrough(next);
        _hotWindow?.ApplyClickThrough(next);
        _chipsWindow?.ApplyClickThrough(next);
        _root.BorderBrush = _clickThrough ? AppTheme.WarnBrush : AppTheme.BorderBrush;
        Topmost = true;
        // With the hotkey unbound there is no key to name, and "press  to interact again"
        // is worse than saying nothing: point at the menu item that always works.
        ToolTip.SetTip(_root, !_clickThrough ? null
            : string.IsNullOrWhiteSpace(_settings.HotkeyClickThrough)
                ? "Click-through ON - unlock from the right-click menu"
                : $"Click-through ON - press {_settings.HotkeyClickThrough} to interact again");
        _clickThroughItem.IsChecked = _clickThrough;
    }

    private void OnGear(object? sender, EventArgs e) => _root.ContextMenu?.Open(_root);

    private void OnStarChanged(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        var btn = (Button)sender!;
        var key = (string)btn.Tag!;
        if (_settings.MiniStats.Contains(key))
        {
            _settings.MiniStats.Remove(key);
        }
        else
        {
            _settings.MiniStats.Add(key);
        }
        UpdateStarVisuals();
        _settings.Save();
    }

    private void UpdateStarVisuals()
    {
        foreach (var star in _stars.Values)
        {
            var isSelected = _settings.MiniStats.Contains((string)star.Tag!);
            star.Content = AppTheme.Icon(isSelected ? AppIcon.StarFilled : AppIcon.Star, isSelected ? AppTheme.AccentBrush : AppTheme.DimBrush, 13);
        }
    }

    private void CheckForUpdates(bool manual)
    {
        Task.Run(async () =>
        {
            var folder = UpdateChecker.FindUpdateFolder(_settings.UpdateFolder);
            var info = await UpdateChecker.FindBestAsync(_settings.UpdateFolder);
            Dispatcher.UIThread.Post(() =>
            {
                if (_installingUpdate) return;
                if (info is not null && UpdateChecker.IsNewer(info))
                {
                    _pendingUpdate = info;
                    // "Click here to install" is only true where the staged installer can
                    // actually run (Windows); Linux always goes to the download page.
                    _updateText.Text = OperatingSystem.IsWindows()
                            && (info.SetupPath is not null || info.DownloadUrl is not null)
                        ? $"Update v{info.Latest} is ready - click here to install."
                        : $"Update v{info.Latest} is available - click to open the download page.";
                    _updateBanner.IsVisible = true;
                }
                else if (manual)
                {
                    _pendingUpdate = null;
                    _updateText.Text = info is null && folder is null
                        ? "Couldn't check for updates (no update folder, GitHub unreachable)."
                        : $"You're up to date (v{UpdateChecker.CurrentVersion}).";
                    _updateBanner.IsVisible = true;
                    _upToDateNoticeUntil = DateTime.Now.AddSeconds(6);
                }
            });
        });
    }

    internal static readonly (string Name, string File)[] AlertSounds =
    [
        ("Ding", "bell.oga"),
        ("Notify", "message-new-instant.oga"),
        ("Chimes", "service-login.oga"),
        ("Chord", "device-added.oga"),
        ("Tada", "complete.oga"),
        ("Exclamation", "dialog-warning.oga"),
        ("Alarm", "alarm-clock-elapsed.oga"),
    ];

    internal void PlayAlertSound() => PlayAlertSound(_settings.AlertSound);

    /// <summary>
    /// Play a specific sound: a built-in name, or the full path of a custom file. The
    /// argument exists so per-rule sounds work — the point of giving each rule its own sound
    /// is telling them apart by ear, which a single shared sound can't do.
    /// With <paramref name="coalesce"/> on, sounds within <see cref="EQBuddy.UI.Shared.SoundGate.Window"/>
    /// of the last are dropped — several rules firing together are one audio alert (here they
    /// would literally overlap, one player process per sound). Previews keep coalesce off.
    /// </summary>
    internal void PlayAlertSound(string choiceOrPath, bool coalesce = false)
    {
        if (coalesce && !_soundGate.TryClaim(DateTime.Now)) return;
        try
        {
            var choice = choiceOrPath switch
            {
                "Asterisk" or "" => "Ding",
                "Beep" => "Chord",
                "Hand" => "Chimes",
                "Question" => "Notify",
                { } other => other,
            };
            var named = Array.Find(AlertSounds, x => x.Name == choice);
            var file = named.File is { } systemFile ? FindFreeDesktopSound(systemFile) : choice;
            if (file.Length > 0 && File.Exists(file))
            {
                if (TryStart("pw-play", file) || TryStart("paplay", file) || TryStart("aplay", file))
                    return;
            }
            Console.Beep();
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    private static string FindFreeDesktopSound(string fileName)
    {
        var dataDirs = new List<string>();
        var userData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(userData))
            dataDirs.Add(userData);
        else
            dataDirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"));

        var systemData = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        dataDirs.AddRange(string.IsNullOrWhiteSpace(systemData)
            ? ["/usr/local/share", "/usr/share"]
            : systemData.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var dataDir in dataDirs)
        {
            var path = System.IO.Path.Combine(dataDir, "sounds", "freedesktop", "stereo", fileName);
            if (File.Exists(path)) return path;
        }
        return "";
    }

    private static bool TryStart(string command, string file)
    {
        try
        {
            var start = new ProcessStartInfo(command) { UseShellExecute = false };
            start.ArgumentList.Add(file);
            Process.Start(start);
            return true;
        }
        catch { return false; }
    }

    private void OnUpdateBannerClick(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (_pendingUpdate is not { } info || _installingUpdate) return;

        // The staged file is always a Windows EQBuddySetup.exe run with an Inno Setup
        // /SILENT flag — there's nothing installable that way on Linux, so a GitHub-sourced
        // update there always goes to the release page, same as when no installer asset
        // is attached at all. OneDrive-sourced updates (SetupPath) predate this and are a
        // Windows-only distribution channel already, so they're unaffected by this check.
        var canAutoInstall = OperatingSystem.IsWindows() && (info.SetupPath is not null || info.DownloadUrl is not null);
        if (!canAutoInstall)
        {
            try
            {
                Process.Start(new ProcessStartInfo(UpdateChecker.GitHubLatestPage) { UseShellExecute = true });
                _pendingUpdate = null;
                // On Linux the setup exe means nothing — say what actually works there
                // (issue #30: the old text told Linux users to run a Windows installer).
                _updateText.Text = OperatingSystem.IsWindows()
                    ? "Download page opened - run the new EQBuddySetup.exe to update."
                    : "Download page opened - get EQBuddy-linux-x64.tar.gz and extract it over this install.";
                _upToDateNoticeUntil = DateTime.Now.AddSeconds(10);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                _updateText.Text = $"Couldn't open browser - visit {UpdateChecker.GitHubLatestPage}";
            }
            return;
        }
        _installingUpdate = true;
        _updateText.Text = info.DownloadUrl is not null
            ? "Downloading update - EQBuddy will restart itself..."
            : "Installing update - EQBuddy will restart itself...";
        Task.Run(async () =>
        {
            try
            {
                var staged = await UpdateChecker.StageForInstall(info);
                Process.Start(staged, "/SILENT");
                Dispatcher.UIThread.Post(Shutdown);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                Dispatcher.UIThread.Post(() =>
                {
                    _installingUpdate = false;
                    _updateText.Text = "Update failed to start - see error.log.";
                });
            }
        });
    }

    /// <summary>Details-style breakdown whose displayed rate follows parser convention:
    /// source total divided by total combat time. The source's active-time burst rate
    /// remains available in the row tooltip.</summary>
    private void FillBreakdown(ItemsControl list, IEnumerable<SourceDamage> stats,
        StatSort sort, double combatSeconds, string rateLabel)
    {
        var secs = Math.Max(1, combatSeconds);
        static double Avg(SourceDamage d) => (double)d.Total / Math.Max(1, d.Hits);
        double Rate(SourceDamage d) => d.Total / secs;
        var sorted = (sort switch
        {
            StatSort.Hits => stats.OrderByDescending(d => d.Hits),
            StatSort.Avg => stats.OrderByDescending(Avg),
            StatSort.Rate => stats.OrderByDescending(Rate),
            _ => stats.OrderByDescending(d => d.Total),
        }).ToList();
        if (sorted.Count == 0)
        {
            list.ItemsSource = Array.Empty<Control>();
            return;
        }

        var grand = Math.Max(1, sorted.Sum(d => d.Total));
        Func<SourceDamage, double> metric = sort switch
        {
            StatSort.Hits => d => d.Hits,
            StatSort.Avg => Avg,
            StatSort.Rate => Rate,
            _ => d => d.Total,
        };
        var topMetric = Math.Max(1e-9, sorted.Max(metric));
        var barBrush = AccentBarBrush();
        list.ItemsSource = sorted.Select(d =>
        {
            var critPart = d.Crits > 0
                ? $" - {100.0 * d.Crits / Math.Max(1, d.Hits):0}% crit"
                : "";
            var value = $"{d.Total:N0} - ×{d.Hits} - avg {Avg(d):0.#} - {Rate(d):0.#} {rateLabel}{critPart}";
            var tooltip = $"{100.0 * d.Total / grand:0.#}% of total - {rateLabel} = total / {secs:0}s in combat" +
                (d.ActiveSeconds > 0
                    ? $" - burst {d.Total / Math.Max(1, d.ActiveSeconds):0.#}/s over the ~{d.ActiveSeconds:0}s it was in use"
                    : "");
            return BarRow(d.Name, value, metric(d) / topMetric, barBrush, tooltip);
        }).ToList();
    }

    private static Grid BarRow(string name, string value, double fraction, IBrush barBrush, string? tooltip)
    {
        fraction = Math.Clamp(fraction, 0.004, 1.0);
        var row = new Grid
        {
            Margin = new Thickness(0, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var bar = new Border
        {
            Background = barBrush,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        row.SizeChanged += (_, args) => bar.Width = Math.Max(0, args.NewSize.Width * fraction);
        row.Children.Add(bar);

        var content = new Grid { Margin = new Thickness(4, 1, 0, 1) };
        content.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        content.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = AppTheme.TextBrush,
        });
        var right = new TextBlock
        {
            Text = value,
            FontSize = 11,
            Foreground = AppTheme.DimBrush,
            Margin = new Thickness(8, 1, 2, 0),
        };
        Grid.SetColumn(right, 1);
        content.Children.Add(right);
        row.Children.Add(content);
        if (tooltip is not null) ToolTip.SetTip(row, tooltip);
        return row;
    }

    private static SolidColorBrush AccentBarBrush()
    {
        var accent = ((SolidColorBrush)AppTheme.AccentBrush).Color;
        return new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
    }

    private void FillStatList(ItemsControl list, IEnumerable<SourceDamage> stats, StatSort sort, string unit)
    {
        var sorted = sort switch
        {
            StatSort.Hits => stats.OrderByDescending(d => d.Hits),
            StatSort.Avg => stats.OrderByDescending(d => (double)d.Total / d.Hits),
            _ => stats.OrderByDescending(d => d.Total),
        };
        FillList(list, sorted.Select(d => (d.Name, $"{d.Total:N0} - {d.Hits} {unit}{(d.Hits == 1 ? "" : "s")} - avg {(double)d.Total / d.Hits:0.#}")));
    }

    private static StatSort ParseSort(object sender) => (string)((TextBlock)sender).Tag! switch
    {
        "hits" => StatSort.Hits,
        "avg" => StatSort.Avg,
        "rate" => StatSort.Rate,
        _ => StatSort.Total,
    };

    private static void SetSortVisual(StatSort mode, TextBlock total, TextBlock hits, TextBlock avg,
        TextBlock? rate = null)
    {
        total.Foreground = mode == StatSort.Total ? AppTheme.AccentBrush : AppTheme.DimBrush;
        hits.Foreground = mode == StatSort.Hits ? AppTheme.AccentBrush : AppTheme.DimBrush;
        avg.Foreground = mode == StatSort.Avg ? AppTheme.AccentBrush : AppTheme.DimBrush;
        if (rate is not null)
            rate.Foreground = mode == StatSort.Rate ? AppTheme.AccentBrush : AppTheme.DimBrush;
    }

    private void OnSortDmgOut(object? sender, PointerPressedEventArgs e)
    {
        _dmgOutSort = ParseSort(sender!);
        SetSortVisual(_dmgOutSort, _dmgOutSortTotal, _dmgOutSortHits, _dmgOutSortAvg, _dmgOutSortDps);
        RefreshUi();
    }

    private void OnSortDmgIn(object? sender, PointerPressedEventArgs e)
    {
        _dmgInSort = ParseSort(sender!);
        SetSortVisual(_dmgInSort, _dmgInSortTotal, _dmgInSortHits, _dmgInSortAvg);
        RefreshUi();
    }

    private void OnSortHeal(object? sender, PointerPressedEventArgs e)
    {
        _healSort = ParseSort(sender!);
        SetSortVisual(_healSort, _healSortTotal, _healSortHits, _healSortAvg, _healSortHps);
        RefreshUi();
    }

    private static void FillList(ItemsControl list, IEnumerable<(string Name, string Value)> rows, Func<string, IBrush>? valueBrush = null)
    {
        list.ItemsSource = rows.Select(row =>
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.Children.Add(new TextBlock
            {
                Text = row.Name,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = AppTheme.TextBrush,
                Margin = new Thickness(0, 1, 8, 1),
            });
            var right = new TextBlock
            {
                Text = row.Value,
                FontSize = 12,
                Foreground = valueBrush?.Invoke(row.Value) ?? AppTheme.DimBrush,
            };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }).ToList();
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2 && _miniRoot.IsVisible)
        {
            SetMode(false);
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>Saves the widget's position while the platform window still exists.
    /// OnClosed (below) used to do this, but OnClosed fires after the window is torn down,
    /// so Position had already collapsed to (0,0) by the time it read it — a widget dragged
    /// to another monitor silently forgot and reopened wherever it started. Closing fires
    /// first, with the real position still in hand; the chip stacks (SavePosition, called
    /// from their own Closing handlers) already worked this way.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Only while the platform window is still there. OnClosed calls Shutdown(), which
        // asks the lifetime to close every window and re-enters OnClosing on this
        // already-destroyed one - where Position has collapsed to (0,0) and the second pass
        // overwrote the good save from the first. That is what actually sent the widget back
        // to the launch monitor: the save had run correctly a moment earlier.
        if (TryGetPlatformHandle() is not null)
        {
            _settings.WindowLeft = Position.X;
            _settings.WindowTop = Position.Y;
            _settings.Save();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        if (_clickThrough)
            X11ClickThrough.Set(this, enabled: false);
        _hotkeys?.Dispose();
        _alertWindow?.Close();
        // Every window the widget owns goes with it — a stack left standing keeps the
        // process alive after the widget is gone.
        _mezWindow?.Close();
        _hotWindow?.Close();
        _chipsWindow?.Close();
        _spawnsWindow?.Close();
        _archiver.FinalizeActiveSync(CurrentSnapshot(), "ApplicationExit");
        _watcher.Dispose();
        _repo.Dispose();
        base.OnClosed(e);
        Shutdown();
    }

    private static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private static Ellipse Dot() => new()
    {
        Width = 9,
        Height = 9,
        Fill = AppTheme.BadBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Takes an already-translucent wash brush (AppTheme.GoodWashBrush/WarnWashBrush)
    // directly rather than deriving one, so a live theme switch repaints it — the brush
    // reference is the same instance AppTheme.Apply mutates in place.
    private static Border Banner(IBrush brush) => new()
    {
        Background = brush,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 6),
        IsVisible = false,
    };
}
