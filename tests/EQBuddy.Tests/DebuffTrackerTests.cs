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

        Assert.Equal(48, tracker.LearnedDurations["Drifting Death"], 0);

        tracker.Apply(Tick("a dervish cutthroat", "Drifting Death", 100));
        var state = Assert.Single(tracker.Active(T0.AddSeconds(100)));

        Assert.NotNull(state.ExpiresAt);
        Assert.Equal(48, state.RemainingSeconds(T0.AddSeconds(100))!.Value, 0);
    }

    /// <summary>Immolate's wiki duration is 48s, and in the fixture its fade line arrives at the
    /// last tick, not a tick after it: nine ticks spanning 48s, then "worn off" in the same second.
    /// The tick-retired path used to add a phantom trailing tick and teach 54s - the number that
    /// looked like a 6s anchoring error and is not one.</summary>
    [Fact]
    public void ATickRetiredDotMeasuresFirstTickToLastTick()
    {
        var tracker = new DebuffTracker();
        for (var i = 0; i <= 48; i += 6)
            tracker.Apply(Tick("a sand giant", "Immolate", i));

        // Ticks stop; the effect retires once TickGrace has passed.
        tracker.Active(T0.AddSeconds(48 + 13));

        Assert.Equal(48, tracker.LearnedDurations["Immolate"]);
    }

    /// <summary>The fade path and the tick-retired path must agree. They measure the same event
    /// by different evidence, so a disagreement means one of them is wrong.</summary>
    [Fact]
    public void TheFadePathAndTheTickPathMeasureTheSameDuration()
    {
        var faded = new DebuffTracker();
        for (var i = 0; i <= 48; i += 6)
            faded.Apply(Tick("a sand giant", "Immolate", i));
        faded.Apply(new SpellWornOffEvent(T0.AddSeconds(48), "Immolate", "a sand giant"));

        var ticked = new DebuffTracker();
        for (var i = 0; i <= 48; i += 6)
            ticked.Apply(Tick("a sand giant", "Immolate", i));
        ticked.Active(T0.AddSeconds(48 + 13));

        Assert.Equal(faded.LearnedDurations["Immolate"], ticked.LearnedDurations["Immolate"]);
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

        // Learned 48s from a cast landing at 100, so it drops at 148.
        Assert.False(tracker.Active(T0.AddSeconds(136))[0].IsAboutToDrop(T0.AddSeconds(136), 10));
        Assert.True(tracker.Active(T0.AddSeconds(142))[0].IsAboutToDrop(T0.AddSeconds(142), 10));
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

        // 36..90 is the second cast: 54s, not 96. The last tick falls on the expiry.
        Assert.Equal(54, tracker.LearnedDurations["Immolate"], 0);
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

        Cast("mob one", 0, 48);        // 48s
        Cast("mob two", 200, 248);     // 48s again
        Cast("mob three", 400, 418);   // 18s - the odd one out, and the most RECENT

        Assert.Equal(48, tracker.LearnedDurations["Ignite"], 0);
    }

    /// <summary>A gap between ticks ends the effect even if nothing asked for the active list
    /// in between.
    ///
    /// Retirement used to happen only inside Active(), which the UI calls once a second - fine
    /// while playing, wrong whenever events arrive in a batch: catching up on a log at startup,
    /// review mode, or any replay. Three separate casts then merge into one long effect that
    /// never expires and never teaches a duration. Caught by feeding a live app synthetic ticks
    /// and seeing a countdown of 0:00 on a spell that should have read 0:30.</summary>
    [Fact]
    public void AGapBetweenTicksEndsTheEffectEvenWithoutAnActiveCall()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(Tick("a sand giant", "Choke", 0));
        tracker.Apply(Tick("a sand giant", "Choke", 6));
        // No Active() call here - the gap must still be noticed.
        tracker.Apply(Tick("a sand giant", "Choke", 120));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(120)));
        Assert.Equal(T0.AddSeconds(120), state.LandedAt);
        Assert.Equal(6, tracker.LearnedDurations["Choke"], 0);
    }

    // ---- slice 2: fades, slows and cripples ----

    /// <summary>The fade line names spell AND target, so it ends the effect exactly rather
    /// than waiting for ticks to stop - and it measures the duration precisely, which the
    /// tick-gap estimate could only approximate.</summary>
    [Fact]
    public void AFadeLineEndsTheEffectAndMeasuresItExactly()
    {
        var tracker = new DebuffTracker();
        tracker.Apply(Tick("a sand giant", "Immolate", 0));
        tracker.Apply(Tick("a sand giant", "Immolate", 6));

        tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(54), "Immolate", "a sand giant"));

        Assert.Empty(tracker.Active(T0.AddSeconds(54)));
        Assert.Equal(54, tracker.LearnedDurations["Immolate"], 0);
    }

    /// <summary>A slow names neither spell nor caster when it lands - only the preceding cast
    /// line does, which is the same problem MezTracker solves with an 8s window.</summary>
    [Fact]
    public void YourSlowIsPairedWithTheCastThatExplainsIt()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(new SpellCastEvent(T0, "Tepid Deeds"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(3), "a bok ghoul knight", DebuffKind.Slow));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(3)));
        Assert.Equal("Tepid Deeds", state.Spell);
        Assert.True(state.IsMine);
    }

    [Fact]
    public void SomeoneElsesSlowIsTrackedButMarkedTheirs()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(new OtherCastEvent(T0, "Cognix", "Enfeeblement"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(2), "a cracked skeleton", DebuffKind.Cripple));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(2)));
        Assert.Equal("Enfeeblement", state.Spell);
        Assert.False(state.IsMine);
        Assert.Equal("Cognix", state.Caster);
    }

    /// <summary>No visible cast means no spell name, and a chip reading "something slowed it"
    /// with no timer is worse than nothing.</summary>
    [Fact]
    public void ALandingWithNoExplainingCastIsIgnored()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(new DebuffLandedEvent(T0, "a bok ghoul knight", DebuffKind.Slow));

        Assert.Empty(tracker.Active(T0));
    }

    /// <summary>A cast too long ago did not cause this landing. MezTracker uses the same 8s
    /// window for the same reason.</summary>
    [Fact]
    public void AStaleCastDoesNotExplainALanding()
    {
        var tracker = new DebuffTracker();

        tracker.Apply(new SpellCastEvent(T0, "Tepid Deeds"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(30), "a bok ghoul knight", DebuffKind.Slow));

        Assert.Empty(tracker.Active(T0.AddSeconds(30)));
    }

    /// <summary>Someone else's slow has no fade line - only yours do - so its timer comes from
    /// a duration you measured yourself, and is absent until you have.</summary>
    [Fact]
    public void ATheirSlowBorrowsADurationYouMeasuredYourself()
    {
        var tracker = new DebuffTracker();
        tracker.Apply(new SpellCastEvent(T0, "Tepid Deeds"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a bok ghoul knight", DebuffKind.Slow));
        tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(61), "Tepid Deeds", "a bok ghoul knight"));

        tracker.Apply(new OtherCastEvent(T0.AddSeconds(100), "Cognix", "Tepid Deeds"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(101), "an ire ghast", DebuffKind.Slow));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(101)));
        Assert.Equal(60, state.RemainingSeconds(T0.AddSeconds(101))!.Value, 0);
    }

    /// <summary>A slow does not tick, so the tick-gap rule must not apply to it.
    ///
    /// It did: every slow was retired twelve seconds after landing, long before its fade line
    /// arrived, so no slow was ever measured. The unit tests missed it because they only asked
    /// for the active list at the end; replaying the real log, where the UI asks every second,
    /// produced 504 parsed landings and zero measured durations.</summary>
    [Fact]
    public void ASlowSurvivesLongerThanTheTickGapAndIsEndedByItsFade()
    {
        var tracker = new DebuffTracker();
        tracker.Apply(new SpellCastEvent(T0, "Tepid Deeds"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a bok ghoul knight", DebuffKind.Slow));

        // The UI asks every second, all the way through - well past TickGrace.
        for (var second = 2; second <= 60; second++)
            tracker.Active(T0.AddSeconds(second));

        Assert.Single(tracker.Active(T0.AddSeconds(60)));

        tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(61), "Tepid Deeds", "a bok ghoul knight"));

        Assert.Empty(tracker.Active(T0.AddSeconds(61)));
        Assert.Equal(60, tracker.LearnedDurations["Tepid Deeds"], 0);
    }

    // ---- derived durations: the catalog as a fallback ----

    private static DebuffTracker WithCatalog() => new(new SpellDurationCatalog(
        new Dictionary<string, double> { ["Shiftless Deeds"] = 150, ["Immolate"] = 48 }));

    /// <summary>Cold start: nothing has been measured, so the catalog answers - marked as an
    /// estimate so the chip can say so.</summary>
    [Fact]
    public void AnUnmeasuredSpellFallsBackToTheDerivedDuration()
    {
        var tracker = WithCatalog();

        tracker.Apply(new SpellCastEvent(T0, "Shiftless Deeds VI"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(6), "a sand giant", DebuffKind.Slow));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(6)));
        Assert.Equal(DurationCertainty.Derived, state.Certainty);
        Assert.Equal(240, state.RemainingSeconds(T0.AddSeconds(6))!.Value, precision: 3);
    }

    /// <summary>The trust order. Tepid Deeds measures ~126s while its wiki page says 150 - and
    /// that page contradicts itself. The measurement wins and is never corrected toward the wiki.</summary>
    [Fact]
    public void AMeasurementSupersedesTheDerivedDuration()
    {
        var tracker = new DebuffTracker(new SpellDurationCatalog(
            new Dictionary<string, double> { ["Immolate"] = 48 }));

        // First cast: nothing measured yet, so the estimate is shown.
        tracker.Apply(new SpellCastEvent(T0, "Immolate"));
        for (var i = 0; i <= 126; i += 6)
            tracker.Apply(Tick("a sand giant", "Immolate", i));
        tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(126), "Immolate", "a sand giant"));

        // Second cast on a fresh mob: the measured 126 is used, not the catalog's 48.
        tracker.Apply(new SpellCastEvent(T0.AddSeconds(200), "Immolate"));
        tracker.Apply(Tick("a griffon", "Immolate", 206));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(206)));
        Assert.Equal(DurationCertainty.Measured, state.Certainty);
        Assert.Equal(126, state.RemainingSeconds(T0.AddSeconds(206))!.Value, precision: 3);
    }

    /// <summary>Ranks have genuinely different durations, so their samples must not pool - a
    /// rank-I measurement must never shorten a rank-V countdown.</summary>
    [Fact]
    public void SamplesForTwoRanksOfOneSpellDoNotPool()
    {
        var tracker = WithCatalog();

        // Rank IV on one mob, measured at 100s.
        tracker.Apply(new SpellCastEvent(T0, "Shiftless Deeds IV"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a sand giant", DebuffKind.Slow));
        tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(101), "Shiftless Deeds", "a sand giant"));

        // Rank VI on another, measured at 130s. Both fade lines say "Shiftless Deeds".
        tracker.Apply(new SpellCastEvent(T0.AddSeconds(200), "Shiftless Deeds VI"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(201), "a hill giant", DebuffKind.Slow));
        tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(331), "Shiftless Deeds", "a hill giant"));

        Assert.Equal(100, tracker.LearnedDurations["Shiftless Deeds IV"]);
        Assert.Equal(130, tracker.LearnedDurations["Shiftless Deeds VI"]);
        // And nothing pooled into the base name the two fades share.
        Assert.False(tracker.LearnedDurations.ContainsKey("Shiftless Deeds"));
    }

    /// <summary>The rank lives on the cast line and nowhere else, so a tick nobody cast has an
    /// unknown tier. Assuming base rank would read 48s against a real 72s for a rank-V DoT and
    /// warn early on every single cast.</summary>
    [Fact]
    public void ATickWithNoExplainingCastShowsNoDerivedDuration()
    {
        var tracker = WithCatalog();

        tracker.Apply(Tick("a sand giant", "Immolate", 0));

        var state = Assert.Single(tracker.Active(T0));
        Assert.Equal(DurationCertainty.Unknown, state.Certainty);
        Assert.Null(state.RemainingSeconds(T0));
    }

    /// <summary>A cast from long ago must not supply a rank. _recentCasts is pruned only when a
    /// new cast arrives, so without a window this quietly becomes "the last rank I ever saw" -
    /// the guess this design rejected.</summary>
    [Fact]
    public void AStaleCastDoesNotSupplyTheRank()
    {
        var tracker = WithCatalog();

        tracker.Apply(new SpellCastEvent(T0, "Immolate III"));
        // Two minutes later, a tick with no cast of its own to explain it.
        tracker.Apply(Tick("a sand giant", "Immolate", 120));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(120)));
        Assert.Equal("Immolate", state.Spell);
        Assert.Equal(DurationCertainty.Unknown, state.Certainty);
    }

    /// <summary>The chip shows the rank you actually cast, while tracking keys on the base name
    /// the tick and fade lines use.</summary>
    [Fact]
    public void TheChipShowsTheRankedNameButTracksByBaseName()
    {
        var tracker = WithCatalog();

        tracker.Apply(new SpellCastEvent(T0, "Shiftless Deeds IV"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a sand giant", DebuffKind.Slow));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(1)));
        Assert.Equal("Shiftless Deeds IV", state.Spell);
        Assert.Equal("Shiftless Deeds", state.BaseName);
    }

    /// <summary>_recastPending held the RANKED cast name and was looked up with the UNRANKED tick
    /// name, so recast detection could never fire for a ranked DoT - the exact failure the tracker
    /// documents ("Immolate 115s against 54-60s for every sibling"). The fixture never caught it
    /// because none of Daggo's DoTs are ranked.</summary>
    [Fact]
    public void ARecastOfARankedDotRestartsTheClock()
    {
        var tracker = new DebuffTracker(new SpellDurationCatalog(
            new Dictionary<string, double> { ["Immolate"] = 48 }));

        tracker.Apply(new SpellCastEvent(T0, "Immolate III"));
        tracker.Apply(Tick("a sand giant", "Immolate", 6));
        tracker.Apply(Tick("a sand giant", "Immolate", 12));

        // Refresh before it drops. The clock must restart from the new cast's first tick.
        tracker.Apply(new SpellCastEvent(T0.AddSeconds(18), "Immolate III"));
        tracker.Apply(Tick("a sand giant", "Immolate", 24));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(24)));
        Assert.Equal(T0.AddSeconds(24), state.LandedAt);
    }

    /// <summary>A derived duration is an ESTIMATE, and the real spell can outlast it: replaying
    /// the user's own log, Shiftless Deeds IV measured 214.0s against a derived 210.0s and
    /// graduated with one second to spare. Retiring the chip must not also forget the effect -
    /// otherwise the fade finds nothing, nothing is recorded, and every later cast re-derives the
    /// same estimate, pinning the spell at the guess for the rest of the session.</summary>
    [Fact]
    public void ASlowOutlastingItsDerivedEstimateIsStillMeasuredWhenItFades()
    {
        var tracker = WithCatalog();   // Shiftless Deeds 150 base; rank IV derives 210s

        tracker.Apply(new SpellCastEvent(T0, "Shiftless Deeds IV"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a sand giant", DebuffKind.Slow));

        // The estimate and its linger run out while the effect is still on the mob: the chip
        // goes away, as it should - a countdown that reached zero is not worth showing.
        Assert.Empty(tracker.Active(T0.AddSeconds(220)));

        // The truth arrives late, and is still the truth.
        tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(231), "Shiftless Deeds", "a sand giant"));

        Assert.Equal(230, tracker.LearnedDurations["Shiftless Deeds IV"]);
    }

    /// <summary>UnknownCap exists so a mis-attributed chip cannot hold the panel forever. A
    /// derived duration must not defeat it - Valor's 3240s would keep a wrong chip up for 54
    /// minutes, which is the exact thing the cap was written to stop.</summary>
    [Fact]
    public void ALongDerivedDurationStillRetiresAtTheUnknownCap()
    {
        var tracker = new DebuffTracker(new SpellDurationCatalog(
            new Dictionary<string, double> { ["Valor"] = 3240 }));

        tracker.Apply(new SpellCastEvent(T0, "Valor"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a sand giant", DebuffKind.Slow));

        Assert.Single(tracker.Active(T0.AddSeconds(590)));
        Assert.Empty(tracker.Active(T0.AddSeconds(700)));
    }

    /// <summary>What is remembered for a late fade is bounded too, or a mob three zones back
    /// could still teach a duration an hour later.</summary>
    [Fact]
    public void AFadeLongAfterTheUnknownCapTeachesNothing()
    {
        var tracker = WithCatalog();

        tracker.Apply(new SpellCastEvent(T0, "Shiftless Deeds IV"));
        tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a sand giant", DebuffKind.Slow));

        Assert.Empty(tracker.Active(T0.AddSeconds(700)));
        tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(800), "Shiftless Deeds", "a sand giant"));

        Assert.Empty(tracker.LearnedDurations);
    }

    /// <summary>A refresh must not read samples under a name it would never write to. The chip
    /// keeps the ranked name, so the countdown has to come from the ranked name's samples: the
    /// 30s measured for unranked Immolate belongs to tier 0 and must never surface on a rank-III
    /// chip, least of all marked Measured.</summary>
    [Fact]
    public void ARefreshedRankedDotDoesNotBorrowTheBaseRanksMeasurement()
    {
        var tracker = new DebuffTracker(new SpellDurationCatalog(
            new Dictionary<string, double> { ["Immolate"] = 48 }));

        // Tier 0, measured at 30s on another mob: ticks with no cast to name a rank.
        foreach (var second in new[] { 0, 6, 12, 18, 24, 30 })
            tracker.Apply(Tick("a hill giant", "Immolate", second));
        Assert.Empty(tracker.Active(T0.AddSeconds(50)));
        Assert.Equal(30, tracker.LearnedDurations["Immolate"]);

        // Rank III on the sand giant, then a refresh. The bard's cast is what used to prune the
        // recast line out of _recentCasts, leaving the refresh with no rank to work from and the
        // sample lookup falling back to the tick's base name.
        tracker.Apply(new SpellCastEvent(T0.AddSeconds(100), "Immolate III"));
        foreach (var second in new[] { 102, 108, 114 })
            tracker.Apply(Tick("a sand giant", "Immolate", second));
        tracker.Apply(new SpellCastEvent(T0.AddSeconds(116), "Immolate III"));
        tracker.Apply(new OtherCastEvent(T0.AddSeconds(125), "Kulwhip", "Chords of Dissonance"));
        tracker.Apply(Tick("a sand giant", "Immolate", 126));

        var state = Assert.Single(tracker.Active(T0.AddSeconds(126)));
        Assert.Equal("Immolate III", state.Spell);
        Assert.NotEqual(DurationCertainty.Measured, state.Certainty);
        Assert.Equal(62.4, state.RemainingSeconds(T0.AddSeconds(126))!.Value, precision: 3);
    }
}
