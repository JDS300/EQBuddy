using EQBuddy.Core;

namespace EQBuddy.Tests;

public class AaCatalogTests
{
    [Fact]
    public void EmbeddedCatalogLoadsEveryAbility()
    {
        // 144 rows parsed from the eqlwiki Alternate Advancement page (2026-08-06);
        // duplicate names (the wiki's doubled Cleric "Divine Aura") fold to one lookup
        // entry, so the map may sit just under the row count — never above it.
        Assert.InRange(AaCatalog.Count, 140, 144);
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndCarriesTheEffectText()
    {
        var scr = AaCatalog.Find("spell casting reinforcement");
        Assert.NotNull(scr);
        Assert.Equal("Archetype", scr!.Category);
        Assert.Equal(4, scr.MaxRank);
        Assert.Contains("increases the duration of beneficial spells", scr.Effect);
    }

    [Fact]
    public void UnknownAbilityIsNullNotAGuess()
    {
        // The log names toggle rows ("Symphonic Aura: Enabled") the wiki page doesn't
        // list — the ledger shows them without a tooltip rather than borrowing one.
        Assert.Null(AaCatalog.Find("Symphonic Aura: Enabled"));
    }
}
