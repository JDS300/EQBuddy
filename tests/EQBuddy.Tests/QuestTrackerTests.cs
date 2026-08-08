using EQBuddy.Core;

namespace EQBuddy.Tests;

public class QuestTrackerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"quest-ledger-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private QuestLedgerStore Store() => new(_path) { TrackFilter = _ => true };

    private static readonly DateTime T0 = new(2026, 8, 7, 12, 0, 0);

    [Fact]
    public void ReplayedLootDoesNotDoubleCount()
    {
        var store = Store();
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0);
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0.AddSeconds(10));
        // The launch-time full-log replay offers the same events again, oldest first.
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0);
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0.AddSeconds(10));
        Assert.Equal(2, store.For("dranak_legends")["Bone Chips"].Looted);
    }

    [Fact]
    public void ManualCountsJoinLootedAndZeroForgetsOnlyManual()
    {
        var store = Store();
        store.SetManual("dranak_legends", "Crushbone Belt", 4);
        store.RecordLoot("dranak_legends", "Crushbone Belt", 1, T0);
        var e = store.For("dranak_legends")["Crushbone Belt"];
        Assert.Equal(5, e.Total);
        store.SetManual("dranak_legends", "Crushbone Belt", 0);
        Assert.Equal(1, store.For("dranak_legends")["Crushbone Belt"].Total);
        // An entry that was ONLY manual disappears entirely at zero.
        store.SetManual("dranak_legends", "Gnoll Fang", 2);
        store.SetManual("dranak_legends", "Gnoll Fang", 0);
        Assert.False(store.For("dranak_legends").ContainsKey("Gnoll Fang"));
    }

    [Fact]
    public void HandInOffsetsZeroTheCountAndFutureLootCountsUp()
    {
        var store = Store();
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0);
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0.AddSeconds(5));
        // Hand-in: the UI writes manual = -looted, netting zero.
        store.SetManual("dranak_legends", "Bone Chips", -2);
        Assert.Equal(0, store.For("dranak_legends")["Bone Chips"].Total);
        // A fresh loot after the hand-in counts up from zero, not from two.
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0.AddSeconds(20));
        Assert.Equal(1, store.For("dranak_legends")["Bone Chips"].Total);
        // The offset can never push Total negative, even if asked to.
        store.SetManual("dranak_legends", "Bone Chips", -99);
        Assert.Equal(0, store.For("dranak_legends")["Bone Chips"].Total);
    }

    [Fact]
    public void LedgerPersistsAcrossReload()
    {
        Store().SetManual("dranak_legends", "Lightstone", 3);
        var reloaded = new QuestLedgerStore(_path);
        Assert.Equal(3, reloaded.For("dranak_legends")["Lightstone"].Manual);
        // And it's per character: another character sees nothing.
        Assert.Empty(reloaded.For("vex_legends"));
    }

    [Fact]
    public void FilterKeepsNonQuestLootOut_ButManualBypasses()
    {
        var store = new QuestLedgerStore(_path) { TrackFilter = i => i == "Bone Chips" };
        store.RecordLoot("dranak_legends", "Rat Whiskers", 5, T0);
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0);
        store.SetManual("dranak_legends", "Rusty Axe", 1);   // user typed it = relevant
        var owned = store.For("dranak_legends");
        Assert.False(owned.ContainsKey("Rat Whiskers"));
        Assert.Equal(1, owned["Bone Chips"].Looted);
        Assert.Equal(1, owned["Rusty Axe"].Manual);
    }

    // ---- matcher ----

    private static QuestCatalog Catalog() => new()
    {
        Quests =
        [
            new QuestEntry { Name = "Belt Collector", Items =
                [new QuestItemNeed { Name = "Crushbone Belt", Qty = 4 }] },
            new QuestEntry { Name = "Bone Ritual", Items =
                [new QuestItemNeed { Name = "Bone Chips", Qty = 2 },
                 new QuestItemNeed { Name = "Gnoll Fang", Qty = 1 }] },
            new QuestEntry { Name = "Grand Epic", Items =
                [new QuestItemNeed { Name = "Bone Chips", Qty = 1 },
                 new QuestItemNeed { Name = "Dragon Scale", Qty = 1 },
                 new QuestItemNeed { Name = "Phoenix Feather", Qty = 1 }] },
            new QuestEntry { Name = "Unrelated", Items =
                [new QuestItemNeed { Name = "Fish Scales", Qty = 1 }] },
        ],
        QuestItems = ["Lightstone"],
    };

    private static Dictionary<string, QuestLedgerStore.Entry> Owned(params (string Item, int N)[] items)
        => items.ToDictionary(i => i.Item, i => new QuestLedgerStore.Entry { Looted = i.N },
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void MatchesRankCompletionFractionThenSmallerQuests()
    {
        var matches = QuestMatcher.Match(Catalog(),
            Owned(("Bone Chips", 3), ("Gnoll Fang", 1), ("Crushbone Belt", 1)));
        // Bone Ritual 2/2 complete → first; Belt Collector 1/1 items-have but short on
        // quantity still ranks by fraction 1.0, smaller quest first among ties.
        Assert.Equal(["Belt Collector", "Bone Ritual", "Grand Epic"],
            matches.Select(m => m.Quest.Name).ToArray());
        Assert.True(matches[1].Complete);
        Assert.False(matches[0].Complete);          // 1 of 4 belts is not done
        Assert.Equal(1, matches[2].ItemsHave);      // epic barely grazed
        Assert.DoesNotContain(matches, m => m.Quest.Name == "Unrelated");
    }

    [Fact]
    public void TrackedQuestsAppearWithZeroOverlapAndSortFirst()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Grand Epic" };
        var matches = QuestMatcher.Match(Catalog(), Owned(("Crushbone Belt", 4)), tracked);
        // Grand Epic has zero owned items but is tracked → present, and first.
        Assert.Equal("Grand Epic", matches[0].Quest.Name);
        Assert.True(matches[0].Tracked);
        Assert.Equal(0, matches[0].ItemsHave);
        Assert.Equal("Belt Collector", matches[1].Quest.Name);
        // Untracked zero-overlap quests still stay out.
        Assert.DoesNotContain(matches, m => m.Quest.Name == "Unrelated");
    }

    [Fact]
    public void TrackedQuestsPersistPerCharacter()
    {
        var store = Store();
        store.SetTracked("dranak_legends", "Crushbone Belts", true);
        store.SetTracked("dranak_legends", "Bone Ritual", true);
        store.SetTracked("dranak_legends", "Bone Ritual", false);
        store.RecordLoot("dranak_legends", "Bone Chips", 1, T0);   // items and pins coexist
        var reloaded = new QuestLedgerStore(_path);
        Assert.Equal(["Crushbone Belts"], reloaded.TrackedFor("dranak_legends").ToArray());
        Assert.Equal(1, reloaded.For("dranak_legends")["Bone Chips"].Looted);
        Assert.Empty(reloaded.TrackedFor("vex_legends"));
    }

    [Fact]
    public void PreTrackingLedgerShapeMigrates()
    {
        // The one-day-old shape: char → item → entry, no Tracked list.
        File.WriteAllText(_path,
            """{"dranak_legends":{"Bone Chips":{"Looted":3,"Manual":1,"LastTime":"2026-08-07T12:00:00"}}}""");
        var store = new QuestLedgerStore(_path);
        Assert.Equal(4, store.For("dranak_legends")["Bone Chips"].Total);
        Assert.Empty(store.TrackedFor("dranak_legends"));
    }

    [Theory]
    [InlineData("ALL except NEC WIZ MAG ENC", "Warrior", true)]
    [InlineData("ALL except NEC WIZ MAG ENC", "Necromancer", false)]
    [InlineData("ALL except NEC WIZ MAG ENC", "Enchanter", false)]
    [InlineData("Cleric", "Cleric", true)]
    [InlineData("Cleric", "Druid", false)]
    [InlineData("Bard, Cleric, Druid, Paladin", "Bard", true)]
    [InlineData("Bard, Cleric, Druid, Paladin", "Rogue", false)]
    [InlineData("All", "Wizard", true)]
    [InlineData("", "Monk", true)]
    [InlineData("Any", "Shaman", true)]
    [InlineData("SHD only", "Shadow Knight", true)]
    [InlineData("see walkthrough", "Ranger", true)]   // no class tokens = unrestricted
    public void ClassFilterReadsTheWikisRestrictionText(string text, string cls, bool expected)
        => Assert.Equal(expected, QuestClassFilter.Matches(text, cls));

    [Fact]
    public void CompletionConsumesOneTurnInSetAndCounts()
    {
        var store = Store();
        var needs = new[] { new QuestItemNeed { Name = "Crushbone Belt", Qty = 1 } };
        store.RecordLoot("dranak_legends", "Crushbone Belt", 1, T0);
        store.RecordLoot("dranak_legends", "Crushbone Belt", 1, T0.AddSeconds(5));
        store.RecordLoot("dranak_legends", "Crushbone Belt", 1, T0.AddSeconds(9));

        store.RecordCompletion("dranak_legends", "Crushbone Belts", needs);
        Assert.Equal(2, store.For("dranak_legends")["Crushbone Belt"].Total);
        store.RecordCompletion("dranak_legends", "Crushbone Belts", needs);
        Assert.Equal(1, store.For("dranak_legends")["Crushbone Belt"].Total);
        Assert.Equal(2, store.CompletedFor("dranak_legends")["Crushbone Belts"]);

        // Persisted, per character.
        var reloaded = new QuestLedgerStore(_path);
        Assert.Equal(2, reloaded.CompletedFor("dranak_legends")["Crushbone Belts"]);
        Assert.Empty(reloaded.CompletedFor("vex_legends"));
    }

    [Fact]
    public void ReadyCountIsTheAffordableTurnInSets()
    {
        var quest = new QuestEntry { Name = "Belts", Items =
            [new QuestItemNeed { Name = "Crushbone Belt", Qty = 2 },
             new QuestItemNeed { Name = "Orc Tooth", Qty = 1 }] };
        var m = QuestMatcher.Match(new QuestCatalog { Quests = [quest] },
            Owned(("Crushbone Belt", 7), ("Orc Tooth", 2)));
        Assert.Equal(2, m[0].ReadyCount);   // min(7/2, 2/1) = min(3, 2)
        Assert.True(m[0].Complete);
    }

    [Fact]
    public void ZoneTouchMatchesWithNameDrift()
    {
        var q = new QuestEntry { Name = "Belts", StartZone = "South Kaladim",
            Zones = ["Crushbone", "Greater Faydark", "South Kaladim"] };
        Assert.True(q.TouchesZone("Crushbone"));
        Assert.True(q.TouchesZone("The Greater Faydark"));   // log article drift
        Assert.True(q.TouchesZone("south kaladim"));
        Assert.False(q.TouchesZone("Qeynos"));
        Assert.False(q.TouchesZone(""));
    }

    [Fact]
    public void QuestItemMarkerCoversTurnInsAndCategory()
    {
        var cat = Catalog();
        Assert.True(cat.IsQuestItem("bone chips"));      // turn-in, case-insensitive
        Assert.True(cat.IsQuestItem("Lightstone"));      // category member only
        Assert.False(cat.IsQuestItem("Rat Whiskers"));
        // The tint/badge signal is narrower: category-only items don't light up.
        Assert.True(cat.IsTurnInItem("bone chips"));
        Assert.False(cat.IsTurnInItem("Lightstone"));
        Assert.Equal(2, cat.QuestsWanting("Bone Chips").Count);
    }

    [Fact]
    public void HiddenQuestsLeaveTheOverlapViewAndPinsContradictHides()
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Belt Collector" };
        var matches = QuestMatcher.Match(Catalog(), Owned(("Crushbone Belt", 4)), hidden: hidden);
        Assert.Empty(matches);   // the only overlapping quest is dismissed

        // Pin ↔ hide are mutually exclusive in the store: the newer action wins.
        var store = Store();
        store.SetTracked("dranak_legends", "Belt Collector", true);
        store.SetHidden("dranak_legends", "Belt Collector", true);
        Assert.Empty(store.TrackedFor("dranak_legends"));
        Assert.Equal(["Belt Collector"], store.HiddenFor("dranak_legends").ToArray());
        store.SetTracked("dranak_legends", "Belt Collector", true);
        Assert.Empty(store.HiddenFor("dranak_legends"));
    }
}
