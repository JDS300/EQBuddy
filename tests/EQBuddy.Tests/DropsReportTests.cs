using EQBuddy.Core;

namespace EQBuddy.Tests;

public class DropsReportTests
{
    private static List<MobSummary> Mobs() =>
    [
        new("azarack", 12, 12, 30, 2.0, 0,
            [new MobLoot("Manna Bread", 5, 41.7), new MobLoot("Gold Ring, Sky", 1, 8.3)]),
        new("gust of wind", 4, 4, 20, 1.0, 0, [new MobLoot("Essence of Wind", 2, 50.0)]),
        new("harmless sparrow", 3, 3, 5, 0, 0, []),   // no drops → excluded from exports
    ];

    [Fact]
    public void TextReportShowsRatesWithTheirDenominators()
    {
        var text = DropsReport.ToText(Mobs(), "Dranak", "legends", new DateTime(2026, 8, 7, 9, 0, 0));
        Assert.Contains("Dranak (legends)", text);
        Assert.Contains("session started 2026-08-07 09:00", text);
        Assert.Contains("azarack — 12 kills", text);
        Assert.Contains("  Manna Bread x5 (41.7% of 12 kills)", text);
        Assert.Contains("gust of wind — 4 kills", text);
        Assert.DoesNotContain("harmless sparrow", text);
    }

    [Fact]
    public void CsvQuotesCommasAndSkipsDroplessMobs()
    {
        var csv = DropsReport.ToCsv(Mobs());
        var lines = csv.TrimEnd().Split('\n').Select(l => l.TrimEnd()).ToArray();
        Assert.Equal("creature,kills,item,count,observed_drop_pct", lines[0]);
        Assert.Equal("azarack,12,Manna Bread,5,41.7", lines[1]);
        Assert.Equal("azarack,12,\"Gold Ring, Sky\",1,8.3", lines[2]);   // comma → quoted
        Assert.Equal("gust of wind,4,Essence of Wind,2,50", lines[3]);
        Assert.Equal(4, lines.Length);   // sparrow contributes nothing
    }

    [Fact]
    public void EmptySessionSaysSoInsteadOfVanishing()
    {
        var text = DropsReport.ToText([], "Dranak", "legends", null);
        Assert.Contains("(no drops recorded this session)", text);
    }
}
