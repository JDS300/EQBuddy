using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>The wiki-harvested CC catalog (Data\CcSpells.json), added for Chaosrah's
/// report: "all CC" alerts missed enchanter stuns — the hand seed knew 6 stuns, the
/// game has 87.</summary>
public class CcSpellCatalogTests
{
    private const string Ts = "[Sat Jul 18 15:39:13 2026] ";

    [Theory]
    [InlineData("Color Slant", SpellCategory.Stun)]              // enchanter, never in the hand seed
    [InlineData("Brusco's Bombastic Bellow", SpellCategory.Stun)] // bard stun
    [InlineData("Harpy Voice", SpellCategory.Mesmerize)]          // NPC mez — breadth check
    [InlineData("Alluring Whispers", SpellCategory.Charm)]        // the NPC-charms-YOU spell
    public void WikiHarvestedSpellsClassify(string spell, SpellCategory expected)
    {
        Assert.Equal(expected, new SpellCatalog().Classify(spell));
    }

    [Fact]
    public void LogVerifiedSeedEntriesKeepTheirClassification()
    {
        // The bard song split (issue #29) is log-verified in the curated seed; whichever
        // source answers first, the classification must hold.
        Assert.Equal(SpellCategory.Charm, new SpellCatalog().Classify("Solon's Bravura"));
        Assert.Equal(SpellCategory.Mesmerize, new SpellCatalog().Classify("Kelin's Lucid Lullaby"));
    }

    [Fact]
    public void ColorFluxFadeFiresAnAnyCrowdControlRule()
    {
        // Chaosrah's exact gap, end to end: a Color Flux fade must count on an
        // "any CC" spell-fade watch rule.
        var rule = new TrackedRule
        {
            Name = "CC broke", Kind = WatchKind.SpellFade,
            SpellFilter = SpellFilter.AnyCrowdControl, Enabled = true,
        };
        var stats = new SessionStats();
        stats.Apply(LogParser.Parse(Ts + "Your Color Flux spell has worn off of an orc pawn.")!);
        var result = stats.Snapshot(null, [rule]).Tracked.Single();
        Assert.Equal(1, result.TotalQuantity);
    }


    /// <summary>Daggo's report: a "CC broke" alert fired every time the Tashania magic-
    /// resist debuff wore off. Tashania is not crowd control and is in no CC catalog —
    /// it was LEARNED as a charm, and the learned store made it permanent.
    ///
    /// The path, reproduced here in three lines: a cast EQBuddy cannot classify is in
    /// flight when a "has been charmed." line arrives, so it is nominated as the charm
    /// candidate; the "Attacking ... Master." tell for that same creature then confirms
    /// the pet and teaches the candidate. The tell is caster-only and the creature names
    /// match, so the evidence is real — what is wrong is the CAST it points at. When the
    /// charm itself came from something that logs no cast line, the most recent cast is
    /// simply whatever the enchanter did last, and Tash-before-charm is the standard
    /// opening. One coincidence, and the debuff is a charm forever.</summary>
    [Fact]
    public void ATashDebuffInFlightWhenSomethingElseCharmsIsNotLearnedAsACharm()
    {
        var stats = new SessionStats();
        Replay(stats,
            "You begin casting Tashania.",
            "a greater skeleton has been charmed.",
            "a greater skeleton tells you, 'Attacking an orc pawn Master.'");

        Assert.False(stats.Spells.IsCrowdControl("Tashania"));
    }

    /// <summary>The symptom itself, end to end: the fade of a Tash debuff must not count
    /// on an "any CC" rule the way a real mez or charm break does.</summary>
    [Fact]
    public void ATashaniaFadeDoesNotFireTheCcBrokeRule()
    {
        var rule = new TrackedRule
        {
            Name = "CC broke", Kind = WatchKind.SpellFade,
            SpellFilter = SpellFilter.AnyCrowdControl, Enabled = true,
        };
        var stats = new SessionStats();
        Replay(stats,
            "You begin casting Tashania.",
            "a greater skeleton has been charmed.",
            "a greater skeleton tells you, 'Attacking an orc pawn Master.'",
            "Your Tashania spell has worn off of an orc pawn.");

        Assert.Equal(0, stats.Snapshot(null, [rule]).Tracked.Single().TotalQuantity);
    }

    /// <summary>A charm spell that genuinely is outside every catalog — the case the
    /// candidate path exists for — still learns. The guard must cost nothing here.</summary>
    [Fact]
    public void AnUncataloguedCharmSpellIsStillLearnedFromTheMasterTell()
    {
        var stats = new SessionStats();
        Replay(stats,
            "You begin casting Boltran's Agacerie.",
            "a greater skeleton has been charmed.",
            "a greater skeleton tells you, 'Attacking an orc pawn Master.'");

        Assert.Equal(SpellCategory.Charm, stats.Spells.Classify("Boltran's Agacerie"));
    }

    /// <summary>Stores already poisoned keep firing the alert unless loading heals them,
    /// and Daggo's live profile is one (it carried {"Tashania": 1}). Same doctrine as
    /// MezTracker's quarantine of inflated durations: the fix has to reach the data the
    /// bug already wrote, not just stop writing more.</summary>
    [Fact]
    public void APoisonedCharmClassificationIsDroppedWhenTheStoreLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eqbuddy-cat-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Tashania":1,"Boltran's Agacerie":1}""");
            var catalog = new SpellCatalog();
            catalog.AttachStore(path);

            Assert.False(catalog.IsCrowdControl("Tashania"));
            // A genuinely learned charm in the same store is untouched.
            Assert.Equal(SpellCategory.Charm, catalog.Classify("Boltran's Agacerie"));
        }
        finally { File.Delete(path); }
    }

    private static void Replay(SessionStats stats, params string[] lines)
    {
        foreach (var line in lines)
            if (LogParser.Parse(Ts + line) is { } ev)
                stats.Apply(ev);
    }

}
