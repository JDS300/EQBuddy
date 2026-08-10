using EQBuddy.Core;
using EQBuddy.UI.Shared;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// DoT states rendered into chip rows: countdown text, the refresh warning, and the ordering
/// that decides what your eye lands on first.
/// </summary>
public class DebuffChipPresentationTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc);

    private static DebuffState State(string target, string spell, double? remaining) =>
        new(target, spell, Caster: "", IsMine: true, LandedAt: T0, LastTickAt: T0,
            ExpiresAt: remaining is { } r ? T0.AddSeconds(r) : null);

    [Fact]
    public void ACountdownIsShownAsMinutesAndSeconds()
    {
        var chips = DebuffChipPresentation.Chips([State("a sand giant", "Immolate", 78)], T0, 10);

        Assert.Equal("1:18", Assert.Single(chips).CountdownText);
    }

    /// <summary>An unmeasured spell says so instead of showing a number nobody measured.</summary>
    [Fact]
    public void AnUnknownDurationShowsADashRatherThanZero()
    {
        var chips = DebuffChipPresentation.Chips([State("a sand giant", "Ignite", null)], T0, 10);

        var chip = Assert.Single(chips);
        Assert.Equal("--", chip.CountdownText);
        Assert.False(chip.IsDue);
    }

    [Fact]
    public void AnEffectInsideTheWarningWindowIsMarkedDue()
    {
        var chips = DebuffChipPresentation.Chips(
            [State("a sand giant", "Immolate", 8), State("a sand giant", "Choke", 40)], T0, 10);

        Assert.True(chips.Single(c => c.Name == "Immolate").IsDue);
        Assert.False(chips.Single(c => c.Name == "Choke").IsDue);
    }

    /// <summary>Soonest to drop first: the panel is read under pressure, and the row that
    /// needs recasting should not be somewhere in the middle.</summary>
    [Fact]
    public void TheSoonestToDropSortsFirstWithinATarget()
    {
        var chips = DebuffChipPresentation.Chips(
            [State("a sand giant", "Choke", 40), State("a sand giant", "Immolate", 8)], T0, 10);

        Assert.Equal(["Immolate", "Choke"], chips.Select(c => c.Name));
    }

    /// <summary>Unknown timers sort last: they are the ones we can say least about, so they
    /// must not sit above an effect that genuinely is about to drop.</summary>
    [Fact]
    public void UnknownTimersSortBelowKnownOnes()
    {
        var chips = DebuffChipPresentation.Chips(
            [State("a sand giant", "Ignite", null), State("a sand giant", "Choke", 40)], T0, 10);

        Assert.Equal(["Choke", "Ignite"], chips.Select(c => c.Name));
    }

    [Fact]
    public void EachTargetIsItsOwnGroup()
    {
        var chips = DebuffChipPresentation.Chips(
            [State("a sand giant", "Choke", 40), State("a dervish cutthroat", "Choke", 20)], T0, 10);

        Assert.Equal(2, chips.Select(c => c.Zone).Distinct().Count());
    }

    [Fact]
    public void YourOwnEffectsAreEmphasised()
    {
        var mine = State("a sand giant", "Immolate", 30);
        var theirs = mine with { IsMine = false, Caster = "Cognix", Spell = "Enfeeblement" };

        var chips = DebuffChipPresentation.Chips([mine, theirs], T0, 10);

        Assert.True(chips.Single(c => c.Name == "Immolate").Emphasis);
        var other = chips.Single(c => c.Name == "Enfeeblement");
        Assert.False(other.Emphasis);
        Assert.Contains("Cognix", other.Detail, StringComparison.Ordinal);
    }
}
