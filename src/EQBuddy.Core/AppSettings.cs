using System.IO;
using System.Text.Json;

namespace EQBuddy.Core;

public sealed class AppSettings
{
    public string? LogFolder { get; set; }
    /// <summary>Folder holding EQBuddySetup.exe for updates; null = auto-detect OneDrive.</summary>
    public string? UpdateFolder { get; set; }
    public bool Minimized { get; set; }
    public List<string> MiniStats { get; set; } = ["kills", "dps"];
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double Opacity { get; set; } = 0.96;
    public double UiScale { get; set; } = 1.0;
    /// <summary>Opacity of the widget's background panel only — text stays fully opaque.</summary>
    public double BackgroundOpacity { get; set; } = 0.95;
    /// <summary>Empty finished-session logs automatically. Off = logs grow forever
    /// (for players who upload their logs elsewhere).</summary>
    public bool TruncateLogs { get; set; } = true;
    /// <summary>User-defined tracked-loot rules (TRACK-018: persisted).</summary>
    public List<TrackedRule> TrackedRules { get; set; } = [];
    /// <summary>Highest version of the built-in default watch rules already applied.
    /// Bumping <see cref="CurrentDefaultRulesVersion"/> hands new defaults to existing
    /// installs exactly once, and never re-adds a rule the user deleted on purpose.</summary>
    public int DefaultRulesVersion { get; set; }
    /// <summary>Options window width, dragged by its right edge. Wide enough by default
    /// that the watch-rule row (kind + name + spell class + match text + toggles) fits
    /// without clipping.</summary>
    public double OptionsWidth { get; set; } = 420;
    /// <summary>Default rolling window for "recent" rates, in minutes (5/15/30).</summary>
    public int RecentWindowMinutes { get; set; } = 15;
    /// <summary>Alert sound: a built-in name (Ding, Notify, Chimes, Chord, Tada,
    /// Exclamation, Alarm) or the full path of a custom .wav/.mp3 file.</summary>
    public string AlertSound { get; set; } = "Ding";
    /// <summary>Position of the floating alert tile; NaN = above the widget.</summary>
    public double AlertLeft { get; set; } = double.NaN;
    public double AlertTop { get; set; } = double.NaN;
    /// <summary>Master switch for watch chips in the mini dashboard. Which rules appear is
    /// then per-rule (<see cref="TrackedRule.Pinned"/>): showing every enabled rule was
    /// all-or-nothing, and a mini bar with eight chips on it isn't a mini bar.</summary>
    public bool PinWatchChips { get; set; }
    /// <summary>Whether the one-time "pin everything you were already seeing" pass has run.
    /// A flag rather than inferring it from "nothing is pinned", so deliberately unpinning
    /// every rule isn't undone at the next launch.</summary>
    public bool WatchPinsMigrated { get; set; }
    /// <summary>Whether the watch-rule examples panel in Options is expanded. Remembered so
    /// someone still learning the feature doesn't have to reopen it every time, and someone
    /// who doesn't need it never sees it again.</summary>
    public bool ShowWatchGuide { get; set; }
    /// <summary>Which of the Combat/Healing subsections are expanded. Separate per card and
    /// per section, because the reason to collapse one isn't the reason to collapse another:
    /// a melee player may want the fight breakdown open and the session one shut, and a
    /// healer the reverse. Default open — a new subsection nobody can see is a wasted one.</summary>
    public bool ShowCombatFight { get; set; } = true;
    public bool ShowCombatSession { get; set; } = true;
    /// <summary>Pet abilities breakdown expanded on the Combat card. Default collapsed
    /// (discussion #28): the pet's overall damage is already a row in the main list,
    /// and a pet class fighting all session got a wall of ability rows for free.</summary>
    public bool ShowPetAbilities { get; set; }
    public bool ShowHealFight { get; set; } = true;
    public bool ShowHealSession { get; set; } = true;
    /// <summary>Show the quick tour at every launch. Turned off by the tutorial's
    /// "Never show again" button or the Options checkbox. While on, the startup
    /// janitor defers log truncation — the tour's first page is its consent question.</summary>
    public bool ShowTutorial { get; set; } = true;
    /// <summary>Overlay card order (section keys); missing keys append in default order.</summary>
    public List<string> SectionOrder { get; set; } = [];
    /// <summary>Hidden overlay cards (still collect data — OVERLAY acceptance).</summary>
    public List<string> HiddenSections { get; set; } = [];
    /// <summary>Global hotkeys ("Ctrl+Shift+H" style; empty disables one).</summary>
    public string HotkeyToggleOverlay { get; set; } = "Ctrl+Shift+H";
    public string HotkeyClickThrough { get; set; } = "Ctrl+Shift+T";
    public string HotkeyMiniMode { get; set; } = "Ctrl+Shift+M";
    public string HotkeyCampMarker { get; set; } = "Ctrl+Shift+K";
    /// <summary>Color theme key (see EQBuddy.UI.Shared.ThemeCatalog); defaults to the
    /// original parchment-and-brass look so existing installs don't change on upgrade.</summary>
    public string Theme { get; set; } = "ParchmentBrass";

    /// <summary>The three colors behind the "Custom" theme (#RRGGBB); the rest of its
    /// palette is derived in EQBuddy.UI.Shared.CustomTheme. Null until first edited —
    /// the seed colors apply.</summary>
    public string? CustomThemeBg { get; set; }
    public string? CustomThemeText { get; set; }
    public string? CustomThemeAccent { get; set; }

    /// <summary>The newest version whose "What's new" notes this install has shown.
    /// Empty on installs from before the feature: those get just the current version's
    /// notes once (if the tutorial was already done — a fresh install skips notes
    /// entirely; onboarding belongs to the tutorial).</summary>
    public string LastSeenVersion { get; set; } = "";

    // ---- spawn timers (the Spawns window) ----
    /// <summary>Track named-mob spawn timers; the Spawns window opens whenever this is on.
    /// Default ON (David's call): the window is the feature's front door, and a default-off
    /// window behind a right-click menu is a feature nobody's family finds. Closing the
    /// window opts out, and that sticks.</summary>
    public bool TrackSpawns { get; set; } = true;
    public double SpawnLeft { get; set; } = double.NaN;
    public double SpawnTop { get; set; } = double.NaN;
    /// <summary>Follow the zone the log says the player is in; off = stay on the zone
    /// picked in the window's dropdown.</summary>
    public bool SpawnFollowZone { get; set; } = true;
    /// <summary>One-time repair (1.20.1): 1.20.0 could untick SpawnFollowZone on a
    /// selection event the user never made, so following silently died. The auto-untick
    /// is gone; this restores the default once for anyone the bug touched.</summary>
    public bool SpawnFollowRepaired { get; set; }
    /// <summary>Last manually-picked zone, for when SpawnFollowZone is off.</summary>
    public string SpawnZone { get; set; } = "";
    /// <summary>UNUSED since 1.23.0 (kept so older settings.json round-trips): spawn
    /// "Default" now follows <see cref="AlertSound"/>, the same default watch rules use —
    /// a second spawn-specific default made "Default" mean silence, which read as broken.</summary>
    public string SpawnSound { get; set; } = "Off";
    /// <summary>Position of the spawn-chicklet stack; NaN = a default spot near the
    /// top-left, clear of the widget's home edge.</summary>
    public double SpawnChipsLeft { get; set; } = double.NaN;
    public double SpawnChipsTop { get; set; } = double.NaN;

    /// <summary>Position of the mez-chip stack — its own window, deliberately separate
    /// from the spawn chips (mez chips are combat-urgent, spawn chips are ambient).</summary>
    public double MezChipsLeft { get; set; } = double.NaN;
    public double MezChipsTop { get; set; } = double.NaN;

    /// <summary>Position of the heal-over-time chip stack; its own window again, since a
    /// healer parks "who am I keeping up" somewhere different from the mez stack.</summary>
    public double HotChipsLeft { get; set; } = double.NaN;
    public double HotChipsTop { get; set; } = double.NaN;
    /// <summary>Show a chip for your HoT on YOURSELF. On by default — recasting on time is
    /// the whole point — but a healer who mostly self-HoTs sees that chip constantly, and
    /// your own buff bar already shows it, so it can be turned off. Self chips render in
    /// the "good" colour either way, to separate them at a glance from the ones on people
    /// whose buff bar you cannot see.</summary>
    public bool ShowSelfHotChips { get; set; } = true;

    private static string FilePath => AppPaths.File("settings.json");

    // NaN is a legitimate value here ("not placed yet" window positions), and the
    // default serializer refuses it — which made Save() throw and silently drop
    // every settings change on profiles with an unplaced window.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Bump when adding a built-in rule; see <see cref="DefaultRulesVersion"/>.</summary>
    private const int CurrentDefaultRulesVersion = 1;

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex); // corrupted settings — start fresh, but say so
            settings = new AppSettings();
        }
        // Non-short-circuiting on purpose: rules saved before ids existed get theirs
        // assigned at construction, and persisting them NOW is what makes the id stable
        // across restarts rather than re-rolled every launch until some unrelated edit
        // happens to save settings.
        if (settings.ApplyDefaultRules() | settings.TrackedRules.Any(r => r.IdWasGenerated))
            settings.Save();
        return settings;
    }

    /// <summary>
    /// Adds built-in watch rules that ship enabled. A charm or mez breaking is the one
    /// event where finding out late is expensive — and you are looking at the game, not
    /// the widget — so both the banner and the sound are on out of the box rather than
    /// waiting for the player to discover watch rules and configure one.
    ///
    /// Everything about it stays editable: 🔔 and 🔊 toggle per rule, the class filter and
    /// name are editable, the whole rule can be deleted (and stays deleted), and the sound
    /// itself is the shared <see cref="AlertSound"/> choice.
    ///
    /// Runs once per version — deleting the rule makes it stay deleted.
    /// Returns true when something changed and the settings need saving.
    /// </summary>
    public bool ApplyDefaultRules()
    {
        if (DefaultRulesVersion >= CurrentDefaultRulesVersion) return false;
        if (DefaultRulesVersion < 1 &&
            !TrackedRules.Any(r => r.Kind == WatchKind.SpellFade &&
                                   r.SpellFilter == SpellFilter.AnyCrowdControl))
        {
            TrackedRules.Add(new TrackedRule
            {
                Name = "CC broke",
                Kind = WatchKind.SpellFade,
                SpellFilter = SpellFilter.AnyCrowdControl,
                AlertBanner = true,
                AlertSound = true,
            });
        }
        DefaultRulesVersion = CurrentDefaultRulesVersion;
        return true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex); // non-fatal, but visible
        }
    }
}
