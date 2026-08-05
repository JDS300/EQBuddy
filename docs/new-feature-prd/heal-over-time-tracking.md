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

**Opened by the first tick**, which names the target. One sentence describes the whole
feature: *a chip appears when your HoT starts healing someone.*

> **Deliberate limitation: a HoT on a full-health target is not tracked, and cannot be.**
> It heals nothing, so it logs nothing, and the cast line names nobody.
>
> An earlier revision opened a provisional chip on the *cast* to cover this, carrying the
> spell name until a tick bound a target. It was removed. Two reasons. The player judged
> full-health casts not worth tracking — the chip's value is knowing when a HoT on *someone
> else* lapses. And the cast path could only ever be gated on "is this name a known HoT",
> since `You begin casting X.` alone would chip every nuke and gate in the game; that set
> was seeded from the learned-duration store, which holds only spells completed through a
> Trigger, so whether a full-health cast produced a chip depended on the player's history
> with that particular spell. Inconsistent is worse than absent: it reads as a flaky
> feature rather than a documented boundary.
>
> Recorded here because the next reader will otherwise re-derive the gap and re-attempt the
> same fix. If it is revisited, persisting the set of known-HoT names separately from the
> learned durations is the piece that was missing.

**Ended by the Trigger**, with the 5-tick/6s estimate as fallback — 128 of 712 runs have no
visible Trigger (zoning, log truncation, the target dying), so the fallback carries ~18% of
real cases. A Trigger naming a target with no open series ends nothing and teaches nothing,
rather than guessing at a duration and poisoning a store that otherwise holds only measured
ones.

**Durations are learned, longest-observed-wins**, exactly as `MezTracker` learns from
land→fade gaps: an interrupted HoT measures short, nothing measures it long. Replaying the
reference log learns 25s for four spells and 24s for Sprouting, against an independently
computed ground truth of exactly 25s and 24s.

### A decision worth reviewing

**Ticks never gap, so gaps cannot separate casts.** All 2972 in-run gaps are 5–7s *including
across recasts* — a gap rule alone can never split a chain, and tick-*count* learning would
drift upward forever (6, 7, 8…) from recast chains. The Trigger is what separates them.
Replay confirms it: 312 Blossoming chips where naive gap-grouping sees 298, the +14 being
exactly the observed 10-tick runs.

## Presentation

`HotChipPresentation` (UI.Shared) converts `HotState` → the shared `SpawnChip` record, the
same split `MezChipPresentation` uses.

- Icon `🌿`, distinct from `⏳` spawn and `💤` mez.
- Name is the **target** — always known, since only a tick opens a chip.
- Countdown is time until healing stops. Warns at **6s** — one tick, the last moment a recast
  still lands before the HoT ends, and the only moment a HoT chip is urgent.
- Your own HoT is **emphasised**, not merged in: the chips that matter are the ones on people
  whose buff bar you cannot see. `AppSettings.ShowSelfHotChips` (default on) drops them
  entirely for a healer who mostly self-HoTs. `IsDue` outranks the self tint — a recast
  warning beats a self/other distinction.

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
| Tests | `HotTrackerTests` (15), `HotChipPresentationTests` (6), `ChipWindowRenderTests` (+3) | done |

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
dotnet test  tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release            # 546
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

Note for reviewers: headless Avalonia tests do **not** load the X11 backend, so they cannot
verify focus behaviour, click-through or multi-monitor placement. Those were checked by
running the real app.

## Open questions for the maintainer

1. **Should the HoT catalog seed cover more spells?** `SpellCategory.HealOverTime` has
   `Echoing Light`, `Budding Heal`, `Regeneration`, `Chloroplast`, `Regrowth`, but not the
   Sprouting/Blooming/Blossoming/Flowering/Efflorescing line this was developed against.
   Seeding it would remove the one-cast cold start; it was left alone deliberately.
2. **Does the 6s warning threshold suit other classes?** It is one tick for a druid's bloom
   line. A HoT with a different tick rate may want a proportional threshold instead.
