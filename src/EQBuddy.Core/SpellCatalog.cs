using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQBuddy.Core;

/// <summary>What a spell does, as far as we can tell from the log.</summary>
public enum SpellCategory
{
    Unknown = 0,
    Charm,
    Mesmerize,
    Root,
    Lull,
    Stun,
    Heal,
    DirectDamage,
    DamageOverTime,
    /// <summary>A heal that ticks — "healed you over time for N hit points by X". Learned
    /// from those ticks, which name the spell; distinct from Heal so a fade rule can watch
    /// "my HoT dropped" without firing on every Light Healing.</summary>
    HealOverTime,
}

/// <summary>
/// Maps spell names to what they do. Two sources feed it:
///
/// 1. A small seed table of crowd-control spells (below). CC is the only category we
///    cannot learn from the log, because charms, mezzes, roots, lulls and stuns produce
///    no numbers — there is nothing to observe. The seed covers the cold start.
/// 2. Observation. Damage and heal spells label themselves: a spell that shows up in a
///    "has taken N damage from your X" line IS a damage-over-time spell, so a hardcoded
///    list for those categories would be pure maintenance debt. <see cref="Learn"/> is
///    called as those lines are parsed. Charm also self-labels via the cast → blink →
///    "Attacking … Master." sequence, which lets us extend the seed at runtime.
///
/// A miss returns <see cref="SpellCategory.Unknown"/> and every caller degrades to its
/// previous behavior, so an unrecognised spell is never worse than not having this class.
///
/// Instance state (not static) so sessions and tests never leak learned spells into
/// each other.
/// </summary>
public sealed partial class SpellCatalog
{
    // Spell ranks appear as roman numerals in EQ Legends ("Stinging Swarm V",
    // "Light Healing V", "Heroic Leap I"), so rank variants must collapse onto the base
    // name before lookup. Matching exact names without this silently misses every
    // ranked spell a character learns past the first one.
    [GeneratedRegex(@"\s+[IVX]{1,6}$")]
    private static partial Regex RankSuffixRx();

    /// <summary>
    /// Crowd-control seed list, adapted from Spyxy's DPS Meter
    /// (https://github.com/khadesh/SpyxysDPSMeter, MIT — see NOTICE) which classifies
    /// spells from equivalent tables. Names are stored without rank suffixes.
    ///
    /// Only "Befriend Animal" is confirmed against a real EQ Legends log so far; the rest
    /// are long-standing EverQuest spell lines carried over. Wrong entries are worse than
    /// missing ones, so keep this conservative and let observation fill the gaps.
    /// </summary>
    private static readonly Dictionary<string, SpellCategory> Seed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // -- Charm --
            ["Charm"] = SpellCategory.Charm,
            ["Beguile"] = SpellCategory.Charm,
            ["Cajoling Whispers"] = SpellCategory.Charm,
            ["Allure"] = SpellCategory.Charm,
            ["Dominate"] = SpellCategory.Charm,
            ["Boltran's Agacerie"] = SpellCategory.Charm,
            ["Befriend Animal"] = SpellCategory.Charm,   // verified: eqlog_Douglas_qeynos
            ["Charm Animals"] = SpellCategory.Charm,
            ["Beguile Animals"] = SpellCategory.Charm,
            ["Allure of the Wild"] = SpellCategory.Charm,
            ["Call of Karana"] = SpellCategory.Charm,
            ["Dominate Undead"] = SpellCategory.Charm,
            ["Cajole Undead"] = SpellCategory.Charm,
            ["Enslave Death"] = SpellCategory.Charm,
            ["Thrall of Bones"] = SpellCategory.Charm,
            // Confirmed-in-Legends charms from Vellum670's list + the eqlwiki sweep
            // (issue #29, 2026-08-04). The bard SONGS are the important ones: they match
            // no family fragment, and song starts weren't even parsed before — a bard's
            // charm could never claim. "Solon's Bravura" is the Legends in-game name of
            // Bewitching Bravura; both kept. Tunare`s Request carries the EQ backtick.
            ["Solon's Song of the Sirens"] = SpellCategory.Charm,
            ["Solon's Bewitching Bravura"] = SpellCategory.Charm,
            ["Solon's Bravura"] = SpellCategory.Charm,
            ["Beguile Plants"] = SpellCategory.Charm,
            ["Beguile Undead"] = SpellCategory.Charm,
            ["Dictate"] = SpellCategory.Charm,
            ["Tunare`s Request"] = SpellCategory.Charm,

            // -- Mesmerize --
            ["Mesmerize"] = SpellCategory.Mesmerize,
            ["Mesmerization"] = SpellCategory.Mesmerize,
            ["Enthrall"] = SpellCategory.Mesmerize,
            ["Entrance"] = SpellCategory.Mesmerize,
            ["Dazzle"] = SpellCategory.Mesmerize,
            ["Rapture"] = SpellCategory.Mesmerize,
            ["Kelin's Lucid Lullaby"] = SpellCategory.Mesmerize,
            // Bard mez songs (eqlwiki sweep 2026-08-04) — they share the "eyes glaze
            // over" landing with the bard CHARM songs, so classification is what keeps
            // a mez song from ever claiming a pet.
            ["Crission's Pixie Strike"] = SpellCategory.Mesmerize,
            ["Sionachie's Dreams"] = SpellCategory.Mesmerize,
            ["Song of Twilight"] = SpellCategory.Mesmerize,
            ["Entrancing Lights"] = SpellCategory.Mesmerize,
            ["Fascination"] = SpellCategory.Mesmerize,
            ["Glamour of Kintaz"] = SpellCategory.Mesmerize,
            ["Screaming Terror"] = SpellCategory.Mesmerize,

            // -- Root --
            ["Root"] = SpellCategory.Root,
            ["Ensnaring Roots"] = SpellCategory.Root,
            ["Engulfing Roots"] = SpellCategory.Root,
            ["Engorging Roots"] = SpellCategory.Root,
            ["Grasping Roots"] = SpellCategory.Root,
            ["Paralyzing Earth"] = SpellCategory.Root,
            ["Immobilize"] = SpellCategory.Root,

            // -- Lull --
            ["Lull"] = SpellCategory.Lull,
            ["Soothe"] = SpellCategory.Lull,
            ["Calm"] = SpellCategory.Lull,
            ["Pacify"] = SpellCategory.Lull,
            ["Harmony"] = SpellCategory.Lull,
            ["Wake of Tranquility"] = SpellCategory.Lull,
            ["Mollify"] = SpellCategory.Lull,
            ["Alliance"] = SpellCategory.Lull,

            // -- Stun --
            ["Stun"] = SpellCategory.Stun,
            ["Divine Stun"] = SpellCategory.Stun,
            ["Color Flux"] = SpellCategory.Stun,
            ["Color Shift"] = SpellCategory.Stun,
            ["Color Skew"] = SpellCategory.Stun,
            ["Scintillating Colors"] = SpellCategory.Stun,

            // -- Heal over time --
            // HoTs mostly self-label (their ticks name the spell), so this seed only
            // covers the cold start: a fade arriving before the first tick was seen.
            ["Echoing Light"] = SpellCategory.HealOverTime,  // verified: eqlog_Hugzee
            ["Budding Heal"] = SpellCategory.HealOverTime,   // verified: eqlog_Hugzee
            ["Regeneration"] = SpellCategory.HealOverTime,
            ["Chloroplast"] = SpellCategory.HealOverTime,
            ["Regrowth"] = SpellCategory.HealOverTime,
        };

    /// <summary>
    /// Family fragments, tried in order when a spell isn't in the seed list or already
    /// learned. EverQuest names spells in families ("Ensnaring Roots", "Engulfing Roots",
    /// "Engorging Roots"), so a fragment covers a whole line — including ranks we've never
    /// seen — where an exact list only covers what someone remembered to type.
    ///
    /// ORDER MATTERS, and the reason is "Lullaby": Kelin's Lucid Lullaby is a MEZ, but it
    /// contains "Lull". Anything whose fragment is a substring of another family's names
    /// has to be listed before the family it would be stolen by. Keep this list narrow —
    /// a wrong classification is worse than none, because callers treat Unknown as
    /// "fall back to previous behavior" while a wrong answer silently misfires alerts.
    /// </summary>
    private static readonly (string Fragment, SpellCategory Category)[] Families =
    [
        // Must precede the Lull family — "Lullaby" contains "Lull".
        ("lullaby", SpellCategory.Mesmerize),

        ("mesmeriz", SpellCategory.Mesmerize),
        ("enthrall", SpellCategory.Mesmerize),
        ("entrance", SpellCategory.Mesmerize),

        ("charm", SpellCategory.Charm),
        ("beguil", SpellCategory.Charm),
        ("dominat", SpellCategory.Charm),
        ("enslave", SpellCategory.Charm),
        ("allure", SpellCategory.Charm),
        ("befriend", SpellCategory.Charm),
        ("cajol", SpellCategory.Charm),

        ("root", SpellCategory.Root),
        ("ensnar", SpellCategory.Root),
        ("entrap", SpellCategory.Root),
        ("immobiliz", SpellCategory.Root),
        ("paralyz", SpellCategory.Root),

        ("pacif", SpellCategory.Lull),
        ("lull", SpellCategory.Lull),
        ("sooth", SpellCategory.Lull),
        ("mollif", SpellCategory.Lull),

        ("stun", SpellCategory.Stun),
        ("scintillat", SpellCategory.Stun),
    ];

    /// <summary>
    /// The wiki-harvested CC catalog (Data\CcSpells.json — 174 spells from the full
    /// eqlwiki Spellpage sweep, 2026-08-06, prompted by Chaosrah's report that enchanter
    /// stuns like Color Flux never fired "all CC" alerts: the stun family alone has 87
    /// entries, far past anything a hand list would catch). The curated seed above still
    /// wins on conflicts — it carries log-verified decisions like the bard mez/charm song
    /// split — the harvest fills everything else.
    /// </summary>
    private static readonly Lazy<Dictionary<string, SpellCategory>> WikiCc = new(() =>
    {
        var map = new Dictionary<string, SpellCategory>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("EQBuddy.Core.Data.CcSpells.json");
            if (stream is null) return map;
            using var doc = JsonDocument.Parse(stream);
            foreach (var s in doc.RootElement.GetProperty("spells").EnumerateArray())
                if (Enum.TryParse<SpellCategory>(s.GetProperty("category").GetString(), out var cat)
                    && s.GetProperty("name").GetString() is { Length: > 0 } name)
                    map[name] = cat;
        }
        catch (Exception ex) { CoreLog.Error(ex); }   // malformed resource: seed still works
        return map;
    });

    private readonly Dictionary<string, SpellCategory> _learned =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _storePath;

    /// <summary>
    /// Loads previously learned categories from <paramref name="path"/> and saves after
    /// every new learning, so a charm spell taught once (via its "Master" tell) stays
    /// known across sessions — issue #29's report was re-learning the same spell every
    /// launch. Tests never attach a store, so learning stays session-local there.
    /// </summary>
    public void AttachStore(string path)
    {
        _storePath = path;
        try
        {
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, SpellCategory>>(
                File.ReadAllText(path));
            if (stored is null) return;
            foreach (var (name, category) in stored)
                if (category != SpellCategory.Unknown && name.Length > 0 && !Seed.ContainsKey(name))
                    _learned.TryAdd(name, category);
        }
        catch
        {
            // A corrupt store must never take the tracker down; the next Learn rewrites it.
        }
    }

    private void SaveStore()
    {
        if (_storePath is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_learned));
        }
        catch
        {
            // Persistence is best-effort; in-memory learning still works for the session.
        }
    }

    /// <summary>Strips a trailing roman-numeral rank so "Stinging Swarm V" and
    /// "Stinging Swarm" are the same spell.</summary>
    public static string BaseName(string spell)
    {
        spell = spell.Trim();
        var stripped = RankSuffixRx().Replace(spell, "");
        // Never strip away the whole name (a spell literally called "V" isn't a rank).
        return stripped.Length > 0 ? stripped : spell;
    }

    public SpellCategory Classify(string spell)
    {
        var name = BaseName(spell);
        if (Seed.TryGetValue(name, out var seeded)) return seeded;
        if (WikiCc.Value.TryGetValue(name, out var wiki)) return wiki;
        if (_learned.TryGetValue(name, out var learned)) return learned;
        // Fall back to family matching, so an unlisted rank or a spell nobody typed into
        // the seed list still classifies.
        foreach (var (fragment, category) in Families)
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return category;
        return SpellCategory.Unknown;
    }

    /// <summary>
    /// Record what a spell was observed doing. The seed table always wins — observation
    /// can add spells but never reclassify a known one, so a stray damage line can't turn
    /// a charm into a nuke. Returns true when this taught us something new.
    /// </summary>
    public bool Learn(string spell, SpellCategory category)
    {
        if (category == SpellCategory.Unknown) return false;
        var name = BaseName(spell);
        if (name.Length == 0 || Seed.ContainsKey(name) || WikiCc.Value.ContainsKey(name)) return false;
        if (_learned.TryGetValue(name, out var existing) && existing == category) return false;
        _learned[name] = category;
        SaveStore();
        return true;
    }

    public static bool IsCrowdControl(SpellCategory category) =>
        category is SpellCategory.Charm or SpellCategory.Mesmerize or SpellCategory.Root
            or SpellCategory.Lull or SpellCategory.Stun;

    public bool IsCrowdControl(string spell) => IsCrowdControl(Classify(spell));

    /// <summary>Human-readable category name for UI and watch-rule labels.</summary>
    public static string Describe(SpellCategory category) => category switch
    {
        SpellCategory.Charm => "Charm",
        SpellCategory.Mesmerize => "Mez",
        SpellCategory.Root => "Root",
        SpellCategory.Lull => "Lull",
        SpellCategory.Stun => "Stun",
        SpellCategory.Heal => "Heal",
        SpellCategory.DirectDamage => "Direct damage",
        SpellCategory.DamageOverTime => "Damage over time",
        SpellCategory.HealOverTime => "HoT",
        _ => "Unknown",
    };
}
