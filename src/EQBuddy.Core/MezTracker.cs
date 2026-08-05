using System.Reflection;
using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>One entry in the embedded mez catalog (Data/MezSpells.json).</summary>
public sealed class MezSpellInfo
{
    public string Name { get; set; } = "";
    public double? DurationSeconds { get; set; }
    public bool Aoe { get; set; }
    public string Landing { get; set; } = "mesmerized";
    public string Source { get; set; } = "";
}

/// <summary>A currently-active (believed) mez, for the chip stack.</summary>
public sealed record MezState(
    string Target, string Spell, string Caster, DateTime LandedAt, DateTime? ExpiresAt)
{
    /// <summary>Seconds until wake-up; null while the duration is unknown (the chip
    /// shows the mez without a countdown — it still clears on break).</summary>
    public double? RemainingSeconds(DateTime now) =>
        ExpiresAt is { } e ? Math.Max(0, (e - now).TotalSeconds) : null;
}

/// <summary>
/// Tracks who is mezzed, for how much longer — from ANY group member's log, not just the
/// caster's. The landing line ("X has been mesmerized.") is bystander-visible, and other
/// players' casts log with spell name and rank ("Shack begins casting Shield of Thistles
/// IV."), so every EQBuddy in the group derives the same state from its own log; no
/// networking involved. Mirrors the charm trust rules: a landing line only counts when a
/// recent cast KNOWN to be a mez explains it — an unexplained landing is someone outside
/// the log's view and is ignored.
///
/// Durations: catalog first (Data/MezSpells.json — null until researched), overridden by
/// learned values. Only the CASTER's log sees "Your X spell has worn off of Y.", so only
/// the caster's EQBuddy can measure real durations; it learns the LONGEST land→fade gap
/// per exact spell name (rank included — ranks lengthen mezzes), because early breaks
/// shorten gaps but nothing lengthens them. Learned values persist via
/// <see cref="AttachStore"/> and flow to the rest of the group through catalog updates.
/// </summary>
public sealed class MezTracker
{
    /// <summary>A landing this long after the cast began no longer belongs to it
    /// (covers cast time + travel + log flushing).</summary>
    public static readonly TimeSpan CastToLand = TimeSpan.FromSeconds(8);
    /// <summary>Without a known duration, a mez chip that nothing ever breaks is
    /// dropped after this long — mezzes don't last minutes.</summary>
    public static readonly TimeSpan UnknownDurationCap = TimeSpan.FromSeconds(120);
    /// <summary>An expired chip stays visible at 0:00 this long before dropping —
    /// rank-lengthened mezzes outlive the base duration, and a chip that silently
    /// vanishes mid-mez reads as a bug (issue #32).</summary>
    public static readonly TimeSpan ExpiryLinger = TimeSpan.FromSeconds(8);
    /// <summary>Break signals against the same creature NAME this close together are one
    /// creature's engagement, worth one broken mez between them — see
    /// <see cref="CreditBreak"/> for the measurements and the reasoning.</summary>
    public static readonly TimeSpan BreakWindow = TimeSpan.FromSeconds(6);

    private readonly Dictionary<string, MezSpellInfo> _catalog;
    private readonly Dictionary<string, double> _learned = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MezState> _active = [];
    private readonly List<(string Caster, string Spell, DateTime Time)> _recentCasts = [];
    /// <summary>Per creature name: when we last saw a break signal for it, whether a kill
    /// line has already claimed that engagement, and when a damage/kill signal last actually
    /// TOOK a chip for it (default = never). Keyed on log time, so replay-safe.
    ///
    /// <c>Removed</c> is separate from <c>At</c> because a live engagement window is not the
    /// same thing as a break that has been counted: in the reported log the group beats on
    /// one creature for 36 seconds without a 6s gap, so the window never lapses, yet only the
    /// first of those lines ever cost a chip. <see cref="OnWornOff"/> yields to a break that
    /// was counted, not to a window that is merely open — see there.</summary>
    private readonly Dictionary<string, (DateTime At, bool KillCredited, DateTime Removed)> _breaks =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Durations learned from a fade that has not yet outlived <see cref="BreakWindow"/>,
    /// with the value they replaced. See <see cref="OnWornOff"/> for why a measurement is only
    /// provisional until the next few seconds of log prove nothing hit the creature.</summary>
    private readonly List<(string Name, string Spell, DateTime At, double? Previous)> _provisional = [];
    private string? _storePath;
    private readonly object _lock = new();

    /// <summary>Raised when the set of active mezzes changes (not on every tick).</summary>
    public event Action? Changed;

    public MezTracker(IEnumerable<MezSpellInfo>? catalog = null)
    {
        _catalog = (catalog ?? LoadEmbedded()).ToDictionary(
            s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
    }

    public static List<MezSpellInfo> LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQBuddy.Core.Data.MezSpells.json")
            ?? throw new InvalidOperationException("MezSpells.json missing from resources");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("spells").Deserialize<List<MezSpellInfo>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    /// <summary>Loads learned durations and saves after each new maximum —
    /// same pattern as SpellCatalog's store; tests don't attach one.</summary>
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
                    if (seconds is > 0 and < 600) _learned.TryAdd(spell, seconds);
        }
        catch { /* corrupt store: rewritten on next learn */ }
    }

    /// <summary>Second/third consumer of the parsed event stream (like SpawnTimers):
    /// replay-safe because everything keys on log timestamps.</summary>
    public void Apply(GameEvent evt)
    {
        var changed = false;
        lock (_lock)
        {
            switch (evt)
            {
                case SpellCastEvent own when IsMezSpell(own.Spell):
                    RememberCast("You", own.Spell, own.Time);
                    break;
                case OtherCastEvent other when IsMezSpell(other.Spell):
                    RememberCast(other.Caster, other.Spell, other.Time);
                    break;
                case MezzedEvent mez:
                    changed = OnLanding(mez);
                    break;
                // Any damage wakes a mezzed creature — from anyone, visible to everyone.
                // All of these go through CreditBreak, which collapses the several lines
                // of one attack round into the single break they actually represent.
                case DamageDealtEvent dd:
                    changed = CreditBreak(dd.Target, dd.Time);
                    break;
                case ThirdMeleeEvent tm:
                    // Damage TO the target breaks it; the target ATTACKING proves it woke.
                    changed = CreditBreak(tm.Target, tm.Time) | CreditBreak(tm.Attacker, tm.Time);
                    break;
                case ThirdDotEvent td:
                    changed = CreditBreak(td.Target, td.Time);
                    break;
                case ThirdSchoolEvent tsch:
                    changed = CreditBreak(tsch.Target, tsch.Time) | CreditBreak(tsch.Attacker, tsch.Time);
                    break;
                // The creature acting proves it's awake — but a DoT tick doesn't count:
                // a dot cast before the mez keeps ticking on you while the mob sleeps.
                case DamageTakenEvent { Self: false, OverTime: false } dt:
                    changed = CreditBreak(dt.Attacker, dt.Time);
                    break;
                case KillEvent k:
                    changed = CreditKill(k.Target, k.Time);
                    break;
                case SpellWornOffEvent { Pet: false } wo when wo.Target.Length > 0 && IsMezSpell(wo.Spell):
                    // Caster-private fade: the exact end of YOUR mez, and — when nothing
                    // broke it — the one signal that can teach a real duration (see class
                    // summary). It joins the same break window as the damage lines, because
                    // a mez broken by a hit logs BOTH (see OnWornOff).
                    changed = OnWornOff(wo);
                    break;
                case ZoneEvent:
                    changed = _active.Count > 0;
                    _active.Clear();
                    _recentCasts.Clear();
                    _breaks.Clear();
                    // Nothing can arrive to retract a measurement across a zone line: the
                    // creature is gone from the log. Pending fades stand as learned.
                    _provisional.Clear();
                    break;
            }
            Prune(evt.Time);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Active mezzes at <paramref name="now"/>, soonest wake-up first;
    /// unknown-duration entries sort last (nothing to warn about yet). Entries past
    /// their expiry stay visible (at 0:00) for <see cref="ExpiryLinger"/> — the mez
    /// may genuinely still hold (rank-lengthened durations) and a silent vanish
    /// mid-mez reads as a bug.</summary>
    public List<MezState> Snapshot(DateTime now)
    {
        lock (_lock)
            return _active
                .Where(m => m.ExpiresAt is null || now - m.ExpiresAt < ExpiryLinger)
                .OrderBy(m => m.ExpiresAt ?? DateTime.MaxValue)
                .ToList();
    }

    /// <summary>Learned durations (exact spell name → seconds), for display/export.</summary>
    public IReadOnlyDictionary<string, double> LearnedDurations
    {
        get { lock (_lock) return new Dictionary<string, double>(_learned); }
    }

    private bool IsMezSpell(string spell) =>
        _catalog.ContainsKey(SpellCatalog.BaseName(spell));

    private void RememberCast(string caster, string spell, DateTime t)
    {
        _recentCasts.Add((caster, spell, t));
        if (_recentCasts.Count > 32) _recentCasts.RemoveRange(0, 16);
    }

    private bool OnLanding(MezzedEvent mez)
    {
        // Newest explaining cast wins. AoE mezzes land on several targets from one cast,
        // so the cast is NOT consumed — each landing within the window claims it.
        var cast = _recentCasts.LastOrDefault(c => mez.Time - c.Time <= CastToLand);
        if (cast.Spell is null || cast.Spell.Length == 0) return false;   // nobody we can see cast a mez

        var entry = new MezState(mez.Target, cast.Spell, cast.Caster, mez.Time,
            DurationFor(cast.Spell) is { } d ? mez.Time.AddSeconds(d) : null);

        // Same-name handling (issue #32, reworked from the original keep-earliest rule):
        // chain-mezzing ONE target is the normal workflow, so a re-landing REFRESHES the
        // earliest-expiring same-name entry. The exception is several landings in the
        // same second (an AoE catching same-named mobs): those are distinct creatures
        // and get their own entries — the UI numbers them.
        var sameName = _active.Where(m =>
            m.Target.Equals(mez.Target, StringComparison.OrdinalIgnoreCase)).ToList();
        var refresh = sameName
            .Where(m => m.LandedAt != mez.Time)
            .OrderBy(m => m.ExpiresAt ?? DateTime.MaxValue)
            .FirstOrDefault();
        if (refresh is not null) _active.Remove(refresh);
        _active.Add(entry);
        return true;
    }

    /// <summary>
    /// "Your X spell has worn off of Y." — the second path to the reported "2 of the same
    /// mobs break clears both", still live after 1.29.1 deduped repeated damage LINES.
    /// Breaking a mez with a hit makes EverQuest log the event TWICE: the spell wearing off,
    /// and the blow that did it, naming the same creature in the same second. From the
    /// reported log — AoE lands on twins at 20:54:19, then at 20:54:26 "Your Mesmerization
    /// spell has worn off of Innoruuk`s Chosen." is immediately followed by "You bash
    /// Innoruuk`s Chosen for 5 points of damage." This method used to remove an entry
    /// directly, bypassing <see cref="CreditBreak"/> entirely, so it neither consumed a
    /// credit nor stamped one: the fade took one twin and the bash took the other.
    ///
    /// So the fade now joins the break window from both ends. It yields when a damage or
    /// kill signal has already TAKEN a chip for this name inside the window (the reverse
    /// ordering — the log's order within one second is not guaranteed), and it stamps the
    /// window when it removes one, so the blow that follows is suppressed like any other
    /// line of the same round.
    ///
    /// Two deliberate asymmetries with <see cref="CreditBreak"/>:
    ///
    /// It yields to a COUNTED break (<c>Removed</c>), not to an open window (<c>At</c>). In
    /// the reported log the fight on that name runs 36 seconds with no 6s gap, so the window
    /// is permanently live; the re-mez at 20:54:52 lands INTO it and therefore can never
    /// have its break credited from damage (deliberate — see <see cref="CreditBreak"/>),
    /// leaving this caster-private line as the only thing that can clear the chip.
    ///
    /// And it never stamps <c>Removed</c> itself, so a fade never suppresses another FADE.
    /// Two fade lines in one second are two mezzes ending, not one event logged twice: an
    /// AoE that landed on twins in the same second expires on both in the same second too.
    /// </summary>
    private bool OnWornOff(SpellWornOffEvent wo)
    {
        // YOUR entries only. This line is caster-private, so it ends YOUR mez and is
        // evidence about nothing else — but EQ logs the fade WITHOUT the rank, so matching
        // on target name alone let it settle on another chanter's entry and file the gap
        // under THEIR rank. A real log taught "Mesmerization VI" = 6s to a player with zero
        // casts of it. Among your own same-named entries the longest-asleep one fades first.
        var name = LogParser.Normalize(wo.Target);
        var entry = _active
            .Where(m => m.Caster.Equals("You", StringComparison.OrdinalIgnoreCase)
                && m.Target.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.LandedAt)
            .FirstOrDefault();
        // No entry of yours: the fade belongs to a mez we never tracked, and it must not
        // touch the window either — stamping it would let YOUR fade swallow the break of
        // somebody else's chip on a same-named creature.
        if (entry is null) return false;

        var known = _breaks.TryGetValue(name, out var prev);
        var counted = known && wo.Time - prev.Removed <= BreakWindow;
        var live = known && wo.Time - prev.At <= BreakWindow;
        _breaks[name] = (wo.Time, live && prev.KillCredited, live ? prev.Removed : default);
        if (counted) return false;   // the blow that broke this mez already cost the chip
        _active.Remove(entry);

        // Learning, and why the measurement is only PROVISIONAL for BreakWindow.
        //
        // A fade measures the real duration only when the mez ended on its own; a fade
        // caused by damage measures the time to the BREAK. Replaying the reported log
        // through 1.29.1 filed "Mesmerization V = 7" off the trace above — a spell whose
        // real length is ~25s. "Longest observed wins" caps that but cannot prevent it: on
        // a fresh store 7s is the only value there is, and every chip cast from it counts
        // down to a wake-up 18 seconds early.
        //
        // The tell arrives AFTER the fade, not before: the engagement in the reported log
        // last touched this name at 20:54:18, eight seconds earlier — outside the window —
        // so nothing at fade time distinguishes the break from an expiry. It is the bash in
        // the NEXT line that gives it away. Hence: learn immediately (so a re-cast in the
        // same breath uses the value, and so a fade at the very end of a session is still
        // persisted), then retract if a break signal for that creature lands inside the
        // window — see RetractProvisional, called from CreditBreak/CreditKill.
        //
        // This also silently discards fades that merely happen during combat on that name,
        // whether or not the damage broke them. That costs nothing: longest-wins means the
        // useful samples are the quiet, full-duration fades, and those are exactly the ones
        // with no combat around them.
        // Longest observed per exact (ranked) spell name wins: early breaks shorten the
        // land->fade gap, nothing lengthens it.
        var observed = (wo.Time - entry.LandedAt).TotalSeconds;
        var previous = _learned.TryGetValue(entry.Spell, out var was) ? was : (double?)null;
        if (observed is > 3 and < 600 && observed > (previous ?? 0))
        {
            _provisional.Add((name, entry.Spell, wo.Time, previous));
            _learned[entry.Spell] = Math.Round(observed, 1);
            SaveStore();
        }
        return true;
    }

    /// <summary>Undoes any fade measurement for <paramref name="name"/> still inside its
    /// <see cref="BreakWindow"/>: a break signal this close behind the fade means the fade
    /// WAS that break, so the gap it measured is the time to the break, not the duration.
    /// Newest first, restoring each one's predecessor, so stacked measurements on one spell
    /// unwind to exactly what was there before.</summary>
    private void RetractProvisional(string name, DateTime now)
    {
        var undone = false;
        for (var i = _provisional.Count - 1; i >= 0; i--)
        {
            var p = _provisional[i];
            if (now - p.At > BreakWindow
                || !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Previous is { } before) _learned[p.Spell] = before;
            else _learned.Remove(p.Spell);
            _provisional.RemoveAt(i);
            undone = true;
        }
        if (undone) SaveStore();
    }

    private double? DurationFor(string spell) =>
        _learned.TryGetValue(spell, out var learned) ? learned
        : _catalog.TryGetValue(SpellCatalog.BaseName(spell), out var info) ? info.DurationSeconds
        : null;

    /// <summary>
    /// One break signal against <paramref name="target"/>, deduped against the ones that
    /// came just before it. Reported from play after 1.29.1: "when the targets share the
    /// same name, the chip shows up but then breaking one of them removes it for both."
    /// <see cref="RemoveTarget"/> already drops only ONE entry per call (issue #32) — the
    /// flaw was upstream, in calling it once per damage LINE. One attack round is main
    /// hand, off hand, kick, bash, the mob's swing back, a damage-shield proc: several
    /// lines, same second, same name, one broken mez. In the 690k-line fixture 54,999 of
    /// 83,299 (second, target-name) buckets carry more than one break line, so the naive
    /// reading over-counted breaks on two thirds of all combat seconds and a pair of twins
    /// was wiped inside one round.
    ///
    /// The window slides: any signal inside it, counted or not, pushes it forward, so one
    /// continuous engagement costs exactly one chip however long it runs. That matters,
    /// because a fixed cooldown would not be enough. Measured over the fixture, runs of
    /// break lines on one name (joined by gaps of 6s or less) last a median of 4s but
    /// p75 = 21s, p90 = 47s, max 723s — a tank grinding down one woken mob for half a
    /// minute would otherwise eat the sleeping twin's chip every cooldown, which is the
    /// reported bug with extra steps.
    ///
    /// 6s for the window itself: 66% of consecutive break lines on a name are in the SAME
    /// second (the floor the round structure forces), 94% within 2s, 97.5% within 6s, and
    /// the histogram flattens into a thin tail after that (7s: 401 lines, 8s: 332, 9s:
    /// 241 …). Six seconds therefore spans a slow two-hander plus a DoT tick landing
    /// between swings without reaching into a genuinely separate pull.
    ///
    /// The residual error is deliberate and follows the rule in <see cref="RemoveTarget"/>:
    /// when a second twin really does wake mid-fight, its break is absorbed as part of the
    /// first one's engagement and its chip lingers until the mez expires or the creature
    /// dies. Showing a mez that has actually broken is self-correcting and visible;
    /// silently erasing a live mez — the reported bug — is neither.
    ///
    /// Replaying the whole fixture through the tracker: 4,407 state changes before this
    /// rule, 3,951 after — 456 chip removals in one log that no separate creature ever
    /// justified. Peak simultaneous chips (17) is unchanged, so nothing stopped clearing.
    /// </summary>
    private bool CreditBreak(string target, DateTime now)
    {
        var name = LogParser.Normalize(target);
        RetractProvisional(name, now);
        var live = _breaks.TryGetValue(name, out var prev) && now - prev.At <= BreakWindow;
        var took = !live && RemoveTarget(name);
        // Marked even when nothing was removed: the point is to recognise the engagement,
        // and a fight already in progress on one "orc pawn" must not eat the chip of the
        // one an enchanter mezzes NEXT to it a moment later. Removed, by contrast, moves
        // only when a chip actually went — that is what a fade yields to (see OnWornOff).
        _breaks[name] = (now, live && prev.KillCredited, took ? now : live ? prev.Removed : default);
        return took;
    }

    /// <summary>
    /// A kill line, which is genuinely one per creature — but arrives in the same second
    /// as the killing blow in 2,504 of the fixture's 2,624 kills (96%, 97.6% within 1s),
    /// so it is almost never NEW information: the damage that killed the creature has
    /// already been credited as its break. Counting it again would take a second chip,
    /// reproducing the reported bug at the end of every fight. So a kill inside a live,
    /// not-yet-killed engagement claims that engagement instead of removing anything —
    /// and flags it, so the NEXT kill on the same name is not swallowed too. Killing two
    /// mezzed adds back to back therefore clears both chips, at either creature's death,
    /// however tightly the deaths are packed (3.3% of same-name kill pairs are within 6s
    /// of each other, 13% within 20s).
    /// </summary>
    private bool CreditKill(string target, DateTime now)
    {
        var name = LogParser.Normalize(target);
        RetractProvisional(name, now);
        var live = _breaks.TryGetValue(name, out var prev) && now - prev.At <= BreakWindow;
        var claimable = live && !prev.KillCredited;
        var took = !claimable && RemoveTarget(name);
        _breaks[name] = (now, true, took ? now : live ? prev.Removed : default);
        return took;
    }

    /// <summary>Drops ONE entry for an already-normalized creature name. Only ever called
    /// through <see cref="CreditBreak"/>/<see cref="CreditKill"/>, which decide whether a
    /// signal is a new break at all.</summary>
    private bool RemoveTarget(string name)
    {
        // ONE entry per break, not all (issue #32): with two mezzed "orc pawn"s, the
        // tank hitting one must not erase the other's chip. The log can't say which
        // woke, so drop the earliest-expiring — the least-harmful guess, and a wrong
        // one self-corrects on the next break (the next ROUND, not the next line: see
        // CreditBreak, where counting per line was the 1.29.1 twin-wipe bug).
        var victim = _active
            .Where(m => m.Target.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.ExpiresAt ?? DateTime.MaxValue)
            .FirstOrDefault();
        if (victim is null) return false;
        _active.Remove(victim);
        return true;
    }

    private void Prune(DateTime now)
    {
        _recentCasts.RemoveAll(c => now - c.Time > CastToLand);
        // Engagement markers are only readable inside BreakWindow, so sweeping them is
        // pure housekeeping — done in a batch (like the _recentCasts cap) rather than on
        // every event, because a long session names thousands of creatures and this runs
        // per parsed line. A handful of names are in flight at once in real combat.
        // A fade measurement that has outlived the window was not a break after all: nothing
        // hit the creature in the seconds behind it, so it stands as learned.
        if (_provisional.Count > 0)
            _provisional.RemoveAll(p => now - p.At > BreakWindow);
        if (_breaks.Count > 32)
            foreach (var name in _breaks.Where(b => now - b.Value.At > BreakWindow)
                         .Select(b => b.Key).ToList())
                _breaks.Remove(name);
        // Entries are RETAINED well past their visible expiry (Snapshot hides them
        // after ExpiryLinger): a rank-lengthened mez can fade long after the base
        // duration, and the natural-fade line must still find its entry to learn
        // from — pruning at the linger would make high ranks unlearnable. A stale
        // retained entry also absorbs the next break line first (earliest expiry),
        // which is the right guess: it's the likeliest-awake one.
        _active.RemoveAll(m => now - m.LandedAt > UnknownDurationCap);
    }

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
