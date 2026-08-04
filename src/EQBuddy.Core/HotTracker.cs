using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>One of your heal-over-time spells currently ticking on a target.</summary>
public sealed record HotState(
    string Target, string Spell, DateTime FirstTick, DateTime LastTick, DateTime ExpiresAt)
{
    /// <summary>Seconds until the HoT stops healing (0 once it has).</summary>
    public double RemainingSeconds(DateTime now) =>
        Math.Max(0, (ExpiresAt - now).TotalSeconds);
}

/// <summary>
/// Tracks YOUR active heal-over-time spells — one countdown per (target, spell), so the
/// chip stack answers "how long until this HoT stops ticking".
///
/// Unlike a mez, a HoT announces neither its landing nor its fade: there is not one
/// "Your Blossoming Heal spell has worn off" line in 690k lines of eqlog_Daggo_freeport,
/// while Mesmerization fades 1197 times in the same file. So the TICK is the evidence —
/// the first one opens the chip, the rest sustain it — and the end comes from two places:
///
///   1. The companion burst, "You healed Daggo for 398 hit points by Blossoming Heal
///      Trigger." — an instant heal (no "over time") worth roughly 3x a tick, which lands
///      with the series' final tick. That is the authoritative end, and first-tick-to-
///      Trigger is a real measured duration, the direct analogue of a mez's land-to-fade
///      gap: it is learned per spell, longest observed wins, since an interrupted HoT
///      measures short and nothing measures it long.
///   2. Failing that — 128 of 712 measured runs have no Trigger at all, and zoning or a
///      truncated log can swallow one — the 5-tick / 6-second estimate expires the chip
///      on its own. Belt and braces: precise when the Trigger is there, close enough when
///      it is not.
///
/// Ticks name the spell WITHOUT its rank ("by Efflorescing Heal" for a cast logged as
/// "You begin casting Efflorescing Heal III."), so everything here keys on the unranked
/// name the ticks use; no cast line is consulted and no rank is reconstructed.
/// </summary>
public sealed class HotTracker
{
    private readonly Dictionary<string, HotState> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _learned = new(StringComparer.OrdinalIgnoreCase);
    private string? _storePath;
    private readonly object _lock = new();

    /// <summary>Raised when the set of active HoTs changes (not on every tick).</summary>
    public event Action? Changed;

    /// <summary>Measured in eqlog_Daggo_freeport (690k lines): of 2972 consecutive
    /// same-(spell,target) tick gaps, 2738 were exactly 6s, 154 were 7s and 80 were 5s —
    /// the jitter is log flushing, the cadence is 6s.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(6);

    /// <summary>Five ticks per cast, measured in the same log: 276/298 Blossoming runs
    /// were exactly 5, 217/234 Blooming, 80/94 Flowering, 64/71 Efflorescing, 15/15
    /// Sprouting. (The 10s and 15s are back-to-back recasts, not longer spells.) So a
    /// cast heals for (5-1)x6 = 24 seconds after its first tick — confirmed independently
    /// by the terminating Trigger burst, which lands 24s after the first tick in 531 of
    /// the 584 runs that have one (49 at 25s, 3 at 23s: the same flush jitter).</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(24);

    /// <summary>A tick this long after the previous one no longer belongs to the same
    /// cast. Two intervals plus jitter, not one: 167 HoT tick lines in the real log carry
    /// a "(Critical)" suffix, and LogParser's heal pattern anchors on the closing "." so
    /// it matches none of them — a live series can therefore show a 12s hole where a crit
    /// went unparsed. (Worth fixing in the parser; until then, tolerate it.)</summary>
    public static readonly TimeSpan MaxTickGap = TimeSpan.FromSeconds(13);

    /// <summary>How far past its expected end a series may still absorb a tick. One
    /// extra interval (plus jitter): it lets a HoT that really is longer than we think
    /// prove it — the Trigger then teaches the true duration — while a recast landing
    /// two intervals late is correctly read as a new cast.</summary>
    public static readonly TimeSpan SeriesOvershoot = TimeSpan.FromSeconds(7);

    /// <summary>Expired entries are kept (invisible) this long: the Trigger can arrive a
    /// beat after the last expected tick, and it is the only thing that can teach a
    /// duration longer than the estimate. Must exceed <see cref="SeriesOvershoot"/> so a
    /// still-running series is never pruned out from under its own next tick.</summary>
    public static readonly TimeSpan ExpiredRetention = TimeSpan.FromSeconds(30);

    /// <summary>The suffix EQ puts on the terminating burst heal's spell name.</summary>
    private const string TriggerSuffix = " Trigger";

    /// <summary>Loads learned durations and saves after each new maximum — same pattern
    /// as MezTracker's and SpellCatalog's stores; tests mostly don't attach one.</summary>
    public void AttachStore(string path)
    {
        _storePath = path;
        try
        {
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path));
            if (stored is null) return;
            lock (_lock)
                foreach (var (spell, seconds) in stored)
                    if (Plausible(seconds)) _learned.TryAdd(spell, seconds);
        }
        catch { /* corrupt store: rewritten on next learn */ }
    }

    /// <summary>Fourth consumer of the parsed event stream (after SessionStats,
    /// SpawnTimers and MezTracker): replay-safe because everything keys on log
    /// timestamps, so a startup ingest reconstructs the HoTs still running.</summary>
    public void Apply(GameEvent evt)
    {
        var changed = false;
        lock (_lock)
        {
            switch (evt)
            {
                // Your own HoT tick — "You healed Daggo over time for 61 hit points by
                // Blossoming Heal." Nothing else announces the spell landing, so the tick
                // both opens and sustains the chip.
                case HealEvent { Outgoing: true, OverTime: true } tick when tick.Spell.Length > 0:
                    changed = OnTick(tick);
                    break;
                // The terminating burst — same line shape as any instant heal, told apart
                // by the " Trigger" suffix on the spell name. It stays a normal HealEvent
                // for the healing stats; here it additionally ends the series.
                case HealEvent { Outgoing: true, OverTime: false } burst
                    when burst.Spell.EndsWith(TriggerSuffix, StringComparison.OrdinalIgnoreCase):
                    changed = OnTrigger(burst);
                    break;
                case ZoneEvent:
                    changed = _active.Count > 0;
                    _active.Clear();
                    break;
            }
            Prune(evt.Time);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Active HoTs at <paramref name="now"/>, soonest to stop first.</summary>
    public List<HotState> Snapshot(DateTime now)
    {
        lock (_lock)
            return _active.Values
                .Where(h => now < h.ExpiresAt)
                .OrderBy(h => h.ExpiresAt)
                .ToList();
    }

    /// <summary>Durations measured from Trigger lines (unranked spell name → seconds
    /// from first tick to end), for display/export. This is what the store holds.</summary>
    public IReadOnlyDictionary<string, double> LearnedDurations
    {
        get { lock (_lock) return new Dictionary<string, double>(_learned); }
    }

    /// <summary>The same knowledge counted in ticks, which is how a player thinks of a
    /// HoT ("five ticks"): a 24s duration is 5 ticks 6s apart, one at each end.</summary>
    public IReadOnlyDictionary<string, double> LearnedTicks
    {
        get
        {
            lock (_lock)
                return _learned.ToDictionary(
                    kv => kv.Key,
                    kv => Math.Round(kv.Value / TickInterval.TotalSeconds) + 1,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool OnTick(HealEvent tick)
    {
        var key = Key(tick.Target, tick.Spell);
        if (_active.TryGetValue(key, out var running) && Continues(running, tick.Time))
        {
            // Same cast still ticking: the countdown belongs to the FIRST tick, so
            // sustaining it changes nothing a chip displays — no Changed, or every tick
            // would repaint the whole stack.
            _active[key] = running with { LastTick = tick.Time };
            return false;
        }

        // ExpiresAt is the LAST expected tick, not the moment a sixth tick would have
        // arrived: the player's question is "when does the healing stop", and the real
        // log answers it exactly — the terminating Trigger burst lands with the fifth
        // tick, 24s after the first. Counting to a phantom 30s would leave the chip
        // claiming healing that has already finished.
        var entry = new HotState(Normalize(tick.Target), tick.Spell, tick.Time, tick.Time,
            tick.Time + DurationFor(tick.Spell));
        _active[key] = entry;
        return true;
    }

    private bool OnTrigger(HealEvent burst)
    {
        var spell = burst.Spell[..^TriggerSuffix.Length];
        if (!_active.Remove(Key(burst.Target, spell), out var entry)) return false;

        // A real measured duration, exactly like a mez's land-to-fade gap: keep the
        // longest seen per spell. An early end (the target died, you zoned, the HoT was
        // overwritten) measures short; nothing measures long.
        var observed = (burst.Time - entry.FirstTick).TotalSeconds;
        if (Plausible(observed)
            && (!_learned.TryGetValue(spell, out var known) || observed > known))
        {
            _learned[spell] = Math.Round(observed, 1);
            SaveStore();
        }
        // The chip vanishing is a visible change even when the estimate had already
        // expired it — the entry was still there to catch a late tick.
        return true;
    }

    /// <summary>A duration only counts if a HoT could plausibly have it: at least one
    /// tick gap, and at most 120s (twenty ticks — four times the longest of the five
    /// measured spells). The series bounds already stop a measurement running away
    /// across a recast chain; this is the backstop, and it also screens a store file
    /// that has been hand-edited into nonsense.</summary>
    private static bool Plausible(double seconds) => seconds is >= 3 and <= 120;

    /// <summary>Expected life of a cast of this spell: what a Trigger measured, else the
    /// five-tick estimate.</summary>
    private TimeSpan DurationFor(string spell) =>
        _learned.TryGetValue(spell, out var learned)
            ? TimeSpan.FromSeconds(learned)
            : DefaultDuration;

    /// <summary>Does this tick belong to the series already running? Two ways it does
    /// not: an implausible hole since the last tick, or a tick so far past the expected
    /// end that the spell must have been recast. Back-to-back recasting is common in the
    /// real log (it is what produced the 10- and 15-tick runs), and the tick stream alone
    /// cannot always tell it from one long cast — the Trigger that ends each cast is what
    /// really separates them; these bounds are the fallback for when it is missing.</summary>
    private static bool Continues(HotState running, DateTime tickTime) =>
        tickTime - running.LastTick <= MaxTickGap
        && tickTime <= running.ExpiresAt + SeriesOvershoot;

    private void Prune(DateTime now)
    {
        // Expired entries linger only so a trailing Trigger can still be matched and
        // learned from; after that they are dead weight, and the dictionary must not
        // accumulate one per target the healer ever touched.
        foreach (var (key, entry) in _active.ToList())
            if (now - entry.ExpiresAt > ExpiredRetention) _active.Remove(key);
    }

    private static string Key(string target, string spell) =>
        $"{Normalize(target)}\0{spell}";

    private static string Normalize(string target) => LogParser.Normalize(target);

    private void SaveStore()
    {
        if (_storePath is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_learned));
        }
        catch { /* best-effort; in-memory learning still works */ }
    }
}
