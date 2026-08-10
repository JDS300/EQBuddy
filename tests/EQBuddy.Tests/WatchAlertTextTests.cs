using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

public class WatchAlertTextTests
{
    [Fact]
    public void SpellFadeWithTargetSaysWhatItCameOff()
    {
        var rule = new TrackedRule { Kind = WatchKind.SpellFade };
        var result = Result("Spirit of the Puma (Drucilog)");

        Assert.Equal("Spirit of the Puma faded off Drucilog",
            WatchAlertText.MatchLabel(rule, result, 1));
    }

    [Fact]
    public void SelfBuffFadeSaysItCameOffYou()
    {
        var rule = new TrackedRule { Kind = WatchKind.SpellFade };
        var result = Result("Spirit of the Puma");

        Assert.Equal("Spirit of the Puma faded off you",
            WatchAlertText.MatchLabel(rule, result, 1));
    }

    [Fact]
    public void CountsUseTheAppsMultiplicationSignButSpeakAsWords()
    {
        var rule = new TrackedRule { Kind = WatchKind.Loot };
        var label = WatchAlertText.MatchLabel(rule, Result("Rusty Sword"), 3);

        Assert.Equal("Rusty Sword ×3", label);                       // matches every other count in the UI
        Assert.Equal("Rusty Sword 3 times", SpokenAlerts.Speakable(label));
    }

    private static TrackedRuleResult Result(string lastItem) =>
        new("Buff dropped", 1, [], 0, 0, null, DateTime.Now, lastItem);
}
