using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Tracking your own damage-over-time spells so they can be refreshed before they drop.
///
/// The log never states a DoT's duration, but unlike a slow it does not have to: ticks arrive
/// every ~6 seconds naming the spell and target, and the moment they stop is observable. So a
/// duration is measured from a completed cast and used for the next one - the first cast of a
/// spell shows no countdown, and every one after it does.
///
/// Line shapes are verbatim from the 690k-line fixture:
///   "a sand giant has taken 63 damage from your Drifting Death."
/// </summary>
public class DebuffTrackerTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc);

    private static DamageDealtEvent Tick(string target, string spell, int secondsIn) =>
        new(T0.AddSeconds(secondsIn), target, 63, DamageKind.Spell, spell,
            Critical: false, OverTime: true);

    [Fact]
    public void YourDotTickStartsTrackingIt()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(Tick("a sand giant", "Drifting Death", 0));

        var state = Assert.Single(tracker.Active(T0));
        Assert.Equal("a sand giant", state.Target);
        Assert.Equal("Drifting Death", state.Spell);
        Assert.True(state.IsMine);
    }

    /// <summary>Third-party DoTs are explicitly unwanted, and in the sample log they are most
    /// of the tick lines - one bard's chords alone produce thousands.</summary>
    [Fact]
    public void SomeoneElsesDotIsIgnored()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(new ThirdDotEvent(T0, "Kulwhip", "a sand giant", 40, "Chords of Dissonance"));

        Assert.Empty(tracker.Active(T0));
    }

    [Fact]
    public void RepeatedTicksAreOneEffectNotMany()
    {
        var tracker = new DebuffTracker();

        foreach (var second in new[] { 0, 6, 12, 18 })
            tracker.Apply(Tick("a sand giant", "Drifting Death", second));

        Assert.Single(tracker.Active(T0.AddSeconds(18)));
    }

    [Fact]
    public void TheSameSpellOnTwoMobsIsTwoEffects()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(Tick("a sand giant", "Drifting Death", 0));
        tracker.Apply(Tick("a dervish cutthroat", "Drifting Death", 0));

        Assert.Equal(2, tracker.Active(T0).Count);
    }

    /// <summary>Two missed ticks means it is gone: the countdown must not linger on a mob that
    /// stopped taking damage twelve seconds ago.</summary>
    [Fact]
    public void AnEffectWhoseTicksStopFallsOffTheList()
    {
        var tracker = new DebuffTracker();
        tracker.Apply(Tick("a sand giant", "Drifting Death", 0));

        Assert.Single(tracker.Active(T0.AddSeconds(6)));
        Assert.Empty(tracker.Active(T0.AddSeconds(30)));
    }

    /// <summary>The whole point: measure one completed cast, then count down the next.</summary>
    [Fact]
    public void ADurationLearnedFromOneCastCountsDownTheNext()
    {
        var tracker = new DebuffTracker();
        for (var second = 0; second <= 48; second += 6)
            tracker.Apply(Tick("a sand giant", "Drifting Death", second));
        tracker.Active(T0.AddSeconds(70));            // ticks stopped: the cast is complete

        Assert.Equal(54, tracker.LearnedDurations["Drifting Death"], 0);

        tracker.Apply(Tick("a dervish cutthroat", "Drifting Death", 100));
        var state = Assert.Single(tracker.Active(T0.AddSeconds(100)));

        Assert.NotNull(state.ExpiresAt);
        Assert.Equal(54, state.RemainingSeconds(T0.AddSeconds(100))!.Value, 0);
    }

    [Fact]
    public void AnUnmeasuredSpellHasNoCountdownRatherThanAGuess()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(Tick("a sand giant", "Immolate", 0));

        var state = Assert.Single(tracker.Active(T0));
        Assert.Null(state.ExpiresAt);
        Assert.Null(state.RemainingSeconds(T0));
    }

    /// <summary>A mob dying stops the ticks early. Learning from that would teach a duration
    /// shorter than the spell's, and every later cast would warn too soon - the failure would
    /// look like the spell being nerfed rather than a bad sample.</summary>
    [Fact]
    public void AKillDoesNotTeachAShortDuration()
    {
        var tracker = new DebuffTracker();
        for (var second = 0; second <= 12; second += 6)
            tracker.Apply(Tick("a sand giant", "Ignite", second));
        tracker.Apply(new KillEvent(T0.AddSeconds(13), "a sand giant", "Daggo"));

        tracker.Active(T0.AddSeconds(40));

        Assert.False(tracker.LearnedDurations.ContainsKey("Ignite"));
    }

    [Fact]
    public void AnEffectIsAboutToDropOnceItIsInsideTheWarningWindow()
    {
        var tracker = new DebuffTracker { WarnSeconds = 10 };
        for (var second = 0; second <= 48; second += 6)
            tracker.Apply(Tick("a sand giant", "Drifting Death", second));
        tracker.Active(T0.AddSeconds(70));

        // A live DoT keeps ticking right up to its expiry, so the ticks have to keep coming
        // or the effect is (correctly) retired as gone before the window is ever reached.
        for (var second = 100; second <= 148; second += 6)
            tracker.Apply(Tick("a dervish cutthroat", "Drifting Death", second));

        // Learned 54s from a cast landing at 100, so it drops at 154.
        Assert.False(tracker.Active(T0.AddSeconds(142))[0].IsAboutToDrop(T0.AddSeconds(142), 10));
        Assert.True(tracker.Active(T0.AddSeconds(148))[0].IsAboutToDrop(T0.AddSeconds(148), 10));
    }

    /// <summary>Refreshing a DoT before it drops is the normal case, and the ticks continue
    /// seamlessly across the recast - nothing in the tick lines says "this is a new cast".
    /// Without the cast line resetting the clock, one measurement spans two casts: replaying
    /// the real log taught Immolate 115s while every sibling druid DoT measured 54-60s.</summary>
    [Fact]
    public void RecastingRestartsTheClockRatherThanExtendingIt()
    {
        var tracker = new DebuffTracker();
        for (var second = 0; second <= 30; second += 6)
            tracker.Apply(Tick("a sand giant", "Immolate", second));

        tracker.Apply(new SpellCastEvent(T0.AddSeconds(31), "Immolate"));
        for (var second = 36; second <= 90; second += 6)
            tracker.Apply(Tick("a sand giant", "Immolate", second));
        tracker.Active(T0.AddSeconds(120));

        // 36..90 is the second cast: 54s + the tick already paid for = 60, not 96.
        Assert.Equal(60, tracker.LearnedDurations["Immolate"], 0);
    }

    /// <summary>One odd sample must not become the duration for good. A mob wandering out of
    /// range, a partial log flush, or a zone crash all truncate ticks in ways a kill does not
    /// announce, so the tracker keeps samples and trusts the repeated one.</summary>
    [Fact]
    public void TheRepeatedMeasurementWinsOverAnOddOne()
    {
        var tracker = new DebuffTracker();

        void Cast(string target, int from, int to)
        {
            for (var second = from; second <= to; second += 6)
                tracker.Apply(Tick(target, "Ignite", second));
            tracker.Active(T0.AddSeconds(to + 30));
        }

        Cast("mob one", 0, 48);        // 54s
        Cast("mob two", 200, 248);     // 54s again
        Cast("mob three", 400, 418);   // 24s - the odd one out, and the most RECENT

        Assert.Equal(54, tracker.LearnedDurations["Ignite"], 0);
    }
}
