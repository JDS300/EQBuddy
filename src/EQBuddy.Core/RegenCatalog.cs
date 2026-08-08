using System.Reflection;
using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>
/// The spells whose regen ticks log the amount-less "Your wounds begin to heal." line
/// (Data\RegenSpells.json, eqlwiki harvest 2026-08-06): Hymn of Restoration plus the
/// necro shadow-pact family. Their per-tick values power ESTIMATED regen healing —
/// wiki base numbers, so a floor: bard instrument resonance and spell ranks raise the
/// real amount, and none of it distinguishes overheal. Estimates are labeled est. and
/// never join the amount-based HPS totals (David's call, live test 2026-08-06).
/// </summary>
public static class RegenCatalog
{
    private static readonly Lazy<Dictionary<string, int>> PerTickByName = new(() =>
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("EQBuddy.Core.Data.RegenSpells.json");
            if (stream is null) return map;
            using var doc = JsonDocument.Parse(stream);
            foreach (var s in doc.RootElement.GetProperty("spells").EnumerateArray())
                if (s.GetProperty("name").GetString() is { Length: > 0 } name)
                    map[name] = s.GetProperty("perTickHp").GetInt32();
        }
        catch (Exception ex) { CoreLog.Error(ex); }
        return map;
    });

    /// <summary>Wiki base hp-per-tick for a regen spell (rank suffix stripped by the
    /// caller), or null when the spell isn't in the amount-less regen family.</summary>
    public static int? PerTick(string spellBaseName) =>
        PerTickByName.Value.TryGetValue(spellBaseName, out var v) ? v : null;
}
