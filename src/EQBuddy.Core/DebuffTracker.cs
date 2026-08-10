namespace EQBuddy.Core;

/// <summary>One damage-over-time effect currently ticking on one target.</summary>
public sealed record DebuffState(
    string Target,
    string Spell,
    string Caster,
    bool IsMine,
    DateTime LandedAt,
    DateTime LastTickAt,
    DateTime? ExpiresAt,
    /// <summary>True for DoTs, which announce themselves every six seconds. A slow announces
    /// itself once and then says nothing until it fades, so silence means nothing for it.</summary>
    bool Ticks = false)
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

    /// <summary>How long after a cast a landing can still be attributed to it. Same window
    /// MezTracker uses, for the same reason: the landing line names no spell.</summary>
    public static readonly TimeSpan CastToLand = TimeSpan.FromSeconds(8);

    /// <summary>How long a non-ticking effect of unmeasured duration is believed before being
    /// dropped. Nothing in the log will announce a stranger's slow ending, so without a cap a
    /// mob killed three zones ago keeps a chip forever.</summary>
    public static readonly TimeSpan UnknownCap = TimeSpan.FromMinutes(10);

    /// <summary>Kept briefly past its expiry so a drop is seen rather than silently vanishing
    /// between two glances at the panel.</summary>
    public static readonly TimeSpan ExpiryLinger = TimeSpan.FromSeconds(5);

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
    private readonly List<(DateTime Time, string Caster, string Spell, bool Mine)> _recentCasts = [];

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
                RememberCast(cast.Time, "", cast.Spell, mine: true);
                break;
            // Someone else's cast is worth remembering only because a slow landing names
            // neither spell nor caster; this is the sole line that can explain one.
            case OtherCastEvent other:
                RememberCast(other.Time, other.Caster, other.Spell, mine: false);
                break;
            case DebuffLandedEvent landed:
                OnLanding(landed);
                break;
            // Names spell AND target, so it ends the right effect exactly and measures it
            // precisely - better than waiting for ticks to stop, which can only approximate.
            case SpellWornOffEvent { Pet: false, Target.Length: > 0 } fade:
                OnFade(fade);
                break;
        }
    }

    private void RememberCast(DateTime time, string caster, string spell, bool mine)
    {
        _recentCasts.Add((time, caster, spell, mine));
        _recentCasts.RemoveAll(c => time - c.Time > CastToLand);
    }

    /// <summary>A slow or cripple landing. The line names the mob and nothing else, so the
    /// newest cast inside the window is what explains it - the same rule MezTracker uses,
    /// including not consuming the cast, since one cast can land on several mobs.</summary>
    private void OnLanding(DebuffLandedEvent landed)
    {
        var cast = _recentCasts.LastOrDefault(c => landed.Time - c.Time <= CastToLand);
        if (cast.Spell is null or "") return;   // nobody we can see cast it: no spell, no chip

        var key = (landed.Target, cast.Spell);
        _active[key] = new DebuffState(
            landed.Target, cast.Spell, cast.Caster, cast.Mine,
            LandedAt: landed.Time, LastTickAt: landed.Time,
            ExpiresAt: Expiry(cast.Spell, landed.Time));
    }

    /// <summary>Only YOUR spells announce a fade, so this both ends and measures your own
    /// effects. Someone else's slow borrows the duration you measured for that same spell,
    /// and shows no countdown until you have measured one.</summary>
    private void OnFade(SpellWornOffEvent fade)
    {
        var key = (fade.Target, fade.Spell);
        if (!_active.Remove(key, out var state)) return;
        if (_died.Contains(fade.Target)) return;

        var measured = (fade.Time - state.LandedAt).TotalSeconds;
        if (measured > 0) Record(state.Spell, measured);
    }

    private void OnTick(DamageDealtEvent tick)
    {
        var key = (tick.Target, tick.Source);
        if (_active.TryGetValue(key, out var existing) && tick.Time - existing.LastTickAt > TickGrace)
        {
            // The previous effect ended before this tick, and nobody was watching. Retirement
            // cannot live only in Active(): that is driven by a once-a-second UI refresh, so a
            // batch of events - catching up on a log at startup, review mode, any replay -
            // walks straight past the gap and glues separate casts into one endless effect.
            _active.Remove(key);
            Learn(existing);
            existing = null!;
        }

        if (existing is not null && _active.ContainsKey(key))
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
                    Ticks = true,
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
            ExpiresAt: Expiry(tick.Source, tick.Time), Ticks: true);
    }

    /// <summary>What is ticking now. Also the point at which effects whose ticks have stopped
    /// are retired - and, when they ended on their own rather than with the mob, measured.</summary>
    public IReadOnlyList<DebuffState> Active(DateTime now)
    {
        foreach (var (key, state) in _active.ToList())
        {
            if (state.Ticks)
            {
                // A DoT that has stopped announcing itself is over.
                if (now - state.LastTickAt <= TickGrace) continue;
                _active.Remove(key);
                Learn(state);
                continue;
            }

            // A slow says nothing between landing and fading, so silence is not evidence.
            // Yours ends at its fade line; a stranger's has no fade line at all, so it ends at
            // the measured duration, or is eventually dropped rather than believed forever.
            var over = state.ExpiresAt is { } expiry
                ? now > expiry + ExpiryLinger
                : now - state.LandedAt > UnknownCap;
            if (over) _active.Remove(key);
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
        Record(state.Spell, (state.LastTickAt - state.LandedAt).TotalSeconds + ServerTickSeconds);
    }

    private void Record(string spell, double measured)
    {
        var samples = _samples.TryGetValue(spell, out var existing) ? existing : [];
        samples.Add(measured);
        if (samples.Count > SampleCap) samples.RemoveAt(0);
        _samples[spell] = samples;
    }

    private DateTime? Expiry(string spell, DateTime from) =>
        _samples.TryGetValue(spell, out var samples) && samples.Count > 0
            ? from.AddSeconds(Consensus(samples))
            : null;
}
