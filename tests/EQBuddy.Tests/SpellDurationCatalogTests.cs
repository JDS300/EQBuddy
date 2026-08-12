using EQBuddy.Core;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// Resolving a cast name to a base duration. The whole contract is the ordering: an exact
/// catalog hit beats a rank interpretation, because 121 wiki spells are genuinely NAMED with a
/// trailing numeral and reading "Clarity II" as tier-2 Clarity would scale a duration the
/// catalog already knows exactly.
/// </summary>
public class SpellDurationCatalogTests
{
    private static readonly SpellDurationCatalog Catalog = new(new Dictionary<string, double>
    {
        ["Shiftless Deeds"] = 150,
        ["Mesmerization"] = 24,
        ["Immolate"] = 48,
        ["Clarity II"] = 2100,
    });

    /// <summary>The trap. "Clarity II" is a spell, not a rank of "Clarity" - and note the
    /// fixture catalog has no "Clarity" entry at all, so a rank reading would resolve nothing
    /// while the correct reading answers exactly.</summary>
    [Fact]
    public void ASpellNamedWithANumeralResolvesExactlyAndIsNotScaled()
    {
        var resolved = Catalog.Resolve("Clarity II");

        Assert.Equal(new ResolvedDuration("Clarity II", 0, 2100), resolved);
    }

    [Fact]
    public void ARankedSpellDerivesFromItsBase()
    {
        Assert.Equal(new ResolvedDuration("Shiftless Deeds", 4, 210), Catalog.Resolve("Shiftless Deeds IV"));
        Assert.Equal(new ResolvedDuration("Shiftless Deeds", 6, 240), Catalog.Resolve("Shiftless Deeds VI"));
        Assert.Equal(new ResolvedDuration("Mesmerization", 5, 36), Catalog.Resolve("Mesmerization V"));
    }

    [Fact]
    public void AnUnrankedSpellResolvesToItsOwnDuration()
    {
        Assert.Equal(new ResolvedDuration("Immolate", 0, 48), Catalog.Resolve("Immolate"));
    }

    /// <summary>No base page means no number. Heroic Leap has no wiki duration entry, so the
    /// panel must say "--" rather than reach for something plausible.</summary>
    [Fact]
    public void AnUnknownSpellResolvesToNothing()
    {
        Assert.Null(Catalog.Resolve("Heroic Leap I"));
        Assert.Null(Catalog.Resolve("Some Spell That Does Not Exist"));
    }

    /// <summary>The tracking key: ticks and fade lines never carry the rank, so a ranked cast
    /// has to collapse onto the same key the tick lines will use.</summary>
    [Fact]
    public void TheBaseNameIsTheNameTickLinesWillUse()
    {
        Assert.Equal("Shiftless Deeds", Catalog.BaseNameOf("Shiftless Deeds IV"));
        Assert.Equal("Immolate", Catalog.BaseNameOf("Immolate"));
        // A spell genuinely named with a numeral keeps it - its tick lines carry it too.
        Assert.Equal("Clarity II", Catalog.BaseNameOf("Clarity II"));
        // Unknown to the catalog: fall back to the naive split rather than refuse to track.
        Assert.Equal("Heroic Leap", Catalog.BaseNameOf("Heroic Leap I"));
    }

    /// <summary>Guards the shipped data, not the code. These four are the values the whole
    /// feature was verified against.</summary>
    [Fact]
    public void TheEmbeddedCatalogCarriesTheVerifiedDurations()
    {
        var catalog = SpellDurationCatalog.Embedded;

        Assert.Equal(150, catalog.Resolve("Shiftless Deeds")!.Seconds);
        Assert.Equal(48, catalog.Resolve("Immolate")!.Seconds);
        Assert.Equal(24, catalog.Resolve("Mesmerization")!.Seconds);
        // Confirmed in game: Shiftless Deeds VI shows 4 minutes.
        Assert.Equal(240, catalog.Resolve("Shiftless Deeds VI")!.Seconds);
    }

    /// <summary>Cripple's wiki duration is the level-scaled range "6.3 minutes @L53 to 7.0
    /// minutes @L60". There is no single base to multiply, so it is absent by design.</summary>
    [Fact]
    public void ALevelScaledDurationIsAbsentRatherThanAveraged()
    {
        Assert.Null(SpellDurationCatalog.Embedded.Resolve("Cripple"));
    }
}
