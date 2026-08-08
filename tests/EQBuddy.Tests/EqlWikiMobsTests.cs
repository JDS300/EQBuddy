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
        Assert.Equal(["Spite golem", "A spite golem", "Spite Golem", "A Spite Golem"], requested);
        Assert.Equal("Apothic Crown", result.Mob!.Drops.Single().Item);
    }

    [Fact]
    public async Task MissingMobIsNotFoundAfterAllCandidates()
    {
        var svc = new EqlWikiMobService(
            Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
            _ => Task.FromResult<string?>(null));
        var result = await svc.LookupAsync("Utterly Fictional");
        Assert.Equal(ItemLookupState.NotFound, result.State);
        Assert.Equal(ItemLookupState.Offline,
            (await new EqlWikiMobService(
                Path.Combine(Path.GetTempPath(), $"mobcache-{Guid.NewGuid():N}"),
                _ => throw new HttpRequestException("no network"))
                .LookupAsync("Anything")).State);
    }
}
