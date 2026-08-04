using EQBuddy.Core;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// The heal-over-time tracker: one countdown per (spell, target) for YOUR own HoTs,
/// started by the first tick, ended by the "… Heal Trigger" burst, and — when that
/// terminator never arrives — by the measured 5-tick/6-second estimate.
/// </summary>
public class HotTrackerTests
{
    private static readonly DateTime T0 = DateTime.Parse("2026-08-04T20:00:00");

    private static GameEvent Ev(int seconds, string message) =>
        LogParser.Parse($"[{T0.AddSeconds(seconds):ddd MMM d HH:mm:ss yyyy}] {message}")!;

    private static HotTracker Replay(params GameEvent[] events)
    {
        var t = new HotTracker();
        foreach (var e in events) t.Apply(e);
        return t;
    }

    /// <summary>Prelude for every cast test. "You begin casting Blossoming Heal." says
    /// nothing about what the spell DOES — nothing in the log does — so the tracker only
    /// knows a name is a HoT because it has watched that name tick. This is the cheapest
    /// way to teach it one: a single tick, closed immediately by its Trigger so it leaves
    /// no chip behind and (a 0-second gap being an implausible duration) teaches no
    /// duration either, leaving the 24s estimate in force. A real session gets this for
    /// free — the startup replay of today's log, or the persisted duration store.</summary>
    private static GameEvent[] KnownHot(string spell) =>
    [
        Ev(-300, $"You healed Chickpea over time for 61 hit points by {spell}."),
        Ev(-300, $"You healed Chickpea for 398 hit points by {spell} Trigger."),
    ];

    /// <summary>Nothing announces that a HoT landed — there is no "you begin to heal"
    /// line and no fade line — so the first tick is the only proof the spell is on the
    /// target, and it is what opens the chip.</summary>
    [Fact]
    public void AFirstTickStartsACountdownForThatTarget()
    {
        var t = Replay(Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."));

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(1)));
        Assert.Equal("Daggo", h.Target);
        Assert.Equal("Blossoming Heal", h.Spell);
        Assert.Equal(23, h.RemainingSeconds(T0.AddSeconds(1)), 0);   // 5 ticks, 6s apart
    }

    /// <summary>The five ticks of one cast are ONE chip whose countdown runs from the
    /// first of them — a tick must sustain the series, not restart it — and when no
    /// Trigger arrives (18% of runs in the real log) the estimate ends it.</summary>
    [Fact]
    public void FiveTicksAtSixSecondsCompleteTheSeriesAndTheChipExpires()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(6, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(12, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(18, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(24, "You healed Daggo over time for 61 hit points by Blossoming Heal."));

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(23)));
        Assert.Equal(1, h.RemainingSeconds(T0.AddSeconds(23)), 0);
        Assert.Empty(t.Snapshot(T0.AddSeconds(25)));
    }

    /// <summary>A tick long after the series should have finished is a recast, not a
    /// late tick: it restarts the countdown from that tick rather than leaving the chip
    /// dead or extending the old series.</summary>
    [Fact]
    public void ARecastRestartsTheCountdownInsteadOfExtendingTheOldSeries()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(6, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(12, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            // Recast: the next tick lands well past where the old series would have ended.
            Ev(60, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(66, "You healed Daggo over time for 61 hit points by Blossoming Heal."));

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(67)));
        Assert.Equal(T0.AddSeconds(60), h.FirstTick);
        Assert.Equal(17, h.RemainingSeconds(T0.AddSeconds(67)), 0);   // 60+24-67
    }

    /// <summary>The same HoT on two people is two chips — the group's healer keeps one
    /// per target.</summary>
    [Fact]
    public void TwoTargetsWithTheSameHotGetSeparateChips()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(3, "You healed Chickpea over time for 61 hit points by Blossoming Heal."));

        var chips = t.Snapshot(T0.AddSeconds(4));
        Assert.Equal(2, chips.Count);
        Assert.Equal(["Daggo", "Chickpea"], chips.Select(h => h.Target));
    }

    /// <summary>Two different HoTs stacked on one target are two chips: they run on
    /// their own timers and the player recasts them independently.</summary>
    [Fact]
    public void TwoDifferentHotsOnOneTargetGetSeparateChips()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(3, "You healed Daggo over time for 13 (61) hit points by Flowering Heal."));

        var chips = t.Snapshot(T0.AddSeconds(4));
        Assert.Equal(2, chips.Count);
        Assert.Equal(["Blossoming Heal", "Flowering Heal"], chips.Select(h => h.Spell));
    }

    /// <summary>The companion burst heal ("… Heal Trigger", no "over time") is an
    /// instant heal, not a HoT tick — on its own it must not conjure a chip.</summary>
    [Fact]
    public void ATriggerHealAloneCreatesNothing()
    {
        var t = Replay(Ev(0, "You healed Daggo for 398 hit points by Blossoming Heal Trigger."));
        Assert.Empty(t.Snapshot(T0.AddSeconds(1)));
    }

    /// <summary>This is YOUR HoT stack: another healer's HoT ticking on you is somebody
    /// else's spell to maintain and gets no chip.</summary>
    [Fact]
    public void AnotherHealersHotOnYouIsNotTracked()
    {
        var t = Replay(Ev(0, "Aenari healed you over time for 8 hit points by Echoing Light."));
        Assert.Empty(t.Snapshot(T0.AddSeconds(1)));
    }

    /// <summary>The "… Heal Trigger" burst is the HoT's terminator: measured across the
    /// real log it lands WITH the final tick — same timestamp in 551 of 584 runs, and in
    /// all 614 same-second cases the Trigger line comes after the tick line, never before
    /// — and it heals ~3x a tick. When it arrives the chip is done, whatever the estimate
    /// still had left on it.</summary>
    [Fact]
    public void ATriggerEndsTheChipImmediately()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 63 hit points by Flowering Heal."),
            Ev(6, "You healed Daggo over time for 63 hit points by Flowering Heal."),
            Ev(12, "You healed Daggo over time for 63 hit points by Flowering Heal."),
            // A shorter HoT than the 24s estimate: the Trigger says so, and it wins.
            Ev(12, "You healed Daggo for 202 hit points by Flowering Heal Trigger."));

        Assert.Empty(t.Snapshot(T0.AddSeconds(13)));
    }

    /// <summary>The Trigger is the direct analogue of a mez's worn-off line: first
    /// tick to Trigger is a REAL measured duration, so it is learned (longest wins,
    /// since an interrupted HoT measures short and nothing measures long) and the next
    /// cast of that spell counts down for the learned time instead of the estimate.</summary>
    [Fact]
    public void TheDurationLearnedFromATriggerLengthensTheNextCastsCountdown()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(6, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(12, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(18, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(24, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            // This one runs a tick longer than the five-tick estimate, and says so.
            Ev(30, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(30, "You healed Daggo for 398 hit points by Blossoming Heal Trigger."),
            // Next cast, on someone else:
            Ev(90, "You healed Chickpea over time for 61 hit points by Blossoming Heal."));

        Assert.Equal(30, t.LearnedDurations["Blossoming Heal"], 0);
        Assert.Equal(6, t.LearnedTicks["Blossoming Heal"], 0);
        var h = Assert.Single(t.Snapshot(T0.AddSeconds(91)));
        Assert.Equal(29, h.RemainingSeconds(T0.AddSeconds(91)), 0);   // 90+30-91
    }

    /// <summary>The Trigger names a target: it ends that target's HoT and says nothing
    /// about the same spell running on anyone else.</summary>
    [Fact]
    public void ATriggerForOneTargetLeavesTheOtherTargetsHotAlone()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(3, "You healed Chickpea over time for 61 hit points by Blossoming Heal."),
            Ev(12, "You healed Daggo for 398 hit points by Blossoming Heal Trigger."));

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(13)));
        Assert.Equal("Chickpea", h.Target);
    }

    /// <summary>A Trigger for something we never saw tick — a HoT that started before
    /// EQBuddy was watching, or another spell family entirely — is simply ignored: no
    /// crash, no phantom chip, and no damage to the HoTs that are running.</summary>
    [Fact]
    public void ATriggerForAnUntrackedSpellDoesNothing()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(6, "You healed Daggo for 489 hit points by Efflorescing Heal Trigger."));

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(7)));
        Assert.Equal("Blossoming Heal", h.Spell);
        Assert.Empty(t.LearnedDurations);
    }

    /// <summary>Recasting the moment the last tick lands is the healer's normal rhythm,
    /// and it keeps the 6s cadence unbroken — which is exactly why the real log contains
    /// 10- and 15-tick "runs". The Trigger is what separates them: it closes the old
    /// series, so the next tick opens a fresh chip with a full countdown.</summary>
    [Fact]
    public void ABackToBackRecastStartsAFreshChipAfterTheTrigger()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(6, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(12, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(18, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(24, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(24, "You healed Daggo for 398 hit points by Blossoming Heal Trigger."),
            Ev(30, "You healed Daggo over time for 61 hit points by Blossoming Heal."));

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(31)));
        Assert.Equal(T0.AddSeconds(30), h.FirstTick);
        Assert.Equal(23, h.RemainingSeconds(T0.AddSeconds(31)), 0);
    }

    /// <summary>Learned durations outlive the process, the way learned mez durations and
    /// spawn timers do — a week of play should not have to be re-measured every launch.</summary>
    [Fact]
    public void LearnedDurationsSurviveARestartThroughTheStoreFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hot-durations-{Guid.NewGuid():N}.json");
        try
        {
            var t = new HotTracker();
            t.AttachStore(path);
            t.Apply(Ev(0, "You healed Daggo over time for 184 hit points by Efflorescing Heal."));
            t.Apply(Ev(6, "You healed Daggo over time for 184 hit points by Efflorescing Heal."));
            t.Apply(Ev(12, "You healed Daggo for 489 hit points by Efflorescing Heal Trigger."));

            var reborn = new HotTracker();
            reborn.AttachStore(path);
            Assert.Equal(12, reborn.LearnedDurations["Efflorescing Heal"], 0);

            reborn.Apply(Ev(60, "You healed Chickpea over time for 184 hit points by Efflorescing Heal."));
            var h = Assert.Single(reborn.Snapshot(T0.AddSeconds(61)));
            Assert.Equal(11, h.RemainingSeconds(T0.AddSeconds(61)), 0);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Changed means "the stack looks different now". A tick that merely
    /// sustains a running series does not: its countdown is anchored to the FIRST tick,
    /// so firing there would repaint the stack every 6 seconds for nothing.</summary>
    [Fact]
    public void ChangedFiresOnlyWhenTheStackActuallyChanges()
    {
        var t = new HotTracker();
        var fired = 0;
        t.Changed += () => fired++;

        t.Apply(Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."));
        Assert.Equal(1, fired);                       // chip appeared

        t.Apply(Ev(6, "You healed Daggo over time for 61 hit points by Blossoming Heal."));
        t.Apply(Ev(12, "You healed Daggo over time for 61 hit points by Blossoming Heal."));
        Assert.Equal(1, fired);                       // same chip, same countdown

        t.Apply(Ev(12, "You healed Daggo for 398 hit points by Blossoming Heal Trigger."));
        Assert.Equal(2, fired);                       // chip gone
    }

    [Fact]
    public void ZoningClearsEverything()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(3, "You have entered Clan Crushbone."));

        Assert.Empty(t.Snapshot(T0.AddSeconds(4)));
    }

    /// <summary>The reported bug: "I'm full health but I didn't see it show up as a chip
    /// anywhere." A tick line only exists when healing actually happened — not one of the
    /// HoT ticks in 690k lines of eqlog_Daggo_freeport reports 0 — so a HoT cast on a
    /// full-health target ticks in total silence. Waiting for a tick means the healer
    /// gets no chip in exactly the case where they cannot see the HoT any other way, so
    /// the CAST has to open one.</summary>
    [Fact]
    public void ACastOpensAChipEvenWhenNoTickEverArrives()
    {
        var t = Replay([.. KnownHot("Blossoming Heal"),
            Ev(0, "You begin casting Blossoming Heal.")]);

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(6)));
        Assert.Equal("Blossoming Heal", h.Spell);
    }

    /// <summary>Nothing in the log names the target of a beneficial cast: "You have
    /// targeted X" and "You are targeting" appear ZERO times in the fixture log, and the
    /// cast line is bare. So the chip a cast opens must admit it does not know who it
    /// landed on rather than guess — an empty Target, which no real name can collide
    /// with, telling the UI to show the spell instead of a person.</summary>
    [Fact]
    public void ACastOpensAChipWithNoTargetBecauseTheLogNeverNamesOne()
    {
        var t = Replay([.. KnownHot("Efflorescing Heal"),
            Ev(0, "You begin casting Efflorescing Heal III.")]);

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(6)));
        Assert.Equal("", h.Target);
        Assert.False(h.TargetKnown);
        // The cast carries the RANK, the ticks and the Trigger never do; the chip keys on
        // the unranked name so the first tick can find its way home.
        Assert.Equal("Efflorescing Heal", h.Spell);
    }

    /// <summary>The half that already worked and must keep working, now as the second
    /// act: the first tick is the only thing that ever names the target, so it BINDS the
    /// waiting chip — one chip that gains a name, not a second chip beside it — and
    /// re-anchors the countdown to the real first tick instead of the estimate.</summary>
    [Fact]
    public void TheFirstTickBindsTheWaitingChipInsteadOfOpeningASecondOne()
    {
        var t = Replay([.. KnownHot("Blossoming Heal"),
            Ev(0, "You begin casting Blossoming Heal."),
            // Cast to first tick measured 1-8s across 768 runs in eqlog_Daggo_freeport,
            // 84% of them 2-7s: this is a perfectly ordinary one.
            Ev(4, "You healed Daggo over time for 61 hit points by Blossoming Heal.")]);

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(5)));
        Assert.Equal("Daggo", h.Target);
        Assert.True(h.TargetKnown);
        Assert.Equal(T0.AddSeconds(4), h.FirstTick);           // the real tick, not the estimate
        Assert.Equal(23, h.RemainingSeconds(T0.AddSeconds(5)), 0);
    }

    /// <summary>"Your Blooming Heal spell is interrupted." — 320 of them in the fixture
    /// log, 20 for this spell alone. The cast never landed, so its chip is a lie and has
    /// to go the moment the log says so.</summary>
    [Fact]
    public void AnInterruptedCastCancelsTheWaitingChip()
    {
        var t = Replay([.. KnownHot("Blooming Heal"),
            Ev(0, "You begin casting Blooming Heal."),
            Ev(2, "Your Blooming Heal spell is interrupted.")]);

        Assert.Empty(t.Snapshot(T0.AddSeconds(3)));
    }

    /// <summary>The other way a cast dies: "Your Flowering Heal spell fizzles!" (119
    /// fizzles in the fixture log, 6 of them this spell — and note the BANG, not a
    /// period; that is what the log and the parser both use).</summary>
    [Fact]
    public void AFizzledCastCancelsTheWaitingChip()
    {
        var t = Replay([.. KnownHot("Flowering Heal"),
            Ev(0, "You begin casting Flowering Heal."),
            Ev(2, "Your Flowering Heal spell fizzles!")]);

        Assert.Empty(t.Snapshot(T0.AddSeconds(3)));
    }

    /// <summary>A chip opened by a cast that never ticks has no Trigger and no last tick
    /// to end it, so the estimate is all there is: it runs the spell's duration from where
    /// the first tick would have been (cast + the measured 5s median delay) and then goes
    /// away. A chip that lingered forever would be worse than no chip.</summary>
    [Fact]
    public void AWaitingChipThatNeverTicksExpiresOnTheEstimate()
    {
        var t = Replay([.. KnownHot("Sprouting Heal"),
            Ev(0, "You begin casting Sprouting Heal III.")]);

        Assert.Single(t.Snapshot(T0.AddSeconds(28)));   // 0 + 5 + 24 = 29
        Assert.Empty(t.Snapshot(T0.AddSeconds(30)));
    }

    /// <summary>Only a HoT gets a chip. Every cast the player makes comes through the same
    /// event, so without this the chip stack would fill with nukes, mezzes and gates. A
    /// spell is a HoT because it has been seen ticking (or because the duration store
    /// remembers it from a previous session) — the cast line itself says nothing.</summary>
    [Fact]
    public void ACastOfSomethingNeverSeenTickingOpensNothing()
    {
        var t = Replay(
            Ev(0, "You begin casting Complete Heal."),
            Ev(1, "You begin casting Careless Lightning."));

        Assert.Empty(t.Snapshot(T0.AddSeconds(2)));
    }

    /// <summary>The ambiguity the log forces on us: a cast while the same HoT is already
    /// running on Daggo is EITHER a refresh on Daggo or a fresh cast on someone else, and
    /// nothing distinguishes them. Refreshing Daggo's chip would be the dangerous guess —
    /// a chip promising 24s of healing that may in fact lapse in 10, which is how a healer
    /// lets a HoT drop — so the running chip is left strictly alone and the cast opens its
    /// own targetless chip beside it.</summary>
    [Fact]
    public void ACastDoesNotStealAChipAlreadyRunningOnAKnownTarget()
    {
        var t = Replay([.. KnownHot("Blossoming Heal"),
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(6, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(12, "You begin casting Blossoming Heal.")]);

        var chips = t.Snapshot(T0.AddSeconds(13));
        Assert.Equal(2, chips.Count);
        // Daggo's countdown still runs from HIS first tick — untouched by the new cast.
        var daggo = Assert.Single(chips, c => c.Target == "Daggo");
        Assert.Equal(T0, daggo.FirstTick);
        Assert.Equal(11, daggo.RemainingSeconds(T0.AddSeconds(13)), 0);
        Assert.Single(chips, c => !c.TargetKnown);
    }

    /// <summary>The other half of that rule: a tick that merely sustains a series already
    /// running is that series' tick, not the waiting cast's first tick. Binding on it
    /// would silently re-point the new cast at a target it may never have touched, and
    /// would restart Daggo's countdown on evidence that says nothing about Daggo.</summary>
    [Fact]
    public void ATickThatSustainsARunningSeriesDoesNotBindTheWaitingChip()
    {
        var t = Replay([.. KnownHot("Blossoming Heal"),
            Ev(0, "You healed Daggo over time for 61 hit points by Blossoming Heal."),
            Ev(6, "You begin casting Blossoming Heal."),
            Ev(12, "You healed Daggo over time for 61 hit points by Blossoming Heal.")]);

        var chips = t.Snapshot(T0.AddSeconds(13));
        Assert.Equal(2, chips.Count);
        Assert.Equal(T0, Assert.Single(chips, c => c.Target == "Daggo").FirstTick);
        Assert.Single(chips, c => !c.TargetKnown);
    }

    /// <summary>An interrupt kills the cast that was in flight, not the HoT already
    /// ticking on someone: that one landed, and it keeps healing.</summary>
    [Fact]
    public void AnInterruptLeavesAHotThatAlreadyLandedAlone()
    {
        var t = Replay([.. KnownHot("Blooming Heal"),
            Ev(0, "You healed Daggo over time for 61 hit points by Blooming Heal."),
            Ev(6, "You begin casting Blooming Heal."),
            Ev(8, "Your Blooming Heal spell is interrupted.")]);

        var h = Assert.Single(t.Snapshot(T0.AddSeconds(9)));
        Assert.Equal("Daggo", h.Target);
    }

    /// <summary>A HoT that healed nothing until its very last beat: the target finally
    /// takes damage and only the terminating burst logs. It names a target the tracker has
    /// no series for, but it does say that cast is over — so it closes the waiting chip
    /// instead of leaving it to run out the estimate. No duration is learned from it: the
    /// chip's anchor was an estimate, and a guessed measurement would poison the store
    /// that real Triggers fill.</summary>
    [Fact]
    public void ATriggerEndsAWaitingChipWithoutLearningADurationFromIt()
    {
        var t = Replay([.. KnownHot("Flowering Heal"),
            Ev(0, "You begin casting Flowering Heal."),
            // 24s, not the 29s the estimate expects: this cast's first tick came
            // instantly (0s cast-to-tick happens 12 times in the fixture log), so the
            // Trigger arrives while the chip still has 5s of guessed countdown left.
            Ev(24, "You healed Daggo for 202 hit points by Flowering Heal Trigger.")]);

        Assert.Empty(t.Snapshot(T0.AddSeconds(25)));
        Assert.Empty(t.LearnedDurations);
    }

    /// <summary>Changed means "the stack looks different now", and both cast-side events
    /// change it: the chip appearing with no name, and the first tick giving it one.
    /// A UI that missed either would show a stale stack.</summary>
    [Fact]
    public void ChangedFiresWhenACastOpensAChipAndWhenATickBindsIt()
    {
        var t = new HotTracker();
        foreach (var e in KnownHot("Blossoming Heal")) t.Apply(e);
        var fired = 0;
        t.Changed += () => fired++;

        t.Apply(Ev(0, "You begin casting Blossoming Heal."));
        Assert.Equal(1, fired);                       // targetless chip appeared

        t.Apply(Ev(4, "You healed Daggo over time for 61 hit points by Blossoming Heal."));
        Assert.Equal(2, fired);                       // it now names Daggo

        t.Apply(Ev(10, "You healed Daggo over time for 61 hit points by Blossoming Heal."));
        Assert.Equal(2, fired);                       // same chip, same countdown

        t.Apply(Ev(16, "Your Blossoming Heal spell is interrupted."));
        Assert.Equal(2, fired);                       // a later cast died; the chip is not it
    }

    /// <summary>Zoning drops the cast-in-flight chip too: whoever it was on, they are not
    /// in this zone with you.</summary>
    [Fact]
    public void ZoningClearsAWaitingChipAsWell()
    {
        var t = Replay([.. KnownHot("Blossoming Heal"),
            Ev(0, "You begin casting Blossoming Heal."),
            Ev(3, "You have entered Clan Crushbone.")]);

        Assert.Empty(t.Snapshot(T0.AddSeconds(4)));
    }

    /// <summary>A HoT the store already knows is a HoT: the duration store is keyed by
    /// unranked spell name, so it carries "this name ticks" across restarts and the very
    /// first cast of a session gets a chip without waiting to be re-taught.</summary>
    [Fact]
    public void AStoredDurationIsAlsoWhatMakesACastRecogniseableAcrossRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hot-durations-{Guid.NewGuid():N}.json");
        try
        {
            var t = new HotTracker();
            t.AttachStore(path);
            t.Apply(Ev(0, "You healed Daggo over time for 184 hit points by Efflorescing Heal."));
            t.Apply(Ev(12, "You healed Daggo for 489 hit points by Efflorescing Heal Trigger."));

            var reborn = new HotTracker();
            reborn.AttachStore(path);
            reborn.Apply(Ev(60, "You begin casting Efflorescing Heal III."));

            var h = Assert.Single(reborn.Snapshot(T0.AddSeconds(61)));
            Assert.Equal("Efflorescing Heal", h.Spell);
            Assert.False(h.TargetKnown);
            Assert.Equal(16, h.RemainingSeconds(T0.AddSeconds(61)), 0);   // 60+5+12-61
        }
        finally { File.Delete(path); }
    }
}
