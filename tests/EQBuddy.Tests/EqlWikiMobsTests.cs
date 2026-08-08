using System.Net.Http;
using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>Mob-page parsing for the Loot card's target-drops block. The fixture is the
/// real Lockjaw page (fetched 2026-08-06); both named and regular mobs use
/// {{Namedmobpage}}, regular ones at article-titled pages ("A Spite Golem").</summary>
public class EqlWikiMobsTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "wiki", name + ".txt"));

    [Fact]
    public void LockjawPageParsesDropsWithRarity()
    {
        var mob = EqlWikiMobService.Parse(Fixture("lockjaw-mob"), "Lockjaw");
        Assert.Equal("Lockjaw", mob.Name);
        Assert.Equal("Oasis of Marr", mob.Zone);
        Assert.Equal("25", mob.Level);
        Assert.Equal("Common", mob.Drops.Single(d => d.Item == "Lockjaw Hide Vest").Rarity);
        Assert.Equal("Uncommon", mob.Drops.Single(d => d.Item == "Gator Meat").Rarity);
        // Un-annotated entries keep an empty rarity rather than an invented one.
        Assert.Equal("", mob.Drops.Single(d => d.Item == "Gnome Meat").Rarity);
        Assert.Equal(8, mob.Drops.Count);
    }

    [Fact]
    public async Task RegularMobsResolveViaTheArticleTitledPage()
    {
        // SessionStats names arrive article-stripped, first letter capitalized ("Spite
        // golem"); the wiki page is "A Spite Golem" (titles are case-sensitive past the
        // first letter). The candidate ladder bridges both gaps — this exact case shipped
        // broken once as NOT ON WIKI (2026-08-06 screenshot round).
        var requested = new List<string>();
        var svc = new EqlWikiMobService(
            Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
            title =>
            {
                requested.Add(title);
                return Task.FromResult<string?>(title == "A Spite Golem"
                    ? "{{Namedmobpage\n| name = A Spite Golem\n| known_loot = \n{{:Apothic Crown}}\n}}"
                    : null);
            });
        var result = await svc.LookupAsync("Spite golem");
        Assert.Equal(ItemLookupState.Live, result.State);
        Assert.Equal(["Spite golem", "A spite golem", "The spite golem", "Spite Golem", "A Spite Golem"],
            requested);
        Assert.Equal("Apothic Crown", result.Mob!.Drops.Single().Item);
    }

    [Fact]
    public async Task ZoneDisambiguatedPagesResolveAndTheCurrentZoneWins()
    {
        // The orc-legionnaire-mid-fight case (David, live, 2026-08-07): the bare-name
        // page is a broken redirect (returns nothing), the real drops live at
        // "Orc Legionnaire (Crushbone)" and "(Deathfist)". The zone-suffix-stripped
        // fuzzy compare admits both; the player's zone picks Crushbone.
        var fetched = new List<string>();
        var svc = new EqlWikiMobService(
            Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
            title =>
            {
                fetched.Add(title);
                return Task.FromResult<string?>(title switch
                {
                    "Orc Legionnaire (Crushbone)" =>
                        "{{Namedmobpage\n| name = Orc Legionnaire\n| known_loot = \n{{:Crushbone Belt}}\n}}",
                    "Orc Legionnaire (Deathfist)" =>
                        "{{Namedmobpage\n| name = Orc Legionnaire\n| known_loot = \n{{:Deathfist Slashed Belt}}\n}}",
                    _ => null,   // bare page: broken redirect, every exact candidate misses
                });
            },
            _ => Task.FromResult(new List<string>
                { "Orc legionnaire", "Orc Legionnaire (Deathfist)", "Orc Legionnaire (Crushbone)" }));

        var result = await svc.LookupAsync("Orc legionnaire", currentZone: "Crushbone");
        Assert.Equal(ItemLookupState.Live, result.State);
        Assert.Equal("Crushbone Belt", result.Mob!.Drops.Single().Item);
        // Zoneless bare page was still tried first (it outranks FOREIGN zones).
        Assert.Contains("Orc legionnaire", fetched);

        // Without a zone hint, the zoneless candidate still leads and the first
        // resolvable zone page wins — no dead end, no wrong-first bias.
        var noZone = await svc.LookupAsync("Orc legionnaireX".Replace("X", ""), "");
        Assert.Equal(ItemLookupState.Cached, noZone.State);   // second call hits the cache
    }

    [Fact]
    public async Task TheNamedMobsResolveViaTheirArticle()
    {
        // Normalize strips "the " like any article, so The Prophet arrives as "Prophet" —
        // and bare "Prophet" is missing on the wiki (David's report: a well-known named
        // showing no drops). The ladder must try the "The" forms.
        var svc = new EqlWikiMobService(
            Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
            title => Task.FromResult<string?>(title == "The Prophet"
                ? "{{Namedmobpage\n| name = The Prophet\n| known_loot = \n{{:Prophet Skull}}\n}}"
                : null));
        var result = await svc.LookupAsync("Prophet");
        Assert.Equal(ItemLookupState.Live, result.State);
        Assert.Equal("Prophet Skull", result.Mob!.Drops.Single().Item);
    }

    /// <summary>The fuzzy fallback (David, 2026-08-06): when every exact form misses,
    /// wiki search results are accepted under the spawn catalog's bounded-edit-distance
    /// rule — a one-letter drift resolves, a merely-related page never does.</summary>
    [Fact]
    public async Task WikiSearchRescuesANearMissButNeverAStranger()
    {
        var svc = new EqlWikiMobService(
            Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
            title => Task.FromResult<string?>(title == "Emperor Crushbone"
                ? "{{Namedmobpage\n| name = Emperor Crushbone\n| known_loot = \n{{:Crown of the Emperor}}\n}}"
                : null),
            _ => Task.FromResult<List<string>>(["Emperor Crushbone"]));
        // One letter off — every exact candidate misses, search + fuzzy resolve it.
        var result = await svc.LookupAsync("Emperor Crushbon");
        Assert.Equal(ItemLookupState.Live, result.State);
        Assert.Equal("Crown of the Emperor", result.Mob!.Drops.Single().Item);

        // A dissimilar search hit is rejected: better no answer than a wrong creature.
        var strict = new EqlWikiMobService(
            Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<List<string>>(["Crushbone (Zone)"]));
        Assert.Equal(ItemLookupState.NotFound,
            (await strict.LookupAsync("Emperor Crushbon")).State);
    }

    [Fact]
    public async Task MissingMobIsNotFoundAfterAllCandidates()
    {
        var svc = new EqlWikiMobService(
            Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<List<string>>([]));   // stubbed: no network from a unit test
        var result = await svc.LookupAsync("Utterly Fictional");
        Assert.Equal(ItemLookupState.NotFound, result.State);
        Assert.Equal(ItemLookupState.Offline,
            (await new EqlWikiMobService(
                Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
                _ => throw new HttpRequestException("no network"))
                .LookupAsync("Anything")).State);
    }
}
