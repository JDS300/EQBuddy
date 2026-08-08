using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>
/// Durable per-character ledger of quest-relevant items (quest-ledger.json in appdata),
/// same shape of promise as <see cref="AaLedgerStore"/>: the session rebuilds from log
/// replay, but the janitor truncates logs — this store is what still knows you looted
/// four Bone Chips in July. Two buckets per item:
///
///   Looted — accumulated from the log, replay-safe via a per-item time high-water mark
///   (a record only lands when its log timestamp is strictly newer than the last one
///   accepted, so the full-log replay every launch re-offers the same events and they
///   all bounce). Cost: a second identical loot in the same one-second log stamp is
///   dropped — rare, and strictly better than doubling on every restart.
///
///   Manual — "I already had this before EQBuddy" (David's spec, 2026-08-07): the user
///   types a count in the Quest Tracker; set to zero to forget it.
///
/// Only items the filter admits are stored (the UI wires the quest catalog's
/// IsQuestItem), so the file stays quest-sized instead of hoarding every rat whisker.
/// </summary>
public sealed class QuestLedgerStore
{
    public sealed class Entry
    {
        public int Looted { get; set; }
        public int Manual { get; set; }
        public DateTime LastTime { get; set; }
        public int Total => Looted + Manual;
    }

    /// <summary>One character's slice: owned items plus the quests they chose to 📌-track
    /// (tracked quests show in the Quest Tracker even before any item overlaps).</summary>
    public sealed class CharacterLedger
    {
        public Dictionary<string, Entry> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Tracked { get; set; } = [];
        /// <summary>Quests dismissed as "not interested" — excluded from the overlap
        /// view, and items only THEY want stop tinting green in the Loot views.</summary>
        public List<string> Hidden { get; set; } = [];
        /// <summary>Quest name → how many times it's been marked completed.</summary>
        public Dictionary<string, int> Completed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, CharacterLedger> _byCharacter;

    /// <summary>Admits items into the ledger; default admits nothing until the UI wires
    /// the catalog in — a ledger that can't identify quest items shouldn't guess.</summary>
    public Func<string, bool> TrackFilter { get; set; } = _ => false;

    public QuestLedgerStore(string path)
    {
        _path = path;
        _byCharacter = Load(path);
    }

    private static Dictionary<string, CharacterLedger> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var text = File.ReadAllText(path);
            if (JsonSerializer.Deserialize<Dictionary<string, CharacterLedger>>(text) is { } stored)
            {
                // The pre-tracking shape (char → item → entry) parses into this type
                // WITHOUT error — unknown item-name properties are silently ignored,
                // leaving every character empty. Empty-but-nonempty-file means old shape:
                // reparse it and carry the items over (no tracked quests existed yet).
                if (stored.Count > 0
                    && stored.Values.All(c => c.Items.Count == 0 && c.Tracked.Count == 0
                                              && c.Hidden.Count == 0 && c.Completed.Count == 0))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(text)
                            is { } old && old.Values.Any(items => items.Count > 0))
                            return Rekey(old.ToDictionary(
                                kv => kv.Key, kv => new CharacterLedger { Items = kv.Value }));
                    }
                    catch (JsonException) { /* genuinely-empty new-shape file */ }
                }
                return Rekey(stored);
            }
        }
        catch (Exception ex) { CoreLog.Error(ex); }   // corrupt store: start over, don't crash
        return new(StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, CharacterLedger> Rekey(Dictionary<string, CharacterLedger> stored) =>
            new(stored.ToDictionary(
                    kv => kv.Key,
                    kv => new CharacterLedger
                    {
                        Items = new Dictionary<string, Entry>(kv.Value.Items, StringComparer.OrdinalIgnoreCase),
                        Tracked = kv.Value.Tracked,
                        Hidden = kv.Value.Hidden,
                        Completed = new Dictionary<string, int>(kv.Value.Completed, StringComparer.OrdinalIgnoreCase),
                    }),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Offer a loot event. Ignored unless the filter admits the item and the
    /// timestamp beats the item's high-water mark (see class remarks).</summary>
    public void RecordLoot(string characterKey, string item, int count, DateTime time)
    {
        if (characterKey.Length == 0 || count <= 0 || !TrackFilter(item)) return;
        lock (_lock)
        {
            var entry = EntryFor(characterKey, item);
            if (time <= entry.LastTime) return;
            entry.Looted += count;
            entry.LastTime = time;
            Save();
        }
    }

    /// <summary>Set the manual adjustment: positive = "already had these before EQBuddy",
    /// negative = a hand-in offset against the looted history (clamped so Total never
    /// goes below zero — you can't owe the ledger items). Zero removes an entry with no
    /// looted history. Manual entries bypass the filter — the user typing a name is its
    /// own statement of relevance.</summary>
    public void SetManual(string characterKey, string item, int count)
    {
        if (characterKey.Length == 0 || item.Trim().Length == 0) return;
        lock (_lock)
        {
            var entry = EntryFor(characterKey, item.Trim());
            entry.Manual = Math.Max(count, -entry.Looted);
            if (entry is { Manual: 0, Looted: 0 })
                _byCharacter[characterKey].Items.Remove(item.Trim());
            Save();
        }
    }

    /// <summary>Item → owned counts for one character (copy; empty when unknown).</summary>
    public Dictionary<string, Entry> For(string characterKey)
    {
        lock (_lock)
            return _byCharacter.TryGetValue(characterKey, out var c)
                ? c.Items.ToDictionary(kv => kv.Key,
                    kv => new Entry { Looted = kv.Value.Looted, Manual = kv.Value.Manual, LastTime = kv.Value.LastTime },
                    StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Quests this character 📌-tracks (copy; empty when unknown).</summary>
    public HashSet<string> TrackedFor(string characterKey)
    {
        lock (_lock)
            return _byCharacter.TryGetValue(characterKey, out var c)
                ? new HashSet<string>(c.Tracked, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
    }

    public void SetTracked(string characterKey, string questName, bool tracked)
        => SetMembership(characterKey, questName, tracked, c => c.Tracked,
            removeFrom: c => c.Hidden);   // pinning a quest un-hides it — they contradict

    /// <summary>Quests this character dismissed (copy; empty when unknown).</summary>
    public HashSet<string> HiddenFor(string characterKey)
    {
        lock (_lock)
            return _byCharacter.TryGetValue(characterKey, out var c)
                ? new HashSet<string>(c.Hidden, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
    }

    public void SetHidden(string characterKey, string questName, bool hidden)
        => SetMembership(characterKey, questName, hidden, c => c.Hidden,
            removeFrom: c => c.Tracked);  // hiding a quest un-pins it

    private void SetMembership(string characterKey, string questName, bool member,
        Func<CharacterLedger, List<string>> list, Func<CharacterLedger, List<string>> removeFrom)
    {
        if (characterKey.Length == 0 || questName.Length == 0) return;
        lock (_lock)
        {
            var c = CharacterFor(characterKey);
            var target = list(c);
            var has = target.Contains(questName, StringComparer.OrdinalIgnoreCase);
            if (member == has) return;
            if (member)
            {
                target.Add(questName);
                removeFrom(c).RemoveAll(q => q.Equals(questName, StringComparison.OrdinalIgnoreCase));
            }
            else target.RemoveAll(q => q.Equals(questName, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    /// <summary>Quest → completion count for one character (copy; empty when unknown).</summary>
    public Dictionary<string, int> CompletedFor(string characterKey)
    {
        lock (_lock)
            return _byCharacter.TryGetValue(characterKey, out var c)
                ? new Dictionary<string, int>(c.Completed, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Mark one completion: consumes one set of turn-ins (each item's manual
    /// offset drops by its quantity, clamped so Total never goes negative) and bumps the
    /// quest's completed count. The hand-in and the bookkeeping are one gesture — the
    /// log never records turn-ins, so this button is the log (David, 2026-08-07).</summary>
    public void RecordCompletion(string characterKey, string questName,
        IEnumerable<QuestItemNeed> consume)
    {
        if (characterKey.Length == 0 || questName.Length == 0) return;
        lock (_lock)
        {
            var c = CharacterFor(characterKey);
            foreach (var item in consume)
            {
                var entry = EntryFor(characterKey, item.Name);
                entry.Manual = Math.Max(entry.Manual - item.Qty, -entry.Looted);
            }
            c.Completed[questName] = c.Completed.TryGetValue(questName, out var n) ? n + 1 : 1;
            Save();
        }
    }

    private CharacterLedger CharacterFor(string characterKey)
    {
        if (!_byCharacter.TryGetValue(characterKey, out var c))
            _byCharacter[characterKey] = c = new CharacterLedger();
        return c;
    }

    private Entry EntryFor(string characterKey, string item)
    {
        var c = CharacterFor(characterKey);
        if (!c.Items.TryGetValue(item, out var entry))
            c.Items[item] = entry = new Entry();
        return entry;
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_byCharacter, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { CoreLog.Error(ex); }
    }
}
