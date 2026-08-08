using System.Reflection;
using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>
/// The world map as a graph: zone → adjacent zones, harvested from eqlwiki zone pages
/// (scripts/harvests/eqlwiki/zones-harvest.py, edges mirrored). Answers "how far is that
/// quest's turn-in from where I'm standing" as a BFS hop count with the path — an honest
/// walking distance; boats and teleports count as the single hop the wiki lists them as.
/// </summary>
public sealed class ZoneGraph
{
    private readonly Dictionary<string, List<string>> _adjacent = new(StringComparer.OrdinalIgnoreCase);

    public int ZoneCount => _adjacent.Count;

    public ZoneGraph() { }

    public ZoneGraph(Dictionary<string, List<string>> adjacency)
    {
        foreach (var (zone, adj) in adjacency) _adjacent[zone] = adj;
    }

    /// <summary>Map a log/quest zone name onto a graph node: exact first, then
    /// containment either way ("The Estate of Unrest" ⇄ "Estate of Unrest"), longest
    /// containment match wins so "Faydark" can't grab Greater over Lesser arbitrarily
    /// when an exact name exists.</summary>
    public string? Resolve(string zoneName)
    {
        var name = zoneName.Trim();
        if (name.Length == 0) return null;
        if (_adjacent.ContainsKey(name))
            return _adjacent.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return _adjacent.Keys
            .Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => Math.Min(k.Length, name.Length))
            .FirstOrDefault();
    }

    /// <summary>Hop count and path between two zones, or null when either is unknown or
    /// unreachable. 0 hops = same zone.</summary>
    public (int Hops, IReadOnlyList<string> Path)? Distance(string from, string to)
    {
        if (Resolve(from) is not { } start || Resolve(to) is not { } goal) return null;
        if (start.Equals(goal, StringComparison.OrdinalIgnoreCase)) return (0, [start]);

        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [start] = start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var zone = queue.Dequeue();
            foreach (var next in _adjacent.TryGetValue(zone, out var adj) ? adj : [])
            {
                if (previous.ContainsKey(next)) continue;
                previous[next] = zone;
                if (next.Equals(goal, StringComparison.OrdinalIgnoreCase))
                {
                    var path = new List<string> { next };
                    for (var at = zone; !at.Equals(start, StringComparison.OrdinalIgnoreCase); at = previous[at])
                        path.Add(at);
                    path.Add(start);
                    path.Reverse();
                    return (path.Count - 1, path);
                }
                queue.Enqueue(next);
            }
        }
        return null;
    }

    public static ZoneGraph LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQBuddy.Core.Data.ZoneGraph.json");
        if (stream is null) return new ZoneGraph();
        try
        {
            return new ZoneGraph(
                JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stream) ?? []);
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
            return new ZoneGraph();
        }
    }
}
