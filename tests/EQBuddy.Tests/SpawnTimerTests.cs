using EQBuddy.Core;
using EQBuddy.UI.Shared;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// Spawn timers: the shipped catalog, kill matching (named + placeholder, zone-gated,
/// per-server), countdown lifecycle, player overrides, and the window's view model.
/// </summary>
public class SpawnTimerTests
{
    private static readonly DateTime T0 = new(2026, 7, 18, 15, 0, 0);

    private static SpawnCatalog TestCatalog() => new()
    {
        Zones =
        [
            new SpawnZone
            {
                Zone = "Lower Guk",
                LogZoneName = "The Ruins of Old Guk",
                NamedDefaultSeconds = 1680,
                Named =
                [
                    new SpawnEntry { Name = "a froglok ghoul lord", RespawnSeconds = 1620 },
                    new SpawnEntry { Name = "the ghoul arch magi", Placeholder = "kor ghoul wizard" },
                ],
            },
            new SpawnZone
            {
                Zone = "Permafrost Keep",
                Named = [new SpawnEntry { Name = "Lady Vox", RespawnSeconds = 604800, Variance = "±8h" }],
            },
        ],
    };

    private static SpawnTimers Tracker(SpawnOverrides? overrides = null, string? path = null) =>
        new(TestCatalog(), overrides ?? new SpawnOverrides(), path) { Server = "freeport" };

    // ---- the shipped catalog ----

    [Fact]
    public void EmbeddedCatalogLoadsAndIsComprehensive()
    {
        var cat = SpawnCatalog.LoadEmbedded();
        Assert.True(cat.Zones.Count >= 100, $"only {cat.Zones.Count} zones");
        Assert.True(cat.Zones.Sum(z => z.Named.Count) >= 800, "named entries went missing");
        // Every zone parses; no entry has a negative or absurd timer (8 days is the
        // ceiling anything documented reaches).
        foreach (var z in cat.Zones)
        foreach (var n in z.Named)
            if (n.RespawnSeconds is { } s)
                Assert.InRange(s, 30, 8 * 86400);
    }

    [Fact]
    public void FindZoneShrugsOffArticlesAndLogNames()
    {
        var cat = SpawnCatalog.LoadEmbedded();
        Assert.NotNull(cat.FindZone("Estate of Unrest"));
        Assert.NotNull(cat.FindZone("The Estate of Unrest"));
        Assert.NotNull(cat.FindZone("Lower Guk"));
    }

    /// <summary>EQ Legends runs difficulty-tier instances of a zone — the log says
    /// "Befallen 1 (Awakened)" or "Befallen 4 (Refined)" (both observed in
    /// eqlog_Hugzee). They resolve to the base zone so Follow and kill matching keep
    /// working there.</summary>
    [Theory]
    [InlineData("Befallen 1 (Awakened)", "Befallen")]
    [InlineData("Befallen 4 (Refined)", "Befallen")]
    [InlineData("Befallen 2", "Befallen")]
    public void DifficultyTierZonesResolveToTheirBase(string logZone, string expected)
    {
        var cat = SpawnCatalog.LoadEmbedded();
        Assert.Equal(expected, cat.FindZone(logZone)?.Zone);
    }

    [Theory]
    [InlineData("Befallen 1 (Awakened)", "Befallen")]
    [InlineData("Clan Crushbone 2 (Adaptive)", "Clan Crushbone")]
    [InlineData("Befallen", "Befallen")]
    [InlineData("Solusek's Eye", "Solusek's Eye")]   // no tier suffix — unchanged
    public void TierVariantStrippingIsConservative(string input, string expected) =>
        Assert.Equal(expected, SpawnCatalog.StripTierVariant(input));

    [Theory]
    [InlineData("a froglok ghoul lord", "froglok ghoul lord", true)]   // article
    [InlineData("orc centurions", "orc centurion", true)]              // plural note
    [InlineData("Lady Vox", "lady vox", true)]                         // case
    [InlineData("a froglok ghoul lord", "froglok ghoul", false)]       // prefix is not a match
    [InlineData("Skeleton Lrodd", "Skeleton L`rodd", true)]            // wikis drop the EQ backtick
    [InlineData("Asaka LRei", "Asaka L`Rei", true)]
    [InlineData("", "anything", false)]
    public void NameMatchingIsForgivingButNotFuzzy(string catalogName, string killed, bool expected) =>
        Assert.Equal(expected, SpawnCatalog.NameMatches(catalogName, killed));

    /// <summary>Fuzzy matching absorbs wiki typos (the Velious page spells Keljemor
    /// "Leljemor") but stays bounded: short names never fuzz, and unrelated names
    /// never collide.</summary>
    [Theory]
    [InlineData("Leljemor", "Keljemor", true)]          // one-letter wiki typo
    [InlineData("Kriegara", "Krigara", true)]           // dropped letter
    [InlineData("Red V", "Red X", false)]               // short names: exact only
    [InlineData("Emperor Crush", "Ambassador D`Vinn", false)]
    [InlineData("Gynok Moltor", "Gynok Molto", true)]   // truncated log capture
    public void FuzzyMatchingToleratesTyposWithoutInventingThem(string a, string b, bool expected) =>
        Assert.Equal(expected, SpawnCatalog.NameMatchesFuzzy(a, b));

    [Fact]
    public void ExactCatalogEntriesAlwaysBeatFuzzyOnes()
    {
        var catalog = new SpawnCatalog
        {
            Zones =
            [
                new SpawnZone
                {
                    Zone = "Testzone", NamedDefaultSeconds = 600,
                    Named =
                    [
                        // The typo'd entry sits FIRST — order must not decide.
                        new SpawnEntry { Name = "Gynok Molto" },
                        new SpawnEntry { Name = "Gynok Moltor" },
                    ],
                },
            ],
        };
        var t = new SpawnTimers(catalog, new SpawnOverrides()) { Server = "freeport" };
        t.Apply(new ZoneEvent(T0, "Testzone"));
        t.Apply(new KillEvent(T0, "Gynok Moltor", "You"));

        Assert.Equal("Gynok Moltor", Assert.Single(t.Snapshot(T0)).Name);
    }

    // ---- kill-driven timers ----

    [Fact]
    public void AKillInTheCurrentZoneStartsTheCountdown()
    {
        var t = Tracker();
        t.Apply(new ZoneEvent(T0, "The Ruins of Old Guk"));
        t.Apply(new KillEvent(T0.AddMinutes(1), "froglok ghoul lord", "You"));

        var timer = Assert.Single(t.Snapshot(T0.AddMinutes(2)));
        Assert.Equal("a froglok ghoul lord", timer.Name);
        Assert.Equal(T0.AddMinutes(1).AddSeconds(1620), timer.DueAt);
    }

    [Fact]
    public void KillingThePlaceholderRunsTheSameClock()
    {
        var t = Tracker();
        t.Apply(new ZoneEvent(T0, "Lower Guk"));
        t.Apply(new KillEvent(T0, "kor ghoul wizard", "Lizzid"));

        var timer = Assert.Single(t.Snapshot(T0.AddMinutes(1)));
        Assert.Equal("the ghoul arch magi", timer.Name);
        // No per-mob timer documented — the zone's named default carries it.
        Assert.Equal(T0.AddSeconds(1680), timer.DueAt);
    }

    [Fact]
    public void KillsMatchNothingWithoutAZoneAndNothingAcrossZones()
    {
        var t = Tracker();
        t.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));      // no zone yet
        Assert.Empty(t.Snapshot(T0));

        t.Apply(new ZoneEvent(T0, "Permafrost Keep"));
        t.Apply(new KillEvent(T0.AddMinutes(1), "froglok ghoul lord", "You")); // wrong zone
        Assert.Empty(t.Snapshot(T0.AddMinutes(1)));
    }

    [Fact]
    public void ReplayingTheLogNeverRewindsATimer()
    {
        var t = Tracker();
        t.Apply(new ZoneEvent(T0, "Lower Guk"));
        t.Apply(new KillEvent(T0.AddMinutes(5), "froglok ghoul lord", "You"));
        // Startup ingest replays the same kill, then an older one from earlier in the log.
        t.Apply(new KillEvent(T0.AddMinutes(5), "froglok ghoul lord", "You"));
        t.Apply(new KillEvent(T0.AddMinutes(2), "froglok ghoul lord", "You"));

        var timer = Assert.Single(t.Snapshot(T0.AddMinutes(6)));
        Assert.Equal(T0.AddMinutes(5), timer.KilledAt);

        // A genuinely newer kill restarts the clock.
        t.Apply(new KillEvent(T0.AddMinutes(30), "froglok ghoul lord", "You"));
        Assert.Equal(T0.AddMinutes(30), Assert.Single(t.Snapshot(T0.AddMinutes(31))).KilledAt);
    }

    [Fact]
    public void TimersArePerServer()
    {
        var t = Tracker();
        t.Apply(new ZoneEvent(T0, "Lower Guk"));
        t.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));

        t.Server = "qeynos";   // character switch to another server
        Assert.Empty(t.Snapshot(T0.AddMinutes(1)));
        t.Server = "freeport";
        Assert.Single(t.Snapshot(T0.AddMinutes(1)));
    }

    [Fact]
    public void AnOverriddenDurationBeatsTheCatalog()
    {
        var overrides = new SpawnOverrides();
        overrides.GetOrAdd("Lower Guk", "a froglok ghoul lord").RespawnSeconds = 2000;
        var t = Tracker(overrides);
        t.Apply(new ZoneEvent(T0, "Lower Guk"));
        t.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));

        Assert.Equal(T0.AddSeconds(2000), Assert.Single(t.Snapshot(T0)).DueAt);
    }

    [Fact]
    public void ManualStartAndDurationEditsRederiveTheCountdown()
    {
        var t = Tracker();
        t.StartManual("Permafrost Keep", "Lady Vox", 604800, elapsed: TimeSpan.FromHours(2));

        var timer = Assert.Single(t.Snapshot(DateTime.Now));
        Assert.True(timer.DueAt < DateTime.Now.AddDays(7));

        t.SetDuration("Permafrost Keep", "Lady Vox", 3 * 86400);
        Assert.Equal(timer.KilledAt.AddDays(3), Assert.Single(t.Snapshot(DateTime.Now)).DueAt);
    }

    /// <summary>DUE shows for one minute, then the timer clears itself — if nobody
    /// clicked it away, they've moved on and a stale DUE tells them nothing.</summary>
    [Fact]
    public void DueTimersShowForAMinuteThenDrop()
    {
        var t = Tracker();
        t.Apply(new ZoneEvent(T0, "Lower Guk"));
        t.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));    // 27 min timer

        var due = T0.AddSeconds(1620);
        Assert.Single(t.Snapshot(due.AddSeconds(30)));    // DUE, within the minute
        Assert.Empty(t.Snapshot(due.AddSeconds(61)));     // cleaned itself up
    }

    [Fact]
    public void TimersSurviveARestartThroughThePersistFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spawn-timers-{Guid.NewGuid():N}.json");
        try
        {
            var t = Tracker(path: path);
            t.Apply(new ZoneEvent(T0, "Lower Guk"));
            t.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));

            var reborn = Tracker(path: path);
            var timer = Assert.Single(reborn.Snapshot(T0.AddMinutes(1)));
            Assert.Equal(T0, timer.KilledAt);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Timers tighten themselves from play: a re-kill sooner than the timer
    /// <summary>David's rule (2026-08-04, Orc Taskmaster running a learned 328s under
    /// Crushbone's MEASURED 738s clock): a trusted timer disables re-kill learning —
    /// a shorter gap against a measurement is multi-spawn noise (two taskmasters at
    /// different camps), not evidence of a faster respawn.</summary>
    [Fact]
    public void TrustedClocksRefuseToLearnFromRekillGaps()
    {
        var catalog = new SpawnCatalog
        {
            Zones =
            [
                new SpawnZone
                {
                    Zone = "Crushbone", NamedDefaultSeconds = 738, NamedDefaultTrusted = true,
                    Named = [new SpawnEntry { Name = "Orc Taskmaster" }],
                },
            ],
        };
        var overrides = new SpawnOverrides();
        var t = new SpawnTimers(catalog, overrides) { Server = "qeynos" };
        t.Apply(LogParser.Parse("[Tue Aug 4 19:00:00 2026] You have entered Clan Crushbone.")!);
        t.Apply(LogParser.Parse("[Tue Aug 4 19:00:10 2026] You have slain Orc Taskmaster!")!);
        // Re-kill 328s later — a second taskmaster at another camp, NOT a fast respawn.
        t.Apply(LogParser.Parse("[Tue Aug 4 19:05:38 2026] You have slain Orc Taskmaster!")!);

        Assert.Null(overrides.Find("Crushbone", "Orc Taskmaster"));   // nothing learned
        Assert.Equal(738, Assert.Single(t.Snapshot(DateTime.Parse("2026-08-04T19:05:39"))).DurationSeconds);
    }

    [Fact]
    public void AStaleLearnedOverrideUnderATrustedClockSelfHeals()
    {
        var catalog = new SpawnCatalog
        {
            Zones =
            [
                new SpawnZone
                {
                    Zone = "Crushbone", NamedDefaultSeconds = 738, NamedDefaultTrusted = true,
                    Named = [new SpawnEntry { Name = "Orc Taskmaster" }],
                },
            ],
        };
        var overrides = new SpawnOverrides();
        var stale = overrides.GetOrAdd("Crushbone", "Orc Taskmaster");
        stale.RespawnSeconds = 328;   // learned before the clock was measured
        stale.Learned = true;
        stale.Alert = true;           // the player's bell choice must survive the heal

        var t = new SpawnTimers(catalog, overrides) { Server = "qeynos" };
        t.Apply(LogParser.Parse("[Tue Aug 4 19:00:00 2026] You have entered Clan Crushbone.")!);
        t.Apply(LogParser.Parse("[Tue Aug 4 19:00:10 2026] You have slain Orc Taskmaster!")!);

        Assert.Equal(738, Assert.Single(t.Snapshot(DateTime.Parse("2026-08-04T19:00:11"))).DurationSeconds);
        var healed = overrides.Find("Crushbone", "Orc Taskmaster")!;
        Assert.Null(healed.RespawnSeconds);
        Assert.False(healed.Learned);
        Assert.True(healed.Alert);

        // A MANUAL (typed) edit is sovereign — never healed away.
        var manual = overrides.GetOrAdd("Crushbone", "Orc Taskmaster");
        manual.RespawnSeconds = 300;
        manual.Learned = false;
        t.Apply(LogParser.Parse("[Tue Aug 4 19:20:00 2026] You have slain Orc Taskmaster!")!);
        Assert.Equal(300, Assert.Single(t.Snapshot(DateTime.Parse("2026-08-04T19:20:01"))).DurationSeconds);
    }

    /// <summary>Issue #36 regression net: article-bearing catalog names ("the froglok
    /// shin lord", 285 entries) must match normalized kill lines, end to end against
    /// the REAL embedded catalog — zone resolution included. When this passes but a
    /// player still reports no timer, the divergence is Legends-vs-catalog data (zone
    /// name or mob placement), not code.</summary>
    /// <summary>Legends renames MOBS too: "the ghoul lord" is "Hoptor Thaggelum"
    /// in-game (issue #38, chrstahl's verbatim lines — which also proved Lower Guk
    /// kept classic's "Old Guk" zone name). Entry aliases absorb mob renames the way
    /// zone aliases absorb zone renames.</summary>
    [Fact]
    public void ARenamedMobStartsItsClassicEntrysTimer()
    {
        var t = new SpawnTimers(SpawnCatalog.LoadEmbedded(), new SpawnOverrides()) { Server = "qeynos" };
        t.Apply(LogParser.Parse("[Tue Aug 4 17:08:21 2026] You have entered The Ruins of Old Guk 4 (Refined).")!);
        t.Apply(LogParser.Parse("[Tue Aug 4 17:16:04 2026] You have slain Hoptor Thaggelum!")!);
        Assert.Single(t.Snapshot(DateTime.Parse("2026-08-04T17:16:05")),
            s => s.Name == "the ghoul lord" && s.Zone == "Lower Guk");
    }

    /// <summary>Legends renamed Lower Guk "The Ruins of ANCIENT Guk" (classic said
    /// "Old"); a single mismatched log-zone name silently kills every timer in the
    /// zone (issue #36's likely cause). The alias list absorbs renames.</summary>
    [Theory]
    [InlineData("You have entered The Ruins of Ancient Guk.")]
    [InlineData("You have entered The Ruins of Old Guk.")]
    [InlineData("You have entered The Ruins of Ancient Guk 2 (Adaptive).")]
    public void ZoneAliasesResolveLegendsRenames(string zoneLine)
    {
        var t = new SpawnTimers(SpawnCatalog.LoadEmbedded(), new SpawnOverrides()) { Server = "qeynos" };
        t.Apply(LogParser.Parse($"[Tue Aug 4 19:00:00 2026] {zoneLine}")!);
        t.Apply(LogParser.Parse("[Tue Aug 4 19:05:00 2026] You have slain the ghoul lord!")!);
        Assert.Single(t.Snapshot(DateTime.Parse("2026-08-04T19:05:01")),
            s => s.Name == "the ghoul lord" && s.Zone == "Lower Guk");
    }

    [Theory]
    [InlineData("You have entered Guk.")]
    [InlineData("You have entered Upper Guk 3 (Fused).")]
    // chrstahl's verbatim lines from issue #36 — Legends' real name for Upper Guk is
    // "The City of Guk", which no classic source predicted. Field data beats theory.
    [InlineData("You have entered The City of Guk 4 (Refined).")]
    public void ArticleNamedMobsStartTimersAgainstTheRealCatalog(string zoneLine)
    {
        var t = new SpawnTimers(SpawnCatalog.LoadEmbedded(), new SpawnOverrides()) { Server = "qeynos" };
        t.Apply(LogParser.Parse($"[Tue Aug 4 19:00:00 2026] {zoneLine}")!);
        t.Apply(LogParser.Parse("[Tue Aug 4 19:05:00 2026] You have slain the froglok shin lord!")!);
        Assert.Single(t.Snapshot(DateTime.Parse("2026-08-04T19:05:01")),
            s => s.Name == "the froglok shin lord");
    }

    /// says is possible proves the respawn is at most that gap. Manual edits are never
    /// touched, learning never loosens, and sub-90-second gaps are multi-spawn noise.</summary>
    [Fact]
    public void RekillsSoonerThanTheTimerTightenIt()
    {
        var overrides = new SpawnOverrides();
        var t = new SpawnTimers(TestCatalog(), overrides) { Server = "freeport" };
        t.Apply(new ZoneEvent(T0, "Lower Guk"));
        t.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));                 // catalog: 1620s
        t.Apply(new KillEvent(T0.AddMinutes(5), "froglok ghoul lord", "You"));   // back in 300s!

        var o = overrides.Find("Lower Guk", "a froglok ghoul lord");
        Assert.NotNull(o);
        Assert.True(o!.Learned);
        Assert.Equal(300, o.RespawnSeconds);
        Assert.Equal(T0.AddMinutes(5).AddSeconds(300), Assert.Single(t.Snapshot(T0.AddMinutes(6))).DueAt);

        // Better evidence keeps tightening…
        t.Apply(new KillEvent(T0.AddMinutes(9), "froglok ghoul lord", "You"));   // 240s gap
        Assert.Equal(240, overrides.Find("Lower Guk", "a froglok ghoul lord")!.RespawnSeconds);
        // …but a slower pair of kills never loosens what was learned.
        t.Apply(new KillEvent(T0.AddMinutes(29), "froglok ghoul lord", "You"));  // 1200s gap
        Assert.Equal(240, overrides.Find("Lower Guk", "a froglok ghoul lord")!.RespawnSeconds);
    }

    [Fact]
    public void LearningNeverOverridesAManualEditAndIgnoresNoiseGaps()
    {
        var overrides = new SpawnOverrides();
        var timers = new SpawnTimers(TestCatalog(), overrides) { Server = "freeport" };
        var vm = new SpawnsViewModel(TestCatalog(), overrides, timers);

        vm.SetDuration("Lower Guk", "a froglok ghoul lord", "20m");   // the player's word
        timers.Apply(new ZoneEvent(T0, "Lower Guk"));
        timers.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));
        timers.Apply(new KillEvent(T0.AddMinutes(5), "froglok ghoul lord", "You"));
        Assert.Equal(1200, overrides.Find("Lower Guk", "a froglok ghoul lord")!.RespawnSeconds);
        Assert.False(overrides.Find("Lower Guk", "a froglok ghoul lord")!.Learned);

        // Fresh named, kills 60 s apart: multi-spawn noise, not a 60-second respawn.
        timers.Apply(new KillEvent(T0.AddMinutes(10), "kor ghoul wizard", "You"));
        timers.Apply(new KillEvent(T0.AddMinutes(11), "kor ghoul wizard", "You"));
        Assert.Null(overrides.Find("Lower Guk", "the ghoul arch magi"));
    }

    // ---- duration text ----

    [Theory]
    [InlineData("22", 1320)]        // bare number = minutes, the wiki convention
    [InlineData("90s", 90)]
    [InlineData("8m", 480)]
    [InlineData("12h", 43200)]
    [InlineData("3d", 259200)]
    [InlineData("3d 12h", 302400)]
    [InlineData("1h30m", 5400)]
    [InlineData("6:40", 400)]       // m:ss, how eqlwiki writes zone timers
    [InlineData("1:00:00", 3600)]
    public void DurationTextParses(string text, double seconds) =>
        Assert.Equal(seconds, SpawnDurationText.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("h")]
    [InlineData("1:2:3:4")]
    public void DurationTextRejectsNoise(string text) =>
        Assert.Null(SpawnDurationText.Parse(text));

    [Theory]
    [InlineData(1320, "22m")]
    [InlineData(302400, "3d 12h")]
    [InlineData(400, "6m 40s")]
    [InlineData(90, "1m 30s")]
    public void DurationTextFormats(double seconds, string expected) =>
        Assert.Equal(expected, SpawnDurationText.Format(seconds));

    // ---- the view model ----

    private static (SpawnsViewModel Vm, SpawnTimers Timers, SpawnOverrides Overrides) Vm()
    {
        var overrides = new SpawnOverrides();
        var timers = new SpawnTimers(TestCatalog(), overrides) { Server = "freeport" };
        return (new SpawnsViewModel(TestCatalog(), overrides, timers), timers, overrides);
    }

    [Fact]
    public void RowsPutRunningTimersFirstAndNamePlaceholders()
    {
        var (vm, timers, _) = Vm();
        timers.Apply(new ZoneEvent(T0, "Lower Guk"));
        timers.Apply(new KillEvent(T0, "kor ghoul wizard", "You"));

        var rows = vm.RowsFor("Lower Guk", T0.AddMinutes(1));
        Assert.Equal(2, rows.Count);
        Assert.Equal("the ghoul arch magi", rows[0].Name);   // running timer sorts first
        Assert.True(rows[0].HasActiveTimer);
        Assert.Equal("the ghoul arch magi — Placeholder (kor ghoul wizard)", rows[0].DisplayName);
        Assert.Equal("27m", rows[1].DurationText);           // catalog 1620 s
    }

    [Fact]
    public void EditingADurationSticksAsAnOverrideAndRetimesTheClock()
    {
        var (vm, timers, overrides) = Vm();
        timers.Apply(new ZoneEvent(T0, "Lower Guk"));
        timers.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));

        vm.SetDuration("Lower Guk", "a froglok ghoul lord", "30m");

        Assert.Equal(1800, overrides.Find("Lower Guk", "a froglok ghoul lord")!.RespawnSeconds);
        Assert.Equal(T0.AddMinutes(30), Assert.Single(timers.Snapshot(T0)).DueAt);
    }

    [Fact]
    public void CustomNamedJoinTheirZoneAndDuplicatesAreRefused()
    {
        var (vm, _, _) = Vm();
        Assert.True(vm.AddCustom("Lower Guk", "the Fabled Froglok", "45m"));
        Assert.False(vm.AddCustom("Lower Guk", "a froglok ghoul lord", "45m")); // already catalogued

        var rows = vm.RowsFor("Lower Guk", T0);
        Assert.Contains(rows, r => r.Name == "the Fabled Froglok" && r.IsCustom && r.DurationText == "45m");
    }

    [Fact]
    public void DueAlertsFireOnceOnTheLiveTransitionAndNeverOnStartup()
    {
        var (vm, timers, _) = Vm();
        vm.ToggleAlert("Lower Guk", "a froglok ghoul lord");   // bell on (default off)
        timers.Apply(new ZoneEvent(T0, "Lower Guk"));
        timers.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));

        // First look happens after the timer already expired — startup priming, no alert.
        Assert.Empty(vm.ConsumeDueAlerts(T0.AddMinutes(60)));

        // A fresh kill counts down live: nothing while running, one alert at zero, silent after.
        timers.Apply(new KillEvent(T0.AddMinutes(70), "froglok ghoul lord", "You"));
        Assert.Empty(vm.ConsumeDueAlerts(T0.AddMinutes(71)));
        var due = vm.ConsumeDueAlerts(T0.AddMinutes(70 + 28));
        Assert.Equal("a froglok ghoul lord", Assert.Single(due).Name);
        Assert.Empty(vm.ConsumeDueAlerts(T0.AddMinutes(70 + 29)));
    }

    /// <summary>ConsumeNewTimers drives the pop-on-kill window: recovered timers pop at
    /// startup (unlike due ALERTS, which prime silently), each kill pops once, and a
    /// re-kill pops again because it carries a new kill time.</summary>
    [Fact]
    public void NewTimersReportOnceIncludingThoseRecoveredAtStartup()
    {
        var (vm, timers, _) = Vm();
        timers.Apply(new ZoneEvent(T0, "Lower Guk"));
        timers.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));   // "recovered" during ingest

        var first = vm.ConsumeNewTimers(T0.AddMinutes(1));
        Assert.Equal("a froglok ghoul lord", Assert.Single(first).Name);
        Assert.Empty(vm.ConsumeNewTimers(T0.AddMinutes(2)));            // unchanged — no re-pop

        timers.Apply(new KillEvent(T0.AddMinutes(5), "froglok ghoul lord", "You"));
        Assert.Single(vm.ConsumeNewTimers(T0.AddMinutes(6)));           // re-kill = new information

        Assert.True(vm.HasActiveTimers(T0.AddMinutes(7)));
    }

    /// <summary>Chicklets: every running timer on the server, soonest first, regardless
    /// of zone — a Befallen camp timer keeps its chip while you bank elsewhere.</summary>
    [Fact]
    public void ChipsSpanZonesSortSoonestFirstAndFlagDue()
    {
        var (vm, timers, _) = Vm();
        timers.Apply(new ZoneEvent(T0, "Lower Guk"));
        timers.Apply(new KillEvent(T0, "froglok ghoul lord", "You"));          // 27 min
        timers.Apply(new ZoneEvent(T0.AddMinutes(1), "Permafrost Keep"));
        timers.Apply(new KillEvent(T0.AddMinutes(1), "Lady Vox", "You"));      // 7 days

        var chips = vm.Chips(T0.AddMinutes(2));
        Assert.Equal(2, chips.Count);
        Assert.Equal("a froglok ghoul lord", chips[0].Name);   // soonest first
        Assert.Equal("Lady Vox", chips[1].Name);
        Assert.All(chips, c => Assert.False(c.IsDue));

        var later = vm.Chips(T0.AddSeconds(1620 + 30));        // ghoul lord due 30 s ago
        Assert.True(later[0].IsDue);
        Assert.False(later[1].IsDue);

        vm.ClearTimer("Lower Guk", "a froglok ghoul lord");    // click-away on a due chip
        Assert.Equal("Lady Vox", Assert.Single(vm.Chips(T0.AddSeconds(1620 + 31))).Name);
    }

    /// <summary>Per-named due sounds: "Default" maps to Alarm (a camp popping is the
    /// most time-critical thing the app announces — David's call, deliberately NOT the
    /// Options alert sound); "Off" silences one named; anything else is that named's
    /// own built-in or file.</summary>
    [Theory]
    [InlineData(null, "Alarm")]             // untouched: Default = Alarm
    [InlineData("", "Alarm")]               // explicit Default pick: same
    [InlineData("Off", null)]               // opted out individually
    [InlineData("Chimes", "Chimes")]        // own pick wins
    [InlineData(@"C:\sounds\vox.mp3", @"C:\sounds\vox.mp3")]
    public void PerNamedSoundResolution(string? own, string? expected)
    {
        var (vm, _, _) = Vm();
        if (own is not null) vm.SetSound("Lower Guk", "a froglok ghoul lord", own);
        Assert.Equal(expected, vm.SoundFor("Lower Guk", "a froglok ghoul lord"));
    }

    /// <summary>The bell defaults OFF, matching watch-rule sounds — a due timer is
    /// visible (chip flips to DUE) but silent until opted in. Picking a concrete
    /// sound counts as opting in.</summary>
    [Fact]
    public void DueSoundsAreOptInAndPickingASoundOptsIn()
    {
        var (vm, timers, _) = Vm();
        vm.ConsumeDueAlerts(T0);                               // prime
        timers.Apply(new ZoneEvent(T0, "Lower Guk"));
        timers.Apply(new KillEvent(T0.AddMinutes(1), "froglok ghoul lord", "You"));
        Assert.Empty(vm.ConsumeDueAlerts(T0.AddMinutes(1 + 28)));   // bell off by default

        vm.SetSound("Lower Guk", "a froglok ghoul lord", "Chimes");  // picking a sound = bell on
        timers.Apply(new KillEvent(T0.AddMinutes(40), "froglok ghoul lord", "You"));
        var due = vm.ConsumeDueAlerts(T0.AddMinutes(40 + 28));
        Assert.Single(due);
        Assert.Equal("Chimes", vm.SoundFor("Lower Guk", "a froglok ghoul lord"));
    }
}
