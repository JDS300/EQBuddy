using EQBuddy.Core;

namespace EQBuddy.Tests;

public class ZoneGraphTests
{
    private static ZoneGraph Graph() => new(new Dictionary<string, List<string>>
    {
        ["Greater Faydark"] = ["Crushbone", "Butcherblock Mountains", "Felwithe", "Lesser Faydark"],
        ["Crushbone"] = ["Greater Faydark"],
        ["Butcherblock Mountains"] = ["Greater Faydark", "Kaladim", "Dagnor's Cauldron"],
        ["Kaladim"] = ["Butcherblock Mountains"],
        ["Dagnor's Cauldron"] = ["Butcherblock Mountains", "Estate of Unrest"],
        ["Estate of Unrest"] = ["Dagnor's Cauldron"],
        ["Felwithe"] = ["Greater Faydark"],
        ["Lesser Faydark"] = ["Greater Faydark"],
        ["Qeynos"] = [],   // disconnected on purpose
    });

    [Fact]
    public void BfsFindsHopCountAndPath()
    {
        var d = Graph().Distance("Greater Faydark", "Estate of Unrest");
        Assert.NotNull(d);
        Assert.Equal(3, d!.Value.Hops);
        Assert.Equal(["Greater Faydark", "Butcherblock Mountains", "Dagnor's Cauldron", "Estate of Unrest"],
            d.Value.Path);
    }

    [Fact]
    public void SameZoneIsZeroHops()
    {
        Assert.Equal(0, Graph().Distance("Crushbone", "Crushbone")!.Value.Hops);
    }

    [Fact]
    public void ResolvesLogNameDriftByContainment()
    {
        // "You have entered The Estate of Unrest." vs the wiki title without "The".
        var d = Graph().Distance("The Estate of Unrest", "kaladim");
        Assert.NotNull(d);
        Assert.Equal(3, d!.Value.Hops);
    }

    [Fact]
    public void UnknownOrUnreachableIsNullNotWrong()
    {
        Assert.Null(Graph().Distance("Greater Faydark", "Plane of Sky"));
        Assert.Null(Graph().Distance("Greater Faydark", "Qeynos"));   // disconnected
    }
}
