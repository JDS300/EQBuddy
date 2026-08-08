namespace EQBuddy.Core;

public sealed record QuestItemProgress(string Name, int Need, int Have);

public sealed record QuestMatch(QuestEntry Quest, int ItemsHave, int ItemsTotal,
    IReadOnlyList<QuestItemProgress> Items, bool Tracked = false)
{
    /// <summary>Distinct turn-in items with at least one copy owned, over distinct
    /// required — the "3/5 items" a card shows.</summary>
    public double Fraction => ItemsTotal == 0 ? 0 : (double)ItemsHave / ItemsTotal;
    public bool Complete => ItemsTotal > 0 && Items.All(i => i.Have >= i.Need);

    /// <summary>How many full turn-in sets the owned counts afford — "ready ×3" on a
    /// repeatable (min over items of have ÷ need).</summary>
    public int ReadyCount => ItemsTotal == 0 ? 0 : Items.Min(i => i.Have / Math.Max(1, i.Need));
}

/// <summary>
/// Pure overlap engine: which known quests want items this character owns (looted or
/// manually declared), and how far along each is. No class gating — the wiki's class
/// text is display-only; a bard farming paladin-quest items for a friend is a use, not
/// a bug (David's spec, 2026-08-07).
/// </summary>
public static class QuestMatcher
{
    /// <summary>Quests overlapping the owned set — plus every 📌-tracked quest even with
    /// zero overlap, since tracking is exactly the promise "keep this one in front of
    /// me". Tracked first, then most-complete, ties broken by fewer total requirements
    /// (a 2-item quest you're half done with outranks a 10-item epic you've grazed)
    /// then by name for stability.</summary>
    public static List<QuestMatch> Match(
        QuestCatalog catalog, IReadOnlyDictionary<string, QuestLedgerStore.Entry> owned,
        ISet<string>? tracked = null, ISet<string>? hidden = null)
    {
        var matches = new List<QuestMatch>();
        foreach (var quest in catalog.Quests)
        {
            if (quest.Items.Count == 0) continue;
            if (hidden?.Contains(quest.Name) == true) continue;   // dismissed: not interested
            var isTracked = tracked?.Contains(quest.Name) == true;
            var progress = quest.Items
                .Select(i => new QuestItemProgress(i.Name, i.Qty,
                    owned.TryGetValue(i.Name, out var e) ? e.Total : 0))
                .ToList();
            var have = progress.Count(p => p.Have > 0);
            if (have == 0 && !isTracked) continue;
            matches.Add(new QuestMatch(quest, have, progress.Count, progress, isTracked));
        }
        return matches
            .OrderByDescending(m => m.Tracked)
            .ThenByDescending(m => m.Fraction)
            .ThenBy(m => m.ItemsTotal)
            .ThenBy(m => m.Quest.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
