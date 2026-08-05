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
}
