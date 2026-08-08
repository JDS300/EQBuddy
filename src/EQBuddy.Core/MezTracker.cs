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
/// SAMPLES per exact spell name (rank included — ranks lengthen mezzes), finds the cluster
/// they form, and snaps that down to the 6-second server tick (<see cref="Effective"/>).
/// It used to keep the
/// LONGEST gap instead, on the reasoning that "early breaks shorten gaps but nothing
/// lengthens them". That reasoning is wrong twice over, and it was reported from play: the
/// store read "Mesmerization V = 43" and the chip started at 0:43 for a spell that runs
/// about 38. Log-flush jitter of a second or two makes a maximum over many samples a reading
/// of the tail by construction, with no way back down — replaying the fixture through the
/// previous code learns 42 for that rank, against a sample cluster of 36-42 whose mode is 38.
/// And a fade cannot always be attributed: with two same-named entries of yours open, the
/// gap can be measured from the wrong landing (see the ambiguity gate in
/// <see cref="OnWornOff"/>). Two further guards come from the same family of reports and
/// survive here: an observation SHORTER than the catalog base is a break, not a duration
/// (the {"Mesmerize": 7} incident — see <see cref="OnWornOff"/>), and legacy scalar stores
/// are quarantined on load (see <see cref="AttachStore"/>). Learned values persist via
/// <see cref="AttachStore"/> and flow to the rest of the group through catalog updates.
///
/// Breaks: a creature is believed awake once a damage/kill line is credited against it, and
/// two rules keep one woken creature from erasing its sleeping same-named siblings — the
/// engagement window (<see cref="CreditBreak"/>, which collapses the several lines of one
/// attack round into one break) and the awake ledger (<see cref="CreditKill"/> and
/// <see cref="OnLanding"/>, which settle the woken creature's death or re-mez without
/// touching a sleeper's chip).
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
    /// <see cref="CreditBreak"/> for the measurements and the reasoning. This governs CHIP
    /// REMOVAL only; it deliberately says nothing about learning (see <see cref="OnWornOff"/>
    /// for why the 1.31.2 rule that used it to retract measurements was withdrawn).</summary>
    public static readonly TimeSpan BreakWindow = TimeSpan.FromSeconds(6);
    /// <summary>How long a creature already known to be awake keeps explaining lines for its
    /// name in the awake LEDGER (issue #35). The ledger is not what dedupes damage — the 6s
    /// <see cref="BreakWindow"/> does that, and it has to stay short so a genuine break of a
    /// second twin a quarter-minute later is still seen. What the ledger survives for is the
    /// two events that SETTLE a woken creature rather than break a new one: its death
    /// (<see cref="CreditKill"/>) and its re-mez (<see cref="OnLanding"/>), both of which can
    /// arrive long after the last blow.</summary>
    public static readonly TimeSpan AwakeMemory = TimeSpan.FromSeconds(45);
    /// <summary>EQ effects run on 6-second server ticks and the worn-off message fires at the
    /// tick BOUNDARY, so a land→fade gap is the true duration plus up to a tick of message
    /// lag — and true durations are tick multiples. Applied to the ESTIMATE rather than to
    /// each raw sample (see <see cref="Effective"/>), and used to heal legacy scalar stores
    /// (<see cref="AttachStore"/>).</summary>
    public const double ServerTickSeconds = 6;
    /// <summary>Unambiguous fade measurements kept per exact (ranked) spell name, newest
    /// preferred. Bounded so the store cannot grow without limit, and so that evidence from
    /// a server-side duration change (or a spell the player has since re-ranked) ages out
    /// instead of outvoting reality forever. The cap matters more than it used to: with
    /// 1.31.2's retraction withdrawn the sets are ~25x larger (the 690k-line fixture records
    /// 418 samples across all ranks where 16 survived before), so the BOUND, not a filter, is
    /// what ages evidence out. 64 is about two days of heavy play for the rank an enchanter
    /// spams — the fixture's week yields 378 Mesmerization V samples — which is enough for a
    /// stable mode and short enough to forget a stale one, and it keeps the store at a few
    /// hundred bytes per spell. Raising it would not sharpen the estimate: the mode of the
    /// upper cluster is already stable at 38 over both the newest 64 and all 378.</summary>
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

    /// <summary>What is known about the creatures answering to one NAME.
    ///
    /// <c>At</c> — when a break signal for the name was last seen at all, counted or not.
    /// The 6-second <see cref="BreakWindow"/> runs from here and slides, so one continuous
    /// engagement costs exactly one chip however long it runs (<see cref="CreditBreak"/>).
    ///
    /// <c>Removed</c> — when a damage/kill signal last actually TOOK a chip for the name.
    /// Separate from <c>At</c> because a live engagement window is not the same thing as a
    /// break that has been counted: in the reported log the group beats on one creature for
    /// 36 seconds without a 6s gap, so the window never lapses, yet only the first of those
    /// lines ever cost a chip. <see cref="OnWornOff"/> yields to a break that was counted,
    /// not to a window that is merely open.
    ///
    /// <c>Awake</c>/<c>AwakeAt</c> — the awake ledger (issue #35): how many creatures of the
    /// name are believed to be up and fighting, and when that belief was last touched. Only
    /// a break that actually took a chip adds one, so a fight on a name nothing has mezzed
    /// never populates it. A kill spends one; a re-mez spends one.</summary>
    private readonly record struct Engagement(
        DateTime At, DateTime Removed, int Awake, DateTime AwakeAt);

    // No AA correction on purpose: the full eqlwiki AA sweep (2026-08-06, AaCatalog)
    // found NO EQ Legends AA that extends detrimental mez/charm durations — unlike live
    // EQ's Mesmerization Mastery. Adamant Will only moves resist chance, which never
    // shifts a landed mez's clock. Learned durations here are therefore character-true
    // without reading the AA ledger. (Beneficial-duration AAs like Spell Casting
    // Reinforcement matter to future BUFF countdowns, not to this tracker.)

    private readonly Dictionary<string, MezSpellInfo> _catalog;
    /// <summary>Per exact (ranked) spell name: the unambiguous land→fade gaps measured for
    /// it, oldest first, capped at <see cref="SampleCap"/>. The estimate is derived from
    /// these on demand (<see cref="Effective"/>) rather than stored, because a single
    /// remembered number cannot be voted against.</summary>
    private readonly Dictionary<string, List<double>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MezState> _active = [];
    private readonly List<(string Caster, string Spell, DateTime Time)> _recentCasts = [];
    /// <summary>Per creature name, keyed on log time so replay stays deterministic.</summary>
    private readonly Dictionary<string, Engagement> _engagements = new(StringComparer.OrdinalIgnoreCase);
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
    /// Stored samples are re-screened on load with the same guard the learner applies
    /// (<see cref="OnWornOff"/>): anything under the catalog base is a break length, not a
    /// duration, so a file written before that guard existed heals itself on next launch —
    /// the {"Mesmerize": 7} incident, where one early break shrank every chip on the machine.
    ///
    /// LEGACY SCALAR STORES (<c>{"Mesmerization V": 43}</c>, one number per spell) still have
    /// to LOAD without throwing — they are on every existing user's disk — so the store is
    /// parsed element by element. They are not, however, admitted as evidence. A stored
    /// scalar is the longest gap ever observed under a rule that could only ratchet upward,
    /// so it is wrong in both directions at once: too long when the ratchet caught the
    /// log-flush tail (the reported store said 43s for a spell that runs ~38, and up to a
    /// server tick of that is worn-off message lag), and far too short when the very first
    /// observation happened to be a break. Seeding it would not be a harmless single vote
    /// either: below <see cref="ModeMinimumSamples"/> the estimate is the MAXIMUM, so the old
    /// number would win every comparison for the whole warm-up — about a week of play in the
    /// reporter's log before it was outvoted.
    ///
    /// So a scalar is kept only in the one case where keeping it claims nothing: when
    /// tick-flooring the message lag off it (<see cref="ServerTickSeconds"/>) lands exactly on
    /// the catalog's own duration for the spell. Then it is not a measurement at all, merely a
    /// confirmation, and it costs nothing to carry until real samples arrive. Anything above
    /// that is the inflated ratchet and anything below it is a break recorded as a duration;
    /// both are dropped and re-learned from play, which costs one or two fades a session.</summary>
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
                    var floor = CatalogBase(prop.Name);
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        // Pre-1.31.3 scalar: healed, then kept only if it merely restates
                        // the catalog (see the summary above).
                        var ticked = Math.Floor(prop.Value.GetDouble() / ServerTickSeconds)
                            * ServerTickSeconds;
                        if (floor > 0 && Math.Abs(ticked - floor) < 0.0001)
                            _samples.TryAdd(prop.Name, [floor]);
                        continue;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                    var samples = prop.Value.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => v.GetDouble())
                        .Where(s => s is > 3 and < 600 && s >= floor)
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
                    _engagements.Clear();
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
                    kv => kv.Key, kv => Effective(kv.Key, kv.Value), StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool IsMezSpell(string spell) =>
        _catalog.ContainsKey(SpellCatalog.BaseName(spell));

    /// <summary>The catalog's duration for a spell's base (unranked) name, or 0 when the
    /// spell is unresearched. Ranks only ever LENGTHEN a mez, so this is a hard floor on any
    /// honest measurement of any rank of it.</summary>
    private double CatalogBase(string spell) =>
        _catalog.TryGetValue(SpellCatalog.BaseName(spell), out var info)
            ? info.DurationSeconds ?? 0 : 0;

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

        // An AWAKE creature of this name getting mezzed is the classic re-mez after a break
        // (issue #35): spend its awake-ledger entry and ADD a chip. It must NOT go through
        // the refresh rule below, which would consume a still-sleeping sibling's chip and
        // report one mez where there are now two.
        if (_engagements.TryGetValue(entry.Target, out var eng) && eng.Awake > 0
            && mez.Time - eng.AwakeAt <= AwakeMemory)
        {
            var left = eng.Awake - 1;
            _engagements[entry.Target] = eng with { Awake = left, AwakeAt = mez.Time };
            _active.Add(entry);
            return true;
        }

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

        _engagements.TryGetValue(name, out var prev);
        var counted = wo.Time - prev.Removed <= BreakWindow;
        var live = wo.Time - prev.At <= BreakWindow;
        var awake = wo.Time - prev.AwakeAt <= AwakeMemory ? prev.Awake : 0;
        _engagements[name] = new Engagement(
            wo.Time, live ? prev.Removed : default, awake, awake > 0 ? prev.AwakeAt : default);
        if (counted) return false;   // the blow that broke this mez already cost the chip
        _active.Remove(entry);

        // Learning. The sample is taken here and KEPT — nothing downstream can retract it.
        //
        // 1.31.2 made it provisional for BreakWindow instead: a fade measures the real
        // duration only when the mez ended on its own, a fade caused by damage measures the
        // time to the BREAK ("Mesmerization V = 7" off the trace above, against a real ~38s),
        // and the tell arrives AFTER the fade — the bash in the next line, not the engagement
        // eight seconds earlier. That was correct against the estimator OF THE TIME, which
        // was "longest observed wins": one 7s reading on a fresh store simply WAS the answer.
        //
        // 1.31.3 replaced that estimator with the mode of the upper cluster (see Effective),
        // which rejects break-shortened samples on its own — a short gap falls below the
        // cluster floor and never votes. The retraction became a second filter for a problem
        // already solved, and it is a ruinously expensive one: replaying the fixture, it
        // discarded 402 of the 418 samples this method records — the whole log ended with 16.
        // A full play session taught nothing at all. And Mesmerization IV could never learn
        // at all — every one of its 13 unambiguous fades is followed by damage inside the
        // window — so its chips were stuck on the catalog's 24s for a spell that runs ~40.
        // Without the retraction the same log puts it at 42.
        //
        // And it could not be salvaged by tightening the window, because the fade→damage
        // delay carries no information. Measured over the fixture, splitting unambiguous
        // fades by how long the mez had held before it ended:
        //
        //   SHORT hold (<=12s, almost certainly broken)   n=43    delay 0s in 40 of 43
        //   LONG  hold (>=25s, plausibly a natural fade)  n=145   delay 0s in 97 of 145
        //
        // Zero in BOTH populations. Whenever a mez ends — broken or expired — the group is on
        // the mob in the same second, because that is the whole point of watching the chip.
        // No window size separates them. What does separate them is the HOLD DURATION: breaks
        // are short and scattered, natural fades cluster at the truth. That is precisely the
        // signal Effective() keys on, so the job belongs there and the filter is gone.
        //
        // Do not reinstate it. The cost is measured above; the benefit is now zero. Note that
        // the 1.30.0 "brokeRecently" guard (don't teach when the awake ledger for this name
        // was touched in the last 3s) is the same filter under another name, and is likewise
        // absent for the same measured reason. What DID survive from 1.30.0 is the base-floor
        // guard below, which needs no timing at all.
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
        //
        // RAW, deliberately — the server-tick snap belongs to the ESTIMATE, not to the
        // sample. Upstream's 6f819fc floored each observation here instead
        // (Math.Floor(gap / ServerTickSeconds) * ServerTickSeconds), which is right about
        // the physics and wrong about where to apply it: it collapses a set like
        // 36 37 38 39 41 42 43 onto 36 and 42 before Effective() ever sees it, and the
        // cluster the estimator exists to find is gone. Measured against this file's tests,
        // flooring here fails eight of them and flooring the estimate fails none. Keep the
        // log's own resolution in the sample set; see Effective() for the snap and for why
        // it is applied downward.
        var observed = Math.Round((wo.Time - entry.LandedAt).TotalSeconds);
        // The one 1.30.0 learning guard that composes cleanly and is kept (4f133af, David's
        // live report): a gap SHORTER than the catalog's base duration is a break, not a
        // duration — ranks only ever lengthen a mez — so it is not a measurement of anything.
        // One 7s "Mesmerize" learned this way shrank every chip on his machine. Unlike the
        // damage-proximity filters above this needs no timing and discards only samples the
        // cluster estimator would have outvoted anyway, so it costs nothing and closes the
        // hole the estimator leaves on a nearly-empty store, where the maximum of one bad
        // sample simply IS the answer.
        if (observed is > 3 and < 600 && observed >= CatalogBase(entry.Spell))
        {
            if (!_samples.TryGetValue(entry.Spell, out var samples))
                _samples[entry.Spell] = samples = [];
            samples.Add(observed);
            if (samples.Count > SampleCap) samples.RemoveAt(0);
            SaveStore();
        }
        return true;
    }

    /// <summary>
    /// The duration a set of unambiguous fade measurements implies: the mode of the upper
    /// cluster (<see cref="Cluster"/>), snapped DOWN to the server tick.
    ///
    /// The two halves of this were developed independently — the cluster by the fork, the
    /// tick snap by upstream (6f819fc, Aenari's report) — and they compose exactly, provided
    /// the snap is applied HERE, to the estimate, and not to each sample as it is recorded.
    /// Flooring the samples themselves would collapse a set like 36 37 38 39 41 42 43 onto
    /// two values, 36 and 42, destroying the very structure <see cref="Cluster"/> exists to
    /// read; measured, that costs eight of this file's regression tests. Flooring the answer
    /// costs none of them, because the cluster is found first and only its result is snapped.
    ///
    /// WHY DOWN, AND WHY THIS IS ALSO THE RIGHT ANSWER FOR THE FORK'S OWN REPORT. Log-flush
    /// and message lag are ONE-DIRECTIONAL: the worn-off line can only arrive late, never
    /// early, so an observed gap can only ever be LONGER than the truth. The true duration is
    /// therefore at the BOTTOM of a clean fade cluster, not at its mode. The fixture set
    /// quoted below for Mesmerization V runs
    /// 36 36 36 36 36 37 37 37 37 38x8 39x5 41 41 42x7 43 43: the fork's estimator answers 38,
    /// yet nine samples sit BELOW that at 36-37, and no amount of lag explains an observation
    /// shorter than the truth. So 38 overshoots by about two seconds — which is precisely
    /// Aenari's report, and the dangerous direction: a chip that still shows time on a mob
    /// that has already woken blindsides the chanter, while one that expires a couple of
    /// seconds early merely invites an early re-mez. Snapping the mode down to the tick lands
    /// on the cluster's floor in every case the fork measured: V's 38 → 36, III's 36 → 36,
    /// IV's 42 → 42. And upstream's single-fade cases fall out for free, because with one
    /// sample the cluster mode IS that sample: 32 → 30, 44 → 42.
    ///
    /// The guard: a snap must never manufacture a shorter chip than the catalog already
    /// promises, and must never reach zero. Under a tick, or under the catalog base, the
    /// catalog base wins — a spell whose only evidence is a handful of 4-second breaks must
    /// not produce a 0:00 chip. With no catalog duration at all (an unresearched spell) there
    /// is nothing to fall back to, so the unsnapped cluster stands.
    /// </summary>
    private double Effective(string spell, List<double> samples)
    {
        var estimate = Cluster(samples);
        var snapped = Math.Floor(estimate / ServerTickSeconds) * ServerTickSeconds;
        var floor = CatalogBase(spell);
        if (snapped >= ServerTickSeconds && snapped >= floor) return snapped;
        return floor > 0 ? floor : estimate;
    }

    /// <summary>
    /// The cluster the measurements form: the MODE of the upper
    /// cluster — not the maximum, and not the mode of everything. Raw, unsnapped; the tick
    /// snap is applied to this result by <see cref="Effective"/>.
    ///
    /// This is the main thing standing between a break-shortened sample and the chip: since
    /// 1.31.3 withdrew the break-retraction (see <see cref="OnWornOff"/>) only the catalog
    /// base-floor filters the sample set, so every early break long enough to clear that
    /// floor is in here and has to be OUTVOTED. Sizing the rule against contaminated sets is
    /// therefore not a hard case any more — it is the case.
    ///
    /// WHY NOT THE MAXIMUM. Reported from play: the store read "Mesmerization V = 43" and
    /// the chip started at 0:43 for a spell that runs about 38. Nothing was wrong with the
    /// measurements; the estimator was. With a second or two of log-flush jitter on every
    /// reading, a maximum over hundreds of samples is a reading of the TAIL by construction,
    /// it only ever ratchets upward, and one freak value is permanent — and now that breaks
    /// are kept, the maximum would ALSO have to be lucky enough never to see a long ambiguous
    /// reading. Replaying the fixture through this tracker, Mesmerization V's retained set
    /// (the newest 64 of 378 samples) runs 4 … 36 36 36 36 36 37 37 37 37 38 38 38 38 38 38
    /// 38 38 39 39 39 39 39 41 41 42 42 42 42 42 42 42 43 43 — max 43, upper-cluster mode 38.
    /// The user is right and the tail is noise.
    ///
    /// WHY NOT A PLAIN MODE EITHER. The samples are a MIXTURE of two populations, not one
    /// cluster: natural fades sit tightly at the true duration but spread over three or four
    /// adjacent values (jitter), while early breaks pile up at the very bottom and repeat
    /// EXACTLY, because "4 seconds" is a common way for a mez to die. With hundreds of
    /// samples the fade cluster still wins (the fixture's unambiguous Mesmerization V,
    /// n=378: mode 38). With a few dozen it does not. The same log's Mesmerization III,
    /// n=27, reads in full
    /// 4 4 4 4 6 11 11 13 14 15 16 16 19 27 29 29 30 31 32 33 34 34 34 35 36 36 36:
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
    /// scatter from 0 up. Over the full fixture this yields Mesmerization V 38, III 36, and
    /// IV 42 (falling through to the max — see below). V is 38 both with the 1.31.2 retraction
    /// in front of it and without: the filter was never what produced the right answer.
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
    /// is no mode here": the fixture's Mesmerization IV has 13 samples
    /// (4 7 8 20 23 23 28 28 30 32 33 35 42) whose upper band is 28 28 30 32 33 35 42 — the
    /// best it can offer is a value seen twice, so the max (42) is the more honest answer
    /// than a coin-flip mode. That 42 is also the rank that could not learn AT ALL under the
    /// retraction, and ~42 against a real ~40 is a far smaller error than the catalog's 24.
    /// NOT the catalog:
    /// it says Mesmerization = 24s (the base rank's number — ranks lengthen mezzes and most
    /// are unresearched) against a real ~38, so "mode when there is evidence, catalog before
    /// that" would make every chip 14 seconds SHORT through the whole warm-up and after
    /// every store reset — silent, and worse than the bug being fixed. Longest-of-few is
    /// biased high but self-limiting, and being a little long never leaves a sleeping mob
    /// unattended.
    /// </summary>
    private static double Cluster(List<double> samples)
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
        _samples.TryGetValue(spell, out var s) && s.Count > 0 ? Effective(spell, s)
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
    /// A break that actually takes a chip also records one creature of the name as AWAKE
    /// (issue #35). The ledger is not a second dedupe — the window above is what suppresses
    /// the rest of the round, and it is deliberately far shorter than
    /// <see cref="AwakeMemory"/> so that a genuine break of a second twin fifteen seconds
    /// later is still seen. What the ledger is for is the two lines that SETTLE the woken
    /// creature instead of breaking a new one: its death (<see cref="CreditKill"/>) and its
    /// re-mez (<see cref="OnLanding"/>).
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
        _engagements.TryGetValue(name, out var prev);
        var live = now - prev.At <= BreakWindow;
        var took = !live && RemoveTarget(name);
        var awake = now - prev.AwakeAt <= AwakeMemory ? prev.Awake : 0;
        if (took) awake++;   // only a break that cost a chip proves something woke up
        // Marked even when nothing was removed: the point is to recognise the engagement,
        // and a fight already in progress on one "orc pawn" must not eat the chip of the
        // one an enchanter mezzes NEXT to it a moment later. Removed, by contrast, moves
        // only when a chip actually went — that is what a fade yields to (see OnWornOff).
        _engagements[name] = new Engagement(
            now,
            took ? now : live ? prev.Removed : default,
            awake,
            awake > 0 ? now : default);   // the awake one is still fighting
        return took;
    }

    /// <summary>
    /// A kill line, which is genuinely one per creature — but arrives in the same second
    /// as the killing blow in 2,504 of the fixture's 2,624 kills (96%, 97.6% within 1s),
    /// so it is almost never NEW information: the damage that killed the creature has
    /// already been credited as its break. Counting it again would take a second chip,
    /// reproducing the reported bug at the end of every fight. So a kill spends an AWAKE
    /// creature of that name rather than removing anything — the dead one is the one that
    /// was fighting (issue #35) — and only a kill with nothing awake is a mezzed creature
    /// killed outright (an AoE nuke, or a blow nobody's log parsed), which drops its chip.
    /// Killing two mezzed adds back to back therefore clears both chips, at either
    /// creature's death, however tightly the deaths are packed (3.3% of same-name kill
    /// pairs are within 6s of each other, 13% within 20s).
    ///
    /// When the last awake creature of a name dies the ENGAGEMENT ends with it, so the
    /// break window closes too: the stream of damage lines the window exists to attribute
    /// has no creature left to come from, and the next line naming it is somebody new. That
    /// is what makes the reported #35 sequence come out right — break a golem, fight it,
    /// kill it, hit the sleeping twin: the last hit is a real break, not more of the dead
    /// one's round. The cost is a line of the SAME round arriving after its kill (a trailing
    /// damage-shield proc); those are rare next to the fights this gets right, and an
    /// over-eager break clears a chip that self-corrects, which is the cheap direction.
    /// </summary>
    private bool CreditKill(string target, DateTime now)
    {
        var name = LogParser.Normalize(target);
        _engagements.TryGetValue(name, out var prev);
        var awake = now - prev.AwakeAt <= AwakeMemory ? prev.Awake : 0;
        if (awake > 0)
        {
            var left = awake - 1;
            _engagements[name] = left > 0
                ? new Engagement(now, prev.Removed, left, now)
                : default;   // nothing of that name is up: the engagement is over
            return false;
        }
        var took = RemoveTarget(name);
        _engagements[name] = new Engagement(now, took ? now : default, 0, default);
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
        // Engagement records are only readable inside BreakWindow / AwakeMemory, so
        // sweeping them is pure housekeeping — done in a batch (like the _recentCasts cap)
        // rather than on every event, because a long session names thousands of creatures
        // and this runs per parsed line. A handful of names are in flight at once in real
        // combat.
        if (_engagements.Count > 32)
            foreach (var name in _engagements
                         .Where(e => now - e.Value.At > BreakWindow
                             && now - e.Value.AwakeAt > AwakeMemory)
                         .Select(e => e.Key).ToList())
                _engagements.Remove(name);
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
