using EQBuddy.Core;

namespace EQBuddy.UI.Shared;

/// <summary>Converts <see cref="HotTracker"/> state into chip rows, shared by every UI.
/// Same split as <see cref="MezChipPresentation"/>: the tracker owns tracking, this owns
/// display (countdown text, recast warning, which chip is you).</summary>
public static class HotChipPresentation
{
    /// <summary>Warn this far out. One tick: past it there is no time left to land a
    /// recast before the healing stops, which is the only moment a HoT chip is urgent.</summary>
    public static readonly double DueSeconds = HotTracker.TickInterval.TotalSeconds;

    /// <summary>HoT chips: who you are healing and for how much longer, soonest to lapse
    /// first (the tracker already sorts). Your own HoT is emphasised rather than mixed in —
    /// your buff bar already shows it, while a groupmate's is invisible to you — and can be
    /// dropped entirely via <paramref name="showSelf"/>.</summary>
    /// <param name="selfName">The logged-in character, or null before the log names one;
    /// null means nothing can be identified as self, so nothing is emphasised or hidden.</param>
    public static List<SpawnChip> Chips(
        IReadOnlyList<HotState> states, DateTime now, string? selfName, bool showSelf)
    {
        var chips = new List<SpawnChip>();
        foreach (var h in states)
        {
            var isSelf = selfName is { Length: > 0 }
                && h.Target.Equals(selfName, StringComparison.OrdinalIgnoreCase);
            if (isSelf && !showSelf) continue;

            var remaining = h.RemainingSeconds(now);
            chips.Add(new SpawnChip(
                Zone: "",
                Name: h.Target,
                CountdownText: $"{(int)remaining / 60}:{(int)remaining % 60:00}",
                IsDue: remaining <= DueSeconds,
                Detail: $"{h.Spell} · started {h.FirstTick:h:mm:ss tt}",
                Icon: "🌿",
                Emphasis: isSelf));
        }
        return chips;
    }
}
