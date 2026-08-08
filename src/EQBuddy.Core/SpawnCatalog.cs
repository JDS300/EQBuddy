using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQBuddy.Core;

/// <summary>One named mob in the shipped spawn catalog.</summary>
public sealed class SpawnEntry
{
    public string Name { get; set; } = "";
    /// <summary>Other names the SAME creature's kill line may use. Legends renames
    /// classic named mobs — "the ghoul lord" is "Hoptor Thaggelum" in-game, classic
    /// name demoted to a subtitle (issue #38, confirmed from chrstahl's kill line) —
    /// and the wiki mostly still titles pages by the classic names.</summary>
    public List<string> Aliases { get; set; } = [];
    /// <summary>Documented respawn in seconds, or null when nobody has written it down —
    /// the zone's <see cref="SpawnZone.NamedDefaultSeconds"/> is the fallback, and a null
    /// there too means the timer has to come from the player.</summary>
    public double? RespawnSeconds { get; set; }
    /// <summary>True when <see cref="RespawnSeconds"/> was MEASURED (camped-on-sight log
    /// timestamps), not copied from a wiki. Trusted timers disable re-kill learning for
    /// the entry: a shorter gap against a measured clock means multiple spawn points
    /// sharing the name, not a faster respawn (David, 2026-08-04 — a learned 328s from
    /// multi-spawn Orc Taskmaster kills sat under Crushbone's measured 738s clock).</summary>
    public bool Trusted { get; set; }
    public string Variance { get; set; } = "";
    /// <summary>The placeholder NPC whose death also restarts this named's cycle, or "".
    /// Displayed as "Named — Placeholder (npc)" and matched against kill lines like the
    /// named itself: killing the PH is what a camp mostly does, and in classic spawn
    /// cycles it runs the same clock.</summary>
    public string Placeholder { get; set; } = "";
    public string Source { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>A zone's named mobs plus its zone-wide default named-respawn.</summary>
public sealed class SpawnZone
{
    public string Zone { get; set; } = "";
    /// <summary>The name the game's "You have entered X." line uses, when it differs
    /// from the wiki's title for the zone.</summary>
    public string LogZoneName { get; set; } = "";
    /// <summary>Additional names the zone-enter line might use — Legends renames zones
    /// versus classic ("The Ruins of ANCIENT Guk" vs classic "…Old Guk", issue #36), and
    /// a wrong single LogZoneName silently kills every timer in the zone. Short /who
    /// codes (guktop) belong here too.</summary>
    public List<string> LogZoneAliases { get; set; } = [];
    public double? NamedDefaultSeconds { get; set; }
    /// <summary>True when <see cref="NamedDefaultSeconds"/> is a MEASURED zone clock
    /// (see SpawnEntry.Trusted) — entries riding it don't re-kill-learn.</summary>
    public bool NamedDefaultTrusted { get; set; }
    public List<SpawnEntry> Named { get; set; } = [];

    public bool MatchesZoneName(string zoneName)
    {
        if (zoneName.Length == 0) return false;
        if (Eq(Zone, zoneName) || Eq(LogZoneName, zoneName)
            // "You have entered The Estate of Unrest." vs catalog "Estate of Unrest":
            // containment either way covers article and prefix differences.
            || Contains(zoneName, Zone) || Contains(Zone, zoneName)
            || (LogZoneName.Length > 0 && (Contains(zoneName, LogZoneName) || Contains(LogZoneName, zoneName))))
            return true;
        foreach (var alias in LogZoneAliases)
            if (Eq(alias, zoneName) || Contains(zoneName, alias) || Contains(alias, zoneName))
                return true;
        return false;

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static bool Contains(string hay, string needle) =>
            needle.Length >= 4 && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The shipped database of named-mob spawn timers (SPAWN-001), embedded so installs need
/// no extra files. Sources: eqlwiki.com (the EQ Legends community wiki, authoritative)
/// with classic-EQ references filling gaps — see the JSON's comment field. Times are
/// community lore, not game data: they ship as *defaults*, and every number is editable
/// in the Spawns window (edits live in spawn-overrides.json via <see cref="SpawnOverrides"/>,
/// never in this file, so an app update refreshing the catalog can't eat anyone's edits).
/// </summary>
public sealed class SpawnCatalog
{
    public List<SpawnZone> Zones { get; init; } = [];

    private sealed class CatalogFile
    {
        public string Comment { get; set; } = "";
        public List<SpawnZone> Zones { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static SpawnCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQBuddy.Core.Data.SpawnCatalog.json")
            ?? throw new InvalidOperationException("SpawnCatalog.json missing from resources");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, JsonOpts);
        return new SpawnCatalog { Zones = file?.Zones ?? [] };
    }

    /// <summary>The catalog zone the given log zone name refers to, or null. EQ Legends
    /// runs instanced tier variants — "Befallen 1 (Awakened)", "Clan Crushbone 2
    /// (Adaptive)" — which resolve to their base zone (observed in eqlog_Hugzee
    /// 2026-08-02). The variant's own named differ from classic; until the catalog
    /// learns them, resolving to the base zone at least keeps Follow and manual timers
    /// working there.</summary>
    public SpawnZone? FindZone(string zoneName)
    {
        // Exact/log-name matches beat containment so "Qeynos" resolves to Qeynos, not
        // Qeynos Hills.
        var found = Zones.FirstOrDefault(z =>
                string.Equals(z.Zone, zoneName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(z.LogZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
            ?? Zones.FirstOrDefault(z => z.MatchesZoneName(zoneName));
        if (found is not null) return found;

        var baseName = StripTierVariant(zoneName);
        return baseName == zoneName ? null : FindZone(baseName);
    }

    /// <summary>"Befallen 1 (Awakened)" → "Befallen"; anything without the trailing
    /// tier pattern comes back unchanged.</summary>
    public static string StripTierVariant(string zoneName) =>
        System.Text.RegularExpressions.Regex
            .Replace(zoneName, @"\s+\d+(\s*\([^)]*\))?$", "").Trim();

    /// <summary>Effective respawn seconds for an entry in a zone: the entry's own timer,
    /// else the zone default, else null (unknown — the player has to supply one).</summary>
    public static double? EffectiveSeconds(SpawnZone zone, SpawnEntry entry) =>
        entry.RespawnSeconds ?? zone.NamedDefaultSeconds;

    /// <summary>Case-insensitive name equality that shrugs off leading articles, a
    /// trailing plural, and name punctuation: catalog names are wiki titles
    /// ("a froglok ghoul lord"), kill lines arrive normalized ("froglok ghoul lord"),
    /// placeholder notes are sometimes plural ("orc centurions") — and wikis routinely
    /// drop the EQ backtick (wiki "Skeleton Lrodd" vs the log's "Skeleton L`rodd",
    /// found by David mid-camp), which must never cost anyone a timer.</summary>
    public static bool NameMatches(string catalogName, string killedName)
    {
        if (catalogName.Length == 0 || killedName.Length == 0) return false;
        return Fold(catalogName).Equals(Fold(killedName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Forgiving-but-bounded match for wiki typos (the Velious page spells Keljemor
    /// "Leljemor"; suggested by David): edit distance ≤1 on the folded names, ≤2 from
    /// twelve characters up, and never for names under six characters — "Red V" must
    /// not match "Red X". Callers must prefer exact matches: fuzzy is the fallback,
    /// checked only after every exact candidate has missed, so a typo'd catalog entry
    /// can't steal a kill from a correct one.
    /// </summary>
    public static bool NameMatchesFuzzy(string catalogName, string killedName)
    {
        if (NameMatches(catalogName, killedName)) return true;
        var a = Fold(catalogName);
        var b = Fold(killedName);
        if (a.Length < 6 || b.Length < 6) return false;
        var budget = Math.Max(a.Length, b.Length) >= 12 ? 2 : 1;
        return WithinEditDistance(a.ToLowerInvariant(), b.ToLowerInvariant(), budget);
    }

    private static string Fold(string s)
    {
        s = s.Trim().Replace("`", "").Replace("'", "");
        foreach (var article in (string[])["a ", "an ", "the "])
            if (s.Length > article.Length && s.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            { s = s[article.Length..]; break; }
        if (s.EndsWith('s') && !s.EndsWith("ss", StringComparison.Ordinal)) s = s[..^1];
        return s;
    }

    /// <summary>Banded Levenshtein: true when edit distance ≤ <paramref name="max"/>.</summary>
    private static bool WithinEditDistance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return false;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > max) return false;   // the whole row exceeded the budget — bail
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length] <= max;
    }
}
