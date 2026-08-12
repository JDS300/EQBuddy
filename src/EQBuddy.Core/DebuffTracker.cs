namespace EQBuddy.Core;

/// <summary>One damage-over-time effect currently ticking on one target.</summary>
public sealed record DebuffState(
    string Target,
    /// <summary>What to show on the chip - the ranked name when a cast supplied one.</summary>
    string Spell,
    /// <summary>What to key on. Tick and fade lines never carry the rank, so tracking keys on
    /// the base name while the chip displays the rank.</summary>
    string BaseName,
    string Caster,
    bool IsMine,
    DateTime LandedAt,
    DateTime LastTickAt,
    DateTime? ExpiresAt,
    /// <summary>True for DoTs, which announce themselves every six seconds. A slow announces
    /// itself once and then says nothing until it fades, so silence means nothing for it.</summary>
    bool Ticks = false,
    /// <summary>Whether the countdown was measured, derived from the wiki, or is unknown.</summary>
    DurationCertainty Certainty = DurationCertainty.Unknown)
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
/// The log never states a duration, but it does not have to. The first tick IS the landing,
/// ticks arrive every ~6 seconds naming the spell, and the last tick falls on the expiry - the
/// fade line arrives in the same second, not one tick later. So a completed cast measures
/// itself as first tick to last tick, and that measurement drives the NEXT cast of the same
/// spell, which is why the first cast of anything shows no countdown and every one after does.
///
/// Measured across the 690k-line fixture (six DoTs, 138 completed casts): first-tick-to-fade
/// equals the wiki duration exactly for every spell whose wiki value is given in exact seconds
/// or ticks - Immolate 48, Drones of Doom 48, Gasping Embrace 48, Stinging Swarm 54. Anchoring
/// on the CAST line instead matches none of them, running long by each spell's own cast time
/// (Immolate 2.5s, Shiftless Deeds 6.0s), which is why the error is not a constant six seconds.
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

    /// <summary>Measurements kept per spell so a single odd cast cannot become the duration
    /// for good. Capped: a long session would otherwise grow this without bound, and the
    /// oldest samples say nothing the newest do not.</summary>
    public static readonly int SampleCap = 16;

    /// <summary>How long after a cast a first TICK can still be attributed to it, and so
    /// supply the rank. Wider than <see cref="CastToLand"/> because the cast line precedes the
    /// landing by the spell's own cast time, which reaches 6s (Shiftless Deeds) before the
    /// first tick is even due. Measured across the fixture, cast-to-first-tick runs a median of
    /// 5s and a 90th percentile of 8s. Still far below the shortest DoT duration (30s), so this
    /// can never reach back and grab the PREVIOUS cast of the same spell.</summary>
    public static readonly TimeSpan CastToTick = TimeSpan.FromSeconds(15);

    /// <summary>How long a cast is remembered. The widest of the windows that read it, or the
    /// wider window is fiction: pruning at <see cref="CastToLand"/> alone meant a tick could
    /// never see a cast older than 8s and <see cref="CastToTick"/>'s 15s described a list that
    /// could not contain them, silently losing the rank on a slow first tick.</summary>
    private static readonly TimeSpan CastMemory =
        CastToLand > CastToTick ? CastToLand : CastToTick;

    private readonly SpellDurationCatalog _catalog;

    private readonly Dictionary<(string Target, string BaseName), DebuffState> _active = [];

    /// <summary>Effects whose chip has been retired but whose fade line has not arrived yet.
    /// A derived duration is an estimate and the real spell can outlast it, so the panel stops
    /// showing a chip long before the effect is safe to FORGET - see <see cref="Active"/>.</summary>
    private readonly Dictionary<(string Target, string BaseName), DebuffState> _awaitingFade = [];
    private readonly Dictionary<string, List<double>> _samples = [];
    private readonly HashSet<string> _died = [];
    private readonly HashSet<string> _recastPending = [];
    private readonly List<(DateTime Time, string Caster, string Spell, bool Mine)> _recentCasts = [];

    public DebuffTracker(SpellDurationCatalog? catalog = null) =>
        _catalog = catalog ?? SpellDurationCatalog.Embedded;

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
                _recastPending.Add(_catalog.BaseNameOf(cast.Spell));
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
        _recentCasts.RemoveAll(c => time - c.Time > CastMemory);
    }

    /// <summary>A slow or cripple landing. The line names the mob and nothing else, so the
    /// newest cast inside the window is what explains it - the same rule MezTracker uses,
    /// including not consuming the cast, since one cast can land on several mobs.</summary>
    private void OnLanding(DebuffLandedEvent landed)
    {
        var cast = _recentCasts.LastOrDefault(c => landed.Time - c.Time <= CastToLand);
        if (cast.Spell is null or "") return;   // nobody we can see cast it: no spell, no chip

        var key = (Target: landed.Target, BaseName: _catalog.BaseNameOf(cast.Spell));
        _awaitingFade.Remove(key);   // a fresh landing supersedes whatever the old one measures
        var (expires, certainty) = Expiry(cast.Spell, cast.Spell, landed.Time);
        _active[key] = new DebuffState(
            landed.Target, cast.Spell, key.BaseName, cast.Caster, cast.Mine,
            LandedAt: landed.Time, LastTickAt: landed.Time,
            ExpiresAt: expires, Certainty: certainty);
    }

    /// <summary>Only YOUR spells announce a fade, so this both ends and measures your own
    /// effects. Someone else's slow borrows the duration you measured for that same spell,
    /// and shows no countdown until you have measured one.</summary>
    private void OnFade(SpellWornOffEvent fade)
    {
        var key = (fade.Target, _catalog.BaseNameOf(fade.Spell));
        // The chip may already be gone - a derived estimate that ran short retires it early -
        // but the effect is only truly forgotten at UnknownCap, so the fade can still measure it.
        if (!_active.Remove(key, out var state) && !_awaitingFade.Remove(key, out state)) return;
        if (state is null || _died.Contains(fade.Target)) return;

        var measured = (fade.Time - state.LandedAt).TotalSeconds;
        if (measured > 0) Record(state.Spell, measured);   // ranked name: samples are per-rank
    }

    private void OnTick(DamageDealtEvent tick)
    {
        // Every key goes through BaseNameOf - no exceptions. Real tick lines carry no numeral,
        // so this is usually the identity; for a spell genuinely NAMED with one and missing from
        // the catalog it is the difference between the tick key and the fade key agreeing and
        // fade-ending silently ceasing to work.
        var baseName = _catalog.BaseNameOf(tick.Source);
        var key = (tick.Target, baseName);
        // The rank is on the cast line and nowhere else, so a tick nobody cast has an unknown
        // tier - and an unknown tier cannot be derived, only measured.
        //
        // The window matters. _recentCasts is pruned only when a new cast arrives, so an
        // unbounded search would match a cast from ten minutes ago and silently become "the
        // last rank I ever saw" - which is exactly the guess this design rejected.
        var castName = _recentCasts
            .LastOrDefault(c => c.Mine && tick.Time - c.Time <= CastToTick
                && _catalog.BaseNameOf(c.Spell) == baseName).Spell;

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
            if (_recastPending.Remove(baseName))
            {
                // A refresh restarts the clock. The interrupted first cast is NOT recorded:
                // it was cut short by the recast, so it measures the gap between two casts
                // rather than the spell's duration - the same reason a kill teaches nothing.
                // Without the restart the two casts read as one long effect, which is how the
                // real log taught Immolate 115s against 54-60s for every sibling druid DoT.
                //
                // The sample key and the displayed name fall back to the SAME name. Split them
                // and a rank-N effect takes its countdown from tier-0 samples and shows it as
                // Measured - the pooling this design forbids, arriving through the display
                // rather than through the store.
                var (refreshedAt, refreshedCertainty) =
                    Expiry(castName ?? existing.Spell, castName, tick.Time);
                _active[key] = existing with
                {
                    Spell = castName ?? existing.Spell,
                    LandedAt = tick.Time,
                    LastTickAt = tick.Time,
                    ExpiresAt = refreshedAt,
                    Certainty = refreshedCertainty,
                    Ticks = true,
                };
                return;
            }
            _active[key] = existing with { LastTickAt = tick.Time };
            return;
        }

        _recastPending.Remove(baseName);   // that cast explains THIS landing, not a refresh

        _died.Remove(tick.Target);   // a fresh cast on a name that died earlier
        var (at, howSure) = Expiry(castName ?? tick.Source, castName, tick.Time);
        _active[key] = new DebuffState(
            tick.Target, castName ?? tick.Source, baseName, Caster: "", IsMine: true,
            LandedAt: tick.Time, LastTickAt: tick.Time,
            ExpiresAt: at, Ticks: true, Certainty: howSure);
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
            //
            // Two deadlines, and the sooner wins. The expiry says when to stop SHOWING a chip;
            // UnknownCap says how long any unmeasured chip may be believed at all, and a derived
            // duration must not defeat it - Valor's 3240s would hold a mis-attributed chip on the
            // panel for 54 minutes, which is the exact thing the cap exists to prevent.
            var cap = state.LandedAt + UnknownCap;
            var retireAt = state.ExpiresAt is { } expiry && expiry + ExpiryLinger < cap
                ? expiry + ExpiryLinger
                : cap;
            if (now <= retireAt) continue;

            // Retired from the panel, not forgotten. A derived duration is an ESTIMATE and the
            // real spell can outlast it - Shiftless Deeds IV measured 214.0s against a derived
            // 210.0s in the user's own log - so dropping the effect outright would leave its fade
            // line nothing to measure, and every later cast would re-derive the same estimate,
            // pinning the spell at the guess for the rest of the session.
            _active.Remove(key);
            _awaitingFade[key] = state;
        }

        // Bounded, for the same reason the chip is: nothing announces a stranger's slow ending.
        foreach (var (key, state) in _awaitingFade.ToList())
            if (now - state.LandedAt > UnknownCap) _awaitingFade.Remove(key);

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
        Record(state.Spell, (state.LastTickAt - state.LandedAt).TotalSeconds);
    }

    private void Record(string spell, double measured)
    {
        var samples = _samples.TryGetValue(spell, out var existing) ? existing : [];
        samples.Add(measured);
        if (samples.Count > SampleCap) samples.RemoveAt(0);
        _samples[spell] = samples;
    }

    /// <summary>Measured samples first, catalog second, nothing third - the trust order the
    /// whole panel rests on. Samples key on the RANKED name because ranks genuinely differ:
    /// pooling Immolate I with Immolate V would corrupt both. A measurement is never adjusted
    /// toward the catalog; Tepid Deeds keeps its measured 126s against a wiki 150.
    ///
    /// <paramref name="castName"/> is null when no cast explained this effect, and then NOTHING
    /// is derived. The rank lives on the cast line alone, so deriving from the base name would
    /// silently assume tier 0 - reading 48s for a rank-V Immolate that runs 72s, and warning
    /// early on every cast. An unknown rank is an unknown duration.</summary>
    private (DateTime? At, DurationCertainty Certainty) Expiry(
        string sampleKey, string? castName, DateTime from)
    {
        if (_samples.TryGetValue(sampleKey, out var samples) && samples.Count > 0)
            return (from.AddSeconds(Consensus(samples)), DurationCertainty.Measured);
        if (castName is not null && _catalog.Resolve(castName) is { } derived)
            return (from.AddSeconds(derived.Seconds), DurationCertainty.Derived);
        return (null, DurationCertainty.Unknown);
    }
}
