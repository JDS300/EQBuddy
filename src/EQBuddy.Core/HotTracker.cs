using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>One of your heal-over-time spells currently running: either ticking on a
/// named target, or cast and not yet ticked, in which case <see cref="TargetKnown"/> is
/// false and <see cref="Target"/> is empty — the log does not say who a beneficial spell
/// was cast on, and only the first tick ever will.</summary>
public sealed record HotState(
    string Target, string Spell, DateTime FirstTick, DateTime LastTick, DateTime ExpiresAt)
{
    /// <summary>Seconds until the HoT stops healing (0 once it has).</summary>
    public double RemainingSeconds(DateTime now) =>
        Math.Max(0, (ExpiresAt - now).TotalSeconds);

    /// <summary>False while this chip is still only a cast: EQ logs no target for a
    /// beneficial spell (see the tracker's header), so between the cast and its first
    /// tick there is genuinely nobody to name and <see cref="Target"/> is empty. Empty
    /// rather than a placeholder because no placeholder is safe — any name we invented
    /// could be somebody's actual character. UIs show the spell instead of a person.
    /// </summary>
    public bool TargetKnown => Target.Length > 0;
}

/// <summary>
/// Tracks YOUR active heal-over-time spells — one countdown per (target, spell), so the
/// chip stack answers "how long until this HoT stops ticking".
///
/// Unlike a mez, a HoT announces neither its landing nor its fade: there is not one
/// "Your Blossoming Heal spell has worn off" line in 690k lines of eqlog_Daggo_freeport,
/// while Mesmerization fades 1197 times in the same file. Worse, a tick line only exists
/// when healing actually happened — ZERO of the tick lines in that log report 0 hit
/// points — so a HoT cast on a full-health target ticks in complete silence. Waiting for
/// a tick therefore loses the chip in exactly the case where the healer has no other way
/// to see the spell, which is what was reported: "I'm full health but I didn't see it
/// show up as a chip anywhere."
///
/// So a chip has two acts. The CAST opens it, and the first TICK completes it:
///
///   • "You begin casting Blossoming Heal." opens a chip with NO target. That is not
///     laziness — nothing in the log names the target of a beneficial cast. "You have
///     targeted X" and "You are targeting" appear 0 times in the fixture log, and the
///     cast line is bare. An empty Target says so honestly; any placeholder name could
///     collide with a real character. Its countdown is anchored where the first tick
///     would have been (see <see cref="CastToFirstTick"/>), so a cast that never ticks
///     still expires on its own.
///   • The first tick of a NEW series binds that chip: it supplies the target and
///     re-anchors the countdown to the real first tick. The rest of the ticks sustain it.
///
/// The end comes from two places:
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
/// name the ticks use and every cast-side name is put through
/// <see cref="SpellCatalog.BaseName"/> before it is looked up. (Interrupt and fizzle
/// lines are already unranked in the fixture log — "Your Blooming Heal spell is
/// interrupted." — but they go through the same door, since a ranked one would cost us
/// the cancellation.)
///
/// One thing the log will not tell us: which spells ARE HoTs. The cast line says only
/// that something was cast, so a chip on every cast would fill the stack with nukes and
/// gates. A name earns HoT status by being SEEN ticking (or triggering), and the learned-
/// duration store — keyed by the same unranked name — carries that knowledge across
/// restarts. In a real session both are free: the startup replay of today's log runs
/// before anything is displayed. The cold-start cost is one landed HoT, once ever, on a
/// target who actually needed the healing.
/// </summary>
public sealed class HotTracker
{
    private readonly Dictionary<string, HotState> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _learned = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Unranked names we have watched tick or trigger, i.e. the spells whose
    /// cast line is worth opening a chip for. Seeded from the duration store on attach,
    /// since every key in it was learned from a real HoT's Trigger.</summary>
    private readonly HashSet<string> _hotSpells = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>How long after the cast line the first tick is expected. Measured over
    /// the 768 cast→first-tick pairs in eqlog_Daggo_freeport: the whole spread is 0–8s
    /// (96.4%, with a 20s tail of 27 stragglers), 84% of it falls in 2–7s, and 5s is the
    /// median. It is the cast time plus the wait for the first tick of the series.
    ///
    /// It only ever anchors a chip that has not ticked yet, and the first real tick
    /// replaces it, so the error it can introduce is bounded by that spread — a few
    /// seconds on a 24-second countdown, and only until the HoT proves otherwise.</summary>
    public static readonly TimeSpan CastToFirstTick = TimeSpan.FromSeconds(5);

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
                    if (Plausible(seconds))
                    {
                        _learned.TryAdd(spell, seconds);
                        // Every key here was measured off a real Trigger, so the store
                        // doubles as the list of names known to be HoTs — which is what
                        // lets the FIRST cast of a session open a chip instead of waiting
                        // to be taught all over again.
                        _hotSpells.Add(spell);
                    }
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
                // "You begin casting Blossoming Heal." — the only evidence a HoT exists
                // when the target is at full health and the ticks heal (and log) nothing.
                // Gated on the spell being a known HoT: every cast the player makes
                // arrives here, and a chip for each would bury the stack.
                case SpellCastEvent cast when IsKnownHot(cast.Spell):
                    changed = OnCast(cast.Time, BaseName(cast.Spell));
                    break;
                // The two ways a started cast dies before it lands — "Your Blooming Heal
                // spell is interrupted." (320 in the fixture log) and "Your Flowering
                // Heal spell fizzles!" (119, and note the bang). Both already come out of
                // LogParser with the spell name attached. Nothing landed, so the chip
                // that cast opened is a lie and goes at once; a chip already bound to a
                // target belongs to an earlier cast that DID land and is left alone.
                case SpellInterruptedEvent stopped:
                    changed = CancelWaiting(stopped.Spell);
                    break;
                case FizzleEvent fizzled when fizzled.Spell.Length > 0:
                    changed = CancelWaiting(fizzled.Spell);
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

    /// <summary>A cast opens a chip that knows its spell and nothing else. It is a real
    /// chip, not a pending note: the healer's question is "is my HoT running", and until
    /// a tick names somebody the honest answer is "yes, on whoever you had targeted".
    ///
    /// It deliberately does NOT touch a chip already running on a known target. A cast
    /// while the same HoT ticks on Daggo is either a refresh on Daggo or a fresh cast on
    /// someone else, and the log cannot say which — it names no target here and there are
    /// no "You have targeted X" lines in 690k lines to fall back on. Of the two guesses,
    /// refreshing Daggo is the one that can hurt: it would promise a full duration on a
    /// chip that may in truth lapse in seconds, and a healer trusting that lets the HoT
    /// drop. A second, targetless chip is the harmless error — it says "another one is
    /// running, on whoever you cast it on", which is exactly what is known — and if the
    /// cast really was a refresh the two chips reconverge as soon as ticks resolve them.
    ///
    /// Recasting before the first tick simply replaces the waiting chip (same key): the
    /// newer cast is the live one, and the earlier one, if it landed at all, will still
    /// be picked up by its own ticks.</summary>
    private bool OnCast(DateTime castTime, string spell)
    {
        var firstTick = castTime + CastToFirstTick;
        _active[Key("", spell)] = new HotState(
            "", spell, firstTick, firstTick, firstTick + DurationFor(spell));
        return true;
    }

    private bool OnTick(HealEvent tick)
    {
        // Ticks are the only line that says a name IS a HoT, which is what makes the next
        // cast of it worth a chip.
        _hotSpells.Add(tick.Spell);
        var key = Key(tick.Target, tick.Spell);
        if (_active.TryGetValue(key, out var running) && Continues(running, tick.Time))
        {
            // Same cast still ticking: the countdown belongs to the FIRST tick, so
            // sustaining it changes nothing a chip displays — no Changed, or every tick
            // would repaint the whole stack.
            _active[key] = running with { LastTick = tick.Time };
            return false;
        }

        // This tick opens a NEW series, so it is the first tick of the most recent cast:
        // if that cast is still waiting to learn who it landed on, this binds it. Binding
        // is a move, not a copy — the waiting chip becomes this chip, which is why a cast
        // followed by its ticks yields ONE chip that gains a name rather than two.
        //
        // Note the order: a tick that merely SUSTAINS a running series (handled above)
        // never gets here, so it can never bind. That is the conservative reading, and
        // the only safe one — Daggo's fourth tick is evidence about Daggo's cast and says
        // nothing about the one that went out two seconds ago.
        _active.Remove(Key("", tick.Spell));

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
        _hotSpells.Add(spell);
        if (!_active.Remove(Key(burst.Target, spell), out var entry))
            // No series on this target — but a cast of this spell may still be waiting
            // for a first tick that never came, because the target stayed at full health
            // until this last burst finally healed (and logged) something. The Trigger
            // says that cast is over, so its chip is done. Nothing is LEARNED from it:
            // the chip was anchored on the estimated first tick, and a measurement taken
            // from a guess would poison a store that otherwise holds only real ones.
            return CancelWaiting(spell);

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

    /// <summary>Drops the chip a cast of this spell opened, if one is still waiting for
    /// its first tick. A no-op for anything else, which is what makes it safe to call on
    /// every interrupt and fizzle the player produces: a spell with no waiting chip —
    /// most of them — simply has no such key.</summary>
    private bool CancelWaiting(string spell) => _active.Remove(Key("", BaseName(spell)));

    /// <summary>Has this name been seen ticking (this session or a previous one)? The
    /// cast line carries the rank, everything else does not, so it is stripped here.</summary>
    private bool IsKnownHot(string spell) => _hotSpells.Contains(BaseName(spell));

    private static string BaseName(string spell) => SpellCatalog.BaseName(spell);

    /// <summary>Chip key. An empty target — the key of a cast still waiting for its first
    /// tick — cannot collide with a real one: <see cref="LogParser.Normalize"/> only ever
    /// returns empty for empty input, and no log line names an empty target.</summary>
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
