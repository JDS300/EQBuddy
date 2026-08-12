# Derived durations, and the measurement that was one tick long

**Date:** 2026-08-12
**Status:** approved, ready for implementation
**Follows:** `2026-08-10-debuff-tracking-design.md` (slices 1 and 2, shipped at `54e9cf1`)

## Goal

A DoT or debuff whose duration has never been measured should still show a countdown, drawn
from the wiki's base duration scaled by the spell's rank — displayed as an estimate, and
discarded the moment a real measurement lands.

Along the way, fix the measurement path that reports every DoT one tick too long.

## Three tiers of trust, in this order

1. **Measured** — from the log. Authoritative. Always wins.
2. **Derived** — wiki base × rank multiplier. Marked `~`. Superseded by any measurement.
3. **Unknown** — `--`. Never a guessed number.

Measurement is never "corrected" toward the wiki. Tepid Deeds measures ~126s across four clean
samples while its wiki page says `2 Min 30 Sec` and contradicts itself in its own prose; the
measurement stands.

## The duration formula

    duration = base × (1 + 0.10 × tier)

`tier` is the trailing Roman numeral of the cast name. Additive, not compounding — `1.1^N`
yields no integer tier for the observed values.

Confirmed four ways, the last two reproduced from `spells.json` during design:

| Evidence | Expected | Computed |
|---|---|---|
| Shiftless Deeds VI shows 4 min in game | 240s | 150 × 1.6 = **240** ✓ |
| Wiki Spell Level slider at 6 reads "Duration +60%" | +60% | +60% ✓ |
| Mesmerization rank V measures ~36s | 36s | 24 × 1.5 = **36** ✓ |
| Shiftless Deeds IV | — | 150 × 1.4 = 210 |

The slider's modifier table is applied client-side and is not in the page wikitext,
`Template:Spellpage`, `Template:Spellpagesmart`, `MediaWiki:Common.js` or
`MediaWiki:BlueprintLoader.js`. All were checked. The formula above removes the need for it.
**Do not go looking for it again.**

## Constraint: the rank appears on cast lines only

Measured across the 690k-line log:

| Line | Carries rank? | Evidence |
|---|---|---|
| `You begin casting <Spell>.` | **yes** | `Mesmerization V` ×630, `Shiftless Deeds IV` ×155 |
| `<mob> has taken N damage from your <Spell>.` | no | 0 of all tick lines carry a numeral |
| `Your <Spell> spell has worn off of <mob>.` | no | `Mesmerization` ×1197, unranked |
| `<mob> slows down.` | n/a | names no spell at all |

So rank is metadata captured at cast and carried on the effect; every other line keys by base
name. A tick with no cast to explain it has an unknown rank, and therefore shows `--` — not a
tier-0 assumption, which would read 48s against a real 72s for a rank-V DoT and warn early
every cast.

## Part 1: the anchoring fix (do this first)

The original brief proposed re-anchoring DoT timers on `SpellCastEvent`, on the theory that the
first tick lands ~6s after the cast. **The log contradicts this.** Six DoTs with both fade lines
and an exact wiki duration, medians over 138 completed casts:

| Spell | wiki | tick1→fade | cast→fade | current `+6` |
|---|---|---|---|---|
| Immolate | 48 | **48** ✓ | 53 | 54 |
| Drones of Doom | 48 | **48** ✓ | 53 | 54 |
| Gasping Embrace | 48 | **48** ✓ | 54 | 54 |
| Stinging Swarm | 54 | **54** ✓ | 59 | 60 |
| Vengeance of the Wild | 30 | **31** ✓ | 38 | 37 |
| Drifting Death | `1 minute` | 54 | 59 | 60 |

Two conclusions:

- **The first tick is the landing**, not one heartbeat after it. `cast→fade` matches nothing,
  and is long by each spell's own cast time (Immolate 2.5s, Shiftless Deeds 6.0s) — which is
  why the error is not a constant 6s.
- **The fade line arrives at the last tick, not a tick later.** `fade − firstTick` equals the
  wiki duration exactly for every spell whose wiki value is given in exact seconds or ticks.

Drifting Death is the only miss and explains itself: its wiki value is the prose `1 minute`,
rounded from 54s, while every spell that matches carries an exact `48 Sec` / `54 Sec` /
`5 ticks`.

**Change:** `DebuffTracker.Record(DebuffState)` computes
`(LastTickAt − LandedAt) + ServerTickSeconds`. Drop the `+ ServerTickSeconds` — it is a phantom
trailing tick, and it is the source of Immolate's 54s. `OnFade` already computes
`fade − LandedAt` and is correct; after this change the two paths agree.

`ServerTickSeconds` survives only if another caller needs it; otherwise it goes.

## Part 2: the duration catalog

`Data/SpellDurations.json`, an embedded resource loaded exactly as `MezSpells.json` is —
**no runtime network**. A game overlay should not make an HTTP request mid-fight for a number
that is only a fallback estimate, and the alternative brings four lookup states
(LIVE/CACHED/STALE/Offline) into a panel read under pressure.

Built by `scripts/harvests/eqlwiki/spells-promote.py`, alongside the existing
`quests-promote.py`, from the already-harvested `spells.json` (1,929 spells; 1,589 with a
parsed `duration_seconds`; 2,997 cached wikitext pages). The promote step reduces to
`{ name → durationSeconds }` and **excludes**:

- `Instant` / `duration_seconds == 0` — not a debuff.
- The 337 entries with an unparsed raw duration, which are level-scaled ranges such as
  `Cripple`'s `6.3 minutes @L53 to 7.0 minutes @L60`. These become **absent**, hence Unknown.
  A level-scaled spell has no single base to multiply, and inventing one violates tier 3.

Refreshed by re-running the harvest and the promote script, the same as every other catalog.

## Part 3: rank resolution — the catalog is the authority on names

121 catalog spells legitimately end in a Roman numeral: `Clarity II`, `Burnout IV`,
`Cannibalize IV`, `Berserker Madness III`. These are distinct spell pages, not ranks. Stripping
numerals naively would read `Clarity II` as tier-2 Clarity and scale a duration the catalog
already knows exactly.

Resolution order, given a cast name:

1. **Exact catalog hit → tier 0, use the catalog value as-is.** `Clarity II` → its own 2100s.
2. **Else strip a trailing Roman numeral; base must be in the catalog → Derived.**
   `Shiftless Deeds IV` → `Shiftless Deeds` 150 × 1.4 = 210s.
3. **Else Unknown.** `Heroic Leap I` — no base page, no guess.

Verified against every ranked cast in the log: `Mesmerization V` → 36, `Shiftless Deeds IV` →
210, `Beguile II` → 1152, `Efflorescing Heal III` → 31.2, `Charm III` → 1248, while
`Clarity II`, `Burnout IV` and `Cannibalize IV` correctly resolve exact.

Note that step 1 yields a *base* duration, which is still an estimate of what will happen on a
mob — it is `Derived` certainty for display purposes. Only the log makes something `Measured`.

## Part 4: model changes

`DebuffState` gains:

- `Rank` (int, 0 when unranked or unknown) and the resolved base name.
- `Certainty { Measured, Derived, Unknown }`.

`DebuffTracker.Expiry(spell, from)` consults, in order: per-rank samples → catalog-derived →
null. Samples are keyed **per rank**, since ranks have genuinely different durations and mixing
`Immolate I` with `Immolate V` samples would corrupt both.

### Latent bug fixed here

`_recastPending` stores the name from `SpellCastEvent` — which carries the rank — and is then
looked up with `tick.Source`, which does not. For any ranked DoT, `_recastPending.Remove(...)`
can never match, so recast detection silently never fires. That is exactly the failure its own
comment documents ("the real log taught Immolate 115s against 54-60s"). The log escapes it only
because none of the DoTs in it are ranked. Key `_recastPending` by base name.

## Part 5: presentation

`DebuffChipPresentation` renders a derived countdown with a tilde prefix: `~2:40` against a
measured `2:40`, beside the existing `--` for unknown. One character, no new colour, legible at
the smallest chip-slider size. Amber/red warning thresholds apply to derived countdowns exactly
as to measured ones — an estimate that is about to drop is still worth showing.

## Testing

**Anchoring (Part 1)** — a completed DoT with a fade line measures `fade − firstTick`; the same
DoT retired by tick silence measures the same value, which is the regression test for the
dropped `+6`. Verbatim Immolate lines from the fixture, asserting 48s.

**Catalog (Part 2)** — promote script output is checked in and asserted: `Shiftless Deeds` 150,
`Immolate` 48, `Mesmerization` 24; `Cripple` and every `Instant` spell absent.

**Rank resolution (Part 3)** — `Clarity II` resolves exact and is *not* scaled; `Shiftless Deeds
IV` derives 210; `Heroic Leap I` is Unknown. These three are the whole contract.

**Trust ordering (Part 4)** — a derived expiry is replaced by a measurement on the next cast and
never the reverse; Tepid Deeds keeps 126s against a catalog 150s; samples for two ranks of one
spell do not pool.

**Presentation (Part 5)** — `~` appears only for `Derived`, `--` only for `Unknown`, and a
derived countdown still turns amber at `DebuffWarnSeconds`.

**Real-app verification** — headless Avalonia tests cannot confirm this panel. Publish, install,
replay the real log, and read the DoT panel and `error.log`. Replaying the real log has caught
two bugs the headless suite could not see.

## Files

- `scripts/harvests/eqlwiki/spells-promote.py` (new)
- `src/EQBuddy.Core/Data/SpellDurations.json` (new, generated), `EQBuddy.Core.csproj`
- `src/EQBuddy.Core/SpellDurationCatalog.cs` (new — load, rank resolution, derivation)
- `src/EQBuddy.Core/DebuffTracker.cs` (anchoring fix, per-rank samples, certainty, recast key)
- `src/EQBuddy.UI.Shared/DebuffChipPresentation.cs` (`~` marker)
- tests alongside each

## Out of scope

Live wiki fetching for spells absent from the catalog; the Spell Level slider's modifier table;
any change to slow/cripple detection or to which effects are tracked.
