using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

public class AlertThrottleTests
{
    private static readonly DateTime T0 = new(2026, 7, 18, 15, 0, 0);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    private static TrackedRule Rule(WatchKind kind = WatchKind.SpellFade) =>
        new() { Name = "CC broke", Kind = kind, SpellFilter = SpellFilter.AnyCrowdControl };

    [Fact]
    public void SameLabelIsThrottledInsideTheCooldown()
    {
        var cd = new AlertCooldowns();
        var rule = Rule();
        Assert.True(cd.ShouldFire(rule, "Mesmerize (orc pawn)", Cooldown, T0));
        Assert.False(cd.ShouldFire(rule, "Mesmerize (orc pawn)", Cooldown, T0.AddSeconds(3)));
        Assert.True(cd.ShouldFire(rule, "Mesmerize (orc pawn)", Cooldown, T0.AddSeconds(6)));
    }

    [Fact]
    public void ADifferentTargetFiresInsideTheCooldown()
    {
        // The incident this exists for: a mez fading on the mob you're on must not mute
        // the alert for a different mob's fade seconds later — the second one is the pull
        // you're about to take a beating from.
        var cd = new AlertCooldowns();
        var rule = Rule();
        Assert.True(cd.ShouldFire(rule, "Mesmerize (orc pawn)", Cooldown, T0));
        Assert.True(cd.ShouldFire(rule, "Mesmerize (orc centurion)", Cooldown, T0.AddSeconds(2)));
    }

    [Fact]
    public void TextRulesKeepRuleOnlyScope()
    {
        // A Text rule's label is the raw matched line — nearly always unique, so scoping
        // by label would disable its cooldown entirely.
        var cd = new AlertCooldowns();
        var rule = Rule(WatchKind.Text);
        Assert.True(cd.ShouldFire(rule, "INC ramp 1", TimeSpan.FromSeconds(1), T0));
        Assert.False(cd.ShouldFire(rule, "INC ramp 2", TimeSpan.FromSeconds(1), T0.AddMilliseconds(400)));
    }

    [Fact]
    public void TwoRulesSharingADisplayNameStayIndependent()
    {
        // Rules key on Id, never name (RULE-ID lesson: David's two "Asaka" rules).
        var cd = new AlertCooldowns();
        var a = Rule();
        var b = Rule();
        Assert.True(cd.ShouldFire(a, "match", Cooldown, T0));
        Assert.True(cd.ShouldFire(b, "match", Cooldown, T0));
    }

    [Fact]
    public void SoundGateFirstWinsAndDoesNotExtend()
    {
        var gate = new SoundGate();
        Assert.True(gate.TryClaim(T0));
        Assert.False(gate.TryClaim(T0.AddSeconds(1)));       // inside the window: dropped
        // The drop did NOT extend the window — a steady stream can't silence the app.
        Assert.True(gate.TryClaim(T0 + SoundGate.Window));
    }
}
