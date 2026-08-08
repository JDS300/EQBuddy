using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The contribution pack is text a player pastes ONTO a community wiki — the bar is
/// "meets eqlwiki house style and never overstates the data" (discussion #65). These
/// tests pin classification (what counts as new), the rarity honesty rules, and the
/// exact paste shapes for the three page situations.
/// </summary>
public class WikiContributionTests
{
    private static MobLookupResult PageWith(params string[] drops) => new(
        new MobInfo
        {
            Name = "Ambassador Dvinn", PageTitle = "Ambassador Dvinn",
            Drops = drops.Select(d => (d, "Common")).ToList(),
        },
        ItemLookupState.Cached, DateTime.UtcNow);

    private static readonly MobLookupResult Missing = new(null, ItemLookupState.NotFound, null);
    private static readonly MobLookupResult Offline = new(null, ItemLookupState.Offline, null);

    private static MobSummary Mob(string name, int kills, params MobLoot[] loot) =>
        new(name, kills, kills, 20, 0, 0, loot.ToList());

    // ---- classification ----

    [Fact]
    public void KnownWhenPageListsIt() =>
        Assert.Equal(WikiDropStatus.Known,
            WikiContribution.Classify(PageWith("Dragoon Dirk"), "Dragoon Dirk"));

    [Fact]
    public void TierSuffixFoldsBeforeComparing() =>
        Assert.Equal(WikiDropStatus.Known,
            WikiContribution.Classify(PageWith("Dragoon Dirk"), "Dragoon Dirk +2"));

    [Fact]
    public void BackticksDropBeforeComparing() =>
        Assert.Equal(WikiDropStatus.Known,
            WikiContribution.Classify(PageWith("Bracelet of Lrodd"), "Bracelet of L`rodd"));

    [Fact]
    public void NewWhenPageDoesNotListIt() =>
        Assert.Equal(WikiDropStatus.NewToPage,
            WikiContribution.Classify(PageWith("Dragoon Dirk"), "Black Heart"));

    [Fact]
    public void EmptyLootListIsItsOwnStatus() =>
        Assert.Equal(WikiDropStatus.PageHasNoLoot,
            WikiContribution.Classify(PageWith(), "Black Heart"));

    [Fact]
    public void MissingPageIsItsOwnStatus() =>
        Assert.Equal(WikiDropStatus.PageMissing,
            WikiContribution.Classify(Missing, "Black Heart"));

    [Theory]
    [InlineData(true)]   // never looked up
    [InlineData(false)]  // wiki unreachable
    public void NoAnswerMeansUnknownNotNew(bool nullLookup) =>
        Assert.Equal(WikiDropStatus.Unknown,
            WikiContribution.Classify(nullLookup ? null : Offline, "Black Heart"));

    // ---- rarity honesty ----

    [Theory]
    [InlineData(100, 20, "Always")]
    [InlineData(100, 10, "Common")]      // 10 straight drops can't prove "Always"
    [InlineData(60, 10, "Common")]
    [InlineData(30, 10, "Uncommon")]
    [InlineData(10, 10, "Rare")]
    [InlineData(2, 50, "Ultra Rare")]
    public void RarityFollowsTheWikisBands(double pct, int kills, string expected) =>
        Assert.Equal(expected, WikiContribution.SuggestRarity(pct, kills));

    [Theory]
    [InlineData(50.0, 9)]   // sample too thin
    [InlineData(null, 100)] // no rate at all
    public void ThinSamplesSuggestNothing(double? pct, int kills) =>
        Assert.Null(WikiContribution.SuggestRarity(pct, kills));

    // ---- edit links ----

    [Fact]
    public void EditUrlEscapesLikeTheWiki() =>
        Assert.Equal("https://eqlwiki.com/index.php?title=Orc_Legionnaire_(Crushbone)&action=edit",
            WikiContribution.EditUrl("Orc Legionnaire (Crushbone)"));

    // ---- the three paste shapes ----

    [Fact]
    public void NewDropOnExistingPageEmitsListItems()
    {
        var mob = Mob("Ambassador Dvinn", 12, new MobLoot("Black Heart", 5, 41.7));
        var text = WikiContribution.BuildExport(
            [new(mob, PageWith("Dragoon Dirk"))], "Dranak", "Legends", "Crushbone",
            new DateTime(2026, 8, 8, 14, 0, 0));
        Assert.Contains("https://eqlwiki.com/index.php?title=Ambassador_Dvinn&action=edit", text);
        Assert.Contains("<li> {{:Black Heart}} <span class='drare'>(Uncommon)</span></li>", text);
        Assert.Contains("Black Heart ×5 in 12 kills (41.7%)", text);
    }

    [Fact]
    public void EmptyLootPageEmitsWholeKnownLootBlock()
    {
        var mob = Mob("Orc Thaumaturgist", 12,
            new MobLoot("Words of Cazic-Thule", 7, 58.3), new MobLoot("Bone Chips", 2, 16.7));
        var text = WikiContribution.BuildExport(
            [new(mob, PageWith())], "Dranak", "Legends", "Crushbone",
            new DateTime(2026, 8, 8, 14, 0, 0));
        Assert.Contains("| known_loot = ", text);
        Assert.Contains("<ul><li> {{:Words of Cazic-Thule}} <span class='drare'>(Common)</span>", text);
        Assert.Contains("</li><li> {{:Bone Chips}} <span class='drare'>(Rare)</span>", text);
        Assert.Contains("</li></ul>", text);
    }

    [Fact]
    public void MissingPageEmitsFullSkeletonWithZoneAndCategory()
    {
        var mob = Mob("Gnoll Reaver", 3, new MobLoot("Gnoll Fang", 1, 33.3));
        var text = WikiContribution.BuildExport(
            [new(mob, Missing)], "Dranak", "Legends", "Blackburrow",
            new DateTime(2026, 8, 8, 14, 0, 0));
        Assert.Contains("Create page:  https://eqlwiki.com/index.php?title=Gnoll_Reaver&action=edit", text);
        Assert.Contains("{{Namedmobpage", text);
        Assert.Contains("| name          = Gnoll Reaver", text);
        Assert.Contains("| zone          = [[Blackburrow]]", text);
        Assert.Contains("[[Category:Blackburrow]]", text);
        // 3 kills is far too thin for a label — the entry ships bare.
        Assert.Contains("<ul><li> {{:Gnoll Fang}}", text);
        Assert.DoesNotContain("Gnoll Fang}} <span", text);
    }

    [Fact]
    public void TierSuffixFoldsInEmittedWikitext()
    {
        var mob = Mob("Orc Centurion", 15, new MobLoot("Crushbone Shoulderpads +2", 4, 26.7));
        var text = WikiContribution.BuildExport(
            [new(mob, PageWith("Dragoon Dirk"))], "Dranak", "Legends", "Crushbone",
            new DateTime(2026, 8, 8, 14, 0, 0));
        // The wiki catalogs base items; the +2 stays out of the transclusion but the
        // edit-summary evidence keeps the exact observed name.
        Assert.Contains("{{:Crushbone Shoulderpads}}", text);
        Assert.Contains("Crushbone Shoulderpads +2 ×4 in 15 kills", text);
    }

    [Fact]
    public void ResolvedZoneSuffixedTitleWinsOverDisplayName()
    {
        var lookup = new MobLookupResult(
            new MobInfo
            {
                Name = "Orc Legionnaire", PageTitle = "Orc Legionnaire (Crushbone)",
                Drops = [("Dragoon Dirk", "Common")],
            },
            ItemLookupState.Live, DateTime.UtcNow);
        var mob = Mob("Orc Legionnaire", 10, new MobLoot("Black Heart", 2, 20));
        var text = WikiContribution.BuildExport(
            [new(mob, lookup)], "Dranak", "Legends", "Crushbone",
            new DateTime(2026, 8, 8, 14, 0, 0));
        Assert.Contains("title=Orc_Legionnaire_(Crushbone)&action=edit", text);
    }

    [Fact]
    public void NothingNewSaysSoInsteadOfEmittingEmptySections()
    {
        var mob = Mob("Ambassador Dvinn", 12, new MobLoot("Dragoon Dirk", 3, 25));
        var text = WikiContribution.BuildExport(
            [new(mob, PageWith("Dragoon Dirk"))], "Dranak", "Legends", "Crushbone",
            new DateTime(2026, 8, 8, 14, 0, 0));
        Assert.Contains("already on the wiki", text);
        Assert.DoesNotContain("===", text);
    }

    [Fact]
    public void PendingLookupsAreNamedNotSilentlyDropped()
    {
        var known = Mob("Ambassador Dvinn", 12, new MobLoot("Black Heart", 5, 41.7));
        var pending = Mob("Emperor Crush", 2, new MobLoot("Crown of Crush", 1, 50));
        var text = WikiContribution.BuildExport(
            [new(known, PageWith("Dragoon Dirk")), new(pending, null)],
            "Dranak", "Legends", "Crushbone", new DateTime(2026, 8, 8, 14, 0, 0));
        Assert.Contains("Not checked against the wiki yet", text);
        Assert.Contains("Emperor Crush", text);
    }
}
