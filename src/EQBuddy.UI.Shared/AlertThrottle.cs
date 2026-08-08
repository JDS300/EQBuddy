using EQBuddy.Core;

namespace EQBuddy.UI.Shared;

/// <summary>
/// Per-rule alert cooldowns, scoped by the matched label for everything except Text rules.
///
/// The scope matters: a fade rule's label names its target ("Mesmerize (orc pawn)"), and a
/// cooldown keyed on the rule alone meant a mez fading on one mob muted the alert for a
/// different mob's fade seconds later — exactly the moment a second alert is information,
/// not spam. Repeats of the SAME label stay throttled. Text rules keep rule-only scope:
/// their label is the raw matched line, which is nearly always unique, and label scoping
/// would disable their cooldown entirely.
/// </summary>
public sealed class AlertCooldowns
{
    private readonly Dictionary<(string RuleId, string Label), DateTime> _lastFired = new();

    /// <summary>Bound the map: a session that fires thousands of distinct labels (busy loot
    /// rules) must not grow it forever. Entries older than the longest sensible cooldown are
    /// dead weight — no future check can be throttled by them.</summary>
    private static readonly TimeSpan PruneAge = TimeSpan.FromMinutes(10);
    private const int PruneAt = 512;

    /// <summary>True when the alert may fire now; records the firing when it may.</summary>
    public bool ShouldFire(TrackedRule rule, string label, TimeSpan cooldown, DateTime now)
    {
        var key = (rule.Id, rule.Kind == WatchKind.Text ? "" : label);
        if (_lastFired.TryGetValue(key, out var last) && now - last < cooldown) return false;
        if (_lastFired.Count >= PruneAt)
            foreach (var stale in _lastFired.Where(kv => now - kv.Value > PruneAge).ToList())
                _lastFired.Remove(stale.Key);
        _lastFired[key] = now;
        return true;
    }

    public void Clear() => _lastFired.Clear();
}

/// <summary>
/// First-wins gate for alert audio: when several alerts fire in one moment, one sound is
/// the alert and the rest are noise — and worse, the WPF player restarting per sound cut
/// the first clip off mid-play. The first sound in the window plays complete; sounds inside
/// the window are dropped, and a dropped sound does NOT extend the window (a steady stream
/// must not silence the app indefinitely). Banners are not gated — only audio.
/// </summary>
public sealed class SoundGate
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1.5);
    private DateTime _lastPlay = DateTime.MinValue;

    /// <summary>True when audio may play now; records the play when it may.</summary>
    public bool TryClaim(DateTime now)
    {
        if (now - _lastPlay < Window) return false;
        _lastPlay = now;
        return true;
    }
}
