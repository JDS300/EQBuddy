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
/// the caster's EQBuddy can measure real durations; it keeps a bounded set of land→fade
/// SAMPLES per exact spell name (rank included — ranks lengthen mezzes) and estimates the
/// duration from the cluster they form (<see cref="Effective"/>). It used to keep the
/// LONGEST gap instead, on the reasoning that "early breaks shorten gaps but nothing
/// lengthens them". That reasoning is wrong twice over, and it was reported from play: the
/// store read "Mesmerization V = 43" and the chip started at 0:43 for a spell that runs
/// about 38. Log-flush jitter of a second or two makes a maximum over many samples a reading
/// of the tail by construction, with no way back down — replaying the fixture through the
/// previous code learns 42 for that rank, against a sample cluster of 36-42 whose mode is 38.
/// And a fade cannot always be attributed: with two same-named entries of yours open, the
/// gap can be measured from the wrong landing (see the ambiguity gate in
/// <see cref="OnWornOff"/>). Learned values persist via <see cref="AttachStore"/> and flow to
/// the rest of the group through catalog updates.
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
    /// <summary>Unambiguous fade measurements kept per exact (ranked) spell name, newest
    /// preferred. Bounded so the store cannot grow without limit, and so that evidence from
    /// a server-side duration change (or a spell the player has since re-ranked) ages out
    /// instead of outvoting reality forever. 64 because samples arrive slowly: the fixture
    /// holds 1,197 caster-private fade lines across all ranks and yields 12 usable samples
    /// for Mesmerization V in a week of play — everything else is a twin fade, a gap under
    /// 4s, or a fade the break-retraction correctly threw away — so 64 is roughly a month,
    /// long enough for a stable mode and short enough to forget a stale one. It also keeps
    /// the store at a few hundred bytes per spell.</summary>
    public static readonly int SampleCap = 64;
    /// <summary>Fewer samples than this and the estimate falls back to the longest one seen
    /// — see <see cref="Effective"/>.</summary>
    public static readonly int ModeMinimumSamples = 8;
    /// <summary>A modal value seen fewer times than this is a coincidence, not a mode, and
    /// the estimate falls back to the longest sample — see <see cref="Effective"/>.</summary>
    public static readonly int ModeMinimumRepeats = 3;
    /// <summary>The samples are a mixture of early breaks (low) and natural fades (clustered
    /// at the truth); only samples at or above this fraction of the set's high-water mark
    /// vote — see <see cref="Effective"/>.</summary>
    public static readonly double ClusterFloor = 0.7;
    /// <summary>The high-water mark the cluster floor is measured from: this percentile of
    /// the samples, NOT the maximum, so one freak reading cannot drag the floor above the
    /// real cluster — see <see cref="Effective"/>.</summary>
    public static readonly double ClusterAnchor = 0.9;

    private readonly Dictionary<string, MezSpellInfo> _catalog;
    /// <summary>Per exact (ranked) spell name: the unambiguous land→fade gaps measured for
    /// it, oldest first, capped at <see cref="SampleCap"/>. The estimate is derived from
    /// these on demand (<see cref="Effective"/>) rather than stored, because a single
    /// remembered number cannot be voted against.</summary>
    private readonly Dictionary<string, List<double>> _samples = new(StringComparer.OrdinalIgnoreCase);
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
    /// <summary>Samples taken from a fade that has not yet outlived <see cref="BreakWindow"/>,
    /// with the value recorded. See <see cref="OnWornOff"/> for why a measurement is only
    /// provisional until the next few seconds of log prove nothing hit the creature.</summary>
    private readonly List<(string Name, string Spell, DateTime At, double Sample)> _provisional = [];
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

    /// <summary>Loads the learned SAMPLES and saves after each new one — same pattern as
    /// SpellCatalog's store; tests don't attach one. The file holds the samples themselves
    /// (<c>{"Mesmerization V": [38, 37, 38, ...]}</c>), not the number derived from them: a
    /// store that kept only the answer has nothing for the next fade to vote against, and
    /// would drift back to whatever it last wrote.
    ///
    /// Migration from the pre-mode format (<c>{"Mesmerization V": 43}</c>, one scalar per
    /// spell): the scalars are READ AND DISCARDED, not seeded as samples. A stored scalar is
    /// the longest gap ever observed under a rule that could only ratchet upward — the
    /// reported store said 43s for a spell that runs ~38 — and seeding it would not be a
    /// harmless single vote: below <see cref="ModeMinimumSamples"/> the estimate is the
    /// MAXIMUM, so the old inflated number would win every comparison for the whole warm-up.
    /// Replaying the reporter's week-long log yields 12 usable samples for the rank they cast
    /// constantly, so seeding would mean serving the exact reported bug for about a week and
    /// then having it outvoted anyway. Discarding costs the other direction and much less of
    /// it: until the first clean fade of a spell arrives — one or two a session in that log —
    /// its chip falls back to the catalog's shorter guess. A scalar file must still LOAD,
    /// though: it is on every existing user's disk, so the store is parsed element by element
    /// and anything that is not an array of numbers is skipped rather than thrown on.</summary>
    public void AttachStore(string path)
    {
        _storePath = path;
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            lock (_lock)
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Array) continue;   // old scalar: dropped
                    var samples = prop.Value.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => v.GetDouble())
                        .Where(s => s is > 3 and < 600)
                        .ToList();
                    // A store written by a future build with a bigger cap keeps its NEWEST
                    // samples here, matching the eviction rule in OnWornOff.
                    if (samples.Count > SampleCap) samples.RemoveRange(0, samples.Count - SampleCap);
                    if (samples.Count > 0) _samples.TryAdd(prop.Name, samples);
                }
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

    /// <summary>Learned durations (exact spell name → seconds), for display/export.
    /// Derived from the samples on read — see <see cref="Effective"/>.</summary>
    public IReadOnlyDictionary<string, double> LearnedDurations
    {
        get
        {
            lock (_lock)
                return _samples.Where(kv => kv.Value.Count > 0).ToDictionary(
                    kv => kv.Key, kv => Effective(kv.Value), StringComparer.OrdinalIgnoreCase);
        }
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
        var mine = _active
            .Where(m => m.Caster.Equals("You", StringComparison.OrdinalIgnoreCase)
                && m.Target.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.LandedAt)
            .ToList();
        // No entry of yours: the fade belongs to a mez we never tracked, and it must not
        // touch the window either — stamping it would let YOUR fade swallow the break of
        // somebody else's chip on a same-named creature.
        if (mine.Count == 0) return false;
        var entry = mine[0];

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
        // real length is ~38s. The estimator cannot save us from that on its own: a break
        // gap is a legitimate-looking number, and on a thin sample set Effective() returns
        // the maximum, so 7s would simply BE the answer and every chip cast from it would
        // count down to a wake-up half a minute early.
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
        // whether or not the damage broke them, and on a melee-heavy log that is most of
        // them: replaying the fixture, 401 of the 417 samples this method records are
        // retracted within the window. The survivors are the quiet, full-duration fades and
        // they are clean — Mesmerization V's twelve read 36-42 with no break contamination
        // at all — but the filter is blunt enough to starve a rank entirely. Mesmerization IV
        // ends the fixture with ZERO samples (13 unambiguous fades, every one followed by a
        // blow on that creature inside the window; gaps 4 7 8 20 23 23 28 28 30 32 33 35 42,
        // which do look like breaks), so its chips fall back to the catalog. That is not new
        // — the previous code learned nothing for IV from this log either — but it is the
        // price of the rule, and the place to look if a rank never learns.
        //
        // AMBIGUITY GATE. The fade line names a creature, not a mez — and EQ logs it without
        // even the rank — so with two of your own entries open on that name there is nothing
        // in the log that says which one ended. The pick above (longest asleep) still has to
        // remove SOMETHING, because a mez of yours genuinely ended; but as a MEASUREMENT it
        // is a guess, and a guess that fails in one direction: when the YOUNGER of two
        // same-named entries fades, the gap is credited to the older one and inflated by the
        // whole stagger between the landings. (Those pairs come from an AoE catching twins in
        // one second — OnLanding's refresh rule collapses staggered landings on one name into
        // a single entry — after which a re-mez refreshes one of the pair and staggers them.)
        //
        // Measured over the 690k-line fixture: of 1,197 caster-private fade lines, 363 arrive
        // with two or more of your entries open. Their gaps are nothing like the clean ones —
        // for Mesmerization V they are 0s x114, 1s x88, 2s x33, i.e. mostly AoE twins broken
        // the instant they land, with a long scatter above. Gating them out costs 3 of the
        // rank's 15 samples and moves the estimate from 39 to 38, and one of the three
        // discarded was a 23s reading against a cluster of 36-42: exactly the attribution
        // error, just not the dominant term. So: chip goes, sample does not.
        if (mine.Count > 1) return true;

        // One sample per unambiguous fade, whole seconds (the log's own resolution — a
        // land→fade gap is always an integer count of log seconds). Bounded and oldest-first
        // evicted: see SampleCap. The estimator over these is Effective().
        var observed = Math.Round((wo.Time - entry.LandedAt).TotalSeconds);
        if (observed is > 3 and < 600)
        {
            if (!_samples.TryGetValue(entry.Spell, out var samples))
                _samples[entry.Spell] = samples = [];
            samples.Add(observed);
            if (samples.Count > SampleCap) samples.RemoveAt(0);
            _provisional.Add((name, entry.Spell, wo.Time, observed));
            SaveStore();
        }
        return true;
    }

    /// <summary>Undoes any fade measurement for <paramref name="name"/> still inside its
    /// <see cref="BreakWindow"/>: a break signal this close behind the fade means the fade
    /// WAS that break, so the gap it measured is the time to the break, not the duration.
    /// Newest first, pulling out the newest sample of that exact value, so stacked
    /// measurements on one spell unwind to exactly the sample set that was there before.
    /// (The one thing that cannot be undone is an eviction the retracted sample caused —
    /// the oldest sample of a full set is gone for good. Worth nothing: it is one vote in
    /// <see cref="SampleCap"/>, and only for spells whose sample set is already full.)</summary>
    private void RetractProvisional(string name, DateTime now)
    {
        var undone = false;
        for (var i = _provisional.Count - 1; i >= 0; i--)
        {
            var p = _provisional[i];
            if (now - p.At > BreakWindow
                || !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (_samples.TryGetValue(p.Spell, out var samples))
            {
                var at = samples.LastIndexOf(p.Sample);
                if (at >= 0) samples.RemoveAt(at);
                // An empty set is removed outright so the spell reads as never-measured
                // rather than as measured-and-unknown.
                if (samples.Count == 0) _samples.Remove(p.Spell);
            }
            _provisional.RemoveAt(i);
            undone = true;
        }
        if (undone) SaveStore();
    }

    /// <summary>
    /// The duration a set of unambiguous fade measurements implies: the MODE of the upper
    /// cluster — not the maximum, and not the mode of everything.
    ///
    /// WHY NOT THE MAXIMUM. Reported from play: the store read "Mesmerization V = 43" and
    /// the chip started at 0:43 for a spell that runs about 38. Nothing was wrong with the
    /// measurements; the estimator was. With a second or two of log-flush jitter on every
    /// reading, a maximum over hundreds of samples is a reading of the TAIL by construction,
    /// it only ever ratchets upward, and one freak value is permanent. Replaying the fixture
    /// through this tracker, the surviving Mesmerization V samples are
    /// 36 37 38 38 38 38 39 39 39 40 41 42 — max 42, mode 38. The user is right and the tail
    /// is noise.
    ///
    /// WHY NOT A PLAIN MODE EITHER. The samples are a MIXTURE of two populations, not one
    /// cluster: natural fades sit tightly at the true duration but spread over three or four
    /// adjacent values (jitter), while early breaks — a mez cut short, whose breaking blow
    /// the retraction window in <see cref="RetractProvisional"/> did not catch — pile up at
    /// the very bottom and repeat EXACTLY, because "4 seconds" is a common way for a mez to
    /// die. With hundreds of samples the fade cluster still wins (the fixture's unambiguous
    /// Mesmerization V, n=378 before retraction: mode 38 x37). With a few dozen it does not.
    /// The same log's Mesmerization III, n=26 before retraction, reads
    /// 4 4 4 4 6 11 11 13 14 15 16 16 19 27 29 29 30 31 32 33 34 34 34 35 36 36:
    /// its most common single value is FOUR SECONDS. A chip claiming 4s for a ~36s mez is a
    /// far worse bug than the 43s it replaced — it would tell the player their mez is about
    /// to break, constantly.
    ///
    /// SO: vote only among samples at or above <see cref="ClusterFloor"/> of the set's
    /// high-water mark, then take the mode of those. The floor is measured from the
    /// <see cref="ClusterAnchor"/> percentile rather than the maximum, so a single freak
    /// long reading cannot drag the floor above the real cluster and leave the estimator
    /// voting on the outlier alone. 70% because the two populations are far apart in exactly
    /// that region: a rank-lengthened mez varies by a couple of seconds, while break gaps
    /// scatter from 0 up. On the fixture's pre-retraction sets (the hard case — full
    /// contamination) this yields V 38, III 34, IV falls through to the max at 42; on the
    /// clean post-retraction sets it yields V 38 and changes nothing else.
    ///
    /// TIE-BREAK: the LONGER value. The contamination is one-directional — a break gap is
    /// always SHORTER than the spell, while nothing but flush jitter can make a gap longer —
    /// so of two equally common values the shorter is the likelier artefact. Erring long is
    /// also the cheaper error in the UI: a chip that reads a second past the wake-up is
    /// corrected by the break line the moment anything touches the creature, while a chip
    /// that expires early says a sleeping mob is awake and invites a re-mez that lands as a
    /// resist.
    ///
    /// FALL BACK TO THE MAXIMUM (today's behaviour) on thin evidence — under
    /// <see cref="ModeMinimumSamples"/> samples, or when the winning value is not seen at
    /// least <see cref="ModeMinimumRepeats"/> times, which is the honest reading of "there
    /// is no mode here": the fixture's Mesmerization IV has 13 pre-retraction samples whose
    /// upper band is 28 28 30 32 33 35 42 — the best it can offer is a value seen twice, so
    /// the max (42) is the more honest answer than a coin-flip mode. NOT the catalog:
    /// it says Mesmerization = 24s (the base rank's number — ranks lengthen mezzes and most
    /// are unresearched) against a real ~38, so "mode when there is evidence, catalog before
    /// that" would make every chip 14 seconds SHORT through the whole warm-up and after
    /// every store reset — silent, and worse than the bug being fixed. Longest-of-few is
    /// biased high but self-limiting, and being a little long never leaves a sleeping mob
    /// unattended.
    /// </summary>
    private static double Effective(List<double> samples)
    {
        if (samples.Count < ModeMinimumSamples) return samples.Max();
        var sorted = samples.Order().ToList();
        var anchor = sorted[(int)Math.Ceiling(ClusterAnchor * sorted.Count) - 1];
        var winner = sorted
            .Where(s => s >= ClusterFloor * anchor)
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .First();
        return winner.Count() >= ModeMinimumRepeats ? winner.Key : samples.Max();
    }

    private double? DurationFor(string spell) =>
        _samples.TryGetValue(spell, out var s) && s.Count > 0 ? Effective(s)
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
            File.WriteAllText(path, JsonSerializer.Serialize(_samples));
        }
        catch { /* best-effort; in-memory learning still works */ }
    }
}
