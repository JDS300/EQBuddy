# Heal-over-time tracking

**Status:** implemented in Core, UI.Shared and the Avalonia app as of 1.31.0. **WPF view not
built** — see [What WPF still needs](#what-wpf-still-needs). Offered upstream as a complete
feature with one view outstanding.

## The problem

A HoT that lapses is healing you never did, and nothing in the game tells you it stopped.
There is no fade message, no buff-bar countdown for spells on other people, and the tick
lines scroll past in combat spam. Healers re-cast on feel, which means either wasted casts
or gaps.

EQBuddy already tracks the two other "how long is this thing still true" problems — mez
countdowns and spawn timers — and both use the same chip-stack idiom. This is the third.

## What the log actually supports

Everything below is measured from a real 690k-line log
(`eqlog_Daggo_freeport.txt`, one week, Legends server, a druid). Numbers matter here because
several plausible designs are ruled out by them.

**Ticks.** A HoT tick logs as:
```
You healed Daggo over time for 130 hit points by Blossoming Heal.
You healed Chickpea over time for 13 (61) hit points by Flowering Heal.
You healed Daggo over time for 78 hit points by Blossoming Heal. (Critical)
```
`LogParser` already produces `HealEvent { Outgoing = true, OverTime = true }` for these.

- Cadence is **6 seconds**: of 2972 consecutive same-(spell, target) gaps, 2738 were exactly
  6s, 154 were 7s, 80 were 5s. The jitter is log flushing.
- A cast runs **5 ticks**, so **24 seconds** of healing from the first tick. Blossoming
  276/298 runs were exactly 5, Blooming 217/234, Flowering 80/94, Efflorescing 64/71,
  Sprouting 15/15.
- Ticks carry the **unranked** name (`by Efflorescing Heal`) while casts carry the rank
  (`You begin casting Efflorescing Heal III.`).

**There is no fade line.** Zero `Your <HoT> spell has worn off` in the whole log, against
1197 for Mesmerization. A HoT ends silently.

**But there is a terminator.** The spell finishes on a bigger companion heal:
```
You healed Daggo for 398 hit points by Blossoming Heal Trigger.
```
~3x a normal tick (Blossoming 398 vs 130 average; Blooming 310 vs 98; Efflorescing 489 vs
184), landing **with the final tick** — 417 of 616 matched cases at exactly 0s offset. This
is the authoritative end, and first-tick→Trigger is a real measured duration.

**Two things the log will not give you:**

1. **Nothing is logged when nothing is healed.** Not one of ~3700 tick lines carries a 0
   amount. A HoT on a full-health target is completely silent.
2. **Nothing names the target of a beneficial cast.** The cast line is bare, and
   `You have targeted X` / `You are targeting` appear **0 times** in 690k lines.

Together these mean: *a HoT cast on a full-health target can never be attributed to a
person.* No amount of cleverness recovers it. The design has to be honest about that rather
than guess.

## Design

`HotTracker` (Core) is a fourth consumer of the parsed event stream, alongside
`SessionStats`, `SpawnTimers` and `MezTracker`, and follows `MezTracker`'s shape exactly:
`Apply(GameEvent)` under a lock, `Snapshot(DateTime)` for the UI, learned values persisted
via `AttachStore`, a `Changed` event, and replay-safety through log timestamps only.

**One entry per (target, spell).**

**Opened by the cast, not the first tick.** This is the correction that matters. Opening on
the tick means a full-health target never gets a chip — the exact case a healer still wants
to see. So the cast opens the entry immediately, with **no target**, and the first tick
*binds* a target to it and re-anchors the countdown precisely.

**A name earns HoT status by having been seen ticking or triggering**, seeded at
`AttachStore` from the learned-duration store. `You begin casting X.` says only that
something was cast; without this gate an ungated cast handler chips every nuke, mez and gate
in the game.

> **Known limitation — the cast path is currently inconsistent.** Only spells with a
> *learned duration* are seeded at startup, and a duration is only learned from a completed
> **Trigger**. A spell that has ticked but never completed is forgotten on restart, so
> whether a full-health cast produces a chip depends on the player's history with that
> particular spell. Reported from play: with a store holding only `Efflorescing Heal`, a
> full-health `Blossoming Heal` produced no chip.
>
> This was missed because the verification seeded a completed HoT before the full-health
> cast, which guaranteed the gate passed — it tested a constructed happy path, not the real
> cold start.
>
> The fix is small: persist the *set of names known to be HoTs* separately from the learned
> durations, so one observed tick makes a name permanent. Until then the honest description
> of the cast path is "works for spells you have already completed once", and a reviewer
> should decide whether to take it that way, take the fix with it, or drop the cast path and
> open chips on the first tick only.

**Ended by the Trigger**, with the 5-tick/6s estimate as fallback — 128 of 712 runs have no
visible Trigger (zoning, log truncation, the target dying), so the fallback carries ~18% of
real cases. A Trigger that closes an un-bound chip teaches **no** duration: its anchor was
an estimate, and a guessed measurement would poison a store that otherwise holds only real
ones.

**Cancelled by interrupt or fizzle** — `SpellInterruptedEvent` (320 in the log) and
`FizzleEvent` (231; note the line ends in `!`, not `.`). Both already exist in `LogParser`.

**Durations are learned, longest-observed-wins**, exactly as `MezTracker` learns from
land→fade gaps: an interrupted HoT measures short, nothing measures it long. Replaying the
reference log learns 25s for four spells and 24s for Sprouting, against an independently
computed ground truth of exactly 25s and 24s.

### Two decisions worth reviewing

**A cast while the same HoT already runs on a known target opens a second targetless chip
rather than refreshing the first.** The log cannot distinguish "refresh on Daggo" from
"fresh cast on Chickpea", so this is a choice of which way to be wrong. Refreshing promises
a full duration on a chip that may lapse in ten seconds, and a healer trusting that lets the
HoT drop — the silent failure the chip exists to prevent. A second chip claims only "another
one is running", and resolves itself when ticks land.

**Ticks never gap, so gaps cannot separate casts.** All 2972 in-run gaps are 5–7s *including
across recasts* — a gap rule alone can never split a chain, and tick-*count* learning would
drift upward forever (6, 7, 8…) from recast chains. The Trigger is what separates them.
Replay confirms it: 312 Blossoming chips where naive gap-grouping sees 298, the +14 being
exactly the observed 10-tick runs.

## Presentation

`HotChipPresentation` (UI.Shared) converts `HotState` → the shared `SpawnChip` record, the
same split `MezChipPresentation` uses.

- Icon `🌿`, distinct from `⏳` spawn and `💤` mez.
- Name is the **target**, or the **spell** when no target is bound yet.
- Countdown is time until healing stops. Warns at **6s** — one tick, the last moment a recast
  still lands before the HoT ends, and the only moment a HoT chip is urgent.
- Your own HoT is **emphasised**, not merged in: the chips that matter are the ones on people
  whose buff bar you cannot see. `AppSettings.ShowSelfHotChips` (default on) drops them
  entirely for a healer who mostly self-HoTs. `IsDue` outranks the self tint — a recast
  warning beats a self/other distinction.
- Unbound chips are **never** hidden by `ShowSelfHotChips`, since they might be on anyone.

`SpawnChip` gained a trailing `bool Emphasis = false` for this. Only HoT chips set it; mez
and spawn rows keep their existing two-state colouring.

## Files

| Layer | File | Status |
|---|---|---|
| Tracker | `src/EQBuddy.Core/HotTracker.cs` | done |
| Pipeline | `src/EQBuddy.Core/LogWatcher.cs` — `Hot` property + dispatch | done |
| Settings | `src/EQBuddy.Core/AppSettings.cs` — `HotChipsLeft/Top`, `ShowSelfHotChips` | done |
| Presentation | `src/EQBuddy.UI.Shared/HotChipPresentation.cs` | done |
| Chip record | `src/EQBuddy.UI.Shared/SpawnsViewModel.cs` — `SpawnChip.Emphasis` | done |
| Avalonia view | `src/EQBuddy.Avalonia/HotChipsWindow.cs` + `MainWindow` wiring + Options toggle | done |
| **WPF view** | — | **not built** |
| Tests | `HotTrackerTests` (29), `HotChipPresentationTests` (8), `ChipWindowRenderTests` (+3) | done |

## What WPF still needs

All logic is in Core/UI.Shared and tested there, so the WPF side is a thin view — the same
shape as the port that closed the mez-chip gap:

1. `MezChipsWindow.xaml` / `.xaml.cs` copied for HoT, sourced from
   `HotChipPresentation.Chips(...)`, with `SpawnChip.Emphasis` selecting a "good" brush for
   the countdown instead of the accent (`IsDue` still wins).
2. `MainWindow.xaml.cs`: a `HotTracker` field, `AttachStore` at `hot-durations.json`,
   `_watcher.Hot = _hotTracker` **before** the first `Select()` so the startup replay
   reconstructs state, and a show/refresh/hide block in the existing 1s tick beside the mez
   one.
3. An Options checkbox for `ShowSelfHotChips`.

Estimated as small; the Avalonia equivalent is ~170 lines of window plus ~30 of wiring.

## Also in this branch, separable

**Critical heals never parsed at all.** A crit appends ` (Critical)` *after* the full stop,
and both heal patterns anchored on that stop — so every critical heal produced no event.
205 lines in the reference log, 186 outgoing, by definition the biggest heals in it, silently
missing from healing stats for everyone on every platform. Fixed in `LogParser` with its
triggering line as a test. **This is worth taking upstream regardless of whether the HoT
feature is.**

Fixing it also improved HoT accuracy: 749 → 746 chips on replay, because a crit tick no
longer reads as a break and a restart.

## Verification

```
dotnet build src/EQBuddy.Avalonia/EQBuddy.Avalonia.csproj -c Release
dotnet test  tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release            # 562
dotnet test  tests/EQBuddy.Avalonia.Tests/EQBuddy.Avalonia.Tests.csproj -c Release   # 25
```
`dotnet build EQBuddy.slnx` requires Windows (the WPF project targets `net10.0-windows`).

Beyond the suites, the feature was verified by **replaying the real log with shifted
timestamps** into a scratch profile (`EQBUDDY_APPDATA`), per `docs/FeatureGuide.md` "Testing
without playing":

- Whole-log replay: 746 chips, max 3 concurrent, 0 stranded, learned durations matching
  independently computed ground truth exactly.
- Three concurrent HoTs on screen, self tinted apart from the others; toggling
  `ShowSelfHotChips` removed only the self chip.
- A cast at full health with zero ticks renders `🌿 Blossoming Heal 0:30` — **but only for a
  spell already in the learned-duration store**, which that scenario seeded. See the known
  limitation above; in real play, with an unseeded spell, no chip appears.

Note for reviewers: headless Avalonia tests do **not** load the X11 backend, so they cannot
verify focus behaviour, click-through or multi-monitor placement. Those were checked by
running the real app.

## Open questions for the maintainer

1. **Is the second-targetless-chip rule right?** It is the honest reading, but a healer may
   find two chips for one spell more confusing than a refreshed one. Only play settles it.
2. **Should the HoT catalog seed cover more spells?** `SpellCategory.HealOverTime` has
   `Echoing Light`, `Budding Heal`, `Regeneration`, `Chloroplast`, `Regrowth`, but not the
   Sprouting/Blooming/Blossoming/Flowering/Efflorescing line this was developed against.
   Seeding it would remove the one-cast cold start; it was left alone deliberately.
3. **Does the 6s warning threshold suit other classes?** It is one tick for a druid's bloom
   line. A HoT with a different tick rate may want a proportional threshold instead.
