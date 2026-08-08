using System.Reflection;
using System.Text.Json;

namespace EQBuddy.Core;

public sealed class QuestItemNeed
{
    public string Name { get; set; } = "";
    public int Qty { get; set; } = 1;
}

/// <summary>One quest from the shipped catalog (harvested from eqlwiki Category:Quests;
/// scripts/harvests/eqlwiki/quests-harvest.py).</summary>
public sealed class QuestEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string StartZone { get; set; } = "";
    public string QuestGiver { get; set; } = "";
    public int MinLevel { get; set; }
    /// <summary>Free-text class restriction as the wiki writes it ("ALL except NEC WIZ
    /// MAG ENC", "Paladin", …). Displayed, not parsed — the wiki's phrasing varies too
    /// much to gate on it silently.</summary>
    public string Classes { get; set; } = "";
    /// <summary>Turn-in items with the largest quantity any step asks for.</summary>
    public List<QuestItemNeed> Items { get; set; } = [];
    public List<string> Rewards { get; set; } = [];
    /// <summary>Zones the quest touches: start zone + Related Zones row + the page's
    /// zone categories — the "what can I work on here" signal.</summary>
    public List<string> Zones { get; set; } = [];
    /// <summary>Wiki-confirmed repeatable (Category:Repeatable Turn-in Quests). A
    /// completed repeatable stays live and shows how many turn-ins you can afford.</summary>
    public bool Repeatable { get; set; }

    /// <summary>Does the quest touch this zone? Names drift between the log, the wiki,
    /// and the atlas ("The Greater Faydark" / "Greater Faydark"), so match on the
    /// article-stripped form, containment either way as backstop.</summary>
    public bool TouchesZone(string zoneName)
    {
        var z = Strip(zoneName);
        if (z.Length == 0) return false;
        return Zones.Concat([StartZone]).Any(candidate =>
        {
            var c = Strip(candidate);
            return c.Length > 0 &&
                   (c.Equals(z, StringComparison.OrdinalIgnoreCase)
                    || c.Contains(z, StringComparison.OrdinalIgnoreCase)
                    || z.Contains(c, StringComparison.OrdinalIgnoreCase));
        });

        static string Strip(string s)
        {
            var t = s.Trim();
            return t.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? t[4..].Trim() : t;
        }
    }
}

/// <summary>Class-restriction matching over the wiki's free-text Classes field
/// ("ALL except NEC WIZ MAG ENC", "Cleric", "Bard, Cleric, Druid…").</summary>
public static class QuestClassFilter
{
    public static readonly string[] Classes =
        ["Bard", "Cleric", "Druid", "Enchanter", "Magician", "Monk", "Necromancer",
         "Paladin", "Ranger", "Rogue", "Shadow Knight", "Shaman", "Warrior", "Wizard"];

    private static readonly Dictionary<string, string[]> Abbrevs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bard"] = ["BRD"], ["Cleric"] = ["CLR"], ["Druid"] = ["DRU"],
        ["Enchanter"] = ["ENC"], ["Magician"] = ["MAG"], ["Monk"] = ["MNK"],
        ["Necromancer"] = ["NEC"], ["Paladin"] = ["PAL"], ["Ranger"] = ["RNG"],
        ["Rogue"] = ["ROG"], ["Shadow Knight"] = ["SHD", "SK"], ["Shaman"] = ["SHM"],
        ["Warrior"] = ["WAR"], ["Wizard"] = ["WIZ"],
    };

    /// <summary>Can this class do a quest with this restriction text? Unknown or
    /// unrestricted text says yes — a filter should hide only what it's sure about.</summary>
    public static bool Matches(string classText, string className)
    {
        var text = classText.Trim();
        if (text.Length == 0 || className.Length == 0) return true;
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase)
            || text.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;

        var exceptAt = text.IndexOf("except", StringComparison.OrdinalIgnoreCase);
        if (exceptAt >= 0)
            return !Mentions(text[(exceptAt + "except".Length)..], className);

        // No class token at all ("see walkthrough") reads as unrestricted.
        var mentionsAny = Classes.Any(c => Mentions(text, c));
        return !mentionsAny || Mentions(text, className);
    }

    private static bool Mentions(string text, string className)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(text,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(className)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        return Abbrevs.TryGetValue(className, out var abbrevs) && abbrevs.Any(a =>
            System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{a}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }
}

/// <summary>
/// The shipped quest knowledge: 201 wiki quests with their turn-in items, plus the
/// wiki's Category:Quest Items membership — the broader "part of a quest, don't vendor
/// it blind" marker that also covers rewards and multi-step intermediates.
/// </summary>
public sealed class QuestCatalog
{
    public List<QuestEntry> Quests { get; set; } = [];
    public List<string> QuestItems { get; set; } = [];

    private HashSet<string>? _questItemSet;
    private Dictionary<string, List<QuestEntry>>? _byItem;

    /// <summary>Item name → quests needing it as a turn-in. Case-insensitive; built lazily.</summary>
    public Dictionary<string, List<QuestEntry>> ByItem()
    {
        if (_byItem is { } cached) return cached;
        var map = new Dictionary<string, List<QuestEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in Quests)
            foreach (var i in q.Items)
            {
                if (!map.TryGetValue(i.Name, out var list)) map[i.Name] = list = [];
                list.Add(q);
            }
        return _byItem = map;
    }

    /// <summary>True when the item is quest-relevant: a turn-in for a known quest OR a
    /// member of the wiki's Quest Items category (4,148 items) — the broad marker.</summary>
    public bool IsQuestItem(string itemName)
    {
        _questItemSet ??= new HashSet<string>(
            QuestItems.Concat(Quests.SelectMany(q => q.Items.Select(i => i.Name))),
            StringComparer.OrdinalIgnoreCase);
        return _questItemSet.Contains(itemName.Trim());
    }

    /// <summary>True when a PARSED quest wants this item as a turn-in — the Loot-view
    /// tint/badge signal. Deliberately narrower than <see cref="IsQuestItem"/>: the raw
    /// category is 4k items wide and painted half the loot green (David, 2026-08-07);
    /// green now always leads to an actual quest card.</summary>
    public bool IsTurnInItem(string itemName) => ByItem().ContainsKey(itemName.Trim());

    /// <summary>The quests wanting this item as a turn-in (empty when none).</summary>
    public IReadOnlyList<QuestEntry> QuestsWanting(string itemName) =>
        ByItem().TryGetValue(itemName.Trim(), out var quests) ? quests : [];

    public static QuestCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQBuddy.Core.Data.QuestCatalog.json");
        if (stream is null) return new QuestCatalog();
        try
        {
            return JsonSerializer.Deserialize<QuestCatalog>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new QuestCatalog();
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
            return new QuestCatalog();
        }
    }
}
