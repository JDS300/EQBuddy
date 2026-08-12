using System.Reflection;
using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>How much to trust a countdown. The ordering is the feature: a measurement always
/// beats an estimate, and an estimate always beats a guess - of which there are none.</summary>
public enum DurationCertainty
{
    /// <summary>Nobody knows. The chip shows "--", never a number.</summary>
    Unknown,
    /// <summary>Wiki base duration, scaled for rank. Shown marked, and discarded the moment a
    /// real measurement lands.</summary>
    Derived,
    /// <summary>Measured from this log. Authoritative - never adjusted toward the wiki.</summary>
    Measured,
}

/// <summary>A cast name resolved against the catalog. <paramref name="Tier"/> is 0 when the
/// spell is unranked or is genuinely named with a numeral.</summary>
public sealed record ResolvedDuration(string BaseName, int Tier, double Seconds);

/// <summary>
/// Base spell durations from eqlwiki, embedded rather than fetched (Data/SpellDurations.json).
/// A game overlay should not make an HTTP request mid-fight for a number that is only a
/// fallback estimate, and the harvest already on disk answers 680 spells offline.
///
/// The resolution order exists because of a trap worth stating plainly: 121 spells in the wiki
/// catalog END in a roman numeral as their real name - "Clarity II", "Burnout IV",
/// "Cannibalize IV", "Berserker Madness III". They are distinct spell pages, not ranks. So the
/// catalog is asked for the full name FIRST, and only a miss is reinterpreted as a rank. Read
/// the other way round, "Clarity II" would scale a duration the catalog already knows exactly.
/// </summary>
public sealed class SpellDurationCatalog
{
    private readonly IReadOnlyDictionary<string, double> _durations;
    private static SpellDurationCatalog? _embedded;

    public SpellDurationCatalog(IReadOnlyDictionary<string, double>? durations = null) =>
        _durations = durations is null
            ? LoadEmbedded()
            : new Dictionary<string, double>(durations, StringComparer.OrdinalIgnoreCase);

    /// <summary>The shipped catalog, loaded once. 680 spells.</summary>
    public static SpellDurationCatalog Embedded => _embedded ??= new SpellDurationCatalog();

    /// <summary>Base seconds for a cast name, scaled for rank - or null when the catalog
    /// cannot answer, which the panel renders as "--".</summary>
    public ResolvedDuration? Resolve(string castName)
    {
        var name = castName.Trim();
        if (name.Length == 0) return null;

        // Exact first: the catalog is the authority on what is a NAME and what is a rank.
        if (_durations.TryGetValue(name, out var exact))
            return new ResolvedDuration(name, 0, exact);

        var (baseName, tier) = SpellRank.Split(name);
        if (tier > 0 && _durations.TryGetValue(baseName, out var seconds))
            return new ResolvedDuration(baseName, tier, SpellRank.Scale(seconds, tier));

        return null;
    }

    /// <summary>The name that this spell's tick and fade lines will use. Those lines never
    /// carry the rank - measured across the fixture, 0 of all tick lines have a numeral, and
    /// "Your Mesmerization spell has worn off" appears 1197 times against 630 casts of
    /// "Mesmerization V" - so a ranked cast has to collapse onto its base to be tracked at all.
    /// A spell genuinely NAMED with a numeral keeps it, because its own tick lines will too.</summary>
    public string BaseNameOf(string castName)
    {
        var name = castName.Trim();
        return _durations.ContainsKey(name) ? name : SpellRank.Split(name).Base;
    }

    private static Dictionary<string, double> LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQBuddy.Core.Data.SpellDurations.json")
            ?? throw new InvalidOperationException("SpellDurations.json missing from resources");
        using var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.GetProperty("durations").EnumerateObject())
            result[entry.Name] = entry.Value.GetDouble();
        return result;
    }
}
