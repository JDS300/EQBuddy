using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

/// <summary>
/// Turning <see cref="HotState"/> into chip rows: the countdown to "stop healing", the
/// self/other split, and the recast warning. Same shape as MezChipPresentation.
/// </summary>
public class HotChipPresentationTests
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

    /// <summary>The countdown is time until the HoT stops ticking — 24s of healing from the
    /// first tick, so 18s remain a tick later.</summary>
    [Fact]
    public void AChipCountsDownToTheEndOfTheHealing()
    {
        var t = Replay(Ev(0, "You healed Chickpea over time for 61 hit points by Flowering Heal."));

        var chip = Assert.Single(HotChipPresentation.Chips(t.Snapshot(T0.AddSeconds(6)), T0.AddSeconds(6), "Daggo", showSelf: true));
        Assert.Equal("Chickpea", chip.Name);
        Assert.Equal("0:18", chip.CountdownText);
        Assert.False(chip.Emphasis);           // somebody else's chip
        Assert.Contains("Flowering Heal", chip.Detail);
    }

    /// <summary>Your own HoT is emphasised rather than hidden: the whole point is spotting
    /// at a glance which chips are people whose buff bar you cannot see.</summary>
    [Fact]
    public void YourOwnHotIsMarkedForEmphasis()
    {
        var t = Replay(Ev(0, "You healed Daggo over time for 130 hit points by Blossoming Heal."));

        var chip = Assert.Single(HotChipPresentation.Chips(t.Snapshot(T0), T0, "Daggo", showSelf: true));
        Assert.True(chip.Emphasis);
    }

    /// <summary>Turning self-chips off drops only your own; everyone else still shows.</summary>
    [Fact]
    public void SelfChipsCanBeTurnedOffWithoutAffectingOthers()
    {
        var t = Replay(
            Ev(0, "You healed Daggo over time for 130 hit points by Blossoming Heal."),
            Ev(1, "You healed Chickpea over time for 61 hit points by Flowering Heal."));

        var chip = Assert.Single(HotChipPresentation.Chips(t.Snapshot(T0.AddSeconds(2)), T0.AddSeconds(2), "Daggo", showSelf: false));
        Assert.Equal("Chickpea", chip.Name);
    }

    /// <summary>Inside the last tick the chip warns — that is the recast moment, and a HoT
    /// that lapses is healing you never did.</summary>
    [Fact]
    public void TheChipWarnsInsideTheFinalTick()
    {
        var t = Replay(Ev(0, "You healed Chickpea over time for 61 hit points by Flowering Heal."));

        Assert.False(Assert.Single(HotChipPresentation.Chips(t.Snapshot(T0.AddSeconds(17)), T0.AddSeconds(17), "Daggo", true)).IsDue);
        Assert.True(Assert.Single(HotChipPresentation.Chips(t.Snapshot(T0.AddSeconds(19)), T0.AddSeconds(19), "Daggo", true)).IsDue);
    }

    /// <summary>Two HoTs on two people are two chips, soonest-to-lapse first.</summary>
    [Fact]
    public void EachTargetGetsItsOwnChipSoonestFirst()
    {
        var t = Replay(
            Ev(0, "You healed Chickpea over time for 61 hit points by Flowering Heal."),
            Ev(6, "You healed Penuche over time for 61 hit points by Flowering Heal."));

        var chips = HotChipPresentation.Chips(t.Snapshot(T0.AddSeconds(7)), T0.AddSeconds(7), "Daggo", true);
        Assert.Equal(["Chickpea", "Penuche"], chips.Select(c => c.Name));
    }

    /// <summary>No character name yet (before the log names one) must not crash or silently
    /// treat every chip as self.</summary>
    [Fact]
    public void AnUnknownCharacterNameLeavesEveryChipUnemphasised()
    {
        var t = Replay(Ev(0, "You healed Daggo over time for 130 hit points by Blossoming Heal."));

        var chip = Assert.Single(HotChipPresentation.Chips(t.Snapshot(T0), T0, null, showSelf: false));
        Assert.False(chip.Emphasis);
    }
}
