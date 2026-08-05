using EQBuddy.Core;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// The mez-target tracker: cast→landing correlation from ANY group member's log (the
/// landing line is bystander-visible and other players' casts log with spell and rank),
/// durations from the eqlwiki catalog, break-on-damage, and caster-side duration
/// learning from natural fades.
/// </summary>
public class MezTrackerTests
{
    private static readonly DateTime T0 = DateTime.Parse("2026-08-04T20:00:00");

    private static GameEvent Ev(int seconds, string message) =>
        LogParser.Parse($"[{T0.AddSeconds(seconds):ddd MMM d HH:mm:ss yyyy}] {message}")!;

    private static MezTracker Replay(params GameEvent[] events)
    {
        var t = new MezTracker();
        foreach (var e in events) t.Apply(e);
        return t;
    }

    [Fact]
    public void OwnMezCastPlusLandingStartsACountdown()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "ice boned skeleton has been mesmerized."));

        var m = Assert.Single(t.Snapshot(T0.AddSeconds(3)));
        Assert.Equal("Ice boned skeleton", m.Target);
        Assert.Equal("You", m.Caster);
        Assert.Equal(23, m.RemainingSeconds(T0.AddSeconds(3))!.Value, 0);   // 24s catalog duration
    }

    [Fact]
    public void AGroupMembersMezIsTrackedFromABystanderLog()
    {
        // The whole point: Hugzee casts, THIS log belongs to someone else in the group.
        var t = Replay(
            Ev(0, "Hugzee begins casting Enthrall."),
            Ev(3, "an orc centurion has been enthralled."));

        var m = Assert.Single(t.Snapshot(T0.AddSeconds(4)));
        Assert.Equal("Hugzee", m.Caster);
        Assert.Equal("Enthrall", m.Spell);
        Assert.Equal(47, m.RemainingSeconds(T0.AddSeconds(4))!.Value, 0);   // 48s catalog duration
    }

    [Fact]
    public void ALandingNobodyVisiblyCastIsIgnored()
    {
        // Same trust rule as charm: an unexplained bystander-visible line claims nothing.
        var t = Replay(Ev(0, "a Teir`Dal rogue has been mesmerized."));
        Assert.Empty(t.Snapshot(T0.AddSeconds(1)));
    }

    [Fact]
    public void DamageWakesTheTargetAndClearsTheChip()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "ice boned skeleton has been mesmerized."),
            Ev(5, "Twiddley slashes ice boned skeleton for 12 points of damage."));

        Assert.Empty(t.Snapshot(T0.AddSeconds(6)));
    }

    [Fact]
    public void AoeMezCoversEveryLandingFromOneCast()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization."),
            Ev(3, "an orc pawn has been mesmerized."),
            Ev(3, "an orc centurion has been mesmerized."));

        Assert.Equal(2, t.Snapshot(T0.AddSeconds(4)).Count);
    }

    [Fact]
    public void TheCasterLearnsTheRealDurationFromANaturalFade()
    {
        // Rank II lasts longer than the base 24s the catalog knows; the caster's own
        // worn-off line measures it, and the next cast uses the learned value.
        var t = Replay(
            Ev(0, "You begin casting Mesmerize II."),
            Ev(2, "an orc pawn has been mesmerized."),
            Ev(34, "Your Mesmerize II spell has worn off of an orc pawn."),   // 32s observed
            Ev(60, "You begin casting Mesmerize II."),
            Ev(62, "a gnoll has been mesmerized."));

        Assert.Equal(32, t.LearnedDurations["Mesmerize II"], 0);
        var m = Assert.Single(t.Snapshot(T0.AddSeconds(63)));
        Assert.Equal(31, m.RemainingSeconds(T0.AddSeconds(63))!.Value, 0);
    }

    [Fact]
    public void ExoticLandingLinesParse()
    {
        Assert.IsType<MezzedEvent>(Ev(0, "an orc pawn swoons in raptured bliss."));
        Assert.IsType<MezzedEvent>(Ev(0, "a gnoll begins to scream."));
        Assert.IsType<MezzedEvent>(Ev(0, "a gnoll's eyes glaze over."));
        Assert.IsType<MezzedEvent>(Ev(0, "an orc oracle has been mesmerized by the Glamour of Kintaz."));
        // And the cast line that isn't a landing stays a cast.
        Assert.IsType<OtherCastEvent>(Ev(0, "Shack begins casting Shield of Thistles IV."));
    }

    /// <summary>Issue #32 item 2: chain-mezzing one target is the normal workflow — a
    /// re-landing must REFRESH the countdown (also what keeps bard pulse songs alive).</summary>
    [Fact]
    public void RemezRefreshesTheCountdown()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "an orc pawn has been mesmerized."),
            Ev(20, "You begin casting Mesmerize."),
            Ev(22, "an orc pawn has been mesmerized."));

        var m = Assert.Single(t.Snapshot(T0.AddSeconds(23)));
        Assert.Equal(23, m.RemainingSeconds(T0.AddSeconds(23))!.Value, 0);   // 22+24-23
    }

    /// <summary>Issue #32 item 3: same-second landings (an AoE catching same-named
    /// mobs) are distinct creatures with separate chips — and a break clears ONE of
    /// them (the earliest-expiring), not both.</summary>
    [Fact]
    public void AoeSameNameGetsSeparateEntriesAndBreaksClearOne()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization."),
            Ev(3, "an orc pawn has been mesmerized."),
            Ev(3, "an orc pawn has been mesmerized."));
        Assert.Equal(2, t.Snapshot(T0.AddSeconds(4)).Count);

        t.Apply(Ev(6, "Twiddley slashes an orc pawn for 5 points of damage."));
        Assert.Single(t.Snapshot(T0.AddSeconds(7)));
    }

    /// <summary>Issue #32 item 1: a DoT cast before the mez keeps ticking on the player
    /// while the mob sleeps — the tick must not clear the chip. Real melee still does.</summary>
    [Fact]
    public void ADotTickFromTheMezzedMobDoesNotClearTheChip()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "an orc oracle has been mesmerized."),
            Ev(8, "You have taken 7 damage from Flame Lick by an orc oracle."));
        Assert.Single(t.Snapshot(T0.AddSeconds(9)));

        t.Apply(Ev(12, "Orc oracle hits YOU for 9 points of damage."));
        Assert.Empty(t.Snapshot(T0.AddSeconds(13)));
    }

    /// <summary>Issue #32 item 1 (the other half): rank-lengthened mezzes outlive the
    /// catalog's base duration — an expired chip lingers visibly at 0:00 instead of
    /// silently vanishing mid-mez.</summary>
    [Fact]
    public void AnExpiredChipLingersBrieflyInsteadOfVanishing()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "an orc pawn has been mesmerized."));   // expires at +26

        var lingering = Assert.Single(t.Snapshot(T0.AddSeconds(30)));
        Assert.Equal(0, lingering.RemainingSeconds(T0.AddSeconds(30))!.Value, 0);
        Assert.Empty(t.Snapshot(T0.AddSeconds(40)));
    }

    /// <summary>A rank-lengthened mez fades long after the base duration — the entry
    /// must still be there (hidden) for the fade to teach the real duration, or high
    /// ranks would be unlearnable.</summary>
    [Fact]
    public void AFadeWellPastTheBaseDurationStillTeaches()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize III."),
            Ev(2, "an orc pawn has been mesmerized."),                     // base says +26
            Ev(46, "Your Mesmerize III spell has worn off of an orc pawn."));   // 44s real

        Assert.Equal(44, t.LearnedDurations["Mesmerize III"], 0);
    }

    [Fact]
    public void ZoningClearsEverything()
    {
        var t = Replay(
            Ev(0, "You begin casting Entrance."),
            Ev(2, "an orc pawn has been entranced."),
            Ev(5, "You have entered Clan Crushbone."));

        Assert.Empty(t.Snapshot(T0.AddSeconds(6)));
    }

    /// <summary>"Your X spell has worn off of Y." is caster-private — it is YOUR mez ending,
    /// and it says nothing about anyone else's. Matching it on target name alone let it land
    /// on another chanter's entry and file the measured gap under THEIR spell rank. Found in
    /// a real 690k-line log: Mesmerization VI was learned at 6s by a player who never casts
    /// it (39 casts in the log, all by other people) — EQ logs the fade WITHOUT the rank
    /// ("Your Mesmerization spell has worn off of ..."), so nothing in the line itself could
    /// contradict the wrong entry.</summary>
    [Fact]
    public void YourOwnFadeLineNeverTeachesAnotherPlayersSpellRank()
    {
        var t = Replay(
            Ev(0, "Wrathos begins casting Mesmerize III."),
            Ev(2, "an orc pawn has been mesmerized."),
            // Your own mez on a same-named creature fades 6s later. The fade is yours; the
            // only entry on that target is Wrathos's.
            Ev(8, "Your Mesmerize spell has worn off of an orc pawn."));

        Assert.False(t.LearnedDurations.ContainsKey("Mesmerize III"));
        // ...and his chip survives: your spell ending is no evidence his mez broke.
        var m = Assert.Single(t.Snapshot(T0.AddSeconds(9)));
        Assert.Equal("Wrathos", m.Caster);
    }

    /// <summary>The 1.29.1 field report ("when the targets share the same name, the chip
    /// shows up but then breaking one of them removes it for both"): RemoveTarget already
    /// drops ONE entry per break (issue #32), but it runs once per damage EVENT and one
    /// attack round is several lines — main hand, off hand, kick, a damage-shield proc,
    /// all in the same log second, all naming the same creature. Each line ate another
    /// twin. In the 690k-line fixture 54,999 of 83,299 (second, target-name) buckets carry
    /// more than one break line, so this is the common case, not the corner.</summary>
    [Fact]
    public void OneAttackRoundOfSeveralLinesClearsOnlyOneOfTwoSameNamedChips()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization."),
            Ev(3, "an orc pawn has been mesmerized."),
            Ev(3, "an orc pawn has been mesmerized."));
        Assert.Equal(2, t.Snapshot(T0.AddSeconds(4)).Count);

        // One round on ONE of them: two weapon swings, a kick, and the damage shield
        // firing when it swung back — four lines, one creature, one break.
        t.Apply(Ev(6, "You slash an orc pawn for 61 points of damage."));
        t.Apply(Ev(6, "You kick an orc pawn for 22 points of damage."));
        t.Apply(Ev(6, "Twiddley slashes an orc pawn for 9 points of damage."));
        t.Apply(Ev(6, "An orc pawn is burned by YOUR flames for 5 points of non-melee damage."));

        Assert.Single(t.Snapshot(T0.AddSeconds(7)));
    }

    /// <summary>The other half of the same rule: dedupe must not deafen the tracker. A
    /// break on the second twin, once the first one's engagement window has lapsed, still
    /// clears its chip.</summary>
    [Fact]
    public void ABreakOnTheSecondTwinAfterTheWindowStillClearsItsChip()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization."),
            Ev(3, "an orc pawn has been mesmerized."),
            Ev(3, "an orc pawn has been mesmerized."),
            Ev(6, "You slash an orc pawn for 61 points of damage."),
            Ev(6, "You kick an orc pawn for 22 points of damage."));
        Assert.Single(t.Snapshot(T0.AddSeconds(7)));

        t.Apply(Ev(20, "Twiddley slashes an orc pawn for 12 points of damage."));
        Assert.Empty(t.Snapshot(T0.AddSeconds(21)));
    }

    /// <summary>Regression guard for the ordinary single-mob case: one chip, one
    /// multi-line round, chip gone. Deduping breaks must not make a real wake-up
    /// invisible.</summary>
    [Fact]
    public void ASingleMezzedMobHitByAMultiLineRoundStillClears()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "an orc pawn has been mesmerized."),
            Ev(5, "You slash an orc pawn for 61 points of damage."),
            Ev(5, "You kick an orc pawn for 22 points of damage."),
            Ev(5, "An orc pawn is burned by YOUR flames for 5 points of non-melee damage."));

        Assert.Empty(t.Snapshot(T0.AddSeconds(6)));
    }

    /// <summary>The reason the window slides instead of expiring on a fixed clock: in the
    /// fixture, a run of damage on one target name (lines joined by gaps of 6s or less)
    /// has a median length of 4s but a 75th percentile of 21s and a 90th of 47s. A fixed
    /// short cooldown would let a long fight on ONE woken mob eat the sleeping twin's chip
    /// every few seconds, which is the reported bug with extra steps.</summary>
    [Fact]
    public void ASustainedFightOnOneWokenMobDoesNotEatTheSleepingTwinsChip()
    {
        var t = Replay(
            Ev(0, "You begin casting Fascination."),
            Ev(3, "an orc pawn has been fascinated."),
            Ev(3, "an orc pawn has been fascinated."));

        // 18 seconds of continuous melee on the one that woke — swings, its swings back,
        // and a DoT ticking on it. All the same creature.
        for (var s = 6; s <= 24; s += 3)
        {
            t.Apply(Ev(s, "You slash an orc pawn for 61 points of damage."));
            t.Apply(Ev(s, "Orc pawn hits YOU for 9 points of damage."));
            t.Apply(Ev(s + 1, "An orc pawn has taken 12 damage from Poison Bolt by Twiddley."));
        }

        Assert.Single(t.Snapshot(T0.AddSeconds(25)));   // the twin is still asleep
    }

    /// <summary>A kill is one line per creature and must always land — but it arrives in
    /// the same second as the killing blow 96% of the time in the fixture (2,504 of 2,624
    /// kills), so it must not be counted as a SECOND break for that creature. Killing two
    /// mezzed adds in sequence has to leave nothing behind.</summary>
    [Fact]
    public void KillingTwoMezzedAddsInSequenceClearsBothChips()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization."),
            Ev(3, "an orc pawn has been mesmerized."),
            Ev(3, "an orc pawn has been mesmerized."));

        t.Apply(Ev(6, "You slash an orc pawn for 61 points of damage."));
        t.Apply(Ev(6, "You kick an orc pawn for 22 points of damage."));
        t.Apply(Ev(6, "You have slain an orc pawn!"));
        Assert.Single(t.Snapshot(T0.AddSeconds(7)));   // exactly one break, not two

        // The second add, five seconds later — inside the engagement window, so the
        // damage lines are deduped, but the kill still ends its chip.
        t.Apply(Ev(11, "You slash an orc pawn for 58 points of damage."));
        t.Apply(Ev(11, "You have slain an orc pawn!"));
        Assert.Empty(t.Snapshot(T0.AddSeconds(12)));
    }

    /// <summary>A kill with no parsed damage in front of it (someone else's unlogged
    /// swing landed the blow) still clears immediately.</summary>
    [Fact]
    public void AKillLineWithNoPrecedingDamageStillClearsTheChipImmediately()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "an orc pawn has been mesmerized."),
            Ev(5, "an orc pawn has been slain by Twiddley!"));

        Assert.Empty(t.Snapshot(T0.AddSeconds(6)));
    }

    /// <summary>The second path to "2 of the same mobs break clears both", still live after
    /// 1.29.1 deduped repeated damage LINES. Breaking a mez with a hit makes EverQuest log
    /// the event TWICE — the spell wearing off, and the blow that did it — naming the same
    /// creature in the same second. From the reported log: the AoE lands on twins at
    /// 20:54:19, then at 20:54:26 "Your Mesmerization spell has worn off of Innoruuk`s
    /// Chosen." is followed by "You bash Innoruuk`s Chosen for 5 points of damage." The fade
    /// took one twin and the bash took the other, because OnWornOff removed an entry without
    /// going through the break window at all. One break, one chip.</summary>
    [Fact]
    public void AFadeAndTheDamageThatCausedItClearOnlyOneOfTwoSameNamedChips()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization V."),
            Ev(1, "Innoruuk`s Chosen has been mesmerized."),
            Ev(1, "Innoruuk`s Chosen has been mesmerized."));
        Assert.Equal(2, t.Snapshot(T0.AddSeconds(2)).Count);

        // Seven seconds in — nowhere near Mesmerization's 24s, which is the tell that this
        // fade is a break, not an expiry. Both lines are the SAME break.
        t.Apply(Ev(8, "Your Mesmerization spell has worn off of Innoruuk`s Chosen."));
        t.Apply(Ev(8, "You bash Innoruuk`s Chosen for 5 points of damage."));

        Assert.Single(t.Snapshot(T0.AddSeconds(9)));
    }

    /// <summary>The same pair the other way round: the log's ordering of the fade and the
    /// blow inside one second is not guaranteed (the reported log happened to put the fade
    /// first), so the damage arriving first must claim the break and the fade must then
    /// recognise its chip as already taken. Nor does it teach a duration — but note WHY, since
    /// 1.31.3: not because anything retracts the sample afterwards, but because this fade
    /// yields to a break already COUNTED and so never reaches the learning path at all. A fade
    /// that does take its own chip keeps its sample however soon damage follows.</summary>
    [Fact]
    public void TheDamageThatBrokeAMezAndTheFadeLineAfterItClearOnlyOneOfTwoSameNamedChips()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization V."),
            Ev(1, "Innoruuk`s Chosen has been mesmerized."),
            Ev(1, "Innoruuk`s Chosen has been mesmerized."));
        Assert.Equal(2, t.Snapshot(T0.AddSeconds(2)).Count);

        t.Apply(Ev(8, "You bash Innoruuk`s Chosen for 5 points of damage."));
        t.Apply(Ev(8, "Your Mesmerization spell has worn off of Innoruuk`s Chosen."));

        Assert.Single(t.Snapshot(T0.AddSeconds(9)));
        Assert.False(t.LearnedDurations.ContainsKey("Mesmerization V"));
    }

    /// <summary>Reported from play, and the reason 1.31.2's break-retraction was withdrawn in
    /// 1.31.3: a mez that ran its full course and faded is attacked in the SAME SECOND, because
    /// that is what a group does the instant a mob wakes. Retracting any sample with a break
    /// signal behind it therefore threw away the natural fades along with the broken ones —
    /// 401 of the fixture's 417 samples, and every single one of Mesmerization IV's, so that
    /// rank could never learn at all and its chips fell back to the catalog's 24s for a spell
    /// that runs ~40.
    ///
    /// The window could not be tightened to fix it either, because the delay carries no signal.
    /// Splitting the fixture's unambiguous fades by how long the mez had held, and measuring
    /// the gap to the next damage line naming that creature:
    /// short holds (&lt;=12s, almost certainly broken) n=43, delay 0s in 40 of 43;
    /// long holds (&gt;=25s, plausibly a natural fade) n=145, delay 0s in 97 of 145.
    /// Zero in both populations, at any window size. What separates them is the HOLD, which is
    /// exactly what the upper-cluster estimator keys on — so the sample is taken and kept, and
    /// the breaks are outvoted rather than filtered.</summary>
    [Fact]
    public void AFadeFollowedImmediatelyByDamageOnThatCreatureStillTeachesItsDuration()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization V."),
            Ev(2, "a necromancer has been mesmerized."),
            // 38s — the rank's real length. The mob wakes and the group is on it at once.
            Ev(40, "Your Mesmerization spell has worn off of a necromancer."),
            Ev(40, "You slash a necromancer for 61 points of damage."));

        Assert.Equal(38, t.LearnedDurations["Mesmerization V"], 0);
    }

    /// <summary>The other half of withdrawing the retraction: nothing filters break-shortened
    /// samples out any more, so the estimator has to reject them on its own — which is what it
    /// was built for in 1.31.3. Six mezzes cut short after 4s and nine that ran ~35s, EVERY one
    /// of the fifteen followed in the same second by the blow that landed on the woken mob (the
    /// shape the fixture actually has). Under 1.31.2 all fifteen were retracted and the rank
    /// learned nothing; the mode of the upper cluster ignores the 4s pile-up instead.</summary>
    [Fact]
    public void BrokenMezSamplesAmongFullDurationFadesAreOutvotedRatherThanFiltered()
    {
        var t = new MezTracker();
        var gaps = new[] { 4, 4, 4, 4, 4, 4, 34, 35, 36, 34, 35, 36, 34, 35, 36 };
        for (var i = 0; i < gaps.Length; i++)
        {
            var c = i * 60;
            t.Apply(Ev(c, "You begin casting Mesmerization III."));
            t.Apply(Ev(c + 2, "a necromancer has been mesmerized."));
            t.Apply(Ev(c + 2 + gaps[i], "Your Mesmerization spell has worn off of a necromancer."));
            t.Apply(Ev(c + 2 + gaps[i], "You slash a necromancer for 61 points of damage."));
        }

        // 4s is the most common value in the set and must still lose.
        Assert.InRange(t.LearnedDurations["Mesmerization III"], 33, 37);
    }

    /// <summary>The fade must yield to a break that was actually COUNTED, not merely to a
    /// live engagement window — otherwise the fade stops working during long fights. In the
    /// reported log the group beats on Innoruuk`s Chosen for half a minute, so the window
    /// never lapses; the second mez lands INTO that fight, meaning no damage line can ever
    /// credit its break (deliberate — see CreditBreak). The caster-private fade is then the
    /// only signal that can clear the chip, and it has to.</summary>
    [Fact]
    public void ANaturalFadeStillClearsItsChipWhileTheFightOnThatNameGrindsOn()
    {
        var t = new MezTracker();
        for (var s = 0; s <= 12; s += 2)
            t.Apply(Ev(s, "You slash Innoruuk`s Chosen for 60 points of damage."));

        t.Apply(Ev(12, "You begin casting Mesmerization V."));
        t.Apply(Ev(13, "Innoruuk`s Chosen has been mesmerized."));
        Assert.Single(t.Snapshot(T0.AddSeconds(14)));

        t.Apply(Ev(14, "You slash Innoruuk`s Chosen for 60 points of damage."));
        t.Apply(Ev(20, "Your Mesmerization spell has worn off of Innoruuk`s Chosen."));

        Assert.Empty(t.Snapshot(T0.AddSeconds(21)));
    }

    /// <summary>Two fade lines in one second are two mezzes ending, not one event logged
    /// twice — an AoE that landed on same-named twins in the same second expires on both in
    /// the same second too. So a fade only ever yields to a DAMAGE-credited break; it never
    /// suppresses another fade.</summary>
    [Fact]
    public void TwoSameNamedTwinsFadingInTheSameSecondBothClear()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization V."),
            Ev(1, "Innoruuk`s Chosen has been mesmerized."),
            Ev(1, "Innoruuk`s Chosen has been mesmerized."),
            Ev(26, "Your Mesmerization spell has worn off of Innoruuk`s Chosen."),
            Ev(26, "Your Mesmerization spell has worn off of Innoruuk`s Chosen."));

        Assert.Empty(t.Snapshot(T0.AddSeconds(27)));
    }

    /// <summary>Reported from play: the learned store read "Mesmerization V = 43" and the
    /// chip therefore started at 0:43 for a spell that runs about 36. Measured over the
    /// 690k-line fixture, the fades where exactly ONE same-named entry of yours was open
    /// (so the log can say which mez ended) put Mesmerization V at n=425, mode 38 (x41)
    /// with 37 (x36) and 39 (x26) beside it — and max 43. A second or two of log-flush
    /// jitter guarantees that a maximum taken over hundreds of samples lands in the tail,
    /// and the old rule could only ratchet upward. The cluster is the duration; the
    /// extreme is noise.</summary>
    [Fact]
    public void ARunOfThirtyEightSecondFadesWithOneFortyThreeSecondOutlierLearnsThirtyEight()
    {
        // Real lines from the fixture (Thu Jul 30 13:05, Befallen): the caster's own
        // Mesmerization V, a bystander-visible landing, and the caster-private fade —
        // which EQ logs WITHOUT the rank.
        var t = new MezTracker();
        for (var i = 0; i < 12; i++)
        {
            var c = i * 60;
            t.Apply(Ev(c, "You begin casting Mesmerization V."));
            t.Apply(Ev(c + 2, "a necromancer has been mesmerized."));
            t.Apply(Ev(c + 40, "Your Mesmerization spell has worn off of a necromancer."));
        }
        // One fade lands two seconds late — the log-flush tail that used to become the
        // whole estimate.
        t.Apply(Ev(720, "You begin casting Mesmerization V."));
        t.Apply(Ev(722, "a necromancer has been mesmerized."));
        t.Apply(Ev(765, "Your Mesmerization spell has worn off of a necromancer."));

        Assert.Equal(38, t.LearnedDurations["Mesmerization V"], 0);
    }

    /// <summary>The other half of the same report, and the reason the old maximum drifted
    /// so far: EQ logs the fade WITHOUT the rank and without any handle on WHICH creature
    /// it was, so with two of your own same-named entries open the tracker picks the
    /// longest-asleep one — and a fade of the YOUNGER twin is then credited to the older,
    /// inflating the gap by the whole stagger between them. Splitting the fixture's fades
    /// by whether exactly one same-named entry was open: unambiguous Mesmerization V tops
    /// out at 43s over 425 samples, the ambiguous ones reach 80s (and Mesmerization III
    /// reaches 90s against an unambiguous max of 36). A measurement the log cannot attribute
    /// is not evidence: the chip still goes (something of yours ended), the sample does not.</summary>
    [Fact]
    public void AFadeArrivingWhileTwoSameNamedMezzesAreOpenTeachesNothing()
    {
        // The fixture's AoE at Thu Jul 30 13:05:38 catches two necromancers in one second —
        // two creatures, two chips (issue #32). A re-mez ten seconds later refreshes one of
        // them, so the pair is now staggered: whichever fades, the gap is ambiguous.
        var t = Replay(
            Ev(0, "You begin casting Mesmerization V."),
            Ev(1, "a necromancer has been mesmerized."),
            Ev(1, "a necromancer has been mesmerized."),
            Ev(20, "You begin casting Mesmerization V."),
            Ev(22, "a necromancer has been mesmerized."));
        Assert.Equal(2, t.Snapshot(T0.AddSeconds(23)).Count);

        t.Apply(Ev(30, "Your Mesmerization spell has worn off of a necromancer."));

        Assert.False(t.LearnedDurations.ContainsKey("Mesmerization V"));   // not the 29s guess
        Assert.Single(t.Snapshot(T0.AddSeconds(31)));                      // one chip still went
    }

    /// <summary>Migration: stores written before this change hold ONE scalar per spell —
    /// the longest gap ever seen, which is exactly the biased number this change exists to
    /// stop trusting (the reported store read "Mesmerization V = 43"). Seeding it as a
    /// sample would let the bias dominate a fresh mode, so the old file is read and its
    /// scalars are dropped; the store re-fills from play. Loading it must never throw.</summary>
    [Fact]
    public void AnOldFormatScalarStoreLoadsWithoutThrowingAndItsInflatedValueIsDiscarded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mez-durations-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Mesmerization V":43,"Mesmerize II":32}""");
            var t = new MezTracker();
            t.AttachStore(path);   // must not throw

            Assert.Empty(t.LearnedDurations);

            // ...and the very next real fade is believed on its own terms, not blended
            // with the discarded 43.
            t.Apply(Ev(0, "You begin casting Mesmerization V."));
            t.Apply(Ev(2, "a necromancer has been mesmerized."));
            t.Apply(Ev(40, "Your Mesmerization spell has worn off of a necromancer."));
            Assert.Equal(38, t.LearnedDurations["Mesmerization V"], 0);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The samples themselves persist, not the number derived from them: a restart
    /// that kept only "38" would have nothing to add the next fade to, and the store would
    /// drift back to whatever single value it last wrote. A week of play should not have to
    /// be re-measured every launch (same bar as spawn timers and learned HoT durations).</summary>
    [Fact]
    public void TheLearnedSampleSetSurvivesARestartThroughTheStoreFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mez-durations-{Guid.NewGuid():N}.json");
        try
        {
            var t = new MezTracker();
            t.AttachStore(path);
            for (var i = 0; i < 12; i++)
            {
                var c = i * 60;
                t.Apply(Ev(c, "You begin casting Mesmerization V."));
                t.Apply(Ev(c + 2, "a necromancer has been mesmerized."));
                t.Apply(Ev(c + 40, "Your Mesmerization spell has worn off of a necromancer."));
            }

            var reborn = new MezTracker();
            reborn.AttachStore(path);
            Assert.Equal(38, reborn.LearnedDurations["Mesmerization V"], 0);

            // A late outlier after the restart is outvoted, which only works if the twelve
            // earlier samples came back with it.
            reborn.Apply(Ev(720, "You begin casting Mesmerization V."));
            reborn.Apply(Ev(722, "a necromancer has been mesmerized."));
            reborn.Apply(Ev(765, "Your Mesmerization spell has worn off of a necromancer."));
            Assert.Equal(38, reborn.LearnedDurations["Mesmerization V"], 0);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The trap in "use the mode once there are enough samples": the catalog says
    /// Mesmerization = 24s and the real spell runs ~38, so falling back to the catalog while
    /// the sample set fills would make every chip 14 seconds short after a store reset — a
    /// worse bug than the one being fixed, and invisible until a mez outlives its chip. Below
    /// the mode threshold the estimate therefore stays what it has always been: the longest
    /// unambiguous gap seen, which is biased high but never leaves a mez unattended.</summary>
    [Fact]
    public void OneUnambiguousFadeAloneStillBeatsTheCatalogsShorterGuess()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization V."),
            Ev(2, "a necromancer has been mesmerized."),
            Ev(40, "Your Mesmerization spell has worn off of a necromancer."),   // 38s
            Ev(90, "You begin casting Mesmerization V."),
            Ev(92, "a necromancer has been mesmerized."));

        Assert.Equal(38, t.LearnedDurations["Mesmerization V"], 0);   // not the catalog's 24
        var m = Assert.Single(t.Snapshot(T0.AddSeconds(93)));
        Assert.Equal(37, m.RemainingSeconds(T0.AddSeconds(93))!.Value, 0);
    }

    /// <summary>Two durations equally common is a coin toss the estimator has to settle the
    /// same way every time, and the safe side is the longer one: the sample set's
    /// contamination is one-directional — a mez cut short by a blow measures the time to the
    /// BREAK, which is always SHORTER than the spell, while nothing except a couple of seconds
    /// of log-flush jitter can make a gap longer. On a tie, prefer the value the short-side
    /// noise cannot have produced.</summary>
    [Fact]
    public void WhenTwoDurationsAreEquallyCommonTheLongerOneWins()
    {
        var t = new MezTracker();
        for (var i = 0; i < 2 * MezTracker.ModeMinimumSamples; i++)
        {
            var c = i * 60;
            t.Apply(Ev(c, "You begin casting Mesmerization V."));
            t.Apply(Ev(c + 2, "a necromancer has been mesmerized."));
            t.Apply(Ev(c + (i % 2 == 0 ? 40 : 36), "Your Mesmerization spell has worn off of a necromancer."));
        }
        // ...and one 48s straggler, so a tie broken correctly still cannot be the maximum.
        var last = 2 * MezTracker.ModeMinimumSamples * 60;
        t.Apply(Ev(last, "You begin casting Mesmerization V."));
        t.Apply(Ev(last + 2, "a necromancer has been mesmerized."));
        t.Apply(Ev(last + 50, "Your Mesmerization spell has worn off of a necromancer."));

        Assert.Equal(38, t.LearnedDurations["Mesmerization V"], 0);   // tied with 34
    }

    /// <summary>The sample set is bounded, and the bound is a recency window: a store that
    /// grew forever would carry a server's spell-duration change (or a rank the player
    /// re-ranked into) as dead weight for as long as the file survives, and the estimator
    /// would need thousands of new fades to outvote it. Oldest samples are evicted first, so
    /// a genuine change of duration takes over after one bound's worth of play.</summary>
    [Fact]
    public void TheSampleSetIsBoundedAndTheNewestEvidenceWins()
    {
        var t = new MezTracker();
        for (var i = 0; i < 2 * MezTracker.SampleCap; i++)
        {
            var c = i * 60;
            // The first bound's worth of fades run 38s; everything after runs 30s.
            t.Apply(Ev(c, "You begin casting Mesmerization V."));
            t.Apply(Ev(c + 2, "a necromancer has been mesmerized."));
            t.Apply(Ev(c + (i < MezTracker.SampleCap ? 40 : 32),
                "Your Mesmerization spell has worn off of a necromancer."));
        }

        Assert.Equal(30, t.LearnedDurations["Mesmerization V"], 0);
    }

    /// <summary>The shape a plain mode gets catastrophically wrong, and it is the shape the
    /// samples really have: they are a MIXTURE of two populations, not one cluster. Early
    /// breaks — a mez interrupted after a few seconds — pile up at the very bottom and repeat
    /// exactly, because "4 seconds" is a common outcome; natural fades cluster tightly at the
    /// true duration but spread over three or four adjacent values. Measured over the
    /// fixture's unambiguous Mesmerization III fades (n=27, the real list): 4, 4, 4, 4, 6,
    /// 11, 11, 13, 14, 15, 16, 16, 19, 27, 29, 29, 30, 31, 32, 33,
    /// 34, 34, 34, 35, 36, 36, 36. The most common single value there is 4 SECONDS — a chip
    /// claiming 4s for a ~36s mez, which is far worse than the 43s over-report this change
    /// set out to fix. So the estimator takes the mode of the UPPER cluster only.</summary>
    [Fact]
    public void ManyShortEarlyBreaksCannotOutvoteASmallClusterOfFullDurationFades()
    {
        var t = new MezTracker();
        // Six mezzes broken almost immediately (nothing in the log says so — the blow that
        // broke them was never parsed, which is exactly when a short gap becomes a sample),
        // then nine that ran their course at 34-36s.
        var gaps = new[] { 4, 4, 4, 4, 4, 4, 34, 35, 36, 34, 35, 36, 34, 35, 36 };
        for (var i = 0; i < gaps.Length; i++)
        {
            var c = i * 60;
            t.Apply(Ev(c, "You begin casting Mesmerization III."));
            t.Apply(Ev(c + 2, "a necromancer has been mesmerized."));
            t.Apply(Ev(c + 2 + gaps[i], "Your Mesmerization spell has worn off of a necromancer."));
        }

        // 4s is the most common value in the set and must lose anyway.
        Assert.InRange(t.LearnedDurations["Mesmerization III"], 33, 37);
    }

    [Fact]
    public void UnknownDurationChipsStillShowAndStillBreak()
    {
        // A mez spell missing from the catalog: chip appears with no countdown, and
        // damage still clears it. (Requires the spell to be cast-correlated — add the
        // name to MezSpells.json for it to track at all; this uses a catalog entry
        // with its duration nulled to simulate the pre-research state.)
        var t = new MezTracker([new MezSpellInfo { Name = "Mesmerize" }]);
        t.Apply(Ev(0, "You begin casting Mesmerize."));
        t.Apply(Ev(2, "an orc pawn has been mesmerized."));

        var m = Assert.Single(t.Snapshot(T0.AddSeconds(3)));
        Assert.Null(m.RemainingSeconds(T0.AddSeconds(3)));

        t.Apply(Ev(6, "Twiddley slashes an orc pawn for 5 points of damage."));
        Assert.Empty(t.Snapshot(T0.AddSeconds(7)));
    }
}
