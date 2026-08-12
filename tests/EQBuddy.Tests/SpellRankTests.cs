using EQBuddy.Core;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// Splitting a rank off a spell name. This helper deliberately does NOT decide whether the
/// numeral it found is a rank at all - 121 spells in the wiki catalog are genuinely NAMED with
/// a trailing numeral ("Clarity II", "Burnout IV"), and only the catalog can tell them apart.
/// See SpellDurationCatalog.
/// </summary>
public class SpellRankTests
{
    [Theory]
    [InlineData("Shiftless Deeds IV", "Shiftless Deeds", 4)]
    [InlineData("Mesmerization V", "Mesmerization", 5)]
    [InlineData("Shiftless Deeds VI", "Shiftless Deeds", 6)]
    [InlineData("Beguile II", "Beguile", 2)]
    [InlineData("Heroic Leap I", "Heroic Leap", 1)]
    [InlineData("Efflorescing Heal III", "Efflorescing Heal", 3)]
    public void ATrailingRomanNumeralIsSplitOff(string name, string expectedBase, int expectedTier)
    {
        Assert.Equal((expectedBase, expectedTier), SpellRank.Split(name));
    }

    [Theory]
    [InlineData("Immolate")]
    [InlineData("Drifting Death")]
    [InlineData("Vengeance of the Wild")]
    public void AnUnrankedNameIsTierZero(string name)
    {
        Assert.Equal((name, 0), SpellRank.Split(name));
    }

    /// <summary>Real spell words that happen to be Roman letters must not be eaten. "Ice" is a
    /// spell Daggo casts 285 times in the fixture; "Mix" and "Dim" are Roman-parseable strings.</summary>
    [Theory]
    [InlineData("Ice")]
    [InlineData("Mana Sieve")]
    [InlineData("Chaos Flux")]
    public void ASingleWordNameIsNeverTreatedAsARank(string name)
    {
        Assert.Equal((name, 0), SpellRank.Split(name));
    }

    /// <summary>The formula is additive, not compounding: 1.1^6 is 1.77, and Shiftless Deeds VI
    /// shows exactly 4 minutes in game against a 150s base.</summary>
    [Theory]
    [InlineData(150, 6, 240)]   // Shiftless Deeds VI - 4 min, confirmed in game
    [InlineData(150, 4, 210)]   // Shiftless Deeds IV
    [InlineData(24, 5, 36)]     // Mesmerization V - measured ~36s
    [InlineData(48, 0, 48)]     // unranked is untouched
    public void DurationScalesTenPercentPerTier(double baseSeconds, int tier, double expected)
    {
        Assert.Equal(expected, SpellRank.Scale(baseSeconds, tier), precision: 6);
    }
}
