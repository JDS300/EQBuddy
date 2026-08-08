using System.IO;
using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>One running (or just-expired) spawn countdown.</summary>
public sealed record SpawnTimerState(
    string Server, string Zone, string Name,
    DateTime KilledAt, double? DurationSeconds)
{
    public DateTime? DueAt => DurationSeconds is { } d ? KilledAt.AddSeconds(d) : null;
    public bool IsDue(DateTime now) => DueAt is { } due && now >= due;
}

/// <summary>
/// Tracks when named mobs (or their placeholders) were seen killed and counts down to
/// their respawn (SPAWN-003). Fed the same parsed event stream as SessionStats, so
/// timestamps come from the log: a restart replays the log and re-derives running
/// countdowns exactly like delayed watch cues do. Timers longer than a log's lifetime
/// (raid targets; auto-emptied logs) survive via a persistence file instead.
///
/// Kill matching is zone-gated: names repeat across zones ("an ice giant"), and a kill
/// line names no zone, so the current zone comes from the "You have entered" lines the
/// same way the Travels card learns them. No zone seen yet means no automatic matching —
/// the ▶ button in the Spawns window is the fallback, not a guess.
///
/// Timers are per-server (freeport's Frenzy is not qeynos's), keyed server|zone|name.
/// A repeat kill restarts the clock; replaying the same kill is a no-op.
/// </summary>
public sealed class SpawnTimers
{
    private readonly SpawnCatalog _catalog;
    private readonly SpawnOverrides _overrides;
    private readonly string? _persistPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, SpawnTimerState> _timers =
        new(StringComparer.OrdinalIgnoreCase);

    private SpawnZone? _currentZone;

    public string Server { get; set; } = "";
    public SpawnZone? CurrentZone { get { lock (_lock) return _currentZone; } }

    public SpawnTimers(SpawnCatalog catalog, SpawnOverrides overrides, string? persistPath = null)
    {
        _catalog = catalog;
        _overrides = overrides;
        _persistPath = persistPath;
        LoadPersisted();
    }

    /// <summary>Fed alongside SessionStats.Apply from the watcher thread.</summary>
    public void Apply(GameEvent evt)
    {
        switch (evt)
        {
            case ZoneEvent z:
                lock (_lock) _currentZone = _catalog.FindZone(z.Zone);
                break;
            case KillEvent k:
                OnKill(k);
                break;
        }
    }

    private void OnKill(KillEvent k)
    {
        lock (_lock)
        {
            if (_currentZone is not { } zone) return;

            // Two passes: every exact candidate before any fuzzy one, so a typo'd
            // catalog entry can never steal a kill from a correctly-spelled neighbour.
            foreach (var fuzzy in (bool[])[false, true])
            {
                foreach (var entry in zone.Named)
                {
                    var o = _overrides.Find(zone.Zone, entry.Name);
                    var placeholder = o?.Placeholder ?? entry.Placeholder;
                    if (!Matches(entry.Name, k.Target, fuzzy)
                        && !Matches(placeholder, k.Target, fuzzy)
                        && !entry.Aliases.Any(a => Matches(a, k.Target, fuzzy))) continue;

                    var trusted = IsTrusted(zone, entry);
                    // Self-heal: a LEARNED override sitting under a measured clock came
                    // from multi-spawn re-kill noise (two taskmasters at different camps
                    // look like one fast respawn) — drop it, the measurement wins.
                    if (trusted && o is { Learned: true, RespawnSeconds: { } bad }
                        && bad < SpawnCatalog.EffectiveSeconds(zone, entry))
                    {
                        o.RespawnSeconds = null;
                        o.Learned = false;
                        _overrides.Save();
                        o = _overrides.Find(zone.Zone, entry.Name);
                    }
                    var duration = o?.RespawnSeconds ?? SpawnCatalog.EffectiveSeconds(zone, entry);
                    if (!trusted)
                        duration = LearnFromRekill(zone.Zone, entry.Name, k.Time, duration);
                    Upsert(new SpawnTimerState(Server, zone.Zone, entry.Name, k.Time, duration));
                    return;
                }

                foreach (var (name, o) in _overrides.CustomFor(zone.Zone))
                {
                    if (!Matches(name, k.Target, fuzzy)
                        && !Matches(o.Placeholder ?? "", k.Target, fuzzy)) continue;
                    Upsert(new SpawnTimerState(Server, zone.Zone, name, k.Time, o.RespawnSeconds));
                    return;
                }
            }
        }

        static bool Matches(string catalogName, string killed, bool fuzzy) =>
            fuzzy ? SpawnCatalog.NameMatchesFuzzy(catalogName, killed)
                  : SpawnCatalog.NameMatches(catalogName, killed);
    }

    /// <summary>A MEASURED timer (entry or zone clock) outranks re-kill learning:
    /// shorter gaps against a measurement are multi-spawn noise, not evidence
    /// (David's rule, 2026-08-04). Player-typed edits still outrank everything —
    /// they're checked before this ever matters.</summary>
    private static bool IsTrusted(SpawnZone zone, SpawnEntry entry) =>
        entry.RespawnSeconds is not null ? entry.Trusted : zone.NamedDefaultTrusted;

    /// <summary>Re-kill gaps shorter than the learning floor are treated as multi-spawn
    /// noise (several mobs sharing a name), not as evidence of a faster respawn.</summary>
    public const double MinLearnSeconds = 90;

    /// <summary>
    /// Timers tighten themselves from play (requested by David after a Splitpaw player
    /// reported 22-minute catalog timers against 2–5-minute Legends reality): killing
    /// the same named again SOONER than its timer says is possible proves the respawn
    /// is at most that gap, so the gap becomes a learned override. Manual edits are
    /// never touched, learning never loosens, and learned values keep tightening as
    /// better evidence arrives.
    /// </summary>
    private double? LearnFromRekill(string zone, string name, DateTime killedAt, double? currentDuration)
    {
        if (currentDuration is not { } d) return currentDuration;
        if (!_timers.TryGetValue(Key(Server, zone, name), out var prev)) return currentDuration;
        var gap = (killedAt - prev.KilledAt).TotalSeconds;
        if (gap < MinLearnSeconds || gap >= d) return currentDuration;

        var o = _overrides.GetOrAdd(zone, name);
        if (o.RespawnSeconds is not null && !o.Learned) return currentDuration; // manual edit wins
        o.RespawnSeconds = Math.Floor(gap);
        o.Learned = true;
        _overrides.Save();
        return o.RespawnSeconds;
    }

    /// <summary>The ▶ button: the player saw (or heard about) the kill themselves.
    /// <paramref name="elapsed"/> covers "it died five minutes ago".</summary>
    public void StartManual(string zone, string name, double? durationSeconds, TimeSpan elapsed = default)
    {
        lock (_lock)
            Upsert(new SpawnTimerState(Server, zone, name, DateTime.Now - elapsed, durationSeconds));
    }

    /// <summary>Re-derives the countdown after a duration edit, from the original kill.</summary>
    public void SetDuration(string zone, string name, double? durationSeconds)
    {
        lock (_lock)
        {
            if (_timers.TryGetValue(Key(Server, zone, name), out var t))
                Upsert(t with { DurationSeconds = durationSeconds });
        }
    }

    public void Clear(string zone, string name)
    {
        lock (_lock)
        {
            if (_timers.Remove(Key(Server, zone, name))) SavePersisted();
        }
    }

    /// <summary>How long a timer stays visible after coming due. One minute (David's
    /// call): long enough to see DUE and react, short enough that a camp you walked
    /// away from cleans up after itself instead of nagging.</summary>
    public static readonly TimeSpan DueLinger = TimeSpan.FromSeconds(60);

    /// <summary>Current timers for this server, expired ones pruned. A due timer shows
    /// DUE for <see cref="DueLinger"/>, then drops on its own — if nobody clicked it
    /// away within a minute, they've moved on.</summary>
    public List<SpawnTimerState> Snapshot(DateTime now)
    {
        lock (_lock)
        {
            var stale = _timers.Values.Where(t => IsStale(t, now)).ToList();
            if (stale.Count > 0)
            {
                foreach (var t in stale) _timers.Remove(Key(t.Server, t.Zone, t.Name));
                SavePersisted();
            }
            return _timers.Values
                .Where(t => string.Equals(t.Server, Server, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.DueAt ?? DateTime.MaxValue)
                .ToList();
        }
    }

    private static bool IsStale(SpawnTimerState t, DateTime now)
    {
        if (t.DueAt is not { } due)
            // No duration known: the row only says "killed N ago" — keep it a day.
            return now - t.KilledAt > TimeSpan.FromHours(24);
        return now - due > DueLinger;
    }

    private static string Key(string server, string zone, string name) => $"{server}|{zone}|{name}";

    private void Upsert(SpawnTimerState t)
    {
        var key = Key(t.Server, t.Zone, t.Name);
        // Replays hand us the same kill again — identical state must not thrash the
        // persistence file. An OLDER kill never overwrites a newer one (a truncated log
        // replayed after a manual start, for example).
        if (_timers.TryGetValue(key, out var existing))
        {
            if (existing == t) return;
            if (t.KilledAt < existing.KilledAt) return;
        }
        _timers[key] = t;
        SavePersisted();
    }

    // -- persistence: for timers that outlive the log (raid targets, auto-emptied logs) --

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private void LoadPersisted()
    {
        if (_persistPath is null || !File.Exists(_persistPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<SpawnTimerState>>(
                File.ReadAllText(_persistPath), JsonOpts);
            if (list is null) return;
            foreach (var t in list)
                _timers[Key(t.Server, t.Zone, t.Name)] = t;
        }
        catch { /* corrupt file loses timers, not the feature */ }
    }

    private void SavePersisted()
    {
        if (_persistPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
            File.WriteAllText(_persistPath, JsonSerializer.Serialize(_timers.Values.ToList(), JsonOpts));
        }
        catch { /* read-only disk: timers just won't survive a restart */ }
    }
}
