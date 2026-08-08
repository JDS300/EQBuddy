using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>
/// Durable per-character AA ledger (aa-ledger.json in appdata). The in-session ledger
/// rebuilds from full-log replay, but log truncation erases purchase lines for good —
/// this store is what remembers "Combat Fury 3, bought July 30th" after the janitor has
/// emptied the log twice. Keyed per character (an ability rank belongs to Dranak, not to
/// the install); rank-max on both write and read, so replaying an old log can never
/// regress what a newer session recorded.
/// </summary>
public sealed class AaLedgerStore
{
    public sealed record Entry(int Rank, DateTime Time);

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, Entry>> _byCharacter;

    public AaLedgerStore(string path)
    {
        _path = path;
        _byCharacter = Load(path);
    }

    private static Dictionary<string, Dictionary<string, Entry>> Load(string path)
    {
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(
                    File.ReadAllText(path)) is { } stored)
                return new(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { CoreLog.Error(ex); }   // corrupt store: start over, don't crash
        return new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Record a purchase; persists only when it raises the known rank.</summary>
    public void Record(string characterKey, string ability, int rank, DateTime time)
    {
        if (characterKey.Length == 0) return;
        lock (_lock)
        {
            if (!_byCharacter.TryGetValue(characterKey, out var abilities))
                _byCharacter[characterKey] = abilities = new(StringComparer.OrdinalIgnoreCase);
            if (abilities.TryGetValue(ability, out var known) && known.Rank >= rank) return;
            abilities[ability] = new Entry(rank, time);
            Save();
        }
    }

    /// <summary>Everything the store knows for one character (empty when unknown).</summary>
    public Dictionary<string, Entry> For(string characterKey)
    {
        lock (_lock)
            return _byCharacter.TryGetValue(characterKey, out var abilities)
                ? new Dictionary<string, Entry>(abilities, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
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
