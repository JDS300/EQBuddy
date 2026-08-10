# DoT and debuff tracking

**Date:** 2026-08-10
**Status:** approved, ready for implementation

## Goal

One panel showing what is currently on a mob, with a countdown that warns a configurable
number of seconds BEFORE an effect drops, so it can be refreshed rather than reapplied after
the fact.

## Scope (settled with the user)

- **DoTs: yours only.** Third-party DoTs are explicitly not wanted, and in the sample log they
  are the overwhelming majority of tick lines (one bard's chords alone produce thousands).
- **Slow and cripple: anyone's**, with the chip marking whether it is yours or someone else's.
- Everything else (tash, malo, snare, root) is out of scope for v1.

## Evidence from the real log (690k lines, `tests/fixtures/eqlog_Daggo_freeport.txt`)

| Line | Meaning |
|---|---|
| `<mob> has taken N damage from your <Spell>.` | your DoT tick - names spell AND target |
| `<mob> has taken N damage from <Spell> by <Caster>.` | someone else's DoT tick - ignored |
| `<mob> slows down.` | a slow landed - names the mob, NOT the spell or caster |
| `<mob> is enfeebled.` | a cripple landed - same |
| `<Caster> begins casting <Spell>.` | the only place the spell and caster appear |
| `Your Mesmerization spell has worn off of <mob>.` | mez fades are announced... |

**Slow and cripple have no wear-off line.** Searching all 690k lines, the only "worn off"
messages are mez, stun, berserk and pet buffs. This is the single most important constraint in
the design: a DoT's end is observable, a slow's end is not.

## Two tiers of trust

- **Measured** - DoTs. Ticks arrive every ~6s naming the spell, so remaining time is derived
  from observation, exactly as `HotTracker` already learns HoT durations.
- **Estimated** - slow and cripple. Duration comes from a hand-seeded catalog, because the log
  never reveals it. The chip marks these (a `?`), and a spell with no researched duration shows
  as unknown rather than inventing a number. A confident wrong countdown is worse than an
  admitted unknown when the decision is "do I recast now".

## Detection

Existing events cover most of it - no new parsing for DoTs:

- `DamageDealtEvent(OverTime: true)` carries Target and Source (the spell). Yours by
  construction; `ThirdDotEvent` (third-party ticks) is ignored.
- Slow/cripple need one new `DebuffLandedEvent(Time, Target, DebuffKind)` from the two landing
  lines above, paired with the most recent `SpellCastEvent` (yours) or `OtherCastEvent`
  (theirs) within `MezTracker.CastToLand` (8s) - the same pairing `MezTracker.OnLanding` uses,
  including its "newest explaining cast wins, cast is not consumed" rule for AoE.

## Model

`DebuffTracker`, shaped on `MezTracker`:

```
DebuffState(Target, Spell, Caster, IsMine, Kind, LandedAt, ExpiresAt?, Certainty)
```

`RemainingSeconds(now)`, `ExpiryLinger` so a just-dropped effect stays visible briefly, and
per-target grouping.

## Alerting

One setting, `DebuffWarnSeconds`, default 10: the lead time before expiry at which an effect
is "about to drop".

- Chip turns amber at `remaining <= DebuffWarnSeconds`, red in the final few seconds.
- Optionally fires the existing alert tile and sound, reusing the watch-rule alert path,
  behind its own checkbox **defaulting to off**. A game overlay that starts making noise
  after an update is an overlay the user turns off; one tick enables it.

## Panel

A chip window in the same family as mez and spawn chips, so it inherits the chip-size slider.
Grouped by target; yours in the accent colour, others' dimmed and labelled with the caster.

## Testing

Parser tests use verbatim lines from the fixture. Tracker tests cover: your DoT tick starts and
refreshes a timer; a third-party tick starts nothing; slow pairs with the preceding cast for
spell and caster; an unpaired landing is ignored; `IsMine` reflects `SpellCastEvent` vs
`OtherCastEvent`; an unresearched slow shows unknown rather than a guessed duration; the amber
threshold fires at exactly `DebuffWarnSeconds`. Presentation tests cover colour and the `?`
marker; a render test covers the panel.

## Files

- `src/EQBuddy.Core/DebuffTracker.cs`, `Data/DebuffSpells.json` (seed durations)
- `src/EQBuddy.Core/GameEvent.cs`, `LogParser.cs` (landing event)
- `src/EQBuddy.UI.Shared/DebuffChipPresentation.cs`
- `src/EQBuddy.Avalonia/DebuffChipsWindow.cs`, Options wiring
- tests alongside each
