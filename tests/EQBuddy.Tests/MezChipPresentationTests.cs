using EQBuddy.Core;
using EQBuddy.UI.Shared;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// The mez-chip presentation layer: <see cref="MezTracker.Snapshot"/> states rendered
/// into <see cref="SpawnChip"/> rows — countdown text, due tint, and the same-name
/// numbering issue #32 asked for. Moved out of WPF code-behind so both UIs share it and
/// it can be unit-tested (CONTRIBUTING.md's shared-first rule).
/// </summary>
public class MezChipPresentationTests
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

    /// <summary>Issue #32: same-second landings on same-named mobs are distinct
    /// creatures and get numbered chips — "orc pawn (1)" and "orc pawn (2)" — so a
    /// player can tell the two timers apart.</summary>
    [Fact]
    public void TwoSameNamedMobsMezzedAtOnceGetNumberedChips()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerization."),
            Ev(3, "an orc pawn has been mesmerized."),
            Ev(3, "an orc pawn has been mesmerized."));

        var chips = MezChipPresentation.Chips(t.Snapshot(T0.AddSeconds(4)), T0.AddSeconds(4));

        Assert.Equal(2, chips.Count);
        Assert.Contains(chips, c => c.Name == "Orc pawn (1)");
        Assert.Contains(chips, c => c.Name == "Orc pawn (2)");
    }

    /// <summary>A lone mezzed target isn't sharing the name with anything else, so its
    /// chip carries no "(1)" suffix — numbering only kicks in for genuine duplicates.</summary>
    [Fact]
    public void ASingleMezzedTargetHasNoNumberSuffix()
    {
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "ice boned skeleton has been mesmerized."));

        var chip = Assert.Single(MezChipPresentation.Chips(t.Snapshot(T0.AddSeconds(3)), T0.AddSeconds(3)));

        Assert.Equal("Ice boned skeleton", chip.Name);
    }

    /// <summary>Without a known duration the chip still appears (it clears on break)
    /// but shows "?" instead of a countdown.</summary>
    [Fact]
    public void UnknownDurationRendersAQuestionMark()
    {
        var t = new MezTracker([new MezSpellInfo { Name = "Mesmerize" }]);
        t.Apply(Ev(0, "You begin casting Mesmerize."));
        t.Apply(Ev(2, "an orc pawn has been mesmerized."));

        var chip = Assert.Single(MezChipPresentation.Chips(t.Snapshot(T0.AddSeconds(3)), T0.AddSeconds(3)));

        Assert.Equal("?", chip.CountdownText);
    }

    /// <summary>The chip tints "due" once 6s or less remain, and not before — the
    /// countdown's warning threshold.</summary>
    [Fact]
    public void IsDueTurnsTrueAtSixSecondsRemainingAndNotBefore()
    {
        // Mesmerize's catalog duration is 24s, landing at +2 => expires at +26.
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(2, "an orc pawn has been mesmerized."));

        var aboveThreshold = Assert.Single(
            MezChipPresentation.Chips(t.Snapshot(T0.AddSeconds(19)), T0.AddSeconds(19)));   // 7s left
        Assert.False(aboveThreshold.IsDue);

        var atThreshold = Assert.Single(
            MezChipPresentation.Chips(t.Snapshot(T0.AddSeconds(20)), T0.AddSeconds(20)));   // 6s left
        Assert.True(atThreshold.IsDue);
    }

    /// <summary>Countdown text formats as m:ss, e.g. 90s remaining shows "1:30".</summary>
    [Fact]
    public void CountdownFormatsAsMinutesColonSeconds()
    {
        // Learn a 96s duration so 90s remain exactly 6s after landing. (The fade is 100s
        // after the landing; learned durations snap down to the 6-second server tick, so
        // the tracker believes 96 — see MezTracker.Effective. Nothing here depends on which
        // number it is, only that the countdown crosses a minute boundary.)
        var t = Replay(
            Ev(0, "You begin casting Mesmerize."),
            Ev(0, "an orc pawn has been mesmerized."),
            Ev(100, "Your Mesmerize spell has worn off of an orc pawn."),
            Ev(200, "You begin casting Mesmerize."),
            Ev(200, "a gnoll has been mesmerized."));

        var chip = Assert.Single(MezChipPresentation.Chips(t.Snapshot(T0.AddSeconds(206)), T0.AddSeconds(206)));

        Assert.Equal("1:30", chip.CountdownText);
    }
}
