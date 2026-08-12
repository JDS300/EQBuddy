using EQBuddy.Core;

namespace EQBuddy.UI.Shared;

/// <summary>
/// Turns tracked effects into chip rows. Shared so both UIs render the same panel and the
/// rules below are unit-testable (CONTRIBUTING.md's shared-first rule).
/// </summary>
public static class DebuffChipPresentation
{
    /// <summary>Shown instead of a countdown for a spell whose duration has never been
    /// measured. Not "0:00": the panel is read while deciding whether to recast, and a zero
    /// reads as "it just dropped" rather than "nobody knows".</summary>
    public const string UnknownCountdown = "--";

    /// <summary>Prefixes a countdown derived from the wiki's base duration rather than measured
    /// from this log. One character, because the chip is narrow and the slider makes it
    /// narrower - but the distinction has to survive a glance mid-fight, so it is in the text
    /// rather than in an opacity a screenshot would lose.</summary>
    public const string EstimatePrefix = "~";

    public static List<SpawnChip> Chips(
        IReadOnlyList<DebuffState> states, DateTime now, double warnSeconds) =>
        states
            .OrderBy(s => s.Target, StringComparer.OrdinalIgnoreCase)
            // Soonest to drop first, and unknowns last: the panel is read under pressure, so
            // the row that needs recasting must not be buried, and a timer we cannot compute
            // must not outrank one that is genuinely about to expire.
            .ThenBy(s => s.RemainingSeconds(now) ?? double.MaxValue)
            .ThenBy(s => s.Spell, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SpawnChip(
                Zone: s.Target,
                Name: s.Spell,
                CountdownText: Countdown(s.RemainingSeconds(now), s.Certainty),
                IsDue: s.IsAboutToDrop(now, warnSeconds),
                Detail: s.IsMine ? "" : s.Caster,
                Icon: "☠",
                Emphasis: s.IsMine))
            .ToList();

    private static string Countdown(double? remaining, DurationCertainty certainty)
    {
        if (remaining is not { } seconds) return UnknownCountdown;
        var whole = (int)Math.Round(seconds);
        var prefix = certainty == DurationCertainty.Derived ? EstimatePrefix : "";
        return $"{prefix}{whole / 60}:{whole % 60:00}";
    }
}
