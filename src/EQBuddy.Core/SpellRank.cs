namespace EQBuddy.Core;

/// <summary>
/// The rank suffix on a spell name. EverQuest Legends adds roman-numeral ranks to spells
/// ("Shiftless Deeds IV"), and each tier adds 10% duration - additive, confirmed three ways:
/// Shiftless Deeds VI shows 4 minutes in game against a 150s base (x1.6), the wiki's Spell
/// Level slider reads "Duration +60%" at level 6, and Mesmerization V measures ~36s against a
/// 24s base (x1.5). Compounding would give 1.1^6 = 1.77, which matches no observed value.
///
/// This splitter is deliberately naive about WHETHER the numeral is a rank. Some spells are
/// genuinely named with a trailing numeral and are not ranks of anything - 121 such names across
/// the 1,929-spell wiki harvest, of which 10 are in the shipped catalog ("Clarity II",
/// "Burnout IV", "Yaulp IV"). Only the catalog can tell the two apart, so that call lives in
/// <see cref="SpellDurationCatalog"/>.
/// </summary>
public static class SpellRank
{
    /// <summary>Duration added per rank tier.</summary>
    public const double PerTier = 0.10;

    /// <summary>Splits a trailing roman numeral off a name. Returns tier 0 when there is none.
    /// A single-word name is never split: the whole name would vanish, and "Ice" is a spell.</summary>
    public static (string Base, int Tier) Split(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.LastIndexOf(' ');
        if (space <= 0) return (trimmed, 0);

        var tier = ParseRoman(trimmed[(space + 1)..]);
        return tier > 0 ? (trimmed[..space], tier) : (trimmed, 0);
    }

    public static double Scale(double baseSeconds, int tier) =>
        baseSeconds * (1 + PerTier * tier);

    /// <summary>I..XXXIX, or 0 for anything that is not a well-formed roman numeral. Only
    /// I/V/X are accepted - L and beyond cannot be a spell rank, and "Cazic" should not be
    /// read as a number because it starts with C.</summary>
    private static int ParseRoman(string token)
    {
        if (token.Length is 0 or > 6) return 0;
        var total = 0; var previous = 0;
        for (var i = token.Length - 1; i >= 0; i--)
        {
            var value = token[i] switch { 'I' => 1, 'V' => 5, 'X' => 10, _ => 0 };
            if (value == 0) return 0;
            total += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }
        // Round-trips only for canonical spellings, so "IIII" and "VV" are rejected.
        return total is > 0 and < 40 && ToRoman(total) == token ? total : 0;
    }

    private static string ToRoman(int value)
    {
        var result = "";
        foreach (var (number, symbol) in
            (ReadOnlySpan<(int, string)>)[(10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")])
            while (value >= number) { result += symbol; value -= number; }
        return result;
    }
}
