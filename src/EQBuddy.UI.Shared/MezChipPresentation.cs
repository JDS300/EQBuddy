using EQBuddy.Core;

namespace EQBuddy.UI.Shared;

/// <summary>Converts <see cref="MezTracker"/> state into chip rows for the mez chip
/// stack, shared by every UI. Not part of MezTracker itself: the tracker owns tracking
/// state, this owns display formatting (countdown text, due tint, numbering) — same
/// split as the spawn chip stack.</summary>
public static class MezChipPresentation
{
    /// <summary>Mez chips: who's asleep, wake-up countdown ("?" until the spell's
    /// duration is known), warning tint inside the last tick. Same-named entries are
    /// numbered — "orc pawn (2)" — since the log can't tell the creatures apart
    /// (issue #32 asked for separate timers rather than one merged chip). Takes an
    /// already-snapshotted state list rather than the tracker itself, so callers control
    /// the read lock / timing (<see cref="MezTracker.Snapshot"/>).</summary>
    public static List<SpawnChip> Chips(IReadOnlyList<MezState> states, DateTime now)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return states.Select(m =>
        {
            var n = seen[m.Target] = seen.GetValueOrDefault(m.Target) + 1;
            var dupe = states.Count(x => x.Target.Equals(m.Target, StringComparison.OrdinalIgnoreCase)) > 1;
            var remaining = m.RemainingSeconds(now);
            var text = remaining is { } r
                ? $"{(int)r / 60}:{(int)r % 60:00}"
                : "?";
            return new SpawnChip(
                Zone: "", Name: dupe ? $"{m.Target} ({n})" : m.Target, CountdownText: text,
                IsDue: remaining is <= 6,
                Detail: $"{m.Spell} by {m.Caster} · landed {m.LandedAt:h:mm:ss tt}",
                Icon: "💤");
        }).ToList();
    }
}
