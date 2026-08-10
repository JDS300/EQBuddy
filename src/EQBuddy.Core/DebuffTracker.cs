namespace EQBuddy.Core;

/// <summary>One damage-over-time effect currently ticking on one target.</summary>
public sealed record DebuffState(
    string Target,
    string Spell,
    string Caster,
    bool IsMine,
    DateTime LandedAt,
    DateTime LastTickAt,
    DateTime? ExpiresAt)
{
    /// <summary>Null when this spell's duration has never been measured. A null countdown is
    /// the honest answer: the alternative is a number invented at the exact moment the user
    /// is deciding whether to recast.</summary>
    public double? RemainingSeconds(DateTime now) =>
        ExpiresAt is { } e ? Math.Max(0, (e - now).TotalSeconds) : null;

    /// <summary>Inside the refresh window - amber on the chip. Unknown duration is never
    /// "about to drop": we have no idea, and pretending otherwise trains the user to ignore
    /// the colour.</summary>
    public bool IsAboutToDrop(DateTime now, double warnSeconds) =>
        RemainingSeconds(now) is { } remaining && remaining <= warnSeconds;
}

/// <summary>
/// Your own DoTs, timed so they can be refreshed before they fall off.
///
/// The log never states a duration, but it does not have to. Ticks arrive every ~6 seconds
/// naming the spell and target, so a completed cast measures itself: first tick to last tick,
/// plus the tick that was already paid for. That measurement drives the NEXT cast of the same
/// spell, which is why the first cast of anything shows no countdown and every one after it
/// does.
///
/// Third-party DoTs are deliberately ignored (<see cref="ThirdDotEvent"/>). They were not
/// wanted, and in a real group log they are the overwhelming majority of tick lines.
/// </summary>
public sealed class DebuffTracker
{
    /// <summary>Two missed ticks. One can be lost to a resist or a partial log flush; two
    /// means the effect is gone.</summary>
    public static readonly TimeSpan TickGrace = TimeSpan.FromSeconds(12);

    /// <summary>A DoT ticks on the six-second server heartbeat, and the first tick lands one
    /// heartbeat after the cast, so a cast's length is (last - first) + one tick.</summary>
    public const double ServerTickSeconds = 6;

    /// <summary>Measurements kept per spell so a single odd cast cannot become the duration
    /// for good. Capped: a long session would otherwise grow this without bound, and the
    /// oldest samples say nothing the newest do not.</summary>
    public static readonly int SampleCap = 16;

    private readonly Dictionary<(string Target, string Spell), DebuffState> _active = [];
    private readonly Dictionary<string, List<double>> _samples = [];
    private readonly HashSet<string> _died = [];
    private readonly HashSet<string> _recastPending = [];

    /// <summary>Lead time, in seconds, at which an effect counts as about to drop.</summary>
    public double WarnSeconds { get; set; } = 10;

    /// <summary>Measured durations by spell name, in seconds: the most repeated sample,
    /// ties broken toward the shorter. A refresh or a truncated observation can only ever
    /// mis-measure in one direction each, and warning early is safer than warning late.</summary>
    public IReadOnlyDictionary<string, double> LearnedDurations =>
        _samples.ToDictionary(kv => kv.Key, kv => Consensus(kv.Value));

    private static double Consensus(List<double> samples) => samples
        .GroupBy(v => v)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key)
        .First().Key;

    public void Apply(GameEvent evt)
    {
        switch (evt)
        {
            // Yours by construction: the "damage from your X" line shape. Third-party ticks
            // arrive as ThirdDotEvent and are not handled here at all.
            case DamageDealtEvent { OverTime: true } tick:
                OnTick(tick);
                break;
            // A corpse stops ticking for reasons that have nothing to do with the spell.
            case KillEvent kill:
                _died.Add(kill.Target);
                break;
            // Nothing in the tick lines marks a recast - the ticks simply continue - so the
            // cast line is the only evidence that the clock restarted.
            case SpellCastEvent cast:
                _recastPending.Add(cast.Spell);
                break;
        }
    }

    private void OnTick(DamageDealtEvent tick)
    {
        var key = (tick.Target, tick.Source);
        if (_active.TryGetValue(key, out var existing))
        {
            if (_recastPending.Remove(tick.Source))
            {
                // A refresh restarts the clock. The interrupted first cast is NOT recorded:
                // it was cut short by the recast, so it measures the gap between two casts
                // rather than the spell's duration - the same reason a kill teaches nothing.
                // Without the restart the two casts read as one long effect, which is how the
                // real log taught Immolate 115s against 54-60s for every sibling druid DoT.
                _active[key] = existing with
                {
                    LandedAt = tick.Time,
                    LastTickAt = tick.Time,
                    ExpiresAt = Expiry(tick.Source, tick.Time),
                };
                return;
            }
            _active[key] = existing with { LastTickAt = tick.Time };
            return;
        }

        _recastPending.Remove(tick.Source);   // that cast explains THIS landing, not a refresh

        _died.Remove(tick.Target);   // a fresh cast on a name that died earlier
        _active[key] = new DebuffState(
            tick.Target, tick.Source, Caster: "", IsMine: true,
            LandedAt: tick.Time, LastTickAt: tick.Time,
            ExpiresAt: Expiry(tick.Source, tick.Time));
    }

    /// <summary>What is ticking now. Also the point at which effects whose ticks have stopped
    /// are retired - and, when they ended on their own rather than with the mob, measured.</summary>
    public IReadOnlyList<DebuffState> Active(DateTime now)
    {
        foreach (var (key, state) in _active.ToList())
        {
            if (now - state.LastTickAt <= TickGrace) continue;
            _active.Remove(key);
            Learn(state);
        }
        return _active.Values
            .OrderBy(s => s.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Spell, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Records a completed cast's length, unless the target died - a kill truncates
    /// the ticks, and learning from it would teach a duration shorter than the spell's, so
    /// every later cast would warn early and look like the spell had been nerfed.</summary>
    private void Learn(DebuffState state)
    {
        if (_died.Contains(state.Target)) return;
        Record(state);
    }

    private void Record(DebuffState state)
    {
        if (state.LastTickAt <= state.LandedAt) return;   // a single tick measures nothing

        var measured = (state.LastTickAt - state.LandedAt).TotalSeconds + ServerTickSeconds;
        var samples = _samples.TryGetValue(state.Spell, out var existing) ? existing : [];
        samples.Add(measured);
        if (samples.Count > SampleCap) samples.RemoveAt(0);
        _samples[state.Spell] = samples;
    }

    private DateTime? Expiry(string spell, DateTime from) =>
        _samples.TryGetValue(spell, out var samples) && samples.Count > 0
            ? from.AddSeconds(Consensus(samples))
            : null;
}
