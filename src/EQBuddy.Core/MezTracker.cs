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

    private readonly Dictionary<string, MezSpellInfo> _catalog;
    private readonly Dictionary<string, double> _learned = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MezState> _active = [];
    private readonly List<(string Caster, string Spell, DateTime Time)> _recentCasts = [];
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
                case DamageDealtEvent dd:
                    changed = RemoveTarget(dd.Target);
                    break;
                case ThirdMeleeEvent tm:
                    // Damage TO the target breaks it; the target ATTACKING proves it woke.
                    changed = RemoveTarget(tm.Target) | RemoveTarget(tm.Attacker);
                    break;
                case ThirdDotEvent td:
                    changed = RemoveTarget(td.Target);
                    break;
                case ThirdSchoolEvent tsch:
                    changed = RemoveTarget(tsch.Target) | RemoveTarget(tsch.Attacker);
                    break;
                // The creature acting proves it's awake — but a DoT tick doesn't count:
                // a dot cast before the mez keeps ticking on you while the mob sleeps.
                case DamageTakenEvent { Self: false, OverTime: false } dt:
                    changed = RemoveTarget(dt.Attacker);
                    break;
                case KillEvent k:
                    changed = RemoveTarget(k.Target);
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

    private bool RemoveTarget(string target)
    {
        // ONE entry per break, not all (issue #32): with two mezzed "orc pawn"s, the
        // tank hitting one must not erase the other's chip. The log can't say which
        // woke, so drop the earliest-expiring — the least-harmful guess, and a wrong
        // one self-corrects on the next damage line.
        var name = LogParser.Normalize(target);
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
