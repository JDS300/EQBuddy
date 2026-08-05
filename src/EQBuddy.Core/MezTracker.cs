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
    /// <summary>Per creature name: when we last saw a break signal for it, and whether a
    /// kill line has already claimed that engagement. Keyed on log time, so replay-safe.</summary>
    private readonly Dictionary<string, (DateTime At, bool KillCredited)> _breaks =
        new(StringComparer.OrdinalIgnoreCase);
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
                    // Caster-private natural fade: the exact end, and the one signal that
                    // can teach a real duration (see class summary).
                    changed = OnWornOff(wo);
                    break;
                case ZoneEvent:
                    changed = _active.Count > 0;
                    _active.Clear();
                    _recentCasts.Clear();
                    _breaks.Clear();
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

    private bool OnWornOff(SpellWornOffEvent wo)
    {
        // YOUR entries only. This line is caster-private ("Your X spell has worn off of Y"),
        // so it ends YOUR mez and is evidence about nothing else — but EQ logs the fade
        // WITHOUT the rank, so matching on target name alone let it settle on another
        // chanter's entry and file the gap under THEIR rank. A real log taught
        // "Mesmerization VI" = 6s to a player with zero casts of it. Among your own
        // same-named entries the longest-asleep one still fades first.
        var entry = _active
            .Where(m => m.Caster.Equals("You", StringComparison.OrdinalIgnoreCase)
                && m.Target.Equals(
                    LogParser.Normalize(wo.Target), StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.LandedAt)
            .FirstOrDefault();
        if (entry is null) return false;
        _active.Remove(entry);
        // A natural fade measures the full duration; learn the longest observed per
        // exact (ranked) spell name — early breaks shorten gaps, nothing lengthens them.
        var observed = (wo.Time - entry.LandedAt).TotalSeconds;
        if (observed is > 3 and < 600
            && (!_learned.TryGetValue(entry.Spell, out var known) || observed > known))
        {
            _learned[entry.Spell] = Math.Round(observed, 1);
            SaveStore();
        }
        return true;
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
        var live = _breaks.TryGetValue(name, out var prev) && now - prev.At <= BreakWindow;
        // Marked even when nothing was removed: the point is to recognise the engagement,
        // and a fight already in progress on one "orc pawn" must not eat the chip of the
        // one an enchanter mezzes NEXT to it a moment later.
        _breaks[name] = (now, live && prev.KillCredited);
        return !live && RemoveTarget(name);
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
        var claimable = _breaks.TryGetValue(name, out var prev)
            && now - prev.At <= BreakWindow && !prev.KillCredited;
        _breaks[name] = (now, true);
        return !claimable && RemoveTarget(name);
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
